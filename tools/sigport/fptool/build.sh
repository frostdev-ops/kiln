#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Builds kiln-fptool (the core's ABI fingerprint pass for the host) and prints its path.
#   tools/sigport/fptool/build.sh        output: $KILN_OUT/core-tests/kiln-fptool (KILN_OUT default native/build)
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
native=$here/../../../native
out=${KILN_OUT:-$native/build}/core-tests
mkdir -p "$out"
cxx=${CXX:-$(command -v clang++ || command -v g++)}
cc=${CC:-$(command -v clang || command -v gcc)}
zy=$native/third_party/safetyhook
src=$native/core/src/services/hooks_fingerprint.cpp
defs=(-DZYDIS_STATIC_BUILD -DZYCORE_STATIC_BUILD)
[[ $out/zydis-host.o -nt $zy/Zydis.c ]] || "$cc" -O1 -w "${defs[@]}" -I"$zy" -c -o "$out/zydis-host.o" "$zy/Zydis.c"
if [[ ! $out/kiln-fptool -nt $src || ! $out/kiln-fptool -nt $here/fptool.cpp || ! $out/kiln-fptool -nt ${src%.cpp}.h ]]; then
  "$cxx" -std=c++20 -O2 -Wall -Wextra "${defs[@]}" -I"$native/core/abi" -I"$native/core/src" -I"$native/third_party" -I"$zy" \
      -o "$out/kiln-fptool" "$here/fptool.cpp" "$src" "$out/zydis-host.o"
fi
echo "$out/kiln-fptool"
