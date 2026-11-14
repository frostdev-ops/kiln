#!/usr/bin/env bash
# The service's pure parts without a game or Wine: the grid box preflight, ray numbers and result mapping, the budget
# bucket, the config file and entity origins.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:=/tmp/kiln-queries-tests}"
mkdir -p "$KILN_OUT/queries-tests"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined \
    -I"$here/.." -I"$here/../../../third_party" "$here/queries_test.cpp" -o "$KILN_OUT/queries-tests/queries-test"
"$KILN_OUT/queries-tests/queries-test"
