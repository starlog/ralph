using Ralph.Models;

namespace Ralph.Services;

/// <summary>
/// Claude 호출 + (optional) verification 명령을 실패 컨텍스트와 함께 self-fix 재시도하는 공용 루프.
/// ParallelExecutor의 단일/배치 두 경로가 공유하던 사실상 동일한 retry 로직을 한 곳에 모은다.
///
/// 진행 메시지(콘솔 라인)는 호출 컨텍스트마다 톤이 다르므로 <see cref="VerificationCallbacks"/>로
/// 분리해 caller가 주입한다 — 단일 task는 Spectre 풀 스타일, 워크트리는 prefix 들여쓰기.
/// </summary>
internal sealed class VerificationLoop
{
    private readonly VerificationRunner _verifier;
    private readonly RalphLogger _logger;
    private readonly CostTracker _cost;
    private readonly IAgentRunner _claude;

    public VerificationLoop(
        IAgentRunner claude, VerificationRunner verifier, CostTracker cost, RalphLogger logger)
    {
        _claude = claude;
        _verifier = verifier;
        _cost = cost;
        _logger = logger;
    }

    /// <summary>
    /// basePrompt를 변경하지 않고, verification 실패 시 failureCtx를 prepend해 다시 호출한다.
    /// 모든 retry 소진 시 false. <paramref name="callbacks"/>의 콘솔 메시지는 caller가 결정.
    /// </summary>
    /// <param name="claudeWorkingDirectory">
    /// Claude 호출에 사용할 cwd. null이면 호출 프로세스 cwd를 상속(단일 task path).
    /// </param>
    /// <param name="verifierWorkingDirectory">
    /// verification 명령을 실행할 cwd. 항상 명시 — 단일 path는 cwd, 워크트리 path는 worktree dir.
    /// </param>
    public async Task<bool> ExecuteAsync(
        TaskItem task,
        string basePrompt,
        int maxVerifyRetries,
        string? claudeWorkingDirectory,
        string verifierWorkingDirectory,
        TextWriter? output,
        string? model,
        VerificationCallbacks callbacks,
        CancellationToken ct)
    {
        if (maxVerifyRetries < 0) maxVerifyRetries = 0;
        string? failureCtx = null;

        for (var attempt = 0; attempt <= maxVerifyRetries; attempt++)
        {
            var fullPrompt = failureCtx == null
                ? basePrompt
                : $"{failureCtx}\n\n---\n\n{basePrompt}";

            // P0-1: cost는 예외 경로에서도 try/finally로 기록.
            ClaudeResult? result = null;
            try
            {
                result = await _claude.RunWithRetryAsync(
                    fullPrompt, model: model,
                    workingDirectory: claudeWorkingDirectory,
                    logger: _logger, output: output, ct: ct);
            }
            finally
            {
                await _cost.RecordAsync(task.Id, model ?? "opus", result, CancellationToken.None);
            }

            if (result == null || !result.Success)
            {
                callbacks.OnClaudeFailure?.Invoke(result);
                return false;
            }

            if (task.Verification is not { } spec || string.IsNullOrWhiteSpace(spec.Command))
                return true;

            callbacks.OnVerificationStart?.Invoke(spec);
            var verify = await _verifier.RunAsync(spec, verifierWorkingDirectory, _logger, output, ct);

            if (verify.Success)
            {
                callbacks.OnVerificationPass?.Invoke(verify);
                return true;
            }

            if (attempt >= maxVerifyRetries)
            {
                callbacks.OnVerificationFailFinal?.Invoke(verify, attempt + 1);
                return false;
            }

            callbacks.OnVerificationRetry?.Invoke(verify, attempt + 1, maxVerifyRetries);
            failureCtx = VerificationRunner.BuildFailureContext(spec.Command, verify);
        }

        return false;
    }
}

/// <summary>
/// 진행/실패 메시지 출력 정책을 caller가 주입한다 (콘솔 톤은 컨텍스트마다 다름).
/// </summary>
internal sealed class VerificationCallbacks
{
    public Action<ClaudeResult?>? OnClaudeFailure { get; init; }
    public Action<VerificationSpec>? OnVerificationStart { get; init; }
    public Action<VerificationResult>? OnVerificationPass { get; init; }
    public Action<VerificationResult, int /*attemptCount*/>? OnVerificationFailFinal { get; init; }
    public Action<VerificationResult, int /*attemptIndex*/, int /*maxRetries*/>? OnVerificationRetry { get; init; }
}
