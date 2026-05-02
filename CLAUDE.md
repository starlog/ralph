# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Ralph is a CLI task orchestrator that generates execution plans from PRD (Product Requirements Document) files and runs them in parallel (or sequentially) using Claude Code. It follows a 4-phase pattern per feature: **plan → implementation → testing → commit** (configurable via `workflow.categories`), with dependency tracking between tasks. Built with .NET 8 for cross-platform support (Windows, macOS, Linux). Current version: **v1.42**.

## Architecture

- **Ralph/** — .NET 8 C# project producing a self-contained single-file binary.
  - `Program.cs` — entrypoint. Sets UTF-8 console, wires Ctrl+C → CancellationToken, runs `DependencyChecker` for `claude` / `git`, parses argv via `ArgParser`, dispatches via `CommandDispatcher`.
  - `Commands/` — one ICommand per CLI subcommand (`PlanCommand`, `PlanPromptCommand`, `RunCommand`, `DryRunCommand`, `SingleTaskCommand`, `InteractiveCommand`, `ListCommand`, `GraphCommand`, `PromptsCommand`, `ShowPromptCommand`, `StatusCommand`, `LogsCommand`, `CostCommand`, `ResetCommand`, `RollbackCommand`, `ValidateCommand`, `CritiqueCommand`, `WorktreeCleanupCommand`, `HelpCommand`, `VersionCommand`, ...) plus `ArgParser` / `CommandDispatcher` / `CommandContext` / `DependencyChecker` / `DisplayHelpers` / `SchemaLoader`. `DisplayHelpers.ShowBanner` is invoked once per session by the entry command (`--plan` / `--run` / `--status` / etc.), so progress lines, model info, and graph scans all appear under a single banner.
  - `Services/` — orchestration and integration code (table below).
  - `Models/` — `TasksFile.cs` (POCOs for tasks.json — **spec only**, no `done` field), `StateFile.cs` (POCOs for `.ralph-logs/state.json` — mutable per-task `done` bits), `RollbackSnapshot.cs` (pre-/post-plan snapshot used by `--rollback`), and `RalphJsonContext.cs` (System.Text.Json source-gen context for AOT-friendly serialization).
- **Ralph.Tests/** — xUnit test project (worktree integration tests, plan validator tests, parallel batch transition tests, etc.).
- **ralph-schema.json** — JSON Schema (2020-12) defining the `tasks.json` structure: tasks array (id/title/prompt/dependsOn/outputFiles/modifiedFiles/subtasks/verification), workflow settings (parallel, notifications, logRetentionDays, budgetUsd, taskTimeoutSec, maxRetries, retryDelay, verifyRetries, smokeTest, categories), and optional apiSpecs/samplePages. **Note**: `done` is intentionally not in the schema — progress lives in `.ralph-logs/state.json` (orchestrator-managed, not git-tracked). Embedded in the binary as `EmbeddedResource`.
- **pricing.json** — Per-model token pricing used by `CostTracker`. Embedded as `EmbeddedResource`; can be overridden by `~/.ralph/pricing.json`.
- **install.sh / install.ps1 / install-binary.sh / release-binary.sh / release-binary.ps1** — install + release scripts. The PowerShell release script mirrors the bash one for native Windows hosts (auto bump-detection, claude-CLI-driven version-ref sync, UTF-8 console encoding to keep Korean commit summaries from killing the run). Homebrew/Scoop manifests under `Formula/` and `scoop/` track the latest GitHub release.

### Key Services (Ralph/Services/)

| Service | Purpose |
|---|---|
| `IAgentRunner.cs` | Abstraction over an LLM agent runner (Claude). Allows tests/mocks to substitute the real CLI. |
| `ClaudeService.cs` | Runs Claude Code with streaming JSON output, retry logic (MAX_RETRIES/RETRY_DELAY), per-call timeout. Implements `IAgentRunner`. |
| `PlanGenerator.cs` | Sends PRD + schema to Claude (tools disabled, opus by default) to produce tasks.json. Atomic write (tmp + rename). Honors `workflow.categories` for non-default stage patterns. Passes the caller's relative paths through unchanged and instructs the planner to emit only relative paths in task prompts — embedding absolute planner-host paths makes worktree-executed tasks write outside their worktree and fail verification. Also instructs the planner to set per-task `model` (`opus` for reasoning-heavy work, `sonnet` for routine impl/test/commit) — see `ModelResolver`. Exposes `BuildCorrectionPrompt` for the validator-driven correction loop (re-sends invalid tasks.json + errors to Claude, up to 2 attempts). |
| `ModelResolver.cs` | Per-task model resolution. Priority: CLI `--model` (forces all tasks) > `task.model` (planner-assigned) > `"sonnet"` default. Allowed values: `opus`, `sonnet`. |
| `PlanValidator.cs` | Validates tasks.json: cycles, dangling deps, duplicate IDs, file overlaps, sensitive paths, eval-string body checks. `errors` trigger the auto-correction loop in `PlanCommand`; `warnings` pass through. |
| `PrdCritic.cs` | Static analysis of tasks.json — finds parallelism gaps, missing verification commands, dependency oddities. Backs `--critique`. |
| `LlmCritic.cs` | Optional LLM-driven critique of the generated plan against the original PRD. Triggered by `--llm-critique` after `--plan`. |
| `PromptBuilder.cs` | Builds the prompt sent to Claude — adds Scope, dependency outputs, sibling context, hard prohibitions. |
| `TaskManager.cs` | Loads/saves/queries tasks.json (spec); dependency DAG traversal, parallel batch grouping, topological layering. Atomic save (tmp + rename). Owns a `StateStore` for done/pending queries. On first load, auto-migrates legacy `done` fields out of tasks.json into state.json (idempotent). |
| `StateStore.cs` | `.ralph-logs/state.json` writer — mutable per-task `done` (and per-subtask) bits split out from tasks.json. Orchestrator-only writer (worktrees never touch it); never committed to git. Atomic save (tmp + rename), thread-safe via SemaphoreSlim. |
| `ParallelExecutor.cs` | Worktree-based parallel execution entrypoint with live dashboard. Delegates merge to `MergeOrchestrator`, per-task work to `WorktreeTaskRunner`, and post-merge smoke test resolution to `SmokeTestPlanner`. |
| `SmokeTestPlanner.cs` | Pure smoke-test resolution logic: `--no-smoke-test` → CLI override → env (`RALPH_SMOKE_TEST_COMMAND`) → `workflow.smokeTest` → repo-root marker auto-inference (multi-marker `&&` combination supported). Skips inferred commands when changed files are docs-only; respects explicit overrides. |
| `HostPlatform.cs` | Single source of truth for host-OS-dependent interpreter names (e.g. `python` on Windows vs `python3` on POSIX) and human-readable OS labels surfaced in plan prompts. Centralises Windows-vs-POSIX divergence so plan generation and smoke-test inference agree. |
| `WorktreeTaskRunner.cs` | Runs a single task inside its worktree: prompt build → Claude → verification loop. |
| `SequentialRunner.cs` | In-place sequential execution path (no worktrees). Used for single-task runs and merge `abort` fallback. |
| `MergeOrchestrator.cs` | Worktree merge pipeline: pre-merge tasks.json normalization, declared-vs-actual file validation, rebase-advance, merge with strategy chain, conflict resolution (auto-* or Claude), done-marking via `StateStore` (writes to `.ralph-logs/state.json`), post-merge smoke test, opt-in auto-rollback on smoke failure (`--auto-rollback-on-smoke-fail`). **No tasks.json commit**: spec is immutable from Ralph's side. On unresolved merge conflict mid-batch, marks already-merged peers as done before aborting (prevents re-dispatch). |
| `MergeLogService.cs` | Append-only merge transaction log at `.ralph-logs/merge-log.jsonl`. One entry per merged task per batch (ts, batchIndex, taskId, baseSha, mergedSha, stateMarked, smokeTest result). Auto-rollback adds a separate `event: "rollback"` entry referencing the revert SHA. Append failure is logged warn-level and is non-fatal. |
| `PlanChunker.cs` | Heuristic for large PRDs: estimates token count and surfaces a chunking decision box (suggests splitting if PRD > ~100k tokens) before sending to Claude. Also detects truncation signals (output token cap, incomplete JSON) so the user can split a too-large PRD instead of silently losing tasks. |
| `RalphPaths.cs` | Single source of truth for filesystem layout constants (`.ralph-logs/`, `.ralph-worktrees/`, ledger filenames, snapshot paths, branch prefix `ralph/`). |
| `RalphIgnoreGuard.cs` | Idempotent guard invoked on every worktree creation (task + smoke). Appends ralph artifact paths (`.ralph-logs/`, `.ralph-worktrees/`, `.ralph-smoke/`) to `.git/info/exclude` (local-only, never committed) so accidental `git add .` can't promote them. Detects pre-existing tracked entries (especially `.ralph-smoke` as a gitlink — which causes per-batch rebase preflight failures once smoke worktree HEAD advances) and fails fast with a copy-paste remediation hint (`git rm --cached -r ... && git commit`). |
| `WorktreeService.cs` | Git worktree lifecycle: create (with optional `--shared`), rebase-advance before merge, merge, conflict file extraction, abort, cleanup, stale detection. Tags Ralph-created branches with `branch.{name}.ralphManaged=true` config marker so user-owned `ralph/*` branches are never silently deleted; falls back to detecting branches still bound to a worktree under `.ralph-worktrees/`. |
| `RollbackService.cs` | Snapshot capture/restore for `--rollback`. `--plan` writes pre-plan/post-plan snapshots (HEAD + tasks.json + PRD) under `.ralph-logs/rollback/`. `RollbackCommand` decides which snapshot to apply by inspecting `state.json` (any `done:true` → post-plan; otherwise pre-plan). `--run` never touches snapshots. |
| `VerificationRunner.cs` | Runs `verification.command` after Claude; exit-code-based ground truth. POSIX `/bin/sh -c`, Windows `cmd /c`. |
| `VerificationLoop.cs` | Wraps `VerificationRunner` with the self-fix retry loop (`workflow.verifyRetries`, default 1). Records each attempt to `validation.jsonl`. |
| `CostTracker.cs` | Records per-call usage to `.ralph-logs/cost.jsonl`; cumulative cache shared across dispatches. Loads embedded `pricing.json` (override at `~/.ralph/pricing.json`). |
| `BudgetGate.cs` | Cumulative cost ceiling (`--budget-usd`); 80% warning, 100% blocks new dispatches. |
| `NotificationService.cs` | Session-completion webhook (Slack/Discord/generic auto-detect by hostname). |
| `LogRotator.cs` | Deletes old logs in `.ralph-logs/` (default retention 30 days; preserves cost.jsonl, validation.jsonl). |
| `DurationParser.cs` | Parses `30m` / `1h` / `90s` / `1800` for `--task-timeout`. |
| `TaskProgressTracker.cs` | Live Spectre.Console table for parallel runs. |
| `GitService.cs` | Git ops: init, commit, branch management, auto initial commit, deadlock-safe stdout/stderr piping. |
| `GraphRenderer.cs` | ASCII task dependency graph with parallel/sequential visualization. |
| `RalphLogger.cs` | Thread-safe file logger writing to `.ralph-logs/`. |

### Execution Modes

- `--run [file]` — Auto mode: parallel by default (uses git worktrees), falls back to sequential for single tasks.
- `--run --sequential` — Force sequential execution (no worktrees, via `SequentialRunner`).
- `--run --max-parallel N` — Cap concurrent tasks.
- `--interactive` — Prompts before each task.
- `--dry-run` — Simulates execution; tasks.json restored on exit (try/finally guarantee).
- `--task <id>` — Runs a single task by ID; honors deps unless `--force`.

### Parallel Execution Flow

1. Ensure at least one commit exists (required for worktree creation).
2. Detect and clean stale worktrees.
3. Group independent tasks into parallel batches via `TaskManager` topological layering.
4. Create a git worktree per task (`ralph/{taskId}` branch under `.ralph-worktrees/`). Optionally `git worktree add --shared` (`--shared-worktrees`).
5. Run Claude Code in each worktree concurrently (live progress table).
6. `VerificationLoop` runs `verification.command` if defined; up to `workflow.verifyRetries` self-fix retries (default 1) on failure.
7. `MergeOrchestrator` per task: normalize tasks.json against base → validate declared vs actual `modifiedFiles` (`--strict-files` aborts on undeclared) → rebase-advance onto latest base → merge with primary strategy → if conflicts, run the `conflictStrategies` chain (auto-* / claude / abort).
8. Mark `done: true` thread-safely in `.ralph-logs/state.json` (atomic save, orchestrator-only writer). tasks.json is **never** rewritten or committed during `--run` — spec is immutable from Ralph's side, eliminating per-batch tasks.json merge reconciliation.
9. Run a post-merge smoke test on base (`workflow.smokeTest` or auto-inferred from repo-root markers; disabled by `--no-smoke-test`).
10. Advance to the next batch.

## Commands

```bash
# Standard workflow
ralph --plan PRD.md              # Generate tasks.json from PRD (atomic write)
ralph --plan PRD.md --llm-critique  # Plan + extra LLM critique pass
ralph --plan-prompt PRD.md       # Show full plan prompt without executing
ralph --validate                 # Validate tasks.json (cycles, deps, file overlaps, sensitive paths)
ralph --critique                 # Static critique of tasks.json (parallelism / verification gaps)
ralph --list                     # List pending tasks (parallel-eligibility shown)
ralph --graph                    # ASCII task dependency graph
ralph --dry-run                  # Preview execution (tasks.json restored on exit)
ralph --run                      # Execute all tasks (parallel by default)
ralph --run custom.json          # Positional file argument
ralph -f custom.json --run       # Or via global -f / --file

# Execution options
ralph --run --sequential         # Force sequential
ralph --run --max-parallel 4     # Cap concurrency
ralph --run --budget-usd 5.00    # Stop dispatching new tasks once cumulative cost ≥ $5
ralph --run --task-timeout 30m   # Per-Claude-call timeout (30m, 1h, 90s, or seconds)
ralph --run --strict-files       # Validate declared vs actual modifiedFiles after merge; abort on undeclared
ralph --run --shared-worktrees   # git worktree add --shared (saves disk/IO; auto-fallback)
ralph --run --no-smoke-test      # Skip post-merge smoke test
ralph --run --smoke-test "..."   # One-shot smoke test override (bypasses workflow.smokeTest + auto-infer)
ralph --run --auto-rollback-on-smoke-fail  # Opt-in: revert this batch's merges if smoke test fails
ralph --run --model opus         # Model override — applies to ALL tasks for this run.
                                 # When omitted, each task runs on its planner-assigned model
                                 # (`task.model`: opus for reasoning-heavy, sonnet for routine),
                                 # falling back to sonnet if the planner left it unset.
                                 # --plan itself still defaults to opus (reasoning-heavy).
                                 # Each task's chosen model is printed/logged at task start.

# Single task
ralph --task <id>                # Honors dependsOn
ralph --task <id> --force        # Bypass dependency / validation checks

# Monitoring
ralph --status                   # Progress dashboard with parallel batch info
ralph --cost                     # Cumulative token usage and estimated USD cost
ralph --logs                     # List log files
ralph --logs <task-id>           # View specific task log
ralph --logs --live <task-id>    # Live tail (like tail -f)
ralph --logs --cleanup           # Delete logs older than retention (default 30d)
ralph --show-prompt <id>         # Show the full Claude prompt for a task

# Maintenance
ralph --interactive              # Run tasks interactively
ralph --prompts                  # Show all task prompts
ralph --reset                    # Reset all task progress to pending (clears .ralph-logs/state.json; tasks.json untouched)
ralph --rollback                 # Restore state from before last --plan / --run (destructive; --force to bypass confirmation)
ralph --worktree-cleanup         # Clean up stale worktrees
ralph --version                  # Print ralph version (alias: -v)
```

## Dependencies

- **claude** — Claude Code CLI, invoked with `--dangerously-skip-permissions --output-format stream-json`.
- **git** — Auto-commit after each task completion, worktree-based parallel execution.
- **.NET 8 SDK** — Build only (published binary is self-contained).

## Environment Variables

| Variable | Default | Description |
|---|---|---|
| `MAX_RETRIES` | 2 | Claude Code retry attempts |
| `RETRY_DELAY` | 5 | Seconds between retries |
| `RALPH_MAX_PARALLEL` | 0 (use tasks.json) | Override max concurrent tasks |
| `RALPH_PARALLEL` | true | Set to `false` to disable parallel execution |
| `RALPH_STRICT_FILES` | false | Set to `true` to enable `--strict-files` |
| `RALPH_SHARED_WORKTREES` | false | Set to `true` to enable `--shared-worktrees` |
| `RALPH_NO_SMOKE_TEST` | false | Set to `true` or `1` to disable post-merge smoke test |
| `RALPH_SMOKE_TEST_COMMAND` | (unset) | One-shot smoke test command override; CLI `--smoke-test` wins, then this, then `workflow.smokeTest`, then auto-infer. |
| `RALPH_BUDGET_USD` | (unset) | Cumulative cost ceiling. CLI `--budget-usd` wins. |
| `RALPH_TASK_TIMEOUT_SEC` | (unset) | Per-Claude-call timeout (seconds). CLI `--task-timeout` wins. |
| `RALPH_AUTO_ROLLBACK_ON_SMOKE_FAIL` | false | Set to `true`/`1` to enable opt-in auto-rollback when post-merge smoke test fails. CLI `--auto-rollback-on-smoke-fail` wins, then this, then `workflow.autoRollbackOnSmokeFail`. |
| `RALPH_WEBHOOK_URL` | (unset) | Default session-completion webhook |
| `RALPH_LOG_RETENTION_DAYS` | 30 | Auto-delete logs older than N days |

Priority for shared knobs: CLI flag > env var > workflow setting in tasks.json > built-in default.

## Workflow Settings (tasks.json)

```jsonc
{
  "workflow": {
    "onTaskComplete": { "commitChanges": true, "commitMessageTemplate": "[Task #{taskId}] {taskTitle}" },
    "parallel": {
      "enabled": true,
      "maxConcurrent": 5,
      "conflictStrategies": ["auto-theirs", "claude"],
      "sharedWorktreeObjects": false
    },
    "notifications": { "onComplete": "https://...", "format": "slack" },
    "logRetentionDays": 30,
    "budgetUsd": 10.00,
    "taskTimeoutSec": 1800,
    "maxRetries": 2,
    "retryDelay": 5,
    "verifyRetries": 1,
    "smokeTest": { "command": "dotnet build", "timeoutSec": 180 },
    "autoRollbackOnSmokeFail": false,
    "categories": ["plan", "implementation", "testing", "commit"]
  }
}
```

- `verifyRetries` — self-fix retries when `verification.command` exits non-zero (default 1, 0 disables).
- `smokeTest` — single command run on the base branch after each merge batch. Auto-inferred from repo-root markers (`*.csproj`/`*.sln` → `dotnet build`, `package.json` → `npm test`, `Cargo.toml` → `cargo build`, `go.mod` → `go build`). Explicit value always wins. Disable with `--no-smoke-test` / `RALPH_NO_SMOKE_TEST=true`.
- `autoRollbackOnSmokeFail` — opt-in auto-rollback when smoke test fails. Captures pre-batch SHA, then revert-merges this batch's commits and resets the affected tasks' `done` bits to pending. Held (no rollback) when working tree is dirty or external commits intervened. CLI `--auto-rollback-on-smoke-fail` and env `RALPH_AUTO_ROLLBACK_ON_SMOKE_FAIL=true` are equivalent and override this.
- `categories` — override the default 4-stage list (`plan / implementation / testing / commit`) when generating plans.

## Conventions

- Task IDs use kebab-case: `{feature}-plan`, `{feature}-impl`, `{feature}-test`, `{feature}-commit`.
- Git commit messages must be in Korean.
- Sensitive files (.env, *.pem, *.key, credentials.json, id_rsa, id_ed25519, etc.) are auto-excluded from commits and flagged by `PlanValidator`.
- Session logs: `.ralph-logs/ralph-YYYYMMDD-HHMMSS.log`.
- Task logs (parallel): `.ralph-logs/{taskId}.log`.
- Cost ledger: `.ralph-logs/cost.jsonl` (preserved across log rotation). Failed cost-jsonl writes are journaled to `.ralph-logs/cost-failures.jsonl` as a fallback.
- Verification ledger: `.ralph-logs/validation.jsonl` (preserved across log rotation).
- Merge transaction log: `.ralph-logs/merge-log.jsonl` (preserved). One entry per merged task per batch + separate entries for auto-rollback events.
- Progress state: `.ralph-logs/state.json` (orchestrator-only writer, never git-tracked, atomic tmp+rename). Cleared by `--reset`.
- Worktrees created at `.ralph-worktrees/{taskId}` (auto-cleaned after execution; force via `ralph --worktree-cleanup`).
- Schema and pricing are embedded in the binary as `EmbeddedResource`; pricing override at `~/.ralph/pricing.json`.
- `tasks.json` writes are atomic (tmp + rename) — never partial files on crash. Ralph only writes tasks.json on `--plan` (full regeneration) and on legacy v1→v2 migration (one-time done-key strip); not during `--run`.
- Legacy v1 tasks.json files (with `done:true/false` keys) auto-migrate on first load: done bits are moved to `.ralph-logs/state.json` and tasks.json is re-saved without the keys. Idempotent.
- Dry-run restores `tasks.json` via try/finally — interruption is safe.
- Already-merged tasks are not auto-rolled-back **by default**. Opt in via `--auto-rollback-on-smoke-fail` (also env / `workflow.autoRollbackOnSmokeFail`) — when smoke fails, this batch's merges are reverted and the affected tasks' `done` bits reset; held if working tree is dirty or external commits intervened. Otherwise use `--strict-files` and `workflow.smokeTest` to catch problems before merge becomes durable.
- On unresolved merge conflict mid-batch, `MergeOrchestrator` marks already-merged peer tasks as done (in `state.json`) and writes their merge-log entries (smoke="skipped") before aborting — prevents next `--run` from re-dispatching tasks whose changes are already in base.
