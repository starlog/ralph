# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Ralph is a CLI task orchestrator that generates execution plans from PRD (Product Requirements Document) files and runs them in parallel (or sequentially) using Claude Code. It follows a 4-phase pattern per feature: **plan → implementation → testing → commit**, with dependency tracking between tasks. Built with .NET 8 for cross-platform support (Windows, macOS, Linux).

## Architecture

- **Ralph/** — .NET 8 C# project producing a self-contained single-file binary.
- **ralph-schema.json** — JSON Schema (2020-12) defining the `tasks.json` structure: tasks array with id/title/done/prompt/dependsOn/outputFiles/modifiedFiles/subtasks, workflow settings (including parallel config), and optional apiSpecs/samplePages.

### Key Services (Ralph/Services/)

| Service | Purpose |
|---|---|
| `PlanGenerator.cs` | Sends PRD + schema to Claude (tools disabled, sonnet model) to produce tasks.json |
| `ClaudeService.cs` | Runs Claude Code process with streaming JSON output, retry logic (MAX_RETRIES/RETRY_DELAY) |
| `TaskManager.cs` | Loads/saves/queries tasks.json, dependency DAG traversal, parallel batch computation, topological layer computation |
| `ParallelExecutor.cs` | Worktree-based parallel task execution with live progress dashboard, merge handling |
| `WorktreeService.cs` | Git worktree lifecycle: create, merge, cleanup, stale detection |
| `TaskProgressTracker.cs` | Live Spectre.Console table showing per-task status during parallel execution |
| `GitService.cs` | Git operations: init, commit, branch management, auto initial commit for worktree support |
| `GraphRenderer.cs` | ASCII task dependency graph rendering with parallel/sequential visualization |
| `RalphLogger.cs` | File-based logging to `.ralph-logs/` |

### Execution Modes

- `--run [file]` — Auto mode: parallel by default (uses git worktrees), falls back to sequential for single tasks
- `--run --sequential` — Force sequential execution (no worktrees)
- `--interactive` — Prompts before each task
- `--dry-run` — Simulates execution, restores tasks.json afterward
- `--task <id>` — Runs a single task by ID

### Parallel Execution Flow

1. Ensures at least one commit exists (required for worktree creation)
2. Detects and cleans stale worktrees
3. Groups independent tasks into parallel batches
4. Creates a git worktree per task (`ralph/{taskId}` branch)
5. Runs Claude Code in each worktree concurrently (with live progress table)
6. Sequentially merges completed branches back to base branch
7. Handles merge conflicts via configured strategy (claude/abort/auto-theirs/auto-ours)

## Commands

```bash
# Standard workflow
ralph --plan PRD.md              # Generate tasks.json from PRD
ralph --list                     # List pending tasks
ralph --dry-run                  # Preview execution (no changes)
ralph --run                      # Execute all tasks (parallel by default)
ralph --run custom.json          # Execute from custom task file

# Execution options
ralph --run --sequential         # Force sequential execution
ralph --run --max-parallel 4     # Limit concurrent tasks

# Single task
ralph --task <id>                # Run single task

# Monitoring
ralph --graph                    # ASCII task dependency graph
ralph --status                   # Progress dashboard with parallel batch info
ralph --logs                     # List log files
ralph --logs <task-id>           # View specific task log
ralph --logs --live <task-id>    # Live tail task log (like tail -f)

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

## Conventions

- Task IDs use kebab-case: `{feature}-plan`, `{feature}-impl`, `{feature}-test`, `{feature}-commit`
- Git commit messages must be in Korean
- Sensitive files (.env, *.pem, *.key, credentials.json, etc.) are auto-excluded from commits
- Session logs: `.ralph-logs/ralph-YYYYMMDD-HHMMSS.log`
- Task logs (parallel): `.ralph-logs/{taskId}.log`
- Worktrees created at `.ralph-worktrees/{taskId}` (auto-cleaned after execution)
- Schema is embedded in the binary as an EmbeddedResource
