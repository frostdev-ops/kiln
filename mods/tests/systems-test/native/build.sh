#!/usr/bin/env bash
# Builds systems-test's native part for a lab server: -> $KILN_OUT/systems-test-native/bin/kiln-systems-test.dll (the
# path is printed). Copy it to <lab>/kiln/plugins/ and create <lab>/kiln/control/systems.parallel before the start.
# KILN_OUT picks the build folder; the default is native/build, like native/build.sh.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../../.." && pwd)
out=${KILN_OUT:-$root/native/build}
[[ $out = /* ]] || out=$root/$out
out=$out/systems-test-native
cmake -S "$here" -B "$out" -G Ninja -DCMAKE_TOOLCHAIN_FILE="$root/native/cmake/clang-cl-xwin.cmake" \
  -DCMAKE_BUILD_TYPE=Release -Wno-dev >/dev/null
cmake --build "$out" >&2
echo "$out/bin/kiln-systems-test.dll"
