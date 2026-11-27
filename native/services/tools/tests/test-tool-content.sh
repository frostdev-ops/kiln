#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Run through the build limiter. ToolCompiler's emitted content (ToolCompilerContentTests with KILN_TOOL_CONTENT_FIXTURE)
# through kiln.content's resolvers with the game client's real reflection and data. The game never runs: its exe and
# archive are only read as files, and what is read from them stays in OUTPUT_DIRECTORY, never this tree.
# It needs two inputs this repository does not hold; without either it prints SKIP and exits 0:
#   - the compiler's export: KILN_TOOL_CONTENT_FIXTURE=<file> dotnet test managed/tests/Kiln.Blocks.Tests
#     --filter ToolCompilerContentTests writes it (pass the file, or leave KILN_TOOL_CONTENT_FIXTURE set);
#   - an installed game: KILN_GAME (default ~/.local/share/Steam/steamapps/common/Enshrouded); KILN_CLIENT_EXE names
#     another copy of its client exe.
#   test-tool-content.sh OUTPUT_DIRECTORY [COMPILER_JSON]
# Inputs extracted before (python3 -I extract_tool_content_inputs.py <repo>/tools/kfc <game folder> <client exe> <scratch>):
#   test-tool-content.sh OUTPUT_DIRECTORY COMPILER_JSON TYPE_DB_JSON BRUSH_BIN POOL_ITEM_BIN REGISTRY_BIN SHAPE_MEDIUM_BIN SHAPE_BIG_BIN
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${1:?usage: test-tool-content.sh OUTPUT_DIRECTORY [COMPILER_JSON [TYPE_DB_JSON BRUSH_BIN POOL_ITEM_BIN REGISTRY_BIN SHAPE_MEDIUM_BIN SHAPE_BIG_BIN]]}
skip() { printf 'SKIP tool content: %s\n' "$1"; exit 0; }
if (($# >= 8)); then
    compiler=$2; types=$3; brush=$4; pool=$5; registry=$6; medium=$7; big=$8
    for input in "$compiler" "$types" "$brush" "$pool" "$registry" "$medium" "$big"; do
        [[ -f $input ]] || { echo "tool content: FAIL: no input $input" >&2; exit 1; }
    done
elif (($# == 1 || $# == 2)); then
    compiler=${2:-${KILN_TOOL_CONTENT_FIXTURE-}}
    [[ -n $compiler && -f $compiler ]] ||
        skip "no compiler export (run ToolCompilerContentTests with KILN_TOOL_CONTENT_FIXTURE=<file>, then pass that file)"
    game=${KILN_GAME-$HOME/.local/share/Steam/steamapps/common/Enshrouded}
    exe=${KILN_CLIENT_EXE-$game/enshrouded.exe}
    [[ -d $game && -f $exe ]] || skip "no installed game at $game (set KILN_GAME, and KILN_CLIENT_EXE for another client exe)"
    mkdir -p "$out/inputs"
    python3 -I "$here/extract_tool_content_inputs.py" "$here/../../../../tools/kfc" "$game" "$exe" "$out/inputs"
    types=$out/inputs/types.json; brush=$out/inputs/brush.bin; pool=$out/inputs/pool-item.bin; registry=$out/inputs/registry.bin
    medium=$out/inputs/shape-medium.bin; big=$out/inputs/shape-big.bin
else
    echo "usage: test-tool-content.sh OUTPUT_DIRECTORY [COMPILER_JSON [TYPE_DB_JSON BRUSH_BIN POOL_ITEM_BIN REGISTRY_BIN SHAPE_MEDIUM_BIN SHAPE_BIG_BIN]]" >&2
    exit 2
fi
mkdir -p "$out"
export TMPDIR="$out"
python3 -I "$here/make_tool_content_fixture.py" "$types" "$out/content-types.json"
"${CXX:-c++}" -std=c++20 -O1 -g -D__fastcall= -ffunction-sections -fdata-sections -fsanitize=address,undefined \
    -I"$here/../../content/tests/host" -I"$here/../../../core/abi" -I"$here/../../../third_party" \
    "$here/tool_content_test.cpp" "$here/../../content/refl.cpp" "$here/../../content/gamedata_walk.cpp" \
    -Wl,--gc-sections -o "$out/tool-content-test"
"$out/tool-content-test" "$compiler" "$out/content-types.json" "$brush" "$pool" "$registry" "$medium" "$big"
