using Ralph.Models;
using Spectre.Console;

namespace Ralph.Services;

/// <summary>
/// 워크트리 실행이 끝난 태스크들을 base 브랜치에 머지하는 책임.
/// 단계: 머지 직전 정규화/검증 → rebase advance → merge → 충돌 해결 체인 →
/// done 마킹 → tasks.json commit → post-merge smoke test.
/// </summary>
internal sealed class MergeOrchestrator
{
    private readonly TaskManager _taskManager;
    private readonly IAgentRunner _claude;
    private readonly GitService _git;
    private readonly WorktreeService _worktree;
    private readonly RalphLogger _logger;
    private readonly VerificationRunner _verifier;
    private readonly CostTracker _cost;
    private readonly string _tasksFile;
    private readonly string? _model;
    private readonly bool _strictFiles;
    private readonly bool _noSmokeTest;
    private readonly SemaphoreSlim _taskFileLock = new(1, 1);

    /// <summary>RunSingle path를 머지가 abort 시 fallback으로 호출하기 위한 콜백.</summary>
    public Func<string, CancellationToken, Task<int>>? RerunSequential { get; set; }

    public MergeOrchestrator(
        TaskManager taskManager, IAgentRunner claude, GitService git, WorktreeService worktree,
        RalphLogger logger, VerificationRunner verifier, CostTracker cost,
        string tasksFile, string? model, bool strictFiles, bool noSmokeTest)
    {
        _taskManager = taskManager;
        _claude = claude;
        _git = git;
        _worktree = worktree;
        _logger = logger;
        _verifier = verifier;
        _cost = cost;
        _tasksFile = tasksFile;
        _model = model;
        _strictFiles = strictFiles;
        _noSmokeTest = noSmokeTest;
    }

    /// <summary>
    /// 머지 단계 시작. 0=성공/스킵 가능, 1=실패(호출자가 종료).
    /// 호출자(BatchOrchestrator)는 worktree cleanup을 finally에서 보장.
    /// </summary>
    public async Task<int> MergeAndFinalizeAsync(
        List<string> taskIds, string baseBranch, string primaryStrategy,
        IReadOnlyList<string> strategyChain,
        Action<int /*cleanupFailures*/> reportCleanupFailures,
        CancellationToken ct)
    {
        var cleanupFailures = 0;

        // 순차적으로 메인에 병합. Live scope는 이미 종료되어 있으므로 진행률만 콘솔로 표시.
        AnsiConsole.MarkupLine(
            $"\n[blue]메인 브랜치에 병합 중...[/] [dim]({taskIds.Count}개 태스크)[/]");

        var mergeIdx = 0;
        foreach (var taskId in taskIds)
        {
            mergeIdx++;
            AnsiConsole.MarkupLine(
                $"  [dim][[{mergeIdx}/{taskIds.Count}]][/] {Markup.Escape(taskId)}");

            // F2: 머지 직전 worktree의 tasks.json이 baseBranch와 다르면 강제 정규화.
            await _worktree.NormalizeTasksJsonAsync(
                taskId, baseBranch,
                tasksFileName: Path.GetFileName(_tasksFile),
                logger: _logger, ct: ct);

            // F4: declared(modifiedFiles ∪ outputFiles) vs actual(base..HEAD) 검증.
            var declared = DeclaredFiles.Build(_taskManager.GetTask(taskId)!);
            var validation = await _worktree.ValidateModifiedFilesAsync(
                taskId, baseBranch, declared, _logger, ct: ct);
            ReportValidation(taskId, validation);

            // P0-3: strict 모드에서 diff 자체가 실패하면 머지 차단.
            if (_strictFiles && validation.DiffFailed)
            {
                AnsiConsole.MarkupLine(
                    $"  [red]✗[/] {Markup.Escape(taskId)} diff 실패로 검증 불가. " +
                    $"머지 중단 (strict-files).");
                _logger.Error(
                    $"[validate:files][strict] {taskId} diff failed: {validation.DiffError}");
                return 1;
            }

            if (_strictFiles && validation.HasUndeclared)
            {
                var preview = string.Join(", ", validation.Undeclared.Take(3));
                var more = validation.Undeclared.Count > 3
                    ? $" (외 {validation.Undeclared.Count - 3}건)" : "";
                AnsiConsole.MarkupLine(
                    $"  [red]✗[/] {Markup.Escape(taskId)} undeclared 파일 {validation.Undeclared.Count}건. " +
                    $"머지 중단 (strict-files): {Markup.Escape(preview + more)}");
                _logger.Error(
                    $"[validate:files][strict] {taskId} undeclared: " +
                    string.Join(", ", validation.Undeclared));
                return 1;
            }

            // 같은 batch의 앞선 머지로 baseBranch가 advance된 경우 충돌 감소를 위해 rebase.
            await _worktree.AdvanceWorktreeOntoBaseAsync(taskId, baseBranch, _logger, ct);

            var mergeResult = await _worktree.MergeWorktreeAsync(
                taskId, baseBranch, primaryStrategy, _logger, ct);

            if (mergeResult.Success)
            {
                AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(taskId)} 병합 완료");
            }
            else
            {
                AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(taskId)} 병합 충돌!");

                var resolved = await HandleMergeConflictAsync(
                    taskId, baseBranch, mergeResult, strategyChain, ct);

                if (!resolved)
                {
                    _logger.Error($"Merge conflict unresolved for {taskId}");
                    foreach (var remaining in taskIds)
                    {
                        if (!await _worktree.CleanupWorktreeAsync(remaining, _logger, ct))
                            cleanupFailures++;
                    }
                    reportCleanupFailures(cleanupFailures);
                    return 1;
                }
            }
        }

        // 4. 상태 업데이트 (thread-safe).
        foreach (var taskId in taskIds)
        {
            try
            {
                await MarkTaskDoneThreadSafeAsync(taskId, ct);
                var task = _taskManager.GetTask(taskId)!;
                AnsiConsole.MarkupLine($"[green]태스크 완료: {Markup.Escape(task.Title)}[/]");
                _logger.TaskEnd(taskId, "completed");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(
                    $"[red]✗[/] {Markup.Escape(taskId)} done 마킹 실패: {Markup.Escape(ex.Message)}");
                _logger.Error($"MarkTaskDone failed for {taskId}: {ex.Message}");
            }
        }

        // 5. tasks.json 변경사항 커밋
        await CommitTasksFileAsync(taskIds, ct);

        // 5.5 머지 후 smoke test
        if (await RunPostMergeSmokeTestAsync(ct) is { } smokeFail)
            return smokeFail;

        return 0;
    }

    /// <summary>
    /// Merge 충돌을 strategy chain으로 순차 시도하여 처리한다.
    /// chain[0]은 이미 merge 명령에 -X로 적용되어 시도된 상태이며 충돌이 남았다는 뜻이므로
    /// 첫 항목이 auto-*인 경우는 다음 fallback으로 즉시 진행.
    /// </summary>
    private async Task<bool> HandleMergeConflictAsync(
        string taskId, string baseBranch, MergeResult mergeResult,
        IReadOnlyList<string> chain, CancellationToken ct)
    {
        var currentMergeResult = mergeResult;

        for (var i = 0; i < chain.Count; i++)
        {
            var strategy = chain[i];
            var isFirst = i == 0;

            switch (strategy)
            {
                case "claude":
                    AnsiConsole.MarkupLine(
                        $"  [cyan]충돌 해결 시도: claude (전략 {i + 1}/{chain.Count})[/]");
                    if (await ResolveConflictsWithClaudeAsync(taskId, currentMergeResult, ct))
                        return true;
                    AnsiConsole.MarkupLine("  [yellow]claude 해결 실패 — 다음 전략 시도[/]");
                    _logger.Warn($"[merge:chain] {taskId} claude failed at step {i + 1}/{chain.Count}");
                    break;

                case "abort":
                    await _worktree.AbortMergeAsync(ct);
                    AnsiConsole.MarkupLine(
                        $"[yellow]전략 abort (전략 {i + 1}/{chain.Count}): " +
                        $"{Markup.Escape(taskId)}를 순차 모드로 재실행합니다...[/]");
                    _logger.Warn($"[merge:chain] {taskId} abort -> sequential rerun at step {i + 1}");
                    if (RerunSequential is null)
                    {
                        _logger.Error($"[merge:chain] {taskId} abort but no RerunSequential callback registered");
                        return false;
                    }
                    return await RerunSequential(taskId, ct) == 0;

                case "auto-theirs":
                case "auto-ours":
                    if (isFirst)
                    {
                        AnsiConsole.MarkupLine(
                            $"  [yellow]{strategy}로 풀 수 없는 충돌 (add/add, rename/delete 등). 다음 전략 시도[/]");
                        _logger.Warn($"[merge:chain] {taskId} {strategy} (-X) 첫 시도에서 미해결 충돌");
                    }
                    else
                    {
                        await _worktree.AbortMergeAsync(ct);
                        AnsiConsole.MarkupLine(
                            $"  [cyan]전략 {strategy}로 재머지 시도 (전략 {i + 1}/{chain.Count})[/]");
                        var retry = await _worktree.MergeWorktreeAsync(
                            taskId, baseBranch, strategy, _logger, ct);
                        if (retry.Success)
                        {
                            AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(taskId)} {strategy} 재머지 성공");
                            return true;
                        }
                        currentMergeResult = retry;
                        AnsiConsole.MarkupLine($"  [yellow]{strategy} 재머지 실패 — 다음 전략 시도[/]");
                        _logger.Warn($"[merge:chain] {taskId} {strategy} retry failed at step {i + 1}");
                    }
                    break;

                default:
                    AnsiConsole.MarkupLine(
                        $"  [yellow]알 수 없는 전략 무시: {Markup.Escape(strategy)}[/]");
                    _logger.Warn($"[merge:chain] {taskId} unknown strategy: {strategy}");
                    break;
            }
        }

        AnsiConsole.MarkupLine(
            $"  [red]✗[/] {Markup.Escape(taskId)} 모든 conflict 전략 실패 ({chain.Count}개 시도)");
        _logger.Error($"[merge:chain] {taskId} all {chain.Count} strategies exhausted");
        await _worktree.AbortMergeAsync(ct);
        return false;
    }

    /// <summary>
    /// Claude를 사용하여 merge 충돌을 해결한다.
    /// </summary>
    private async Task<bool> ResolveConflictsWithClaudeAsync(
        string taskId, MergeResult mergeResult, CancellationToken ct)
    {
        if (mergeResult.ConflictFiles is not { Count: > 0 })
        {
            await _worktree.AbortMergeAsync(ct);
            return false;
        }

        var repoRoot = await _git.GetRepoRootAsync(ct: ct);

        var conflictList = string.Join("\n", mergeResult.ConflictFiles.Select(f => $"  - {f}"));
        var prompt = $"""
            다음 git merge 충돌을 해결해주세요.

            작업 디렉토리: {repoRoot}
            태스크: {taskId}
            충돌 파일 (repo 루트 기준 상대 경로):
            {conflictList}

            지시:
            1. `git status`로 현재 충돌 상태를 확인하세요.
            2. 위 각 파일을 열어 충돌 마커(<<<<<<< HEAD, =======, >>>>>>> branch)를 모두 제거하세요.
            3. 양쪽 변경사항을 모두 살리는 방향으로 통합하세요.
            4. 마커가 남아있는지 검증한 뒤 파일을 저장하세요. (마커가 남아있으면 빌드/실행이 깨집니다)

            staging과 commit은 ralph가 처리하므로 git add/commit은 실행하지 마세요.
            """;

        AnsiConsole.MarkupLine($"[cyan]Claude Code로 충돌 해결 중 ({mergeResult.ConflictFiles.Count}개 파일, repo: {Markup.Escape(repoRoot)})...[/]");

        ClaudeResult? result = null;
        try
        {
            result = await _claude.RunWithRetryAsync(
                prompt, model: _model, workingDirectory: repoRoot, logger: _logger, ct: ct);
        }
        finally
        {
            await _cost.RecordAsync($"conflict:{taskId}", _model ?? "opus", result, CancellationToken.None);
        }
        if (result == null || !result.Success)
        {
            await _worktree.AbortMergeAsync(ct);
            return false;
        }

        // 해결된 파일에 충돌 마커가 남아있는지 1차 검증.
        foreach (var file in mergeResult.ConflictFiles)
        {
            var fullPath = Path.Combine(repoRoot, file);
            if (File.Exists(fullPath))
            {
                var content = await File.ReadAllTextAsync(fullPath, ct);
                if (content.Contains("<<<<<<<") || content.Contains(">>>>>>>"))
                {
                    AnsiConsole.MarkupLine($"[red]충돌 마커가 여전히 남아있음: {Markup.Escape(file)}[/]");
                    _logger.Error($"Conflict markers remain in {file} after Claude resolution");
                    await _worktree.AbortMergeAsync(ct);
                    return false;
                }
            }
        }

        // 해결된 파일 staging
        foreach (var file in mergeResult.ConflictFiles)
        {
            await _git.RunAsync(["add", "--", file], workingDirectory: repoRoot, ct: ct);
        }

        // P1-2: staged 영역 전체를 git diff --check --cached로 한 번 더 검증.
        var (checkExit, checkOut) = await _git.RunAsync(
            ["diff", "--check", "--cached"], workingDirectory: repoRoot, ct: ct);
        if (checkExit != 0)
        {
            AnsiConsole.MarkupLine($"[red]staged 영역에 충돌 마커/문제 감지:[/]");
            if (!string.IsNullOrWhiteSpace(checkOut))
                AnsiConsole.WriteLine(checkOut.Trim());
            _logger.Error($"git diff --check --cached failed for {taskId}: {checkOut.Trim()}");
            await _worktree.AbortMergeAsync(ct);
            return false;
        }

        var (exitCode, _) = await _git.RunAsync(
            ["commit", "--no-edit"], workingDirectory: repoRoot, ct: ct);

        if (exitCode == 0)
        {
            AnsiConsole.MarkupLine($"[green]충돌 해결 완료: {Markup.Escape(taskId)}[/]");
            _logger.Info($"Conflict resolved via Claude for {taskId}");
            return true;
        }

        await _worktree.AbortMergeAsync(ct);
        return false;
    }

    /// <summary>
    /// 머지 후 base 브랜치에서 smoke test를 실행해 머지 결과의 semantic 정합성을 검증.
    /// 우선순위: --no-smoke-test → workflow.smokeTest → repo root marker로 자동 추론.
    /// </summary>
    private async Task<int?> RunPostMergeSmokeTestAsync(CancellationToken ct)
    {
        if (_noSmokeTest)
        {
            _logger.Info("[smoke-test] skipped (--no-smoke-test)");
            return null;
        }

        var repoRoot = await _git.GetRepoRootAsync(ct: ct);
        var configured = _taskManager.Data.Workflow?.SmokeTest;
        VerificationSpec? spec;
        bool inferred = false;

        if (configured is not null && !string.IsNullOrWhiteSpace(configured.Command))
        {
            spec = configured;
        }
        else
        {
            spec = ParallelExecutor.InferSmokeTestCommand(repoRoot);
            inferred = spec is not null;
            if (spec is null)
            {
                _logger.Info("[smoke-test] skipped (no workflow.smokeTest, inference matched no marker)");
                return null;
            }
        }

        var label = inferred ? "Smoke test 실행 (자동 추론)" : "Smoke test 실행";
        AnsiConsole.MarkupLine(
            $"\n[cyan]{label}:[/] [dim]{Markup.Escape(spec!.Command)}[/] [dim](cwd: {Markup.Escape(repoRoot)})[/]");
        _logger.Info($"[smoke-test] running: {spec.Command} (cwd: {repoRoot}, inferred: {inferred})");

        var result = await _verifier.RunAsync(spec, repoRoot, _logger, output: null, ct);
        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]✓ Smoke test 통과[/] ({result.Duration.TotalSeconds:F1}s)");
            return null;
        }

        AnsiConsole.MarkupLine(
            $"[red]✗ Smoke test 실패[/] (exit={result.ExitCode}{(result.TimedOut ? ", TIMEOUT" : "")}, {result.Duration.TotalSeconds:F1}s)");
        if (!string.IsNullOrWhiteSpace(result.Stderr))
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(result.Stderr.Trim())}[/]");
        _logger.Error(
            $"[smoke-test] failed exit={result.ExitCode} timedOut={result.TimedOut}");
        return 1;
    }

    /// <summary>
    /// tasks.json 변경사항(done 상태 업데이트)을 커밋한다.
    /// 다음 배치의 worktree 병합 시 충돌을 방지.
    /// </summary>
    private async Task CommitTasksFileAsync(List<string> completedTaskIds, CancellationToken ct)
    {
        var (exitCode, _) = await _git.RunAsync(["add", _tasksFile], ct: ct);
        if (exitCode != 0) return;

        var taskList = string.Join(", ", completedTaskIds);
        var commitMsg = $"chore: 태스크 상태 업데이트 ({taskList})";

        (exitCode, _) = await _git.RunAsync(
            ["commit", "-m", commitMsg], ct: ct);

        if (exitCode == 0)
            _logger.Info($"Tasks file committed: {taskList}");
        else
            _logger.Warn("No tasks file changes to commit");
    }

    /// <summary>thread-safe하게 태스크를 완료 상태로 변경한다.</summary>
    private async Task MarkTaskDoneThreadSafeAsync(string taskId, CancellationToken ct)
    {
        await _taskFileLock.WaitAsync(ct);
        try
        {
            await _taskManager.ReloadAsync();
            var task = _taskManager.GetTask(taskId)!;

            if (task.Subtasks is { Count: > 0 })
            {
                foreach (var sub in task.Subtasks.Where(s => !s.Done))
                    _taskManager.MarkSubtaskDone(taskId, sub.Id);
            }

            _taskManager.MarkTaskDone(taskId);
            await _taskManager.SaveAsync();
        }
        finally
        {
            _taskFileLock.Release();
        }
    }

    /// <summary>
    /// F4 검증 결과를 콘솔에 표시한다. strict 차단 메시지는 별도 분기에서 출력하므로
    /// 여기서는 warn-only/info 메시지만 다룬다.
    /// </summary>
    private void ReportValidation(string taskId, FileValidationResult validation)
    {
        if (validation.DiffFailed)
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]⚠[/] {Markup.Escape(taskId)} diff 실패 — 검증 스킵");
            return;
        }

        if (validation.HasUndeclared && !_strictFiles)
        {
            var preview = string.Join(", ", validation.Undeclared.Take(3));
            var more = validation.Undeclared.Count > 3
                ? $" (외 {validation.Undeclared.Count - 3}건)" : "";
            AnsiConsole.MarkupLine(
                $"  [yellow]⚠[/] {Markup.Escape(taskId)} undeclared {validation.Undeclared.Count}건 (warn-only): " +
                $"{Markup.Escape(preview + more)}");
        }

        if (validation.HasNotChanged)
        {
            var preview = string.Join(", ", validation.NotChanged.Take(3));
            var more = validation.NotChanged.Count > 3
                ? $" (외 {validation.NotChanged.Count - 3}건)" : "";
            AnsiConsole.MarkupLine(
                $"  [dim]ℹ {Markup.Escape(taskId)} notChanged {validation.NotChanged.Count}건: " +
                $"{Markup.Escape(preview + more)}[/]");
        }
    }
}
