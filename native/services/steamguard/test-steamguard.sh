#!/usr/bin/env bash
# kiln.steamguard's logic on the host: boot verification, the detour's drop/pass, the latch and holds, the refusal
# paths. No game, no Wine.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../build}/steamguard-tests
mkdir -p "$out"
cxx=${CXX:-$(command -v clang++ || command -v g++)}
"$cxx" -std=c++20 -O2 -Wall -Wextra -Wno-unused-function -pthread -I"$here/../../core/abi" -I"$here" -o "$out/steamguard_test" "$here/steamguard_test.cpp"
"$out/steamguard_test"
