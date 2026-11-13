#!/usr/bin/env bash
# Builds and runs kiln.assets' host test: the real store (store.cpp, util.cpp) with the game faked at the calls hooks.cpp makes for it, on
# the pin (KILN_ASSET_PUT_PIN) and the player graph's reservation. No game, no Wine.
#   native/services/assets/tests/run.sh
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:=$here/../../../build}"
out=$KILN_OUT/assets-host-test
mkdir -p "$out"

# The service's header is C: it compiles as C11.
printf '#include "services/kiln_assets.h"\n_Static_assert(KILN_ASSET_PUT_PIN == 4, "KILN_ASSET_PUT_PIN");\n' |
  gcc -std=c11 -Wall -Wextra -pedantic -fsyntax-only -I"$here/../../../core/abi" -x c -
echo "PASS kiln_assets.h compiles as C11"

g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -fno-sanitize-recover=undefined \
  -I"$here/platform" -I"$here/.." -I"$here/../../../core/abi" \
  "$here/test_store.cpp" "$here/../store.cpp" "$here/../util.cpp" -o "$out/test_store"
# Resource bytes are never freed by design (the game keeps pointers into them): no leak check.
ASAN_OPTIONS=detect_leaks=0 "$out/test_store"

# Which cooked files a game loads (cooktrust.cpp): SHA-256, the cook receipts, kiln.json's "cooked".
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -fno-sanitize-recover=undefined \
  -I"$here/.." -I"$here/../../../third_party" \
  "$here/test_cooktrust.cpp" "$here/../cooktrust.cpp" -o "$out/test_cooktrust"
"$out/test_cooktrust" "$out/cooktrust-scratch"
