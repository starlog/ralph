using Ralph.Models;

namespace Ralph.Services;

/// <summary>
/// 태스크 단위 model 결정 규칙. 우선순위:
///   1) CLI <c>--model</c> override (지정되면 모든 태스크에서 그 값을 강제)
///   2) <c>task.Model</c> (PlanGenerator가 plan 단계에서 복잡도를 보고 채워둔 값)
///   3) 기본값 <c>"sonnet"</c>
/// </summary>
internal static class ModelResolver
{
    public const string DefaultModel = "sonnet";

    /// <summary>지원되는 model 값. schema enum과 동기화 유지.</summary>
    public static readonly IReadOnlyCollection<string> Allowed =
        new[] { "opus", "sonnet" };

    public static (string Model, string Source) Resolve(string? cliOverride, TaskItem task)
    {
        if (!string.IsNullOrEmpty(cliOverride))
            return (cliOverride, "--model");
        if (!string.IsNullOrEmpty(task.Model))
            return (task.Model, "plan");
        return (DefaultModel, "default");
    }

    /// <summary>
    /// task 컨텍스트가 없는 Claude 호출(머지 충돌 해결, plan critique 등) — override 또는 default.
    /// </summary>
    public static string ResolveForNonTask(string? cliOverride) =>
        !string.IsNullOrEmpty(cliOverride) ? cliOverride : DefaultModel;
}
