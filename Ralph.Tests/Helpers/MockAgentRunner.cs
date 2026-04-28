using Ralph.Services;

namespace Ralph.Tests.Helpers;

/// <summary>
/// IAgentRunner의 단위 테스트용 mock 구현. 콜백을 통해 prompt → ClaudeResult 매핑을 주입한다.
/// </summary>
public class MockAgentRunner : IAgentRunner
{
    private readonly Func<string, Task<ClaudeResult>> _callback;
    private readonly List<string> _prompts = new();

    public MockAgentRunner(Func<string, ClaudeResult> callback)
        : this(prompt => Task.FromResult(callback(prompt)))
    {
    }

    public MockAgentRunner(Func<string, Task<ClaudeResult>> callback)
    {
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    public bool Debug { get; set; }
    public int? TaskTimeoutSec { get; set; }

    public int CallCount { get; private set; }
    public string? LastPrompt { get; private set; }
    public IReadOnlyList<string> Prompts => _prompts;

    public Task<ClaudeResult> RunStreamAsync(
        string prompt,
        string? model = null,
        string? workingDirectory = null,
        RalphLogger? logger = null,
        TextWriter? output = null,
        CancellationToken ct = default,
        string? allowedTools = null)
    {
        CallCount++;
        LastPrompt = prompt;
        _prompts.Add(prompt);
        return _callback(prompt);
    }

    public Task<ClaudeResult> RunWithRetryAsync(
        string prompt,
        string? model = null,
        string? workingDirectory = null,
        RalphLogger? logger = null,
        TextWriter? output = null,
        CancellationToken ct = default,
        Func<ClaudeResult, string?>? buildRetryContext = null,
        string? allowedTools = null)
    {
        return RunStreamAsync(prompt, model, workingDirectory, logger, output, ct, allowedTools);
    }
}
