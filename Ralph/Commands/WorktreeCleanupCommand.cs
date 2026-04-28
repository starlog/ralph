using Ralph.Services;
using Spectre.Console;

namespace Ralph.Commands;

/// <summary><c>ralph --worktree-cleanup</c> — 잔존 ralph worktree 강제 삭제.</summary>
public sealed class WorktreeCleanupCommand : ICommand
{
    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var git = new GitService();
        using var logger = new RalphLogger();
        var worktree = new WorktreeService(git);

        var stale = await worktree.DetectStaleWorktreesAsync(ct);
        if (stale.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]정리할 worktree가 없습니다.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[yellow]{stale.Count}개의 ralph worktree를 발견했습니다:[/]");
        foreach (var s in stale)
            AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(s)}[/]");

        await worktree.CleanupAllAsync(logger, ct);
        AnsiConsole.MarkupLine("[green]모든 worktree가 정리되었습니다.[/]");
        return 0;
    }
}
