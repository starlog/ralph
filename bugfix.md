# Ralph 자가 수정 PRD — 코드 분석으로 발견된 버그 일괄 수정

본 문서는 `ralph --plan bugfix.md`로 tasks.json을 생성한 뒤 `ralph --run`으로
자가 수정을 실행하기 위한 PRD입니다. 각 버그는 가능한 한 **독립 feature**로 정의하여
병렬 실행이 가능하도록 했습니다 (수정 파일이 겹치지 않으면 worktree에서 동시 수정).

## 프로젝트 개요

- 프로젝트: `ralph` (.NET 8 CLI 태스크 오케스트레이터)
- 주 코드 위치: `Ralph/`, 테스트: `Ralph.Tests/`
- 언어/스택: C# 12 / .NET 8, Spectre.Console
- 빌드: `dotnet build Ralph/Ralph.csproj -nologo`
- 테스트: `dotnet test`
- 커밋 메시지: 한국어로 작성 (CLAUDE.md 규칙)

## 공통 작업 규칙

- 모든 task의 `verification`은 `dotnet build -nologo` 또는 해당 테스트 명령으로 지정.
- `tasks.json`은 절대 수정 금지 (worktree 격리, ralph가 자동 관리).
- 각 feature는 1~2 task로 구성 (granularity: small). 수정 파일이 1~2개로 한정되므로
  4-phase로 부풀리지 말 것.
- 수정 파일을 `modifiedFiles`에 정확히 명시 — ralph의 병렬 충돌 감지에 사용됨.

---

## Feature 1 — `validation.jsonl` 로그 보호 누락

**파일:** `Ralph/Services/LogRotator.cs:15-19`

`ProtectedFiles`에 `"validation.json"`이 등록되어 있으나 실제 파일명은
`Ralph/Services/WorktreeService.cs:285`에서 `validation.jsonl`로 기록됨.
30일 retention 도달 시 검증 이력이 삭제되는 버그.

**수정:**
- `LogRotator.cs:18`의 `"validation.json"`을 `"validation.jsonl"`로 변경.

**verification:** `dotnet build Ralph/Ralph.csproj -nologo`

**modifiedFiles:** `Ralph/Services/LogRotator.cs`

---

## Feature 2 — `GitService.RunAsync`의 stdout/stderr 파이프 데드락

**파일:** `Ralph/Services/GitService.cs:42-68`

`stdout = await ReadToEndAsync()` → `stderr = await ReadToEndAsync()` 순차 처리로
인해 stderr 버퍼(약 64KB)가 먼저 차면 자식 프로세스가 stderr write에서 블록되고
부모는 stdout read에서 블록되어 데드락 가능. `ClaudeService`/`VerificationRunner`는
이미 두 스트림을 병렬로 읽고 있음.

**수정:**
- `RunAsync`에서 `ReadToEndAsync()` 두 호출을 모두 Task로 먼저 시작한 뒤 await.
  ```csharp
  var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
  var stderrTask = process.StandardError.ReadToEndAsync(ct);
  var stdout = await stdoutTask;
  var stderr = await stderrTask;
  await process.WaitForExitAsync(ct);
  ```

**verification:** `dotnet test --filter "FullyQualifiedName~GitService"` 또는
이에 해당하는 테스트가 없으면 `dotnet build Ralph/Ralph.csproj -nologo`.

**modifiedFiles:** `Ralph/Services/GitService.cs`

---

## Feature 3 — `VerificationRunner`에서 외부 cancel을 timeout으로 오인

**파일:** `Ralph/Services/VerificationRunner.cs:48-56`

`Task.WhenAny(exitTask, Task.Delay(timeout, ct))`에서 사용자 Ctrl+C로 `ct`가 취소되면
`Task.Delay`가 canceled 상태로 먼저 완료되어 코드가 `timedOut = true`로 분기,
프로세스를 강제 종료하고 `TimedOut` 결과를 반환하면서 `OperationCanceledException`을
삼킴. `ClaudeService.RunStreamAsync`처럼 외부 ct fire를 별도로 판정해야 함.

**수정:**
- `WhenAny` 직후 `ct.IsCancellationRequested`를 검사해 true면 OCE를 throw.
  외부 cancel과 internal timeout을 분리.
- 분기 예시:
  ```csharp
  if (winner != exitTask)
  {
      if (ct.IsCancellationRequested)
      {
          try { process.Kill(entireProcessTree: true); } catch { }
          ct.ThrowIfCancellationRequested();
      }
      timedOut = true;
      ...
  }
  ```

**verification:** `dotnet test --filter "FullyQualifiedName~Verification"` 또는
없으면 `dotnet build Ralph/Ralph.csproj -nologo`.

**modifiedFiles:** `Ralph/Services/VerificationRunner.cs`

---

## Feature 4 — `RalphLogger`의 thread-safety 결여

**파일:** `Ralph/Services/RalphLogger.cs:5-32`

`StreamWriter.WriteLine`은 thread-safe가 아닌데 `ParallelExecutor`는 N개의
worker Task에서 `_logger.Info/Warn/Error`를 동시 호출. byte 단위 인터리브 또는
드물게 IOException 가능.

**수정:**
- `Log()` 내부를 `lock (_lockObj)` 블록으로 감싸거나, `_writer`를
  `TextWriter.Synchronized(...)` 래퍼로 사용.
- `private readonly object _lockObj = new();`를 추가.

**verification:** `dotnet build Ralph/Ralph.csproj -nologo`

**modifiedFiles:** `Ralph/Services/RalphLogger.cs`

---

## Feature 5 — `DetectStaleWorktreesAsync`의 슬라이스 ArgumentOutOfRangeException

**파일:** `Ralph/Services/WorktreeService.cs:466-483`

`line["branch refs/heads/".Length..]`는 prefix 19자를 가정. git 출력이 변형되어
`refs/heads/` 없이 `branch ralph/foo`로 오면 16자 < 19자라 슬라이스 예외 발생.

**수정:**
- `line.StartsWith("branch refs/heads/")` 검사 후 슬라이스.
- 그렇지 않고 `line.StartsWith("branch ")`만 만족하면 `line.Substring("branch ".Length)`
  로 fallback. `ralph/`로 시작하는지 한 번 더 확인 후 추가.

**verification:** `dotnet test --filter "FullyQualifiedName~Worktree"` 또는
없으면 `dotnet build Ralph/Ralph.csproj -nologo`.

**modifiedFiles:** `Ralph/Services/WorktreeService.cs`

---

## Feature 6 — `GuardTasksFileAsync`의 porcelain status 분류 오류

**파일:** `Ralph/Services/ParallelExecutor.cs:608-640`

`AM`(staged add + worktree modify), `AD`(staged add + worktree delete) 같은 코드는
`changeCode.Contains('M')`/`Contains('D')` 검사에서 첫 번째 분기로 빠지고
`git checkout HEAD -- tasks.json`을 실행 → 파일이 HEAD에 없어 실패.
porcelain은 X(index), Y(worktree) 2-character 코드이므로 컬럼 단위로 분류해야 함.

**수정:**
- 첫 두 글자를 인덱스/워킹 상태로 분리:
  - `var x = changeCode.Length > 0 ? changeCode[0] : ' ';`
  - `var y = changeCode.Length > 1 ? changeCode[1] : ' ';`
- 추가/언트랙(`?`, `A`)은 working tree 파일을 삭제.
- 그 외 수정/삭제/이름변경은 `git checkout HEAD -- tasksFile` 실행.
- 두 케이스 모두 처리되도록 `if (x == 'A' || x == '?')` → 삭제,
  `else if (x == 'M' || x == 'D' || x == 'R' || y == 'M' || y == 'D')` → checkout HEAD.

**verification:** `dotnet build Ralph/Ralph.csproj -nologo` (행위 변경이므로
가능하면 `dotnet test --filter "GuardTasksFile"` 시도).

**modifiedFiles:** `Ralph/Services/ParallelExecutor.cs`

---

## Feature 7 — Dry-run 실패 시 tasks.json 미복원

**파일:** `Ralph/Program.cs:386-407` (HandleDryRun)

`backupJson = await File.ReadAllTextAsync(...)` → `RunAutoLoop(...)` →
`File.WriteAllTextAsync(..., backupJson, ...)` 순서로, 중간에 예외(특히 OCE)가
발생하면 복원이 건너뛰어지고 dry-run 도중 마킹된 done 상태가 남음.

**수정:**
- `try { var result = await RunAutoLoop(...); } finally { await File.WriteAllTextAsync(tasksFile, backupJson, CancellationToken.None); }`
  형태로 감쌈. cancel 중에도 복원이 보장되도록 `CancellationToken.None` 사용.
- `result` 변수를 `try` 바깥에서 선언.

**verification:** `dotnet build Ralph/Ralph.csproj -nologo`

**modifiedFiles:** `Ralph/Program.cs`

---

## Feature 8 — `PlanGenerator`의 비원자적 파일 쓰기

**파일:** `Ralph/Services/PlanGenerator.cs:114-116`

```csharp
var formatted = JsonSerializer.Serialize(parsed, TaskManager.JsonOptions);
await File.WriteAllTextAsync(tasksFile, formatted, ct);
```

쓰기 도중 중단되면 tasks.json이 truncate. `TaskManager.SaveAsync`(`Ralph/Services/TaskManager.cs:53-73`)는
이미 tmp+rename 패턴 사용 중이므로 그 패턴을 차용해야 함.

**수정:**
- `tasksFile + ".tmp." + Guid.NewGuid():N`에 쓰고 `File.Move(tmp, tasksFile, overwrite: true)`.
- 예외 시 tmp 정리.
- 또는 `TaskManager.SaveAsync` 내부 헬퍼를 `internal static`으로 노출해 재사용.
  단순화 위해 별도 헬퍼 없이 인라인으로 구현해도 무방.

**verification:** `dotnet build Ralph/Ralph.csproj -nologo`

**modifiedFiles:** `Ralph/Services/PlanGenerator.cs`

---

## Feature 9 — `--max-parallel` 비숫자 입력이 silent fallback

**파일:** `Ralph/Program.cs:56-62`

```csharp
int.TryParse(argList[maxParallelIdx + 1], out maxParallelArg);  // 반환값 무시
argList.RemoveRange(maxParallelIdx, 2);
```

`--max-parallel banana`가 0으로 폴백하고 경고 없음. `--budget-usd`/`--task-timeout`은
파싱 실패 시 에러 + 종료하는데 본 옵션만 누락.

**수정:**
- 파싱 실패 또는 `<= 0`이면 `[red]Error: --max-parallel ...[/]` 출력하고 `return 1`.
  (파싱 시점에는 main 함수 안이므로 직접 return 가능.)
- 동일 패턴을 `--budget-usd` 분기와 일관되게.

**verification:** `dotnet build Ralph/Ralph.csproj -nologo`

**modifiedFiles:** `Ralph/Program.cs`

---

## Feature 10 — `WorktreeService.MergeWorktreeAsync` 인자 순서 정규화 (cosmetic, 선택)

**파일:** `Ralph/Services/WorktreeService.cs:99-144`

현재 `merge {branch} -X theirs --no-ff -m "..."` 순서로 들어가는데 git 표준 형태는
`merge -X theirs --no-ff -m "..." {branch}`. 현재도 동작하지만 가독성/이식성 개선.

**수정:** (선택사항. 우선순위 낮음 — 시간 남으면)
- `mergeArgs`를 base list `["merge", "--no-ff", "-m", $"merge: {taskId} ..."]`로 시작.
- strategy가 있으면 `-X strategy`를 `--no-ff` 앞에 insert.
- 마지막에 `branchName` 추가.

**verification:** `dotnet test --filter "FullyQualifiedName~Worktree"` (행위 동일성 보장).

**modifiedFiles:** `Ralph/Services/WorktreeService.cs`

> 주의: Feature 5와 동일 파일을 수정하므로 dependsOn으로 순차화하거나 한 task에
> 합쳐도 됨. ralph의 병렬 충돌 감지가 자동 직렬화하지만, 명시 의존이 더 안전.

---

## 작업 분할 / 의존성 가이드

- Feature 1, 2, 3, 4, 7, 8, 9는 **서로 다른 파일**만 수정하므로 완전 병렬 가능.
- Feature 5와 Feature 10은 같은 파일(`WorktreeService.cs`)이므로 한쪽이
  다른 쪽의 commit에 의존하도록 `dependsOn`으로 직렬화. (또는 한 task로 묶기)
- Feature 6은 단독 (`ParallelExecutor.cs`).

권장 task ID 네이밍:
- `bug-logrotator-impl`, `bug-logrotator-commit`
- `bug-gitservice-impl`, `bug-gitservice-commit`
- `bug-verifier-impl`, `bug-verifier-commit`
- `bug-logger-impl`, `bug-logger-commit`
- `bug-stale-worktree-impl`, `bug-stale-worktree-commit`
- `bug-guard-status-impl`, `bug-guard-status-commit`
- `bug-dryrun-restore-impl`, `bug-dryrun-restore-commit`
- `bug-plan-atomic-impl`, `bug-plan-atomic-commit`
- `bug-maxparallel-impl`, `bug-maxparallel-commit`
- (선택) `bug-merge-args-impl` — `bug-stale-worktree-commit`에 dependsOn

## workflow 권장 설정

```json
{
  "workflow": {
    "onTaskComplete": { "commitChanges": true },
    "parallel": {
      "enabled": true,
      "maxConcurrent": 5,
      "conflictStrategies": ["auto-theirs", "claude"]
    }
  }
}
```

각 commit task는 한국어 커밋 메시지로 "버그수정: <내용>" 형식 권장.

## 사용 절차

```bash
# 1. tasks.json 생성
ralph --plan bugfix.md

# 2. (선택) 검증 및 그래프 확인
ralph --validate
ralph --graph

# 3. dry-run으로 동작 확인
ralph --dry-run

# 4. 실행 (기본 병렬)
ralph --run
```

실행 중 충돌 발생 시 `conflictStrategies`의 `auto-theirs` → `claude` 순서로
자동 fallback. 모두 실패하면 `ralph --logs <task-id>`로 원인 확인 후 수동 해결.
