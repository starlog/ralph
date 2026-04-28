using Ralph.Services;
using Spectre.Console;

namespace Ralph.Commands;

/// <summary><c>ralph --prompts</c> — 모든 pending 태스크의 prompt를 차례로 출력.</summary>
public sealed class PromptsCommand : ICommand
{
    private readonly CommandContext _ctx;

    public PromptsCommand(CommandContext ctx) => _ctx = ctx;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        DisplayHelpers.RequireFile(_ctx.TasksFile);
        var tm = await TaskManager.LoadAsync(_ctx.TasksFile);

        AnsiConsole.MarkupLine("[blue]Task Prompts:[/]");
        foreach (var task in tm.GetPendingTasks())
        {
            AnsiConsole.Write(new Rule($"{Markup.Escape(task.Id)}").RuleStyle("dim"));
            AnsiConsole.WriteLine(task.Prompt ?? "No prompt defined");
        }
        return 0;
    }
}
