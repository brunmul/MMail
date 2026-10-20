#!/bin/bash
# Installs the latest MMail (facteur branch) into /Applications and connects it to Claude.
#   curl -fsSL https://raw.githubusercontent.com/brunmul/MMail/facteur/scripts/install.sh | bash
set -euo pipefail

URL="https://github.com/brunmul/MMail/releases/download/facteur-latest/MMail.zip"
EXE="/Applications/MMail.app/Contents/MacOS/MMail"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "→ Téléchargement de MMail…"
for i in $(seq 1 40); do
    curl -fsL -o "$TMP/MMail.zip" "$URL" && break
    [ "$i" = 40 ] && { echo "✗ L'app n'est pas encore disponible, réessaie dans quelques minutes."; exit 1; }
    echo "  (pas encore prête, nouvel essai dans 30 s)"; sleep 30
done

echo "→ Installation dans Applications…"
ditto -x -k "$TMP/MMail.zip" "$TMP/app"
osascript -e 'quit app "MMail"' >/dev/null 2>&1 || true
rm -rf /Applications/MMail.app
mv "$TMP/app/MMail.app" /Applications/
xattr -dr com.apple.quarantine /Applications/MMail.app 2>/dev/null || true

echo "→ Branchement de Claude Desktop…"
CONF="$HOME/Library/Application Support/Claude/claude_desktop_config.json"
mkdir -p "$(dirname "$CONF")"
if [ -s "$CONF" ]; then cp "$CONF" "$CONF.bak"; else echo '{}' > "$CONF"; fi
plutil -extract mcpServers raw "$CONF" >/dev/null 2>&1 || plutil -insert mcpServers -dictionary "$CONF"
plutil -replace mcpServers.mmail -json "{\"command\":\"$EXE\",\"args\":[\"--mcp\"]}" "$CONF"

if command -v claude >/dev/null 2>&1; then
    echo "→ Branchement de Claude Code…"
    claude mcp remove --scope user mmail >/dev/null 2>&1 || true
    claude mcp add --scope user mmail -- "$EXE" --mcp >/dev/null
fi

open /Applications/MMail.app
echo "✓ TOUT EST OK. Quitte Claude Desktop (Cmd+Q) et rouvre-le : MMail y sera branché."
