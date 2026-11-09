#!/usr/bin/env bash
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:?set KILN_OUT}/types-test
mkdir -p "$out"
g++ -std=c++20 -O1 -g -pthread -Wall -Wextra -fsanitize=address,undefined "$here/system_hooks_test.cpp" -o "$out/system_hooks_test"
"$out/system_hooks_test"
