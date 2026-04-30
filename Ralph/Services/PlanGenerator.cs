using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ralph.Models;
using Spectre.Console;

namespace Ralph.Services;

public partial class PlanGenerator
{
    /// <summary>
    /// 기본 4-stage. workflow.categories가 명시되지 않은 프로젝트에서 사용.
    /// 외부에서 재정의하면 PlanGenerator는 이 목록의 첫 항목을 plan, 마지막을 commit으로 가정 없이
    /// 단순히 사용자 정의 stage 이름으로 prompt에 주입한다.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultCategories =
        ["plan", "implementation", "testing", "commit"];

    public async Task<int> GenerateAsync(
        string prdFile, string schemaContent, string tasksFile,
        IAgentRunner claude, string model = "opus", RalphLogger? logger = null,
        IReadOnlyList<string>? categories = null,
        string? correctionContext = null,
        CancellationToken ct = default)
    {
        categories ??= DefaultCategories;
        var isCorrection = !string.IsNullOrEmpty(correctionContext);
        AnsiConsole.MarkupLine(isCorrection
            ? "\n[yellow]Re-generating task plan with Claude Code (correction pass)...[/]\n"
            : "\n[cyan]Generating task plan with Claude Code...[/]\n");

        // Build prompt with the caller-supplied (typically relative) paths.
        // 절대 경로를 주입하면 모델이 "프로젝트 루트는 이 디렉토리" 라고 추론해 생성된 각
        // task의 prompt 에 그 절대 경로를 그대로 박아넣는 경향이 있다. 그러면 worktree 에서
        // task 가 실행될 때(`. ralph-worktrees/{taskId}/`) 파일이 메인 레포에 쓰이고
        // verification 은 worktree 에서 돌면서 파일을 못 찾아 실패한다. 따라서 GetFullPath
        // 로 감싸지 않고 호출자가 넘긴 상대 경로 그대로 전달한다.
        var basePrompt = BuildPlanPrompt(prdFile, schemaContent, tasksFile, categories);
        var prompt = isCorrection
            ? correctionContext + "\n\n---\n\n" + basePrompt
            : basePrompt;

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

        // Validate category distribution (informational — flexible granularity is allowed).
        // 사용자 정의 categories를 받아 카운팅. 모든 카테고리가 0건이면 아무 task도 분류되지
        // 않은 것이므로 경고. 그 외에는 분포만 보여주고 진행.
        var categoryCounts = categories
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(c => c, c => parsed.Tasks.Count(t => t.Category == c), StringComparer.Ordinal);
        var totalCategorized = categoryCounts.Values.Sum();
        if (totalCategorized == 0 && parsed.Tasks.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Warning: 어떤 task도 정의된 categories({string.Join(", ", categories)})에 매칭되지 않습니다 — feature granularity 확인 필요.[/]");
        }

        // Write validated JSON atomically (tmp + rename) to avoid truncation on interrupt
        var formatted = JsonSerializer.Serialize(parsed, TaskManager.JsonOptions);
        var tmp = tasksFile + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(tmp, formatted, ct);
            File.Move(tmp, tasksFile, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }

        // Analyze parallelism potential
        var noDeps = parsed.Tasks.Count(t => t.DependsOn is not { Count: > 0 });
        var withModFiles = parsed.Tasks.Count(t => t.ModifiedFiles is { Count: > 0 });
        var opusCount = parsed.Tasks.Count(t => string.Equals(t.Model, "opus", StringComparison.OrdinalIgnoreCase));
        var sonnetCount = parsed.Tasks.Count(t => string.Equals(t.Model, "sonnet", StringComparison.OrdinalIgnoreCase));
        var unsetCount = parsed.Tasks.Count - opusCount - sonnetCount;

        // Summary
        AnsiConsole.MarkupLine("\n[green]Plan generated successfully![/]");
        AnsiConsole.Write(new Rule().RuleStyle("blue"));

        var table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn("Key");
        table.AddColumn("Value");
        table.AddRow("Total tasks", parsed.Tasks.Count.ToString());
        table.AddRow("Per feature", string.Join(" -> ", categories));
        foreach (var (cat, count) in categoryCounts)
            table.AddRow($"[cyan]{Markup.Escape(cat)}[/]", $"{count} tasks");
        table.AddRow("[green]Root tasks (no deps)[/]", $"{noDeps} (parallel start points)");
        table.AddRow("[green]With modifiedFiles[/]", $"{withModFiles} tasks");
        // 모델명만 컬러 강조 (sonnet=sky-blue, opus=amber-gold). 라벨/숫자/구분자는 평문.
        var modelBreakdown = $"[bold #d4a017]opus[/]: {opusCount} / [bold #6cb6ff]sonnet[/]: {sonnetCount}"
            + (unsetCount > 0 ? $" / unset: {unsetCount} (will default to [bold #6cb6ff]sonnet[/])" : "");
        table.AddRow("[green]Model distribution[/]", modelBreakdown);
        AnsiConsole.Write(table);

        AnsiConsole.Write(new Rule().RuleStyle("blue"));
        AnsiConsole.MarkupLine("\nNext steps:");
        AnsiConsole.MarkupLine("  [green]ralph --list[/]       Review generated tasks");
        AnsiConsole.MarkupLine("  [green]ralph --status[/]     Check parallel execution plan");
        AnsiConsole.MarkupLine("  [green]ralph --dry-run[/]    Preview execution");
        AnsiConsole.MarkupLine("  [green]ralph --run[/]        Execute all tasks (parallel by default)\n");
        return 0;
    }

    internal static string BuildPlanPrompt(
        string prdFilePath, string schemaContent, string tasksFilePath = "tasks.json",
        IReadOnlyList<string>? categories = null)
    {
        categories ??= DefaultCategories;
        var categoryListText = string.Join(", ", categories.Select(c => $"\"{c}\""));
        var pythonCmd = HostPlatform.PythonCommand;
        var osName = HostPlatform.OsName;
        var pythonHostNote = OperatingSystem.IsWindows()
            ? $"`{pythonCmd}` (NOT `python3` — on Windows, `python3.exe` is usually a Microsoft Store stub that prints 'Python' to stderr and exits with code 9009, which makes verification fail regardless of code correctness; `python` resolves to the real Anaconda / python.org install)"
            : $"`{pythonCmd}` (system `python` may be missing or be Python 2)";
        var sb = new StringBuilder();
        sb.AppendLine($$"""
            You are a project planner that generates a tasks.json file for the Ralph task executor.
            Ralph supports **parallel execution** of independent tasks using git worktrees.

            Allowed task categories for this project: {{categoryListText}}.
            Use ONLY these category values in the `category` field. The descriptions below match
            the default 4-stage pattern (plan/implementation/testing/commit); when this project
            defines a different category set, infer each stage's intent from its name and adapt
            the prompt template accordingly.

            ## Host environment (use these binary names in verification.command and workflow.smokeTest.command)

            - Operating system: {{osName}}
            - Python interpreter: {{pythonHostNote}}

            All examples below that show `python3` are written for the POSIX convention; if the host
            OS above lists a different Python command, substitute it. The binary name actually has to
            resolve on this machine, otherwise verification will fail with a "command not found"
            error and the task will be marked failed regardless of whether the code is correct.

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

            8. **Prompts must be detailed, self-contained, and use ONLY relative paths.**
               Tasks may execute inside a git worktree at `.ralph-worktrees/{taskId}/`,
               whose cwd differs from where this plan is being generated. Embedding
               absolute paths from the planner's machine — `D:\proj`, `C:\...`,
               `/home/user/proj`, `/Users/...`, `/tmp/...` — causes Claude to write outside
               the worktree, after which the verification command (which runs inside the
               worktree) cannot find the file and fails the task even when the code is
               correct. Always reference files by name relative to the project root
               (`add.py`, `src/foo.ts`, `tests/test_x.py`), never by the absolute path of
               the directory you happen to observe while planning. Do NOT phrase task
               prompts as "create the file at `<absolute path>`" or "the file must exist
               at `<absolute path>` directory" — say "create `add.py` in the project
               root" or simply "create `add.py`".

            9. **Workflow settings:** Set `workflow.onTaskComplete.commitChanges` to `true`. Include `workflow.parallel.enabled: true`.

            9.5. **Per-task `model` field (cost vs quality tradeoff).** Set `"model"` on every task with one of the allowed values:
               - `"opus"` — reasoning-heavy, slow, expensive. Use ONLY when the task genuinely benefits from deeper reasoning:
                 * Plan tasks (architecture/design, multi-file impact analysis, schema migration planning)
                 * Complex implementation tasks: cross-cutting refactors, non-trivial algorithms, concurrency, schema migrations, security-sensitive code, public API design
                 * Tasks whose verification command is expensive (slow integration tests, heavy build) where a retry from a cheap-model failure costs more than running opus once
               - `"sonnet"` — fast, cheap, good general default. Use for:
                 * Straightforward implementation tasks (most CRUD, single-feature additions, well-scoped changes)
                 * Testing tasks (writing unit tests against an already-clear feature)
                 * Commit tasks (mechanical even with the Korean message + scope checks)
                 * Doc/config/version-bump tasks
               When in doubt, pick `"sonnet"`. The user can override all tasks at run time via `--model opus|sonnet`, so an aggressive `"sonnet"` default is safe.

            10. **Do NOT include a `"done"` field.** Per-task progress is tracked separately by Ralph in `.ralph-logs/state.json` (orchestrator-managed); `tasks.json` is spec-only and immutable from Ralph's perspective.

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
               - **Single-statement inline** (use `;` separators, no `\n`): `{{pythonCmd}} -c "from m import f; assert f(10, 3) == 3.5; print('OK')"`
               - **Saved script file**: `{{pythonCmd}} path/to/check.py` (the implementation task writes the file)
               - **Heredoc to stdin** with a quoted delimiter: `{{pythonCmd}} - <<'PY'\\nimport m\\nassert m.f(1,2)==3\\nPY` — and only when the JSON encoder will produce real `\n` characters in the command string (NOT the two-character `\` + `n` escape).

               FORBIDDEN forms (these will fail at run time across every interpreter):
               - `python3 -c "from m import f\\nimport sys\\ntry: ..."` — `\\n` inside double quotes → literal backslash-n → SyntaxError
               - `node -e "const m = require('./m')\\nconsole.log(m.f(1,2))"` — same problem
               - `ruby -e "require 'm'\\nputs M.f(1,2)"` — same problem
               Any `<lang> -c|-e|--eval "..."` whose body contains a `\\n`, `\\t`, or `\\r` escape is wrong.

            14. **`workflow.smokeTest` (CRITICAL — must be progressively safe).** This command runs on the base branch after **every parallel batch merge**, not only at the end. So at the time it runs, only files produced by *already-merged* batches exist; files scheduled for later batches do NOT exist yet.

               Therefore the smoke test command MUST NOT enumerate specific files by name. A command like `python3 -m py_compile add.py subtract.py main.py` will fail on the first batch if `main.py` is in a later batch — even though the plan is correct.

               PREFERRED forms (work at every batch, regardless of which files exist yet):
               - **Project test/build runner** — `dotnet build -nologo`, `npm test --silent`, `cargo build --quiet`, `go build ./...`, `pytest -q` (only if a test runner is set up).
               - **Recursive compile/typecheck** — `{{pythonCmd}} -m compileall -q .`, `tsc --noEmit`, `ruby -wc *.rb` (glob expanded by shell at run time, so empty-match is fine on most shells with `nullglob`-equivalent — verify per stack).
               - **Omit entirely** — if no safe whole-tree command exists for the stack, leave `workflow.smokeTest` unset. Ralph will fall back to its built-in inference (`pyproject.toml`/`setup.py`/`requirements.txt` → `{{pythonCmd}} -m compileall -q .`, etc.). Setting an over-specific smoke test is worse than setting none.

               FORBIDDEN forms:
               - Any command that names specific source files the plan creates (`python3 -m py_compile a.py b.py c.py`, `node a.js b.js`, `gcc a.c b.c -o app`). These break the moment a referenced file lives in a later batch.
               - Test commands that import from yet-to-be-created modules.

               If unsure, **prefer omitting `workflow.smokeTest` over guessing a file list**.

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

    /// <summary>
    /// PlanValidator가 보고한 errors를 Claude에게 다시 보내 tasks.json을 정정시키기 위한
    /// "보정" 컨텍스트를 만든다. 호출자는 이 문자열을 BuildPlanPrompt 결과 앞에 prepend해서
    /// 재생성 호출에 사용한다 (GenerateAsync의 correctionContext 파라미터).
    /// </summary>
    public static string BuildCorrectionPrompt(
        string currentInvalidJson, IReadOnlyList<string> errors, int attempt, int maxAttempts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# IMPORTANT: tasks.json 검증 실패 — 정정이 필요합니다");
        sb.AppendLine();
        sb.AppendLine($"이전에 생성한 tasks.json이 Ralph의 PlanValidator 검증을 통과하지 못했습니다.");
        sb.AppendLine($"이번이 정정 시도 {attempt}/{maxAttempts}회 입니다.");
        sb.AppendLine();
        sb.AppendLine("## 반드시 수정해야 할 검증 오류");
        sb.AppendLine();
        foreach (var error in errors)
            sb.AppendLine($"- {error}");
        sb.AppendLine();
        sb.AppendLine("## 현재 (검증 실패한) tasks.json");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(currentInvalidJson);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## 정정 지침");
        sb.AppendLine();
        sb.AppendLine("PRD를 다시 읽고, 위 오류를 **모두** 해결한 corrected tasks.json을 작성하세요.");
        sb.AppendLine("이미 올바른 부분은 그대로 유지하고, 오류 해결에 필요한 최소 변경만 가하세요. 특히:");
        sb.AppendLine("- **순환 의존성**: 의존 관계를 재구성해 cycle 제거");
        sb.AppendLine("- **dangling dependsOn**: 존재하는 task ID만 참조하도록 수정 (오타 가능성 점검)");
        sb.AppendLine("- **중복 ID**: 고유한 ID로 rename");
        sb.AppendLine("- **민감 파일**(.env, .pem, .key, credentials.json 등)이 modifiedFiles/outputFiles에 명시된 경우 → 제거");
        sb.AppendLine("- **verification.command 이스케이프 오류** (`\\n`/`\\t` 포함 시): `;` separator로 single statement로 바꾸거나, 프로젝트 표준 test runner(예: `dotnet test`, `pytest -q`, `npm test`)를 사용");
        sb.AppendLine();
        sb.AppendLine("아래는 원래의 plan 생성 지침과 schema입니다. 동일한 규칙을 따르되 위 정정 사항을 반영하세요.");
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
