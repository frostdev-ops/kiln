#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln.items host tests under plain Wine, no game: the item-instance stamps and reads (instances_test.cpp), the gear
# options (pide_test.cpp), the equip logic (equip_test.cpp), the forge actions' logic (gems_test.cpp), the station actions' logic (stations_test.cpp), the ingredient rows' count hook (stock_ui_native_test.cpp), the gear writes' logic (gear_test.cpp), the container renames' text check and steps against a fake string table (rename_test.cpp), the equipment-set snapshot, store, planner, executor and request order (setsnap_test.cpp, setplan_test.cpp, setexec_test.cpp, setorder_test.cpp) and the moves, ledger, transfer
# rules, handlers, reports and hook chaining against a fake inventory world (move_test.cpp). What they prove is the
# service's own logic; the game's behaviour is the lab test mods/tests/items-test.
#   KILN_OUT=<build dir> native/services/items/tests/run.sh
# Exit code 0 = every check passed. The hook chain runs in two more processes (hooks are never removed).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
export KILN_OUT=${KILN_OUT:-$here/../../../build}
"$here/../../../build.sh" items-move-test items-pide-test items-results-test items-instances-test items-equip-test items-setsnap-test items-setplan-test items-setexec-test items-setorder-test items-wear-test items-toolwear-test items-toolwear-table-test items-gems-test items-stations-test items-gear-test items-rename-test items-craft-sources-test items-craft-payment-test items-lock-sources-test items-respec-sources-test items-build-sources-test items-item-fill-test items-craft-preview-test items-stock-ui-test items-dye-sources-test items-dyes-native-test items-storage-native-test items-client-stations-native-test >/dev/null
bin=$KILN_OUT/cmake/bin/items-tests
export WINEPREFIX=$KILN_OUT/items-tests-pfx WINEDEBUG=-all WINEDLLOVERRIDES="mscoree=;mshtml=;winedbg.exe=d"
# no window may reach the desktop: an unset WAYLAND_DISPLAY means the desktop's wayland-0
unset DISPLAY; export WAYLAND_DISPLAY=kiln-no-display
mkdir -p "$WINEPREFIX"
[[ -f $WINEPREFIX/system.reg ]] || wine wineboot -i >/dev/null 2>&1
fail=0
for run in "items-client-stations-native-test.exe" "items-storage-native-test.exe" "items-move-test.exe storage" "items-rename-test.exe" "items-gear-test.exe" "items-stations-test.exe" "items-craft-sources-test.exe" "items-craft-payment-test.exe" "items-lock-sources-test.exe" "items-respec-sources-test.exe" "items-build-sources-test.exe" "items-item-fill-test.exe" "items-craft-preview-test.exe" "items-stock-ui-test.exe" "items-dye-sources-test.exe" "items-dyes-native-test.exe" "items-gems-test.exe" "items-wear-test.exe" "items-toolwear-test.exe" "items-toolwear-table-test.exe" "items-results-test.exe" "items-equip-test.exe" "items-setsnap-test.exe" "items-setplan-test.exe" "items-setexec-test.exe" "items-setorder-test.exe" "items-instances-test.exe" "items-pide-test.exe" "items-move-test.exe" "items-move-test.exe chain-before" "items-move-test.exe chain-after" "items-move-test.exe partial-install" "items-move-test.exe links" "items-move-test.exe review" "items-move-test.exe move-off" "items-move-test.exe stamps" "items-move-test.exe replace" "items-move-test.exe give-equip"; do
  echo "== $run"
  # shellcheck disable=SC2086
  wine "$bin/"$run || fail=1
done
# kiln.items.inventory_kinds' pure part (inventory_kinds_logic.h, GitHub issue #138) on the host (no Wine, no game)
echo "== inventory_kinds_test (host)"
g++ -std=c++20 -Wall -Wextra -I"$here/../../../core/abi" "$here/inventory_kinds_test.cpp" -o "$KILN_OUT/inventory_kinds_test" &&
  "$KILN_OUT/inventory_kinds_test" || fail=1
# the approved builds of kiln.toolwear against the program catalog (no Wine, no game)
echo "== wear_catalog_test.py"
python3 -I "$here/wear_catalog_test.py" || fail=1
# the player storage sigdb rows on read-only exe copies (KILN_SERVER_EXE / KILN_CLIENT_EXE, read as files, never run);
# a side without a copy of the qualified build is skipped with a message
echo "== storage_sigdb_test.py"
python3 -I "$here/storage_sigdb_test.py" || fail=1
# kiln.stations.client's pure part (client_stations_logic.h, 0.2.0-dev.31.13) on the host (no Wine, no game)
echo "== client_stations_test (host)"
g++ -std=c++20 -Wall -Wextra -I"$here/../../../core/abi" "$here/client_stations_test.cpp" -o "$KILN_OUT/client_stations_test" &&
  "$KILN_OUT/client_stations_test" || fail=1
echo "== client_stations_sigdb_test.py"
if [[ -n ${KILN_CLIENT_EXE:-} && -n ${KILN_SERVER_EXE:-} ]]; then
  python3 -I "$here/client_stations_sigdb_test.py" --client "$KILN_CLIENT_EXE" --server "$KILN_SERVER_EXE" || fail=1
else
  echo "SKIP client station view signatures: supply KILN_CLIENT_EXE and KILN_SERVER_EXE static copies"
fi
echo "== craft_preview_sigdb_test.py"
if [[ -n ${KILN_CLIENT_EXE:-} && -n ${KILN_SERVER_EXE:-} ]]; then
  python3 -I "$here/craft_preview_sigdb_test.py" --client "$KILN_CLIENT_EXE" --server "$KILN_SERVER_EXE" || fail=1
else
  echo "SKIP craft preview signatures: supply KILN_CLIENT_EXE and KILN_SERVER_EXE static copies"
fi
echo "== build_sources_sigdb_test.py"
if [[ -n ${KILN_CLIENT_EXE:-} && -n ${KILN_SERVER_EXE:-} ]]; then
  python3 -I "$here/build_sources_sigdb_test.py" --client "$KILN_CLIENT_EXE" --server "$KILN_SERVER_EXE" || fail=1
else
  echo "SKIP build sources signatures: supply KILN_CLIENT_EXE and KILN_SERVER_EXE static copies"
fi
echo "== lock_sources_sigdb_test.py"
if [[ -n ${KILN_CLIENT_EXE:-} && -n ${KILN_SERVER_EXE:-} ]]; then
  python3 -I "$here/lock_sources_sigdb_test.py" --client "$KILN_CLIENT_EXE" --server "$KILN_SERVER_EXE" || fail=1
else
  echo "SKIP lock sources signatures: supply KILN_CLIENT_EXE and KILN_SERVER_EXE static copies"
fi
echo "== respec_sources_sigdb_test.py"
if [[ -n ${KILN_CLIENT_EXE:-} && -n ${KILN_SERVER_EXE:-} ]]; then
  python3 -I "$here/respec_sources_sigdb_test.py" --client "$KILN_CLIENT_EXE" --server "$KILN_SERVER_EXE" || fail=1
else
  echo "SKIP respec sources signatures: supply KILN_CLIENT_EXE and KILN_SERVER_EXE static copies"
fi
echo "== item_fill_sigdb_test.py"
if [[ -n ${KILN_CLIENT_EXE:-} && -n ${KILN_SERVER_EXE:-} ]]; then
  python3 -I "$here/item_fill_sigdb_test.py" --client "$KILN_CLIENT_EXE" --server "$KILN_SERVER_EXE" || fail=1
else
  echo "SKIP item fill signatures: supply KILN_CLIENT_EXE and KILN_SERVER_EXE static copies"
fi
echo "== give_equip_instance_ammo_sigdb_test.py"
if [[ -n ${KILN_CLIENT_EXE:-} && -n ${KILN_SERVER_EXE:-} ]]; then
  python3 -I "$here/give_equip_instance_ammo_sigdb_test.py" --client "$KILN_CLIENT_EXE" --server "$KILN_SERVER_EXE" || fail=1
else
  echo "SKIP GiveEquip instance-backed ammo signatures: supply KILN_CLIENT_EXE and KILN_SERVER_EXE static copies"
fi
exit $fail
