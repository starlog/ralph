using Ralph.Models;
using Spectre.Console;

namespace Ralph.Services;

/// <summary>
/// PlanGenerator가 생성한 tasks.json을 정적 분석해 PRD 품질·병렬화 가능성·verification 누락 등을
/// 사용자에게 보고한다. PlanValidator는 무결성(error/warning)을 검증하지만, 이 클래스는
/// "plan을 어떻게 더 좋게 쓰면 병렬성이 올라갈지" 같은 권고를 출력한다.
/// </summary>
public static class PrdCritic
{
    public sealed record Suggestion(string Severity, string Message);

    public static IReadOnlyList<Suggestion> Analyze(TaskManager tm)
    {
        var list = new List<Suggestion>();
        var tasks = tm.Data.Tasks;
        if (tasks.Count == 0) return list;

        // 1. modifiedFiles가 비어있는 task 비율 — 너무 많으면 병렬 충돌 검출 불가
        var noFiles = tasks.Count(t =>
            (t.ModifiedFiles is null || t.ModifiedFiles.Count == 0)
            && (t.OutputFiles is null || t.OutputFiles.Count == 0));
        if (tasks.Count >= 3 && noFiles >= tasks.Count * 0.5)
        {
            list.Add(new Suggestion(
                "warn",
                $"{noFiles}/{tasks.Count} 태스크가 modifiedFiles/outputFiles 미선언. " +
                "PRD에 \"각 feature가 만들고 수정할 파일 목록\"을 명시하면 병렬 충돌 검출 정확도가 올라갑니다."));
        }

        // 2. dependsOn 길이가 큰 chain — 병렬화 기회 손실
        var layers = tm.ComputeTopologicalLayers();
        if (layers.Count >= 3 && tasks.Count >= 4)
        {
            var maxWidth = layers.Select(l => l.Count).DefaultIfEmpty(0).Max();
            var avgWidth = (double)tasks.Count / layers.Count;
            if (maxWidth <= 1)
            {
                list.Add(new Suggestion(
                    "warn",
                    $"의존성 그래프가 완전히 직렬({layers.Count}개 레이어 × 1개씩). " +
                    "feature 간 강한 의존이 의도된 게 아니라면, PRD에서 독립 feature를 분리해 cross-feature deps를 줄이세요 — 병렬성 0%."));
            }
            else if (avgWidth < 1.5 && layers.Count >= 4)
            {
                list.Add(new Suggestion(
                    "info",
                    $"평균 레이어 폭 {avgWidth:F1} ({layers.Count}개 레이어, 최대 {maxWidth}). " +
                    "의존성을 더 느슨하게 잡으면 병렬화 효과가 커집니다."));
            }
        }

        // 3. 같은 파일을 수정하는 비-의존 task 쌍 (PlanValidator도 보고하지만 권고 톤으로 한 번 더)
        var fileMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (task.ModifiedFiles is { Count: > 0 }) files.UnionWith(task.ModifiedFiles);
            if (task.OutputFiles is { Count: > 0 }) files.UnionWith(task.OutputFiles);
            foreach (var f in files)
            {
                if (!fileMap.TryGetValue(f, out var l)) fileMap[f] = l = [];
                l.Add(task.Id);
            }
        }
        var conflictPairs = 0;
        foreach (var (_, ids) in fileMap)
        {
            if (ids.Count < 2) continue;
            for (var i = 0; i < ids.Count; i++)
                for (var j = i + 1; j < ids.Count; j++)
                {
                    if (!HasDepPath(tasks, ids[i], ids[j]) && !HasDepPath(tasks, ids[j], ids[i]))
                        conflictPairs++;
                }
        }
        if (conflictPairs >= 3)
        {
            list.Add(new Suggestion(
                "warn",
                $"같은 파일을 수정하는 비의존 task 쌍 {conflictPairs}개 — 병렬 머지에서 충돌 가능. " +
                "feature 경계를 더 깨끗히 분리하거나 의존(depends_on)을 추가하세요."));
        }

        // 4. verification.command 누락 — implementation/testing 카테고리는 사실상 필수
        var verifMissing = tasks
            .Where(t => (t.Category == "implementation" || t.Category == "testing")
                        && (t.Verification is null || string.IsNullOrWhiteSpace(t.Verification.Command)))
            .Select(t => t.Id)
            .Take(5)
            .ToList();
        if (verifMissing.Count > 0)
        {
            list.Add(new Suggestion(
                "warn",
                $"verification.command이 없는 implementation/testing task: {string.Join(", ", verifMissing)}{(verifMissing.Count >= 5 ? "..." : "")}. " +
                "외부 검증 게이트가 없으면 Claude self-report로만 성공 판단됩니다 — build/test 명령을 추가하세요."));
        }

        // 5. prompt 길이 — 너무 짧으면 self-contained가 아닐 가능성
        var shortPrompts = tasks
            .Where(t => !string.IsNullOrWhiteSpace(t.Prompt) && t.Prompt!.Length < 80)
            .Select(t => t.Id)
            .Take(5)
            .ToList();
        if (shortPrompts.Count > 0)
        {
            list.Add(new Suggestion(
                "info",
                $"매우 짧은 prompt(<80자) task: {string.Join(", ", shortPrompts)}. " +
                "self-contained하게 작성된 prompt가 Claude self-fix retry의 회복력을 높입니다."));
        }

        // 6. 단일 root task 비율 — root가 1개뿐이면 시작부터 직렬
        var rootCount = tasks.Count(t => t.DependsOn is null || t.DependsOn.Count == 0);
        if (tasks.Count >= 6 && rootCount == 1)
        {
            list.Add(new Suggestion(
                "info",
                "root task(no deps)가 1개뿐입니다. PRD가 \"공통 setup → 각 feature\" 구조라면 자연스럽지만, " +
                "독립 feature가 여러 개라면 setup을 더 작게 나눠 root를 늘리는 것이 병렬성에 유리합니다."));
        }

        return list;
    }

    public static void PrintReport(IReadOnlyList<Suggestion> suggestions)
    {
        if (suggestions.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "\n[green]✓ PRD critique: 추가로 권고할 사항 없음.[/]");
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[blue]PRD Critique[/] [dim]({suggestions.Count}개 권고)[/]")
            .RuleStyle("blue"));
        foreach (var s in suggestions)
        {
            var icon = s.Severity == "warn" ? "[yellow]⚠[/]" : "[cyan]ℹ[/]";
            AnsiConsole.MarkupLine($"  {icon} {Markup.Escape(s.Message)}");
        }
        AnsiConsole.Write(new Rule().RuleStyle("blue"));
    }

    private static bool HasDepPath(IReadOnlyList<TaskItem> tasks, string from, string to)
    {
        var byId = tasks.ToDictionary(t => t.Id, t => t, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!visited.Add(cur)) continue;
            if (!byId.TryGetValue(cur, out var t)) continue;
            if (t.DependsOn is not { Count: > 0 }) continue;
            foreach (var d in t.DependsOn)
            {
                if (d == to) return true;
                queue.Enqueue(d);
            }
        }
        return false;
    }
}
