using Ralph.Models;
using Spectre.Console;

namespace Ralph.Services;

/// <summary>
/// 워크트리 실행이 끝난 태스크들을 base 브랜치에 머지하는 책임.
/// 단계: 머지 직전 정규화/검증 → rebase advance → merge → 충돌 해결 체인(<see cref="ConflictStrategyRunner"/>)
/// → done 마킹 → post-merge smoke test → 실패 시 자동 롤백(<see cref="AutoRollbackHandler"/>).
/// 충돌/롤백 세부 로직은 각각 별도 클래스로 분리되어 있으므로 본 클래스는 batch 흐름 제어만 담당.
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
    private readonly RunOptions _options;

    private readonly ConflictStrategyRunner _conflict;
    private readonly AutoRollbackHandler _rollback;

    // fix2 #8: 머지 트랜잭션 로그. MergeAndFinalizeAsync 첫 호출 시 lazy-init.
    private MergeLogService? _mergeLog;
    private int _batchCounter;

    /// <summary>RunSingle path를 머지가 abort 시 fallback으로 호출하기 위한 콜백.</summary>
    public Func<string, CancellationToken, Task<int>>? RerunSequential { get; set; }

    public MergeOrchestrator(
        TaskManager taskManager, IAgentRunner claude, GitService git, WorktreeService worktree,
        RalphLogger logger, VerificationRunner verifier, CostTracker cost,
        RunOptions options)
    {
        _taskManager = taskManager;
        _claude = claude;
        _git = git;
        _worktree = worktree;
        _logger = logger;
        _verifier = verifier;
        _cost = cost;
        _options = options;

        _conflict = new ConflictStrategyRunner(claude, git, worktree, logger, cost, options.ModelOverride);
        _rollback = new AutoRollbackHandler(git, taskManager, logger);
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
                tasksFileName: Path.GetFileName(_options.TasksFile),
                logger: _logger, ct: ct);

            // F4: declared(modifiedFiles ∪ outputFiles) vs actual(base...HEAD) 검증.
            var declared = DeclaredFiles.Build(_taskManager.GetTask(taskId)!);
            var validation = await _worktree.ValidateModifiedFilesAsync(
                taskId, baseBranch, declared, _logger, ct: ct);
            ReportValidation(taskId, validation);

            // P0-3: strict 모드에서 diff 자체가 실패하면 머지 차단.
            if (_options.StrictFiles && validation.DiffFailed)
            {
                AnsiConsole.MarkupLine(
                    $"  [red]✗[/] {Markup.Escape(taskId)} diff 실패로 검증 불가. " +
                    $"머지 중단 (strict-files).");
                _logger.Error(
                    $"[validate:files][strict] {taskId} diff failed: {validation.DiffError}");
                return 1;
            }

            if (_options.StrictFiles && validation.HasUndeclared)
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
                taskId, baseBranch, _logger, _options.StrictCleanup, ct);

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

                var resolved = await _conflict.ResolveAsync(
                    taskId, baseBranch, mergeResult, strategyChain, RerunSequential, ct);

                if (!resolved)
                {
                    _logger.Error($"Merge conflict unresolved for {taskId}");

                    // C3 fix: 충돌로 batch를 중단하기 전에 이미 base에 머지된 동료 task들의
                    // done 마킹을 먼저 처리한다. 머지 커밋은 이미 base에 반영된 상태인데
                    // done이 안 찍히면 다음 --run에서 재dispatch되어 토큰 낭비 + 재머지 충돌
                    // 가능성이 생긴다. Loop 2(아래)와 동일하게 state 쓰기 실패 시 진단 후 중단.
                    var alreadyMerged = mergeShaBytaskId.Keys.ToList();
                    var earlyStateMarkResults = new Dictionary<string, bool>();
                    var earlyMarked = new List<string>();
                    var earlyPending = new List<string>(alreadyMerged);
                    foreach (var doneId in alreadyMerged)
                    {
                        try
                        {
                            await MarkTaskDoneThreadSafeAsync(doneId, ct);
                            earlyStateMarkResults[doneId] = true;
                            earlyMarked.Add(doneId);
                            earlyPending.Remove(doneId);
                            var t = _taskManager.GetTask(doneId)!;
                            AnsiConsole.MarkupLine(
                                $"[green]태스크 완료: {Markup.Escape(t.Title)}[/]");
                            _logger.TaskEnd(doneId, "completed");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            earlyStateMarkResults[doneId] = false;
                            ReportStateWriteFailure(doneId, ex, earlyMarked, earlyPending);
                            await AppendMergeLogEntriesAsync(
                                alreadyMerged, mergeShaBytaskId, earlyStateMarkResults,
                                "skipped", preMergeSha ?? "", batchIndex, ct);
                            foreach (var remaining in taskIds)
                            {
                                if (!await _worktree.CleanupWorktreeAsync(remaining, _logger, ct))
                                    cleanupFailures++;
                            }
                            reportCleanupFailures(cleanupFailures);
                            return 1;
                        }
                    }

                    if (alreadyMerged.Count > 0)
                    {
                        await AppendMergeLogEntriesAsync(
                            alreadyMerged, mergeShaBytaskId, earlyStateMarkResults,
                            "skipped", preMergeSha ?? "", batchIndex, ct);
                    }

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
        var smoke = await RunPostMergeSmokeTestAsync(preMergeSha, baseBranch, ct);

        // 5.6 smoke 실패 + --auto-fix-smoke 활성화: Claude로 1회 자동 수정 시도.
        // 성공 시 smoke 결과를 통과로 갱신; 실패 시 fix 커밋을 되돌리고 기존 실패 경로로 폴스루.
        if (!smoke.Skipped && !smoke.Passed && _options.AutoFixSmoke && mergedTasks.Count > 0)
        {
            smoke = await TryFixSmokeWithClaudeAsync(
                smoke, baseBranch, mergedTasks, preMergeSha, ct);
        }
        var smokeStr = smoke.Skipped ? "skipped" : smoke.Passed ? "passed" : "failed";

        // fix2 #8: 모든 머지된 task에 대해 merge-log entry append (batch 단위 일괄)
        await AppendMergeLogEntriesAsync(
            mergedTasks, mergeShaBytaskId, stateMarkResults, smokeStr,
            preMergeSha ?? "", batchIndex, ct);

        if (!smoke.Skipped && !smoke.Passed)
        {
            // fix2 #7: opt-in 자동 롤백. 실행 여부와 무관하게 batch는 실패(1)로 종료한다 —
            // 자동 롤백 성공도 base를 batch 이전 상태로 되돌렸을 뿐 "smoke가 통과한" 것은 아님.
            if (_options.AutoRollbackOnSmokeFail && batchSnapshot is not null && mergedTasks.Count > 0)
            {
                // fix2 #8: TryRollbackAsync는 revert SHA(성공) 또는 null(실패/보류) 반환
                var revertSha = await _rollback.TryRollbackAsync(batchSnapshot, mergedTasks, smoke, ct);
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
    /// 머지 후 base 브랜치에서 smoke test를 실행해 머지 결과의 semantic 정합성을 검증.
    /// 우선순위 결정은 <see cref="SmokeTestPlanner.Plan"/>에 위임. preMergeSha가 있으면
    /// 그 시점부터 HEAD까지의 변경 파일을 인자로 넘겨 docs-only 변경일 때 추론을 스킵한다.
    /// 실행은 <c>.ralph-smoke</c> 격리 worktree에서 수행해 빌드 산출물(bin/, obj/, tsbuildinfo,
    /// node_modules 등)이 master worktree를 더티화하지 않도록 한다 — 더티 트리는 다음 batch의
    /// rebase preflight를 깨뜨리는 직접 원인이었다. 격리 worktree 생성이 실패하면 안전하게
    /// repoRoot fallback으로 진행한다 (이전 동작).
    /// </summary>
    private async Task<SmokePhaseResult> RunPostMergeSmokeTestAsync(
        string? preMergeSha, string baseBranch, CancellationToken ct)
    {
        var repoRoot = await _git.GetRepoRootAsync(ct: ct);
        var configured = _taskManager.Data.Workflow?.SmokeTest;
        var changedFiles = await GetChangedFilesAsync(preMergeSha, repoRoot, ct);

        var spec = SmokeTestPlanner.Plan(
            repoRoot: repoRoot,
            configured: configured,
            cliCommand: _options.SmokeTestCommandOverride,
            envCommand: null, // ArgParser가 CLI/env merge하여 SmokeTestCommandOverride 한 곳으로 전달.
            noSmokeTest: _options.NoSmokeTest,
            changedFiles: changedFiles);

        if (spec is null)
        {
            var reason = DetermineSkipReason(configured, changedFiles);
            _logger.Info($"[smoke-test] skipped ({reason})");
            return new SmokePhaseResult(Skipped: true, Passed: false, Command: null, Detail: null);
        }

        // 격리 worktree 확보 — 실패 시 repoRoot fallback (경고 로그만 남기고 진행).
        var smokeCwd = await _worktree.EnsureSmokeWorktreeAsync(repoRoot, baseBranch, _logger, ct)
                       ?? repoRoot;
        var isolated = !ReferenceEquals(smokeCwd, repoRoot) && smokeCwd != repoRoot;

        // 라벨링: 어디서 왔는지 사용자에게 보여주는 게 진단에 도움이 됨.
        var origin = ResolveSpecOrigin(spec, configured);
        var label = origin switch
        {
            "cli/env"  => "Smoke test 실행 (CLI/env override)",
            "workflow" => "Smoke test 실행 (workflow.smokeTest)",
            _          => "Smoke test 실행 (자동 추론)",
        };

        var cwdHint = isolated ? "isolated worktree" : "repoRoot — fallback";
        AnsiConsole.MarkupLine(
            $"\n[cyan]{label}:[/] [dim]{Markup.Escape(spec.Command)}[/] " +
            $"[dim](cwd: {Markup.Escape(smokeCwd)}, {cwdHint})[/]");
        _logger.Info(
            $"[smoke-test] running: {spec.Command} (cwd: {smokeCwd}, isolated={isolated}, origin: {origin})");

        var result = await _verifier.RunAsync(spec, smokeCwd, _logger, output: null, ct);
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

    /// <summary>
    /// `--auto-fix-smoke` opt-in: smoke test 실패 시 Claude를 1회 호출해 base 브랜치에서 자동 수정 시도.
    ///
    /// 동작:
    ///   1. baseBranch HEAD를 preFixSha로 캡처 (실패 시 fix 커밋을 되돌릴 기준점).
    ///   2. 실패한 smoke 명령 + stdout/stderr + 이번 batch에서 머지된 task/파일 컨텍스트로 프롬프트 구성.
    ///   3. Claude를 repoRoot에서 실행 (full tools). repoRoot는 baseBranch에 체크아웃되어 있다고 가정.
    ///   4. Claude가 변경한 파일이 있으면 `[smoke-fix]` 단일 커밋으로 base에 추가.
    ///   5. smoke worktree를 새 base HEAD로 갱신 후 같은 명령을 재실행.
    ///   6. 통과 → 갱신된 SmokePhaseResult(Passed=true) 반환.
    ///      실패 → preFixSha로 hard reset (fix 커밋 폐기) 후 원래 실패 결과를 반환해 기존
    ///             auto-rollback / batch fail 경로가 변하지 않도록 한다.
    ///
    /// 안전 장치:
    ///   - repoRoot HEAD가 baseBranch가 아니면(분기) 스킵 — 사용자 작업을 건드리지 않는다.
    ///   - working tree가 dirty하면 스킵 — 사용자 미커밋 변경 보호.
    ///   - 호출 1회로 제한 (반복 호출은 cost runaway 위험).
    /// </summary>
    private async Task<SmokePhaseResult> TryFixSmokeWithClaudeAsync(
        SmokePhaseResult failed, string baseBranch, List<string> mergedTasks,
        string? preMergeSha, CancellationToken ct)
    {
        if (failed.Detail is null || string.IsNullOrWhiteSpace(failed.Command))
            return failed;

        var repoRoot = await _git.GetRepoRootAsync(ct: ct);

        // 안전 (a): 현재 브랜치 == baseBranch?
        var (brExit, brOut) = await _git.RunAsync(
            new[] { "rev-parse", "--abbrev-ref", "HEAD" }, repoRoot, ct);
        var currentBranch = brExit == 0 ? brOut.Trim() : "";
        if (!string.Equals(currentBranch, baseBranch, StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]⚠ auto-fix-smoke 스킵: HEAD가 base 밖 ({Markup.Escape(currentBranch)} ≠ {Markup.Escape(baseBranch)}).[/]");
            _logger.Warn(
                $"[smoke-fix] skipped — HEAD={currentBranch} != base={baseBranch}");
            return failed;
        }

        // 안전 (b): working tree dirty?
        var (stExit, stOut) = await _git.RunAsync(
            new[] { "status", "--porcelain=v1" }, repoRoot, ct);
        if (stExit == 0 && !string.IsNullOrWhiteSpace(stOut))
        {
            AnsiConsole.MarkupLine(
                "[yellow]⚠ auto-fix-smoke 스킵: working tree가 dirty 상태입니다.[/]");
            _logger.Warn("[smoke-fix] skipped — working tree dirty");
            return failed;
        }

        var preFixSha = await CaptureCurrentShaAsync(ct);
        if (string.IsNullOrEmpty(preFixSha))
        {
            _logger.Warn("[smoke-fix] skipped — preFixSha 캡처 실패");
            return failed;
        }

        // 이번 batch에서 변경된 파일 (smoke 실패 컨텍스트로 Claude에 전달)
        var changedFiles = await GetChangedFilesAsync(preMergeSha, repoRoot, ct);
        var changedListText = changedFiles is { Count: > 0 }
            ? string.Join("\n", changedFiles.Take(40).Select(f => $"  - {f}"))
              + (changedFiles.Count > 40 ? $"\n  - ... (외 {changedFiles.Count - 40}건)" : "")
            : "  (변경 파일 캡처 실패 — git status로 직접 확인하세요)";

        var taskListText = string.Join(", ", mergedTasks);

        // stdout/stderr는 길 수 있으므로 마지막 80줄로 트림.
        var stdoutTail = TailLines(failed.Detail.Stdout, 80);
        var stderrTail = TailLines(failed.Detail.Stderr, 80);

        var prompt = $$"""
            방금 ralph가 batch를 base 브랜치에 머지한 직후 실행한 post-merge smoke test가 실패했습니다.
            당신의 임무는 base 브랜치에서 직접 수정해 smoke test를 통과시키는 것입니다.

            ## 환경
            - 작업 디렉토리: {{repoRoot}}
            - 현재 브랜치: {{baseBranch}} (이 브랜치에서 직접 수정 + 커밋)
            - 이번 batch에서 머지된 태스크: {{taskListText}}

            ## 실패한 smoke 명령
            ```
            {{failed.Command}}
            ```
            exit code: {{failed.Detail.ExitCode}}{{(failed.Detail.TimedOut ? " (TIMED OUT)" : "")}}

            ## stdout (최근 80줄)
            ```
            {{stdoutTail}}
            ```

            ## stderr (최근 80줄)
            ```
            {{stderrTail}}
            ```

            ## 이번 batch에서 변경된 파일
            {{changedListText}}

            ## 지시
            1. 먼저 위 출력을 읽고 실패 원인을 파악하세요. 흔한 패턴:
               - 설정 파일(예: tsconfig.json, package.json)의 자기모순으로 인한 latent 버그가 새 파일이 추가되면서 표면화.
               - 새로 추가된 파일의 import/syntax 오류.
               - 누락된 의존성.
            2. **근본 원인**을 수정하세요. 빌드/테스트 명령을 우회하거나 끄지 마세요.
            3. 수정 후 같은 명령을 직접 실행해 통과를 확인하세요:
               ```
               {{failed.Command}}
               ```
               (작업 디렉토리는 위와 다를 수 있습니다 — `.ralph-smoke` 격리 worktree에서 실행됩니다. 동일 명령을 repo root에서 돌려도 무방합니다.)
            4. 통과를 확인한 뒤 종료하세요. **git add / git commit / git push는 실행하지 마세요** — staging과 커밋은 ralph가 처리합니다.

            만약 root cause를 찾지 못하거나 수정이 위험하다고 판단되면, 변경 없이 종료하세요. ralph가 fix를 폐기하고 기존 실패 경로(설정에 따라 auto-rollback)로 넘어갑니다.
            """;

        AnsiConsole.MarkupLine(
            $"\n[cyan]auto-fix-smoke:[/] Claude로 자동 수정 시도 [dim](base: {Markup.Escape(baseBranch)})[/]");
        _logger.Info(
            $"[smoke-fix] invoking Claude (preFixSha={preFixSha}, mergedTasks=[{string.Join(",", mergedTasks)}])");

        var fixModel = ModelResolver.ResolveForNonTask(_options.ModelOverride);
        ClaudeResult? fixResult = null;
        try
        {
            fixResult = await _claude.RunWithRetryAsync(
                prompt, model: fixModel, workingDirectory: repoRoot, logger: _logger, ct: ct);
        }
        finally
        {
            await _cost.RecordAsync("smoke-fix", fixModel, fixResult, CancellationToken.None);
        }

        if (fixResult is null || !fixResult.Success)
        {
            AnsiConsole.MarkupLine("[yellow]auto-fix-smoke: Claude 호출 실패 — fix 폐기, 기존 경로로 진행.[/]");
            _logger.Warn(
                $"[smoke-fix] Claude failed: success={fixResult?.Success}, exit={fixResult?.ExitCode}");
            // Claude가 부분 변경을 남겼을 수 있으므로 working tree를 강제 정리.
            await _git.RunAsync(new[] { "reset", "--hard", preFixSha }, repoRoot, ct);
            return failed;
        }

        // Claude가 변경한 게 있는지 확인.
        var (postExit, postOut) = await _git.RunAsync(
            new[] { "status", "--porcelain=v1" }, repoRoot, ct);
        var hasChanges = postExit == 0 && !string.IsNullOrWhiteSpace(postOut);
        if (!hasChanges)
        {
            AnsiConsole.MarkupLine("[yellow]auto-fix-smoke: Claude가 수정 없이 종료 — 기존 경로로 진행.[/]");
            _logger.Info("[smoke-fix] Claude finished without changes");
            return failed;
        }

        // fix 커밋 생성. 사용자 지정 commitMessageTemplate를 따르지 않고 고정 prefix를 사용한다 —
        // 이 커밋은 ralph 자동 복구이지 task 결과물이 아니다.
        var commitMsg = $"[smoke-fix] {string.Join(", ", mergedTasks)} 머지 후 smoke 자동 수정";
        var (addExit, _) = await _git.RunAsync(new[] { "add", "-A" }, repoRoot, ct);
        if (addExit != 0)
        {
            await _git.RunAsync(new[] { "reset", "--hard", preFixSha }, repoRoot, ct);
            _logger.Warn("[smoke-fix] git add 실패 — fix 폐기");
            return failed;
        }
        var (cmExit, cmOut) = await _git.RunAsync(
            new[] { "commit", "-m", commitMsg, "--no-verify" }, repoRoot, ct);
        if (cmExit != 0)
        {
            await _git.RunAsync(new[] { "reset", "--hard", preFixSha }, repoRoot, ct);
            _logger.Warn($"[smoke-fix] git commit 실패: {cmOut.Trim()}");
            return failed;
        }

        // smoke를 다시 실행. EnsureSmokeWorktreeAsync 안에서 base HEAD로 reset되므로 fix가 반영된다.
        AnsiConsole.MarkupLine("[cyan]auto-fix-smoke: smoke test 재실행 중...[/]");
        var retried = await RunPostMergeSmokeTestAsync(preMergeSha, baseBranch, ct);

        if (retried.Passed)
        {
            AnsiConsole.MarkupLine($"[green]auto-fix-smoke: 통과 ✓[/] [dim]({Markup.Escape(commitMsg)})[/]");
            _logger.Info("[smoke-fix] passed after fix commit");
            return retried;
        }

        AnsiConsole.MarkupLine(
            "[yellow]auto-fix-smoke: 재실행도 실패 — fix 커밋 폐기 후 기존 경로로 진행.[/]");
        _logger.Warn("[smoke-fix] retry still failing — reverting fix commit");
        await _git.RunAsync(new[] { "reset", "--hard", preFixSha }, repoRoot, ct);
        return failed;
    }

    private static string TailLines(string? text, int n)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        var lines = text.Split('\n');
        if (lines.Length <= n) return text!;
        return string.Join('\n', lines[(lines.Length - n)..]);
    }

    private string DetermineSkipReason(
        VerificationSpec? configured, IReadOnlyList<string>? changedFiles)
    {
        if (_options.NoSmokeTest) return "--no-smoke-test";
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
        if (!string.IsNullOrWhiteSpace(_options.SmokeTestCommandOverride)
            && spec.Command == _options.SmokeTestCommandOverride!.Trim())
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

        if (validation.HasUndeclared && !_options.StrictFiles)
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

}
