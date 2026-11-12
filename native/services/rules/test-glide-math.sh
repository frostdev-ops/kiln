#!/usr/bin/env bash
# Builds and runs the host tests of the glider's math (glide_math.h, glide_steer_math.h): plain C++, no game, no Windows.
#   native/services/rules/test-glide-math.sh        output: one PASS/FAIL line per check; exit 1 on a failure
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../build}/glide-math
mkdir -p "$out"
cxx=${CXX:-$(command -v clang++ || command -v g++)}
"$cxx" -std=c++20 -O2 -Wall -Wextra -o "$out/glide_math_test" "$here/glide_math_test.cpp"
"$out/glide_math_test"
"$cxx" -std=c++20 -O2 -Wall -Wextra -o "$out/glide_steer_test" "$here/glide_steer_test.cpp"
"$out/glide_steer_test"
