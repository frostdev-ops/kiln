#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Host tests of kiln.ui.values (services/ui_values*.cpp) with ASan/UBSan, and a C compile of its public header:
#   native/core/tests/test-ui-values.sh
# Build output: $KILN_OUT/core-tests (KILN_OUT default native/build).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../build}/core-tests
mkdir -p "$out"
cxx=${CXX:-$(command -v clang++ || command -v g++)}
cc=${CC:-$(command -v clang || command -v gcc)}
# the public header is C: it must compile as C11 on its own
printf '#include "services/kiln_ui_values.h"\nint kiln_ui_values_header_ok;\n' |
  "$cc" -std=c11 -Wall -Wextra -Werror -pedantic -fsyntax-only -I"$here/../abi" -x c -
echo "kiln_ui_values.h: compiles as C11"
flags=(-std=c++20 -O1 -g -Wall -Wextra -pthread -fsanitize=address,undefined -fno-sanitize-recover=undefined
       -I"$here/stubs" -I"$here/../abi" -I"$here/../src/services")
"$cxx" "${flags[@]}" -o "$out/ui_values_test" "$here/ui_values_test.cpp"
timeout 300 "$out/ui_values_test"
