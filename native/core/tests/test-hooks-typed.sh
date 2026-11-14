#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Builds hooks_typed_test.exe (the typed link kind of kiln.hooks) and runs it under plain Wine (a scratch prefix under
# $KILN_OUT; no game, no window). Exit code 0 = everything passed.
#   KILN_OUT=<build dir> native/core/tests/test-hooks-typed.sh
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
export KILN_OUT=${KILN_OUT:-$here/../../build}
"$here/../../build.sh" hooks_typed_test >/dev/null
export WINEPREFIX=$KILN_OUT/hooksv2-pfx WINEDEBUG=-all WINEDLLOVERRIDES="mscoree=;mshtml=;winedbg.exe=d"
# no window may reach the desktop: an unset WAYLAND_DISPLAY means the desktop's wayland-0
unset DISPLAY; export WAYLAND_DISPLAY=kiln-no-display
mkdir -p "$WINEPREFIX"
[[ -f $WINEPREFIX/system.reg ]] || wine wineboot -i >/dev/null 2>&1
exe=$KILN_OUT/cmake/bin/hooksv2/hooks_typed_test.exe
echo "== hooks_typed_test"
wine "$exe"
