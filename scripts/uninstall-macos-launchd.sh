#!/usr/bin/env bash
set -euo pipefail

LABEL="com.findyoucef.codex-copilot-bridge"
INSTALL_DIR="${CODEX_COPILOT_BRIDGE_INSTALL_DIR:-$HOME/.local/share/codex-copilot-bridge}"
LOG_DIR="${CODEX_COPILOT_BRIDGE_LOG_DIR:-$HOME/Library/Logs/codex-copilot-bridge}"
BIN_DIR="${CODEX_COPILOT_BRIDGE_BIN_DIR:-$HOME/.local/bin}"
PLIST_PATH="$HOME/Library/LaunchAgents/$LABEL.plist"
GUI_DOMAIN="gui/$(id -u)"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "uninstall-macos-launchd.sh only supports macOS." >&2
  exit 1
fi

if launchctl print "$GUI_DOMAIN/$LABEL" >/dev/null 2>&1; then
  launchctl bootout "$GUI_DOMAIN" "$PLIST_PATH" >/dev/null 2>&1 || true
fi

rm -f "$PLIST_PATH"

if [[ "${1:-}" == "--remove-files" ]]; then
  rm -rf "$INSTALL_DIR" "$LOG_DIR"
  rm -f "$BIN_DIR/codex-copilot"
  echo "Removed service, install directory, and logs."
else
  echo "Removed service. Pass --remove-files to remove $INSTALL_DIR and $LOG_DIR."
fi
