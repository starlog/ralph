using Ralph.Models;
using Spectre.Console;

namespace Ralph.Services;

/// <summary>
/// 병렬 실행의 entry point. 이전엔 god class였으나 책임을 셋으로 분리:
///   - <see cref="WorktreeTaskRunner"/> : worktree 안에서 단일 태스크 실행
///   - <see cref="MergeOrchestrator"/>  : 머지 + 충돌 해결 + smoke test
///   - <see cref="VerificationLoop"/>  : Claude + verification self-fix retry 공용 루프
///
/// ParallelExecutor 자신은 batch loop, 단일 task fast path, worktree 생성/cleanup,
/// budget 게이트, 사이클 감지만 담당한다.
/// </summary>
public class ParallelExecutor
{
    private readonly TaskManager _taskManager;
    private readonly IAgentRunner _claude;
    private readonly GitService _git;
    private readonly WorktreeService _worktree;
    private readonly RalphLogger _logger;
    private readonly RunOptions _options;
    private readonly CostTracker _cost;
    private readonly BudgetGate _budgetGate;
    private readonly VerificationRunner _verifier = new();
    private readonly VerificationLoop _verificationLoop;
    private readonly WorktreeTaskRunner _worktreeRunner;
    private readonly MergeOrchestrator _mergeOrchestrator;
    private int _cleanupFailures;

    /// <summary>Per-worktree cleanup timeout. Tests can override to a short value.</summary>
    internal TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);
    /// <summary>Tests can inject a custom cleanup function to simulate slow/hung cleanup.</summary>
    internal Func<string, RalphLogger, CancellationToken, Task<bool>>? CleanupDelegate;
    /// <summary>Accumulated cleanup failure count, exposed for testing.</summary>
    internal int CleanupFailureCount => _cleanupFailures;

    /// <summary>
    /// budget(USD) 임계값 도달로 새 dispatch를 차단했는지 여부.
    /// 호출자가 확인해 종료 코드 2를 결정할 수 있다.
    /// </summary>
    public bool BudgetReached => _budgetGate.Reached;

    public ParallelExecutor(
        TaskManager taskManager, IAgentRunner claude, GitService git,
        WorktreeService worktree, RalphLogger logger, RunOptions options,
        CostTracker? cost = null, BudgetGate? budgetGate = null)
    {
        _taskManager = taskManager;
        _claude = claude;
        _git = git;
        _worktree = worktree;
        _logger = logger;
        _options = options;
        _cost = cost ?? new CostTracker();
        _budgetGate = budgetGate ?? new BudgetGate(options.BudgetUsd, _cost, logger);

        _verificationLoop = new VerificationLoop(_claude, _verifier, _cost, _logger);
        var verifyRetries = Math.Max(0, _taskManager.Data.Workflow?.VerifyRetries ?? 1);

        _worktreeRunner = new WorktreeTaskRunner(
            _taskManager, _git, _logger, _verificationLoop,
            _options.TasksFile, _options.ModelOverride, _options.StrictFiles, verifyRetries);

        _mergeOrchestrator = new MergeOrchestrator(
            _taskManager, _claude, _git, _worktree, _logger, _verifier, _cost, _options)
        {
            // abort 전략 시 fallback으로 sequential RunSingle 호출.
            RerunSequential = (taskId, ct) => RunSingleTaskAsync(taskId, ct),
        };
    }

    public async Task<int> RunAsync(int maxConcurrent, CancellationToken ct)
    {
        if (_taskManager.HasCycle(out var cycle))
        {
            AnsiConsole.MarkupLine("[red]순환 의존성이 발견되었습니다:[/]");
            AnsiConsole.MarkupLine($"  {Markup.Escape(string.Join(" → ", cycle))}");
            _logger.Error($"Cycle detected: {string.Join(" → ", cycle)}");
            return 1;
        }

        // worktree 사용을 위해 최소 1개의 커밋이 필요.
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

        // mid-task가 아닌 잔존 worktree(이전 실행에서 cleanup 누락된 빈 worktree)는 자동 정리.
        var stale = await _worktree.DetectStaleWorktreesAsync(ct);
        if (stale.Count > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]잔존 worktree {stale.Count}개 감지 (clean). 정리합니다...[/]");
            using var staleCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await _worktree.CleanupAllAsync(_logger, staleCts.Token); }
            catch (OperationCanceledException)
            {
                _logger.Warn("잔존 worktree 전체 정리 타임아웃 (30초). 'ralph --worktree-cleanup'으로 정리하세요.");
            }
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // F5: budget 게이트 — 새 dispatch 직전에 검사. 차단 시 break.
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
                AnsiConsole.MarkupLine($"\n[blue]단일 태스크 실행: {Markup.Escape(readyTasks[0])}[/]");
                var result = await RunSingleTaskAsync(readyTasks[0], ct);
                if (result != 0) return result;
            }
            else
            {
                var batches = _taskManager.GetParallelBatches();
                var batch = batches[0];

                if (batch.Count > maxConcurrent)
                    batch = batch.Take(maxConcurrent).ToList();

                if (batch.Count == 1)
                {
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

        using var finalCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await _worktree.CleanupAllAsync(_logger, finalCts.Token); }
        catch (OperationCanceledException)
        {
            _logger.Warn("최종 worktree 전체 정리 타임아웃 (30초). 'ralph --worktree-cleanup'으로 정리하세요.");
        }
        return 0;
    }

    /// <summary>
    /// worktree 없이 단일 태스크를 직접 실행한다.
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

            var ok = await RunSingleWithVerificationAsync(task, ct);
            if (!ok)
            {
                _logger.TaskEnd(taskId, "failed");
                return 1;
            }
            AnsiConsole.MarkupLine("\n[green]Claude Code 실행 완료[/]");
        }

        if (task.Subtasks is { Count: > 0 })
        {
            foreach (var sub in task.Subtasks.Where(s => !_taskManager.IsSubtaskDone(taskId, s.Id)))
                await _taskManager.MarkSubtaskDoneAsync(taskId, sub.Id, ct);
        }

        await _taskManager.MarkTaskDoneAsync(taskId, ct);

        AnsiConsole.MarkupLine($"[green]태스크 완료: {Markup.Escape(task.Title)}[/]");
        _logger.TaskEnd(taskId, "completed");

        if (_taskManager.CommitOnComplete)
            await _git.CommitChangesAsync(taskId, task.Title, _taskManager.CommitTemplate, _logger, ct: ct);

        return 0;
    }

    /// <summary>
    /// 단일 task path용 verification 루프 — 콘솔 메시지는 풀스타일(Spectre 마크업).
    /// </summary>
    private Task<bool> RunSingleWithVerificationAsync(TaskItem task, CancellationToken ct)
    {
        var basePrompt = PromptBuilder.Build(task, _taskManager, _options.TasksFile, siblings: null);
        var maxVerifyRetries = Math.Max(0, _taskManager.Data.Workflow?.VerifyRetries ?? 1);
        var (resolvedModel, modelSource) = ModelResolver.Resolve(_options.ModelOverride, task);
        AnsiConsole.MarkupLine($"[cyan]Model:[/] {Ralph.Commands.DisplayHelpers.FormatModel(resolvedModel)} [dim]({modelSource})[/]");
        _logger.Info($"[{task.Id}] Model: {resolvedModel} ({modelSource})");

        var callbacks = new VerificationCallbacks
        {
            OnClaudeFailure = _ => AnsiConsole.MarkupLine("\n[red]Claude Code 실행 실패[/]"),
            OnVerificationStart = spec =>
                AnsiConsole.MarkupLine($"\n[cyan]검증 명령 실행:[/] [dim]{Markup.Escape(spec.Command)}[/]"),
            OnVerificationPass = verify =>
                AnsiConsole.MarkupLine($"[green]✓ 검증 통과[/] ({verify.Duration.TotalSeconds:F1}s)"),
            OnVerificationFailFinal = (verify, attemptCount) =>
            {
                AnsiConsole.MarkupLine(
                    $"[red]✗ 검증 실패[/] (exit={verify.ExitCode}{(verify.TimedOut ? ", TIMEOUT" : "")}, {attemptCount}회 시도)");
                if (!string.IsNullOrWhiteSpace(verify.Stderr))
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape(verify.Stderr.Trim())}[/]");
            },
            OnVerificationRetry = (_, attemptIndex, maxRetries) =>
                AnsiConsole.MarkupLine(
                    $"[yellow]⚠ 검증 실패, Claude에게 수정 요청 ({attemptIndex}/{maxRetries} retry)[/]"),
        };

        return _verificationLoop.ExecuteAsync(
            task, basePrompt, maxVerifyRetries,
            claudeWorkingDirectory: null, // 단일 path는 호출 프로세스 cwd 상속
            verifierWorkingDirectory: Directory.GetCurrentDirectory(),
            output: null, model: resolvedModel, callbacks: callbacks, ct: ct);
    }

    /// <summary>
    /// 여러 태스크를 worktree 기반으로 병렬 실행한다.
    /// 단계: worktree 생성 → live dashboard + 동시 실행 → 머지 phase 위임 → cleanup.
    /// </summary>
    private async Task<int> RunParallelBatchAsync(
        List<string> taskIds, string baseBranch, CancellationToken ct)
    {
        var strategyChain = _taskManager.ParallelConfig.GetStrategyChain();
        var primaryStrategy = strategyChain[0];
        var worktrees = new Dictionary<string, string>();
        var tracker = new TaskProgressTracker();
        tracker.AttachCostTracker(_cost, _budgetGate.BudgetUsd);

        try
        {
            AnsiConsole.MarkupLine("\n[blue]Worktree 생성 중...[/]");
            const string logDir = RalphPaths.LogDir;
            Directory.CreateDirectory(logDir);

            foreach (var taskId in taskIds)
            {
                var path = await _worktree.CreateWorktreeAsync(
                    taskId, baseBranch, _logger, sharedObjects: _options.SharedWorktrees, ct: ct);
                worktrees[taskId] = path;

                var logFile = Path.GetFullPath(Path.Combine(logDir, $"{taskId}.log"));
                var task = _taskManager.GetTask(taskId)!;
                tracker.Register(taskId, task.Title, logFile);

                AnsiConsole.MarkupLine($"  [dim]→ {Markup.Escape(taskId)}: {Markup.Escape(path)}[/]");
            }

            var taskResults = new Dictionary<string, bool>();

            await AnsiConsole.Live(tracker.BuildTable())
                .AutoClear(false)
                .StartAsync(async ctx =>
                {
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

                    var execTasks = taskIds.Select(async taskId =>
                    {
                        var siblings = taskIds
                            .Where(id => id != taskId)
                            .Select(id => _taskManager.GetTask(id))
                            .Where(t => t != null)
                            .Select(t => t!)
                            .ToList();
                        var success = await _worktreeRunner.RunAsync(
                            taskId, worktrees[taskId], siblings, tracker, ct);
                        lock (taskResults)
                            taskResults[taskId] = success;
                    }).ToList();

                    await Task.WhenAll(execTasks);

                    refreshTimer.Dispose();
                    try { await refreshTask; }
                    catch (OperationCanceledException) { /* refresh task drain — cancellation은 정상 종료 신호 */ }

                    tracker.RefreshAllOutputSizes();
                    ctx.UpdateTarget(tracker.BuildTable());
                });

            var failed = taskIds.Where(id => taskResults.TryGetValue(id, out var ok) && !ok).ToList();
            if (failed.Count > 0)
            {
                AnsiConsole.MarkupLine($"\n[red]{failed.Count}개 태스크 실행 실패:[/]");
                foreach (var f in failed)
                {
                    AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(f)}");
                    AnsiConsole.MarkupLine($"    [dim]로그 확인: ralph --logs {Markup.Escape(f)}[/]");
                }

                foreach (var f in failed)
                {
                    if (!await _worktree.CleanupWorktreeAsync(f, _logger, ct))
                        _cleanupFailures++;
                }

                taskIds = taskIds.Except(failed).ToList();
                if (taskIds.Count == 0) return 1;
            }

            // 머지 단계 위임. mergeExit != 0 인 경우는 다음 중 하나:
            //   - 머지 phase 자체 실패 (충돌 미해결, strict-files 위반 등)
            //   - state.json 쓰기 실패로 batch 중단 (fix #1: silent 진행 금지)
            //   - 머지 후 smoke test 실패
            // 어느 경우든 다음 batch 진입을 차단하고 비-0으로 종료한다.
            var mergeExit = await _mergeOrchestrator.MergeAndFinalizeAsync(
                taskIds, baseBranch, primaryStrategy, strategyChain,
                reportCleanupFailures: extra => _cleanupFailures += extra,
                ct: ct);
            if (mergeExit != 0) return mergeExit;
        }
        finally
        {
            // worktree 정리
            AnsiConsole.MarkupLine("\n[dim]Worktree 정리 중...[/]");
            var doCleanup = CleanupDelegate ?? _worktree.CleanupWorktreeAsync;
            foreach (var taskId in worktrees.Keys)
            {
                using var cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cleanupCts.CancelAfter(CleanupTimeout);
                try
                {
                    if (!await doCleanup(taskId, _logger, cleanupCts.Token))
                        Interlocked.Increment(ref _cleanupFailures);
                }
                catch (OperationCanceledException)
                {
                    _logger.Warn($"Cleanup timed out or cancelled for {taskId}; 수동 정리: ralph --worktree-cleanup");
                    Interlocked.Increment(ref _cleanupFailures);
                }
            }

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
