# Fix #5 — Silent error swallowing 정리 설계

## 1. 배경

`fix1.md` 5번 항목 요약: 코드 전반에 두 가지 안티패턴이 섞여 있다.

**(a) Null-coalesce logger** — `logger?.Error(...)` 형태. 호출자가 logger를 안 넘기면
에러가 디스크 어디에도 남지 않고 사라진다. 로컬에서는 console 출력으로 가려지지만,
parallel 실행처럼 worker가 console을 공유하지 않는 경로에서는 **운영 시 사고가 생겨도
사후 추적이 불가능**하다.

**(b) Best-effort try/catch** — `catch { /* best-effort */ }` 또는 빈 catch. 정상
경로에선 무해하지만, 실패가 빈번하게 누적되면 silent해서 원인을 영원히 모른다.

**(c) Null prompt 마스킹** — `Ralph/Services/PromptBuilder.cs:88`이 `task.Prompt ?? "(prompt 미지정)"`로
fallback. 빈 prompt를 가진 task가 그대로 Claude로 dispatch되어 시간/비용을 낭비한 뒤
"왜 task가 멍하니 노는지" 사람이 사후에 디버깅하게 된다. 같은 검증이
`PlanValidator.cs:71-72`에는 **warning**으로만 들어가 있어 실질적으로 막지 못한다.

본 fix는 위 세 가지를 한 PR로 정리한다. Scope는 **`Ralph/Services/`**, **`Ralph/Commands/`**,
**`Ralph.Tests/`** (test 파일은 시그니처 변화에 맞춰 컴파일을 통과시키는 최소 수정만).
`Ralph/Services/RalphLogger.cs` 는 not-null 도입을 위해 변경한다.

---

## 2. 조사 결과

### 2.1 `logger?.` 사용 위치 (총 ~80건)

`grep -nE "logger\?\." Ralph/Services/ Ralph/Commands/` 결과를 파일별로 집계:

| 파일 | 건수 | 형태 |
|---|---|---|
| `Ralph/Services/ClaudeService.cs` | 25 | `RalphLogger? logger` 매개변수, `logger?.X(...)` |
| `Ralph/Services/WorktreeService.cs` | 20 | 메서드 매개변수 `RalphLogger? logger`, `logger?.X(...)` |
| `Ralph/Services/GitService.cs` | 16 | 메서드 매개변수 `RalphLogger? logger`, `logger?.X(...)` |
| `Ralph/Services/NotificationService.cs` | 3 | 매개변수 |
| `Ralph/Services/VerificationRunner.cs` | 2 | 매개변수 |
| `Ralph/Services/BudgetGate.cs` | 2 | `private readonly RalphLogger? _logger` 필드 |
| `Ralph/Services/PlanGenerator.cs` | 1 | 매개변수 |
| `Ralph.Tests/ClaudeServiceLargePromptTests.cs` | 1 | mock IAgentRunner의 stub 시그니처 |

호출자 추적: 모든 진입 명령(`PlanCommand`, `RunCommand`, `DryRunCommand`, `InteractiveCommand`,
`SingleTaskCommand`, `WorktreeCleanupCommand`)이 `using var logger = new RalphLogger();`
로 **항상 비-null 인스턴스**를 만들어 서비스에 넘긴다. 즉 운영 코드 경로에는
`logger == null`이 절대 들어오지 않는다. 현재의 `?` 는 **테스트 호출 편의** 한 가지를
위한 코드 스멜이다.

### 2.2 빈 / 주석-only catch 위치 (총 ~25건)

`grep -rEn "catch[^{]*\{[[:space:]]*(/\*[^*]*\*/[[:space:]]*)?\}" Ralph/Services/ Ralph/Commands/` 결과를
**의미별로 분류**:

**Group A — tmp 파일/스냅샷 정리 (의도적 best-effort, 유지)**
- `Ralph/Services/RollbackService.cs:54` — pre-plan 캡처 후 stale post-plan 삭제
- `Ralph/Services/RollbackService.cs:77, 83, 88` — `ClearAll`/`ClearPrePlan`/`ClearPostPlan`의 `File.Delete`
- `Ralph/Services/RollbackService.cs:206` — atomic write 실패 시 tmp 정리
- `Ralph/Services/StateStore.cs:203` — atomic save 실패 시 tmp 정리
- `Ralph/Services/PlanGenerator.cs:139` — tasks.json atomic write 실패 시 tmp 정리

**Group B — 외부 자원 fallback (의도적, 유지하되 logger 도입)**
- `Ralph/Services/CostTracker.cs:247` — `~/.ralph/pricing.json` 읽기 실패 → embedded fallback
- `Ralph/Services/CostTracker.cs:263` — embedded resource 읽기 실패 → hardcoded fallback
- `Ralph/Services/CostTracker.cs:302, 348` — cost.jsonl의 깨진 라인 skip (`JsonException` 한정)
- `Ralph/Commands/PlanCommand.cs:85` — 깨진 기존 tasks.json 무시 후 default categories
- `Ralph/Commands/PlanPromptCommand.cs:42` — 동일

**Group C — 프로세스 강제 종료 / 취소 경로 (의도적, 유지)**
- `Ralph/Services/ClaudeService.cs:123, 444, 488, 489, 491` — spinner OCE / stdin OCE / Kill / WaitForExitAsync 시 OCE / stdin 잔여 task drain
- `Ralph/Services/VerificationRunner.cs:56, 57, 61, 62` — 동일 (Kill / WaitForExitAsync / exitTask drain)
- `Ralph/Services/ParallelExecutor.cs:347` — 백그라운드 refresh task drain `OperationCanceledException`

**Group D — 정말로 silent해서 위험한 케이스 (수정 대상)**
- `Ralph/Services/WorktreeService.cs:704` — `Directory.Delete(_worktreeBase, true)`. 디렉터리
  삭제 실패가 silent. 후속 cleanup도 같은 디렉터리에서 출발하므로 이 실패는 누적된다.
- `Ralph/Services/TaskProgressTracker.cs:51` — `cost.GetTotalUsdAsync` 실패. progress 표가
  깨지는 것보단 낫다는 의도지만 **실패 누적이 silent**라 budget gate 진단을 흐리게 한다.

### 2.3 PromptBuilder null masking

`Ralph/Services/PromptBuilder.cs:88`:
```csharp
sb.AppendLine(task.Prompt ?? "(prompt 미지정)");
```
이 한 줄 때문에 `Prompt == null`인 task가 plan 단계에서 누락되어도 run까지 흘러가
빈 prompt로 Claude를 호출한다. `PlanValidator.cs:71-72`는 빈 prompt를 **warning**으로만
잡고 있어 `--plan` 종료 코드에 영향을 주지 않는다.

---

## 3. logger null-able 제거 전략

### 3.1 선택지 비교

| 안 | 변경 폭 | 테스트 영향 | 안전성 |
|---|---|---|---|
| (A) `RalphLogger logger` 비-null 강제, default 인자 제거 | 시그니처 ~80곳 + 테스트 ~10곳 | 모든 테스트가 `RalphLogger`를 만들어 넘김 — 임시 dir 필요 | 강함 |
| (B) `RalphLogger.Null` 정적 인스턴스 도입, default 값으로 사용 | 시그니처 동일 형태(`= RalphLogger.Null`) + `?` 만 제거 | 테스트 영향 거의 없음 | 강함 |
| (C) `IRalphLogger` 인터페이스 + `NullLogger` | 인터페이스 도입, 모든 사용처 swap | 큼, 본 fix 범위 초과 | 강함 |

**채택: (B)**. 이유:
- 운영 코드에서 logger 주입은 이미 100% 비-null. `?` 만 빼면 끝.
- 테스트는 `RalphLogger`를 만들면 디스크 I/O가 발생해 격리에 부적합. 정적 `Null`
  인스턴스 한 개로 모든 테스트가 영향 없이 컴파일된다.
- 인터페이스 추출(C)은 같은 효과를 더 큰 변경 폭으로 달성. fix5는 silent error 정리에
  집중하므로 abstraction 추가는 후속 fix로 분리한다.

### 3.2 `RalphLogger`에 `Null` 인스턴스 추가

현재 `Ralph/Services/RalphLogger.cs`는 `sealed`이고 ctor에서 디스크 파일을 연다.
정적 `Null` 인스턴스가 디스크를 만들면 안 되므로 다음 중 한 가지로 바꾼다:

**선택지 B-1 (권장)** — `RalphLogger`에 `protected` ctor 추가하고 클래스에서 `sealed` 제거.
내부 nested `private sealed class NullLogger : RalphLogger { ... }`가 모든 메서드를 no-op로
override. `public static readonly RalphLogger Null = new NullLogger();` 노출.

```csharp
// Ralph/Services/RalphLogger.cs (변경 후)
public class RalphLogger : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly object _lockObj = new();

    public string LogFile { get; }

    public static RalphLogger Null { get; } = new NullLogger();

    public RalphLogger(string logDir = RalphPaths.LogDir)
    {
        Directory.CreateDirectory(logDir);
        LogFile = Path.Combine(logDir, $"ralph-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _writer = new StreamWriter(LogFile, append: true) { AutoFlush = true };
        _writer.WriteLine($"Ralph session started at {DateTime.Now}");
    }

    // NullLogger 전용 보호 ctor — 디스크 I/O 없음.
    protected RalphLogger()
    {
        LogFile = "";
        _writer = null;
    }

    public virtual void Log(string level, string message)
    {
        if (_writer is null) return; // NullLogger 안전망
        lock (_lockObj)
        {
            _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}");
        }
    }

    public void Info(string message) => Log("INFO", message);
    public void Warn(string message) => Log("WARN", message);
    public void Error(string message) => Log("ERROR", message);

    public void TaskStart(string taskId, string title)
        => Info($"=== Task started: {taskId} - {title} ===");

    public void TaskEnd(string taskId, string status)
        => Info($"=== Task ended: {taskId} - status: {status} ===");

    public virtual void Dispose()
    {
        lock (_lockObj) { _writer?.Dispose(); }
    }

    private sealed class NullLogger : RalphLogger
    {
        public override void Log(string level, string message) { }
        public override void Dispose() { }
    }
}
```

요점:
- `RalphLogger.Null`은 모든 호출에서 no-op. ctor가 디스크를 만들지 않는다.
- 운영 동작은 정확히 동일 — `Log`가 가상이지만 default 구현이 기존과 동일.
- `LogFile`은 `Null`에서 빈 문자열. `DisplayHelpers.cs:36`에서 `logger.LogFile`을
  사용하지만 운영 경로에선 진짜 logger만 들어가므로 빈 문자열이 화면에 노출되지 않는다.

### 3.3 시그니처 변경 규칙

다음을 **일괄 적용**한다:

1. `RalphLogger? logger = null` → `RalphLogger? logger = null` 그대로 두지 않는다.
   → `RalphLogger logger` (default 인자 제거)
   - **이유**: 호출자가 명시적으로 `RalphLogger.Null`을 넘기게 강제. "logger 안 넘기면
     사라진다"는 함정을 코드 시그니처에서 차단.
   - 호출자 측면에서 모든 운영 경로는 이미 logger를 넘기고 있으므로 보일러플레이트가
     늘어나지 않는다.

2. `_logger?.X(...)` / `logger?.X(...)` 호출부의 `?`를 모두 제거. **수정 패턴은 단순
   문자열 치환** (`logger?.` → `logger.`, `_logger?.` → `_logger.`).

3. `BudgetGate`의 `private readonly RalphLogger? _logger`도 `RalphLogger _logger`로 변경.
   ctor `RalphLogger? logger = null` 시그니처를 `RalphLogger logger`로 강제. 모든 호출처
   (`RunCommand` 등)는 이미 logger를 만들어 넘기고 있으므로 영향 없음.

4. `IAgentRunner` 인터페이스 시그니처(`Ralph/Services/IAgentRunner.cs:32, 44`)의
   `RalphLogger? logger = null` 도 `RalphLogger logger` (default 제거)로 변경. 구현체
   (`ClaudeService`)와 mock 구현(`Ralph.Tests/ClaudeServiceLargePromptTests.cs`의 stub)도
   동시에 수정.

5. **테스트 측 호출**: 기존에 `logger: null` 또는 logger를 안 넘기던 테스트는
   `RalphLogger.Null`을 명시. `Ralph.Tests/` 전체에서 변경이 필요한 호출 위치는
   `grep -rn "logger:\|new ClaudeService\|new GitService\|new WorktreeService"`로 한 번에 추출.

### 3.4 Group D 수정에서 logger 주입 필요 위치

위 시그니처 변경의 부수 작용으로, 현재 logger를 받지 않는 메서드 두 곳에 logger
파라미터를 추가한다 (Group D 수정과 함께):
- `WorktreeService.CleanupAllAsync` — 이미 `RalphLogger? logger` 매개변수 보유.
  `Directory.Delete` 실패 시 logger.Warn 추가.
- `TaskProgressTracker.RefreshCostAsync` — 현재 logger 미보유. `TaskProgressTracker`
  생성자에 `RalphLogger logger` 추가하고 `RefreshCostAsync` 안에서 catch에 logger.Warn 추가.
  호출처(`ParallelExecutor.RunAsync`)는 이미 logger를 들고 있으므로 주입만 하면 된다.

### 3.5 검증 절차 (정적 검사)

PRD §5 검증 요건:
- `grep -rn "logger?\." Ralph/Services/ Ralph/Commands/` → **0건이 되어야 함**.
- `grep -rn "RalphLogger?" Ralph/Services/ Ralph/Commands/` → 0건 (필드/매개변수에서 nullable 사라짐).

CI 또는 로컬 verification 단계에서 위 두 grep을 실행하고 결과가 비어 있는지 확인. impl
단계 막바지에 `verification.command`에 한 줄로 추가 가능:
```sh
! grep -rn "logger\?\\." Ralph/Services Ralph/Commands
```
(exit 0이면 0건 = 통과)

---

## 4. 빈 catch 처리 정책

### 4.1 분류와 처리 방식

§2.2의 4개 그룹을 그대로 따라간다.

**Group A — tmp 파일 정리**: 이미 outer catch에서 본 예외를 throw하거나 결과를 반환한
직후의 cleanup이다. 여기서 또 throw하면 원인 예외가 가려진다. **유지**하되 한 줄 주석을
표준화:
```csharp
catch { /* tmp 정리 실패는 의도적 무시: 원인 예외 보존이 우선 */ }
```

**Group B — fallback 경로**: pricing.json을 읽지 못해도 hardcoded fallback이 있다.
사용자가 이를 알 수 있도록 **logger.Warn 한 줄을 추가**:
```csharp
catch (Exception ex)
{
    logger.Warn($"~/.ralph/pricing.json 로드 실패 — embedded fallback 사용: {ex.Message}");
}
```

CostTracker는 현재 logger를 받지 않는다. 두 가지 선택:
- (i) 생성자에 `RalphLogger logger` 추가 — 영향 큼 (`CostTracker` 인스턴스화 지점이 다수).
- (ii) `LoadPricing()` 같은 정적 helper에 logger 매개변수만 추가하고 호출자가 넘김.

**(ii) 채택**. `LoadPricing()`은 `BuildPriceMap()` 정적 메서드에서 호출되며 한 번만 일어난다.
시그니처에 `RalphLogger logger` 추가 → 호출자 `EnsureHydratedAsync`에서 인스턴스 필드의
logger를 넘기도록 한다. 결과적으로 `CostTracker` 생성자에 `RalphLogger logger` 매개변수만
추가하면 된다 (또한 4.3에서 다루는 jsonl 파싱 실패에도 logger가 필요하므로 필드로 가져가는
편이 자연스럽다).

깨진 jsonl 라인(`CostTracker.cs:302, 348`)은 빈도가 비교적 흔하고 대부분 의도적 로테이션
잔존물이라 매번 Warn 출력하면 노이즈. **카운터로 누적 + 로드 종료 시 한 번 Warn**:
```csharp
int malformed = 0;
await foreach (var line in File.ReadLinesAsync(LogFilePath, ct))
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    try { ... }
    catch (JsonException) { malformed++; }
}
if (malformed > 0)
    logger.Warn($"cost.jsonl: {malformed}개 라인이 깨져 있어 무시됨 — 누적 비용이 누락될 수 있습니다");
```

**Group C — 프로세스 종료/취소 경로**: `Process.Kill`, `WaitForExitAsync`,
드레인용 `await stdinTask`의 OCE는 정의상 무시해도 안전(이미 cancel/kill 처리됨).
**유지**, 주석 표준화:
```csharp
catch { /* 종료 후 잔여 task drain — 추가 진단 가치 없음 */ }
```

`ClaudeService.cs:444`의 `catch (OperationCanceledException) { /* handled by outer catch below */ }`는
의미 명확. 그대로 유지.

**Group D — 위험한 silent catch**: logger.Warn으로 변환.
- `WorktreeService.cs:704`:
  ```csharp
  try { Directory.Delete(_worktreeBase, true); }
  catch (Exception ex)
  {
      logger.Warn($"worktree 베이스 디렉터리 삭제 실패 ({_worktreeBase}): {ex.Message} — 'ralph --worktree-cleanup'으로 재시도하세요");
  }
  ```
- `TaskProgressTracker.cs:51`:
  ```csharp
  try { _cachedTotalUsd = await _cost.GetTotalUsdAsync(ct); }
  catch (OperationCanceledException) { throw; }
  catch (Exception ex)
  {
      _logger.Warn($"[progress] cost 조회 실패 — 대시보드 표시는 계속됨: {ex.Message}");
  }
  ```
  (`OperationCanceledException`은 dashboard refresh task 종료 신호이므로 propagate.)

### 4.2 표준 패턴 정리 (impl이 적용할 템플릿)

| 의도 | 패턴 |
|---|---|
| 정말로 무시 (tmp/잔여 task) | `catch { /* 사유 */ }` 그대로, 주석 표준화 |
| OCE만 swallow | `catch (OperationCanceledException) { /* 사유 */ }` |
| 재시도/추가 보고 가치 있음 | `catch (Exception ex) { logger.Warn($"{콘텍스트} 실패: {ex.Message}"); }` |
| Cancel은 propagate, 그 외는 보고 | `catch (OperationCanceledException) { throw; } catch (Exception ex) { logger.Warn(...); }` |

### 4.3 검증 (정적 검사)

PRD §5 검증 요건:
> `grep -rn "catch.*{[^}]*}" Ralph/Services/`에서 빈 catch 또는 주석만 있는 catch 0건.

엄격히 적용하면 Group A·C도 잡힌다. 본 fix는 Group A·C는 정당화된 경우로 간주하므로
**검증 grep은 한 단계 완화**: `// 사유` 주석이 포함되지 않은 빈 catch만 잡는다.
구현 단계에서 다음 grep을 사용:
```sh
grep -rEn "catch[^{]*\{[[:space:]]*\}" Ralph/Services Ralph/Commands
```
(주석조차 없는 빈 catch가 0건이어야 함)

추가로 §2.2 Group D 분류 항목이 모두 logger.Warn으로 바뀌었는지 manual 점검 (체크리스트):
- [ ] `WorktreeService.cs:~704`
- [ ] `TaskProgressTracker.cs:~51`
- [ ] `CostTracker.cs:~247, ~263, ~302, ~348` (logger 주입 + Warn)
- [ ] `PlanCommand.cs:~85` (`logger.Warn($"기존 tasks.json 로드 실패 — default categories 사용: {ex.Message}");`)
- [ ] `PlanPromptCommand.cs:~42` (동일)

---

## 5. PromptBuilder null masking 제거 + Validator 강화

### 5.1 변경 1 — `Ralph/Services/PromptBuilder.cs:88`

```csharp
// 변경 전
sb.AppendLine(task.Prompt ?? "(prompt 미지정)");

// 변경 후
sb.AppendLine(task.Prompt!);
```

**이유**: `PlanValidator`에서 빈 prompt가 error로 잡히도록 강화하므로(§5.2),
이 시점에 `task.Prompt`가 null/empty일 가능성은 정상 흐름에서 제거된다. 비정상
경로(plan 우회 / 수동 편집된 tasks.json)에서 만약 null이라면 NullReferenceException으로
**큰 소리로 실패**하는 편이 silent 마스킹보다 낫다.

추가 안전장치를 원한다면 `WorktreeTaskRunner`/`SequentialRunner`가 task를 dispatch하기
직전에 `Build`를 호출하기 전 `task.Prompt`가 비었는지 한 번 더 검사하고 Error 메시지를
띄울 수 있다. 단, 본 fix에서는 PlanValidator 강화로 충분하다고 보고 별도 가드는
넣지 않는다 (의도된 lean 변경).

### 5.2 변경 2 — `Ralph/Services/PlanValidator.cs:71-72`

```csharp
// 변경 전
if (string.IsNullOrWhiteSpace(task.Prompt))
    report.Warnings.Add($"'{task.Id}'의 prompt가 비어있습니다");

// 변경 후
if (string.IsNullOrWhiteSpace(task.Prompt))
    report.Errors.Add($"'{task.Id}'의 prompt가 비어있습니다 — task가 의미 있는 작업 지시를 가져야 합니다");
```

**효과**:
- `--plan` 단계의 자동 보정 루프(`PlanCommand`가 `BuildCorrectionPrompt`로 Claude에
  재생성 요청하는 흐름)에 `errors`로 들어가 재생성 트리거. 빈 prompt가 plan에서 절대
  통과하지 못한다.
- `--validate` 명령이 빈 prompt task에서 종료 코드 1을 반환.
- `--run` 직전의 PlanValidator가 (있다면) error로 차단.

### 5.3 변경 3 — 회귀 검증 테스트

**본 fix의 산출물 범위는 plan 단계이므로 테스트 코드 작성은 후속 impl 단계에서 처리**.
단, impl 단계가 다음 단위 테스트를 추가하도록 명시:

- `Ralph.Tests/PlanValidatorTests.cs` (신규 또는 기존 확장):
  `PlanValidator_Reports_Empty_Prompt_As_Error()` — `Prompt = null`인 task가
  `report.Errors`에 들어가고 `IsClean == false`임을 확인.

---

## 6. 변경 영향 파일 전체 목록과 변경 폭

| 파일 | 변경 폭 추정 | 변경 내용 |
|---|---|---|
| `Ralph/Services/RalphLogger.cs` | +30 / −5 | `sealed` 제거, `protected` ctor, `NullLogger` nested, `Null` 정적 인스턴스, `Log`/`Dispose` `virtual` |
| `Ralph/Services/IAgentRunner.cs` | ~4 | 시그니처에서 `RalphLogger?` → `RalphLogger`, default 제거 |
| `Ralph/Services/ClaudeService.cs` | ~30 | 매개변수 nullable 제거 + `?.` 25건 제거 |
| `Ralph/Services/WorktreeService.cs` | ~22 | 동일 + `Directory.Delete` 실패 catch에 logger.Warn |
| `Ralph/Services/GitService.cs` | ~18 | 동일 |
| `Ralph/Services/NotificationService.cs` | ~4 | 동일 |
| `Ralph/Services/VerificationRunner.cs` | ~3 | 동일 (Group C 빈 catch는 유지) |
| `Ralph/Services/BudgetGate.cs` | ~4 | 필드/매개변수 nullable 제거 + `_logger?.` → `_logger.` |
| `Ralph/Services/PlanGenerator.cs` | ~3 | 매개변수 nullable 제거 |
| `Ralph/Services/CostTracker.cs` | ~20 | 생성자에 logger 추가, fallback Warn 4곳 |
| `Ralph/Services/TaskProgressTracker.cs` | ~10 | 생성자에 logger 추가, RefreshCost catch에 Warn |
| `Ralph/Services/PromptBuilder.cs` | 1 | `?? "(prompt 미지정)"` 제거 |
| `Ralph/Services/PlanValidator.cs` | 1 | Warning → Error 메시지 |
| `Ralph/Commands/PlanCommand.cs` | 1 | `catch { /* best-effort... */ }` → logger.Warn |
| `Ralph/Commands/PlanPromptCommand.cs` | ~3 | 동일 (logger 주입 + Warn). PlanPromptCommand는 현재 logger를 만들지 않으니 `using var logger = new RalphLogger();` 추가 또는 `RalphLogger.Null` 사용 결정 |
| `Ralph/Commands/RunCommand.cs` 외 | 0~약간 | `new TaskProgressTracker(...)`, `new CostTracker(...)` 호출에 logger 추가 |
| `Ralph.Tests/ClaudeServiceLargePromptTests.cs` | ~3 | mock `RunStreamAsync` 시그니처에서 `RalphLogger?` → `RalphLogger`, 호출자 `RalphLogger.Null` |
| `Ralph.Tests/*.cs` (기타) | ~10~20 | logger를 안 넘기던 테스트가 `RalphLogger.Null` 명시 |

총 변경 LOC 추정: **~150 LOC 추가/수정** (대부분이 mechanical 시그니처/호출 변경).
신규 클래스/파일은 없음. 새 의존성도 없음.

### 6.1 PlanPromptCommand의 logger 처리

`PlanPromptCommand`는 출력만 하는 read-only 명령이라 현재 logger를 만들지 않는다. logger를
요구하는 함수(없음)에는 `RalphLogger.Null`을 넘긴다. catch에서 `logger.Warn`을 호출해야
하는데 `Null`이면 silent하다 — 그러나 이 catch는 디스플레이용 read이고, 실패해도 사용자가
콘솔에 출력하면 즉시 알 수 있다. 따라서 다음 두 안 중 후자를 채택:
- (a) `using var logger = new RalphLogger();` 새로 생성. 잠시 보는 명령용으로 디스크 파일
  생성은 과함.
- (b) `AnsiConsole.MarkupLine($"[yellow]경고: 기존 tasks.json 로드 실패 ({ex.Message}) — default categories 사용[/]");`로
  콘솔에 직접 알림. **채택**.

### 6.2 동시 실행 task와의 충돌 가능성

병렬 실행 중인 `fix7-failure-tests-impl`은 `Ralph.Tests/BudgetCancelConsistencyTests.cs`,
`Ralph.Tests/ClaudeServiceFailureTests.cs`만 수정한다. 본 fix가 `Ralph.Tests/` 안의
mock 시그니처 변경을 한다면 **`ClaudeServiceFailureTests.cs`에서 충돌 가능성**.

회피: 본 fix의 plan 산출물은 `docs/plans/fix5-silent-errors-plan.md` 한 개로 한정된다.
시그니처 변경의 실제 적용은 후속 impl 단계에서, fix7 impl과 fix5 impl이 같은 batch에
들어가지 않도록 `dependsOn`에 직렬화 마커를 두거나 plan 결과를 바탕으로 두 impl이 같은
test 파일을 건드린다면 한쪽이 후행되도록 plan generator에 반영하는 것을 권장한다.

---

## 7. 검증 절차

### 7.1 정적 검사 (필수, PRD 명시)

다음 grep이 모두 빈 결과여야 한다:
```sh
grep -rn "logger\?\\." Ralph/Services/ Ralph/Commands/
grep -rn "RalphLogger\?"  Ralph/Services/ Ralph/Commands/
grep -rEn "catch[^{]*\{[[:space:]]*\}" Ralph/Services Ralph/Commands
```

마지막 grep은 **주석조차 없는 빈 catch**를 잡는다. Group A·C의 정당한 swallow는 사유
주석이 포함되어 통과한다.

### 7.2 빌드/테스트

- `dotnet build` 통과.
- `dotnet test` 전 테스트 통과. logger 시그니처 변화로 컴파일 깨지는 테스트는 fix5
  impl 단계에서 함께 수정.

### 7.3 수동 점검 체크리스트

- [ ] `docs/plans/fix5-silent-errors-plan.md` §2.2 Group D 5개 위치가 모두 logger.Warn 또는
  AnsiConsole.MarkupLine으로 변환됨.
- [ ] `RalphLogger.Null`이 디스크 파일을 생성하지 않음 (테스트 1개로 회귀 방지).
- [ ] `PlanValidator.Validate`가 빈 prompt task에 대해 `Errors`에 메시지를 추가 (단위 테스트).
- [ ] `PlanCommand`의 자동 보정 루프가 빈 prompt error를 받으면 재생성 시도 (기존 흐름이
  `report.Errors`를 보정 prompt에 포함시킴 — 별도 변경 불필요, manual 확인만).

### 7.4 주의: 동작 변화로 인한 잠재 회귀

- `task.Prompt`가 null인 tasks.json을 가진 사용자가 `--run`을 돌리면 PromptBuilder에서
  NullReferenceException이 발생한다. 이는 의도된 변화 — silent 마스킹보다 낫다 — 이지만,
  사용자가 이전에는 통과했던 입력이 이제 실패한다. CHANGELOG에 명시 필요.
  - **완화**: `PlanValidator`가 `--validate`/`--plan`에서 error로 잡으므로, 정상 워크플로우를
    따른 사용자는 영향이 없다.
- `CostTracker`가 logger를 요구하면 외부에서 `CostTracker`를 직접 생성하던 테스트/툴이
  수정 필요. fix5 impl이 모두 잡아 수정한다.

---

## 8. 비목표 / 후속 작업

- **`IRalphLogger` 인터페이스 추출** — 본 fix는 `RalphLogger` 클래스 + `Null` 인스턴스로
  충분. 추가 abstraction은 별도 PR.
- **CostTracker의 cost.jsonl rotation/compact** — 깨진 라인이 누적되는 경우의 근본
  원인은 별도 fix 필요. 본 fix는 보고만 한다.
- **`ParallelExecutor.cs:347` OCE swallow를 logger trace로 강화** — 이미 의도된 drain.
  본 fix 범위 외.
- **모든 빈 catch 일괄 제거** — Group A/C는 정당한 케이스. 본 fix는 Group D만 제거.
- **테스트 분리 PR (fix7과의 충돌 회피)** — fix5 impl이 test mock 시그니처를 건드리는
  순간 fix7 impl과 conflict 가능. impl 단계에서 두 task를 직렬화하거나 한쪽이 다른
  쪽의 변경을 흡수.
