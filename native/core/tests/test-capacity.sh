#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Host test of the shared capacity and registration contract (abi/services/kiln_capacity.h), with ASan/UBSan, as C++
# and (the header alone) as C:
#   native/core/tests/test-capacity.sh [registration id vector, to compare]
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../build}/core-tests
mkdir -p "$out"
cxx=${CXX:-$(command -v clang++ || command -v g++)}
cc=${CC:-$(command -v clang || command -v gcc)}
printf '#include "services/kiln_capacity.h"\n_Static_assert(sizeof(KilnCapacityInfo) == 48, "size");\n_Static_assert(sizeof(KilnDefinitionReply) == 64, "size");\nint main(void) { return 0; }\n' > "$out/capacity_c.c"
"$cc" -std=c11 -Wall -Wextra -Werror -I"$here/../abi" -o "$out/capacity_c" "$out/capacity_c.c"
"$cxx" -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -fno-sanitize-recover=undefined \
    -I"$here/../abi" -o "$out/capacity_test" "$here/capacity_test.cpp"
"$out/capacity_test" "$@"
