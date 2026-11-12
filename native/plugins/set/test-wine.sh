#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Builds tests/wine_watch_test.cpp with clang-cl against the xwin SDK and runs it under Wine (no display).
#   KILN_OUT=<build dir> native/plugins/set/test-wine.sh      (KILN_WINE: wine binary, default GE-Proton's or `wine`)
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../build}/set-wine
xwin=${XWIN_ROOT:-$HOME/.cache/cargo-xwin/xwin}
mkdir -p "$out"
inc=()
for d in crt/include sdk/include/ucrt sdk/include/um sdk/include/shared; do inc+=(/imsvc "$xwin/$d"); done
compile=(clang-cl --target=x86_64-pc-windows-msvc /nologo /std:c++20 /O2 /EHsc /MT /DNOMINMAX /DWIN32_LEAN_AND_MEAN "${inc[@]}")
link=(-fuse-ld=lld-link /link /libpath:"$xwin/crt/lib/x86_64" /libpath:"$xwin/sdk/lib/um/x86_64" /libpath:"$xwin/sdk/lib/ucrt/x86_64")
"${compile[@]}" "$here/tests/wine_watch_test.cpp" /Fo"$out/wine_watch_test.obj" /Fe"$out/wine_watch_test.exe" "${link[@]}" >/dev/null
"${compile[@]}" /LD /DKSET_WATCH_DLL "$here/tests/wine_watch_test.cpp" /Fo"$out/watch-fixture.obj" /Fe"$out/watch-fixture.dll" "${link[@]}" >/dev/null
"${compile[@]}" /LD /DKSET_SILENT_DLL "$here/tests/wine_watch_test.cpp" /Fo"$out/silent-loader.obj" /Fe"$out/silent-loader.dll" "${link[@]}" >/dev/null
# Issue 23: two powrprof.dll files in folders that are not System32: a forwarding proxy loader and one with SET's markers.
mkdir -p "$out/forward" "$out/set"
"${compile[@]}" /LD /DKSET_POWER_DLL=1 "$here/tests/wine_watch_test.cpp" /Fo"$out/forward/powrprof.obj" /Fe"$out/forward/powrprof.dll" "${link[@]}" >/dev/null
"${compile[@]}" /LD /DKSET_POWER_DLL=2 "$here/tests/wine_watch_test.cpp" /Fo"$out/set/powrprof.obj" /Fe"$out/set/powrprof.dll" "${link[@]}" >/dev/null
# The rule field write guard and stack check (hooks.cpp), with the plugin's /EHa (native/CMakeLists.txt says why).
"${compile[@]}" /EHa /I"$here/src" /I"$here/../../core/abi" "$here/tests/wine_rules_test.cpp" "$here/src/rules.cpp" \
  /Fo"$out/" /Fe"$out/wine_rules_test.exe" "${link[@]}" >/dev/null
ge=$HOME/.local/share/Steam/compatibilitytools.d/GE-Proton11-11-x86_64/files/bin/wine
wine=${KILN_WINE:-$([[ -x $ge ]] && echo "$ge" || command -v wine)}
# powrprof=n,b: the test's own powrprof.dll files are mapped from their folders (Wine's builtin otherwise)
export WINEPREFIX=$out/pfx WINEDEBUG=-all WINEDLLOVERRIDES="mscoree=;mshtml=;winedbg.exe=d;powrprof=n,b"
# no window may reach the desktop: an unset WAYLAND_DISPLAY means the desktop's wayland-0
unset DISPLAY; export WAYLAND_DISPLAY=kiln-no-display
mkdir -p "$WINEPREFIX"
"$wine" "$out/wine_watch_test.exe" 2>"$out/wine.err" | tr -d '\r'
"$wine" "$out/wine_rules_test.exe" 2>"$out/wine-rules.err" | tr -d '\r'
