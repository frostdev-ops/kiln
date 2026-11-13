#!/usr/bin/env bash
# Host checks of rule publication, ABI compatibility, Shroud floors, client ray guards and the flight, NoClip, creative
# and glider-steering hooks.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../build}/rules-guards
mkdir -p "$out"
cxx=${CXX:-$(command -v clang++ || command -v g++)}
"$cxx" -std=c++20 -O2 -Wall -Wextra -Wno-unused-function -pthread -I"$here/../../core/abi" -o "$out/rules_test" "$here/rules_test.cpp"
"$out/rules_test"
"$cxx" -std=c++20 -O2 -Wall -Wextra -o "$out/creative_math_test" "$here/creative_math_test.cpp"
"$out/creative_math_test"
"$cxx" -std=c++20 -O2 -Wall -Wextra -o "$out/flight_math_test" "$here/flight_math_test.cpp"
"$out/flight_math_test"
"$cxx" -std=c++20 -O2 -Wall -Wextra -Wno-unused-function -pthread -I"$here/tests/stubs" -I"$here/../../core/abi" \
    -o "$out/flight_hook_test" "$here/flight_hook_test.cpp"
"$out/flight_hook_test"
"$cxx" -std=c++20 -O2 -Wall -Wextra -Wno-unused-function -pthread -I"$here/tests/stubs" -I"$here/../../core/abi" \
    -o "$out/noclip_hook_test" "$here/noclip_hook_test.cpp"
"$out/noclip_hook_test"
"$cxx" -std=c++20 -O2 -Wall -Wextra -Wno-unused-function -pthread -I"$here/tests/stubs" -I"$here/../../core/abi" \
    -o "$out/steer_hook_test" "$here/steer_hook_test.cpp"
"$out/steer_hook_test"
"$cxx" -std=c++20 -O2 -Wall -Wextra -Wno-unused-function -pthread -I"$here/tests/stubs" -I"$here/../../core/abi" \
    -I"$here/../../plugins/flame/include" -o "$out/creative_hook_test" "$here/creative_hook_test.cpp"
"$out/creative_hook_test"
"$cxx" -std=c++20 -O2 -Wall -Wextra -pthread -I"$here/tests/stubs" -I"$here/../../core/abi" \
    -o "$out/pide_test" "$here/../items/tests/pide_test.cpp"
"$out/pide_test"
