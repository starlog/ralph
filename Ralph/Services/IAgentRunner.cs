namespace Ralph.Services;

/// <summary>
/// 외부 에이전트 CLI(Claude Code, Aider, Cursor CLI, Codex 등)를 호출하는 통합 인터페이스.
/// ParallelExecutor·PlanGenerator·Program는 이 추상에만 의존하며 ClaudeService는 default 구현.
/// 새 에이전트는 IAgentRunner를 구현해 동일한 ClaudeResult 모양으로 응답하면 된다.
///
/// 구현체가 지켜야 할 계약:
/// - RunStreamAsync는 single attempt. 실패 시 Success=false인 ClaudeResult 반환(예외 던지지 않음).
///   외부 ct 발화 시에만 OperationCanceledException propagate.
/// - RunWithRetryAsync는 maxRetries 만큼 재시도, 실패 컨텍스트를 다음 prompt에 prepend.
///   RateLimited 신호면 backoff 시간을 늘릴 것.
/// - Debug, TaskTimeoutSec는 호출 전 set 가능한 옵션. 구현체가 지원하지 않으면 무시 가능.
/// </summary>
public interface IAgentRunner
{
    /// <summary>스트림 이벤트를 stdout/log에 그대로 보일지 여부. 미지원 구현체는 무시 가능.</summary>
    bool Debug { get; set; }

    /// <summary>호출 1회당 wall-clock timeout(초). null/0/음수면 timeout 미적용. 초과 시 process tree kill.</summary>
    int? TaskTimeoutSec { get; set; }

    /// <summary>
    /// 단일 시도. 결과는 항상 ClaudeResult로 반환(예외는 ct 외에는 던지지 않음).
    /// </summary>
    Task<ClaudeResult> RunStreamAsync(
        string prompt,
        string? model = null,
        string? workingDirectory = null,
        RalphLogger? logger = null,
        TextWriter? output = null,
        CancellationToken ct = default,
        string? allowedTools = null);

    /// <summary>
    /// 실패 시 컨텍스트 prepend 후 maxRetries 까지 재시도. RateLimited 결과면 exponential backoff 적용.
    /// </summary>
    Task<ClaudeResult> RunWithRetryAsync(
        string prompt,
        string? model = null,
        string? workingDirectory = null,
        RalphLogger? logger = null,
        TextWriter? output = null,
        CancellationToken ct = default,
        Func<ClaudeResult, string?>? buildRetryContext = null,
        string? allowedTools = null);
}
