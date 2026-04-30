using Ralph.Services;
using Spectre.Console;

namespace Ralph.Commands;

/// <summary>
/// <c>ralph --dry-run</c> — 실제 Claude 호출 없이 시뮬레이션. tasks.json은 try/finally로 복원.
/// </summary>
public sealed class DryRunCommand : ICommand
{
    private readonly CommandContext _ctx;

    public DryRunCommand(CommandContext ctx) => _ctx = ctx;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        DisplayHelpers.RequireFile(_ctx.TasksFile);
        var tm = await TaskManager.LoadAsync(_ctx.TasksFile);
        var claude = _ctx.NewClaudeService(tm);
        var git = new GitService();
        using var logger = new RalphLogger();
        logger.Info("Exec mode: dry-run");

        var modelOverride = string.IsNullOrEmpty(_ctx.ModelArg) ? null : _ctx.ModelArg;
        if (modelOverride != null)
        {
            AnsiConsole.MarkupLine($"[cyan]Model:[/] {DisplayHelpers.FormatModel(modelOverride)} [dim](--model — 모든 태스크에 강제)[/]");
            logger.Info($"Model override: {modelOverride}");
        }
        else
        {
            AnsiConsole.MarkupLine($"[cyan]Model:[/] per-task [dim](task.model 또는 [/]{DisplayHelpers.FormatModel("sonnet")}[dim] 기본)[/]");
            logger.Info("Model: per-task (task.model or sonnet default)");
        }

        var backupJson = await File.ReadAllTextAsync(_ctx.TasksFile, ct);

        int result;
        try
        {
            var runner = new SequentialRunner(tm, claude, git, logger, _ctx.TasksFile, modelOverride, new CostTracker());
            result = await runner.RunAutoLoopAsync(
                dryRun: true, commitOnComplete: false, budgetUsd: null,
                cost: new CostTracker(), ct);
        }
        finally
        {
            // try/finally — 인터럽트/취소에서도 복원 보장.
            await File.WriteAllTextAsync(_ctx.TasksFile, backupJson, CancellationToken.None);
            AnsiConsole.MarkupLine($"[cyan][[DRY-RUN]] {Markup.Escape(_ctx.TasksFile)} restored to original state.[/]");
        }

        return result;
    }
}
