using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

[Collection("cost")]
public class BudgetGateTests : IDisposable
{
    private readonly string _tempDir;

    public BudgetGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ralph-budget-{Guid.NewGuid():N}");
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
    public async Task Null_or_zero_or_negative_budget_always_passes()
    {
        var cost = new CostTracker();
        Assert.True(await new BudgetGate(null, cost).CheckAsync());
        Assert.True(await new BudgetGate(0.0, cost).CheckAsync());
        Assert.True(await new BudgetGate(-5.0, cost).CheckAsync());
    }

    [Fact]
    public async Task Below_threshold_passes_without_marking_reached()
    {
        var cost = new CostTracker();
        var gate = new BudgetGate(100.0, cost);
        Assert.True(await gate.CheckAsync());
        Assert.False(gate.Reached);
    }

    [Fact]
    public async Task At_or_above_budget_blocks_and_sets_reached()
    {
        var cost = new CostTracker();
        // opus input 단가 $15/1M → 1M tokens = $15.00. budget $10이면 100% 초과.
        var fakeUsage = new TokenUsage(1_000_000, 0, 0, 0);
        var fakeResult = new ClaudeResult { Success = true, Usage = fakeUsage, Duration = TimeSpan.FromSeconds(1) };
        await cost.RecordAsync("test", "opus", fakeResult);

        var gate = new BudgetGate(10.0, cost);
        Assert.False(await gate.CheckAsync());
        Assert.True(gate.Reached);
    }
}
