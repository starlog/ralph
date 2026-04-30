using System.Text.RegularExpressions;
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

    /// <summary>
    /// `<interpreter> <eval-flag> "..."` 또는 `'...'` 패턴을 잡아 quoted body를 캡처합니다.
    /// 인터프리터: python/python3, node/nodejs, bun, ruby, perl, php, lua, Rscript.
    /// 평가 flag: `-c`, `-e`, `-E`, `-r`(php), `-p`/`--print`(node), `--eval`.
    /// </summary>
    private static readonly Regex InlineScriptPattern = new(
        @"(?<!\w)(python3?|node|nodejs|bun|ruby|perl|php|lua|Rscript)\b\s+(?:-c|-e|-E|-r|-p|--eval|--print)\s+(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)')",
        RegexOptions.Compiled);

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
                report.Errors.Add(
                    $"'{task.Id}'의 prompt가 비어있습니다 — task가 의미 있는 작업 지시를 가져야 합니다");
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

        // 6. category-prompt 정합성 (간이 휴리스틱). workflow.categories를 customize한 프로젝트에서는
        //    아래의 plan/test/commit 키워드 가정이 맞지 않을 수 있어 default 4-stage 사용 시에만 적용.
        var configuredCats = tm.Data.Workflow?.Categories;
        var usingDefaultCategories = configuredCats is null || configuredCats.Count == 0;
        if (usingDefaultCategories)
        {
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
        }

        // 7. verification.command anti-pattern: `\n`/`\t`/`\r` 리터럴이 다음 두 위치 중 하나에 있는 경우 error.
        //    (a) shell 레벨 — 명령 전체가 `set -e\ncd ...\n...` 처럼 multi-line shell script로 작성된 경우.
        //        ralph가 `/bin/sh -c "<command>"`로 실행하므로 shell은 backslash+n을 LF로 변환하지 않음.
        //    (b) 인터프리터 레벨 — `<lang> -c "...\n..."` 안의 quoted body. 같은 이유로 인터프리터가 SyntaxError.
        //    두 스캔 모두 string-literal-aware하여 정상적인 string literal 안의 `\n`은 false positive 없이 통과.
        foreach (var task in tasks)
        {
            var cmd = task.Verification?.Command;
            if (string.IsNullOrWhiteSpace(cmd)) continue;

            // (a) shell-level scan — quoted string 밖의 top-level `\n`을 잡음
            if (ContainsBadEscape(cmd))
            {
                report.Errors.Add(
                    $"'{task.Id}' verification.command에 top-level `\\n`/`\\t`/`\\r` 이스케이프가 있습니다 " +
                    "(예: `set -e\\ncd ...\\nout=...`). ralph는 명령을 `/bin/sh -c`로 실행하는데, " +
                    "shell은 backslash+n을 개행으로 변환하지 않습니다. multi-line script가 필요하면 " +
                    "별도 `.sh` 파일로 저장 후 `bash path/to/script.sh`로 호출하거나, 명령 전체를 " +
                    "`bash -c $'set -e\\ncd ...'` 처럼 ANSI-C quoting으로 감싸세요.");
                continue; // shell-level error가 이미 있으면 인터프리터 레벨까지 보고할 필요 없음
            }

            // (b) interpreter-level scan — `<lang> -c|-e|--eval "..."` 본문
            foreach (Match m in InlineScriptPattern.Matches(cmd))
            {
                // bash ANSI-C quoting `$'...'` 은 \n을 실제 개행으로 확장하므로 안전 — skip.
                var quoteGroup = m.Groups["dq"].Success ? m.Groups["dq"] : m.Groups["sq"];
                var quoteStart = quoteGroup.Index - 1; // 여는 따옴표 위치
                if (quoteStart > 0 && cmd[quoteStart - 1] == '$') continue;

                var body = quoteGroup.Value;
                if (ContainsBadEscape(body))
                {
                    var lang = m.Groups[1].Value;
                    report.Errors.Add(
                        $"'{task.Id}' verification.command: `{lang} -c/-e/--eval` 안에 `\\n`/`\\t`/`\\r` 이스케이프가 있습니다. " +
                        "shell은 따옴표 안의 `\\n`을 개행으로 변환하지 않아 인터프리터가 SyntaxError를 일으킵니다. " +
                        "단일 statement(`;` 구분)나 프로젝트 표준 테스트 러너(예: `pytest -q`, `npm test`)를 사용하세요.");
                }
            }
        }

        // 7.5. task.model이 지정되어 있으면 허용 값(opus|sonnet)인지 확인.
        //      잘못된 값(haiku, gpt-4 등)이 들어오면 ClaudeService 실행 시 알 수 없는 모델로
        //      넘어가 fail할 수 있으니 plan 단계에서 차단.
        foreach (var task in tasks)
        {
            if (string.IsNullOrEmpty(task.Model)) continue;
            if (!ModelResolver.Allowed.Contains(task.Model, StringComparer.OrdinalIgnoreCase))
            {
                report.Errors.Add(
                    $"'{task.Id}'의 model 값 '{task.Model}'이 허용되지 않습니다. " +
                    $"허용: {string.Join(" | ", ModelResolver.Allowed)}");
            }
        }

        // 8. 민감 파일이 modifiedFiles/outputFiles에 명시되어 있으면 error
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
    /// 인터프리터 inline script body에 statement separator로 사용된 `\n`/`\t`/`\r`이
    /// 있는지 확인합니다. shell의 single/double quote는 `\n`을 LF로 변환하지 않으므로
    /// top-level에 있는 backslash-n은 거의 항상 SyntaxError를 일으킵니다.
    ///
    /// 단, 인터프리터의 string literal 내부(`"..."`, `'...'`, `` `...` ``)에 들어간 `\n`은
    /// 해당 언어가 자체 escape rule로 처리하므로 안전 → false positive 방지를 위해 건너뜁니다.
    /// `\\n`(escaped backslash) 도 단일 backslash로 전달되므로 안전.
    /// </summary>
    private static bool ContainsBadEscape(string body)
    {
        char? inString = null;
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];

            if (inString.HasValue)
            {
                // string literal 내부 — 어떤 escape도 안전 (인터프리터가 처리)
                if (c == '\\' && i + 1 < body.Length) { i++; continue; }
                if (c == inString.Value) inString = null;
                continue;
            }

            if (c == '"' || c == '\'' || c == '`')
            {
                inString = c;
                continue;
            }

            if (c == '\\' && i + 1 < body.Length)
            {
                var next = body[i + 1];
                if (next == '\\') { i++; continue; }     // \\ → 단일 backslash, 안전
                if (next == 'n' || next == 't' || next == 'r') return true;
            }
        }
        return false;
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
