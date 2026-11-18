#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Host test of tick-stats.jsonl's line and the parts of Pre and Post it reports (src/tick_stats.h, since 0.2.0-dev.29) and
# of tick.cpp's use of them, with ASan/UBSan:
#   native/core/tests/test-tick-stats.sh
# Build output: $KILN_OUT/core-tests (KILN_OUT default native/build).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../build}/core-tests
mkdir -p "$out"
cxx=${CXX:-$(command -v clang++ || command -v g++)}
"$cxx" -std=c++20 -O1 -g -Wall -Wextra -pthread -fsanitize=address,undefined -fno-sanitize-recover=undefined \
    -I"$here/../abi" -I"$here/../src" -o "$out/tick_stats_test" "$here/tick_stats_test.cpp" "$here/../src/tasks_stats.cpp"
"$out/tick_stats_test" "$here/../src/tick.cpp"
