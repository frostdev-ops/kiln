#!/usr/bin/env bash
# Cooks MaterialSample's assets/cook.json into assets/cooked/ (gitignored) and blocks/ember_sword.materials.json. The installed game is only read.
#   GAME  game folder (default: Steam's Enshrouded)   EXE  its exe (default GAME/enshrouded.exe; a copy is fine)
#   KILN_OUT  tools' cache (default a folder in the system temp dir)   --sources  regenerate textures/ and assets/src/ first (tools/modelcook/samples/material_sword.py)
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../.." && pwd)
game=${GAME:-$HOME/.local/share/Steam/steamapps/common/Enshrouded}
exe=${EXE:-$game/enshrouded.exe}
export KILN_OUT=${KILN_OUT:-${TMPDIR:-/tmp}/kiln-models}
if [ "${1:-}" = "--sources" ]; then
  python3 "$root/tools/modelcook/samples/material_sword.py" "$here"
fi
python3 "$root/tools/modelcook/modelcook.py" --game "$game" --exe "$exe" recipe "$here/assets/cook.json" --update-manifest
