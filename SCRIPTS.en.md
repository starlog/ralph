# Scripts Guide

[한국어](SCRIPTS.md) | **English**

This document describes every script file in the repository root — what it does and how to use it. Each script ships as a POSIX-shell variant (`*.sh`) and a PowerShell variant (`*.ps1`); the two are functionally equivalent.

| Script | Purpose | Audience |
|---|---|---|
| [`install-binary.sh`](install-binary.sh) / [`install-binary.ps1`](install-binary.ps1) | Download a prebuilt binary from GitHub Releases and install it | **End users** |
| [`install.sh`](install.sh) / [`install.ps1`](install.ps1) | Build from source and install locally (requires .NET 8 SDK) | **Source builders** |
| [`release-binary.sh`](release-binary.sh) / [`release-binary.ps1`](release-binary.ps1) | Build per-platform binaries, create a git tag, publish a GitHub Release | **Maintainers only** |
| [`clean-sample.sh`](clean-sample.sh) / [`clean-sample.ps1`](clean-sample.ps1) | Wipe `samples/` clean except `PRD.md` | **Developers / testers** |

---

## install-binary.sh / install-binary.ps1

Downloads a self-contained binary from GitHub Releases — no `.NET SDK` needed. This is the recommended install path for most users.

### Usage — POSIX (macOS / Linux / WSL / Git Bash)

```bash
# Quickest — pipe through curl
curl -fsSL https://raw.githubusercontent.com/starlog/ralph/main/install-binary.sh | bash

# Custom install directory
curl -fsSL https://raw.githubusercontent.com/starlog/ralph/main/install-binary.sh | bash -s -- --dir ~/.local/bin

# Pin a specific version
curl -fsSL https://raw.githubusercontent.com/starlog/ralph/main/install-binary.sh | bash -s -- --version v1.22

# Run a local clone directly
./install-binary.sh --dir ~/bin --quiet
```

### Usage — Windows (PowerShell)

```powershell
# Quickest — pipe through iwr
iwr -useb https://raw.githubusercontent.com/starlog/ralph/main/install-binary.ps1 | iex

# Run directly with options
.\install-binary.ps1 -Version v1.22 -Dir "$env:USERPROFILE\bin"
```

### Options

| Option | Default | Description |
|---|---|---|
| `--version` / `-Version` | latest | Release tag to install (e.g. `v1.22`) |
| `--dir` / `-Dir` | `$HOME/.local/bin` | Install directory |
| `--quiet` / `-Quiet` | off | Less verbose output |

### Environment

- `RALPH_REPO` — override the source repo (default: `starlog/ralph`)

### Flow

1. Auto-detect OS / architecture (`linux-x64`, `osx-arm64`, `win-x64`, etc.).
2. If no version was given, query GitHub API for the latest release tag.
3. Download the archive (POSIX: `tar.gz`, Windows: `zip`).
4. If a `SHA256SUMS.txt` is published alongside the release, verify the checksum.
5. Extract and copy the binary into the install directory (POSIX also `chmod +x`).
6. If the install directory is not on `PATH`, print guidance for adding it.

---

## install.sh / install.ps1

Builds from the source tree and installs locally. Requires the `.NET 8 SDK`.

### Usage

```bash
# macOS / Linux
git clone https://github.com/starlog/ralph.git
cd ralph
./install.sh

# Windows (PowerShell)
git clone https://github.com/starlog/ralph.git
cd ralph
.\install.ps1
```

### Flow

1. Detect OS / architecture and pick a RID (`linux-x64`, `osx-arm64`, `win-x64`, `win-arm64`).
2. Verify `.NET SDK` is installed via `dotnet --version`.
3. `dotnet publish -c Release -r <RID>` to produce a self-contained binary.
4. Prompt for install directory (default `$HOME/bin`).
5. Offer to create the directory if it doesn't exist.
6. Copy the binary in (POSIX also `chmod +x`).
7. If the directory is not on `PATH`, offer to append it to your shell rc file (`.zshrc` / `.bashrc` / `.bash_profile`) on POSIX, or register it in user PATH on Windows.

### vs. `install-binary.sh`

| Item | `install.sh` | `install-binary.sh` |
|---|---|---|
| .NET SDK | **required** | not needed |
| Source clone | required | not needed |
| Time | full build | download only |
| When to use | local edits, validating an unreleased change | normal use |

---

## release-binary.sh / release-binary.ps1

**Maintainers only.** Builds binaries for every supported platform, creates a git tag, pushes it, and publishes a GitHub Release.

### Usage

```bash
# Auto-bump from the latest tag (analyzes commit messages)
./release-binary.sh

# Explicit version
./release-binary.sh --version v1.3

# Force +0.1 (major) or +0.01 (minor)
./release-binary.sh --bump major
./release-binary.sh --bump minor

# Build & package only — no tag, no release
./release-binary.sh --dry-run

# Reuse an existing dist/ directory
./release-binary.sh --skip-build
```

PowerShell:

```powershell
.\release-binary.ps1                 # auto-bump
.\release-binary.ps1 -Bump major     # force +0.1
.\release-binary.ps1 -Version v1.3   # explicit version
.\release-binary.ps1 -DryRun         # build only
.\release-binary.ps1 -SkipBuild      # reuse dist/
.\release-binary.ps1 -NoTag          # skip tag creation/push
```

### Auto-bump rules

Inspects commit messages since the latest `v*` tag to choose the next version:

- **+0.1 (major)** — if any commit contains a feature/refactor/breaking marker (`기능추가`, `기능개선`, `리팩토링`, `feat`, `BREAKING`)
- **+0.01 (minor)** — otherwise (docs / chore / fix only)

### Key options

| Option | Description |
|---|---|
| `--version <tag>` | Explicit release tag (overrides auto-bump) |
| `--bump major\|minor` | Override commit-message analysis |
| `--notes <file>` | Use a custom release-notes file instead of auto-generated |
| `--draft` | Publish as draft |
| `--prerelease` | Mark as pre-release |
| `--no-tag` | Skip tag creation and push (assumes the tag already exists) |
| `--no-push` | Create the tag locally only, do not push |
| `--allow-dirty` | Allow tagging with a dirty working tree |
| `--skip-build` | Reuse the existing `dist/` artifacts |
| `--dry-run` | Build + package only — no tag, no push, no release |

### Required tools

- `dotnet` — build
- `git` — tag create / push
- `gh` — GitHub Release publish (not required for `--dry-run`)
- `tar` — archive packaging

### Target platforms

`win-x64`, `osx-x64`, `osx-arm64`, `linux-x64`, `linux-arm64` (matches the release workflow at `.github/workflows/release.yml`).

### Environment

- `RALPH_REPO` — override the target repo (default: `starlog/ralph`)

> **Note**: The PowerShell variant forces UTF-8 console encoding so Korean commit summaries don't kill `git` on a Windows `cp949` console.

---

## clean-sample.sh / clean-sample.ps1

Removes everything inside `samples/` except `PRD.md`. Handy for wiping out artifacts produced while validating Ralph against the sample PRD.

### Usage

```bash
# POSIX
./clean-sample.sh

# PowerShell
.\clean-sample.ps1
```

### Flow

1. Confirm `samples/` exists.
2. Recursively delete everything except `PRD.md`.
3. Print a completion message.

> **Caution**: deletion is irreversible. If you have artifacts in `samples/` you want to keep, move them elsewhere before running.
