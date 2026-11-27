#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Run through the shared build limiter. This links the production service and presentation lifecycle together.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${1:?usage: test-service-presentation.sh OUTPUT_DIRECTORY}
mkdir -p "$out"
"${CXX:-c++}" -std=c++20 -pthread -D__fastcall= -I"$here/../../../core/abi" -I"$here/../include" -I"$here/../../../third_party" \
    "$here/service_presentation_lifecycle.cpp" "$here/../hooks/presentation.cpp" "$here/../policy.cpp" \
    -o "$out/service-presentation-lifecycle"
"$out/service-presentation-lifecycle"
