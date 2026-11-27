#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln.content: resource patches and Kiln's base rules (bootpatch_test.cpp, baserules_test.cpp, host); the game's own texts (text_test.cpp, host: the loca hook, text() and text_info() over a fake lookup); content transactions (txn_test.cpp, host: store.cpp over a made-up memory with failing writes); the hook on the
# game's item lookup only when it is needed (itemfind_test.cpp, host: newcontent.cpp over a fake kiln.hooks table); a late
# kitbash reference written only by the world that has the clone, a hosted server world's clone only in server Pre
# (lateref_test.cpp, host, and under ThreadSanitizer); a mod's data packs following its load: a reload re-applies them, a
# reload whose new files do not all read keeps them whole (packreload_test.cpp, host: content.cpp over the real packs.cpp and
# store.cpp), and a pack that throws partway leaves nothing of its mod behind (packundo_test.cpp: newcontent.cpp's pack
# journal; gdundo_test.cpp: gamedata's clones, config adds and other services' sections); a drop table patched before any caller walks it, whichever thread
# acquires it first (droptable_test.cpp, host: content.cpp's hook body, and under ThreadSanitizer); a sequence clone's names in a later world of the process (seqnames_test.cpp, host); the buff room (buffroom_test.cpp, host: the 4,096 reservation, its fallback, checked sizes, the map the game reads); near memory
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
bash "$here/baserules_test.sh"
bash "$here/bootpatch_equiv_test.sh"
bash "$here/bootstruct_test.sh"
bash "$here/bootview_test.sh"
# resource patches in a kiln-backend start (the committed archive generation)
bash "$here/bootview_eml_test.sh"
bash "$here/refs_test.sh"
echo "== txn-test (host)"
bash "$here/txn_test.sh"
echo "== seqnames-test (host)"
bash "$here/seqnames_test.sh"
bash "$here/seqworld_test.sh"
bash "$here/buffroom_test.sh"
bash "$here/walk_test.sh"
bash "$here/itemtag_test.sh"
bash "$here/pending_test.sh"
bash "$here/apply_report_test.sh"
bash "$here/itemfind_test.sh"
bash "$here/lateref_test.sh"
bash "$here/droptable_test.sh"
bash "$here/packreload_test.sh"
bash "$here/packundo_test.sh"
bash "$here/gdundo_test.sh"
bash "$here/menugroup_test.sh"
bash "$here/text_test.sh"
bash "$here/bootpack_test.sh"
bash "$here/menulayout_test.sh"
echo "== gear-test (host)"
bash "$here/gear_test.sh"
bash "$here/registry_claims_test.sh"
bash "$here/registry_compose_test.sh"
bash "$here/registry_schema_test.sh"
bash "$here/registry_generated_test.sh"
bash "$here/registry_regmap_test.sh"
bash "$here/registry_formulas_test.sh"
bash "$here/registry_raw_test.sh"
bash "$here/registry_review_test.sh"
bash "$here/registry_review2_test.sh"
bash "$here/registry_ir_test.sh"
bash "$here/registry_refs_test.sh"
bash "$here/registry_report_test.sh"
bash "$here/registry_saves_test.sh"
bash "$here/registry_writers_test.sh"
bash "$here/registry_model_test.sh"
bash "$here/registry_stage_test.sh"
bash "$here/registry_store_test.sh"
bash "$here/registry_live_test.sh"
bash "$here/registry_live_transactions_test.sh"
bash "$here/registry_live_reauthor_test.sh"
bash "$here/registry_live_reauthor_cost_test.sh"
bash "$here/registry_stage_review_test.sh"
bash "$here/registry_live_review_test.sh"
echo "== recipe-water-test (host)"
bash "$here/recipe_water_test.sh"
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
