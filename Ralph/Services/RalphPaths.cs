using System.Globalization;

namespace Ralph.Services;

/// <summary>
/// Ralph가 사용하는 디렉토리·파일 basename·브랜치 namespace·git config 키 템플릿을
/// 한 곳에 모은 매직 스트링 카탈로그. 모든 멤버는 cwd/repoRoot에 가정하지 않은
/// 상대 segment 또는 템플릿이며, 절대 경로 합성과 IO는 호출자 책임이다.
/// </summary>
public static class RalphPaths
{
    /// <summary>로그·state·cost·validation·rollback이 보관되는 디렉토리.</summary>
    public const string LogDir = ".ralph-logs";

    /// <summary>worktree 베이스 디렉토리 (`<repoRoot>/.ralph-worktrees/<taskId>`).</summary>
    public const string WorktreeDir = ".ralph-worktrees";

    /// <summary>ralph 소유 브랜치 namespace prefix (마지막 `/` 포함).</summary>
    public const string BranchPrefix = "ralph/";

    /// <summary>`git branch --list <glob>` 인자.</summary>
    public const string BranchListGlob = BranchPrefix + "*";

    /// <summary>사용자 브랜치 보호용 git config 마커 템플릿. <see cref="GetManagedConfigKey"/>로 합성.</summary>
    public const string ManagedConfigKeyTemplate = "branch.{0}.ralphManaged";

    /// <summary>mutable 진행 상태 파일 basename (LogDir과 결합).</summary>
    public const string StateFileName = "state.json";

    /// <summary>누적 비용 ledger basename.</summary>
    public const string CostLedgerFileName = "cost.jsonl";

    /// <summary>파일 검증 ledger basename.</summary>
    public const string ValidationLedgerFileName = "validation.jsonl";

    /// <summary>rollback 스냅샷 sub-directory (LogDir 산하).</summary>
    public const string RollbackDirName = "rollback";

    /// <summary>--plan 직전 스냅샷 basename.</summary>
    public const string PrePlanSnapshotFileName = "pre-plan.json";

    /// <summary>--plan 직후 스냅샷 basename.</summary>
    public const string PostPlanSnapshotFileName = "post-plan.json";

    /// <summary>머지 시 untracked 충돌 파일을 옮기는 백업 sub-directory (LogDir 산하).</summary>
    public const string UntrackedBackupDirName = "untracked-backup";

    /// <summary>const string 컨텍스트(예: 메서드 default arg)에서 사용 가능한 `.ralph-logs/state.json` 표기.</summary>
    public const string StateFileRelativePath = LogDir + "/" + StateFileName;

    /// <summary>const string 컨텍스트에서 사용 가능한 `.ralph-logs/cost.jsonl` 표기.</summary>
    public const string CostLedgerRelativePath = LogDir + "/" + CostLedgerFileName;

    /// <summary>const string 컨텍스트에서 사용 가능한 `.ralph-logs/validation.jsonl` 표기.</summary>
    public const string ValidationLedgerRelativePath = LogDir + "/" + ValidationLedgerFileName;

    /// <summary><c>"ralph/" + taskId</c> — 브랜치 이름 합성.</summary>
    public static string GetBranchName(string taskId) => BranchPrefix + taskId;

    /// <summary><c>"branch.{branchName}.ralphManaged"</c> config key 합성.</summary>
    public static string GetManagedConfigKey(string branchName)
        => string.Format(CultureInfo.InvariantCulture, ManagedConfigKeyTemplate, branchName);

    /// <summary>호출자 cwd 기준의 `.ralph-logs/state.json` 상대 경로 (Path.Combine).</summary>
    public static string StateFileRelative => Path.Combine(LogDir, StateFileName);

    /// <summary>`.ralph-logs/cost.jsonl` 상대 경로.</summary>
    public static string CostLedgerRelative => Path.Combine(LogDir, CostLedgerFileName);

    /// <summary>`.ralph-logs/validation.jsonl` 상대 경로.</summary>
    public static string ValidationLedgerRelative => Path.Combine(LogDir, ValidationLedgerFileName);

    /// <summary>cost.jsonl 기록 실패 시 fallback ledger basename.</summary>
    public const string CostFailuresLedgerFileName = "cost-failures.jsonl";

    /// <summary>const string 컨텍스트에서 사용 가능한 `.ralph-logs/cost-failures.jsonl` 표기.</summary>
    public const string CostFailuresLedgerRelativePath = LogDir + "/" + CostFailuresLedgerFileName;

    /// <summary>`.ralph-logs/cost-failures.jsonl` 상대 경로 (Path.Combine).</summary>
    public static string CostFailuresLedgerRelative => Path.Combine(LogDir, CostFailuresLedgerFileName);

    /// <summary>주어진 디렉토리 산하의 `.ralph-logs` 경로 합성.</summary>
    public static string LogDirUnder(string parentDir) => Path.Combine(parentDir, LogDir);
}
