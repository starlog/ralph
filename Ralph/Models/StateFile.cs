using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ralph.Models;

/// <summary>
/// `.ralph-logs/state.json` 의 루트. tasks.json(spec)에서 분리된 mutable progress 비트를 보관한다.
/// Orchestrator 단독 writer — worktree 안에서는 절대 쓰지 않는다. git에 커밋되지 않는다.
/// </summary>
public class StateFile
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("tasks")]
    public Dictionary<string, TaskState> Tasks { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class TaskState
{
    [JsonPropertyName("done")]
    public bool Done { get; set; }

    /// <summary>subtaskId -> done</summary>
    [JsonPropertyName("subtasks")]
    public Dictionary<string, bool>? Subtasks { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
