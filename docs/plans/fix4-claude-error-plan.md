# Fix #4 — Claude CLI 에러 분류 설계

## 1. 배경

`fix1.md` 4번 항목 요약: `Ralph/Services/ClaudeService.cs` 의 모든 실패가 `Success=false`
하나로 뭉뚱그려져 있어서 (a) 바이너리 부재 / 권한 거부처럼 절대 풀리지 않을 실패도
`MAX_RETRIES`회 재시도하며 시간/비용을 낭비하거나 (b) timeout / rate-limit 같이 회복
가능한 실패를 너무 빨리 포기할 위험이 공존한다.

현재 흐름 (`ClaudeService.cs:562-646`):

- `RunWithRetryAsync` 는 `result.Success == false` 면 무조건 `maxRetries` 까지 재시도.
- 단 두 갈래의 차별만 존재:
  - `result.TimedOut == true` → break (재시도 skip).
  - `result.RateLimited == true` → `ComputeRateLimitBackoffSec` 로 jittered backoff
    적용 (서버 retry-after 우선, 없으면 60·120·240… 최대 600s).
- 그 외(바이너리 없음, 권한 거부, malformed JSON, 일반 비정상 종료)는 모두 동일 경로
  로 들어가 `retryDelay`(기본 5s) sleep 후 재시도.

본 설계는 **분류만** 도입한다 — backoff 산식, retry-after 추출, RateLimited 시그널
검출은 모두 기존 로직(§5의 회귀 방지 대상)을 그대로 두고 enum 매핑 + 정책 분기만 얇게
얹는다.

---

## 2. `ClaudeFailureKind` 명세

파일: `Ralph/Services/ClaudeService.cs` (기존 파일 안에 enum 선언 — `ClaudeService` 와
같은 namespace `Ralph.Services` 내 top-level enum). 별도 파일 분리는 본 PR에서 하지
않는다(머지 표면 최소화).

```csharp
namespace Ralph.Services;

/// <summary>
/// Claude CLI 호출 실패의 분류. 재시도 정책·로그 메시지 분기에 사용한다.
/// `Success == true` 일 때는 의미 없음 (호출자가 참조하지 않아야 한다).
/// </summary>
public enum ClaudeFailureKind
{
    /// <summary>실패 분류 미적용 (Success=true 또는 분류 전 기본값).</summary>
    None = 0,
    /// <summary>`claude` 실행 파일을 찾지 못함. 영구 실패 — 재시도 의미 없음.</summary>
    BinaryNotFound,
    /// <summary>권한 거부(Win32 5 / errno EACCES 등). 영구 실패.</summary>
    PermissionDenied,
    /// <summary>per-attempt timeout 초과로 process tree kill. 회복 가능성 낮음 → 재시도 skip.</summary>
    Timeout,
    /// <summary>rate-limit / overloaded / quota. backoff 후 재시도 가치 있음.</summary>
    RateLimited,
    /// <summary>stream-json 파싱 실패가 누적되거나 기대 메시지(assistant/result)가 전혀 안 옴.</summary>
    MalformedOutput,
    /// <summary>위 어디에도 해당하지 않는 비정상 종료. 1회만 더 시도.</summary>
    Unknown,
}
```

설계 원칙:

- 멤버 6 + `None` 1. `None` 은 `default(ClaudeFailureKind)` = `Success=true` 결과에
  대해 의미 없는 값을 강제로 떠안지 않기 위한 sentinel.
- `Timeout` 은 기존 `TimedOut` flag 와 1:1. flag 자체는 호환을 위해 유지 (§4).
- `RateLimited` 도 마찬가지로 기존 `RateLimited` flag 와 1:1.
- `BinaryNotFound`/`PermissionDenied` 는 process start 단계에서만 발생 — 즉 prompt
  쓰기 직전 `Process.Start()` 또는 stdin pipe IOException 시점에서 분류된다.

---

## 3. 분류 규칙

분류는 `ClaudeService.RunStreamAsync` 의 결과 산출 직전(현재 `return new ClaudeResult { ... }`
지점들) 에서 결정한다. 입력은 (a) 캐치된 .NET 예외 타입, (b) `process.ExitCode`,
(c) `stderr` + `errorMessages` 텍스트, (d) 기존 `TimedOut` / `RateLimited` flag.

### 3.1 예외 기반 분류 (Process 시작 / stdin pipe 단계)

| 상황 | 캐치 위치 (현재 코드) | 신규 분류 |
|---|---|---|
| `Process.Start()` 가 `Win32Exception` (errno 2 / ERROR_FILE_NOT_FOUND) | `process.Start()` 호출 (`ClaudeService.cs:160`) — 현재는 catch가 없어 throw됨 | `BinaryNotFound` |
| `Process.Start()` 가 `Win32Exception` (errno 13 EACCES / Win32 5) | 동일 | `PermissionDenied` |
| stdin write 시 `IOException` (이미 catch됨) + stderr 가 ENOENT/`command not found` 패턴 포함 | `ClaudeService.cs:174-190` | `BinaryNotFound` |
| stdin write 시 `IOException` 그 외 | 동일 | `Unknown` (또는 stderr가 비었으면 `MalformedOutput`) |
| `OperationCanceledException` 중 `localCts` 만 fired (현재 `TimedOut=true` 분기) | `ClaudeService.cs:377-409` | `Timeout` |

`Process.Start()` 의 Win32Exception 은 **현재 catch 되지 않고 throw 된다**. 본 설계에서
`RunStreamAsync` 진입부에 try/catch 를 추가하여 `BinaryNotFound`/`PermissionDenied` 결과
객체를 graceful 반환한다.

```csharp
// 의사코드
try { process.Start(); }
catch (System.ComponentModel.Win32Exception ex)
{
    var kind = ex.NativeErrorCode switch
    {
        2  => ClaudeFailureKind.BinaryNotFound,   // POSIX ENOENT / Win32 ERROR_FILE_NOT_FOUND
        13 => ClaudeFailureKind.PermissionDenied, // POSIX EACCES
        5  => ClaudeFailureKind.PermissionDenied, // Win32 ERROR_ACCESS_DENIED
        _  => ClaudeFailureKind.Unknown,
    };
    logger?.Error($"claude process start failed ({kind}, native={ex.NativeErrorCode}): {ex.Message}");
    return new ClaudeResult
    {
        Success = false, ExitCode = -1,
        Stderr = ex.Message, ErrorMessages = ex.Message,
        FailureKind = kind,
    };
}
```

### 3.2 정상 종료(exit code) + 텍스트 기반 분류

`RunStreamAsync` 의 정상 경로 (process 가 끝까지 돈 뒤) 분류 우선순위:

| 우선순위 | 조건 | 분류 |
|---|---|---|
| 1 | `process.ExitCode == 0` | `None` (Success=true) |
| 2 | `TimedOut == true` (기존 flag) | `Timeout` |
| 3 | `IsRateLimitSignal(stderr, errorMessages) == true` | `RateLimited` |
| 4 | `ExitCode == 127` 또는 stderr 가 `"command not found"`/`"No such file"`/`"is not recognized as"` 매치 | `BinaryNotFound` |
| 5 | `ExitCode == 126` 또는 stderr 가 `"permission denied"`/`"access is denied"` 매치 | `PermissionDenied` |
| 6 | stream 동안 누적된 `JsonException` 횟수 ≥ 1 **이고** `outputBuf.Length == 0` **이고** `streamedOutput.Length == 0` (즉 result/assistant 메시지를 한 번도 못 받음) | `MalformedOutput` |
| 7 | 그 외 (`ExitCode != 0`) | `Unknown` |

검사 순서가 중요:

- `Timeout` 은 `RateLimited` 보다 먼저 — timeout 으로 죽인 process 의 stderr 에 "rate limit"
  단어가 들어 있어도 분류는 timeout 이어야 한다 (재시도해도 같은 hang 이 될 가능성이 큼).
- `BinaryNotFound`/`PermissionDenied` 텍스트 검사는 `RateLimited` 뒤에 둔다 — 정상
  rate-limit 응답에 "denied" 류 문구가 들어가는 false positive 를 피하기 위함.
- `MalformedOutput` 은 마지막 직전. JSON parse error 만 있다고 즉시 malformed 판정하지
  않고, 정상 메시지를 한 번도 못 받았는지를 함께 확인 (assistant/result 메시지 한 줄이라도
  파싱했으면 `Unknown` 으로 분류 — partial 출력 후 비정상 종료 케이스).

JSON parse 누적 카운터는 신규로 도입한다:

```csharp
// 기존 catch (JsonException) { ... } 내부에 +1
var jsonParseFailures = 0;
// ...
catch (JsonException) { jsonParseFailures++; logger?.Warn(...); ... }
```

### 3.3 패턴 상수 (분류 헬퍼 내부)

`ClaudeService` 내부에 `internal static ClaudeFailureKind ClassifyFailure(...)` 헬퍼를
두고, 정규식 / `Contains` 패턴은 다음과 같이 고정한다 (모두 case-insensitive):

| 패턴 | 매핑 |
|---|---|
| `"command not found"`, `"no such file or directory"`, `"is not recognized as an internal or external command"` | `BinaryNotFound` |
| `"permission denied"`, `"access is denied"`, `"operation not permitted"` | `PermissionDenied` |

`IsRateLimitSignal` 은 기존 함수 (`ClaudeService.cs:462-474`) 를 그대로 재사용한다 —
신규 분류 함수에서 호출만 한다. 회귀 위험 0.

### 3.4 `ClassifyFailure` 시그니처 (제안)

```csharp
internal static ClaudeFailureKind ClassifyFailure(
    int exitCode,
    bool timedOut,
    bool rateLimited,
    string stderr,
    string errorMessages,
    int jsonParseFailures,
    bool gotAnyAssistantMessage)
{
    if (exitCode == 0) return ClaudeFailureKind.None;
    if (timedOut) return ClaudeFailureKind.Timeout;
    if (rateLimited) return ClaudeFailureKind.RateLimited;

    var combined = ((stderr ?? "") + "\n" + (errorMessages ?? "")).ToLowerInvariant();
    if (exitCode == 127
        || combined.Contains("command not found")
        || combined.Contains("no such file or directory")
        || combined.Contains("is not recognized as an internal or external"))
        return ClaudeFailureKind.BinaryNotFound;
    if (exitCode == 126
        || combined.Contains("permission denied")
        || combined.Contains("access is denied")
        || combined.Contains("operation not permitted"))
        return ClaudeFailureKind.PermissionDenied;

    if (jsonParseFailures >= 1 && !gotAnyAssistantMessage)
        return ClaudeFailureKind.MalformedOutput;

    return ClaudeFailureKind.Unknown;
}
```

`internal static` 로 노출해 테스트에서 직접 호출 가능하게 한다 (`InternalsVisibleTo("Ralph.Tests")`
는 csproj 에 이미 있는 것으로 가정 — 없으면 본 PR에서 추가).

---

## 4. 재시도 정책 표

`RunWithRetryAsync` 내부에서 `lastResult.FailureKind` 로 분기.

| 분류 | 추가 시도 횟수 | backoff | 비고 |
|---|---|---|---|
| `BinaryNotFound` | 0 (즉시 break) | — | 환경 문제 — 재시도해도 동일. |
| `PermissionDenied` | 0 (즉시 break) | — | 환경 문제. |
| `Timeout` | `maxRetries - 1` 까지 | 기존 `retryDelay` (5s) | **변경**: 기존엔 즉시 break 였음. fix1 §4 요구사항이 "Timeout → backoff 재시도" 이므로 정책 전환. 단, hang 이 반복 cost 를 키우는 것을 막기 위해 backoff 베이스를 `retryDelay`(5s) 로만 두고 exponential 화 하지 않는다 — 실용상 1회 재시도면 충분 (대부분 hang 은 reproducible). |
| `RateLimited` | `maxRetries - 1` 까지 | `ComputeRateLimitBackoffSec(attempt, RetryAfterSec)` (기존) | 변경 없음. 회귀 방지 우선. |
| `MalformedOutput` | **1회만** | `retryDelay` | `attempt == 1` 일 때만 한 번 더. attempt 2 에서 실패해도 break. |
| `Unknown` | **1회만** | `retryDelay` | 동일. |

`maxRetries == 1` 이면 분류와 무관하게 재시도가 0회 (현재 동작과 동일).

`MalformedOutput`/`Unknown` 의 "1회만" 은 의미상 _첫 실패 직후 1회 재시도_ 다. 즉:

```text
attempt 1: 실패(Malformed) → 재시도 1회
attempt 2: 실패            → break (분류 무관)
```

구현은 `RunWithRetryAsync` 의 for 루프에서 `attempt >= 2 && (kind is MalformedOutput or Unknown)`
이면 break.

```csharp
// 의사코드 — 기존 timeout break 자리에 분류 분기
switch (lastResult.FailureKind)
{
    case ClaudeFailureKind.BinaryNotFound:
    case ClaudeFailureKind.PermissionDenied:
        logger?.Error($"Claude {lastResult.FailureKind} — fail-fast (재시도 의미 없음)");
        return lastResult; // 즉시 종료, 추가 attempt 없음
    case ClaudeFailureKind.Timeout:
        // 기존엔 break — 정책 변경: 1회까지는 재시도 허용
        if (attempt >= maxRetries) return lastResult;
        break;
    case ClaudeFailureKind.MalformedOutput:
    case ClaudeFailureKind.Unknown:
        if (attempt >= 2) { logger?.Warn($"{lastResult.FailureKind} 재시도 1회 소진 — break"); return lastResult; }
        break;
    case ClaudeFailureKind.RateLimited:
        // 기존 backoff 경로 유지
        break;
}
```

> **회귀 영향 분석**: 기존 timeout 동작은 "즉시 break" 였다. 본 설계에서 `Timeout → 1회
> 재시도 허용` 으로 바꾼다. fix1 §4 요구사항을 만족하기 위함이지만, 실제 운영에서 hang
> 이 cost 를 키울 수 있으므로 **`workflow.maxRetries` 의 기본값(2)** 하에서만 1회 추가가
> 일어난다. 회피가 필요하면 `--task-timeout` 으로 attempt 단위 timeout 을 짧게 잡는 식으로
> 운영 가드를 권장 (CLAUDE.md 에 메모 추가 — 본 PR 범위 외).

---

## 5. `IAgentRunner` 결과 타입 변경 — `ClaudeResult` 에 필드 추가

대안 비교:

| 방안 | 호환성 | 구현 부담 | 결정 |
|---|---|---|---|
| A. `ClaudeResult` 에 `FailureKind` 프로퍼티 추가 | 기존 callsite 영향 없음 (default `None`) | 작음 | **채택** |
| B. 새 타입 `ClaudeFailureInfo` 도입 + `ClaudeResult.FailureInfo?` | 호환 OK 지만 두 타입 동기화 필요 | 큼 | 기각 |
| C. `IAgentRunner` 시그니처 변경 (`Task<ClaudeResult<TKind>>`) | 모든 구현체 변경 필요 | 큼 | 기각 (현재 구현체 1개지만 인터페이스 안정성 우선) |

### 5.1 `ClaudeResult` 변경

```diff
 public class ClaudeResult
 {
     public bool Success { get; init; }
     public string Output { get; init; } = "";
     public string Stderr { get; init; } = "";
     public string ErrorMessages { get; init; } = "";
     public int ExitCode { get; init; }
     public TokenUsage? Usage { get; init; }
     public TimeSpan Duration { get; init; }
     public bool TimedOut { get; init; }
     public bool RateLimited { get; init; }
     public int? RetryAfterSec { get; init; }
+
+    /// <summary>
+    /// 실패 분류. Success=true 일 때는 None.
+    /// 재시도 정책 분기와 사용자 진단 메시지에 사용.
+    /// 기존 TimedOut/RateLimited flag 와 의미상 중복되지만, flag 호환을 위해 둘 다 유지한다
+    /// (TimedOut=true ⇔ FailureKind=Timeout, RateLimited=true ⇔ FailureKind=RateLimited).
+    /// </summary>
+    public ClaudeFailureKind FailureKind { get; init; }
 }
```

### 5.2 `IAgentRunner` 시그니처 — 변경 없음

`IAgentRunner` 자체는 그대로 둔다. 결과 객체 형태 변경은 인터페이스의 public surface 가
아닌 ClaudeResult 의 public surface 변경이다 (기존 구현체 모두 `new ClaudeResult { ... }`
빌더 사용 — `FailureKind` 미설정 시 default `None`).

다만 docstring 에 한 줄 추가:

```diff
 /// 구현체가 지켜야 할 계약:
 /// - RunStreamAsync는 single attempt. 실패 시 Success=false인 ClaudeResult 반환(예외 던지지 않음).
 ///   외부 ct 발화 시에만 OperationCanceledException propagate.
+/// - 실패 시 FailureKind 를 가능한 한 정확히 채울 것. 분류 불가 시 Unknown.
+///   BinaryNotFound/PermissionDenied 는 재시도 정책상 fail-fast 신호로 사용된다.
 /// - RunWithRetryAsync는 maxRetries 만큼 재시도, 실패 컨텍스트를 다음 prompt에 prepend.
 ///   RateLimited 신호면 backoff 시간을 늘릴 것.
```

### 5.3 호환성 보존

- 기존 `TimedOut`, `RateLimited`, `RetryAfterSec` 필드는 모두 유지. 외부 코드 (예:
  `MergeOrchestrator`, `ParallelExecutor`) 가 참조하고 있으면 그대로 동작.
- 신규 `FailureKind` 는 추가만 하므로 binary breaking change 없음.
- `RunWithRetryAsync` 의 외부 시그니처는 동일.

---

## 6. 로그 사양

분류와 backoff 결정을 사람이 읽어서 추적 가능해야 한다. 메시지 포맷은 한국어 (CLAUDE.md
관례), 기존 메시지 톤 유지.

### 6.1 attempt 실패 직후

위치: `RunWithRetryAsync` 내부, `lastResult = result;` 직후.

```text
[ClaudeFailure] kind=<FailureKind> exit=<ExitCode> timedOut=<true|false> rateLimited=<true|false>
```

- `logger?.Warn(...)` 로 기록.
- 콘솔(output==null) 에는 분류명을 한국어로:
  - `BinaryNotFound` → `"claude 바이너리를 찾지 못함 (PATH 확인)"`
  - `PermissionDenied` → `"claude 실행 권한 없음"`
  - `Timeout` → `"Claude 호출 timeout (process killed)"`
  - `RateLimited` → 기존 `"Rate limit 감지 — backoff ..."` 유지
  - `MalformedOutput` → `"Claude 출력 파싱 실패 (JSON 깨짐)"`
  - `Unknown` → `"Claude 실패 (exit={code})"`

### 6.2 재시도 결정 직전

```text
[ClaudeRetry] kind=<FailureKind> attempt=<n>/<max> action=<retry|skip|fail-fast>
              backoffSec=<n> backoffSource=<retryDelay|server-retry-after|exponential>
```

- `retry` — 다음 attempt 진행.
- `skip` — `MalformedOutput`/`Unknown` 가 1회 재시도를 이미 소진해 break.
- `fail-fast` — `BinaryNotFound`/`PermissionDenied`.

`backoffSource` 값:

| 값 | 의미 |
|---|---|
| `retryDelay` | 일반 retryDelay 적용 (Timeout/MalformedOutput/Unknown). |
| `server-retry-after` | RateLimited + `RetryAfterSec` 비어있지 않음 — 서버가 제시한 값을 베이스. |
| `exponential` | RateLimited + `RetryAfterSec == null` — 60·120·240… 베이스. |

기존 rate-limit 메시지 (`ClaudeService.cs:599-602`) 의 `(server retry-after=Ns / exponential, jittered)`
표기를 그대로 살리고, backoffSource 라벨을 logger 쪽 메시지에만 추가한다 (사용자 콘솔
표기는 변경 없음 — 회귀 최소화).

### 6.3 분류 자체 로그

`ClassifyFailure` 가 결정한 분류는 RunStreamAsync 종료 직전 한 번 기록:

```csharp
logger?.Info($"[ClaudeClassify] kind={kind} exit={exitCode} jsonFails={jsonParseFailures} gotMsg={gotAnyAssistantMessage}");
```

진단 시 "왜 이 분류로 결정됐는지" 를 추적 가능.

---

## 7. 테스트 계획

신규 파일: `Ralph.Tests/ClaudeFailureClassificationTests.cs`.

기존 `RateLimitBackoffTests.cs` 는 **수정하지 않는다** (회귀 방지 — 본 PR의 명시 요구사항).

### 7.1 분류 단위 테스트 — `ClassifyFailure` 직접 호출

`ClassifyFailure` 가 `internal static` 이므로 `InternalsVisibleTo` 로 테스트에서 직접
호출 가능. 6개 분류 각각 확인:

```csharp
[Theory]
// (exitCode, timedOut, rateLimited, stderr, errorMsg, jsonFails, gotMsg, expected)
[InlineData(0, false, false, "", "", 0, true, ClaudeFailureKind.None)]
[InlineData(-1, true, false, "", "", 0, false, ClaudeFailureKind.Timeout)]
[InlineData(1, false, true, "rate limit exceeded", "", 0, false, ClaudeFailureKind.RateLimited)]
[InlineData(127, false, false, "claude: command not found", "", 0, false, ClaudeFailureKind.BinaryNotFound)]
[InlineData(126, false, false, "permission denied", "", 0, false, ClaudeFailureKind.PermissionDenied)]
[InlineData(1, false, false, "", "", 3, false, ClaudeFailureKind.MalformedOutput)]
[InlineData(1, false, false, "", "", 3, true, ClaudeFailureKind.Unknown)] // 메시지를 받았으면 Unknown
[InlineData(1, false, false, "weird crash", "", 0, true, ClaudeFailureKind.Unknown)]
public void ClassifyFailure_maps_correctly(int exit, bool to, bool rl, string stderr, string em, int jf, bool gm, ClaudeFailureKind expected)
{
    Assert.Equal(expected, ClaudeService.ClassifyFailure(exit, to, rl, stderr, em, jf, gm));
}

[Fact]
public void Timeout_takes_precedence_over_RateLimited_signal()
{
    // hang 으로 timeout 됐는데 stderr 에 'rate limit' 단어가 있어도 timeout 으로 분류
    Assert.Equal(ClaudeFailureKind.Timeout,
        ClaudeService.ClassifyFailure(-1, timedOut: true, rateLimited: true,
            stderr: "rate limit", errorMessages: "", jsonParseFailures: 0, gotAnyAssistantMessage: false));
}

[Fact]
public void RateLimited_signal_takes_precedence_over_PermissionDenied_text()
{
    // 'access is denied' 가 rate-limit 페이로드에 우연히 포함되어도 RateLimited 가 우선
    Assert.Equal(ClaudeFailureKind.RateLimited,
        ClaudeService.ClassifyFailure(1, false, rateLimited: true,
            stderr: "HTTP 429: access is denied for now", errorMessages: "",
            jsonParseFailures: 0, gotAnyAssistantMessage: false));
}
```

### 7.2 재시도 정책 테스트 — `IAgentRunner` mock

`IAgentRunner` 추상에 의존하는 부분은 이미 분리되어 있으나 `RunWithRetryAsync` 자체는
`ClaudeService` 인스턴스 메서드라 직접 mock 이 어렵다. 두 가지 접근:

**접근 A** (권장): `RunWithRetryAsync` 의 분류 분기 로직을 `internal static`
`DecideRetryAction(ClaudeFailureKind kind, int attempt, int maxRetries)` 로 추출 →
순수 함수 테스트.

```csharp
internal enum RetryAction { Retry, Skip, FailFast }

internal static RetryAction DecideRetryAction(ClaudeFailureKind kind, int attemptJustFailed, int maxRetries)
{
    return kind switch
    {
        ClaudeFailureKind.BinaryNotFound or ClaudeFailureKind.PermissionDenied
            => RetryAction.FailFast,
        ClaudeFailureKind.MalformedOutput or ClaudeFailureKind.Unknown
            => attemptJustFailed >= 2 ? RetryAction.Skip : RetryAction.Retry,
        ClaudeFailureKind.Timeout or ClaudeFailureKind.RateLimited
            => attemptJustFailed >= maxRetries ? RetryAction.Skip : RetryAction.Retry,
        _ => RetryAction.Skip,
    };
}
```

테스트:

```csharp
[Theory]
[InlineData(ClaudeFailureKind.BinaryNotFound, 1, 5, RetryAction.FailFast)]
[InlineData(ClaudeFailureKind.PermissionDenied, 1, 5, RetryAction.FailFast)]
[InlineData(ClaudeFailureKind.MalformedOutput, 1, 5, RetryAction.Retry)]
[InlineData(ClaudeFailureKind.MalformedOutput, 2, 5, RetryAction.Skip)]
[InlineData(ClaudeFailureKind.Unknown, 1, 5, RetryAction.Retry)]
[InlineData(ClaudeFailureKind.Unknown, 2, 5, RetryAction.Skip)]
[InlineData(ClaudeFailureKind.Timeout, 1, 2, RetryAction.Retry)]
[InlineData(ClaudeFailureKind.Timeout, 2, 2, RetryAction.Skip)]
[InlineData(ClaudeFailureKind.RateLimited, 1, 3, RetryAction.Retry)]
[InlineData(ClaudeFailureKind.RateLimited, 3, 3, RetryAction.Skip)]
public void Retry_policy_per_kind(ClaudeFailureKind k, int attempt, int max, RetryAction expected)
    => Assert.Equal(expected, ClaudeService.DecideRetryAction(k, attempt, max));
```

**접근 B** (보조): 통합 테스트로 `RunWithRetryAsync` 자체를 호출하되, `RunStreamAsync`
를 가짜 결과로 대체할 hook 이 필요. 본 PR에서는 접근 A 만 채택하고 통합 테스트는
다음 PR로 미룬다 (PR 표면 최소화).

### 7.3 횟수 검증 — `IAgentRunner` mock 시나리오

`IAgentRunner` 를 구현하는 `FakeAgentRunner` 를 도입해 `RunStreamAsync` 호출 횟수를
카운트:

```csharp
private sealed class FakeAgentRunner : IAgentRunner
{
    public bool Debug { get; set; }
    public int? TaskTimeoutSec { get; set; }
    public Queue<ClaudeResult> Scripted { get; } = new();
    public int RunStreamCalls { get; private set; }

    public Task<ClaudeResult> RunStreamAsync(...) {
        RunStreamCalls++;
        return Task.FromResult(Scripted.Dequeue());
    }
    public Task<ClaudeResult> RunWithRetryAsync(...) => throw new NotImplementedException();
}
```

이 fake 는 본 PR의 단위 테스트가 `ClassifyFailure` + `DecideRetryAction` 만 검증하므로
실제로는 사용되지 않는다 — 다만 향후 통합 테스트 도입 시 재사용 가능한 형태로 §7.2 의
스켈레톤만 명시. 본 PR 범위에서는 작성 의무 없음.

### 7.4 회귀 방지 (필수)

| 기존 테스트 파일 | 검증 내용 | 본 PR 변경 |
|---|---|---|
| `RateLimitBackoffTests.cs` | `ComputeRateLimitBackoffSec`, `ReadRetryAfterFromError`, `ExtractRetryAfterSeconds`, `IsRateLimitSignal` | **0 변경** — 모두 그대로 통과해야 함 |

CI 통과 조건:

1. `dotnet build Ralph/Ralph.csproj` 성공.
2. `dotnet test Ralph.Tests/Ralph.Tests.csproj` — 신규 `ClaudeFailureClassificationTests`
   포함 100% 통과.
3. `RateLimitBackoffTests.cs` 의 모든 테스트 (16개) 가 `Skipped` 없이 그대로 pass.

### 7.5 `InternalsVisibleTo` 처리

`Ralph/Ralph.csproj` 에 다음이 없으면 추가:

```xml
<ItemGroup>
  <InternalsVisibleTo Include="Ralph.Tests" />
</ItemGroup>
```

이미 존재한다면 본 PR 변경 없음 (구현 task 가 확인).

---

## 8. 작업 순서 (구현 task 가이드)

본 문서는 설계만 담당하므로 실제 구현은 별도 task. 권장 순서:

1. `ClaudeFailureKind` enum 추가 (`ClaudeService.cs` 끝에 append).
2. `ClaudeResult` 에 `FailureKind` 프로퍼티 추가.
3. `ClassifyFailure` / `DecideRetryAction` `internal static` 헬퍼 작성.
4. `RunStreamAsync` 의 결과 산출부 5곳 (`ClaudeResult.cs:183, 398, 443` 등) 에서
   `FailureKind = ClassifyFailure(...)` 채우기.
5. `Process.Start()` Win32Exception catch 추가 (`BinaryNotFound`/`PermissionDenied` 즉시
   반환 경로).
6. `RunWithRetryAsync` 의 `if (result.TimedOut) break;` 자리에 `DecideRetryAction` 분기
   교체.
7. 로그 메시지 포맷 §6 적용.
8. 신규 테스트 파일 추가 (§7.1, §7.2).
9. `dotnet test` 전수 통과 확인 — 특히 `RateLimitBackoffTests` 가 모두 그대로 통과.

---

## 9. 변경하지 않는 것 (스코프 외)

- `ComputeRateLimitBackoffSec` 산식 — 그대로.
- `IsRateLimitSignal` 패턴 목록 — 그대로.
- `ReadRetryAfterFromError` / `ExtractRetryAfterSeconds` — 그대로.
- `IAgentRunner` 메서드 시그니처 — 그대로 (docstring 한 줄 추가만).
- `MAX_RETRIES`, `RETRY_DELAY` 환경변수 의미 — 그대로.
- `workflow.maxRetries` / `workflow.retryDelay` JSON schema — 그대로.
- 기존 `TimedOut`, `RateLimited`, `RetryAfterSec` 필드 — 호환을 위해 모두 유지.

---

## 완료 보고

- 생성: `docs/plans/fix4-claude-error-plan.md` (본 문서)
- Scope 외 파일 변경: 없음.
