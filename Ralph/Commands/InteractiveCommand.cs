using Ralph.Services;

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

        var runner = new SequentialRunner(tm, claude, git, logger, _ctx.TasksFile, _ctx.ModelArg, new CostTracker());
        return await runner.RunInteractiveLoopAsync(ct);
    }
}
