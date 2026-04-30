# Fix #6 — 매직 스트링 중앙화 설계

## 1. 배경

`fix1.md` 6번 항목 요약: 경로 / 브랜치 prefix / config key 가 코드 전반에 하드코딩되어 있어
한 곳만 바꿔도 cleanup, branch detection 등이 silently 망가질 위험이 있다. 본 문서는 신규
정적 클래스 `Ralph/Services/RalphPaths.cs` 도입과 점진적 치환 계획을 정의한다.

`grep` 결과 (Ralph 본체 16개 파일 / 59 occurrences, 테스트 6개 파일):

| 리터럴 | 의미 | 등장 형태 |
|---|---|---|
| `.ralph-logs` | 로그 / state / cost / validation / rollback 디렉토리 | 디렉토리 segment, `Path.Combine` 인자 |
| `.ralph-worktrees` | worktree 베이스 디렉토리 | 디렉토리 segment, `WorktreeService` ctor 기본값 |
| `ralph/` | worktree 브랜치 prefix | `$"ralph/{taskId}"`, `branch --list ralph/*`, `StartsWith("ralph/")` |
| `branch.{name}.ralphManaged` | 사용자 브랜치 보호용 git config 마커 | `config` 인자 보간 + `--get` |
| `state.json` | mutable 진행 상태 파일명 | `Path.Combine(..., "state.json")` |
| `cost.jsonl` | 누적 비용 기록 | `LogFileName` 상수 / `ProtectedFiles` |
| `validation.jsonl` | 파일 검증 ledger | `validationLogPath` 기본값 / `ProtectedFiles` |
| `rollback`, `pre-plan.json`, `post-plan.json` | 스냅샷 디렉토리·파일 | `RollbackService` 내부 const (이미 const화) |
| `untracked-backup` | merge 시 untracked 파일 백업 sub-dir | `WorktreeService.cs:181` 인라인 |

---

## 2. 신규 정적 클래스 명세

파일: `Ralph/Services/RalphPaths.cs`
명명 규약은 기존 `LogRotator`(static class) 패턴을 따른다.

### 2.1 멤버 표

| 멤버 | 타입 | 값 | 형식 / 비고 |
|---|---|---|---|
| `LogDir` | `const string` | `".ralph-logs"` | 디렉토리 segment (relative). 모든 사용처가 `Path.Combine`으로 결합 |
| `WorktreeDir` | `const string` | `".ralph-worktrees"` | worktree 베이스 segment |
| `BranchPrefix` | `const string` | `"ralph/"` | 브랜치 namespace prefix (마지막 `/` 포함) |
| `ManagedConfigKeyTemplate` | `const string` | `"branch.{0}.ralphManaged"` | **템플릿** — `string.Format(template, branchName)` 또는 `GetManagedConfigKey(branchName)` 헬퍼 사용 |
| `StateFileName` | `const string` | `"state.json"` | 파일 basename. 디렉토리는 `LogDir` 와 결합 |
| `CostLedgerFileName` | `const string` | `"cost.jsonl"` | basename |
| `ValidationLedgerFileName` | `const string` | `"validation.jsonl"` | basename |
| `RollbackDirName` | `const string` | `"rollback"` | `LogDir` 산하 sub-dir (현재 `RollbackService` 내부 const, 노출로 끌어올림) |
| `PrePlanSnapshotFileName` | `const string` | `"pre-plan.json"` | basename |
| `PostPlanSnapshotFileName` | `const string` | `"post-plan.json"` | basename |
| `UntrackedBackupDirName` | `const string` | `"untracked-backup"` | `LogDir` 산하 sub-dir (`WorktreeService.cs:181`에서 발견, 신규 추가) |
| `BranchListGlob` | `const string` | `"ralph/*"` | `git branch --list <glob>` 인자. `BranchPrefix + "*"` 로 합성해도 됨 |

### 2.2 헬퍼 메서드

```csharp
public static class RalphPaths
{
    // ... (위 const들) ...

    /// <summary>"ralph/" + taskId. 브랜치 이름 합성을 한 곳에서.</summary>
    public static string GetBranchName(string taskId) => BranchPrefix + taskId;

    /// <summary>"branch.{branchName}.ralphManaged" config key를 합성.</summary>
    public static string GetManagedConfigKey(string branchName)
        => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                         ManagedConfigKeyTemplate, branchName);

    /// <summary>$"{LogDir}/state.json" — 호출자 cwd 기준 상대 경로.</summary>
    public static string StateFileRelative => Path.Combine(LogDir, StateFileName);

    /// <summary>$"{LogDir}/cost.jsonl"</summary>
    public static string CostLedgerRelative => Path.Combine(LogDir, CostLedgerFileName);

    /// <summary>$"{LogDir}/validation.jsonl"</summary>
    public static string ValidationLedgerRelative => Path.Combine(LogDir, ValidationLedgerFileName);

    /// <summary>특정 디렉토리(예: tasks.json 디렉토리) 산하의 .ralph-logs 절대/상대 경로.</summary>
    public static string LogDirUnder(string parentDir) => Path.Combine(parentDir, LogDir);
}
```

설계 원칙:
- 모든 멤버는 **상대 경로 segment**다. 절대 경로는 호출자가 `Path.GetFullPath` 또는 `Path.Combine(repoRoot, ...)` 으로 만들어야 한다 — `RalphPaths`는 cwd / repoRoot에 대해 가정하지 않는다.
- 헬퍼는 **합성만** 한다. 디렉토리 생성 / IO는 호출자 책임.
- 템플릿(`ManagedConfigKeyTemplate`)은 raw 노출 + 헬퍼 동시 제공: 이미 string.Format 패턴이 들어간 callsite와 헬퍼 도입 callsite를 모두 지원.

---

## 3. 파일별 치환 계획

각 항목: `파일:라인` — before → after.
머지 충돌을 줄이기 위해 동일 PR에서 **모두 일괄 치환**한다.

### 3.1 `Ralph/Services/StateStore.cs`

- **L9 / L36-37 / L43** (주석 + DefaultPathFor 본문)
  - L43 before:
    ```csharp
    return Path.Combine(dir, ".ralph-logs", "state.json");
    ```
  - L43 after:
    ```csharp
    return Path.Combine(dir, RalphPaths.LogDir, RalphPaths.StateFileName);
    ```
  - L9, L36-37 docstring의 `.ralph-logs/state.json` 표기는 그대로 둔다 (사람이 읽는 문서).

### 3.2 `Ralph/Services/RollbackService.cs`

- **L21-23**: 기존 internal const 3개(`RollbackDirName`, `PrePlanFileName`, `PostPlanFileName`)를 제거하고 `RalphPaths`의 동명 멤버를 참조.
  - before:
    ```csharp
    private const string RollbackDirName = "rollback";
    private const string PrePlanFileName = "pre-plan.json";
    private const string PostPlanFileName = "post-plan.json";
    ```
  - after: 삭제. 본문에서 `RollbackDirName` → `RalphPaths.RollbackDirName` 등으로 교체.
- **L37** ctor 기본값:
  - before: `public RollbackService(string logDir = ".ralph-logs")`
  - after:  `public RollbackService(string logDir = RalphPaths.LogDir)`  *(const 이므로 default 인자 가능)*
- **L39**:
  - before: `_rollbackDir = Path.Combine(logDir, RollbackDirName);`
  - after:  `_rollbackDir = Path.Combine(logDir, RalphPaths.RollbackDirName);`

### 3.3 `Ralph/Services/LogRotator.cs`

- **L12**: `private const string LogDir = ".ralph-logs";` → 제거하고 본문의 `LogDir`을 `RalphPaths.LogDir`로 치환 (L37, L44).
- **L15-19**: ProtectedFiles HashSet 초기화에 리터럴 대신 RalphPaths 멤버 사용.
  - before:
    ```csharp
    private static readonly HashSet<string> ProtectedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "cost.jsonl",
        "validation.jsonl",
    };
    ```
  - after:
    ```csharp
    private static readonly HashSet<string> ProtectedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        RalphPaths.CostLedgerFileName,
        RalphPaths.ValidationLedgerFileName,
    };
    ```

### 3.4 `Ralph/Services/CostTracker.cs`

- **L45-48**: `DefaultLogDir`, `LogFileName` 제거. `LogDir` 프로퍼티는 override 로직(`_logDirOverride`)을 위해 유지하되 default를 `RalphPaths.LogDir`로 변경.
  - before:
    ```csharp
    private const string DefaultLogDir = ".ralph-logs";
    private const string LogFileName = "cost.jsonl";
    private static string? _logDirOverride;
    private static string LogDir => _logDirOverride ?? DefaultLogDir;
    ```
  - after:
    ```csharp
    private static string? _logDirOverride;
    private static string LogDir => _logDirOverride ?? RalphPaths.LogDir;
    ```
- **L72**:
  - before: `public string LogFilePath => Path.Combine(LogDir, LogFileName);`
  - after:  `public string LogFilePath => Path.Combine(LogDir, RalphPaths.CostLedgerFileName);`
- **L333**: `MarkupLine("[yellow]비용 기록이 없습니다 (.ralph-logs/cost.jsonl이 없습니다).[/]")` — 사용자 표시 메시지의 경로 표기는 유지 가능하지만, 일관성을 위해
  ```csharp
  console.MarkupLine($"[yellow]비용 기록이 없습니다 ({RalphPaths.CostLedgerRelative}이 없습니다).[/]");
  ```
  로 권장 (선택적 개선).

### 3.5 `Ralph/Services/RalphLogger.cs`

- **L10**:
  - before: `public RalphLogger(string logDir = ".ralph-logs")`
  - after:  `public RalphLogger(string logDir = RalphPaths.LogDir)`

### 3.6 `Ralph/Services/WorktreeService.cs` (가장 손이 많이 가는 파일)

- **L55** ctor 기본값:
  - before: `public WorktreeService(GitService git, string worktreeBase = ".ralph-worktrees")`
  - after:  `public WorktreeService(GitService git, string worktreeBase = RalphPaths.WorktreeDir)`
- **L72 / L141 / L613** (브랜치 이름 합성):
  - before: `var branchName = $"ralph/{taskId}";`
  - after:  `var branchName = RalphPaths.GetBranchName(taskId);`
- **L102** (주석 안의 예시) — 코멘트라 변경 불필요. 그러나 가독성을 위해 `ralph/{taskId}` 표기를 유지.
- **L181** (untracked-backup 디렉토리):
  - before:
    ```csharp
    var backupDir = Path.Combine(
        repoRoot, ".ralph-logs", "untracked-backup",
        $"{taskId}-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
    ```
  - after:
    ```csharp
    var backupDir = Path.Combine(
        repoRoot, RalphPaths.LogDir, RalphPaths.UntrackedBackupDirName,
        $"{taskId}-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
    ```
- **L420** (`ValidateModifiedFilesAsync` default arg):
  - before: `string validationLogPath = ".ralph-logs/validation.jsonl",`
  - after:  `string? validationLogPath = null,` 으로 nullable 변환 + 본문 첫 줄에서
           `validationLogPath ??= RalphPaths.ValidationLedgerRelative;` 로 풀어냄.
           *(const 식 default arg는 가능하지만 `Path.Combine` 결과는 const가 아니므로 null sentinel 패턴 사용)*
- **L506** (사용자 메시지 안의 `validation.jsonl`):
  - 일관성 위해
    ```csharp
    logger?.Warn($"[validate:files] {result.TaskId}: {RalphPaths.ValidationLedgerFileName} 기록 실패 — {ex.Message}");
    ```
- **L528 / L541** (config key 보간):
  - before:
    ```csharp
    ["config", $"branch.{branchName}.ralphManaged", "true"]
    // ...
    ["config", "--get", $"branch.{branchName}.ralphManaged"]
    ```
  - after:
    ```csharp
    ["config", RalphPaths.GetManagedConfigKey(branchName), "true"]
    // ...
    ["config", "--get", RalphPaths.GetManagedConfigKey(branchName)]
    ```
- **L673**:
  - before: `var (_, branchOutput) = await _git.RunAsync(["branch", "--list", "ralph/*"], ct: ct);`
  - after:  `var (_, branchOutput) = await _git.RunAsync(["branch", "--list", RalphPaths.BranchListGlob], ct: ct);`
- **L677**:
  - before: `.Where(b => b.StartsWith("ralph/"))`
  - after:  `.Where(b => b.StartsWith(RalphPaths.BranchPrefix, StringComparison.Ordinal))`
- **L726**:
  - before: `if (branch is { Length: > 0 } && branch.StartsWith("ralph/"))`
  - after:  `if (branch is { Length: > 0 } && branch.StartsWith(RalphPaths.BranchPrefix, StringComparison.Ordinal))`
- L88, L533 등 docstring 안의 `ralph/*`, `branch.{name}.ralphManaged` 표기는 사람이 읽는 문서이므로 유지.

### 3.7 `Ralph/Services/ParallelExecutor.cs`

- **L280**:
  - before: `const string logDir = ".ralph-logs";`
  - after:  `var logDir = RalphPaths.LogDir;` *(const string `RalphPaths.LogDir` 자체가 컴파일타임 상수이므로 `const` 키워드도 유지 가능: `const string logDir = RalphPaths.LogDir;`)*

### 3.8 `Ralph/Services/WorktreeTaskRunner.cs`

- **L52**: 동일 패턴.
  - before: `const string logDir = ".ralph-logs";`
  - after:  `const string logDir = RalphPaths.LogDir;`

### 3.9 `Ralph/Commands/LogsCommand.cs`

- **L12**:
  - before: `private const string LogDir = ".ralph-logs";`
  - after:  로컬 const 제거 + 본문의 `LogDir` → `RalphPaths.LogDir`. 또는
           `private const string LogDir = RalphPaths.LogDir;` 로 alias 유지 (callsite 가독성).

### 3.10 `Ralph/Commands/StatusCommand.cs`

- **L36-37**:
  - before:
    ```csharp
    const string worktreeBase = ".ralph-worktrees";
    const string logDir = ".ralph-logs";
    ```
  - after:
    ```csharp
    const string worktreeBase = RalphPaths.WorktreeDir;
    const string logDir = RalphPaths.LogDir;
    ```

### 3.11 변경하지 않는 곳 (docstring / display 메시지)

다음은 사람이 읽는 문서/메시지로, 치환은 선택적이며 본 PR의 검증 grep에서 예외 처리할 수 있다:
- `Ralph/Models/StateFile.cs:7` — XML doc 안의 `.ralph-logs/state.json`
- `Ralph/Services/MergeOrchestrator.cs:163, 476` — 코멘트
- `Ralph/Services/PlanGenerator.cs:292, 319` — Claude에게 보내는 prompt 텍스트.
  ⚠ **주의**: 이 두 곳은 prompt 본문에 리터럴 경로가 들어가야 한다 (Claude가 그 경로를 보고 작업).
  치환하면 Claude 프롬프트가 변경되어 plan 출력이 미세하게 달라질 수 있으므로,
  prompt builder에서 `string.Format` 으로 `RalphPaths.LogDir` / `RalphPaths.WorktreeDir` 를
  주입하는 형태로 바꾸는 것을 권장.
- `Ralph/Services/TaskManager.cs:46-47, 327, 346` — 코멘트
- `Ralph/Services/RollbackService.cs:10-17` — 코멘트
- `Ralph/Services/LogRotator.cs:6-8` — 코멘트
- `Ralph/Services/StateStore.cs:9, 36-37` — 코멘트
- `Ralph/Services/WorktreeService.cs:88, 102, 411, 533, 545` — 코멘트
- `Ralph/Services/CostTracker.cs:38, 50, 61, 88, 232, 315` — 코멘트 + 사용자 override 경로(`~/.ralph/pricing.json`은 본 task 범위 외)
- `Ralph/Services/PlanGenerator.cs:292` 의 `.ralph-worktrees/{taskId}/` 표기 — 위와 동일한 prompt 주의사항.
- `Ralph/Commands/ResetCommand.cs:20`, `Ralph/Commands/RollbackCommand.cs:84` — 사용자 안내 메시지의 경로 표기.

위 항목들은 **검증 grep 시 예외 등록**(아래 §5)으로 처리한다.

---

## 4. 테스트 코드 영향 범위

`Ralph.Tests/`에서 동일 리터럴을 검증하는 위치 (총 6개 파일):

### 4.1 `Ralph.Tests/StateStoreTests.cs`

- **L86**:
  - before: `Assert.Equal(Path.Combine(dir, ".ralph-logs", "state.json"), statePath);`
  - after:  `Assert.Equal(Path.Combine(dir, RalphPaths.LogDir, RalphPaths.StateFileName), statePath);`
- L17, L33, L47, L63: 임시 디렉토리에 직접 만든 `state.json` 파일은 `RalphPaths.StateFileName` 으로 치환.

### 4.2 `Ralph.Tests/ParallelExecutorTests.cs`

- **L46**:
  - before: `CostTracker.SetLogDirForTesting(Path.Combine(_repoDir, ".ralph-logs"));`
  - after:  `CostTracker.SetLogDirForTesting(Path.Combine(_repoDir, RalphPaths.LogDir));`
- **L49**: 동일 패턴.
  - before: `_logger = new RalphLogger(Path.Combine(_repoDir, ".ralph-logs"));`
  - after:  `_logger = new RalphLogger(Path.Combine(_repoDir, RalphPaths.LogDir));`
- L40-41 코멘트는 그대로.

### 4.3 `Ralph.Tests/WorktreeBranchGuardTests.cs`

- L25, L35, L47, L55, L68, L70, L78, L93, L102, L116, L125 — `ralph/...` 와 `branch.ralph/....ralphManaged` 가 산재.
- 치환 가이드:
  - `"ralph/user-owned"` → `RalphPaths.GetBranchName("user-owned")`
  - `"refs/heads/ralph/user-owned"` → `$"refs/heads/{RalphPaths.GetBranchName("user-owned")}"`
  - `"branch.ralph/managed.ralphManaged"` → `RalphPaths.GetManagedConfigKey(RalphPaths.GetBranchName("managed"))`
- 단, 테스트 가독성을 위해 callsite마다 `var branch = RalphPaths.GetBranchName("user-owned");` 로 한 번 추출 후 재사용.

### 4.4 `Ralph.Tests/GitFixture.cs`

- **L27**:
  - before: `ValidationLogPath = Path.Combine(_root, "validation.jsonl");`
  - after:  `ValidationLogPath = Path.Combine(_root, RalphPaths.ValidationLedgerFileName);`
- **L68**:
  - before: `var branchName = $"ralph/{taskId}";`
  - after:  `var branchName = RalphPaths.GetBranchName(taskId);`

### 4.5 `Ralph.Tests/PlanGeneratorPromptTests.cs`

- **L60**:
  - before: `Assert.Contains(".ralph-worktrees/{taskId}/", prompt);`
  - after:  `Assert.Contains($"{RalphPaths.WorktreeDir}/{{taskId}}/", prompt);`
  - 단, 이 assertion은 prompt 안의 정확한 텍스트를 검증하므로, `PlanGenerator`가 prompt를 만들 때 동일한 합성을 수행하는지 확인 후 일치시킬 것.

### 4.6 `Ralph.Tests/CostTrackerConcurrencyTests.cs`

- L30, L61: 코멘트 / 에러 메시지 — 변경 불필요.

---

## 5. 검증 전략

### 5.1 정적 grep 체크

PR 자동 검사로 다음을 실행 (CI에 추가 권장):

```bash
# 1) 본체 코드에서 `.ralph-logs` 리터럴이 RalphPaths.cs와 허용된 prompt/comment 외에는 없어야 함
grep -rn "\.ralph-logs" Ralph/ \
    --exclude-dir=bin --exclude-dir=obj \
    | grep -v "Ralph/Services/RalphPaths.cs" \
    | grep -v "// " \
    | grep -v "/// " \
    | grep -v "Ralph/Services/PlanGenerator.cs"  # prompt 텍스트
# → 빈 결과여야 통과

# 2) `.ralph-worktrees`
grep -rn "\.ralph-worktrees" Ralph/ \
    --exclude-dir=bin --exclude-dir=obj \
    | grep -v "Ralph/Services/RalphPaths.cs" \
    | grep -v "// " \
    | grep -v "/// " \
    | grep -v "Ralph/Services/PlanGenerator.cs"
# → 빈 결과여야 통과

# 3) 브랜치 prefix `"ralph/"` (코드)
grep -rn '"ralph/' Ralph/ \
    --exclude-dir=bin --exclude-dir=obj \
    | grep -v "Ralph/Services/RalphPaths.cs"
# → 빈 결과여야 통과

# 4) ralphManaged config key
grep -rn "ralphManaged" Ralph/ \
    --exclude-dir=bin --exclude-dir=obj \
    | grep -v "Ralph/Services/RalphPaths.cs" \
    | grep -v "// " \
    | grep -v "/// "
# → 빈 결과여야 통과

# 5) 핵심 파일 basename
grep -rn '"state\.json"\|"cost\.jsonl"\|"validation\.jsonl"' Ralph/ \
    --exclude-dir=bin --exclude-dir=obj \
    | grep -v "Ralph/Services/RalphPaths.cs"
# → 빈 결과여야 통과
```

### 5.2 동일 grep을 테스트에도 적용

`Ralph.Tests/`도 같은 방식으로 검사하되, 테스트는 일부 placeholder 비교가 남을 수 있으므로
경고만 출력하고 실패는 시키지 않는다 (gradual cleanup).

### 5.3 동작 회귀 확인

치환 자체는 동작 변경이 없어야 한다. 다음으로 확인:

1. `dotnet build Ralph/Ralph.csproj` — 컴파일 성공.
2. `dotnet test Ralph.Tests/Ralph.Tests.csproj` — 기존 테스트 100% 통과.
   특히 `WorktreeBranchGuardTests`, `StateStoreTests`, `CostTrackerConcurrencyTests`,
   `ParallelExecutorTests` 가 통과하면 핵심 경로가 안전하다.
3. 수동 smoke: 임시 폴더에서 `ralph --plan PRD.md && ralph --run --dry-run` 한 번 돌려
   `.ralph-logs/`, `.ralph-worktrees/` 가 정상 생성되고 cleanup 되는지 확인.

### 5.4 회귀 방지용 단위 테스트 (권장 신규)

`Ralph.Tests/RalphPathsTests.cs` (신규, 작은 규모):

```csharp
[Fact] public void GetBranchName_uses_prefix() =>
    Assert.Equal("ralph/foo", RalphPaths.GetBranchName("foo"));

[Fact] public void GetManagedConfigKey_formats_template() =>
    Assert.Equal("branch.ralph/foo.ralphManaged",
        RalphPaths.GetManagedConfigKey("ralph/foo"));

[Fact] public void StateFileRelative_combines_logdir_and_filename() =>
    Assert.Equal(Path.Combine(".ralph-logs", "state.json"),
        RalphPaths.StateFileRelative);
```

값이 영구적으로 고정되어야 하는 “contract”라는 사실을 명시적으로 잠근다 — 누군가
`LogDir` 값을 무심코 바꾸면 이 테스트가 실패하면서 cleanup / detection이 silently
망가지기 전에 잡힌다.

---

## 6. 작업 순서 권장

1. `Ralph/Services/RalphPaths.cs` 신규 작성 + `RalphPathsTests.cs` 추가 → green.
2. 본체 파일 일괄 치환 (§3.1–§3.10) — 단일 커밋.
3. 테스트 파일 일괄 치환 (§4.1–§4.5) — 별도 커밋(선택).
4. CI 또는 로컬에서 §5.1 grep 스니펫 실행 후 빈 결과 확인.
5. `dotnet build` + `dotnet test` 전수 통과 후 PR 머지.

PR을 작게 유지하되 **본체 치환은 단일 커밋**으로 묶는다 (부분 치환 상태에서 머지되면
혼재된 표기로 오히려 grep 검증이 어려워진다).
