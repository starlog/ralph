using Ralph.Services;
using Xunit;
using Xunit.Abstractions;

namespace Ralph.Tests;

[Collection("cost")]
public class CostTrackerConcurrencyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITestOutputHelper _output;

    public CostTrackerConcurrencyTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), $"ralph-conc-{Guid.NewGuid():N}");
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

    /// <summary>
    /// 사용자 의심 시나리오: 병렬 RecordAsync 다수 호출 시 cost.jsonl 라인이 인터리빙되어
    /// 파싱 깨짐 → GetTotalUsdAsync 누락 → budget gate 부정확.
    /// 100 parallel writers × 5 records each = 500 lines 기대.
    /// </summary>
    [Fact]
    public async Task Parallel_record_does_not_corrupt_jsonl()
    {
        const int writers = 100;
        const int perWriter = 5;
        const int expected = writers * perWriter;

        var usage = new TokenUsage(1000, 1000, 0, 0); // small entry per call

        // 단일 CostTracker 인스턴스를 모든 writer가 공유 — 프로덕션 동작 (CommandContext.Cost)과 일치.
        // 인스턴스별 _writeLock이 라인 손실을 막는 단일 직렬화 지점이다.
        var cost = new CostTracker();

        var tasks = Enumerable.Range(0, writers).Select(async i =>
        {
            for (var j = 0; j < perWriter; j++)
            {
                var result = new ClaudeResult
                {
                    Success = true,
                    Usage = usage,
                    Duration = TimeSpan.FromMilliseconds(1),
                };
                await cost.RecordAsync($"task-{i}-{j}", "opus", result);
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        var path = new CostTracker().LogFilePath;
        Assert.True(File.Exists(path), $"cost.jsonl 미생성: {path}");

        var lines = await File.ReadAllLinesAsync(path);
        var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

        _output.WriteLine($"기대 라인: {expected}, 실제 라인: {nonEmpty.Count}");

        // 라인 수가 정확해야 함 (인터리빙으로 라인이 쪼개지면 더 많아짐)
        Assert.Equal(expected, nonEmpty.Count);

        // 모든 라인이 완전한 JSON으로 파싱되어야 함
        var parsedCount = 0;
        var firstError = "";
        foreach (var line in nonEmpty)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                parsedCount++;
            }
            catch (System.Text.Json.JsonException ex)
            {
                if (string.IsNullOrEmpty(firstError))
                    firstError = $"line: {line.Substring(0, Math.Min(80, line.Length))}... err: {ex.Message}";
            }
        }
        Assert.True(parsedCount == nonEmpty.Count,
            $"파싱 실패 {nonEmpty.Count - parsedCount}건. 첫 에러: {firstError}");
    }
}
