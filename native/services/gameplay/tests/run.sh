#!/usr/bin/env bash
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:?set KILN_OUT}/gameplay-host-test
mkdir -p "$out"
g++ -std=c++20 -O1 -g -pthread -Wall -Wextra -fsanitize=address,undefined \
    -I"$here/../../../core/abi" "$here/component_change_test.cpp" -o "$out/component_change_test"
"$out/component_change_test"
