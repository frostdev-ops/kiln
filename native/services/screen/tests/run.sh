#!/usr/bin/env bash
# Builds and runs kiln.screen's host test: the effect list, no game, no GPU, no Wine. On a GPU: build.sh, then test_gpu.py.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:=$here/../../../build}"
out=$KILN_OUT/screen-host-test
mkdir -p "$out"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -pthread -I"$here/../../../core/abi" -I"$here/.." "$here/test_core.cpp" "$here/../core.cpp" -o "$out/test_core_asan"
"$out/test_core_asan"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=thread -pthread -I"$here/../../../core/abi" -I"$here/.." "$here/test_core.cpp" "$here/../core.cpp" -o "$out/test_core_tsan"
"$out/test_core_tsan" | tail -1
