#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Host test of foreign_scan.h (Kiln waits for shroudlink's client addon before it hooks game code). No game, no Wine.
# The same wait against the real addon DLL under Wine: native/services/ui/tests/test-addon-wine.sh.
#   native/core/tests/test-foreign-scan.sh
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../build}/core-tests
mkdir -p "$out"
cxx=${CXX:-$(command -v clang++ || command -v g++)}
"$cxx" -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -o "$out/foreign_scan_test" "$here/foreign_scan_test.cpp"
"$out/foreign_scan_test"
