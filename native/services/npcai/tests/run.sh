#!/usr/bin/env bash
# Native state, hook lifecycle and concurrent readers without a game or Wine.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:=/tmp/kiln-npcai-tests}"
mkdir -p "$KILN_OUT/npcai-tests"
g++ -std=c++20 -O1 -g -Wall -Wextra -pthread -fsanitize=address,undefined \
    -I"$here/stubs" -I"$here/../../../core/abi" "$here/npcai_test.cpp" -o "$KILN_OUT/npcai-tests/npcai-test"
"$KILN_OUT/npcai-tests/npcai-test"
