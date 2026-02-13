# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Ralph is a CLI task orchestrator that generates execution plans from PRD (Product Requirements Document) files and runs them sequentially using Claude Code. It follows a 4-phase pattern per feature: **plan → implementation → testing → commit**, with dependency tracking between tasks.

## Architecture

- **ralph.sh** — Single-file bash script containing the entire orchestrator: CLI parsing, task querying, Claude Code integration (streaming JSON), dependency resolution, git auto-commit, logging, and signal handling.
- **ralph-schema.json** — JSON Schema (2020-12) defining the `tasks.json` structure: tasks array with id/title/done/prompt/dependsOn/outputFiles/subtasks, workflow settings, and optional apiSpecs/samplePages.
- **install.sh** — Copies ralph.sh and ralph-schema.json to `~/bin` and updates PATH.

### Key Internal Components (ralph.sh)

| Function | Purpose |
|---|---|
| `generate_plan()` | Sends PRD + schema to Claude (tools disabled, sonnet model) to produce tasks.json |
| `run_claude_stream()` | Runs Claude in background with stream-json output, polls for new lines, parses deltas — designed for Ctrl+C safety via `wait` builtin |
| `run_claude()` | Wraps `run_claude_stream` with retry logic (MAX_RETRIES/RETRY_DELAY) |
| `safe_jq_update()` | Atomic jq-based JSON mutation with validation (prevents corrupt tasks.json) |
| `check_dependencies()` / `get_next_ready_task()` | Dependency DAG traversal for task ordering |
| `commit_changes()` | Auto git-add with sensitive file exclusion patterns |

### Execution Modes

- `--run [file]` — Auto mode: runs all ready tasks sequentially (optional custom tasks JSON, defaults to tasks.json)
- `--interactive` — Prompts before each task (y/n/p/s/q)
- `--dry-run` — Simulates execution, restores tasks.json afterward
- `--task <id>` — Runs a single task by ID

## Commands

```bash
# Standard workflow
./ralph.sh --plan PRD.md          # Generate tasks.json from PRD
./ralph.sh --list                 # List pending tasks
./ralph.sh --dry-run              # Preview execution (no changes)
./ralph.sh --run                  # Execute all pending tasks
./ralph.sh --run custom.json      # Execute from custom task file

# Other
./ralph.sh --task <id>            # Run single task
./ralph.sh --prompts              # Show all task prompts
./ralph.sh --status               # Progress dashboard
./ralph.sh --reset                # Reset all tasks to pending
./ralph.sh --logs                 # Show recent log files
```

## Dependencies

- **jq** — Required for all JSON operations (`brew install jq`)
- **claude** — Claude Code CLI, invoked with `--dangerously-skip-permissions --output-format stream-json`
- **git** — Auto-commit after each task completion

## Environment Variables

| Variable | Default | Description |
|---|---|---|
| `MAX_RETRIES` | 2 | Claude Code retry attempts |
| `RETRY_DELAY` | 5 | Seconds between retries |
| `CLAUDE_CODE_MAX_OUTPUT_TOKENS` | 65536 | Token limit for plan generation |

## Conventions

- Task IDs use kebab-case: `{feature}-plan`, `{feature}-impl`, `{feature}-test`, `{feature}-commit`
- Git commit messages must be in Korean
- Sensitive files (.env, *.pem, *.key, credentials.json, etc.) are auto-excluded from commits
- Logs go to `.ralph-logs/ralph-YYYYMMDD-HHMMSS.log`
- `RALPH_HOME` resolves to the directory containing ralph.sh; schema is loaded relative to it
