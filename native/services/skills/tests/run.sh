#!/usr/bin/env bash
# Builds and runs kiln.skills' host test: player_scan.h, which entities the shadow table maps every server step (the
# players' records every step, the entity-map walk only when the entity count moved or now and then), against a fake
# world: the same set as a walk every step would give, and how many walks it takes. No game, no Wine.
# The tree builder has its own host test: tools/kfc/tests/test_skilltree_slots.py.
#   native/services/skills/tests/run.sh
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:=$here/../../../build}"
out=$KILN_OUT/skills-host-test
mkdir -p "$out"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -fno-sanitize-recover=undefined \
  -I"$here/.." -I"$here/../../../core/abi" "$here/test_player_scan.cpp" -o "$out/test_player_scan"
"$out/test_player_scan"
