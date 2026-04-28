using Ralph.Services;
using Spectre.Console;

namespace Ralph.Commands;

/// <summary><c>ralph --validate</c> — tasks.json 검증 (cycles, deps, file overlaps, sensitive paths).</summary>
public sealed class ValidateCommand : ICommand
{
    private readonly CommandContext _ctx;

    public ValidateCommand(CommandContext ctx) => _ctx = ctx;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        DisplayHelpers.RequireFile(_ctx.TasksFile);
        var tm = await TaskManager.LoadAsync(_ctx.TasksFile);

        AnsiConsole.Write(new Rule($"[green]Validating {Markup.Escape(_ctx.TasksFile)}[/]").RuleStyle("blue"));
        AnsiConsole.MarkupLine($"태스크 수: [cyan]{tm.Data.Tasks.Count}[/]");
        AnsiConsole.WriteLine();

        var report = PlanValidator.Validate(tm);
        return PlanValidator.PrintReport(report, failOnWarning: _ctx.ForceFlag);
    }
}
