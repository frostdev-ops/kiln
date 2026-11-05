#!/usr/bin/env bash
# Cooks ImpactSample's impact/cook.json into assets/cooked/impact/ (gitignored) and updates assets/assets.json.
# The installed game is only read.
#   GAME  game folder (default: Steam's Enshrouded client; a dedicated server folder works too)
#   KILN_OUT  tools' cache (default <repo>/.out/impact)
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../.." && pwd)
game=${GAME:-$HOME/.local/share/Steam/steamapps/common/Enshrouded}
export KILN_OUT=${KILN_OUT:-$root/.out/impact}
python3 "$root/tools/impact/impact.py" --game "$game" cook "$here/impact/cook.json" --update-manifest
