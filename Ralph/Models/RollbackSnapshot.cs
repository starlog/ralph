using System.Text.Json.Serialization;

namespace Ralph.Models;

/// <summary>
/// 한 시점의 ralph 상태 스냅샷. --rollback이 사용한다.
/// pre-plan: --plan 실행 직전 상태 (rollback 대상: "before ralph execution").
/// post-plan: --plan 성공 직후 상태 (rollback 대상: "after --plan / before --run").
/// </summary>
public class RollbackSnapshot
{
    [JsonPropertyName("phase")]
    public string Phase { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";

    [JsonPropertyName("gitHead")]
    public string GitHead { get; set; } = "";

    [JsonPropertyName("branch")]
    public string Branch { get; set; } = "";

    /// <summary>스냅샷 시점에 tasks.json이 존재했는지.</summary>
    [JsonPropertyName("hadTasksJson")]
    public bool HadTasksJson { get; set; }

    /// <summary>스냅샷 시점의 tasks.json 원본 내용. HadTasksJson=false면 null.</summary>
    [JsonPropertyName("tasksJsonContent")]
    public string? TasksJsonContent { get; set; }

    /// <summary>스냅샷이 가리키는 tasks.json 경로 (상대/절대 그대로).</summary>
    [JsonPropertyName("tasksFilePath")]
    public string TasksFilePath { get; set; } = "";
}
