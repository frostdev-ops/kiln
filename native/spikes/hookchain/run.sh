#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Builds hookchain.exe and runs it under plain Wine (a scratch prefix under $KILN_OUT). Exit code 0 = all passed.
#   KILN_OUT=<build dir> native/spikes/hookchain/run.sh
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
export KILN_OUT=${KILN_OUT:-$here/../../build}
"$here/../../build.sh" hookchain >/dev/null
export WINEPREFIX=$KILN_OUT/hookchain-pfx WINEDEBUG=-all WINEDLLOVERRIDES="mscoree=;mshtml="
unset DISPLAY WAYLAND_DISPLAY
mkdir -p "$WINEPREFIX"
[[ -f $WINEPREFIX/system.reg ]] || wine wineboot -i >/dev/null 2>&1
wine "$KILN_OUT/cmake/bin/hookchain/hookchain.exe"
