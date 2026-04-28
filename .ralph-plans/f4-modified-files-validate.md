# F4 계획: modifiedFiles 머지 후 실측 검증 (P1)

## 1. 배경 / 문제 정의

태스크 스키마는 `outputFiles`(생성)와 `modifiedFiles`(수정)로 각 태스크의
**선언된 파일 영향 범위**를 표명한다. 이는 Plan 단계의 약속이지만, 현재
실행 파이프라인에는 약속과 실측을 대조하는 단계가 없다.

영향:
- Claude가 prompt의 Scope 지시를 무시하고 선언 외 파일을 건드려도 머지가 그대로 통과한다.
- 사후 추적(누가 이 파일을 바꿨나)이 불가능하여 회귀 디버깅이 어렵다.
- "선언만 적고 실제로는 안 만드는" 케이스도 잡지 못해 후속 태스크의 의존 가정이 깨진다.
- F2가 들어왔지만 `tasks.json`만 보호할 뿐, 그 외 파일 영향 범위는 여전히 자유 영역이다.

목적: **머지 직전, base..HEAD 사이 실제 변경 파일 집합과 declared 집합을 대조해 둘의 차집합을
관측·기록하고, 옵션으로 차단까지 한다.**

## 2. 현재 구현 분석

### 2.1 머지 흐름의 핵심 지점

`Ralph/Services/ParallelExecutor.cs:264-300` — 순차 머지 루프(주석 발췌):

```csharp
foreach (var taskId in taskIds)
{
    tracker.UpdateStatus(taskId, TaskProgressStatus.Merging);

    // F2: tasks.json 정규화 (commit-tree 위반 방어)
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

F4 검증은 **`NormalizeTasksJsonAsync` 직후 / `MergeWorktreeAsync` 직전**에 들어가야 한다. 이유는 §3.5에서 설명.

### 2.2 데이터 모델

`Ralph/Models/TasksFile.cs:50-54`:

```csharp
[JsonPropertyName("outputFiles")]
public List<string>? OutputFiles { get; set; }

[JsonPropertyName("modifiedFiles")]
public List<string>? ModifiedFiles { get; set; }
```

- 둘 다 `List<string>?` (nullable). null/빈 리스트 모두 정상 케이스로 다룬다.
- 합집합 시 `List<string>?` 두 개를 안전하게 처리해야 함(`?? new List<string>()` + `Distinct(StringComparer.Ordinal)`).
- 경로는 PRD 작성 컨벤션상 repo-root 기준의 슬래시 구분 상대경로(`Ralph/Services/Foo.cs`)를 가정. Windows 호환을 위해 비교 시 `\` → `/` 정규화 단계를 둔다.

### 2.3 git diff 사용 가능성

`Ralph/Services/GitService.cs:42-68`의 `RunAsync(string[], string? workingDirectory, CancellationToken)`이 worktree cwd 지정을 지원하므로 `git -C {worktree} diff --name-only {baseRef}..HEAD`를 그대로 호출할 수 있다. F2의 `NormalizeTasksJsonAsync`(`Ralph/Services/WorktreeService.cs:131-188`)가 이미 동일 패턴(`git diff --name-only baseRef..HEAD -- pathspec`)을 쓰고 있어 일관된 인터페이스로 추가 가능.

### 2.4 RalphLogger

`Ralph/Services/RalphLogger.cs:22-24` — `Info/Warn/Error` 세 단계만 제공. F4의 위반 기록은 `Warn`을 쓴다. JSONL은 별도 파일이므로 logger와 무관.

### 2.5 CLI 파서 패턴

`Ralph/Program.cs:39-77`의 패턴은 단순한 `argList.Remove("--flag")` 또는 `argList.IndexOf("--flag")` + 인접 값. 새 boolean 플래그는 `argList.Remove("--strict-files")` 한 줄로 충분.

### 2.6 ParallelExecutor 생성자

`Ralph/Services/ParallelExecutor.cs:18-30`, 호출은 `Ralph/Program.cs:274` 한 곳뿐:

```csharp
var executor = new ParallelExecutor(tm, claude, git, worktree, logger, tasksFile, modelArg);
```

생성자에 `bool strictFiles = false` 옵션 인자를 추가하고 호출부에서 전달.

## 3. 설계

### 3.1 신규 메서드: `WorktreeService.ValidateModifiedFilesAsync`

**시그니처:**

```csharp
public async Task<FileValidationResult> ValidateModifiedFilesAsync(
    string taskId,
    string baseRef,
    IReadOnlyCollection<string> declared,
    RalphLogger? logger = null,
    string validationLogPath = ".ralph-logs/validation.jsonl",
    CancellationToken ct = default);
```

- `declared`는 호출자가 `task.ModifiedFiles ∪ task.OutputFiles`를 미리 합쳐서 전달. 정규화(슬래시 통일, `Distinct`)도 호출자 책임.
- `validationLogPath`는 테스트 가능성을 위해 옵션으로 받되 기본값은 고정.
- worktree 경로는 `Path.GetFullPath(Path.Combine(_worktreeBase, taskId))`로 내부에서 계산(F2 패턴과 동일).

**왜 `WorktreeService`에 두는가:** 이미 `NormalizeTasksJsonAsync`/`MergeWorktreeAsync`/`AbortMergeAsync` 등 머지 도메인 메서드가 모여 있고, 검증 역시 worktree HEAD vs base 비교라는 동일한 git 호출 패턴이다. `ParallelExecutor`의 private에 두면 단위 테스트가 어렵고 향후 단일 태스크 경로(`--task`) 확장 시 재사용도 막힌다.

### 3.2 반환 타입: `FileValidationResult`

```csharp
public sealed record FileValidationResult(
    string TaskId,
    DateTimeOffset TimestampUtc,
    IReadOnlyList<string> Declared,
    IReadOnlyList<string> Actual,
    IReadOnlyList<string> Undeclared,   // actual − declared
    IReadOnlyList<string> NotChanged,   // declared − actual
    bool DiffFailed,                    // git diff 자체가 실패했는지
    string? DiffError                   // diff 실패 시 stderr
)
{
    public bool HasUndeclared => Undeclared.Count > 0;
    public bool HasNotChanged => NotChanged.Count > 0;
}
```

- `record`로 둬 immutable + 직렬화 친화적.
- 두 리스트는 모두 정렬된 형태로 채워 비결정적 출력을 막는다(테스트 친화성, JSONL diff 가독성).
- `DiffFailed`는 strict 모드에서 "검증 자체가 실패한 케이스"를 머지 강행 vs 차단으로 분기시키는 기준이 된다(§3.7 정책표 참고).

### 3.3 git 명령 시퀀스

`WorktreeService.ValidateModifiedFilesAsync` 내부:

```
1. var (exit, out) = git -C {worktreePath} diff --name-only {baseRef}..HEAD
   (pathspec 없음 — 모든 변경 파일 수집)
2. if exit != 0:
     logger.Warn("[validate:files] diff 실패: {stderr}")
     return FileValidationResult { DiffFailed=true, DiffError=stderr, lists=[] }
3. actual = out.Split('\n').Where(non-empty).Select(NormalizeSlash).Distinct(Ordinal).OrderBy(Ordinal)
4. declaredNorm = declared.Select(NormalizeSlash).Distinct(Ordinal).OrderBy(Ordinal)
5. undeclared = actual.Except(declaredNorm, Ordinal).OrderBy(Ordinal)
6. notChanged = declaredNorm.Except(actual, Ordinal).OrderBy(Ordinal)
7. if undeclared.Any():
     logger.Warn(
       "[validate:files] {taskId}: undeclared {n}건 — {first 3 paths}{...n>3?}")
8. JSONL 한 줄 append (§3.4)
9. return FileValidationResult { all fields filled }
```

`NormalizeSlash`는 Windows 백슬래시 → 슬래시 1줄 헬퍼. `git diff --name-only`는 POSIX path를 그대로 출력하므로 7번 단계까지 가서 차이가 나는 케이스는 거의 없지만, `declared` 측 입력이 사용자가 작성한 PRD에서 왔으므로 한 번 거른다.

### 3.4 JSONL 직렬화

**파일 경로:** `.ralph-logs/validation.jsonl` (append-only, 세션 누적)

**스키마(한 줄당 한 객체, camelCase, `WriteIndented=false`):**

```json
{"taskId":"f3-impl","timestamp":"2026-04-28T12:34:56.789Z","declared":["Ralph/Program.cs"],"actual":["Ralph/Program.cs","Ralph/Services/Foo.cs"],"undeclared":["Ralph/Services/Foo.cs"],"notChanged":[]}
```

필드:

| 필드 | 타입 | 설명 |
|---|---|---|
| `taskId` | string | 태스크 id |
| `timestamp` | string (ISO-8601 UTC, 'Z' suffix) | `DateTimeOffset.UtcNow.ToString("o")` |
| `declared` | string[] | `task.ModifiedFiles ∪ task.OutputFiles` 정규화·정렬 |
| `actual` | string[] | `git diff --name-only base..HEAD` 결과 정규화·정렬 |
| `undeclared` | string[] | `actual − declared` |
| `notChanged` | string[] | `declared − actual` |

**누적 모드:**

- `File.AppendAllTextAsync(path, line + "\n", ct)`로 `cost.jsonl`(`Ralph/Services/CostTracker.cs`)와 동일 패턴.
- 한 번에 하나의 worktree만 검증을 호출하므로(머지 루프는 sequential — `Ralph/Services/ParallelExecutor.cs:264`) 동시 쓰기 경쟁 없음. 굳이 lock 안 둠.
- 부모 디렉토리는 `Directory.CreateDirectory(".ralph-logs")` 한 번만 보장(이미 `RalphLogger`가 보장). 방어적으로 1회 더 호출해도 무해.
- JSON 직렬화는 `System.Text.Json`의 source-generated context는 도입하지 말고 단발 `JsonSerializer.Serialize` 사용(F2/F3과 일관).

**JSONL 파일 회전:** F4에서는 다루지 않는다. 기존 `LogRotator`는 `.log` 파일 대상이라 `.jsonl`은 별개. 향후 별도 P1 과제(누적 무한 증가 위험)로 분리.

### 3.5 호출 위치 — F2 → F4 → 머지 순서

```csharp
foreach (var taskId in taskIds)
{
    tracker.UpdateStatus(taskId, TaskProgressStatus.Merging);

    // F2: tasks.json 정규화 (먼저!)
    await _worktree.NormalizeTasksJsonAsync(
        taskId, baseBranch,
        tasksFileName: Path.GetFileName(_tasksFile),
        logger: _logger, ct: ct);

    // F4: declared vs actual 검증
    var declared = BuildDeclaredSet(_taskManager.GetTask(taskId)!);
    var validation = await _worktree.ValidateModifiedFilesAsync(
        taskId, baseBranch, declared, _logger, ct: ct);

    if (_strictFiles && validation.HasUndeclared)
    {
        AnsiConsole.MarkupLine(
            $"  [red]✗[/] {Markup.Escape(taskId)} undeclared 파일 {validation.Undeclared.Count}건. " +
            $"머지 중단 (strict-files).");
        _logger.Error(
            $"[validate:files][strict] {taskId} undeclared: " +
            string.Join(", ", validation.Undeclared));

        // 해당 worktree 정리·실패 표시
        await _worktree.CleanupWorktreeAsync(taskId, _logger, ct);
        // 미머지 잔여 태스크 정리 (기존 충돌 분기와 동일 패턴)
        foreach (var remaining in taskIds.SkipWhile(id => id != taskId).Skip(1))
            await _worktree.CleanupWorktreeAsync(remaining, _logger, ct);
        return 1;  // RunParallelBatchAsync 종료, 호출자 RunAsync가 종료
    }

    var mergeResult = await _worktree.MergeWorktreeAsync(...);
    ...
}
```

`BuildDeclaredSet`은 `ParallelExecutor`의 private 헬퍼:

```csharp
private static IReadOnlyCollection<string> BuildDeclaredSet(TaskItem task)
{
    var set = new HashSet<string>(StringComparer.Ordinal);
    if (task.ModifiedFiles is { Count: > 0 }) foreach (var p in task.ModifiedFiles) set.Add(p);
    if (task.OutputFiles  is { Count: > 0 }) foreach (var p in task.OutputFiles)  set.Add(p);
    return set;
}
```

**왜 F2 → F4 순서인가:**

F2의 `NormalizeTasksJsonAsync`가 worktree HEAD에 `tasks.json`을 base와 동일한 내용으로 다시 커밋한다(`Ralph/Services/WorktreeService.cs:168-170`). 이 시점 `git diff --name-only base..HEAD`는 트리 비교이므로, **내용이 같아진 `tasks.json`은 결과에서 사라진다**. 즉:

- 정규화 전에 검증하면 → 거의 모든 worktree에서 `tasks.json`이 actual에 등장 → declared에 없으면 매번 undeclared 거짓 양성.
- 정규화 후에 검증하면 → `tasks.json`은 자연스럽게 actual에서 빠짐 → 진짜 undeclared만 남음.

F4 입장에서 F2가 사실상 "tasks.json 노이즈 제거 단계" 역할을 한다. 의존성을 코드 주석에 명시한다.

### 3.6 strict 모드에서 머지 중단 + 실패 표시

`--strict-files`가 active이고 `validation.HasUndeclared`인 경우:

1. `MergeWorktreeAsync`를 호출하지 않는다(머지 자체를 건너뜀).
2. `_logger.Error(...)`로 위반 파일 목록 기록.
3. 해당 태스크의 worktree만 즉시 `CleanupWorktreeAsync`로 정리(검토용 보존이 더 유용한가는 §6에서 논의).
4. 동일 batch의 후속 태스크는 머지하지 않고 worktree만 정리한 뒤 `RunParallelBatchAsync`는 `return 1`로 종료.
5. 결과적으로 `RunAsync`가 1을 받아 세션 종료, exit code 1.

**왜 동일 batch 후속도 차단하나:** strict 모드의 사용 의도는 "엄격한 영향 범위 검증"이다. 한 태스크가 위반했다면 그 변경이 다른 worktree와 결합해 일으킬 부수 효과를 알 수 없으므로 보수적으로 전체 배치를 중단한다.

**`tasks.json`의 done 상태:** strict 차단 케이스에서는 `MarkTaskDoneThreadSafe`(`Ralph/Services/ParallelExecutor.cs:573`)를 호출하지 않는다(머지 안 했으므로 미완료가 정상). 다음 실행 시 같은 태스크가 다시 ready 큐에 들어감.

### 3.7 비-strict 기본 모드(warn-only)

- `Undeclared` 비어있지 않아도 `_logger.Warn` + JSONL append만 수행하고 머지 진행.
- `NotChanged`는 strict 여부와 무관하게 항상 warn-only(거짓 양성 가능성이 높음 — Claude가 동등한 동작을 다른 파일로 구현했을 수 있음).
- `DiffFailed=true`는 strict 모드여도 머지를 막지 않는다. F2의 동일 정책("방어 로직이 머지를 망가뜨리면 안 된다")과 일관.

### 3.8 CLI 플래그: `--strict-files`

`Ralph/Program.cs:39-41` 인근에 추가:

```csharp
var debug = argList.Remove("--debug");
var sequential = argList.Remove("--sequential");
var forceFlag = argList.Remove("--force");
var strictFiles = argList.Remove("--strict-files");   // F4 추가
```

`Ralph/Program.cs:274` 호출부 수정:

```csharp
var executor = new ParallelExecutor(
    tm, claude, git, worktree, logger, tasksFile, modelArg,
    strictFiles: strictFiles);
```

`ParallelExecutor` 생성자에 `bool strictFiles = false` 추가하고 `_strictFiles` 필드에 보관:

```csharp
public ParallelExecutor(
    TaskManager taskManager, ClaudeService claude, GitService git,
    WorktreeService worktree, RalphLogger logger, string tasksFile,
    string? model = null, bool strictFiles = false)
{
    ...
    _strictFiles = strictFiles;
}
```

**환경변수 호환:** `RALPH_STRICT_FILES=true`도 같은 의미로 받게 한다(F2의 `RALPH_PARALLEL` 패턴과 일치). 우선순위: CLI > env. CLI/env 모두 미지정 시 false.

```csharp
var envStrictFiles = Environment.GetEnvironmentVariable("RALPH_STRICT_FILES")?.ToLower() == "true";
var strictFiles = argList.Remove("--strict-files") || envStrictFiles;
```

**`--sequential` 모드:** F4 검증은 worktree 기반 머지 단계에 결합된다. 순차 모드는 worktree를 안 쓰므로 검증을 적용하지 않는다(scope 외). 향후 P2에서 순차 모드용 dirty-tree 비교 검증을 별도 검토.

**`--task <id>` / `--interactive` 모드:** 동일 이유로 본 PR에서는 적용하지 않는다.

**`--help`:** F4 구현 PR에서 `Ralph/Program.cs`의 `ShowHelp` 본문에 한 줄 추가:

```
  --strict-files       머지 직전 declared vs actual 파일 검증.
                       undeclared 파일 발견 시 머지 중단·태스크 실패.
```

### 3.9 사용자 표시(콘솔)

머지 루프 중 검증 결과를 적절히 노출:

- `validation.HasUndeclared` (warn-only): `[yellow]⚠[/] {taskId} undeclared {n}건 (warn-only)` 한 줄. 파일 목록은 JSONL/log에서 확인하도록 안내.
- `validation.HasNotChanged`: 동일 형식의 `[dim]ℹ[/] notChanged {n}건` 줄(소음 줄이려 dim).
- `validation.DiffFailed`: `[yellow]⚠[/] {taskId} diff 실패 — 검증 스킵`.
- strict 차단: `[red]✗[/] {taskId} undeclared 파일 {n}건. 머지 중단 (strict-files).`

너무 길어지지 않도록 첫 3개 경로만 노출하고 나머지는 `... (외 N건)`로 줄임.

## 4. 구현 단계 분해 (구현 PR에서 수행할 작업)

1. `Ralph/Services/WorktreeService.cs`에 `ValidateModifiedFilesAsync` + `FileValidationResult` record 추가.
2. JSONL 직렬화 옵션 정의: `static readonly JsonSerializerOptions ValidationJsonOpts` (camelCase, no indent). `Ralph/Services/CostTracker.cs:JsonOpts` 패턴 답습.
3. `Ralph/Services/ParallelExecutor.cs`:
   - 생성자에 `bool strictFiles = false` 추가, `_strictFiles` 필드 보관.
   - `BuildDeclaredSet(TaskItem)` private static 헬퍼.
   - `RunParallelBatchAsync` 머지 루프에 F2 직후 호출 삽입(§3.5).
   - strict 차단 분기와 콘솔 출력.
4. `Ralph/Program.cs`:
   - `--strict-files` 파싱(env var 포함).
   - `ParallelExecutor` 생성자 호출부에 인자 전달.
   - `ShowHelp` 본문에 옵션 줄 추가.
5. (선택) `Ralph/Services/WorktreeService.cs`의 `ValidateModifiedFilesAsync`에 단위 테스트 가능한 형태로 fake repo 시나리오 1개(undeclared 1건 + notChanged 1건) 검증.

코드 변경은 위 4개 파일에 한정. 다른 파일은 손대지 않는다.

## 5. 회귀 위험 분석

| 위험 | 가능성 | 영향 | 완화책 |
|---|---|---|---|
| F2 정규화 전에 F4를 호출하면 `tasks.json` 거짓 양성 | 높음(순서 실수 시) | warn-only면 소음, strict면 강제 차단 | 코드 주석 + §3.5 호출 순서 명시. 통합 테스트에서 `tasks.json`이 undeclared에 없는지 1회 검증 |
| `git diff --name-only`가 rename(`R`) 케이스에서 두 줄(`old\nnew`)로 출력 | 낮음 | actual에 두 경로가 함께 잡힘 → declared가 둘 중 하나만 가지면 다른 하나가 undeclared | 일단 raw로 처리(보수적). 추후 `git diff --name-status -z`로 정밀화는 별도 P2 |
| Windows 백슬래시 path가 declared에 들어와 비교 실패 | 낮음 | undeclared/notChanged 거짓 양성 | `NormalizeSlash` 한 단계로 양쪽 통일(§3.3) |
| `.gitignore`에 의해 추적 안 되는 파일은 actual에 안 잡힘 | 중간 | declared에 있으나 untracked로 만든 산출물은 notChanged에 등장 | warn-only로 두고 운영자에게 검토 위임. PR 본문에 명시 |
| 위반이 폭증한 worktree에서 JSONL 한 줄이 매우 길어짐 | 낮음 | grep/jq 도구가 라인 길이 제한에 걸림 | JSON 한 라인 제한은 두지 않음. 필요 시 후처리 도구로 잘라쓰도록 권고 |
| `validation.jsonl`이 무한 누적되어 디스크 점유 | 중간 | 장기 운영 시 GB 단위 가능성 | F4 scope 외. 별도 P1 과제로 회전(rotate) 추가 |
| strict 모드에서 batch 일부 머지 후 차단 → base 상태 비대칭 | 중간 | 다음 실행이 부분 완료 상태에서 시작됨 | 기존 충돌 분기와 동일 정책: 처리한 부분은 보존, 실패 태스크는 미완료로 둔다. `tasks.json` 일관성은 F2가 보장 |
| strict 차단 시 worktree 즉시 정리로 사용자 디버깅 손실 | 중간 | 운영자가 위반 파일 내용을 못 봄 | JSONL의 `actual` 필드에 경로 모두 기록되어 있음 + `--logs` 명령으로 task 로그 확인 가능. 별도 옵션 `--keep-failed-worktree`는 P2 |
| `Path.GetFileName(_tasksFile)`처럼 `_tasksFile`이 서브디렉토리 경로일 때 declared 비교 어긋남 | 매우 낮음 | undeclared 거짓 양성 | 현 ralph는 repo root의 단일 파일 가정. F2와 동일 한계, 본 PR 외 |
| Claude가 declared 외 파일을 보조 작업으로 만든 임시 산출물(예: 캐시) | 중간 | warn-only면 소음, strict면 차단 | strict 모드는 opt-in이므로 사용자가 결과를 보고 결정. PR 본문에서 명시 |
| `validation.jsonl` 동시 쓰기 경쟁 | 매우 낮음 | 라인 손상 | 머지 루프가 sequential(`Ralph/Services/ParallelExecutor.cs:264`)이라 1회 1쓰기 보장. lock 불필요 |
| 빈 declared(`task.ModifiedFiles`/`OutputFiles` 둘 다 null/[]) | 중간 | 모든 actual이 undeclared로 분류 | 기본 warn-only이므로 소음일 뿐. strict 모드 사용자는 PRD 작성 시 declared를 채우는 책임을 진다 — PR 본문에 명시 |

## 6. 검증 시나리오 (구현 PR의 e2e 테스트)

1. **declared 일치(actual = declared)** — `Undeclared=[], NotChanged=[]`. JSONL 한 줄 추가, warn 없음, 머지 정상.
2. **undeclared 발견 (warn-only)** — declared = `[A.cs]`, actual = `[A.cs, B.cs]`. `Undeclared=[B.cs]`. logger.Warn + JSONL 기록 + 머지 정상.
3. **undeclared 발견 (strict)** — 동일 시나리오 + `--strict-files`. 머지 미실행, exit 1, task done=false.
4. **notChanged만 발견** — declared = `[A.cs, B.cs]`, actual = `[A.cs]`. strict여도 머지 진행(notChanged는 차단 사유 아님).
5. **F2 → F4 순서 회귀** — worktree에서 `tasks.json` 수정·커밋 후 F2 정규화 → F4 호출 시 actual에 `tasks.json`이 **없어야 함**.
6. **diff 실패** — 임시로 `baseRef` 잘못 지정 등으로 diff 실패 시 strict 모드여도 머지 강행.
7. **빈 declared** — `task.ModifiedFiles`/`OutputFiles` 모두 null. warn-only면 모든 actual이 undeclared로 기록되지만 머지는 진행. strict면 차단.
8. **`--sequential` 비영향** — 순차 모드에서 본 검증 코드 경로가 호출되지 않는지(worktree 미사용 분기) 확인.

자동화는 `WorktreeService.ValidateModifiedFilesAsync` 단위에서 fake git repo를 만들어 1·2·5·6·7번을 우선 커버한다.

## 7. 비목적 (Out of Scope)

- **순차 모드용 검증.** 단일 태스크/`--interactive`/`--sequential` 경로의 검증은 본 PR에서 제외.
- **위반 누적 카운터.** F2의 `TasksJsonViolationCounter`와 유사한 별도 집계 파일은 본 PR에서 만들지 않는다(JSONL이 raw 데이터 역할). 운영 가시성이 더 필요해지면 별도 P2.
- **`validation.jsonl` 회전/요약.** 무한 누적은 별도 과제.
- **rename/copy 정밀 분류.** `git diff --name-status` 도입은 보류.
- **declared 외 파일을 자동 unstage하는 자동 복원.** 본 PR은 관측·차단까지만 하고 자동 변경 되돌림은 다루지 않는다.
- **글로브/디렉토리 매칭.** `declared`에 `Ralph/Services/*` 같은 패턴이 들어와도 단순 문자열 비교만 한다. 패턴 지원은 P2.
- **plan 단계의 declared 자동 채움.** PRD/계획에서 declared가 비어 있으면 채워야 한다는 제안은 PRD 컨벤션 문서의 몫.

## 8. 결론

머지 직전(`Ralph/Services/ParallelExecutor.cs`의 머지 루프, F2 정규화 직후)에 `git diff --name-only base..HEAD`로 worktree 실측 변경을 수집해 `task.ModifiedFiles ∪ task.OutputFiles`와 대조한다. 결과는 `.ralph-logs/validation.jsonl`에 한 줄 누적 기록하고, undeclared 발견 시 `RalphLogger.Warn`을 남긴다. 기본은 warn-only로 머지를 막지 않으며, `--strict-files`(또는 `RALPH_STRICT_FILES=true`) 활성 시 undeclared가 있으면 머지 중단 + 태스크 실패 처리한다. 신규 메서드는 `WorktreeService.ValidateModifiedFilesAsync` 한 곳에 응집하고, F2의 `NormalizeTasksJsonAsync`가 `tasks.json` 노이즈를 사전 제거하므로 검증 결과의 거짓 양성을 최소화한다.
