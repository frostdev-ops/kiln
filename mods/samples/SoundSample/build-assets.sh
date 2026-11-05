#!/usr/bin/env bash
# Cooks SoundSample's assets/cook.json into assets/cooked/ (gitignored) and keeps assets/assets.json in step.
# New sounds need no game; the one override (the vanilla dialogue-confirm cue) reads the installed client, read only.
#   GAME  client folder (default: Steam's Enshrouded)   EXE  its exe (default GAME/enshrouded.exe; a copy is fine)
#   KILN_OUT  tools' cache and the native helper's build (default <repo>/.out/soundcook)
#   --sources  regenerate audio/** first (tools/soundcook/samples/tones.py; FLAC/Ogg/MP3 need ffmpeg)
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../.." && pwd)
game=${GAME:-$HOME/.local/share/Steam/steamapps/common/Enshrouded}
export KILN_OUT=${KILN_OUT:-$root/.out/soundcook}
if [ "${1:-}" = "--sources" ]; then
  python3 "$root/tools/soundcook/samples/tones.py" "$here/audio"
fi
args=(--game "$game")
[ -n "${EXE:-}" ] && args+=(--exe "$EXE")
python3 "$root/tools/soundcook/soundcook.py" "${args[@]}" recipe "$here/assets/cook.json" --update-manifest
