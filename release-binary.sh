#!/usr/bin/env bash
# Ralph release publisher — builds self-contained binaries for each platform,
# creates a matching git tag, pushes it, and publishes a GitHub Release using gh.
#
# Usage:
#   ./release-binary.sh                              # auto-bump from latest tag based on commit analysis
#   ./release-binary.sh --bump major                 # force +0.1 bump
#   ./release-binary.sh --bump minor                 # force +0.01 bump
#   ./release-binary.sh --version v1.3               # explicit version
#   ./release-binary.sh --no-tag                     # skip git tag/push (assume tag exists)
#   ./release-binary.sh --no-push                    # create tag locally but don't push
#   ./release-binary.sh --skip-build                 # reuse existing dist/
#   ./release-binary.sh --dry-run                    # build & package only; no tag, no release
#
# Auto-bump rules (when --version is omitted):
#   +0.1  (major) if any commit since the latest tag contains a feature/refactor/breaking marker
#                 (기능추가, 기능개선, 리팩토링, feat, BREAKING)
#   +0.01 (minor) otherwise (docs / chore / fix only)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/Ralph/Ralph.csproj"
DIST_DIR="$SCRIPT_DIR/dist"
REPO="${RALPH_REPO:-starlog/ralph}"

VERSION=""
BUMP=""              # "" | major | minor — explicit override for auto-bump
NOTES_FILE=""
DRAFT=0
PRERELEASE=0
SKIP_BUILD=0
DRY_RUN=0
GENERATE_NOTES=1
CREATE_TAG=1
PUSH_TAG=1
ALLOW_DIRTY=0

# RID list mirrors .github/workflows/release.yml
RIDS=(win-x64 osx-x64 osx-arm64 linux-x64 linux-arm64)

usage() {
    cat <<EOF
Ralph release publisher

Usage:
  release-binary.sh [--version <tag> | --bump major|minor] [options]

Versioning:
  --version <tag>    Explicit release tag (e.g. v1.3). Overrides auto-bump.
  --bump major|minor Force +0.1 (major) or +0.01 (minor) bump from latest tag.
                     Default: auto-detect from commit messages since latest tag.

Options:
  --notes <file>     Path to release notes markdown (overrides auto-generated notes)
  --draft            Publish as draft release
  --prerelease       Mark as pre-release
  --no-tag           Skip git tag creation and push (tag must already exist)
  --no-push          Create tag locally but don't push it
  --allow-dirty      Allow tagging when the working tree has uncommitted changes
  --skip-build       Skip dotnet publish; reuse existing dist/ artifacts
  --dry-run          Build & package only; no tag, no push, no release
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
        --version)     VERSION="$2"; shift 2 ;;
        --bump)        BUMP="$2"; shift 2 ;;
        --notes)       NOTES_FILE="$2"; GENERATE_NOTES=0; shift 2 ;;
        --draft)       DRAFT=1; shift ;;
        --prerelease)  PRERELEASE=1; shift ;;
        --no-tag)      CREATE_TAG=0; shift ;;
        --no-push)     PUSH_TAG=0; shift ;;
        --allow-dirty) ALLOW_DIRTY=1; shift ;;
        --skip-build)  SKIP_BUILD=1; shift ;;
        --dry-run)     DRY_RUN=1; shift ;;
        -h|--help)     usage; exit 0 ;;
        *)             err "Unknown option: $1 (try --help)" ;;
    esac
done

[[ -z "$BUMP" || "$BUMP" == "major" || "$BUMP" == "minor" ]] || err "--bump must be 'major' or 'minor'"
[[ -z "$VERSION" || -z "$BUMP" ]] || err "--version and --bump are mutually exclusive"

need dotnet
need tar
need zip
need shasum
need git
[[ $DRY_RUN -eq 1 ]] || need gh

cd "$SCRIPT_DIR"

# ─── Resolve version ───────────────────────────────────────────────────────
latest_tag() {
    git tag --list 'v[0-9]*' --sort=-v:refname | head -n1
}

# Compute next version: $1 = current "vX.Y", $2 = "major"|"minor"
bump_version() {
    local current="$1" kind="$2" delta
    case "$kind" in
        major) delta="0.1" ;;
        minor) delta="0.01" ;;
        *)     err "internal: unknown bump kind '$kind'" ;;
    esac
    local num="${current#v}"
    awk -v v="$num" -v d="$delta" 'BEGIN {
        r = v + d
        s = sprintf("%.2f", r)
        sub(/0+$/, "", s)
        sub(/\.$/, "", s)
        printf "v%s", s
    }'
}

# Decide bump kind from commit messages between $1..HEAD
detect_bump_kind() {
    local since="$1" log
    log=$(git log --format='%s' "${since}..HEAD" 2>/dev/null || true)
    if [[ -z "$log" ]]; then
        echo "minor"
        return
    fi
    if echo "$log" | grep -Eq '기능추가|기능개선|리팩토링|^feat(\(|:|!)|BREAKING CHANGE'; then
        echo "major"
    else
        echo "minor"
    fi
}

if [[ -z "$VERSION" ]]; then
    CURRENT="$(latest_tag || true)"
    [[ -n "$CURRENT" ]] || err "No prior 'v*' tag found; pass --version explicitly for the first release"

    if [[ -z "$BUMP" ]]; then
        BUMP="$(detect_bump_kind "$CURRENT")"
        log "Auto-detected bump kind: $BUMP (from commits since $CURRENT)"
    fi
    VERSION="$(bump_version "$CURRENT" "$BUMP")"
    log "Resolved version: $CURRENT → $VERSION"
fi

[[ "$VERSION" =~ ^v[0-9] ]] || err "Version must start with 'v' followed by a digit (e.g. v1.3), got: $VERSION"

if [[ -n "$NOTES_FILE" && ! -f "$NOTES_FILE" ]]; then
    err "Notes file not found: $NOTES_FILE"
fi

# ─── Update version references in source/docs via claude CLI ───────────────
# Files that display the current version and must be kept in sync:
#   - Ralph/Commands/DisplayHelpers.cs  (const Version, without the "v" prefix)
#   - CLAUDE.md                          ("Current version: **vX.Y**.")
#   - README.md                          ("Current version: **vX.Y**.")
#   - README.ko.md                       ("현재 버전: **vX.Y**.")
update_version_refs() {
    local new_version="$1"
    local stripped="${new_version#v}"

    need claude
    log "Updating version references to $new_version via claude CLI"

    local prompt
    prompt=$(cat <<EOF
Bump the Ralph project version to ${new_version}. Make exactly these edits and nothing else:

1. File: Ralph/Commands/DisplayHelpers.cs
   Replace the existing Version constant value so the line reads:
       public const string Version = "${stripped}";
   (no leading "v" — this constant holds the bare number).

2. File: CLAUDE.md
   In the "## Project Overview" paragraph, replace the existing "Current version: **vX.Y**." sentence so it reads "Current version: **${new_version}**.".

3. File: README.md
   On the first paragraph after the H1, replace the existing "Current version: **vX.Y**." sentence so it reads "Current version: **${new_version}**.".

4. File: README.ko.md
   On the first paragraph after the H1, replace the existing "현재 버전: **vX.Y**." sentence so it reads "현재 버전: **${new_version}**.".

Use the Edit tool for each file. Do not modify any other lines, files, or formatting. Do not create new files. Do not run git or any other shell commands.
EOF
)
    claude --dangerously-skip-permissions -p "$prompt"
}

if [[ $CREATE_TAG -eq 1 && $DRY_RUN -eq 0 ]]; then
    if git rev-parse -q --verify "refs/tags/$VERSION" >/dev/null; then
        log "Tag $VERSION already exists locally; skipping version-ref update"
    else
        if [[ $ALLOW_DIRTY -eq 0 ]] && ! git diff-index --quiet HEAD --; then
            err "Working tree has uncommitted changes; commit/stash or pass --allow-dirty"
        fi

        update_version_refs "$VERSION"

        VERSION_FILES=(
            "Ralph/Commands/DisplayHelpers.cs"
            "CLAUDE.md"
            "README.md"
            "README.ko.md"
        )
        if ! git diff --quiet -- "${VERSION_FILES[@]}"; then
            log "Committing version bump to $VERSION"
            git add -- "${VERSION_FILES[@]}"
            git commit -m "릴리스: 버전을 $VERSION 으로 업데이트"
        else
            log "Version references already at $VERSION; nothing to commit"
        fi
    fi
fi

# ─── Generate bilingual release notes via claude CLI ──────────────────────
# Builds a Markdown body with two sections — English "What's Changed" and
# Korean "변경 사항" — derived from the commit log between the previous tag
# and $VERSION, plus a Full Changelog compare link. Replaces the default
# `--generate-notes` body which is just a compare URL.
build_release_notes() {
    local version="$1" prev_tag="$2" out="$3"
    need claude

    local commits
    commits=$(git log --format='- %s' "${prev_tag}..${version}" 2>/dev/null || true)
    if [[ -z "$commits" ]]; then
        commits=$(git log --format='- %s' -n 20 "${version}" 2>/dev/null || true)
    fi
    [[ -n "$commits" ]] || commits="(no commits found between ${prev_tag} and ${version})"

    local prompt
    prompt=$(cat <<EOF
Write GitHub release notes for Ralph ${version} based on these commits since ${prev_tag}:

${commits}

Output exactly this Markdown structure and nothing else (no preamble, no code fences, no trailing text):

## What's Changed

<2-5 bullet points summarising user-facing changes in English. Group related commits. Drop noise like the version-bump / release commits ("릴리스: 버전을 ..."). Each bullet starts with a past-tense verb.>

## 변경 사항

<Same bullets translated into natural Korean. Match the count and ordering of the English bullets.>

**Full Changelog**: https://github.com/${REPO}/compare/${prev_tag}...${version}
EOF
)
    claude --dangerously-skip-permissions -p "$prompt" > "$out"
    [[ -s "$out" ]] || err "Failed to generate release notes via claude"
}

GENERATED_NOTES_FILE=""
cleanup_generated_notes() {
    [[ -n "$GENERATED_NOTES_FILE" && -f "$GENERATED_NOTES_FILE" ]] && rm -f "$GENERATED_NOTES_FILE" || true
}
trap cleanup_generated_notes EXIT

# ─── Tag (before build, so a build failure leaves no orphan tag pushed) ────
if [[ $CREATE_TAG -eq 1 && $DRY_RUN -eq 0 ]]; then
    if git rev-parse -q --verify "refs/tags/$VERSION" >/dev/null; then
        log "Tag $VERSION already exists locally; skipping creation"
    else
        log "Creating annotated tag $VERSION at HEAD"
        git tag -a "$VERSION" -m "Release $VERSION"
    fi

    if [[ $PUSH_TAG -eq 1 ]]; then
        if git ls-remote --tags origin "refs/tags/$VERSION" 2>/dev/null | grep -q "refs/tags/$VERSION"; then
            log "Tag $VERSION already on origin; skipping push"
        else
            log "Pushing tag $VERSION to origin"
            git push origin "refs/tags/$VERSION"
        fi
    fi
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

# Generate bilingual notes if the user didn't pass --notes
if [[ -z "$NOTES_FILE" && $GENERATE_NOTES -eq 1 ]]; then
    PREV_TAG="$(git tag --list 'v[0-9]*' --sort=-v:refname | grep -v "^${VERSION}$" | head -n1 || true)"
    if [[ -n "$PREV_TAG" ]]; then
        GENERATED_NOTES_FILE="$(mktemp -t ralph-release-notes.XXXXXX)"
        log "Generating bilingual release notes (${PREV_TAG} → ${VERSION}) via claude CLI"
        build_release_notes "$VERSION" "$PREV_TAG" "$GENERATED_NOTES_FILE"
        NOTES_FILE="$GENERATED_NOTES_FILE"
    else
        log "No previous tag found; release will have no body"
    fi
fi

if gh release view "$VERSION" --repo "$REPO" >/dev/null 2>&1; then
    log "Release $VERSION already exists on $REPO; uploading assets (clobber)"
    gh release upload "$VERSION" "${ARTIFACTS[@]}" --repo "$REPO" --clobber
    if [[ -n "$NOTES_FILE" ]]; then
        log "Updating release body with bilingual notes"
        gh release edit "$VERSION" --repo "$REPO" --notes-file "$NOTES_FILE"
    fi
else
    log "Creating release $VERSION on $REPO"
    CREATE_ARGS=("$VERSION" --repo "$REPO" --title "$VERSION")
    [[ $DRAFT -eq 1 ]]      && CREATE_ARGS+=(--draft)
    [[ $PRERELEASE -eq 1 ]] && CREATE_ARGS+=(--prerelease)
    if [[ -n "$NOTES_FILE" ]]; then
        CREATE_ARGS+=(--notes-file "$NOTES_FILE")
    fi
    gh release create "${CREATE_ARGS[@]}" "${ARTIFACTS[@]}"
fi

log "Done. View at: https://github.com/${REPO}/releases/tag/${VERSION}"
