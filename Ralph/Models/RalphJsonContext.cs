using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ralph.Models;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(TasksFile))]
[JsonSerializable(typeof(ParallelSettings))]
[JsonSerializable(typeof(JsonElement))]
internal partial class RalphJsonContext : JsonSerializerContext;
