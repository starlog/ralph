using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

/// <summary>
/// ClaudeService 에러 분류·재시도 정책 검증.
/// ClassifyFailure / DecideRetryAction 순수 함수 테스트와
/// TestableClaudeService를 통한 RunWithRetryAsync 호출 횟수 검증을 포함한다.
/// 기존 RateLimitBackoffTests도 같은 빌드에서 함께 통과해야 한다.
/// </summary>
public class ClaudeServiceFailureTests
{
    // ──────────────────────────────────────────────────────────
    // 테스트용 ClaudeService 서브클래스
    // ──────────────────────────────────────────────────────────

    private sealed class TestableClaudeService : ClaudeService
    {
        private readonly Queue<ClaudeResult> _results;
        public int CallCount { get; private set; }

        public TestableClaudeService(Queue<ClaudeResult> results, int maxRetries = 2, int retryDelay = 0)
            : base(maxRetries, retryDelay)
        {
            _results = results;
            // 테스트에서는 지연을 건너뜀
            DelayOverride = (_, _) => Task.CompletedTask;
        }

        public override Task<ClaudeResult> RunStreamAsync(
            string prompt,
            string? model = null,
            string? workingDirectory = null,
            RalphLogger? logger = null,
            TextWriter? output = null,
            CancellationToken ct = default,
            string? allowedTools = null)
        {
            CallCount++;
            if (_results.Count == 0)
                return Task.FromResult(new ClaudeResult { Success = false, ExitCode = 1, FailureKind = ClaudeFailureKind.Unknown });
            return Task.FromResult(_results.Dequeue());
        }
    }

    /// <summary>첫 번째 RunStreamAsync 호출 후 CancellationTokenSource를 취소한다.</summary>
    private sealed class CancellableAfterFirstCallService : ClaudeService
    {
        private readonly ClaudeResult _result;
        private readonly CancellationTokenSource _cts;
        private bool _firstCalled;

        public CancellableAfterFirstCallService(ClaudeResult result, CancellationTokenSource cts, int maxRetries = 3)
            : base(maxRetries, retryDelay: 0)
        {
            _result = result;
            _cts = cts;
            // rate-limit backoff도 즉시 취소되므로 지연 함수는 실제 Task.Delay 그대로 유지
        }

        public override Task<ClaudeResult> RunStreamAsync(
            string prompt,
            string? model = null,
            string? workingDirectory = null,
            RalphLogger? logger = null,
            TextWriter? output = null,
            CancellationToken ct = default,
            string? allowedTools = null)
        {
            if (!_firstCalled)
            {
                _firstCalled = true;
                _cts.Cancel(); // 다음 Task.Delay(backoff, ct)가 즉시 취소되도록
            }
            return Task.FromResult(_result);
        }
    }

    private static Queue<ClaudeResult> Repeat(ClaudeResult r, int n)
    {
        var q = new Queue<ClaudeResult>();
        for (var i = 0; i < n; i++) q.Enqueue(r);
        return q;
    }

    // ──────────────────────────────────────────────────────────
    // ClassifyFailure — 6개 케이스 분류 검증
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void Classify_BinaryNotFound_ExitCode127()
    {
        var kind = ClaudeService.ClassifyFailure(127, false, false, "", "", 0, false);
        Assert.Equal(ClaudeFailureKind.BinaryNotFound, kind);
    }

    [Fact]
    public void Classify_BinaryNotFound_CommandNotFound_Stderr()
    {
        var kind = ClaudeService.ClassifyFailure(1, false, false, "command not found: claude", "", 0, false);
        Assert.Equal(ClaudeFailureKind.BinaryNotFound, kind);
    }

    [Fact]
    public void Classify_PermissionDenied_ExitCode126()
    {
        var kind = ClaudeService.ClassifyFailure(126, false, false, "", "", 0, false);
        Assert.Equal(ClaudeFailureKind.PermissionDenied, kind);
    }

    [Fact]
    public void Classify_PermissionDenied_Stderr()
    {
        var kind = ClaudeService.ClassifyFailure(1, false, false, "permission denied", "", 0, false);
        Assert.Equal(ClaudeFailureKind.PermissionDenied, kind);
    }

    [Fact]
    public void Classify_Timeout_Flag_TakesPriority_Over_RateLimit()
    {
        // timedOut=true이면 rateLimited=true여도 Timeout으로 분류된다
        var kind = ClaudeService.ClassifyFailure(1, timedOut: true, rateLimited: true, "", "", 0, false);
        Assert.Equal(ClaudeFailureKind.Timeout, kind);
    }

    [Fact]
    public void Classify_RateLimited_Flag()
    {
        var kind = ClaudeService.ClassifyFailure(1, false, rateLimited: true, "", "", 0, false);
        Assert.Equal(ClaudeFailureKind.RateLimited, kind);
    }

    [Fact]
    public void Classify_MalformedOutput_JsonParseFailures()
    {
        var kind = ClaudeService.ClassifyFailure(1, false, false, "", "", jsonParseFailures: 3, gotAnyAssistantMessage: false);
        Assert.Equal(ClaudeFailureKind.MalformedOutput, kind);
    }

    [Fact]
    public void Classify_Unknown_GenericExitCode()
    {
        var kind = ClaudeService.ClassifyFailure(42, false, false, "", "", 0, false);
        Assert.Equal(ClaudeFailureKind.Unknown, kind);
    }

    // ──────────────────────────────────────────────────────────
    // DecideRetryAction — 재시도 정책 단위 검증
    // ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ClaudeFailureKind.BinaryNotFound)]
    [InlineData(ClaudeFailureKind.PermissionDenied)]
    public void DecideRetry_FailFast_For_PermanentFailures(ClaudeFailureKind kind)
    {
        // maxRetries가 크더라도 attempt=1에서 즉시 fail-fast
        var action = ClaudeService.DecideRetryAction(kind, attemptJustFailed: 1, maxRetries: 5);
        Assert.Equal(RetryAction.FailFast, action);
    }

    [Theory]
    [InlineData(ClaudeFailureKind.MalformedOutput)]
    [InlineData(ClaudeFailureKind.Unknown)]
    public void DecideRetry_OnlyOneRetry_For_MalformedOrUnknown(ClaudeFailureKind kind)
    {
        // attempt=1 → Retry; attempt≥2 → Skip (maxRetries와 무관)
        Assert.Equal(RetryAction.Retry, ClaudeService.DecideRetryAction(kind, 1, maxRetries: 5));
        Assert.Equal(RetryAction.Skip, ClaudeService.DecideRetryAction(kind, 2, maxRetries: 5));
        Assert.Equal(RetryAction.Skip, ClaudeService.DecideRetryAction(kind, 3, maxRetries: 5));
    }

    [Theory]
    [InlineData(ClaudeFailureKind.Timeout)]
    [InlineData(ClaudeFailureKind.RateLimited)]
    public void DecideRetry_UpToMaxRetries_For_TransientFailures(ClaudeFailureKind kind)
    {
        // maxRetries=3: attempt 1,2 → Retry; attempt 3 → Skip
        Assert.Equal(RetryAction.Retry, ClaudeService.DecideRetryAction(kind, 1, 3));
        Assert.Equal(RetryAction.Retry, ClaudeService.DecideRetryAction(kind, 2, 3));
        Assert.Equal(RetryAction.Skip, ClaudeService.DecideRetryAction(kind, 3, 3));
    }

    // ──────────────────────────────────────────────────────────
    // RunWithRetryAsync 호출 횟수 검증 (end-to-end)
    // ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ClaudeFailureKind.BinaryNotFound, 127)]
    [InlineData(ClaudeFailureKind.PermissionDenied, 126)]
    public async Task CallCount_Is_1_For_FailFast(ClaudeFailureKind kind, int exitCode)
    {
        var failResult = new ClaudeResult { Success = false, ExitCode = exitCode, FailureKind = kind };
        var svc = new TestableClaudeService(Repeat(failResult, 5), maxRetries: 3);
        var output = new StringWriter();

        await svc.RunWithRetryAsync("test", output: output);

        Assert.Equal(1, svc.CallCount);
    }

    [Theory]
    [InlineData(ClaudeFailureKind.MalformedOutput)]
    [InlineData(ClaudeFailureKind.Unknown)]
    public async Task CallCount_Is_2_For_MalformedOutput_And_Unknown(ClaudeFailureKind kind)
    {
        var failResult = new ClaudeResult { Success = false, ExitCode = 1, FailureKind = kind };
        // maxRetries=5여도 1회 재시도만 허용(총 2회 호출)
        var svc = new TestableClaudeService(Repeat(failResult, 10), maxRetries: 5);
        var output = new StringWriter();

        await svc.RunWithRetryAsync("test", output: output);

        Assert.Equal(2, svc.CallCount);
    }

    [Theory]
    [InlineData(ClaudeFailureKind.Timeout, false)]
    [InlineData(ClaudeFailureKind.RateLimited, true)]
    public async Task CallCount_Equals_MaxRetries_For_TransientFailures(ClaudeFailureKind kind, bool rateLimited)
    {
        const int maxRetries = 3;
        var failResult = new ClaudeResult
        {
            Success = false,
            ExitCode = 1,
            FailureKind = kind,
            TimedOut = kind == ClaudeFailureKind.Timeout,
            RateLimited = rateLimited,
        };
        var svc = new TestableClaudeService(Repeat(failResult, 10), maxRetries: maxRetries);
        var output = new StringWriter();

        await svc.RunWithRetryAsync("test", output: output);

        Assert.Equal(maxRetries, svc.CallCount);
    }

    // ──────────────────────────────────────────────────────────
    // RateLimited — 서버 retry-after가 backoff 소스로 사용됨을 로그로 검증
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RateLimited_WithRetryAfter_LogContainsServerHint()
    {
        // 첫 번째 RunStreamAsync 호출 직후 cts를 취소 → backoff 메시지는 출력된 뒤
        // Task.Delay(backoffMs, cancelledCt)가 즉시 OperationCanceledException 발생
        const int retryAfterSec = 30;
        var cts = new CancellationTokenSource();
        var rateLimitedResult = new ClaudeResult
        {
            Success = false,
            ExitCode = 1,
            FailureKind = ClaudeFailureKind.RateLimited,
            RateLimited = true,
            RetryAfterSec = retryAfterSec,
        };

        var svc = new CancellableAfterFirstCallService(rateLimitedResult, cts, maxRetries: 3);
        var output = new StringWriter();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.RunWithRetryAsync("test", output: output, ct: cts.Token));

        var log = output.ToString();
        // RunWithRetryAsync는 rate-limit backoff 시 output에 "server retry-after=Ns" 포함 메시지를 기록한다
        Assert.Contains($"server retry-after={retryAfterSec}s", log);
    }

    [Fact]
    public async Task RateLimited_WithoutRetryAfter_LogContainsExponential()
    {
        var cts = new CancellationTokenSource();
        var rateLimitedResult = new ClaudeResult
        {
            Success = false,
            ExitCode = 1,
            FailureKind = ClaudeFailureKind.RateLimited,
            RateLimited = true,
            RetryAfterSec = null,
        };

        var svc = new CancellableAfterFirstCallService(rateLimitedResult, cts, maxRetries: 3);
        var output = new StringWriter();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.RunWithRetryAsync("test", output: output, ct: cts.Token));

        var log = output.ToString();
        Assert.Contains("exponential", log);
    }
}
