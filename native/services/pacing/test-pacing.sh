#!/usr/bin/env bash
# kiln.pacing's logic on the host: arming, hook register contracts, caps, refusals. No game, no Wine.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../build}/pacing-tests
mkdir -p "$out"
cxx=${CXX:-$(command -v clang++ || command -v g++)}
"$cxx" -std=c++20 -O2 -Wall -Wextra -Wno-unused-function -pthread -I"$here/../../core/abi" -I"$here" -o "$out/pacing_test" "$here/pacing_test.cpp"
"$out/pacing_test"
