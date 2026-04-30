# Fix #1 — 머지 batch state.json 실패 시 중단 설계

## 1. 배경

`fix1.md` 1번 항목 요약: `Ralph/Services/MergeOrchestrator.cs:147-160`의
`MarkTaskDoneThreadSafeAsync` 실패가 catch 블록에서 로그만 남기고 swallow된다.
git merge는 base에 반영되었는데 `.ralph-logs/state.json`에는 done이 기록되지 않은
상태로 batch가 계속 진행되며, 다음 `--run`에서 **이미 머지된 task가 다시 dispatch되어
worktree 충돌 / 중복 작업**이 발생한다.

현재 흐름 (요약):

```
1) for taskId in batch:                      ─ 머지 phase (147행 이전)
       NormalizeTasksJsonAsync
       ValidateModifiedFilesAsync
       AdvanceWorktreeOntoBaseAsync
       MergeWorktreeAsync (+ HandleMergeConflictAsync)
2) for taskId in batch:                      ─ done 마킹 phase (147-161)
       try   { MarkTaskDoneThreadSafeAsync }
       catch { log.Error(); /* 계속 진행 */ }   ← 결함
3) RunPostMergeSmokeTestAsync
```

머지 phase는 실패 시 즉시 `return 1`으로 batch를 끊는다. done 마킹 phase만 swallow하고
다음 task로 넘어간다. 따라서 본 설계는 **"done 마킹 phase의 첫 실패에서 즉시 중단,
이미 done 처리된 task와 미처리 task를 분리해 보고"** 하는 흐름으로 변경한다.

이미 머지된 변경분 자체는 base 브랜치에서 되돌리지 않는다 (상위 정책: 이미 머지된
task는 자동 rollback하지 않음). state.json만 일관 상태로 멈춘다.

---

## 2. `StateStore.MarkTaskDoneAsync` 재시도 로직

### 2.1 적용 범위

`MarkTaskDoneAsync`, `MarkSubtaskDoneAsync`의 `SaveInternalAsync` 호출 구간만 재시도한다.
실패는 거의 전부 `SaveInternalAsync`(tmp + rename)에서 발생하므로 락 획득은 정상 흐름으로
보고 락 안쪽 save 호출만 감싼다. `ResetAllAsync` / `SaveAsync`는 사용 시점이 다르므로
이번 변경 범위에서 제외한다 (필요 시 별도 후속 작업).

### 2.2 재시도 정책

| 항목 | 값 |
|---|---|
| 최대 재시도 횟수 | 2 (총 시도 3회: 1회 본 + 2회 재시도) |
| 간격 | 100 ms 고정 |
| 재시도 대상 예외 | `IOException`, `UnauthorizedAccessException` |
| 재시도 안 함 | `OperationCanceledException`, `JsonException`, 그 외 모든 예외 |

근거:
- `IOException` — Windows의 일시적 file lock(MoveFile during AV scan, file handle 잠시 점유)에
  실효성 있음. POSIX에서도 EBUSY 등.
- `UnauthorizedAccessException` — Windows에서 임시 ACL 충돌로 `File.Move`가 실패할 수 있음.
- `JsonException` — 직렬화 자체가 실패했다면 재시도해도 실패. 즉시 throw.
- 짧은 backoff (100 ms × 2) — 사용자 체감 지연을 거의 더하지 않으면서 transient만 흡수.

### 2.3 시그니처와 throw 정책

`MarkTaskDoneAsync` / `MarkSubtaskDoneAsync` 모두 기존 시그니처 (`Task` 반환) 유지.
재시도가 모두 실패하면 **마지막 예외를 그대로 rethrow**한다 (래핑하지 않음). 호출자가
`IOException`인지 `UnauthorizedAccessException`인지 구분해 사용자 메시지에
원인을 노출할 수 있도록 하기 위함이다. 별도 `StateStoreWriteException` 신설은
하지 않는다 (래퍼는 진단 정보를 흐릴 뿐 호출자에 추가 가치가 없음).

### 2.4 의사코드

```csharp
public async Task MarkDoneAsync(string taskId, CancellationToken ct = default)
{
    await _lock.WaitAsync(ct);
    try
    {
        EnsureTaskState(taskId).Done = true;
        await SaveWithRetryAsync(ct);
    }
    finally { _lock.Release(); }
}

private async Task SaveWithRetryAsync(CancellationToken ct)
{
    const int maxRetries = 2;
    const int delayMs = 100;
    for (var attempt = 0; ; attempt++)
    {
        try { await SaveInternalAsync(ct); return; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
            when ((ex is IOException || ex is UnauthorizedAccessException)
                  && attempt < maxRetries)
        {
            // 진단을 위해 재시도 회수와 예외 타입을 로깅하고 싶으나, StateStore는
            // 현재 RalphLogger 의존성이 없으므로 호출자가 catch 시 함께 보고한다.
            await Task.Delay(delayMs, ct);
        }
    }
}
```

`MarkSubtaskDoneAsync`도 동일한 헬퍼를 호출한다.

---

## 3. `MergeOrchestrator` — 즉시 중단 + 분리 보고

### 3.1 호출자에 신호를 어떻게 전달할지

선택: **반환 값(int 종료 코드 1) + 콘솔/로그에 분리 리스트 출력**.
예외 throw로 처리할 수도 있으나:

- `MergeAndFinalizeAsync`는 이미 `Task<int>` (0/1) 인터페이스를 가지며, 머지 phase의
  실패도 모두 `return 1`로 표현한다. 동일 패턴을 따르면 호출자(`ParallelExecutor`)
  쪽 분기가 깔끔하다.
- 진행 정보(이미 done 처리된 task / 미처리 task)는 콘솔과 logger에 직접 쓰면
  충분하며, 호출자가 추가 가공할 필요가 없다.

따라서 새로운 예외 타입이나 결과 DTO를 도입하지 않는다.

### 3.2 진행 정보 보고용 임시 데이터 구조

`MergeAndFinalizeAsync` 내부에서만 사용할 두 개의 로컬 리스트:

```csharp
var marked = new List<string>();   // 이번 batch에서 done=true로 기록 완료된 taskId
var pending = new List<string>();  // 머지는 끝났지만 아직 done 마킹이 안 된 taskId
```

- 머지 phase 종료 시점에 batch의 모든 taskId가 base에 머지된 상태로 들어온다.
  `pending`을 `taskIds.ToList()`로 초기화한 뒤 done 루프에서 성공 항목을
  `marked`로 옮기는 식으로 갱신한다.
- 실패가 발생하면 실패한 taskId는 `pending`의 head로 남고, 그 뒤 taskId들도
  여전히 `pending`에 남아있다 (do-not-continue 정책).

새 클래스/struct는 만들지 않는다. 이 정보는 batch 단일 함수 안에서만 의미가 있다.

### 3.3 변경된 catch 블록 흐름

```csharp
var marked = new List<string>();
var pending = new List<string>(taskIds);

foreach (var taskId in taskIds)
{
    try
    {
        await MarkTaskDoneThreadSafeAsync(taskId, ct);
        marked.Add(taskId);
        pending.Remove(taskId);

        var task = _taskManager.GetTask(taskId)!;
        AnsiConsole.MarkupLine($"[green]태스크 완료: {Markup.Escape(task.Title)}[/]");
        _logger.TaskEnd(taskId, "completed");
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        ReportStateWriteFailure(taskId, ex, marked, pending);
        return 1;
    }
}
```

`ReportStateWriteFailure`는 §4의 메시지를 출력하고 logger에 기록한다.
return 1 후에는 **smoke test를 실행하지 않는다** — state가 깨진 상태에서 추가
신호를 섞지 않는다.

### 3.4 worktree cleanup 처리

merge phase의 충돌 실패 분기(133-138행)는 자체적으로 남은 taskId의 worktree를
cleanup한 뒤 `reportCleanupFailures` 콜백을 호출하고 return 1한다. done 마킹 단계는
그 시점에 이미 모든 worktree에 대한 머지 자체는 완료된 상태이므로, 정리는
호출자(`ParallelExecutor.RunParallelBatchAsync`)의 `finally` 블록이
모든 worktree에 대해 일괄 처리한다 — 추가 로직 불필요.

---

## 4. 사용자 메시지 사양

콘솔 출력 (Spectre 마크업, 한국어):

```
✗ <taskId> done 마킹 실패: <예외 메시지>
  원인: <예외 타입 풀 네임>
  state.json 쓰기에 실패하여 batch를 중단합니다. 이미 머지된 변경분은 base 브랜치에 남아 있습니다.

  완료 처리된 task (state.json 반영 완료):
    - <id1>
    - <id2>
  미처리 task (머지는 됐으나 done 마킹 안 됨):
    - <id3>  ← 실패 지점
    - <id4>

  복구 안내:
    1) `<state.json 경로>` 의 디스크 / 권한 / 잠금을 확인하세요.
    2) 미처리 task가 다음 실행에서 재실행되지 않도록, base 브랜치에서 해당
       변경이 이미 적용되었는지 직접 확인하세요. 적용되었다면 다음 명령으로 done을 표시할 수 있습니다:
         (수동) state.json의 tasks["<id>"].done = true 로 편집
       또는 task가 사라져도 무방하다면 `ralph --reset` 후 tasks.json 단위로 재계획하세요.
    3) 수동 정리 후 `ralph --run` 으로 재개하세요.
```

핵심 요건:
- 첫 줄에 `state.json 쓰기 실패로 batch 중단; 수동 복구 필요` 라는 의도가
  명확히 드러나야 함 (요구사항 §1).
- "이미 done 처리된 task ID 목록"과 "미처리 task ID 목록"을 분리해 표시 (요구사항 §1).
- 미처리 목록의 첫 항목(실패 지점)은 시각적으로 구분 (`← 실패 지점`).
- 빈 목록은 `(없음)`으로 표기.
- 경로 표시는 `_taskManager`의 StateStore를 통해 얻은 `state.json` 절대 경로를
  사용 — 사용자가 바로 점검할 수 있도록.

logger (`RalphLogger`) 출력:
- `Error`: `[merge:done-mark] {taskId} state save failed after retries: {ExceptionType}: {Message}`
- `Error`: `[merge:done-mark] marked={[ids]} pending={[ids]}` (구조화 검색용)

`AnsiConsole` 마크업은 `Markup.Escape`로 모든 동적 토큰(예외 메시지, taskId)을 감싸야 한다.

---

## 5. `ParallelExecutor` 측 후처리

`MergeAndFinalizeAsync`가 `1`을 반환하면 `RunParallelBatchAsync`의 기존 코드 흐름이
그대로 작동한다 (`Ralph/Services/ParallelExecutor.cs:357-361`):

```csharp
var mergeExit = await _mergeOrchestrator.MergeAndFinalizeAsync(...);
if (mergeExit != 0) return mergeExit;
```

이후 `RunAsync` 루프(`ParallelExecutor.cs:174-176`)도 `result != 0`이면 즉시 break/return.
즉, **추가 코드 변경 없이도** "다음 배치 진입 차단 + 종료 코드 비-0" 요구사항이 충족된다.

검증 포인트:
- `RunParallelBatchAsync`의 `finally` 블록은 모든 worktree에 대해 cleanup을 수행한다.
  done 마킹 실패 시점에 batch의 모든 worktree는 머지가 끝나 base에 반영된 상태이므로
  cleanup이 실패할 가능성은 낮지만, 실패해도 `_cleanupFailures`가 누적되어 사용자에게
  표시된다 — 기존 로직 그대로.
- `RunAsync`가 `mergeExit (=1)`을 그대로 반환하므로 프로세스 종료 코드는 1.

추가 변경 사항: 없음. (가독성 차원에서 `MergeAndFinalizeAsync`가 `1`을 반환할 때의
사유 주석을 한 줄 추가한다.)

---

## 6. 테스트 전략

### 6.1 신규 파일: `Ralph.Tests/MergeOrchestratorFailureTests.cs`

기존 `Ralph.Tests/ParallelExecutorTests.cs`의 fixture 패턴을 그대로 따른다:
- `[Collection("cost")]`로 CostTracker 정적 캐시 직렬화.
- 임시 git repo + worktree base를 `_root` 아래에 만들고 `Directory.SetCurrentDirectory`
  로 격리, Dispose에서 복원.
- `WorktreeAwareRunner`(또는 동일 시그니처의 mock)로 `IAgentRunner`를 주입.
- 두 개의 독립 task A / B로 batch를 구성 (smoke test는 `noSmokeTest: true`로 비활성).

### 6.2 IOException 강제 주입 지점

`StateStore`는 `OpenAsync` 정적 팩토리만 노출하고 인터페이스가 없다. 테스트에서
실패를 강제 주입하려면 다음 중 하나의 접근을 쓴다:

**선택 A — 디스크 락 강제 (의도된 환경 시뮬레이션, 외부 의존성 0)**

`state.json` 파일을 테스트가 미리 만들고 `FileStream`을 `FileShare.None`으로
열어둔 채로 `RunAsync`를 호출. POSIX에서는 `FileShare.None`이 `File.Move`를
직접 막지 않으므로 이 방식은 Windows 전용이 된다.

**선택 B — `state.json`의 부모 디렉토리를 read-only로 만든다 (POSIX/Windows 양쪽에서 동작)**

`.ralph-logs` 디렉토리를 미리 만들고 `chmod 0500` (POSIX) / `Directory ACL Deny Write`
(Windows)을 적용. `File.Move`가 `UnauthorizedAccessException` 또는 `IOException`을
던진다. Dispose에서 권한 원복.

**선택 C — `StateStore`에 인터페이스 추출 + DI (권장)**

`MergeOrchestrator`가 직접 `StateStore`를 호출하지 않고 `TaskManager`를 통해 접근하므로,
가장 깔끔한 방법은 `TaskManager`에 `MarkTaskDoneAsync` 호출 시 사용할
`IStateWriter`(가칭) 시드 포인트를 두는 것이다. 다만 이는 본 fix의 범위를 넘어서며,
다른 task의 변경과 충돌할 가능성이 있다.

→ **선택 B를 1순위, A를 2순위**로 채택한다. 본 plan에서는 코드 시그니처를 바꾸지 않고
   real `StateStore` + 디스크 권한 조작으로 IOException을 일으킨다.
   POSIX 친화적이며 CI(ubuntu+windows matrix)에서 동작한다.

플랫폼별 구현 노트:
- POSIX: `File.SetUnixFileMode(stateLogDir, UnixFileMode.UserRead | UnixFileMode.UserExecute)`
  (.NET 8 API). Dispose에서 원복.
- Windows: `DirectoryInfo.GetAccessControl()` + `Deny Write` ACE 추가. .NET 8에서는
  `System.IO.AccessControl` NuGet 패키지가 필요. **테스트 의존성 추가를 피하기 위해
  Windows에서는 `[SkippableFact]` 또는 `Skip = OperatingSystem.IsWindows()`로 스킵**하고
  POSIX 케이스만 회귀 방지로 둔다. 향후 Windows 테스트는 별도로 보강.

> 단, root로 실행되는 컨테이너 CI에서는 0500 권한이 우회될 수 있다. 회피책으로
> `.ralph-logs/state.json`을 미리 디렉토리(파일이 아닌)로 만들어 `File.Move`가
> `UnauthorizedAccessException`을 던지게 하는 보완 트릭을 사용 가능. 두 방법을
> 헬퍼로 캡슐화해 환경에 맞게 fallback한다.

### 6.3 시나리오 (각각 별도 `[Fact]`)

#### 6.3.1 `Done_marking_failure_aborts_batch_and_leaves_first_task_pending`

설정:
- 두 독립 task A / B (`a.txt`, `b.txt`).
- `WorktreeAwareRunner`는 둘 다 성공 + 파일 생성.
- `state.json` 부모 디렉토리에 쓰기 권한 차단 (선택 B).

검증할 invariants:
1. `executor.RunAsync` 종료 코드 == `1`.
2. `manager.IsDone("A") == false` (요구사항: 첫 번째 task `done==false` 유지).
3. `manager.IsDone("B") == false` (두 번째 task의 done 마킹은 시도조차 안 됨).
4. base 브랜치에 `a.txt`는 존재 (머지는 이미 완료된 상태에서 done 마킹만 실패했음을 확인).
5. `b.txt`도 존재할 수 있다 — done 마킹 phase는 머지 phase 이후라 두 task 모두 머지된 후에
   첫 번째 done 마킹에서 실패한다. 따라서 `b.txt` 존재는 무관하지만, 다음 invariant가 핵심:
6. **두 번째 task의 머지가 호출되지 않았는지** 가 아니라 (머지 phase는 정상 완료),
   **두 번째 task의 done 마킹이 호출되지 않았는지** 를 검증해야 한다.

> 요구사항 §5에 적힌 "두 번째 task의 머지가 호출되지 않음"은 done 마킹 실패가
> **머지 phase 자체** 안에서 발생할 때 의미가 있다. 현재 코드 구조상 done 마킹은
> 머지 루프와 분리된 별도 phase이므로 본 invariant는 다음과 같이 해석한다:
>
> - **(a)** done 마킹 phase에서 첫 실패 후 두 번째 task의 done 마킹 시도가 없어야 한다.
> - **(b)** smoke test가 실행되지 않아야 한다 (state 손상 시 추가 신호 노이즈 방지).
>
> 향후 done 마킹을 머지 루프 안으로 인터리브하는 리팩터를 한다면 그때 invariant를
> "두 번째 task 머지 미호출"로 강화한다.

#### 6.3.2 `State_save_retries_transient_io_then_succeeds`

`StateStore` 단위 테스트. RalphPaths 기반 임시 경로에서:
- `state.json` 부모 디렉토리에 잠시 잠금을 건 뒤 (\~50 ms) 해제하는 background task 띄움.
- `MarkDoneAsync` 호출이 정상 완료 (재시도가 transient를 흡수함을 검증).
- 호출 직후 파일이 존재하고 `IsDone(taskId) == true`.

#### 6.3.3 `State_save_retries_exhausted_throws_original_exception_type`

`StateStore` 단위 테스트:
- 부모 디렉토리 권한을 영구 차단.
- `await Assert.ThrowsAsync<IOException>(...)` 또는 `UnauthorizedAccessException`
  (OS에 따라 매칭) — 어느 쪽이든 transient 분류 예외임을 확인.
- 시도 횟수가 정확히 3회임을 검증하기 위해 `Stopwatch`로 200 ms 이상 소요됐는지
  ( 100 ms × 2 회 ) 확인. 정확한 카운터를 노출하려면 `StateStore`에
  `internal int RetryCountForTesting` 같은 hook이 필요하나, 본 변경에서는
  최소 침습으로 `Stopwatch.Elapsed >= 180ms` 검증으로 갈음한다 (CI flakiness
  회피를 위해 임계값에 마진).

#### 6.3.4 `Done_marking_failure_message_contains_marked_and_pending_lists`

설정:
- 세 task A / B / C (모두 독립). A의 done 마킹은 정상, B에서 실패, C는 시도 안 됨.
- 재현을 위해서는 권한 토글 타이밍이 까다로우므로, 본 케이스는 `MergeOrchestrator`에
  `IStateWriter` 형태의 시드 포인트를 두는 별도 리팩터가 선행되어야 깔끔히 가능.
- 본 fix 범위에서는 **단순화**: A 한 개만 있을 때 실패하는 케이스와 A/B 모두 실패하는
  케이스로 나누어 콘솔 출력에 `marked=[]`, `pending=[A]` 같은 logger 라인이
  남는지를 검증한다 (logger 파일을 읽어 grep).

### 6.4 로깅 / 콘솔 검증 방법

`AnsiConsole` 출력은 테스트에서 캡처하기 번거롭다. 회귀 방지의 주요 신호는:

- `RalphLogger`가 쓰는 `.ralph-logs/ralph-*.log` 파일에 §4의 logger 메시지가
  포함되어 있는지 (`File.ReadAllText` + `Assert.Contains`).
- 종료 코드.
- `state.json`의 최종 상태 (`StateStore.OpenAsync`로 읽어 IsDone 확인).

콘솔 출력 자체는 사람이 보기용 — 단위 회귀에서는 logger 라인을 진실의 기준으로 삼는다.

---

## 7. 구현 단계 (impl 단계 위임용)

1. `StateStore.SaveWithRetryAsync` 헬퍼 추가, `MarkDoneAsync` / `MarkSubtaskDoneAsync`가
   호출하도록 변경. 다른 진입점(`ResetAllAsync`, `SaveAsync`)은 그대로 둔다.
2. `MergeOrchestrator.MergeAndFinalizeAsync`의 done 마킹 루프(`Ralph/Services/MergeOrchestrator.cs:144-161`)를
   §3.3의 흐름으로 교체. `ReportStateWriteFailure(taskId, exception, marked, pending)`
   private 헬퍼 신설. logger / Spectre 출력은 §4 사양을 그대로 사용.
3. `MergeAndFinalizeAsync`가 done 마킹 실패로 `1`을 반환할 때 `RunPostMergeSmokeTestAsync`를
   호출하지 않도록 흐름 보장 (early return으로 자연스럽게 처리됨).
4. `ParallelExecutor` 측은 변경 없음 — `RunParallelBatchAsync`가 이미 `mergeExit != 0`을
   바로 return하고 `RunAsync` 루프가 동일하게 break함을 확인.
5. `Ralph.Tests/MergeOrchestratorFailureTests.cs` 신규 파일에 §6.3.1, §6.3.4 시나리오.
   `Ralph.Tests/StateStoreRetryTests.cs` 신규 파일에 §6.3.2, §6.3.3 단위 테스트.

## 8. 비목표 / 후속 작업

- **이미 머지된 task의 자동 rollback** — 본 fix에서 다루지 않음. 상위 정책상
  `--strict-files`와 `workflow.smokeTest`로 머지 전 차단을 권장하며, 머지 후
  state 일관성 손상에 대해서는 사용자 수동 복구를 안내한다 (§4).
- **`MergeOrchestrator` ↔ `StateStore` DI 인터페이스화** — 테스트 mock 주입을 더
  깔끔히 하려면 필요하지만, 본 fix는 디스크 권한 트릭으로 회귀 방지를 확보하고
  인터페이스 도입은 별도 PR로 분리한다 (변경 범위 최소화).
- **Windows에서 권한 기반 IOException 강제 주입** — POSIX 케이스만 우선 도입하고
  Windows 회귀는 후속 보강.
