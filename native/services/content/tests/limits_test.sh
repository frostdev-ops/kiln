#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln.content registry stage: the limits registry's back-end (registry/limits.cpp, dev.32) on the host, with ASan/UBSan.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:?set KILN_OUT to the build directory}"
out=$KILN_OUT/content-tests
mkdir -p "$out"
echo "== limits-test (host)"
g++ -std=c++20 -O1 -g -Wall -Wextra -Wno-unused-function -Wno-missing-field-initializers -D__fastcall= -fsanitize=address,undefined -fno-sanitize-recover=all \
    -I"$here/host" -I"$here/../../../core/abi" -I"$here/../../../third_party" "$here/limits_test.cpp" "$here/../registry/limits.cpp" \
    "$here/../registry/ir.cpp" "$here/../registry/compose.cpp" -o "$out/limits-test"
"$out/limits-test"
