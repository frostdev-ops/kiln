#!/usr/bin/env bash
# Builds and runs kiln.worldtables' host test: ids, the handshake hash, names and the table writers, on synthetic resources. No game, no Wine.
#   native/services/worldtables/tests/run.sh [registry.bin pools.bin blueprints.bin world.bin [voxel-model.bin]]
# The optional files are a game's own resources (local data, never in the repo): the test then also checks that a merge with nothing to do gives
# the same bytes back, and that a new material reads back from them.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:=$here/../../../build}"
out=$KILN_OUT/worldtables-host-test
mkdir -p "$out"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -I"$here/.." -I"$here/../../surfaces/cook" -I"$here/../../../third_party" \
    "$here/test_merge.cpp" "$here/../merge.cpp" "$here/../../surfaces/cook/surfacecheck.cpp" -o "$out/test_merge"
"$out/test_merge" "$@"
