using Ralph.Models;
using Spectre.Console;

namespace Ralph.Services;

public class PlanValidationReport
{
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];

    public bool HasErrors => Errors.Count > 0;
    public bool HasWarnings => Warnings.Count > 0;

    public bool IsClean => !HasErrors && !HasWarnings;
}

/// <summary>
/// tasks.json의 무결성·일관성 검증. PlanGenerator 직후, --run 시작 직전,
/// 또는 ralph --validate로 단독 실행됩니다.
/// </summary>
public static class PlanValidator
{
    private static readonly string[] SensitivePatterns =
        [".env", ".pem", ".key", ".p12", ".pfx", "credentials.json", "id_rsa", "id_ed25519"];

    public static PlanValidationReport Validate(TaskManager tm)
    {
        var report = new PlanValidationReport();
        var tasks = tm.Data.Tasks;
        var idSet = tasks.Select(t => t.Id).ToHashSet();

        // 1. ID 중복
        var duplicates = tasks.GroupBy(t => t.Id).Where(g => g.Count() > 1).Select(g => g.Key);
        foreach (var d in duplicates)
            report.Errors.Add($"중복된 task ID: '{d}'");

        // 2. DAG cycle
        if (tm.HasCycle(out var cycle))
            report.Errors.Add($"순환 의존성: {string.Join(" → ", cycle)}");

        // 3. dependsOn 참조 무결성
        foreach (var task in tasks)
        {
            if (task.DependsOn is not { Count: > 0 }) continue;
            foreach (var dep in task.DependsOn)
            {
                if (!idSet.Contains(dep))
                    report.Errors.Add($"'{task.Id}' → 존재하지 않는 의존 task '{dep}'를 참조합니다");
                if (dep == task.Id)
                    report.Errors.Add($"'{task.Id}'가 자기 자신에 의존합니다");
            }
        }

        // 4. 필수 필드
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Id))
                report.Errors.Add("id가 비어있는 task가 있습니다");
            if (string.IsNullOrWhiteSpace(task.Title))
                report.Errors.Add($"'{task.Id}'의 title이 비어있습니다");
            if (string.IsNullOrWhiteSpace(task.Prompt))
                report.Warnings.Add($"'{task.Id}'의 prompt가 비어있습니다");
        }

        // 5. modifiedFiles overlap — 서로 의존이 없는 task 쌍이 같은 파일을 수정하면 병렬 시 충돌 가능
        var fileMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (task.ModifiedFiles is { Count: > 0 }) files.UnionWith(task.ModifiedFiles);
            if (task.OutputFiles is { Count: > 0 }) files.UnionWith(task.OutputFiles);
            foreach (var f in files)
            {
                if (!fileMap.TryGetValue(f, out var list))
                    fileMap[f] = list = [];
                list.Add(task.Id);
            }
        }
        foreach (var (file, taskIds) in fileMap)
        {
            if (taskIds.Count < 2) continue;
            // 두 태스크 사이에 의존 경로가 없는 경우만 경고
            for (var i = 0; i < taskIds.Count; i++)
            {
                for (var j = i + 1; j < taskIds.Count; j++)
                {
                    var a = taskIds[i];
                    var b = taskIds[j];
                    if (!HasDependencyPath(tasks, a, b) && !HasDependencyPath(tasks, b, a))
                    {
                        report.Warnings.Add(
                            $"'{a}'와 '{b}'가 같은 파일 '{file}'을 수정하지만 서로 의존이 없습니다 → 병렬 실행 시 머지 충돌 위험");
                    }
                }
            }
        }

        // 6. category-prompt 정합성 (간이 휴리스틱)
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Prompt) || string.IsNullOrWhiteSpace(task.Category)) continue;
            var lower = task.Prompt.ToLowerInvariant();
            switch (task.Category)
            {
                case "test" or "testing":
                    if (!lower.Contains("test") && !lower.Contains("테스트") && !lower.Contains("검증"))
                        report.Warnings.Add($"'{task.Id}' (category=testing)의 prompt에 test/테스트/검증 키워드가 없습니다");
                    break;
                case "commit":
                    if (!lower.Contains("commit") && !lower.Contains("커밋") && !lower.Contains("git "))
                        report.Warnings.Add($"'{task.Id}' (category=commit)의 prompt에 commit/커밋/git 키워드가 없습니다");
                    break;
                case "plan":
                    if (!lower.Contains("plan") && !lower.Contains("계획") && !lower.Contains("설계") && !lower.Contains("분석"))
                        report.Warnings.Add($"'{task.Id}' (category=plan)의 prompt에 plan/계획/설계/분석 키워드가 없습니다");
                    break;
            }
        }

        // 7. 민감 파일이 modifiedFiles/outputFiles에 명시되어 있으면 error
        foreach (var task in tasks)
        {
            var files = new List<string>();
            if (task.ModifiedFiles is { Count: > 0 }) files.AddRange(task.ModifiedFiles);
            if (task.OutputFiles is { Count: > 0 }) files.AddRange(task.OutputFiles);
            foreach (var f in files)
            {
                if (SensitivePatterns.Any(p =>
                        f.EndsWith(p, StringComparison.OrdinalIgnoreCase)
                        || f.Equals(p, StringComparison.OrdinalIgnoreCase)))
                {
                    report.Errors.Add($"'{task.Id}'가 민감 파일 패턴 '{f}'을 modifiedFiles/outputFiles에 명시했습니다");
                }
            }
        }

        return report;
    }

    /// <summary>
    /// from에서 to로 의존 경로가 있는지 (from이 to에 의존하는지) BFS로 확인합니다.
    /// </summary>
    private static bool HasDependencyPath(IReadOnlyList<TaskItem> tasks, string from, string to)
    {
        var byId = tasks.ToDictionary(t => t.Id, t => t);
        var visited = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current)) continue;
            if (!byId.TryGetValue(current, out var task)) continue;
            if (task.DependsOn is not { Count: > 0 }) continue;
            foreach (var dep in task.DependsOn)
            {
                if (dep == to) return true;
                queue.Enqueue(dep);
            }
        }
        return false;
    }

    /// <summary>
    /// 검증 결과를 콘솔에 출력합니다. errors가 있으면 1을 반환.
    /// </summary>
    public static int PrintReport(PlanValidationReport report, bool failOnWarning = false)
    {
        if (report.IsClean)
        {
            AnsiConsole.MarkupLine("[green]✓ Plan validation passed (errors: 0, warnings: 0).[/]");
            return 0;
        }

        if (report.HasErrors)
        {
            AnsiConsole.MarkupLine($"\n[red]✗ Errors ({report.Errors.Count}):[/]");
            foreach (var e in report.Errors)
                AnsiConsole.MarkupLine($"  [red]•[/] {Markup.Escape(e)}");
        }

        if (report.HasWarnings)
        {
            AnsiConsole.MarkupLine($"\n[yellow]⚠ Warnings ({report.Warnings.Count}):[/]");
            foreach (var w in report.Warnings)
                AnsiConsole.MarkupLine($"  [yellow]•[/] {Markup.Escape(w)}");
        }

        AnsiConsole.WriteLine();
        return (report.HasErrors || (failOnWarning && report.HasWarnings)) ? 1 : 0;
    }
}
