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

    [JsonPropertyName("subtasks")]
    public List<SubTask>? Subtasks { get; set; }

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
