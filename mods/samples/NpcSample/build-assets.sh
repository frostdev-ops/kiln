#!/usr/bin/env bash
# Cooks NpcSample's assets/cook.json into assets/cooked/ (gitignored) and keeps assets/assets.json in step. New sounds
# only: no game needed. KILN_OUT: tools' cache and the native helper's build (default <repo>/.out/soundcook).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../.." && pwd)
export KILN_OUT=${KILN_OUT:-$root/.out/soundcook}
python3 "$root/tools/soundcook/soundcook.py" --no-game recipe "$here/assets/cook.json" --update-manifest
