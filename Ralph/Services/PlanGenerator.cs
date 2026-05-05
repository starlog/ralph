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
        logger ??= RalphLogger.Null;
        categories ??= DefaultCategories;
        var isCorrection = !string.IsNullOrEmpty(correctionContext);
        AnsiConsole.MarkupLine(isCorrection
            ? "\n[yellow]Re-generating task plan with Claude Code (correction pass)...[/]\n"
            : "\n[cyan]Generating task plan with Claude Code...[/]\n");

        // 청킹 의사결정: PRD 크기/추정 토큰을 임계치와 비교한다. 1차 PR에서는 단일 호출 path를
        // 그대로 사용하되 박스를 출력해 가시화하고, 응답이 truncated 면 §4.2 안내로 종료한다.
        // 본격 2단계(outline → per-area) 호출은 후속 PR에서 chunked path를 채워 넣는다.
        ChunkingDecision? decision = null;
        try
        {
            var prdContent = await File.ReadAllTextAsync(prdFile, ct);
            decision = PlanChunker.Decide(prdContent);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // PRD 읽기 실패는 plan 자체 실패로 이어지지만, 여기서는 청킹 분기에만 영향이 있고
            // Claude도 같은 파일을 읽으므로 동일 원인으로 곧 실패한다. 진단 로그만 남기고 진행.
            logger.Warn($"PlanChunker.Decide skipped — PRD 읽기 실패: {ex.Message}");
        }

        if (decision is not null && !isCorrection)
        {
            AnsiConsole.Markup(PlanChunker.FormatDecisionBox(decision));
            AnsiConsole.WriteLine();
        }

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
            // Truncation 휴리스틱: 응답이 출력 토큰 한계로 잘렸으면 같은 PRD로 재호출해도
            // 같은 자리에서 또 잘리므로 correction loop 대신 명확한 가이드를 주고 종료한다.
            // 신규 exit code(3)로 일반 실패(1)와 구분 — CI 스크립트가 PRD 분할 필요를 식별 가능.
            if (PlanChunker.LooksTruncated(result))
            {
                AnsiConsole.Markup(PlanChunker.BuildTruncationGuidance(decision));
                logger.Error("Plan generation 응답 truncation 감지 — PRD 분할 권장");
                return PlanChunker.ExitCodePlanTruncated;
            }

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
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch { /* tmp 정리 실패는 의도적 무시: 원인 예외 보존이 우선 */ }
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

            ## Critical Run-Time Hazards (read this BEFORE Rule 1)

            These four are the failure modes that have actually wasted whole batches in past runs.
            Every other rule below is a more specific elaboration of one of these. Get these right
            and the rest of the plan generally falls into place.

            **H1. Every file your task creates or modifies MUST be declared.** Each task runs in its
            own git worktree at `.ralph-worktrees/{taskId}/`. Right before merge, ralph runs
            `git reset --hard HEAD && git clean -fd` to discard any change not staged at commit time —
            and only files in the task's `outputFiles` ∪ `modifiedFiles` get staged. So a file the
            implementation creates but forgets to declare is **silently deleted** before merge. If
            anything imports that file (or it's needed by smoke), the next batch breaks and ralph
            auto-rollbacks the whole batch. When in doubt, declare it. "It's just a small helper" is
            exactly the file that disappears and breaks the build.

            *Most-missed file in Node projects:* the lockfile. If a task runs `npm install` /
            `pnpm install` / `yarn install` (typically the scaffold task), the resulting
            `package-lock.json` / `pnpm-lock.yaml` / `yarn.lock` / `bun.lockb` MUST be in that
            task's `outputFiles`. Without it, the lockfile gets discarded and every later worktree
            re-resolves dependencies from scratch — slow, non-deterministic, and prone to
            registry/network failure.

            **H2. `workflow.smokeTest` MUST NOT enumerate specific source files.** Smoke runs after
            *every* batch on the integrated base branch. Files produced by later batches don't exist
            yet at smoke time, so a command like `python3 -m py_compile add.py subtract.py main.py`
            fails the first batch even when the plan is correct. Use whole-tree commands
            (`pytest -q`, `npm run build && npm test --silent`, `dotnet build && dotnet test`,
            `cargo build && cargo test`, `go build ./... && go test ./...`) — or omit
            `workflow.smokeTest` entirely and let ralph's built-in inference pick the right command.
            See Rule 14.

            **H3. vite/vitest projects MUST set `server.fs.strict: false` at scaffold time.**
            Smoke runs in an isolated `.ralph-smoke` worktree alongside the main repo. Vite's default
            (`true`) blocks the test runner from loading setup files like `@testing-library/jest-dom`
            from the workspace `node_modules`, so every smoke run fails immediately. The scaffold
            task that creates `vitest.config.*` must include the setting from the start and list the
            file in its `modifiedFiles`. Don't defer this. See Rule 3.5.

            **H4. Tasks MUST NOT modify `tasks.json`.** It's the spec ralph reads to dispatch every
            worktree; concurrent edits to it from inside a worktree cause every other worktree's
            merge to conflict. Progress tracking lives in `.ralph-logs/state.json` (orchestrator-only,
            not git-tracked). Never instruct a task prompt to "update tasks.json".

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
                  - **STRONGLY RECOMMENDED**: include a `verification` field running a **file-scoped** build/typecheck command (NOT the project test suite — full test execution happens post-merge in `workflow.smokeTest`, see Rule 14). Examples: `{ "command": "dotnet build src/Foo/Foo.csproj", "timeoutSec": 180 }`, `{ "command": "tsc --noEmit src/foo.ts src/bar.ts", "timeoutSec": 120 }`, `{ "command": "go build ./internal/foo/", "timeoutSec": 120 }`, `{ "command": "cargo check -p foo", "timeoutSec": 180 }`. See Rule 16 for why file-scoped matters.

               **Testing task** (category: "testing", id: `{feature}-test`)
                  - dependsOn: [`{feature}-impl`]
                  - The prompt must instruct Claude to: **write** tests for the implemented feature. Tests are EXECUTED post-merge by `workflow.smokeTest` (Rule 14), not by this task's verification command — so the task itself only needs to author the tests, not run them.
                  - **`verification` field**: compile/typecheck only, file-scoped to this task's test files. The point is to catch syntax/type errors before merge; full execution belongs in smoke. Examples by stack:
                    - .NET: `{ "command": "dotnet build src/Foo.Tests/Foo.Tests.csproj", "timeoutSec": 120 }`
                    - Python (pytest): `{ "command": "python -m py_compile tests/test_bid.py", "timeoutSec": 30 }`
                    - Go: `{ "command": "go vet ./internal/bid/...", "timeoutSec": 60 }`
                    - Node/TS: `{ "command": "tsc --noEmit tests/bid.test.ts", "timeoutSec": 60 }` — NOT `npx vitest run`, NOT `npm test`, NOT bare `tsc --noEmit`.
                    - Rust: `{ "command": "cargo check -p bid_test", "timeoutSec": 60 }`
                  - **Why this split:** per-task verification runs in a git worktree (`.ralph-worktrees/{taskId}/`) BEFORE sibling tasks have merged. Running the full test suite there hits two failure classes that have caused most recent loop breakages: (a) sibling-not-merged-yet imports fail tsc/test, (b) vitest/jest in a worktree can't load setup files from the main repo's `node_modules` due to vite `server.fs.strict`. Both vanish when test execution moves to smoke (which runs against the integrated base branch). Per-task verification is the syntax gate; smoke is the behavior gate.
                  - Skip this task entirely if the feature is mechanical (doc, config, single-line edit) where tests provide no value.

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

            3.5. **vite/vitest projects: fs.strict must be disabled at scaffold time.** When the project uses vite or vitest (presence of `vite.config.ts` / `vitest.config.ts` / `vitest.workspace.ts`, or `vite`/`vitest` in `package.json` deps), the **scaffold/setup task** (the first task that creates or owns the test-runner config) MUST set `server: { fs: { strict: false } }` inside the `test:` block (vitest) or the equivalent for other runners.

               **Why:** `workflow.smokeTest` executes the full test suite in an isolated `.ralph-smoke` worktree, which is a sibling of the main repo root. Vite's default `server.fs.strict: true` blocks the test runner from loading setup files like `@testing-library/jest-dom/vitest` that live in the workspace `node_modules` outside the worktree root. Without the fix, every smoke run on a vitest project fails immediately and triggers an auto-rollback, wasting the entire batch.

               **How:** the scaffold task that creates `vitest.config.ts` (or modifies an existing one) must include the file in its `modifiedFiles` and emit the setting from the start. Don't defer this to a test task — by the time any test task merges, smoke already needs the fix. If the repo already has a vitest config without the setting, add a one-line setup task at the top of the plan that patches it.

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

            14. **`workflow.smokeTest` is THE test execution gate (CRITICAL — must be progressively safe).** This command runs in an isolated `.ralph-smoke` worktree on the base branch after **every parallel batch merge**, not only at the end. It is the canonical place to RUN the project's full test suite — per-task verification only typechecks/compiles (Rule 13, Rule 16). Smoke is where actual `dotnet test` / `npm test` / `pytest` / `go test` lives.

               At the time it runs, only files produced by *already-merged* batches exist; files scheduled for later batches do NOT exist yet. Therefore the smoke test command MUST NOT enumerate specific files by name. A command like `python3 -m py_compile add.py subtract.py main.py` will fail on the first batch if `main.py` is in a later batch — even though the plan is correct.

               PREFERRED forms (work at every batch, regardless of which files exist yet):
               - **Build + test chain (canonical when both exist)** — chain build then test so a build break is reported before tests run:
                 - .NET: `dotnet build -nologo && dotnet test`
                 - Node/TS: `npm ci --silent && npm run build && npm test --silent` — **the `npm ci` prefix is mandatory** for Node projects. The `.ralph-smoke` worktree is a fresh checkout with no `node_modules`; without `npm ci` the build/test commands either fail immediately or trigger npm's slow auto-install of unpinned versions every batch. This requires a committed lockfile — the scaffold task that creates `package.json` MUST also declare its lockfile (`package-lock.json` / `pnpm-lock.yaml` / `yarn.lock` / `bun.lockb`) in `outputFiles`. Use the install command matching the lockfile (`pnpm install --frozen-lockfile`, `yarn install --frozen-lockfile`, `bun install --frozen-lockfile`).
                 - Rust: `cargo build --quiet && cargo test --quiet`
                 - Go: `go build ./... && go test ./...`
                 - Python: `pytest -q` (no separate build step)
               - **Build/typecheck only** — when the project has no test runner set up: `tsc --noEmit`, `dotnet build -nologo`, `cargo check`, `{{pythonCmd}} -m compileall -q .`, `ruby -wc *.rb`.
               - **Omit entirely** — if no safe whole-tree command exists for the stack, leave `workflow.smokeTest` unset. Ralph will fall back to its built-in inference (`package.json` with both build+test scripts → `<pm> run build && <pm> test --silent`; `pyproject.toml`/`setup.py`/`requirements.txt` → `{{pythonCmd}} -m compileall -q .`; etc.). Setting an over-specific smoke test is worse than setting none.

               FORBIDDEN forms:
               - Any command that names specific source files the plan creates (`python3 -m py_compile a.py b.py c.py`, `node a.js b.js`, `gcc a.c b.c -o app`). These break the moment a referenced file lives in a later batch.
               - Test commands that import from yet-to-be-created modules.

               If unsure, **prefer omitting `workflow.smokeTest` over guessing a file list** — the built-in inference is conservative and correct for most stacks.

            15. **Config files must be internally self-consistent.** When a setup/scaffold task creates a config file (`tsconfig.json`, `pyproject.toml`, `Cargo.toml`, `vite.config.ts`, `webpack.config.js`, `eslint.config.*`, etc.), the options inside that file must NOT contradict each other — even if individually each option looks plausible. Examples of contradictions that have caused latent failures in past runs:

               - `tsconfig.json` with `"rootDir": "src"` AND `"include": ["src/**/*", "tests/**/*"]` — `tests/` lives outside `rootDir`. Compiles fine while `tests/` is empty (e.g., only `.gitkeep`), then explodes with `TS6059` the first time a later task adds a `.ts` file under `tests/`. If you want tests included, either drop `rootDir`, set `rootDir: "."`, or maintain a separate `tsconfig.test.json` that extends the base.
               - `pyproject.toml` declaring a package layout (`[tool.setuptools.packages.find] where = ["src"]`) while sources live at the project root.
               - `Cargo.toml` with `[lib] path = "src/lib.rs"` while the file is actually at `src/main.rs` (or vice versa).
               - ESLint flat config that `extends` a preset incompatible with `parserOptions.ecmaVersion`.

               Before finalizing a setup-task prompt that emits a config file, mentally run through the file's options pairwise and ask: "if a later task adds a normal file matching `include`/`paths`/`packages`, will the build/typecheck still pass?" If the answer is "only because the matched directory is currently empty," the config is wrong — fix it now, not after the latent bug detonates mid-run.

               This is broader than tooling-specific lint: per-task `verification.command` cannot catch config self-contradictions when the contradicting input doesn't yet exist (e.g., empty `tests/`), and `workflow.smokeTest` will only fire once a later batch creates the triggering file. Plan the config correctly the first time.

            16. **`verification.command` MUST be scoped to this task's files (CRITICAL — fixes a major class of false-failure).** Each task runs in its own git worktree at `.ralph-worktrees/{taskId}/` BEFORE its sibling tasks have been merged. So at verification time, the worktree contains: this task's own changes + the base branch state at task start. It does NOT contain anything produced by sibling/parallel tasks.

               If your verification command runs the WHOLE project (`npm test`, `npm run typecheck`, `dotnet test`, `cargo test`, `pytest`, `go test ./...`, bare `tsc --noEmit`), it will fail whenever any part of the project depends on a sibling task's output that doesn't exist yet — even though THIS task's code is perfectly correct. The task is then marked failed and merging is blocked, despite no actual problem with what was implemented. This has caused ~40% of recent verification failures.

               The fix: make `verification.command` check ONLY the files in this task's `outputFiles` ∪ `modifiedFiles` (plus any `outputFiles` of tasks listed in this task's `dependsOn`, since those are guaranteed merged before this task starts).

               PREFERRED forms (file-scoped, **compile/typecheck only — no test execution**):
               - **TypeScript**: `tsc --noEmit src/bid.ts src/bid-helpers.ts` — list this task's .ts files explicitly. NOT bare `tsc --noEmit` (whole project), NOT `npx vitest run`, NOT `npm test`.
               - **Python (compile)**: `python3 -m py_compile src/bid.py` — name the source file. NOT `python3 -m compileall .`, NOT `pytest`.
               - **Go**: `go build ./internal/bid/` or `go vet ./internal/bid/...` — name the package. NOT `go build ./...`, NOT `go test ./...`.
               - **Rust**: `cargo check -p bid` (per-crate) when in a workspace; in a single-crate project `cargo check` is acceptable since there's no cross-task contamination risk. NOT `cargo test`.
               - **.NET**: `dotnet build src/Bid/Bid.csproj` (per-project). NOT `dotnet build` from a solution root with sibling projects mid-flight, NOT `dotnet test`.

               **Test execution belongs in `workflow.smokeTest`, not in per-task verification.** See Rule 14. The smoke gate runs against the integrated base branch in an isolated worktree and reports failures with batch-level attribution. (For Node projects, smoke must `npm ci` from a committed lockfile — see Rule 14 — so the smoke worktree has reproducible deps.)

               EXCEPTION — when whole-project verification IS appropriate:
               - Trivial single-feature repos with no parallel sibling tasks (planner can confirm this from the DAG).
               - Tasks marked `category: "commit"` or `"plan"` (no executable artifact, usually no `verification` field at all).
               - The very first scaffold/setup task (no siblings exist yet by definition).

               How to choose the scope: take the union of this task's `outputFiles` and `modifiedFiles`. If those files import from sibling-task outputs not in this task's `dependsOn`, restructure the task graph (add the dependency, or split the work) — do NOT widen the verification command to compensate, because that re-introduces the sibling-not-merged-yet problem.

               This rule overrides any earlier rule that suggested whole-project commands. The earlier examples like `dotnet test` / `npm test --silent` were correct for project-level smoke tests but wrong for per-task verification. Use this rule's file-scoped forms for `verification.command`. Use Rule 14's whole-project forms for `workflow.smokeTest`.

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
        sb.AppendLine("- **implementation/testing 카테고리 task의 outputFiles/modifiedFiles 빈 set** (Hazard H1): task가 만들거나 수정할 파일을 빠짐없이 outputFiles에 추가하세요. 미선언 파일은 머지 직전 silent discard됩니다.");
        sb.AppendLine("- **verification.command이 미선언 파일 참조** (Hazard H1): 명령에 등장하는 파일(`src/foo.ts`, `tests/test_x.py`, `Foo/Foo.csproj` 등)을 task의 outputFiles/modifiedFiles 또는 의존 task의 outputFiles에 추가하세요.");
        sb.AppendLine("- **workflow.smokeTest가 specific source 파일 enumerate** (Hazard H2): 전체 트리 명령(예: `pytest -q`, `npm run build && npm test --silent`, `dotnet build && dotnet test`)으로 교체하거나 smokeTest 자체를 비워 ralph 자동 추론에 맡기세요.");
        sb.AppendLine("- **verification.command 이스케이프 오류** (`\\n`/`\\t` 포함 시): `;` separator로 single statement로 바꾸거나, 프로젝트 표준 test runner(예: `dotnet test`, `pytest -q`, `npm test`)를 사용");
        sb.AppendLine();
        sb.AppendLine("아래는 원래의 plan 생성 지침과 schema입니다. 동일한 규칙을 따르되 위 정정 사항을 반영하세요.");
        return sb.ToString();
    }

    /// <summary>
    /// PlanValidator가 보고한 warnings를 Claude에게 다시 보내 tasks.json을 개선시키기 위한
    /// 보정 컨텍스트를 만든다. 경고는 실행을 막지 않지만 플랜 품질 향상을 위해 자동 정정을 시도한다.
    /// </summary>
    public static string BuildWarningCorrectionPrompt(
        string currentJson, IReadOnlyList<string> warnings, int attempt, int maxAttempts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# IMPORTANT: tasks.json 검증 경고 — 개선이 필요합니다");
        sb.AppendLine();
        sb.AppendLine($"이전에 생성한 tasks.json에 Ralph의 PlanValidator 검증 경고가 있습니다.");
        sb.AppendLine($"이번이 경고 정정 시도 {attempt}/{maxAttempts}회 입니다.");
        sb.AppendLine("경고는 실행을 막지는 않지만, 플랜 품질을 위해 가능하면 해결해야 합니다.");
        sb.AppendLine();
        sb.AppendLine("## 해결해야 할 검증 경고");
        sb.AppendLine();
        foreach (var warning in warnings)
            sb.AppendLine($"- {warning}");
        sb.AppendLine();
        sb.AppendLine("## 현재 tasks.json");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(currentJson);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## 정정 지침");
        sb.AppendLine();
        sb.AppendLine("위 경고를 **모두** 해결한 improved tasks.json을 작성하세요.");
        sb.AppendLine("이미 올바른 부분은 그대로 유지하고, 경고 해결에 필요한 최소 변경만 가하세요. 특히:");
        sb.AppendLine("- **파일 중복 수정**: 동일 파일을 수정하는 독립 태스크들에 dependsOn을 추가하거나 outputFiles/modifiedFiles를 명확히 분리");
        sb.AppendLine("- **카테고리 불일치**: 카테고리와 실제 작업이 맞지 않으면 prompt 내용이나 카테고리를 일치시킬 것");
        sb.AppendLine("- **verification.command 복잡성**: 5개 이상의 명령은 프로젝트 표준 test runner(`dotnet test`, `pytest -q`, `npm test` 등)로 단순화");
        sb.AppendLine("- **`npx <tool>` 사용 (verification 또는 smokeTest)** (Hazard H1 보조): worktree에는 node_modules가 없어 매번 cold install이 발생합니다. `npm test` / `npm run build` 같은 npm script로 감싸거나, scaffold task의 outputFiles에 `package-lock.json`을 추가하세요. smoke는 가능하면 `npm ci --silent && npm test --silent` 형태로 결정론적 install을 명시하세요.");
        sb.AppendLine("- **package.json 만드는 task의 lockfile 선언 누락**: scaffold가 `npm install`을 돌리면 생기는 `package-lock.json`(또는 `pnpm-lock.yaml`/`yarn.lock`/`bun.lockb`)을 그 task의 outputFiles에 추가하세요. 미선언이면 cleanup이 폐기해 base에서 사라집니다.");
        sb.AppendLine("- **알 수 없는 명령어**: 검증 명령에 표준 도구를 사용했는지 확인");
        sb.AppendLine("- **새로운 errors를 절대 도입하지 마세요** — 현재 통과된 검증 규칙은 그대로 유지해야 합니다.");
        sb.AppendLine();
        sb.AppendLine("아래는 원래의 plan 생성 지침과 schema입니다. 동일한 규칙을 따르되 위 경고를 해결하세요.");
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
