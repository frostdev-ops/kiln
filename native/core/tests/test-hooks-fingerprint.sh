#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Host tests of the ABI fingerprint pass and the `sig` descriptor (services/hooks_fingerprint.cpp), with ASan/UBSan.
# The pass is decoded with the vendored Zydis, built here for the host:
#   native/core/tests/test-hooks-fingerprint.sh
# Build output: $KILN_OUT/core-tests (KILN_OUT default native/build).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../build}/core-tests
mkdir -p "$out"
cxx=${CXX:-$(command -v clang++ || command -v g++)}
cc=${CC:-$(command -v clang || command -v gcc)}
zy=$here/../../third_party/safetyhook
defs=(-DZYDIS_STATIC_BUILD -DZYCORE_STATIC_BUILD)
[[ $out/zydis-host.o -nt $zy/Zydis.c ]] || "$cc" -O1 -w "${defs[@]}" -I"$zy" -c -o "$out/zydis-host.o" "$zy/Zydis.c"
"$cxx" -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -fno-sanitize-recover=undefined "${defs[@]}" \
    -I"$here/../abi" -I"$here/../src" -I"$here/../../third_party" -I"$zy" \
    -o "$out/hooks_fingerprint_test" "$here/hooks_fingerprint_test.cpp" "$here/../src/services/hooks_fingerprint.cpp" \
    "$out/zydis-host.o"
"$out/hooks_fingerprint_test"
