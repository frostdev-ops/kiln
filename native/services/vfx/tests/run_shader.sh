#!/usr/bin/env bash
# Runs the cook policy tests using the portable cooker's objects. All output stays under KILN_OUT (default /tmp).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../../.." && pwd)
export KILN_OUT=${KILN_OUT:-/tmp/kiln-vfx-shader-test}
"$root/tools/vfxcook/native/build.sh" >/dev/null
mapfile -t objects < <(rg --files --no-ignore "$KILN_OUT/vfxcook/CMakeFiles" -g '*.o' -g '!main.cpp.o')
out=$KILN_OUT/vfx-shader-test
mkdir -p "$out"
"${CXX:-c++}" -std=c++23 -O1 -Wall -Wextra -pthread -I"$root/native/third_party" \
    "$here/test_shader.cpp" "${objects[@]}" -o "$out/test_shader"
"$out/test_shader" "$out" "$root"
