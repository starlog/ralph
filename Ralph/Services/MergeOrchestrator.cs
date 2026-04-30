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
    private readonly string? _smokeTestCommandOverride;
    private readonly bool _autoRollbackOnSmokeFail;

    // fix2 #8: 머지 트랜잭션 로그. MergeAndFinalizeAsync 첫 호출 시 lazy-init.
    private MergeLogService? _mergeLog;
    private int _batchCounter;

    /// <summary>RunSingle path를 머지가 abort 시 fallback으로 호출하기 위한 콜백.</summary>
    public Func<string, CancellationToken, Task<int>>? RerunSequential { get; set; }

    public MergeOrchestrator(
        TaskManager taskManager, IAgentRunner claude, GitService git, WorktreeService worktree,
        RalphLogger logger, VerificationRunner verifier, CostTracker cost,
        string tasksFile, string? model, bool strictFiles, bool noSmokeTest,
        string? smokeTestCommandOverride = null, bool autoRollbackOnSmokeFail = false)
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
        _smokeTestCommandOverride = smokeTestCommandOverride;
        _autoRollbackOnSmokeFail = autoRollbackOnSmokeFail;
    }

    /// <summary>
    /// fix2 #7: smoke 실행 결과를 호출자(자동 롤백 핸들러)가 사용할 수 있도록 풍부화한 구조체.
    /// Skipped=true면 명령이 아예 실행되지 않은 경우 (--no-smoke-test, docs-only 추론 스킵 등).
    /// </summary>
    private sealed record SmokePhaseResult(
        bool Skipped, bool Passed, string? Command, VerificationResult? Detail);

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

        // smoke test의 docs-only 스킵 판단을 위해 머지 시작 직전 baseBranch HEAD SHA를 기록.
        // 실패해도(unborn branch 등) 그냥 null로 두고 fall-through.
        var preMergeSha = await CaptureBaseShaAsync(baseBranch, ct);

        // fix2 #7: 자동 롤백을 위한 in-memory batch 스냅샷. preMergeSha가 잡혔을 때만 의미 있다.
        var batchSnapshot = !string.IsNullOrEmpty(preMergeSha)
            ? RollbackService.CaptureBatchSnapshot(baseBranch, preMergeSha!, taskIds.ToList())
            : null;

        // fix2 #8: 머지 트랜잭션 로그. lazy-init + batch 인덱스 할당.
        var repoRoot = await _git.GetRepoRootAsync(ct: ct);
        _mergeLog ??= new MergeLogService(repoRoot, _logger);
        var batchIndex = Interlocked.Increment(ref _batchCounter);
        var mergeShaBytaskId = new Dictionary<string, string>();

        // 순차적으로 메인에 병합. Live scope는 이미 종료되어 있으므로 진행률만 콘솔로 표시.
        AnsiConsole.MarkupLine(
            $"\n[blue]메인 브랜치에 병합 중...[/] [dim]({taskIds.Count}개 태스크)[/]");

        // fix2 #5: rebase 충돌로 실패한 task는 done 마킹에서 제외하고 batch는 계속.
        var rebaseFailedTasks = new HashSet<string>();
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

            // F4: declared(modifiedFiles ∪ outputFiles) vs actual(base...HEAD) 검증.
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
            // fix2 #5: rebase 충돌은 silent fallback 없이 RebaseConflict로 분류해 task만 실패.
            var advance = await _worktree.AdvanceWorktreeOntoBaseAsync(
                taskId, baseBranch, _logger, ct);

            if (!advance.Success && advance.FailureKind == MergeFailureKind.RebaseConflict)
            {
                PrintRebaseConflict(taskId, baseBranch, advance);
                _logger.Error(
                    $"[merge:advance] {taskId} RebaseConflict — task 실패 마킹, batch 진행 계속. " +
                    $"files=[{string.Join(",", advance.ConflictFiles ?? new())}]");

                AnsiConsole.MarkupLine(
                    $"  [red]✗[/] {Markup.Escape(taskId)} rebase 충돌 (자세한 내용은 stderr 확인)");

                if (!await _worktree.CleanupWorktreeAsync(taskId, _logger, ct))
                    cleanupFailures++;
                rebaseFailedTasks.Add(taskId);
                _logger.TaskEnd(taskId, "rebase-conflict");
                continue;
            }

            if (!advance.Success && advance.FailureKind == MergeFailureKind.Other)
            {
                AnsiConsole.MarkupLine(
                    $"  [red]✗[/] {Markup.Escape(taskId)} rebase abort 실패 — batch 중단 " +
                    $"(worktree가 더러운 상태일 수 있음)");
                _logger.Error(
                    $"[merge:advance] {taskId} abort 실패 — batch 중단. " +
                    $"detail: {advance.ErrorMessage}");
                return 1;
            }

            var mergeResult = await _worktree.MergeWorktreeAsync(
                taskId, baseBranch, primaryStrategy, _logger, ct);

            if (mergeResult.Success)
            {
                AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(taskId)} 병합 완료");
                // fix2 #8: 머지 커밋 SHA 기록
                mergeShaBytaskId[taskId] = await CaptureCurrentShaAsync(ct) ?? "";
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
                // fix2 #8: 충돌 해결 후 커밋 SHA 기록
                mergeShaBytaskId[taskId] = await CaptureCurrentShaAsync(ct) ?? "";
            }
        }

        // 4. 상태 업데이트 (thread-safe). rebase 충돌 task는 머지 안 됐으므로 done 마킹 제외.
        // state.json 쓰기 실패 시 silent 진행하지 않고 즉시 batch를 중단한다.
        // 머지는 이미 base에 반영된 상태이므로, done 마킹이 누락된 채 다음 batch로 넘어가면
        // 다음 --run에서 동일 task가 재dispatch되어 worktree 충돌이 발생한다 (fix1.md 1번).
        var mergedTasks = taskIds.Where(id => !rebaseFailedTasks.Contains(id)).ToList();
        var stateMarkResults = new Dictionary<string, bool>(); // fix2 #8: per-task done-mark 결과
        var marked = new List<string>();
        var pending = new List<string>(mergedTasks);
        foreach (var taskId in mergedTasks)
        {
            try
            {
                await MarkTaskDoneThreadSafeAsync(taskId, ct);
                stateMarkResults[taskId] = true;
                marked.Add(taskId);
                pending.Remove(taskId);
                var task = _taskManager.GetTask(taskId)!;
                AnsiConsole.MarkupLine($"[green]태스크 완료: {Markup.Escape(task.Title)}[/]");
                _logger.TaskEnd(taskId, "completed");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                stateMarkResults[taskId] = false;
                ReportStateWriteFailure(taskId, ex, marked, pending);
                // smoke test는 실행하지 않는다 — state가 깨진 상태에서 추가 신호를 섞지 않는다.
                // fix2 #8: batch abort 전 머지된 task들을 smokeTest="skipped"으로 기록
                await AppendMergeLogEntriesAsync(
                    mergedTasks, mergeShaBytaskId, stateMarkResults, "skipped",
                    preMergeSha ?? "", batchIndex, ct);
                return 1;
            }
        }

        // 5. (이전: tasks.json done 커밋) — done 상태는 .ralph-logs/state.json으로 분리되어
        // 더 이상 git에 트래킹되지 않으므로 커밋이 불필요.

        // 5.5 머지 후 smoke test
        var smoke = await RunPostMergeSmokeTestAsync(preMergeSha, ct);
        var smokeStr = smoke.Skipped ? "skipped" : smoke.Passed ? "passed" : "failed";

        // fix2 #8: 모든 머지된 task에 대해 merge-log entry append (batch 단위 일괄)
        await AppendMergeLogEntriesAsync(
            mergedTasks, mergeShaBytaskId, stateMarkResults, smokeStr,
            preMergeSha ?? "", batchIndex, ct);

        if (!smoke.Skipped && !smoke.Passed)
        {
            // fix2 #7: opt-in 자동 롤백. 실행 여부와 무관하게 batch는 실패(1)로 종료한다 —
            // 자동 롤백 성공도 base를 batch 이전 상태로 되돌렸을 뿐 "smoke가 통과한" 것은 아님.
            if (_autoRollbackOnSmokeFail && batchSnapshot is not null && mergedTasks.Count > 0)
            {
                // fix2 #8: TryAutoRollbackAsync는 revert SHA(성공) 또는 null(실패) 반환
                var revertSha = await TryAutoRollbackAsync(batchSnapshot, mergedTasks, smoke, ct);
                if (revertSha is not null)
                {
                    await AppendRollbackLogEntriesAsync(
                        mergedTasks, mergeShaBytaskId, revertSha,
                        preMergeSha ?? "", batchIndex, ct);
                }
            }
            return 1;
        }

        // rebase 충돌이 한 건이라도 있었으면 batch 부분 성공 — exit 1로 호출자에 알림.
        return rebaseFailedTasks.Count > 0 ? 1 : 0;
    }

    /// <summary>
    /// fix2 #5: rebase 충돌을 stderr로 사람이 읽기 좋게 출력 (한국어, locale-safe).
    /// stdout(AnsiConsole)에는 한 줄 요약만 남기고 자세한 내용은 stderr로 분리.
    /// </summary>
    private static void PrintRebaseConflict(
        string taskId, string baseBranch, MergeResult advance)
    {
        var branch = $"ralph/{taskId}";
        var files = advance.ConflictFiles ?? new List<string>();

        var err = Console.Error;
        err.WriteLine();
        err.WriteLine($"[merge:advance] {taskId}: rebase 단계 충돌 (RebaseConflict)");
        err.WriteLine($"  base: {baseBranch} → {branch}");
        if (files.Count > 0)
        {
            err.WriteLine($"  충돌 파일 ({files.Count}건):");
            foreach (var f in files)
                err.WriteLine($"    - {f}");
        }
        else
        {
            err.WriteLine("  충돌 파일: (목록 캡처 실패)");
        }
        err.WriteLine("  조치: 이 task만 실패 처리하고 batch의 다른 독립 task는 계속 진행합니다.");
        err.WriteLine($"  재실행: ralph --task {taskId} --force");
        err.WriteLine($"  수동 머지: git checkout {branch} && git rebase {baseBranch}");
        if (!string.IsNullOrEmpty(advance.ErrorMessage))
            err.WriteLine($"  detail: {advance.ErrorMessage}");
    }

    /// <summary>
    /// done 마킹 실패 시 사용자에게 batch 중단 사유와 복구 안내를 표시하고 logger에 기록한다.
    /// 이미 머지된 변경분은 base 브랜치에 남아있으며 자동 롤백하지 않는다 (정책).
    /// </summary>
    private void ReportStateWriteFailure(
        string failedTaskId, Exception ex, List<string> marked, List<string> pending)
    {
        var stateFilePath = _taskManager.State.FilePath;
        var exceptionType = ex.GetType().FullName ?? ex.GetType().Name;

        AnsiConsole.MarkupLine(
            $"\n[red]✗[/] {Markup.Escape(failedTaskId)} done 마킹 실패: {Markup.Escape(ex.Message)}");
        AnsiConsole.MarkupLine($"  [dim]원인: {Markup.Escape(exceptionType)}[/]");
        AnsiConsole.MarkupLine(
            "  [red]state.json 쓰기 실패로 batch 중단; 수동 복구 필요.[/]");
        AnsiConsole.MarkupLine(
            "  [yellow]이미 머지된 변경분은 base 브랜치에 남아 있습니다 (자동 롤백하지 않음).[/]");

        AnsiConsole.MarkupLine("\n  [green]완료 처리된 task (state.json 반영 완료):[/]");
        if (marked.Count == 0)
        {
            AnsiConsole.MarkupLine("    [dim](없음)[/]");
        }
        else
        {
            foreach (var id in marked)
                AnsiConsole.MarkupLine($"    - {Markup.Escape(id)}");
        }

        AnsiConsole.MarkupLine("  [yellow]미처리 task (머지는 됐으나 done 마킹 안 됨):[/]");
        if (pending.Count == 0)
        {
            AnsiConsole.MarkupLine("    [dim](없음)[/]");
        }
        else
        {
            foreach (var id in pending)
            {
                var marker = id == failedTaskId ? "  [red]← 실패 지점[/]" : "";
                AnsiConsole.MarkupLine($"    - {Markup.Escape(id)}{marker}");
            }
        }

        AnsiConsole.MarkupLine("\n  [cyan]복구 안내:[/]");
        AnsiConsole.MarkupLine(
            $"    1) [dim]{Markup.Escape(stateFilePath)}[/] 의 디스크 / 권한 / 잠금을 확인하세요.");
        AnsiConsole.MarkupLine(
            "    2) 미처리 task의 변경이 base 브랜치에 적용되었는지 직접 확인 후, " +
            "필요 시 state.json의 tasks[[\"<id>\"]].done = true 로 수동 편집하세요.");
        AnsiConsole.MarkupLine(
            "    3) 수동 정리 후 [cyan]ralph --run[/] 으로 재개하세요.");

        _logger.Error(
            $"[merge:done-mark] {failedTaskId} state save failed after retries: " +
            $"{exceptionType}: {ex.Message}");
        _logger.Error(
            $"[merge:done-mark] marked=[{string.Join(",", marked)}] " +
            $"pending=[{string.Join(",", pending)}]");
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

        // 머지 충돌 해결은 특정 task가 아니라 batch 결과에 대한 작업이므로 task.model을 쓰지 않고
        // 명시적 override 또는 기본값(sonnet)을 사용한다.
        var conflictModel = ModelResolver.ResolveForNonTask(_model);
        ClaudeResult? result = null;
        try
        {
            result = await _claude.RunWithRetryAsync(
                prompt, model: conflictModel, workingDirectory: repoRoot, logger: _logger, ct: ct);
        }
        finally
        {
            await _cost.RecordAsync($"conflict:{taskId}", conflictModel, result, CancellationToken.None);
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
    /// 우선순위 결정은 <see cref="SmokeTestPlanner.Plan"/>에 위임. preMergeSha가 있으면
    /// 그 시점부터 HEAD까지의 변경 파일을 인자로 넘겨 docs-only 변경일 때 추론을 스킵한다.
    /// </summary>
    private async Task<SmokePhaseResult> RunPostMergeSmokeTestAsync(string? preMergeSha, CancellationToken ct)
    {
        var repoRoot = await _git.GetRepoRootAsync(ct: ct);
        var configured = _taskManager.Data.Workflow?.SmokeTest;
        var changedFiles = await GetChangedFilesAsync(preMergeSha, repoRoot, ct);

        var spec = SmokeTestPlanner.Plan(
            repoRoot: repoRoot,
            configured: configured,
            cliCommand: _smokeTestCommandOverride,
            envCommand: null, // ArgParser가 CLI/env merge하여 _smokeTestCommandOverride 한 곳으로 전달.
            noSmokeTest: _noSmokeTest,
            changedFiles: changedFiles);

        if (spec is null)
        {
            var reason = DetermineSkipReason(configured, changedFiles);
            _logger.Info($"[smoke-test] skipped ({reason})");
            return new SmokePhaseResult(Skipped: true, Passed: false, Command: null, Detail: null);
        }

        // 라벨링: 어디서 왔는지 사용자에게 보여주는 게 진단에 도움이 됨.
        var origin = ResolveSpecOrigin(spec, configured);
        var label = origin switch
        {
            "cli/env"  => "Smoke test 실행 (CLI/env override)",
            "workflow" => "Smoke test 실행 (workflow.smokeTest)",
            _          => "Smoke test 실행 (자동 추론)",
        };

        AnsiConsole.MarkupLine(
            $"\n[cyan]{label}:[/] [dim]{Markup.Escape(spec.Command)}[/] [dim](cwd: {Markup.Escape(repoRoot)})[/]");
        _logger.Info($"[smoke-test] running: {spec.Command} (cwd: {repoRoot}, origin: {origin})");

        var result = await _verifier.RunAsync(spec, repoRoot, _logger, output: null, ct);
        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]✓ Smoke test 통과[/] ({result.Duration.TotalSeconds:F1}s)");
            return new SmokePhaseResult(Skipped: false, Passed: true, Command: spec.Command, Detail: result);
        }

        AnsiConsole.MarkupLine(
            $"[red]✗ Smoke test 실패[/] (exit={result.ExitCode}{(result.TimedOut ? ", TIMEOUT" : "")}, {result.Duration.TotalSeconds:F1}s)");
        if (!string.IsNullOrWhiteSpace(result.Stderr))
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(result.Stderr.Trim())}[/]");
        _logger.Error(
            $"[smoke-test] failed exit={result.ExitCode} timedOut={result.TimedOut}");
        return new SmokePhaseResult(Skipped: false, Passed: false, Command: spec.Command, Detail: result);
    }

    private string DetermineSkipReason(
        VerificationSpec? configured, IReadOnlyList<string>? changedFiles)
    {
        if (_noSmokeTest) return "--no-smoke-test";
        // configured가 있으면 Plan이 무조건 그것을 반환했어야 하므로 여기에 도달하면 추론 단계에서 null.
        if (configured is null && changedFiles is { Count: > 0 })
        {
            // changedFiles가 모두 docs면 docs-only로 스킵된 것
            // (recompute하지 않고 changed list 기반으로 추정 — 표시용).
            return "no workflow.smokeTest, all changes are docs-only";
        }
        return "no workflow.smokeTest, inference matched no marker";
    }

    private string ResolveSpecOrigin(VerificationSpec spec, VerificationSpec? configured)
    {
        if (!string.IsNullOrWhiteSpace(_smokeTestCommandOverride)
            && spec.Command == _smokeTestCommandOverride!.Trim())
            return "cli/env";
        if (configured is not null && ReferenceEquals(spec, configured))
            return "workflow";
        return "inferred";
    }

    private async Task<string?> CaptureBaseShaAsync(string baseBranch, CancellationToken ct)
    {
        try
        {
            var (exit, output) = await _git.RunAsync(
                new[] { "rev-parse", "--verify", baseBranch + "^{commit}" }, ct: ct);
            if (exit != 0) return null;
            var sha = output.Trim();
            return string.IsNullOrEmpty(sha) ? null : sha;
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<string>?> GetChangedFilesAsync(
        string? preMergeSha, string repoRoot, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(preMergeSha)) return null;
        try
        {
            var (exit, output) = await _git.RunAsync(
                new[] { "diff", "--name-only", $"{preMergeSha}..HEAD" },
                workingDirectory: repoRoot, ct: ct);
            if (exit != 0) return null;
            var lines = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.Length > 0)
                .ToList();
            return lines;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 태스크를 완료 상태로 마킹한다. State 저장은 StateStore 내부에서 thread-safe하게 처리되며
    /// tasks.json은 변경되지 않는다 (done은 .ralph-logs/state.json에만 기록).
    /// </summary>
    private async Task MarkTaskDoneThreadSafeAsync(string taskId, CancellationToken ct)
    {
        var task = _taskManager.GetTask(taskId)!;

        if (task.Subtasks is { Count: > 0 })
        {
            foreach (var sub in task.Subtasks.Where(s => !_taskManager.IsSubtaskDone(taskId, s.Id)))
                await _taskManager.MarkSubtaskDoneAsync(taskId, sub.Id, ct);
        }

        await _taskManager.MarkTaskDoneAsync(taskId, ct);
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

    // ─── fix2 #7: smoke 실패 자동 롤백 ────────────────────────────────────────────

    /// <summary>
    /// smoke 실패 직후 호출. 사용자 working tree가 깨끗하고 base에 외부 커밋이 끼지 않은
    /// 경우에만 batch에서 만든 머지 커밋들을 단일 revert 커밋으로 되돌리고, 해당 task들의
    /// state.json done 비트를 pending으로 재설정한다. revert 자체가 충돌나면 abort 후 보류.
    /// 보류/실패는 사용자 안내 + logger 기록으로 끝나고 batch는 항상 exit 1.
    /// </summary>
    // fix2 #8: 반환값 변경 — null=실패/보류, non-null string=성공(revert commit SHA)
    private async Task<string?> TryAutoRollbackAsync(
        BatchRollbackSnapshot snapshot,
        IReadOnlyList<string> mergedTasks,
        SmokePhaseResult smoke,
        CancellationToken ct)
    {
        AnsiConsole.MarkupLine(
            "[yellow]⚠ 자동 롤백을 시작합니다 (--auto-rollback-on-smoke-fail).[/]");
        _logger.Warn(
            $"[auto-rollback] start — base={snapshot.BaseBranch} baseSha={Short(snapshot.BaseSha)} " +
            $"mergedTasks=[{string.Join(",", mergedTasks)}]");

        var safety = await CheckRollbackSafetyAsync(snapshot, ct);
        if (!safety.Safe)
        {
            PrintAutoRollbackHeld(snapshot, mergedTasks, smoke, safety);
            _logger.Warn($"[auto-rollback] held — {safety.Reason}");
            return null;
        }

        // base..HEAD 사이의 머지 커밋들 (오래된 것 → 최신 순으로 정렬). mergedTasks와 1:1 매핑.
        var mergeShas = await GetFirstParentMergeShasAsync(snapshot.BaseSha, ct);
        if (mergeShas.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]자동 롤백 대상 머지 커밋을 찾지 못했습니다 — base가 이미 batch 시작 시점입니다.[/]");
            _logger.Warn("[auto-rollback] no merge commits in base..HEAD; skipping revert");
            return null;
        }

        var pairs = PairMergesWithTasks(mergeShas, mergedTasks);
        var message = BuildRevertMessage(snapshot, pairs, smoke);

        // git revert --no-commit -m 1 <sha-newest> ... <sha-oldest>
        // 다중 SHA는 git이 새 → 오래된 순으로 받아 차례로 적용. 여기서는 안전하게 newest-first로 전달.
        var revertArgs = new List<string> { "revert", "--no-commit", "-m", "1" };
        revertArgs.AddRange(mergeShas.AsEnumerable().Reverse());

        var (rExit, rOut) = await _git.RunAsync(revertArgs.ToArray(), ct: ct);
        if (rExit != 0)
        {
            // abort로 깔끔히 정리. 실패해도 message만 남기고 보류.
            await _git.RunAsync(["revert", "--abort"], ct: ct);
            PrintAutoRollbackFailed(snapshot, mergeShas, rOut);
            _logger.Error($"[auto-rollback] revert failed: {rOut.Trim()}");
            return null;
        }

        // 단일 revert 커밋으로 묶기. --allow-empty는 staged가 비었을 때(이미 동일 상태) 안전망.
        var (cExit, cOut) = await _git.RunAsync(
            ["commit", "-m", message, "--allow-empty"], ct: ct);
        if (cExit != 0)
        {
            await _git.RunAsync(["revert", "--abort"], ct: ct);
            PrintAutoRollbackFailed(snapshot, mergeShas, cOut);
            _logger.Error($"[auto-rollback] commit failed after revert: {cOut.Trim()}");
            return null;
        }

        // fix2 #8: revert 커밋 SHA 획득 (merge-log rollback entry에 기록)
        var (revHeadExit, revHeadOut) = await _git.RunAsync(["rev-parse", "HEAD"], ct: ct);
        var revertCommitSha = revHeadExit == 0 ? revHeadOut.Trim() : "";

        // state.json: 해당 task들을 다시 pending으로. revert 성공 후 실패해도 깨진 상태가 남지 않게
        // best-effort로 진행하되, 실패는 사용자에게 명시적으로 안내.
        var statePending = new List<string>();
        var stateFailed = new List<(string Id, string Error)>();
        foreach (var taskId in mergedTasks)
        {
            try
            {
                _taskManager.State.SetDoneInMemory(taskId, false);
                statePending.Add(taskId);
            }
            catch (Exception ex)
            {
                stateFailed.Add((taskId, ex.Message));
            }
        }
        try
        {
            await _taskManager.State.SaveAsync(ct);
        }
        catch (Exception ex)
        {
            // revert는 이미 커밋되었으나 state.json 저장 실패 — 사용자에게 부분 실패 안내.
            AnsiConsole.MarkupLine(
                $"[red]✗ state.json 저장 실패 — 수동 편집 필요: {Markup.Escape(ex.Message)}[/]");
            AnsiConsole.MarkupLine(
                $"  [yellow]revert 커밋은 정상적으로 생성되었습니다. " +
                $"{Markup.Escape(_taskManager.State.FilePath)}에서 다음 task의 done을 false로 편집하세요:[/]");
            foreach (var id in statePending)
                AnsiConsole.MarkupLine($"    - {Markup.Escape(id)}");
            _logger.Error($"[auto-rollback] state save failed after revert: {ex.Message}");
            return null;
        }

        PrintAutoRollbackSucceeded(snapshot, mergedTasks, mergeShas, smoke);
        _logger.Warn(
            $"[auto-rollback] reverted {mergeShas.Count} merge(s); " +
            $"tasks reset to pending: {string.Join(",", statePending)}");
        if (stateFailed.Count > 0)
        {
            foreach (var (id, err) in stateFailed)
                _logger.Error($"[auto-rollback] state pending failed for {id}: {err}");
        }
        return revertCommitSha; // fix2 #8: revert SHA 반환 (비어있어도 성공 신호)
    }

    /// <summary>
    /// 자동 revert 적용 가능 여부 검사.
    ///   (a) working tree가 깨끗한가 (`git status --porcelain=v1` 빈 결과).
    ///   (b) 현재 HEAD가 baseBranch에 있는가 (사용자가 다른 브랜치로 이동하지 않았는가).
    ///   (c) baseSha..HEAD의 first-parent 라인에 ralph 머지 외 외부 커밋이 끼지 않았는가.
    /// </summary>
    private async Task<RollbackSafety> CheckRollbackSafetyAsync(
        BatchRollbackSnapshot snapshot, CancellationToken ct)
    {
        // (a) working tree dirty
        var (stExit, stOut) = await _git.RunAsync(["status", "--porcelain=v1"], ct: ct);
        var dirtyLines = stExit == 0
            ? stOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.Length > 0).ToList()
            : new List<string>();

        // (b) 현재 브랜치
        var (brExit, brOut) = await _git.RunAsync(["rev-parse", "--abbrev-ref", "HEAD"], ct: ct);
        var currentBranch = brExit == 0 ? brOut.Trim() : "";

        // (c) baseSha..HEAD에 first-parent 비-머지 커밋
        var (xExit, xOut) = await _git.RunAsync(
            new[] { "rev-list", "--first-parent", "--no-merges", $"{snapshot.BaseSha}..HEAD" }, ct: ct);
        var externalCommits = xExit == 0
            ? xOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.Length > 0).ToList()
            : new List<string>();

        var problems = new List<string>();
        if (dirtyLines.Count > 0)
            problems.Add($"working tree dirty ({dirtyLines.Count} entries)");
        if (!string.IsNullOrEmpty(currentBranch)
            && !string.Equals(currentBranch, snapshot.BaseBranch, StringComparison.Ordinal)
            && currentBranch != "HEAD")
        {
            problems.Add($"HEAD가 base 브랜치 밖 ({currentBranch} ≠ {snapshot.BaseBranch})");
        }
        if (externalCommits.Count > 0)
            problems.Add($"base..HEAD에 외부 커밋 {externalCommits.Count}건");

        return new RollbackSafety(
            Safe: problems.Count == 0,
            Reason: problems.Count == 0 ? "" : string.Join("; ", problems),
            DirtyEntries: dirtyLines,
            CurrentBranch: currentBranch,
            ExternalCommits: externalCommits);
    }

    private async Task<List<string>> GetFirstParentMergeShasAsync(
        string baseSha, CancellationToken ct)
    {
        // 머지 커밋만 추출. rev-list는 newest-first; reverse하여 머지 순서(oldest-first)로 반환.
        var (exit, output) = await _git.RunAsync(
            new[] { "rev-list", "--first-parent", "--merges", $"{baseSha}..HEAD" }, ct: ct);
        if (exit != 0) return new List<string>();
        var shas = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length == 40 || l.Length == 64) // sha1 or sha256
            .ToList();
        shas.Reverse();
        return shas;
    }

    private static List<(string Sha, string? TaskId)> PairMergesWithTasks(
        IReadOnlyList<string> mergeShasOldestFirst, IReadOnlyList<string> mergedTasks)
    {
        var pairs = new List<(string Sha, string? TaskId)>();
        for (var i = 0; i < mergeShasOldestFirst.Count; i++)
        {
            var taskId = i < mergedTasks.Count ? mergedTasks[i] : null;
            pairs.Add((mergeShasOldestFirst[i], taskId));
        }
        return pairs;
    }

    private static string BuildRevertMessage(
        BatchRollbackSnapshot snapshot,
        IReadOnlyList<(string Sha, string? TaskId)> pairs,
        SmokePhaseResult smoke)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("chore(rollback): smoke test 실패로 batch 자동 revert");
        sb.AppendLine();
        sb.AppendLine("Smoke test 실패에 의해 직전 batch가 자동 롤백되었습니다.");
        sb.AppendLine("Ralph가 수행한 변경:");
        sb.AppendLine($"  - base 브랜치 '{snapshot.BaseBranch}'를 batch 시작 시점으로 되돌리는 revert 커밋 생성");
        sb.AppendLine("  - state.json의 batch 소속 task들을 다시 pending으로 표시");
        sb.AppendLine();
        sb.AppendLine("batch 정보:");
        sb.AppendLine($"  base: {snapshot.BaseBranch}");
        sb.AppendLine($"  base sha (스냅샷): {Short(snapshot.BaseSha)}");
        sb.AppendLine($"  reverted merge commits ({pairs.Count}건):");
        foreach (var (sha, taskId) in pairs)
        {
            var taskLabel = taskId is null ? "(matching task: ?)" : $"(task: {taskId})";
            sb.AppendLine($"    - {Short(sha)}  {taskLabel}");
        }
        sb.AppendLine();

        if (smoke.Detail is { } d)
        {
            sb.AppendLine("smoke test:");
            sb.AppendLine($"  command: {smoke.Command}");
            var timedOutSuffix = d.TimedOut ? ", TIMEOUT" : "";
            sb.AppendLine($"  exit: {d.ExitCode}{timedOutSuffix}");
            sb.AppendLine($"  duration: {d.Duration.TotalSeconds:F1}s");
            sb.AppendLine();
            sb.AppendLine("smoke stdout (tail, max 4 KB):");
            sb.AppendLine(SmokeTestPlanner.TruncateTail(d.Stdout));
            sb.AppendLine();
            sb.AppendLine("smoke stderr (tail, max 4 KB):");
            sb.AppendLine(SmokeTestPlanner.TruncateTail(d.Stderr));
            sb.AppendLine();
        }

        sb.AppendLine("옵션:");
        sb.AppendLine("  --auto-rollback-on-smoke-fail (CLI) /");
        sb.AppendLine("  RALPH_AUTO_ROLLBACK_ON_SMOKE_FAIL=true (env) /");
        sb.AppendLine("  workflow.autoRollbackOnSmokeFail=true (tasks.json)");
        sb.AppendLine();
        sb.AppendLine("다음 `ralph --run` 시 동일 task들이 새 worktree로 재실행됩니다.");
        return sb.ToString();
    }

    private static void PrintAutoRollbackHeld(
        BatchRollbackSnapshot snapshot,
        IReadOnlyList<string> mergedTasks,
        SmokePhaseResult smoke,
        RollbackSafety safety)
    {
        var err = Console.Error;
        err.WriteLine();
        err.WriteLine("[auto-rollback] held — 자동 롤백을 적용하지 않았습니다.");
        err.WriteLine($"  사유: {safety.Reason}");
        err.WriteLine($"  base: {snapshot.BaseBranch} (sha {Short(snapshot.BaseSha)})");
        err.WriteLine($"  현재 브랜치: {(string.IsNullOrEmpty(safety.CurrentBranch) ? "?" : safety.CurrentBranch)}");
        err.WriteLine($"  working tree dirty: {(safety.DirtyEntries.Count > 0 ? $"yes ({safety.DirtyEntries.Count} entries)" : "no")}");
        if (safety.ExternalCommits.Count > 0)
        {
            err.WriteLine($"  base..HEAD 외부 커밋: {safety.ExternalCommits.Count}건");
            foreach (var sha in safety.ExternalCommits.Take(5))
                err.WriteLine($"    - {Short(sha)}");
        }
        err.WriteLine();
        err.WriteLine("  smoke 실패는 그대로 종료 코드로 반환됩니다.");
        err.WriteLine("  복구 안내:");
        err.WriteLine("    1) 로컬 변경을 커밋/스태시한 뒤 다시 `ralph --run`을 시도해도");
        err.WriteLine("       이번 batch는 이미 머지된 상태로 남아있어 자동 롤백 대상이 아닙니다.");
        err.WriteLine("    2) 수동으로 되돌리려면:");
        err.WriteLine("       git revert -m 1 <머지 SHA들>");
        err.WriteLine("       그리고 .ralph-logs/state.json에서 해당 task의 done을 false로 편집.");
        err.WriteLine($"  되돌릴 머지 후보 task: {string.Join(", ", mergedTasks)}");
        if (smoke.Command is not null)
            err.WriteLine($"  smoke command: {smoke.Command}");
    }

    private static void PrintAutoRollbackSucceeded(
        BatchRollbackSnapshot snapshot,
        IReadOnlyList<string> mergedTasks,
        IReadOnlyList<string> mergeShas,
        SmokePhaseResult smoke)
    {
        AnsiConsole.MarkupLine(
            $"[green]✓ batch revert 완료[/] ({mergeShas.Count}건 머지 커밋 → 단일 revert 커밋)");
        AnsiConsole.MarkupLine(
            $"[green]✓ state.json 재설정[/] ({mergedTasks.Count} task → pending)");
        AnsiConsole.MarkupLine("[dim]다음 ralph --run에서 동일 task가 재실행됩니다.[/]");

        var err = Console.Error;
        err.WriteLine();
        err.WriteLine("[auto-rollback] reverted batch on smoke failure");
        err.WriteLine($"  base: {snapshot.BaseBranch} (sha {Short(snapshot.BaseSha)})");
        err.WriteLine($"  reverted merge commits ({mergeShas.Count}건):");
        foreach (var sha in mergeShas)
            err.WriteLine($"    - {Short(sha)}");
        err.WriteLine($"  tasks → pending: {string.Join(", ", mergedTasks)}");
        if (smoke.Command is not null)
            err.WriteLine($"  smoke command: {smoke.Command}");
    }

    private static void PrintAutoRollbackFailed(
        BatchRollbackSnapshot snapshot, IReadOnlyList<string> mergeShas, string detail)
    {
        AnsiConsole.MarkupLine(
            "[red]✗ 자동 revert 실패 — base는 머지된 상태 그대로입니다.[/]");
        var err = Console.Error;
        err.WriteLine();
        err.WriteLine("[auto-rollback] revert failed");
        err.WriteLine($"  base: {snapshot.BaseBranch} (sha {Short(snapshot.BaseSha)})");
        err.WriteLine($"  대상 머지 커밋 ({mergeShas.Count}건):");
        foreach (var sha in mergeShas)
            err.WriteLine($"    - {Short(sha)}");
        err.WriteLine($"  detail: {detail.Trim()}");
        err.WriteLine("  복구: git revert -m 1 <머지 SHA들> 후 state.json을 직접 편집하세요.");
    }

    private static string Short(string sha) =>
        string.IsNullOrEmpty(sha) ? "?" : sha.Length <= 7 ? sha : sha[..7];

    // ─── fix2 #8: merge-log 헬퍼 ─────────────────────────────────────────────────

    /// <summary>현재 브랜치 HEAD SHA를 가져온다. 실패 시 null.</summary>
    private async Task<string?> CaptureCurrentShaAsync(CancellationToken ct)
    {
        try
        {
            var (exit, output) = await _git.RunAsync(["rev-parse", "HEAD"], ct: ct);
            if (exit != 0) return null;
            var sha = output.Trim();
            return string.IsNullOrEmpty(sha) ? null : sha;
        }
        catch { return null; }
    }

    /// <summary>
    /// 머지된 task 목록에 대해 merge-log entry를 일괄 append한다.
    /// mergeShaBytaskId에 없는 task는 mergedSha=""로 기록한다.
    /// stateMarkResults에 없는 task는 stateMarked=false로 기록한다.
    /// append 실패는 warn 후 silent — batch를 중단하지 않는다.
    /// </summary>
    private async Task AppendMergeLogEntriesAsync(
        IReadOnlyList<string> mergedTasks,
        IReadOnlyDictionary<string, string> mergeShaBytaskId,
        IReadOnlyDictionary<string, bool> stateMarkResults,
        string smokeStr,
        string baseSha,
        int batchIndex,
        CancellationToken ct)
    {
        if (_mergeLog is null) return;
        foreach (var taskId in mergedTasks)
        {
            var mergedSha = mergeShaBytaskId.TryGetValue(taskId, out var s) ? s : "";
            var stateMarked = stateMarkResults.TryGetValue(taskId, out var ok) && ok;
            await _mergeLog.AppendMergeAsync(new MergeLogEntry
            {
                Ts = DateTime.UtcNow.ToString("o"),
                Batch = batchIndex,
                TaskId = taskId,
                BaseSha = baseSha,
                MergedSha = mergedSha,
                StateMarked = stateMarked,
                SmokeTest = smokeStr,
            }, ct);
        }
    }

    /// <summary>
    /// 자동 롤백 발동 시 rollback entry를 일괄 append한다.
    /// append 실패는 warn 후 silent.
    /// </summary>
    private async Task AppendRollbackLogEntriesAsync(
        IReadOnlyList<string> mergedTasks,
        IReadOnlyDictionary<string, string> mergeShaBytaskId,
        string revertSha,
        string baseSha,
        int batchIndex,
        CancellationToken ct)
    {
        if (_mergeLog is null) return;
        foreach (var taskId in mergedTasks)
        {
            var mergedSha = mergeShaBytaskId.TryGetValue(taskId, out var s) ? s : "";
            await _mergeLog.AppendRollbackAsync(new MergeLogEntry
            {
                Ts = DateTime.UtcNow.ToString("o"),
                Batch = batchIndex,
                TaskId = taskId,
                BaseSha = baseSha,
                MergedSha = mergedSha,
                StateMarked = false,
                SmokeTest = "failed",
                Event = "rollback",
                RollbackRevertSha = revertSha,
            }, ct);
        }
    }

    private sealed record RollbackSafety(
        bool Safe,
        string Reason,
        IReadOnlyList<string> DirtyEntries,
        string CurrentBranch,
        IReadOnlyList<string> ExternalCommits);
}
