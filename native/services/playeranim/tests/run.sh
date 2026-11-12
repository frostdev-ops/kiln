#!/usr/bin/env bash
# Builds and runs kiln.playeranim's host test: the type layouts, the codec, the definition reader and the merge in C++, against
# the Python reference (tools/animcook/anicook/playergraph.py, playerstates.py, tools/kfc) on the same inputs. No game process, no Wine.
#   native/services/playeranim/tests/run.sh
# gen_cases.py runs the Python side and writes its inputs and outputs to $KILN_OUT/playeranim-host-test/cases (nothing generated is in
# the repository). With the game's player graph (set KILN_PLAYER_GRAPH to its file) the real-graph cases run too; with the
# installed game (KILN_GAME) the clips' facts come from it and the exe's type table is checked against the bundled layouts (the exe is
# only read as a file). Without them those parts print SKIP.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:=$here/../../../build}"
out=$KILN_OUT/playeranim-host-test
mkdir -p "$out"
graph=${KILN_PLAYER_GRAPH-}
if [[ -z $graph ]]; then
  printf '%s\n' 'SKIP real-graph cases: KILN_PLAYER_GRAPH is unset'
fi
game=${KILN_GAME-$HOME/.local/share/Steam/steamapps/common/Enshrouded}
exe="$game/enshrouded.exe"
[[ -f $exe ]] || exe=""

# The service's header is C: it compiles as C11, and its structs have the sizes x64 gives them.
printf '#include "services/kiln_playeranim.h"\n_Static_assert(sizeof(KilnPaGameState) == 48, "KilnPaGameState");\n_Static_assert(sizeof(KilnPaState) == 112, "KilnPaState");\n' |
  gcc -std=c11 -Wall -Wextra -pedantic -fsyntax-only -I"$here/../../../core/abi" -x c -
echo "PASS kiln_playeranim.h compiles as C11"

rm -rf "$out/cases"
python3 -I "$here/gen_cases.py" --out "$out/cases" ${graph:+--graph "$graph"} ${game:+--game "$game"}

src=("$here/test_playeranim.cpp" "$here/../typedb.cpp" "$here/../blob.cpp" "$here/../states.cpp" "$here/../merge.cpp")
inc=(-I"$here/.." -I"$here/../../../third_party")
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -fno-sanitize-recover=undefined "${inc[@]}" "${src[@]}" -o "$out/test_playeranim"
"$out/test_playeranim" "$out/cases" ${exe:+"$exe"}

# The service lifecycle with an assets table that calls ready synchronously. A lock regression fails within 5 seconds.
g++ -std=c++20 -O1 -g -Wall -Wextra "${inc[@]}" -I"$here/platform" -I"$here/../../../core/abi" \
  "$here/test_service.cpp" "${src[@]:1}" -o "$out/test_service"
timeout 5 "$out/test_service" "$out/service-fixtures"

# The times on the game's graph, from an optimized build without sanitizers.
g++ -std=c++20 -O2 -Wall -Wextra "${inc[@]}" "${src[@]}" -o "$out/time_playeranim"
"$out/time_playeranim" --time "$out/cases" ${exe:+"$exe"}
