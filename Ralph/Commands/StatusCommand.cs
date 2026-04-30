using Ralph.Services;
using Spectre.Console;

namespace Ralph.Commands;

/// <summary>
/// <c>ralph --status</c> — 진행률 + 병렬 batch + 현재 worktree (live/idle 검출) +
/// fix2 #8 머지 트랜잭션 로그 섹션 (merge-log.jsonl 존재 시).
/// </summary>
public sealed class StatusCommand : ICommand
{
    private readonly CommandContext _ctx;

    public StatusCommand(CommandContext ctx) => _ctx = ctx;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        DisplayHelpers.RequireFile(_ctx.TasksFile);
        var tm = await TaskManager.LoadAsync(_ctx.TasksFile);
        DisplayHelpers.ShowBanner();
        DisplayHelpers.ShowProgress(tm, RalphLogger.Null);

        // 병렬 배치 정보
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

        // P3-1/P2-4: 현재 worktree를 fs로 검출 (다른 터미널의 ralph --run 가시성 확보).
        // 모두 idle인 경우 stale 잔존 가능성 → cleanup 안내를 강조.
        const string worktreeBase = RalphPaths.WorktreeDir;
        const string logDir = RalphPaths.LogDir;
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

        // fix2 #8: merge-log 섹션 (파일 존재 시만 표시 — legacy 호환)
        await ShowMergeLogSectionAsync(tm, ct);

        return 0;
    }

    /// <summary>
    /// merge-log.jsonl을 읽어 마지막 batch, smoke 결과, 최근 머지, 이력 건수를 표시한다.
    /// 파일이 없거나 entry가 없으면 섹션 자체를 생략한다 (legacy 호환).
    /// </summary>
    private static async Task ShowMergeLogSectionAsync(TaskManager tm, CancellationToken ct)
    {
        var repoRoot = Directory.GetCurrentDirectory();
        var logSvc = new MergeLogService(repoRoot, RalphLogger.Null);
        IReadOnlyList<Models.MergeLogEntry> entries;
        try
        {
            entries = await logSvc.ReadAllAsync(ct);
        }
        catch
        {
            return;
        }

        if (entries.Count == 0) return;

        var mergeEntries = entries.Where(e => string.IsNullOrEmpty(e.Event) || e.Event == "merge").ToList();
        var rollbackEntries = entries.Where(e => e.Event == "rollback").ToList();

        // 마지막 batch 기준 정보
        var lastBatchNo = entries.Max(e => e.Batch);
        var lastBatchMerges = mergeEntries.Where(e => e.Batch == lastBatchNo).ToList();
        var lastBatchRollbacks = rollbackEntries.Where(e => e.Batch == lastBatchNo).ToList();
        var lastTs = entries.Last().Ts;
        var lastSmoke = lastBatchMerges.FirstOrDefault()?.SmokeTest ?? "?";

        AnsiConsole.MarkupLine($"\n[cyan]머지 트랜잭션 로그[/] [dim]({RalphPaths.MergeLogRelativePath}):[/]");
        AnsiConsole.MarkupLine($"  [cyan]마지막 batch :[/] #{lastBatchNo}  [dim]({Markup.Escape(lastTs)})[/]");

        var smokeColor = lastSmoke switch { "passed" => "green", "failed" => "red", _ => "dim" };
        AnsiConsole.MarkupLine($"  [cyan]smoke test   :[/] [{smokeColor}]{Markup.Escape(lastSmoke)}[/{smokeColor}]");

        if (lastBatchRollbacks.Count > 0)
        {
            var revertSha = ShortSha(lastBatchRollbacks[0].RollbackRevertSha ?? "");
            AnsiConsole.MarkupLine(
                $"  [cyan]자동 롤백    :[/] [red]revert {Markup.Escape(revertSha)} ({lastBatchRollbacks.Count} task pending 복귀)[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("  [cyan]자동 롤백    :[/] [dim]없음[/]");
        }

        if (lastBatchMerges.Count > 0)
        {
            AnsiConsole.MarkupLine("  [cyan]최근 머지    :[/]");
            foreach (var e in lastBatchMerges)
            {
                var shortSha = ShortSha(e.MergedSha);
                var stateLabel = e.StateMarked ? "[dim]state=marked[/]" : "[red]state=unmarked[/]";
                AnsiConsole.MarkupLine(
                    $"    [dim]-[/] {Markup.Escape(e.TaskId)}  [dim]merged={Markup.Escape(shortSha)}[/]  {stateLabel}");
            }
        }

        AnsiConsole.MarkupLine(
            $"  [cyan]history      :[/] [dim]merge entry {mergeEntries.Count}건, rollback entry {rollbackEntries.Count}건[/]");

        // state.json 불일치 경고 (merge-log stateMarked=true인데 state.json done=false인 경우)
        var mismatchCount = mergeEntries.Count(e => e.StateMarked && !tm.IsDone(e.TaskId));
        if (mismatchCount > 0)
        {
            AnsiConsole.MarkupLine(
                $"\n[yellow]⚠ state.json과 merge-log의 stateMarked 불일치 {mismatchCount}건. " +
                "--rollback 검토를 권장합니다.[/]");
        }
    }

    private static string ShortSha(string sha) =>
        string.IsNullOrEmpty(sha) ? "?" : sha.Length <= 7 ? sha : sha[..7];
}
