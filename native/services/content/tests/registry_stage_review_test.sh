#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln.content registry stage: the registry line review's findings on the stage runner (registry_stage_review_test.cpp), on the host.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:?set KILN_OUT to the build directory}"
out=$KILN_OUT/content-tests
mkdir -p "$out"
echo "== registry-stage-review-test (host)"
r=$here/../registry
g++ -std=c++20 -pthread -O1 -g -Wall -Wextra -Wno-unused-function -Wno-missing-field-initializers -D__fastcall= -ffunction-sections -fdata-sections \
    -fsanitize=address,undefined -I"$here/host" -I"$here/../../../core/abi" -I"$here/../../../third_party" \
    "$here/registry_stage_review_test.cpp" "$here/registry_model_fakes.cpp" "$r/stage.cpp" "$r/archive_journal.cpp" "$r/replay.cpp" "$r/boot_bridge.cpp" "$r/model.cpp" "$r/regmap.cpp" \
    "$r/ir.cpp" "$r/compose.cpp" "$r/saves.cpp" "$r/report.cpp" "$r/formulas.cpp" "$here/../gear.cpp" -Wl,--gc-sections -o "$out/registry-stage-review-test"
"$out/registry-stage-review-test" "$here/../../../../regmap"
