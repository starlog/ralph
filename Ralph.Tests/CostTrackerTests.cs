using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

[Collection("cost")]
public class CostTrackerTests : IDisposable
{
    private readonly string _tempDir;

    public CostTrackerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ralph-cost-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        CostTracker.SetLogDirForTesting(_tempDir);
        CostTracker.ResetForTesting();
    }

    public void Dispose()
    {
        CostTracker.ResetForTesting();
        CostTracker.SetLogDirForTesting(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task RecordAsync_with_null_result_writes_placeholder_and_does_not_increment()
    {
        var cost = new CostTracker();
        await cost.RecordAsync("missing-task", "opus", result: null);

        var total = await cost.GetTotalUsdAsync();
        Assert.Equal(0.0, total);
        Assert.True(File.Exists(cost.LogFilePath));
        var line = (await File.ReadAllLinesAsync(cost.LogFilePath)).Last();
        Assert.Contains("\"usageMissing\":true", line);
    }

    [Fact]
    public async Task RecordAsync_with_usage_increments_cumulative_total()
    {
        var cost = new CostTracker();
        var usage = new TokenUsage(1_000_000, 1_000_000, 0, 0); // opus: $15 + $75 = $90
        var result = new ClaudeResult { Success = true, Usage = usage, Duration = TimeSpan.FromSeconds(2) };
        await cost.RecordAsync("t1", "opus", result);

        var total = await cost.GetTotalUsdAsync();
        Assert.Equal(90.0, total, 4);
    }

    [Fact]
    public static void EstimateUsd_unknown_model_returns_zero()
    {
        var u = new TokenUsage(1000, 1000, 0, 0);
        Assert.Equal(0.0, CostTracker.EstimateUsd("nonexistent-model-2099", u));
    }

    [Theory]
    // pricing.json: opus(15/75/1.5/18.75), sonnet(3/15/0.30/3.75), haiku(0.80/4/0.08/1.0) per 1M
    [InlineData("opus", 1_000_000, 0, 0, 0, 15.0)]               // input only
    [InlineData("opus", 0, 1_000_000, 0, 0, 75.0)]              // output only
    [InlineData("opus", 0, 0, 1_000_000, 0, 1.5)]               // cache read
    [InlineData("opus", 0, 0, 0, 1_000_000, 18.75)]             // cache create
    [InlineData("sonnet", 1_000_000, 1_000_000, 0, 0, 18.0)]    // 3 + 15
    [InlineData("haiku", 1_000_000, 1_000_000, 0, 0, 4.8)]      // 0.80 + 4.0
    [InlineData("opus-4-7", 1_000_000, 0, 0, 0, 15.0)]          // 모델 변형 → opus 매칭
    [InlineData("claude-sonnet-4-5", 0, 1_000_000, 0, 0, 15.0)] // sonnet 매칭
    public static void EstimateUsd_known_models_apply_correct_pricing(
        string model, long input, long output, long cacheRead, long cacheCreate, double expected)
    {
        var u = new TokenUsage(input, output, cacheRead, cacheCreate);
        Assert.Equal(expected, CostTracker.EstimateUsd(model, u), 4);
    }

    [Fact]
    public static void EstimateUsd_zero_tokens_returns_zero()
    {
        var u = new TokenUsage(0, 0, 0, 0);
        Assert.Equal(0.0, CostTracker.EstimateUsd("opus", u));
    }

    [Theory]
    [InlineData("claude-opus-4-7", "opus-4-7")]
    [InlineData("claude-opus-4-7-20251101", "opus-4-7")]
    [InlineData("claude-opus-4", "opus-4")]
    [InlineData("claude-opus", "opus")]
    [InlineData("claude-sonnet-4-6", "sonnet-4-6")]
    [InlineData("claude-sonnet-4", "sonnet-4")]
    [InlineData("claude-haiku-4-5", "haiku-4-5")]
    [InlineData("", "opus")]
    [InlineData("totally-unknown-model-x", "totally-unknown-model-x")]
    [InlineData("CLAUDE-OPUS-4-7", "opus-4-7")]
    public static void NormalizeModel_returns_longest_matching_pricing_key(
        string input, string expected)
    {
        Assert.Equal(expected, CostTracker.NormalizeModel(input));
    }

    [Fact]
    public static void NormalizeModel_with_empty_pricing_falls_back_to_family_fold()
    {
        var empty = new Dictionary<string, PricingEntry>();
        Assert.Equal("opus", CostTracker.NormalizeModel("claude-opus-4-7", empty));
        Assert.Equal("sonnet", CostTracker.NormalizeModel("claude-sonnet-4", empty));
        Assert.Equal("haiku", CostTracker.NormalizeModel("haiku-4-5", empty));
        Assert.Equal("opus", CostTracker.NormalizeModel("", empty));
        Assert.Equal("foo-bar", CostTracker.NormalizeModel("foo-bar", empty));
    }

    [Fact]
    public async Task PrintSummary_separates_conflict_section_when_conflict_entries_exist()
    {
        var cost = new CostTracker();
        var usage = new TokenUsage(1_000_000, 1_000_000, 0, 0); // opus: $90 per call
        var result = new ClaudeResult { Success = true, Usage = usage, Duration = TimeSpan.FromSeconds(1) };

        await cost.RecordAsync("foo", "opus", result);
        await cost.RecordAsync("conflict:foo", "opus", result);
        await cost.RecordAsync("conflict:foo", "opus", result);

        var sw = new StringWriter();
        await cost.PrintSummaryAsync(CancellationToken.None, sw);
        var output = sw.ToString();

        Assert.Contains("충돌 해결 비용", output);
        Assert.Contains("호출 수", output);
        Assert.Matches(@"호출 수\s*\D*2", output); // 2건 표기 확인
    }

    [Fact]
    public async Task PrintSummary_excludes_conflict_rows_from_top_task_table()
    {
        var cost = new CostTracker();
        var usage = new TokenUsage(1_000_000, 1_000_000, 0, 0);
        var result = new ClaudeResult { Success = true, Usage = usage, Duration = TimeSpan.FromSeconds(1) };

        await cost.RecordAsync("foo", "opus", result);
        await cost.RecordAsync("conflict:foo", "opus", result);
        await cost.RecordAsync("conflict:foo", "opus", result);

        var sw = new StringWriter();
        await cost.PrintSummaryAsync(CancellationToken.None, sw);
        var output = sw.ToString();

        // 상위 10개 표가 시작된 이후 영역에는 conflict:foo가 등장하면 안됨.
        var topIdx = output.IndexOf("태스크별 상위 10개", StringComparison.Ordinal);
        Assert.True(topIdx >= 0, "태스크별 상위 10개 섹션이 없습니다.");
        var topSection = output[topIdx..];
        Assert.DoesNotContain("conflict:foo", topSection);
        Assert.Contains("foo", topSection); // 일반 태스크는 표에 포함
    }

    [Fact]
    public async Task PrintSummary_omits_conflict_section_when_no_conflict_entries()
    {
        var cost = new CostTracker();
        var usage = new TokenUsage(1_000_000, 1_000_000, 0, 0);
        var result = new ClaudeResult { Success = true, Usage = usage, Duration = TimeSpan.FromSeconds(1) };

        await cost.RecordAsync("foo", "opus", result);
        await cost.RecordAsync("bar", "opus", result);

        var sw = new StringWriter();
        await cost.PrintSummaryAsync(CancellationToken.None, sw);
        var output = sw.ToString();

        Assert.DoesNotContain("충돌 해결 비용", output);
    }
}
