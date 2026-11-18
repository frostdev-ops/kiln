#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Headless Wine run of gate-priority-host.exe: the mod's real DLL, loaded with LoadLibrary, against the fake Kiln of
# tests/harness.h, whose compare-exchange writes run over kiln.engine's real Windows primitives (the SEH copies and
# writes, the Interlocked words, SetThreadPriority for the raise). Every scenario of tests/scenarios.h, one process each.
# Build first: native/build.sh gate-priority-test gate-priority-host. Needs a private absolute WINEPREFIX; never a game
# install or a lab prefix.
set -euo pipefail
: "${KILN_OUT:?set KILN_OUT to the build output}"
: "${WINEPREFIX:?use a private absolute Wine prefix}"
[[ $WINEPREFIX == /* ]] || { printf 'WINEPREFIX must be absolute\n' >&2; exit 2; }
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../../.." && pwd)
host=$KILN_OUT/cmake/bin/tests/gate-priority-host.exe
dll=$KILN_OUT/cmake/bin/mods/gate-priority-test/gate-priority-test.dll
[[ -f $host && -f $dll ]] || { printf 'Build gate-priority-test and gate-priority-host first\n' >&2; exit 2; }
unset DISPLAY
export WAYLAND_DISPLAY=kiln-no-display WINEDLLOVERRIDES=winedbg.exe=d WINEDEBUG=-all
win() { printf 'Z:%s' "${1//\//\\}"; }
scenarios=(write raise-off noop named named-offset named-field named-gap tracked no-padding other-world no-world no-file bad-config stop stop-warmup uncertain-change uncertain-restore)
log=$KILN_OUT/gate-priority-test-wine
mkdir -p "$log"
result=0
for s in "${scenarios[@]}"; do
  if timeout 120s wine "$host" "$s" "$(win "$root/sigdb/types")" "$(win "$dll")" > "$log/$s.log" 2>&1; then
    printf 'gate-priority-test [wine] %s\n' "$(grep -a -E '^[a-z-]+: [0-9]+ checks' "$log/$s.log" | tail -1)"
  else
    grep -a -E '^FAIL' "$log/$s.log" | head -20 || true
    printf 'gate-priority-test [wine] %s: FAILED (log: %s)\n' "$s" "$log/$s.log"
    result=1
  fi
done
wineserver -w
exit "$result"
