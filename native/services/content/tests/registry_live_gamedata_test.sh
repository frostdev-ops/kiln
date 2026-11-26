#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# dev.32: the registry's live back-end over kiln.gamedata's entries (buffs, abilities) through RegistryLive's gd_* hooks, over the
# real store, IR and composition and the repository's regmap rows (every build row ok), ASan/UBSan. No game process.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:?set KILN_OUT}"
out=$KILN_OUT/content-tests
mkdir -p "$out"
. "$here/regmap_fixture.sh"
regmap=$(regmap_fixture "$out")  # every row ok: the back-end, not the proof
echo '== registry-live-gamedata-test (host)'
g++ -std=c++20 -O1 -g -Wall -Wextra -Wno-unused-function -Wno-missing-field-initializers -D__fastcall= -ffunction-sections -fdata-sections \
    -fsanitize=address,undefined -fno-sanitize-recover=all -I"$here/host" -I"$here/../../../core/abi" -I"$here/../../../third_party" \
    -DKILN_REGMAP_DIR="\"$regmap\"" \
    "$here/registry_live_gamedata_test.cpp" "$here/../refl.cpp" "$here/../gear.cpp" "$here/../registry/ir.cpp" "$here/../registry/compose.cpp" "$here/../registry/saves.cpp" \
    "$here/../registry/formulas.cpp" "$here/../registry/regmap.cpp" "$here/../registry/report.cpp" "$here/../registry/live_table.cpp" \
    -Wl,--gc-sections -o "$out/registry-live-gamedata-test"
ASAN_OPTIONS=detect_leaks=0 "$out/registry-live-gamedata-test"
