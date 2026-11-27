#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln.settings: the difficulty bounds of Kiln's registry (bounds.cpp, dev.32) on the host, with ASan/UBSan.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:?set KILN_OUT to the build directory}"
out=$KILN_OUT/settings-tests
mkdir -p "$out"
echo "== settings-bounds-test (host)"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -fno-sanitize-recover=all \
    -I"$here/../../../core/abi" -I"$here/../../../third_party" "$here/bounds_test.cpp" "$here/../bounds.cpp" -o "$out/settings-bounds-test"
"$out/settings-bounds-test"
