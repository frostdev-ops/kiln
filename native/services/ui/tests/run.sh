#!/usr/bin/env bash
# Builds and runs kiln.render's host test (the projection, render_math.h): no game, no GPU, no Wine.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:=$here/../../../build}"
out=$KILN_OUT/ui-host-test
mkdir -p "$out"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/render_math_test.cpp" -o "$out/render_math_test"
"$out/render_math_test"
