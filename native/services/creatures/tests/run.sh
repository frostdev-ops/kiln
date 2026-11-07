#!/usr/bin/env bash
# Exercise the real queue on Linux with address and undefined-behaviour sanitizers.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:=/tmp/kiln-review-rulesq}"
mkdir -p "$KILN_OUT/creatures-tests"
g++ -std=c++20 -O1 -g -Wall -Wextra -pthread -ffunction-sections -fdata-sections -Wl,--gc-sections \
    -fsanitize=address,undefined -I"$here/stubs" -I"$here/../../../core/abi" \
    "$here/rules_queue_test.cpp" -o "$KILN_OUT/creatures-tests/rules-queue-test"
"$KILN_OUT/creatures-tests/rules-queue-test"
