# Ralph

**English** | [한국어](README.ko.md)

A CLI task orchestrator that generates execution plans from PRD (Product Requirements Document) files and runs them automatically through Claude Code. Built on .NET 8 for cross-platform support (Windows, macOS, Linux).

The first Ralph implementation with **parallel git worktree execution**. Run multiple Claude Code agents simultaneously on independent features, with automatic dependency resolution, conflict-aware merging, exit-code-based verification, cost-budget gating, and live progress monitoring.

## ⚠️ Security Note

Ralph runs Claude Code directly on the host machine. Untrusted PRDs or external `tasks.json` files should be executed in an isolated environment (separate user account, VM, or container). The following may be exposed:

- Credentials in ~/.ssh, ~/.aws, ~/.config
- API keys in environment variables
- Read access to all host files

## Comparison with Other Ralph Implementations

| Feature | snarktank/ralph | PageAI/ralph-loop | starlog/ralph |
|---------|----------------|-------------------|---------------|
| Parallel execution | ❌ | ❌ | ✅ |
| Windows support | ❌ | ❌ | ✅ |
| DAG dependencies | ❌ | partial | ✅ |
| Cost tracking & budget gate | ❌ | ❌ | ✅ |
| Verification gate (exit code) | ❌ | ❌ | ✅ |
| Webhook notifications | ❌ | ❌ | ✅ |
| Single binary | ❌ | ❌ | ✅ |

## How It Works

Ralph follows a **4-phase pattern** per feature:

```
plan → implementation → testing → commit
```

Each feature produces these four tasks, chained by dependencies to enforce order. Independent features run **in parallel** through git worktrees.

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
| v0.1 | `ralph.sh` (Bash) | macOS, Linux | Sequential execution |
| v0.6 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | Parallel execution, worktrees, live logs |
| v0.7 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | `--graph` task dependency visualization |
| v1.0 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | Cost tracker, plan validator, prompt builder, webhook notifications, log rotation |
| v1.1 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | Verification gate, conflict-strategy chain, `--task-timeout`, `--budget-usd`, `--strict-files`, worktree rebase-advance |

## Requirements

| Tool | Description |
|---|---|
| [Claude Code](https://claude.ai/code) | Claude Code CLI |
| [git](https://git-scm.com/) | Version control (required for worktree-based parallel execution) |

## Installation

### Option 1: Install Script (build from source)

Requires the .NET 8 SDK. The script builds Ralph and installs it on your PATH.

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

# 2. Validate the generated plan (cycles, deps, file overlaps)
ralph --validate

# 3. Inspect the generated tasks
ralph --list

# 4. Preview execution (no changes are made)
ralph --dry-run

# 5. Run the entire pipeline
ralph --run
```

## Commands

| Command | Description |
|---|---|
| `--plan <file>` | Analyze a PRD file and produce `tasks.json` (atomic write) |
| `--plan-prompt <file>` | Show the full plan prompt without executing |
| `--validate` | Validate `tasks.json` (cycles, dangling deps, duplicate IDs, file overlaps, sensitive paths) |
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
| `--shared-worktrees` | Use `git worktree add --shared` to share `.git` objects across worktrees (saves disk/IO; falls back if unsupported) |
| `--budget-usd <amt>` | Stop dispatching new tasks once cumulative cost reaches `<amt>` USD |
| `--task-timeout <dur>` | Per-Claude-call timeout (e.g. `30m`, `1h`, `90s`, `1800`) — kills hung calls |
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
| `RALPH_BUDGET_USD` | unset | Cumulative cost ceiling — CLI `--budget-usd` wins |
| `RALPH_TASK_TIMEOUT_SEC` | unset | Per-Claude-call timeout (seconds) — CLI `--task-timeout` wins |
| `RALPH_WEBHOOK_URL` | unset | Default session-completion webhook |
| `RALPH_LOG_RETENTION_DAYS` | 30 | Auto-delete logs older than N days |

Priority for shared knobs: CLI flag > env var > `workflow` setting in `tasks.json` > built-in default.

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

1. Analyze the dependency DAG and group ready tasks into batches
2. Create a git worktree per task (`ralph/{taskId}` branch under `.ralph-worktrees/`)
3. Run Claude Code concurrently in each worktree (live progress dashboard)
4. Run the per-task `verification.command` if defined; one self-fix retry on failure
5. Rebase the worktree branch onto the latest base (advance) just before merge
6. Sequentially merge completed branches back into the base branch
7. Resolve any merge conflicts via the configured strategy chain (`conflictStrategies`)
8. Optionally validate that the merge wrote only the declared `modifiedFiles` (`--strict-files`)
9. Advance to the next batch (newly unblocked tasks)
10. Fall back to in-place execution when only one task remains

### Failure Handling & Resume

What happens when a parallel batch partially fails:

| Event | Behavior |
|---|---|
| Claude fails for one task in a batch | The other tasks in the same batch **continue and merge normally**. The failed task's worktree is cleaned up; its `done` flag stays `false`. |
| `verification.command` fails | One self-fix retry (configurable via `workflow.verifyRetries`). If still failing, the task is marked failed and **excluded from merge**. |
| Pre-commit scope violation (`--strict-files`) | Worktree fails fast before merge — saves cleanup cost. Other tasks in the batch are not affected. |
| Merge conflict unresolvable by the strategy chain | Remaining unmerged worktrees are cleaned up; already-merged tasks **stay merged** (no rollback). |
| Post-merge `workflow.smokeTest` fails | Run stops with exit code 1. No merges are reverted; the smoke-test failure is logged and surfaced. |

**Resume after interruption:**
- `done: true` is written atomically per-task — re-running `ralph --run` picks up exactly where it left off (only `done: false` tasks dispatch).
- If a worktree has uncommitted changes or commits ahead of base when `--run` starts, Ralph **does not silently delete it**. It prints the worktree path and asks you to merge/clean manually (or run `ralph --worktree-cleanup` to force-remove).
- Stale worktrees that are clean (cleanup was missed but no work was lost) are auto-removed.

**Already-merged tasks are not rolled back.** Ralph's design treats merge as the commit point — undoing requires a human-driven `git revert` or `git reset`. Use `--strict-files` and `workflow.smokeTest` to catch problems before the merge becomes durable.

**Smoke test is opt-out.** When `workflow.smokeTest` is not set, Ralph auto-infers a smoke test from repo-root markers (`*.csproj`/`*.sln` → `dotnet build`, `package.json` → `npm test`, `Cargo.toml` → `cargo build`, `go.mod` → `go build`). An explicit `workflow.smokeTest` always wins. Disable entirely with `--no-smoke-test` or `RALPH_NO_SMOKE_TEST=true`.

### Conflict Resolution Strategies

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

### Verification Gate

Each task can declare a `verification.command` whose exit code is the ground truth — Claude's self-report is ignored. On non-zero exit, Ralph feeds stdout/stderr back to Claude for **one self-fix retry** before failing the task.

```json
{
  "id": "math-impl",
  "verification": { "command": "go test ./...", "timeoutSec": 120 }
}
```

Common commands: `pytest tests/`, `go test ./...`, `tsc --noEmit`, `dotnet test`, `npm test --silent`, `cargo test --quiet`.

### Cost Tracking & Budget Gate

Per-call usage from Claude's `stream-json` `result` event is recorded to `.ralph-logs/cost.jsonl`. `--budget-usd <amt>` (or `RALPH_BUDGET_USD`) blocks new dispatches once the cumulative cost reaches the ceiling, with a one-shot warning at 80%.

```bash
ralph --cost                            # show cumulative tokens and USD
ralph --run --budget-usd 5.00           # stop dispatching at $5
```

Pricing is loaded from the embedded `pricing.json`; override at `~/.ralph/pricing.json`.

### Webhook Notifications

A single webhook fires at session end. Resolution priority:

1. `workflow.notifications.onComplete` / `onFailure` in `tasks.json`
2. `RALPH_WEBHOOK_URL` env (global fallback)

`format` is auto-detected by hostname (`hooks.slack.com` → Slack, `discord(app)?.com` → Discord, else `generic`) and can be forced via `workflow.notifications.format`.

### Live Monitoring

Tail a per-task log from another terminal during a parallel run:

```bash
# Terminal 1: run
ralph --run

# Terminal 2: live-tail one task
ralph --logs --live add-impl

# Terminal 3: live-tail another task
ralph --logs --live subtract-impl
```

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
| `category` | | string | Category (`"plan"`, `"implementation"`, `"testing"`, `"commit"`) |
| `prompt` | | string | Prompt sent to Claude Code; tasks without a prompt skip Claude |
| `outputFiles` | | string[] | Expected output file paths |
| `modifiedFiles` | | string[] | Files this task will edit — used for parallel merge-conflict detection and `--strict-files` |
| `dependsOn` | | string[] | Predecessor task IDs; missing means parallel-eligible |
| `subtasks` | | array | Optional subtasks |
| `verification` | | object | `{ command, timeoutSec? }` — exit-code-based verification (see Verification Gate above) |

### Workflow Settings

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
      "conflictStrategies": ["auto-theirs", "claude"]
    },
    "notifications": {
      "onComplete": "https://hooks.slack.com/services/XXX",
      "format": "slack"
    },
    "logRetentionDays": 30,
    "budgetUsd": 10.00,
    "taskTimeoutSec": 1800,
    "maxRetries": 2,
    "retryDelay": 5
  }
}
```

| Setting | Default | Description |
|---|---|---|
| `parallel.enabled` | true | Enable parallel execution |
| `parallel.maxConcurrent` | 5 | Maximum concurrent tasks (capped at 16) |
| `parallel.conflictStrategy` | `"claude"` | Single legacy strategy (used only when `conflictStrategies` is unset) |
| `parallel.conflictStrategies` | (unset) | Ordered fallback chain — takes precedence over `conflictStrategy` |
| `notifications.onComplete` / `onFailure` | (unset) | Session webhook URLs |
| `notifications.format` | auto | `generic` / `slack` / `discord` |
| `logRetentionDays` | 30 | Auto-delete old logs in `.ralph-logs/` (preserves `cost.jsonl`, `validation.jsonl`) |
| `budgetUsd` | (unset) | Cumulative cost ceiling — CLI/env wins |
| `taskTimeoutSec` | (unset) | Per-Claude-call timeout — CLI/env wins |
| `maxRetries` | 2 | Retry attempts per Claude call (env `MAX_RETRIES` wins) |
| `retryDelay` | 5 | Seconds between retries (env `RETRY_DELAY` wins) |

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

## Security

The following file patterns are excluded from auto-commits and flagged by `--validate`:

`.env`, `.env.*`, `*.pem`, `*.key`, `*.p12`, `*.pfx`, `credentials.json`, `service-account*.json`, `.secret*`, `*.secrets`, `id_rsa`, `id_ed25519`

If a sensitive file is detected, Ralph emits a warning.

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
