#!/usr/bin/env bash
# Cross-builds Kiln's native Windows DLLs on Linux (clang-cl + lld-link + the xwin SDK) via CMake + Ninja.
#   native/build.sh [target...]      default: all.  targets: kiln-proxy kiln-core kiln-native-hello kiln-tickprobe
# Output: $KILN_OUT/cmake/bin/{dinput8,kiln-core,kiln-native-hello,kiln-tickprobe}.dll (KILN_OUT default native/build).
# Parallel workers: give each its own KILN_OUT (ninja must not share a build dir).
set -euo pipefail
cd "$(dirname "$0")"
KILN_OUT=${KILN_OUT:-$PWD/build}
B=$KILN_OUT/cmake
if [[ ! -f $B/build.ninja ]]; then
  cmake -S . -B "$B" -G Ninja -DCMAKE_TOOLCHAIN_FILE=cmake/clang-cl-xwin.cmake -DCMAKE_BUILD_TYPE=Release \
    -Wno-author -Wno-deprecated >/dev/null
fi
if (($#)) && [[ $1 != all ]]; then cmake --build "$B" --target "$@"; else cmake --build "$B"; fi
