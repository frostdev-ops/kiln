#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# The kiln.tasks pool over the real Windows platform under headless Wine: the model + pool + Windows
# platform + SEH shim cross-built with clang-cl against the xwin SDK, with a test DLL whose exported bodies prove
# module-reference lifetime and fault containment. No game, no CMake. Build and run:
#   native/core/tests/test-tasks-wine.sh
# Output: $KILN_OUT/tasks-wine when KILN_OUT is set, else a private temporary directory removed on exit.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
src=$here/../src
abi=$here/../abi
if [[ -n ${KILN_OUT:-} ]]; then
  out=$KILN_OUT/tasks-wine
  mkdir -p "$out"
else
  out=$(mktemp -d "${TMPDIR:-/tmp}/kiln-tasks-wine.XXXXXX")
  trap 'rm -rf "$out"' EXIT
fi
xwin=${XWIN_ROOT:-$HOME/.cache/cargo-xwin/xwin}
[[ -d $xwin/crt/include ]] || { printf 'No xwin SDK under %s; run `cargo xwin env` once\n' "$xwin" >&2; exit 2; }
inc=(-imsvc "$xwin/crt/include" -imsvc "$xwin/sdk/include/ucrt" -imsvc "$xwin/sdk/include/um" -imsvc "$xwin/sdk/include/shared")
libdirs=(-libpath:"$xwin/crt/lib/x86_64" -libpath:"$xwin/sdk/lib/um/x86_64" -libpath:"$xwin/sdk/lib/ucrt/x86_64")
cl=(clang-cl --target=x86_64-pc-windows-msvc /std:c++20 /EHsc /W4 /WX /O1 /MT -Wno-msvc-not-found -fuse-ld=lld "${inc[@]}" -I"$src" -I"$abi" -I"$here")

# the test module DLL
"${cl[@]}" /LD "$here/tasks_test_module.cpp" /Fe:"$out/tasks_test_module.dll" /link "${libdirs[@]}" >/dev/null
# the host exe: pool + model + Windows platform + SEH shim + the host test
"${cl[@]}" "$here/tasks_windows_host.cpp" "$src/tasks.cpp" "$src/tasks_model.cpp" "$src/tasks_stats.cpp" "$src/tasks_win.cpp" "$src/tasks_seh.cpp" \
  /Fe:"$out/tasks_windows_host.exe" /link "${libdirs[@]}" >/dev/null

# Always this run's own private prefix under the output folder: an inherited WINEPREFIX (it could be a game's prefix)
# is ignored, and the waits below only ever concern this prefix's wineserver.
out=$(cd "$out" && pwd)
export WINEPREFIX=$out/pfx
mkdir -p "$WINEPREFIX"
unset DISPLAY
export WAYLAND_DISPLAY=kiln-no-display WINEDLLOVERRIDES=winedbg.exe=d WINEDEBUG=-all
[[ -f $WINEPREFIX/system.reg ]] || timeout 120s wine wineboot -i >/dev/null 2>&1
set +e
( cd "$out" && timeout 90s wine tasks_windows_host.exe )
rc=$?
timeout 60s wineserver -w || { printf 'wineserver of %s did not exit within 60 s\n' "$WINEPREFIX" >&2; [[ $rc != 0 ]] || rc=1; }
exit $rc
