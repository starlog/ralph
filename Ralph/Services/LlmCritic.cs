using System.Text;

namespace Ralph.Services;

/// <summary>
/// PRD 본문과 PlanGenerator가 만든 tasks.json을 LLM에 보내 정성 비평을 받는 비평기.
/// PrdCritic의 정적 휴리스틱과 별개로 "전체 PRD 의도와 plan 분배가 맞는가" 같은
/// 추론이 필요한 권고를 받기 위한 옵트인 단계 — 기본은 off, --llm-critique flag로만 활성.
/// 본 클래스는 prompt 구성과 LLM 호출만 담당하고, cost 기록·콘솔 출력은 호출 측 책임.
/// </summary>
public class LlmCritic
{
    private const int MaxPrdChars = 4000;
    private const int MaxTaskSummaryEntries = 60;

    public async Task<string> AnalyzeAsync(
        string prdContent,
        TaskManager tm,
        IAgentRunner runner,
        string? model,
        CancellationToken ct)
    {
        if (runner is null) throw new ArgumentNullException(nameof(runner));
        if (tm is null) throw new ArgumentNullException(nameof(tm));

        var prompt = BuildPrompt(prdContent ?? "", tm);
        var result = await runner.RunStreamAsync(
            prompt,
            model: model,
            logger: null,
            output: null,
            ct: ct,
            allowedTools: "");

        return result?.Output?.Trim() ?? "";
    }

    internal static string BuildPrompt(string prdContent, TaskManager tm)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a senior software architect critiquing this PRD and the generated task plan.");
        sb.AppendLine("Focus on the relationship between the PRD intent and how it was decomposed into tasks.");
        sb.AppendLine();
        sb.AppendLine("<prd>");
        sb.AppendLine(TrimPrd(prdContent));
        sb.AppendLine("</prd>");
        sb.AppendLine();
        sb.AppendLine("<plan>");
        sb.AppendLine(BuildPlanSummary(tm));
        sb.AppendLine("</plan>");
        sb.AppendLine();
        sb.AppendLine("Return at most 5 bullets covering structural problems, missed parallelism,");
        sb.AppendLine("scope inflation/deflation, and dependency cycle risks. Plain text. Korean if PRD is Korean.");
        return sb.ToString();
    }

    internal static string TrimPrd(string prd)
    {
        if (string.IsNullOrEmpty(prd)) return "";
        if (prd.Length <= MaxPrdChars) return prd;

        var head = MaxPrdChars / 2;
        var tail = MaxPrdChars - head;
        return prd[..head]
               + $"\n\n... [중략 {prd.Length - MaxPrdChars}자 생략] ...\n\n"
               + prd[^tail..];
    }

    internal static string BuildPlanSummary(TaskManager tm)
    {
        var tasks = tm.Data.Tasks;
        if (tasks.Count == 0) return "(no tasks)";

        var sb = new StringBuilder();
        var limit = Math.Min(tasks.Count, MaxTaskSummaryEntries);
        for (var i = 0; i < limit; i++)
        {
            var t = tasks[i];
            var deps = t.DependsOn is { Count: > 0 }
                ? string.Join(",", t.DependsOn)
                : "-";
            var modified = t.ModifiedFiles is { Count: > 0 }
                ? string.Join(",", t.ModifiedFiles)
                : "-";
            sb.Append("- id=").Append(t.Id)
              .Append(" deps=[").Append(deps).Append(']')
              .Append(" modifiedFiles=[").Append(modified).Append(']')
              .AppendLine();
        }

        if (tasks.Count > limit)
            sb.Append("- ... (").Append(tasks.Count - limit).AppendLine(" task 추가 — 생략)");

        return sb.ToString().TrimEnd();
    }
}
