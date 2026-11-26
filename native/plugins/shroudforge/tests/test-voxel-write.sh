#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# The host voxel writer's portable core (src/voxel_write_core.h) on Linux: g++ with ASan/UBSan, no game, no Windows.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:?set KILN_OUT to the assigned output directory}/sf-voxel-write
mkdir -p "$out"
"${CXX:-g++}" -std=c++20 -O1 -g -Wall -Wextra -Werror -fsanitize=address,undefined -fno-sanitize-recover=undefined \
    -I"$here/../../../core/abi" "$here/voxel_write_core_test.cpp" -o "$out/voxel-write-core"
"$out/voxel-write-core"
