#!/usr/bin/env bash
# Host checks of rule publication, ABI compatibility, Shroud floors and client ray guards.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../build}/rules-guards
mkdir -p "$out"
cxx=${CXX:-$(command -v clang++ || command -v g++)}
"$cxx" -std=c++20 -O2 -Wall -Wextra -Wno-unused-function -pthread -I"$here/../../core/abi" -o "$out/rules_test" "$here/rules_test.cpp"
"$out/rules_test"
"$cxx" -std=c++20 -O2 -Wall -Wextra -o "$out/creative_math_test" "$here/creative_math_test.cpp"
"$out/creative_math_test"
