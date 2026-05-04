using Ralph.Commands;
using Ralph.Services;
using Spectre.Console;

// ─── UTF-8 console encoding ─────────────────────────────────────────────────
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

// ─── Ctrl+C handling ─────────────────────────────────────────────────────────
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    if (cts.IsCancellationRequested)
    {
        // 두 번째 Ctrl+C: 즉시 종료
        e.Cancel = false;
        return;
    }
    e.Cancel = true;
    cts.Cancel();
    AnsiConsole.MarkupLine("\n[red]Interrupted. Aborting...[/]");
};

// ─── Dependency checks ──────────────────────────────────────────────────────
DependencyChecker.Check("claude", "Claude Code CLI", "https://claude.ai/code");
DependencyChecker.Check("git", "Git", "https://git-scm.com");

// ─── Parse argv → CommandContext ─────────────────────────────────────────────
var ctx = ArgParser.Parse(args);
if (ctx is null) return 1;

// ─── Dispatch ────────────────────────────────────────────────────────────────
var command = CommandDispatcher.Resolve(ctx);
try
{
    return await command.ExecuteAsync(cts.Token);
}
catch (OperationCanceledException)
{
    AnsiConsole.MarkupLine("\n[red]Interrupted. Aborted.[/]");
    return 130;
}
catch (RalphUserException)
{
    // 안내 메시지는 호출자가 이미 출력했다. stack trace 없이 깔끔히 종료.
    return 1;
}
