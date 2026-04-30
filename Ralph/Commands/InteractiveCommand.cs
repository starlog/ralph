using Ralph.Services;
using Spectre.Console;

namespace Ralph.Commands;

/// <summary><c>ralph --interactive</c> — 각 태스크마다 사용자 확인 후 실행.</summary>
public sealed class InteractiveCommand : ICommand
{
    private readonly CommandContext _ctx;

    public InteractiveCommand(CommandContext ctx) => _ctx = ctx;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        DisplayHelpers.RequireFile(_ctx.TasksFile);
        var tm = await TaskManager.LoadAsync(_ctx.TasksFile);
        var claude = _ctx.NewClaudeService(tm);
        var git = new GitService();
        using var logger = new RalphLogger();
        logger.Info("Exec mode: interactive");

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

        var runner = new SequentialRunner(tm, claude, git, logger, _ctx.TasksFile, modelOverride, new CostTracker());
        return await runner.RunInteractiveLoopAsync(ct);
    }
}
