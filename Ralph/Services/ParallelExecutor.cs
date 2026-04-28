using Ralph.Models;
using Spectre.Console;

namespace Ralph.Services;

public class ParallelExecutor
{
    private readonly TaskManager _taskManager;
    private readonly IAgentRunner _claude;
    private readonly GitService _git;
    private readonly WorktreeService _worktree;
    private readonly RalphLogger _logger;
    private readonly string _tasksFile;
    private readonly string? _model;
    private readonly bool _strictFiles;
    private readonly bool _sharedWorktrees;
    private readonly bool _noSmokeTest;
    private readonly CostTracker _cost;
    private readonly BudgetGate _budgetGate;
    private readonly VerificationRunner _verifier = new();
    private readonly SemaphoreSlim _taskFileLock = new(1, 1);
    private int _cleanupFailures;

    /// <summary>
    /// verification 실패 시 self-fix 재시도 횟수. workflow.verifyRetries > 1(기본).
    /// </summary>
    private int VerifyRetries => Math.Max(0, _taskManager.Data.Workflow?.VerifyRetries ?? 1);

    /// <summary>
    /// budget(USD) 임계값 도달로 새 dispatch를 차단했는지 여부.
    /// 호출자가 확인해 종료 코드 2를 결정할 수 있다.
    /// </summary>
    public bool BudgetReached => _budgetGate.Reached;

    public ParallelExecutor(
        TaskManager taskManager, IAgentRunner claude, GitService git,
        WorktreeService worktree, RalphLogger logger, string tasksFile, string? model = null,
        bool strictFiles = false, double? budgetUsd = null,
        CostTracker? cost = null, BudgetGate? budgetGate = null,
        bool sharedWorktrees = false, bool noSmokeTest = false)
    {
        _taskManager = taskManager;
        _claude = claude;
        _git = git;
        _worktree = worktree;
        _logger = logger;
        _tasksFile = tasksFile;
        _model = model;
        _strictFiles = strictFiles;
        _sharedWorktrees = sharedWorktrees;
        _noSmokeTest = noSmokeTest;
        _cost = cost ?? new CostTracker();
        _budgetGate = budgetGate ?? new BudgetGate(budgetUsd, _cost, logger);
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

        // worktree 사용을 위해 최소 1개의 커밋이 필요 (base branch 식별 전에 보장)
        await _git.EnsureInitialCommitAsync(_logger, ct);

        var baseBranch = await _git.GetCurrentBranchAsync(ct: ct);
        _logger.Info($"Parallel execution starting on branch: {baseBranch}");

        // mid-task 잔존 worktree 검출. uncommitted 변경 또는 base 위로 진행된 커밋이 있으면
        // 이전 실행이 중단된 상태일 수 있다 — 사용자 확인 없이 silently 삭제하지 않는다.
        var midTask = await _worktree.DetectMidTaskWorktreesAsync(baseBranch, ct);
        if (midTask.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"\n[yellow]⚠ 중단된 작업으로 보이는 worktree {midTask.Count}개 감지:[/]");
            foreach (var w in midTask)
            {
                var ahead = w.AheadCount > 0 ? $"{w.AheadCount} commit(s) ahead" : "no commits";
                var dirty = w.HasUncommitted ? "uncommitted 변경 있음" : "clean";
                AnsiConsole.MarkupLine(
                    $"  [dim]•[/] [cyan]{Markup.Escape(w.TaskId)}[/]  [dim]({ahead}, {dirty})[/]");
                AnsiConsole.MarkupLine($"    [dim]{Markup.Escape(w.WorktreePath)}[/]");
            }
            AnsiConsole.MarkupLine(
                "[yellow]자동 정리하지 않습니다. 직접 머지/회수하거나, [cyan]ralph --worktree-cleanup[/]으로 강제 삭제하세요.[/]");
            _logger.Warn(
                $"Mid-task worktrees detected: {string.Join(", ", midTask.Select(m => m.TaskId))} — leaving in place");
            return 1;
        }

        // mid-task가 아닌 잔존 worktree(이전 실행에서 cleanup 누락된 빈 worktree)는 자동 정리
        var stale = await _worktree.DetectStaleWorktreesAsync(ct);
        if (stale.Count > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]잔존 worktree {stale.Count}개 감지 (clean). 정리합니다...[/]");
            await _worktree.CleanupAllAsync(_logger, ct);
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // F5: budget 게이트 — 새 dispatch 직전에 검사. 차단 시 break (호출자가 종료 코드 2로 변환).
            if (!await _budgetGate.CheckAsync(ct)) break;

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
            AnsiConsole.MarkupLine("[cyan]Prompt:[/]");
            AnsiConsole.Write(new Panel(Markup.Escape(task.Prompt)).Border(BoxBorder.Rounded));
            AnsiConsole.MarkupLine("\n[cyan]Running Claude Code...[/]\n");

            // verification까지 포함한 retry 루프. cwd는 repo 루트(worktree 없음).
            var ok = await RunSingleWithVerificationAsync(task, ct);
            if (!ok)
            {
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
        var strategyChain = _taskManager.ParallelConfig.GetStrategyChain();
        var primaryStrategy = strategyChain[0]; // 첫 항목으로 merge -X 결정
        var worktrees = new Dictionary<string, string>(); // taskId → worktreePath
        var tracker = new TaskProgressTracker();
        tracker.AttachCostTracker(_cost, _budgetGate.BudgetUsd);

        try
        {
            // 1. 모든 worktree 생성 및 tracker 등록
            AnsiConsole.MarkupLine("\n[blue]Worktree 생성 중...[/]");
            const string logDir = ".ralph-logs";
            Directory.CreateDirectory(logDir);

            foreach (var taskId in taskIds)
            {
                var path = await _worktree.CreateWorktreeAsync(
                    taskId, baseBranch, _logger, sharedObjects: _sharedWorktrees, ct: ct);
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
                            await tracker.RefreshCostAsync(ct);
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

                // 실패한 worktree 정리 (실패는 _cleanupFailures로 누적)
                foreach (var f in failed)
                {
                    if (!await _worktree.CleanupWorktreeAsync(f, _logger, ct))
                        _cleanupFailures++;
                }

                // 성공한 것만 merge 진행
                taskIds = taskIds.Except(failed).ToList();
                if (taskIds.Count == 0) return 1;
            }

            // 3. 순차적으로 메인에 병합. Live scope는 이미 종료되었으므로 tracker.UpdateStatus는
            //    화면에 반영되지 않는다. 머지는 본질적으로 sequential이므로 진행률만 콘솔로 표시.
            AnsiConsole.MarkupLine(
                $"\n[blue]메인 브랜치에 병합 중...[/] [dim]({taskIds.Count}개 태스크)[/]");

            var mergeIdx = 0;
            foreach (var taskId in taskIds)
            {
                mergeIdx++;
                AnsiConsole.MarkupLine(
                    $"  [dim][[{mergeIdx}/{taskIds.Count}]][/] {Markup.Escape(taskId)}");

                // F2: 머지 직전 worktree의 tasks.json이 baseBranch와 다르면 강제 정규화.
                // 1차 방어(GuardTasksFileAsync)는 working-tree만 보지만, 본 단계는
                // worktree HEAD까지 검사하여 commit-tree 위반을 잡는다.
                await _worktree.NormalizeTasksJsonAsync(
                    taskId, baseBranch,
                    tasksFileName: Path.GetFileName(_tasksFile),
                    logger: _logger,
                    ct: ct);

                // F4: declared(modifiedFiles ∪ outputFiles) vs actual(base..HEAD) 검증.
                // F2 정규화 "이후"에 호출해야 tasks.json 정규화 결과가 actual에서 빠진다.
                var declared = BuildDeclaredSet(_taskManager.GetTask(taskId)!);
                var validation = await _worktree.ValidateModifiedFilesAsync(
                    taskId, baseBranch, declared, _logger, ct: ct);

                ReportValidation(taskId, validation);

                // P0-3: strict 모드에서 diff 자체가 실패하면 검증 우회되는 효과를 주지 않도록 머지 차단.
                if (_strictFiles && validation.DiffFailed)
                {
                    AnsiConsole.MarkupLine(
                        $"  [red]✗[/] {Markup.Escape(taskId)} diff 실패로 검증 불가. " +
                        $"머지 중단 (strict-files).");
                    _logger.Error(
                        $"[validate:files][strict] {taskId} diff failed: {validation.DiffError}");
                    return 1;
                }

                if (_strictFiles && validation.HasUndeclared)
                {
                    var preview = string.Join(", ", validation.Undeclared.Take(3));
                    var more = validation.Undeclared.Count > 3
                        ? $" (외 {validation.Undeclared.Count - 3}건)" : "";
                    AnsiConsole.MarkupLine(
                        $"  [red]✗[/] {Markup.Escape(taskId)} undeclared 파일 {validation.Undeclared.Count}건. " +
                        $"머지 중단 (strict-files): {Markup.Escape(preview + more)}");
                    _logger.Error(
                        $"[validate:files][strict] {taskId} undeclared: " +
                        string.Join(", ", validation.Undeclared));
                    // finally 블록이 worktree 정리. 잔여 태스크의 done 마킹은 일어나지 않음.
                    return 1;
                }

                // 머지 직전 worktree를 현재 baseBranch 위로 rebase. 같은 batch의 앞선 머지로
                // baseBranch가 advance되어 후속 worktree가 옛 분기점에서 머지될 때 발생하는
                // 공유 파일 충돌을 줄인다. 실패 시 abort 후 기존 3-way merge로 fallback(회귀 없음).
                await _worktree.AdvanceWorktreeOntoBaseAsync(taskId, baseBranch, _logger, ct);

                var mergeResult = await _worktree.MergeWorktreeAsync(
                    taskId, baseBranch, primaryStrategy, _logger, ct);

                if (mergeResult.Success)
                {
                    AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(taskId)} 병합 완료");
                }
                else
                {
                    AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(taskId)} 병합 충돌!");

                    var resolved = await HandleMergeConflictAsync(
                        taskId, baseBranch, mergeResult, strategyChain, ct);

                    if (!resolved)
                    {
                        _logger.Error($"Merge conflict unresolved for {taskId}");
                        // 나머지 태스크 정리
                        foreach (var remaining in taskIds)
                        {
                            if (!await _worktree.CleanupWorktreeAsync(remaining, _logger, ct))
                                _cleanupFailures++;
                        }
                        return 1;
                    }
                }
            }

            // 4. 상태 업데이트 (thread-safe). P1-3: 개별 태스크의 ReloadAsync/Save 예외가 전체
            //    배치를 폭파시키지 않도록 격리 — 머지는 이미 성공했으므로 다음 태스크 마킹은 계속 시도.
            foreach (var taskId in taskIds)
            {
                try
                {
                    await MarkTaskDoneThreadSafe(taskId, ct);
                    var task = _taskManager.GetTask(taskId)!;
                    AnsiConsole.MarkupLine($"[green]태스크 완료: {Markup.Escape(task.Title)}[/]");
                    _logger.TaskEnd(taskId, "completed");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]✗[/] {Markup.Escape(taskId)} done 마킹 실패: {Markup.Escape(ex.Message)}");
                    _logger.Error($"MarkTaskDone failed for {taskId}: {ex.Message}");
                }
            }

            // 5. tasks.json 변경사항 커밋 (다음 배치 병합 시 충돌 방지)
            await CommitTasksFileAsync(taskIds, ct);

            // 5.5 머지 후 smoke test (workflow.smokeTest 설정 시). claude conflict 전략이나
            // auto-* 머지로 인한 semantic 정합성 깨짐을 잡는 단계. 실패 시 호출자에게 신호.
            if (await RunPostMergeSmokeTestAsync(ct) is { } smokeFail)
                return smokeFail;
        }
        finally
        {
            // 6. worktree 정리
            AnsiConsole.MarkupLine("\n[dim]Worktree 정리 중...[/]");
            foreach (var taskId in worktrees.Keys)
            {
                if (!await _worktree.CleanupWorktreeAsync(taskId, _logger, CancellationToken.None))
                    _cleanupFailures++;
            }

            // P1-4: 정리 실패가 누적되면 사용자에게 명시적으로 안내
            if (_cleanupFailures > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]⚠ worktree 정리 실패 {_cleanupFailures}건. " +
                    $"다음 명령으로 강제 정리하세요: [cyan]ralph --worktree-cleanup[/][/]");
                _logger.Warn($"Cleanup failures accumulated: {_cleanupFailures}");
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
                var ok = await RunPromptWithVerificationAsync(
                    task, siblings, worktreePath, logWriter, tracker, ct);
                if (!ok) return false;
            }

            // tasks.json worktree 보호: Claude가 실수로 또는 prompt를 무시하고
            // tasks.json을 수정했을 가능성을 방어. 머지 충돌의 가장 흔한 원인.
            await GuardTasksFileAsync(taskId, worktreePath, logWriter, ct);

            // F4-pre: 워크트리 staging 직전 scope 위반 검사. base..HEAD가 아닌 working-tree
            // 변경(`git status --porcelain`) 전체를 본다. 이 시점에서 declared 외 변경이
            // 검출되면 declaredFiles 필터에 의해 silently 사라지는 것을 막을 수 있다.
            // strict-files 모드에서는 worktree 단계에서 fail-fast해 머지/cleanup 비용을 절약.
            if (!await PreCommitScopeGuardAsync(task, worktreePath, logWriter, tracker, ct))
                return false;

            // worktree 안에서 커밋. declared 파일만 staging해서 격리 보장
            // (선언 안 된 worktree 변경 — 예: __pycache__, 다른 task의 파일 — 머지 표면에서 제외).
            // declared가 비어있으면 fallback으로 -A 사용 (legacy 동작).
            if (_taskManager.CommitOnComplete)
            {
                var declared = BuildDeclaredSet(task);
                await _git.CommitChangesAsync(
                    taskId, task.Title, _taskManager.CommitTemplate,
                    _logger, worktreePath, silent: true,
                    declaredFiles: declared, ct: ct);
            }

            tracker.UpdateStatus(taskId, TaskProgressStatus.Completed);
            await logWriter.WriteLineAsync($"\n=== Completed: {DateTime.Now} ===");
            _logger.TaskEnd(taskId, "completed-in-worktree");
            return true;
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C/취소를 task 실패로 변환하면 outer Task.WhenAll이 정상 완료처럼 보여
            // 후속 merge/cleanup 단계가 cancel을 무시한 채 계속 진행됨. 반드시 propagate.
            tracker.UpdateStatus(taskId, TaskProgressStatus.Failed);
            _logger.Warn($"Task {taskId} canceled in worktree");
            throw;
        }
        catch (Exception ex)
        {
            tracker.UpdateStatus(taskId, TaskProgressStatus.Failed);
            _logger.Error($"Task {taskId} failed in worktree: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 단일 태스크용(no worktree, console output) verification retry 루프.
    /// </summary>
    private async Task<bool> RunSingleWithVerificationAsync(TaskItem task, CancellationToken ct)
    {
        var maxVerifyRetries = VerifyRetries;
        var basePrompt = BuildPrompt(task);
        string? failureCtx = null;

        for (var attempt = 0; attempt <= maxVerifyRetries; attempt++)
        {
            var fullPrompt = failureCtx == null
                ? basePrompt
                : $"{failureCtx}\n\n---\n\n{basePrompt}";

            ClaudeResult? result = null;
            try
            {
                result = await _claude.RunWithRetryAsync(
                    fullPrompt, model: _model, logger: _logger, ct: ct);
            }
            finally
            {
                await _cost.RecordAsync(task.Id, _model ?? "opus", result, CancellationToken.None);
            }

            if (result == null || !result.Success)
            {
                AnsiConsole.MarkupLine("\n[red]Claude Code 실행 실패[/]");
                return false;
            }

            if (task.Verification is not { } spec || string.IsNullOrWhiteSpace(spec.Command))
                return true;

            AnsiConsole.MarkupLine($"\n[cyan]검증 명령 실행:[/] [dim]{Markup.Escape(spec.Command)}[/]");
            var verify = await _verifier.RunAsync(spec, Directory.GetCurrentDirectory(), _logger, output: null, ct);

            if (verify.Success)
            {
                AnsiConsole.MarkupLine($"[green]✓ 검증 통과[/] ({verify.Duration.TotalSeconds:F1}s)");
                return true;
            }

            if (attempt >= maxVerifyRetries)
            {
                AnsiConsole.MarkupLine(
                    $"[red]✗ 검증 실패[/] (exit={verify.ExitCode}{(verify.TimedOut ? ", TIMEOUT" : "")}, {attempt + 1}회 시도)");
                if (!string.IsNullOrWhiteSpace(verify.Stderr))
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape(verify.Stderr.Trim())}[/]");
                return false;
            }

            AnsiConsole.MarkupLine(
                $"[yellow]⚠ 검증 실패, Claude에게 수정 요청 ({attempt + 1}/{maxVerifyRetries} retry)[/]");
            failureCtx = VerificationRunner.BuildFailureContext(spec.Command, verify);
        }

        return false;
    }

    /// <summary>
    /// Claude 실행 + (선택) 외부 verification 명령 실행. verification 실패 시 stdout/stderr를
    /// 다음 시도 prompt에 prepend해 self-fix 시도. workflow.verifyRetries 횟수만큼 반복하며
    /// 모두 실패하면 false 반환.
    /// </summary>
    private async Task<bool> RunPromptWithVerificationAsync(
        TaskItem task, IReadOnlyList<TaskItem> siblings, string workingDirectory,
        TextWriter logWriter, TaskProgressTracker tracker, CancellationToken ct)
    {
        var maxVerifyRetries = VerifyRetries;
        string? failureCtx = null;

        for (var attempt = 0; attempt <= maxVerifyRetries; attempt++)
        {
            var basePrompt = BuildPrompt(task, siblings);
            var fullPrompt = failureCtx == null
                ? basePrompt
                : $"{failureCtx}\n\n---\n\n{basePrompt}";

            // P0-1: RunWithRetryAsync가 예외(취소 등)로 throw해도 cost는 try/finally로 기록.
            ClaudeResult? result = null;
            try
            {
                result = await _claude.RunWithRetryAsync(
                    fullPrompt, model: _model, workingDirectory: workingDirectory, logger: _logger,
                    output: logWriter, ct: ct);
            }
            finally
            {
                await _cost.RecordAsync(task.Id, _model ?? "opus", result, CancellationToken.None);
            }

            if (result == null || !result.Success)
            {
                tracker.UpdateStatus(task.Id, TaskProgressStatus.Failed);
                var exitInfo = result?.ExitCode.ToString() ?? "?";
                await logWriter.WriteLineAsync($"\n=== FAILED (exit code: {exitInfo}) ===");
                _logger.TaskEnd(task.Id, "failed");
                return false;
            }

            // verification 미설정 → Claude success로 통과
            if (task.Verification is not { } spec || string.IsNullOrWhiteSpace(spec.Command))
                return true;

            var verify = await _verifier.RunAsync(spec, workingDirectory, _logger, logWriter, ct);
            if (verify.Success) return true;

            if (attempt >= maxVerifyRetries)
            {
                AnsiConsole.MarkupLine(
                    $"  [red]✗[/] {Markup.Escape(task.Id)} verification 실패 " +
                    $"(exit={verify.ExitCode}{(verify.TimedOut ? ", TIMEOUT" : "")}, {attempt + 1}회 시도)");
                tracker.UpdateStatus(task.Id, TaskProgressStatus.Failed);
                _logger.Error(
                    $"[verification] {task.Id} failed exit={verify.ExitCode} timedOut={verify.TimedOut} " +
                    $"after {attempt + 1} attempt(s)");
                return false;
            }

            AnsiConsole.MarkupLine(
                $"  [yellow]⚠[/] {Markup.Escape(task.Id)} verification 실패 → Claude에게 수정 요청 ({attempt + 1}/{maxVerifyRetries} retry)");
            _logger.Warn(
                $"[verification] {task.Id} failed (attempt {attempt + 1}); retrying with failure context");
            failureCtx = VerificationRunner.BuildFailureContext(spec.Command, verify);
        }

        return false; // unreachable
    }

    /// <summary>
    /// Claude 실행 직후 staging 직전, worktree의 working-tree 변경 전체와 declared 집합을 비교한다.
    /// 새 파일/수정/삭제(staged·unstaged·untracked) 모두 보고 declared 외면 warn-only(또는 strict-files면 fail).
    /// commit 이후의 base..HEAD 검증과 보완 관계 — 이쪽은 staging 필터에 의해 사라지기 전 raw 변경을 본다.
    /// tasks.json은 별도 GuardTasksFileAsync가 정규화하므로 검사에서 제외.
    /// </summary>
    private async Task<bool> PreCommitScopeGuardAsync(
        TaskItem task, string worktreePath, TextWriter logWriter,
        TaskProgressTracker tracker, CancellationToken ct)
    {
        var (statusExit, statusOut) = await _git.RunAsync(
            ["status", "--porcelain"], worktreePath, ct);
        if (statusExit != 0)
        {
            await logWriter.WriteLineAsync($"\n=== [scope-guard] git status 실패 — skip ===");
            _logger.Warn($"[scope-guard] {task.Id}: git status 실패 — 검사 스킵");
            return true; // diff 실패가 머지를 막지 않게(F4와 동일 정책)
        }
        if (string.IsNullOrWhiteSpace(statusOut)) return true;

        var declared = BuildDeclaredSet(task);
        var declaredSet = new HashSet<string>(
            declared.Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(p => p.Replace('\\', '/').Trim()),
            StringComparer.Ordinal);

        var tasksFileName = Path.GetFileName(_tasksFile);
        var changed = new List<string>();
        foreach (var line in statusOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // porcelain v1: "XY path" 또는 "XY orig -> new" (rename)
            if (line.Length < 4) continue;
            var rest = line[3..];
            string path;
            var arrowIdx = rest.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrowIdx >= 0) path = rest[(arrowIdx + 4)..].Trim();
            else path = rest.Trim();

            // " 안의 따옴표 제거 (renamed/space-containing 경로)
            if (path.Length >= 2 && path[0] == '"' && path[^1] == '"') path = path[1..^1];
            path = path.Replace('\\', '/').Trim();
            if (path.Length == 0) continue;
            if (string.Equals(path, tasksFileName, StringComparison.Ordinal)) continue;
            changed.Add(path);
        }

        var undeclared = changed
            .Where(p => !declaredSet.Contains(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (undeclared.Count == 0) return true;

        var preview = string.Join(", ", undeclared.Take(5));
        var more = undeclared.Count > 5 ? $" (외 {undeclared.Count - 5}건)" : "";

        if (_strictFiles)
        {
            await logWriter.WriteLineAsync(
                $"\n=== [scope-guard] STRICT FAIL: undeclared {undeclared.Count}건 — {preview}{more} ===");
            tracker.UpdateStatus(task.Id, TaskProgressStatus.Failed);
            _logger.Error(
                $"[scope-guard][strict] {task.Id} undeclared {undeclared.Count}건: {string.Join(", ", undeclared)}");
            return false;
        }

        await logWriter.WriteLineAsync(
            $"\n=== [scope-guard] WARN: undeclared {undeclared.Count}건 (warn-only) — {preview}{more} ===");
        _logger.Warn(
            $"[scope-guard] {task.Id} undeclared {undeclared.Count}건 (warn-only): {string.Join(", ", undeclared)}");
        return true;
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
        var x = changeCode.Length > 0 ? changeCode[0] : ' ';
        var y = changeCode.Length > 1 ? changeCode[1] : ' ';
        var msg = $"⚠️  worktree '{taskId}'에서 {tasksFileName}이 수정되었습니다 (status: '{changeCode.Trim()}'). 강제 되돌립니다.";
        _logger.Warn(msg);
        logWriter?.WriteLine($"\n=== {msg} ===");

        // staged 변경이 있으면 unstage
        await _git.RunAsync(["reset", "HEAD", "--", tasksFileName], worktreePath, ct);

        // 새로 추가된 파일(untracked '?' 또는 staged add 'A')이면 HEAD에 없으므로 작업트리에서 제거
        if (x == 'A' || x == '?')
        {
            var fullPath = Path.Combine(worktreePath, tasksFileName);
            try { if (File.Exists(fullPath)) File.Delete(fullPath); }
            catch (Exception ex) { _logger.Warn($"Failed to delete {fullPath}: {ex.Message}"); }
        }
        // 추적 중인 파일의 수정/삭제/이름변경이면 HEAD 버전으로 복원
        else if (x == 'M' || x == 'D' || x == 'R' || y == 'M' || y == 'D')
        {
            await _git.RunAsync(["checkout", "HEAD", "--", tasksFileName], worktreePath, ct);
        }
        else
        {
            _logger.Warn($"[GuardTasksFile] {taskId}: 알 수 없는 status '{changeCode}' — 무시");
        }
    }

    /// <summary>
    /// Merge 충돌을 strategy chain으로 순차 시도하여 처리합니다.
    /// chain[0]은 이미 merge 명령에 -X로 적용되어 시도된 상태이며 충돌이 남았다는 뜻이므로
    /// 첫 항목이 auto-*인 경우는 다음 fallback으로 즉시 진행합니다.
    /// claude 항목이 실패하면 다음 fallback으로 계속 진행, abort 항목을 만나면 즉시 sequential 재실행.
    /// </summary>
    private async Task<bool> HandleMergeConflictAsync(
        string taskId, string baseBranch, MergeResult mergeResult,
        IReadOnlyList<string> chain, CancellationToken ct)
    {
        var currentMergeResult = mergeResult;

        for (var i = 0; i < chain.Count; i++)
        {
            var strategy = chain[i];
            var isFirst = i == 0;

            switch (strategy)
            {
                case "claude":
                    AnsiConsole.MarkupLine(
                        $"  [cyan]충돌 해결 시도: claude (전략 {i + 1}/{chain.Count})[/]");
                    if (await ResolveConflictsWithClaudeAsync(taskId, currentMergeResult, ct))
                        return true;
                    AnsiConsole.MarkupLine("  [yellow]claude 해결 실패 — 다음 전략 시도[/]");
                    _logger.Warn($"[merge:chain] {taskId} claude failed at step {i + 1}/{chain.Count}");
                    break;

                case "abort":
                    await _worktree.AbortMergeAsync(ct);
                    AnsiConsole.MarkupLine(
                        $"[yellow]전략 abort (전략 {i + 1}/{chain.Count}): " +
                        $"{Markup.Escape(taskId)}를 순차 모드로 재실행합니다...[/]");
                    _logger.Warn($"[merge:chain] {taskId} abort -> sequential rerun at step {i + 1}");
                    return await RunSingleTaskAsync(taskId, ct) == 0;

                case "auto-theirs":
                case "auto-ours":
                    if (isFirst)
                    {
                        // chain[0]의 -X로 이미 시도되었지만 풀리지 않은 충돌(add/add, rename/delete 등).
                        // 같은 -X를 재시도해도 결과 동일이므로 다음 fallback으로 진행.
                        AnsiConsole.MarkupLine(
                            $"  [yellow]{strategy}로 풀 수 없는 충돌 (add/add, rename/delete 등). 다음 전략 시도[/]");
                        _logger.Warn($"[merge:chain] {taskId} {strategy} (-X) 첫 시도에서 미해결 충돌");
                    }
                    else
                    {
                        // fallback에 등장한 auto-*: abort 후 다른 -X로 재머지 시도
                        await _worktree.AbortMergeAsync(ct);
                        AnsiConsole.MarkupLine(
                            $"  [cyan]전략 {strategy}로 재머지 시도 (전략 {i + 1}/{chain.Count})[/]");
                        var retry = await _worktree.MergeWorktreeAsync(
                            taskId, baseBranch, strategy, _logger, ct);
                        if (retry.Success)
                        {
                            AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(taskId)} {strategy} 재머지 성공");
                            return true;
                        }
                        currentMergeResult = retry; // 후속 claude 시도가 최신 충돌 파일 보도록
                        AnsiConsole.MarkupLine($"  [yellow]{strategy} 재머지 실패 — 다음 전략 시도[/]");
                        _logger.Warn($"[merge:chain] {taskId} {strategy} retry failed at step {i + 1}");
                    }
                    break;

                default:
                    AnsiConsole.MarkupLine(
                        $"  [yellow]알 수 없는 전략 무시: {Markup.Escape(strategy)}[/]");
                    _logger.Warn($"[merge:chain] {taskId} unknown strategy: {strategy}");
                    break;
            }
        }

        AnsiConsole.MarkupLine(
            $"  [red]✗[/] {Markup.Escape(taskId)} 모든 conflict 전략 실패 ({chain.Count}개 시도)");
        _logger.Error($"[merge:chain] {taskId} all {chain.Count} strategies exhausted");
        await _worktree.AbortMergeAsync(ct);
        return false;
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

        // P0-1 패턴: 예외 경로에서도 cost 기록 보장 (CT.None으로 cancel과 분리).
        ClaudeResult? result = null;
        try
        {
            result = await _claude.RunWithRetryAsync(
                prompt, model: _model, workingDirectory: repoRoot, logger: _logger, ct: ct);
        }
        finally
        {
            await _cost.RecordAsync($"conflict:{taskId}", _model ?? "opus", result, CancellationToken.None);
        }
        if (result == null || !result.Success)
        {
            await _worktree.AbortMergeAsync(ct);
            return false;
        }

        // 해결된 파일에 충돌 마커가 남아있는지 1차 검증 (P2-2: Path.Combine은 file이 절대경로면
        // file을 그대로 반환하므로 IsPathRooted 분기 불필요).
        foreach (var file in mergeResult.ConflictFiles)
        {
            var fullPath = Path.Combine(repoRoot, file);
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

        // P1-2: 1차 검증이 ConflictFiles만 보았다면, staged 영역 전체를 git diff --check --cached로
        // 한 번 더 검증한다. Claude가 다른 파일을 건드리거나 새 충돌 마커를 만들었을 가능성을 포착.
        var (checkExit, checkOut) = await _git.RunAsync(
            ["diff", "--check", "--cached"], workingDirectory: repoRoot, ct: ct);
        if (checkExit != 0)
        {
            AnsiConsole.MarkupLine($"[red]staged 영역에 충돌 마커/문제 감지:[/]");
            if (!string.IsNullOrWhiteSpace(checkOut))
                AnsiConsole.WriteLine(checkOut.Trim());
            _logger.Error($"git diff --check --cached failed for {taskId}: {checkOut.Trim()}");
            await _worktree.AbortMergeAsync(ct);
            return false;
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
    /// 머지 후 base 브랜치에서 smoke test를 실행해 머지 결과의 semantic 정합성을 검증한다.
    /// 우선순위: <c>--no-smoke-test</c>(또는 env)면 즉시 skip → workflow.smokeTest 명시 지정 시 그것을
    /// 사용 → 미지정이면 repo root marker(.csproj/package.json/Cargo.toml/go.mod 등)로 자동 추론.
    /// 실패 시 종료 코드 1(non-success)을 반환해 호출자가 다음 배치를 dispatch하지 않게 한다.
    /// 추론 결과도 null이면 null을 반환해 호출자가 다음 단계로 진행.
    /// </summary>
    private async Task<int?> RunPostMergeSmokeTestAsync(CancellationToken ct)
    {
        if (_noSmokeTest)
        {
            _logger.Info("[smoke-test] skipped (--no-smoke-test)");
            return null;
        }

        var repoRoot = await _git.GetRepoRootAsync(ct: ct);
        var configured = _taskManager.Data.Workflow?.SmokeTest;
        VerificationSpec? spec;
        bool inferred = false;

        if (configured is not null && !string.IsNullOrWhiteSpace(configured.Command))
        {
            spec = configured;
        }
        else
        {
            spec = InferSmokeTestCommand(repoRoot);
            inferred = spec is not null;
            if (spec is null)
            {
                _logger.Info("[smoke-test] skipped (no workflow.smokeTest, inference matched no marker)");
                return null;
            }
        }

        var label = inferred ? "Smoke test 실행 (자동 추론)" : "Smoke test 실행";
        AnsiConsole.MarkupLine(
            $"\n[cyan]{label}:[/] [dim]{Markup.Escape(spec!.Command)}[/] [dim](cwd: {Markup.Escape(repoRoot)})[/]");
        _logger.Info($"[smoke-test] running: {spec.Command} (cwd: {repoRoot}, inferred: {inferred})");

        var result = await _verifier.RunAsync(spec, repoRoot, _logger, output: null, ct);
        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]✓ Smoke test 통과[/] ({result.Duration.TotalSeconds:F1}s)");
            return null;
        }

        AnsiConsole.MarkupLine(
            $"[red]✗ Smoke test 실패[/] (exit={result.ExitCode}{(result.TimedOut ? ", TIMEOUT" : "")}, {result.Duration.TotalSeconds:F1}s)");
        if (!string.IsNullOrWhiteSpace(result.Stderr))
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(result.Stderr.Trim())}[/]");
        _logger.Error(
            $"[smoke-test] failed exit={result.ExitCode} timedOut={result.TimedOut}");
        return 1;
    }

    /// <summary>
    /// repo root에 있는 빌드 시스템 marker를 보고 smoke test 명령을 추론한다.
    /// 우선순위: .csproj/.sln(dotnet) → package.json(npm) → Cargo.toml(cargo) → go.mod(go).
    /// 매치 없으면 null. 깊은 재귀 탐색은 비용이 크므로 top-level만 본다 — monorepo는 root marker를 가정.
    /// 순수 함수: 외부 상태에 의존하지 않으며 명령을 실행하지 않는다.
    /// </summary>
    public static VerificationSpec? InferSmokeTestCommand(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return null;

        bool HasTopLevel(string pattern) =>
            Directory.EnumerateFiles(repoRoot, pattern, SearchOption.TopDirectoryOnly).Any();

        if (HasTopLevel("*.csproj") || HasTopLevel("*.sln"))
            return new VerificationSpec { Command = "dotnet build -nologo", TimeoutSec = 180 };
        if (HasTopLevel("package.json"))
            return new VerificationSpec { Command = "npm test --silent", TimeoutSec = 180 };
        if (HasTopLevel("Cargo.toml"))
            return new VerificationSpec { Command = "cargo build --quiet", TimeoutSec = 300 };
        if (HasTopLevel("go.mod"))
            return new VerificationSpec { Command = "go build ./...", TimeoutSec = 180 };

        return null;
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

    /// <summary>
    /// 태스크의 modifiedFiles ∪ outputFiles를 normalized 집합으로 만든다.
    /// </summary>
    private static IReadOnlyCollection<string> BuildDeclaredSet(TaskItem task)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (task.ModifiedFiles is { Count: > 0 })
        {
            foreach (var p in task.ModifiedFiles)
                if (!string.IsNullOrWhiteSpace(p)) set.Add(p);
        }
        if (task.OutputFiles is { Count: > 0 })
        {
            foreach (var p in task.OutputFiles)
                if (!string.IsNullOrWhiteSpace(p)) set.Add(p);
        }
        return set;
    }

    /// <summary>
    /// F4 검증 결과를 콘솔에 표시한다. strict 차단 메시지는 별도 분기에서 출력하므로
    /// 여기서는 warn-only/info 메시지만 다룬다.
    /// </summary>
    private void ReportValidation(string taskId, FileValidationResult validation)
    {
        if (validation.DiffFailed)
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]⚠[/] {Markup.Escape(taskId)} diff 실패 — 검증 스킵");
            return;
        }

        if (validation.HasUndeclared && !_strictFiles)
        {
            var preview = string.Join(", ", validation.Undeclared.Take(3));
            var more = validation.Undeclared.Count > 3
                ? $" (외 {validation.Undeclared.Count - 3}건)" : "";
            AnsiConsole.MarkupLine(
                $"  [yellow]⚠[/] {Markup.Escape(taskId)} undeclared {validation.Undeclared.Count}건 (warn-only): " +
                $"{Markup.Escape(preview + more)}");
        }

        if (validation.HasNotChanged)
        {
            var preview = string.Join(", ", validation.NotChanged.Take(3));
            var more = validation.NotChanged.Count > 3
                ? $" (외 {validation.NotChanged.Count - 3}건)" : "";
            AnsiConsole.MarkupLine(
                $"  [dim]ℹ {Markup.Escape(taskId)} notChanged {validation.NotChanged.Count}건: " +
                $"{Markup.Escape(preview + more)}[/]");
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
