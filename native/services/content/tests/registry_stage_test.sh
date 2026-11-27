#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln.content registry stage: the stage runner (registry/stage.cpp) and the boot back-end's bridge (registry/boot_bridge.cpp)
# over a fake tree view and an in-memory data folder, on the host, with ASan/UBSan.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:?set KILN_OUT to the build directory}"
out=$KILN_OUT/content-tests
mkdir -p "$out"
. "$here/regmap_fixture.sh"
regmap=$(regmap_fixture "$out")  # every row of the build records ok: the back-end, not the proof
echo "== registry-stage-test (host)"
r=$here/../registry
g++ -std=c++20 -O1 -g -Wall -Wextra -Wno-unused-function -Wno-missing-field-initializers -D__fastcall= -ffunction-sections -fdata-sections \
    -fsanitize=address,undefined -I"$here/host" -I"$here/../../../core/abi" -I"$here/../../../third_party" \
    "$here/registry_stage_test.cpp" "$here/registry_model_fakes.cpp" "$r/stage.cpp" "$r/archive_journal.cpp" "$r/replay.cpp" "$r/boot_bridge.cpp" "$r/model.cpp" "$r/regmap.cpp" \
    "$r/ir.cpp" "$r/compose.cpp" "$r/saves.cpp" "$r/report.cpp" "$r/formulas.cpp" "$here/../gear.cpp" -Wl,--gc-sections -o "$out/registry-stage-test"
"$out/registry-stage-test" "$regmap"
