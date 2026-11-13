#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln.ui's hook on LoadLibraryExW (load_hook.h) in a Windows process under plain Wine: no game, no display
# (tests/load_hook/harness.cpp and its three libraries, fixture.cpp). The check that matters most: a load in progress
# does not hold up another thread's load (GitHub PR 27; the game hung at its start).
#   KILN_OUT=<build dir> native/services/ui/tests/test-load-hook-wine.sh
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:?set KILN_OUT to an isolated build directory}"
export KILN_OUT
"$here/../../../build.sh" ui-load-hook-test ui-load-hook-plain ui-load-hook-blocker ui-load-hook-nested >/dev/null
export WINEPREFIX=$KILN_OUT/ui-load-hook-pfx WINEDEBUG=-all WINEDLLOVERRIDES="mscoree=;mshtml=;winedbg.exe=d"
unset DISPLAY
export WAYLAND_DISPLAY=kiln-no-display
mkdir -p "$WINEPREFIX"
[[ -f $WINEPREFIX/system.reg ]] || wine wineboot -i >/dev/null 2>&1
wine "$KILN_OUT/cmake/bin/ui-load-hook-test.exe" | tr -d '\r'
