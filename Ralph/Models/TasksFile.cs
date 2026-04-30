using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ralph.Models;

public class TasksFile
{
    [JsonPropertyName("projectName")]
    public string? ProjectName { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("tasks")]
    public List<TaskItem> Tasks { get; set; } = [];

    [JsonPropertyName("workflow")]
    public WorkflowSettings? Workflow { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class TaskItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("done")]
    public bool Done { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("dependsOn")]
    public List<string>? DependsOn { get; set; }

    [JsonPropertyName("outputFiles")]
    public List<string>? OutputFiles { get; set; }

    [JsonPropertyName("modifiedFiles")]
    public List<string>? ModifiedFiles { get; set; }

    [JsonPropertyName("subtasks")]
    public List<SubTask>? Subtasks { get; set; }

    [JsonPropertyName("verification")]
    public VerificationSpec? Verification { get; set; }

    /// <summary>
    /// 이 태스크 실행 시 사용할 Claude 모델 (`opus` 또는 `sonnet`). PlanGenerator가 태스크의
    /// 복잡도/스코프를 보고 채워준다. 사용자가 CLI `--model`을 지정하면 모든 태스크에서
    /// 그 값이 우선하고 이 필드는 무시된다. 미지정 시 기본 `sonnet`.
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Task 완료 후 외부 검증 명령. Claude self-report가 아닌 exit code 기반 ground truth.
/// 실패 시 stdout/stderr가 다음 Claude 시도 prompt에 prepend되어 self-fix 1회 시도.
/// </summary>
public class VerificationSpec
{
    /// <summary>shell command. 예: "dotnet test", "pytest tests/", "go test ./...", "tsc --noEmit"</summary>
    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    /// <summary>실행 timeout(초). 미설정 시 120초 기본값 사용.</summary>
    [JsonPropertyName("timeoutSec")]
    public int? TimeoutSec { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class SubTask
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("done")]
    public bool Done { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class WorkflowSettings
{
    [JsonPropertyName("onTaskComplete")]
    public OnTaskComplete? OnTaskComplete { get; set; }

    [JsonPropertyName("parallel")]
    public ParallelSettings? Parallel { get; set; }

    [JsonPropertyName("notifications")]
    public NotificationSettings? Notifications { get; set; }

    [JsonPropertyName("logRetentionDays")]
    public int? LogRetentionDays { get; set; }

    /// <summary>누적 비용 임계값(USD). CLI --budget-usd > env > 이 값 > 미적용.</summary>
    [JsonPropertyName("budgetUsd")]
    public double? BudgetUsd { get; set; }

    /// <summary>Per-attempt Claude 호출 timeout(초). CLI --task-timeout > env > 이 값 > 미적용.</summary>
    [JsonPropertyName("taskTimeoutSec")]
    public int? TaskTimeoutSec { get; set; }

    /// <summary>Claude 호출 최대 시도 횟수. env MAX_RETRIES > 이 값 > 2.</summary>
    [JsonPropertyName("maxRetries")]
    public int? MaxRetries { get; set; }

    /// <summary>Claude 호출 retry 간 대기(초). env RETRY_DELAY > 이 값 > 5.</summary>
    [JsonPropertyName("retryDelay")]
    public int? RetryDelay { get; set; }

    /// <summary>
    /// verification.command가 실패했을 때 Claude에게 self-fix를 시도하게 할 최대 재시도 횟수.
    /// 0이면 retry 없이 즉시 실패. null이면 1(기본). 큰 값(예: 3)을 주면 같은 prompt에
    /// failure context를 누적해 재시도하므로 cost는 비례해서 증가.
    /// </summary>
    [JsonPropertyName("verifyRetries")]
    public int? VerifyRetries { get; set; }

    /// <summary>
    /// 배치 머지 완료 후 base 브랜치에서 한 번 실행되는 smoke test 명령. 충돌을 LLM으로 풀거나
    /// auto-* 전략으로 해결한 후의 semantic 정합성을 검증한다. 실패 시 exit code 3으로 ralph 종료.
    /// </summary>
    [JsonPropertyName("smokeTest")]
    public VerificationSpec? SmokeTest { get; set; }

    /// <summary>
    /// PlanGenerator가 사용할 task category 목록. 기본 ["plan","implementation","testing","commit"].
    /// 4-stage가 강제되지 않도록 외부에서 재정의 가능. 이 목록은 prompt에 그대로 주입되어
    /// Claude가 생성하는 task의 category 값으로 사용된다.
    /// </summary>
    [JsonPropertyName("categories")]
    public List<string>? Categories { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class NotificationSettings
{
    [JsonPropertyName("onComplete")]
    public string? OnComplete { get; set; }

    [JsonPropertyName("onFailure")]
    public string? OnFailure { get; set; }

    /// <summary>
    /// "generic" | "slack" | "discord". null/누락이면 URL hostname으로 자동 감지
    /// (hooks.slack.com → slack, discord(app)?.com → discord, 그 외 → generic).
    /// </summary>
    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class ParallelSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("maxConcurrent")]
    public int MaxConcurrent { get; set; } = 5;

    /// <summary>
    /// 단일 전략(legacy). conflictStrategies가 없을 때만 사용.
    /// 호환 위해 유지 — 기존 tasks.json은 그대로 동작.
    /// </summary>
    [JsonPropertyName("conflictStrategy")]
    public string ConflictStrategy { get; set; } = "claude";

    /// <summary>
    /// 머지 충돌 시 순차 시도할 전략 체인. 첫 항목은 merge -X 결정, 나머지는 fallback.
    /// 예: ["auto-theirs", "claude"] — auto-theirs로도 못 푸는 충돌(add/add, rename/delete)을 claude로.
    /// 비어있거나 null이면 ConflictStrategy 단일 값을 1-element chain으로 사용.
    /// </summary>
    [JsonPropertyName("conflictStrategies")]
    public List<string>? ConflictStrategies { get; set; }

    /// <summary>
    /// `git worktree add --shared`로 .git objects를 공유해 디스크/IO를 절약하는 옵트인 옵션.
    /// CLI `--shared-worktrees` > env `RALPH_SHARED_WORKTREES` > 이 값 > false. 일부 git 환경은
    /// `--shared`를 모를 수 있어 첫 시도 실패 시 자동으로 fallback한다.
    /// </summary>
    [JsonPropertyName("sharedWorktreeObjects")]
    public bool? SharedWorktreeObjects { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// 실제 사용할 전략 chain. ConflictStrategies 우선, 없으면 [ConflictStrategy] 1개.
    /// </summary>
    public IReadOnlyList<string> GetStrategyChain()
    {
        if (ConflictStrategies is { Count: > 0 })
            return ConflictStrategies;
        if (!string.IsNullOrWhiteSpace(ConflictStrategy))
            return [ConflictStrategy];
        return ["claude"];
    }
}

public class OnTaskComplete
{
    [JsonPropertyName("commitChanges")]
    public bool CommitChanges { get; set; }

    [JsonPropertyName("commitMessageTemplate")]
    public string CommitMessageTemplate { get; set; } = "[Task #{taskId}] {taskTitle}";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
