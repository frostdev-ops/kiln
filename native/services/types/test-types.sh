#!/usr/bin/env bash
# Host test for kiln.types' scanner. Pass exe paths, or set KILN_SERVER_EXE / KILN_CLIENT_EXE; without any the test
# runs its exe-independent checks only.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../build}/types-test
mkdir -p "$out"
cxx=${CXX:-$(command -v clang++ || command -v g++)}
"$cxx" -std=c++20 -O2 -Wall -Wextra -o "$out/types_test" "$here/types_test.cpp"
exes=("$@")
if ((${#exes[@]} == 0)); then
  for e in "${KILN_SERVER_EXE:-}" "${KILN_CLIENT_EXE:-}"; do [[ -n $e && -f $e ]] && exes+=("$e"); done
fi
"$out/types_test" "${exes[@]}"

KILN_OUT=${KILN_OUT:-$here/../../build} "$here/test-system-hooks.sh"
