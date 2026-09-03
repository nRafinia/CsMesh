#!/usr/bin/env sh
# csmesh installer - https://github.com/nRafinia/CsMesh
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/nRafinia/CsMesh/main/install.sh | sh
#
# Environment variables:
#   CSMESH_INSTALL_DIR : destination directory (default: ~/.local/bin or ~/bin)
#   CSMESH_VERSION     : specific version tag to install (default: latest)
#   CSMESH_USE_DOTNET  : set to 1 to force installation via dotnet tool

set -e

REPO="nRafinia/CsMesh"
TOOL_ID="CsMesh.Cli"
BINARY_NAME="csmesh"

# Formatting & Colors
if [ -t 1 ]; then
    RED='\033[0;31m'
    GREEN='\033[0;32m'
    YELLOW='\033[1;33m'
    BLUE='\033[0;34m'
    BOLD='\033[1m'
    NC='\033[0m'
else
    RED=''
    GREEN=''
    YELLOW=''
    BLUE=''
    BOLD=''
    NC=''
fi

info() {
    printf "${BLUE}[INFO]${NC} %s\n" "$1"
}

success() {
    printf "${GREEN}${BOLD}[SUCCESS]${NC} %s\n" "$1"
}

warn() {
    printf "${YELLOW}[WARN]${NC} %s\n" "$1"
}

error() {
    printf "${RED}${BOLD}[ERROR]${NC} %s\n" "$1"
    exit 1
}

# Determine installation directory
if [ -n "$CSMESH_INSTALL_DIR" ]; then
    INSTALL_DIR="$CSMESH_INSTALL_DIR"
elif [ -d "$HOME/.local/bin" ] || [ ! -d "$HOME/bin" ]; then
    INSTALL_DIR="$HOME/.local/bin"
else
    INSTALL_DIR="$HOME/bin"
fi

# Detect OS
detect_os() {
    case "$(uname -s)" in
        Linux*)
            OS="linux"
            ;;
        Darwin*)
            OS="osx"
            ;;
        MINGW*|MSYS*|CYGWIN*)
            OS="win"
            BINARY_NAME="csmesh.exe"
            ;;
        *)
            OS="unknown"
            ;;
    esac
}

# Detect Architecture
detect_arch() {
    case "$(uname -m)" in
        x86_64|amd64)
            ARCH="x64"
            ;;
        arm64|aarch64)
            ARCH="arm64"
            ;;
        *)
            ARCH="unknown"
            ;;
    esac
}

# Try to install via dotnet tool as fallback or when requested
install_via_dotnet() {
    if command -v dotnet >/dev/null 2>&1; then
        info "Installing CsMesh as a .NET global tool via 'dotnet tool'..."
        if dotnet tool update --global "$TOOL_ID" 2>/dev/null || dotnet tool install --global "$TOOL_ID"; then
            success "csmesh installed successfully via dotnet tool!"
            echo ""
            info "Run 'csmesh --help' or 'csmesh index' to get started."
            exit 0
        fi
    fi
    return 1
}

# 1. If user explicitly requested dotnet tool
if [ "$CSMESH_USE_DOTNET" = "1" ]; then
    install_via_dotnet || error "dotnet tool installation failed. Ensure .NET SDK is installed."
fi

detect_os
detect_arch

if [ "$OS" = "unknown" ] || [ "$ARCH" = "unknown" ]; then
    warn "Unsupported OS ($(uname -s)) or Architecture ($(uname -m)) for prebuilt binaries."
    install_via_dotnet || error "Could not install via prebuilt binary or dotnet tool."
    exit 0
fi

# Determine target asset name
if [ "$OS" = "win" ]; then
    ARCHIVE_EXT="zip"
    ASSET_NAME="csmesh-win-${ARCH}.zip"
else
    ARCHIVE_EXT="tar.gz"
    ASSET_NAME="csmesh-${OS}-${ARCH}.tar.gz"
fi

# Resolve version
if [ -n "$CSMESH_VERSION" ]; then
    VERSION="$CSMESH_VERSION"
    RELEASE_URL="https://github.com/${REPO}/releases/download/${VERSION}"
else
    # Resolve latest release tag without exhausting API rate limits
    info "Resolving latest release of ${REPO}..."
    VERSION=$(curl -sI "https://github.com/${REPO}/releases/latest" \
        | grep -i '^location:' \
        | sed -E 's|.*/tag/([^[:space:]]+).*|\1|' \
        | tr -d '\r')

    if [ -z "$VERSION" ]; then
        VERSION=$(curl -fsSL "https://api.github.com/repos/${REPO}/releases/latest" 2>/dev/null \
            | grep '"tag_name":' \
            | sed -E 's/.*"([^"]+)".*/\1/' || true)
    fi

    if [ -n "$VERSION" ]; then
        RELEASE_URL="https://github.com/${REPO}/releases/download/${VERSION}"
    else
        RELEASE_URL="https://github.com/${REPO}/releases/latest/download"
    fi
fi

DOWNLOAD_URL="${RELEASE_URL}/${ASSET_NAME}"
info "Target binary: ${ASSET_NAME}"

# Temporary directory for download and extraction
TMP_DIR=$(mktemp -d 2>/dev/null || mktemp -d -t 'csmesh')
cleanup() {
    rm -rf "$TMP_DIR"
}
trap cleanup EXIT INT TERM

info "Downloading ${DOWNLOAD_URL}..."
HTTP_CODE=$(curl -sSL -w "%{http_code}" -o "${TMP_DIR}/${ASSET_NAME}" "$DOWNLOAD_URL" || echo "000")

if [ "$HTTP_CODE" != "200" ]; then
    warn "Prebuilt release binary not found (HTTP ${HTTP_CODE})."
    info "Attempting fallback to .NET global tool installation..."
    if install_via_dotnet; then
        exit 0
    else
        error "Failed to download binary from ${DOWNLOAD_URL} and .NET SDK is not available.\nFor manual installation: dotnet tool install --global CsMesh.Cli"
    fi
fi

# Ensure target directory exists
mkdir -p "$INSTALL_DIR"

info "Extracting to ${INSTALL_DIR}..."
if [ "$ARCHIVE_EXT" = "zip" ]; then
    if command -v unzip >/dev/null 2>&1; then
        unzip -q -o "${TMP_DIR}/${ASSET_NAME}" -d "$INSTALL_DIR"
    elif command -v 7z >/dev/null 2>&1; then
        7z x -y "${TMP_DIR}/${ASSET_NAME}" -o"$INSTALL_DIR" >/dev/null
    else
        error "Neither 'unzip' nor '7z' was found to extract ${ASSET_NAME}."
    fi
else
    tar -xzf "${TMP_DIR}/${ASSET_NAME}" -C "$INSTALL_DIR"
fi

TARGET_BIN="${INSTALL_DIR}/${BINARY_NAME}"
if [ ! -f "$TARGET_BIN" ]; then
    error "Extraction finished but binary was not found at ${TARGET_BIN}"
fi

chmod +x "$TARGET_BIN"
success "Installed csmesh to ${TARGET_BIN}"

# PATH Verification
case ":$PATH:" in
    *":$INSTALL_DIR:"*)
        IN_PATH=true
        ;;
    *)
        IN_PATH=false
        ;;
esac

echo ""
if [ "$IN_PATH" = "false" ]; then
    warn "${INSTALL_DIR} is not in your PATH environment variable."
    printf "Add it to your shell configuration file (e.g. ~/.bashrc, ~/.zshrc):\n"
    printf "  ${BOLD}export PATH=\"%s:\$PATH\"${NC}\n\n" "$INSTALL_DIR"
    printf "Then restart your shell or run:\n"
    printf "  ${BOLD}export PATH=\"%s:\$PATH\"${NC}\n\n" "$INSTALL_DIR"
fi

# Try running csmesh to verify
if [ "$IN_PATH" = "true" ] && command -v csmesh >/dev/null 2>&1; then
    csmesh --version 2>/dev/null || true
else
    "$TARGET_BIN" --version 2>/dev/null || true
fi

echo ""
success "csmesh is ready! Run 'csmesh --help' or 'csmesh index' inside any .NET project."
