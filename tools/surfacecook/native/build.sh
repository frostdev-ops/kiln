#!/usr/bin/env bash
# Builds kiln-surfacecook.
#   build.sh            this machine -> $KILN_OUT/surfacecook/bin/kiln-surfacecook
#   build.sh --windows  Windows x64 with clang-cl + the xwin SDK (as native/build.sh) -> $KILN_OUT/surfacecook/win/bin/kiln-surfacecook.exe
# KILN_OUT picks the build folder; the default is native/build, like native/build.sh.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../.." && pwd)
out=${KILN_OUT:-$root/native/build}
[[ $out = /* ]] || out=$root/$out
out=$out/surfacecook
if [[ ${1:-} == --windows ]]; then
  cmake -S "$here" -B "$out/win" -G Ninja -DCMAKE_TOOLCHAIN_FILE="$root/native/cmake/clang-cl-xwin.cmake" \
    -DCMAKE_BUILD_TYPE=Release -Wno-dev >/dev/null
  cmake --build "$out/win" >&2
  echo "$out/win/bin/kiln-surfacecook.exe"
  exit 0
fi
cmake -S "$here" -B "$out" -G Ninja -DCMAKE_BUILD_TYPE=Release -Wno-dev >/dev/null
cmake --build "$out" >&2
echo "$out/bin/kiln-surfacecook"
