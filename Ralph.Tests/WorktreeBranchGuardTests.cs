using Xunit;

namespace Ralph.Tests;

/// <summary>
/// CleanupWorktreeAsync / CleanupAllAsync / CreateWorktreeAsync는 process CWD를 따라가는
/// git 호출을 하므로, 이 테스트들은 [Collection("worktree-cwd")]로 직렬화하고 fix.UseRepoCwd()로
/// CWD를 잠시 RepoDir로 옮겨 검증한다.
///
/// 회귀 방어 대상: 사용자가 직접 만든 ralph/* 브랜치를 silent 삭제하던 버그
/// (ralphManaged config 마커 미존재 + worktree 연결도 없으면 보존되어야 함).
/// </summary>
[Collection("cost")]
public class WorktreeBranchGuardTests
{
    [Fact]
    public async Task CleanupWorktree_preserves_user_owned_branch_with_same_name()
    {
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("seed.txt", "S");
        await fix.CommitAllAsync("initial");

        // ralphManaged 마커 없이 사용자가 직접 만든 ralph/* 브랜치
        var (e, _) = await fix.Git.RunAsync(["branch", "ralph/user-owned"], fix.RepoDir);
        Assert.Equal(0, e);

        using (fix.UseRepoCwd())
        {
            var ok = await fix.Worktree.CleanupWorktreeAsync("user-owned");
            Assert.True(ok); // 디렉터리는 없으니 cleanup 자체는 성공
        }

        var (showExit, _) = await fix.Git.RunAsync(
            ["show-ref", "--verify", "--quiet", "refs/heads/ralph/user-owned"], fix.RepoDir);
        Assert.Equal(0, showExit); // 브랜치는 보존되어 있어야 함
    }

    [Fact]
    public async Task CleanupAll_skips_branches_without_ralph_marker()
    {
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("seed.txt", "S");
        await fix.CommitAllAsync("initial");

        await fix.Git.RunAsync(["branch", "ralph/personal"], fix.RepoDir);

        using (fix.UseRepoCwd())
        {
            await fix.Worktree.CleanupAllAsync();
        }

        var (showExit, _) = await fix.Git.RunAsync(
            ["show-ref", "--verify", "--quiet", "refs/heads/ralph/personal"], fix.RepoDir);
        Assert.Equal(0, showExit); // 보존
    }

    [Fact]
    public async Task CleanupAll_deletes_branches_with_ralph_marker()
    {
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("seed.txt", "S");
        await fix.CommitAllAsync("initial");

        // ralph 소유 시뮬레이션: 브랜치 + 마커
        await fix.Git.RunAsync(["branch", "ralph/managed"], fix.RepoDir);
        await fix.Git.RunAsync(
            ["config", "branch.ralph/managed.ralphManaged", "true"], fix.RepoDir);

        using (fix.UseRepoCwd())
        {
            await fix.Worktree.CleanupAllAsync();
        }

        var (showExit, _) = await fix.Git.RunAsync(
            ["show-ref", "--verify", "--quiet", "refs/heads/ralph/managed"], fix.RepoDir);
        Assert.NotEqual(0, showExit); // 삭제되어야 함
    }

    [Fact]
    public async Task CleanupWorktree_with_marker_deletes_branch()
    {
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("seed.txt", "S");
        await fix.CommitAllAsync("initial");

        // 정상 워크트리 + 마커 (CreateWorktreeAsync가 production에서 박는 것과 동일)
        await fix.SetupWorktreeAsync("t1");
        await fix.Git.RunAsync(
            ["config", "branch.ralph/t1.ralphManaged", "true"], fix.RepoDir);

        using (fix.UseRepoCwd())
        {
            var ok = await fix.Worktree.CleanupWorktreeAsync("t1");
            Assert.True(ok);
        }

        var (showExit, _) = await fix.Git.RunAsync(
            ["show-ref", "--verify", "--quiet", "refs/heads/ralph/t1"], fix.RepoDir);
        Assert.NotEqual(0, showExit);
    }

    [Fact]
    public async Task CleanupWorktree_without_marker_but_active_worktree_treats_as_managed()
    {
        // legacy 마이그레이션 케이스: 마커 없이 워크트리만 있는 경우 (이전 버전 ralph가 만든 것),
        // worktree 연결로 소유권 추론해 정리 가능해야 함.
        using var fix = new GitFixture();
        await fix.InitAsync();
        await fix.WriteFileAsync("seed.txt", "S");
        await fix.CommitAllAsync("initial");

        await fix.SetupWorktreeAsync("legacy"); // 마커 없이 ralph/legacy 생성

        using (fix.UseRepoCwd())
        {
            var ok = await fix.Worktree.CleanupWorktreeAsync("legacy");
            Assert.True(ok);
        }

        var (showExit, _) = await fix.Git.RunAsync(
            ["show-ref", "--verify", "--quiet", "refs/heads/ralph/legacy"], fix.RepoDir);
        Assert.NotEqual(0, showExit); // legacy fallback으로 삭제 성공
    }
}
