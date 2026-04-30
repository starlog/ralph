using Ralph.Services;
using Spectre.Console;

namespace Ralph.Commands;

/// <summary>
/// <c>ralph --task &lt;id&gt;</c> — 한 태스크만 실행. 의존성 미충족 시 --force 또는 사용자 확인.
/// </summary>
public sealed class SingleTaskCommand : ICommand
{
    private readonly CommandContext _ctx;

    public SingleTaskCommand(CommandContext ctx) => _ctx = ctx;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        if (_ctx.Args.Count < 2)
        {
            AnsiConsole.MarkupLine("[red]Error: Task ID required. Usage: ralph --task <task-id>[/]");
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

        // 의존성 검사 — 미완료 의존이 있으면 경고 + 확인 (--force 시 우회)
        if (!tm.CheckDependencies(taskId, out var blockedBy))
        {
            AnsiConsole.MarkupLine(
                $"\n[yellow]⚠️  태스크 '{Markup.Escape(taskId)}'의 의존성이 완료되지 않았습니다:[/]");
            foreach (var depId in blockedBy)
            {
                var dep = tm.GetTask(depId);
                var depTitle = dep?.Title ?? "(unknown)";
                var status = dep == null ? "missing" : (dep.Done ? "done" : "pending");
                AnsiConsole.MarkupLine($"  - {Markup.Escape(depId)}: {Markup.Escape(depTitle)} [dim]({status})[/]");
            }

            if (_ctx.ForceFlag)
            {
                AnsiConsole.MarkupLine("[yellow]--force 지정됨 — 의존성 무시하고 진행합니다.[/]\n");
            }
            else
            {
                var nonInteractive = Console.IsInputRedirected || Console.IsOutputRedirected;
                if (nonInteractive)
                {
                    AnsiConsole.MarkupLine("\n[red]비대화형 환경에서는 --force 없이 의존성을 우회할 수 없습니다.[/]");
                    AnsiConsole.MarkupLine($"  예: [cyan]ralph --task {Markup.Escape(taskId)} --force[/]");
                    return 1;
                }

                var proceed = AnsiConsole.Confirm("\n[yellow]그래도 진행하시겠습니까?[/]", defaultValue: false);
                if (!proceed)
                {
                    AnsiConsole.MarkupLine("[dim]사용자 취소.[/]");
                    return 1;
                }
                AnsiConsole.MarkupLine("[yellow]사용자 확인 — 의존성 무시하고 진행합니다.[/]\n");
            }
        }

        var claude = _ctx.NewClaudeService(tm);
        var git = new GitService();
        using var logger = new RalphLogger();

        var modelOverride = string.IsNullOrEmpty(_ctx.ModelArg) ? null : _ctx.ModelArg;
        var (resolved, source) = ModelResolver.Resolve(modelOverride, task);
        AnsiConsole.MarkupLine($"[cyan]Model:[/] {DisplayHelpers.FormatModel(resolved)} [dim]({source})[/]");
        logger.Info($"Model: {resolved} ({source})");

        var runner = new SequentialRunner(tm, claude, git, logger, _ctx.TasksFile, modelOverride, new CostTracker());
        return await runner.RunTaskAsync(
            taskId, dryRun: false, commitOnComplete: tm.CommitOnComplete, ct, force: true);
    }
}
