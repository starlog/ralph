# fix2 #2 — Cost 기록 실패 fallback 로그 설계

## 0. 목적

`Ralph/Services/CostTracker.cs`의 `RecordAsync`는 5초 timeout 또는 IO 예외
시 `AnsiConsole.MarkupLine`으로 노란 경고만 출력하고 호출이 silent하게 사
라진다. 결과적으로:

- 누적 비용(`_cumulativeUsd`)이 실제 호출 대비 과소 집계되어
  `--budget-usd` gate가 잘못 통과될 수 있다.
- jsonl 라인 누락은 사후에 어떤 dispatch가 빠졌는지 식별 불가능.
- 콘솔 경고는 병렬 실행 중 다른 출력에 묻혀 사용자가 인지하기 어렵다.

본 task는 RecordAsync 실패 시 `.ralph-logs/cost-failures.jsonl`에 fallback
한 줄을 append하고, 세션 종료 시 실패 카운트를 노출하며, fallback 자체가
실패하면 stderr로 명확히 알리는 변경의 **설계 산출물**이다. 구현은 후속
impl/test task에서 수행한다.

전제: fix2 #1(CostTracker DI/인스턴스화) 머지가 이미 완료된 상태이므로
모든 설계는 **인스턴스 메서드/필드** 기준이다 (`_logDir`, `_writeLock`
등).

---

## 1. RecordAsync 현재 흐름 분석

`Ralph/Services/CostTracker.cs:111-192`의 호출 그래프를 두 단계로 분해한다.

### 1.1 외부 진입점 `RecordAsync(taskId, model, result, ct)`

```text
RecordAsync(taskId, model, result, ct)
├─ CancellationTokenSource.CreateLinkedTokenSource(ct)
├─ cts.CancelAfter(WriteTimeout = 5s)         ← timeout 1차
├─ try
│   └─ await RecordInnerAsync(taskId, model, result, cts.Token)
├─ catch OperationCanceledException
│   when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
│   └─ MarkupLine("⚠ cost 기록 timeout ...")  ← silent drop A
└─ catch Exception (not OCE)
    └─ MarkupLine("⚠ cost 기록 실패 ...")     ← silent drop B
```

drop A는 **timeout 경로**, drop B는 **IO/직렬화/디스크 가득참/permission
denied/sharing violation** 등 임의 예외. 둘 다 호출자(보통 `finally`
블록)에 예외를 전파하지 않는다. 호출자 ct가 직접 취소된 경우는 silent
drop이 아니라 정상적인 cancellation으로 분류되어 (`when` 가드) 그대로
re-throw — 본 task의 fallback 대상에서 제외한다.

### 1.2 내부 처리 `RecordInnerAsync`

```text
RecordInnerAsync
├─ await EnsureHydratedAsync(ct)
│   └─ ReadTotalFromDiskAsync(ct)            ← jsonl 1회 read (실패 → 예외 전파)
├─ Directory.CreateDirectory(_logDir)        ← permission/디스크 → 예외
├─ result?.Usage == null
│   ├─ JsonSerializer.Serialize(placeholder)
│   ├─ await _writeLock.WaitAsync(ct)
│   ├─ await File.AppendAllTextAsync(LogFilePath, ph, ct)  ← 실패 지점 P1
│   └─ release; MarkupLine usage 누락 경고
└─ result.Usage != null
    ├─ EstimateUsd(...)
    ├─ JsonSerializer.Serialize(entry)
    ├─ await _writeLock.WaitAsync(ct)
    ├─ await File.AppendAllTextAsync(LogFilePath, line, ct) ← 실패 지점 P2
    ├─ lock(_incrementLock) _cumulativeUsd += estimated
    └─ release
```

실패가 발생할 수 있는 호출은 다음과 같다.

| 위치 | 예외 종류 | drop 분류 |
|---|---|---|
| `EnsureHydratedAsync` → `ReadTotalFromDiskAsync` | IO 예외, JsonException(line 단위로는 catch함) | drop B |
| `Directory.CreateDirectory(_logDir)` | UnauthorizedAccess, IO | drop B |
| `_writeLock.WaitAsync(cts.Token)` | OperationCanceled | drop A |
| `File.AppendAllTextAsync(...)` | IO, UnauthorizedAccess, OperationCanceled (timeout) | drop A 또는 B |
| `JsonSerializer.Serialize(...)` | NotSupported, Json (자체 타입은 발생 거의 없음) | drop B |

### 1.3 중요한 부수 효과

- **누적 캐시 갱신 시점**: `_cumulativeUsd += estimated`는 file append
  *성공 후*에만 실행 → 실패 시 디스크와 메모리 상태는 일관(둘 다 미반영)
  하지만 호출자가 기대한 비용 추적은 lost. 즉 fallback 경로에서 in-memory
  누적값을 어떻게 다룰지 별도 결정이 필요(§4.4 참고).
- **timeout cancel 후 in-flight write**: `cts.CancelAfter(5s)` 후
  `File.AppendAllTextAsync`가 실제 디스크에 부분 기록을 했을 가능성이
  존재한다 — 즉 cost.jsonl에 일부 줄이 들어가는 상태에서 fallback에도
  같은 항목을 append할 수 있다. 본 task에서는 **중복 기록 가능성을
  허용**하고 사후 분석은 `cost-failures.jsonl`을 신뢰원으로 본다(§4.5).

---

## 2. fallback 경로 — RalphPaths 상수 추가

### 2.1 현재 `Ralph/Services/RalphPaths.cs`의 ledger 상수 패턴

| 상수 | 값 | 사용 |
|---|---|---|
| `CostLedgerFileName` | `"cost.jsonl"` | basename |
| `CostLedgerRelativePath` (const) | `".ralph-logs/cost.jsonl"` | const 컨텍스트 |
| `CostLedgerRelative` (prop) | `Path.Combine(LogDir, CostLedgerFileName)` | cwd 기준 합성 |
| `ValidationLedgerFileName` | `"validation.jsonl"` | basename |
| `ValidationLedgerRelativePath` / `ValidationLedgerRelative` | 동일 패턴 | 동일 |

### 2.2 추가할 상수 (정확히 3개, 기존 패턴과 1:1 매핑)

`Ralph/Services/RalphPaths.cs`에 다음을 추가한다 (위치는 cost ledger 상수
바로 다음, validation ledger 앞):

```csharp
/// <summary>cost.jsonl 기록 실패 시 fallback ledger basename.</summary>
public const string CostFailuresLedgerFileName = "cost-failures.jsonl";

/// <summary>const string 컨텍스트에서 사용 가능한
/// `.ralph-logs/cost-failures.jsonl` 표기.</summary>
public const string CostFailuresLedgerRelativePath
    = LogDir + "/" + CostFailuresLedgerFileName;

/// <summary>`.ralph-logs/cost-failures.jsonl` 상대 경로 (Path.Combine).</summary>
public static string CostFailuresLedgerRelative
    => Path.Combine(LogDir, CostFailuresLedgerFileName);
```

CostTracker 내부에서는 cost.jsonl과 동일한 `_logDir` 기준 합성을 사용한다:

```csharp
public string FailuresLogFilePath
    => Path.Combine(_logDir, RalphPaths.CostFailuresLedgerFileName);
```

`LogFilePath`가 instance property로 노출되는 것과 대칭. 외부(예: `--logs
--cleanup`)가 보존 대상에 추가할 수 있도록 public.

### 2.3 로그 회전 보존 정책

`Ralph/Services/LogRotator.cs`는 cost.jsonl, validation.jsonl을 회전에서
**보존**한다 (CLAUDE.md §"Cost ledger" 참조). `cost-failures.jsonl`도
같은 보존 대상에 포함해야 한다. impl task에서:

- `LogRotator.cs`의 보존 파일 목록(현재 cost.jsonl, validation.jsonl)에
  `RalphPaths.CostFailuresLedgerFileName` 추가.
- 검증: 41일 전 mtime 부여 후 LogRotator 수행 → 파일 잔존 확인 (§6.4).

---

## 3. 실패 카운터 보관 위치 + 세션 요약 출력 지점

### 3.1 카운터 필드 — CostTracker 인스턴스 필드

```csharp
public sealed class CostTracker
{
    // ... 기존 필드 ...

    // P-FAIL: cost.jsonl 기록 실패 누적 카운트.
    // _writeLock 보호 하에서만 증가, 읽기는 Interlocked.Read로 lock-free 허용.
    private long _failureCount;

    public long FailureCount => Interlocked.Read(ref _failureCount);
}
```

설계 결정:

- **인스턴스 필드**(`long`): 세션-범위 카운터. `CommandContext.Cost`가 단일
  인스턴스이므로 ralph 호출 1회 = 카운터 1개 — 정적 필드 불필요(§fix2 #1
  설계 합의와 정합).
- **`long` + `Interlocked.Increment`**: 증가는 fallback append가 끝난 직후
  `_writeLock` 안에서 일어나도록 묶어 정합성 보장. 타입을 `long`으로
  잡는 것은 Interlocked가 `int`도 지원하지만 향후 long-running session에
서 32-bit 오버플로 가능성을 차단하기 위함.
- **유형별 분류 노출 안 함**: 요구사항은 단일 카운트("cost ledger writes
  failed: N")이므로 timeout vs io를 콘솔에는 합쳐 보고. 분류는
  `cost-failures.jsonl`의 `reason` 필드로 보존 — 사후 분석 가능.
- **fallback 자체가 실패한 횟수**는 별도 카운터로 분리하지 않는다 — 그
  경우는 즉시 stderr로 출력되어 사용자가 즉각 인지하므로(요구사항 §5) 추가
  카운터 가치 적음. 사후에 grep할 자료는 stderr 캡처 + `cost.jsonl` 라인
  diff로 충분.

### 3.2 세션 종료 요약 출력 지점

세션-종료 출력은 두 경로가 있다:

1. **`--run`** (parallel 또는 sequential): `Ralph/Commands/RunCommand.cs:141-169`
   의 try/catch 블록 — `costTracker.GetTotalUsdAsync` 직후가 자연스러운
   삽입 지점.
2. **`--cost`**: `Ralph/Services/CostTracker.cs:330` `PrintSummaryAsync` —
   "Ralph Cost Summary" 패널의 `Rule` 다음 행에 `usage 누락 placeholder` 같은
   warning 라인이 이미 있음. 같은 위치에 failure 카운트 라인을 합류한다.

#### 3.2.1 RunCommand 세션 종료 (`RunCommand.cs:150` 직후)

```csharp
var costSummary = await costTracker.GetTotalUsdAsync(ct);
var costFailures = costTracker.FailureCount;
if (costFailures > 0)
{
    AnsiConsole.MarkupLine(
        $"[yellow]⚠ cost ledger writes failed: {costFailures} " +
        $"(see {Markup.Escape(RalphPaths.CostFailuresLedgerRelative)})[/]");
    logger.Warn($"cost ledger writes failed: {costFailures}");
}
```

위치 근거: `notifier.NotifyAsync` 호출 직전 — 사용자가 콘솔에서 가장
마지막에 보는 메타 정보 영역. `logger.Warn`을 동시에 남겨야
`.ralph-logs/ralph-*.log`에도 기록되어 사후 추적 가능.

#### 3.2.2 PrintSummaryAsync (`CostTracker.cs:381` 직후)

```csharp
if (missingCount > 0)
    console.MarkupLine(
        $"[yellow]usage 누락 placeholder: {missingCount}개[/] ...");
if (FailureCount > 0)                        // ← 추가
    console.MarkupLine(
        $"[red]cost ledger writes failed: {FailureCount}회[/] " +
        $"(fallback: {Markup.Escape(RalphPaths.CostFailuresLedgerRelative)})");
console.WriteLine();
```

본 인스턴스의 카운터는 ralph 호출 단위로만 의미가 있으므로
`PrintSummaryAsync`가 호출되는 시점(`ralph --cost`)에는 보통 0이다
(이전 세션의 실패는 인스턴스 캐시에 없음). 그러나 같은 세션에서
`--plan` 직후 `--cost`로 표시되는 등의 경우를 위해 일관되게 노출. 실제
사후 분석은 디스크의 `cost-failures.jsonl` 라인 수가 source of truth —
요약은 보조.

#### 3.2.3 SequentialRunner 단독 사용처

`InteractiveCommand`, `SingleTaskCommand`, `DryRunCommand`도
`SequentialRunner.RunAutoLoopAsync`를 호출하지만 세션-요약 콘솔 출력은
하지 않는다 (notifier도 없음). 본 task에서는 **추가 출력 도입을 보류** —
실패는 `cost-failures.jsonl`에 기록되고, 사용자가 `ralph --cost`로 확인.
`--task <id>` 직후 카운트를 보고 싶다는 요구가 후속에 들어오면 그때
`SingleTaskCommand` 종료부에 동일 패턴(§3.2.1) 라인을 추가.

---

## 4. 재시도 전략 의사코드

### 4.1 명세 매핑

요구사항: "재시도는 1회만 (5초 → 200ms 백오프 → 포기)".
해석:

1. 1차 시도: 기존 `WriteTimeout = 5s`의 `RecordInnerAsync`.
2. 5초 안에 끝나지 못하거나 IO 예외로 실패 → **200ms sleep**.
3. 2차 시도: 동일 `RecordInnerAsync`를 다시 호출, 5초 timeout 재적용.
4. 2차도 실패 → **포기 후 fallback append**(`cost-failures.jsonl`).

호출자 자신이 이미 `ct`를 취소한 경우(예: 사용자 Ctrl+C)는 재시도 없이
즉시 propagate — 5s 안에 호출 종료해야 하는 graceful shutdown 보장.

### 4.2 의사코드 (CostTracker.RecordAsync 재구성)

```text
RecordAsync(taskId, model, result, ct):
    failureReason = null
    failureException = null
    estimatedUsdAtCall = (Usage있으면 EstimateUsd, 없으면 0.0)

    for attempt in [1, 2]:
        if ct.IsCancellationRequested:        # 외부 취소: 재시도 금지
            throw OperationCanceledException(ct)

        using cts = CreateLinkedTokenSource(ct)
        cts.CancelAfter(5s)
        try:
            await RecordInnerAsync(taskId, model, result, cts.Token)
            return                            # 성공 — 정상 종료
        catch OperationCanceledException
            when cts.IsCancellationRequested and !ct.IsCancellationRequested:
            failureReason = "timeout"
            failureException = "RecordAsync exceeded 5s timeout"
            MarkupLine yellow "⚠ cost 기록 timeout (attempt {attempt}/2) ..."
        catch OperationCanceledException:     # 외부 ct 취소
            throw
        catch Exception ex:
            failureReason = ClassifyReason(ex)   # §4.3
            failureException = ex.GetType().Name + ": " + ex.Message
            MarkupLine yellow "⚠ cost 기록 실패 (attempt {attempt}/2): {ex.Message}"

        if attempt == 1:
            try await Task.Delay(200ms, ct)
            catch OperationCanceledException: throw

    # 두 번 다 실패 → fallback ledger append
    await AppendFailureAsync(taskId, model, estimatedUsdAtCall,
                             failureReason, failureException, ct)
```

```text
ClassifyReason(ex):
    if ex is OperationCanceledException: return "timeout"
    if ex is UnauthorizedAccessException: return "permission"
    if ex is DirectoryNotFoundException: return "missing-dir"
    if ex is IOException: return "io"          # 디스크 가득참 등 포함
    if ex is JsonException: return "serialize"
    return "other"
```

### 4.3 fallback append 의사코드

```text
AppendFailureAsync(taskId, model, usd, reason, exception, ct):
    record = {
        ts: DateTime.UtcNow.ToString("o"),
        taskId: taskId,
        model: model,
        usd: usd,                              # Usage 있었던 경우만 의미있음
        reason: reason,                        # "timeout" | "io" | ...
        exception: exception                   # type+message, stack 제외
    }
    line = JsonSerializer.Serialize(record, JsonOpts) + "\n"

    using cts = CreateLinkedTokenSource(ct)
    cts.CancelAfter(5s)                        # fallback도 무한 대기 차단
    try:
        Directory.CreateDirectory(_logDir)
        await _writeLock.WaitAsync(cts.Token)
        try:
            await File.AppendAllTextAsync(FailuresLogFilePath, line, cts.Token)
        finally:
            _writeLock.Release()
        Interlocked.Increment(ref _failureCount)
    catch Exception fbEx:
        # 핵심 요구: silent drop 금지
        WriteFallbackToStderr(taskId, model, reason, fbEx)   # §5
        # 카운터는 증가시키지 않음 (콘솔 출력으로 이미 가시화)
```

설계 결정:

- **재시도 횟수 1회만**: 더 많이 retry하면 Ctrl+C 후 finally 블록에서 cost
  기록만으로 수십 초가 흘러 graceful shutdown 위반.
- **백오프 200ms**: 디스크 일시 sharing violation/AV 스캐너 재시도가 보통
  ms 단위로 풀린다. 1차 5초 + 200ms + 2차 5초 = 최대 ~10.2s — 5초 finally
  타임아웃에 비해 한 번 더 늘어나지만 graceful shutdown은 외부 ct로 보장.
- **fallback 경로의 timeout**: 5초 동일 적용. fallback이 더 빨리 timeout
  되면 stderr drop 메시지가 수반되므로 `cost-failures.jsonl`이 비어도 stderr
  로그가 잔존 — 사용자가 추적 가능.
- **재시도 사이 ct 체크**: `Task.Delay(200ms, ct)` 자체가 ct에 반응. 사용자
  취소 시 재시도 없이 즉시 propagate.
- **estimatedUsd**: usage가 있던 케이스에서는 fallback 라인에도 USD를
  기록 → 사후에 누락된 비용을 합산해 실제 누적값을 복원 가능. usage
  누락 placeholder 경로는 `usd: 0.0`이 자연스러움.

### 4.4 누적 캐시(`_cumulativeUsd`) 갱신 정책

재시도 두 번 다 실패한 경우 `_cumulativeUsd`를 어떻게 다룰지 두 가지
선택지:

| 정책 | 효과 | 비고 |
|---|---|---|
| (A) 갱신하지 않음 | jsonl과 in-memory 누적이 동기화. budget gate가 약간 underestimate | 단순. 사용자에게 "실제 비용은 fallback ledger 합산 필요" 메시지 노출 |
| (B) fallback에도 누적 합산 | budget gate가 정확. 단 jsonl에 들어가지 않은 비용도 budget에 반영되어 디스크-메모리 불일치 | 차후 같은 세션 재시작 시 hydrate에서 ?잘못 계산? |

**채택: (A)**. 근거:

- `EnsureHydratedAsync`는 `cost.jsonl`만 합산하므로 정책 (B)를 선택하면
  세션 재시작 후 누적값이 줄어드는 inconsistency 발생.
- budget gate underestimate는 안전 측 — 너무 일찍 멈추는 게 아니라 너무
  늦게 멈추는 쪽이지만, fallback 경로에 도달했다는 사실 자체가 비정상이며
  콘솔/stderr에 노출되어 사용자가 즉시 인지.
- (B)를 원하면 `cost-failures.jsonl`도 hydrate 대상에 포함시켜야 하므로
  파일 의미가 흐려진다 — `cost.jsonl`은 "성공한 기록", `cost-failures.jsonl`
  은 "실패한 기록"이라는 2-tier 의미를 보존하기 위해 (A) 유지.

향후 (B)가 필요하면 별 task로 `EnsureHydratedAsync`에 fallback ledger 합산
을 옵션으로 추가.

### 4.5 중복 기록 가능성

§1.3에서 언급한 대로 5초 timeout cancel 시 `File.AppendAllTextAsync`가
부분 또는 완전 기록을 마쳤을 수 있다. 다음 두 시나리오가 가능:

1. cost.jsonl에 라인이 들어갔는데 timeout으로 cancel → fallback도 append.
   → cost.jsonl + cost-failures.jsonl 모두에 동일 호출 기록. 사후 분석은
   timestamp + taskId 매칭으로 deduplicate.
2. cost.jsonl에 부분 라인(잘림)이 들어감 → `ReadTotalFromDiskAsync`의
   `JsonException` catch로 skip되므로 누적값 영향 없음. fallback에는 정상
   기록.

**결정**: 중복 허용. 정확한 dedup은 성능/복잡도 비해 가치 적음. 다만
`cost-failures.jsonl`에 항상 `ts`(ISO-8601 UTC)를 기록해 사후 매칭 가능
하도록 보장.

---

## 5. fallback append 실패 시 stderr 메시지 포맷

### 5.1 요구사항 매핑

> fallback 기록도 실패하면 stderr에 명확히 출력 (silent drop 금지).

목표: 운영자가 `2>` 리다이렉트로 캡처된 로그에서 즉시 찾을 수 있을 정
도로 분명한 prefix + 단일 라인 형식.

### 5.2 출력 채널과 형식

```csharp
private static void WriteFallbackToStderr(
    string taskId, string model, string reason, Exception fbEx)
{
    // ANSI 마크업/색상 없음 — stderr 캡처/grep 친화.
    var line = string.Format(
        CultureInfo.InvariantCulture,
        "[ralph cost-failures] FALLBACK_WRITE_FAILED " +
        "ts={0:o} taskId={1} model={2} reason={3} " +
        "fallbackException={4}: {5}",
        DateTime.UtcNow,
        taskId, model, reason,
        fbEx.GetType().Name,
        fbEx.Message?.Replace('\n', ' ').Replace('\r', ' '));
    Console.Error.WriteLine(line);
}
```

설계 결정:

- **`Console.Error`로 직접 쓰기**: `AnsiConsole`은 stdout에 묶여 있고
  마크업이 끼면 grep이 어려움. fallback의 fallback이므로 가능한 한 가공
  최소화.
- **고정 prefix `[ralph cost-failures]`**: `grep -F '[ralph cost-failures]'`
  한 줄로 추출 가능. `FALLBACK_WRITE_FAILED` 토큰으로 일반 경고와 구분.
- **단일 라인 강제**: `fbEx.Message`의 줄바꿈을 공백으로 치환 → 행 단위
  파싱(awk/cut) 호환.
- **stack trace 미포함**: privacy/길이 균형. 디버깅이 필요하면 동일
  사용자 보고 시 `RalphLogger.Warn`에 예외 객체를 같이 남겨 별도 회수
  (logger 주입은 fix5 task에서 합쳐짐 — 본 task는 placeholder까지만 둠).
- **`Replace('\\n', ' ')`로 line 정합성 유지**: ex.Message는 OS/locale에 따라
  여러 줄일 수 있다. `\\r`도 함께 정리해 Windows CRLF 케이스를 커버.

### 5.3 stderr 출력 자체가 실패하는 경우

`Console.Error.WriteLine`이 IOException을 던질 가능성은 거의 없으나
(stderr가 close된 환경 등), 발생 시 catch하지 않고 위로 전파한다 — 본
fallback은 이미 "마지막 보루"이고 더 이상 안전망이 없다. RecordAsync는
호출자(보통 finally) 컨텍스트에서 외부로 throw되면 RunCommand의 외곽
try/catch가 잡거나 프로세스 종료 직전이므로 영향 최소.

### 5.4 예시 출력

```
[ralph cost-failures] FALLBACK_WRITE_FAILED ts=2026-04-30T12:34:56.7890123Z taskId=fix2-cost-failures-impl model=sonnet reason=permission fallbackException=UnauthorizedAccessException: Access to the path '.ralph-logs/cost-failures.jsonl' is denied.
```

---

## 6. 테스트 시나리오 설계

대상 파일: `Ralph.Tests/CostTrackerFailuresTests.cs` (신규).
fix2 #1 디자인에 따라 **`[Collection("cost")]` 미사용** — 각 테스트가
자체 tempDir를 가진 `CostTracker` 인스턴스를 생성한다.

### 6.1 테스트 헬퍼 (요약)

```csharp
public class CostTrackerFailuresTests : IDisposable
{
    private readonly string _tempDir;

    public CostTrackerFailuresTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ralph-cf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() { try { Directory.Delete(_tempDir, true); } catch { } }
}
```

### 6.2 핵심 시나리오

#### T1. `read-only logDir → cost-failures.jsonl 생성 + 카운터 1`

POSIX 한정(테스트 skip on Windows). 기본 회귀 검증.

```text
1. _logDir 자체를 0o555 (read-only) 로 chmod.
   → File.AppendAllTextAsync(LogFilePath) 가 UnauthorizedAccess 던짐.
2. 그러나 fallback append도 동일 _logDir → 함께 실패해야 한다.
   → 검증: stderr에 "FALLBACK_WRITE_FAILED" 라인 발생.
   → 검증: tracker.FailureCount == 0 (fallback 자체가 실패했으므로 §4.3 정책).
   → 검증: cost.jsonl도 cost-failures.jsonl도 미생성.
```

이 경로는 stderr 분기를 테스트한다. POSIX/`Skip` 가드:
`if (!OperatingSystem.IsWindows())`.

#### T2. `cost.jsonl만 read-only, _logDir는 쓰기 가능 → fallback 성공`

```text
1. _logDir 권한 0o755.
2. cost.jsonl을 미리 빈 파일로 만들고 0o444 (read-only).
3. RecordAsync 호출.
   → 1차 시도 IO 실패, 200ms 후 2차도 동일 실패.
   → fallback append → cost-failures.jsonl 생성.
4. 검증:
   - tracker.FailureCount == 1.
   - cost-failures.jsonl 라인 수 == 1.
   - JSON 파싱 결과: reason ∈ {"permission","io"}, taskId/model 일치.
   - tracker.GetTotalUsdAsync() == 0 (정책 §4.4 (A)).
```

#### T3. `정상 동작 — 실패 카운터 0 유지`

회귀 가드. RecordAsync 정상 1회 호출 후 `FailureCount == 0`,
`cost-failures.jsonl` 미생성, `cost.jsonl`에 라인 1개.

#### T4. `Usage 누락 placeholder도 같은 fallback 경로`

result?.Usage == null 분기에서 일부러 IO 실패 강제 → fallback 라인의
`usd == 0.0`, `reason ∈ {"permission","io"}` 검증. RecordInnerAsync의
양쪽 분기가 동일 fallback을 거치는지 확인.

#### T5. `재시도 횟수 정확히 2`

Mock IAgentRunner는 무관, 핵심은 `File.AppendAllTextAsync` 실패 횟수.
간단 우회: 임시 `IFileWriter` 추상화를 도입하지 않고 **시간 측정**으로
간접 검증.

```text
- read-only cost.jsonl로 강제 실패.
- Stopwatch로 RecordAsync 소요 시간 측정.
- 기대: 200ms < elapsed < 5.5s (1차는 IO가 즉시 실패, 200ms 백오프, 2차도
  즉시 실패 → 약 200~400ms). fallback 자체는 성공해 빠르게 종료.
- 5s 이상 걸리면 timeout 분기를 태웠다는 신호 — 이 테스트가 아닌 T6에서.
```

#### T6. `타임아웃 분기 — 5초 후 fallback 진입`

직접 5초를 기다리는 테스트는 너무 느리다. 대안:

- `WriteTimeout`을 `internal static`로 풀고 테스트에서 `TimeSpan.FromMilliseconds(50)`로 override할 수 있는 테스트 훅을 추가하는 방법은 §7.4 잔여 위험으로 추적(현재 구현 호환성 영향 큼).
- 본 task 범위에서는 **시뮬레이트 가능 시나리오로 한정**: read-only 파일로 IO 실패 분기만 검증(§T2). timeout 분기는 통합 테스트에서 환경변수 `RALPH_COST_WRITE_TIMEOUT_MS`로 주입할 수 있도록 impl task에서 internal hook을 노출(예: `internal static TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);` 유지하되 ctor에서 override 가능한 second constructor 인자 추가).

T6는 **선택 테스트** — 구현이 timeout-override 경로를 노출했을 때 추가.

#### T7. `세션 요약 출력 형식`

`PrintSummaryAsync`에 `TextWriter`를 주입(이미 시그니처 존재) →
`failureCount > 0`일 때 출력에 "cost ledger writes failed: N" 토큰이 포함
되는지 검증.

```csharp
var sw = new StringWriter();
var cost = new CostTracker(logDir: _tempDir);
// FailureCount를 1 이상으로 만들기 위해 T2 시나리오 선행 또는
// internal setter 노출(테스트 전용).
await cost.PrintSummaryAsync(output: sw);
Assert.Contains("cost ledger writes failed", sw.ToString());
```

`FailureCount`를 직접 세팅할 internal hook이 없으면 T2의 후속 단계로 합쳐
하나의 테스트로 검증한다.

#### T8. `LogRotator 보존`

`LogRotator` 단위 테스트가 이미 cost.jsonl 보존을 검증한다면 동일 패턴
으로 cost-failures.jsonl도 41일 전 mtime 부여 후 회전 → 잔존 확인.

### 6.3 Windows 호환성

`File.SetUnixFileMode`는 .NET 8 표준. Windows에서는 `FileAttributes.ReadOnly`
가 directory에 효과 없음 → T1/T2는 POSIX-only로 `[Fact(Skip = ...)]` 또는
`SkippableFact` 사용. T3/T4/T7은 OS 무관.

대안 Windows 경로: `FileStream`을 `FileShare.None`으로 미리 잡아
sharing violation 유도. 단 `_writeLock`이 인스턴스 mutex라 동일 인스턴스에
서는 발생시키기 어렵고, 별도 프로세스 시뮬레이션이 필요 → 본 task는
POSIX-only로 한정.

### 6.4 통합 검증 (수동/CI)

- `chmod -w .ralph-logs/cost.jsonl && ralph --run` → 콘솔에 노란 경고 +
  세션 끝에 "cost ledger writes failed: N" + `.ralph-logs/cost-failures.jsonl`
  생성 확인.
- `chmod -w .ralph-logs && ralph --run` → stderr에
  `[ralph cost-failures] FALLBACK_WRITE_FAILED` 라인 확인.

---

## 7. 회귀 위험 및 잔여 항목

### 7.1 위험 매트릭스

| 위험 | 영향 | 완화 |
|---|---|---|
| 재시도로 RecordAsync 총 소요시간이 최대 ~10.2s까지 증가 | 중 | 외부 `ct` 즉시 반응; 1회 retry로 한정; finally 블록 graceful shutdown 보장은 외부 ct로 |
| `cost-failures.jsonl` 누적이 무한 증가 | 저 | LogRotator 보존 대상이지만 사용자 권한 회복 후 정상화되면 더 이상 증가하지 않음. 운영 알림 정도로 충분 |
| fallback 라인의 estimatedUsd가 cost.jsonl과 별도로 존재해 사용자 혼동 | 저 | `cost-failures.jsonl` 헤더 주석/CLAUDE.md에 "성공 ledger와 합산 시 §4.4 정책 참조" 명시. impl task에서 README 갱신 |
| Windows에서 read-only 시뮬레이션이 어려워 timeout 분기 미검증 | 중 | T6를 internal hook으로 후속화; impl 단계에서 `internal static TimeSpan` 노출 검토 |
| stderr 라인이 다른 스레드 출력과 인터리브 | 저 | `Console.Error.WriteLine`은 line-atomic; multi-line 메시지를 명시적으로 single line으로 정규화(§5.2) |
| `_failureCount`를 logger.Warn으로도 남기는데 logger가 null인 호출 경로(예: 아직 fix5 머지 전)에서 NullReferenceException | 중 | logger 호출은 §3.2.1 `RunCommand` 콘솔 경로에 한정 — CostTracker 내부에서는 logger 미호출 (fix5 task가 채움). null 가드 불필요 |

### 7.2 잔여 위험 — 후속 task

- **timeout 분기 단위 테스트화**: §6 T6. 별도 fix2 #2 후속 또는 fix5 통합
  시점에 `WriteTimeout` test override hook 추가.
- **다중 인스턴스 동일 logDir 동시 append**: fix2 #1 §7.4 잔여 위험과
  공유. 본 task의 fallback 경로도 동일 `_writeLock`을 쓰므로 별도 회귀
  추가는 없다.
- **fallback 라인 deduplication 도구**: 사용자 보고가 들어오면 `ralph
  --cost --include-failures` 같은 옵션으로 제공 검토. 본 task 범위 외.

---

## 8. 작업 분해 (후속 impl/test task 입력용)

본 task 산출물은 본 문서뿐이다. 후속 impl이 수행할 변경:

1. `Ralph/Services/RalphPaths.cs`
   - `CostFailuresLedgerFileName` / `CostFailuresLedgerRelativePath` /
     `CostFailuresLedgerRelative` 추가 (§2.2).
2. `Ralph/Services/CostTracker.cs`
   - `_failureCount` 인스턴스 필드 + `FailureCount` 공개 속성 (§3.1).
   - `FailuresLogFilePath` 공개 속성 (§2.2).
   - `RecordAsync` 재구성 — 1회 retry 루프 + 200ms 백오프 (§4.2).
   - `AppendFailureAsync` private 헬퍼 (§4.3).
   - `WriteFallbackToStderr` private static 헬퍼 (§5.2).
   - `PrintSummaryAsync`에 failure count 라인 추가 (§3.2.2).
3. `Ralph/Commands/RunCommand.cs`
   - 세션 종료 블록에 failure count 콘솔 출력 + `logger.Warn` (§3.2.1).
4. `Ralph/Services/LogRotator.cs`
   - 보존 파일 목록에 `CostFailuresLedgerFileName` 추가 (§2.3).
5. `Ralph.Tests/CostTrackerFailuresTests.cs`
   - 신규 — T1~T5, T7 (§6.2). T6/T8은 별 task.
6. CLAUDE.md
   - "Cost ledger" 섹션에 `cost-failures.jsonl`을 보존 ledger 목록에 추가.
7. `dotnet build` 무경고 + `dotnet test` 전체 PASS.

---

## 9. 산출물 / 보고

- 생성 파일: `docs/fix2/02-cost-failures-plan.md` (본 문서).
- Scope 외 파일 변경: 없음.
- 추가 컨텍스트 참조: `fix2.md` §2, `docs/fix2/01-cost-tracker-di-plan.md`,
  `Ralph/Services/CostTracker.cs:111-192`, `Ralph/Services/RalphPaths.cs:31-72`,
  `Ralph/Commands/RunCommand.cs:107-169`.
