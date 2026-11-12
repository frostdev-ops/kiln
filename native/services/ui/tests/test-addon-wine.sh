#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Optional integration test with an operator-supplied, unmodified shroudlink 0.8.0 client DLL. No game or display.
# SHROUDLINK_ADDON=/path/to/client/dinput8.dll KILN_OUT=/build native/services/ui/tests/test-addon-wine.sh
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../../../.." && pwd)
: "${KILN_OUT:?set KILN_OUT to an isolated build directory}"
: "${SHROUDLINK_ADDON:?set SHROUDLINK_ADDON to the existing client addon DLL}"
[[ -f $SHROUDLINK_ADDON ]]
export CMAKE_BUILD_PARALLEL_LEVEL=${CMAKE_BUILD_PARALLEL_LEVEL:-2}
out=$KILN_OUT/addon-wine
mkdir -p "$out"
cmake -S "$repo/native" -B "$KILN_OUT/cmake" -G Ninja -DCMAKE_TOOLCHAIN_FILE="$repo/native/cmake/clang-cl-xwin.cmake" -DCMAKE_BUILD_TYPE=Release
cmake --build "$KILN_OUT/cmake" --target ui-addon-harness ui-addon-inert ui-addon-forwarded
# A fresh directory for every run: old addon logs, core DLLs and addon-created state cannot affect another test.
run=$(mktemp -d "$out/run-XXXXXX")
mkdir "$run/input"
cp -- "$SHROUDLINK_ADDON" "$run/input/dinput8.dll"
sha256sum "$SHROUDLINK_ADDON" "$run/input/dinput8.dll" > "$run/addon.sha256"
export WINEPREFIX=$out/pfx WINEDEBUG=-all WINEDLLOVERRIDES="winedbg.exe=d"
unset DISPLAY
export WAYLAND_DISPLAY=kiln-no-display
wine=${KILN_WINE:-wine}
# No archive contents are a working directory or script path. Only this test's own synthetic exe runs.
modes=(race wait window window-late tabs tabs-late timeout unrelated)
# Opt-in only for a privately prepared cooperating source build. Stock remains a negative API check.
if [[ ${SHROUDLINK_BRIDGE_TEST:-0} == 1 ]]; then modes+=(bridge bridge-late); else modes+=(bridge-stock); fi
for mode in "${modes[@]}"; do
    mkdir "$run/$mode"
    cp "$KILN_OUT/cmake/bin/ui-addon-harness.exe" "$run/$mode/"
    dll=$run/input/dinput8.dll
    if [[ $mode == timeout ]]; then dll=$KILN_OUT/cmake/bin/ui-addon-inert.dll; fi
    if [[ $mode == unrelated ]]; then dll=$KILN_OUT/cmake/bin/ui-addon-forwarded.dll; fi
    timeout 60s "$wine" "$run/$mode/ui-addon-harness.exe" "Z:$dll" "$mode" \
        > "$run/$mode/stdout.log" 2> "$run/$mode/wine.err" || { printf 'FAIL %s (see %s)\n' "$mode" "$run/$mode"; exit 1; }
    tr -d '\r' < "$run/$mode/stdout.log"
done
printf 'addon Wine evidence: %s\n' "$run"
