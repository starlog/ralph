using System.Text.Json;
using Ralph.Commands;
using Ralph.Models;
using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

/// <summary>
/// fix2 #8: 머지 트랜잭션 로그 (merge-log.jsonl) 기능 통합 테스트.
///
/// 케이스:
///   1. batch 실행 후 merge-log.jsonl이 생성되고 필수 필드(ts/batch/taskId/baseSha/mergedSha/stateMarked/smokeTest)가 채워짐
///   2. 동일 (taskId, mergedSha) 두 번 append 시 중복 없음 (idempotent); 다른 mergedSha는 별도 entry
///   3. --status 명령이 merge-log.jsonl 섹션을 반영해 exit 0으로 완료
///   4. RollbackService.GetMergeLogEntriesSinceSnapshotAsync가 스냅샷 이후 entry만 반환
/// </summary>
[Collection("cost")]
public class MergeLogTests : IDisposable
{
    private readonly string _root;
    private readonly string _repoDir;
    private readonly string _worktreeBase;
    private readonly string _tasksFile;
    private readonly string _origCwd;
    private readonly RalphLogger _logger;

    private static string PassSmokeCmd => OperatingSystem.IsWindows() ? "exit 0" : "true";

    public MergeLogTests()
    {
        _origCwd = Directory.GetCurrentDirectory();
        _root = Path.Combine(Path.GetTempPath(), $"ralph-mlt-{Guid.NewGuid():N}");
        _repoDir = Path.Combine(_root, "repo");
        _worktreeBase = Path.Combine(_root, "worktrees");
        _tasksFile = Path.Combine(_repoDir, "tasks.json");

        Directory.CreateDirectory(_repoDir);
        Directory.CreateDirectory(_worktreeBase);

        Directory.SetCurrentDirectory(_repoDir);

        CostTracker.SetLogDirForTesting(Path.Combine(_repoDir, ".ralph-logs"));
        CostTracker.ResetForTesting();
        _logger = new RalphLogger(Path.Combine(_repoDir, ".ralph-logs"));
    }

    public void Dispose()
    {
        try { _logger.Dispose(); } catch { }
        CostTracker.ResetForTesting();
        CostTracker.SetLogDirForTesting(null);
        try { Directory.SetCurrentDirectory(_origCwd); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ─── 케이스 1: batch 실행 후 merge-log.jsonl 생성 및 필수 필드 채워짐 ───────────

    [Fact]
    public async Task After_Batch_MergeLog_Created_With_All_Required_Fields()
    {
        await InitRepoAsync();
        var manager = await SetupTwoTasksAsync();

        var (git, worktree) = MakeGitServices();
        var executor = new ParallelExecutor(
            manager, MakeRunner(), git, worktree, _logger,
            tasksFile: _tasksFile, modelOverride: "opus",
            noSmokeTest: false,
            smokeTestCommandOverride: PassSmokeCmd,
            autoRollbackOnSmokeFail: false);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var exit = await executor.RunAsync(maxConcurrent: 4, cts.Token);

        Assert.Equal(0, exit);

        var logPath = Path.Combine(_repoDir, RalphPaths.MergeLogRelative);
        Assert.True(File.Exists(logPath), "merge-log.jsonl이 생성되어야 한다");

        var svc = new MergeLogService(_repoDir, RalphLogger.Null);
        var entries = await svc.ReadAllAsync(CancellationToken.None);
        Assert.NotEmpty(entries);

        // 필수 필드 검증
        foreach (var entry in entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Ts),
                $"[{entry.TaskId}] ts 필드가 비어있음");
            Assert.True(entry.Batch > 0,
                $"[{entry.TaskId}] batch는 1-based 양수여야 함 (actual={entry.Batch})");
            Assert.False(string.IsNullOrWhiteSpace(entry.TaskId),
                "taskId 필드가 비어있음");
            Assert.False(string.IsNullOrWhiteSpace(entry.MergedSha),
                $"[{entry.TaskId}] mergedSha 필드가 비어있음");
            Assert.True(entry.StateMarked,
                $"[{entry.TaskId}] stateMarked는 true여야 함");
            Assert.True(
                entry.SmokeTest == "passed" || entry.SmokeTest == "skipped",
                $"[{entry.TaskId}] smokeTest는 passed 또는 skipped여야 함 (actual={entry.SmokeTest})");
        }

        // 두 task 모두 기록됐는지
        var taskIds = entries.Select(e => e.TaskId).ToHashSet();
        Assert.Contains("A", taskIds);
        Assert.Contains("B", taskIds);
    }

    // ─── 케이스 2: idempotent — 동일 entry 두 번 append 시 중복 없음 ──────────────

    [Fact]
    public async Task AppendMerge_Same_TaskId_And_MergedSha_Produces_Single_Entry()
    {
        var dir = Path.Combine(_root, "idempotent-same");
        Directory.CreateDirectory(dir);
        var svc = new MergeLogService(dir, RalphLogger.Null);

        var entry = new MergeLogEntry
        {
            Ts = "2026-01-01T00:00:00.000Z",
            Batch = 1,
            TaskId = "task-a",
            BaseSha = "base1111",
            MergedSha = "merged2222",
            StateMarked = true,
            SmokeTest = "passed",
        };

        await svc.AppendMergeAsync(entry, CancellationToken.None);
        await svc.AppendMergeAsync(entry, CancellationToken.None); // 동일 entry 재시도

        var entries = await svc.ReadAllAsync(CancellationToken.None);
        Assert.Single(entries);
        Assert.Equal("task-a", entries[0].TaskId);
        Assert.Equal("merged2222", entries[0].MergedSha);
    }

    [Fact]
    public async Task AppendMerge_New_Instance_Deduplicates_Via_Disk_Reload()
    {
        // 첫 번째 인스턴스로 append 후, 새 인스턴스가 디스크를 읽고 동일 entry를 건너뜀
        var dir = Path.Combine(_root, "idempotent-reload");
        Directory.CreateDirectory(dir);

        var svc1 = new MergeLogService(dir, RalphLogger.Null);
        var entry = new MergeLogEntry
        {
            Ts = "2026-01-01T00:00:00.000Z",
            Batch = 1,
            TaskId = "task-b",
            BaseSha = "base1",
            MergedSha = "sha-unique",
            StateMarked = true,
            SmokeTest = "skipped",
        };
        await svc1.AppendMergeAsync(entry, CancellationToken.None);

        // 새 인스턴스: 디스크에서 기존 entry를 로드해 dedup
        var svc2 = new MergeLogService(dir, RalphLogger.Null);
        await svc2.AppendMergeAsync(entry, CancellationToken.None); // 중복 → skip

        var entries = await svc2.ReadAllAsync(CancellationToken.None);
        Assert.Single(entries);
    }

    [Fact]
    public async Task AppendMerge_Different_MergedSha_Same_TaskId_Creates_Two_Entries()
    {
        var dir = Path.Combine(_root, "two-entries");
        Directory.CreateDirectory(dir);
        var svc = new MergeLogService(dir, RalphLogger.Null);

        var entry1 = new MergeLogEntry
        {
            Ts = "2026-01-01T10:00:00.000Z",
            Batch = 1,
            TaskId = "task-a",
            BaseSha = "base1",
            MergedSha = "sha-first",
            StateMarked = true,
            SmokeTest = "passed",
        };
        var entry2 = new MergeLogEntry
        {
            Ts = "2026-01-01T11:00:00.000Z",
            Batch = 2,
            TaskId = "task-a",
            BaseSha = "base2",
            MergedSha = "sha-second", // mergedSha 다름 → 별도 entry
            StateMarked = true,
            SmokeTest = "passed",
        };

        await svc.AppendMergeAsync(entry1, CancellationToken.None);
        await svc.AppendMergeAsync(entry2, CancellationToken.None);

        var entries = await svc.ReadAllAsync(CancellationToken.None);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.MergedSha == "sha-first");
        Assert.Contains(entries, e => e.MergedSha == "sha-second");
    }

    // ─── 케이스 3: --status 출력에 merge-log 섹션 반영 ────────────────────────────

    [Fact]
    public async Task StatusCommand_With_MergeLog_Exits_Zero()
    {
        // tasks.json 생성 (DisplayHelpers.RequireFile 통과)
        await WriteMinimalTasksJsonAsync();

        // merge-log.jsonl 수동 생성 (cwd=_repoDir → .ralph-logs/merge-log.jsonl)
        var logDir = Path.Combine(_repoDir, RalphPaths.LogDir);
        Directory.CreateDirectory(logDir);
        var mergeLogPath = Path.Combine(logDir, RalphPaths.MergeLogFileName);

        var entry = new MergeLogEntry
        {
            Ts = "2026-01-01T00:00:00.000Z",
            Batch = 1,
            TaskId = "task-a",
            BaseSha = "aabbccdd",
            MergedSha = "11223344",
            StateMarked = true,
            SmokeTest = "passed",
        };
        var line = JsonSerializer.Serialize(entry, RalphJsonContext.Default.MergeLogEntry);
        await File.WriteAllTextAsync(mergeLogPath, line + "\n");

        var ctx = new CommandContext
        {
            Command = "--status",
            Args = ["--status"],
            TasksFile = _tasksFile,
        };
        var cmd = new StatusCommand(ctx);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var exit = await cmd.ExecuteAsync(cts.Token);

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task StatusCommand_Without_MergeLog_Exits_Zero()
    {
        // merge-log.jsonl 없을 때도 섹션 생략하고 정상 완료해야 함 (legacy 호환)
        await WriteMinimalTasksJsonAsync();

        var ctx = new CommandContext
        {
            Command = "--status",
            Args = ["--status"],
            TasksFile = _tasksFile,
        };
        var cmd = new StatusCommand(ctx);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var exit = await cmd.ExecuteAsync(cts.Token);

        Assert.Equal(0, exit);
    }

    // ─── 케이스 4: --rollback이 merge-log.jsonl 활용 경로 호출 ────────────────────

    [Fact]
    public async Task GetMergeLogEntriesSinceSnapshot_Returns_Only_Entries_After_Snapshot_Ts()
    {
        var dir = Path.Combine(_root, "rollback-filter");
        Directory.CreateDirectory(dir);
        var svc = new MergeLogService(dir, RalphLogger.Null);

        const string snapshotTs = "2026-01-01T12:00:00.000Z";

        var beforeEntry = new MergeLogEntry
        {
            Ts = "2026-01-01T11:00:00.000Z", // 스냅샷 이전
            Batch = 1,
            TaskId = "old-task",
            BaseSha = "b1",
            MergedSha = "m1",
            StateMarked = true,
            SmokeTest = "passed",
        };
        var afterEntry1 = new MergeLogEntry
        {
            Ts = "2026-01-01T13:00:00.000Z", // 스냅샷 이후
            Batch = 2,
            TaskId = "new-task-1",
            BaseSha = "b2",
            MergedSha = "m2",
            StateMarked = true,
            SmokeTest = "passed",
        };
        var afterEntry2 = new MergeLogEntry
        {
            Ts = "2026-01-01T14:00:00.000Z", // 스냅샷 이후
            Batch = 2,
            TaskId = "new-task-2",
            BaseSha = "b3",
            MergedSha = "m3",
            StateMarked = false,
            SmokeTest = "failed",
        };

        await svc.AppendMergeAsync(beforeEntry, CancellationToken.None);
        await svc.AppendMergeAsync(afterEntry1, CancellationToken.None);
        await svc.AppendMergeAsync(afterEntry2, CancellationToken.None);

        var snapshot = new RollbackSnapshot
        {
            Phase = "post-plan",
            Timestamp = snapshotTs,
            GitHead = "abc",
            Branch = "main",
        };

        var result = await RollbackService.GetMergeLogEntriesSinceSnapshotAsync(snapshot, dir);

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.True(
            string.CompareOrdinal(e.Ts, snapshotTs) > 0,
            $"[{e.TaskId}] ts={e.Ts}는 스냅샷({snapshotTs}) 이후여야 함"));
        Assert.Contains(result, e => e.TaskId == "new-task-1");
        Assert.Contains(result, e => e.TaskId == "new-task-2");
        Assert.DoesNotContain(result, e => e.TaskId == "old-task");
    }

    [Fact]
    public async Task GetMergeLogEntriesSinceSnapshot_Empty_Snapshot_Ts_Returns_All()
    {
        // Timestamp가 비어있으면 전체 entry 반환 (RollbackService 구현 기준)
        var dir = Path.Combine(_root, "rollback-all");
        Directory.CreateDirectory(dir);
        var svc = new MergeLogService(dir, RalphLogger.Null);

        var entry = new MergeLogEntry
        {
            Ts = "2026-01-01T10:00:00.000Z",
            Batch = 1,
            TaskId = "some-task",
            BaseSha = "b1",
            MergedSha = "m1",
            StateMarked = true,
            SmokeTest = "passed",
        };
        await svc.AppendMergeAsync(entry, CancellationToken.None);

        var snapshot = new RollbackSnapshot
        {
            Phase = "pre-plan",
            Timestamp = "", // 빈 타임스탬프 → 전체 반환
            GitHead = "abc",
            Branch = "main",
        };

        var result = await RollbackService.GetMergeLogEntriesSinceSnapshotAsync(snapshot, dir);

        Assert.Single(result);
        Assert.Equal("some-task", result[0].TaskId);
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

        // .ralph-logs/ 제외 → 안전 체크 시 clean tree 유지
        await File.WriteAllTextAsync(Path.Combine(_repoDir, ".gitignore"), ".ralph-logs/\n");
        await Run("add", ".gitignore");
        await Run("commit", "-m", "initial");
    }

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

    private async Task WriteMinimalTasksJsonAsync()
    {
        var tasks = new TasksFile
        {
            Tasks = { MakeTask("task-a", "Task A", ["a.txt"]) },
        };
        var json = JsonSerializer.Serialize(tasks, TaskManager.JsonOptions);
        await File.WriteAllTextAsync(_tasksFile, json);
    }

    private (GitService git, WorktreeService worktree) MakeGitServices() =>
        (new GitService(), new WorktreeService(new GitService(), _worktreeBase));

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
