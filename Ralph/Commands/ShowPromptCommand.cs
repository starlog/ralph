using Ralph.Models;
using Ralph.Services;
using Spectre.Console;

namespace Ralph.Commands;

/// <summary><c>ralph --show-prompt &lt;id&gt;</c> — Claude에 보낼 full prompt를 미리 본다 (siblings 포함).</summary>
public sealed class ShowPromptCommand : ICommand
{
    private readonly CommandContext _ctx;

    public ShowPromptCommand(CommandContext ctx) => _ctx = ctx;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        if (_ctx.Args.Count < 2)
        {
            AnsiConsole.MarkupLine("[red]Error: Task ID required. Usage: ralph --show-prompt <task-id>[/]");
            return 1;
        }

        var taskId = _ctx.Args[1];
        DisplayHelpers.RequireFile(_ctx.TasksFile);
        var tm = await TaskManager.LoadAsync(_ctx.TasksFile);

        var task = tm.GetTask(taskId);
        if (task == null)
        {
            AnsiConsole.MarkupLine($"[red]Error: Task '{Markup.Escape(taskId)}' not found.[/]");
            return 1;
        }

        // 같은 ready batch에 있는 sibling task를 자동 추정 (실제 실행 시와 동일한 prompt를 보기 위해)
        var siblings = new List<TaskItem>();
        var batches = tm.GetParallelBatches();
        var myBatch = batches.FirstOrDefault(b => b.Contains(taskId));
        if (myBatch != null)
        {
            siblings = myBatch
                .Where(id => id != taskId)
                .Select(tm.GetTask)
                .Where(t => t != null)
                .Select(t => t!)
                .ToList();
        }

        var fullPrompt = PromptBuilder.Build(task, tm, _ctx.TasksFile, siblings);

        AnsiConsole.Write(new Rule($"[green]Full prompt for {Markup.Escape(taskId)}[/]").RuleStyle("blue"));
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine(fullPrompt);
        AnsiConsole.Write(new Rule().RuleStyle("blue"));

        if (siblings.Count > 0)
        {
            AnsiConsole.MarkupLine($"[dim]siblings: {Markup.Escape(string.Join(", ", siblings.Select(s => s.Id)))}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[dim]siblings: (none — runs alone or no other ready tasks in same batch)[/]");
        }
        return 0;
    }
}
