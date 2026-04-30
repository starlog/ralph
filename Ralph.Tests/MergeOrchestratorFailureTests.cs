using System.Text.Json;
using Ralph.Models;
using Ralph.Services;
using Ralph.Tests.Helpers;
using Xunit;

namespace Ralph.Tests;

/// <summary>
/// state.json 쓰기 실패(IOException) 시 머지 batch가 중단되는 invariant를 검증하는 통합 테스트.
///
/// fix1.md 1번 정책:
///   - done 마킹 IOException → batch 즉시 중단 (다음 task의 done 마킹 미실행).
///   - 이미 머지된 변경분은 base에 그대로 남음 (자동 롤백 없음).
///   - state.json은 일관된 상태: 실패한 task는 done=false, 미실행 task도 done=false.
/// </summary>
[Collection("cost")]
public class MergeOrchestratorFailureTests : IDisposable
{
    private readonly string _root;
    private readonly string _repoDir;
    private readonly string _worktreeBase;
    private readonly string _tasksFile;
    private readonly string _logDir;
    private readonly string _origCwd;
    private readonly RalphLogger _logger;

    public MergeOrchestratorFailureTests()
    {
        _origCwd = Directory.GetCurrentDirectory();
        _root = Path.Combine(Path.GetTempPath(), $"ralph-mo-{Guid.NewGuid():N}");
        _repoDir = Path.Combine(_root, "repo");
        _worktreeBase = Path.Combine(_root, "worktrees");
        _tasksFile = Path.Combine(_repoDir, "tasks.json");
        _logDir = Path.Combine(_repoDir, ".ralph-logs");

        Directory.CreateDirectory(_repoDir);
        Directory.CreateDirectory(_worktreeBase);
        Directory.CreateDirectory(_logDir);

        Directory.SetCurrentDirectory(_repoDir);

        _logger = new RalphLogger(_logDir);
    }

    public void Dispose()
    {
        try { _logger.Dispose(); } catch { /* best-effort */ }
        try { Directory.SetCurrentDirectory(_origCwd); } catch { /* best-effort */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // ─── 테스트 1: state.json 쓰기 실패 → batch 중단 ─────────────────────────────

    /// <summary>
    /// 2-task batch에서 첫 번째 done 마킹 단계에서 IOException 발생 시:
    /// 1. batch가 비-0 종료 코드로 중단됨
    /// 2. task1의 MarkDoneAsync는 호출됨(in-memory done=true), task2는 미호출(in-memory done=false)
    /// 3. disk state는 양쪽 모두 done=false (state.json 쓰기 실패)
    /// 4. 이미 머지된 변경분은 base에 유지 (자동 롤백 없음)
    /// 5. logger에 "[merge:done-mark]" 실패 기록 → ReportStateWriteFailure 및 "수동 복구" 안내 출력됨
    /// </summary>
    [Fact]
    public async Task StateJson_write_failure_aborts_batch_after_first_task_done_marking()
    {
        await InitRepoAsync();

        var tasks = new TasksFile
        {
            Tasks =
            {
                MakeTask("task1", "Task One", new[] { "a.txt" }),
                MakeTask("task2", "Task Two", new[] { "b.txt" }),
            },
            Workflow = MakeWorkflow(),
        };
        var manager = await SaveAndLoadTasksAsync(tasks);

        // state.json 경로를 디렉토리로 생성 → File.Move(tmp → state.json) 실패 → IOException.
        // SaveWithRetryAsync가 2회 재시도 후 IOException을 전파하며 batch를 중단시킨다.
        var statePath = StateStore.DefaultPathFor(_tasksFile);
        Directory.CreateDirectory(statePath);

        var runner = new WorktreeAwareRunner((prompt, wd) =>
        {
            var taskId = ExtractTaskId(prompt);
            var fileName = taskId == "task1" ? "a.txt" : "b.txt";
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

        // 1. batch 중단: 비-0 종료 코드
        Assert.NotEqual(0, exit);

        // 2. 호출 카운트 검증 (in-memory StateStore 상태로 판별)
        //    MergeOrchestrator done-marking 루프는 task 순서대로 실행.
        //    task1의 MarkDoneAsync: in-memory Done=true 설정 후 disk 저장 실패 → IOException 전파.
        //    task2의 MarkDoneAsync: 루프 즉시 종료로 호출되지 않음 → in-memory Done=false 유지.
        Assert.True(manager.IsDone("task1"),
            "task1의 MarkDoneAsync가 호출되어 in-memory는 true여야 한다 (disk 저장 실패와 무관)");
        Assert.False(manager.IsDone("task2"),
            "batch 중단으로 task2의 MarkDoneAsync가 호출되지 않아야 한다");

        // 3. disk state 일관성: 양쪽 모두 done=false
        //    statePath가 디렉토리이므로 File.Exists(statePath)=false → 새 StateStore는 빈 상태
        var freshState = await StateStore.OpenAsync(statePath);
        Assert.False(freshState.IsDone("task1"),
            "disk 쓰기 실패 → disk state에서 task1은 done=false여야 한다");
        Assert.False(freshState.IsDone("task2"),
            "done 마킹 미실행 → disk state에서 task2는 done=false여야 한다");

        // 4. 머지된 변경분은 base에 유지 (done 마킹 실패는 자동 롤백하지 않음)
        //    done-marking 루프는 merge 루프가 완전히 끝난 후 실행되므로 양쪽 모두 이미 base에 반영됨
        Assert.True(File.Exists(Path.Combine(_repoDir, "a.txt")),
            "task1 변경분이 base 브랜치에 머지되어 있어야 한다");
        Assert.True(File.Exists(Path.Combine(_repoDir, "b.txt")),
            "task2 변경분도 base 브랜치에 머지되어 있어야 한다");

        // 5. "수동 복구" 안내 출력 검증: logger 기록으로 간접 확인
        //    ReportStateWriteFailure가 호출되면 AnsiConsole에 "수동 복구 필요" 메시지를 출력하는
        //    동시에 logger에 "[merge:done-mark] ... state save failed after retries"를 기록한다.
        var logContent = LogReader.ReadOpenLog(_logger.LogFile);
        Assert.Contains("[merge:done-mark]", logContent);
        Assert.Contains("state save failed after retries", logContent);
    }

    // ─── helpers ────────────────────────────────────────────────────────────────

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
        await File.WriteAllTextAsync(Path.Combine(_repoDir, "README.md"), "# test\n");
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
            Prompt = $"do task {id}",
            ModifiedFiles = modifiedFiles.ToList(),
        };

    private static WorkflowSettings MakeWorkflow() =>
        new()
        {
            OnTaskComplete = new OnTaskComplete { CommitChanges = true },
            Parallel = new ParallelSettings { Enabled = true, MaxConcurrent = 4 },
        };

    private async Task<TaskManager> SaveAndLoadTasksAsync(TasksFile data)
    {
        var json = JsonSerializer.Serialize(data, TaskManager.JsonOptions);
        await File.WriteAllTextAsync(_tasksFile, json);
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

        public WorktreeAwareRunner(Func<string, string?, ClaudeResult> callback)
            => _callback = callback;

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
