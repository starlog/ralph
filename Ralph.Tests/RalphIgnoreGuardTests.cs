using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

/// <summary>
/// .ralph-smoke가 gitlink로 추적되어 batch 전체 rebase preflight가 깨지던 회귀를
/// 방지하는 가드의 단위 테스트.
/// </summary>
public class RalphIgnoreGuardTests
{
    /// <summary>최소 git repo 한 개를 임시 디렉터리에 만들어 반환.</summary>
    private static async Task<string> MakeRepoAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ralph-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var git = new GitService();

        async Task Run(params string[] args)
        {
            var (exit, output) = await git.RunAsync(args, dir);
            if (exit != 0)
                throw new InvalidOperationException(
                    $"git {string.Join(' ', args)} 실패 in {dir}: {output.Trim()}");
        }

        await Run("init", "-b", "main");
        await Run("config", "user.email", "test@ralph.test");
        await Run("config", "user.name", "Ralph Test");
        await Run("config", "commit.gpgsign", "false");
        // 첫 커밋이 있어야 ls-files 등이 의미 있게 동작.
        await File.WriteAllTextAsync(Path.Combine(dir, "README.md"), "init\n");
        await Run("add", "README.md");
        await Run("commit", "-m", "init");
        return dir;
    }

    [Fact]
    public async Task EnsureAsync_creates_exclude_lines_when_missing()
    {
        var repo = await MakeRepoAsync();
        try
        {
            var git = new GitService();
            await RalphIgnoreGuard.EnsureAsync(git, repo, RalphLogger.Null);

            var excludePath = Path.Combine(repo, ".git", "info", "exclude");
            Assert.True(File.Exists(excludePath));
            var content = await File.ReadAllTextAsync(excludePath);
            Assert.Contains(".ralph-logs/", content);
            Assert.Contains(".ralph-worktrees/", content);
            Assert.Contains(".ralph-smoke/", content);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EnsureAsync_is_idempotent()
    {
        var repo = await MakeRepoAsync();
        try
        {
            var git = new GitService();
            await RalphIgnoreGuard.EnsureAsync(git, repo, RalphLogger.Null);
            var excludePath = Path.Combine(repo, ".git", "info", "exclude");
            var first = await File.ReadAllTextAsync(excludePath);

            await RalphIgnoreGuard.EnsureAsync(git, repo, RalphLogger.Null);
            var second = await File.ReadAllTextAsync(excludePath);

            Assert.Equal(first, second);
            // 라인 카운트도 안 늘어남 — 중복 append 안 함.
            var lines = (await File.ReadAllLinesAsync(excludePath))
                .Count(l => l.Trim() == ".ralph-smoke/");
            Assert.Equal(1, lines);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EnsureAsync_preserves_existing_exclude_content()
    {
        var repo = await MakeRepoAsync();
        try
        {
            var infoDir = Path.Combine(repo, ".git", "info");
            Directory.CreateDirectory(infoDir);
            var excludePath = Path.Combine(infoDir, "exclude");
            await File.WriteAllTextAsync(excludePath, "# user comment\nmy-secret-dir/\n");

            var git = new GitService();
            await RalphIgnoreGuard.EnsureAsync(git, repo, RalphLogger.Null);

            var content = await File.ReadAllTextAsync(excludePath);
            Assert.Contains("# user comment", content);
            Assert.Contains("my-secret-dir/", content);
            Assert.Contains(".ralph-smoke/", content);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EnsureAsync_throws_when_smoke_dir_tracked_as_gitlink()
    {
        var repo = await MakeRepoAsync();
        try
        {
            var git = new GitService();
            // .ralph-smoke를 별도 worktree로 add — 이 디렉터리는 자체 .git을 가져
            // 호스트 repo에서 add 시 gitlink가 된다 (실제 버그 재현).
            var smokePath = Path.Combine(repo, ".ralph-smoke");
            var (e1, o1) = await git.RunAsync(
                ["worktree", "add", "--detach", smokePath, "main"], repo);
            Assert.Equal(0, e1);

            // 호스트 repo에서 .ralph-smoke를 staged.
            var (e2, o2) = await git.RunAsync(["add", ".ralph-smoke"], repo);
            Assert.Equal(0, e2);
            var (e3, o3) = await git.RunAsync(
                ["commit", "-m", "accidentally tracked"], repo);
            Assert.Equal(0, e3);

            var ex = await Assert.ThrowsAsync<RalphUserException>(() =>
                RalphIgnoreGuard.EnsureAsync(git, repo, RalphLogger.Null));

            Assert.Contains(".ralph-smoke", ex.Message);
            Assert.Contains("git rm --cached", ex.Message);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EnsureAsync_throws_when_logs_dir_has_tracked_file()
    {
        var repo = await MakeRepoAsync();
        try
        {
            var git = new GitService();
            var logsDir = Path.Combine(repo, ".ralph-logs");
            Directory.CreateDirectory(logsDir);
            await File.WriteAllTextAsync(Path.Combine(logsDir, "stale.log"), "x");

            var (e1, _) = await git.RunAsync(["add", ".ralph-logs/stale.log"], repo);
            Assert.Equal(0, e1);
            var (e2, _) = await git.RunAsync(["commit", "-m", "oops"], repo);
            Assert.Equal(0, e2);

            await Assert.ThrowsAsync<RalphUserException>(() =>
                RalphIgnoreGuard.EnsureAsync(git, repo, RalphLogger.Null));
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EnsureAsync_passes_when_paths_only_exist_as_untracked()
    {
        var repo = await MakeRepoAsync();
        try
        {
            // untracked 디렉터리 — 가드는 통과해야 한다.
            Directory.CreateDirectory(Path.Combine(repo, ".ralph-logs"));
            Directory.CreateDirectory(Path.Combine(repo, ".ralph-worktrees"));

            var git = new GitService();
            await RalphIgnoreGuard.EnsureAsync(git, repo, RalphLogger.Null);
            // throw 없으면 성공.
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { }
        }
    }
}
