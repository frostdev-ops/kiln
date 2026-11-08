#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Builds and runs the SEH probe under Wine: does __try catch a fault inlined in its body with /EHsc vs /EHa?
#   KILN_OUT=<build dir> native/spikes/sehprobe/run.sh
# Exit 0 = every /EHa check passed. The /EHsc build is informational.
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd)
KILN_OUT=${KILN_OUT:-$here/../../build}
export KILN_OUT
"$here/../../build.sh" sehprobe-ehsc sehprobe-eha >/dev/null || exit 1
bin=$KILN_OUT/cmake/bin/sehprobe
export WINEPREFIX=$KILN_OUT/sehprobe/pfx WINEDEBUG=-all WINEDLLOVERRIDES="mscoree=;mshtml=;winedbg.exe=d"
# no window may reach the desktop: an unset WAYLAND_DISPLAY means the desktop's wayland-0
unset DISPLAY; export WAYLAND_DISPLAY=kiln-no-display
mkdir -p "$WINEPREFIX"
wine "$bin/sehprobe-ehsc.exe" 2>"$KILN_OUT/sehprobe/ehsc.err" | tr -d '\r'
wine "$bin/sehprobe-eha.exe" 2>"$KILN_OUT/sehprobe/eha.err" | tr -d '\r' | tee "$KILN_OUT/sehprobe/eha.log"
status=${PIPESTATUS[0]}
if [[ ! -s $KILN_OUT/sehprobe/eha.log ]]; then
    cat "$KILN_OUT/sehprobe/eha.err" >&2
    echo "sehprobe: Wine did not run the tests (exit $status)" >&2
    exit 1
fi
[[ $status == 0 ]] && echo "sehprobe: every EHa check passed" || echo "sehprobe: EHa build failed $status check(s)"
exit "$status"
