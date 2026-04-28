using Ralph.Services;
using Spectre.Console;

namespace Ralph.Commands;

/// <summary>
/// <c>ralph --logs [--live] [--cleanup] [task-id]</c> — 로그 디렉토리 탐색.
/// 인자 없으면 task/session 로그 목록, taskId 주면 해당 task 로그 출력 또는 live tail.
/// </summary>
public sealed class LogsCommand : ICommand
{
    private const string LogDir = ".ralph-logs";
    private readonly CommandContext _ctx;

    public LogsCommand(CommandContext ctx) => _ctx = ctx;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        if (!Directory.Exists(LogDir))
        {
            AnsiConsole.MarkupLine("[yellow]No logs found.[/]");
            return 0;
        }

        // --cleanup: 오래된 로그 정리
        if (_ctx.Args.Contains("--cleanup"))
        {
            var deleted = LogRotator.Rotate(quiet: false);
            if (deleted == 0)
                AnsiConsole.MarkupLine("[green]정리할 오래된 로그가 없습니다.[/]");
            return 0;
        }

        var liveMode = _ctx.Args.Contains("--live");
        var logArgs = _ctx.Args.Skip(1).Where(a => a is not "--live" and not "--cleanup").ToList();

        // ralph --logs [--live] {taskId}
        if (logArgs.Count >= 1 && !logArgs[0].StartsWith("--"))
        {
            var taskId = logArgs[0];
            var taskLogFile = Path.Combine(LogDir, $"{taskId}.log");

            if (liveMode)
            {
                return await TailFollowAsync(taskLogFile, taskId, ct);
            }

            if (File.Exists(taskLogFile))
            {
                AnsiConsole.MarkupLine($"[blue]Task log: {Markup.Escape(taskId)}[/]");
                AnsiConsole.Write(new Rule().RuleStyle("dim"));
                // FileShare.ReadWrite — 병렬 executor가 쓰는 동안에도 읽기 허용
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

        // 태스크별 로그 목록
        var taskLogs = Directory.GetFiles(LogDir, "*.log")
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
        var sessionLogs = Directory.GetFiles(LogDir, "ralph-*.log")
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

    private static async Task<int> TailFollowAsync(string filePath, string taskId, CancellationToken ct)
    {
        AnsiConsole.MarkupLine($"[blue]Live tail: {Markup.Escape(taskId)}[/] [dim](Ctrl+C to stop)[/]");
        AnsiConsole.Write(new Rule().RuleStyle("dim"));

        // 파일 생성 대기
        while (!File.Exists(filePath))
        {
            ct.ThrowIfCancellationRequested();
            AnsiConsole.MarkupLine("[dim]로그 파일 대기 중...[/]");
            await Task.Delay(500, ct);
        }

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);

        var existing = await sr.ReadToEndAsync(ct);
        if (!string.IsNullOrEmpty(existing))
            Console.Write(existing);

        var buf = new char[4096];
        while (!ct.IsCancellationRequested)
        {
            var read = await sr.ReadAsync(buf, ct);
            if (read > 0)
                Console.Write(buf, 0, read);
            else
                await Task.Delay(200, ct);
        }

        return 0;
    }
}
