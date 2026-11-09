#!/usr/bin/env bash
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:?set KILN_OUT}/flame-host
mkdir -p "$out"
${CXX:-g++} -std=c++20 -O1 -g -pthread -Wall -Wextra -fsanitize=address,undefined \
  -I"$here/host" -I"$here/../../../core/abi" "$here/flame_test.cpp" -o "$out/flame_test"
ASAN_OPTIONS=detect_leaks=0 "$out/flame_test"
