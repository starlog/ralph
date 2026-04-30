# Fix2 #8 — 머지 트랜잭션 로그 설계

## 1. 배경

`MergeOrchestrator.MergeAndFinalizeAsync`는 batch마다 다음을 수행한다.

1. `git worktree`의 task 브랜치를 base에 머지 → 새 머지 커밋 SHA 생성.
2. `StateStore.MarkTaskDoneAsync`로 `.ralph-logs/state.json`에 done 비트 기록.
3. batch 끝에 post-merge smoke test 실행.
4. (fix2 #7) smoke 실패 + opt-in 시 자동 revert.

이 흐름의 결과는 git 커밋 그래프와 `state.json`에 분산된다. 사후에
"`feature-x-impl` 가 어떤 SHA로 머지됐고 그때 smoke가 통과했는가?"를
재구성하려면 `git log --merges` 메시지 파싱 + state.json의 done 비트 +
fragile한 시간 정렬에 의존해야 한다.

`fix1 #1`(done-mark 실패 시 batch abort)로 "머지됐지만 done이 false" 상태가
이론상 사라졌지만, 정작 그 매핑은 디스크에 남지 않는다. `--rollback`은
**전체 스냅샷**(pre-plan/post-plan)만 다루며 batch 단위 정밀 복구를 못 한다.
`--status`는 tasks.json의 spec과 state.json의 done 비트만 보여준다 — 어느
SHA가 어느 task에 대응되는지는 표시하지 않는다.

`fix2.md` #8 요구 요약:

- `.ralph-logs/merge-log.jsonl`에 batch별 entry append. 스키마:
  `{ ts, batch, taskId, baseSha, mergedSha, stateMarked, smokeTest }`.
- 한 task당 idempotent하게 1회만 entry (재실행 시 중복 없음).
- `--status`가 이 로그를 읽어 더 정확히 표시.
- `--rollback`이 이 로그로 정밀 복구.

본 fix는 위 요구를 충족하면서 다음 기존 패턴을 그대로 따른다.

- `CostTracker`의 JSONL append + `SemaphoreSlim(1,1)` 잠금 + `RalphJsonContext`
  source-gen 직렬화 (`Ralph/Services/CostTracker.cs:68-73, 92-99, 247-306`).
- `RalphPaths`의 ledger 상수 패턴 (cost/validation/cost-failures와 동일 자리).
- `RalphJsonContext`의 `[JsonSerializable]` 엔트리 추가.

Scope:

- 신설: `Ralph/Services/MergeLogService.cs`, `Ralph/Models/MergeLogEntry.cs`.
- 수정: `Ralph/Services/RalphPaths.cs`, `Ralph/Services/MergeOrchestrator.cs`,
  `Ralph/Services/RollbackService.cs`, `Ralph/Commands/StatusCommand.cs`,
  `Ralph/Commands/RollbackCommand.cs`, `Ralph/Models/RalphJsonContext.cs`,
  `Ralph/Commands/RunCommand.cs` (DI 한 줄), `ralph-schema.json` (변경 없음 —
  로그는 spec 외부).
- 테스트: `Ralph.Tests/MergeLogTests.cs` (신규).

선행 의존:

- fix2 #2 — `RalphPaths`에 `CostFailuresLedgerFileName`/관련 헬퍼 상수가
  추가되어 있다 (mirror 가능한 자리).
- fix2 #7 — `MergeOrchestrator`에 in-memory `BatchRollbackSnapshot`,
  `_autoRollbackOnSmokeFail`, `TryAutoRollbackAsync`, smoke 결과 풍부화
  (`SmokePhaseResult`)가 들어 있다. **이 위에서** 동작하도록 설계 — #7과
  같은 배치 컨텍스트(머지된 task 목록, smoke 결과, batch 인덱스)에서 로그를
  쓴다.

---

## 2. 현재 흐름 분석

### 2.1 `MergeOrchestrator.MergeAndFinalizeAsync` (fix2 #7 이후 상태)

핵심 구간 (참고: 라인은 fix2 #7 적용본 기준 근사):

```
preMergeSha       = CaptureBaseShaAsync(baseBranch)            // batch 시작 시 base HEAD
batchSnapshot     = RollbackService.CaptureBatchSnapshot(      // in-memory 스냅샷
                       baseBranch, preMergeSha, taskIds)
foreach taskId in taskIds:
    AdvanceWorktreeOntoBaseAsync (rebase)
    MergeWorktreeAsync                          // 머지 커밋 1개 생성 → mergedSha
    (rebase 실패 시 rebaseFailedTasks에 추가, 다음 task로)
foreach mergedTaskId in mergedTasks:
    MarkTaskDoneThreadSafeAsync                 // state.json에 done=true (성공 시)
                                                // 실패 시 stateMarked=false 분기
smoke = RunPostMergeSmokeTestAsync(preMergeSha) // SmokePhaseResult
if smoke 실패 && _autoRollbackOnSmokeFail:
    TryAutoRollbackAsync(batchSnapshot, mergedTasks, smoke)  // git revert + state pending
return ...
```

핵심 관찰:

1. 한 task의 `mergedSha`는 `MergeWorktreeAsync` 직후 손에 들어온다.
2. `stateMarked` 결과는 `MarkTaskDoneThreadSafeAsync`의 try/catch에서 결정된다.
3. `smokeTest` 결과는 batch 끝에서야 결정 — task 단위가 아닌 **batch 단위**.
   하지만 PRD 스키마는 task 단위 entry에 smoke 결과를 적도록 요구한다.
   동일 batch에 머지된 모든 task entry는 같은 smoke 결과를 공유한다.
4. `rebaseFailedTasks` 는 이번 batch에서 **머지되지 않은** task. 머지 로그
   대상이 아니다 (이미 base에 반영 안 됨). § 3.5에서 별도 정책.
5. 자동 롤백이 발동하면 base에 revert 커밋이 추가되지만, **이미 append된
   merge-log entry는 수정하지 않는다** (JSONL은 append-only). 대신 별도
   `event="rollback"` entry를 추가로 append. § 3.6.

### 2.2 `RalphPaths.cs` 현재 상태 (fix2 #2 적용본 기준)

```
LogDir                       = ".ralph-logs"
StateFileName                = "state.json"
CostLedgerFileName           = "cost.jsonl"
CostFailuresLedgerFileName   = "cost-failures.jsonl"   ← fix2 #2가 추가
ValidationLedgerFileName     = "validation.jsonl"
RollbackDirName              = "rollback"
UntrackedBackupDirName       = "untracked-backup"
```

→ 동일 자리에 `MergeLogFileName = "merge-log.jsonl"`(PRD 표기 그대로) 와
헬퍼 상수/메서드를 추가한다 (§ 4.1).

### 2.3 `StatusCommand.cs` 현재 출력

`StatusCommand.RunAsync` (`Ralph/Commands/StatusCommand.cs`):

- `DisplayHelpers.ShowProgress(tm, RalphLogger.Null)` — tasks.json + state.json
  기반 done/pending 카운트와 카테고리별 진행률.
- ready 태스크 / parallel batch 그룹 표시.
- `.ralph-worktrees/` 디렉터리 스캔 → 살아있는/유휴 워크트리 분류 (로그
  mtime 30초 임계).

merge-log 가 추가하면:

- 가장 최근 batch index (= 마지막 entry의 `batch` 값).
- 마지막 머지 커밋 SHA (task별로 가장 최근 entry).
- 해당 batch의 smoke 결과.
- 자동 롤백 entry가 있으면 "롤백된 batch"로 표시.

### 2.4 `RollbackService.cs` 현재 인터페이스

- pre-plan / post-plan 디스크 스냅샷: `--plan` 시 캡처, `--rollback`이 복원.
- (fix2 #7) `CaptureBatchSnapshot(...)` — in-memory 휘발성, smoke 자동 revert
  전용. **디스크 I/O 없음**.
- `RestoreAsync(...)` — 전체 스냅샷 단위 복원 (`git reset --hard` + tasks.json
  덮어쓰기 + PRD 재생성).

merge-log 의 `--rollback` 활용 (§ 3.7) 은 **전체 복원의 보조 수단**으로
설계한다. 핵심 동작 (snapshot 기반 reset)은 그대로 두고, "어느 batch까지
복원되는가"의 명확성을 높이는 부가 정보로 활용한다.

### 2.5 `RalphJsonContext.cs` 현재 등록 타입

```
[JsonSerializable(typeof(TasksFile))]
[JsonSerializable(typeof(StateFile))]
[JsonSerializable(typeof(ParallelSettings))]
[JsonSerializable(typeof(VerificationSpec))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(CostEntry))]
[JsonSerializable(typeof(PricingFile))]
[JsonSerializable(typeof(PricingEntry))]
[JsonSerializable(typeof(ValidationLogEntry))]
[JsonSerializable(typeof(RollbackSnapshot))]
```

`MergeLogEntry` 한 타입을 추가한다 (§ 4.2). 현재 `CostFailureEntry`도 등록되어
있을 가능성이 높다 — 본 PR에서는 새 타입 1개만 추가.

---

## 3. 제안 설계

### 3.1 파일 위치 / 형식

- 경로: `.ralph-logs/merge-log.jsonl` (PRD 표기 그대로).
- 인코딩: UTF-8, LF newline (CostTracker와 동일).
- 형식: JSONL (한 줄 한 entry, `\n` 종결). `WriteIndented = false`,
  `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`.
- 보존 정책: `LogRotator`의 preserved 목록(`cost.jsonl`, `validation.jsonl`)에
  **추가**. `merge-log.jsonl`은 history 가치가 높고 양이 작아 보존 대상.

### 3.2 entry 스키마 (`MergeLogEntry`)

```csharp
// Ralph/Models/MergeLogEntry.cs
public sealed class MergeLogEntry
{
    /// <summary>UTC ISO-8601 timestamp (millisecond 정밀).</summary>
    [JsonPropertyName("ts")]
    public string Ts { get; set; } = "";

    /// <summary>
    /// 이번 --run 세션에서의 batch 인덱스 (1-based).
    /// 한 ralph 세션 안에서만 단조 증가 — 세션 간 비교 의미 없음.
    /// </summary>
    [JsonPropertyName("batch")]
    public int Batch { get; set; }

    /// <summary>tasks.json의 task ID.</summary>
    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = "";

    /// <summary>batch 시작 시점 base HEAD (preMergeSha). full 40자.</summary>
    [JsonPropertyName("baseSha")]
    public string BaseSha { get; set; } = "";

    /// <summary>해당 task의 머지 커밋 SHA. full 40자.</summary>
    [JsonPropertyName("mergedSha")]
    public string MergedSha { get; set; } = "";

    /// <summary>
    /// state.json의 done=true 마킹이 성공했는지.
    /// MarkTaskDoneThreadSafeAsync가 IO 실패로 false면 false.
    /// </summary>
    [JsonPropertyName("stateMarked")]
    public bool StateMarked { get; set; }

    /// <summary>"passed" | "failed" | "skipped".</summary>
    [JsonPropertyName("smokeTest")]
    public string SmokeTest { get; set; } = "";

    /// <summary>
    /// "merge" (기본) | "rollback" (fix2 #7 자동 revert로 인한 보정 entry).
    /// 미존재(=null) 또는 빈 문자열은 "merge"로 해석 — 호환.
    /// </summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>
    /// event="rollback"일 때만 채움. 사용자 진단용.
    /// merge entry에는 null (스키마 단순성을 위해 직렬화 제외).
    /// </summary>
    [JsonPropertyName("rollbackRevertSha")]
    public string? RollbackRevertSha { get; set; }
}
```

직렬화 옵션:

- `JsonIgnoreCondition.WhenWritingNull`로 `event`/`rollbackRevertSha` 가
  null인 경우 출력 생략. PRD 스키마(`{ ts, batch, taskId, baseSha, mergedSha,
  stateMarked, smokeTest }`)와 정확히 일치하는 결과.

### 3.3 idempotency 키 + 보장 방식

키 후보 비교:

| 키 | 보장 강도 | 단점 |
|---|---|---|
| `taskId` 단독 | 약함 — 자동 롤백 후 재머지 시 동일 taskId가 재등장. | PRD 의도와 어긋남 (정상적 재머지를 막음). |
| `taskId + mergedSha` | 강함 — git이 동일 SHA를 두 번 만들 수 없음 (트리/메타데이터 동일이면 same SHA지만 그 경우 진짜 중복). | dedup을 위해 기존 로그를 한 번 스캔해야 함. |
| `batch + taskId` | 세션 내에서만 의미. 세션 재시작 시 batch 인덱스가 1로 리셋. | 세션 간 dedup 불가. |

채택: **`(taskId, mergedSha)` 페어**.

이유:

- "동일 task가 동일 SHA로 두 번 기록되는 것"이 진짜 중복. 이를 정확히
  잡는다.
- 자동 롤백 후 재실행 → 새 worktree → 새 mergedSha → 별도 entry로 정상
  기록.
- `event="rollback"` entry는 별도 dedup 키 — `(taskId, rollbackRevertSha)`
  로 동일 정책 적용.

구현 (개념):

```csharp
// MergeLogService.cs
private readonly HashSet<(string taskId, string mergedSha)> _seenMerges = new();
private bool _loaded = false;
private readonly SemaphoreSlim _writeLock = new(1, 1);

public async Task AppendMergeAsync(MergeLogEntry entry, CancellationToken ct)
{
    if (entry.Event != null && entry.Event != "merge")
        throw new ArgumentException(...);  // 이 메서드는 merge 전용

    await _writeLock.WaitAsync(ct);
    try
    {
        await EnsureLoadedAsync(ct);
        var key = (entry.TaskId, entry.MergedSha);
        if (!_seenMerges.Add(key)) return;   // 중복 — silent skip
        await AppendLineAsync(entry, ct);
    }
    finally { _writeLock.Release(); }
}
```

`EnsureLoadedAsync`: 첫 append 직전에 기존 `merge-log.jsonl`을 한 번 읽어
`_seenMerges` / `_seenRollbacks`를 채움. 파일이 없으면 빈 set. 1세션 1회만
스캔 — 동일 프로세스 안에서 메모리로만 dedup.

다중 프로세스 동시 실행 처리:

- ralph는 단일 프로세스 가정 (워크트리 격리는 인-프로세스 task 병렬). 동시
  여러 `ralph --run`을 의도하지 않는다 (fix2 범위 외). 단, 안전망으로:
- 파일 잠금은 두지 않되, 한 줄 단위 atomic append (`File.AppendAllTextAsync`)
  를 사용하므로 줄 깨짐은 없음. 동시 두 프로세스가 같은 (taskId, mergedSha)
  를 쓰면 entry 2개가 들어가지만 — 사실상 발생하지 않음 + reader는 마지막
  entry를 신뢰하면 됨 (§ 3.7).

### 3.4 append 시점 (state 마킹 직후)

`MergeOrchestrator.MergeAndFinalizeAsync` 내부에 다음 호출 지점을 추가한다.
호출 위치는 시간 순서대로:

```
[A] foreach taskId in taskIds:
        AdvanceWorktreeOntoBaseAsync                 (rebase)
        mergedSha = MergeWorktreeAsync
        ① taskId, mergedSha를 모은다 (메모리)

[B] foreach (taskId, mergedSha) in mergedTasks:
        ok = MarkTaskDoneThreadSafeAsync             (state.json 쓰기)
        ② stateMarked=ok 로 (taskId, mergedSha)에 결합 (메모리)

[C] smoke = RunPostMergeSmokeTestAsync(preMergeSha)
        smokeStr = smoke.Skipped ? "skipped"
                  : smoke.Passed   ? "passed"
                  : "failed"

[D] foreach mergedTask in mergedTasks:                ← append 시점
        await _mergeLog.AppendMergeAsync(new MergeLogEntry {
            Ts          = UtcNowIso8601(),
            Batch       = _batchIndex,
            TaskId      = mergedTask.taskId,
            BaseSha     = preMergeSha,
            MergedSha   = mergedTask.mergedSha,
            StateMarked = mergedTask.stateMarked,
            SmokeTest   = smokeStr,
        }, ct);

[E] if smoke 실패 && _autoRollbackOnSmokeFail:
        var revertSha = await TryAutoRollbackAsync(...)
        if (revertSha != null):
            foreach mergedTask in mergedTasks:
                await _mergeLog.AppendRollbackAsync(new MergeLogEntry {
                    Ts                = UtcNowIso8601(),
                    Batch             = _batchIndex,
                    TaskId            = mergedTask.taskId,
                    BaseSha           = preMergeSha,
                    MergedSha         = mergedTask.mergedSha,    ← 어느 머지를 되돌렸는지
                    StateMarked       = false,                   ← #7이 pending으로 되돌림
                    SmokeTest         = "failed",
                    Event             = "rollback",
                    RollbackRevertSha = revertSha,
                }, ct);
```

순서 근거:

- [A] 시점에는 stateMarked / smokeTest 가 미정 → 아직 쓰지 않음.
- [B] 시점에는 stateMarked 결정. smokeTest 미정.
- [C] 시점 종료 후 smokeTest 결정. 이때 모든 정보가 모였으므로 [D]에서 일괄
  append. **batch 단위로 entry를 모아 한 트랜잭션으로 쓰는 효과**.
- 한 task에 대한 entry는 한 batch당 정확히 한 줄. PRD의 "idempotent 1회"
  요구 충족.

대안 검토: "각 task 머지 직후 stateMarked만 알 수 있는 시점에서 append하고
smoke 결과는 batch 끝에 별도 append"도 가능하나, **읽는 쪽 복잡도**가 커진다
(같은 (taskId, mergedSha)에 대해 여러 entry를 합산해야 함). 현재 설계는
"한 batch가 끝나야 해당 batch의 entry가 디스크에 보인다" — 일관된 단위.

trade-off: smoke 도중 프로세스 강제 종료 시 batch entry가 누락된다. 이때는
git log에 머지 커밋만 남는데, 이는 현재 동작과 동일 (오히려 일부만 기록되어
혼란을 주는 것보다 나음). `--rollback`은 git 그래프 + state.json을 통한
기존 복구 경로를 그대로 사용 (§ 3.7).

### 3.5 batch 인덱스 / rebase 실패 task

batch 인덱스:

- `MergeOrchestrator`의 새 필드 `_batchCounter` (생성자에서 0으로 초기화).
- `MergeAndFinalizeAsync` 진입 시 `Interlocked.Increment(ref _batchCounter)`
  로 1-based 값 획득 → entry의 `Batch` 필드.
- 단일 `MergeOrchestrator` 인스턴스는 `ParallelExecutor`가 보유하고 같은
  `--run` 세션 동안 재사용되므로 monotonic.

rebase 실패 task:

- 이번 batch에서 머지되지 않음 → base에 변경 반영 없음 → mergedSha 없음.
- merge-log entry **생성하지 않는다**. (PRD의 "이미 머지된 SHA" 매핑 목적과
  맞지 않음.)
- `RalphLogger`(.ralph-logs/ralph-...log)에 기록되는 rebase 실패 정보가
  대신 진단 자료로 남는다 (현재 fix2 #5 동작).

자동 롤백 보류 케이스 (working dirty 등):

- merge entry는 정상 append (이미 머지된 사실은 변하지 않음).
- rollback entry는 **append하지 않음** (실제로 revert가 실행되지 않았으므로).

### 3.6 `MergeLogService` 구조

```csharp
// Ralph/Services/MergeLogService.cs
public sealed class MergeLogService
{
    private readonly string _logFilePath;
    private readonly RalphLogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly HashSet<(string taskId, string mergedSha)> _seenMerges = new();
    private readonly HashSet<(string taskId, string revertSha)>  _seenRollbacks = new();
    private bool _loaded = false;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = RalphJsonContext.Default,
    };

    public MergeLogService(string repoRoot, RalphLogger logger)
    {
        _logFilePath = Path.Combine(repoRoot, RalphPaths.MergeLogRelative);
        _logger = logger;
    }

    public Task AppendMergeAsync(MergeLogEntry entry, CancellationToken ct);
    public Task AppendRollbackAsync(MergeLogEntry entry, CancellationToken ct);

    /// <summary>읽기 전용 — --status / --rollback에서 사용.</summary>
    public Task<IReadOnlyList<MergeLogEntry>> ReadAllAsync(CancellationToken ct);

    private async Task EnsureLoadedAsync(CancellationToken ct);
    private async Task AppendLineAsync(MergeLogEntry entry, CancellationToken ct);
}
```

`AppendLineAsync` 핵심 (CostTracker 패턴 mirror):

```csharp
private async Task AppendLineAsync(MergeLogEntry entry, CancellationToken ct)
{
    var dir = Path.GetDirectoryName(_logFilePath)!;
    Directory.CreateDirectory(dir);
    var line = JsonSerializer.Serialize(
        entry,
        typeof(MergeLogEntry),
        RalphJsonContext.Default) + "\n";
    // 5초 타임아웃 — CostTracker와 동일 톤 (batch 마지막에 디스크 행킹 방지).
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(TimeSpan.FromSeconds(5));
    await File.AppendAllTextAsync(_logFilePath, line, cts.Token);
}
```

실패 처리:

- IO 실패 시 `RalphLogger.Warn`으로 경고 + **batch는 계속 진행** (merge-log
  실패가 머지 커밋/state를 되돌릴 이유가 되지 않음 — 로그 부재가 머지의
  옳고그름을 바꾸지 않음).
- CostTracker 처럼 `merge-log-failures.jsonl`까지는 **만들지 않는다** —
  과잉 설계. CostTracker는 회계상 비가역 손실(돈) 때문에 fallback이 필요한
  것이고, 머지 로그는 git log + state.json 으로 재구성 가능한 부가 기록이다.

잠금:

- `SemaphoreSlim(1, 1)` 으로 직렬화. `MergeOrchestrator`는 이미 batch 단위
  순차 머지를 하므로 사실상 경합이 없지만, `--status`가 동시에 read를 한다면
  reader는 잠금 없이 파일을 한 번 통째로 읽고 EOF에서 가장 최근 줄이 깨진
  경우 무시 (defensive parsing).

### 3.7 `--status`에서의 활용

현 `StatusCommand` 출력에 다음 섹션을 **선택적으로** 추가 (merge-log가
존재할 때만):

```
머지 트랜잭션 로그 (.ralph-logs/merge-log.jsonl):
  마지막 batch    : #7  (UTC 2026-04-30T03:14:22.123Z)
  smoke test      : passed
  자동 롤백       : 없음
  최근 머지       :
    - feature-x-impl   merged=a1b2c3d  state=marked
    - feature-x-test   merged=4e5f6a7  state=marked
  history         : merge entry 23건, rollback entry 1건
```

자동 롤백이 마지막 batch에 있었다면:

```
  자동 롤백       : revert e7d8c9a (3 task pending 복귀)
```

구현:

- `StatusCommand`가 `MergeLogService.ReadAllAsync`로 전체 entry 로드 후 끝쪽
  몇 개만 사용. 파일이 없으면 섹션 자체를 표시하지 않는다 (legacy 호환).
- `state.json`의 done 비트와 merge-log의 `stateMarked`가 일치하지 않으면
  경고 한 줄: `state.json과 merge-log의 stateMarked 불일치 (X건). --rollback
  검토를 권장합니다.` — 진단 가치 큼.

표시 위치: 기존 워크트리 섹션 **뒤**. 워크트리 섹션이 "현재" 상태,
merge-log 섹션이 "이력". 사용자가 보는 자연스러운 순서.

DI: `StatusCommand`는 현재 `TaskManager`를 받는다. `MergeLogService`도
`CommandContext`를 통해 주입하거나, `RepoRoot + Logger`로 즉석 생성.
복잡도 최소화를 위해 후자 — `StatusCommand` 내부에서 `new MergeLogService(...)`.

### 3.8 `--rollback`에서의 활용

`RollbackCommand`(`Ralph/Commands/RollbackCommand.cs`)는 현재
`state.json`의 done 비트를 보고 pre-plan/post-plan 스냅샷을 선택한 뒤
`RollbackService.RestoreAsync`로 전체 복원한다 — git reset --hard, tasks.json
덮어쓰기, PRD 재생성.

merge-log의 활용은 **두 가지 방향**으로 분리:

#### (a) 정밀 진단 (Phase 1, 본 fix 범위)

복원을 실행하기 **직전**에 사용자에게 다음을 요약 출력:

```
복원 대상 스냅샷: post-plan (2026-04-30T01:02:03Z)
이 스냅샷 이후 merge-log entry: 7건
  - batch #1: feature-x-impl  merged=a1b2c3d  smoke=passed
  - batch #2: feature-y-impl  merged=4e5f6a7  smoke=passed
  - batch #3: feature-z-impl  merged=7890abc  smoke=failed (auto-rollback)
  ...
복원 시 위 머지 커밋들이 모두 사라집니다 (git reset --hard).
계속하시겠습니까? [y/N]
```

이는 사용자가 `--rollback`이 어디까지 되감는지 시각적으로 알게 한다. 현재는
"7개 머지가 사라진다"가 git log를 직접 봐야만 보이는 정보다.

`--force` 시 확인 프롬프트 없이 위 요약만 출력한다.

#### (b) 정밀 복구 (Phase 2, 별도 fix로 미룸)

PRD 의 "정밀 복구"의 강한 해석 = "특정 batch 이후만 되돌리기" (스냅샷이
아닌 merge-log 기반 reset to specific baseSha + selective state pending).
이는 다음 위험을 동반한다:

1. baseSha 시점 이후 사용자가 직접 추가한 커밋이 있다면 reset --hard로 손실.
2. tasks.json / PRD가 그 사이에 변경됐다면 스냅샷 메타데이터와 충돌.
3. 사용자가 의도하는 "어느 batch까지" 의 UX 정의가 비자명 (CLI 인자 형태,
   default).

본 fix는 (a)만 구현한다. (b)는 **별도 후속 fix** 로 분리. 이유:

- (a)만으로도 PRD의 "더 정확히" / "정밀 복구의 보조" 요구를 충족한다.
- (b)는 RollbackService 시그니처 확장과 사용자 UX 디자인이 추가로 필요하다 —
  단일 fix 범위를 넘어선다.

§ 7 (마이그레이션 / 향후 작업) 에 (b)를 명시.

### 3.9 AOT JSON 직렬화 (RalphJsonContext 패턴)

```csharp
// Ralph/Models/RalphJsonContext.cs (기존 위에 추가)
[JsonSerializable(typeof(MergeLogEntry))]
[JsonSerializable(typeof(IReadOnlyList<MergeLogEntry>))]   // ReadAll에서 사용
internal partial class RalphJsonContext : JsonSerializerContext { }
```

직렬화/역직렬화는 `JsonSerializer.Serialize(entry, RalphJsonContext.Default.MergeLogEntry)`
및 `JsonSerializer.Deserialize(line, RalphJsonContext.Default.MergeLogEntry)`
형태로 source-gen path만 사용 (reflection fallback 금지).

`UnsafeRelaxedJsonEscaping`은 사용하지 않는다 — entry 필드는 모두 ASCII
SHA / kebab-case taskId / 정해진 enum 문자열이라 escaping 이슈 없음.

---

## 4. 인터페이스 변경 요약

### 4.1 `RalphPaths.cs`

```csharp
public const string MergeLogFileName     = "merge-log.jsonl";
public const string MergeLogRelativePath = LogDir + "/" + MergeLogFileName;
public static string MergeLogRelative    => Path.Combine(LogDir, MergeLogFileName);
```

`LogRotator`의 preserved 파일 목록에 `MergeLogFileName` 추가.

### 4.2 `Ralph/Models/MergeLogEntry.cs` (신설)

§ 3.2 의 POCO. nullable 어노테이션 + `JsonPropertyName` 부여.

### 4.3 `RalphJsonContext.cs`

`[JsonSerializable(typeof(MergeLogEntry))]` 추가. `IReadOnlyList<MergeLogEntry>`도
함께 등록하면 read 시 reflection 회피.

### 4.4 `Ralph/Services/MergeLogService.cs` (신설)

§ 3.6의 클래스.

### 4.5 `MergeOrchestrator.cs`

- 생성자에 `MergeLogService mergeLog` 추가 (기존 _taskManager/_git/_logger 등과 같은 자리).
- `_batchCounter` 필드 추가.
- `MergeAndFinalizeAsync` 입구에서 `Interlocked.Increment(ref _batchCounter)`.
- 머지/state 마킹 결과를 `(taskId, mergedSha, stateMarked)` 튜플 리스트로 모음.
- smoke 결과 결정 후 batch entry 일괄 append.
- 자동 롤백 발동 시 `revertSha`(이미 fix2 #7에서 commit hash 알려짐) 를 받아
  rollback entry append.
- `TryAutoRollbackAsync`의 반환을 `string? revertSha` (현재 `bool` 라면 변경)
  로 보강. 호출 분기는 동일.

### 4.6 `RollbackService.cs`

변경 없음 — merge-log 와 직교. `RollbackCommand`가 `MergeLogService.ReadAllAsync`
를 별도 호출.

### 4.7 `StatusCommand.cs`

- `MergeLogService` 인스턴스 생성 + `ReadAllAsync`.
- merge-log 섹션 렌더링 (§ 3.7).
- 파일 부재 시 silent skip (legacy 호환).

### 4.8 `RollbackCommand.cs`

- 복원 직전 merge-log entry 요약 출력 (§ 3.8 (a)).
- `--force` 미지정 시 entry 수가 1개 이상이면 확인 프롬프트.

### 4.9 `RunCommand.cs`

`MergeLogService` 한 번 생성 (`CommandContext.RepoRoot` + `_logger`) 하여
`MergeOrchestrator` 생성자에 전달. CommandContext 자체는 수정 불필요.

### 4.10 `ralph-schema.json`

변경 없음. merge-log는 spec(`tasks.json`) 외부.

---

## 5. 회귀 / 호환성

| 시나리오 | 현재 동작 | 변경 후 기대 |
|---|---|---|
| 첫 `--run` (merge-log 부재) | — | append-only 생성, 누적. |
| 동일 task 중복 entry 시도 | — | `(taskId, mergedSha)` dedup으로 silent skip. |
| 자동 롤백 + 재머지 | (#7만 있을 때) state pending → 다음 run에서 새 mergedSha로 머지 | 새 mergedSha의 새 entry 정상 append. 이전 머지의 rollback entry도 보존. |
| `--status` (merge-log 부재) | tasks/state/worktree 정보만 | 동일. merge-log 섹션 미표시. |
| `--status` (merge-log 존재) | (해당 없음) | 마지막 batch + smoke + recent merges 표시. |
| `--rollback` (merge-log 부재) | snapshot 기반 복원 | 동일. entry 요약 섹션 0건 표시. |
| `--rollback` (merge-log 존재) | snapshot 기반 복원 | 복원 직전 7건 entry 요약 + 확인 프롬프트. |
| merge-log IO 실패 | (해당 없음) | warn 1줄 + batch 계속 진행 (merge-log는 부가 기록). |
| `LogRotator` 30일 경과 | merge-log 도 삭제될 수 있음 | preserved 목록에 추가 → 보존. |
| 다중 동시 `ralph --run` | 권장하지 않음 | 동일. merge-log는 race가 가능하나 atomic append로 줄 깨짐 없음. |
| Windows | 동일 | LF 줄바꿈 명시 → CRLF 변환 회피. |

확인 항목 (PR 단계):

- [ ] AOT 빌드(`dotnet publish -c Release`) 시 reflection 경고 없음.
- [ ] merge-log 파일 부재 시 `EnsureLoadedAsync`가 예외 없이 빈 set 로드.
- [ ] `--status`가 빈 merge-log 파일(0 byte) 도 정상 처리.
- [ ] `LogRotator`의 preserved 목록 확장이 cost.jsonl/validation.jsonl 케이스를
      깨지 않음.
- [ ] fix2 #7 의 `BatchRollbackSnapshot.TaskIds` 와 본 fix의 `mergedTasks` 가
      혼동되지 않음 (rebase 실패 task는 mergedTasks에 없음 — entry 미생성과
      일치).

---

## 6. 테스트 시나리오

테스트는 임시 git repo + 실제 git 명령(IGitService 실 구현) + 실제 디스크
JSONL append를 사용. mock 금지 (CostTracker / MergeOrchestrator 통합과 동일
정책).

1. **fresh_run_creates_jsonl**
   - tasks 2개를 머지.
   - assert: `.ralph-logs/merge-log.jsonl`이 생성되고 entry 정확히 2줄.
     각 entry의 ts/batch/taskId/baseSha/mergedSha/stateMarked/smokeTest 필드
     형식 검증. `event` 필드는 출력되지 않음(null skip).

2. **idempotent_same_taskid_mergedsha**
   - 동일 (taskId, mergedSha)로 `AppendMergeAsync` 두 번 호출.
   - assert: 파일에 entry 1줄.

3. **idempotent_across_process_restart**
   - 1세션에서 2 entry append → 프로세스 재시작 → 같은 (taskId, mergedSha)로
     append 시도.
   - assert: `EnsureLoadedAsync`가 기존 entry를 dedup set에 로드 → 두 번째
     append no-op.

4. **distinct_mergedsha_creates_new_entry**
   - 동일 taskId, 다른 mergedSha (자동 롤백 후 재머지 시뮬).
   - assert: entry 2줄 — 둘 다 보존.

5. **smoke_passed_recorded**
   - 머지 후 smoke `true` 통과.
   - assert: 모든 entry의 `smokeTest = "passed"`.

6. **smoke_failed_recorded_no_autorollback**
   - smoke 실패 + `_autoRollbackOnSmokeFail = false`.
   - assert: 모든 entry의 `smokeTest = "failed"`. rollback entry 없음.

7. **smoke_failed_with_autorollback_appends_rollback_entry**
   - smoke 실패 + 옵션 ON + working clean.
   - assert: 머지 entry N건 + 동일 batch에 대한 rollback entry N건.
     각 rollback entry에 `event = "rollback"`, `rollbackRevertSha` 채워짐,
     `stateMarked = false`.

8. **smoke_skipped_recorded**
   - `--no-smoke-test` 또는 docs-only 자동 skip.
   - assert: entry의 `smokeTest = "skipped"`.

9. **rebase_failed_task_excluded**
   - 두 task 중 하나가 rebase 충돌로 머지 안 됨, 나머지 1개 머지 + smoke
     통과.
   - assert: entry 1건만(머지된 task). rebase 실패 task는 merge-log에 없음.

10. **state_mark_failure_recorded**
    - `MarkTaskDoneThreadSafeAsync`가 IO 실패하도록 시뮬 (state.json read-only).
    - fix1 #1 정책으로 batch가 abort되더라도 **이미 머지된 task의 entry는
      stateMarked=false 로 기록**되어야 함 — 사후 진단을 위해.
    - assert: entry 존재 + `stateMarked = false`.

11. **status_displays_merge_log_section**
    - `--run` 후 `--status` 호출.
    - assert: stdout에 "마지막 batch", "smoke test", "최근 머지" 섹션 포함.
      각 줄에 task ID와 7자 short SHA 포함.

12. **status_no_merge_log_silent**
    - merge-log 파일 미존재 상태에서 `--status`.
    - assert: stderr/stdout에 merge-log 관련 출력 없음 (legacy 호환).

13. **rollback_summary_lists_entries_since_snapshot**
    - `--plan` 후 N batch `--run` → `--rollback`.
    - assert: 복원 직전 출력에 N batch 만큼의 entry 요약. batch 번호, taskId,
      smoke 결과 포함.

14. **rollback_force_skips_prompt_keeps_summary**
    - `--rollback --force`.
    - assert: 확인 프롬프트 없음, 요약은 출력됨.

15. **log_rotator_preserves_merge_log**
    - merge-log mtime을 60일 전으로 조작 + `--logs --cleanup`.
    - assert: merge-log 파일 보존됨 (cost.jsonl 보존과 동일 동작).

16. **append_io_failure_does_not_break_run**
    - merge-log 파일을 OS 락(또는 디렉터리를 read-only로)으로 일시 잠금.
    - assert: append는 warn 출력 후 silent fail. batch는 정상 종료
      (exit 0 또는 본래 결과).

17. **state_mismatch_warning**
    - merge-log 의 stateMarked=true 인데 state.json 의 done=false (인위적
      편집).
    - assert: `--status` 가 경고 한 줄 출력.

18. **aot_serialization_smoke**
    - `dotnet publish -c Release -p:PublishAot=true` 빌드 후 위 핵심 시나리오
      (1, 2, 7) 재실행.
    - assert: reflection 경고 없음, entry JSON이 source-gen path와 동일.

---

## 7. 마이그레이션 / 향후 작업

- **정밀 복구(b)** (§3.8): 특정 batch까지 reset --hard + selective state
  pending. UX 디자인과 안전 가드(누가 그 사이에 commit 했는지)가 별도 검토
  필요. 별도 fix.
- **Notification 페이로드 확장**: `NotificationService`에 자동 롤백 발동 시
  merge-log 의 마지막 N건을 첨부 (Slack/Discord). fix2 후속 작업.
- **외부 도구 호환**: `merge-log.jsonl`은 `jq` 친화적 — README/CLAUDE.md에
  스키마와 사용 예 한 단락 추가 (별도 doc fix).
- **batch 인덱스 글로벌화**: 현재 batch는 세션 내 1-based. 누적 monotonic
  카운터(예: `state.json`에 `lastBatchSeq` 필드)로 격상하면 세션 간 비교가
  가능. 본 fix 범위 외.
- **merge-log 압축**: 1000건 넘기면 `merge-log.jsonl.1` 로 rotate. 현재
  `LogRotator`는 시간 기반만 지원 — 추후 크기 기반 정책 별도.

---

## 8. 완료 보고

- **생성**: `docs/fix2/08-merge-log-plan.md` (본 문서)
- **수정**: 없음
- **Scope 외 변경**: 없음
- **선행 의존**: fix2 #2 (RalphPaths의 ledger 패턴), fix2 #7 (BatchRollbackSnapshot,
  TryAutoRollbackAsync, SmokePhaseResult).
- **참고 문서**: `docs/fix2/02-cost-failures-plan.md` (CostTracker 의 JSONL
  append + SemaphoreSlim 패턴 차용), `docs/fix2/07-auto-rollback-plan.md`
  (자동 롤백 발동 시 rollback entry 추가 정책), `fix2.md` #8 항목.
