using Ralph.Services;

namespace Ralph.Commands;

/// <summary>
/// 한 번의 ralph 호출에서 모든 핸들러가 공유하는 입력/팩토리 모음.
/// 이전엔 Program.cs top-level 변수에 흩어져 있던 것을 한 객체로 묶어
/// 핸들러가 dependency injection 받게 한다 (테스트 시 fake 주입 가능).
///
/// 우선순위 적용 규칙: CLI > env > workflow > default.
/// CLI/env 값은 ArgParser가 채워 넣고, workflow 값은 TaskManager 로딩 후
/// <see cref="NewClaudeService"/> 같은 팩토리 메서드에서 합친다.
/// </summary>
public sealed class CommandContext
{
    // ─── command + remaining args ────────────────────────────────────────────
    public required string Command { get; init; }
    /// <summary>전역 flag 제거 후 남은 토큰 (positional args). Args[0] == Command.</summary>
    public required IReadOnlyList<string> Args { get; init; }

    // ─── parsed CLI flags (boolean) ───────────────────────────────────────────
    public bool Debug { get; init; }
    public bool Sequential { get; init; }
    public bool ForceFlag { get; init; }
    public bool CliStrictFiles { get; init; }
    public bool CliSharedWorktrees { get; init; }
    public bool CliNoSmokeTest { get; init; }
    public bool LlmCritique { get; init; }

    // ─── parsed CLI flags (value) ─────────────────────────────────────────────
    public int MaxParallelArg { get; init; }
    public string ModelArg { get; init; } = "opus";
    public string TasksFile { get; init; } = "tasks.json";
    public double? CliBudgetUsd { get; init; }
    public int? CliTaskTimeoutSec { get; init; }
    /// <summary>--smoke-test "&lt;cmd&gt;" 1회용 override. workflow.smokeTest와 자동 추론을 모두 우회.</summary>
    public string? CliSmokeTestCommand { get; init; }

    // ─── env-derived ──────────────────────────────────────────────────────────
    public int? EnvMaxRetries { get; init; }
    public int? EnvRetryDelay { get; init; }
    public int EnvMaxParallel { get; init; }
    public bool EnvParallelDisabled { get; init; }
    public bool EnvStrictFiles { get; init; }
    public bool EnvNoSmokeTest { get; init; }
    public bool? EnvSharedWorktrees { get; init; }
    public double? EnvBudgetUsd { get; init; }
    public int? EnvTaskTimeoutSec { get; init; }
    /// <summary>RALPH_SMOKE_TEST_COMMAND. CLI --smoke-test가 우선.</summary>
    public string? EnvSmokeTestCommand { get; init; }

    // ─── computed (CLI > env merge) ───────────────────────────────────────────
    public bool StrictFiles => CliStrictFiles || EnvStrictFiles;
    public bool NoSmokeTest => CliNoSmokeTest || EnvNoSmokeTest;
    public double? BudgetUsd => CliBudgetUsd ?? EnvBudgetUsd;
    public int? TaskTimeoutSec => CliTaskTimeoutSec ?? EnvTaskTimeoutSec;
    /// <summary>최종 smoke test 명령 override: CLI &gt; env. SmokeTestPlanner.Plan에 그대로 전달.</summary>
    public string? SmokeTestCommandOverride => !string.IsNullOrWhiteSpace(CliSmokeTestCommand)
        ? CliSmokeTestCommand
        : EnvSmokeTestCommand;

    // ─── factories ────────────────────────────────────────────────────────────

    /// <summary>
    /// IAgentRunner 팩토리. workflow 값과 cli/env를 합쳐 ClaudeService 인스턴스 생성.
    /// 우선순위: cli > env > workflow > default.
    /// </summary>
    public IAgentRunner NewClaudeService(TaskManager? tm)
    {
        var w = tm?.Data.Workflow;
        var resolvedRetries = EnvMaxRetries ?? w?.MaxRetries ?? 2;
        var resolvedDelay = EnvRetryDelay ?? w?.RetryDelay ?? 5;
        var resolvedTimeout = CliTaskTimeoutSec ?? EnvTaskTimeoutSec ?? w?.TaskTimeoutSec;
        return new ClaudeService(resolvedRetries, resolvedDelay)
        {
            Debug = Debug,
            TaskTimeoutSec = resolvedTimeout,
        };
    }

    /// <summary>budget 적용 우선순위: cli > env > workflow.</summary>
    public double? EffectiveBudgetUsd(TaskManager tm) =>
        CliBudgetUsd ?? EnvBudgetUsd ?? tm.Data.Workflow?.BudgetUsd;
}
