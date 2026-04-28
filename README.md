# Ralph

**English** | [한국어](README.ko.md)

A CLI task orchestrator that generates execution plans from PRD (Product Requirements Document) files and runs them automatically through Claude Code. Built on .NET 8 for cross-platform support (Windows, macOS, Linux).

The first Ralph implementation with **parallel git worktree execution**. Run multiple Claude Code agents simultaneously on independent features, with automatic dependency resolution, conflict-aware merging, and live progress monitoring.

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
| Cost tracking | ❌ | ❌ | ✅ |
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

## Versions

| Version | Implementation | Platforms | Key Features |
|---|---|---|---|
| v0.1 | `ralph.sh` (Bash) | macOS, Linux | Sequential execution |
| v0.6 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | Parallel execution, worktrees, live logs |
| v0.7 | `Ralph/` (.NET 8 C#) | Windows, macOS, Linux | `--graph` task dependency visualization |

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
curl -LO https://github.com/starlog/ralph/releases/latest/download/ralph-v0.7.0-linux-x64.tar.gz
tar -xzf ralph-v0.7.0-linux-x64.tar.gz
sudo mv ralph /usr/local/bin/
```

The binary is self-contained, so the .NET runtime is not required.

## Quick Start

```bash
# 1. Generate a task plan from a PRD
ralph --plan docs/PRD.md

# 2. Inspect the generated tasks
ralph --list

# 3. Preview execution (no changes are made)
ralph --dry-run

# 4. Run the entire pipeline
ralph --run
```

## Commands

| Command | Description |
|---|---|
| `--plan <file>` | Analyze a PRD file and produce `tasks.json` |
| `--run [file]` | Execute all pending tasks (parallel by default). Defaults to `tasks.json` |
| `--dry-run` | Simulate execution without modifying `tasks.json` |
| `--task <id>` | Run a single task by ID |
| `--interactive` | Interactive mode — confirm before each task |
| `--list`, `-l` | List pending tasks (shows parallel-eligibility) |
| `--graph`, `-g` | Render the task dependency graph in ASCII |
| `--prompts`, `-p` | Print the Claude prompt for every task |
| `--status`, `-s` | Progress dashboard with parallel batch info |
| `--reset`, `-r` | Reset all tasks back to pending |
| `--logs` | List log files (session + per-task) |
| `--logs <task-id>` | Print a specific task log |
| `--logs --live <task-id>` | Tail a task log live (like `tail -f`) |
| `--worktree-cleanup` | Remove leftover worktrees |
| `--help`, `-h` | Show help |

### Execution Options

| Option | Description |
|---|---|
| `--sequential` | Disable parallel execution; run tasks one at a time |
| `--max-parallel N` | Cap the number of concurrent tasks |

### Custom tasks.json

Pass a path to `--run` to use a file other than the default `tasks.json`:

```bash
ralph --run my-project-tasks.json
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

```bash
# Linux/macOS
MAX_RETRIES=3 ralph --run
RALPH_MAX_PARALLEL=4 ralph --run

# Windows (PowerShell)
$env:MAX_RETRIES=3; ralph --run
$env:RALPH_PARALLEL="false"; ralph --run   # Force sequential mode
```

## Parallel Execution Flow

Ralph runs independent tasks concurrently using git worktrees. The dependency graph drives scheduling — any task without `dependsOn` becomes a parallel candidate.

```
ralph --run
```

1. Analyze the dependency DAG and group ready tasks into batches
2. Create a git worktree per task (`ralph/{taskId}` branch under `.ralph-worktrees/`)
3. Run Claude Code concurrently in each worktree (live progress dashboard)
4. Merge completed branches back into the base branch sequentially
5. Resolve any merge conflicts via the configured strategy
6. Advance to the next batch (newly unblocked tasks)
7. Fall back to in-place execution when only one task remains

### Conflict Resolution Strategies

Configured under `workflow.parallel.conflictStrategy` in `tasks.json`:

| Strategy | Behavior |
|---|---|
| `claude` | Claude Code analyzes conflict markers and merges both sides (recommended) |
| `abort` | Abort the merge and re-run the task in sequential mode |
| `auto-theirs` | Use git's `-X theirs` — prefer the worktree branch's changes |
| `auto-ours` | Use git's `-X ours` — prefer the base branch's changes |

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

`tasks.json` is generated by `ralph --plan` or written by hand. The full schema lives in `ralph-schema.json`.

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
| `modifiedFiles` | | string[] | Files this task will edit — used for parallel merge-conflict detection |
| `dependsOn` | | string[] | Predecessor task IDs; missing means parallel-eligible |
| `subtasks` | | array | Optional subtasks |

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
      "maxConcurrent": 3,
      "conflictStrategy": "claude"
    }
  }
}
```

| Setting | Default | Description |
|---|---|---|
| `parallel.enabled` | true | Enable parallel execution |
| `parallel.maxConcurrent` | 3 | Maximum concurrent tasks (tune to CPU/memory) |
| `parallel.conflictStrategy` | `"claude"` | Merge conflict strategy (see above) |

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
└── multiply-plan.log
```

```bash
ralph --logs                    # list log files
ralph --logs add-impl           # print a specific task log
ralph --logs --live add-impl    # live tail
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
ralph --status            # inspect the parallel batch structure
ralph --run               # Phase 1 runs 4-wide; Phase 2-3 sequential
```

## Security

The following file patterns are excluded from auto-commits:

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
