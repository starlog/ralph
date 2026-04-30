# Ralph

**English** | [한국어](README.ko.md)

A CLI task orchestrator that turns a PRD (Product Requirements Document) into an executable plan and runs it through Claude Code — in parallel by default, on git worktrees, with dependency tracking, exit-code-based verification, conflict-aware merging, cost budgets, smoke testing, and live progress monitoring. Built on .NET 8 for cross-platform support (Windows, macOS, Linux). Current version: **v1.22**.

The first Ralph implementation with **parallel git worktree execution**: independent features run concurrently as isolated Claude Code agents and are merged back automatically.

---

## Table of Contents

- [Why Ralph](#why-ralph)
- [Security Note](#-security-note)
- [Comparison](#comparison-with-other-ralph-implementations)
- [How It Works](#how-it-works)
- [Case Study — Ralph Fixes Itself](#case-study--ralph-fixes-itself)
- [Versions](#versions)
- [Requirements](#requirements)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Commands](#commands)
- [Execution Options](#execution-options)
- [Environment Variables](#environment-variables)
- [Parallel Execution Flow](#parallel-execution-flow)
- [Failure Handling & Resume](#failure-handling--resume)
- [Conflict Resolution Strategies](#conflict-resolution-strategies)
- [Verification Gate](#verification-gate)
- [Smoke Test (post-merge)](#smoke-test-post-merge)
- [Cost Tracking & Budget Gate](#cost-tracking--budget-gate)
- [Webhook Notifications](#webhook-notifications)
- [Live Monitoring](#live-monitoring)
- [tasks.json Structure](#tasksjson-structure)
- [Workflow Settings](#workflow-settings)
- [Design Note — Why is `tasks.json` mutable + declarative?](#design-note--why-is-tasksjson-mutable--declarative)
- [Writing PRDs for Parallel Execution](#writing-prds-for-parallel-execution)
- [Logs](#logs)
- [Example](#example)
- [Things to Consider](#things-to-consider)
- [Troubleshooting](#troubleshooting)
- [Security](#security)
- [Contributing & Development](#contributing--development)
- [GitHub Topics](#github-topics)

---

## Why Ralph

| Capability | What it gives you |
|---|---|
| **Parallel by default** | Independent features run concurrently in isolated git worktrees — no manual orchestration. |
| **Dependency-aware** | A topological DAG (`dependsOn`) drives scheduling — dependents wait, siblings parallelise. |
| **Verification gate** | `verification.command` exit code is the ground truth; Claude's self-report is ignored. One self-fix retry by default. |
| **Conflict strategy chain** | Try `auto-theirs` first, fall back to `claude`, fall back to `abort` — configured per project. |
| **Cost budget** | Hard ceiling (`--budget-usd`) with an 80% warning. Per-call usage is recorded as an append-only ledger. |
| **Smoke test (post-merge)** | A single command runs on the base branch after each batch's merges, catching semantic conflicts that survived auto-merge. |
| **Resume-safe** | `done: true` is written atomically per task; re-run picks up exactly where it left off. |
| **Plan critique** | Static `--critique` finds parallelism / verification gaps; optional `--llm-critique` adds an LLM-driven review of PRD vs. generated plan. |
| **Single self-contained binary** | No .NET runtime install on target machines. Schema and pricing are embedded. |

## ⚠️ Security Note

Ralph runs Claude Code directly on the host machine with `--dangerously-skip-permissions`. Untrusted PRDs or external `tasks.json` files should be executed in an isolated environment (separate user account, VM, or container). The following may be exposed:

- Credentials in `~/.ssh`, `~/.aws`, `~/.config`
- API keys in environment variables
- Read access to all host files

`PlanValidator` flags sensitive paths in declared `outputFiles` / `modifiedFiles`, but this is a heuristic — not a sandbox.

## Comparison with Other Ralph Implementations

| Feature | snarktank/ralph | PageAI/ralph-loop | starlog/ralph |
|---------|----------------|-------------------|---------------|
| Parallel execution | ❌ | ❌ | ✅ |
| Windows support | ❌ | ❌ | ✅ |
| DAG dependencies | ❌ | partial | ✅ |
| Cost tracking & budget gate | ❌ | ❌ | ✅ |
| Verification gate (exit code) | ❌ | ❌ | ✅ |
| Post-merge smoke test | ❌ | ❌ | ✅ |
| Webhook notifications | ❌ | ❌ | ✅ |
| Single binary | ❌ | ❌ | ✅ |

## How It Works

Ralph follows a **4-phase pattern** per feature (configurable via `workflow.categories`):

```
plan → implementation → testing → commit
```

The four phases inside one feature are sequential by `dependsOn`. Independent features run **in parallel** through git worktrees and are merged back into the base branch.

```
user-auth-plan ─→ user-auth-impl ─→ user-auth-test ─→ user-auth-commit ─┐
                                                                          ├─→ main-plan ─→ ...
payment-plan ─→ payment-impl ─→ payment-test ─→ payment-commit ──────────┘
   (parallel execution)                                       (sequential after merge)
```

## Case Study — Ralph Fixes Itself

Ralph was used to fix bugs found by static analysis of its own source. The PRD and the resulting parallel run exercise every part of the pipeline described above.

- **Starting point:** `bugfix.md` collects **9 independent bugs** in Ralph's own services (`LogRotator`, `GitService`, `VerificationRunner`, `RalphLogger`, `WorktreeService`, `ParallelExecutor`, `Program`, `PlanGenerator`) plus **1 optional cosmetic refactor** — each scoped to one or two files with declared `modifiedFiles`.
- **Decomposition:** `ralph --plan bugfix.md` turned the PRD into a `tasks.json` of small `*-impl` / `*-commit` task pairs. Seven bugs touch entirely disjoint files and form a single **fully parallel layer**; the two `WorktreeService.cs` features (Feature 5 and the optional Feature 10) are serialised through `dependsOn`.
- **Execution:** `ralph --run` dispatches up to **5 worktrees concurrently** (`workflow.parallel.maxConcurrent: 5`), each on its own `ralph/{taskId}` branch under `.ralph-worktrees/`, with Claude Code streaming into per-task logs.
- **Merge:** every worktree is rebased onto the latest base just before merge; `conflictStrategies: ["auto-theirs", "claude"]` resolves trivial conflicts with `-X theirs` and escalates the rest to Claude.
- **Verification:** each task carries a `verification.command` (`dotnet build` or a targeted `dotnet test --filter ...`) whose exit code is the ground truth — Claude's self-report is ignored. One self-fix retry is allowed before a task is marked failed and excluded from merge.
- **Outcome:** the same orchestrator that the PRD targets runs the fixes against itself — Ralph produces the plan, schedules the parallel batch, merges the branches, and verifies each fix end-to-end without human intervention beyond the initial `ralph --run`.

Full PRD: [bugfix.md](bugfix.md)

## Versions

| Version | Implementation | Platforms | Key Features |
|---|---|---|---|
| v0.1 | `ralph.sh` / `ralph.ps1` (Bash / PowerShell, now under [`legacy/`](legacy/)) | macOS, Linux, Windows | Sequential execution |
| v0.6 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | Parallel execution, worktrees, live logs |
| v0.7 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | `--graph` task dependency visualization |
| v1.0 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | Cost tracker, plan validator, prompt builder, webhook notifications, log rotation |
| v1.1 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | Verification gate, conflict-strategy chain, post-merge smoke test, `--task-timeout`, `--budget-usd`, `--strict-files`, `--shared-worktrees`, `--critique` / `--llm-critique`, worktree rebase-advance |

## Requirements

| Tool | Description |
|---|---|
| [Claude Code](https://claude.ai/code) | Claude Code CLI |
| [git](https://git-scm.com/) | Version control (required for worktree-based parallel execution; 2.10+ for `--shared-worktrees`) |

## Installation

### Option 1: Install Script (build and install compiled binary locally)

Requires the .NET 8 SDK. The script publishes a self-contained Ralph binary for your platform, then copies it into a directory on your PATH (defaults to `~/bin`). The result is the same single-file binary as Option 2 — you just build it yourself instead of downloading it.

**macOS / Linux:**

```bash
git clone https://github.com/starlog/ralph.git
cd ralph
./install.sh
```

**Windows (PowerShell):**

```powershell
git clone https://github.com/starlog/ralph.git
cd ralph
.\install.ps1
```

### Option 2: Download a Prebuilt Binary

Grab the matching binary from [GitHub Releases](https://github.com/starlog/ralph/releases). No .NET SDK install required.

| Platform | File |
|---|---|
| Windows (x64) | `ralph-vX.X.X-win-x64.zip` |
| macOS (Intel) | `ralph-vX.X.X-osx-x64.tar.gz` |
| macOS (Apple Silicon) | `ralph-vX.X.X-osx-arm64.tar.gz` |
| Linux (x64) | `ralph-vX.X.X-linux-x64.tar.gz` |

```bash
# Example: Linux
curl -LO https://github.com/starlog/ralph/releases/latest/download/ralph-v1.1.0-linux-x64.tar.gz
tar -xzf ralph-v1.1.0-linux-x64.tar.gz
sudo mv ralph /usr/local/bin/
```

The binary is self-contained, so the .NET runtime is not required.

### Option 3: Package Manager

```bash
# macOS / Linux — Homebrew tap
brew tap starlog/ralph https://github.com/starlog/ralph
brew install ralph

# Windows — Scoop (custom manifest)
scoop install https://raw.githubusercontent.com/starlog/ralph/main/scoop/ralph.json
```

Manifests live under [`Formula/ralph.rb`](Formula/ralph.rb) and [`scoop/ralph.json`](scoop/ralph.json) and track the latest GitHub release.

## Quick Start

```bash
# 1. Generate a task plan from a PRD
ralph --plan docs/PRD.md

# 2. (Optional) Critique the generated plan
ralph --critique                  # static checks: parallelism / verification / dep oddities
ralph --plan docs/PRD.md --llm-critique  # adds an LLM-driven review (extra cost)

# 3. Validate the generated plan (cycles, deps, file overlaps, sensitive paths)
ralph --validate

# 4. Inspect the generated tasks
ralph --list
ralph --graph

# 5. Preview execution (no changes are made)
ralph --dry-run

# 6. Run the entire pipeline
ralph --run
```

## Commands

| Command | Description |
|---|---|
| `--plan <file>` | Analyze a PRD file and produce `tasks.json` (atomic write) |
| `--plan-prompt <file>` | Show the full plan prompt without executing |
| `--validate` | Validate `tasks.json` (cycles, dangling deps, duplicate IDs, file overlaps, sensitive paths) |
| `--critique` | Static critique of `tasks.json` (parallelism / verification gaps / dep oddities) |
| `--run [file]` | Execute all pending tasks (parallel by default). Defaults to `tasks.json` |
| `--dry-run [file]` | Simulate execution; `tasks.json` is restored on exit |
| `--task <id>` | Run a single task by ID (use `--force` to bypass dependency checks) |
| `--interactive` | Interactive mode — confirm before each task |
| `--list`, `-l` | List pending tasks (shows parallel-eligibility) |
| `--graph`, `-g` | Render the task dependency graph in ASCII |
| `--prompts`, `-p` | Print the Claude prompt for every task |
| `--show-prompt <id>` | Print the full prompt sent to Claude for one task |
| `--status`, `-s` | Progress dashboard with parallel batch info |
| `--cost` | Cumulative token usage and estimated USD cost |
| `--reset`, `-r` | Reset all tasks back to pending |
| `--logs` | List log files (session + per-task) |
| `--logs <task-id>` | Print a specific task log |
| `--logs --live <task-id>` | Tail a task log live (like `tail -f`) |
| `--logs --cleanup` | Delete logs older than the retention period |
| `--worktree-cleanup` | Remove leftover worktrees |
| `--help`, `-h` | Show help |

### Execution Options

| Option | Description |
|---|---|
| `-f`, `--file <path>` | Use a custom tasks file (works with most commands) |
| `--sequential` | Disable parallel execution; run tasks one at a time |
| `--max-parallel N` | Cap the number of concurrent tasks |
| `--force` | Bypass dependency / validation checks (with `--task` or `--run`) |
| `--strict-files` | Validate declared vs actual `modifiedFiles` after merge; abort on undeclared writes |
| `--shared-worktrees` | Use `git worktree add --shared` to share `.git` objects across worktrees (saves disk/IO; auto-falls-back if unsupported) |
| `--no-smoke-test` | Skip the post-merge smoke test (otherwise auto-inferred or from `workflow.smokeTest`) |
| `--budget-usd <amt>` | Stop dispatching new tasks once cumulative cost reaches `<amt>` USD |
| `--task-timeout <dur>` | Per-Claude-call timeout (e.g. `30m`, `1h`, `90s`, `1800`) — kills hung calls |
| `--llm-critique` | After `--plan`, run an extra LLM-driven critique of the PRD + generated plan (off by default; adds one LLM call) |
| `--model <name>` | Model: `sonnet` or `opus` (default: `opus`) |
| `--debug` | Print Claude stream events for diagnostics |

### Custom tasks.json

Two ways to point at a non-default file:

```bash
ralph --run my-project-tasks.json     # positional (run/dry-run/list/graph/etc.)
ralph -f my-project-tasks.json --run  # global -f / --file flag
```

### Interactive Mode

`--interactive` presents choices before each task:

- `Yes - Execute` — run the task
- `Preview prompt` — show the prompt without running
- `Skip` — skip this task
- `Quit` — exit

## Environment Variables

| Variable | Default | Description |
|---|---|---|
| `MAX_RETRIES` | 2 | Number of Claude Code retry attempts on failure |
| `RETRY_DELAY` | 5 | Seconds between retries |
| `RALPH_MAX_PARALLEL` | 0 (use tasks.json) | Override the maximum number of concurrent tasks |
| `RALPH_PARALLEL` | true | Set to `false` to disable parallel execution |
| `RALPH_STRICT_FILES` | false | Set to `true` to enable `--strict-files` by default |
| `RALPH_SHARED_WORKTREES` | false | Set to `true` to enable `--shared-worktrees` by default |
| `RALPH_NO_SMOKE_TEST` | false | Set to `true` or `1` to disable post-merge smoke test |
| `RALPH_BUDGET_USD` | unset | Cumulative cost ceiling — CLI `--budget-usd` wins |
| `RALPH_TASK_TIMEOUT_SEC` | unset | Per-Claude-call timeout (seconds) — CLI `--task-timeout` wins |
| `RALPH_WEBHOOK_URL` | unset | Default session-completion webhook |
| `RALPH_LOG_RETENTION_DAYS` | 30 | Auto-delete logs older than N days |

Priority for shared knobs: **CLI flag > env var > `workflow` setting in `tasks.json` > built-in default.**

```bash
# Linux/macOS
MAX_RETRIES=3 ralph --run
RALPH_MAX_PARALLEL=4 ralph --run
RALPH_BUDGET_USD=10.00 ralph --run
RALPH_TASK_TIMEOUT_SEC=1800 ralph --run

# Windows (PowerShell)
$env:MAX_RETRIES=3; ralph --run
$env:RALPH_PARALLEL="false"; ralph --run    # Force sequential mode
```

## Parallel Execution Flow

Ralph runs independent tasks concurrently using git worktrees. The dependency graph drives scheduling — any task without `dependsOn` becomes a parallel candidate.

```
ralph --run
```

1. Ensure at least one commit exists (required for worktree creation; an initial commit is auto-created if missing).
2. Detect and clean stale worktrees.
3. Group ready tasks into parallel batches via topological layering.
4. Create a git worktree per task (`ralph/{taskId}` branch under `.ralph-worktrees/`); optionally shared via `--shared-worktrees`.
5. Run Claude Code concurrently in each worktree (live progress dashboard).
6. Run `verification.command` if defined; up to `workflow.verifyRetries` self-fix retries on failure.
7. Rebase the worktree branch onto the latest base (advance) just before merge.
8. Optionally validate that the merge wrote only the declared `modifiedFiles` (`--strict-files` aborts on undeclared writes).
9. Sequentially merge completed branches back into the base branch.
10. Resolve any merge conflicts via the configured strategy chain (`conflictStrategies`).
11. Mark `done: true` thread-safely, atomic-save `tasks.json`, commit the change.
12. Run a post-merge smoke test (auto-inferred or from `workflow.smokeTest`) on the base branch.
13. Advance to the next batch (newly unblocked tasks).
14. Fall back to in-place execution when only one task remains.

## Failure Handling & Resume

What happens when a parallel batch partially fails:

| Event | Behavior |
|---|---|
| Claude fails for one task in a batch | The other tasks in the same batch **continue and merge normally**. The failed task's worktree is cleaned up; its `done` flag stays `false`. |
| `verification.command` fails | Up to `workflow.verifyRetries` (default 1) self-fix retries with stdout/stderr fed back as context. If still failing, the task is marked failed and **excluded from merge**. |
| Pre-merge scope violation (`--strict-files`) | Worktree fails fast before merge — saves cleanup cost. Other tasks in the batch are not affected. |
| Merge conflict unresolvable by the strategy chain | Remaining unmerged worktrees are cleaned up; already-merged tasks **stay merged** (no rollback). |
| Post-merge `workflow.smokeTest` fails | Run stops with a non-zero exit. No merges are reverted; the smoke-test failure is logged and surfaced. |

**Resume after interruption:**
- `done: true` is written atomically per task — re-running `ralph --run` picks up exactly where it left off (only `done: false` tasks dispatch).
- If a worktree has uncommitted changes or commits ahead of base when `--run` starts, Ralph **does not silently delete it**. It prints the worktree path and asks you to merge/clean manually (or run `ralph --worktree-cleanup` to force-remove).
- Stale worktrees that are clean (cleanup was missed but no work was lost) are auto-removed.

**Already-merged tasks are not rolled back.** Ralph's design treats merge as the commit point — undoing requires a human-driven `git revert` or `git reset`. Use `--strict-files` and `workflow.smokeTest` to catch problems before the merge becomes durable.

## Conflict Resolution Strategies

Configured under `workflow.parallel.conflictStrategies` (chain) or the legacy `workflow.parallel.conflictStrategy` (single) in `tasks.json`. The chain is an **ordered fallback list** — the first entry decides the initial merge `-X` flag (for `auto-*`); the remaining entries are tried in order if the merge or previous step fails.

| Strategy | Behavior |
|---|---|
| `claude` | Claude Code analyzes conflict markers and merges both sides (recommended terminal step) |
| `abort` | Abort the merge and re-run the task in sequential mode |
| `auto-theirs` | Use git's `-X theirs` — prefer the worktree branch's changes |
| `auto-ours` | Use git's `-X ours` — prefer the base branch's changes |

Example — auto-merge trivial conflicts and only escalate to Claude for the cases `-X theirs` cannot resolve (add/add, rename/delete):

```json
"conflictStrategies": ["auto-theirs", "claude"]
```

After Claude resolves, the staged area is checked with `git diff --check --cached` to ensure no conflict markers leak into the commit.

## Verification Gate

Each task can declare a `verification.command` whose exit code is the ground truth — Claude's self-report is ignored. On non-zero exit, Ralph feeds stdout/stderr back to Claude for **self-fix retries** (count: `workflow.verifyRetries`, default 1) before failing the task.

```json
{
  "id": "math-impl",
  "verification": { "command": "go test ./...", "timeoutSec": 120 }
}
```

The command is run in the task's working directory (worktree for parallel mode, repo root for sequential) via `/bin/sh -c` on POSIX or `cmd /c` on Windows. Each attempt is logged to `.ralph-logs/validation.jsonl`.

Common commands: `pytest tests/`, `go test ./...`, `tsc --noEmit`, `dotnet test`, `npm test --silent`, `cargo test --quiet`.

## Smoke Test (post-merge)

After every parallel batch's merges complete, Ralph runs **one** smoke test on the base branch to catch semantic conflicts that survived auto-merge or Claude resolution.

Resolution priority:

1. `--no-smoke-test` / `RALPH_NO_SMOKE_TEST=true` — skip entirely.
2. Explicit `workflow.smokeTest` in `tasks.json`.
3. Auto-infer from repo-root markers:
   - `*.csproj` / `*.sln` → `dotnet build -nologo`
   - `package.json` → `npm test --silent`
   - `Cargo.toml` → `cargo build --quiet`
   - `go.mod` → `go build ./...`
4. No marker → smoke test is silently skipped.

Failure stops the run with a non-zero exit. No merges are reverted.

```json
"workflow": {
  "smokeTest": { "command": "dotnet build", "timeoutSec": 180 }
}
```

## Cost Tracking & Budget Gate

Per-call usage from Claude's `stream-json` `result` event is recorded to `.ralph-logs/cost.jsonl` (preserved across log rotation). `--budget-usd <amt>` (or `RALPH_BUDGET_USD`) blocks new dispatches once the cumulative cost reaches the ceiling, with a one-shot warning at 80%.

```bash
ralph --cost                            # show cumulative tokens and USD
ralph --run --budget-usd 5.00           # stop dispatching at $5
```

Pricing is loaded from the embedded `pricing.json`; override at `~/.ralph/pricing.json`.

The budget gate **does not interrupt in-flight tasks** — it blocks only new dispatches, so you can overshoot by the cost of any tasks already running.

## Webhook Notifications

A single webhook fires at session end. Resolution priority:

1. `workflow.notifications.onComplete` / `onFailure` in `tasks.json`
2. `RALPH_WEBHOOK_URL` env (global fallback)

`format` is auto-detected by hostname (`hooks.slack.com` → Slack, `discord(app)?.com` → Discord, else `generic`) and can be forced via `workflow.notifications.format`.

Slack uses `{text, blocks}`, Discord uses `{content, embeds}`, and `generic` posts Ralph's structured JSON.

## Live Monitoring

Tail a per-task log from another terminal during a parallel run:

```bash
# Terminal 1: run
ralph --run

# Terminal 2: live-tail one task
ralph --logs --live add-impl

# Terminal 3: live-tail another task
ralph --logs --live subtract-impl
```

The main `--run` console also shows a Spectre.Console live table with each worktree's status, elapsed time, and current Claude phase.

## tasks.json Structure

`tasks.json` is generated by `ralph --plan` or written by hand. The full schema lives in `ralph-schema.json` and is embedded in the binary.

### Minimal Example

```json
{
  "projectName": "my-project",
  "version": "1.0.0",
  "tasks": [
    {
      "id": "setup-plan",
      "title": "Project setup plan",
      "done": false,
      "phase": "phase1-setup",
      "category": "plan",
      "prompt": "Analyze the project structure and draft a setup plan...",
      "outputFiles": ["docs/setup-plan.md"]
    }
  ]
}
```

### Task Object

| Field | Required | Type | Description |
|---|---|---|---|
| `id` | **yes** | string | Unique kebab-case ID (`^[a-zA-Z0-9_-]+$`) |
| `title` | **yes** | string | Task title (≤ 200 chars) |
| `done` | **yes** | boolean | Completion flag — set to `true` automatically after execution |
| `description` | | string | Long-form description |
| `phase` | | string | Project phase (e.g. `"phase1"`, `"phase2"`) |
| `category` | | string | Category (`"plan"`, `"implementation"`, `"testing"`, `"commit"`, or any string in `workflow.categories`) |
| `prompt` | | string | Prompt sent to Claude Code; tasks without a prompt skip Claude |
| `outputFiles` | | string[] | Expected output file paths |
| `modifiedFiles` | | string[] | Files this task will edit — used for parallel merge-conflict detection and `--strict-files` |
| `dependsOn` | | string[] | Predecessor task IDs; missing means parallel-eligible |
| `subtasks` | | array | Optional subtasks |
| `verification` | | object | `{ command, timeoutSec? }` — exit-code-based verification (see Verification Gate above) |

## Workflow Settings

```json
{
  "workflow": {
    "onTaskComplete": {
      "commitChanges": true,
      "commitMessageTemplate": "[Task #{taskId}] {taskTitle}"
    },
    "parallel": {
      "enabled": true,
      "maxConcurrent": 5,
      "conflictStrategies": ["auto-theirs", "claude"],
      "sharedWorktreeObjects": false
    },
    "notifications": {
      "onComplete": "https://hooks.slack.com/services/XXX",
      "format": "slack"
    },
    "logRetentionDays": 30,
    "budgetUsd": 10.00,
    "taskTimeoutSec": 1800,
    "maxRetries": 2,
    "retryDelay": 5,
    "verifyRetries": 1,
    "smokeTest": { "command": "dotnet build", "timeoutSec": 180 },
    "categories": ["plan", "implementation", "testing", "commit"]
  }
}
```

| Setting | Default | Description |
|---|---|---|
| `parallel.enabled` | true | Enable parallel execution |
| `parallel.maxConcurrent` | 5 | Maximum concurrent tasks (capped at 10) |
| `parallel.conflictStrategy` | `"claude"` | Single legacy strategy (used only when `conflictStrategies` is unset) |
| `parallel.conflictStrategies` | (unset) | Ordered fallback chain — takes precedence over `conflictStrategy` |
| `parallel.sharedWorktreeObjects` | false | Use `git worktree add --shared` (requires git 2.10+) |
| `notifications.onComplete` / `onFailure` | (unset) | Session webhook URLs |
| `notifications.format` | auto | `generic` / `slack` / `discord` |
| `logRetentionDays` | 30 | Auto-delete old logs in `.ralph-logs/` (preserves `cost.jsonl`, `validation.jsonl`) |
| `budgetUsd` | (unset) | Cumulative cost ceiling — CLI/env wins |
| `taskTimeoutSec` | (unset) | Per-Claude-call timeout — CLI/env wins |
| `maxRetries` | 2 | Retry attempts per Claude call (env `MAX_RETRIES` wins) |
| `retryDelay` | 5 | Seconds between retries (env `RETRY_DELAY` wins) |
| `verifyRetries` | 1 | Self-fix retries when `verification.command` exits non-zero (0 disables) |
| `smokeTest` | (unset → auto-infer) | Single command run on base branch after each merge batch |
| `categories` | `["plan","implementation","testing","commit"]` | Override the per-feature stage list used by `--plan` |

## Design Note — Why is `tasks.json` mutable + declarative?

`tasks.json` plays two roles at once:

- **Declarative** — it is the manifest of intent: which tasks exist, what each one should do, which files it touches, what verifies it, and how they depend on each other. `--plan` writes it, and humans can edit it.
- **Mutable** — Ralph itself writes back into it during a run: `done: true` flips per task, the file is auto-committed after each batch, and `--reset` rewrites it.

Keeping the two roles in **one file** is a deliberate trade-off.

### Why one file (the upside)

- **Resume-safe by construction.** State lives where the spec lives, so re-running `ralph --run` picks up exactly where it left off (only `done: false` tasks dispatch). No sidecar DB, no separate state directory to keep in sync.
- **Single auditable artifact.** `git log tasks.json` is a complete run history — what was planned, what was completed, and when. PRs can review plan + progress together.
- **Works offline / portable.** No coordination service required. Copy the file (and the repo) anywhere and it still describes the entire pipeline.
- **Simple mental model.** No split between `spec.json` (immutable) and `state.json` (mutable) that would have to be reconciled and could drift.

### What you give up (the downside)

- **Edit-while-running is unsafe.** Two writers (you + Ralph) can race. Ralph mitigates this with an atomic tmp+rename save, an in-process file lock around `MarkTaskDone`, and a reload between merges — but you should still avoid hand-editing the file mid-`--run`.
- **Mixed concerns in one file.** "What should happen" and "what already happened" share the schema. Reviewers have to look twice to tell intent from progress.
- **Merge conflicts on `tasks.json` itself.** Each worktree may end up touching `tasks.json` (when its `done` flag flips). Ralph mitigates this by normalizing the worktree's `tasks.json` against the base branch immediately before merging — but the conflict surface is non-zero.
- **Blurred provenance.** `--plan` (LLM) writes it, humans tweak it, Ralph mutates it. Without commits in between, "who wrote which line" is hard to recover.
- **`--reset` is destructive.** It rewrites the same file you may have hand-edited. Use version control as the safety net.

### Mitigations Ralph applies

- **Atomic writes** (`tmp + rename`) — never partial files on crash.
- **In-process lock** when marking tasks done across a parallel batch.
- **Pre-merge normalization** — each worktree's `tasks.json` is reset to base before merge, so only Ralph's `done` flips reach the merged commit.
- **`--dry-run` try/finally** — preview runs always restore the original `tasks.json`.
- **Auto-commit per batch** — `done: true` flips are committed immediately, so a crash never loses recorded progress.

### When to push back on this design

If your project needs a strict spec/state split (e.g. you want the plan signed and frozen, with state stored elsewhere), keep `tasks.json` in version control as the spec, and use `--dry-run` as the read-only check. Ralph's mutation is contained to `done` flags and a few subtask flips — it does not rewrite prompts or dependencies after the plan is generated.

## Writing PRDs for Parallel Execution

To make `ralph --plan` produce a parallel-friendly `tasks.json`, **separate independent features clearly** in your PRD.

**Independent feature** = touches different files and does not reference another feature's code.

The plan generator decides dependencies as follows:
- The four phases inside one feature (plan → impl → test → commit) are always sequential
- Two features with no shared output → parallel-eligible
- A feature that depends on another's output → linked through `dependsOn`

### Good PRD Structure

Split features into independent modules and put any shared foundation into its own phase:

```markdown
# PRD: Calculator app

## Phase 1 — Operation modules (independent, run in parallel)

### Addition module
- Implement add(a, b) in `add.py`
- Add tests in `tests/test_add.py`

### Subtraction module
- Implement subtract(a, b) in `subtract.py`
- Add tests in `tests/test_subtract.py`

## Phase 2 — Main entry point (after Phase 1)

### CLI main
- Import the operation modules in `main.py` and expose a CLI

## Phase 3 — Integration tests (after Phase 2)
```

Tips that encourage parallelism:

| Tactic | Effect |
|---|---|
| **List exact files per feature** | The plan generator emits accurate `modifiedFiles` |
| **Phase separation** | Independent features in the same phase, dependents in the next |
| **Hint keywords** | Phrases like "independent" / "can run in parallel" in the PRD |
| **Minimize shared code** | Put shared utilities in the first phase so others can depend on them |
| **State dependencies explicitly** | "Module X depends on module Y" makes `dependsOn` accurate |

After `ralph --plan`, run `ralph --critique` to get a static report on the resulting `tasks.json` (parallelism gaps, missing verification, dep oddities). Use `--llm-critique` during `--plan` for an LLM-driven review of PRD vs plan.

## Logs

Run logs are written to `.ralph-logs/`:

```
.ralph-logs/
├── ralph-20260219-165209.log   # session log
├── add-plan.log                # per-task logs (parallel runs)
├── subtract-plan.log
├── multiply-plan.log
├── cost.jsonl                  # cumulative token usage / cost ledger (preserved)
└── validation.jsonl            # verification command ledger (preserved)
```

```bash
ralph --logs                    # list log files
ralph --logs add-impl           # print a specific task log
ralph --logs --live add-impl    # live tail
ralph --logs --cleanup          # delete logs older than retention (default 30d)
```

`cost.jsonl` and `validation.jsonl` are preserved across log rotation so historical data is never lost.

## Example

`samples/PRD.md` — a parallel-optimized PRD that builds a small Python calculator:

- **Phase 1** — four operation modules (`add.py`, `subtract.py`, `multiply.py`, `divide.py`) run in parallel
- **Phase 2** — `main.py` imports all four, so it runs sequentially after Phase 1
- **Phase 3** — integration tests, after Phase 2

```bash
mkdir my-calculator && cd my-calculator
cp /path/to/ralph/samples/PRD.md .

ralph --plan PRD.md       # 24 tasks (4 parallel start points)
ralph --validate          # sanity-check the generated plan
ralph --status            # inspect the parallel batch structure
ralph --run               # Phase 1 runs 4-wide; Phase 2-3 sequential
```

## Things to Consider

A non-exhaustive list of constraints, gotchas, and design choices to be aware of before running Ralph against a real repo.

### Repository state

- Ralph **requires a git repository** with at least one commit. If missing, an initial commit is auto-created.
- Worktrees are created at `.ralph-worktrees/{taskId}` and the corresponding branches at `ralph/{taskId}`. Add both to your repo's `.gitignore` if they are not already.
- Existing branches named `ralph/*` from a previous run are detected and (if clean) auto-removed. **If a `ralph/*` branch has uncommitted changes or unmerged commits, Ralph stops and asks you to handle it manually** — it never silently destroys work.

### Concurrency

- Default `maxConcurrent` is **5** and is capped at **10**. Higher values are clipped because most repos hit disk/IO or Claude API rate limits before CPU.
- `--max-parallel N` overrides everything, including `tasks.json`.
- `--shared-worktrees` saves disk and `.git` IO when running many worktrees, but requires git 2.10+ — Ralph auto-falls-back if `--shared` is unsupported.

### Merging

- Merges happen sequentially **after** all tasks in the batch finish. The first merged task can shift the base branch out from under later worktrees — Ralph rebase-advances each worktree onto the new base just before its merge to minimise late conflicts.
- The conflict strategy chain runs in order. The first entry sets the initial `git merge -X` flag (for `auto-*`); subsequent entries are tried only after the previous one has failed. Always finish the chain with `claude` or `abort` so unsolvable cases don't silently fail.
- `--strict-files` only catches **undeclared writes** — it does not enforce that all declared files were modified. Use the verification gate or smoke test for that.

### Cost

- The budget gate (`--budget-usd`) **does not kill in-flight tasks** — only blocks new dispatches. You can overshoot by the cost of whatever was already running.
- Cost is computed from Claude's reported token usage and `pricing.json`. If a model is missing from pricing, the call is recorded with USD = 0.
- `--llm-critique` adds **one extra Claude call** per `--plan` and is off by default.

### Verification & smoke tests

- The verification command runs in the task's working directory. Make sure tools (pytest, dotnet, etc.) are available there — worktrees inherit the repo state but not your shell aliases.
- `verifyRetries` defaults to **1**. Set higher for flaky tests at the cost of more Claude tokens; set `0` to fail fast.
- The post-merge smoke test is **opt-out**, not opt-in. If you don't want it, set `--no-smoke-test` (or `RALPH_NO_SMOKE_TEST=true`); otherwise Ralph will auto-infer one for common build systems. Explicit `workflow.smokeTest` always wins.

### Sensitive paths

- `PlanValidator` flags declared paths matching `.env`, `*.pem`, `*.key`, `credentials.json`, `id_rsa`, `id_ed25519`, etc. and the auto-commit step excludes them.
- This is **best-effort heuristics, not a sandbox.** Untrusted PRDs can still ask Claude to read or write any file the host user can. Run untrusted plans inside a VM/container with no real credentials.

### Determinism

- Plan generation is non-deterministic (LLM output). Two `ralph --plan` runs on the same PRD may produce slightly different `tasks.json`. Pin a generated plan into version control if you need reproducibility.
- Claude executions inside `--run` are also non-deterministic. The verification gate is what makes the pipeline reliable; weak verification = unreliable runs.

### Platform notes

- Self-contained binaries are produced for `win-x64`, `osx-x64`, `osx-arm64`, `linux-x64`. Other platforms must build from source via the `install.sh` / `install.ps1` scripts.
- Verification commands run via `/bin/sh -c` on POSIX and `cmd /c` on Windows. Cross-platform shell features (e.g. POSIX-only redirections) won't work uniformly.
- ANSI/Spectre.Console output assumes a UTF-8 console — Windows users on cmd.exe may want `chcp 65001` or use Windows Terminal.

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| `Error: claude not found` | Install Claude Code CLI (`https://claude.ai/code`) and ensure it's on PATH. |
| `Error: git not found` | Install git 2.10+. Required for worktree-based parallel execution. |
| Run starts but immediately exits with "no pending tasks" | All tasks have `done: true`. Use `ralph --reset` to reset, or edit `tasks.json`. |
| Worktree creation fails with "already exists" | A previous run left a worktree behind. Run `ralph --worktree-cleanup` (or remove `.ralph-worktrees/{taskId}` and run `git worktree prune`). |
| Worktree blocked because branch has uncommitted changes | Inspect, then either commit/merge manually or run `ralph --worktree-cleanup` to force-remove. |
| Task hangs on a Claude call forever | Set `--task-timeout 30m` (or whatever is appropriate). The process tree is killed on timeout. |
| Cost overrun past `--budget-usd` | Expected — the gate blocks new dispatches, not in-flight ones. Lower `maxConcurrent` if you need a tighter cap. |
| Smoke test fails on the first batch | Inspect `.ralph-logs/`. Either fix the underlying merge result manually (no auto-rollback), set a more targeted `workflow.smokeTest`, or use `--no-smoke-test` while iterating. |
| `tasks.json` change midway through a run | Ralph reloads `tasks.json` between merges. Avoid editing it while `--run` is active — use `--reset` for a clean slate. |
| Verification keeps retrying | Lower `workflow.verifyRetries` (set `0` to fail fast). Check `validation.jsonl` for the actual command output. |

## Security

The following file patterns are excluded from auto-commits and flagged by `--validate`:

`.env`, `.env.*`, `*.pem`, `*.key`, `*.p12`, `*.pfx`, `credentials.json`, `service-account*.json`, `.secret*`, `*.secrets`, `id_rsa`, `id_ed25519`

If a sensitive file is detected, Ralph emits a warning. **Treat these checks as a tripwire, not a defence — Claude runs with `--dangerously-skip-permissions` and can read anything the host user can.** Run untrusted plans in an isolated environment.

## Contributing & Development

```bash
# Build
dotnet build ralph.sln

# Test
dotnet test ralph.sln

# Publish a self-contained binary for the current OS
dotnet publish Ralph/Ralph.csproj -c Release -r osx-arm64 --self-contained true

# Release script (uses gh CLI)
./release-binary.sh v1.1.0
```

The repo layout:

- `Ralph/` — main project (Program.cs + Commands/ + Services/ + Models/).
- `Ralph.Tests/` — xUnit test project.
- `ralph-schema.json`, `pricing.json` — embedded resources.
- `samples/PRD.md` — example PRD for the calculator demo.
- `bugfix.md`, `enhance1.md` — historical PRDs used to ship Ralph features through Ralph itself.

See `CLAUDE.md` for a service-level architectural map oriented to LLM contributors.

## GitHub Topics

If you maintain a fork of this repository, add the following topics on GitHub to improve discoverability. Topics must be set by the repository owner via the GitHub web UI (the green "About" gear on the repo home page → "Topics" field):

- `ralph-loop`
- `agentic-ai`
- `ai-coding`
- `prd`
- `task-orchestrator`
- `claude-code`
- `autonomous-agent`
- `parallel-execution`
