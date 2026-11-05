#!/usr/bin/env bash
# Cooks ModelSample's assets/cook.json into assets/cooked/ (gitignored). The installed game is only read.
#   GAME  game folder (default: Steam's Enshrouded)   EXE  its exe (default GAME/enshrouded.exe; a copy is fine)
#   KILN_OUT  tools' cache (default <repo>/.out/models)   --sources  regenerate assets/src/*.gltf first
# The icons (assets/icons/*.png, committed: Kiln's own art) are rendered from the same glTF by tools/assets/render-icon.py;
# the UV regions of the vanilla sword texture the models use get flat colours (steel, dark metal, leather).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../.." && pwd)
game=${GAME:-$HOME/.local/share/Steam/steamapps/common/Enshrouded}
exe=${EXE:-$game/enshrouded.exe}
export KILN_OUT=${KILN_OUT:-$root/.out/models}
if [ "${1:-}" = "--sources" ]; then
  python3 "$root/tools/modelcook/samples/cube_sword.py" "$here/assets/src/cube_sword.gltf" cubes
  python3 "$root/tools/modelcook/samples/cube_sword.py" "$here/assets/src/tower_sword.gltf" tower
fi
python3 "$root/tools/modelcook/modelcook.py" --game "$game" --exe "$exe" recipe "$here/assets/cook.json"
uv='0.10,0.40,0.50,0.85=#c4ccd4;0.06,0.03,0.44,0.14=#3c3a3a;0.72,0.26,0.94,0.50=#7a5232'
mkdir -p "$here/assets/icons"
python3 "$root/tools/assets/render-icon.py" "$here/assets/src/cube_sword.gltf" "$here/assets/icons/cube_sword.png" --uv-colors "$uv"
python3 "$root/tools/assets/render-icon.py" "$here/assets/src/tower_sword.gltf" "$here/assets/icons/tower_sword.png" --uv-colors "$uv"
