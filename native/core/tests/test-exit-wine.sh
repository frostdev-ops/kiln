#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# The exit sequence on the Windows code path under headless Wine: api.cpp's exit block and boot.cpp's exit trigger,
# cut out of those files as they are, over the real task pool (Windows platform, SEH shim), built with clang-cl against
# the xwin SDK and run until its real ExitProcess (exit_windows_host.cpp). In flight at the exit: an active task, a
# task that ignores its cancel, a queued task, a suspended managed task and a pending tick hop. Checks: exit code 0,
# the process gone within 10 s and its prefix's wineserver idle (no hung process), Kiln's own steps in order (admission,
# seal, Kiln's service handler, the park naming the stuck task, then the plugin handler), and nothing started after the
# park. No game, no CMake. Build and run:
#   native/core/tests/test-exit-wine.sh
# Output: $KILN_OUT/exit-wine when KILN_OUT is set, else a private temporary directory removed on exit.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
src=$here/../src
abi=$here/../abi
if [[ -n ${KILN_OUT:-} ]]; then
  out=$KILN_OUT/exit-wine
  mkdir -p "$out"
else
  out=$(mktemp -d "${TMPDIR:-/tmp}/kiln-exit-wine.XXXXXX")
  trap 'rm -rf "$out"' EXIT
fi
xwin=${XWIN_ROOT:-$HOME/.cache/cargo-xwin/xwin}
[[ -d $xwin/crt/include ]] || { printf 'No xwin SDK under %s; run `cargo xwin env` once\n' "$xwin" >&2; exit 2; }
inc=(-imsvc "$xwin/crt/include" -imsvc "$xwin/sdk/include/ucrt" -imsvc "$xwin/sdk/include/um" -imsvc "$xwin/sdk/include/shared")
libdirs=(-libpath:"$xwin/crt/lib/x86_64" -libpath:"$xwin/sdk/lib/um/x86_64" -libpath:"$xwin/sdk/lib/ucrt/x86_64")
cl=(clang-cl --target=x86_64-pc-windows-msvc /std:c++20 /EHsc /W4 /WX /O1 /MT -Wno-msvc-not-found -fuse-ld=lld "${inc[@]}"
    -I"$out" -I"$src" -I"$abi" -I"$here")

# the exit block and the trigger, as they are in the sources (the same cuts as test-unload-sequence.sh)
python3 -I - "$src/api.cpp" "$src/boot.cpp" "$out" <<'PY'
import pathlib, sys
api, boot = (pathlib.Path(p).read_text() for p in sys.argv[1:3])
out = pathlib.Path(sys.argv[3])
start = api.index('namespace {', api.index('// ---- on_unload at process exit'))
end = api.rindex('}  // namespace kiln')
(out / 'exit_sequence.inc').write_text(api[start:end])
start = boot.index('void* g_exit_next =')
end = boot.index('// ---- on_unload: is the exit link armed?', start)
(out / 'exit_trigger.inc').write_text(boot[start:end])
PY
"${cl[@]}" -Wno-unused-function -Wno-unused-parameter "$here/exit_windows_host.cpp" "$src/tasks.cpp" "$src/tasks_model.cpp" \
  "$src/tasks_stats.cpp" "$src/tasks_win.cpp" "$src/tasks_seh.cpp" /Fe:"$out/exit_windows_host.exe" /link "${libdirs[@]}" >/dev/null

# Always this run's own private prefix under the output folder: an inherited WINEPREFIX (it could be a game's prefix)
# is ignored, and the waits below only ever concern this prefix's wineserver.
out=$(cd "$out" && pwd)
export WINEPREFIX=$out/pfx
mkdir -p "$WINEPREFIX"
unset DISPLAY
export WAYLAND_DISPLAY=kiln-no-display WINEDLLOVERRIDES=winedbg.exe=d WINEDEBUG=-all
[[ -f $WINEPREFIX/system.reg ]] || timeout 120s wine wineboot -i >/dev/null 2>&1
timeout 60s wineserver -w || true  # the prefix's own boot is not part of the measurement
set +e
t0=$(date +%s%N)
( cd "$out" && timeout 60s wine exit_windows_host.exe ) >"$out/stdout.txt" 2>"$out/stderr.txt"
rc=$?
t1=$(date +%s%N)
timeout 60s wineserver -w
server_rc=$?
set -e
cat "$out/stdout.txt"

fails=0
pass() { printf 'PASS %s\n' "$1"; }
fail() { printf 'FAIL %s\n' "$1"; fails=$((fails + 1)); }
line_of() { grep -n -F -m1 -- "$1" "$out/stdout.txt" | cut -d: -f1; }
ms=$(((t1 - t0) / 1000000))
[[ $rc == 0 ]] && pass "the process exited with code 0" || fail "exit code $rc"
((ms < 10000)) && pass "it was gone within 10 s ($ms ms, the park's bound is 500 ms)" || fail "it took $ms ms"
[[ $server_rc == 0 ]] && pass "the prefix's wineserver went idle: no process left behind" || fail "wineserver -w: $server_rc"
order=()
for step in "step: admission closed" "step: owners sealed" "step: service handler" "step: parked" "step: plugin handler" \
            "log: unload: done in"; do
  order+=("$(line_of "$step")")
done
ok=1
for i in "${!order[@]}"; do
  [[ -n ${order[$i]} ]] || ok=0
  ((i == 0)) || [[ -z ${order[$i]} || -z ${order[$((i - 1))]} ]] || ((order[i] > order[i - 1])) || ok=0
done
((ok)) && pass "Kiln's own steps in order: admission, seal, its service handler, the park, then the plugin handler" ||
  fail "order (lines ${order[*]})"
parked=$(grep -m1 '^step: parked' "$out/stdout.txt" || true)
[[ $parked == *"1 still running"*"mod.stuck/stuck"* ]] && pass "the park named the task that ignores its cancel: $parked" ||
  fail "park line: '${parked:-none}'"
plugin=$(grep -m1 '^step: plugin handler' "$out/stdout.txt" || true)
[[ $plugin == *"started since: 0; segments since: 0;"*"hops: 0; queued ran: 0)"* ]] &&
  pass "after the park nothing started: no body, no done, no resumed segment, no hop, not the queued task" ||
  fail "plugin handler line: '${plugin:-none}'"
grep -q -F 'exit: returned' "$out/stdout.txt" && fail "the trigger returned instead of exiting" || true
printf 'exit wine: %d failure(s)\n' "$fails"
((fails == 0))
