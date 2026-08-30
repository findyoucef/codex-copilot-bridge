#!/usr/bin/env bash
set -euo pipefail

LABEL="com.findyoucef.codex-copilot-bridge"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOURCE_DIR="${1:-$ROOT_DIR/artifacts/publish/codex-copilot-bridge-osx-arm64}"
INSTALL_DIR="${CODEX_COPILOT_BRIDGE_INSTALL_DIR:-$HOME/.local/share/codex-copilot-bridge}"
PORT="${CODEX_COPILOT_BRIDGE_PORT:-17776}"
LOG_DIR="${CODEX_COPILOT_BRIDGE_LOG_DIR:-$HOME/Library/Logs/codex-copilot-bridge}"
BIN_DIR="${CODEX_COPILOT_BRIDGE_BIN_DIR:-$HOME/.local/bin}"
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

if [[ -f "$SOURCE_DIR/codex-copilot" ]]; then
  mkdir -p "$BIN_DIR"
  cp "$SOURCE_DIR/codex-copilot" "$BIN_DIR/codex-copilot"
  chmod +x "$BIN_DIR/codex-copilot"
fi

sed \
  -e "s#__INSTALL_DIR__#$(escape_sed "$INSTALL_DIR")#g" \
  -e "s#__LOG_DIR__#$(escape_sed "$LOG_DIR")#g" \
  -e "s#__PORT__#$(escape_sed "$PORT")#g" \
  "$PLIST_TEMPLATE" > "$PLIST_PATH"

plutil -lint "$PLIST_PATH" >/dev/null
launchctl bootstrap "$GUI_DOMAIN" "$PLIST_PATH"
launchctl enable "$GUI_DOMAIN/$LABEL"
launchctl kickstart -k "$GUI_DOMAIN/$LABEL"

CATALOG_REFRESHED=false
if [[ -x "$BIN_DIR/codex-copilot" ]]; then
  for _ in {1..50}; do
    if CODEX_BIN=/usr/bin/true \
      CODEX_COPILOT_BRIDGE_URL="http://127.0.0.1:$PORT" \
      "$BIN_DIR/codex-copilot" >/dev/null 2>&1; then
      CATALOG_REFRESHED=true
      break
    fi
    sleep 0.1
  done
fi

echo "Installed $LABEL"
echo "URL: http://127.0.0.1:$PORT"
echo "Logs: $LOG_DIR"
if [[ -x "$BIN_DIR/codex-copilot" ]]; then
  echo "Codex launcher: $BIN_DIR/codex-copilot"
fi
if [[ "$CATALOG_REFRESHED" == true ]]; then
  echo "Codex model catalog refreshed"
else
  echo "Warning: model catalog was not refreshed; run codex-copilot after the bridge is ready" >&2
fi
