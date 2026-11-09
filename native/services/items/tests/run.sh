#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln.items host tests under plain Wine, no game: the gear options (pide_test.cpp) and the moves, ledger, transfer
# rules, handlers, reports and hook chaining against a fake inventory world (move_test.cpp). What they prove is the
# service's own logic; the game's behaviour is the lab test mods/tests/items-test.
#   KILN_OUT=<build dir> native/services/items/tests/run.sh
# Exit code 0 = every check passed. The hook chain runs in two more processes (hooks are never removed).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
export KILN_OUT=${KILN_OUT:-$here/../../../build}
"$here/../../../build.sh" items-move-test items-pide-test items-results-test >/dev/null
bin=$KILN_OUT/cmake/bin/items-tests
export WINEPREFIX=$KILN_OUT/items-tests-pfx WINEDEBUG=-all WINEDLLOVERRIDES="mscoree=;mshtml=;winedbg.exe=d"
# no window may reach the desktop: an unset WAYLAND_DISPLAY means the desktop's wayland-0
unset DISPLAY; export WAYLAND_DISPLAY=kiln-no-display
mkdir -p "$WINEPREFIX"
[[ -f $WINEPREFIX/system.reg ]] || wine wineboot -i >/dev/null 2>&1
fail=0
for run in "items-results-test.exe" "items-pide-test.exe" "items-move-test.exe" "items-move-test.exe chain-before" "items-move-test.exe chain-after" "items-move-test.exe partial-install" "items-move-test.exe review"; do
  echo "== $run"
  # shellcheck disable=SC2086
  wine "$bin/"$run || fail=1
done
exit $fail
