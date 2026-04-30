# Ralph 코드베이스 개선 사항 (Fix 1차)

본 문서는 코드 분석에서 발견된 실패 경로 취약점을 우선순위 순으로 정리한 작업 명세입니다.
각 항목은 독립 실행 가능하며, 위에서부터 구현/검증 순서대로 처리하는 것을 권장합니다.

---

## 1. [P0] 머지 배치 partial-failure 시 batch abort

### 문제
`Ralph/Services/MergeOrchestrator.cs:147-160`에서 `MarkTaskDoneThreadSafeAsync`가
실패해도 로그만 남기고 다음 task로 진행한다. 결과적으로 git merge는 성공했지만
`.ralph-logs/state.json` 쓰기가 실패한 task가 생기면, 다음 실행 시
**이미 머지된 task가 다시 디스패치**되어 충돌/중복 작업이 발생한다.

```csharp
// 현재 코드 (문제)
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(taskId)} done 마킹 실패: ...");
    _logger.Error($"MarkTaskDone failed for {taskId}: {ex.Message}");
    // ← 여기서 batch가 계속 진행됨
}
```

### 요구사항
- `MarkTaskDoneThreadSafeAsync` 실패는 해당 batch를 즉시 중단해야 함.
- 중단 시 사용자에게 "state.json 쓰기 실패로 batch 중단; 수동 복구 필요" 명확히 안내.
- 이미 done 마킹된 task와 안 된 task를 분리하여 보고.
- 가능하면 state.json 쓰기 자체에 한해 짧은 재시도(최대 2회, 100ms 간격) 추가.

### 검증
- `Ralph.Tests/`에 통합 테스트 추가:
  - 2개 task batch에서 첫 번째 task의 `StateStore.MarkTaskDoneAsync`가 IOException을 던지도록 강제.
  - batch가 두 번째 task의 머지를 진행하지 않고 중단되는지 확인.
  - 종료 후 state.json이 일관된 상태(첫 번째 task는 미완료 표시)인지 확인.

### 영향 파일
- `Ralph/Services/MergeOrchestrator.cs`
- `Ralph/Services/StateStore.cs` (재시도 로직 추가 시)
- `Ralph.Tests/ParallelExecutorTests.cs` 또는 신규 `MergeOrchestratorFailureTests.cs`

---

## 2. [P0] Cleanup 타임아웃 — Ctrl+C 행(hang) 방지

### 문제
`Ralph/Services/ParallelExecutor.cs:369`의 finally 블록 cleanup이
`CancellationToken.None`으로 호출되어 Ctrl+C가 무시된다.

```csharp
if (!await _worktree.CleanupWorktreeAsync(taskId, _logger, CancellationToken.None))
```

NFS, 네트워크 마운트, 또는 `git worktree remove`가 잠금에 걸리면 프로세스가
**무한정 행걸리고 사용자의 추가 Ctrl+C도 먹지 않는다**.

### 요구사항
- finally 블록의 cleanup에 30초 타임아웃 적용.
- 타임아웃 발생 시 Warn 로그 + `_cleanupFailures` 증가.
- 사용자 Ctrl+C는 cleanup을 즉시 중단할 수 있어야 함 (단, partial cleanup은 후속 `--worktree-cleanup` 명령으로 처리되도록 안내 메시지 출력).
- `ParallelExecutor.cs:117`, `:180`의 `CleanupAllAsync` 호출에도 동일한 타임아웃 보호 적용 검토.

### 구현 힌트
```csharp
using var cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
cleanupCts.CancelAfter(TimeSpan.FromSeconds(30));
try
{
    if (!await _worktree.CleanupWorktreeAsync(taskId, _logger, cleanupCts.Token))
        Interlocked.Increment(ref _cleanupFailures);
}
catch (OperationCanceledException)
{
    _logger.Warn($"Cleanup timed out for {taskId}; run 'ralph --worktree-cleanup' to recover");
    Interlocked.Increment(ref _cleanupFailures);
}
```

### 검증
- `WorktreeService.CleanupWorktreeAsync`를 mock으로 30초 이상 대기시키는 테스트.
- 타임아웃 후 프로세스가 정상 종료되는지 확인.

### 영향 파일
- `Ralph/Services/ParallelExecutor.cs`

---

## 3. [P1] Git 출력 locale 의존성 제거

### 문제
`Ralph/Services/WorktreeService.cs:244-275`의 `ParseUntrackedOverwrites`가
영어 메시지("untracked working tree files would be overwritten by merge")를
문자열 검색한다. CI나 사용자 환경에서 `LANG=ko_KR` / `LANG=de_DE`이면
**파싱 실패 → 빈 리스트 → 같은 머지 재시도 → 같은 실패** 무한 루프 또는
잘못된 분기.

### 요구사항
- 모든 git 호출에 `LC_ALL=C` (또는 `LANG=C`) 환경 변수 강제 주입.
- `GitService` 레벨에서 일괄 처리하는 것이 이상적 (모든 git invocation이 일관되게).
- 파싱이 실패한 경우(예상 패턴 못 찾음) 명시적으로 Warn 로그 — 현재는 silent.

### 구현 힌트
```csharp
// GitService에서 ProcessStartInfo 생성 시
psi.Environment["LC_ALL"] = "C";
psi.Environment["LANG"] = "C";
```

### 검증
- 테스트 환경에서 `LANG=de_DE.UTF-8`로 git 명령을 실행해도
  `ParseUntrackedOverwrites`가 정상 동작하는지 확인.
- (현실적으로 어렵다면) `git status --porcelain` 같은 머신 파서블 출력으로
  대체 가능한지 검토.

### 영향 파일
- `Ralph/Services/GitService.cs`
- `Ralph/Services/WorktreeService.cs`

---

## 4. [P1] Claude CLI 에러 분류

### 문제
`Ralph/Services/ClaudeService.cs`에서 다음 실패가 모두 동일한 `Success=false`로 뭉뚱그려진다:
- `claude` 바이너리 없음 (permanent — 재시도 무의미)
- 권한 거부 (permanent)
- 네트워크 타임아웃 (transient — 재시도 가치 있음)
- 레이트 리밋 (transient — backoff 후 재시도)
- 깨진 JSON stderr (불명 — 진단 필요)

분류가 없어서 permanent 실패도 `MAX_RETRIES`회 재시도하며 시간을 낭비하거나,
반대로 transient 실패를 빨리 포기할 위험.

### 요구사항
- `ClaudeFailureKind` enum 추가: `BinaryNotFound`, `PermissionDenied`, `Timeout`, `RateLimited`, `MalformedOutput`, `Unknown`.
- exit code + stderr 텍스트 + 예외 타입 기반으로 분류.
- 재시도 정책:
  - `BinaryNotFound`, `PermissionDenied` → 즉시 fail-fast.
  - `Timeout`, `RateLimited` → backoff 재시도.
  - `MalformedOutput`, `Unknown` → 1회만 재시도.
- 재시도 시 어떤 분류로 판단했는지, 어떤 backoff을 썼는지 로그.
- 레이트 리밋 시 server-supplied `retry-after` 값을 실제로 썼는지 vs 추정값을 썼는지 명시 로그.

### 검증
- `Ralph.Tests/`에 mock `IAgentRunner`로 각 실패 케이스 시뮬레이션.
- 분류별로 재시도 횟수가 정책대로 동작하는지 확인.

### 영향 파일
- `Ralph/Services/ClaudeService.cs`
- `Ralph/Services/IAgentRunner.cs` (필요 시 시그니처 확장)

---

## 5. [P2] Silent error swallowing 정리

### 문제
코드 전반에 두 가지 안티패턴:

**(a) Null-coalesce logger:**
```csharp
logger?.Error("...");  // logger가 null이면 에러가 사라짐
```

**(b) Best-effort try/catch:**
```csharp
try { File.Delete(path); }
catch { /* best-effort */ }  // 실패 이유를 영원히 모름
```

발견 위치 (대표):
- `Ralph/Services/RollbackService.cs:58, 81, 82`
- `Ralph/Services/WorktreeService.cs:697`
- `Ralph/Services/PromptBuilder.cs:88` (null prompt를 "(prompt 미지정)"으로 마스킹)

### 요구사항
- `logger?.X(...)` → 호출부에서 logger 비-null 보장 (생성자 주입 필수화) 또는
  `NullLogger` 인스턴스 주입.
- `catch { /* best-effort */ }` → `catch (Exception ex) { logger.Warn($"... failed: {ex.Message}"); }`
- `PromptBuilder`의 null prompt 마스킹은 제거하고, null prompt를 일찍 발견하도록 `PlanValidator`에서 검증 추가.

### 검증
- 정적 검사: `grep -rn "logger?\." Ralph/Services/` 결과가 0이 되어야 함.
- 정적 검사: `grep -rn "catch.*{[^}]*}" Ralph/Services/` 에서 빈 catch 또는
  주석만 있는 catch 0건.

### 영향 파일
- 대부분의 `Ralph/Services/*.cs`

---

## 6. [P2] 매직 스트링 중앙화

### 문제
경로/접두사가 수십 군데 하드코딩:
- `.ralph-logs` (StateStore, RollbackService, LogRotator, CostTracker, RalphLogger 등)
- `.ralph-worktrees`
- `ralph/{taskId}` 브랜치 prefix
- `branch.{name}.ralphManaged` config key

한 곳만 바꿔도 cleanup/branch detection이 silently 망가질 위험.

### 요구사항
- 신규 정적 클래스 `Ralph/Services/RalphPaths.cs` (또는 `Constants.cs`):
  ```csharp
  public static class RalphPaths
  {
      public const string LogDir = ".ralph-logs";
      public const string WorktreeDir = ".ralph-worktrees";
      public const string BranchPrefix = "ralph/";
      public const string ManagedConfigKeyTemplate = "branch.{0}.ralphManaged";
      public const string StateFile = "state.json";
      public const string CostLedger = "cost.jsonl";
      public const string ValidationLedger = "validation.jsonl";
      // ...
  }
  ```
- 모든 하드코딩을 위 상수로 치환.
- 테스트도 동일 상수를 참조하도록 수정 (테스트가 hardcoded path를 검증하면 변경 시 깨짐을 빨리 잡을 수 있음).

### 검증
- `grep -rn "\.ralph-logs" Ralph/` 결과가 `RalphPaths.cs` 한 군데만 남는지 확인.
- 동일하게 `\.ralph-worktrees`, `"ralph/"` 도 검사.

### 영향 파일
- 신규: `Ralph/Services/RalphPaths.cs`
- 수정: 거의 모든 Services + Commands

---

## 7. [P2] 실패 경로 테스트 보강

### 문제
현재 테스트는 happy path 위주. 다음 시나리오에 대한 자동화된 회귀 방지 없음:

| 미커버 시나리오 | 영향 |
|---|---|
| 머지 batch 중 state.json 쓰기 실패 | 위 #1 |
| Cleanup 타임아웃 | 위 #2 |
| 비영어 locale에서 git 출력 파싱 | 위 #3 |
| `claude` 바이너리 없음 / 권한 거부 | 위 #4 |
| Claude stderr가 깨진 JSON 라인 다수 포함 | 진단 어려움 |
| Budget gate 트리거 직후 Ctrl+C | state 일관성 |
| 큰 프롬프트(>64KB pipe buffer)의 stdin 데드락 | hang 위험 |

### 요구사항
- 위 7개 시나리오 각각에 대해 최소 1개 통합 테스트 추가.
- mock 사용은 허용하되, **실패 경로의 의미 있는 부분(state 일관성, 종료 코드,
  사용자 메시지)이 검증되어야** 함 — assertion이 단순히 "예외가 던져졌다"에
  머무르면 안 됨.

### 영향 파일
- `Ralph.Tests/MergeOrchestratorFailureTests.cs` (신규)
- `Ralph.Tests/ParallelExecutorTests.cs` (cleanup timeout 케이스 추가)
- `Ralph.Tests/ClaudeServiceFailureTests.cs` (신규)
- `Ralph.Tests/Helpers/` 의 mock 인프라 확장

---

## 8. [P3] 큰 프롬프트 stdin 데드락 방어

### 문제
`Ralph/Services/ClaudeService.cs:163-172`은 stdin에 prompt 전체를 한 번에 쓴다.
prompt가 OS pipe buffer(보통 Linux 64KB) 초과 시 `WriteAsync`가 reader 대기로
블록될 수 있다. 현재는 `WaitForExitAsync` 의존성에 기대고 있음.

### 요구사항
- 큰 prompt도 안전하게 처리하도록 다음 중 택일:
  - (a) 청크 단위로 비동기 write + flush.
  - (b) 임시 파일에 prompt를 쓰고 `claude` CLI에 파일 경로 전달 (CLI가 지원하는 경우).
  - (c) 현재 구조 유지하되, prompt 크기가 임계치(예: 32KB) 초과 시 Warn 로그 + 모니터링.
- 최소한 prompt 크기를 cost ledger에 기록하여 사후 분석 가능하게.

### 검증
- 256KB prompt를 보내는 테스트 추가 (mock agent runner로 검증; 실 Claude 호출 불필요).

### 영향 파일
- `Ralph/Services/ClaudeService.cs`

---

## 작업 순서 권장

1. **#1, #2를 먼저 처리** (P0 — 운영 위험 직결).
2. **#3, #4** (P1 — 환경/외부 의존 신뢰성).
3. **#5, #6, #7**을 병행 (P2 — 유지보수성/회귀 방지). #6은 다른 작업 충돌을 줄이기 위해 가능하면 먼저.
4. **#8** (P3 — 현재 데이터로는 발생 빈도 낮음).

각 항목 완료 시 별도 PR로 분리하고, 커밋 메시지는 한국어로 작성 (CLAUDE.md 규칙).

---

## 분석 메타데이터

- 분석 대상 버전: v1.32 (commit f9f3025 기준)
- 분석 일자: 2026-04-30
- 코드 규모: ~13,000 LOC, Services 28개, Commands 26개, Tests 19개 파일
- 주요 검토 파일: ParallelExecutor, MergeOrchestrator, WorktreeService, ClaudeService,
  StateStore, RollbackService, PlanValidator, PromptBuilder, GitService
