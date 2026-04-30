using Ralph.Models;
using Spectre.Console;

namespace Ralph.Services;

/// <summary>
/// 머지 후 smoke test 결과 + 사용자에게 보여줄 메시지를 묶은 구조체.
/// MergeOrchestrator가 산출하고 AutoRollbackHandler가 commit 메시지/콘솔 출력에 사용.
///
/// Skipped=true면 명령이 아예 실행되지 않은 경우 (--no-smoke-test, docs-only 추론 스킵 등).
/// </summary>
internal sealed record SmokePhaseResult(
    bool Skipped, bool Passed, string? Command, VerificationResult? Detail);

/// <summary>
/// fix2 #7: smoke 실패 시 batch 자동 롤백 책임.
///
/// 호출 흐름:
///   1) <see cref="CheckSafetyAsync"/>로 working tree dirty / 외부 커밋 / 잘못된 브랜치 확인.
///   2) base..HEAD의 first-parent 머지 커밋들을 단일 revert 커밋으로 묶는다.
///   3) state.json의 해당 task들을 pending으로 되돌린다.
///   4) 성공 시 revert 커밋 SHA 반환 (호출자가 merge-log에 rollback 이벤트로 기록), 실패/보류 시 null.
///
/// batch는 호출자(MergeOrchestrator)가 항상 exit 1로 종료하므로, 이 핸들러는 "exit 코드"는
/// 결정하지 않고 "롤백을 시도/적용했는가"만 책임진다.
/// </summary>
internal sealed class AutoRollbackHandler
{
    private readonly GitService _git;
    private readonly TaskManager _taskManager;
    private readonly RalphLogger _logger;

    public AutoRollbackHandler(GitService git, TaskManager taskManager, RalphLogger logger)
    {
        _git = git;
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <summary>
    /// smoke 실패 직후 호출. 사용자 working tree가 깨끗하고 base에 외부 커밋이 끼지 않은
    /// 경우에만 batch에서 만든 머지 커밋들을 단일 revert 커밋으로 되돌리고, 해당 task들의
    /// state.json done 비트를 pending으로 재설정한다. revert 자체가 충돌나면 abort 후 보류.
    /// 보류/실패는 사용자 안내 + logger 기록으로 끝나고 호출자는 항상 batch를 exit 1로 종료한다.
    ///
    /// 반환값: 성공 시 revert 커밋 SHA(빈 문자열일 수 있음 — best-effort), 실패/보류 시 null.
    /// </summary>
    public async Task<string?> TryRollbackAsync(
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

        var safety = await CheckSafetyAsync(snapshot, ct);
        if (!safety.Safe)
        {
            PrintHeld(snapshot, mergedTasks, smoke, safety);
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
            PrintFailed(snapshot, mergeShas, rOut);
            _logger.Error($"[auto-rollback] revert failed: {rOut.Trim()}");
            return null;
        }

        // 단일 revert 커밋으로 묶기. --allow-empty는 staged가 비었을 때(이미 동일 상태) 안전망.
        var (cExit, cOut) = await _git.RunAsync(
            ["commit", "-m", message, "--allow-empty"], ct: ct);
        if (cExit != 0)
        {
            await _git.RunAsync(["revert", "--abort"], ct: ct);
            PrintFailed(snapshot, mergeShas, cOut);
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

        PrintSucceeded(snapshot, mergedTasks, mergeShas, smoke);
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
    private async Task<RollbackSafety> CheckSafetyAsync(
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

    private static void PrintHeld(
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

    private static void PrintSucceeded(
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

    private static void PrintFailed(
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

    private sealed record RollbackSafety(
        bool Safe,
        string Reason,
        IReadOnlyList<string> DirtyEntries,
        string CurrentBranch,
        IReadOnlyList<string> ExternalCommits);
}
