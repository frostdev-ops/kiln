#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../build}/hookchain-host
mkdir -p "$out"
shook=$here/../../third_party/safetyhook
cc -O2 -I"$shook" -c "$shook/Zydis.c" -o "$out/Zydis.o"
g++ -std=c++23 -O2 -pthread -Wno-unused-result -I"$shook" \
    "$shook/safetyhook.cpp" "$here/hookchain_host.cpp" "$out/Zydis.o" -o "$out/hookchain-host"
"$out/hookchain-host" "$@"
