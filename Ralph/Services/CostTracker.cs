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
    /// <summary>stdin으로 전송한 prompt의 UTF-8 바이트 수. 0이면 미기록(구버전 항목).</summary>
    public long PromptBytes { get; set; }
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
/// 누적 합계는 인스턴스 단위 캐시에 유지되어 dispatch 마다 jsonl 전체를
/// 다시 read-parse하지 않습니다. 동일 세션은 CommandContext.Cost가 단일 인스턴스를 공유합니다.
/// </summary>
public sealed class CostTracker
{
    // 단가는 EmbeddedResource pricing.json에서 1회 로드. ~/.ralph/pricing.json이 있으면 override.
    // 인스턴스 무관 불변 데이터이므로 static readonly 유지 (동일 단가, 메모리 절약).
    private static readonly Dictionary<string, PricingEntry> Pricing = LoadPricing();

    // RalphJsonContext.Default를 chain해 trimming/AOT에서도 reflection fallback 없이 동작.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = RalphJsonContext.Default,
    };

    // P1-2: hung 디스크에서 finally 블록이 무한정 막히지 않도록 timeout 가드.
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    // 레거시 테스트 호환용: SetLogDirForTesting이 설정한 ambient override.
    // 신규 코드는 생성자 인자로 logDir을 명시 — fix1-test 태스크가 모든 호출지를 옮기면 제거 예정.
    private static string? _logDirOverride;

    private readonly string _logDir;

    // 인스턴스 단위 누적 캐시. 첫 호출 시 cost.jsonl로부터 hydrate.
    private readonly SemaphoreSlim _hydrateLock = new(1, 1);
    private readonly object _incrementLock = new();
    // P-CONCURRENCY: 동일 인스턴스에서 병렬 RecordAsync 호출 시 jsonl 라인 손실 방지.
    // File.AppendAllTextAsync는 plat별로 동시 open이 sharing violation을 일으킬 수 있고,
    // .NET의 FileStream(FileMode.Append, ..., FileShare.Read)은 다른 writer를 허용하지 않는다.
    // 단일 writer로 직렬화하지 않으면 일부 RecordAsync 호출이 silent fail로 손실됨(검증됨).
    // 다른 인스턴스가 같은 logDir에 동시 append하는 시나리오는 §7.4 잔여 위험으로 추적.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private double _cumulativeUsd;
    private bool _hydrated;

    /// <summary>
    /// 단일 CostTracker 인스턴스 생성. logDir이 null이면 SetLogDirForTesting override를 거쳐
    /// 최종적으로 RalphPaths.LogDir로 fallback. 한 세션은 CommandContext.Cost로 인스턴스를 공유한다.
    /// </summary>
    public CostTracker(string? logDir = null)
    {
        _logDir = logDir ?? _logDirOverride ?? RalphPaths.LogDir;
    }

    public string LogFilePath => Path.Combine(_logDir, RalphPaths.CostLedgerFileName);

    /// <summary>
    /// 레거시 테스트 호환용 — 인스턴스 누적 캐시는 인스턴스 단위이므로 이 호출은 no-op이다.
    /// 신규 테스트는 <c>new CostTracker(tempDir)</c>로 직접 격리하라. fix1-test 태스크에서 제거된다.
    /// </summary>
    [Obsolete("인스턴스 재생성으로 대체. fix1-test 태스크에서 제거 예정.")]
    internal static void ResetForTesting() { /* no-op: 누적 캐시는 인스턴스 필드. */ }

    /// <summary>
    /// 레거시 테스트 호환용 — 인자 없는 <c>new CostTracker()</c> 호출이 사용할 ambient logDir override.
    /// 신규 코드는 생성자 인자로 logDir을 명시하라. fix1-test 태스크에서 제거된다.
    /// </summary>
    [Obsolete("CostTracker(logDir) 생성자 인자로 대체. fix1-test 태스크에서 제거 예정.")]
    internal static void SetLogDirForTesting(string? path) => _logDirOverride = path;

    /// <summary>
    /// 호출 결과를 jsonl에 1줄 기록하고 누적 캐시를 갱신합니다. result가 null이거나
    /// usage 정보가 없으면 placeholder(estimatedUsd=0, usageMissing=true)로 기록 + 경고합니다.
    /// 디스크 IO가 5초 안에 끝나지 않으면 기록을 포기하고 경고만 남깁니다(graceful shutdown 보장).
    /// 동시 호출은 인스턴스 _writeLock으로 직렬화되어 jsonl 라인 손실/손상이 발생하지 않습니다.
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
        Directory.CreateDirectory(_logDir);

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
                PromptBytes = result?.PromptBytes ?? 0,
            };
            var ph = JsonSerializer.Serialize(placeholder, JsonOpts) + "\n";
            await _writeLock.WaitAsync(ct);
            try { await File.AppendAllTextAsync(LogFilePath, ph, ct); }
            finally { _writeLock.Release(); }
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
            PromptBytes = result.PromptBytes,
        };

        var line = JsonSerializer.Serialize(entry, JsonOpts) + "\n";

        // file write와 누적 캐시 갱신을 한 critical section으로 묶어 직렬화.
        // 동시 file open(FileMode.Append)에서 발생하는 sharing violation/데이터 손실 방지.
        await _writeLock.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(LogFilePath, line, ct);
            lock (_incrementLock) _cumulativeUsd += estimated;
        }
        finally
        {
            _writeLock.Release();
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

    internal static string NormalizeModel(string model)
        => NormalizeModel(model, Pricing);

    internal static string NormalizeModel(
        string model, IReadOnlyDictionary<string, PricingEntry> pricing)
    {
        if (string.IsNullOrEmpty(model)) return "opus";
        var lower = model.ToLowerInvariant();

        if (pricing.Count > 0)
        {
            string? best = null;
            foreach (var key in pricing.Keys)
            {
                var keyLower = key.ToLowerInvariant();
                if (lower.Contains(keyLower)
                    && (best is null || keyLower.Length > best.Length))
                {
                    best = keyLower;
                }
            }
            if (best is not null) return best;
            return lower;
        }

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
        await _hydrateLock.WaitAsync(ct);
        try
        {
            if (_hydrated) return;
            _cumulativeUsd = await ReadTotalFromDiskAsync(ct);
            _hydrated = true;
        }
        finally
        {
            _hydrateLock.Release();
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
        lock (_incrementLock) return _cumulativeUsd;
    }

    /// <summary>
    /// 누적 cost.jsonl을 읽어 요약 출력합니다.
    /// output이 null이면 Console.Out, 아니면 주입된 TextWriter로 렌더링합니다(테스트용).
    /// </summary>
    public async Task PrintSummaryAsync(CancellationToken ct = default, TextWriter? output = null)
    {
        // output 주입 시에는 색상/ANSI 비활성화 — StringWriter로 캡처 가능한 평문 출력.
        var console = output is null
            ? AnsiConsole.Console
            : AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(output),
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Interactive = InteractionSupport.No,
            });

        if (!File.Exists(LogFilePath))
        {
            console.MarkupLine("[yellow]비용 기록이 없습니다 (.ralph-logs/cost.jsonl이 없습니다).[/]");
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
            console.MarkupLine("[yellow]비용 기록이 비어있습니다.[/]");
            return;
        }

        var conflictEntries = entries.Where(e => e.TaskId.StartsWith("conflict:")).ToList();
        var normalEntries = entries.Where(e => !e.TaskId.StartsWith("conflict:")).ToList();

        var totalIn = entries.Sum(e => e.InputTokens);
        var totalOut = entries.Sum(e => e.OutputTokens);
        var totalCacheR = entries.Sum(e => e.CacheReadTokens);
        var totalCacheC = entries.Sum(e => e.CacheCreationTokens);
        var totalUsd = entries.Sum(e => e.EstimatedUsd);
        var totalSec = entries.Sum(e => e.DurationSec);
        var missingCount = entries.Count(e => e.UsageMissing);

        console.Write(new Rule("[green]Ralph Cost Summary[/]").RuleStyle("blue"));
        console.MarkupLine($"기록 수: [cyan]{entries.Count}[/]개 호출");
        console.MarkupLine($"기간: [cyan]{entries.Min(e => e.TimestampUtc):yyyy-MM-dd HH:mm}[/] ~ [cyan]{entries.Max(e => e.TimestampUtc):yyyy-MM-dd HH:mm}[/] UTC");
        if (missingCount > 0)
            console.MarkupLine(
                $"[yellow]usage 누락 placeholder: {missingCount}개[/] (실제 토큰은 추정 비용에 반영되지 않음)");
        console.WriteLine();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("항목");
        table.AddColumn(new TableColumn("값").RightAligned());
        table.AddRow("Input tokens", $"{totalIn:N0}");
        table.AddRow("Output tokens", $"{totalOut:N0}");
        table.AddRow("Cache read tokens", $"{totalCacheR:N0}");
        table.AddRow("Cache creation tokens", $"{totalCacheC:N0}");
        table.AddRow("총 실행 시간", $"{totalSec:N0}초 ({totalSec / 60:F1}분)");
        table.AddRow("[green]추정 비용[/]", $"[green]${totalUsd:F2}[/]");
        console.Write(table);

        if (conflictEntries.Count > 0)
        {
            var conflictIn = conflictEntries.Sum(e => e.InputTokens);
            var conflictOut = conflictEntries.Sum(e => e.OutputTokens);
            var conflictUsd = conflictEntries.Sum(e => e.EstimatedUsd);
            var conflictAvg = conflictUsd / conflictEntries.Count;

            console.WriteLine();
            console.Write(new Rule("[red]충돌 해결 비용[/]").RuleStyle("red"));

            var conflictTable = new Table().Border(TableBorder.Rounded);
            conflictTable.AddColumn("항목");
            conflictTable.AddColumn(new TableColumn("값").RightAligned());
            conflictTable.AddRow("호출 수", $"{conflictEntries.Count:N0}");
            conflictTable.AddRow("Input tokens", $"{conflictIn:N0}");
            conflictTable.AddRow("Output tokens", $"{conflictOut:N0}");
            conflictTable.AddRow("USD 합계", $"${conflictUsd:F3}");
            conflictTable.AddRow("평균 USD", $"${conflictAvg:F3}");
            console.Write(conflictTable);
        }

        console.WriteLine();
        console.Write(new Rule("[blue]태스크별 상위 10개 (비용순)[/]").RuleStyle("blue"));

        var byTask = normalEntries
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
        console.Write(taskTable);

        console.MarkupLine($"\n[dim]전체 기록: {Markup.Escape(LogFilePath)}[/]");
        console.MarkupLine("[dim]단가는 추정값. 실제 청구액은 Anthropic 콘솔을 참조하세요.[/]");
    }
}
