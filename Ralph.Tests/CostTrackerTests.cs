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
}
