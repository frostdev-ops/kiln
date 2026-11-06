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
# The build stamp kiln.log's first line carries, as the runtime bundle names it: +<yyyymmdd>.<git> of the commit (UTC;
# SOURCE_DATE_EPOCH and KILN_GIT override it, as in tools/package/package.sh). Written only when it changes: only
# core/src/boot.cpp includes it.
repo=$(cd .. && pwd)
epoch=${SOURCE_DATE_EPOCH:-$(git -C "$repo" log -1 --format=%ct 2>/dev/null || true)}
git=${KILN_GIT:-$(git -C "$repo" rev-parse --short=7 HEAD 2>/dev/null || true)}
stamp=""
if [[ -n $epoch && -n $git ]]; then stamp="+$(date -u -d "@$epoch" +%Y%m%d).$git"; fi
mkdir -p "$B/generated"
line="#define KILN_BUILD \"$stamp\""
if [[ ! -f $B/generated/kiln_build.h || $(<"$B/generated/kiln_build.h") != "$line" ]]; then
  printf '%s\n' "$line" > "$B/generated/kiln_build.h"
fi
if (($#)) && [[ $1 != all ]]; then cmake --build "$B" --target "$@"; else cmake --build "$B"; fi
