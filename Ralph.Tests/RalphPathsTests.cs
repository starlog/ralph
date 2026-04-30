using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

public class RalphPathsTests
{
    // ─── 상수 값 ────────────────────────────────────────────────────────────────

    [Fact]
    public void LogDir_is_dot_ralph_logs()
        => Assert.Equal(".ralph-logs", RalphPaths.LogDir);

    [Fact]
    public void WorktreeDir_is_dot_ralph_worktrees()
        => Assert.Equal(".ralph-worktrees", RalphPaths.WorktreeDir);

    [Fact]
    public void BranchPrefix_ends_with_slash()
        => Assert.Equal("ralph/", RalphPaths.BranchPrefix);

    [Fact]
    public void BranchListGlob_is_prefix_star()
        => Assert.Equal("ralph/*", RalphPaths.BranchListGlob);

    [Fact]
    public void StateFileName_is_state_json()
        => Assert.Equal("state.json", RalphPaths.StateFileName);

    [Fact]
    public void CostLedgerFileName_is_cost_jsonl()
        => Assert.Equal("cost.jsonl", RalphPaths.CostLedgerFileName);

    [Fact]
    public void ValidationLedgerFileName_is_validation_jsonl()
        => Assert.Equal("validation.jsonl", RalphPaths.ValidationLedgerFileName);

    [Fact]
    public void RollbackDirName_is_rollback()
        => Assert.Equal("rollback", RalphPaths.RollbackDirName);

    [Fact]
    public void PrePlanSnapshotFileName_is_pre_plan_json()
        => Assert.Equal("pre-plan.json", RalphPaths.PrePlanSnapshotFileName);

    [Fact]
    public void PostPlanSnapshotFileName_is_post_plan_json()
        => Assert.Equal("post-plan.json", RalphPaths.PostPlanSnapshotFileName);

    [Fact]
    public void UntrackedBackupDirName_is_untracked_backup()
        => Assert.Equal("untracked-backup", RalphPaths.UntrackedBackupDirName);

    // ─── const 복합 경로 ────────────────────────────────────────────────────────

    [Fact]
    public void StateFileRelativePath_is_composite_of_logdir_and_filename()
        => Assert.Equal(".ralph-logs/state.json", RalphPaths.StateFileRelativePath);

    [Fact]
    public void CostLedgerRelativePath_is_composite_of_logdir_and_filename()
        => Assert.Equal(".ralph-logs/cost.jsonl", RalphPaths.CostLedgerRelativePath);

    [Fact]
    public void ValidationLedgerRelativePath_is_composite_of_logdir_and_filename()
        => Assert.Equal(".ralph-logs/validation.jsonl", RalphPaths.ValidationLedgerRelativePath);

    // ─── ManagedConfigKeyTemplate 포맷팅 ────────────────────────────────────────

    [Fact]
    public void ManagedConfigKeyTemplate_formats_branch_name_correctly()
    {
        var result = string.Format(RalphPaths.ManagedConfigKeyTemplate, "ralph/foo");
        Assert.Equal("branch.ralph/foo.ralphManaged", result);
    }

    [Fact]
    public void ManagedConfigKeyTemplate_formats_simple_branch_name()
    {
        var result = string.Format(RalphPaths.ManagedConfigKeyTemplate, "ralph/my-task");
        Assert.Equal("branch.ralph/my-task.ralphManaged", result);
    }

    // ─── 정적 메서드 ────────────────────────────────────────────────────────────

    [Fact]
    public void GetBranchName_prepends_prefix()
        => Assert.Equal("ralph/task-impl", RalphPaths.GetBranchName("task-impl"));

    [Fact]
    public void GetBranchName_empty_id_returns_prefix_only()
        => Assert.Equal("ralph/", RalphPaths.GetBranchName(""));

    [Fact]
    public void GetManagedConfigKey_returns_expected_key()
        => Assert.Equal("branch.ralph/foo.ralphManaged", RalphPaths.GetManagedConfigKey("ralph/foo"));

    [Fact]
    public void GetManagedConfigKey_matches_template_format_result()
    {
        var branchName = "ralph/some-task";
        var fromTemplate = string.Format(RalphPaths.ManagedConfigKeyTemplate, branchName);
        var fromMethod = RalphPaths.GetManagedConfigKey(branchName);
        Assert.Equal(fromTemplate, fromMethod);
    }

    // ─── 속성(Path.Combine 경로) ─────────────────────────────────────────────────

    [Fact]
    public void StateFileRelative_combines_logdir_and_statefile()
        => Assert.Equal(Path.Combine(".ralph-logs", "state.json"), RalphPaths.StateFileRelative);

    [Fact]
    public void CostLedgerRelative_combines_logdir_and_costfile()
        => Assert.Equal(Path.Combine(".ralph-logs", "cost.jsonl"), RalphPaths.CostLedgerRelative);

    [Fact]
    public void ValidationLedgerRelative_combines_logdir_and_validationfile()
        => Assert.Equal(Path.Combine(".ralph-logs", "validation.jsonl"), RalphPaths.ValidationLedgerRelative);

    [Fact]
    public void LogDirUnder_combines_parent_and_logdir()
    {
        var parent = Path.Combine(Path.GetTempPath(), "ralph-test");
        var result = RalphPaths.LogDirUnder(parent);
        Assert.Equal(Path.Combine(parent, ".ralph-logs"), result);
    }

    // ─── 일관성: const 경로와 Property 경로가 동일 세그먼트를 가리킴 ───────────────

    [Fact]
    public void StateFileRelativePath_and_StateFileRelative_refer_to_same_segments()
    {
        // const는 '/' 구분자, Property는 Path.Combine — OS별 구분자 차이를 허용하고 세그먼트만 비교
        var parts = RalphPaths.StateFileRelativePath.Split('/');
        Assert.Equal(RalphPaths.LogDir, parts[0]);
        Assert.Equal(RalphPaths.StateFileName, parts[1]);
    }
}
