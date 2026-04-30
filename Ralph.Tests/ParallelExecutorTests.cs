using System.Text.Json;
using Ralph.Models;
using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

/// <summary>
/// ParallelExecutor의 batch 흐름(워크트리 생성 → 실행 → 머지 → smoke test)을 실제 git
/// fixture + IAgentRunner mock으로 검증한다. 회귀 위험이 가장 큰 라인을 지키는 통합 테스트.
///
/// 주의:
/// - ParallelExecutor 내부 GitService 호출 다수가 workingDirectory 없이 cwd에 의존하므로
///   각 테스트는 임시 repo로 SetCurrentDirectory를 변경한 뒤 Dispose에서 복원한다.
/// - CostTracker는 process-wide 정적 상태를 가지므로 "cost" 컬렉션과 직렬화한다.
/// </summary>
[Collection("cost")]
public class ParallelExecutorTests : IDisposable
{
    private readonly string _root;
    private readonly string _repoDir;
    private readonly string _worktreeBase;
    private readonly string _tasksFile;
    private readonly string _logDir;
    private readonly string _origCwd;
    private readonly RalphLogger _logger;

    public ParallelExecutorTests()
    {
        _origCwd = Directory.GetCurrentDirectory();
        _root = Path.Combine(Path.GetTempPath(), $"ralph-pe-{Guid.NewGuid():N}");
        _repoDir = Path.Combine(_root, "repo");
        _worktreeBase = Path.Combine(_root, "worktrees");
        _tasksFile = Path.Combine(_repoDir, "tasks.json");
        _logDir = Path.Combine(_root, "logs");
        Directory.CreateDirectory(_repoDir);
        Directory.CreateDirectory(_worktreeBase);
        Directory.CreateDirectory(_logDir);

        // ParallelExecutor 내부에서 .ralph-logs/cost.jsonl, .ralph-logs/{taskId}.log,
        // .ralph-logs/validation.jsonl을 cwd 상대로 생성한다. cwd를 _repoDir로 옮겨두면
        // 모든 산출물이 _root 아래로 격리된다.
        Directory.SetCurrentDirectory(_repoDir);

        // CostTracker 정적 캐시 격리
        CostTracker.SetLogDirForTesting(Path.Combine(_repoDir, ".ralph-logs"));
        CostTracker.ResetForTesting();

        _logger = new RalphLogger(Path.Combine(_repoDir, ".ralph-logs"));
    }

    public void Dispose()
    {
        try { _logger.Dispose(); } catch { /* best-effort */ }
        CostTracker.ResetForTesting();
        CostTracker.SetLogDirForTesting(null);
        try { Directory.SetCurrentDirectory(_origCwd); } catch { /* best-effort */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // ─── 테스트 1: 두 독립 task 모두 성공 ───────────────────────────────────────

    [Fact]
    public async Task Two_independent_tasks_both_succeed_and_merge_into_base()
    {
        await InitRepoAsync();

        var tasks = new TasksFile
        {
            Tasks =
            {
                MakeTask("A", "feat-a", new[] { "a.txt" }),
                MakeTask("B", "feat-b", new[] { "b.txt" }),
            },
            Workflow = MakeWorkflow(commitOnComplete: true),
        };
        var manager = await SaveAndLoadTasksAsync(tasks);

        // mock: workingDirectory(=worktreePath) 안에 declared 파일을 생성하고 success 반환
        var runner = new WorktreeAwareRunner((prompt, wd) =>
        {
            Assert.False(string.IsNullOrEmpty(wd), "병렬 batch에서는 worktreePath가 전달되어야 한다");
            // prompt 내 "Task ID: X" 토큰으로 어떤 task인지 식별
            var taskId = ExtractTaskId(prompt);
            var fileName = taskId == "A" ? "a.txt" : "b.txt";
            File.WriteAllText(Path.Combine(wd!, fileName), $"content from {taskId}");
            return SuccessResult();
        });

        var (git, worktree) = MakeGitServices();
        var executor = new ParallelExecutor(
            manager, runner, git, worktree, _logger,
            tasksFile: _tasksFile, modelOverride: "opus",
            noSmokeTest: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var exit = await executor.RunAsync(maxConcurrent: 4, cts.Token);

        Assert.Equal(0, exit);

        // tasks.json 상에서 둘 다 done
        await manager.ReloadAsync();
        Assert.True(manager.GetTask("A")!.Done);
        Assert.True(manager.GetTask("B")!.Done);

        // base 브랜치(main)에 두 파일이 모두 머지되었는지 확인
        Assert.Equal("content from A", File.ReadAllText(Path.Combine(_repoDir, "a.txt")));
        Assert.Equal("content from B", File.ReadAllText(Path.Combine(_repoDir, "b.txt")));
        Assert.Equal("main", await git.GetCurrentBranchAsync(_repoDir));

        // 두 worktree가 모두 정리되었는지
        Assert.False(Directory.Exists(Path.Combine(_worktreeBase, "A")));
        Assert.False(Directory.Exists(Path.Combine(_worktreeBase, "B")));

        // 각 task별로 정확히 1번씩 호출
        Assert.Equal(2, runner.CallCount);
    }

    // ─── 테스트 2: partial failure ──────────────────────────────────────────────

    [Fact]
    public async Task Partial_failure_merges_success_and_blocks_failed_task()
    {
        await InitRepoAsync();

        var tasks = new TasksFile
        {
            Tasks =
            {
                MakeTask("A", "feat-a", new[] { "a.txt" }),
                MakeTask("B", "feat-b", new[] { "b.txt" }),
            },
            Workflow = MakeWorkflow(commitOnComplete: true),
        };
        var manager = await SaveAndLoadTasksAsync(tasks);

        var runner = new WorktreeAwareRunner((prompt, wd) =>
        {
            var taskId = ExtractTaskId(prompt);
            if (taskId == "B") return FailureResult("intentional B failure");

            // A는 정상 처리. wd가 null인 경우(단일 task path)에도 안전하게 동작.
            if (!string.IsNullOrEmpty(wd))
                File.WriteAllText(Path.Combine(wd!, "a.txt"), "A done");
            return SuccessResult();
        });

        var (git, worktree) = MakeGitServices();
        var executor = new ParallelExecutor(
            manager, runner, git, worktree, _logger,
            tasksFile: _tasksFile, modelOverride: "opus",
            noSmokeTest: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var exit = await executor.RunAsync(maxConcurrent: 4, cts.Token);

        // B가 마지막까지 실패하면 RunSingleTaskAsync에서 1을 반환하며 ralph가 종료된다.
        Assert.NotEqual(0, exit);

        await manager.ReloadAsync();
        // A는 머지 + done
        Assert.True(manager.GetTask("A")!.Done);
        Assert.True(File.Exists(Path.Combine(_repoDir, "a.txt")));

        // B는 실패 → done=false, base에 b.txt도 없음
        Assert.False(manager.GetTask("B")!.Done);
        Assert.False(File.Exists(Path.Combine(_repoDir, "b.txt")));

        // 실패한 worktree는 정리되어야 한다
        Assert.False(Directory.Exists(Path.Combine(_worktreeBase, "B")));
    }

    // ─── 테스트 3: 머지 후 smoke test 실패 ──────────────────────────────────────

    // 비고: ParallelExecutor의 smoke test는 RunParallelBatchAsync 경로(>=2 task)에서만
    // 실행된다. 단일 task는 RunSingleTaskAsync로 분기되어 smoke test가 호출되지 않으므로,
    // smoke test 실패 회귀를 잡기 위한 최소 시나리오는 2개의 독립 task로 구성한다.
    [Fact]
    public async Task Smoke_test_failure_returns_nonzero_and_cleans_worktrees()
    {
        await InitRepoAsync();

        var tasks = new TasksFile
        {
            Tasks =
            {
                MakeTask("A", "feat-a", new[] { "a.txt" }),
                MakeTask("B", "feat-b", new[] { "b.txt" }),
            },
            Workflow = MakeWorkflow(
                commitOnComplete: true,
                smokeTest: new VerificationSpec { Command = "sh -c 'exit 1'", TimeoutSec = 30 }),
        };
        var manager = await SaveAndLoadTasksAsync(tasks);

        var runner = new WorktreeAwareRunner((prompt, wd) =>
        {
            var taskId = ExtractTaskId(prompt);
            var fileName = taskId == "A" ? "a.txt" : "b.txt";
            File.WriteAllText(Path.Combine(wd!, fileName), $"content from {taskId}");
            return SuccessResult();
        });

        var (git, worktree) = MakeGitServices();
        var executor = new ParallelExecutor(
            manager, runner, git, worktree, _logger,
            tasksFile: _tasksFile, modelOverride: "opus",
            noSmokeTest: false); // smoke test 활성화

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var exit = await executor.RunAsync(maxConcurrent: 4, cts.Token);

        // smoke test 실패 → ParallelExecutor는 비-0 반환
        Assert.NotEqual(0, exit);

        // 두 task의 머지 자체는 완료되어 base에 파일이 존재하고 done=true로 마킹됐어야 한다
        // (smoke test는 머지 직후 단계에서 실행됨 — 5단계 done 마킹 이후, return 직전에 실행).
        Assert.True(File.Exists(Path.Combine(_repoDir, "a.txt")));
        Assert.True(File.Exists(Path.Combine(_repoDir, "b.txt")));

        // worktree는 finally 블록에서 정리되어야 한다
        Assert.False(Directory.Exists(Path.Combine(_worktreeBase, "A")));
        Assert.False(Directory.Exists(Path.Combine(_worktreeBase, "B")));
    }

    // ─── helpers ────────────────────────────────────────────────────────────────

    private async Task InitRepoAsync()
    {
        var git = new GitService();
        async Task Run(params string[] args)
        {
            var (e, o) = await git.RunAsync(args, _repoDir);
            if (e != 0)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed in {_repoDir}: {o.Trim()}");
        }
        await Run("init", "-b", "main");
        await Run("config", "user.email", "test@ralph.test");
        await Run("config", "user.name", "Ralph Test");
        await Run("config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(_repoDir, "README.md"), "# ralph-test\n");
        await Run("add", "README.md");
        await Run("commit", "-m", "initial");
    }

    private (GitService git, WorktreeService worktree) MakeGitServices()
    {
        var git = new GitService();
        var worktree = new WorktreeService(git, _worktreeBase);
        return (git, worktree);
    }

    private static TaskItem MakeTask(string id, string title, IEnumerable<string> modifiedFiles) =>
        new()
        {
            Id = id,
            Title = title,
            Done = false,
            Prompt = $"do task {id}",
            ModifiedFiles = modifiedFiles.ToList(),
        };

    private static WorkflowSettings MakeWorkflow(
        bool commitOnComplete, VerificationSpec? smokeTest = null) =>
        new()
        {
            OnTaskComplete = new OnTaskComplete { CommitChanges = commitOnComplete },
            Parallel = new ParallelSettings { Enabled = true, MaxConcurrent = 4 },
            SmokeTest = smokeTest,
        };

    private async Task<TaskManager> SaveAndLoadTasksAsync(TasksFile data)
    {
        var json = JsonSerializer.Serialize(data, TaskManager.JsonOptions);
        await File.WriteAllTextAsync(_tasksFile, json);
        // 초기 commit 이후 tasks.json은 untracked 상태로 base에 진입하지 않는다.
        // ParallelExecutor가 머지 후 _git.RunAsync(["add", _tasksFile])로 staging해 자체 커밋한다.
        return await TaskManager.LoadAsync(_tasksFile);
    }

    private static ClaudeResult SuccessResult() =>
        new()
        {
            Success = true,
            ExitCode = 0,
            Output = "ok",
            Duration = TimeSpan.FromMilliseconds(10),
        };

    private static ClaudeResult FailureResult(string stderr) =>
        new()
        {
            Success = false,
            ExitCode = 1,
            Stderr = stderr,
            Duration = TimeSpan.FromMilliseconds(10),
        };

    private static string ExtractTaskId(string prompt)
    {
        // PromptBuilder가 첫 줄에 "Task ID: {id}"를 박아 넣는다.
        const string marker = "Task ID: ";
        var idx = prompt.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return "";
        var start = idx + marker.Length;
        var end = prompt.IndexOf('\n', start);
        if (end < 0) end = prompt.Length;
        return prompt[start..end].Trim();
    }

    /// <summary>
    /// IAgentRunner 테스트 전용 구현. callback이 (prompt, workingDirectory)를 받아
    /// worktree 안에 직접 파일을 생성할 수 있게 한다. Helpers/MockAgentRunner는 prompt만
    /// 노출해 worktree 격리 시나리오에서는 부족하므로 이 클래스를 별도로 둔다.
    /// </summary>
    private sealed class WorktreeAwareRunner : IAgentRunner
    {
        private readonly Func<string, string?, ClaudeResult> _callback;
        public int CallCount { get; private set; }
        public bool Debug { get; set; }
        public int? TaskTimeoutSec { get; set; }

        public WorktreeAwareRunner(Func<string, string?, ClaudeResult> callback)
        {
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        }

        public Task<ClaudeResult> RunStreamAsync(
            string prompt, string? model = null, string? workingDirectory = null,
            RalphLogger? logger = null, TextWriter? output = null,
            CancellationToken ct = default, string? allowedTools = null)
        {
            CallCount++;
            return Task.FromResult(_callback(prompt, workingDirectory));
        }

        public Task<ClaudeResult> RunWithRetryAsync(
            string prompt, string? model = null, string? workingDirectory = null,
            RalphLogger? logger = null, TextWriter? output = null,
            CancellationToken ct = default,
            Func<ClaudeResult, string?>? buildRetryContext = null,
            string? allowedTools = null) =>
            RunStreamAsync(prompt, model, workingDirectory, logger, output, ct, allowedTools);
    }
}
