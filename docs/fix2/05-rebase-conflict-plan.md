# Fix2 #5 — Rebase-advance 충돌 처리 설계

## 1. 배경

`Ralph/Services/WorktreeService.cs`의 `AdvanceWorktreeOntoBaseAsync`(L427~453)는
같은 batch에서 앞선 머지가 base를 advance시킨 뒤, 후속 worktree를 머지하기
직전에 base 위로 rebase해 LCA를 최신 base로 끌어올리는 **머지 충돌 감소
최적화**다.

현재 흐름의 문제 (`fix2.md` #5 요약):

- rebase 충돌이 나면 단순히 `git rebase --abort`로 worktree를 복구하고
  `false`를 반환만 한다 — 호출자(`MergeOrchestrator`)는 그 신호를 받아도
  로그 한 줄도 남기지 않고 **곧장 3-way merge로 fallback**한다
  (`MergeOrchestrator.cs:114`).
- 결과적으로 동일 충돌이 머지 단계에서 다시 발생하고, 그제서야 머지
  실패로 잡혀 `conflictStrategies` 체인을 탄다. 사용자 입장에서는 어느
  단계에서 어떤 이유로 충돌이 났는지가 모호하고, 같은 충돌이 두 번
  처리되어 로그가 시끄럽고 시간도 낭비된다.
- `MergeFailureKind` 같은 분류 enum이 없어 `MergeResult`만으로는 "rebase
  단계에서 깨졌다"를 표현할 수 없다 — 진단 메시지/통계를 분리할 길이
  없다.
- batch 내 다른 독립 task(다른 worktree, 충돌 없음)들의 운명도 코드
  주석에서만 암시되어 있다 — 한 task의 rebase 실패가 batch 전체를 막아
  세우는지, 다른 task는 계속 진행되는지가 불분명하다.

본 fix는 다음을 확정한다.

1. rebase 충돌은 **rebase 단계 실패로 명시적 분류**(`MergeFailureKind.RebaseConflict`)
   하고, 같은 task에 대한 3-way merge fallback을 끊는다 (silent fallback 제거).
2. 해당 task만 실패로 마킹하고, **batch 내 다른 독립 task는 계속 진행**한다
   (현재도 다른 worktree는 별개 디렉터리이므로 진행 가능 — 정책을 코드와
   문서로 명시).
3. 충돌 파일 목록을 stderr에 한국어로 locale-safe하게 출력하고
   (`fix1 #3`과 동일 톤), 사용자에게 `ralph --task {id} --force` 또는 수동
   머지 안내를 함께 표시.
4. `MergeOrchestrator`의 `conflictStrategies` 체인이 rebase 단계에도
   적용 가능한지를 결정하고 (§3.4), 본 fix에서는 적용 **안 함**으로
   확정한다 (근거 명시).

Scope:
- 수정 예정: `Ralph/Services/WorktreeService.cs`, `Ralph/Services/MergeOrchestrator.cs`
- 신설: `MergeFailureKind` enum (WorktreeService 인근 또는 동일 namespace)
- 테스트: `Ralph.Tests/MergeOrchestratorTests.cs` (또는 동등 위치) §6 시나리오

---

## 2. 현재 흐름 분석

### 2.1 `AdvanceWorktreeOntoBaseAsync` (현재)

```
L433  git rebase {baseRef}  (in worktreePath)
L436  exit==0 → log info, return true
L442  exit!=0 → log warn ("3-way merge로 fallback")
L447  git rebase --abort
L449  abort 실패 시 추가 warn
L452  return false   ◀── 호출자는 그냥 다음 단계 진행
```

문제 지점:
- 충돌 파일 목록을 추출하지 않는다 (`git diff --name-only --diff-filter=U`
  를 abort 전에 캡처해야 의미 있음).
- 실패 사유 분류 없음 — `false`만 반환.
- 사용자 콘솔에는 어떤 메시지도 나가지 않음 (logger.Warn은 파일 로그).

### 2.2 `MergeOrchestrator.MergeAndFinalizeAsync` (현재)

`MergeOrchestrator.cs:114` 근처:

```csharp
// 같은 batch의 앞선 머지로 baseBranch가 advance된 경우 충돌 감소를 위해 rebase.
await _worktree.AdvanceWorktreeOntoBaseAsync(taskId, baseBranch, _logger, ct);

var mergeResult = await _worktree.MergeWorktreeAsync(
    taskId, baseBranch, primaryStrategy, _logger, ct);
```

- 반환값(`bool`)을 무시한다 — rebase 성공/실패와 무관하게 그대로 머지.
- 따라서 rebase 실패 = 곧 머지 충돌로 재현 → `HandleMergeConflictAsync`
  체인으로 흘러간다. 이 경로는 모호하다.

### 2.3 `MergeFailureKind` 부재

현재 모델:
- `MergeResult { Success, ConflictFiles, ErrorMessage }` (`WorktreeService.cs:9~14`).
- 머지 충돌과 다른 종류 실패 (untracked overwrite, rebase 충돌)를 구분할
  수단이 없다.

### 2.4 conflictStrategies 적용 위치 (현재)

`MergeOrchestrator.HandleMergeConflictAsync`(L245~319)는 `merge` 명령
결과에 대해서만 동작한다.
- `auto-theirs` / `auto-ours` → `git merge -X {strategy}` 재머지.
- `claude` → `claude` CLI로 충돌 마커 해결 후 `git commit --no-edit`.
- `abort` → 머지 abort + sequential 재실행.

rebase 단계에는 어떤 전략도 적용되지 않는다.

---

## 3. 제안 설계

### 3.1 `MergeFailureKind` 신설

```csharp
public enum MergeFailureKind
{
    None,            // 성공
    MergeConflict,   // git merge 충돌 (기존 경로)
    RebaseConflict,  // git rebase --advance 단계 충돌 (신규)
    UntrackedOverwrite, // base working tree의 untracked 파일이 머지를 막음
                        // (이미 처리되는 케이스. 분류만 부여, 동작 변화 없음)
    Other,           // git checkout 실패 등
}
```

`MergeResult`에 필드 추가:

```csharp
public class MergeResult
{
    public bool Success { get; set; }
    public MergeFailureKind FailureKind { get; set; } = MergeFailureKind.None;
    public List<string>? ConflictFiles { get; set; }
    public string? ErrorMessage { get; set; }
}
```

기존 `Success` 필드는 호환성을 위해 유지 (Success=false일 때
FailureKind ≠ None가 보장).

### 3.2 `AdvanceWorktreeOntoBaseAsync` 시그니처 변경

```csharp
public async Task<MergeResult> AdvanceWorktreeOntoBaseAsync(
    string taskId, string baseRef, RalphLogger? logger = null,
    CancellationToken ct = default)
```

기존 `Task<bool>` → `Task<MergeResult>`로 변경. 성공 시
`{ Success = true, FailureKind = None }`, 실패 시
`{ Success = false, FailureKind = RebaseConflict, ConflictFiles, ErrorMessage }`.

호출처는 `MergeOrchestrator` 한 곳뿐이라 영향 범위가 좁다 (테스트 코드는
`grep`으로 동시 갱신).

### 3.3 새 흐름 (의사코드)

```csharp
// WorktreeService.cs
public async Task<MergeResult> AdvanceWorktreeOntoBaseAsync(
    string taskId, string baseRef, RalphLogger? logger = null,
    CancellationToken ct = default)
{
    logger ??= RalphLogger.Null;
    var worktreePath = Path.GetFullPath(Path.Combine(_worktreeBase, taskId));

    var (exitCode, output) = await _git.RunAsync(
        ["rebase", baseRef], worktreePath, ct);

    if (exitCode == 0)
    {
        logger.Info($"[merge:advance] {taskId} rebased onto current {baseRef}");
        return new MergeResult { Success = true };
    }

    // 충돌 파일을 abort 전에 캡처 (abort 후엔 unmerged index가 비워짐).
    var conflictFiles = await GetRebaseConflictFilesAsync(worktreePath, ct);

    logger.Warn(
        $"[merge:advance] {taskId} rebase 실패 — RebaseConflict로 분류, " +
        $"abort 후 task만 실패 처리. detail: {output.Trim()}");

    var (abortExit, abortOut) = await _git.RunAsync(
        ["rebase", "--abort"], worktreePath, ct);
    if (abortExit != 0)
    {
        // abort 실패는 worktree가 깨진 상태로 남는다는 뜻 — 별도 분류.
        logger.Error(
            $"[merge:advance] {taskId} rebase --abort 실패: {abortOut.Trim()}. " +
            $"worktree가 더러운 상태일 수 있습니다.");
        return new MergeResult
        {
            Success = false,
            FailureKind = MergeFailureKind.Other,
            ConflictFiles = conflictFiles,
            ErrorMessage = $"rebase abort failed: {abortOut.Trim()}",
        };
    }

    return new MergeResult
    {
        Success = false,
        FailureKind = MergeFailureKind.RebaseConflict,
        ConflictFiles = conflictFiles,
        ErrorMessage = output.Trim(),
    };
}

private async Task<List<string>> GetRebaseConflictFilesAsync(
    string worktreePath, CancellationToken ct)
{
    var (_, output) = await _git.RunAsync(
        ["diff", "--name-only", "--diff-filter=U"], worktreePath, ct);
    return output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(f => f.Trim())
        .Where(f => f.Length > 0)
        .ToList();
}
```

`MergeOrchestrator.MergeAndFinalizeAsync` 변경 (현재 L113~141 부근):

```csharp
// 같은 batch의 앞선 머지로 baseBranch가 advance된 경우 충돌 감소를 위해 rebase.
var advance = await _worktree.AdvanceWorktreeOntoBaseAsync(
    taskId, baseBranch, _logger, ct);

if (!advance.Success && advance.FailureKind == MergeFailureKind.RebaseConflict)
{
    // fix2 #5: rebase 충돌 = 즉시 task 실패. 3-way merge fallback 안 함.
    PrintRebaseConflict(taskId, advance);   // §3.5 — locale-safe stderr 출력
    _logger.Error(
        $"[merge:advance] {taskId} RebaseConflict — task 실패 마킹, batch 진행 계속. " +
        $"files=[{string.Join(",", advance.ConflictFiles ?? new())}]");

    if (!await _worktree.CleanupWorktreeAsync(taskId, _logger, ct))
        cleanupFailures++;
    failedTasks.Add((taskId, MergeFailureKind.RebaseConflict));
    continue;   // 다음 task로 진행 (독립 task는 영향 없음)
}

if (!advance.Success && advance.FailureKind == MergeFailureKind.Other)
{
    // abort 실패 = worktree 더러움. batch 중단이 안전.
    _logger.Error($"[merge:advance] {taskId} abort 실패 — batch 중단");
    return 1;
}

// rebase 성공 → 기존 머지 흐름.
var mergeResult = await _worktree.MergeWorktreeAsync(
    taskId, baseBranch, primaryStrategy, _logger, ct);
// ... (기존 코드 그대로)
```

루프 종료 후, 한 건이라도 RebaseConflict가 있으면 batch는 "부분 성공"으로
종료한다:

```csharp
if (failedTasks.Count > 0)
{
    PrintBatchSummary(failedTasks);  // 태스크별 사유 요약 + 재실행 안내
    return 1;   // ParallelExecutor가 다음 batch로 진행할지 결정
}
```

> 정책 — RebaseConflict task는 done 마킹하지 않는다 (당연히 머지가 안
> 됐으므로 base에 적용된 변경 없음). 사용자가 다시 `ralph --run`을
> 돌리면 같은 task가 새 worktree로 재시도된다. 충돌 원인이 base 쪽
> 변화이면 다음 시도에서 깨끗이 풀릴 가능성이 높다.

### 3.4 conflictStrategies가 rebase에도 적용 가능한가? — **본 fix에서는 NO**

가능성 검토:

| 전략 | rebase 적용 가능? | 의미 / 위험 |
|---|---|---|
| `auto-theirs` | 기술적 가능 (`git rebase -X theirs base`) | rebase에서 `theirs` 의미가 머지와 정반대다 — 머지에서 `-X theirs`는 머지하는 쪽(=worktree)의 변경을 우선하지만, rebase 중에는 "theirs"가 우리가 위에 얹는 base 쪽을 가리키게 된다 (`man git-rebase`의 NOTES). 사용자가 "auto-theirs"를 머지 의미로 설정해뒀을 때 rebase에서 정반대 동작이 일어나면 silent 회귀. |
| `auto-ours` | 동일하게 가능, 동일하게 의미 반전 | 위와 동일. |
| `claude` | 매 commit마다 충돌 해결 후 `git rebase --continue` 루프 가능 | worktree에 commit이 N개 있으면 LLM 호출 N회 + 매 단계 검증. 비용/시간 폭증 + 한 commit 해결 실패 시 abort. 머지 단계 한 번 호출과 비용 차이 큼. |
| `abort` | 의미 동일 (rebase --abort + sequential 재실행) | 본 fix의 RebaseConflict 분기가 abort + cleanup으로 이미 같은 결과. `--task {id} --force` 안내까지 포함하므로 별도 abort 전략 불요. |

결정: **rebase 단계에는 conflictStrategies를 적용하지 않는다.**

근거:
1. **의미 반전 위험**: `-X theirs/ours`가 머지와 rebase에서 가리키는 쪽이
   다르다. 같은 설정으로 양 단계를 동작시키면 사용자가 머지 의미로 둔
   설정이 rebase에서 정반대로 적용된다. 사일런트 회귀.
2. **rebase = 최적화일 뿐**: `AdvanceWorktreeOntoBaseAsync`는 같은 batch에서
   base가 advance된 케이스의 LCA 최적화일 뿐 핵심 정합성 단계가 아니다.
   복잡한 자동 해결로 시간/비용을 늘리기보다 빠른 실패 + 재실행이 안전.
3. **claude resolver의 N×비용**: rebase는 worktree의 N개 commit을 하나씩
   reapply하므로 충돌이 commit 단위로 N번 날 수 있다. claude 호출 N번은
   머지 1번 호출 대비 비용·시간이 한 자릿수 늘어나며 실패 누적 시 사용자
   기대(`workflow.parallel.maxConcurrent`로 LLM 호출 cap)도 깨진다.
4. **abort 전략은 중복**: RebaseConflict 분기가 task 실패 + cleanup +
   사용자 안내(재실행 명령)까지 포함하므로 사실상 abort와 동일.

향후 작업(범위 외): `workflow.parallel.rebaseStrategies` 별도 옵션을
도입한다면 (1) 의미 반전 정정, (2) commit 수 cap, (3) claude 호출 비용
가드를 같이 설계해야 한다. 본 fix에서는 단일 정책 — rebase 충돌은
**즉시 RebaseConflict 실패**로 고정한다.

### 3.5 stderr 메시지 포맷 (한국어, locale-safe)

`fix1 #3`과 일관된 톤 — 진단 prefix(`[merge:advance]`) + 사람이 읽기
좋은 요약 + 후속 명령. **stderr로 출력**해 stdout 파이프(`tee`,
프로그래매틱 캡처)와 분리.

```
[merge:advance] fix5-rebase-conflict-plan: rebase 단계 충돌 (RebaseConflict)
  base: main → ralph/fix5-rebase-conflict-plan
  충돌 파일 (3건):
    - Ralph/Services/WorktreeService.cs
    - Ralph/Services/MergeOrchestrator.cs
    - tasks.json
  조치: 이 task만 실패 처리하고 batch의 다른 독립 task는 계속 진행합니다.
  재실행: ralph --task fix5-rebase-conflict-plan --force
  수동 머지: git checkout ralph/fix5-rebase-conflict-plan && git rebase main
```

구현 메모 (locale-safe):
- `Console.OpenStandardError()` 또는 `AnsiConsole.Console.Profile.Out`이
  아니라 `AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) })`
  로 별도 출력 인스턴스 사용 (또는 단순히 `Console.Error.WriteLine`).
- 메시지는 모두 ASCII + 한국어 (UTF-8). `Program.cs`가 이미 `Console.OutputEncoding = Encoding.UTF8`을
  세팅하지만, `Console.Error`가 별도면 같이 UTF-8로 강제. 환경 변수
  `RALPH_NO_COLOR=1`이 있으면 ANSI escape를 끄는 기존 정책 재사용.
- 파일 경로는 worktree 기준 상대 경로로 표시 (절대 경로는 진단용으로
  로그에만 기록).
- locale 의존 함수(`DateTime.ToString()` 등) 사용 금지 — 메시지 자체에
  포맷 변환이 들어가는 부분 없음.

stdout(AnsiConsole)에는 한 줄 요약만:

```
  [red]✗[/] fix5-rebase-conflict-plan rebase 충돌 (자세한 내용은 stderr 확인)
```

### 3.6 batch 내 다른 task 진행 보장

`MergeAndFinalizeAsync`의 `foreach (var taskId in taskIds)` 루프는 이미
순차다. RebaseConflict 시 `continue`로 다음 task로 진행하면, 다른 task의
worktree와 base 사이에서는 **별개 rebase**가 다시 일어난다.
- 다른 task의 rebase는 자기 worktree에서 자기 commit을 base 위로 다시
  얹는 작업이라 실패 task의 변경에 영향받지 않는다.
- 단, 실패 task의 rebase 시도는 base에 commit을 남기지 않았으므로 (rebase는
  worktree 안에서만 동작) 다른 task가 보는 base는 변화 없음 → 깨끗.

부수효과 검증:
- `state.json` 마킹: 실패 task는 done 마킹 안 함 (정상). 다른 task는
  자기 머지 단계에서 done 마킹.
- `validation.jsonl`: 실패 task는 머지 단계에 도달하지 못해 declared 검증
  로그도 남지 않음 (의도). 진단 정보는 ralph session 로그에만.

### 3.7 `fix2 #2` (cost-failures-impl)와의 격리

본 fix가 동시 진행 중인 `fix2-cost-failures-impl`은 `Ralph/Services/CostTracker.cs`,
`Ralph/Services/RalphPaths.cs`만 만진다. 본 fix의 코드 변경 예상 파일
(`WorktreeService.cs`, `MergeOrchestrator.cs`)와 겹치지 않으므로 머지 시
충돌 없음. 단, `MergeFailureKind` enum 도입 시점에 `RalphPaths.cs`를
건드리지 않도록 위치를 `WorktreeService.cs` 내부 또는 별도 새 파일로
한정한다.

---

## 4. 회귀 / 호환성

| 시나리오 | 현재 동작 | 변경 후 기대 |
|---|---|---|
| **충돌 없는 정상 rebase** | rebase 성공 → 그대로 머지 | 동일. `MergeResult { Success = true }` 반환만 추가. **회귀 없음**. |
| **rebase 충돌 → 머지에서 풀리는 케이스** (현 silent fallback) | rebase abort → 3-way merge → conflictStrategies로 해결 가능 | task 실패로 분류 → 사용자에게 재실행 안내. **정책 변화** — 자동 회복이 줄어드는 대신 진단 명확성↑. PRD 요구사항이 이를 명시. |
| **rebase 성공 / 머지 충돌** | conflictStrategies 체인 적용 | 동일. **회귀 없음**. |
| **rebase abort 자체 실패** | warn 후 fallback 머지 (worktree 더러운 채로) | `Other` 분류 + batch 중단 (안전). **정책 변화** — 더러운 worktree에서 silent 진행하지 않음. |
| **untracked overwrite (머지 단계)** | 자동 백업 + 재머지 | 동일. `FailureKind = UntrackedOverwrite` 라벨만 추가, 동작 동일. |
| **`--max-parallel 1`** | 순차 머지에서 rebase가 거의 no-op | 동일. **회귀 없음**. |
| **batch 내 한 task RebaseConflict, 다른 task 정상** | 한 task의 fallback 머지가 다른 task 머지와 섞여 디버깅 어려움 | 실패 task는 즉시 cleanup, 나머지 task는 정상 진행. **개선**. |

확인 항목 (PR 단계):
- [ ] 충돌 없는 rebase의 logger 메시지 포맷이 그대로인지.
- [ ] `MergeResult.Success == true` 분기의 호출자가 `FailureKind`에
  의존하지 않는지 (`None` 기본값으로 유지되는지).
- [ ] `bool` → `MergeResult` 시그니처 변경의 모든 호출처(`MergeOrchestrator`만)
  업데이트 누락 없음.
- [ ] `Ralph.Tests/` 안에서 `AdvanceWorktreeOntoBaseAsync`를 직접 호출하는
  테스트가 있다면 시그니처 갱신.

---

## 5. 영향 파일 (구현 단계 예상)

- `Ralph/Services/WorktreeService.cs`
  - `MergeFailureKind` enum 신설 (namespace 레벨, public).
  - `MergeResult.FailureKind` 추가.
  - `AdvanceWorktreeOntoBaseAsync` 반환형 `bool` → `MergeResult`.
  - `GetRebaseConflictFilesAsync` 사설 헬퍼 신설 (worktree 작업 디렉터리
    기준 unmerged 파일 추출).
  - `MergeWorktreeAsync` 의 실패 경로에 `FailureKind = MergeConflict` 또는
    `UntrackedOverwrite` 부착 (라벨링만, 동작 동일).
- `Ralph/Services/MergeOrchestrator.cs`
  - rebase 결과를 `MergeResult`로 받아 분기.
  - `PrintRebaseConflict(taskId, MergeResult)` 신설 — stderr 출력.
  - 실패 task 누적 리스트 + batch 종료 시 요약.
  - RebaseConflict 시 `continue`로 다음 task 진행.
- `Ralph.Tests/MergeOrchestratorTests.cs` 또는
  `Ralph.Tests/WorktreeServiceTests.cs`
  - §6 시나리오 추가.

---

## 6. 테스트 시나리오

각 시나리오는 임시 git repo + 임시 `.ralph-worktrees/` 베이스로 격리.
실제 git 명령을 사용하는 통합 테스트 (mock 금지 — fix2 #5의 핵심 동작은
git rebase의 실제 충돌 거동이므로).

1. **rebase_clean_no_change**
   - base에서 commit A. worktree branch에서 commit B (다른 파일).
   - `AdvanceWorktreeOntoBaseAsync` 호출.
   - assert: `Success = true`, `FailureKind = None`. worktree 디렉터리에
     변경 없음. 회귀 안전망.

2. **rebase_conflict_marks_task_failed**
   - base에서 commit A1 (`foo.txt = "base"`).
   - worktree branch에서 commit B1 (`foo.txt = "worktree"`).
   - base에 commit A2 추가 (`foo.txt = "base2"`).
   - `AdvanceWorktreeOntoBaseAsync` 호출.
   - assert: `Success = false`, `FailureKind = RebaseConflict`,
     `ConflictFiles = ["foo.txt"]`. `git rebase --abort` 후 worktree HEAD가
     B1 시점. unmerged index 비어있음.

3. **batch_continues_when_one_task_rebase_conflicts**
   - 두 task가 각자 worktree를 가지며, 한쪽은 base와 충돌, 다른 쪽은
     무관한 파일을 수정.
   - `MergeAndFinalizeAsync` 호출.
   - assert: 충돌 task는 cleanup + 실패 누적, 다른 task는 정상 머지 +
     done 마킹. batch 종료 코드 1 (한 건이라도 실패 시).

4. **rebase_conflict_message_to_stderr**
   - 시나리오 #2 + stderr 캡처.
   - assert: 캡처된 stderr가 `[merge:advance]` prefix, taskId, 충돌 파일
     목록, `ralph --task {id} --force` 안내를 모두 포함. UTF-8 디코딩
     안정 (한글 문구 포함).

5. **no_silent_fallback_to_3way_merge**
   - 시나리오 #2 직후 `MergeWorktreeAsync`가 호출되지 않음을 검증
     (MergeOrchestrator 흐름 단위 테스트, 또는 spy logger).
   - assert: `[merge:advance] ... RebaseConflict` 로그가 나오면 같은 taskId
     에 대해 `[merge]` 로그가 안 나온다.

6. **rebase_abort_failure_classified_as_other**
   - rebase 충돌 발생 + `git rebase --abort` 가 (시뮬레이션으로) 실패하는
     상황. 예: 외부 프로세스가 `.git/rebase-merge/` lock 파일을 점유.
     실현 어렵다면 fake `IGitClient`로 abort exit code != 0 강제.
   - assert: `FailureKind = Other`, batch 중단 (return 1).

7. **conflict_strategies_not_applied_to_rebase_step**
   - `workflow.parallel.conflictStrategies = ["auto-theirs", "claude"]`
     설정 + 시나리오 #2.
   - assert: rebase 충돌 시 `claude` CLI 가 호출되지 않음 (claude runner
     spy의 호출 카운트 0). `auto-theirs`로 재 rebase 시도하지 않음.

8. **already_up_to_date** (회귀 안전망)
   - base와 worktree branch의 커밋이 동일 (advance 불필요).
   - assert: `Success = true`. 사용자 대상 메시지 출력 없음 (info 로그만).

---

## 7. 마이그레이션 / 향후 작업

- **워크트리 보존 옵션**: 현재 RebaseConflict 시 worktree를 cleanup하지만,
  사용자가 수동으로 충돌을 보고 싶을 수 있다. `--keep-failed-worktrees`
  옵션은 별도 fix로 분리 (범위 외).
- **`workflow.parallel.rebaseStrategies`**: §3.4의 의미 반전·비용 가드를
  설계한 뒤 도입 검토. 본 fix는 단일 정책 fix.
- **batch 부분 성공 정책**: 현재는 한 건이라도 실패하면 exit 1. 별도
  exit code(예: 2 = 부분 성공)로 분리할지는 별도 논의.
- **state.json 실패 분류 기록**: `state.json`에 task별 last-failure
  (`{ kind: "RebaseConflict", at: "..." }`) 를 남기면 `--list` /
  `--status`에서 시각화 가능. 범위 외.

---

## 8. 완료 보고

- **생성**: `docs/fix2/05-rebase-conflict-plan.md` (본 문서)
- **수정**: 없음
- **Scope 외 변경**: 없음
- **참고 문서**: `docs/fix2/04-worktree-branch-guard-plan.md` (선행 task,
  branch guard 모델), `fix2.md` #5 항목.
