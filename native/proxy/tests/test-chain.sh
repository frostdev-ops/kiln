#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
export KILN_OUT=${KILN_OUT:-$here/../../build}
"$here/../../build.sh" proxy-chain-test
export WINEPREFIX=$KILN_OUT/proxy-chain-pfx WINEDEBUG=-all WINEDLLOVERRIDES='winedbg.exe=d'
unset DISPLAY; export WAYLAND_DISPLAY=kiln-no-display
mkdir -p "$WINEPREFIX"
[[ -f $WINEPREFIX/system.reg ]] || wine wineboot -i >/dev/null 2>&1
scratch=$(mktemp -d "$KILN_OUT/proxy-chain.XXXXXX")
trap 'rm -rf "$scratch"' EXIT
wine "$KILN_OUT/cmake/bin/proxy-chain-test.exe" "$scratch" \
    "$KILN_OUT/cmake/bin/proxy-test-addon.dll" "$KILN_OUT/cmake/bin/proxy-test-plain.dll"
