#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Renders the Kiln mod manager to PNGs without the game (tools/ui-preview/README.md).
#   tools/ui-preview/preview.sh [--font TTF] [--no-game-font] [--scene NAME] [--size WxH --scale S]
#                               [--language TAG --locales DIR]  (Kiln's texts in that language, e.g. zh-CN with a stub
#                               translation in DIR; a Chinese, Japanese or Korean one reads the game's own font of that
#                               language group with game_fonts.py, outside the tree)
# (--no-game-font also leaves the game's colours out: the game look is then its neutral stand-in)
# Output: $KILN_OUT/preview/*.png (KILN_OUT default: <repo>/.out). Builds the shim and the preview app first.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
: "${KILN_OUT:=$repo/.out}"
out=$KILN_OUT/preview
mkdir -p "$out" "$KILN_OUT/ui-preview"
font="" scene="" size="" scale=1 gamefont=1 checkz=0 checktext=0 language="" locales=""
while (($#)); do
  case $1 in
    --font) font=$2; shift 2 ;;
    --no-game-font) gamefont=0; shift ;;
    --scene) scene=$2; shift 2 ;;
    --size) size=$2; shift 2 ;;
    --scale) scale=$2; shift 2 ;;
    --check-z) checkz=1; shift ;;
    --check-text) checktext=1; shift ;;
    --language) language=$2; shift 2 ;;
    --locales) locales=$2; shift 2 ;;
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
# the game's UI colours (its UI bundle and the layout of the fields kiln.ui reads), the same way; else the neutral stand-in
colors=()
if [[ $gamefont == 1 ]]; then
  mapfile -t colors < <(python3 -I "$here/game_colors.py" "$KILN_OUT/ui-preview/game-ui-bundle" 2>/dev/null || true)
  ((${#colors[@]} == 2)) || { colors=(); echo "game colours not found: the game look is its neutral stand-in" >&2; }
fi

# a Chinese, Japanese or Korean language: the game's font resource and the files of its group (and the default group's)
fontres=()
case ${language,,} in
  zh-tw|zh-hk|zh-mo|zh-hant*) lid=21 ;;
  zh*) lid=20 ;;
  ja*) lid=11 ;;
  ko*) lid=13 ;;
  *) lid=0 ;;
esac
if [[ $lid != 0 && $gamefont == 1 ]]; then
  res=$(python3 -I "$here/game_fonts.py" "$KILN_OUT/ui-preview/game-fonts" --language "$lid" 2>/dev/null || true)
  if [[ -n $res ]]; then fontres=(--font-resource "$res" --font-blobs "$KILN_OUT/ui-preview/game-fonts"); font=""
  else echo "the game's fonts were not found: Kiln's texts stay English in the preview" >&2; fi
fi

dotnet build "$here/Kiln.Ui.Preview/Kiln.Ui.Preview.csproj" --artifacts-path "$KILN_OUT/dotnet" -v q -nologo | grep -E "error|Error" || true
app=$KILN_OUT/dotnet/bin/Kiln.Ui.Preview/debug/Kiln.Ui.Preview.dll
if ((checkz)); then exec dotnet "$app" --shim "$b/libkiln_ui_preview.so" --out "$out" --size 1920x1080 --check-z; fi
if ((checktext)); then exec dotnet "$app" --shim "$b/libkiln_ui_preview.so" --out "$out" --size 1920x1080 --check-text; fi
for s in "${sizes[@]}"; do
  read -r size scale <<<"$s"
  args=(--shim "$b/libkiln_ui_preview.so" --out "$out" --size "$size" --scale "$scale")
  [[ -n $font ]] && args+=(--font "$font")
  bold=${font%.ttf}-bold.ttf  # game_font.py writes the game's bold face next to the regular one
  [[ -n $font && -f $bold ]] && args+=(--bold-font "$bold")
  ((${#colors[@]} == 2)) && args+=(--game-colors "${colors[0]}" --game-layout "${colors[1]}")
  [[ -n $scene ]] && args+=(--scene "$scene")
  [[ -n $language ]] && args+=(--language "$language")
  [[ -n $locales ]] && args+=(--locales "$locales")
  ((${#fontres[@]})) && args+=("${fontres[@]}")
  dotnet "$app" "${args[@]}"
done
