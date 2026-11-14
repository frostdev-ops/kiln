#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Host tests of the capability registry (services/caps.cpp, kiln.caps) and of sigdb::feature_problem, with ASan/UBSan:
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
# the client gate's list, as the core's build generates it (a bad native/core/gates/*.json fails here too)
python3 -I "$here/../gates/gen.py" "$here/../gates" "$out/kiln_gates.g.h"
"$cxx" "${flags[@]}" -I"$out" -I"$here/../src" -o "$out/caps_test" "$here/caps_test.cpp"
# a subscriber that deadlocks the registry hangs the test: the time limit turns that into a failure
timeout 120 "$out/caps_test" "$out"
