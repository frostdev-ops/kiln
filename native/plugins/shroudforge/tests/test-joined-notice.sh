#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Host test of the notice kiln-shroudforge gives when a joined game's ShroudForge mod tries to change blocks (src/joined_notice.h), with ASan/UBSan:
#   native/plugins/shroudforge/tests/test-joined-notice.sh
# Build output: $KILN_OUT/sf-tests (KILN_OUT default native/build).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../../build}/sf-tests
mkdir -p "$out"
cxx=${CXX:-$(command -v clang++ || command -v g++)}
"$cxx" -std=c++20 -O1 -g -Wall -Wextra -Werror -fsanitize=address,undefined -fno-sanitize-recover=undefined \
  -I"$here/../src" -I"$here/../../../core/abi" -o "$out/sf_joined_notice_test" "$here/joined_notice_test.cpp"
"$out/sf_joined_notice_test"
