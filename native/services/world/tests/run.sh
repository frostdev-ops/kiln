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

# writes_test links a consumer compiled against the v2 header 0.2.0-dev.26 shipped (tests/v2 comes first for it).
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -I"$here/v2" -I"$here/../../../core/abi/services" \
    -I"$here/../../../core/abi" -c "$here/v2_consumer.cpp" -o "$out/v2_consumer.o"
g++ -std=c++20 -O1 -g -pthread -Wall -Wextra -fsanitize=address,undefined -I"$here/../../../third_party" -I"$here/../../../core/abi" \
    "$here/writes_test.cpp" "$out/v2_consumer.o" -o "$out/writes_test"
"$out/writes_test" "$here/parity.json"
