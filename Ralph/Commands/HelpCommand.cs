using Spectre.Console;

namespace Ralph.Commands;

/// <summary>
/// <c>ralph --help</c> / 빈 명령. 사용법 + 옵션 + 환경변수 표시.
/// </summary>
public sealed class HelpCommand : ICommand
{
    public Task<int> ExecuteAsync(CancellationToken ct)
    {
        AnsiConsole.Write(new Rule($"[green]RALPH - Task Orchestrator[/] [cyan]v{DisplayHelpers.Version}[/]").RuleStyle("grey"));
        AnsiConsole.MarkupLine("\nUsage: [green]ralph[/] [yellow][[command]][/] [dim][[options]][/]\n");

        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn("[bold]Command[/]");
        table.AddColumn("[bold]Description[/]");
        table.AddRow("[green]--plan[/] <file>", "Generate tasks.json from a PRD file");
        table.AddRow("[green]--plan-prompt[/] <file>", "Show full plan prompt without executing");
        table.AddRow("[green]--run[/] [[file]]", "Run all pending tasks (parallel by default)");
        table.AddRow("[green]--dry-run[/] [[file]]", "Preview execution without changes");
        table.AddRow("[green]--task[/] <id>", "Run a specific task by ID (use --force to bypass deps)");
        table.AddRow("[green]--interactive[/]", "Run tasks interactively (confirm each)");
        table.AddRow("[green]--list[/], -l", "List all pending tasks");
        table.AddRow("[green]--graph[/], -g", "Show ASCII task dependency graph");
        table.AddRow("[green]--prompts[/], -p", "Show all task prompts");
        table.AddRow("[green]--show-prompt[/] <id>", "Show the full prompt sent to Claude for a task");
        table.AddRow("[green]--validate[/]", "Validate tasks.json (cycles, deps, file overlaps, etc.)");
        table.AddRow("[green]--critique[/]", "Analyze tasks.json for parallelism / verification gaps");
        table.AddRow("[green]--status[/], -s", "Show progress status (with parallel batch info)");
        table.AddRow("[green]--reset[/], -r", "Reset all tasks to pending");
        table.AddRow("[green]--logs[/] [[task-id]]", "Show logs (task log or session log list)");
        table.AddRow("[green]--logs --live[/] <task-id>", "Live tail a task log (like tail -f)");
        table.AddRow("[green]--logs --cleanup[/]", "Delete logs older than retention period");
        table.AddRow("[green]--cost[/]", "Show cumulative token usage and estimated cost");
        table.AddRow("[green]--worktree-cleanup[/]", "Clean up stale worktrees");
        table.AddRow("[green]--help[/], -h", "Show this help message");
        table.AddRow("[green]--version[/], -v", "Show ralph version");
        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine("\n[blue]Options:[/]");
        AnsiConsole.MarkupLine("  [green]-f[/], [green]--file[/] <path>    Use custom tasks file (default: tasks.json)");
        AnsiConsole.MarkupLine("  [green]--sequential[/]         Force sequential execution (disable parallel)");
        AnsiConsole.MarkupLine("  [green]--max-parallel[/] N     Maximum concurrent tasks (default: 5)");
        AnsiConsole.MarkupLine("  [green]--force[/]              Bypass dependency/validation checks (--task, --run)");
        AnsiConsole.MarkupLine("  [green]--strict-files[/]       Validate declared vs actual files at merge; abort on undeclared");
        AnsiConsole.MarkupLine("  [green]--shared-worktrees[/]   Use 'git worktree add --shared' to share .git objects across worktrees");
        AnsiConsole.MarkupLine("  [green]--no-smoke-test[/]      Disable post-merge smoke test (auto-inferred or explicit)");
        AnsiConsole.MarkupLine("  [green]--smoke-test[/] <cmd>   1회용 smoke test 명령 override (workflow.smokeTest와 자동 추론을 모두 우회)");
        AnsiConsole.MarkupLine("  [green]--llm-critique[/]       --plan 직후 LLM 기반 PRD/plan 비평 추가 1회 실행 (기본 off, 추가 비용)");
        AnsiConsole.MarkupLine("  [green]--budget-usd[/] <amt>   누적 비용이 amt(USD) 도달 시 새 태스크 시작 중단 (--run only)");
        AnsiConsole.MarkupLine("  [green]--task-timeout[/] <dur> Per-Claude-call timeout (예: 30m, 1h, 90s, 1800). hang 방지");
        AnsiConsole.MarkupLine("  [green]--model[/] <name>       Model (sonnet, opus; default: opus for --plan, sonnet for --run/--task/--dry-run/--interactive)");
        AnsiConsole.MarkupLine("  [green]--debug[/]              Show Claude stream events for diagnostics");

        AnsiConsole.MarkupLine("\n[blue]Workflow:[/]");
        AnsiConsole.MarkupLine("  1. ralph --plan PRD.md");
        AnsiConsole.MarkupLine("  2. ralph --list");
        AnsiConsole.MarkupLine("  3. ralph --dry-run");
        AnsiConsole.MarkupLine("  4. ralph --run\n");

        AnsiConsole.MarkupLine("[blue]Environment variables:[/]");
        AnsiConsole.MarkupLine("  MAX_RETRIES                 Max retry attempts (default: 2)");
        AnsiConsole.MarkupLine("  RETRY_DELAY                 Seconds between retries (default: 5)");
        AnsiConsole.MarkupLine("  RALPH_MAX_PARALLEL          Max concurrent worktrees (default: 3)");
        AnsiConsole.MarkupLine("  RALPH_PARALLEL              Set to 'false' to disable parallel execution");
        AnsiConsole.MarkupLine("  RALPH_STRICT_FILES          Set to 'true' to enable --strict-files");
        AnsiConsole.MarkupLine("  RALPH_SHARED_WORKTREES      Set to 'true' to enable --shared-worktrees");
        AnsiConsole.MarkupLine("  RALPH_NO_SMOKE_TEST         Set to 'true' or '1' to disable post-merge smoke test");
        AnsiConsole.MarkupLine("  RALPH_SMOKE_TEST_COMMAND    Override smoke test 명령. CLI --smoke-test가 우선");
        AnsiConsole.MarkupLine("  RALPH_BUDGET_USD            누적 비용 임계값(USD). CLI --budget-usd가 우선");
        AnsiConsole.MarkupLine("  RALPH_TASK_TIMEOUT_SEC      Per-Claude-call timeout(seconds). CLI --task-timeout이 우선");
        AnsiConsole.MarkupLine("  RALPH_WEBHOOK_URL           Default webhook for session completion notifications");
        AnsiConsole.MarkupLine("  RALPH_LOG_RETENTION_DAYS    Auto-delete logs older than N days (default: 30)\n");

        return Task.FromResult(0);
    }
}

/// <summary>알 수 없는 명령에 대한 메시지 + 종료 코드 1.</summary>
public sealed class UnknownCommand : ICommand
{
    private readonly string _command;

    public UnknownCommand(string command) => _command = command;

    public Task<int> ExecuteAsync(CancellationToken ct)
    {
        AnsiConsole.MarkupLine($"[red]Unknown option: {Markup.Escape(_command)}[/]");
        AnsiConsole.MarkupLine("Run [green]ralph --help[/] for usage information.");
        return Task.FromResult(1);
    }
}
