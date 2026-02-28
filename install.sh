#!/bin/bash

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/Ralph/Ralph.csproj"

echo "==============================="
echo "  Ralph Installer"
echo "==============================="
echo ""

# ─── Detect OS and Architecture ──────────────────────────────────────────────

detect_rid() {
    local os arch
    case "$(uname -s)" in
        Darwin*) os="osx" ;;
        Linux*)  os="linux" ;;
        *)
            echo "Error: Unsupported OS '$(uname -s)'" >&2
            exit 1
            ;;
    esac

    case "$(uname -m)" in
        x86_64|amd64)  arch="x64" ;;
        arm64|aarch64) arch="arm64" ;;
        *)
            echo "Error: Unsupported architecture '$(uname -m)'" >&2
            exit 1
            ;;
    esac

    echo "${os}-${arch}"
}

RID=$(detect_rid)
echo "Platform: $RID"

# ─── Check .NET SDK ──────────────────────────────────────────────────────────

if ! command -v dotnet &>/dev/null; then
    echo "Error: .NET SDK not found. Install from https://dot.net/download"
    exit 1
fi

echo ".NET SDK: $(dotnet --version)"
echo ""

# ─── Publish ─────────────────────────────────────────────────────────────────

PUBLISH_DIR="$SCRIPT_DIR/Ralph/publish"

echo "Publishing ralph for $RID..."
dotnet publish "$PROJECT" -c Release -r "$RID" -o "$PUBLISH_DIR" --nologo -v q
echo "Build complete."
echo ""

# ─── Ask destination ─────────────────────────────────────────────────────────

DEFAULT_DIR="$HOME/bin"
read -rp "Install directory [$DEFAULT_DIR]: " INSTALL_DIR
INSTALL_DIR="${INSTALL_DIR:-$DEFAULT_DIR}"

# Expand ~ manually
INSTALL_DIR="${INSTALL_DIR/#\~/$HOME}"

if [[ ! -d "$INSTALL_DIR" ]]; then
    read -rp "'$INSTALL_DIR' does not exist. Create it? [Y/n]: " CREATE
    CREATE="${CREATE:-Y}"
    if [[ "$CREATE" =~ ^[Yy]$ ]]; then
        mkdir -p "$INSTALL_DIR"
    else
        echo "Aborted."
        exit 1
    fi
fi

# ─── Copy binary ─────────────────────────────────────────────────────────────

echo "Installing ralph to $INSTALL_DIR..."
cp "$PUBLISH_DIR/ralph" "$INSTALL_DIR/ralph"
chmod +x "$INSTALL_DIR/ralph"

# ─── PATH check ──────────────────────────────────────────────────────────────

if echo "$PATH" | tr ':' '\n' | grep -qx "$INSTALL_DIR"; then
    echo ""
    echo "Done! 'ralph' is ready to use."
else
    # Detect rc file
    detect_rc_file() {
        local current_shell
        current_shell=$(basename "$SHELL")
        case "$current_shell" in
            zsh)  echo "$HOME/.zshrc" ;;
            bash)
                if [[ "$(uname -s)" == Darwin* && -f "$HOME/.bash_profile" ]]; then
                    echo "$HOME/.bash_profile"
                else
                    echo "$HOME/.bashrc"
                fi
                ;;
            *)
                for f in "$HOME/.zshrc" "$HOME/.bashrc" "$HOME/.bash_profile" "$HOME/.profile"; do
                    [[ -f "$f" ]] && { echo "$f"; return; }
                done
                echo "$HOME/.profile"
                ;;
        esac
    }

    RC_FILE=$(detect_rc_file)

    # Check if already configured in rc file
    already_in_rc=false
    if [[ -f "$RC_FILE" ]]; then
        if grep -q "PATH=.*$(echo "$INSTALL_DIR" | sed 's|/|\\/|g')" "$RC_FILE" 2>/dev/null; then
            already_in_rc=true
        fi
    fi

    if [[ "$already_in_rc" == false ]]; then
        read -rp "'$INSTALL_DIR' is not in PATH. Add to $RC_FILE? [Y/n]: " ADD_PATH
        ADD_PATH="${ADD_PATH:-Y}"
        if [[ "$ADD_PATH" =~ ^[Yy]$ ]]; then
            echo '' >> "$RC_FILE"
            echo '# Added by ralph installer' >> "$RC_FILE"
            echo "export PATH=\"$INSTALL_DIR:\$PATH\"" >> "$RC_FILE"
            echo "Added to $RC_FILE. Run 'source $RC_FILE' or restart your terminal."
        fi
    fi

    echo ""
    echo "Done! ralph installed at $INSTALL_DIR/ralph"
fi
