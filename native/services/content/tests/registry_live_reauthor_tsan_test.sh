#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Registry.Reauthor's suite under ThreadSanitizer (review of LOOT-2): the preparation, a pass's reads and the report builder run
# off the world thread (a_preparation_races_the_world_thread races them against the world thread's commits, reverts and
# landings; the store's lock and the landed turn's garbage freed on its own thread). Any data race fails it. No game process.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:?set KILN_OUT}"
out=$KILN_OUT/content-tests
mkdir -p "$out"
. "$here/regmap_fixture.sh"
regmap=$(regmap_fixture "$out")
echo '== registry-live-reauthor-tsan (host, ThreadSanitizer)'
g++ -std=c++20 -O1 -g -Wall -Wextra -Wno-unused-function -Wno-missing-field-initializers -D__fastcall= -ffunction-sections -fdata-sections \
    -fsanitize=thread -pthread -I"$here/host" -I"$here/../../../core/abi" -I"$here/../../../third_party" -DKILN_REGMAP_DIR="\"$regmap\"" \
    "$here/registry_live_reauthor_test.cpp" "$here/../refl.cpp" "$here/../gear.cpp" "$here/../registry/ir.cpp" "$here/../registry/compose.cpp" "$here/../registry/saves.cpp" \
    "$here/../registry/formulas.cpp" "$here/../registry/regmap.cpp" "$here/../registry/report.cpp" -Wl,--gc-sections -o "$out/registry-live-reauthor-tsan"
TSAN_OPTIONS="halt_on_error=1 exitcode=66" "$out/registry-live-reauthor-tsan"
