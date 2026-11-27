#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln.content registry: naming run-time writers from kiln.assets' write records (registry/runtime_writes.h, sfi R3), on the
# host.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:?set KILN_OUT to the build directory}"
out=$KILN_OUT/content-tests
mkdir -p "$out"
echo "== registry-runtime-writes-test (host)"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -fno-sanitize-recover=all -I"$here/../../../core/abi" \
    "$here/registry_runtime_writes_test.cpp" -o "$out/registry-runtime-writes-test"
"$out/registry-runtime-writes-test"
