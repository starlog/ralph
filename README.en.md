# Ralph

[한국어](README.md) | **English**

> One page of intent → many Claudes working in parallel → automatically merged into a finished project.
> Single self-contained binary on .NET 8. Windows / macOS / Linux. Current version: **v1.32**.

---

## What is Ralph?

These days everyone uses AI to write code. But when you ask Claude to do something big — "build login, add billing, and wire up notifications" — it works through it one slow line at a time. It takes forever and the thread tends to lose focus halfway through.

**Ralph takes that big request, splits it into small pieces, runs many Claudes at the same time on the parts that don't depend on each other, and merges the results back together for you.**

```
Your one-page spec (PRD.md)
                ↓  ralph --plan
        broken down into 24 small tasks
                ↓  ralph --run
   ┌────────┬────────┬────────┬────────┐
   │ login  │billing │notify  │settings│   ← 4 Claudes running at once
   └────────┴────────┴────────┴────────┘
                ↓ auto-merge + smoke build
            finished code
```

### How is this different from other AI coding tools

- **It runs many at once.** Most AI tools do one thing at a time. Ralph isolates each task in its own git worktree, then runs the independent parts concurrently — 4 features = roughly 4× faster.
- **It checks itself with builds and tests.** Instead of trusting Claude saying "done," Ralph runs `dotnet build` / `pytest` / etc. and uses the exit code as ground truth. If it fails, Claude gets one shot to self-fix.
- **You can cap the spend.** `--budget-usd 5` and Ralph stops dispatching new tasks once $5 is reached.
- **It's resumable.** Power off mid-run? Re-run and it picks up exactly where it left off.
- **Single self-contained binary.** No runtime install (you just need `claude` and `git`).

For full features / configuration / internals / troubleshooting, see **[TECHNICAL.en.md](TECHNICAL.en.md)**.

---

## ⚠️ Security note — read this first

Ralph runs Claude Code on your host with `--dangerously-skip-permissions`. That means **Claude can freely read and write files on your machine** — `.env`, SSH keys, AWS credentials are all reachable.

If the PRD is something *you* wrote and understand, running it on your dev box is fine. But for any **PRD or `tasks.json` someone else gave you**, run it inside a separate user account, VM, or container.

---

## What you need

| Tool | Why |
|---|---|
| [Claude Code](https://claude.ai/code) | Claude Code CLI. Ralph calls it under the hood. |
| [git](https://git-scm.com/) | Used for worktree-based parallel execution. (2.10+ recommended) |

---

## Install

Pick one of the three.

### Option 1 — Download a prebuilt binary (easiest)

Grab the file matching your OS from [GitHub Releases](https://github.com/starlog/ralph/releases). No .NET install needed.

| Platform | File |
|---|---|
| Windows (x64) | `ralph-vX.X.X-win-x64.zip` |
| macOS (Intel) | `ralph-vX.X.X-osx-x64.tar.gz` |
| macOS (Apple Silicon) | `ralph-vX.X.X-osx-arm64.tar.gz` |
| Linux (x64) | `ralph-vX.X.X-linux-x64.tar.gz` |

```bash
# Example: Linux
curl -LO https://github.com/starlog/ralph/releases/latest/download/ralph-v1.22-linux-x64.tar.gz
tar -xzf ralph-v1.22-linux-x64.tar.gz
sudo mv ralph /usr/local/bin/
```

### Option 2 — Package manager

```bash
# macOS / Linux — Homebrew
brew tap starlog/ralph https://github.com/starlog/ralph
brew install ralph

# Windows — Scoop
scoop install https://raw.githubusercontent.com/starlog/ralph/main/scoop/ralph.json
```

### Option 3 — Build from source (needs .NET 8 SDK)

```bash
git clone https://github.com/starlog/ralph.git
cd ralph

# macOS / Linux
./install.sh

# Windows (PowerShell)
.\install.ps1
```

Verify:

```bash
ralph --version
```

---

## Use it (5-minute quickstart)

### Step 1 — write a one-page spec (PRD)

Write what you want in `PRD.md` in plain language. Group independent features into separate phases — Ralph will run them in parallel automatically.

```markdown
# Calculator app

## Phase 1 — operation modules (independent)
- add(a, b) in `add.py`
- subtract(a, b) in `subtract.py`
- multiply(a, b) in `multiply.py`

## Phase 2 — CLI (after Phase 1)
- import the modules above in `main.py` and expose a CLI
```

A more complete example lives at `samples/PRD.md`.

### Step 2 — turn it into tasks

```bash
ralph --plan PRD.md
```

Generates `tasks.json` (typically ~24 small tasks for a real PRD).

### Step 3 — preview what will happen

```bash
ralph --graph     # dependency graph
ralph --list      # task list
ralph --dry-run   # simulate without changing anything
```

### Step 4 — run it

```bash
ralph --run
```

Phase 1's independent modules run concurrently; Phase 2 follows. You see a live progress table in the console, and everything is merged back automatically when each batch finishes.

### Common options

```bash
ralph --run --budget-usd 5.00     # stop dispatching once $5 is reached
ralph --run --max-parallel 3      # never run more than 3 at a time
ralph --run --task-timeout 30m    # kill any single Claude call after 30 minutes
ralph --status                    # show current progress
ralph --cost                      # cumulative cost / tokens
ralph --rollback                  # roll back to before --plan or --run
```

Full command / option / env-var reference is in **[TECHNICAL.en.md](TECHNICAL.en.md)**.

---

## Going deeper

- **[TECHNICAL.en.md](TECHNICAL.en.md)** — full command list, options, environment variables, parallel-execution flow, verification gate, conflict strategies, smoke test, `tasks.json` schema, workflow settings, PRD-writing guide, troubleshooting, and design notes.
- **[CLAUDE.md](CLAUDE.md)** — service-level architecture map, oriented to LLM contributors.
- **[samples/PRD.md](samples/PRD.md)** — a parallel-optimized example PRD.

---

## License / Contributing

PRs and issues welcome. Build + test:

```bash
dotnet build ralph.sln
dotnet test ralph.sln
```

See **[TECHNICAL.en.md](TECHNICAL.en.md#contributing--development)** for the full developer guide.
