using System.Text.Json;
using Spectre.Console;

namespace Ralph.Services;

public class CostEntry
{
    public string TaskId { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }
    public double EstimatedUsd { get; set; }
    public double DurationSec { get; set; }
}

/// <summary>
/// Claude Code 호출별 token 사용량과 추정 비용을 .ralph-logs/cost.jsonl에 누적 기록합니다.
/// stream-json의 result 메시지에 포함된 usage 데이터를 파싱해서 사용합니다.
/// </summary>
public class CostTracker
{
    private const string LogDir = ".ralph-logs";
    private const string LogFileName = "cost.jsonl";

    // 추정 단가 (USD per 1M tokens) — 2026 기준 대략값. 정확한 값은 Anthropic 공식가 참조.
    // input/output을 model별로 구분.
    private static readonly Dictionary<string, (double input, double output, double cacheRead, double cacheCreate)>
        Pricing = new(StringComparer.OrdinalIgnoreCase)
        {
            ["opus"]   = (15.0, 75.0, 1.5, 18.75),
            ["sonnet"] = (3.0, 15.0, 0.30, 3.75),
            ["haiku"]  = (0.80, 4.0, 0.08, 1.0),
        };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string LogFilePath => Path.Combine(LogDir, LogFileName);

    public async Task RecordAsync(string taskId, string model, ClaudeResult result, CancellationToken ct = default)
    {
        if (result.Usage == null) return; // usage 정보가 없으면 기록할 게 없음

        Directory.CreateDirectory(LogDir);

        var u = result.Usage;
        var entry = new CostEntry
        {
            TaskId = taskId,
            Model = model,
            TimestampUtc = DateTime.UtcNow,
            InputTokens = u.InputTokens,
            OutputTokens = u.OutputTokens,
            CacheReadTokens = u.CacheReadTokens,
            CacheCreationTokens = u.CacheCreationTokens,
            EstimatedUsd = EstimateUsd(model, u),
            DurationSec = result.Duration.TotalSeconds,
        };

        var line = JsonSerializer.Serialize(entry, JsonOpts) + "\n";
        await File.AppendAllTextAsync(LogFilePath, line, ct);
    }

    public static double EstimateUsd(string model, TokenUsage u)
    {
        var key = NormalizeModel(model);
        if (!Pricing.TryGetValue(key, out var p))
            return 0.0;
        return (u.InputTokens * p.input
                + u.OutputTokens * p.output
                + u.CacheReadTokens * p.cacheRead
                + u.CacheCreationTokens * p.cacheCreate) / 1_000_000.0;
    }

    private static string NormalizeModel(string model)
    {
        if (string.IsNullOrEmpty(model)) return "opus";
        var lower = model.ToLowerInvariant();
        if (lower.Contains("opus")) return "opus";
        if (lower.Contains("sonnet")) return "sonnet";
        if (lower.Contains("haiku")) return "haiku";
        return lower;
    }

    /// <summary>
    /// cost.jsonl의 모든 entry usd를 합산해 누적 비용(USD)을 반환합니다.
    /// 파일이 없거나 비었으면 0.0. 손상된 라인은 skip.
    /// </summary>
    public async Task<double> GetTotalUsdAsync(CancellationToken ct = default)
    {
        if (!File.Exists(LogFilePath)) return 0.0;

        var total = 0.0;
        await foreach (var line in File.ReadLinesAsync(LogFilePath, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<CostEntry>(line, JsonOpts);
                if (entry != null) total += entry.EstimatedUsd;
            }
            catch (JsonException) { /* skip malformed line */ }
        }
        return total;
    }

    /// <summary>
    /// 누적 cost.jsonl을 읽어 콘솔에 요약 출력합니다.
    /// </summary>
    public async Task PrintSummaryAsync(CancellationToken ct = default)
    {
        if (!File.Exists(LogFilePath))
        {
            AnsiConsole.MarkupLine("[yellow]비용 기록이 없습니다 (.ralph-logs/cost.jsonl이 없습니다).[/]");
            return;
        }

        var entries = new List<CostEntry>();
        await foreach (var line in File.ReadLinesAsync(LogFilePath, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<CostEntry>(line, JsonOpts);
                if (entry != null) entries.Add(entry);
            }
            catch (JsonException) { /* skip malformed line */ }
        }

        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]비용 기록이 비어있습니다.[/]");
            return;
        }

        var totalIn = entries.Sum(e => e.InputTokens);
        var totalOut = entries.Sum(e => e.OutputTokens);
        var totalCacheR = entries.Sum(e => e.CacheReadTokens);
        var totalCacheC = entries.Sum(e => e.CacheCreationTokens);
        var totalUsd = entries.Sum(e => e.EstimatedUsd);
        var totalSec = entries.Sum(e => e.DurationSec);

        AnsiConsole.Write(new Rule("[green]Ralph Cost Summary[/]").RuleStyle("blue"));
        AnsiConsole.MarkupLine($"기록 수: [cyan]{entries.Count}[/]개 호출");
        AnsiConsole.MarkupLine($"기간: [cyan]{entries.Min(e => e.TimestampUtc):yyyy-MM-dd HH:mm}[/] ~ [cyan]{entries.Max(e => e.TimestampUtc):yyyy-MM-dd HH:mm}[/] UTC");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("항목");
        table.AddColumn(new TableColumn("값").RightAligned());
        table.AddRow("Input tokens", $"{totalIn:N0}");
        table.AddRow("Output tokens", $"{totalOut:N0}");
        table.AddRow("Cache read tokens", $"{totalCacheR:N0}");
        table.AddRow("Cache creation tokens", $"{totalCacheC:N0}");
        table.AddRow("총 실행 시간", $"{totalSec:N0}초 ({totalSec / 60:F1}분)");
        table.AddRow("[green]추정 비용[/]", $"[green]${totalUsd:F2}[/]");
        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[blue]태스크별 상위 10개 (비용순)[/]").RuleStyle("blue"));

        var byTask = entries
            .GroupBy(e => e.TaskId)
            .Select(g => new
            {
                TaskId = g.Key,
                Calls = g.Count(),
                In = g.Sum(e => e.InputTokens),
                Out = g.Sum(e => e.OutputTokens),
                Usd = g.Sum(e => e.EstimatedUsd),
                Sec = g.Sum(e => e.DurationSec),
            })
            .OrderByDescending(x => x.Usd)
            .Take(10)
            .ToList();

        var taskTable = new Table().Border(TableBorder.Rounded);
        taskTable.AddColumn("TaskId");
        taskTable.AddColumn(new TableColumn("Calls").RightAligned());
        taskTable.AddColumn(new TableColumn("Input").RightAligned());
        taskTable.AddColumn(new TableColumn("Output").RightAligned());
        taskTable.AddColumn(new TableColumn("Sec").RightAligned());
        taskTable.AddColumn(new TableColumn("USD").RightAligned());

        foreach (var row in byTask)
        {
            taskTable.AddRow(
                Markup.Escape(row.TaskId),
                row.Calls.ToString(),
                $"{row.In:N0}",
                $"{row.Out:N0}",
                $"{row.Sec:N0}",
                $"${row.Usd:F3}");
        }
        AnsiConsole.Write(taskTable);

        AnsiConsole.MarkupLine($"\n[dim]전체 기록: {Markup.Escape(LogFilePath)}[/]");
        AnsiConsole.MarkupLine("[dim]단가는 추정값. 실제 청구액은 Anthropic 콘솔을 참조하세요.[/]");
    }
}
