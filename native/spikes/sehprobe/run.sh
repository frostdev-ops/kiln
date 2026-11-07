#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Builds and runs the SEH probe under Wine: does __try catch a fault inlined in its body with /EHsc vs /EHa?
#   KILN_OUT=<build dir> native/spikes/sehprobe/run.sh
# Exit 0 = every case caught in the /EHa build (the project's flag). The /EHsc build is informational.
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd)
KILN_OUT=${KILN_OUT:-$here/../../build}
export KILN_OUT
"$here/../../build.sh" sehprobe-ehsc sehprobe-eha >/dev/null || exit 1
bin=$KILN_OUT/cmake/bin/sehprobe
export WINEPREFIX=$KILN_OUT/sehprobe/pfx WINEDEBUG=-all WINEDLLOVERRIDES="mscoree=;mshtml=;winedbg.exe=d"
unset DISPLAY WAYLAND_DISPLAY
mkdir -p "$WINEPREFIX"
wine "$bin/sehprobe-ehsc.exe" 2>/dev/null | tr -d '\r'
wine "$bin/sehprobe-eha.exe" 2>/dev/null | tr -d '\r'
status=${PIPESTATUS[0]}
[[ $status == 0 ]] && echo "sehprobe: EHa build caught every fault" || echo "sehprobe: EHa build missed $status case(s)"
exit "$status"
