using System.Text.Json;
using System.Text.Json.Serialization;
using Ralph.Services;

namespace Ralph.Models;

// 명시적 [JsonPropertyName]이 없는 타입(CostEntry, PricingFile 등)을 위해 CamelCase 정책 활성화.
// 명시적 attribute가 있는 TasksFile/ParallelSettings 등은 영향 없음(explicit > policy).
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TasksFile))]
[JsonSerializable(typeof(ParallelSettings))]
[JsonSerializable(typeof(JsonElement))]
// CostTracker / WorktreeService 직렬화 대상 — trimming/AOT 켜져도 reflection fallback 없이 동작.
[JsonSerializable(typeof(CostEntry))]
[JsonSerializable(typeof(PricingFile))]
[JsonSerializable(typeof(PricingEntry))]
[JsonSerializable(typeof(ValidationLogEntry))]
internal partial class RalphJsonContext : JsonSerializerContext;
