#!/usr/bin/env bash
# Parent runs under the shared slot. "baseline" deliberately fails the exact 2049-key regression.
# KILN_OUT=... slot bash native/services/assets/tests/run_vfx.sh baseline
# KILN_OUT=... slot bash native/services/assets/tests/run_vfx.sh
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:?Set KILN_OUT to your task output directory}"
out=$KILN_OUT/assets-vfx-test
mkdir -p "$out"
source_file=$here/../store.cpp
flags=(-DKILN_ASSETS_OMISSION_REPORT_TEST)
if [[ ${1:-} == baseline ]]; then
  git -C "$here" show cd85edc8:native/services/assets/store.cpp > "$out/store-baseline.cpp"
  source_file=$out/store-baseline.cpp
  flags=(-DKILN_ASSETS_VFX_BASELINE)
else
  # Compile the real type-list hook and collector reporter with only their game/core calls faked.
  sed -n '/^bool copy_vfx_keys(/,/^void note_S(/p' "$here/../hooks.cpp" | sed '$d' > "$out/vfx_copy_keys.inc"
  sed -n '/^TypeList\* __fastcall hk_by_type(/,/^\/\/ ---- content:/p' "$here/../hooks.cpp" | sed '$d' > "$out/vfx_by_type.inc"
  sed -n '/^void report_resource_omission(/,/^namespace {/p' "$here/../assets.cpp" | sed '$d' > "$out/resource_omission.inc"
fi
g++ -std=c++20 -O1 -g -Wall -Wextra -DKILN_ASSETS_CATALOG_TEST "${flags[@]}" \
  -fsanitize=address,undefined -fno-sanitize-recover=undefined \
  -I"$here/platform" -I"$here/.." -I"$here/../../../core/abi" -I"$here/../../../third_party" -I"$out" \
  "$here/test_vfx.cpp" "$source_file" "$here/../catalog.cpp" "$here/../util.cpp" -o "$out/test_vfx" -pthread
if [[ ${1:-} == baseline ]]; then
  ASAN_OPTIONS=detect_leaks=0 "$out/test_vfx" boundary-any "$out/stock.kfc"
else
  for mode in boundary-any boundary-new retry originals actual bind bind-unreadable unknown capacity concurrent metadata provenance-bind provenance-final provenance-unreadable provenance-retry; do
    ASAN_OPTIONS=detect_leaks=0 "$out/test_vfx" "$mode" "$out/stock.kfc"
  done
fi
