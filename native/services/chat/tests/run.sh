#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln.chat and kiln-emf's client hooks layered on one function (ChatLogAdd) in the real kiln.hooks v2 registry engine,
# under plain Wine, no game (chat_layers_test.cpp). Three processes, one per install order: kiln.chat first, kiln-emf first,
# and an EMF mod's hook in front of both. Exit code 0 = every check passed.
#   KILN_OUT=<build dir> native/services/chat/tests/run.sh
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
export KILN_OUT=${KILN_OUT:-$here/../../../build}
"$here/../../../build.sh" chat-layers-test >/dev/null
exe=$KILN_OUT/cmake/bin/chat-tests/chat-layers-test.exe
export WINEPREFIX=$KILN_OUT/chat-tests-pfx WINEDEBUG=-all WINEDLLOVERRIDES="mscoree=;mshtml=;winedbg.exe=d"
# no window may reach the desktop: an unset WAYLAND_DISPLAY means the desktop's wayland-0
unset DISPLAY; export WAYLAND_DISPLAY=kiln-no-display
mkdir -p "$WINEPREFIX"
[[ -f $WINEPREFIX/system.reg ]] || wine wineboot -i >/dev/null 2>&1
rc=0
for mode in chat-first emf-first front; do
  echo "== chat-layers-test ($mode)"
  wine "$exe" "$mode" || rc=1
done
exit $rc
