using Ralph.Models;
using Xunit;

namespace Ralph.Tests;

public class ParallelSettingsTests
{
    [Fact]
    public void Strategies_array_takes_priority_over_single_strategy()
    {
        var p = new ParallelSettings
        {
            ConflictStrategy = "abort",
            ConflictStrategies = new() { "auto-theirs", "claude" },
        };
        Assert.Equal(new[] { "auto-theirs", "claude" }, p.GetStrategyChain());
    }

    [Fact]
    public void Falls_back_to_single_strategy_when_array_null()
    {
        var p = new ParallelSettings
        {
            ConflictStrategy = "abort",
            ConflictStrategies = null,
        };
        Assert.Equal(new[] { "abort" }, p.GetStrategyChain());
    }

    [Fact]
    public void Falls_back_to_single_strategy_when_array_empty()
    {
        var p = new ParallelSettings
        {
            ConflictStrategy = "claude",
            ConflictStrategies = new(),
        };
        Assert.Equal(new[] { "claude" }, p.GetStrategyChain());
    }

    [Fact]
    public void Default_settings_yield_single_claude_chain()
    {
        // ParallelSettings 기본값: ConflictStrategy="claude", ConflictStrategies=null
        var p = new ParallelSettings();
        Assert.Equal(new[] { "claude" }, p.GetStrategyChain());
    }

    [Fact]
    public void Empty_single_strategy_falls_back_to_claude_default()
    {
        var p = new ParallelSettings { ConflictStrategy = "" };
        Assert.Equal(new[] { "claude" }, p.GetStrategyChain());
    }

    [Fact]
    public void Whitespace_single_strategy_falls_back_to_claude_default()
    {
        var p = new ParallelSettings { ConflictStrategy = "   " };
        Assert.Equal(new[] { "claude" }, p.GetStrategyChain());
    }

    [Fact]
    public void Single_element_array_works()
    {
        var p = new ParallelSettings
        {
            ConflictStrategies = new() { "auto-theirs" },
        };
        Assert.Equal(new[] { "auto-theirs" }, p.GetStrategyChain());
    }

    [Fact]
    public void Multi_element_array_preserves_order()
    {
        var p = new ParallelSettings
        {
            ConflictStrategies = new() { "auto-theirs", "auto-ours", "claude", "abort" },
        };
        var chain = p.GetStrategyChain();
        Assert.Equal(4, chain.Count);
        Assert.Equal("auto-theirs", chain[0]);
        Assert.Equal("auto-ours", chain[1]);
        Assert.Equal("claude", chain[2]);
        Assert.Equal("abort", chain[3]);
    }
}
