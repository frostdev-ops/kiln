#!/usr/bin/env bash
# Host tests of kiln-emf's EmfApi v22-v26 logic (src/v26_model.*): no game, no Wine.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:=$here/../../../build}"
out=$KILN_OUT/emf-host-test
mkdir -p "$out"
g++ -std=c++20 -Wall -Wextra -I"$here/../include" "$here/abi_test.cpp" -o "$out/abi_test"
"$out/abi_test"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -I"$here/../include" -I"$here/../src" \
    "$here/v26_model_test.cpp" "$here/../src/v26_model.cpp" -o "$out/v26_model_test"
"$out/v26_model_test"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined \
    -I"$here/host" -I"$here/../include" -I"$here/../src" -I"$here/../../../core/abi" -I"$here/../../../services/players" \
    "$here/glue_test.cpp" "$here/../src/api_windows.cpp" "$here/../src/api_level.cpp" "$here/../src/api_components.cpp" "$here/../src/api_events.cpp" "$here/../src/v26_model.cpp" \
    -o "$out/glue_test"
"$out/glue_test" "$out"
