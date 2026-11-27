#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# A data pack's "gearRules" as a registry front-end (registry/boot_bridge.cpp, registry/stage.cpp, gear.cpp): generated gearRules
# written into a balancing table both ways, as their resource patch and by the registry's boot back-end, the same bytes; the
# report and the composition around a front-end. On the host, with ASan/UBSan, over the real regmap (every row of the build
# records ok: the back-end, not the proof).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:?set KILN_OUT to the build directory}"
out=$KILN_OUT/content-tests
mkdir -p "$out"
. "$here/regmap_fixture.sh"
regmap=$(regmap_fixture "$out")
echo "== gear-frontend-test (host)"
r=$here/../registry
g++ -std=c++20 -O1 -g -Wall -Wextra -Wno-unused-function -Wno-missing-field-initializers -D__fastcall= -ffunction-sections -fdata-sections \
    -fsanitize=address,undefined -I"$here/host" -I"$here/../../../core/abi" -I"$here/../../../third_party" \
    "$here/gear_frontend_test.cpp" "$here/registry_model_fakes.cpp" "$r/stage.cpp" "$r/archive_journal.cpp" "$r/boot_bridge.cpp" "$r/model.cpp" "$r/regmap.cpp" \
    "$r/ir.cpp" "$r/compose.cpp" "$r/saves.cpp" "$r/report.cpp" "$r/formulas.cpp" "$here/../gear.cpp" -Wl,--gc-sections -o "$out/gear-frontend-test"
"$out/gear-frontend-test" "$regmap"
