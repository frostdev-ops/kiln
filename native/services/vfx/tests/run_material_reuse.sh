#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln-vfx's material records (test_material_reuse.cpp): builds counted, reports the same as a cook without records. Host only, no Wine.
#   native/services/vfx/tests/run_material_reuse.sh [mod folder]   (a mod folder: also times every material of it; scratch under $TMPDIR)
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
cook=$here/../cook
: "${KILN_OUT:=$here/../../../build}"
out=$KILN_OUT/vfx-material-reuse
mkdir -p "$out"
src=("$cook"/{contenthash,ids,budget,starter,shader,spvlink,define,build,texture,material,pack,fingerprint,carry}.cpp "$here/../../../third_party/bc7enc/bc7enc.cpp" "$here/../../../third_party/bc7enc/rgbcx.cpp")
"${CXX:-g++}" -std=c++26 -O2 -ffp-contract=off -fno-fast-math -w -pthread -I"$cook" -I"$here/../../../third_party" \
  "$here/test_material_reuse.cpp" "${src[@]}" -o "$out/test_material_reuse"
"$out/test_material_reuse" "$@"
