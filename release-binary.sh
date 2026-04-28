#!/usr/bin/env bash
# Ralph release publisher — builds self-contained binaries for each platform
# and publishes them as a GitHub Release using the `gh` CLI.
#
# Usage:
#   ./release-binary.sh --version v1.2
#   ./release-binary.sh --version v1.2 --draft
#   ./release-binary.sh --version v1.2 --notes RELEASE_NOTES.md
#   ./release-binary.sh --version v1.2 --skip-build       # reuse existing dist/
#   ./release-binary.sh --version v1.2 --dry-run          # build & package only
#
# Mirrors .github/workflows/release.yml so you can publish from a local machine
# without waiting for CI (e.g. when re-cutting an existing tag).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/Ralph/Ralph.csproj"
DIST_DIR="$SCRIPT_DIR/dist"
REPO="${RALPH_REPO:-starlog/ralph}"

VERSION=""
NOTES_FILE=""
DRAFT=0
PRERELEASE=0
SKIP_BUILD=0
DRY_RUN=0
GENERATE_NOTES=1

# RID list mirrors .github/workflows/release.yml
RIDS=(win-x64 osx-x64 osx-arm64 linux-x64 linux-arm64)

usage() {
    cat <<EOF
Ralph release publisher

Usage:
  release-binary.sh --version <tag> [options]

Required:
  --version <tag>    Release tag (e.g. v1.2). Must match an existing or new git tag.

Options:
  --notes <file>     Path to release notes markdown (overrides auto-generated notes)
  --draft            Publish as draft release
  --prerelease       Mark as pre-release
  --skip-build       Skip dotnet publish; reuse existing dist/ artifacts
  --dry-run          Build & package only; do not create GitHub release
  -h, --help         Show this help

Environment:
  RALPH_REPO         Override target repo (default: $REPO)
EOF
}

log()  { echo "==> $*"; }
err()  { echo "Error: $*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || err "'$1' is required but not installed"; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version)    VERSION="$2"; shift 2 ;;
        --notes)      NOTES_FILE="$2"; GENERATE_NOTES=0; shift 2 ;;
        --draft)      DRAFT=1; shift ;;
        --prerelease) PRERELEASE=1; shift ;;
        --skip-build) SKIP_BUILD=1; shift ;;
        --dry-run)    DRY_RUN=1; shift ;;
        -h|--help)    usage; exit 0 ;;
        *)            err "Unknown option: $1 (try --help)" ;;
    esac
done

[[ -n "$VERSION" ]] || { usage; err "--version is required"; }
[[ "$VERSION" =~ ^v[0-9] ]] || err "Version must start with 'v' followed by a digit (e.g. v1.2), got: $VERSION"

need dotnet
need tar
need zip
need shasum
[[ $DRY_RUN -eq 1 ]] || need gh

if [[ -n "$NOTES_FILE" && ! -f "$NOTES_FILE" ]]; then
    err "Notes file not found: $NOTES_FILE"
fi

# ─── Build ─────────────────────────────────────────────────────────────────
if [[ $SKIP_BUILD -eq 0 ]]; then
    log "Cleaning $DIST_DIR"
    rm -rf "$DIST_DIR"
    mkdir -p "$DIST_DIR"

    for rid in "${RIDS[@]}"; do
        log "Publishing $rid"
        dotnet publish "$PROJECT" \
            -c Release \
            -r "$rid" \
            -o "$DIST_DIR/$rid" \
            --nologo -v q
    done
else
    log "Skipping build (--skip-build); expecting artifacts in $DIST_DIR"
    [[ -d "$DIST_DIR" ]] || err "$DIST_DIR does not exist; cannot --skip-build"
fi

# ─── Package ───────────────────────────────────────────────────────────────
cd "$DIST_DIR"
log "Packaging archives in $DIST_DIR"

# Clean any stale archives for this version
rm -f "ralph-${VERSION}-"*.zip "ralph-${VERSION}-"*.tar.gz "ralph-${VERSION}-SHA256SUMS.txt"

for rid in "${RIDS[@]}"; do
    src="$DIST_DIR/$rid"
    if [[ "$rid" == win-* ]]; then
        bin="ralph.exe"
        [[ -f "$src/$bin" ]] || err "Missing $src/$bin"
        archive="ralph-${VERSION}-${rid}.zip"
        ( cd "$src" && zip -q -j "$DIST_DIR/$archive" "$bin" )
    else
        bin="ralph"
        [[ -f "$src/$bin" ]] || err "Missing $src/$bin"
        archive="ralph-${VERSION}-${rid}.tar.gz"
        tar -czf "$DIST_DIR/$archive" -C "$src" "$bin"
    fi
    log "  $archive"
done

# ─── Checksums ─────────────────────────────────────────────────────────────
SUMS_FILE="ralph-${VERSION}-SHA256SUMS.txt"
log "Generating $SUMS_FILE"
shasum -a 256 ralph-"${VERSION}"-*.zip ralph-"${VERSION}"-*.tar.gz > "$SUMS_FILE"

ARTIFACTS=()
for rid in "${RIDS[@]}"; do
    if [[ "$rid" == win-* ]]; then
        ARTIFACTS+=("$DIST_DIR/ralph-${VERSION}-${rid}.zip")
    else
        ARTIFACTS+=("$DIST_DIR/ralph-${VERSION}-${rid}.tar.gz")
    fi
done
ARTIFACTS+=("$DIST_DIR/$SUMS_FILE")

log "Artifacts ready:"
for f in "${ARTIFACTS[@]}"; do
    echo "    $f"
done

if [[ $DRY_RUN -eq 1 ]]; then
    log "Dry run complete; skipping gh release."
    exit 0
fi

# ─── Publish via gh ────────────────────────────────────────────────────────
cd "$SCRIPT_DIR"

GH_ARGS=(release)
if gh release view "$VERSION" --repo "$REPO" >/dev/null 2>&1; then
    log "Release $VERSION already exists on $REPO; uploading assets (clobber)"
    gh release upload "$VERSION" "${ARTIFACTS[@]}" --repo "$REPO" --clobber
else
    log "Creating release $VERSION on $REPO"
    CREATE_ARGS=("$VERSION" --repo "$REPO" --title "$VERSION")
    [[ $DRAFT -eq 1 ]]      && CREATE_ARGS+=(--draft)
    [[ $PRERELEASE -eq 1 ]] && CREATE_ARGS+=(--prerelease)
    if [[ -n "$NOTES_FILE" ]]; then
        CREATE_ARGS+=(--notes-file "$NOTES_FILE")
    elif [[ $GENERATE_NOTES -eq 1 ]]; then
        CREATE_ARGS+=(--generate-notes)
    fi
    gh release create "${CREATE_ARGS[@]}" "${ARTIFACTS[@]}"
fi

log "Done. View at: https://github.com/${REPO}/releases/tag/${VERSION}"
