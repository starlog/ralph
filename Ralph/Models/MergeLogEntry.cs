using System.Text.Json.Serialization;

namespace Ralph.Models;

/// <summary>
/// .ralph-logs/merge-log.jsonl 한 줄에 해당하는 머지 트랜잭션 레코드.
/// PRD 스키마: { ts, batch, taskId, baseSha, mergedSha, stateMarked, smokeTest }.
/// event/rollbackRevertSha는 WhenWritingNull로 생략되므로 기본 entry에는 나타나지 않는다.
/// </summary>
public sealed class MergeLogEntry
{
    /// <summary>UTC ISO-8601 타임스탬프 (밀리초 정밀).</summary>
    [JsonPropertyName("ts")]
    public string Ts { get; set; } = "";

    /// <summary>
    /// 이번 --run 세션에서의 batch 인덱스 (1-based).
    /// 세션 재시작 시 1로 초기화되므로 세션 간 비교는 의미 없다.
    /// </summary>
    [JsonPropertyName("batch")]
    public int Batch { get; set; }

    /// <summary>tasks.json의 task ID.</summary>
    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = "";

    /// <summary>batch 시작 시점 base HEAD SHA (full 40자).</summary>
    [JsonPropertyName("baseSha")]
    public string BaseSha { get; set; } = "";

    /// <summary>해당 task의 머지 커밋 SHA (full 40자).</summary>
    [JsonPropertyName("mergedSha")]
    public string MergedSha { get; set; } = "";

    /// <summary>state.json의 done=true 마킹 성공 여부.</summary>
    [JsonPropertyName("stateMarked")]
    public bool StateMarked { get; set; }

    /// <summary>"passed" | "failed" | "skipped".</summary>
    [JsonPropertyName("smokeTest")]
    public string SmokeTest { get; set; } = "";

    /// <summary>
    /// "merge" (기본, 생략) | "rollback" (fix2 #7 자동 revert로 인한 보정 entry).
    /// null 또는 빈 문자열은 "merge"로 해석한다.
    /// </summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>
    /// event="rollback"일 때만 채움. 어느 revert 커밋이 생성됐는지 진단용.
    /// merge entry에는 null (WhenWritingNull로 직렬화 생략).
    /// </summary>
    [JsonPropertyName("rollbackRevertSha")]
    public string? RollbackRevertSha { get; set; }
}
