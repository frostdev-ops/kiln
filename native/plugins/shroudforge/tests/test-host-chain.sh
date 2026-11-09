#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Builds sf_host_chain_test.exe and runs it under plain Wine (a scratch prefix under $KILN_OUT; no game, no window).
#   KILN_OUT=<build dir> native/plugins/shroudforge/tests/test-host-chain.sh
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
export KILN_OUT=${KILN_OUT:-$here/../../../build}
"$here/../../../build.sh" sf_host_chain_test >/dev/null
export WINEPREFIX=$KILN_OUT/sfhost-pfx WINEDEBUG=-all WINEDLLOVERRIDES="mscoree=;mshtml=;winedbg.exe=d"
unset DISPLAY; export WAYLAND_DISPLAY=kiln-no-display
mkdir -p "$WINEPREFIX"
[[ -f $WINEPREFIX/system.reg ]] || wine wineboot -i >/dev/null 2>&1
wine "$KILN_OUT/cmake/bin/sfhost/sf_host_chain_test.exe"
