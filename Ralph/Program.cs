using System.Reflection;
using Ralph.Models;
using Ralph.Services;
using Spectre.Console;

const string Version = "1.0";

// ─── UTF-8 console encoding ─────────────────────────────────────────────────
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

// ─── Ctrl+C handling ─────────────────────────────────────────────────────────
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    if (cts.IsCancellationRequested)
    {
        // Second Ctrl+C: force exit immediately
        e.Cancel = false;
        return;
    }
    e.Cancel = true;
    cts.Cancel();
    AnsiConsole.MarkupLine("\n[red]Interrupted. Aborting...[/]");
};

// ─── Environment variables ───────────────────────────────────────────────────
var maxRetries = int.TryParse(Environment.GetEnvironmentVariable("MAX_RETRIES"), out var mr) ? mr : 2;
var retryDelay = int.TryParse(Environment.GetEnvironmentVariable("RETRY_DELAY"), out var rd) ? rd : 5;
var envMaxParallel = int.TryParse(Environment.GetEnvironmentVariable("RALPH_MAX_PARALLEL"), out var mp) ? mp : 0;
var envParallelDisabled = Environment.GetEnvironmentVariable("RALPH_PARALLEL")?.ToLower() == "false";
var envStrictFiles = Environment.GetEnvironmentVariable("RALPH_STRICT_FILES")?.ToLower() == "true";
var envBudgetRaw = Environment.GetEnvironmentVariable("RALPH_BUDGET_USD");
double? envBudgetUsd = double.TryParse(envBudgetRaw,
        System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var ebu)
    ? ebu
    : (double?)null;

// ─── Dependency checks ──────────────────────────────────────────────────────
CheckCommand("claude", "Claude Code CLI", "https://claude.ai/code");
CheckCommand("git", "Git", "https://git-scm.com");

// ─── Parse CLI arguments ────────────────────────────────────────────────────
var argList = args.ToList();
var debug = argList.Remove("--debug");
var sequential = argList.Remove("--sequential");
var forceFlag = argList.Remove("--force");
var strictFiles = argList.Remove("--strict-files") || envStrictFiles;
var maxParallelArg = 0;
var maxParallelIdx = argList.IndexOf("--max-parallel");
if (maxParallelIdx >= 0 && maxParallelIdx + 1 < argList.Count)
{
    int.TryParse(argList[maxParallelIdx + 1], out maxParallelArg);
    argList.RemoveRange(maxParallelIdx, 2);
}

double? cliBudgetUsd = null;
var budgetIdx = argList.IndexOf("--budget-usd");
if (budgetIdx >= 0 && budgetIdx + 1 < argList.Count)
{
    var raw = argList[budgetIdx + 1];
    if (double.TryParse(raw,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var bv))
    {
        cliBudgetUsd = bv;
    }
    else
    {
        AnsiConsole.MarkupLine(
            $"[red]Error: --budget-usd 값을 파싱할 수 없습니다: '{Markup.Escape(raw)}'[/]");
        return 1;
    }
    argList.RemoveRange(budgetIdx, 2);
}
double? budgetUsd = cliBudgetUsd ?? envBudgetUsd;

var modelArg = "opus";
var modelIdx = argList.IndexOf("--model");
if (modelIdx >= 0 && modelIdx + 1 < argList.Count)
{
    var modelValue = argList[modelIdx + 1].ToLower();
    if (modelValue is "sonnet" or "opus")
    {
        modelArg = modelValue;
    }
    else
    {
        AnsiConsole.MarkupLine($"[red]Error: Invalid model '{Markup.Escape(modelValue)}'. Allowed: sonnet, opus[/]");
        return 1;
    }
    argList.RemoveRange(modelIdx, 2);
}

// ─── Resolve tasks file (used by most commands) ─────────────────────────────
var tasksFile = "tasks.json";

// Global -f / --file flag (works with any command)
var fileIdx = argList.IndexOf("--file");
if (fileIdx < 0) fileIdx = argList.IndexOf("-f");
if (fileIdx >= 0 && fileIdx + 1 < argList.Count)
{
    tasksFile = argList[fileIdx + 1];
    argList.RemoveRange(fileIdx, 2);
}
// Positional file argument for commands that support it
else if (argList.Count > 1
         && argList[0] is "--run" or "--dry-run" or "--list" or "-l" or "--graph" or "-g"
             or "--prompts" or "-p" or "--status" or "-s" or "--reset" or "-r" or "--interactive"
         && !argList[1].StartsWith("--"))
{
    tasksFile = argList[1];
}

// ─── Parse command ───────────────────────────────────────────────────────────
var command = argList.Count > 0 ? argList[0] : "";

try
{
    return await (command switch
    {
        "--plan" => HandlePlan(),
        "--plan-prompt" => HandlePlanPrompt(),
        "--run" => HandleRun(),
        "--dry-run" => HandleDryRun(),
        "--task" => HandleSingleTask(),
        "--interactive" => HandleInteractive(),
        "--list" or "-l" => HandleList(),
        "--graph" or "-g" => HandleGraph(),
        "--prompts" or "-p" => HandlePrompts(),
        "--status" or "-s" => HandleStatus(),
        "--reset" or "-r" => HandleReset(),
        "--logs" => HandleLogs(),
        "--cost" => HandleCost(),
        "--show-prompt" => HandleShowPrompt(),
        "--validate" => HandleValidate(),
        "--worktree-cleanup" => HandleWorktreeCleanup(),
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
    if (argList.Count < 2)
    {
        AnsiConsole.MarkupLine("[red]Error: PRD file required. Usage: ralph --plan <prd-file>[/]");
        return 1;
    }

    var prdFile = argList[1];
    if (!File.Exists(prdFile))
    {
        AnsiConsole.MarkupLine($"[red]Error: File '{Markup.Escape(prdFile)}' not found.[/]");
        return 1;
    }

    // Backup existing tasks.json before generating new one
    if (File.Exists(tasksFile))
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupPath = $"{tasksFile}.backup.{timestamp}";
        File.Copy(tasksFile, backupPath);
        AnsiConsole.MarkupLine($"[yellow]기존 tasks.json을 백업했습니다: {Markup.Escape(backupPath)}[/]");
    }

    var schemaContent = LoadEmbeddedSchema();
    var claude = new ClaudeService(maxRetries, retryDelay) { Debug = debug };
    var git = new GitService();
    using var logger = new RalphLogger();

    // Initialize git repo if not already initialized
    if (!await git.IsRepoInitializedAsync(cts.Token))
        await git.InitAsync(logger, cts.Token);

    var planModel = modelArg;

    var generator = new PlanGenerator();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = await generator.GenerateAsync(prdFile, schemaContent, tasksFile, claude, planModel, logger, cts.Token);
    sw.Stop();

    if (result == 0)
        AnsiConsole.MarkupLine($"\n[green]플랜 생성 완료[/] [dim]({sw.Elapsed.Minutes}분 {sw.Elapsed.Seconds}초)[/]");

    return result;
}

Task<int> HandlePlanPrompt()
{
    if (argList.Count < 2)
    {
        AnsiConsole.MarkupLine("[red]Error: PRD file required. Usage: ralph --plan-prompt <prd-file>[/]");
        return Task.FromResult(1);
    }

    var prdFile = argList[1];
    if (!File.Exists(prdFile))
    {
        AnsiConsole.MarkupLine($"[red]Error: File '{Markup.Escape(prdFile)}' not found.[/]");
        return Task.FromResult(1);
    }

    var prdFullPath = Path.GetFullPath(prdFile);
    var tasksFullPath = Path.GetFullPath(tasksFile);
    var schemaContent = LoadEmbeddedSchema();
    var prompt = PlanGenerator.BuildPlanPrompt(prdFullPath, schemaContent, tasksFullPath);

    AnsiConsole.Write(new Rule("[green]RALPH - Plan Prompt Preview[/]").RuleStyle("blue"));
    AnsiConsole.MarkupLine($"[cyan]PRD File:[/] {Markup.Escape(prdFile)}");
    AnsiConsole.Write(new Rule().RuleStyle("blue"));
    AnsiConsole.WriteLine(prompt);

    return Task.FromResult(0);
}

async Task<int> HandleRun()
{
    RequireFile(tasksFile);
    var tm = await TaskManager.LoadAsync(tasksFile);
    var claude = new ClaudeService(maxRetries, retryDelay) { Debug = debug };
    var git = new GitService();
    using var logger = new RalphLogger();
    logger.Info($"Tasks file: {tasksFile}");

    // 세션 시작 시 자동 로그 rotation (silent)
    LogRotator.Rotate(retentionDays: tm.Data.Workflow?.LogRetentionDays, quiet: true);

    // 실행 전 plan 검증 — error가 있으면 중단 (--force 시 우회)
    var preReport = PlanValidator.Validate(tm);
    if (preReport.HasErrors)
    {
        AnsiConsole.MarkupLine($"[red]✗ Plan 검증 실패 ({preReport.Errors.Count}개 error):[/]");
        foreach (var e in preReport.Errors)
            AnsiConsole.MarkupLine($"  [red]•[/] {Markup.Escape(e)}");
        if (!forceFlag)
        {
            AnsiConsole.MarkupLine("\n[yellow]계속하려면 --force 플래그를 추가하거나 'ralph --validate'로 자세히 보세요.[/]");
            return 1;
        }
        AnsiConsole.MarkupLine("[yellow]--force 지정됨 — error 무시하고 진행합니다.[/]\n");
    }
    if (preReport.HasWarnings)
    {
        AnsiConsole.MarkupLine($"[yellow]⚠ Plan 검증 경고 {preReport.Warnings.Count}개 (실행은 계속됨, 자세히는 'ralph --validate')[/]");
        foreach (var w in preReport.Warnings.Take(3))
            AnsiConsole.MarkupLine($"  [yellow]•[/] {Markup.Escape(w)}");
        if (preReport.Warnings.Count > 3)
            AnsiConsole.MarkupLine($"  [dim]... 외 {preReport.Warnings.Count - 3}개[/]");
        AnsiConsole.WriteLine();
    }

    // 병렬 실행 여부 결정
    var parallelConfig = tm.ParallelConfig;
    var useParallel = !sequential && !envParallelDisabled && parallelConfig.Enabled;

    // 그래프 스캔: pending 태스크들의 topological layer 중 최대 폭을 계산
    const int hardCap = 16;
    var pendingIds = tm.GetPendingTasks().Select(t => t.Id).ToHashSet();
    var layers = tm.ComputeTopologicalLayers();
    var maxLayerWidth = layers
        .Select(l => l.Count(id => pendingIds.Contains(id)))
        .DefaultIfEmpty(0)
        .Max();
    var scannedConcurrency = Math.Clamp(maxLayerWidth, 1, hardCap);

    // 우선순위: --max-parallel > RALPH_MAX_PARALLEL > 그래프 스캔 결과
    var concurrency = maxParallelArg > 0 ? maxParallelArg
        : envMaxParallel > 0 ? envMaxParallel
        : scannedConcurrency;
    if (concurrency > hardCap) concurrency = hardCap;

    if (useParallel && maxParallelArg == 0 && envMaxParallel == 0)
    {
        AnsiConsole.MarkupLine(
            $"[cyan]그래프 스캔:[/] 최대 동시 실행 가능 태스크 {maxLayerWidth}개 → {concurrency}개로 설정 (상한 {hardCap})");
    }

    var sessionStart = DateTime.UtcNow;
    var sessionStopwatch = System.Diagnostics.Stopwatch.StartNew();
    var totalAtStart = tm.GetPendingTasks().Count;
    int exitCode;

    // P1-3: 단일 CostTracker 인스턴스를 ParallelExecutor / RunAutoLoop / Notification 모두 공유.
    var costTracker = new CostTracker();

    if (useParallel)
    {
        logger.Info($"Exec mode: parallel (max concurrent: {concurrency})");
        AnsiConsole.MarkupLine($"[green]병렬 실행 모드[/] (최대 동시 실행: {concurrency})");

        ShowProgress(tm, logger);

        var worktree = new WorktreeService(git);
        var executor = new ParallelExecutor(
            tm, claude, git, worktree, logger, tasksFile, modelArg,
            strictFiles: strictFiles, budgetUsd: budgetUsd, cost: costTracker);
        exitCode = await executor.RunAsync(concurrency, cts.Token);
        if (exitCode == 0 && executor.BudgetReached) exitCode = 2;
    }
    else
    {
        logger.Info("Exec mode: sequential");
        AnsiConsole.MarkupLine("[yellow]순차 실행 모드[/]");

        exitCode = await RunAutoLoop(tm, claude, git, logger,
            dryRun: false, commitOnComplete: true, modelArg, budgetUsd, costTracker, cts.Token);
    }

    sessionStopwatch.Stop();

    // 세션 종료 알림 (webhook 설정 있을 때만 발화)
    try
    {
        await tm.ReloadAsync();
        var stillPending = tm.GetPendingTasks().Count;
        var completedNow = Math.Max(0, totalAtStart - stillPending);
        var success = exitCode == 0;
        var costSummary = await costTracker.GetTotalUsdAsync(cts.Token);

        var notifier = new NotificationService();
        await notifier.NotifyAsync(
            success: success,
            sessionId: sessionStart.ToString("yyyyMMdd-HHmmss"),
            totalTasks: totalAtStart,
            completedTasks: completedNow,
            failedTasks: success ? 0 : Math.Max(0, totalAtStart - completedNow),
            durationSec: sessionStopwatch.Elapsed.TotalSeconds,
            estimatedCostUsd: costSummary,
            settings: tm.Data.Workflow?.Notifications,
            logger: logger,
            ct: cts.Token);
    }
    catch (Exception ex)
    {
        logger.Warn($"Notification post-processing failed: {ex.Message}");
    }

    return exitCode;
}

async Task<int> HandleDryRun()
{
    RequireFile(tasksFile);
    var tm = await TaskManager.LoadAsync(tasksFile);
    var claude = new ClaudeService(maxRetries, retryDelay) { Debug = debug };
    var git = new GitService();
    using var logger = new RalphLogger();
    logger.Info("Exec mode: dry-run");

    // Backup for restore after dry-run
    var backupJson = await File.ReadAllTextAsync(tasksFile, cts.Token);

    var result = await RunAutoLoop(tm, claude, git, logger,
        dryRun: true, commitOnComplete: false, modelArg, budgetUsd: null,
        cost: new CostTracker(), cts.Token);

    // Restore original
    await File.WriteAllTextAsync(tasksFile, backupJson, cts.Token);
    AnsiConsole.MarkupLine($"[cyan][[DRY-RUN]] {Markup.Escape(tasksFile)} restored to original state.[/]");

    return result;
}

async Task<int> HandleSingleTask()
{
    if (argList.Count < 2)
    {
        AnsiConsole.MarkupLine("[red]Error: Task ID required. Usage: ralph --task <task-id>[/]");
        return 1;
    }

    var taskId = argList[1];
    RequireFile(tasksFile);
    var tm = await TaskManager.LoadAsync(tasksFile);

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

        if (forceFlag)
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

    var claude = new ClaudeService(maxRetries, retryDelay) { Debug = debug };
    var git = new GitService();
    using var logger = new RalphLogger();

    return await RunTaskAuto(tm, claude, git, logger, taskId,
        dryRun: false, commitOnComplete: tm.CommitOnComplete, modelArg,
        cost: new CostTracker(), cts.Token,
        force: true);
}

async Task<int> HandleInteractive()
{
    RequireFile(tasksFile);
    var tm = await TaskManager.LoadAsync(tasksFile);
    var claude = new ClaudeService(maxRetries, retryDelay) { Debug = debug };
    var git = new GitService();
    using var logger = new RalphLogger();
    logger.Info("Exec mode: interactive");

    return await RunInteractiveLoop(tm, claude, git, logger, modelArg, new CostTracker(), cts.Token);
}

async Task<int> HandleList()
{
    RequireFile(tasksFile);
    var tm = await TaskManager.LoadAsync(tasksFile);

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

async Task<int> HandleGraph()
{
    RequireFile(tasksFile);
    var tm = await TaskManager.LoadAsync(tasksFile);
    var renderer = new GraphRenderer(tm);
    renderer.RenderToConsole();
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

    // 병렬 배치 정보 표시
    var readyTasks = tm.GetAllReadyTasks();
    if (readyTasks.Count > 1)
    {
        var batches = tm.GetParallelBatches();
        AnsiConsole.MarkupLine($"\n[green]병렬 실행 가능한 태스크: {readyTasks.Count}개[/]");
        for (var i = 0; i < batches.Count; i++)
        {
            AnsiConsole.MarkupLine($"  [cyan]Batch {i + 1}:[/] {string.Join(", ", batches[i].Select(Markup.Escape))}");
        }
    }

    // P3-1/P2-4: 현재 worktree를 fs로 검출 (다른 터미널의 ralph --run 가시성 확보)
    // 모두 idle인 경우 stale 잔존 가능성 → cleanup 안내를 강조한다.
    const string worktreeBase = ".ralph-worktrees";
    const string logDir = ".ralph-logs";
    if (Directory.Exists(worktreeBase))
    {
        var threshold = DateTime.Now.AddSeconds(-30);
        var active = Directory.GetDirectories(worktreeBase)
            .Select(d => new DirectoryInfo(d))
            .Select(d =>
            {
                var logFile = Path.Combine(logDir, $"{d.Name}.log");
                DateTime? logMtime = File.Exists(logFile) ? File.GetLastWriteTime(logFile) : null;
                return new { TaskId = d.Name, Created = d.CreationTime, LogMtime = logMtime };
            })
            .OrderByDescending(x => x.LogMtime ?? x.Created)
            .ToList();

        if (active.Count > 0)
        {
            var liveCount = active.Count(w => w.LogMtime is { } m && m >= threshold);
            var allIdle = liveCount == 0;
            var header = allIdle
                ? $"[dim]잔존 worktree {active.Count}개 (모두 idle — stale 가능성)[/]"
                : $"[yellow]현재 worktree: {active.Count}개 (live {liveCount}개)[/]";
            AnsiConsole.MarkupLine($"\n{header}");
            foreach (var w in active)
            {
                var fresh = w.LogMtime is { } m && m >= threshold ? "[green]live[/]" : "[dim]idle[/]";
                var lastLog = w.LogMtime?.ToString("HH:mm:ss") ?? "(no log)";
                AnsiConsole.MarkupLine(
                    $"  {fresh} {Markup.Escape(w.TaskId)} [dim](last log: {lastLog})[/]");
            }
            if (allIdle)
                AnsiConsole.MarkupLine(
                    "[yellow]→ 다른 ralph 프로세스가 동작 중이 아니라면 [cyan]ralph --worktree-cleanup[/]으로 정리하세요.[/]");
            else
                AnsiConsole.MarkupLine(
                    "[dim]idle 상태의 worktree는 종료 후 cleanup이 누락된 잔존본일 수 있습니다.[/]");
        }
    }

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

async Task<int> HandleCost()
{
    var tracker = new CostTracker();
    await tracker.PrintSummaryAsync(cts.Token);
    return 0;
}

async Task<int> HandleValidate()
{
    RequireFile(tasksFile);
    var tm = await TaskManager.LoadAsync(tasksFile);

    AnsiConsole.Write(new Rule($"[green]Validating {Markup.Escape(tasksFile)}[/]").RuleStyle("blue"));
    AnsiConsole.MarkupLine($"태스크 수: [cyan]{tm.Data.Tasks.Count}[/]");
    AnsiConsole.WriteLine();

    var report = PlanValidator.Validate(tm);
    return PlanValidator.PrintReport(report, failOnWarning: forceFlag);
}

async Task<int> HandleShowPrompt()
{
    if (argList.Count < 2)
    {
        AnsiConsole.MarkupLine("[red]Error: Task ID required. Usage: ralph --show-prompt <task-id>[/]");
        return 1;
    }

    var taskId = argList[1];
    RequireFile(tasksFile);
    var tm = await TaskManager.LoadAsync(tasksFile);

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

    var fullPrompt = PromptBuilder.Build(task, tm, tasksFile, siblings);

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

async Task<int> HandleWorktreeCleanup()
{
    var git = new GitService();
    using var logger = new RalphLogger();
    var worktree = new WorktreeService(git);

    var stale = await worktree.DetectStaleWorktreesAsync(cts.Token);
    if (stale.Count == 0)
    {
        AnsiConsole.MarkupLine("[green]정리할 worktree가 없습니다.[/]");
        return 0;
    }

    AnsiConsole.MarkupLine($"[yellow]{stale.Count}개의 ralph worktree를 발견했습니다:[/]");
    foreach (var s in stale)
        AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(s)}[/]");

    await worktree.CleanupAllAsync(logger, cts.Token);
    AnsiConsole.MarkupLine("[green]모든 worktree가 정리되었습니다.[/]");
    return 0;
}

async Task<int> HandleLogs()
{
    const string logDir = ".ralph-logs";
    if (!Directory.Exists(logDir))
    {
        AnsiConsole.MarkupLine("[yellow]No logs found.[/]");
        return 0;
    }

    // --cleanup 플래그 처리: 오래된 로그 파일 정리
    if (argList.Contains("--cleanup"))
    {
        var deleted = LogRotator.Rotate(quiet: false);
        if (deleted == 0)
            AnsiConsole.MarkupLine("[green]정리할 오래된 로그가 없습니다.[/]");
        return 0;
    }

    // --live 플래그 파싱
    var liveMode = argList.Contains("--live");
    var logArgs = argList.Skip(1).Where(a => a is not "--live" and not "--cleanup").ToList();

    // ralph --logs [--live] {taskId} → 특정 태스크 로그 출력
    if (logArgs.Count >= 1 && !logArgs[0].StartsWith("--"))
    {
        var taskId = logArgs[0];
        var taskLogFile = Path.Combine(logDir, $"{taskId}.log");

        if (liveMode)
        {
            return await TailFollowAsync(taskLogFile, taskId, cts.Token);
        }

        if (File.Exists(taskLogFile))
        {
            AnsiConsole.MarkupLine($"[blue]Task log: {Markup.Escape(taskId)}[/]");
            AnsiConsole.Write(new Rule().RuleStyle("dim"));
            // FileShare.ReadWrite allows reading while the parallel executor is still writing
            using var fs = new FileStream(taskLogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var content = sr.ReadToEnd();
            AnsiConsole.WriteLine(content);
            return 0;
        }

        AnsiConsole.MarkupLine($"[red]Task log not found: {Markup.Escape(taskId)}[/]");
        AnsiConsole.MarkupLine($"[dim]Expected: {Markup.Escape(taskLogFile)}[/]");
        return 1;
    }

    // 태스크별 로그
    var taskLogs = Directory.GetFiles(logDir, "*.log")
        .Select(f => new FileInfo(f))
        .Where(f => !f.Name.StartsWith("ralph-"))
        .OrderByDescending(f => f.LastWriteTime)
        .ToList();

    if (taskLogs.Count > 0)
    {
        AnsiConsole.MarkupLine("[blue]Task logs:[/]");
        foreach (var log in taskLogs)
        {
            var taskId = Path.GetFileNameWithoutExtension(log.Name);
            AnsiConsole.MarkupLine(
                $"  [cyan]{Markup.Escape(taskId)}[/]  ({log.Length:N0} bytes, {log.LastWriteTime:yyyy-MM-dd HH:mm})");
        }
        AnsiConsole.MarkupLine($"\n[dim]View with: ralph --logs <task-id>[/]");
        AnsiConsole.MarkupLine($"[dim]Live tail: ralph --logs --live <task-id>[/]");
    }

    // 세션 로그
    var sessionLogs = Directory.GetFiles(logDir, "ralph-*.log")
        .Select(f => new FileInfo(f))
        .OrderByDescending(f => f.LastWriteTime)
        .Take(10)
        .ToList();

    if (sessionLogs.Count > 0)
    {
        AnsiConsole.MarkupLine("\n[blue]Session logs:[/]");
        foreach (var log in sessionLogs)
        {
            AnsiConsole.MarkupLine(
                $"  {Markup.Escape(log.Name)}  ({log.Length:N0} bytes, {log.LastWriteTime:yyyy-MM-dd HH:mm})");
        }
    }

    if (taskLogs.Count == 0 && sessionLogs.Count == 0)
    {
        AnsiConsole.MarkupLine("[yellow]No logs found.[/]");
    }

    return 0;
}

async Task<int> TailFollowAsync(string filePath, string taskId, CancellationToken ct)
{
    AnsiConsole.MarkupLine($"[blue]Live tail: {Markup.Escape(taskId)}[/] [dim](Ctrl+C to stop)[/]");
    AnsiConsole.Write(new Rule().RuleStyle("dim"));

    // 파일이 생성될 때까지 대기
    while (!File.Exists(filePath))
    {
        ct.ThrowIfCancellationRequested();
        AnsiConsole.MarkupLine("[dim]로그 파일 대기 중...[/]");
        await Task.Delay(500, ct);
    }

    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var sr = new StreamReader(fs);

    // 기존 내용 먼저 출력
    var existing = await sr.ReadToEndAsync(ct);
    if (!string.IsNullOrEmpty(existing))
        Console.Write(existing);

    // 새 내용을 폴링하며 출력 (버퍼 기반 — 줄바꿈을 원본 그대로 유지)
    var buf = new char[4096];
    while (!ct.IsCancellationRequested)
    {
        var read = await sr.ReadAsync(buf, ct);
        if (read > 0)
        {
            Console.Write(buf, 0, read);
        }
        else
        {
            await Task.Delay(200, ct);
        }
    }

    return 0;
}

int ShowHelp()
{
    AnsiConsole.Write(new Rule($"[green]RALPH - Task Orchestrator[/] [cyan]v{Version}[/]").RuleStyle("grey"));
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
    table.AddRow("[green]--status[/], -s", "Show progress status (with parallel batch info)");
    table.AddRow("[green]--reset[/], -r", "Reset all tasks to pending");
    table.AddRow("[green]--logs[/] [[task-id]]", "Show logs (task log or session log list)");
    table.AddRow("[green]--logs --live[/] <task-id>", "Live tail a task log (like tail -f)");
    table.AddRow("[green]--logs --cleanup[/]", "Delete logs older than retention period");
    table.AddRow("[green]--cost[/]", "Show cumulative token usage and estimated cost");
    table.AddRow("[green]--worktree-cleanup[/]", "Clean up stale worktrees");
    table.AddRow("[green]--help[/], -h", "Show this help message");
    AnsiConsole.Write(table);

    AnsiConsole.MarkupLine("\n[blue]Options:[/]");
    AnsiConsole.MarkupLine("  [green]-f[/], [green]--file[/] <path>    Use custom tasks file (default: tasks.json)");
    AnsiConsole.MarkupLine("  [green]--sequential[/]         Force sequential execution (disable parallel)");
    AnsiConsole.MarkupLine("  [green]--max-parallel[/] N     Maximum concurrent tasks (default: 5)");
    AnsiConsole.MarkupLine("  [green]--force[/]              Bypass dependency/validation checks (--task, --run)");
    AnsiConsole.MarkupLine("  [green]--strict-files[/]       Validate declared vs actual files at merge; abort on undeclared");
    AnsiConsole.MarkupLine("  [green]--budget-usd[/] <amt>   누적 비용이 amt(USD) 도달 시 새 태스크 시작 중단 (--run only)");
    AnsiConsole.MarkupLine("  [green]--model[/] <name>       Model (sonnet, opus; default: opus)");
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
    AnsiConsole.MarkupLine("  RALPH_BUDGET_USD            누적 비용 임계값(USD). CLI --budget-usd가 우선");
    AnsiConsole.MarkupLine("  RALPH_WEBHOOK_URL           Default webhook for session completion notifications");
    AnsiConsole.MarkupLine("  RALPH_LOG_RETENTION_DAYS    Auto-delete logs older than N days (default: 30)\n");
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

    AnsiConsole.Write(new Rule($"[green]RALPH - Task Orchestrator[/] [cyan]v{Version}[/]").RuleStyle("grey"));
    AnsiConsole.MarkupLine(
        $"Total: {total} | [green]Done: {done}[/] | [yellow]Ready: {ready}[/] | [red]Blocked: {blocked}[/]");
    if (ready > 1)
        AnsiConsole.MarkupLine($"[green]{ready}개 태스크 병렬 실행 가능[/]");
    if (logger != null)
        AnsiConsole.MarkupLine($"[cyan]Log: {Markup.Escape(logger.LogFile)}[/]");
    AnsiConsole.Write(new Rule().RuleStyle("grey"));
}

void DisplayTask(TaskManager tm, string taskId)
{
    var task = tm.GetTask(taskId)!;
    var index = tm.GetTaskIndex(taskId);
    var total = tm.Data.Tasks.Count;
    var outputFiles = task.OutputFiles is { Count: > 0 } ? string.Join(", ", task.OutputFiles) : "";
    var modifiedFiles = task.ModifiedFiles is { Count: > 0 } ? string.Join(", ", task.ModifiedFiles) : "";
    var deps = task.DependsOn is { Count: > 0 } ? string.Join(", ", task.DependsOn) : "";

    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule().RuleStyle("grey"));
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
    if (!string.IsNullOrEmpty(modifiedFiles))
        AnsiConsole.MarkupLine($"[cyan]Modified Files:[/] {Markup.Escape(modifiedFiles)}");
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

    AnsiConsole.Write(new Rule().RuleStyle("grey"));
    AnsiConsole.WriteLine();
}

// ═══════════════════════════════════════════════════════════════════════════════
// Task execution (sequential mode)
// ═══════════════════════════════════════════════════════════════════════════════

async Task<int> RunTaskAuto(
    TaskManager tm, ClaudeService claude, GitService git, RalphLogger logger,
    string taskId, bool dryRun, bool commitOnComplete, string? model,
    CostTracker cost, CancellationToken ct,
    bool force = false)
{
    var task = tm.GetTask(taskId)!;

    // Check dependencies (skip when --force was confirmed upstream)
    if (!force && !tm.CheckDependencies(taskId, out var blockedBy))
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

            // P0-1: 예외 경로에서도 cost 기록 보장
            ClaudeResult? result = null;
            try
            {
                result = await claude.RunWithRetryAsync(fullPrompt, model: model, logger: logger, ct: ct);
            }
            finally
            {
                await cost.RecordAsync(taskId, model ?? "opus", result, CancellationToken.None);
            }
            if (result == null || !result.Success)
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
            await git.CommitChangesAsync(taskId, task.Title, tm.CommitTemplate, logger, ct: ct);
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
    bool dryRun, bool commitOnComplete, string? model, double? budgetUsd,
    CostTracker cost, CancellationToken ct)
{
    ShowProgress(tm, logger);

    // P1-1: 단일 BudgetGate가 80%/100% 분기와 메시지를 통합 관리.
    var budgetGate = new BudgetGate(budgetUsd, cost, logger);

    while (true)
    {
        ct.ThrowIfCancellationRequested();

        // F5: budget 게이트 — 새 task 시작 직전 검사. 차단 시 종료 코드 2 반환.
        if (!await budgetGate.CheckAsync(ct)) return 2;

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
            dryRun, commitOnComplete, model, cost, ct);

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
    string? model, CostTracker cost, CancellationToken ct)
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
                        // P0-1: 예외 경로에서도 cost 기록 보장
                        ClaudeResult? result = null;
                        try
                        {
                            result = await claude.RunWithRetryAsync(fullPrompt, model: model, logger: logger, ct: ct);
                        }
                        finally
                        {
                            await cost.RecordAsync(nextId, model ?? "opus", result, CancellationToken.None);
                        }

                        if (result == null || !result.Success)
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
                        await git.CommitChangesAsync(nextId, task.Title, tm.CommitTemplate, logger, ct: ct);

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
