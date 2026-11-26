#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# dev.32 creatures, arsenals and perks over the live back-end (gamedata-section binders), ASan/UBSan. No game process.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:?set KILN_OUT}"
out=$KILN_OUT/content-tests
mkdir -p "$out"
. "$here/regmap_fixture.sh"
regmap=$(regmap_fixture "$out")  # every row of the build records ok: the back-end, not the proof
echo '== registry-gd-live-test (host)'
g++ -std=c++20 -O1 -g -Wall -Wextra -Wno-unused-function -Wno-missing-field-initializers -D__fastcall= -ffunction-sections -fdata-sections \
    -fsanitize=address,undefined -fno-sanitize-recover=all -I"$here/host" -I"$here/../../../core/abi" -I"$here/../../../third_party" \
    -DKILN_REGMAP_DIR="\"$regmap\"" \
    "$here/registry_gd_live_test.cpp" "$here/../refl.cpp" "$here/../gear.cpp" "$here/../registry/ir.cpp" "$here/../registry/compose.cpp" "$here/../registry/saves.cpp" \
    "$here/../registry/formulas.cpp" "$here/../registry/regmap.cpp" "$here/../registry/report.cpp" -Wl,--gc-sections -o "$out/registry-gd-live-test"
ASAN_OPTIONS=detect_leaks=0 "$out/registry-gd-live-test"
