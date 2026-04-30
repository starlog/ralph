using System.Text.Json;
using Ralph.Models;
using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

/// <summary>
/// fix2 #7: --auto-rollback-on-smoke-fail 자동 롤백 기능 통합 테스트.
///
/// 커버 케이스:
///   1. opt-in OFF + smoke 실패 → 자동 revert 미발생 (현행 동작 유지)
///   2. opt-in ON + smoke 실패 + 워킹트리 클린 → batch revert + state.json pending 복귀
///   3. opt-in ON + smoke 실패 + 워킹트리 dirty → 자동 롤백 보류, done=true 유지
///   4. opt-in ON + smoke 성공 → 정상 진행 (변동 없음)
///
/// 구현 노트:
///   - tasks.json을 초기 커밋에 포함하고 .gitignore로 .ralph-logs/ 제외 → 안전 체크 시 clean tree 유지.
///   - smokeTestCommandOverride("false"/"exit 1")로 smoke 실패를 강제.
///   - ParallelExecutor는 2개 독립 task(병렬 batch 경로)로 smoke 실행 경로를 통과.
/// </summary>
[Collection("cost")]
public class AutoRollbackTests : IDisposable
{
    private readonly string _root;
    private readonly string _repoDir;
    private readonly string _worktreeBase;
    private readonly string _tasksFile;
    private readonly string _origCwd;
    private readonly RalphLogger _logger;

    // POSIX: false/true, Windows: exit 1/exit 0
    private static string FailSmokeCmd => OperatingSystem.IsWindows() ? "exit 1" : "false";
    private static string PassSmokeCmd => OperatingSystem.IsWindows() ? "exit 0" : "true";

    public AutoRollbackTests()
    {
        _origCwd = Directory.GetCurrentDirectory();
        _root = Path.Combine(Path.GetTempPath(), $"ralph-arb-{Guid.NewGuid():N}");
        _repoDir = Path.Combine(_root, "repo");
        _worktreeBase = Path.Combine(_root, "worktrees");
        _tasksFile = Path.Combine(_repoDir, "tasks.json");

        Directory.CreateDirectory(_repoDir);
        Directory.CreateDirectory(_worktreeBase);

        // ParallelExecutor 내 git/cost 호출이 cwd에 의존하므로 _repoDir로 전환.
        Directory.SetCurrentDirectory(_repoDir);

        _logger = new RalphLogger(Path.Combine(_repoDir, ".ralph-logs"));
    }

    public void Dispose()
    {
        try { _logger.Dispose(); } catch { }
        try { Directory.SetCurrentDirectory(_origCwd); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ─── 케이스 1: opt-in OFF + smoke 실패 → 자동 revert 미발생 ────────────────────

    [Fact]
    public async Task OptInOff_SmokeFails_NoAutoRevert()
    {
        await InitRepoAsync();
        var manager = await SetupTwoTasksAsync();

        var (git, worktree) = MakeGitServices();
        var executor = new ParallelExecutor(
            manager, MakeRunner(), git, worktree, _logger,
            new RunOptions(
                TasksFile: _tasksFile, ModelOverride: "opus",
                NoSmokeTest: false, SmokeTestCommandOverride: FailSmokeCmd,
                AutoRollbackOnSmokeFail: false)); // opt-in OFF

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var exit = await executor.RunAsync(maxConcurrent: 4, cts.Token);

        // smoke 실패 → 비-0 종료
        Assert.NotEqual(0, exit);

        // 자동 롤백 비활성 → 파일이 base에 그대로 존재
        Assert.True(File.Exists(Path.Combine(_repoDir, "a.txt")));
        Assert.True(File.Exists(Path.Combine(_repoDir, "b.txt")));

        // state.json: done=true 유지 (롤백 없음)
        await manager.ReloadAsync();
        Assert.True(manager.IsDone("A"), "opt-in OFF → smoke 실패 후에도 A는 done=true 유지");
        Assert.True(manager.IsDone("B"), "opt-in OFF → smoke 실패 후에도 B는 done=true 유지");

        // git log 최신 커밋: revert/rollback 포함 안 됨
        var (_, logOut) = await git.RunAsync(["log", "--oneline", "-1"], _repoDir);
        Assert.False(
            logOut.Contains("revert", StringComparison.OrdinalIgnoreCase) ||
            logOut.Contains("rollback", StringComparison.OrdinalIgnoreCase),
            $"opt-in OFF → revert 커밋 없어야 함. 최신 커밋: {logOut.Trim()}");
    }

    // ─── 케이스 2: opt-in ON + smoke 실패 + 클린 tree → batch revert + pending 복귀 ──

    [Fact]
    public async Task OptInOn_SmokeFails_CleanTree_BatchRevertAndStatePending()
    {
        await InitRepoAsync();
        var manager = await SetupTwoTasksAsync();

        var (git, worktree) = MakeGitServices();
        var executor = new ParallelExecutor(
            manager, MakeRunner(), git, worktree, _logger,
            new RunOptions(
                TasksFile: _tasksFile, ModelOverride: "opus",
                NoSmokeTest: false, SmokeTestCommandOverride: FailSmokeCmd,
                AutoRollbackOnSmokeFail: true)); // opt-in ON

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var exit = await executor.RunAsync(maxConcurrent: 4, cts.Token);

        // smoke 실패 → batch는 항상 비-0 (revert 성공해도 smoke 자체는 실패)
        Assert.NotEqual(0, exit);

        // state.json: 두 task 모두 pending으로 복귀
        await manager.ReloadAsync();
        Assert.False(manager.IsDone("A"), "자동 rollback 후 A는 pending 복귀해야 한다");
        Assert.False(manager.IsDone("B"), "자동 rollback 후 B는 pending 복귀해야 한다");

        // git log: 최신 커밋이 revert/rollback 커밋이어야 함
        var (_, logOut) = await git.RunAsync(["log", "--oneline", "-1"], _repoDir);
        Assert.True(
            logOut.Contains("revert", StringComparison.OrdinalIgnoreCase) ||
            logOut.Contains("rollback", StringComparison.OrdinalIgnoreCase),
            $"자동 rollback 후 최신 커밋이 revert/rollback이어야 함: {logOut.Trim()}");
    }

    // ─── 케이스 3: opt-in ON + smoke 실패 + dirty tree → 자동 롤백 보류 ───────────

    [Fact]
    public async Task OptInOn_SmokeFails_DirtyTree_RollbackHeld()
    {
        await InitRepoAsync();
        var manager = await SetupTwoTasksAsync();

        // working tree를 dirty하게: untracked 파일 추가 (`.ralph-logs/`는 .gitignore 제외)
        await File.WriteAllTextAsync(Path.Combine(_repoDir, "dirty.txt"), "dirty");

        var (git, worktree) = MakeGitServices();
        var executor = new ParallelExecutor(
            manager, MakeRunner(), git, worktree, _logger,
            new RunOptions(
                TasksFile: _tasksFile, ModelOverride: "opus",
                NoSmokeTest: false, SmokeTestCommandOverride: FailSmokeCmd,
                AutoRollbackOnSmokeFail: true)); // opt-in ON

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var exit = await executor.RunAsync(maxConcurrent: 4, cts.Token);

        // smoke 실패 → 비-0
        Assert.NotEqual(0, exit);

        // dirty tree로 인해 안전 체크 실패 → 자동 롤백 보류 → task들은 done=true 유지
        await manager.ReloadAsync();
        Assert.True(manager.IsDone("A"), "dirty tree로 rollback 보류 → A는 done=true 유지");
        Assert.True(manager.IsDone("B"), "dirty tree로 rollback 보류 → B는 done=true 유지");

        // base에 머지된 파일이 그대로 존재 (revert 없음)
        Assert.True(File.Exists(Path.Combine(_repoDir, "a.txt")));
        Assert.True(File.Exists(Path.Combine(_repoDir, "b.txt")));

        // git log 최신 커밋: revert/rollback이 아님
        var (_, logOut) = await git.RunAsync(["log", "--oneline", "-1"], _repoDir);
        Assert.False(
            logOut.Contains("revert", StringComparison.OrdinalIgnoreCase) ||
            logOut.Contains("rollback", StringComparison.OrdinalIgnoreCase),
            $"dirty tree → revert 커밋 없어야 함. 최신 커밋: {logOut.Trim()}");
    }

    // ─── 케이스 4: opt-in ON + smoke 성공 → 정상 진행 ─────────────────────────────

    [Fact]
    public async Task OptInOn_SmokeSucceeds_NormalFlow()
    {
        await InitRepoAsync();
        var manager = await SetupTwoTasksAsync();

        var (git, worktree) = MakeGitServices();
        var executor = new ParallelExecutor(
            manager, MakeRunner(), git, worktree, _logger,
            new RunOptions(
                TasksFile: _tasksFile, ModelOverride: "opus",
                NoSmokeTest: false, SmokeTestCommandOverride: PassSmokeCmd,
                AutoRollbackOnSmokeFail: true)); // opt-in ON이지만 smoke 성공

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var exit = await executor.RunAsync(maxConcurrent: 4, cts.Token);

        // smoke 성공 → 정상 종료
        Assert.Equal(0, exit);

        // state.json: done=true 유지
        await manager.ReloadAsync();
        Assert.True(manager.IsDone("A"), "smoke 성공 시 A는 done=true 유지");
        Assert.True(manager.IsDone("B"), "smoke 성공 시 B는 done=true 유지");

        // base에 머지된 파일 존재
        Assert.True(File.Exists(Path.Combine(_repoDir, "a.txt")));
        Assert.True(File.Exists(Path.Combine(_repoDir, "b.txt")));

        // git log 최신 커밋: revert/rollback이 아님 (smoke 성공)
        var (_, logOut) = await git.RunAsync(["log", "--oneline", "-1"], _repoDir);
        Assert.False(
            logOut.Contains("revert", StringComparison.OrdinalIgnoreCase) ||
            logOut.Contains("rollback", StringComparison.OrdinalIgnoreCase),
            $"smoke 성공 → revert 커밋 없어야 함. 최신 커밋: {logOut.Trim()}");
    }

    // ─── helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 임시 git repo 초기화. .gitignore로 .ralph-logs/ 제외 → 안전 체크 시 클린 tree 보장.
    /// </summary>
    private async Task InitRepoAsync()
    {
        var git = new GitService();
        async Task Run(params string[] args)
        {
            var (e, o) = await git.RunAsync(args, _repoDir);
            if (e != 0)
                throw new InvalidOperationException(
                    $"git {string.Join(' ', args)} failed in {_repoDir}: {o.Trim()}");
        }

        await Run("init", "-b", "main");
        await Run("config", "user.email", "test@ralph.test");
        await Run("config", "user.name", "Ralph Test");
        await Run("config", "commit.gpgsign", "false");

        // .ralph-logs/ 무시 → state.json/cost.jsonl 등이 untracked로 표시되지 않음
        await File.WriteAllTextAsync(Path.Combine(_repoDir, ".gitignore"), ".ralph-logs/\n");
        await Run("add", ".gitignore");
        await Run("commit", "-m", "initial");
    }

    /// <summary>
    /// 2개 독립 task(A, B)를 tasks.json에 저장하고 커밋한 뒤 TaskManager를 반환.
    /// tasks.json을 커밋하면 worktree 체크아웃 시 동일 파일이 포함되어 merge conflict 없이
    /// NormalizeTasksJsonAsync가 정규화를 통과하고, 안전 체크 시 dirty로 잡히지 않는다.
    /// </summary>
    private async Task<TaskManager> SetupTwoTasksAsync()
    {
        var tasks = new TasksFile
        {
            Tasks =
            {
                MakeTask("A", "feat-a", ["a.txt"]),
                MakeTask("B", "feat-b", ["b.txt"]),
            },
            Workflow = new WorkflowSettings
            {
                OnTaskComplete = new OnTaskComplete { CommitChanges = true },
                Parallel = new ParallelSettings { Enabled = true, MaxConcurrent = 4 },
            },
        };

        var json = JsonSerializer.Serialize(tasks, TaskManager.JsonOptions);
        await File.WriteAllTextAsync(_tasksFile, json);

        var git = new GitService();
        var (e1, o1) = await git.RunAsync(["add", "tasks.json"], _repoDir);
        if (e1 != 0) throw new InvalidOperationException($"add tasks.json failed: {o1.Trim()}");
        var (e2, o2) = await git.RunAsync(["commit", "-m", "add tasks.json"], _repoDir);
        if (e2 != 0) throw new InvalidOperationException($"commit tasks.json failed: {o2.Trim()}");

        return await TaskManager.LoadAsync(_tasksFile);
    }

    private (GitService git, WorktreeService worktree) MakeGitServices() =>
        (new GitService(), new WorktreeService(new GitService(), _worktreeBase));

    /// <summary>
    /// Task ID("A"/"B")에 따라 각 task의 선언 파일(a.txt/b.txt)을 worktree에 생성.
    /// </summary>
    private WorktreeAwareRunner MakeRunner() =>
        new((prompt, wd) =>
        {
            var taskId = ExtractTaskId(prompt);
            var fileName = taskId == "A" ? "a.txt" : "b.txt";
            if (!string.IsNullOrEmpty(wd))
                File.WriteAllText(Path.Combine(wd!, fileName), $"content from {taskId}");
            return new ClaudeResult
            {
                Success = true,
                ExitCode = 0,
                Output = "ok",
                Duration = TimeSpan.FromMilliseconds(10),
            };
        });

    private static TaskItem MakeTask(string id, string title, IEnumerable<string> modifiedFiles) =>
        new()
        {
            Id = id,
            Title = title,
            Prompt = $"do task {id}",
            ModifiedFiles = modifiedFiles.ToList(),
        };

    private static string ExtractTaskId(string prompt)
    {
        const string marker = "Task ID: ";
        var idx = prompt.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return "";
        var start = idx + marker.Length;
        var end = prompt.IndexOf('\n', start);
        if (end < 0) end = prompt.Length;
        return prompt[start..end].Trim();
    }

    private sealed class WorktreeAwareRunner : IAgentRunner
    {
        private readonly Func<string, string?, ClaudeResult> _callback;
        public bool Debug { get; set; }
        public int? TaskTimeoutSec { get; set; }

        public WorktreeAwareRunner(Func<string, string?, ClaudeResult> callback) =>
            _callback = callback;

        public Task<ClaudeResult> RunStreamAsync(
            string prompt, string? model = null, string? workingDirectory = null,
            RalphLogger? logger = null, TextWriter? output = null,
            CancellationToken ct = default, string? allowedTools = null)
            => Task.FromResult(_callback(prompt, workingDirectory));

        public Task<ClaudeResult> RunWithRetryAsync(
            string prompt, string? model = null, string? workingDirectory = null,
            RalphLogger? logger = null, TextWriter? output = null,
            CancellationToken ct = default,
            Func<ClaudeResult, string?>? buildRetryContext = null,
            string? allowedTools = null)
            => RunStreamAsync(prompt, model, workingDirectory, logger, output, ct, allowedTools);
    }
}
