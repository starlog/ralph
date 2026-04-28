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
        var tm = await TaskManager.LoadAsync(_ctx.TasksFile);

        AnsiConsole.MarkupLine("[yellow]Resetting all tasks to pending...[/]");
        tm.ResetAll();
        await tm.SaveAsync();
        AnsiConsole.MarkupLine("[green]All tasks reset.[/]");
        return 0;
    }
}
