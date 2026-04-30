using Ralph.Commands;
using Ralph.Models;
using Spectre.Console;

namespace Ralph.Services;

/// <summary>
/// 순차/단일/대화형 모드의 실행 루프.
/// 이전엔 Program.cs의 local async function이라 통합 테스트로만 검증 가능했다.
/// </summary>
public sealed class SequentialRunner
{
    private readonly TaskManager _tm;
    private readonly IAgentRunner _claude;
    private readonly GitService _git;
    private readonly RalphLogger _logger;
    private readonly string _tasksFile;
    private readonly string? _modelOverride;
    private readonly VerificationLoop _verificationLoop;

    public SequentialRunner(
        TaskManager tm, IAgentRunner claude, GitService git, RalphLogger logger,
        string tasksFile, string? modelOverride, CostTracker cost)
    {
        _tm = tm;
        _claude = claude;
        _git = git;
        _logger = logger;
        _tasksFile = tasksFile;
        _modelOverride = modelOverride;
        _verificationLoop = new VerificationLoop(claude, new VerificationRunner(), cost, logger);
    }

    /// <summary>단일 태스크 실행 (의존성 검사 → Claude 호출 → done 마킹 → optional commit).</summary>
    public async Task<int> RunTaskAsync(
        string taskId, bool dryRun, bool commitOnComplete,
        CancellationToken ct, bool force = false)
    {
        var task = _tm.GetTask(taskId)!;

        if (!force && !_tm.CheckDependencies(taskId, out var blockedBy))
        {
            AnsiConsole.MarkupLine("[yellow]Skipping task due to unmet dependencies.[/]");
            foreach (var dep in blockedBy)
                AnsiConsole.MarkupLine($"  [red]Blocked by:[/] {Markup.Escape(dep)}");
            _logger.Warn($"Skipped {taskId}: blocked by {string.Join(", ", blockedBy)}");
            return 2; // blocked
        }

        _logger.TaskStart(taskId, task.Title);
        DisplayHelpers.DisplayTask(_tm, taskId);

        AnsiConsole.MarkupLine($"[blue]Executing task: {Markup.Escape(task.Title)}[/]");
        AnsiConsole.WriteLine();

        if (!string.IsNullOrEmpty(task.Prompt))
        {
            // 모든 실행 경로(parallel/sequential/single/interactive)가 동일한 PromptBuilder 사용해
            // Scope·금지 사항·의존 산출물 등의 컨텍스트가 누락 없이 적용되도록 통일.
            var basePrompt = PromptBuilder.Build(task, _tm, _tasksFile, siblings: null);

            if (dryRun)
            {
                AnsiConsole.MarkupLine("[cyan]Prompt:[/]");
                AnsiConsole.Write(new Panel(Markup.Escape(task.Prompt)).Border(BoxBorder.Rounded));
                AnsiConsole.MarkupLine("[cyan][[DRY-RUN]] Would execute Claude Code with above prompt[/]");
                if (task.Verification?.Command is { Length: > 0 } cmd)
                    AnsiConsole.MarkupLine($"[cyan][[DRY-RUN]] Would verify with:[/] [dim]{Markup.Escape(cmd)}[/]");
                _logger.Info("[DRY-RUN] Skipped Claude Code execution");
            }
            else
            {
                AnsiConsole.MarkupLine("[cyan]Prompt:[/]");
                AnsiConsole.Write(new Panel(Markup.Escape(task.Prompt)).Border(BoxBorder.Rounded));
                AnsiConsole.MarkupLine("\n[cyan]Running Claude Code...[/]\n");

                var ok = await RunClaudeWithVerificationAsync(task, basePrompt, ct);
                if (!ok)
                {
                    _logger.TaskEnd(taskId, "failed");
                    return 1;
                }
                AnsiConsole.MarkupLine("\n[green]Claude Code execution completed[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]No prompt defined for this task. Skipping Claude Code execution.[/]");
            _logger.Info($"No prompt for task {taskId}");
        }

        if (task.Subtasks is { Count: > 0 })
        {
            foreach (var sub in task.Subtasks.Where(s => !_tm.IsSubtaskDone(taskId, s.Id)))
            {
                AnsiConsole.MarkupLine($"  [yellow]Subtask:[/] {Markup.Escape(sub.Title)}");
                await _tm.MarkSubtaskDoneAsync(taskId, sub.Id, ct);
                AnsiConsole.MarkupLine($"  [green]Subtask completed[/]");
            }
        }

        await _tm.MarkTaskDoneAsync(taskId, ct);

        if (!dryRun)
        {
            AnsiConsole.MarkupLine($"[green]Task completed: {Markup.Escape(task.Title)}[/]");
            _logger.TaskEnd(taskId, "completed");

            if (commitOnComplete)
                await _git.CommitChangesAsync(taskId, task.Title, _tm.CommitTemplate, _logger, ct: ct);
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[cyan][[DRY-RUN]] Would mark task as done: {Markup.Escape(task.Title)}[/]");
            _logger.TaskEnd(taskId, "dry-run");
        }

        return 0;
    }

    /// <summary>Auto loop — pending이 없을 때까지 RunTaskAsync 반복. budget gate 적용.</summary>
    public async Task<int> RunAutoLoopAsync(
        bool dryRun, bool commitOnComplete, double? budgetUsd, CostTracker cost,
        CancellationToken ct)
    {
        DisplayHelpers.ShowProgress(_tm, _logger);

        var budgetGate = new BudgetGate(budgetUsd, cost, _logger);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // F5: budget 게이트 — 차단 시 종료 코드 2.
            if (!await budgetGate.CheckAsync(ct)) return 2;

            var nextId = _tm.GetNextReadyTask();
            if (nextId == null)
            {
                var remaining = _tm.GetNextTask();
                if (remaining != null)
                {
                    AnsiConsole.MarkupLine(
                        "\n[red]All remaining tasks are blocked by unmet dependencies:[/]");
                    foreach (var t in _tm.GetPendingTasks())
                    {
                        var deps = t.DependsOn is { Count: > 0 }
                            ? string.Join(", ", t.DependsOn)
                            : "none";
                        AnsiConsole.MarkupLine(
                            $"  {Markup.Escape(t.Id)}: depends on {Markup.Escape(deps)}");
                    }
                    _logger.Warn("Execution stopped: remaining tasks blocked by dependencies");
                }
                else
                {
                    AnsiConsole.MarkupLine("\n[green]All tasks completed![/]");
                    _logger.Info("All tasks completed");
                }
                break;
            }

            var exitCode = await RunTaskAsync(nextId, dryRun, commitOnComplete, ct);

            if (exitCode == 2) continue; // blocked, try next
            if (exitCode != 0)
            {
                AnsiConsole.MarkupLine("[red]Task failed. Stopping auto execution.[/]");
                _logger.Error("Auto execution stopped due to task failure");
                break;
            }
        }

        return 0;
    }

    /// <summary>Interactive loop — 각 태스크마다 사용자 확인 후 실행.</summary>
    public async Task<int> RunInteractiveLoopAsync(CancellationToken ct)
    {
        DisplayHelpers.ShowProgress(_tm, _logger);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var nextId = _tm.GetNextReadyTask();
            if (nextId == null)
            {
                var remaining = _tm.GetNextTask();
                if (remaining != null)
                {
                    AnsiConsole.MarkupLine(
                        "\n[red]All remaining tasks are blocked by unmet dependencies:[/]");
                    foreach (var t in _tm.GetPendingTasks())
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

            var task = _tm.GetTask(nextId)!;

            if (!_tm.CheckDependencies(nextId, out var blocked))
            {
                foreach (var dep in blocked)
                    AnsiConsole.MarkupLine(
                        $"[red]Blocked:[/] Task '{Markup.Escape(nextId)}' depends on '{Markup.Escape(dep)}'");
                continue;
            }

            DisplayHelpers.DisplayTask(_tm, nextId);

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
                        _logger.TaskStart(nextId, task.Title);
                        AnsiConsole.MarkupLine($"[blue]Executing task: {Markup.Escape(task.Title)}[/]\n");

                        if (!string.IsNullOrEmpty(task.Prompt))
                        {
                            var basePrompt = PromptBuilder.Build(task, _tm, _tasksFile, siblings: null);

                            AnsiConsole.MarkupLine("[cyan]Running Claude Code...[/]\n");
                            var ok = await RunClaudeWithVerificationAsync(task, basePrompt, ct);
                            if (!ok)
                            {
                                AnsiConsole.MarkupLine("\n[red]Claude Code 실행 또는 verification 실패[/]");
                                if (!AnsiConsole.Confirm("Continue anyway?", defaultValue: false))
                                {
                                    _logger.TaskEnd(nextId, "failed");
                                    return 1;
                                }
                            }
                            else
                            {
                                AnsiConsole.MarkupLine("\n[green]Claude Code execution completed[/]");
                            }
                        }

                        if (task.Subtasks is { Count: > 0 })
                        {
                            foreach (var sub in task.Subtasks.Where(s => !_tm.IsSubtaskDone(nextId, s.Id)))
                            {
                                AnsiConsole.MarkupLine(
                                    $"  [yellow]Subtask:[/] {Markup.Escape(sub.Title)}");
                                await _tm.MarkSubtaskDoneAsync(nextId, sub.Id, ct);
                                AnsiConsole.MarkupLine("  [green]Subtask completed[/]");
                            }
                        }

                        await _tm.MarkTaskDoneAsync(nextId, ct);
                        AnsiConsole.MarkupLine(
                            $"[green]Task completed: {Markup.Escape(task.Title)}[/]");
                        _logger.TaskEnd(nextId, "completed");

                        if (_tm.CommitOnComplete)
                            await _git.CommitChangesAsync(nextId, task.Title, _tm.CommitTemplate, _logger, ct: ct);

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
                        break;

                    case "Skip":
                        AnsiConsole.MarkupLine("[yellow]Skipping task...[/]");
                        _logger.Info($"Task {nextId} skipped by user");
                        done = true;
                        break;

                    case "Quit":
                        AnsiConsole.MarkupLine("[red]Quitting...[/]");
                        _logger.Info("User quit");
                        return 0;
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// Sequential/Interactive 모드의 verification-aware 실행.
    /// VerificationLoop을 단일 task path 톤(풀스타일 마크업)으로 호출.
    /// </summary>
    private Task<bool> RunClaudeWithVerificationAsync(
        TaskItem task, string basePrompt, CancellationToken ct)
    {
        var maxVerifyRetries = Math.Max(0, _tm.Data.Workflow?.VerifyRetries ?? 1);

        var callbacks = new VerificationCallbacks
        {
            OnClaudeFailure = _ => AnsiConsole.MarkupLine("\n[red]Claude Code execution failed[/]"),
            OnVerificationStart = spec =>
                AnsiConsole.MarkupLine($"\n[cyan]검증 명령 실행:[/] [dim]{Markup.Escape(spec.Command)}[/]"),
            OnVerificationPass = verify =>
                AnsiConsole.MarkupLine($"[green]✓ 검증 통과[/] ({verify.Duration.TotalSeconds:F1}s)"),
            OnVerificationFailFinal = (verify, attemptCount) =>
            {
                AnsiConsole.MarkupLine(
                    $"[red]✗ 검증 실패[/] (exit={verify.ExitCode}{(verify.TimedOut ? ", TIMEOUT" : "")}, {attemptCount}회 시도)");
                if (!string.IsNullOrWhiteSpace(verify.Stderr))
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape(verify.Stderr.Trim())}[/]");
                _logger.Error(
                    $"[verification] {task.Id} failed exit={verify.ExitCode} timedOut={verify.TimedOut}");
            },
            OnVerificationRetry = (_, attemptIndex, maxRetries) =>
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]⚠ 검증 실패, Claude에게 수정 요청 ({attemptIndex}/{maxRetries} retry)[/]");
                _logger.Warn($"[verification] {task.Id} failed (attempt {attemptIndex}); retrying with failure context");
            },
        };

        var (resolvedModel, modelSource) = ModelResolver.Resolve(_modelOverride, task);
        AnsiConsole.MarkupLine($"[cyan]Model:[/] {DisplayHelpers.FormatModel(resolvedModel)} [dim]({modelSource})[/]");
        _logger.Info($"[{task.Id}] Model: {resolvedModel} ({modelSource})");

        return _verificationLoop.ExecuteAsync(
            task, basePrompt, maxVerifyRetries,
            claudeWorkingDirectory: null,
            verifierWorkingDirectory: Directory.GetCurrentDirectory(),
            output: null, model: resolvedModel, callbacks: callbacks, ct: ct);
    }
}
