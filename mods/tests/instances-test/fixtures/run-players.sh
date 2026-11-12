#!/usr/bin/env bash
# Task t3, PlayerChecks.cs: two boots of a lab with simulated players (no client). Run from the root of the worktree,
# the lab created, booted once (so it has a committed mod-data generation), stopped, and Kiln with instances-test
# deployed into it:
#   mods/tests/instances-test/fixtures/run-players.sh <lab> <out dir>
# It seeds the characters' records (seed-character.py), writes the replay (players-test's slot-reuse.log with pauses:
# A joins in slot 0, an autosave, A leaves, B joins slot 0, A rejoins in slot 1) into the mod's data folder, runs only
# the check t3.players, lists the records with /instances through kiln/control/console, stops (the shutdown save with
# both players in game), and runs the same again after the restart. $LABSTART starts the lab when set.
set -euo pipefail
lab=${1:?lab}; out=${2:?out dir}
here=$(cd "$(dirname "$0")" && pwd)
dir=${KILN_LABS:-$HOME/srv/enshrouded/labs}/$lab
log=$dir/kiln/logs/kiln.log
data=$dir/kiln/data/instances-test
mkdir -p "$out" "$data"
python3 -I "$here/seed-character.py" "$dir"
echo t3.players > "$data/only"
python3 -I - "$here/../../players-test/fixtures/slot-reuse.log" "$data/player-visits.log" <<'EOF'
import sys
src = open(sys.argv[1], encoding="utf-8").read().splitlines()
pad = lambda n: ["[I 00:00:00,000] (waiting for the server to run)"] * n
lines = pad(1200) + src[0:26] + pad(700) + src[26:37] + pad(200) + src[37:62] + pad(200) + src[62:87]
open(sys.argv[2], "w", encoding="utf-8").write("\n".join(lines) + "\n")
EOF
for boot in 1 2; do
  from=0; [[ -f $log ]] && from=$(stat -c %s "$log")
  KILN_PLAYERS_SIMULATE='kiln\data\instances-test\player-visits.log' KILN_CHARACTERS_SIMULATE=1 \
    "${LABSTART:-tools/lab/lab.sh}" $([[ -z ${LABSTART:-} ]] && echo start) "$lab"
  for _ in $(seq 1 900); do
    [[ -f $log ]] && (($(stat -c %s "$log") < from)) && from=0   # a start begins a new kiln.log
    tail -c +"$((from + 1))" "$log" 2>/dev/null | grep -q 'instances-test done' && break
    sleep 1
  done
  mkdir -p "$dir/kiln/control/console"
  rm -f "$dir/kiln/control/console/t3.result.json"
  echo "instances" > "$dir/kiln/control/console/t3.cmd"
  for _ in $(seq 1 60); do [[ -f $dir/kiln/control/console/t3.result.json ]] && break; sleep 1; done
  cp "$dir/kiln/control/console/t3.result.json" "$out/players-boot$boot-console.json" 2>/dev/null || echo "boot $boot: no console result"
  tools/lab/lab.sh stop "$lab"
  cp "$log" "$out/players-boot$boot-kiln.log"
  echo "boot $boot: $(grep -c 'PASS t3' "$out/players-boot$boot-kiln.log") PASS, $(grep -c 'FAIL t3' "$out/players-boot$boot-kiln.log") FAIL"
done
