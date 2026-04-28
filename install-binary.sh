#!/usr/bin/env bash
# Ralph universal installer — downloads a pre-built binary from GitHub Releases.
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/starlog/ralph/main/install-binary.sh | bash
#   curl -fsSL https://raw.githubusercontent.com/starlog/ralph/main/install-binary.sh | bash -s -- --dir ~/.local/bin
#   curl -fsSL https://raw.githubusercontent.com/starlog/ralph/main/install-binary.sh | bash -s -- --version v1.0
#
# .NET SDK 설치 없이 바로 사용 가능한 self-contained binary를 받아 설치합니다.

set -euo pipefail

REPO="${RALPH_REPO:-starlog/ralph}"
VERSION=""
INSTALL_DIR="${HOME}/.local/bin"
QUIET=0

usage() {
    cat <<EOF
Ralph universal installer

Usage:
  install-binary.sh [--version <tag>] [--dir <path>] [--quiet]

Options:
  --version <tag>   Install a specific tag (default: latest release)
  --dir <path>      Install directory (default: \$HOME/.local/bin)
  --quiet           Less verbose output
  -h, --help        Show this help

Environment:
  RALPH_REPO        Override the source repo (default: $REPO)
EOF
}

log() { [[ $QUIET -eq 0 ]] && echo "$@"; }
err() { echo "Error: $*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version) VERSION="$2"; shift 2 ;;
        --dir)     INSTALL_DIR="$2"; shift 2 ;;
        --quiet)   QUIET=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *)         err "Unknown option: $1 (try --help)" ;;
    esac
done

# ─── OS / arch 감지 ─────────────────────────────────────────────────────────
detect_rid() {
    local os arch
    case "$(uname -s)" in
        Darwin*)  os="osx" ;;
        Linux*)   os="linux" ;;
        MINGW*|MSYS*|CYGWIN*) os="win" ;;
        *) err "Unsupported OS: $(uname -s)" ;;
    esac

    case "$(uname -m)" in
        x86_64|amd64)  arch="x64" ;;
        arm64|aarch64) arch="arm64" ;;
        *) err "Unsupported architecture: $(uname -m)" ;;
    esac

    echo "${os}-${arch}"
}

# ─── 의존 도구 ─────────────────────────────────────────────────────────────
need() { command -v "$1" >/dev/null 2>&1 || err "'$1' is required but not installed"; }
need curl
need tar

# ─── 최신 버전 조회 ────────────────────────────────────────────────────────
fetch_latest_tag() {
    local api="https://api.github.com/repos/${REPO}/releases/latest"
    curl -fsSL "$api" | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -1
}

RID=$(detect_rid)
log "Platform: $RID"

if [[ -z "$VERSION" ]]; then
    log "Fetching latest release tag..."
    VERSION=$(fetch_latest_tag) || err "Failed to fetch latest release"
    [[ -n "$VERSION" ]] || err "Could not determine latest version"
fi
log "Version:  $VERSION"

# ─── 다운로드 URL 결정 ──────────────────────────────────────────────────────
ext="tar.gz"
[[ "$RID" == win-* ]] && ext="zip"
ASSET="ralph-${VERSION}-${RID}.${ext}"
URL="https://github.com/${REPO}/releases/download/${VERSION}/${ASSET}"
SUMS_URL="https://github.com/${REPO}/releases/download/${VERSION}/ralph-${VERSION}-SHA256SUMS.txt"

# ─── 임시 디렉토리에 다운로드 ──────────────────────────────────────────────
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

log "Downloading $ASSET..."
curl -fsSL -o "$TMP/$ASSET" "$URL" || err "Download failed: $URL"

# ─── 체크섬 검증 (제공되면) ────────────────────────────────────────────────
if curl -fsSL -o "$TMP/SHA256SUMS.txt" "$SUMS_URL" 2>/dev/null; then
    log "Verifying SHA256..."
    expected=$(grep " $ASSET\$" "$TMP/SHA256SUMS.txt" | awk '{print $1}' || true)
    if [[ -n "$expected" ]]; then
        if command -v sha256sum >/dev/null 2>&1; then
            actual=$(sha256sum "$TMP/$ASSET" | awk '{print $1}')
        elif command -v shasum >/dev/null 2>&1; then
            actual=$(shasum -a 256 "$TMP/$ASSET" | awk '{print $1}')
        else
            log "  (sha256sum/shasum not available, skipping verification)"
            actual=""
        fi
        if [[ -n "$actual" ]]; then
            [[ "$actual" == "$expected" ]] || err "SHA256 mismatch: expected $expected, got $actual"
            log "  SHA256 OK"
        fi
    else
        log "  (no checksum entry for $ASSET in SHA256SUMS.txt, skipping)"
    fi
fi

# ─── 압축 해제 ────────────────────────────────────────────────────────────
log "Extracting..."
cd "$TMP"
if [[ "$ext" == "zip" ]]; then
    need unzip
    unzip -q "$ASSET"
    BIN_NAME="ralph.exe"
else
    tar -xzf "$ASSET"
    BIN_NAME="ralph"
fi

[[ -f "$BIN_NAME" ]] || err "Binary '$BIN_NAME' not found in archive"

# ─── 설치 ──────────────────────────────────────────────────────────────────
mkdir -p "$INSTALL_DIR"
DEST="$INSTALL_DIR/$BIN_NAME"
mv "$BIN_NAME" "$DEST"
chmod +x "$DEST"

log "Installed: $DEST"

# ─── PATH 안내 ────────────────────────────────────────────────────────────
case ":$PATH:" in
    *":$INSTALL_DIR:"*) log "Done. Run 'ralph --help' to get started." ;;
    *)
        log ""
        log "Note: $INSTALL_DIR is not in your PATH."
        log "Add this to your shell rc file (~/.zshrc, ~/.bashrc, etc.):"
        log ""
        log "  export PATH=\"$INSTALL_DIR:\$PATH\""
        log ""
        ;;
esac
