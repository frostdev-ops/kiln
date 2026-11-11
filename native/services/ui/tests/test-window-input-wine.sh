#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln.ui's window procedure and subscribe_window_input (kiln_ui.h v5) under plain Wine: a message-only window, no game,
# no display (tests/window_input/harness.cpp).
#   KILN_OUT=<build dir> native/services/ui/tests/test-window-input-wine.sh
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:?set KILN_OUT to an isolated build directory}"
export KILN_OUT
"$here/../../../build.sh" ui-window-input-test >/dev/null
export WINEPREFIX=$KILN_OUT/ui-input-pfx WINEDEBUG=-all WINEDLLOVERRIDES="mscoree=;mshtml=;winedbg.exe=d"
unset DISPLAY
export WAYLAND_DISPLAY=kiln-no-display
mkdir -p "$WINEPREFIX"
[[ -f $WINEPREFIX/system.reg ]] || wine wineboot -i >/dev/null 2>&1
wine "$KILN_OUT/cmake/bin/ui-window-input-test.exe" | tr -d '\r'
