#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln.content registry views (registry/views.cpp, dev.32 J1): Kiln names derived from hand-made reflection, the alias table,
# covering registry rows, the catalog over the generated schema plus a fixture registry, values, ids; on the host, with
# ASan/UBSan.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:?set KILN_OUT to the build directory}"
out=$KILN_OUT/content-tests
mkdir -p "$out"
echo "== registry-views-test (host)"
g++ -std=c++20 -O1 -g -Wall -Wextra -Wno-unused-function -Wno-missing-field-initializers -fsanitize=address,undefined \
    -I"$here/../../../third_party" -I"$here/../../../core/abi" \
    "$here/registry_views_test.cpp" "$here/../registry/views.cpp" "$here/../registry/regmap.cpp" "$here/../registry/ir.cpp" \
    -o "$out/registry-views-test"
"$out/registry-views-test"
