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

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class NotificationSettings
{
    [JsonPropertyName("onComplete")]
    public string? OnComplete { get; set; }

    [JsonPropertyName("onFailure")]
    public string? OnFailure { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class ParallelSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("maxConcurrent")]
    public int MaxConcurrent { get; set; } = 5;

    [JsonPropertyName("conflictStrategy")]
    public string ConflictStrategy { get; set; } = "claude";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
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
