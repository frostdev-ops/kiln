#!/usr/bin/env bash
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../build}/time-guards
mkdir -p "$out"
cxx=${CXX:-$(command -v clang++ || command -v g++)}
"$cxx" -std=c++20 -O2 -Wall -Wextra -Wno-unused-function -pthread -I"$here/../../core/abi" -o "$out/time_test" "$here/time_test.cpp"
"$out/time_test"
