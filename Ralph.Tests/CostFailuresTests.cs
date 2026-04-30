using Ralph.Services;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Ralph.Tests;

[Collection("cost")]
public class CostFailuresTests : IDisposable
{
    private readonly string _tempDir;

    public CostFailuresTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ralph-fail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static ClaudeResult MakeResult() => new()
    {
        Success = true,
        Usage = new TokenUsage(1000, 1000, 0, 0),
        Duration = TimeSpan.FromSeconds(1),
    };

    // cost.jsonl 위치에 디렉토리를 만들어 File.AppendAllTextAsync가 IOException을 던지게 한다.
    // POSIX: EISDIR → IOException / Windows: UnauthorizedAccessException — 둘 다 동작.
    private void BlockCostJsonl()
        => Directory.CreateDirectory(Path.Combine(_tempDir, RalphPaths.CostLedgerFileName));

    /// <summary>cost.jsonl 쓰기 불가 시 cost-failures.jsonl이 생성되고
    /// reason/exception 필드가 채워지는지 확인한다.</summary>
    [Fact]
    public async Task RecordAsync_fallback_creates_cost_failures_jsonl_with_reason_and_exception()
    {
        BlockCostJsonl();
        var cost = new CostTracker(_tempDir);

        await cost.RecordAsync("task1", "opus", MakeResult());

        Assert.True(File.Exists(cost.FailuresLogFilePath),
            $"cost-failures.jsonl 미생성: {cost.FailuresLogFilePath}");

        var nonEmpty = (await File.ReadAllLinesAsync(cost.FailuresLogFilePath))
            .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        Assert.Single(nonEmpty);

        using var doc = JsonDocument.Parse(nonEmpty[0]);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("reason", out var reasonProp), "reason 필드 누락");
        Assert.False(string.IsNullOrEmpty(reasonProp.GetString()), "reason 필드 비어있음");

        Assert.True(root.TryGetProperty("exception", out var exProp), "exception 필드 누락");
        Assert.False(string.IsNullOrEmpty(exProp.GetString()), "exception 필드 비어있음");

        Assert.True(root.TryGetProperty("taskId", out var taskIdProp), "taskId 필드 누락");
        Assert.Equal("task1", taskIdProp.GetString());
    }

    /// <summary>cost.jsonl 쓰기 실패 횟수만큼 FailureCount가 증가한다.</summary>
    [Fact]
    public async Task FailureCount_increments_once_per_failed_write()
    {
        BlockCostJsonl();
        var cost = new CostTracker(_tempDir);

        await cost.RecordAsync("t1", "opus", MakeResult());
        Assert.Equal(1, cost.FailureCount);

        await cost.RecordAsync("t2", "opus", MakeResult());
        Assert.Equal(2, cost.FailureCount);

        await cost.RecordAsync("t3", "sonnet", MakeResult());
        Assert.Equal(3, cost.FailureCount);
    }

    /// <summary>정상 기록 경로에서는 FailureCount가 0이다.</summary>
    [Fact]
    public async Task FailureCount_is_zero_on_successful_writes()
    {
        var cost = new CostTracker(_tempDir);

        await cost.RecordAsync("t1", "opus", MakeResult());
        await cost.RecordAsync("t2", "sonnet", MakeResult());

        Assert.Equal(0, cost.FailureCount);
        Assert.True(File.Exists(cost.LogFilePath), "cost.jsonl 미생성");
    }

    /// <summary>1회 시도 실패 후 200ms 백오프가 적용되어 전체 소요 시간이 ≥ 150ms이다.</summary>
    [Fact]
    public async Task RecordAsync_applies_200ms_backoff_before_second_attempt()
    {
        BlockCostJsonl();
        var cost = new CostTracker(_tempDir);

        var sw = Stopwatch.StartNew();
        await cost.RecordAsync("t1", "opus", MakeResult());
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 150,
            $"200ms 백오프 미적용. 경과: {sw.ElapsedMilliseconds}ms");
    }
}
