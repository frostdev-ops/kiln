#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Builds hooks_v2_test.exe and runs it under plain Wine (a scratch prefix under $KILN_OUT; no game, no window).
# Runs it twice: with the caller-table TLS slot below 64 (TEB TlsSlots) and at/above 64 (TlsExpansionSlots), then once
# in "slots" mode (a process that uses up every link slot) and in "targets" mode (every target slot).
# Exit code 0 = everything passed.
#   KILN_OUT=<build dir> native/core/tests/test-hooks-v2.sh
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
export KILN_OUT=${KILN_OUT:-$here/../../build}
"$here/../../build.sh" hooks_v2_test >/dev/null
export WINEPREFIX=$KILN_OUT/hooksv2-pfx WINEDEBUG=-all WINEDLLOVERRIDES="mscoree=;mshtml=;winedbg.exe=d"
# no window may reach the desktop: an unset WAYLAND_DISPLAY means the desktop's wayland-0
unset DISPLAY; export WAYLAND_DISPLAY=kiln-no-display
mkdir -p "$WINEPREFIX"
[[ -f $WINEPREFIX/system.reg ]] || wine wineboot -i >/dev/null 2>&1
exe=$KILN_OUT/cmake/bin/hooksv2/hooks_v2_test.exe
rc=0
for mode in normal tls64 slots targets; do
  echo "== hooks_v2_test ($mode)"
  wine "$exe" "$mode" || rc=1
done
exit $rc
