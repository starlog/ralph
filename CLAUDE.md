# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Ralph is a CLI task orchestrator that generates execution plans from PRD (Product Requirements Document) files and runs them in parallel (or sequentially) using Claude Code. It follows a 4-phase pattern per feature: **plan → implementation → testing → commit**, with dependency tracking between tasks. Built with .NET 8 for cross-platform support (Windows, macOS, Linux). Current version: **v1.1**.

## Architecture

- **Ralph/** — .NET 8 C# project producing a self-contained single-file binary.
- **Ralph.Tests/** — xUnit test project (worktree integration tests, plan validator tests, etc.).
- **ralph-schema.json** — JSON Schema (2020-12) defining the `tasks.json` structure: tasks array (id/title/done/prompt/dependsOn/outputFiles/modifiedFiles/subtasks/verification), workflow settings (parallel, notifications, logRetentionDays, budgetUsd, taskTimeoutSec, maxRetries, retryDelay), and optional apiSpecs/samplePages. Embedded in the binary as `EmbeddedResource`.
- **pricing.json** — Per-model token pricing used by `CostTracker`. Embedded as `EmbeddedResource`; can be overridden by `~/.ralph/pricing.json`.

### Key Services (Ralph/Services/)

| Service | Purpose |
|---|---|
| `PlanGenerator.cs` | Sends PRD + schema to Claude (tools disabled, opus model) to produce tasks.json. Atomic write (tmp + rename). |
| `PlanValidator.cs` | Validates tasks.json: cycles, dangling deps, duplicate IDs, file overlaps, sensitive paths, eval-string body checks. |
| `ClaudeService.cs` | Runs Claude Code with streaming JSON output, retry logic (MAX_RETRIES/RETRY_DELAY), per-call timeout. |
| `PromptBuilder.cs` | Builds the prompt sent to Claude — adds Scope, dependency outputs, sibling context, hard prohibitions. |
| `TaskManager.cs` | Loads/saves/queries tasks.json; dependency DAG traversal, parallel batch grouping, topological layering. |
| `ParallelExecutor.cs` | Worktree-based parallel execution with live dashboard, merge handling, conflict-strategy chain. |
| `WorktreeService.cs` | Git worktree lifecycle: create, rebase-advance before merge, merge, cleanup, stale detection. |
| `VerificationRunner.cs` | Runs `verification.command` after Claude; exit-code-based ground truth (one self-fix retry on failure). |
| `CostTracker.cs` | Records per-call usage to `.ralph-logs/cost.jsonl`; cumulative cache shared across dispatches. |
| `BudgetGate.cs` | Cumulative cost ceiling (`--budget-usd`); 80% warning, 100% blocks new dispatches. |
| `NotificationService.cs` | Session-completion webhook (Slack/Discord/generic auto-detect by hostname). |
| `LogRotator.cs` | Deletes old logs in `.ralph-logs/` (default retention 30 days; preserves cost.jsonl, validation.jsonl). |
| `DurationParser.cs` | Parses `30m` / `1h` / `90s` / `1800` for `--task-timeout`. |
| `TaskProgressTracker.cs` | Live Spectre.Console table for parallel runs. |
| `GitService.cs` | Git ops: init, commit, branch management, auto initial commit, deadlock-safe stdout/stderr piping. |
| `GraphRenderer.cs` | ASCII task dependency graph with parallel/sequential visualization. |
| `RalphLogger.cs` | Thread-safe file logger writing to `.ralph-logs/`. |

### Execution Modes

- `--run [file]` — Auto mode: parallel by default (uses git worktrees), falls back to sequential for single tasks
- `--run --sequential` — Force sequential execution (no worktrees)
- `--run --max-parallel N` — Cap concurrent tasks
- `--interactive` — Prompts before each task
- `--dry-run` — Simulates execution; tasks.json restored on exit (try/finally guarantee)
- `--task <id>` — Runs a single task by ID; honors deps unless `--force`

### Parallel Execution Flow

1. Ensure at least one commit exists (required for worktree creation)
2. Detect and clean stale worktrees
3. Group independent tasks into parallel batches
4. Create a git worktree per task (`ralph/{taskId}` branch under `.ralph-worktrees/`)
5. Run Claude Code in each worktree concurrently (live progress table)
6. Run `verification.command` if defined; one self-fix retry on failure
7. Rebase the worktree branch onto the latest base before merge (advance)
8. Sequentially merge completed branches; resolve conflicts via the strategy chain
   (`conflictStrategies`: ordered fallback, e.g. `["auto-theirs", "claude"]`)
9. Optionally validate `modifiedFiles` post-merge (`--strict-files`)

## Commands

```bash
# Standard workflow
ralph --plan PRD.md              # Generate tasks.json from PRD (atomic write)
ralph --plan-prompt PRD.md       # Show full plan prompt without executing
ralph --validate                 # Validate tasks.json (cycles, deps, file overlaps, sensitive paths)
ralph --list                     # List pending tasks (parallel-eligibility shown)
ralph --dry-run                  # Preview execution (tasks.json restored on exit)
ralph --run                      # Execute all tasks (parallel by default)
ralph --run custom.json          # Positional file argument
ralph -f custom.json --run       # Or via global -f / --file

# Execution options
ralph --run --sequential         # Force sequential
ralph --run --max-parallel 4     # Cap concurrency
ralph --run --budget-usd 5.00    # Stop dispatching new tasks once cumulative cost ≥ $5
ralph --run --task-timeout 30m   # Per-Claude-call timeout (30m, 1h, 90s, or seconds)
ralph --run --strict-files       # Validate declared vs actual modifiedFiles after merge
ralph --run --model sonnet       # Model override (sonnet | opus, default: opus)

# Single task
ralph --task <id>                # Honors dependsOn
ralph --task <id> --force        # Bypass dependency / validation checks

# Monitoring
ralph --graph                    # ASCII task dependency graph
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
ralph --reset                    # Reset all tasks to pending
ralph --worktree-cleanup         # Clean up stale worktrees
```

## Dependencies

- **claude** — Claude Code CLI, invoked with `--dangerously-skip-permissions --output-format stream-json`
- **git** — Auto-commit after each task completion, worktree-based parallel execution
- **.NET 8 SDK** — Build only (published binary is self-contained)

## Environment Variables

| Variable | Default | Description |
|---|---|---|
| `MAX_RETRIES` | 2 | Claude Code retry attempts |
| `RETRY_DELAY` | 5 | Seconds between retries |
| `RALPH_MAX_PARALLEL` | 0 (use tasks.json) | Override max concurrent tasks |
| `RALPH_PARALLEL` | true | Set to `false` to disable parallel execution |
| `RALPH_STRICT_FILES` | false | Set to `true` to enable `--strict-files` |
| `RALPH_BUDGET_USD` | (unset) | Cumulative cost ceiling. CLI `--budget-usd` wins. |
| `RALPH_TASK_TIMEOUT_SEC` | (unset) | Per-Claude-call timeout (seconds). CLI `--task-timeout` wins. |
| `RALPH_WEBHOOK_URL` | (unset) | Default session-completion webhook |
| `RALPH_LOG_RETENTION_DAYS` | 30 | Auto-delete logs older than N days |

Priority for shared knobs: CLI flag > env var > workflow setting in tasks.json > built-in default.

## Conventions

- Task IDs use kebab-case: `{feature}-plan`, `{feature}-impl`, `{feature}-test`, `{feature}-commit`
- Git commit messages must be in Korean
- Sensitive files (.env, *.pem, *.key, credentials.json, id_rsa, id_ed25519, etc.) are auto-excluded from commits and flagged by `PlanValidator`
- Session logs: `.ralph-logs/ralph-YYYYMMDD-HHMMSS.log`
- Task logs (parallel): `.ralph-logs/{taskId}.log`
- Cost ledger: `.ralph-logs/cost.jsonl` (preserved across log rotation)
- Verification ledger: `.ralph-logs/validation.jsonl` (preserved across log rotation)
- Worktrees created at `.ralph-worktrees/{taskId}` (auto-cleaned after execution)
- Schema and pricing are embedded in the binary as `EmbeddedResource`; pricing override at `~/.ralph/pricing.json`
- `tasks.json` writes are atomic (tmp + rename) — never partial files on crash
- Dry-run restores `tasks.json` via try/finally — interruption is safe
