using Spectre.Console;

namespace Ralph.Services;

/// <summary>
/// .ralph-logs/ 디렉토리에 누적된 오래된 로그 파일을 정리합니다.
/// retention 기간보다 오래된 파일을 삭제합니다.
/// 단, cost.jsonl(누적 비용 기록)과 validation.jsonl은 보존.
/// </summary>
public static class LogRotator
{
    private const string LogDir = ".ralph-logs";
    private const int DefaultRetentionDays = 30;

    private static readonly HashSet<string> ProtectedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "cost.jsonl",
        "validation.jsonl",
    };

    /// <summary>
    /// retention period보다 오래된 로그 파일을 삭제합니다.
    /// quiet=true면 출력 없이 silent로 정리.
    /// </summary>
    public static int Rotate(int? retentionDays = null, bool quiet = false)
    {
        var days = retentionDays
            ?? ParseEnvInt("RALPH_LOG_RETENTION_DAYS")
            ?? DefaultRetentionDays;

        if (days <= 0)
        {
            if (!quiet) AnsiConsole.MarkupLine("[dim]로그 rotation 비활성화 (retentionDays=0)[/]");
            return 0;
        }

        if (!Directory.Exists(LogDir))
            return 0;

        var cutoff = DateTime.UtcNow.AddDays(-days);
        var deleted = 0;
        long bytesFreed = 0;

        foreach (var file in Directory.EnumerateFiles(LogDir))
        {
            var name = Path.GetFileName(file);
            if (ProtectedFiles.Contains(name)) continue;

            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc >= cutoff) continue;
                bytesFreed += info.Length;
                File.Delete(file);
                deleted++;
            }
            catch
            {
                // best effort — 삭제 실패는 무시
            }
        }

        if (!quiet && deleted > 0)
        {
            AnsiConsole.MarkupLine(
                $"[dim]로그 rotation: {deleted}개 파일 삭제 ({FormatSize(bytesFreed)} 회수, {days}일 이전)[/]");
        }

        return deleted;
    }

    private static int? ParseEnvInt(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return int.TryParse(v, out var n) ? n : null;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1}KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1}MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2}GB";
    }
}
