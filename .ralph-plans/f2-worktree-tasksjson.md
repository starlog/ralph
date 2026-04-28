# F2 계획: worktree 안 `tasks.json` 쓰기 방어 (P0)

## 1. 배경 / 문제 정의

병렬 실행에서 각 태스크는 `git worktree`에 격리되어 Claude가 작업한다.
프롬프트(`PromptBuilder`)는 "`tasks.json` 수정 금지"를 명시하지만, Claude가 이를 무시하고
`tasks.json`을 수정·삭제·재생성하는 사고가 실측됨.

영향:
- 머지 단계에서 `tasks.json` 충돌이 가장 흔한 충돌 케이스가 됨.
- 동일 배치 내 다른 worktree의 정상 머지까지 지연/실패시킴.
- `--reset` 같은 안전 동작 없이 사용자 진행 상태가 임의로 바뀔 수 있음.

## 2. 현재 구현 분석

### 2.1 이미 존재하는 1차 방어선

`Ralph/Services/ParallelExecutor.cs:386` — `GuardTasksFileAsync(taskId, worktreePath, logWriter, ct)` private 메서드:

- `git status --porcelain -- tasks.json` 으로 working-tree 변경 감지.
- 변경 발견 시 `_logger.Warn` 기록 + `logWriter` 에 마커 출력.
- `git reset HEAD -- tasks.json` 으로 staged 변경 unstage.
- 추적 파일이면 `git checkout HEAD -- tasks.json` (HEAD 기준 복원).
- 신규 파일(`??`/`A`)이면 디스크에서 삭제.

호출 지점: `RunInWorktreeWithLogAsync`의 `ParallelExecutor.cs:358`
(Claude 실행 직후, worktree 커밋(`CommitChangesAsync`) 직전).

### 2.2 현재 구현의 한계 (왜 추가 방어가 필요한가)

1. **커밋 후 변경을 잡지 못함.** `Guard`는 `CommitChangesAsync` 이전에 실행되지만,
   `CommitChangesAsync`는 `git add -A` 로 모든 변경을 stage 한다. 만약 Claude가
   `tasks.json` 을 수정한 뒤 1차 방어가 실패(예: status 파싱 오류)하거나
   Claude가 worktree 안에서 직접 `git commit`을 실행했다면, 머지 시점에 이미
   커밋된 상태로 들어와 있다.
2. **HEAD 기준 복원의 모호성.** 현재는 `git checkout HEAD -- tasks.json`인데,
   머지 충돌 방지의 핵심은 **base 브랜치(머지 대상)** 기준으로 복원하는 것.
   다른 배치에서 먼저 머지된 `tasks.json` 상태와 정확히 일치해야 충돌이 0이다.
3. **누적 위반 통계 부재.** Claude가 반복적으로 규칙을 위반해도 단발성
   경고만 남고, 운영자가 패턴을 인지하기 어렵다.

## 3. 설계

### 3.1 새 메서드: `WorktreeService.NormalizeTasksJsonAsync`

**시그니처:**
```csharp
public async Task<TasksJsonGuardResult> NormalizeTasksJsonAsync(
    string taskId,
    string worktreePath,
    string baseRef,
    string tasksFileName,
    RalphLogger? logger = null,
    CancellationToken ct = default);
```

**반환 타입:**
```csharp
public sealed class TasksJsonGuardResult
{
    public bool Violated { get; init; }      // tasks.json이 base와 달랐는지
    public bool Normalized { get; init; }    // 복원/커밋이 성공했는지
    public string? ErrorMessage { get; init; }
}
```

**왜 `WorktreeService`에 두는가:** 머지 도메인 로직이며 `MergeWorktreeAsync`와
같은 라인업에서 호출된다. `ParallelExecutor`의 private에 두지 않고 서비스로
끌어올려 테스트성과 재사용성을 확보한다(예: `--task` 단일 모드 확장 시 재사용).

### 3.2 git 명령 시퀀스 (책임은 모두 `WorktreeService` 안)

```
1. git -C {worktreePath} fetch --no-tags --quiet origin   # 생략 가능 (로컬 ref 사용)
2. git -C {worktreePath} diff --name-only {baseRef}..HEAD -- {tasksFileName}
   → 출력이 비면 위반 없음, 즉시 Violated=false 반환.
3. (위반 시) RalphLogger.Warn:
   "⚠️  worktree '{taskId}' committed changes to {tasksFileName} (vs {baseRef}). 강제 정상화."
4. git -C {worktreePath} checkout {baseRef} -- {tasksFileName}
   → exit != 0 이면 ErrorMessage 채우고 Normalized=false 반환.
5. git -C {worktreePath} add -- {tasksFileName}
6. git -C {worktreePath} diff --cached --quiet
   → exit==0이면(차이 없음) 커밋 생략. 이미 base와 동일한 결과로 staging 됐을 수 있음.
7. (차이 있을 때) git -C {worktreePath} commit -m "guard: {tasksFileName} 정상화 (taskId={taskId})"
   → exit != 0 이면 ErrorMessage 채우고 Normalized=false 반환.
8. Normalized=true, Violated=true 로 반환.
```

`baseRef`는 호출자가 정한다(예: `main`, `ralph/parent` 등). `ParallelExecutor`는
`RunAsync`의 `baseBranch` 변수를 그대로 전달.

`tasksFileName`은 항상 `Path.GetFileName(_tasksFile)` 로 얻은 상대 경로.
worktree 안에서는 repo 루트 = worktree 루트이므로 추가 경로 조작 불필요.

### 3.3 호출 위치

`Ralph/Services/ParallelExecutor.cs` 의 `RunParallelBatchAsync` 안, **머지 직전 루프**.

현재 코드(요약):
```
foreach (var taskId in taskIds)            // line 264
{
    tracker.UpdateStatus(taskId, TaskProgressStatus.Merging);

    var mergeResult = await _worktree.MergeWorktreeAsync(   // line 268
        taskId, baseBranch, conflictStrategy, _logger, ct);
    ...
}
```

수정 후:
```
foreach (var taskId in taskIds)
{
    tracker.UpdateStatus(taskId, TaskProgressStatus.Merging);

    // F2: 머지 직전 tasks.json 정상화
    var guardResult = await _worktree.NormalizeTasksJsonAsync(
        taskId, worktrees[taskId], baseBranch,
        Path.GetFileName(_tasksFile), _logger, ct);

    if (guardResult.Violated)
    {
        _violationCounter.Record(taskId);   // 3.5 참조
        if (!guardResult.Normalized)
        {
            _logger.Error($"tasks.json 정상화 실패: {guardResult.ErrorMessage}");
            // 정책: 정상화 실패 시 해당 태스크 머지 스킵, 워크트리는 보존(사용자 검토용)
            AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(taskId)} 정상화 실패. 머지 스킵.");
            continue;
        }
    }

    var mergeResult = await _worktree.MergeWorktreeAsync(...);
    ...
}
```

### 3.4 1차 방어(`GuardTasksFileAsync`)와의 관계

- **유지한다.** 1차는 working-tree, 2차는 commit-tree 를 본다. 두 계층이 직교하므로
  중복이 아니다. 1차에서 차단되면 2차에서는 `Violated=false` 가 자연스럽게 나온다.
- 단, 1차의 `git checkout HEAD -- tasks.json` 을 `git checkout {baseRef} -- tasks.json`
  로 바꾼다(시그니처에 `baseRef` 추가). 이유: HEAD = worktree 자기 브랜치 tip 이므로
  Claude가 이전 단계에서 커밋했다면 HEAD 자체가 오염돼 있을 수 있다. `baseRef`가 진실의 원천이다.

### 3.5 누적 위반 카운터 (선택, 권장)

- 위치: `Ralph/Services/TasksJsonViolationCounter.cs` (신규, 단일 책임).
- 저장: 메모리 누적 + 세션 종료 시 `.ralph-logs/tasks-json-violations-{yyyyMMdd-HHmmss}.json`
  로 직렬화.
- 형식:
  ```json
  {
    "session": "2026-04-28T11:30:00Z",
    "totalViolations": 3,
    "byTask": { "f3-impl": 2, "f5-impl": 1 }
  }
  ```
- 노출: `RalphLogger.Warn`에 누적 카운트 포함(`"위반 누적: 3건 / 태스크 f3-impl 2회"`).
- 실패 모드 분리는 추후 단계: 동일 태스크가 N회 이상 위반하면 자동 비활성화 등은
  본 계획에서 제외(P1+ 대상).

### 3.6 에러 처리 정책 (요약)

| 단계 | 실패 시 |
|---|---|
| `diff` 실행 자체 실패 | `_logger.Error` 후 **머지를 막지 않음**(false-positive 차단). worktree는 그대로 머지 시도. |
| `checkout` 실패 | `Normalized=false` 반환. 호출자가 머지 스킵 + worktree 보존. |
| `commit` 실패 (스테이지 비었음) | 정상 케이스로 간주, Normalized=true. |
| `commit` 실패 (그 외) | `Normalized=false`, 머지 스킵. |
| 정상화 자체 성공 | 머지 진행. 후속 머지에서 충돌 0 보장. |

핵심 원칙: **방어 로직이 머지를 망가뜨리면 안 된다.** 의심스러우면 보수적으로
머지를 시도하고, 충돌이 나면 기존 `HandleMergeConflictAsync` 경로가 받는다.

## 4. 회귀 위험 분석

| 위험 | 가능성 | 완화책 |
|---|---|---|
| `baseRef`에 `tasks.json`이 없는 신규 프로젝트에서 `git checkout {baseRef} -- tasks.json` 실패 | 낮음(F2가 도는 시점엔 이미 `EnsureInitialCommitAsync`가 1커밋을 보장) | exit code 점검 후 fallback: 디스크에서 파일 삭제 + 결과 보고 |
| 합법적으로 `tasks.json`을 수정해야 하는 태스크(예: 자체 메타 관리) | 매우 낮음(현 schema 기준 없음) | 향후 `task.allowsTasksJsonWrite: true` 같은 opt-in 플래그가 필요해지면 그때 `WorktreeService` 호출자에서 분기 |
| `--sequential` 모드(worktree 미사용)에 영향 | 없음 | 호출 위치가 `RunParallelBatchAsync` 한정 |
| 1차 방어와 2차 방어의 이중 경고로 사용자 혼란 | 중간 | 메시지 prefix를 분리: 1차 `[guard:pre-commit]`, 2차 `[guard:pre-merge]` |
| `tasks.json` 외 파일도 같은 방식으로 보호하고 싶어질 때 확장성 | 낮음 | 시그니처가 `tasksFileName`을 받으므로 추후 일반화 가능. 본 PR은 단일 파일에 한정 |
| 위반 카운터 파일 쓰기 실패가 머지를 막는 경우 | 낮음 | 카운터는 try/catch로 감싸 best-effort 저장만 수행 |
| 커밋 메시지 한국어 컨벤션 위반 | 낮음 | 7번 단계의 메시지는 한국어 사용 (CLAUDE.md 컨벤션 준수) |
| `_violationCounter` 미주입 시 NRE | 낮음 | `ParallelExecutor` 생성자에서 항상 인스턴스화 (옵션 아님) |

## 5. 검증 시나리오 (구현 단계에서 수행)

1. **정상 worktree** — Claude가 `tasks.json` 미수정. `Violated=false`, 머지 0충돌.
2. **uncommitted 위반** — Claude가 worktree에서 `tasks.json` 수정 후 커밋 안 함.
   1차 `GuardTasksFileAsync`가 잡고, 2차는 `Violated=false`.
3. **committed 위반** — Claude가 worktree에서 `tasks.json` 수정 + 직접 커밋.
   2차 `NormalizeTasksJsonAsync`가 잡고 정상화 후 머지 성공.
4. **신규 worktree에서 tasks.json 삭제** — Claude가 파일을 지우고 커밋.
   `checkout {baseRef}`로 복원되어야 함.
5. **`--sequential` 회귀 검사** — 본 변경이 단일 태스크 직접 실행 경로에 영향 없는지 확인.
6. **여러 배치 연속** — 첫 배치 위반 → 정상화 → 머지. 다음 배치 시작 전 base의
   `tasks.json`이 일관된 상태인지 확인.

## 6. 작업 분해 (구현 PR에서)

1. `WorktreeService.NormalizeTasksJsonAsync` + `TasksJsonGuardResult` 추가.
2. `Ralph/Services/TasksJsonViolationCounter.cs` 신설 + `ParallelExecutor` 주입.
3. `ParallelExecutor.RunParallelBatchAsync` 머지 루프(현 `~line 264-291`)에 호출 삽입.
4. 기존 `GuardTasksFileAsync`의 복원 ref를 HEAD → `baseRef` 로 교체 (시그니처 확장).
5. 단위/통합 테스트: 위 §5의 6개 시나리오를 e2e 형태로 자동화하기 어려우므로
   `WorktreeService` 단위에서 fake repo 만들어 1·3·4번 시나리오 검증.
6. 문서: `CLAUDE.md` 의 "Parallel Execution Flow" 섹션에 가드 두 단계 명시.

## 7. 비목적 (Out of Scope)

- Claude의 위반 자체를 사전에 막는 sandboxing(파일시스템 읽기 전용 등)은 별도 P1 이상 과제.
- `tasks.json` 외 다른 보호 대상(`.ralph-plans/`, `README.md` 등)은 본 계획에서 다루지 않음.
- 위반 누적 시 자동 차단 정책(예: 3회 이상 → 태스크 비활성화)은 차기 단계.

## 8. 결론

머지 직전 `git diff --name-only {baseRef}..HEAD -- tasks.json` 검사를
`WorktreeService.NormalizeTasksJsonAsync` 로 신설하여 ParallelExecutor의 머지 루프
직전에 호출한다. 기존 1차 방어(`GuardTasksFileAsync`)는 유지하되 복원 기준을 HEAD →
`baseRef` 로 교체한다. 위반은 별도 카운터에 누적해 운영 가시성을 확보한다.
정상화 실패 시 머지를 스킵하고 worktree를 보존하여 사용자 검토 여지를 남긴다.
