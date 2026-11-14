#!/usr/bin/env bash
# Builds and runs kiln.assets' host test: the real store (store.cpp, catalog.cpp, util.cpp) with the game faked at the calls hooks.cpp
# makes for it, on the pin (KILN_ASSET_PUT_PIN), the player graph's reservation, blob provenance and the content blob catalog
# (PASS assets.catalog-snapshot), and a native owner the core revokes without revert_owner (PASS assets.revoked-owner-cleanup).
# No game, no Wine.
#   native/services/assets/tests/run.sh
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:=$here/../../../build}"
out=$KILN_OUT/assets-host-test
mkdir -p "$out"

# The service's header is C: it compiles as C11.
printf '#include "services/kiln_assets.h"\n_Static_assert(KILN_ASSET_PUT_PIN == 4, "KILN_ASSET_PUT_PIN");\n_Static_assert(sizeof(KilnBlobCatalogInfo) == 32, "info");\n_Static_assert(sizeof(KilnBlobCatalogEntry) == 48, "entry");\n' |
  gcc -std=c11 -Wall -Wextra -pedantic -fsyntax-only -I"$here/../../../core/abi" -x c -
echo "PASS kiln_assets.h compiles as C11"

g++ -std=c++20 -O1 -g -Wall -Wextra -DKILN_ASSETS_CATALOG_TEST -fsanitize=address,undefined -fno-sanitize-recover=undefined \
  -I"$here/platform" -I"$here/.." -I"$here/../../../core/abi" \
  "$here/test_store.cpp" "$here/../store.cpp" "$here/../catalog.cpp" "$here/../util.cpp" -o "$out/test_store" -pthread
# Resource bytes are never freed by design (the game keeps pointers into them): no leak check.
ASAN_OPTIONS=detect_leaks=0 "$out/test_store"

# Which cooked files a game loads (cooktrust.cpp): SHA-256, the cook receipts, kiln.json's "cooked".
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -fno-sanitize-recover=undefined \
  -I"$here/.." -I"$here/../../../third_party" \
  "$here/test_cooktrust.cpp" "$here/../cooktrust.cpp" -o "$out/test_cooktrust"
"$out/test_cooktrust" "$out/cooktrust-scratch"

# Reading a mod's files from its own folder, the portable half (scan.cpp): the identity scan, the alias verdict, the
# by-name check of the assets folder and below.
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -fno-sanitize-recover=undefined \
  -I"$here/.." -I"$here/../../../third_party" \
  "$here/test_scan.cpp" "$here/../scan.cpp" "$here/../cooktrust.cpp" -o "$out/test_scan"
"$out/test_scan" "$out/scan-scratch"
