using System.Reflection;
using Ralph.Models;
using Ralph.Services;
using Spectre.Console;

const string Version = "0.2";

// ─── UTF-8 console encoding ─────────────────────────────────────────────────
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

// ─── Ctrl+C handling ─────────────────────────────────────────────────────────
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    AnsiConsole.MarkupLine("\n[red]Interrupted. Cleaning up...[/]");
};

// ─── Environment variables ───────────────────────────────────────────────────
var maxRetries = int.TryParse(Environment.GetEnvironmentVariable("MAX_RETRIES"), out var mr) ? mr : 2;
var retryDelay = int.TryParse(Environment.GetEnvironmentVariable("RETRY_DELAY"), out var rd) ? rd : 5;

// ─── Dependency checks ──────────────────────────────────────────────────────
CheckCommand("claude", "Claude Code CLI", "https://claude.ai/code");
CheckCommand("git", "Git", "https://git-scm.com");

// ─── Resolve tasks file (used by most commands) ─────────────────────────────
// --run and --dry-run accept optional [file] argument; other commands use tasks.json
var tasksFile = "tasks.json";
if (args.Length > 1 && (args[0] is "--run" or "--dry-run") && !args[1].StartsWith("--"))
    tasksFile = args[1];

// ─── Parse command ───────────────────────────────────────────────────────────
var command = args.Length > 0 ? args[0] : "";

try
{
    return await (command switch
    {
        "--plan" => HandlePlan(),
        "--run" => HandleRun(),
        "--dry-run" => HandleDryRun(),
        "--task" => HandleSingleTask(),
        "--interactive" => HandleInteractive(),
        "--list" or "-l" => HandleList(),
        "--prompts" or "-p" => HandlePrompts(),
        "--status" or "-s" => HandleStatus(),
        "--reset" or "-r" => HandleReset(),
        "--logs" => Task.FromResult(HandleLogs()),
        "--help" or "-h" => Task.FromResult(ShowHelp()),
        "" => Task.FromResult(ShowHelp()),
        _ => Task.FromResult(ShowUnknown(command)),
    });
}
catch (OperationCanceledException)
{
    AnsiConsole.MarkupLine("\n[red]Interrupted. Aborted.[/]");
    return 130;
}

// ═══════════════════════════════════════════════════════════════════════════════
// Command Handlers
// ═══════════════════════════════════════════════════════════════════════════════

async Task<int> HandlePlan()
{
    if (args.Length < 2)
    {
        AnsiConsole.MarkupLine("[red]Error: PRD file required. Usage: ralph --plan <prd-file>[/]");
        return 1;
    }

    var prdFile = args[1];
    if (!File.Exists(prdFile))
    {
        AnsiConsole.MarkupLine($"[red]Error: File '{Markup.Escape(prdFile)}' not found.[/]");
        return 1;
    }

    var schemaContent = LoadEmbeddedSchema();
    var claude = new ClaudeService(maxRetries, retryDelay);
    using var logger = new RalphLogger();

    var generator = new PlanGenerator();
    return await generator.GenerateAsync(prdFile, schemaContent, tasksFile, claude, logger, cts.Token);
}

async Task<int> HandleRun()
{
    RequireFile(tasksFile);
    var tm = await TaskManager.LoadAsync(tasksFile);
    var claude = new ClaudeService(maxRetries, retryDelay);
    var git = new GitService();
    using var logger = new RalphLogger();
    logger.Info($"Tasks file: {tasksFile}");
    logger.Info("Exec mode: auto");

    // --run always commits after each task step
    return await RunAutoLoop(tm, claude, git, logger, dryRun: false, commitOnComplete: true, cts.Token);
}

async Task<int> HandleDryRun()
{
    RequireFile(tasksFile);
    var tm = await TaskManager.LoadAsync(tasksFile);
    var claude = new ClaudeService(maxRetries, retryDelay);
    var git = new GitService();
    using var logger = new RalphLogger();
    logger.Info("Exec mode: dry-run");

    // Backup for restore after dry-run
    var backupJson = await File.ReadAllTextAsync(tasksFile, cts.Token);

    var result = await RunAutoLoop(tm, claude, git, logger, dryRun: true, commitOnComplete: false, cts.Token);

    // Restore original
    await File.WriteAllTextAsync(tasksFile, backupJson, cts.Token);
    AnsiConsole.MarkupLine($"[cyan][[DRY-RUN]] {Markup.Escape(tasksFile)} restored to original state.[/]");

    return result;
}

async Task<int> HandleSingleTask()
{
    if (args.Length < 2)
    {
        AnsiConsole.MarkupLine("[red]Error: Task ID required. Usage: ralph --task <task-id>[/]");
        return 1;
    }

    var taskId = args[1];
    RequireFile(tasksFile);
    var tm = await TaskManager.LoadAsync(tasksFile);

    if (tm.GetTask(taskId) == null)
    {
        AnsiConsole.MarkupLine($"[red]Error: Task '{Markup.Escape(taskId)}' not found.[/]");
        return 1;
    }

    var claude = new ClaudeService(maxRetries, retryDelay);
    var git = new GitService();
    using var logger = new RalphLogger();

    return await RunTaskAuto(tm, claude, git, logger, taskId,
        dryRun: false, commitOnComplete: tm.CommitOnComplete, cts.Token);
}

async Task<int> HandleInteractive()
{
    RequireFile(tasksFile);
    var tm = await TaskManager.LoadAsync(tasksFile);
    var claude = new ClaudeService(maxRetries, retryDelay);
    var git = new GitService();
    using var logger = new RalphLogger();
    logger.Info("Exec mode: interactive");

    return await RunInteractiveLoop(tm, claude, git, logger, cts.Token);
}

async Task<int> HandleList()
{
    RequireFile(tasksFile);
    var tm = await TaskManager.LoadAsync(tasksFile);

    AnsiConsole.MarkupLine("[blue]Pending Tasks:[/]");
    foreach (var task in tm.GetPendingTasks())
    {
        var deps = task.DependsOn is { Count: > 0 }
            ? $" (depends: {string.Join(", ", task.DependsOn)})"
            : "";
        AnsiConsole.MarkupLine(
            $"[dim]{Markup.Escape(task.Phase ?? "")}[/] {Markup.Escape(task.Id)}: {Markup.Escape(task.Title)}{Markup.Escape(deps)}");
    }
    return 0;
}

async Task<int> HandlePrompts()
{
    RequireFile(tasksFile);
    var tm = await TaskManager.LoadAsync(tasksFile);

    AnsiConsole.MarkupLine("[blue]Task Prompts:[/]");
    foreach (var task in tm.GetPendingTasks())
    {
        AnsiConsole.Write(new Rule($"{Markup.Escape(task.Id)}").RuleStyle("dim"));
        AnsiConsole.WriteLine(task.Prompt ?? "No prompt defined");
    }
    return 0;
}

async Task<int> HandleStatus()
{
    RequireFile(tasksFile);
    var tm = await TaskManager.LoadAsync(tasksFile);
    ShowProgress(tm, null);
    return 0;
}

async Task<int> HandleReset()
{
    RequireFile(tasksFile);
    var tm = await TaskManager.LoadAsync(tasksFile);

    AnsiConsole.MarkupLine("[yellow]Resetting all tasks to pending...[/]");
    tm.ResetAll();
    await tm.SaveAsync();
    AnsiConsole.MarkupLine("[green]All tasks reset.[/]");
    return 0;
}

int HandleLogs()
{
    const string logDir = ".ralph-logs";
    if (!Directory.Exists(logDir))
    {
        AnsiConsole.MarkupLine("[yellow]No logs found.[/]");
        return 0;
    }

    AnsiConsole.MarkupLine("[blue]Recent logs:[/]");
    var logs = Directory.GetFiles(logDir, "*.log")
        .Select(f => new FileInfo(f))
        .OrderByDescending(f => f.LastWriteTime)
        .Take(10)
        .ToList();

    foreach (var log in logs)
    {
        AnsiConsole.MarkupLine(
            $"  {Markup.Escape(log.Name)}  ({log.Length:N0} bytes, {log.LastWriteTime:yyyy-MM-dd HH:mm})");
    }

    if (logs.Count > 0)
    {
        AnsiConsole.MarkupLine($"\n[cyan]View latest: type {Markup.Escape(logs[0].FullName)}[/]");
    }
    return 0;
}

int ShowHelp()
{
    AnsiConsole.Write(new Rule($"[green]RALPH - Task Orchestrator[/] [dim]v{Version}[/]").RuleStyle("blue"));
    AnsiConsole.MarkupLine("\nUsage: [green]ralph[/] [yellow][[command]][/]\n");

    var table = new Table().Border(TableBorder.Simple);
    table.AddColumn("[bold]Command[/]");
    table.AddColumn("[bold]Description[/]");
    table.AddRow("[green]--plan[/] <file>", "Generate tasks.json from a PRD file");
    table.AddRow("[green]--run[/] [[file]]", "Run all pending tasks (default: tasks.json)");
    table.AddRow("[green]--dry-run[/]", "Preview execution without changes");
    table.AddRow("[green]--task[/] <id>", "Run a specific task by ID");
    table.AddRow("[green]--interactive[/]", "Run tasks interactively (confirm each)");
    table.AddRow("[green]--list[/], -l", "List all pending tasks");
    table.AddRow("[green]--prompts[/], -p", "Show all task prompts");
    table.AddRow("[green]--status[/], -s", "Show progress status");
    table.AddRow("[green]--reset[/], -r", "Reset all tasks to pending");
    table.AddRow("[green]--logs[/]", "Show recent log files");
    table.AddRow("[green]--help[/], -h", "Show this help message");
    AnsiConsole.Write(table);

    AnsiConsole.MarkupLine("\n[blue]Workflow:[/]");
    AnsiConsole.MarkupLine("  1. ralph --plan PRD.md");
    AnsiConsole.MarkupLine("  2. ralph --list");
    AnsiConsole.MarkupLine("  3. ralph --dry-run");
    AnsiConsole.MarkupLine("  4. ralph --run\n");

    AnsiConsole.MarkupLine("[blue]Environment variables:[/]");
    AnsiConsole.MarkupLine("  MAX_RETRIES    Max retry attempts (default: 2)");
    AnsiConsole.MarkupLine("  RETRY_DELAY    Seconds between retries (default: 5)\n");
    return 0;
}

int ShowUnknown(string cmd)
{
    AnsiConsole.MarkupLine($"[red]Unknown option: {Markup.Escape(cmd)}[/]");
    AnsiConsole.MarkupLine("Run [green]ralph --help[/] for usage information.");
    return 1;
}

// ═══════════════════════════════════════════════════════════════════════════════
// Display helpers
// ═══════════════════════════════════════════════════════════════════════════════

void ShowProgress(TaskManager tm, RalphLogger? logger)
{
    var total = tm.Data.Tasks.Count;
    var done = tm.Data.Tasks.Count(t => t.Done);
    var pending = tm.GetPendingTasks();
    var blocked = pending.Count(t => !tm.CheckDependencies(t.Id, out _));
    var ready = pending.Count - blocked;

    AnsiConsole.Write(new Rule($"[green]RALPH - Task Orchestrator[/] [dim]v{Version}[/]").RuleStyle("blue"));
    AnsiConsole.MarkupLine(
        $"Total: {total} | [green]Done: {done}[/] | [yellow]Ready: {ready}[/] | [red]Blocked: {blocked}[/]");
    if (logger != null)
        AnsiConsole.MarkupLine($"[cyan]Log: {Markup.Escape(logger.LogFile)}[/]");
    AnsiConsole.Write(new Rule().RuleStyle("blue"));
}

void DisplayTask(TaskManager tm, string taskId)
{
    var task = tm.GetTask(taskId)!;
    var index = tm.GetTaskIndex(taskId);
    var total = tm.Data.Tasks.Count;
    var outputFiles = task.OutputFiles is { Count: > 0 } ? string.Join(", ", task.OutputFiles) : "";
    var deps = task.DependsOn is { Count: > 0 } ? string.Join(", ", task.DependsOn) : "";

    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule().RuleStyle("blue"));
    AnsiConsole.MarkupLine(
        $"[yellow][[{index}/{total}]][/] [green]Task ID:[/] {Markup.Escape(task.Id)}");
    AnsiConsole.MarkupLine(
        $"[green]Phase:[/] {Markup.Escape(task.Phase ?? "-")} | [green]Category:[/] {Markup.Escape(task.Category ?? "-")}");
    AnsiConsole.MarkupLine($"[green]Title:[/] {Markup.Escape(task.Title)}");

    if (!string.IsNullOrEmpty(task.Description))
        AnsiConsole.MarkupLine($"[green]Description:[/] {Markup.Escape(task.Description)}");
    if (!string.IsNullOrEmpty(deps))
        AnsiConsole.MarkupLine($"[cyan]Depends On:[/] {Markup.Escape(deps)}");
    if (!string.IsNullOrEmpty(outputFiles))
        AnsiConsole.MarkupLine($"[cyan]Output Files:[/] {Markup.Escape(outputFiles)}");
    if (!string.IsNullOrEmpty(task.Prompt))
        AnsiConsole.MarkupLine("[cyan]Claude Prompt:[/] (available)");

    if (task.Subtasks is { Count: > 0 })
    {
        AnsiConsole.MarkupLine("[yellow]Subtasks:[/]");
        foreach (var sub in task.Subtasks)
        {
            var check = sub.Done ? "v" : " ";
            AnsiConsole.MarkupLine(
                $"  [[{check}]] {Markup.Escape(sub.Id)}: {Markup.Escape(sub.Title)}");
        }
    }

    AnsiConsole.Write(new Rule().RuleStyle("blue"));
    AnsiConsole.WriteLine();
}

// ═══════════════════════════════════════════════════════════════════════════════
// Task execution
// ═══════════════════════════════════════════════════════════════════════════════

async Task<int> RunTaskAuto(
    TaskManager tm, ClaudeService claude, GitService git, RalphLogger logger,
    string taskId, bool dryRun, bool commitOnComplete, CancellationToken ct)
{
    var task = tm.GetTask(taskId)!;

    // Check dependencies
    if (!tm.CheckDependencies(taskId, out var blockedBy))
    {
        AnsiConsole.MarkupLine("[yellow]Skipping task due to unmet dependencies.[/]");
        foreach (var dep in blockedBy)
            AnsiConsole.MarkupLine($"  [red]Blocked by:[/] {Markup.Escape(dep)}");
        logger.Warn($"Skipped {taskId}: blocked by {string.Join(", ", blockedBy)}");
        return 2; // blocked
    }

    logger.TaskStart(taskId, task.Title);
    DisplayTask(tm, taskId);

    AnsiConsole.MarkupLine($"[blue]Executing task: {Markup.Escape(task.Title)}[/]");
    AnsiConsole.WriteLine();

    if (!string.IsNullOrEmpty(task.Prompt))
    {
        var fullPrompt = $"""
            Task ID: {taskId}
            Task: {task.Title}

            {task.Prompt}

            참고: {tasksFile} 파일에서 apiSpecs, samplePages 등 추가 정보를 확인할 수 있습니다.
            완료 후 생성된 파일 목록을 알려주세요.
            """;

        if (dryRun)
        {
            AnsiConsole.MarkupLine("[cyan]Prompt:[/]");
            AnsiConsole.Write(new Panel(Markup.Escape(task.Prompt)).Border(BoxBorder.Rounded));
            AnsiConsole.MarkupLine("[cyan][[DRY-RUN]] Would execute Claude Code with above prompt[/]");
            logger.Info("[DRY-RUN] Skipped Claude Code execution");
        }
        else
        {
            // Display prompt summary
            AnsiConsole.MarkupLine("[cyan]Prompt:[/]");
            AnsiConsole.Write(new Panel(Markup.Escape(task.Prompt)).Border(BoxBorder.Rounded));
            AnsiConsole.MarkupLine("\n[cyan]Running Claude Code...[/]\n");

            var result = await claude.RunWithRetryAsync(fullPrompt, logger: logger, ct: ct);
            if (!result.Success)
            {
                AnsiConsole.MarkupLine("\n[red]Claude Code execution failed[/]");
                logger.TaskEnd(taskId, "failed");
                return 1;
            }

            AnsiConsole.MarkupLine("\n[green]Claude Code execution completed[/]");
        }
    }
    else
    {
        AnsiConsole.MarkupLine("[yellow]No prompt defined for this task. Skipping Claude Code execution.[/]");
        logger.Info($"No prompt for task {taskId}");
    }

    // Process subtasks
    if (task.Subtasks is { Count: > 0 })
    {
        foreach (var sub in task.Subtasks.Where(s => !s.Done))
        {
            AnsiConsole.MarkupLine($"  [yellow]Subtask:[/] {Markup.Escape(sub.Title)}");
            tm.MarkSubtaskDone(taskId, sub.Id);
            AnsiConsole.MarkupLine($"  [green]Subtask completed[/]");
        }
    }

    // Mark done and persist (needed for dependency advancement; dry-run restores later)
    tm.MarkTaskDone(taskId);
    await tm.SaveAsync();

    if (!dryRun)
    {
        AnsiConsole.MarkupLine($"[green]Task completed: {Markup.Escape(task.Title)}[/]");
        logger.TaskEnd(taskId, "completed");

        if (commitOnComplete)
            await git.CommitChangesAsync(taskId, task.Title, tm.CommitTemplate, logger, ct);
    }
    else
    {
        AnsiConsole.MarkupLine(
            $"[cyan][[DRY-RUN]] Would mark task as done: {Markup.Escape(task.Title)}[/]");
        logger.TaskEnd(taskId, "dry-run");
    }

    return 0;
}

async Task<int> RunAutoLoop(
    TaskManager tm, ClaudeService claude, GitService git, RalphLogger logger,
    bool dryRun, bool commitOnComplete, CancellationToken ct)
{
    ShowProgress(tm, logger);

    while (true)
    {
        ct.ThrowIfCancellationRequested();

        var nextId = tm.GetNextReadyTask();
        if (nextId == null)
        {
            var remaining = tm.GetNextTask();
            if (remaining != null)
            {
                AnsiConsole.MarkupLine(
                    "\n[red]All remaining tasks are blocked by unmet dependencies:[/]");
                foreach (var t in tm.GetPendingTasks())
                {
                    var deps = t.DependsOn is { Count: > 0 }
                        ? string.Join(", ", t.DependsOn)
                        : "none";
                    AnsiConsole.MarkupLine(
                        $"  {Markup.Escape(t.Id)}: depends on {Markup.Escape(deps)}");
                }
                logger.Warn("Execution stopped: remaining tasks blocked by dependencies");
            }
            else
            {
                AnsiConsole.MarkupLine("\n[green]All tasks completed![/]");
                logger.Info("All tasks completed");
            }
            break;
        }

        var exitCode = await RunTaskAuto(tm, claude, git, logger, nextId,
            dryRun, commitOnComplete, ct);

        if (exitCode == 2) continue; // blocked, try next
        if (exitCode != 0)
        {
            AnsiConsole.MarkupLine("[red]Task failed. Stopping auto execution.[/]");
            logger.Error("Auto execution stopped due to task failure");
            break;
        }
    }

    return 0;
}

async Task<int> RunInteractiveLoop(
    TaskManager tm, ClaudeService claude, GitService git, RalphLogger logger,
    CancellationToken ct)
{
    ShowProgress(tm, logger);

    while (true)
    {
        ct.ThrowIfCancellationRequested();

        var nextId = tm.GetNextReadyTask();
        if (nextId == null)
        {
            var remaining = tm.GetNextTask();
            if (remaining != null)
            {
                AnsiConsole.MarkupLine(
                    "\n[red]All remaining tasks are blocked by unmet dependencies:[/]");
                foreach (var t in tm.GetPendingTasks())
                {
                    var deps = t.DependsOn is { Count: > 0 }
                        ? string.Join(", ", t.DependsOn)
                        : "none";
                    AnsiConsole.MarkupLine(
                        $"  {Markup.Escape(t.Id)}: depends on {Markup.Escape(deps)}");
                }
            }
            else
            {
                AnsiConsole.MarkupLine("\n[green]All tasks completed![/]");
            }
            break;
        }

        var task = tm.GetTask(nextId)!;

        if (!tm.CheckDependencies(nextId, out var blocked))
        {
            foreach (var dep in blocked)
                AnsiConsole.MarkupLine(
                    $"[red]Blocked:[/] Task '{Markup.Escape(nextId)}' depends on '{Markup.Escape(dep)}'");
            continue;
        }

        DisplayTask(tm, nextId);

        // Interactive choice loop
        var done = false;
        while (!done)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]Execute this task?[/]")
                    .AddChoices("Yes - Execute", "Preview prompt", "Skip", "Quit"));

            switch (choice)
            {
                case "Yes - Execute":
                {
                    logger.TaskStart(nextId, task.Title);
                    AnsiConsole.MarkupLine($"[blue]Executing task: {Markup.Escape(task.Title)}[/]\n");

                    if (!string.IsNullOrEmpty(task.Prompt))
                    {
                        var fullPrompt =
                            $"Task ID: {nextId}\nTask: {task.Title}\n\n{task.Prompt}\n\n참고: {tasksFile} 파일에서 apiSpecs, samplePages 등 추가 정보를 확인할 수 있습니다.\n완료 후 생성된 파일 목록을 알려주세요.";

                        AnsiConsole.MarkupLine("[cyan]Running Claude Code...[/]\n");
                        var result = await claude.RunWithRetryAsync(fullPrompt, logger: logger, ct: ct);

                        if (!result.Success)
                        {
                            AnsiConsole.MarkupLine("\n[red]Claude Code execution failed[/]");
                            if (!AnsiConsole.Confirm("Continue anyway?", defaultValue: false))
                            {
                                logger.TaskEnd(nextId, "failed");
                                return 1;
                            }
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("\n[green]Claude Code execution completed[/]");
                        }
                    }

                    // Process subtasks
                    if (task.Subtasks is { Count: > 0 })
                    {
                        foreach (var sub in task.Subtasks.Where(s => !s.Done))
                        {
                            AnsiConsole.MarkupLine(
                                $"  [yellow]Subtask:[/] {Markup.Escape(sub.Title)}");
                            tm.MarkSubtaskDone(nextId, sub.Id);
                            AnsiConsole.MarkupLine("  [green]Subtask completed[/]");
                        }
                    }

                    tm.MarkTaskDone(nextId);
                    await tm.SaveAsync();
                    AnsiConsole.MarkupLine(
                        $"[green]Task completed: {Markup.Escape(task.Title)}[/]");
                    logger.TaskEnd(nextId, "completed");

                    if (tm.CommitOnComplete)
                        await git.CommitChangesAsync(nextId, task.Title, tm.CommitTemplate, logger, ct);

                    done = true;
                    break;
                }

                case "Preview prompt":
                    if (!string.IsNullOrEmpty(task.Prompt))
                    {
                        AnsiConsole.Write(
                            new Panel(Markup.Escape(task.Prompt))
                                .Header("[cyan]Claude Code Prompt[/]")
                                .Border(BoxBorder.Rounded));
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[yellow]No prompt defined for this task.[/]");
                    }
                    break; // loops back to ask again

                case "Skip":
                    AnsiConsole.MarkupLine("[yellow]Skipping task...[/]");
                    logger.Info($"Task {nextId} skipped by user");
                    done = true;
                    break;

                case "Quit":
                    AnsiConsole.MarkupLine("[red]Quitting...[/]");
                    logger.Info("User quit");
                    return 0;
            }
        }
    }

    return 0;
}

// ═══════════════════════════════════════════════════════════════════════════════
// Utility functions
// ═══════════════════════════════════════════════════════════════════════════════

void RequireFile(string path)
{
    if (File.Exists(path)) return;
    AnsiConsole.MarkupLine(
        $"[red]Error: {Markup.Escape(path)} not found. Run 'ralph --plan <prd-file>' to generate it.[/]");
    Environment.Exit(1);
}

void CheckCommand(string name, string displayName, string url)
{
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = name,
            Arguments = "--version",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        proc?.WaitForExit(5000);
    }
    catch (Exception)
    {
        AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(displayName)} is required but not found.[/]");
        AnsiConsole.MarkupLine($"Install from: {Markup.Escape(url)}");
        Environment.Exit(1);
    }
}

string LoadEmbeddedSchema()
{
    var assembly = Assembly.GetExecutingAssembly();
    using var stream = assembly.GetManifestResourceStream("ralph-schema.json")
                       ?? throw new FileNotFoundException("Embedded ralph-schema.json not found");
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}
