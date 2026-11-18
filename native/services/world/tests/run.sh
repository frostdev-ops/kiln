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
# client_world_test: Kiln programs in a client's client world and prediction storage (0.2.0-dev.29, chapter 14) over a
# fake process with three worlds (a host's server world, its client world, the prediction storage).
g++ -std=c++20 -O1 -g -pthread -Wall -Wextra -fsanitize=address,undefined -D__fastcall= \
    -I"$here/host" -I"$here/../../../third_party" -I"$here/../../../core/abi" "$here/client_world_test.cpp" -o "$out/client_world_test"
"$out/client_world_test" "$here/abi.json"

# writes_test links a consumer compiled against the v2 header 0.2.0-dev.26 shipped (tests/v2 comes first for it).
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -I"$here/v2" -I"$here/../../../core/abi/services" \
    -I"$here/../../../core/abi" -c "$here/v2_consumer.cpp" -o "$out/v2_consumer.o"
g++ -std=c++20 -O1 -g -pthread -Wall -Wextra -fsanitize=address,undefined -I"$here/../../../third_party" -I"$here/../../../core/abi" \
    "$here/writes_test.cpp" "$out/v2_consumer.o" -o "$out/writes_test"
"$out/writes_test" "$here/parity.json"

# budget_test: kiln.world.budget (0.2.0-dev.29) over a fake session, directory and slot ledger.
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -D__fastcall= -I"$here/host" \
    -I"$here/../../../third_party" -I"$here/../../../core/abi" "$here/budget_test.cpp" -o "$out/budget_test"
"$out/budget_test"

# ecs_reads_test: verified-copy ECS reads over a fake engine memory model (identity, snapshots, queries,
# concurrency), under ASan/UBSan and ThreadSanitizer.
g++ -std=c++20 -O1 -g -pthread -Wall -Wextra -fsanitize=address,undefined -I"$here/../../../core/abi" \
    "$here/ecs_reads_test.cpp" "$here/../ecs_reads.cpp" -o "$out/ecs_reads_test_asan"
"$out/ecs_reads_test_asan"
g++ -std=c++20 -O1 -g -pthread -Wall -Wextra -fsanitize=thread -I"$here/../../../core/abi" \
    "$here/ecs_reads_test.cpp" "$here/../ecs_reads.cpp" -o "$out/ecs_reads_test_tsan"
TSAN_OPTIONS=halt_on_error=1:exitcode=66 "$out/ecs_reads_test_tsan"

# wraps_test: kiln.world v4 (the game's programs: directory, lab switch, wraps) over a fake world table.
g++ -std=c++20 -O1 -g -pthread -Wall -Wextra -fsanitize=address,undefined -D__fastcall= -I"$here/host" \
    -I"$here/../../../third_party" -I"$here/../../../core/abi" "$here/wraps_test.cpp" -o "$out/wraps_test"
"$out/wraps_test" "$here/abi.json"
