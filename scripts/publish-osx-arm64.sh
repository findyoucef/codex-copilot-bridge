#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PUBLISH_ROOT="$ROOT_DIR/artifacts/publish"
PUBLISH_DIR="$PUBLISH_ROOT/codex-copilot-bridge-osx-arm64"
DIST_DIR="$ROOT_DIR/artifacts/dist"
ARCHIVE="$DIST_DIR/codex-copilot-bridge-osx-arm64.tar.gz"

rm -rf "$PUBLISH_DIR" "$ARCHIVE"
mkdir -p "$PUBLISH_ROOT" "$DIST_DIR"

dotnet publish "$ROOT_DIR/CodexCopilotBridge/CodexCopilotBridge.csproj" \
  --configuration Release \
  --runtime osx-arm64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true \
  --output "$PUBLISH_DIR"

tar -C "$PUBLISH_ROOT" -czf "$ARCHIVE" "codex-copilot-bridge-osx-arm64"

echo "Published $PUBLISH_DIR"
echo "Archived $ARCHIVE"
