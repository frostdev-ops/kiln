#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Host test of the owner-retiring observers (src/registry.cpp), with ASan/UBSan:
#   native/core/tests/test-owner-retiring.sh
# Build output: $KILN_OUT/core-tests (KILN_OUT default native/build).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../build}/core-tests
mkdir -p "$out"
cxx=${CXX:-$(command -v clang++ || command -v g++)}
"$cxx" -std=c++20 -O1 -g -Wall -Wextra -pthread -fsanitize=address,undefined -fno-sanitize-recover=undefined \
    -I"$here/stubs" -I"$here/../abi" -I"$here/../../third_party" \
    -o "$out/owner_retiring_test" "$here/owner_retiring_test.cpp"
"$out/owner_retiring_test"
