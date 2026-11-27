#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# tools/registry-compat/session.sh <lab name> <out dir>: the registry stage's before/after run on one deployed lab (registry P1
# S12). Run from the root of a worktree, with Kiln deployed into the lab and the mods registry-compat-test and
# registry-mixed-test in its kiln/mods. Two boots:
#   old   the old paths alone: registry-mixed-test's content/registry.json set aside and <data>/registry-mixed-test/old-only
#         made (no C# registry edit): its old data pack and old boot patch only;
#   full  the same with the registry section and the C# edit; registry-mixed-test hot-reloads its packs three times
#         (section edited, removed, restored), registry-compat-test dumps at the world load and after each reload.
# dev.32: the older mod's pack also edits a creature template, an arsenal and a perk through the old gamedata sections, and the
# section edits them through the creatures, arsenals and perks registries (the fields allowed in the old-vs-full diff).
# Then compat.py: every resource of the registries' types is byte-identical to the game's archive (a Kiln-off boot) except
# keen::BalancingTable (the old patch and the registry's boot writes); old vs full differ only in the fields the registry
# edits; the dump after the section's removal equals the old-only one but for the C# edit (no residue); the restored one
# equals the first. KILN_LABSTART: the start command (default tools/lab/lab.sh start).
set -uo pipefail
LAB=${1:?usage: session.sh <lab name> <out dir>}
OUTD=${2:?usage: session.sh <lab name> <out dir>}
L=${KILN_LABS:-$HOME/srv/enshrouded/labs}/$LAB
START=${KILN_LABSTART:-tools/lab/lab.sh start}
C=tools/registry-compat/compat.py
BAL=82706b40-61b1-4b8f-8b23-dcec6971bda1
M=$L/kiln/mods/registry-mixed-test
D=$L/kiln/data
log=$L/kiln/logs/kiln.log
mkdir -p "$OUTD"
say() { echo "[$(date +%T)] $*" | tee -a "$OUTD/session.txt"; }
[[ -d $M && -d $L/kiln/mods/registry-compat-test ]] || { say "the lab has no registry-mixed-test / registry-compat-test"; exit 2; }

boot() {  # boot <name>
  local name=$1 t0=$SECONDS
  rm -f "$log" "$L/kiln/state/registry.json"
  rm -rf "$D/registry-compat-test"
  $START "$LAB" > "$OUTD/start-$name.log" 2>&1
  say "$name: started (rc $?)"
  for _ in $(seq 1 300); do /usr/bin/grep -q "registry-mixed-test: DONE" "$log" 2>/dev/null && break; sleep 3; done
  sleep 8
  say "$name: $(/usr/bin/grep -h "registry-mixed-test: DONE" "$log" 2>/dev/null | tail -1 | cut -c1-200) after $((SECONDS - t0)) s"
  cp "$L/kiln/state/registry.json" "$OUTD/registry-$name.json" 2>/dev/null
  tools/lab/lab.sh stop "$LAB" > "$OUTD/stop-$name.log" 2>&1
  cp "$log" "$OUTD/kiln-$name.log" 2>/dev/null
  rm -rf "$OUTD/$name"; cp -a "$D/registry-compat-test" "$OUTD/$name" 2>/dev/null
  /usr/bin/grep -hE "registry-(mixed|compat)-test:|registry( \(live\))?:" "$OUTD/kiln-$name.log" | cut -c1-400 > "$OUTD/lines-$name.txt"
}

tools/lab/lab.sh stop "$LAB" > /dev/null 2>&1
cp "$M/content/registry.json" "$OUTD/section.keep"
rm -f "$M/content/registry.json"; mkdir -p "$D/registry-mixed-test"; : > "$D/registry-mixed-test/old-only"
boot old
cp "$OUTD/section.keep" "$M/content/registry.json"; rm -f "$D/registry-mixed-test/old-only"
boot full

rc=0
step() { say "$*"; "$@" >> "$OUTD/session.txt" 2>&1 || { rc=1; say "  FAILED"; }; }
F1=$OUTD/full/dump-1.json; O1=$OUTD/old/dump-1.json
exe=$L/enshrouded_server.exe
step python3 -I "$C" archive --exe "$exe" --dump "$F1" --out "$OUTD/archive.json"
step python3 -I "$C" check --dump "$F1" --archive "$OUTD/archive.json" --allow "$BAL"
step python3 -I "$C" check --dump "$O1" --archive "$OUTD/archive.json" --allow "$BAL"
step python3 -I "$C" diff --before "$O1" --after "$F1" --allow "$BAL" \
  --allow-field items:Material_T1_Wood_Logs.maxStackSize --allow-field items:Material_T1_Plant_Fibre.maxStackSize \
  --allow-field recipes:Material_T1_Factory_String_SpinningWheel.craftingDuration \
  --allow-field templates:Enemy_Vukah_Melee.Enemy01BehaviorState.combatCommitmentSetup.view.range \
  --allow-field arsenals:e30312e7-9749-4329-8bfd-469a295747b5.attacks[0].description.maxRange \
  --allow-field perks:Perk_Armor_Fog.perkArmorSetup.distribution.fog \
  --allow-field skills:0x4475e8dd.costs
step python3 -I "$C" diff --before "$O1" --after "$OUTD/full/dump-3.json" --allow "$BAL" --allow-field items:Material_T1_Plant_Fibre.maxStackSize
step python3 -I "$C" diff --before "$F1" --after "$OUTD/full/dump-4.json"
say "session over: $([[ $rc == 0 ]] && echo PASS || echo FAIL)"
exit $rc
