using Ralph.Services;
using Spectre.Console;

namespace Ralph.Commands;

/// <summary><c>ralph --plan-prompt &lt;PRD.md&gt;</c> — 실제 plan을 실행하지 않고 prompt만 출력.</summary>
public sealed class PlanPromptCommand : ICommand
{
    private readonly CommandContext _ctx;

    public PlanPromptCommand(CommandContext ctx) => _ctx = ctx;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        if (_ctx.Args.Count < 2)
        {
            AnsiConsole.MarkupLine("[red]Error: PRD file required. Usage: ralph --plan-prompt <prd-file>[/]");
            return 1;
        }

        var prdFile = _ctx.Args[1];
        if (!File.Exists(prdFile))
        {
            AnsiConsole.MarkupLine($"[red]Error: File '{Markup.Escape(prdFile)}' not found.[/]");
            return 1;
        }

        var prdFullPath = Path.GetFullPath(prdFile);
        var tasksFullPath = Path.GetFullPath(_ctx.TasksFile);
        var schemaContent = SchemaLoader.Load();

        IReadOnlyList<string>? configuredCategories = null;
        if (File.Exists(_ctx.TasksFile))
        {
            try
            {
                var existingTm = await TaskManager.LoadAsync(_ctx.TasksFile);
                var cats = existingTm.Data.Workflow?.Categories;
                if (cats is { Count: > 0 })
                    configuredCategories = cats.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
            }
            catch { }
        }

        var prompt = PlanGenerator.BuildPlanPrompt(prdFullPath, schemaContent, tasksFullPath, configuredCategories);

        AnsiConsole.Write(new Rule("[green]RALPH - Plan Prompt Preview[/]").RuleStyle("blue"));
        AnsiConsole.MarkupLine($"[cyan]PRD File:[/] {Markup.Escape(prdFile)}");
        AnsiConsole.Write(new Rule().RuleStyle("blue"));
        AnsiConsole.WriteLine(prompt);

        return 0;
    }
}
