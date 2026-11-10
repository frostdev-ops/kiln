#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Renders the Kiln mod manager to PNGs without the game (tools/ui-preview/README.md).
#   tools/ui-preview/preview.sh [--font TTF] [--no-game-font] [--scene NAME] [--size WxH --scale S]
# Output: $KILN_OUT/preview/*.png (KILN_OUT default: <repo>/.out). Builds the shim and the preview app first.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
: "${KILN_OUT:=$repo/.out}"
out=$KILN_OUT/preview
mkdir -p "$out" "$KILN_OUT/ui-preview"
font="" scene="" size="" scale=1 gamefont=1
while (($#)); do
  case $1 in
    --font) font=$2; shift 2 ;;
    --no-game-font) gamefont=0; shift ;;
    --scene) scene=$2; shift 2 ;;
    --size) size=$2; shift 2 ;;
    --scale) scale=$2; shift 2 ;;
    *) echo "unknown argument $1" >&2; exit 2 ;;
  esac
done
if [[ -n $size ]]; then sizes=("$size $scale"); else sizes=("1920x1080 1" "2560x1440 1" "1920x1080 1.5" "3440x1440 1" "1920x1200 1"); fi

# the shim: Dear ImGui + kiln.ui's drawing slots and looks (the game's code) + the rasteriser
imgui=$repo/native/third_party/imgui
b=$KILN_OUT/ui-preview
for f in imgui imgui_draw imgui_tables imgui_widgets; do
  [[ $b/$f.o -nt $imgui/$f.cpp ]] || g++ -std=c++20 -O2 -fPIC -w -I"$imgui" -c "$imgui/$f.cpp" -o "$b/$f.o" &
done
wait
inc=(-I"$repo/native/services/ui/tests/stubs" -I"$repo/native/services/ui" -I"$repo/native/core/abi" -I"$imgui" -I"$repo/native/third_party")
g++ -std=c++20 -O2 -fPIC -Wall -Wextra -Wno-unused-parameter -Wno-unused-function -c "$here/shim.cpp" "${inc[@]}" -o "$b/shim.o"
g++ -std=c++20 -O2 -fPIC -w -c "$repo/native/services/ui/style.cpp" "${inc[@]}" -o "$b/style.o"
g++ -shared -pthread "$b/shim.o" "$b/style.o" "$b"/imgui*.o -o "$b/libkiln_ui_preview.so"

# the game's UI font, read from the local game at run time (never copied into the tree)
if [[ -z $font && $gamefont == 1 ]]; then
  font=$(python3 -I "$here/game_font.py" "$KILN_OUT/ui-preview/game-ui-font.ttf" 2>/dev/null || true)
  [[ -n $font ]] || echo "game font not found: the preview uses a system font" >&2
fi

dotnet build "$here/Kiln.Ui.Preview/Kiln.Ui.Preview.csproj" --artifacts-path "$KILN_OUT/dotnet" -v q -nologo | grep -E "error|Error" || true
app=$KILN_OUT/dotnet/bin/Kiln.Ui.Preview/debug/Kiln.Ui.Preview.dll
for s in "${sizes[@]}"; do
  read -r size scale <<<"$s"
  args=(--shim "$b/libkiln_ui_preview.so" --out "$out" --size "$size" --scale "$scale")
  [[ -n $font ]] && args+=(--font "$font")
  bold=${font%.ttf}-bold.ttf  # game_font.py writes the game's bold face next to the regular one
  [[ -n $font && -f $bold ]] && args+=(--bold-font "$bold")
  [[ -n $scene ]] && args+=(--scene "$scene")
  dotnet "$app" "${args[@]}"
done
