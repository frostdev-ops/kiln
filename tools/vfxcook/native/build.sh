#!/usr/bin/env bash
# Builds kiln-vfxcook.
#   build.sh            this machine -> $KILN_OUT/vfxcook/bin/kiln-vfxcook
#   build.sh --windows  Windows x64 with clang-cl + the xwin SDK (as native/build.sh) -> $KILN_OUT/vfxcook/win/bin/kiln-vfxcook.exe
# KILN_OUT picks the build folder; the default is native/build, like native/build.sh.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../.." && pwd)
out=${KILN_OUT:-$root/native/build}
[[ $out = /* ]] || out=$root/$out
out=$out/vfxcook
if [[ ${1:-} == --windows ]]; then
  cmake -S "$here" -B "$out/win" -G Ninja -DCMAKE_TOOLCHAIN_FILE="$root/native/cmake/clang-cl-xwin.cmake" \
    -DCMAKE_BUILD_TYPE=Release -Wno-dev >/dev/null
  cmake --build "$out/win" >&2
  echo "$out/win/bin/kiln-vfxcook.exe"
  exit 0
fi
cmake -S "$here" -B "$out" -G Ninja -DCMAKE_BUILD_TYPE=Release -Wno-dev >/dev/null
cmake --build "$out" >&2
echo "$out/bin/kiln-vfxcook"
