using System.Text.Json;
using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

public class RateLimitBackoffTests
{
    // ---------- ComputeRateLimitBackoffSec ----------

    [Theory]
    [InlineData(2, 0.0, 30)]   // attempt=2 base=60, jitter 0.5 → 30s
    [InlineData(2, 0.5, 60)]   // jitter 1.0 → 60s
    [InlineData(2, 0.999, 90)] // jitter ~1.5 → ~90s
    [InlineData(3, 0.0, 60)]   // attempt=3 base=120, jitter 0.5 → 60s
    [InlineData(3, 0.999, 180)]// jitter ~1.5 → ~180s
    [InlineData(5, 0.0, 240)]  // attempt=5 base=480, jitter 0.5 → 240s
    [InlineData(6, 0.0, 300)]  // attempt=6 base would be 1920, capped at 600 → 300s
    [InlineData(10, 0.999, 600)] // capped at 600 even after jitter
    public void Backoff_Without_RetryAfter_Uses_Exponential_With_Jitter(int attempt, double rng, int expectedSec)
    {
        var got = ClaudeService.ComputeRateLimitBackoffSec(attempt, retryAfterSec: null, rng: () => rng);
        // ±1초 tolerance — Math.Round 경계 케이스
        Assert.InRange(got, expectedSec - 1, expectedSec + 1);
    }

    [Theory]
    [InlineData(15, 0.0, 8)]    // server says 15s, jitter 0.5 → ~8s
    [InlineData(15, 0.5, 15)]
    [InlineData(15, 0.999, 22)] // ~22.5s → 23 또는 22
    [InlineData(900, 0.5, 600)] // server says 900s, capped at 600
    public void Backoff_With_RetryAfter_Uses_Server_Value_With_Jitter(int retryAfter, double rng, int expectedSec)
    {
        var got = ClaudeService.ComputeRateLimitBackoffSec(attempt: 2, retryAfterSec: retryAfter, rng: () => rng);
        Assert.InRange(got, expectedSec - 1, expectedSec + 1);
    }

    [Fact]
    public void Backoff_Result_Is_Always_Within_1_To_600()
    {
        // 무작위 1만회 sweep — clamp가 부서지면 즉시 잡힘
        var rng = new Random(42);
        for (var i = 0; i < 10_000; i++)
        {
            var attempt = rng.Next(1, 12);
            int? retryAfter = i % 3 == 0 ? rng.Next(1, 1000) : null;
            var d = ClaudeService.ComputeRateLimitBackoffSec(attempt, retryAfter, rng: rng.NextDouble);
            Assert.InRange(d, 1, 600);
        }
    }

    [Fact]
    public void Jitter_Spreads_Concurrent_Backoffs()
    {
        // 5 task가 동시에 같은 attempt로 backoff 들어갔을 때, jitter가 실제로 분산을 만드는지.
        var rng = new Random(123);
        var samples = new int[5];
        for (var i = 0; i < 5; i++)
            samples[i] = ClaudeService.ComputeRateLimitBackoffSec(attempt: 2, retryAfterSec: null, rng: rng.NextDouble);

        // 5개 다 동일하면 jitter가 안 먹은 것 — 분산이 0보다 커야 한다.
        Assert.True(samples.Distinct().Count() > 1, $"expected spread, got [{string.Join(",", samples)}]");
        // 모두 [30, 90] 안에 있어야 한다(base=60, jitter [0.5, 1.5)).
        Assert.All(samples, s => Assert.InRange(s, 30, 90));
    }

    // ---------- ReadRetryAfterFromError (stream-json) ----------

    [Fact]
    public void ReadRetryAfter_From_Error_Object_Numeric()
    {
        var json = """{"type":"error","error":{"message":"rate limited","retry_after":42}}""";
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(42, ClaudeService.ReadRetryAfterFromError(doc.RootElement));
    }

    [Fact]
    public void ReadRetryAfter_From_Error_Object_CamelCase()
    {
        var json = """{"type":"error","error":{"retryAfter":17}}""";
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(17, ClaudeService.ReadRetryAfterFromError(doc.RootElement));
    }

    [Fact]
    public void ReadRetryAfter_From_Root_Field()
    {
        var json = """{"type":"error","retry_after":9}""";
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(9, ClaudeService.ReadRetryAfterFromError(doc.RootElement));
    }

    [Fact]
    public void ReadRetryAfter_String_Form_Is_Accepted()
    {
        var json = """{"error":{"retry_after":"30"}}""";
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(30, ClaudeService.ReadRetryAfterFromError(doc.RootElement));
    }

    [Fact]
    public void ReadRetryAfter_Fractional_Rounded_Up()
    {
        var json = """{"error":{"retry_after":12.3}}""";
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(13, ClaudeService.ReadRetryAfterFromError(doc.RootElement));
    }

    [Theory]
    [InlineData("""{"error":{"retry_after":0}}""")]      // 0 거부
    [InlineData("""{"error":{"retry_after":-5}}""")]     // 음수 거부
    [InlineData("""{"error":{"retry_after":99999}}""")]  // 1일 이상 거부
    [InlineData("""{"error":{"message":"oops"}}""")]     // 필드 없음
    [InlineData("""{}""")]                                 // 비어있음
    public void ReadRetryAfter_Rejects_Invalid_Or_Missing(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Null(ClaudeService.ReadRetryAfterFromError(doc.RootElement));
    }

    // ---------- ExtractRetryAfterSeconds (text fallback) ----------

    [Fact]
    public void Extract_From_Json_Snippet_In_Stderr()
    {
        var stderr = """API error: {"type":"error","error":{"message":"...","retry_after":45}}""";
        Assert.Equal(45, ClaudeService.ExtractRetryAfterSeconds(stderr, ""));
    }

    [Fact]
    public void Extract_From_Http_Header_Format()
    {
        var stderr = "HTTP/1.1 429 Too Many Requests\nRetry-After: 23\nContent-Type: application/json";
        Assert.Equal(23, ClaudeService.ExtractRetryAfterSeconds(stderr, ""));
    }

    [Fact]
    public void Extract_Prefers_Json_Over_Header_When_Both_Present()
    {
        // JSON 패턴이 먼저 매치(우선순위) — 일관성을 위해 명시 테스트
        var stderr = "Retry-After: 999\n{\"retry_after\": 7}";
        Assert.Equal(7, ClaudeService.ExtractRetryAfterSeconds(stderr, ""));
    }

    [Fact]
    public void Extract_Returns_Null_When_No_Pattern()
    {
        Assert.Null(ClaudeService.ExtractRetryAfterSeconds("just some random error\nno hints here", ""));
        Assert.Null(ClaudeService.ExtractRetryAfterSeconds("", ""));
    }

    [Fact]
    public void Extract_Rejects_Garbage_Values()
    {
        // 0, 음수, 너무 큰 값은 무시
        Assert.Null(ClaudeService.ExtractRetryAfterSeconds("""{"retry_after":0}""", ""));
        Assert.Null(ClaudeService.ExtractRetryAfterSeconds("Retry-After: 0", ""));
    }

    // ---------- IsRateLimitSignal sanity ----------

    [Theory]
    [InlineData("rate limit exceeded")]
    [InlineData("HTTP 429: too many requests")]
    [InlineData("\"status\":429")]
    [InlineData("server overloaded")]
    [InlineData("resource_exhausted")]
    public void RateLimit_Signal_Detected(string text)
    {
        Assert.True(ClaudeService.IsRateLimitSignal(text, ""));
    }

    [Fact]
    public void RateLimit_Signal_Not_Detected_For_Generic_Errors()
    {
        Assert.False(ClaudeService.IsRateLimitSignal("connection refused", ""));
        Assert.False(ClaudeService.IsRateLimitSignal("", ""));
    }
}
