# F2 테스트 리포트 — tasks.json 머지 가드 검증

- **태스크 ID**: f2-worktree-tasksjson-test
- **검증 대상 구현**: f2-worktree-tasksjson-impl
  - `Ralph/Services/WorktreeService.cs`
  - `Ralph/Services/ParallelExecutor.cs`
- **검증 일자**: 2026-04-28
- **결론**: 1차 코드 리뷰에서 **머지 가드 미작동 결함**을 시뮬레이션으로 재현. `WorktreeService.NormalizeTasksJsonAsync`에 정규화 결과를 커밋하는 단계를 추가하여 보완 완료. 보완 후 동일 시나리오에서 충돌이 발생하지 않음을 재시뮬레이션으로 확인.

---

## 1. 빌드 검증

```
dotnet build Ralph/Ralph.csproj
→ 경고 0개 / 오류 0개 (보완 전·후 모두 OK)
```

## 2. 코드 리뷰 — git 명령 인자/cwd

### 2.1 `WorktreeService.NormalizeTasksJsonAsync` (보완 후)

| 단계 | 호출 | cwd | 비고 |
|---|---|---|---|
| diff | `git diff --name-only {baseRef}..HEAD -- {tasksFileName}` | worktreePath | ✓ 인자 순서, `--`로 path-spec 구분 |
| checkout | `git checkout {baseRef} -- {tasksFileName}` | worktreePath | ✓ baseRef 버전을 worktree index/working tree에 적재 |
| **commit (보완)** | `git commit -m "guard: …" -- {tasksFileName}` | worktreePath | **신규**. checkout만으로는 branch tip이 변하지 않아 머지가 여전히 충돌 (3.b 참고) |

`_git.RunAsync(string[] arguments, string? workingDirectory, CancellationToken ct)`(`Ralph/Services/GitService.cs:42-43`)의 시그니처상 두 번째 인자가 그대로 cwd로 전달되어 `worktreePath`로 동작함을 확인.

### 2.2 `ParallelExecutor` 머지 직전 호출 위치

`Ralph/Services/ParallelExecutor.cs:264-278` — 순차 머지 루프 내, `MergeWorktreeAsync` 직전:

```csharp
foreach (var taskId in taskIds)
{
    tracker.UpdateStatus(taskId, TaskProgressStatus.Merging);

    // F2: 머지 직전 worktree의 tasks.json이 baseBranch와 다르면 강제 정규화.
    await _worktree.NormalizeTasksJsonAsync(
        taskId, baseBranch,
        tasksFileName: Path.GetFileName(_tasksFile),
        logger: _logger,
        ct: ct);

    var mergeResult = await _worktree.MergeWorktreeAsync(
        taskId, baseBranch, conflictStrategy, _logger, ct);
    ...
}
```

- 호출 시점이 `MergeWorktreeAsync` 직전이며 모든 batch 태스크에 대해 보장됨. ✓
- `tasksFileName`로 `Path.GetFileName(_tasksFile)`을 넘기는데, `_tasksFile`이 단순 파일명(`tasks.json`)인 일반 케이스와 사용자 지정 경로(`./custom.json`) 모두 안전. ✓
- 1차 방어인 `GuardTasksFileAsync`(working-tree 단계)와 직교하여 **commit-tree 변경** 케이스만 추가로 본다. ✓

---

## 3. 시나리오 시뮬레이션

`/tmp/ralph-test-f2`에 임시 git 저장소를 만들어 실제 git CLI로 재현. (자세한 명령은 본 리포트 작성 중 세션 로그 참조)

### 3.a 워크트리에서 tasks.json 미수정 → no-op

```
git diff --name-only main..HEAD -- tasks.json
→ ""(빈 출력)
→ NormalizeTasksJsonAsync는 line 147-148에서 false 반환, checkout/commit 미실행
```

**결과: PASS** — 정상 케이스에서 부작용 없이 통과.

### 3.b 워크트리에서 tasks.json 수정·커밋 → 정규화 → 머지 무충돌

세팅:
- 초기 `tasks.json`: `{"tasks":[{"id":"a","done":false},{"id":"b","done":false}]}`
- 워크트리(`ralph/a`)에서 `a:done=true`로 수정·커밋 (Claude의 잘못된 행동 모사)
- 메인(main)에서 `b:done=true`로 수정·커밋 (이전 batch의 `CommitTasksFileAsync` 모사)

#### (1) **보완 전** 동작 — 결함 재현

```
[diff] tasks.json                  ← 변경 감지됨
git checkout main -- tasks.json    ← 워크트리 작업트리/인덱스만 갱신
git status --porcelain → "M  tasks.json"  ← HEAD 커밋은 그대로
git merge --no-ff ralph/a -m ...   ← 메인에서 머지
→ CONFLICT (content): Merge conflict in tasks.json
```

**원인**: `git checkout {ref} -- {file}`은 path-restricted checkout이라 *현재 브랜치 tip 커밋을 수정하지 않는다*. 워크트리의 인덱스/작업트리만 baseRef로 맞춰질 뿐, `ralph/a` 브랜치의 HEAD 커밋은 여전히 수정된 tasks.json을 들고 있어 메인에서 `git merge ralph/a`를 실행하면 3-way 머지가 충돌함.

#### (2) **보완 후** 동작

`NormalizeTasksJsonAsync`에 `git commit -m "guard: …" -- {tasksFileName}`을 추가하면:

```
[diff] tasks.json
git checkout main -- tasks.json
git commit -m "guard: tasks.json을 main 버전으로 정규화" -- tasks.json
→ ralph/a HEAD가 새 커밋으로 갱신, tasksFile 내용은 main과 동일
git merge --no-ff ralph/a -m ...
→ Merge made by the 'ort' strategy. (충돌 없음)
→ 최종 tasks.json = main의 버전 보존
```

**결과: PASS** — Warn 로그 출력 후 충돌 없이 머지 완료.

### 3.c base의 tasks.json이 ralph 갱신본으로 보존되는가

3.b의 머지 결과에서 `cat tasks.json` 확인:

```
{"tasks":[{"id":"a","done":false},{"id":"b","done":true}]}
```

- 정규화로 worktree tip의 tasks.json이 main의 tip과 **바이트 단위로 동일**해진 상태에서 머지하므로, 3-way 머지의 ours/theirs가 같아 main의 버전이 그대로 보존됨. ✓
- 즉 `ParallelExecutor.CommitTasksFileAsync`가 매 배치 끝에 갱신해 둔 base의 진본이 절대 덮어써지지 않음.

**결과: PASS**

---

## 4. 발견 사항 및 보완

### 4.1 결함

`WorktreeService.NormalizeTasksJsonAsync`(보완 전, line 154-163)는 `git checkout baseRef -- tasksFileName`만 수행했으며, 이는 worktree의 작업트리/인덱스만 갱신하고 ralph/{taskId} 브랜치 tip 커밋을 변경하지 않음. 따라서 Claude가 worktree에서 tasks.json을 **커밋한** 케이스(이 메서드가 본래 막아야 할 commit-tree 위반)에서 가드가 무력화되어 충돌이 그대로 발생.

3-way 머지가 ours/theirs를 worktree 작업트리가 아니라 브랜치 tip 커밋에서 가져오기 때문이며, 시뮬레이션(3.b-1)에서 이 동작이 그대로 재현됨.

### 4.2 패치

`Ralph/Services/WorktreeService.cs` `NormalizeTasksJsonAsync`에 정규화 결과를 worktree에서 커밋하는 단계를 추가:

```csharp
var (commitExit, commitOut) = await _git.RunAsync(
    ["commit", "-m", $"guard: {tasksFileName}을 {baseRef} 버전으로 정규화", "--", tasksFileName],
    worktreePath, ct);
```

- pathspec(`-- {tasksFileName}`)을 사용해 tasks.json만 커밋. 다른 staged 변경(있으면)은 영향 없음.
- 실패해도 머지를 막지 않고 Warn만 남김(기존 정책 일관성).
- 시나리오 3.a(no-op)에서는 그 이전 단계에서 일찍 return하므로 commit이 호출되지 않아 빈 커밋이 생성되지 않음. ✓

### 4.3 기타 검토(보고만)

- `ParallelExecutor.RunInWorktreeWithLogAsync`의 `GuardTasksFileAsync` 호출은 working-tree 단계(line 367)에서 동작하며, Claude가 `git commit`을 직접 실행하기 전에 호출되어 일반 케이스를 충분히 커버한다. 1차/2차 방어가 직교적으로 동작하는 설계 자체에는 이상 없음.
- `Path.GetFileName(_tasksFile)`은 `_tasksFile`이 절대 경로이거나 디렉토리 포함 경로(`./tasks.json`, `subdir/tasks.json`)일 때 파일명만 추출한다. 만약 사용자가 서브디렉토리의 task 파일을 사용한다면 cwd가 worktreePath이므로 git pathspec이 어긋날 수 있으나, 현 시점 ralph는 repo root의 단일 파일을 가정하므로 기존 동작을 유지했다(개선은 별도 태스크로 권장).

---

## 5. 산출물

- 본 리포트: `.ralph-plans/f2-test-report.md`
- 보완 패치 적용 파일:
  - `Ralph/Services/WorktreeService.cs` (NormalizeTasksJsonAsync에 commit 단계 추가)

Scope 외 파일 변경 없음. 빌드 정상.
