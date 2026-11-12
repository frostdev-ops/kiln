#!/usr/bin/env bash
# Builds typedump with the host compiler into $KILN_OUT/typedump/typedump (KILN_OUT defaults to native/build, which
# git ignores) and prints that path.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../../native/build}/typedump
mkdir -p "$out"
cxx=${CXX:-$(command -v clang++ || command -v g++)}
"$cxx" -std=c++20 -O2 -Wall -Wextra -o "$out/typedump" "$here/typedump.cpp"
echo "$out/typedump"
