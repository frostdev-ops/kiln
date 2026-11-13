#!/usr/bin/env bash
# kiln.combat attributes: ABI layout of v2 and the attribute-resource parser, on the host (no game, no Wine).
#   native/services/combat/tests/run.sh
# Real-resource checks run when KILN_ATTR_RESOURCE names the raw bytes of keen::AttributeContainerResource, or when
# KILN_GAME_DIR (a game or server folder, read only) and KILN_SERVER_EXE (a copy of its exe) are set: the script then
# extracts the resource with tools/kfc. Without either, only the synthetic checks run.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../../../.." && pwd)
: "${KILN_OUT:=/tmp/kiln-combat-tests}"
out=$KILN_OUT/combat-tests
mkdir -p "$out"
if [[ -z ${KILN_ATTR_RESOURCE:-} && -n ${KILN_GAME_DIR:-} && -n ${KILN_SERVER_EXE:-} ]]; then
  # AttributeContainerResource, the game's only one (listed by type; the GUID is not baked into the test)
  guid=$(cd "$out" && KILN_OUT="$out" python3 "$repo/tools/kfc/kfc.py" --game "$KILN_GAME_DIR" --exe "$KILN_SERVER_EXE" \
         list --type keen::AttributeContainerResource | head -1 | cut -d' ' -f1)
  (cd "$out" && KILN_OUT="$out" python3 "$repo/tools/kfc/kfc.py" --game "$KILN_GAME_DIR" --exe "$KILN_SERVER_EXE" \
         get "$guid" keen::AttributeContainerResource -o "$out/attributes.bin" >/dev/null)
  export KILN_ATTR_RESOURCE=$out/attributes.bin
fi
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined \
    -I"$here/../../../core/abi" -I"$here/../../../plugins/emf/include" "$here/attr_test.cpp" -o "$out/attr-test"
"$out/attr-test"
# the entity pre-test of held hits (since 0.2.0-dev.27)
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined \
    -I"$here/../../../core/abi" "$here/hold_test.cpp" -o "$out/hold-test"
"$out/hold-test"
