using Ralph.Services;
using Spectre.Console;

namespace Ralph.Commands;

/// <summary><c>ralph --reset</c> — 모든 태스크를 pending 상태로 되돌린다.</summary>
public sealed class ResetCommand : ICommand
{
    private readonly CommandContext _ctx;

    public ResetCommand(CommandContext ctx) => _ctx = ctx;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        DisplayHelpers.RequireFile(_ctx.TasksFile);
        var tm = await TaskManager.LoadAsync(_ctx.TasksFile, ct: ct);

        AnsiConsole.MarkupLine("[yellow]Resetting all tasks to pending...[/]");
        await tm.ResetAllAsync(ct);
        AnsiConsole.MarkupLine("[green]All tasks reset.[/] [dim](spec(tasks.json)은 보존; .ralph-logs/state.json만 초기화)[/]");
        return 0;
    }
}
