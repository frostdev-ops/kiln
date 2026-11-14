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
# sigdb checks `sig` descriptors against the function's code (services/hooks_fingerprint.cpp, decoded with Zydis)
cc=${CC:-$(command -v clang || command -v gcc)}
zy=$here/../../third_party/safetyhook
[[ $out/zydis-host.o -nt $zy/Zydis.c ]] || "$cc" -O1 -w -DZYDIS_STATIC_BUILD -DZYCORE_STATIC_BUILD -I"$zy" -c -o "$out/zydis-host.o" "$zy/Zydis.c"
flags+=(-DZYDIS_STATIC_BUILD -DZYCORE_STATIC_BUILD -I"$here/../src" -I"$zy")
fp=("$here/../src/services/hooks_fingerprint.cpp" "$out/zydis-host.o")
"$cxx" "${flags[@]}" -o "$out/sigdb_problem_test" "$here/sigdb_problem_test.cpp" "${fp[@]}"
"$out/sigdb_problem_test" "$out"
# the client gate's list, as the core's build generates it (a bad native/core/gates/*.json fails here too)
python3 -I "$here/../gates/gen.py" "$here/../gates" "$out/kiln_gates.g.h"
"$cxx" "${flags[@]}" -I"$out" -o "$out/caps_test" "$here/caps_test.cpp" "${fp[@]}"
# a subscriber that deadlocks the registry hangs the test: the time limit turns that into a failure
timeout 120 "$out/caps_test" "$out"
