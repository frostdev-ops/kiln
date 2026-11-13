#!/usr/bin/env bash
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:?set KILN_OUT}/world-host-test
mkdir -p "$out"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -I"$here/../../../third_party" -I"$here/../../../core/abi" "$here/boot_test.cpp" -o "$out/boot_test"
"$out/boot_test"
g++ -std=c++20 -O1 -g -pthread -Wall -Wextra -fsanitize=address,undefined -D__fastcall= \
    -I"$here/host" -I"$here/../../../core/abi" "$here/programs_test.cpp" -o "$out/programs_test"
"$out/programs_test"

g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -I"$here/../../../third_party" -I"$here/../../../core/abi" \
    "$here/writes_test.cpp" -o "$out/writes_test"
"$out/writes_test"
