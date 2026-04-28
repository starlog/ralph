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
    private readonly string? _model;
    private readonly CostTracker _cost;
    private readonly SemaphoreSlim _taskFileLock = new(1, 1);

    public ParallelExecutor(
        TaskManager taskManager, ClaudeService claude, GitService git,
        WorktreeService worktree, RalphLogger logger, string tasksFile, string? model = null)
    {
        _taskManager = taskManager;
        _claude = claude;
        _git = git;
        _worktree = worktree;
        _logger = logger;
        _tasksFile = tasksFile;
        _model = model;
        _cost = new CostTracker();
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

            var result = await _claude.RunWithRetryAsync(fullPrompt, model: _model, logger: _logger, ct: ct);
            await _cost.RecordAsync(taskId, _model ?? "opus", result, ct);
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

                    // 병렬 실행 — 각 태스크에 같은 batch의 sibling 정보 전달
                    var execTasks = taskIds.Select(async taskId =>
                    {
                        var siblings = taskIds
                            .Where(id => id != taskId)
                            .Select(id => _taskManager.GetTask(id))
                            .Where(t => t != null)
                            .Select(t => t!)
                            .ToList();
                        var success = await RunInWorktreeWithLogAsync(
                            taskId, worktrees[taskId], siblings, tracker, ct);
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
                {
                    AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(f)}");
                    AnsiConsole.MarkupLine($"    [dim]로그 확인: ralph --logs {Markup.Escape(f)}[/]");
                }

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

                // F2: 머지 직전 worktree의 tasks.json이 baseBranch와 다르면 강제 정규화.
                // 1차 방어(GuardTasksFileAsync)는 working-tree만 보지만, 본 단계는
                // worktree HEAD까지 검사하여 commit-tree 위반을 잡는다.
                await _worktree.NormalizeTasksJsonAsync(
                    taskId, baseBranch,
                    tasksFileName: Path.GetFileName(_tasksFile),
                    logger: _logger,
                    ct: ct);

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

            // 5. tasks.json 변경사항 커밋 (다음 배치 병합 시 충돌 방지)
            await CommitTasksFileAsync(taskIds, ct);
        }
        finally
        {
            // 6. worktree 정리
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
        string taskId, string worktreePath, IReadOnlyList<TaskItem> siblings,
        TaskProgressTracker tracker, CancellationToken ct)
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
                var fullPrompt = BuildPrompt(task, siblings);

                var result = await _claude.RunWithRetryAsync(
                    fullPrompt, model: _model, workingDirectory: worktreePath, logger: _logger,
                    output: logWriter, ct: ct);
                await _cost.RecordAsync(taskId, _model ?? "opus", result, ct);

                if (!result.Success)
                {
                    tracker.UpdateStatus(taskId, TaskProgressStatus.Failed);
                    await logWriter.WriteLineAsync($"\n=== FAILED (exit code: {result.ExitCode}) ===");
                    _logger.TaskEnd(taskId, "failed");
                    return false;
                }
            }

            // tasks.json worktree 보호: Claude가 실수로 또는 prompt를 무시하고
            // tasks.json을 수정했을 가능성을 방어. 머지 충돌의 가장 흔한 원인.
            await GuardTasksFileAsync(taskId, worktreePath, logWriter, ct);

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
    /// worktree에서 tasks.json이 수정되었으면 강제로 되돌립니다.
    /// Claude가 prompt 지시를 무시하거나 보조 작업으로 tasks.json을 건드린 경우의 안전망.
    /// 머지 단계에서 tasks.json 충돌(가장 흔한 충돌 케이스)을 사전 차단합니다.
    /// </summary>
    private async Task GuardTasksFileAsync(
        string taskId, string worktreePath, TextWriter? logWriter, CancellationToken ct)
    {
        var tasksFileName = Path.GetFileName(_tasksFile);

        // worktree 내 tasks.json 변경 여부 검사
        var (statusExit, statusOut) = await _git.RunAsync(
            ["status", "--porcelain", "--", tasksFileName], worktreePath, ct);

        if (statusExit != 0 || string.IsNullOrWhiteSpace(statusOut))
            return; // 변경 없음

        var changeCode = statusOut.Length >= 2 ? statusOut[..2] : "";
        var msg = $"⚠️  worktree '{taskId}'에서 {tasksFileName}이 수정되었습니다 (status: '{changeCode.Trim()}'). 강제 되돌립니다.";
        _logger.Warn(msg);
        logWriter?.WriteLine($"\n=== {msg} ===");

        // staged 변경이 있으면 unstage
        await _git.RunAsync(["reset", "HEAD", "--", tasksFileName], worktreePath, ct);

        // 추적 중인 파일이면 HEAD 버전으로 복원
        if (changeCode.Contains('M') || changeCode.Contains('D') || changeCode.Contains('R'))
        {
            await _git.RunAsync(["checkout", "HEAD", "--", tasksFileName], worktreePath, ct);
        }
        // 새로 추가된 파일이면 (?? 또는 A) 작업트리에서 제거
        else if (changeCode.Contains('?') || changeCode.Contains('A'))
        {
            var fullPath = Path.Combine(worktreePath, tasksFileName);
            try { if (File.Exists(fullPath)) File.Delete(fullPath); }
            catch (Exception ex) { _logger.Warn($"Failed to delete {fullPath}: {ex.Message}"); }
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

        // base repo 루트를 작업 디렉토리로 명시 (Claude가 ralph 호출 위치가 아닌
        // 머지가 진행 중인 repo 루트에서 충돌 마커를 찾도록 보장)
        var repoRoot = await _git.GetRepoRootAsync(ct: ct);

        var conflictList = string.Join("\n", mergeResult.ConflictFiles.Select(f => $"  - {f}"));
        var prompt = $"""
            다음 git merge 충돌을 해결해주세요.

            작업 디렉토리: {repoRoot}
            태스크: {taskId}
            충돌 파일 (repo 루트 기준 상대 경로):
            {conflictList}

            지시:
            1. `git status`로 현재 충돌 상태를 확인하세요.
            2. 위 각 파일을 열어 충돌 마커(<<<<<<< HEAD, =======, >>>>>>> branch)를 모두 제거하세요.
            3. 양쪽 변경사항을 모두 살리는 방향으로 통합하세요.
            4. 마커가 남아있는지 검증한 뒤 파일을 저장하세요. (마커가 남아있으면 빌드/실행이 깨집니다)

            staging과 commit은 ralph가 처리하므로 git add/commit은 실행하지 마세요.
            """;

        AnsiConsole.MarkupLine($"[cyan]Claude Code로 충돌 해결 중 ({mergeResult.ConflictFiles.Count}개 파일, repo: {Markup.Escape(repoRoot)})...[/]");

        var result = await _claude.RunWithRetryAsync(
            prompt, model: _model, workingDirectory: repoRoot, logger: _logger, ct: ct);
        await _cost.RecordAsync($"conflict:{taskId}", _model ?? "opus", result, ct);
        if (!result.Success)
        {
            await _worktree.AbortMergeAsync(ct);
            return false;
        }

        // 해결된 파일에 충돌 마커가 남아있는지 검증
        foreach (var file in mergeResult.ConflictFiles)
        {
            var fullPath = Path.IsPathRooted(file) ? file : Path.Combine(repoRoot, file);
            if (File.Exists(fullPath))
            {
                var content = await File.ReadAllTextAsync(fullPath, ct);
                if (content.Contains("<<<<<<<") || content.Contains(">>>>>>>"))
                {
                    AnsiConsole.MarkupLine($"[red]충돌 마커가 여전히 남아있음: {Markup.Escape(file)}[/]");
                    _logger.Error($"Conflict markers remain in {file} after Claude resolution");
                    await _worktree.AbortMergeAsync(ct);
                    return false;
                }
            }
        }

        // 해결된 파일 staging (repo 루트 기준)
        foreach (var file in mergeResult.ConflictFiles)
        {
            await _git.RunAsync(["add", "--", file], workingDirectory: repoRoot, ct: ct);
        }

        // merge commit 완료
        var (exitCode, _) = await _git.RunAsync(
            ["commit", "--no-edit"], workingDirectory: repoRoot, ct: ct);

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
    /// tasks.json 변경사항(done 상태 업데이트)을 커밋합니다.
    /// 다음 배치의 worktree 병합 시 충돌을 방지합니다.
    /// </summary>
    private async Task CommitTasksFileAsync(List<string> completedTaskIds, CancellationToken ct)
    {
        var (exitCode, _) = await _git.RunAsync(["add", _tasksFile], ct: ct);
        if (exitCode != 0) return;

        var taskList = string.Join(", ", completedTaskIds);
        var commitMsg = $"chore: 태스크 상태 업데이트 ({taskList})";

        (exitCode, _) = await _git.RunAsync(
            ["commit", "-m", commitMsg], ct: ct);

        if (exitCode == 0)
        {
            _logger.Info($"Tasks file committed: {taskList}");
        }
        else
        {
            _logger.Warn("No tasks file changes to commit");
        }
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

    private string BuildPrompt(TaskItem task, IReadOnlyList<TaskItem>? siblings = null)
        => PromptBuilder.Build(task, _taskManager, _tasksFile, siblings);

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
