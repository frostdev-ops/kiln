#!/usr/bin/env bash
# kiln.creature_control on the host (no game, no Wine): the broker's registry and lease life with address and
# undefined-behaviour sanitizers, and the layout guard against the committed type dumps.
#   [KILN_OUT=<build dir>] native/services/creature_control/tests/run.sh
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:=/tmp/kiln-creature-control-tests}"
out=$KILN_OUT/creature-control-tests
mkdir -p "$out"
g++ -std=c++20 -O1 -g -Wall -Wextra -pthread -fsanitize=address,undefined -fno-sanitize-recover=undefined \
    -I"$here/.." -I"$here/../../../core/abi" \
    "$here/broker_test.cpp" "$here/../registry.cpp" "$here/../lease.cpp" -o "$out/broker-test"
"$out/broker-test"
python3 "$here/guard_dump_test.py"
