using Xunit;

namespace Ralph.Tests;

/// <summary>
/// CommitChangesAsync의 staging 전략 통합 테스트. 실제 git fixture를 써서
/// declared-only / -A fallback / sensitive 거부 / missing skip 동작을 확인한다.
/// </summary>
public class GitServiceCommitTests
{
    private static async Task<int> CommitCount(GitFixture f) =>
        int.Parse((await f.Git.RunAsync(["rev-list", "--count", "HEAD"], f.RepoDir)).Output.Trim());

    private static async Task<List<string>> ChangedInLastCommit(GitFixture f)
    {
        var (_, output) = await f.Git.RunAsync(
            ["diff-tree", "--no-commit-id", "--name-only", "-r", "HEAD"], f.RepoDir);
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())];
    }

    [Fact]
    public async Task DeclaredOnly_stages_only_listed_files()
    {
        using var f = new GitFixture();
        await f.InitAsync();
        await f.WriteFileAsync("seed.txt", "seed");
        await f.CommitAllAsync("seed");

        // 세 파일 변경: 둘은 declared, 하나는 unrelated (다른 task에서 흘러든 변경 시뮬레이션)
        await f.WriteFileAsync("declared-a.py", "a=1\n");
        await f.WriteFileAsync("declared-b.py", "b=2\n");
        await f.WriteFileAsync("__pycache__/leak.pyc", "binary");

        var declared = new[] { "declared-a.py", "declared-b.py" };
        await f.Git.CommitChangesAsync(
            "task-x", "task title", "[Task #{taskId}] {taskTitle}",
            workingDirectory: f.RepoDir, silent: true, declaredFiles: declared);

        var changed = await ChangedInLastCommit(f);
        Assert.Contains("declared-a.py", changed);
        Assert.Contains("declared-b.py", changed);
        Assert.DoesNotContain("__pycache__/leak.pyc", changed);

        // unrelated 파일은 working tree에 untracked로 그대로 남아 있어야 함
        var (_, status) = await f.Git.RunAsync(["status", "--porcelain"], f.RepoDir);
        Assert.Contains("__pycache__", status); // git이 디렉터리만 표시하거나 파일을 표시하거나 둘 다 OK
        Assert.True(File.Exists(Path.Combine(f.RepoDir, "__pycache__", "leak.pyc")));
    }

    [Fact]
    public async Task Empty_declared_falls_back_to_add_all()
    {
        using var f = new GitFixture();
        await f.InitAsync();
        await f.WriteFileAsync("seed.txt", "seed");
        await f.CommitAllAsync("seed");

        await f.WriteFileAsync("anything.py", "x=1\n");
        await f.WriteFileAsync("more.py", "y=2\n");

        // null declared → -A fallback
        await f.Git.CommitChangesAsync(
            "task-y", "title", "[Task #{taskId}] {taskTitle}",
            workingDirectory: f.RepoDir, silent: true, declaredFiles: null);

        var changed = await ChangedInLastCommit(f);
        Assert.Contains("anything.py", changed);
        Assert.Contains("more.py", changed);
    }

    [Fact]
    public async Task Empty_collection_also_falls_back_to_add_all()
    {
        using var f = new GitFixture();
        await f.InitAsync();
        await f.WriteFileAsync("seed.txt", "seed");
        await f.CommitAllAsync("seed");

        await f.WriteFileAsync("foo.py", "x=1\n");

        await f.Git.CommitChangesAsync(
            "task-z", "title", "[Task #{taskId}] {taskTitle}",
            workingDirectory: f.RepoDir, silent: true,
            declaredFiles: Array.Empty<string>());

        var changed = await ChangedInLastCommit(f);
        Assert.Contains("foo.py", changed);
    }

    [Fact]
    public async Task Declared_file_missing_on_disk_is_silently_skipped()
    {
        using var f = new GitFixture();
        await f.InitAsync();
        await f.WriteFileAsync("seed.txt", "seed");
        await f.CommitAllAsync("seed");

        await f.WriteFileAsync("real.py", "x=1\n");
        // ghost.py 는 declared지만 디스크에 없음 → silently skip, real.py는 정상 commit

        var declared = new[] { "real.py", "ghost.py" };
        await f.Git.CommitChangesAsync(
            "task-m", "title", "[Task #{taskId}] {taskTitle}",
            workingDirectory: f.RepoDir, silent: true, declaredFiles: declared);

        var changed = await ChangedInLastCommit(f);
        Assert.Contains("real.py", changed);
        Assert.DoesNotContain("ghost.py", changed);
    }

    [Fact]
    public async Task Declared_sensitive_file_is_refused()
    {
        using var f = new GitFixture();
        await f.InitAsync();
        await f.WriteFileAsync("seed.txt", "seed");
        await f.CommitAllAsync("seed");

        await f.WriteFileAsync("safe.py", "x=1\n");
        await f.WriteFileAsync(".env", "SECRET=hunter2\n");
        await f.WriteFileAsync("credentials.json", "{}");

        var declared = new[] { "safe.py", ".env", "credentials.json" };
        await f.Git.CommitChangesAsync(
            "task-s", "title", "[Task #{taskId}] {taskTitle}",
            workingDirectory: f.RepoDir, silent: true, declaredFiles: declared);

        var changed = await ChangedInLastCommit(f);
        Assert.Contains("safe.py", changed);
        Assert.DoesNotContain(".env", changed);
        Assert.DoesNotContain("credentials.json", changed);
    }

    [Fact]
    public async Task Absolute_path_under_worktree_is_normalized()
    {
        using var f = new GitFixture();
        await f.InitAsync();
        await f.WriteFileAsync("seed.txt", "seed");
        await f.CommitAllAsync("seed");

        await f.WriteFileAsync("subdir/abs.py", "x=1\n");
        var absolute = Path.Combine(f.RepoDir, "subdir", "abs.py");

        await f.Git.CommitChangesAsync(
            "task-abs", "title", "[Task #{taskId}] {taskTitle}",
            workingDirectory: f.RepoDir, silent: true, declaredFiles: new[] { absolute });

        var changed = await ChangedInLastCommit(f);
        Assert.Contains("subdir/abs.py", changed);
    }

    [Fact]
    public async Task Path_outside_worktree_is_skipped()
    {
        using var f = new GitFixture();
        await f.InitAsync();
        await f.WriteFileAsync("seed.txt", "seed");
        await f.CommitAllAsync("seed");

        await f.WriteFileAsync("inside.py", "x=1\n");
        // /tmp/elsewhere — worktree 밖
        var outside = Path.Combine(Path.GetTempPath(), $"ralph-outside-{Guid.NewGuid():N}.py");

        await f.Git.CommitChangesAsync(
            "task-out", "title", "[Task #{taskId}] {taskTitle}",
            workingDirectory: f.RepoDir, silent: true,
            declaredFiles: new[] { "inside.py", outside });

        var changed = await ChangedInLastCommit(f);
        Assert.Contains("inside.py", changed);
        Assert.Single(changed);
    }
}
