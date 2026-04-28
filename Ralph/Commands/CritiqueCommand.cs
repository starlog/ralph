using Ralph.Services;
using Spectre.Console;

namespace Ralph.Commands;

/// <summary><c>ralph --critique</c> — tasks.json에 대한 정성적 critique (병렬화/verification 누락 등).</summary>
public sealed class CritiqueCommand : ICommand
{
    private readonly CommandContext _ctx;

    public CritiqueCommand(CommandContext ctx) => _ctx = ctx;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        DisplayHelpers.RequireFile(_ctx.TasksFile);
        var tm = await TaskManager.LoadAsync(_ctx.TasksFile);
        AnsiConsole.Write(new Rule($"[green]PRD Critique - {Markup.Escape(_ctx.TasksFile)}[/]").RuleStyle("blue"));
        AnsiConsole.MarkupLine($"태스크 수: [cyan]{tm.Data.Tasks.Count}[/]");
        var suggestions = PrdCritic.Analyze(tm);
        PrdCritic.PrintReport(suggestions);
        return suggestions.Any(s => s.Severity == "warn") ? 1 : 0;
    }
}
