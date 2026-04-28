using Ralph.Services;
using Spectre.Console;

namespace Ralph.Commands;

/// <summary>
/// <c>ralph --run</c> — 모든 pending 태스크 실행. 기본 병렬, --sequential 또는 단일 task면 순차.
/// 종료 후 webhook 알림. budget 도달 시 종료 코드 2.
/// </summary>
public sealed class RunCommand : ICommand
{
    private readonly CommandContext _ctx;

    public RunCommand(CommandContext ctx) => _ctx = ctx;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        DisplayHelpers.RequireFile(_ctx.TasksFile);
        var tm = await TaskManager.LoadAsync(_ctx.TasksFile);
        var claude = _ctx.NewClaudeService(tm);
        var git = new GitService();
        using var logger = new RalphLogger();
        logger.Info($"Tasks file: {_ctx.TasksFile}");

        // 세션 시작 시 자동 로그 rotation (silent)
        LogRotator.Rotate(retentionDays: tm.Data.Workflow?.LogRetentionDays, quiet: true);

        // 실행 전 plan 검증
        var preReport = PlanValidator.Validate(tm);
        if (preReport.HasErrors)
        {
            AnsiConsole.MarkupLine($"[red]✗ Plan 검증 실패 ({preReport.Errors.Count}개 error):[/]");
            foreach (var e in preReport.Errors)
                AnsiConsole.MarkupLine($"  [red]•[/] {Markup.Escape(e)}");
            if (!_ctx.ForceFlag)
            {
                AnsiConsole.MarkupLine("\n[yellow]계속하려면 --force 플래그를 추가하거나 'ralph --validate'로 자세히 보세요.[/]");
                return 1;
            }
            AnsiConsole.MarkupLine("[yellow]--force 지정됨 — error 무시하고 진행합니다.[/]\n");
        }
        if (preReport.HasWarnings)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ Plan 검증 경고 {preReport.Warnings.Count}개 (실행은 계속됨, 자세히는 'ralph --validate')[/]");
            foreach (var w in preReport.Warnings.Take(3))
                AnsiConsole.MarkupLine($"  [yellow]•[/] {Markup.Escape(w)}");
            if (preReport.Warnings.Count > 3)
                AnsiConsole.MarkupLine($"  [dim]... 외 {preReport.Warnings.Count - 3}개[/]");
            AnsiConsole.WriteLine();
        }

        // 병렬 실행 여부 결정
        var parallelConfig = tm.ParallelConfig;
        var useParallel = !_ctx.Sequential && !_ctx.EnvParallelDisabled && parallelConfig.Enabled;

        // 그래프 스캔: pending 태스크들의 topological layer 중 최대 폭을 계산
        const int hardCap = 16;
        var pendingIds = tm.GetPendingTasks().Select(t => t.Id).ToHashSet();
        var layers = tm.ComputeTopologicalLayers();
        var maxLayerWidth = layers
            .Select(l => l.Count(id => pendingIds.Contains(id)))
            .DefaultIfEmpty(0)
            .Max();
        var scannedConcurrency = Math.Clamp(maxLayerWidth, 1, hardCap);

        // 우선순위: --max-parallel > RALPH_MAX_PARALLEL > 그래프 스캔 결과
        var concurrency = _ctx.MaxParallelArg > 0 ? _ctx.MaxParallelArg
            : _ctx.EnvMaxParallel > 0 ? _ctx.EnvMaxParallel
            : scannedConcurrency;
        if (concurrency > hardCap) concurrency = hardCap;

        if (useParallel && _ctx.MaxParallelArg == 0 && _ctx.EnvMaxParallel == 0)
        {
            AnsiConsole.MarkupLine(
                $"[cyan]그래프 스캔:[/] 최대 동시 실행 가능 태스크 {maxLayerWidth}개 → {concurrency}개로 설정 (상한 {hardCap})");
        }

        var sessionStart = DateTime.UtcNow;
        var sessionStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var totalAtStart = tm.GetPendingTasks().Count;
        int exitCode;

        // P1-3: 단일 CostTracker 인스턴스 공유.
        var costTracker = new CostTracker();

        if (useParallel)
        {
            logger.Info($"Exec mode: parallel (max concurrent: {concurrency})");
            AnsiConsole.MarkupLine($"[green]병렬 실행 모드[/] (최대 동시 실행: {concurrency})");

            DisplayHelpers.ShowProgress(tm, logger);

            var worktree = new WorktreeService(git);
            // 우선순위: CLI > env > workflow > false
            var sharedWorktrees = _ctx.CliSharedWorktrees
                ? true
                : _ctx.EnvSharedWorktrees ?? tm.Data.Workflow?.Parallel?.SharedWorktreeObjects ?? false;
            var executor = new ParallelExecutor(
                tm, claude, git, worktree, logger, _ctx.TasksFile, _ctx.ModelArg,
                strictFiles: _ctx.StrictFiles, budgetUsd: _ctx.EffectiveBudgetUsd(tm), cost: costTracker,
                sharedWorktrees: sharedWorktrees, noSmokeTest: _ctx.NoSmokeTest);
            exitCode = await executor.RunAsync(concurrency, ct);
            if (exitCode == 0 && executor.BudgetReached) exitCode = 2;
        }
        else
        {
            logger.Info("Exec mode: sequential");
            AnsiConsole.MarkupLine("[yellow]순차 실행 모드[/]");

            var runner = new SequentialRunner(tm, claude, git, logger, _ctx.TasksFile, _ctx.ModelArg, costTracker);
            exitCode = await runner.RunAutoLoopAsync(
                dryRun: false, commitOnComplete: true,
                _ctx.EffectiveBudgetUsd(tm), costTracker, ct);
        }

        sessionStopwatch.Stop();

        // 세션 종료 알림
        try
        {
            await tm.ReloadAsync();
            var stillPending = tm.GetPendingTasks().Count;
            var completedNow = Math.Max(0, totalAtStart - stillPending);
            var success = exitCode == 0;
            var costSummary = await costTracker.GetTotalUsdAsync(ct);

            var notifier = new NotificationService();
            await notifier.NotifyAsync(
                success: success,
                sessionId: sessionStart.ToString("yyyyMMdd-HHmmss"),
                totalTasks: totalAtStart,
                completedTasks: completedNow,
                failedTasks: success ? 0 : Math.Max(0, totalAtStart - completedNow),
                durationSec: sessionStopwatch.Elapsed.TotalSeconds,
                estimatedCostUsd: costSummary,
                settings: tm.Data.Workflow?.Notifications,
                logger: logger,
                ct: ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.Warn($"Notification post-processing failed: {ex.Message}");
        }

        return exitCode;
    }
}
