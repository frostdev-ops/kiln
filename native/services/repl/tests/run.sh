#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln.repl host tests under plain Wine, no game: the ABI layout of kiln_repl.h and the fault rule every observer
# detour follows (guard.h), with real access violations; the lab harness's gate (policy "any" and kiln.net's lab port)
# on the service's own start. The game's behaviour is the lab test mods/tests/repl-test.
#   KILN_OUT=<build dir> native/services/repl/tests/run.sh
# Exit code 0 = every check passed.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
export KILN_OUT=${KILN_OUT:-$here/../../../build}
"$here/../../../build.sh" repl-host-test repl-gate-test >/dev/null
bin=$KILN_OUT/cmake/bin/repl-tests
export WINEPREFIX=$KILN_OUT/repl-tests-pfx WINEDEBUG=-all WINEDLLOVERRIDES="mscoree=;mshtml=;winedbg.exe=d"
# no window may reach the desktop: an unset WAYLAND_DISPLAY means the desktop's wayland-0
unset DISPLAY; export WAYLAND_DISPLAY=kiln-no-display
mkdir -p "$WINEPREFIX"
[[ -f $WINEPREFIX/system.reg ]] || wine wineboot -i >/dev/null 2>&1
fail=0
wine "$bin/repl-host-test.exe" || fail=1
wine "$bin/repl-gate-test.exe" || fail=1
exit $fail
