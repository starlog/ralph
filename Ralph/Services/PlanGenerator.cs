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
        ClaudeService claude, string model = "opus", RalphLogger? logger = null, CancellationToken ct = default)
    {
        // Header
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[green]RALPH - Plan Generator[/]").RuleStyle("blue"));
        AnsiConsole.MarkupLine($"[cyan]PRD File:[/] {Markup.Escape(prdFile)}");
        AnsiConsole.MarkupLine($"[cyan]Model:[/]    {Markup.Escape(model)}");
        AnsiConsole.MarkupLine($"[cyan]Output:[/]   {Markup.Escape(tasksFile)}");
        AnsiConsole.Write(new Rule().RuleStyle("blue"));
        AnsiConsole.MarkupLine("\n[cyan]Generating task plan with Claude Code...[/]\n");

        // Build prompt (PRD file path only — Claude reads it via Read tool)
        var prdFullPath = Path.GetFullPath(prdFile);
        var tasksFullPath = Path.GetFullPath(tasksFile);
        var prompt = BuildPlanPrompt(prdFullPath, schemaContent, tasksFullPath);

        // Run Claude (full tool access — Claude can explore codebase and write tasks.json directly)
        AnsiConsole.Write(new Rule("[yellow]Claude Code Output[/]").RuleStyle("yellow"));

        // Track if Claude writes the file directly via tools
        var preExisting = File.Exists(tasksFile);
        var preWriteTime = preExisting ? File.GetLastWriteTimeUtc(tasksFile) : DateTime.MinValue;

        var result = await claude.RunStreamAsync(prompt, model: model, logger: logger, ct: ct);

        AnsiConsole.WriteLine(); // ensure newline after streamed output
        AnsiConsole.Write(new Rule().RuleStyle("yellow"));
        AnsiConsole.WriteLine();

        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[red]Error: Claude Code execution failed (exit code: {result.ExitCode}).[/]");
            if (!string.IsNullOrWhiteSpace(result.Stderr))
                AnsiConsole.MarkupLine($"[red]Stderr: {Markup.Escape(result.Stderr.Trim())}[/]");
            if (!string.IsNullOrWhiteSpace(result.Output))
                AnsiConsole.MarkupLine($"[yellow]Output: {Markup.Escape(result.Output.Trim())}[/]");
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

        // Validate phase distribution (informational — flexible granularity is allowed)
        var planCount = parsed.Tasks.Count(t => t.Category == "plan");
        var implCount = parsed.Tasks.Count(t => t.Category == "implementation");
        var testCount = parsed.Tasks.Count(t => t.Category == "testing");
        var commitCount = parsed.Tasks.Count(t => t.Category == "commit");

        if (implCount == 0 && parsed.Tasks.Count > 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Warning: No 'implementation' tasks found — feature granularity may be off.[/]");
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

    internal static string BuildPlanPrompt(string prdFilePath, string schemaContent, string tasksFilePath = "tasks.json")
    {
        var sb = new StringBuilder();
        sb.AppendLine($$"""
            You are a project planner that generates a tasks.json file for the Ralph task executor.
            Ralph supports **parallel execution** of independent tasks using git worktrees.

            ## Your Goal
            Read the PRD file at `{{prdFilePath}}`, explore the codebase, and write a valid JSON task plan to `{{tasksFilePath}}`.

            ## Task Generation Rules

            1. **Break down the PRD into logical features or components.** Each feature becomes a group of 1~4 sequential tasks depending on the feature's complexity.

            2. **Choose the right granularity per feature.** Default to the smallest split that still ensures quality:

               - **Trivial change** (single-file edit, doc tweak, config bump, version bump):
                 → 1 task only (`category: "implementation"`). Skip plan/test/commit split entirely.

               - **Small feature** (1~3 files, no new module/architecture):
                 → 2 tasks: implementation → commit. Skip plan if the PRD itself is specific enough; skip test if the change is mechanical (e.g. doc/config).

               - **Standard feature** (multiple files, new module or non-trivial logic):
                 → Full 4-phase: plan → impl → test → commit.

               - **Complex feature** (cross-cutting, schema migration, architectural):
                 → Split into multiple sub-features, each handled with its own appropriate granularity.

               Do NOT force the 4-phase pattern when the feature does not need it. Forcing 4 tasks on a trivial PRD wastes tokens and time. Pick granularity per feature based on actual scope.

            3. **Task definitions** (use as appropriate based on chosen granularity):

               **Plan task** (category: "plan", id: `{feature}-plan`)
                  - The prompt must instruct Claude to: analyze requirements for this feature, examine the existing codebase, identify files to create/modify, design the architecture, and write a detailed implementation plan as a markdown file.

               **Implementation task** (category: "implementation", id: `{feature}-impl` or `{feature}` for trivial/small)
                  - dependsOn: [`{feature}-plan`] if a plan task exists for this feature, otherwise `[]`.
                  - The prompt must instruct Claude to: implement the feature (according to the plan if one exists), create all necessary files, follow project conventions.
                  - **STRONGLY RECOMMENDED**: include a `verification` field running the build/typecheck command (NOT the test suite — that belongs on the testing task). Examples: `{ "command": "dotnet build -nologo", "timeoutSec": 180 }`, `{ "command": "tsc --noEmit", "timeoutSec": 120 }`, `{ "command": "go build ./...", "timeoutSec": 120 }`, `{ "command": "cargo check --quiet", "timeoutSec": 180 }`. This catches compilation errors immediately so the testing task starts from a known-good state.

               **Testing task** (category: "testing", id: `{feature}-test`)
                  - dependsOn: [`{feature}-impl`]
                  - The prompt must instruct Claude to: write and run tests for the implemented feature, ensure all tests pass, fix any issues found.
                  - **MUST include a `verification` field** that runs the project's test command. This is the ground-truth gate — Ralph runs this externally and exit code 0 = success (Claude self-report is NOT trusted). Examples by stack:
                    - .NET: `{ "command": "dotnet test", "timeoutSec": 300 }`
                    - Python (pytest): `{ "command": "pytest -q tests/", "timeoutSec": 180 }`
                    - Go: `{ "command": "go test ./...", "timeoutSec": 120 }`
                    - Node/TS: `{ "command": "npm test --silent", "timeoutSec": 180 }`
                    - Rust: `{ "command": "cargo test --quiet", "timeoutSec": 300 }`
                    Detect the actual stack from the codebase (e.g., `Ralph.csproj` → .NET) and pick a command that runs the **specific test suite added by this feature** if possible (e.g. `dotnet test --filter "FullyQualifiedName~FeatureXTests"`).
                  - Skip this task if the feature is mechanical (doc, config, single-line edit) where tests provide no value.

               **Commit task** (category: "commit", id: `{feature}-commit`)
                  - dependsOn: previous task in this feature's chain (test → impl → plan, whichever is the last that exists)
                  - The prompt must instruct Claude to create a **pure commit** that contains ONLY the files this feature's processor created, modified, or deleted — never files changed by unrelated or parallel work. The prompt MUST require Claude to:
                    a. Run `git status` and `git diff --name-status HEAD` to see all changed files.
                    b. Cross-check against this feature's planned scope: the union of `outputFiles` and `modifiedFiles` declared on the feature's tasks (plus any files those steps actually created/modified/deleted in this run).
                    c. Stage ONLY those in-scope files explicitly by path with `git add <path>` (one path per file, or a precise pathspec). Do NOT use `git add .`, `git add -A`, `git add -u`, or wildcard globs that could sweep in unrelated changes.
                    d. If the working tree contains changes that are NOT part of this feature's scope, leave them unstaged — do not stash, revert, or restore them; they belong to other work.
                    e. Never stage sensitive files (.env, *.pem, *.key, credentials.json, secrets.*, *.p12, *.pfx) even if they appear in scope — abort and report instead.
                    f. Run `git diff --cached --name-only` to verify the staged set equals the intended in-scope set; if extra files leaked in, unstage them with `git restore --staged <path>` before committing.
                    g. Create a single `git commit` with a descriptive message in Korean summarizing this feature's changes.
                  - The commit task can be skipped entirely if `workflow.onTaskComplete.commitChanges` is `true` (Ralph auto-commits after each task) AND the feature is small enough that one commit suffices.

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

            12. **`verification` field is the external success gate.** Ralph runs this command after Claude finishes the task and only accepts exit code 0. On non-zero, Ralph feeds stdout/stderr back to Claude for one self-fix retry; if it still fails, the task is marked failed and merging is blocked. Use it on:
               - Every `testing` task (REQUIRED — see testing task definition above)
               - Every `implementation` task that produces buildable code (build/typecheck)
               - Skip on `plan` and `commit` tasks (no executable artifact to check)
               - Skip on documentation/config-only tasks
               First, **detect the stack from the codebase** (presence of `package.json`, `*.csproj`, `go.mod`, `Cargo.toml`, `pyproject.toml`, `requirements.txt`, etc.) and choose the appropriate command. Prefer commands that are quiet (`-q`, `--silent`, `-nologo`) so logs stay readable.

            13. **Verification command must be a single safe shell invocation.** The command runs through `/bin/sh -c` (POSIX) or `cmd /c` (Windows). It MUST NOT embed multi-line interpreter scripts inside flag arguments — the shell does not turn `\n` into a newline inside `"..."` or `'...'`, so the interpreter receives a literal backslash-n and crashes with a syntax error. This applies to every interpreter that has a `-c` / `-e` / `--eval` / `-E` flag (python, python3, node, deno, bun, ruby, perl, php, lua, R, tclsh, etc.).

               STRONGLY PREFERRED — invoke the project's standard test/build runner; it always works:
               - `pytest -q tests/` · `dotnet test` · `go test ./...` · `npm test --silent` · `cargo test --quiet` · `tsc --noEmit` · `dotnet build -nologo`

               If a one-off ad-hoc check is genuinely needed, ALLOWED forms are:
               - **Single-statement inline** (use `;` separators, no `\n`): `python3 -c "from m import f; assert f(10, 3) == 3.5; print('OK')"`
               - **Saved script file**: `python3 path/to/check.py` (the implementation task writes the file)
               - **Heredoc to stdin** with a quoted delimiter: `python3 - <<'PY'\\nimport m\\nassert m.f(1,2)==3\\nPY` — and only when the JSON encoder will produce real `\n` characters in the command string (NOT the two-character `\` + `n` escape).

               FORBIDDEN forms (these will fail at run time across every interpreter):
               - `python3 -c "from m import f\\nimport sys\\ntry: ..."` — `\\n` inside double quotes → literal backslash-n → SyntaxError
               - `node -e "const m = require('./m')\\nconsole.log(m.f(1,2))"` — same problem
               - `ruby -e "require 'm'\\nputs M.f(1,2)"` — same problem
               Any `<lang> -c|-e|--eval "..."` whose body contains a `\\n`, `\\t`, or `\\r` escape is wrong.

            ## JSON Schema
            """);

        sb.AppendLine("```json");
        sb.AppendLine(schemaContent);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Output Instructions");
        sb.AppendLine();
        sb.AppendLine("1. Read the PRD file.");
        sb.AppendLine("2. Explore the existing codebase to understand the project structure (use Glob and Read as needed).");
        sb.AppendLine("3. Generate the complete tasks.json content conforming to the schema above.");
        sb.AppendLine("4. Write the JSON to the `tasks.json` file in the current directory using the Write tool.");
        sb.AppendLine("5. After writing, print a brief summary: total task count, feature list, and parallel execution info. Do NOT print the full JSON to the screen.");

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
