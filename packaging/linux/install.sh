#!/bin/sh
# Adds Kakudai to the application menu, pointing at this folder. Run it again if you move the folder.
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
mkdir -p "$apps"
sed "s|@DIR@|$dir|g" "$dir/kakudai.desktop" > "$apps/kakudai.desktop"
echo "Kakudai added to the application menu ($apps/kakudai.desktop)."
