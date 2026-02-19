using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ralph.Models;
using Spectre.Console;

namespace Ralph.Services;

public partial class PlanGenerator
{
    public async Task<int> GenerateAsync(
        string prdFile, string schemaContent, string tasksFile,
        ClaudeService claude, RalphLogger? logger = null, CancellationToken ct = default)
    {
        var prdContent = await File.ReadAllTextAsync(prdFile, ct);

        // Check for existing tasks.json
        if (File.Exists(tasksFile))
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: {Markup.Escape(tasksFile)} already exists.[/]");
            if (!AnsiConsole.Confirm("Overwrite?", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[red]Aborted.[/]");
                return 1;
            }

            var backup = $"{tasksFile}.backup.{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(tasksFile, backup);
            AnsiConsole.MarkupLine($"[cyan]Backup saved: {Markup.Escape(backup)}[/]");
        }

        // Header
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[green]RALPH - Plan Generator[/]").RuleStyle("blue"));
        AnsiConsole.MarkupLine($"[cyan]PRD File:[/] {Markup.Escape(prdFile)}");
        AnsiConsole.MarkupLine($"[cyan]Output:[/]   {Markup.Escape(tasksFile)}");
        AnsiConsole.Write(new Rule().RuleStyle("blue"));
        AnsiConsole.MarkupLine("\n[cyan]Generating task plan with Claude Code...[/]\n");

        // Build prompt
        var prompt = BuildPlanPrompt(prdContent, schemaContent, prdFile);

        // Run Claude (no tools, sonnet model)
        AnsiConsole.Write(new Rule("[yellow]Claude Code Output[/]").RuleStyle("yellow"));

        // Track if Claude writes the file directly via tools
        var preExisting = File.Exists(tasksFile);
        var preWriteTime = preExisting ? File.GetLastWriteTimeUtc(tasksFile) : DateTime.MinValue;

        var result = await claude.RunStreamAsync(prompt, noTools: true, logger: logger, ct: ct);

        AnsiConsole.Write(new Rule().RuleStyle("yellow"));
        AnsiConsole.WriteLine();

        if (!result.Success)
        {
            AnsiConsole.MarkupLine("[red]Error: Claude Code execution failed.[/]");
            return 1;
        }

        // Extract JSON from output
        var jsonContent = ExtractJson(result.Output);

        // Fallback: Claude may have written the file directly using tools
        if (jsonContent == null && File.Exists(tasksFile))
        {
            var postWriteTime = File.GetLastWriteTimeUtc(tasksFile);
            if (!preExisting || postWriteTime > preWriteTime)
            {
                var fileContent = await File.ReadAllTextAsync(tasksFile, ct);
                if (TryParseTasksJson(fileContent, out var fromFile))
                {
                    jsonContent = fromFile;
                    AnsiConsole.MarkupLine("[cyan]Note: Using tasks.json created by Claude directly.[/]");
                }
            }
        }

        if (jsonContent == null)
        {
            AnsiConsole.MarkupLine("[red]Error: No valid JSON found in Claude output.[/]");
            return 1;
        }

        // Validate structure
        TasksFile parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<TasksFile>(jsonContent, TaskManager.JsonOptions)
                     ?? throw new JsonException("Deserialized to null");
        }
        catch (JsonException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: Invalid JSON — {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        if (parsed.Tasks.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error: Generated JSON does not have a valid 'tasks' array.[/]");
            return 1;
        }

        var invalid = parsed.Tasks.Count(t => string.IsNullOrEmpty(t.Id) || string.IsNullOrEmpty(t.Title));
        if (invalid > 0)
        {
            AnsiConsole.MarkupLine($"[red]Error: {invalid} task(s) missing required fields (id, title).[/]");
            return 1;
        }

        // Validate 4-phase pattern (warn only)
        var planCount = parsed.Tasks.Count(t => t.Category == "plan");
        var implCount = parsed.Tasks.Count(t => t.Category == "implementation");
        var testCount = parsed.Tasks.Count(t => t.Category == "testing");
        var commitCount = parsed.Tasks.Count(t => t.Category == "commit");

        if (planCount != implCount || implCount != testCount || testCount != commitCount)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Warning: Uneven task phases — plan:{planCount} impl:{implCount} test:{testCount} commit:{commitCount}[/]");
        }

        // Write validated JSON
        var formatted = JsonSerializer.Serialize(parsed, TaskManager.JsonOptions);
        await File.WriteAllTextAsync(tasksFile, formatted, ct);

        // Analyze parallelism potential
        var noDeps = parsed.Tasks.Count(t => t.DependsOn is not { Count: > 0 });
        var withModFiles = parsed.Tasks.Count(t => t.ModifiedFiles is { Count: > 0 });

        // Summary
        AnsiConsole.MarkupLine("\n[green]Plan generated successfully![/]");
        AnsiConsole.Write(new Rule().RuleStyle("blue"));

        var table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn("Key");
        table.AddColumn("Value");
        table.AddRow("Total tasks", parsed.Tasks.Count.ToString());
        table.AddRow("Features", planCount.ToString());
        table.AddRow("Per feature", "plan -> implementation -> testing -> commit");
        table.AddRow("[cyan]Plan[/]", $"{planCount} tasks");
        table.AddRow("[cyan]Implementation[/]", $"{implCount} tasks");
        table.AddRow("[cyan]Testing[/]", $"{testCount} tasks");
        table.AddRow("[cyan]Commit[/]", $"{commitCount} tasks");
        table.AddRow("[green]Root tasks (no deps)[/]", $"{noDeps} (parallel start points)");
        table.AddRow("[green]With modifiedFiles[/]", $"{withModFiles} tasks");
        AnsiConsole.Write(table);

        AnsiConsole.Write(new Rule().RuleStyle("blue"));
        AnsiConsole.MarkupLine("\nNext steps:");
        AnsiConsole.MarkupLine("  [green]ralph --list[/]       Review generated tasks");
        AnsiConsole.MarkupLine("  [green]ralph --status[/]     Check parallel execution plan");
        AnsiConsole.MarkupLine("  [green]ralph --dry-run[/]    Preview execution");
        AnsiConsole.MarkupLine("  [green]ralph --run[/]        Execute all tasks (parallel by default)\n");
        return 0;
    }

    private static string BuildPlanPrompt(string prdContent, string schemaContent, string prdFile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            You are a project planner that generates a tasks.json file for the Ralph task executor.
            Ralph supports **parallel execution** of independent tasks using git worktrees.

            ## Your Goal
            Read the PRD (Product Requirements Document) below and produce a **single valid JSON** object that conforms to the provided JSON schema. Output ONLY the JSON — no markdown fences, no commentary.

            ## Task Generation Rules

            1. **Break down the PRD into logical features or components.** Each feature becomes a "group" of 4 sequential tasks.

            2. **For every feature/component, generate exactly 4 tasks in this order:**

               Step A - **Plan** (category: "plan")
                  - id: `{feature}-plan`
                  - The prompt must instruct Claude to: analyze requirements for this feature, examine the existing codebase, identify files to create/modify, design the architecture, and write a detailed implementation plan as a markdown file.

               Step B - **Implementation** (category: "implementation")
                  - id: `{feature}-impl`
                  - dependsOn: [`{feature}-plan`]
                  - The prompt must instruct Claude to: implement the feature according to the plan created in the plan step, create all necessary files, and follow project conventions.

               Step C - **Testing** (category: "testing")
                  - id: `{feature}-test`
                  - dependsOn: [`{feature}-impl`]
                  - The prompt must instruct Claude to: write and run tests for the implemented feature, ensure all tests pass, fix any issues found.

               Step D - **Commit** (category: "commit")
                  - id: `{feature}-commit`
                  - dependsOn: [`{feature}-test`]
                  - The prompt must instruct Claude to: review all changes, stage the relevant files (not sensitive files like .env), and create a git commit with a descriptive message in Korean.

            3. **Cross-feature dependencies (IMPORTANT for parallel execution):**
               - Features that are **independent** (don't share files or code dependencies) should have NO cross-feature dependencies. This allows Ralph to execute them in parallel using git worktrees.
               - Only add cross-feature dependencies when features genuinely depend on each other (e.g., feature B uses APIs created by feature A, or both modify the same files).
               - Example of GOOD parallel structure:
                 ```
                 auth-plan (no deps) → auth-impl → auth-test → auth-commit
                 payment-plan (no deps) → payment-impl → payment-test → payment-commit
                 ```
                 Here auth and payment can run in parallel because they are independent.
               - Example of REQUIRED sequential dependency:
                 ```
                 db-setup-plan → db-setup-impl → db-setup-test → db-setup-commit
                 user-api-plan (depends: db-setup-commit) → user-api-impl → ...
                 ```
                 Here user-api depends on db-setup because it uses the database schema.

            4. **`modifiedFiles` field:** List the specific files each task will create or modify. This is critical for parallel execution — Ralph uses this to detect potential merge conflicts and avoid running conflicting tasks simultaneously.

            5. **`outputFiles` field:** List the files each task is expected to create or modify.

            6. **Task ID format:** Use lowercase kebab-case.

            7. **Phase naming:** Group related features into phases (e.g., "phase1-setup", "phase2-core", "phase3-ui").

            8. **Prompts must be detailed and self-contained.**

            9. **Workflow settings:** Set `workflow.onTaskComplete.commitChanges` to `true`. Include `workflow.parallel.enabled: true`.

            10. **All tasks start with `"done": false`.**

            11. **Include a `projectName` and `version` field** derived from the PRD.

            ## JSON Schema
            """);

        sb.AppendLine("```json");
        sb.AppendLine(schemaContent);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine($"## PRD Document (source: {prdFile})");
        sb.AppendLine();
        sb.AppendLine(prdContent);
        sb.AppendLine();
        sb.AppendLine("## Output");
        sb.AppendLine("Generate the complete tasks.json now. Output ONLY valid JSON, nothing else.");

        return sb.ToString();
    }

    private static string? ExtractJson(string output)
    {
        // Strategy 1: Extract last complete fenced code block
        var matches = FencedBlockRegex().Matches(output);

        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var candidate = matches[i].Groups[1].Value.Trim();
            if (TryParseTasksJson(candidate, out var result))
                return result;
        }

        // Strategy 2: Try the entire output after stripping fences
        var stripped = FenceMarkerRegex().Replace(output, "").Trim();
        if (TryParseTasksJson(stripped, out var fallback))
            return fallback;

        // Strategy 3: Find the outermost { ... } that contains a valid tasks JSON
        var firstBrace = output.IndexOf('{');
        var lastBrace = output.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            var candidate = output[firstBrace..(lastBrace + 1)];
            if (TryParseTasksJson(candidate, out var braceResult))
                return braceResult;
        }

        return null;
    }

    private static bool TryParseTasksJson(string text, out string formatted)
    {
        formatted = "";
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("tasks", out var tasks)
                && tasks.ValueKind == JsonValueKind.Array)
            {
                formatted = JsonSerializer.Serialize(doc.RootElement, TaskManager.JsonOptions);
                return true;
            }
        }
        catch (JsonException)
        {
            // Not valid JSON
        }
        return false;
    }

    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)```")]
    private static partial Regex FencedBlockRegex();

    [GeneratedRegex(@"```(?:json)?")]
    private static partial Regex FenceMarkerRegex();
}
