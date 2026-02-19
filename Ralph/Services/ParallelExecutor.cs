using Ralph.Models;
using Spectre.Console;

namespace Ralph.Services;

public class ParallelExecutor
{
    private readonly TaskManager _taskManager;
    private readonly ClaudeService _claude;
    private readonly GitService _git;
    private readonly WorktreeService _worktree;
    private readonly RalphLogger _logger;
    private readonly string _tasksFile;
    private readonly SemaphoreSlim _taskFileLock = new(1, 1);

    public ParallelExecutor(
        TaskManager taskManager, ClaudeService claude, GitService git,
        WorktreeService worktree, RalphLogger logger, string tasksFile)
    {
        _taskManager = taskManager;
        _claude = claude;
        _git = git;
        _worktree = worktree;
        _logger = logger;
        _tasksFile = tasksFile;
    }

    public async Task<int> RunAsync(int maxConcurrent, CancellationToken ct)
    {
        // 순환 참조 검사
        if (_taskManager.HasCycle(out var cycle))
        {
            AnsiConsole.MarkupLine("[red]순환 의존성이 발견되었습니다:[/]");
            AnsiConsole.MarkupLine($"  {Markup.Escape(string.Join(" → ", cycle))}");
            _logger.Error($"Cycle detected: {string.Join(" → ", cycle)}");
            return 1;
        }

        // 잔존 worktree 감지
        var stale = await _worktree.DetectStaleWorktreesAsync(ct);
        if (stale.Count > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]잔존 worktree {stale.Count}개 감지. 정리합니다...[/]");
            await _worktree.CleanupAllAsync(_logger, ct);
        }

        // worktree 사용을 위해 최소 1개의 커밋이 필요
        await _git.EnsureInitialCommitAsync(_logger, ct);

        var baseBranch = await _git.GetCurrentBranchAsync(ct: ct);
        _logger.Info($"Parallel execution starting on branch: {baseBranch}");

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var readyTasks = _taskManager.GetAllReadyTasks();

            if (readyTasks.Count == 0)
            {
                if (_taskManager.GetPendingTasks().Count > 0)
                {
                    AnsiConsole.MarkupLine("\n[red]모든 남은 태스크가 의존성에 의해 차단되었습니다:[/]");
                    foreach (var t in _taskManager.GetPendingTasks())
                    {
                        var deps = t.DependsOn is { Count: > 0 } ? string.Join(", ", t.DependsOn) : "none";
                        AnsiConsole.MarkupLine($"  {Markup.Escape(t.Id)}: depends on {Markup.Escape(deps)}");
                    }
                    _logger.Warn("Execution stopped: remaining tasks blocked by dependencies");
                    return 1;
                }

                AnsiConsole.MarkupLine("\n[green]모든 태스크가 완료되었습니다![/]");
                _logger.Info("All tasks completed");
                break;
            }

            if (readyTasks.Count == 1)
            {
                // 단일 태스크: worktree 없이 직접 실행
                AnsiConsole.MarkupLine($"\n[blue]단일 태스크 실행: {Markup.Escape(readyTasks[0])}[/]");
                var result = await RunSingleTaskAsync(readyTasks[0], ct);
                if (result != 0) return result;
            }
            else
            {
                // 복수 태스크: 배치 단위 병렬 실행
                var batches = _taskManager.GetParallelBatches();
                var batch = batches[0]; // 현재 실행 가능한 첫 배치

                // maxConcurrent 제한
                if (batch.Count > maxConcurrent)
                    batch = batch.Take(maxConcurrent).ToList();

                if (batch.Count == 1)
                {
                    // 배치에 하나만 있으면 직접 실행
                    AnsiConsole.MarkupLine($"\n[blue]단일 태스크 실행: {Markup.Escape(batch[0])}[/]");
                    var result = await RunSingleTaskAsync(batch[0], ct);
                    if (result != 0) return result;
                }
                else
                {
                    AnsiConsole.MarkupLine($"\n[green]병렬 실행: {batch.Count}개 태스크[/]");
                    foreach (var id in batch)
                        AnsiConsole.MarkupLine($"  [cyan]→[/] {Markup.Escape(id)}");

                    var result = await RunParallelBatchAsync(batch, baseBranch, ct);
                    if (result != 0) return result;
                }
            }
        }

        // 최종 정리
        await _worktree.CleanupAllAsync(_logger, ct);
        return 0;
    }

    /// <summary>
    /// worktree 없이 단일 태스크를 직접 실행합니다.
    /// </summary>
    private async Task<int> RunSingleTaskAsync(string taskId, CancellationToken ct)
    {
        var task = _taskManager.GetTask(taskId)!;
        _logger.TaskStart(taskId, task.Title);

        DisplayTaskInfo(taskId);
        AnsiConsole.MarkupLine($"[blue]실행 중: {Markup.Escape(task.Title)}[/]\n");

        if (!string.IsNullOrEmpty(task.Prompt))
        {
            var fullPrompt = BuildPrompt(task);

            AnsiConsole.MarkupLine("[cyan]Prompt:[/]");
            AnsiConsole.Write(new Panel(Markup.Escape(task.Prompt)).Border(BoxBorder.Rounded));
            AnsiConsole.MarkupLine("\n[cyan]Running Claude Code...[/]\n");

            var result = await _claude.RunWithRetryAsync(fullPrompt, logger: _logger, ct: ct);
            if (!result.Success)
            {
                AnsiConsole.MarkupLine("\n[red]Claude Code 실행 실패[/]");
                _logger.TaskEnd(taskId, "failed");
                return 1;
            }
            AnsiConsole.MarkupLine("\n[green]Claude Code 실행 완료[/]");
        }

        // subtasks 처리
        ProcessSubtasks(task, taskId);

        // 상태 업데이트
        _taskManager.MarkTaskDone(taskId);
        await _taskManager.SaveAsync();

        AnsiConsole.MarkupLine($"[green]태스크 완료: {Markup.Escape(task.Title)}[/]");
        _logger.TaskEnd(taskId, "completed");

        if (_taskManager.CommitOnComplete)
            await _git.CommitChangesAsync(taskId, task.Title, _taskManager.CommitTemplate, _logger, ct: ct);

        return 0;
    }

    /// <summary>
    /// 여러 태스크를 worktree 기반으로 병렬 실행합니다.
    /// </summary>
    private async Task<int> RunParallelBatchAsync(
        List<string> taskIds, string baseBranch, CancellationToken ct)
    {
        var conflictStrategy = _taskManager.ParallelConfig.ConflictStrategy;
        var worktrees = new Dictionary<string, string>(); // taskId → worktreePath
        var tracker = new TaskProgressTracker();

        try
        {
            // 1. 모든 worktree 생성 및 tracker 등록
            AnsiConsole.MarkupLine("\n[blue]Worktree 생성 중...[/]");
            const string logDir = ".ralph-logs";
            Directory.CreateDirectory(logDir);

            foreach (var taskId in taskIds)
            {
                var path = await _worktree.CreateWorktreeAsync(taskId, baseBranch, _logger, ct);
                worktrees[taskId] = path;

                var logFile = Path.GetFullPath(Path.Combine(logDir, $"{taskId}.log"));
                var task = _taskManager.GetTask(taskId)!;
                tracker.Register(taskId, task.Title, logFile);

                AnsiConsole.MarkupLine($"  [dim]→ {Markup.Escape(taskId)}: {Markup.Escape(path)}[/]");
            }

            // 2. Live 대시보드 + 병렬 실행
            var taskResults = new Dictionary<string, bool>();

            await AnsiConsole.Live(tracker.BuildTable())
                .AutoClear(false)
                .StartAsync(async ctx =>
                {
                    // 500ms 주기 refresh 타이머
                    using var refreshTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
                    var refreshTask = Task.Run(async () =>
                    {
                        while (await refreshTimer.WaitForNextTickAsync(ct))
                        {
                            tracker.RefreshAllOutputSizes();
                            ctx.UpdateTarget(tracker.BuildTable());
                        }
                    }, ct);

                    // 병렬 실행
                    var execTasks = taskIds.Select(async taskId =>
                    {
                        var success = await RunInWorktreeWithLogAsync(taskId, worktrees[taskId], tracker, ct);
                        lock (taskResults)
                            taskResults[taskId] = success;
                    }).ToList();

                    await Task.WhenAll(execTasks);

                    // 타이머 중지 및 최종 갱신
                    refreshTimer.Dispose();
                    try { await refreshTask; } catch (OperationCanceledException) { }

                    tracker.RefreshAllOutputSizes();
                    ctx.UpdateTarget(tracker.BuildTable());
                });

            // 실패한 태스크 확인
            var failed = taskIds.Where(id => taskResults.TryGetValue(id, out var ok) && !ok).ToList();
            if (failed.Count > 0)
            {
                AnsiConsole.MarkupLine($"\n[red]{failed.Count}개 태스크 실행 실패:[/]");
                foreach (var f in failed)
                    AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(f)}");

                // 실패한 worktree 정리
                foreach (var f in failed)
                    await _worktree.CleanupWorktreeAsync(f, _logger, ct);

                // 성공한 것만 merge 진행
                taskIds = taskIds.Except(failed).ToList();
                if (taskIds.Count == 0) return 1;
            }

            // 3. 순차적으로 메인에 병합
            AnsiConsole.MarkupLine("\n[blue]메인 브랜치에 병합 중...[/]");

            foreach (var taskId in taskIds)
            {
                tracker.UpdateStatus(taskId, TaskProgressStatus.Merging);

                var mergeResult = await _worktree.MergeWorktreeAsync(
                    taskId, baseBranch, conflictStrategy, _logger, ct);

                if (mergeResult.Success)
                {
                    AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(taskId)} 병합 완료");
                }
                else
                {
                    AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(taskId)} 병합 충돌!");

                    var resolved = await HandleMergeConflictAsync(
                        taskId, baseBranch, mergeResult, conflictStrategy, ct);

                    if (!resolved)
                    {
                        _logger.Error($"Merge conflict unresolved for {taskId}");
                        // 나머지 태스크 정리
                        foreach (var remaining in taskIds)
                            await _worktree.CleanupWorktreeAsync(remaining, _logger, ct);
                        return 1;
                    }
                }
            }

            // 4. 상태 업데이트 (thread-safe)
            foreach (var taskId in taskIds)
            {
                await MarkTaskDoneThreadSafe(taskId, ct);
                var task = _taskManager.GetTask(taskId)!;
                AnsiConsole.MarkupLine($"[green]태스크 완료: {Markup.Escape(task.Title)}[/]");
                _logger.TaskEnd(taskId, "completed");
            }
        }
        finally
        {
            // 5. worktree 정리
            AnsiConsole.MarkupLine("\n[dim]Worktree 정리 중...[/]");
            foreach (var taskId in worktrees.Keys)
            {
                await _worktree.CleanupWorktreeAsync(taskId, _logger, ct);
            }
        }

        return 0;
    }

    /// <summary>
    /// worktree 안에서 태스크를 실행하며 출력을 로그 파일에 기록합니다.
    /// </summary>
    private async Task<bool> RunInWorktreeWithLogAsync(
        string taskId, string worktreePath, TaskProgressTracker tracker, CancellationToken ct)
    {
        var task = _taskManager.GetTask(taskId)!;
        _logger.TaskStart(taskId, task.Title);
        tracker.UpdateStatus(taskId, TaskProgressStatus.Running);

        const string logDir = ".ralph-logs";
        var logFile = Path.GetFullPath(Path.Combine(logDir, $"{taskId}.log"));

        try
        {
            await using var logWriter = new StreamWriter(logFile, append: false) { AutoFlush = true };
            await logWriter.WriteLineAsync($"=== Task: {taskId} - {task.Title} ===");
            await logWriter.WriteLineAsync($"=== Started: {DateTime.Now} ===\n");

            if (!string.IsNullOrEmpty(task.Prompt))
            {
                var fullPrompt = BuildPrompt(task);

                var result = await _claude.RunWithRetryAsync(
                    fullPrompt, workingDirectory: worktreePath, logger: _logger,
                    output: logWriter, ct: ct);

                if (!result.Success)
                {
                    tracker.UpdateStatus(taskId, TaskProgressStatus.Failed);
                    await logWriter.WriteLineAsync($"\n=== FAILED (exit code: {result.ExitCode}) ===");
                    _logger.TaskEnd(taskId, "failed");
                    return false;
                }
            }

            // worktree 안에서 커밋
            if (_taskManager.CommitOnComplete)
            {
                await _git.CommitChangesAsync(
                    taskId, task.Title, _taskManager.CommitTemplate,
                    _logger, worktreePath, silent: true, ct: ct);
            }

            tracker.UpdateStatus(taskId, TaskProgressStatus.Completed);
            await logWriter.WriteLineAsync($"\n=== Completed: {DateTime.Now} ===");
            _logger.TaskEnd(taskId, "completed-in-worktree");
            return true;
        }
        catch (Exception ex)
        {
            tracker.UpdateStatus(taskId, TaskProgressStatus.Failed);
            _logger.Error($"Task {taskId} failed in worktree: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Merge 충돌을 처리합니다.
    /// </summary>
    private async Task<bool> HandleMergeConflictAsync(
        string taskId, string baseBranch, MergeResult mergeResult,
        string strategy, CancellationToken ct)
    {
        switch (strategy)
        {
            case "claude":
                return await ResolveConflictsWithClaudeAsync(taskId, mergeResult, ct);

            case "abort":
                await _worktree.AbortMergeAsync(ct);
                AnsiConsole.MarkupLine($"[yellow]병합 중단. {Markup.Escape(taskId)}를 순차 모드로 재실행합니다...[/]");
                _logger.Warn($"Merge aborted for {taskId}, falling back to sequential");

                // 순차 모드로 재실행
                return await RunSingleTaskAsync(taskId, ct) == 0;

            case "auto-theirs":
            case "auto-ours":
                // 이미 merge 명령에 전략이 포함되어 있으므로 여기에 올 경우 실패
                await _worktree.AbortMergeAsync(ct);
                _logger.Error($"Merge with strategy {strategy} failed for {taskId}");
                return false;

            default:
                await _worktree.AbortMergeAsync(ct);
                return false;
        }
    }

    /// <summary>
    /// Claude를 사용하여 merge 충돌을 해결합니다.
    /// </summary>
    private async Task<bool> ResolveConflictsWithClaudeAsync(
        string taskId, MergeResult mergeResult, CancellationToken ct)
    {
        if (mergeResult.ConflictFiles is not { Count: > 0 })
        {
            await _worktree.AbortMergeAsync(ct);
            return false;
        }

        var conflictList = string.Join("\n", mergeResult.ConflictFiles.Select(f => $"  - {f}"));
        var prompt = $"""
            다음 git merge 충돌을 해결해주세요.

            태스크: {taskId}
            충돌 파일:
            {conflictList}

            각 충돌 파일을 열어서 충돌 마커(<<<<<<< HEAD, =======, >>>>>>> branch)를 찾고,
            양쪽의 변경사항을 모두 살리는 방향으로 해결해주세요.
            해결 후 파일을 저장해주세요.
            """;

        AnsiConsole.MarkupLine($"[cyan]Claude Code로 충돌 해결 중 ({mergeResult.ConflictFiles.Count}개 파일)...[/]");

        var result = await _claude.RunWithRetryAsync(prompt, logger: _logger, ct: ct);
        if (!result.Success)
        {
            await _worktree.AbortMergeAsync(ct);
            return false;
        }

        // 해결된 파일 staging
        foreach (var file in mergeResult.ConflictFiles)
        {
            await _git.RunAsync(["add", file], ct: ct);
        }

        // merge commit 완료
        var (exitCode, _) = await _git.RunAsync(
            ["commit", "--no-edit"], ct: ct);

        if (exitCode == 0)
        {
            AnsiConsole.MarkupLine($"[green]충돌 해결 완료: {Markup.Escape(taskId)}[/]");
            _logger.Info($"Conflict resolved via Claude for {taskId}");
            return true;
        }

        await _worktree.AbortMergeAsync(ct);
        return false;
    }

    /// <summary>
    /// thread-safe하게 태스크를 완료 상태로 변경합니다.
    /// </summary>
    private async Task MarkTaskDoneThreadSafe(string taskId, CancellationToken ct)
    {
        await _taskFileLock.WaitAsync(ct);
        try
        {
            await _taskManager.ReloadAsync();
            var task = _taskManager.GetTask(taskId)!;

            // subtasks 처리
            ProcessSubtasks(task, taskId);

            _taskManager.MarkTaskDone(taskId);
            await _taskManager.SaveAsync();
        }
        finally
        {
            _taskFileLock.Release();
        }
    }

    private void ProcessSubtasks(TaskItem task, string taskId)
    {
        if (task.Subtasks is not { Count: > 0 }) return;
        foreach (var sub in task.Subtasks.Where(s => !s.Done))
        {
            _taskManager.MarkSubtaskDone(taskId, sub.Id);
        }
    }

    private string BuildPrompt(TaskItem task)
    {
        return $"""
            Task ID: {task.Id}
            Task: {task.Title}

            {task.Prompt}

            참고: {_tasksFile} 파일에서 apiSpecs, samplePages 등 추가 정보를 확인할 수 있습니다.
            완료 후 생성된 파일 목록을 알려주세요.
            """;
    }

    private void DisplayTaskInfo(string taskId)
    {
        var task = _taskManager.GetTask(taskId)!;
        var index = _taskManager.GetTaskIndex(taskId);
        var total = _taskManager.Data.Tasks.Count;

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule().RuleStyle("blue"));
        AnsiConsole.MarkupLine(
            $"[yellow][[{index}/{total}]][/] [green]Task ID:[/] {Markup.Escape(task.Id)}");
        AnsiConsole.MarkupLine(
            $"[green]Phase:[/] {Markup.Escape(task.Phase ?? "-")} | [green]Category:[/] {Markup.Escape(task.Category ?? "-")}");
        AnsiConsole.MarkupLine($"[green]Title:[/] {Markup.Escape(task.Title)}");
        AnsiConsole.Write(new Rule().RuleStyle("blue"));
    }
}
