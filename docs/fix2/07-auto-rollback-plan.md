# Fix2 #7 — Smoke 실패 시 자동 롤백 설계

## 1. 배경

`MergeOrchestrator.MergeAndFinalizeAsync`의 끝부분(`MergeOrchestrator.cs`
L207~208)에서 smoke test가 실패하면 즉시 `return 1`로 batch가 끝난다.
이미 `git merge`로 base 브랜치에 N개의 머지 커밋이 들어간 상태인데,
사용자는 다음 중 하나를 직접 해야 한다.

- `git reset --hard {previous-base}` 로 base를 되돌리고 `state.json`의
  `done` 마커도 직접 삭제,
- 또는 깨진 base 위에서 다음 `--run` 시도 (smoke 실패가 누적).

`RollbackService`(`RollbackService.cs`)는 `--plan` 시점의 pre-plan/post-plan
스냅샷만 다룬다. **batch 단위 스냅샷이 없어** smoke 실패 후 자동 복구가
구조적으로 불가능했다.

`fix2.md` #7 요약:

- opt-in 옵션 `--auto-rollback-on-smoke-fail` (CLI / env / workflow).
- smoke 실패 시 해당 batch에서 base에 들어간 머지 커밋을 자동 revert.
- revert 커밋 메시지에 실패 사유 + smoke 출력 일부 포함.
- 해당 task들의 `state.json` `done`을 다시 pending으로.
- 기본 off — 현재 동작 유지.
- 사용자 working tree에 변경이 있으면 자동 롤백 보류 + 안내.

본 fix는 위 요구를 충족하면서 **이미 머지된 변경분의 안전한 되돌리기**와
**로컬 사용자 작업의 보호** 두 축을 동시에 만족시킨다.

Scope:

- 수정 예정: `Ralph/Services/RollbackService.cs`, `Ralph/Services/MergeOrchestrator.cs`,
  `Ralph/Services/SmokeTestPlanner.cs`, `Ralph/Services/StateStore.cs`,
  `Ralph/Services/ParallelExecutor.cs`, `Ralph/Commands/RunCommand.cs`,
  `Ralph/Commands/CommandContext.cs`, `Ralph/Commands/ArgParser.cs`,
  `Ralph/Models/TasksFile.cs` (workflow 필드 추가).
- 테스트: `Ralph.Tests/AutoRollbackOnSmokeFailTests.cs` (신규) 또는
  `MergeOrchestratorTests`/`RollbackServiceTests` 확장.

---

## 2. 현재 흐름 분석

### 2.1 batch 머지 → smoke test 위치

`MergeOrchestrator.cs`의 핵심 경로:

```
L63   preMergeSha = CaptureBaseShaAsync(baseBranch)   ← base의 머지 직전 HEAD SHA
L72   foreach taskId in taskIds:                      ← 순차 머지 루프
L117    AdvanceWorktreeOntoBaseAsync (rebase)
L148    MergeWorktreeAsync (실제 머지 커밋이 base에 push)
L183    foreach mergedTasks: MarkTaskDoneThreadSafeAsync (state.json done=true)
L207    RunPostMergeSmokeTestAsync(preMergeSha)        ← 실패 시 return 1
```

특징:

- `preMergeSha`는 이미 캡처된다 (smoke의 docs-only 스킵 판단용). **batch
  스냅샷의 핵심 키가 이미 손에 있다** — 새로 git 호출을 추가할 필요 없음.
- smoke 실패는 `int?` 1만 반환하고 종료한다. 호출자(`ParallelExecutor`)는
  실패 결과의 stdout/stderr나 실행 명령에 접근할 수 없다 — 자동 revert
  메시지를 작성하려면 이 인터페이스를 손대야 한다.
- `done` 마킹은 smoke test **이전에** 끝나 있다 (L183~201). 따라서 자동
  롤백이 발동하면 마킹된 task들의 `done`을 되돌리는 작업이 필요하다.

### 2.2 RollbackService 인터페이스 (현재)

`RollbackService.cs`:

- `CaptureBeforePlanAsync` / `CaptureAfterPlanAsync` — `--plan` 전용. 디스크에
  `pre-plan.json` / `post-plan.json` 두 파일만 관리.
- `LoadPrePlanAsync` / `LoadPostPlanAsync` / `RestoreAsync` — `--rollback`
  명령에서만 사용.
- `--run`은 RollbackService를 전혀 건드리지 않는다 (의도적 분리, fix2 #7
  이전까지의 설계).

→ `--run` 흐름에 batch 스냅샷이 살아 있어야 하지만, 디스크에 영구 저장할
필요는 없다. smoke 실패 후 in-process에서 즉시 사용/폐기되는 휘발성
값이다. 별도 파일로 직렬화하지 않고 `MergeOrchestrator` 내부 메모리 변수
또는 `RollbackService`의 in-memory 메서드로 다룬다.

### 2.3 SmokeTestPlanner 결과 노출 (현재)

`SmokeTestPlanner.Plan(...)` → `VerificationSpec?` 반환 (실행할 명령). 실제
실행은 `MergeOrchestrator.RunPostMergeSmokeTestAsync`가 직접 `_verifier.RunAsync`
호출. 실패 시 stdout/stderr는 `RalphLogger`와 콘솔로만 흘러가고 호출자에는
`int? 1`만 돌아간다.

자동 revert 메시지 작성을 위해 호출자가 `VerificationResult`(stdout/stderr/exit/timedOut/duration)에
접근할 수 있어야 한다 — `RunPostMergeSmokeTestAsync`의 반환을
구조체로 풍부화한다 (§3.4).

### 2.4 RunCommand 우선순위 패턴 (참고)

`RunCommand.cs` 및 `CommandContext.cs`에 자리 잡힌 관례:

```
CLI > env > workflow > 기본값
```

`CommandContext`가 CLI/env를 흡수하고, computed property
(`StrictFiles`/`NoSmokeTest`/`BudgetUsd` 등)에서 두 값을 합쳐 다음 단계로
넘긴다. 다음 단계 (`MergeOrchestrator` 생성자 또는 `EffectiveBudgetUsd(tm)`)에서
workflow 값과 머지된다. 본 fix도 같은 패턴을 따른다 — 새 패턴을 만들지
않는다.

### 2.5 StateStore의 done→pending 인터페이스 (현재)

`StateStore.cs`에는 `MarkDoneAsync`/`MarkSubtaskDoneAsync`/`ResetAllAsync`만
있다. **개별 task의 done을 false로 되돌리는 API는 없다.** `--reset`은
`StateFile` 전체를 새로 만든다. 자동 롤백은 batch에 속한 task만 선택적으로
되돌려야 하므로 새 API가 필요하다 (§3.6).

---

## 3. 제안 설계

### 3.1 옵션 우선순위 + 노출 위치

```
opt-in 결정:
  CLI --auto-rollback-on-smoke-fail
    > env RALPH_AUTO_ROLLBACK_ON_SMOKE_FAIL (= "true"/"1")
    > workflow.autoRollbackOnSmokeFail
    > false (기본 off)
```

신설/수정 위치:

- `ArgParser`: `--auto-rollback-on-smoke-fail` flag 파싱.
- `CommandContext`:
  ```csharp
  public bool CliAutoRollbackOnSmokeFail { get; init; }
  public bool EnvAutoRollbackOnSmokeFail { get; init; }
  // 우선순위 1단계: cli > env. workflow와의 머지는 RunCommand에서.
  public bool? AutoRollbackOnSmokeFailCliEnv =>
      CliAutoRollbackOnSmokeFail ? true
      : EnvAutoRollbackOnSmokeFail ? true
      : null;
  ```
  null이면 "사용자가 명시 안 함 → workflow로 fallback".
- `WorkflowSettings` (TasksFile.cs):
  ```csharp
  /// <summary>
  /// post-merge smoke test 실패 시 해당 batch의 머지 커밋을 자동 revert.
  /// CLI --auto-rollback-on-smoke-fail > env > 이 값 > false.
  /// 기본 off — opt-in.
  /// </summary>
  [JsonPropertyName("autoRollbackOnSmokeFail")]
  public bool? AutoRollbackOnSmokeFail { get; set; }
  ```
- `RunCommand`: `MergeOrchestrator` / `ParallelExecutor` 생성 시점에 합산.
  ```csharp
  var autoRollback = _ctx.AutoRollbackOnSmokeFailCliEnv
                     ?? tm.Data.Workflow?.AutoRollbackOnSmokeFail
                     ?? false;
  ```
  → `MergeOrchestrator` 생성자에 `bool autoRollbackOnSmokeFail` 추가하여 전달.
- `tasks.json` schema(`ralph-schema.json`)의 workflow object에
  `autoRollbackOnSmokeFail`(boolean) 필드 추가.

env 키 정확한 표현: `RALPH_AUTO_ROLLBACK_ON_SMOKE_FAIL`. 값 파싱은 다른
boolean env (`RALPH_NO_SMOKE_TEST`, `RALPH_STRICT_FILES`) 와 동일하게 truthy
판정 (`"true"`/`"1"`, 대소문자 무시).

### 3.2 batch 스냅샷의 데이터 모델

`preMergeSha` 단독으로는 부족하다. 자동 revert 결정과 메시지 작성을 위해
batch 시작 시점에 다음을 묶어 보관한다.

```csharp
// Ralph/Services/RollbackService.cs (또는 같은 namespace의 별도 파일)
public sealed record BatchRollbackSnapshot(
    string BaseBranch,
    string BaseSha,                    // batch 시작 전 base HEAD ('preMergeSha'와 동일)
    DateTime CapturedAt,
    IReadOnlyList<string> TaskIds);    // batch에 포함된 task ID들 (의도된 순서)
```

저장 위치: **메모리 only**. `MergeAndFinalizeAsync` 진입 시 변수로 잡고
함수 종료 시 자연스럽게 폐기. 디스크에 쓰지 않는 이유는 다음과 같다.

- batch 스냅샷은 한 batch가 진행되는 동안만 유효하다. 디스크 직렬화는
  과도하다.
- pre-plan/post-plan 스냅샷과 의미가 다르다 (`--rollback` 명령은
  batch 스냅샷을 사용하지 않는다 — 사용자가 후일 수동 호출하는 것이 아니라
  smoke 실패 핸들러가 즉시 사용한다).
- 디스크 저장 시 multi-process 동시성/cleanup 책임이 따라온다 — 회피.

`RollbackService`는 batch 메서드만 추가하고 디스크 I/O는 건드리지 않는다.

```csharp
// RollbackService.cs에 메서드 추가 (in-memory; 디스크 X)
public BatchRollbackSnapshot CaptureBatchSnapshot(
    string baseBranch, string baseSha,
    IReadOnlyList<string> taskIds)
    => new(baseBranch, baseSha, DateTime.UtcNow, taskIds);
```

호출은 `MergeOrchestrator.MergeAndFinalizeAsync`의 입구에서 1회 (지금 이미
`preMergeSha = CaptureBaseShaAsync(...)`가 있는 자리).

### 3.3 reset 대신 revert를 선택하는 이유

PRD는 `git reset --hard {snapshot-sha}`를 언급하지만, 본 fix에서는
**`git revert`를 기본 전략으로** 채택한다. 근거:

| 방식 | 장점 | 단점 |
|---|---|---|
| `git reset --hard {baseSha}` | 단순, 1회. base 커밋 그래프가 batch 머지 이전과 완전히 동일. | base 브랜치를 강제로 뒤로 밀어 force-push가 사실상 필요. push 안 해도 다른 사용자/CI가 base를 fetch했다면 발산. **히스토리 분실** — 무엇이 왜 사라졌는지 git log에 기록되지 않음. |
| `git revert -m 1 {merge-shas...}` | append-only. push 안전. revert 커밋 메시지에 실패 사유/smoke 출력을 남길 수 있어 진단 용이. | merge 커밋 revert는 `-m 1`이 필요. revert 커밋이 N개(머지 N건마다 1개) 또는 1개 (range revert) 생성. |

결정: **revert가 기본**. 단, 다음 케이스 한정으로 reset 모드 fallback을
허용한다.

- batch 시작 후 base에 추가된 커밋이 정확히 "ralph가 이번 batch에서 만든
  머지 커밋들"뿐이고,
- `BatchSnapshot.BaseSha..HEAD` 사이에 다른 커밋이 끼어 있지 않으며,
- (옵션) 사용자가 `--auto-rollback-mode=reset`을 명시한 경우.

본 fix에서는 **reset 모드를 도입하지 않는다** (단순화). 이유:

1. revert가 두 위험 시나리오 (base가 다른 사용자/원격에 fetch된 경우 / batch
   진행 중 누가 base에 push한 경우) 모두를 안전하게 다룬다.
2. 머지 커밋 N개를 revert하는 비용은 작고, 후행 진단이 용이하다 (`git log`
   에서 자동 revert 커밋을 한눈에 식별 가능).
3. reset을 원하는 사용자는 자동 롤백 후 직접 `git reset --hard
   HEAD~N`으로 추가 정리 가능.

향후 옵션 도입 여지를 위해 모드 enum(`AutoRollbackMode { Revert, Reset }`)을
설계 문서에 명시만 해둔다 — 이번 PR에서는 `Revert`만 구현.

### 3.4 smoke 실패 결과 풍부화

현재 `RunPostMergeSmokeTestAsync`는 `Task<int?>`를 반환한다 (1=실패,
null=성공/스킵). 자동 revert 메시지를 작성하려면 명령/exit/stdout/stderr/timedOut
정보가 필요하다. 결과를 구조체로 바꾼다.

```csharp
// MergeOrchestrator.cs
internal sealed record SmokePhaseResult(
    bool Skipped,                        // smoke가 아예 실행되지 않은 경우
    bool Passed,                         // skipped=false 일 때만 의미
    string? Command,                     // 실행된 명령 (skipped=true이면 null)
    VerificationResult? Detail);         // skipped=false 일 때 항상 채워짐

private async Task<SmokePhaseResult> RunPostMergeSmokeTestAsync(
    string? preMergeSha, CancellationToken ct)
{
    // 기존 본문 그대로. 스킵 시 SmokePhaseResult { Skipped=true } 반환.
    // 실행 시 _verifier.RunAsync 결과를 그대로 묶어 반환.
}
```

호출 측 분기:

```csharp
var smoke = await RunPostMergeSmokeTestAsync(preMergeSha, ct);
if (smoke.Skipped || smoke.Passed)
    return rebaseFailedTasks.Count > 0 ? 1 : 0;

// smoke 실패 — 자동 롤백 분기
if (_autoRollbackOnSmokeFail)
{
    var handled = await TryAutoRollbackAsync(
        snapshot, mergedTasks, smoke, ct);
    return handled ? 1 : 1;   // 둘 다 1 (batch 실패) — 롤백 성공 여부와 무관
}
return 1;
```

`SmokeTestPlanner`는 변경 불필요 — 결과 노출은 `MergeOrchestrator` 내부의
helper signature 변경만으로 충분하다 (호출 위치가 한 곳뿐).

### 3.5 자동 revert 흐름

```csharp
private async Task<bool> TryAutoRollbackAsync(
    BatchRollbackSnapshot snapshot,
    IReadOnlyList<string> mergedTasks,
    SmokePhaseResult smoke,
    CancellationToken ct)
{
    // 1. 사용자 working tree 안전 검사 (§3.7).
    var dirty = await IsWorkingTreeDirtyAsync(snapshot.BaseBranch, ct);
    if (dirty.HasUserChanges)
    {
        PrintAutoRollbackHeld(snapshot, smoke, dirty);
        _logger.Warn(
            $"[auto-rollback] held — working tree dirty. tasks=[{string.Join(",", mergedTasks)}]");
        return false;
    }

    // 2. base..HEAD 사이 머지 커밋 SHA 목록 수집 (역순 — 최신 → 과거)
    var mergeShas = await GetMergeCommitsSinceAsync(
        snapshot.BaseSha, snapshot.BaseBranch, ct);
    if (mergeShas.Count == 0)
    {
        // 이미 다른 누군가가 되돌렸거나, 이상 상태. silent 진행하지 않고 안내.
        PrintAutoRollbackNoOp(snapshot);
        return false;
    }

    // 3. revert 메시지 본문 구성
    var message = BuildRevertMessage(snapshot, mergedTasks, smoke);

    // 4. 단일 revert 커밋 1개로 압축 — git revert -m 1 --no-commit ... + 직접 commit.
    //    여러 머지 SHA를 동시에 revert하면 git이 1 step씩 진행하므로 --no-commit으로
    //    누적해 한 커밋으로 묶는다 (git log 한 줄로 식별 가능).
    var revertArgs = new List<string> {
        "revert", "--no-commit", "-m", "1"
    };
    revertArgs.AddRange(mergeShas);
    var (rExit, rOut) = await _git.RunAsync(revertArgs.ToArray(), ct: ct);
    if (rExit != 0)
    {
        // revert가 도중에 충돌나면 abort + 사용자에게 수동 안내. 이미 staged 영역이
        // 이상해졌을 수 있으므로 보수적으로 abort 시도 후 종료.
        await _git.RunAsync(["revert", "--abort"], ct: ct);
        PrintAutoRollbackFailed(snapshot, mergeShas, rOut);
        _logger.Error($"[auto-rollback] revert failed: {rOut.Trim()}");
        return false;
    }

    var (cExit, cOut) = await _git.RunAsync(
        ["commit", "-m", message, "--allow-empty"], ct: ct);
    if (cExit != 0)
    {
        await _git.RunAsync(["revert", "--abort"], ct: ct);
        PrintAutoRollbackFailed(snapshot, mergeShas, cOut);
        return false;
    }

    // 5. state.json 되돌리기 (§3.6).
    foreach (var taskId in mergedTasks)
        await _taskManager.MarkTaskPendingAsync(taskId, ct);

    PrintAutoRollbackSucceeded(snapshot, mergedTasks, mergeShas, smoke);
    _logger.Warn(
        $"[auto-rollback] reverted {mergeShas.Count} merge(s); " +
        $"tasks reset to pending: {string.Join(",", mergedTasks)}");
    return true;
}
```

세부:

- `GetMergeCommitsSinceAsync`는 `git rev-list --merges {baseSha}..HEAD` 결과를
  파싱. 머지 커밋만 대상으로 삼는다 (사이에 누가 끼어 만든 일반 커밋은
  건드리지 않음 — §3.7과 결합해 안전).
- `git revert` 충돌은 fast-fail로 처리 (자동 LLM 충돌 해결 체인은 적용하지
  않음). 이유: 자동 롤백은 안전 기본값이어야 한다 — revert 충돌이 나는
  상황은 base에 이질적인 변경이 끼었거나 머지가 의외로 복잡했다는 뜻이므로
  사용자 개입이 옳다.
- 단일 revert 커밋(`--no-commit` + `commit -m`)으로 묶는 이유: `git log
  --grep="auto-rollback"` 한 줄로 식별 가능. 다중 revert 커밋은 메시지
  prefix가 git 자동 생성이라 검색 일관성이 떨어진다.

### 3.6 `state.json`에서 task pending 으로 되돌리기

`StateStore`에 새 mutator 추가.

```csharp
// StateStore.cs
public async Task MarkPendingAsync(string taskId, CancellationToken ct = default)
{
    await _lock.WaitAsync(ct);
    try
    {
        if (_data.Tasks.TryGetValue(taskId, out var ts))
        {
            ts.Done = false;
            // subtask 비트는 의도적으로 그대로 둔다 — task 단위 롤백이지
            // subtask 단위 cleanup이 아님. 다음 --run에서 task 전체가
            // 다시 dispatch될 때 subtask는 done 체크로 skip되며, 사용자가
            // 명시적으로 재실행하려면 --reset을 권장.
            await SaveWithRetryAsync(ct);
        }
        // 존재하지 않는 taskId는 no-op (이미 pending 상태와 동치).
    }
    finally { _lock.Release(); }
}
```

`TaskManager`에 thin wrapper:

```csharp
// TaskManager.cs
public Task MarkTaskPendingAsync(string taskId, CancellationToken ct = default)
    => _state.MarkPendingAsync(taskId, ct);
```

호출자 (`MergeOrchestrator.TryAutoRollbackAsync`)는 mergedTasks를 순회하며
`MarkTaskPendingAsync`. 한 건이라도 실패하면 `ReportStateWriteFailure`와
같은 톤의 에러 메시지 출력 + batch 종료 (revert는 이미 성공했으므로
git 커밋 그래프는 일관, state만 깨진 상태 — 사용자 수동 개입 필요).

> **subtask 정책**: 본 fix는 `MarkPendingAsync`가 subtask 비트를 보존한다.
> 자동 롤백은 batch 단위 거시 작업이고, subtask 비트는 사용자 시각적
> 진행도일 뿐 실행 분기에는 영향이 없다. 사용자가 완전 초기화를 원하면
> `--reset`을 사용한다.

### 3.7 사용자 working tree dirty 보호

PRD의 핵심 안전 요구사항: **사용자가 만진 게 있으면 자동 롤백을 보류한다.**

검사 항목:

| 항목 | 검사 명령 | 의미 |
|---|---|---|
| (a) working tree 변경 | `git status --porcelain=v1` | unstaged + staged + untracked가 한 줄이라도 있으면 dirty. |
| (b) baseBranch ≠ HEAD branch | `git rev-parse --abbrev-ref HEAD` ≠ `snapshot.BaseBranch` | 사용자가 batch 도중 다른 브랜치로 이동. base에 revert를 적용하면 사용자의 현재 브랜치를 흔들 수 있다. |
| (c) base에 batch 외 커밋 끼어듦 | `git rev-list {baseSha}..HEAD` 중 (`mergeShas` ∪ ralph가 만든 커밋) 외 커밋 존재 | batch 시작 후 누가 base에 직접 push/commit. revert 안전 영역 밖. |

판정: 위 셋 중 하나라도 참이면 자동 롤백 **보류**(held). 콘솔 + stderr에
다음을 출력:

```
[auto-rollback] held — 자동 롤백을 적용하지 않았습니다.
  사유: working tree가 깨끗하지 않거나 base에 외부 커밋이 섞여 있습니다.
  detail:
    - working tree dirty: yes (3 file(s))
    - 현재 브랜치: feature-x  (base: main)
    - base..HEAD 외부 커밋: 1건 (b3a91c)
  smoke 실패는 그대로 종료 코드로 반환됩니다.
  복구 안내:
    1) 로컬 변경을 커밋/스태시한 뒤 다시 `ralph --run`을 시도하면
       이번 batch는 이미 머지된 상태로 남아있으므로 자동 롤백 대상이 아닙니다.
    2) 수동으로 되돌리려면:
       git revert -m 1 <머지 SHA들>
       그리고 .ralph-logs/state.json에서 해당 task의 done을 false로 편집.
  되돌릴 머지 후보:
    - <merge-sha-1> (Task: <id>)
    - <merge-sha-2> (Task: <id>)
```

이 메시지는 자동 롤백 시도조차 안 하므로 idempotent — 사용자가 dirty
상태를 정리하고 재시도하는 동안 base는 머지된 상태로 유지된다 (지금과
동일한 동작).

(c) 검사 메모: ralph 머지 커밋만 정확히 식별하려면 `mergeShas`(rev-list
--merges) 와 비교. 일반 commit이 끼어들면 dirty 판정. 단, ralph가 머지 외에
직접 만든 일반 commit (예: `--strict-files` 위반 차단 후 사용자가 손으로
fixup commit을 base에 추가했을 가능성)은 보수적으로 dirty 처리한다 — 자동
revert가 사용자 commit을 되돌리는 회귀를 절대 허용하지 않는다.

### 3.8 revert 커밋 메시지 템플릿

자동 revert 1건당 다음 한 커밋. UTF-8 한국어 + 머지 SHA들 + smoke 출력
일부.

```
chore(rollback): smoke test 실패로 batch 자동 revert

Smoke test 실패에 의해 직전 batch가 자동 롤백되었습니다.
Ralph가 수행한 변경:
  - base 브랜치 '{baseBranch}'를 batch 시작 시점으로 되돌리는 revert 커밋 생성
  - state.json의 batch 소속 task들을 다시 pending으로 표시

batch 정보:
  base: {baseBranch}
  base sha (스냅샷): {baseSha7}
  reverted merge commits ({N}건):
    - {sha7-1}  (task: {id-1})
    - {sha7-2}  (task: {id-2})
    - ...

smoke test:
  command: {spec.Command}
  exit: {result.ExitCode}{ ", TIMEOUT" if timedOut }
  duration: {duration:F1}s

smoke stdout (tail, max 4 KB):
{truncated_stdout}

smoke stderr (tail, max 4 KB):
{truncated_stderr}

옵션:
  --auto-rollback-on-smoke-fail (CLI) /
  RALPH_AUTO_ROLLBACK_ON_SMOKE_FAIL=true (env) /
  workflow.autoRollbackOnSmokeFail=true (tasks.json)

다음 `ralph --run` 시 동일 task들이 새 worktree로 재실행됩니다.
```

세부:

- `{baseSha7}` / `{shaN-7}` 는 `git rev-parse --short` 동치(앞 7자).
- `truncated_stdout/stderr` 는 마지막 4 KB만 잘라 붙임 (`Truncate`는 이미
  `VerificationRunner`에 비슷한 헬퍼 존재 — 재사용 또는 동일 정책으로 신설).
  너무 큰 빌드 로그가 커밋 메시지를 비대하게 만드는 것을 방지.
- 메시지 첫 줄은 50자 이내, 나머지 본문은 72자 wrap을 권장하나 **번역된
  smoke 출력**은 wrap을 강제하지 않음 (정확한 출력 재현이 우선).
- prefix `chore(rollback):` 는 conventional commits 호환 + grep 친화적.
- 메시지에 `Co-Authored-By` 라인은 넣지 않는다 — ralph가 자동 생성한
  복구 커밋임을 분명히 한다.

stderr 콘솔 안내(성공 케이스, AnsiConsole + Console.Error 분리):

```
[red]✗ Smoke test 실패[/] (exit=1, 23.5s)
[yellow]⚠ 자동 롤백을 시작합니다 (--auto-rollback-on-smoke-fail).[/]
[green]✓ batch revert 완료[/] (3건 머지 커밋 → 단일 revert 커밋 abc1234)
[green]✓ state.json 재설정[/] (3 task → pending)
[dim]다음 ralph --run에서 동일 task가 재실행됩니다.[/]
```

stdout 한 줄 요약은 위 4줄. 자세한 머지 SHA 목록은 stderr로 분리 (rebase
충돌 메시지 패턴 §`MergeOrchestrator.PrintRebaseConflict`와 동일 톤).

### 3.9 종료 코드 정책

자동 롤백이 발동하든 안 하든, smoke 실패는 batch 실패다. `MergeAndFinalizeAsync`
는 **항상 1을 반환**한다 (현재 동작과 동일).

이유:

- 호출자(`ParallelExecutor`)가 batch 실패를 보고 다음 batch 진입을 차단.
  자동 롤백 성공 시에도 base는 batch 이전으로 돌아간 상태이므로, 다음
  batch를 곧장 진행하면 동일한 task가 다시 실패할 가능성이 높다.
- 부분 성공을 별도 exit code로 표현하지 않는다 (rebase 충돌과 동일 정책).
  세분화는 `fix2 #5`의 향후 작업 항목에 묶여 있다.

자동 롤백 성공/보류/실패는 콘솔 메시지와 logger로 구분되며, exit는 단일
`1` (또는 cancel 시 OperationCanceledException 전파).

### 3.10 ParallelExecutor 루프와의 상호작용

자동 롤백 후 batch가 1로 끝나면 `ParallelExecutor.RunParallelBatchAsync`가
1을 반환하고 `RunAsync` 메인 루프가 종료한다 (현재 동작 동일). 사용자가
다시 `ralph --run`을 호출하면:

- `state.json`에서 해당 task들이 다시 pending 으로 보이고,
- `TaskManager.GetAllReadyTasks()`가 그 task들을 다시 ready로 분류,
- 새 worktree가 만들어지고 같은 batch가 재시도된다.

이 흐름은 `--reset`이나 `--rollback`(snapshot 복원) 없이 동작한다.

---

## 4. 인터페이스 변경 요약

| 파일 | 변경 |
|---|---|
| `Ralph/Models/TasksFile.cs` | `WorkflowSettings.AutoRollbackOnSmokeFail` (bool?) 추가. |
| `ralph-schema.json` | `workflow.autoRollbackOnSmokeFail` (boolean) 추가. |
| `Ralph/Commands/ArgParser.cs` | `--auto-rollback-on-smoke-fail` flag + env `RALPH_AUTO_ROLLBACK_ON_SMOKE_FAIL` 파싱. |
| `Ralph/Commands/CommandContext.cs` | `CliAutoRollbackOnSmokeFail`, `EnvAutoRollbackOnSmokeFail`, computed `AutoRollbackOnSmokeFailCliEnv`. |
| `Ralph/Commands/RunCommand.cs` | CLI/env/workflow 머지 후 `ParallelExecutor`/`MergeOrchestrator` 생성자에 전달. |
| `Ralph/Services/StateStore.cs` | `MarkPendingAsync(taskId)` 신설. |
| `Ralph/Services/TaskManager.cs` | `MarkTaskPendingAsync` thin wrapper. |
| `Ralph/Services/RollbackService.cs` | `CaptureBatchSnapshot(...)` (in-memory) + `BatchRollbackSnapshot` record 추가. 디스크 I/O는 기존과 동일. |
| `Ralph/Services/MergeOrchestrator.cs` | `_autoRollbackOnSmokeFail` 필드, smoke 결과 풍부화(`SmokePhaseResult`), `TryAutoRollbackAsync`, `IsWorkingTreeDirtyAsync`, `GetMergeCommitsSinceAsync`, `BuildRevertMessage`, `Print*` 핸들러. 호출자 변경 최소화 (`MergeAndFinalizeAsync` 시그니처 유지). |
| `Ralph/Services/ParallelExecutor.cs` | 생성자에 `bool autoRollbackOnSmokeFail` 추가, `MergeOrchestrator`로 전달. |
| `Ralph.Tests/AutoRollbackOnSmokeFailTests.cs` | §6 시나리오. |

`SmokeTestPlanner`는 시그니처 변경 없음 (smoke 실행 결과의 풍부화는
`MergeOrchestrator` 내부에서 처리).

---

## 5. 회귀 / 호환성

| 시나리오 | 현재 동작 | 변경 후 기대 |
|---|---|---|
| 옵션 미지정 (기본 off) | smoke 실패 → exit 1, base 머지 그대로 남음 | 동일. **회귀 없음**. |
| 옵션 ON, smoke 통과 | (해당 없음 — 옵션 무관) | 동일. revert 시도 자체가 없음. |
| 옵션 ON, smoke 실패, working clean | (옵션 없음) | revert 1건 + state pending. **신규 동작**. |
| 옵션 ON, smoke 실패, working dirty | (옵션 없음) | held. base/state 그대로 두고 안내. **안전 기본**. |
| 옵션 ON, smoke 실패, base에 외부 커밋 | (옵션 없음) | held. **안전 기본**. |
| 옵션 ON, revert 도중 충돌 | (옵션 없음) | revert --abort + 안내. base는 여전히 머지된 상태. 사용자 수동 처리. |
| 옵션 ON, smoke skipped (docs-only) | smoke 미실행 → exit 0 | 동일. revert 분기 진입 안 함. |
| `--no-smoke-test` + 옵션 ON | smoke 미실행 → exit 0 | 동일. 옵션이 켜져도 smoke가 없으면 자동 롤백 트리거 없음. |
| rebase 충돌이 한 건 있고 옵션 ON, smoke 실패 | (옵션 없음) | 자동 revert 대상은 **머지 성공한 task만** (`mergedTasks`). rebase 실패 task는 이미 base에 반영 안 됐으므로 손대지 않음. |
| `--rollback` 명령 | pre-plan/post-plan 스냅샷 복원 | 변경 없음. batch 스냅샷은 in-memory라 영향 없음. |

확인 항목 (PR 단계):

- [ ] 기본 off에서 옵션 관련 코드 경로가 한 줄도 실행되지 않는지 (회귀
  안전망).
- [ ] `MarkPendingAsync`가 존재하지 않는 task ID에 대해 no-op 인지.
- [ ] 자동 revert 커밋 메시지에 `<<<<<<<` 같은 문자열이 우연히 포함된
  smoke 출력을 그대로 넣었을 때 git이 거부하지 않는지 (커밋 메시지에는
  영향 없음 — 검증).
- [ ] Windows에서 `git revert --no-commit -m 1 <multi-shas>` 동일 동작하는지
  (POSIX와 동일하지만 회귀 안전망).
- [ ] 매우 긴 smoke 출력(수 MB)이 4 KB로 잘리는지.

---

## 6. 테스트 시나리오

각 시나리오는 임시 git repo + 임시 `.ralph-worktrees/` + 실제 git 명령
사용 (mock 금지 — revert/state 결합 동작은 실제 거동을 따라야 함).

1. **opt_in_default_off**
   - 옵션 미지정 + smoke 실패.
   - assert: 머지 커밋이 base에 그대로 남아 있음. `state.json`에 done=true
     유지. exit 1. **회귀 안전망**.

2. **cli_overrides_workflow_off**
   - `workflow.autoRollbackOnSmokeFail = false` + CLI `--auto-rollback-on-smoke-fail`.
   - smoke 실패.
   - assert: revert 발동, state pending.

3. **env_overrides_workflow**
   - workflow false + `RALPH_AUTO_ROLLBACK_ON_SMOKE_FAIL=true`.
   - assert: revert 발동.

4. **cli_overrides_env_workflow_explicit_off**
   - workflow true + env true + CLI `--auto-rollback-on-smoke-fail` 미지정.
   - assert: 우선순위 순서대로 ON으로 평가 — revert 발동. (CLI에 explicit off
     플래그는 본 fix에서 도입하지 않으므로 별도 케이스 없음.)

5. **happy_path_revert_and_state_reset**
   - 두 task 머지 → smoke `false` 강제 실패.
   - assert: `git log {baseSha}..HEAD`가 1개의 revert 커밋만 가짐(또는
     머지 + revert 짝). `state.json`의 두 task `done = false`.
     base의 working tree 내용이 batch 시작 시점과 git diff상 동일.

6. **held_when_working_tree_dirty**
   - smoke 실패 직전 base에 untracked file 생성.
   - assert: revert 미발동. base 머지 커밋 그대로. state.json 그대로
     done=true. stderr에 held 메시지.

7. **held_when_external_commit_in_base**
   - batch 진행 중 별도 git 호출로 base에 직접 commit 추가.
   - smoke 실패.
   - assert: revert 미발동 + 사유에 "외부 커밋" 명시.

8. **revert_conflict_aborts_safely**
   - revert가 충돌하도록 base를 사전 조작 (예: 같은 라인을 다른 task가
     머지 후 base에 또 다른 변경). `git revert --no-commit ...`이 충돌.
   - assert: `git revert --abort` 실행됨. base 그대로(repo unmerged 상태
     아님). state.json 그대로 done=true. exit 1. 사용자 안내 출력.

9. **revert_message_contains_smoke_output**
   - smoke가 stdout/stderr를 출력하면서 실패.
   - assert: revert 커밋 메시지에 `command:`, `exit:`, stdout 일부, stderr
     일부, task ID 목록이 모두 포함. 출력이 4 KB를 넘으면 잘려 있음.

10. **rebase_failed_task_excluded_from_revert**
    - 두 task 중 하나가 rebase 충돌(§ fix2 #5)로 머지 안 됨, 나머지 1개만
      머지 + smoke 실패 + 옵션 ON.
    - assert: revert 대상은 머지된 1개만. rebase 실패 task는 자동 롤백
      대상이 아님 (애초에 base에 반영 안 됨).

11. **state_save_failure_after_revert**
    - revert는 성공했으나 `MarkPendingAsync`가 IO 실패 (디스크 full 시뮬).
    - assert: 콘솔에 부분 실패 안내 (revert 성공 + state 재설정 실패),
      logger에 명시. exit 1.

12. **subtask_bits_preserved**
    - subtask가 있는 task가 자동 롤백 대상이 되는 경우.
    - assert: `MarkPendingAsync` 후 task.done=false, subtask.done은 그대로.
      다음 `--run` 시 task가 재실행되며 subtask 마킹은 사용자 시각에서만
      잔존 (PRD §3.6 정책 명시).

13. **disabled_option_is_compatible_with_no_smoke_test**
    - 옵션 ON + `--no-smoke-test`.
    - assert: smoke 자체가 실행되지 않으므로 자동 롤백 분기 미진입.
      현재 동작과 동일.

14. **schema_validation_accepts_new_field**
    - `tasks.json` 의 workflow에 `autoRollbackOnSmokeFail: true` 가 들어 있을
      때 `PlanValidator` / schema 통과.

---

## 7. 마이그레이션 / 향후 작업

- **reset 모드**: §3.3에서 보류한 `AutoRollbackMode { Revert, Reset }` enum.
  base가 단독 사용자 환경(원격 push 안 함)이며 히스토리 단축이 더 중요한
  케이스를 위한 옵션. 별도 fix에서 검토.
- **부분 성공 exit code**: 자동 롤백 성공/보류/실패를 종료 코드로 분리하면
  CI 파이프라인에서 재시도 정책을 differentiate 가능. fix2 전반의 exit
  code 재정렬과 묶어 별도 작업.
- **smoke 외 실패 트리거**: 머지 직후 `--strict-files` 위반이나 verification
  실패에도 동일 자동 롤백을 적용할지는 별도 결정. 본 fix는 **post-merge
  smoke test 실패에만** 적용한다 (PRD 명시 범위).
- **Linear/Slack 통보**: 자동 롤백 발동 시 `NotificationService` 채널에
  별도 페이로드(`auto_rollback: true`)를 보낼지 — fix2 #8 (관측성)에서
  통합.

---

## 8. 완료 보고

- **생성**: `docs/fix2/07-auto-rollback-plan.md` (본 문서)
- **수정**: 없음
- **Scope 외 변경**: 없음
- **참고 문서**: `docs/fix2/05-rebase-conflict-plan.md` (선행 task; rebase
  충돌 분류와 mergedTasks 정의를 본 fix가 그대로 사용),
  `docs/fix2/04-worktree-branch-guard-plan.md` (워크트리/브랜치 안전 가드
  모델), `fix2.md` #7 항목.
