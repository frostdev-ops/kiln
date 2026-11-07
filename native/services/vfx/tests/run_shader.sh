#!/usr/bin/env bash
# Runs the cook policy tests using the portable cooker's objects. All output stays under KILN_OUT (default /tmp).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../../.." && pwd)
export KILN_OUT=${KILN_OUT:-/tmp/kiln-vfx-shader-test}
"$root/tools/vfxcook/native/build.sh" >/dev/null
mapfile -t objects < <(rg --files --no-ignore "$KILN_OUT/vfxcook/CMakeFiles" -g '*.o' -g '!main.cpp.o')
out=$KILN_OUT/vfx-shader-test
mkdir -p "$out"
"${CXX:-c++}" -std=c++23 -O1 -Wall -Wextra -pthread -I"$root/native/third_party" \
    "$here/test_shader.cpp" "${objects[@]}" -o "$out/test_shader"
"$out/test_shader" "$out" "$root"
# Compile the actual boot reskin state machine and KFC-key reader with mock APIs. Windows/game boot code is excluded, not reimplemented.
python3 - "$root" "$out" <<'PY'
import sys
from pathlib import Path
root, out = map(Path, sys.argv[1:])
s = (root / 'native/services/vfx/boot.cpp').read_text()
def between(first, last):
    return first + s.split(first, 1)[1].split(last, 1)[0]
(out / 'reskin_impl.inc').write_text(
    between('struct Reskin {', 'kvb::GameCounts g_game;') +
    between('bool read_game_materials(', 'kvb::GameCounts counts_from_game()') +
    between('KilnOwner reskin_owner_locked(', '// Serves the result.'))
PY
"${CXX:-c++}" -std=c++23 -O1 -Wall -Wextra -pthread -I"$root/native/third_party" -I"$root/native/core/abi" -I"$out" \
    "$here/test_reskins.cpp" "${objects[@]}" -o "$out/test_reskins"
"$out/test_reskins" "$out"
