using Ralph.Services;
using Spectre.Console;

namespace Ralph.Commands;

/// <summary>
/// <c>ralph --status</c> — 진행률 + 병렬 batch + 현재 worktree (live/idle 검출).
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
        DisplayHelpers.ShowProgress(tm, null);

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

        return 0;
    }
}
