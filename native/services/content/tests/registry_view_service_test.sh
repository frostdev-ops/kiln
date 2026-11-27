#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln.content registry views: the service behind kiln.registry.view (registry/view_service.cpp) over a fake game, on the host,
# with ASan/UBSan.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:?set KILN_OUT to the build directory}"
out=$KILN_OUT/content-tests
mkdir -p "$out"
echo "== registry-view-service-test (host)"
g++ -std=c++20 -O1 -g -Wall -Wextra -Wno-unused-function -Wno-missing-field-initializers -fsanitize=address,undefined \
    -I"$here/../../../third_party" -I"$here/../../../core/abi" \
    "$here/registry_view_service_test.cpp" "$here/../registry/view_service.cpp" "$here/../registry/views.cpp" \
    "$here/../registry/regmap.cpp" "$here/../registry/ir.cpp" -o "$out/registry-view-service-test"
"$out/registry-view-service-test"
