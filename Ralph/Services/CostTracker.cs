using System.Reflection;
using System.Text.Json;
using Ralph.Models;
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
    /// <summary>true이면 stream-json result에 usage 정보가 누락된 placeholder 기록.</summary>
    public bool UsageMissing { get; set; }
}

public sealed class PricingEntry
{
    public double Input { get; set; }
    public double Output { get; set; }
    public double CacheRead { get; set; }
    public double CacheCreate { get; set; }
}

public sealed class PricingFile
{
    public Dictionary<string, PricingEntry> Models { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Claude Code 호출별 token 사용량과 추정 비용을 .ralph-logs/cost.jsonl에 누적 기록합니다.
/// stream-json의 result 메시지에 포함된 usage 데이터를 파싱해서 사용합니다.
/// 누적 합계는 프로세스 단일 캐시(_cumulativeUsd)에 유지되어 dispatch 마다 jsonl 전체를
/// 다시 read-parse하지 않습니다.
/// </summary>
public class CostTracker
{
    private const string DefaultLogDir = ".ralph-logs";
    private const string LogFileName = "cost.jsonl";
    private static string? _logDirOverride;
    private static string LogDir => _logDirOverride ?? DefaultLogDir;

    // 단가는 EmbeddedResource pricing.json에서 1회 로드. ~/.ralph/pricing.json이 있으면 override.
    private static readonly Dictionary<string, PricingEntry> Pricing = LoadPricing();

    // RalphJsonContext.Default를 chain해 trimming/AOT에서도 reflection fallback 없이 동작.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = RalphJsonContext.Default,
    };

    // 프로세스 단일 누적 캐시. 첫 호출 시 cost.jsonl로부터 hydrate.
    private static readonly SemaphoreSlim HydrateLock = new(1, 1);
    private static readonly object IncrementLock = new();
    // P-CONCURRENCY: 병렬 task가 동시에 RecordAsync를 호출할 때 jsonl 데이터 손실 방지.
    // File.AppendAllTextAsync는 plat별로 동시 open이 sharing violation을 일으킬 수 있고,
    // .NET의 FileStream(FileMode.Append, ..., FileShare.Read)은 다른 writer를 허용하지 않는다.
    // 단일 writer로 직렬화하지 않으면 일부 RecordAsync 호출이 silent fail로 손실됨(검증됨).
    private static readonly SemaphoreSlim WriteLock = new(1, 1);
    private static double _cumulativeUsd;
    private static bool _hydrated;

    public string LogFilePath => Path.Combine(LogDir, LogFileName);

    /// <summary>
    /// 테스트 격리용 — 누적 캐시와 hydrate 플래그를 0으로 리셋합니다.
    /// 프로덕션 코드에서는 호출하지 마세요. 다음 GetTotalUsdAsync/RecordAsync 시 다시 hydrate됩니다.
    /// </summary>
    internal static void ResetForTesting()
    {
        lock (IncrementLock)
        {
            _cumulativeUsd = 0.0;
            _hydrated = false;
        }
    }

    /// <summary>
    /// 테스트 격리용 — cost.jsonl 위치를 임시 디렉터리로 override합니다. null 전달 시 기본값으로 복귀.
    /// </summary>
    internal static void SetLogDirForTesting(string? path) => _logDirOverride = path;

    // P1-2: hung 디스크에서 finally 블록이 무한정 막히지 않도록 timeout 가드.
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 호출 결과를 jsonl에 1줄 기록하고 누적 캐시를 갱신합니다. result가 null이거나
    /// usage 정보가 없으면 placeholder(estimatedUsd=0, usageMissing=true)로 기록 + 경고합니다.
    /// 디스크 IO가 5초 안에 끝나지 않으면 기록을 포기하고 경고만 남깁니다(graceful shutdown 보장).
    /// 동시 호출은 WriteLock으로 직렬화되어 jsonl 라인 손실/손상이 발생하지 않습니다.
    /// </summary>
    public async Task RecordAsync(
        string taskId, string model, ClaudeResult? result, CancellationToken ct = default)
    {
        // timeout만 적용 (호출자 ct는 보통 None — finally에서 호출). 예외는 아래 catch로.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(WriteTimeout);
        try
        {
            await RecordInnerAsync(taskId, model, result, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]⚠ cost 기록 timeout (taskId={Markup.Escape(taskId)}, >{WriteTimeout.TotalSeconds:F0}s). 누적 비용 추적이 부정확할 수 있습니다.[/]");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]⚠ cost 기록 실패 (taskId={Markup.Escape(taskId)}): {Markup.Escape(ex.Message)}[/]");
        }
    }

    private async Task RecordInnerAsync(
        string taskId, string model, ClaudeResult? result, CancellationToken ct)
    {
        await EnsureHydratedAsync(ct);
        Directory.CreateDirectory(LogDir);

        if (result?.Usage == null)
        {
            // P0-2: usage 누락은 silent miss하지 않고 placeholder 기록
            var placeholder = new CostEntry
            {
                TaskId = taskId,
                Model = model,
                TimestampUtc = DateTime.UtcNow,
                EstimatedUsd = 0.0,
                DurationSec = result?.Duration.TotalSeconds ?? 0.0,
                UsageMissing = true,
            };
            var ph = JsonSerializer.Serialize(placeholder, JsonOpts) + "\n";
            await WriteLock.WaitAsync(ct);
            try { await File.AppendAllTextAsync(LogFilePath, ph, ct); }
            finally { WriteLock.Release(); }
            AnsiConsole.MarkupLine(
                $"[yellow]⚠ usage 누락 (taskId={Markup.Escape(taskId)}, " +
                $"exit={result?.ExitCode.ToString() ?? "?"}). 비용 추정 0 처리.[/]");
            return;
        }

        var u = result.Usage;
        var estimated = EstimateUsd(model, u);
        var entry = new CostEntry
        {
            TaskId = taskId,
            Model = model,
            TimestampUtc = DateTime.UtcNow,
            InputTokens = u.InputTokens,
            OutputTokens = u.OutputTokens,
            CacheReadTokens = u.CacheReadTokens,
            CacheCreationTokens = u.CacheCreationTokens,
            EstimatedUsd = estimated,
            DurationSec = result.Duration.TotalSeconds,
        };

        var line = JsonSerializer.Serialize(entry, JsonOpts) + "\n";

        // file write와 누적 캐시 갱신을 한 critical section으로 묶어 직렬화.
        // 동시 file open(FileMode.Append)에서 발생하는 sharing violation/데이터 손실 방지.
        await WriteLock.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(LogFilePath, line, ct);
            lock (IncrementLock) _cumulativeUsd += estimated;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public static double EstimateUsd(string model, TokenUsage u)
    {
        var key = NormalizeModel(model);
        if (!Pricing.TryGetValue(key, out var p))
            return 0.0;
        return (u.InputTokens * p.Input
                + u.OutputTokens * p.Output
                + u.CacheReadTokens * p.CacheRead
                + u.CacheCreationTokens * p.CacheCreate) / 1_000_000.0;
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

    private static Dictionary<string, PricingEntry> LoadPricing()
    {
        var caseInsensitive = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            TypeInfoResolver = RalphJsonContext.Default,
        };

        // 1) 사용자 override (~/.ralph/pricing.json)
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var userPath = Path.Combine(home, ".ralph", "pricing.json");
            if (File.Exists(userPath))
            {
                var json = File.ReadAllText(userPath);
                var pf = JsonSerializer.Deserialize<PricingFile>(json, caseInsensitive);
                if (pf?.Models is { Count: > 0 })
                    return new Dictionary<string, PricingEntry>(pf.Models, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { /* fallback to embedded */ }

        // 2) Embedded pricing.json
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("pricing.json");
            if (stream != null)
            {
                using var sr = new StreamReader(stream);
                var json = sr.ReadToEnd();
                var pf = JsonSerializer.Deserialize<PricingFile>(json, caseInsensitive);
                if (pf?.Models is { Count: > 0 })
                    return new Dictionary<string, PricingEntry>(pf.Models, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { /* hardcoded fallback */ }

        // 3) Hardcoded fallback (embedded resource 누락 시)
        return new Dictionary<string, PricingEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["opus"]   = new() { Input = 15.0, Output = 75.0, CacheRead = 1.5,  CacheCreate = 18.75 },
            ["sonnet"] = new() { Input = 3.0,  Output = 15.0, CacheRead = 0.30, CacheCreate = 3.75 },
            ["haiku"]  = new() { Input = 0.80, Output = 4.0,  CacheRead = 0.08, CacheCreate = 1.0 },
        };
    }

    private async Task EnsureHydratedAsync(CancellationToken ct)
    {
        if (_hydrated) return;
        await HydrateLock.WaitAsync(ct);
        try
        {
            if (_hydrated) return;
            _cumulativeUsd = await ReadTotalFromDiskAsync(ct);
            _hydrated = true;
        }
        finally
        {
            HydrateLock.Release();
        }
    }

    private async Task<double> ReadTotalFromDiskAsync(CancellationToken ct)
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
    /// 누적 비용(USD)을 반환합니다. 캐시된 값을 사용해 jsonl을 매번 재읽지 않습니다.
    /// </summary>
    public async Task<double> GetTotalUsdAsync(CancellationToken ct = default)
    {
        await EnsureHydratedAsync(ct);
        lock (IncrementLock) return _cumulativeUsd;
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
        var missingCount = entries.Count(e => e.UsageMissing);

        AnsiConsole.Write(new Rule("[green]Ralph Cost Summary[/]").RuleStyle("blue"));
        AnsiConsole.MarkupLine($"기록 수: [cyan]{entries.Count}[/]개 호출");
        AnsiConsole.MarkupLine($"기간: [cyan]{entries.Min(e => e.TimestampUtc):yyyy-MM-dd HH:mm}[/] ~ [cyan]{entries.Max(e => e.TimestampUtc):yyyy-MM-dd HH:mm}[/] UTC");
        if (missingCount > 0)
            AnsiConsole.MarkupLine(
                $"[yellow]usage 누락 placeholder: {missingCount}개[/] (실제 토큰은 추정 비용에 반영되지 않음)");
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
