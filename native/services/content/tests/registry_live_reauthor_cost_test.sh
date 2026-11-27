#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Registry.Reauthor's tick budget (dev.32 LOOT-2): a full randomLoot re-author (797 tables x 16 fields = 12,752 edits) lands on
# the world thread within 16 ms, prepared off it. Built at -O2 without sanitizers, as the release is (the sanitizer build of
# registry_live_reauthor_test.sh only checks the cost stays bounded). No game process.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:?set KILN_OUT}"
out=$KILN_OUT/content-tests
mkdir -p "$out"
. "$here/regmap_fixture.sh"
regmap=$(regmap_fixture "$out")
echo '== registry-live-reauthor-cost-test (host, -O2)'
g++ -std=c++20 -O2 -g -Wall -Wextra -Wno-unused-function -Wno-missing-field-initializers -D__fastcall= -ffunction-sections -fdata-sections \
    -pthread -I"$here/host" -I"$here/../../../core/abi" -I"$here/../../../third_party" -DKILN_REGMAP_DIR="\"$regmap\"" -DKILN_REAUTHOR_BUDGET_MS=16 \
    "$here/registry_live_reauthor_test.cpp" "$here/../refl.cpp" "$here/../gear.cpp" "$here/../registry/ir.cpp" "$here/../registry/compose.cpp" "$here/../registry/saves.cpp" \
    "$here/../registry/formulas.cpp" "$here/../registry/regmap.cpp" "$here/../registry/report.cpp" -Wl,--gc-sections -o "$out/registry-live-reauthor-cost-test"
KILN_TEST_ONLY=a_full_reauthor_lands_within_the_tick_budget "$out/registry-live-reauthor-cost-test"
