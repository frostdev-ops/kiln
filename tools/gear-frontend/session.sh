#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# tools/gear-frontend/session.sh <lab name> <out dir>: gear and gearRules against the same edits as registry edits, on one
# deployed lab (dev.32 J2; docs/kiln/services/content.md "Gear rules on the registry"). Run from the root of a worktree, with
# Kiln deployed into the lab and mods/tests/gear-frontend-test in its kiln/mods. Three boots, each to its first dump:
#   patch     content/gear.json (gear + gearRules) with kiln/data/content/registry/front-ends.off: gearRules as resource
#             patches, as before 0.2.0-dev.32;
#   front     the same pack, the front-end on: the registry's boot back-end writes the gearRules;
#   registry  content/gear.json set aside, registry/registry.json in content/ instead: the same edits as registry edits.
# Then compare.py: every item and the balancing table byte-identical across the three; the front boot's registry.json names
# the gearRules file as written by the registry. KILN_LABSTART: the start command (default tools/lab/lab.sh start).
set -uo pipefail
LAB=${1:?usage: session.sh <lab name> <out dir>}
OUTD=${2:?usage: session.sh <lab name> <out dir>}
L=${KILN_LABS:-$HOME/srv/enshrouded/labs}/$LAB
START=${KILN_LABSTART:-tools/lab/lab.sh start}
M=$L/kiln/mods/gear-frontend-test
D=$L/kiln/data
OFF=$D/content/registry/front-ends.off
log=$L/kiln/logs/kiln.log
mkdir -p "$OUTD"
say() { echo "[$(date +%T)] $*" | tee -a "$OUTD/session.txt"; }
[[ -f $M/kiln.mod.json && -f $M/content/gear.json && -f $M/registry/registry.json ]] || { say "the lab has no gear-frontend-test (with content/gear.json and registry/registry.json)"; exit 2; }

boot() {  # boot <name>
  local name=$1 t0=$SECONDS
  rm -f "$log" "$L/kiln/state/registry.json"
  rm -rf "$D/gear-frontend-test"
  $START "$LAB" > "$OUTD/start-$name.log" 2>&1
  say "$name: started (rc $?)"
  # the first dump, or the mod's FAIL line (then no dump comes: stop waiting, the comparison names the missing dump)
  for _ in $(seq 1 300); do /usr/bin/grep -q "gear-frontend-test: \(DUMP 1 \|FAIL\)" "$log" 2>/dev/null && break; sleep 3; done
  sleep 3
  say "$name: $(/usr/bin/grep -h "gear-frontend-test: \(DUMP\|FAIL\)" "$log" 2>/dev/null | tail -1 | cut -c1-200) after $((SECONDS - t0)) s"
  tools/lab/lab.sh stop "$LAB" > "$OUTD/stop-$name.log" 2>&1
  cp "$log" "$OUTD/kiln-$name.log" 2>/dev/null
  cp "$L/kiln/state/registry.json" "$OUTD/registry-$name.json" 2>/dev/null
  cp "$L/kiln/state/boot.json" "$OUTD/boot-$name.json" 2>/dev/null
  cp "$D/gear-frontend-test/dump-1.json" "$OUTD/dump-$name.json" 2>/dev/null
  /usr/bin/grep -haE "gear-frontend-test:|registry: .*(gearRules|front-end)|registry: refused|boot: resource patch|gearRules|data packs" "$OUTD/kiln-$name.log" | cut -c1-400 > "$OUTD/lines-$name.txt"
}

tools/lab/lab.sh stop "$LAB" > /dev/null 2>&1
cp "$M/content/gear.json" "$OUTD/gear.json.keep"
mkdir -p "$(dirname "$OFF")"
: > "$OFF"
boot patch
rm -f "$OFF"
boot front
rm -f "$M/content/gear.json"; cp "$M/registry/registry.json" "$M/content/registry.json"
boot registry
rm -f "$M/content/registry.json"; cp "$OUTD/gear.json.keep" "$M/content/gear.json"

rc=0
for b in patch front registry; do [[ -f $OUTD/dump-$b.json ]] || { say "no dump from the $b boot"; rc=1; }; done
if [[ $rc == 0 ]]; then
  python3 -I tools/gear-frontend/compare.py --patch "$OUTD/dump-patch.json" --front "$OUTD/dump-front.json" \
    --registry "$OUTD/dump-registry.json" --json "$OUTD/compare.json" | tee -a "$OUTD/session.txt"
  rc=${PIPESTATUS[0]}
fi
say "session over: $([[ $rc == 0 ]] && echo PASS || echo FAIL)"
exit $rc
