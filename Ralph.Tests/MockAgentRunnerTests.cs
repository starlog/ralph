using Ralph.Services;
using Ralph.Tests.Helpers;
using Xunit;

namespace Ralph.Tests;

public class MockAgentRunnerTests
{
    [Fact]
    public async Task Returns_Injected_Result()
    {
        var injected = new ClaudeResult
        {
            Success = true,
            Output = "hello-world",
            ExitCode = 0,
        };
        var runner = new MockAgentRunner(_ => injected);

        var result = await runner.RunStreamAsync("any prompt");

        Assert.Same(injected, result);
        Assert.True(result.Success);
        Assert.Equal("hello-world", result.Output);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RunWithRetry_Increments_CallCount()
    {
        var runner = new MockAgentRunner(_ => new ClaudeResult { Success = true });

        await runner.RunWithRetryAsync("prompt-1");

        Assert.Equal(1, runner.CallCount);
    }

    [Fact]
    public async Task Last_Prompt_Is_Captured()
    {
        var runner = new MockAgentRunner(_ => new ClaudeResult { Success = true });

        await runner.RunStreamAsync("first");
        await runner.RunWithRetryAsync("second");

        Assert.Equal("second", runner.LastPrompt);
        Assert.Equal(2, runner.CallCount);
        Assert.Equal(new[] { "first", "second" }, runner.Prompts);
    }
}
