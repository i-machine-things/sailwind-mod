#!/usr/bin/env bash
# Installs the third-party BepInEx plugin dependencies this mod needs, into the live
# Sailwind install's BepInEx/plugins/ folder - same convention our own mod uses, but these
# aren't ours to build, so there's nothing for `dotnet build` to deploy for them.
#
# Pinned to specific tested versions, not "latest" - a silent upstream update could change
# behavior (or break the GUID/API assumptions documented in .claude/CODING_NOTES.md) without
# us noticing. Bump the version/checksum here deliberately, after testing, not automatically.
#
# Usage: ./scripts/install-deps.sh [path to Sailwind install]
#   Defaults to the path mod/SailwindRealEarth.csproj's $(SailwindDir) also uses - keep
#   both in sync if that ever changes.

set -euo pipefail

SAILWIND_DIR="${1:-$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Sailwind}"
PLUGINS_DIR="$SAILWIND_DIR/BepInEx/plugins"

if [ ! -d "$SAILWIND_DIR/BepInEx" ]; then
    echo "error: $SAILWIND_DIR/BepInEx not found - is BepInEx itself installed, and is the path right?" >&2
    echo "Pass the install path explicitly: $0 /path/to/Sailwind" >&2
    exit 1
fi

install_dep() {
    local name="$1" url="$2" expected_sha256="$3" dest_dir="$4" dest_file="$5"

    local dest="$PLUGINS_DIR/$dest_dir/$dest_file"
    if [ -f "$dest" ]; then
        local current_sha
        current_sha="$(sha256sum "$dest" | cut -d' ' -f1)"
        if [ "$current_sha" = "$expected_sha256" ]; then
            echo "$name: already installed and matches pinned version, skipping."
            return
        fi
        echo "$name: installed but checksum doesn't match the pinned version - reinstalling."
    fi

    mkdir -p "$PLUGINS_DIR/$dest_dir"
    local tmp
    tmp="$(mktemp)"
    echo "$name: downloading..."
    curl -sL -o "$tmp" "$url"

    local actual_sha
    actual_sha="$(sha256sum "$tmp" | cut -d' ' -f1)"
    if [ "$actual_sha" != "$expected_sha256" ]; then
        echo "error: $name checksum mismatch - expected $expected_sha256, got $actual_sha" >&2
        echo "Refusing to install an unverified file. Check the URL/version above." >&2
        rm -f "$tmp"
        exit 1
    fi

    mv "$tmp" "$dest"
    echo "$name: installed to $dest (sha256 verified)."
}

# NANDbrew/BorderExpander v0.5.0 - expands WorldBorder to +-70deg, enables free sailing.
# GUID com.nandbrew.borderexpander confirmed by strings-ing the DLL (see CODING_NOTES.md).
# IMPORTANT: after install, enable Settings -> Free Sailing in its generated config
# (BepInEx/config/com.nandbrew.borderexpander.cfg) - defaults to false.
install_dep \
    "BorderExpander" \
    "https://github.com/NANDbrew/BorderExpander/releases/download/v0.5.0/BorderExpander.dll" \
    "14a9a3daf5b9ec67ee7c585c3d5a5eda79fa0e4a2bc72d9b19e39ce9c285e874" \
    "BorderExpander" \
    "BorderExpander.dll"

# BepInEx/BepInEx.ConfigurationManager v19.0 (BepInEx5 build) - adds an in-game overlay
# (default key: F1) listing every loaded mod's BepInEx config options, so toggles like
# HistoricalWindEnabled and RepositionIslands don't need hand-editing a .cfg file.
install_dep \
    "ConfigurationManager" \
    "https://github.com/BepInEx/BepInEx.ConfigurationManager/releases/download/v19.0/BepInEx.ConfigurationManager_BepInEx5_v19.0.zip" \
    "eed83f6e6accbdd0c3771f3061a6ae05b979b9b9b8a00dead69aabc1bf5fe950" \
    "." \
    ".ConfigurationManager.zip.tmp"

# The ConfigurationManager release is a zip with the DLL nested at a fixed path inside it -
# extract just that. Extracts to a temp file first and only moves it into place once unzip
# has actually succeeded (and produced non-empty output), so a failed/partial extraction can
# never truncate or corrupt an existing good install - redirecting unzip's output straight at
# the destination would truncate it immediately, before unzip could fail. Also keeps the
# checksum-verified zip around afterward (instead of deleting it) so a later run can re-extract
# without needing network access again. Caught by CodeRabbit (PR #3).
if [ -f "$PLUGINS_DIR/.ConfigurationManager.zip.tmp" ]; then
    mkdir -p "$PLUGINS_DIR/ConfigurationManager"
    extract_tmp="$(mktemp)"
    if unzip -p "$PLUGINS_DIR/.ConfigurationManager.zip.tmp" \
        "BepInEx/plugins/ConfigurationManager/ConfigurationManager.dll" \
        > "$extract_tmp" && [ -s "$extract_tmp" ]; then
        mv "$extract_tmp" "$PLUGINS_DIR/ConfigurationManager/ConfigurationManager.dll"
        echo "ConfigurationManager: extracted DLL to $PLUGINS_DIR/ConfigurationManager/ConfigurationManager.dll"
    else
        echo "error: ConfigurationManager extraction failed - existing install (if any) left untouched" >&2
        rm -f "$extract_tmp"
        exit 1
    fi
fi

echo
echo "Done. Remember: BorderExpander's 'Free Sailing' setting defaults to off - enable it in"
echo "BepInEx/config/com.nandbrew.borderexpander.cfg (Settings -> Free Sailing = true) or the"
echo "mod will warn on startup and the vanilla world-border will bounce you back to the dock."
