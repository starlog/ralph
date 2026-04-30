using Ralph.Services;

namespace Ralph.Tests;

/// <summary>
/// 통합 테스트용 임시 git repo + worktree 셋업. 각 fixture는 unique temp dir 사용 → 병렬 안전.
/// production code의 CreateWorktreeAsync는 CWD 의존이 있어, 테스트에서는 GitService에
/// workingDirectory를 명시적으로 넘기는 직접 호출로 worktree를 만든다.
/// 그 후 NormalizeTasksJsonAsync/ValidateModifiedFilesAsync/AdvanceWorktreeOntoBaseAsync는
/// 모두 worktreePath를 workingDirectory로 사용하므로 CWD 영향 없음.
/// </summary>
internal sealed class GitFixture : IDisposable
{
    public string RepoDir { get; }
    public string WorktreeBase { get; }
    public GitService Git { get; }
    public WorktreeService Worktree { get; }
    public string ValidationLogPath { get; }

    private readonly string _root;

    public GitFixture()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ralph-fix-{Guid.NewGuid():N}");
        RepoDir = Path.Combine(_root, "main");
        WorktreeBase = Path.Combine(_root, "worktrees");
        ValidationLogPath = Path.Combine(_root, "validation.jsonl");
        Directory.CreateDirectory(RepoDir);
        Directory.CreateDirectory(WorktreeBase);
        Git = new GitService();
        Worktree = new WorktreeService(Git, WorktreeBase);
    }

    private async Task RunAsync(params string[] args)
    {
        var (exit, output) = await Git.RunAsync(args, RepoDir);
        if (exit != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed in {RepoDir}: {output.Trim()}");
    }

    public async Task InitAsync(string defaultBranch = "main")
    {
        await RunAsync("init", "-b", defaultBranch);
        await RunAsync("config", "user.email", "test@ralph.test");
        await RunAsync("config", "user.name", "Ralph Test");
        // GPG 서명 비활성 (테스트 호스트에 키가 없을 수 있음)
        await RunAsync("config", "commit.gpgsign", "false");
    }

    public async Task WriteFileAsync(string relPath, string content)
    {
        var path = Path.Combine(RepoDir, relPath);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, content);
    }

    public async Task CommitAllAsync(string msg)
    {
        await RunAsync("add", "-A");
        await RunAsync("commit", "-m", msg);
    }

    public async Task SetupWorktreeAsync(string taskId, string baseBranch = "main")
    {
        var worktreePath = Path.Combine(WorktreeBase, taskId);
        var branchName = $"ralph/{taskId}";
        await RunAsync("worktree", "add", "-b", branchName, worktreePath, baseBranch);
    }

    public string ReadInWorktree(string taskId, string relPath) =>
        File.ReadAllText(Path.Combine(WorktreeBase, taskId, relPath));

    public bool FileExistsInWorktree(string taskId, string relPath) =>
        File.Exists(Path.Combine(WorktreeBase, taskId, relPath));

    public async Task WriteInWorktreeAsync(string taskId, string relPath, string content)
    {
        var path = Path.Combine(WorktreeBase, taskId, relPath);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, content);
    }

    public async Task CommitInWorktreeAsync(string taskId, string msg)
    {
        var path = Path.Combine(WorktreeBase, taskId);
        var (e1, o1) = await Git.RunAsync(["add", "-A"], path);
        if (e1 != 0) throw new InvalidOperationException($"add failed: {o1.Trim()}");
        var (e2, o2) = await Git.RunAsync(["commit", "-m", msg], path);
        if (e2 != 0) throw new InvalidOperationException($"commit failed: {o2.Trim()}");
    }

    /// <summary>
    /// CleanupWorktree/CleanupAll/CreateWorktree처럼 CWD 의존 메서드를 테스트할 때
    /// process CWD를 RepoDir로 잠깐 바꾸기 위한 헬퍼. xUnit collection으로 직렬화 필수.
    /// </summary>
    public IDisposable UseRepoCwd() => new CwdScope(RepoDir);

    private sealed class CwdScope : IDisposable
    {
        private readonly string _prev;
        public CwdScope(string newCwd)
        {
            _prev = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(newCwd);
        }
        public void Dispose()
        {
            try { Directory.SetCurrentDirectory(_prev); } catch { }
        }
    }

    public void Dispose()
    {
        // worktree 디렉터리는 git locking 때문에 즉시 삭제가 실패할 수 있음 — best-effort.
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}

