using Ralph.Services;
using Spectre.Console;

namespace Ralph.Commands;

/// <summary><c>ralph --list</c> — pending 태스크 목록 (병렬 실행 가능 표시).</summary>
public sealed class ListCommand : ICommand
{
    private readonly CommandContext _ctx;

    public ListCommand(CommandContext ctx) => _ctx = ctx;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        DisplayHelpers.RequireFile(_ctx.TasksFile);
        var tm = await TaskManager.LoadAsync(_ctx.TasksFile);

        var readyTasks = new HashSet<string>(tm.GetAllReadyTasks());
        var pending = tm.GetPendingTasks();

        AnsiConsole.MarkupLine($"[blue]Pending Tasks ({pending.Count}):[/]");
        foreach (var task in pending)
        {
            var deps = task.DependsOn is { Count: > 0 }
                ? $" (depends: {string.Join(", ", task.DependsOn)})"
                : "";
            var readyMark = readyTasks.Contains(task.Id) ? "[green]●[/]" : "[red]○[/]";
            AnsiConsole.MarkupLine(
                $"  {readyMark} [dim]{Markup.Escape(task.Phase ?? "")}[/] {Markup.Escape(task.Id)}: {Markup.Escape(task.Title)}{Markup.Escape(deps)}");
        }

        if (readyTasks.Count > 1)
        {
            AnsiConsole.MarkupLine($"\n[green]{readyTasks.Count}개 태스크가 병렬 실행 가능합니다.[/]");
        }

        return 0;
    }
}
