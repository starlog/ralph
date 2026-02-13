#!/bin/bash

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN_DIR="$HOME/bin"

echo "Installing ralph..."

# Detect OS
detect_os() {
    case "$(uname -s)" in
        Darwin*) echo "macos" ;;
        Linux*)  echo "linux" ;;
        *)       echo "unknown" ;;
    esac
}

# Detect shell and return appropriate rc file
detect_rc_file() {
    local current_shell=$(basename "$SHELL")
    local os=$(detect_os)

    case "$current_shell" in
        zsh)
            echo "$HOME/.zshrc"
            ;;
        bash)
            if [[ "$os" == "macos" ]]; then
                if [[ -f "$HOME/.bash_profile" ]]; then
                    echo "$HOME/.bash_profile"
                else
                    echo "$HOME/.bashrc"
                fi
            else
                echo "$HOME/.bashrc"
            fi
            ;;
        *)
            if [[ -f "$HOME/.zshrc" ]]; then
                echo "$HOME/.zshrc"
            elif [[ -f "$HOME/.bashrc" ]]; then
                echo "$HOME/.bashrc"
            elif [[ -f "$HOME/.bash_profile" ]]; then
                echo "$HOME/.bash_profile"
            else
                echo "$HOME/.profile"
            fi
            ;;
    esac
}

OS=$(detect_os)
RC_FILE=$(detect_rc_file)
CURRENT_SHELL=$(basename "$SHELL")

echo "Detected OS: $OS"
echo "Detected shell: $CURRENT_SHELL"
echo "RC file: $RC_FILE"
echo ""

# Create ~/bin directory if not exist
if [[ ! -d "$BIN_DIR" ]]; then
    echo "Creating $BIN_DIR directory..."
    mkdir -p "$BIN_DIR"
fi

# Copy files to ~/bin
echo "Copying ralph.sh to $BIN_DIR..."
cp "$SCRIPT_DIR/ralph.sh" "$BIN_DIR/"
chmod +x "$BIN_DIR/ralph.sh"

echo "Copying ralph-schema.json to $BIN_DIR..."
cp "$SCRIPT_DIR/ralph-schema.json" "$BIN_DIR/"

# Check if PATH already contains ~/bin
path_already_configured() {
    local rc_file=$1
    local home_bin_expanded="$HOME/bin"

    # Check current PATH environment variable
    if echo "$PATH" | tr ':' '\n' | grep -qx "$home_bin_expanded"; then
        return 0
    fi

    # Check rc file for various patterns
    if [[ -f "$rc_file" ]]; then
        if grep -q 'PATH=.*\$HOME/bin' "$rc_file" || \
           grep -q 'PATH=.*~/bin' "$rc_file" || \
           grep -q "PATH=.*$home_bin_expanded" "$rc_file"; then
            return 0
        fi
    fi

    return 1
}

# Add PATH to rc file if not included
if path_already_configured "$RC_FILE"; then
    echo "PATH already includes ~/bin (detected in current PATH or $RC_FILE)"
else
    echo "Adding ~/bin to PATH in $RC_FILE..."
    echo '' >> "$RC_FILE"
    echo '# Added by ralph installer' >> "$RC_FILE"
    echo 'export PATH="$HOME/bin:$PATH"' >> "$RC_FILE"
fi

echo ""
echo "Installation complete!"
echo "Run 'source $RC_FILE' or restart your terminal to use ralph.sh"
