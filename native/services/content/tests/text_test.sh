#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln.content: the game's own texts (text_test.cpp: newcontent.cpp's loca hook, text() and text_info() over a fake game
# lookup, LocaPool, kiln.hooks, kiln.caps and kiln.ui), on the host.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:?set KILN_OUT to the build directory}"
out=$KILN_OUT/content-tests
mkdir -p "$out"
echo "== text-test (host)"
g++ -std=c++20 -O1 -g -Wall -Wextra -Wno-unused-function -Wno-unused-parameter -Wno-unused-variable \
    -Wno-unused-but-set-variable -Wno-type-limits -D__fastcall= -ffunction-sections \
    -fdata-sections -fsanitize=address,undefined -fno-sanitize-recover=undefined -I"$here/host" -I"$here/.." \
    -I"$here/../../../core/abi" -I"$here/../../../third_party" "$here/text_test.cpp" -Wl,--gc-sections \
    -o "$out/text-test"
ASAN_OPTIONS=detect_leaks=0 "$out/text-test"
