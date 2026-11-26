#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln.content registry stage: the lab's registry proof producer, native half (registry/proof.cpp), on the host with
# ASan/UBSan: nothing without the request file; regmapDigest; probes; a boot-generation sample on bytes; both files written.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:?set KILN_OUT to the build directory}"
out=$KILN_OUT/content-tests
mkdir -p "$out"
echo "== registry-proof-test (host)"
r=$here/../registry
g++ -std=c++20 -O1 -g -Wall -Wextra -Wno-unused-function -Wno-missing-field-initializers -D__fastcall= -ffunction-sections -fdata-sections \
    -fsanitize=address,undefined -I"$here/host" -I"$here/../../../core/abi" -I"$here/../../../third_party" \
    "$here/registry_proof_test.cpp" "$here/registry_model_fakes.cpp" "$r/proof.cpp" "$r/limits.cpp" "$r/boot_bridge.cpp" "$r/model.cpp" "$r/regmap.cpp" \
    "$r/ir.cpp" "$r/compose.cpp" "$r/formulas.cpp" "$here/../gear.cpp" -Wl,--gc-sections -o "$out/registry-proof-test"
"$out/registry-proof-test"
