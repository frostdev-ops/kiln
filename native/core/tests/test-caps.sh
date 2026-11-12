#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Host tests of sigdb::feature_problem (and, from the next commit, the capability registry), with ASan/UBSan:
#   native/core/tests/test-caps.sh
# Build output: $KILN_OUT/core-tests (KILN_OUT default native/build).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../build}/core-tests
mkdir -p "$out"
cxx=${CXX:-$(command -v clang++ || command -v g++)}
flags=(-std=c++20 -O1 -g -Wall -Wextra -pthread -fsanitize=address,undefined -fno-sanitize-recover=undefined
       -I"$here/stubs" -I"$here/../abi" -I"$here/../../third_party")
"$cxx" "${flags[@]}" -o "$out/sigdb_problem_test" "$here/sigdb_problem_test.cpp"
"$out/sigdb_problem_test" "$out"
