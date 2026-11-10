#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Builds compat_test.exe and runs it under plain Wine (a scratch prefix under $KILN_OUT; no game, no window): the
# compatibility rules against schema/fixtures/compat-rules/cases.json, and the overlap report on synthetic hook maps.
# Exit code 0 = everything passed.
#   KILN_OUT=<build dir> native/core/tests/test-compat.sh
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
export KILN_OUT=${KILN_OUT:-$here/../../build}
"$here/../../build.sh" compat_test >/dev/null
export WINEPREFIX=$KILN_OUT/compat-pfx WINEDEBUG=-all WINEDLLOVERRIDES="mscoree=;mshtml=;winedbg.exe=d"
# no window may reach the desktop: an unset WAYLAND_DISPLAY means the desktop's wayland-0
unset DISPLAY; export WAYLAND_DISPLAY=kiln-no-display
mkdir -p "$WINEPREFIX"
[[ -f $WINEPREFIX/system.reg ]] || wine wineboot -i >/dev/null 2>&1
fixtures=$(winepath -w "$here/../../../schema/fixtures")
wine "$KILN_OUT/cmake/bin/closeddll/compat_test.exe" "$fixtures"
