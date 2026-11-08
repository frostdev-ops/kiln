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
clang-cl --target=x86_64-pc-windows-msvc /nologo /std:c++20 /O2 /EHsc /MT /DNOMINMAX /DWIN32_LEAN_AND_MEAN "${inc[@]}" \
  "$here/tests/wine_watch_test.cpp" /Fo"$out/wine_watch_test.obj" /Fe"$out/wine_watch_test.exe" \
  -fuse-ld=lld-link /link /libpath:"$xwin/crt/lib/x86_64" /libpath:"$xwin/sdk/lib/um/x86_64" \
  /libpath:"$xwin/sdk/lib/ucrt/x86_64" >/dev/null
ge=$HOME/.local/share/Steam/compatibilitytools.d/GE-Proton11-11-x86_64/files/bin/wine
wine=${KILN_WINE:-$([[ -x $ge ]] && echo "$ge" || command -v wine)}
export WINEPREFIX=$out/pfx WINEDEBUG=-all WINEDLLOVERRIDES="mscoree=;mshtml=;winedbg.exe=d"
# no window may reach the desktop: an unset WAYLAND_DISPLAY means the desktop's wayland-0
unset DISPLAY; export WAYLAND_DISPLAY=kiln-no-display
mkdir -p "$WINEPREFIX"
"$wine" "$out/wine_watch_test.exe" 2>"$out/wine.err" | tr -d '\r'
