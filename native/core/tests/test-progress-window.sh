#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln.progress's window under Wine on a virtual display of its own (Xvfb through xvfb-run: nothing reaches the desktop).
# Builds progress_probe.exe (core/src/services/progress.cpp + stubs) and runs its scenarios; shots land in
# $KILN_OUT/progress-shots/. Exit code 0 = every check of every scenario passed.
#   KILN_OUT=<build dir> native/core/tests/test-progress-window.sh
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
export KILN_OUT=${KILN_OUT:-$here/../../build}
if [[ -z ${KILN_PROGRESS_TEST_INNER:-} ]]; then
  "$here/../../build.sh" progress_probe >/dev/null
  mkdir -p "$KILN_OUT/progress-shots"
  # a display of our own: DISPLAY from the caller is dropped, the Wayland driver is switched off
  unset DISPLAY; export WAYLAND_DISPLAY=kiln-no-display
  exec env KILN_PROGRESS_TEST_INNER=1 xvfb-run -a -s "-screen 0 1280x800x24 -nolisten tcp" bash "$0" "$@"
fi
export WINEPREFIX=$KILN_OUT/progress-pfx WINEDEBUG=-all WINEDLLOVERRIDES="mscoree=;mshtml=;winedbg.exe=d;winewayland.drv=d"
export WAYLAND_DISPLAY=kiln-no-display
mkdir -p "$WINEPREFIX"
[[ -f $WINEPREFIX/system.reg ]] || wine wineboot -i >/dev/null 2>&1
exe=$KILN_OUT/cmake/bin/progressprobe/progress_probe.exe
shots=$KILN_OUT/progress-shots
rc=0

run() {  # run <scenario> [env...]: the probe's output is checked for FAIL lines and its exit code
  local name=$1; shift
  echo "== progress_probe $name"
  env "$@" wine "$exe" "$name" > "$shots/$name.out" 2>&1 || rc=1
  grep -E "^PROBE" "$shots/$name.out" | grep -v "SHOT-READY" || true
  if grep -q "PROBE FAIL" "$shots/$name.out"; then rc=1; fi
}

# scenarios that show a window: a shot is taken when the probe says it is ready
shoot() {  # shoot <scenario> <shot name> [env...]
  local name=$1 shot=$2; shift 2
  echo "== progress_probe $name ($shot)"
  env "$@" wine "$exe" "$name" > "$shots/$shot.out" 2>&1 &
  local pid=$!
  for _ in $(seq 1 120); do grep -q "SHOT-READY" "$shots/$shot.out" 2>/dev/null && break; sleep 0.1; done
  import -window root "$shots/$shot.png" 2>/dev/null || echo "(no screenshot tool: skipped)"
  wait $pid || rc=1
  grep -E "^PROBE" "$shots/$shot.out" | grep -v "SHOT-READY" || true
  if grep -q "PROBE FAIL" "$shots/$shot.out"; then rc=1; fi
}

shoot show bar KILN_PROGRESS_DELAY_MS=500
shoot nobar marquee KILN_PROGRESS_DELAY_MS=500
shoot show fullscreen KILN_PROGRESS_DELAY_MS=500 KILN_PROGRESS_FULLSCREEN=1
shoot show gamescope KILN_PROGRESS_DELAY_MS=500 GAMESCOPE_WAYLAND_DISPLAY=gamescope-0
run quick KILN_PROGRESS_DELAY_MS=2000
run forget KILN_PROGRESS_DELAY_MS=300
run disabled KILN_UI_DISABLE=1
run server KILN_PROGRESS_DELAY_MS=100
# no desktop at all: no DISPLAY, the Wayland driver is off: Wine runs without a display
echo "== progress_probe nowindow (no display)"
env -u DISPLAY KILN_PROGRESS_DELAY_MS=100 wine "$exe" nowindow > "$shots/nowindow.out" 2>&1 || rc=1
grep -E "^PROBE" "$shots/nowindow.out" || true
if grep -q "PROBE FAIL" "$shots/nowindow.out"; then rc=1; fi
exit $rc
