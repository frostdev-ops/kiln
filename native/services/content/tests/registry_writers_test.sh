#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln.content registry stage: archive-writer attribution from kiln-eml's change journal (registry/archive_journal.cpp) through
# composition and registry.json, on the host. The document it builds is left in $KILN_OUT/content-tests/registry-writers.json
# for a schema check (schema/state-registry.schema.json).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:?set KILN_OUT to the build directory}"
out=$KILN_OUT/content-tests
mkdir -p "$out"
echo "== registry-writers-test (host)"
g++ -std=c++20 -O1 -g -Wall -Wextra -Wno-unused-function -fsanitize=address,undefined -I"$here/../../../core/abi" -I"$here/../../../third_party" \
    "$here/registry_writers_test.cpp" "$here/../registry/archive_journal.cpp" "$here/../registry/ir.cpp" "$here/../registry/compose.cpp" \
    "$here/../registry/report.cpp" -o "$out/registry-writers-test"
"$out/registry-writers-test" "$here/../../../.." "$out/registry-writers.json" "$out/registry-writers-eml.json"
