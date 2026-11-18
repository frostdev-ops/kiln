#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln.content: the game's own texts (text_test.cpp, host: the loca hook, text() and text_info() over a fake lookup); content transactions (txn_test.cpp, host: store.cpp over a made-up memory with failing writes); the hook on the
# game's item lookup only when it is needed (itemfind_test.cpp, host: newcontent.cpp over a fake kiln.hooks table); a sequence clone's names in a later world of the process (seqnames_test.cpp, host); the buff room (buffroom_test.cpp, host: the 4,096 reservation, its fallback, checked sizes, the map the game reads); near memory
# (nearmem.cpp): the policy against simulated address spaces on the host (near_test.cpp: the layout of GitHub issue 14,
# the Wine layout, no free hole, budget, top and bottom of the address space), then the Windows side (nearwin.h) on a
# real fragmented address space under Wine, no game, with the walk's timing (near_win_test.cpp).
# Exit code 0 = every check passed.
#   KILN_OUT=<build dir> native/services/content/tests/run.sh
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:=$here/../../../build}"
export KILN_OUT
out=$KILN_OUT/content-tests
mkdir -p "$out"
bash "$here/bootpatch_test.sh"
bash "$here/bootstruct_test.sh"
bash "$here/refs_test.sh"
echo "== txn-test (host)"
bash "$here/txn_test.sh"
echo "== seqnames-test (host)"
bash "$here/seqnames_test.sh"
bash "$here/seqworld_test.sh"
bash "$here/buffroom_test.sh"
bash "$here/pending_test.sh"
bash "$here/apply_report_test.sh"
bash "$here/itemfind_test.sh"
bash "$here/text_test.sh"
echo "== near-test (host)"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/near_test.cpp" "$here/../nearmem.cpp" -o "$out/near-test"
"$out/near-test"
echo "== near-win-test (Wine)"
"$here/../../../build.sh" near-win-test pending-win-test >/dev/null
export WINEPREFIX=$KILN_OUT/content-tests-pfx WINEDEBUG=-all WINEDLLOVERRIDES="mscoree=;mshtml=;winedbg.exe=d"
# no window may reach the desktop: an unset WAYLAND_DISPLAY means the desktop's wayland-0
unset DISPLAY; export WAYLAND_DISPLAY=kiln-no-display
mkdir -p "$WINEPREFIX"
[[ -f $WINEPREFIX/system.reg ]] || wine wineboot -i >/dev/null 2>&1
wine "$KILN_OUT/cmake/bin/content-tests/near-win-test.exe"

echo "== pending-win-test (Wine)"
wine "$KILN_OUT/cmake/bin/content-tests/pending-win-test.exe"
