#!/usr/bin/env bash
set -euo pipefail

LABEL="com.findyoucef.codex-copilot-bridge"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOURCE_DIR="${1:-$ROOT_DIR/artifacts/publish/codex-copilot-bridge-osx-arm64}"
INSTALL_DIR="${CODEX_COPILOT_BRIDGE_INSTALL_DIR:-$HOME/.local/share/codex-copilot-bridge}"
PORT="${CODEX_COPILOT_BRIDGE_PORT:-17776}"
LOG_DIR="${CODEX_COPILOT_BRIDGE_LOG_DIR:-$HOME/Library/Logs/codex-copilot-bridge}"
PLIST_TEMPLATE="$ROOT_DIR/ops/launchd/$LABEL.plist"
PLIST_PATH="$HOME/Library/LaunchAgents/$LABEL.plist"
GUI_DOMAIN="gui/$(id -u)"

escape_sed() {
  printf '%s' "$1" | sed 's/[#&]/\\&/g'
}

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "install-macos-launchd.sh only supports macOS." >&2
  exit 1
fi

if [[ ! -x "$SOURCE_DIR/CodexCopilotBridge" ]]; then
  echo "Missing executable: $SOURCE_DIR/CodexCopilotBridge" >&2
  echo "Run scripts/publish-osx-arm64.sh first, or pass an extracted publish directory." >&2
  exit 1
fi

mkdir -p "$(dirname "$PLIST_PATH")" "$LOG_DIR"

if launchctl print "$GUI_DOMAIN/$LABEL" >/dev/null 2>&1; then
  launchctl bootout "$GUI_DOMAIN" "$PLIST_PATH" >/dev/null 2>&1 || true
fi

rm -rf "$INSTALL_DIR"
mkdir -p "$INSTALL_DIR"
ditto "$SOURCE_DIR" "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/CodexCopilotBridge"

sed \
  -e "s#__INSTALL_DIR__#$(escape_sed "$INSTALL_DIR")#g" \
  -e "s#__LOG_DIR__#$(escape_sed "$LOG_DIR")#g" \
  -e "s#__PORT__#$(escape_sed "$PORT")#g" \
  "$PLIST_TEMPLATE" > "$PLIST_PATH"

plutil -lint "$PLIST_PATH" >/dev/null
launchctl bootstrap "$GUI_DOMAIN" "$PLIST_PATH"
launchctl enable "$GUI_DOMAIN/$LABEL"
launchctl kickstart -k "$GUI_DOMAIN/$LABEL"

echo "Installed $LABEL"
echo "URL: http://127.0.0.1:$PORT"
echo "Logs: $LOG_DIR"
