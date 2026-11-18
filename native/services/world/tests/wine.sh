#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Headless Wine run of world-ecs-fault.exe: kiln.world's ECS reads over kiln.engine's real Windows primitives (the SEH
# copy) and a fake engine whose pages are made unreadable, guarded, decommitted and flipped under the reads. Build
# first: native/build.sh world-ecs-fault. Needs a private absolute WINEPREFIX; never a game install or a lab prefix.
set -euo pipefail
: "${KILN_OUT:?set KILN_OUT to the build output}"
: "${WINEPREFIX:?use a private absolute Wine prefix}"
[[ $WINEPREFIX == /* ]] || { printf 'WINEPREFIX must be absolute\n' >&2; exit 2; }
test_exe=$KILN_OUT/cmake/bin/tests/world-ecs-fault.exe
[[ -f $test_exe ]] || { printf 'Build world-ecs-fault first (native/build.sh world-ecs-fault)\n' >&2; exit 2; }
unset DISPLAY
export WAYLAND_DISPLAY=kiln-no-display WINEDLLOVERRIDES=winedbg.exe=d WINEDEBUG=-all
set +e
timeout 300s wine "$test_exe"
result=$?
wineserver -w
exit "$result"
