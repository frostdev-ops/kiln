#!/usr/bin/env bash
# Pet clearance calibration (dossier 19d, gate C6) on a dedicated lab server: no game client, no player needed.
# docs/kiln/modding/pets.md "Measured clearance" says how a mod author calibrates its own creature or scale with it.
#
#   tools/pets-clearance/calibrate.sh prepare [options]   build the lab mod (and Kiln), make/deploy the lab, write its plan
#   tools/pets-clearance/calibrate.sh run [options]       one whole lab session: start, wait for the lab mod's DONE, stop,
#                                                         copy the evidence to <out>/session-<time>/ (run it under your
#                                                         lab lock: tools/lab tools/pets-clearance/calibrate.sh run ...)
#   tools/pets-clearance/calibrate.sh profile [options]   judge every session in <out> and write <out>/clearance.json and
#                                                         <out>/report.md (tools/pets-clearance/profiles.py)
#   tools/pets-clearance/calibrate.sh all [options]       the three in order (run through $KILN_LAB_WRAP when it is set)
# Options:
#   --lab NAME           lab name (default dev32-pets-clearance); --port N its UDP query port (default 32066)
#   --out DIR            evidence and results (default <repo>/.out/pets-clearance)
#   --creature ID[@S]    a creature by its Kiln id at scale S (default 1); repeat for more. Default game:enemy_wildbeast_wolf_earlygame
#   --fixtures A,B       only these fixtures (names in mods/tests/pets-clearance-lab/Fixtures.cs); default all
#   --census             also write the census of the game's enemy creatures
#   --no-native          skip the body's own walker (the comparison)
#   --walk S / --native-seconds S   game seconds per walk (default 20) and per native walk (default 20)
#   --mod DIR            a built mod folder (with kiln.mod.json) deployed into the lab too: a mod's own creature block
#                        is calibrated by naming it (--creature my-mod:my-creature); repeat for more
#   --no-deploy          prepare: keep the lab's Kiln as it is (only the lab mod and the plan)
#   --timeout S          run: seconds to wait for DONE (default 3600)
# Environment: KILN_OUT (build output, default <repo>/.out/pets-clearance-build), KILN_SLOT (a command prefix for heavy
# builds, e.g. tools/slot), KILN_LABSTART (how to start the lab; default the wave's labstart when present, else
# tools/lab/lab.sh start at nice 19), KILN_LAB_WRAP (all: the lock wrapper for run), KILN_LAB_ROOT (tools/lab/lab.sh's).
set -euo pipefail
repo=$(cd "$(dirname "$0")/../.." && pwd)
cmd=${1:-}; shift || true
lab=dev32-pets-clearance port=32066 out=$repo/.out/pets-clearance census=false native=true walk=20 nsec=20 deploy=1 timeout=3600
fixtures="" creatures=() mods=()
while (($#)); do
  case $1 in
    --lab) lab=$2; shift ;;
    --port) port=$2; shift ;;
    --out) out=$2; shift ;;
    --creature) creatures+=("$2"); shift ;;
    --fixtures) fixtures=$2; shift ;;
    --census) census=true ;;
    --no-native) native=false ;;
    --walk) walk=$2; shift ;;
    --native-seconds) nsec=$2; shift ;;
    --mod) mods+=("$2"); shift ;;
    --no-deploy) deploy=0 ;;
    --timeout) timeout=$2; shift ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
  shift
done
((${#creatures[@]})) || creatures=(game:enemy_wildbeast_wolf_earlygame)
[[ $lab =~ ^[a-z0-9]+(-[a-z0-9]+)+$ ]] || { echo "lab names are <wave>-<task>, lowercase" >&2; exit 2; }
LABS=${KILN_LABS:-${KILN_LAB_ROOT:-$HOME/srv/enshrouded}/labs}
dir=$LABS/$lab
export KILN_OUT=${KILN_OUT:-$repo/.out/pets-clearance-build}
slot=(${KILN_SLOT:-})
mkdir -p "$out"

plan() {
  python3 - "$dir/kiln/data/pets-clearance-lab/plan.json" "$fixtures" "$census" "$native" "$walk" "$nsec" "${creatures[@]}" <<'PY'
import json, os, sys
path, fixtures, census, native, walk, nsec, *creatures = sys.argv[1:]
plan = {"creatures": [], "fixtures": [f for f in fixtures.split(",") if f] or None, "horizontal": [0.5, 1, 2], "vertical": [1, 2, 3],
        "native": native == "true", "census": census == "true", "walkSeconds": float(walk), "nativeSeconds": float(nsec), "roster": True, "rosterSeconds": 60}
for c in creatures:
    cid, _, scale = c.partition("@")
    plan["creatures"].append({"id": cid, "scale": float(scale or 1)})
os.makedirs(os.path.dirname(path), exist_ok=True)
json.dump(plan, open(path, "w"), indent=2)
print(f"plan: {len(plan['creatures'])} creature(s), fixtures {plan['fixtures'] or 'all'}")
PY
}

prepare() {
  cd "$repo"
  [[ -d $dir ]] || tools/lab/lab.sh new "$lab" "$port" --save "${KILN_LAB_ROOT:-$HOME/srv/enshrouded}/lab2/savegame"
  [[ -n $(tools/lab/lab.sh pid "$lab") ]] && { echo "lab $lab is running; stop it first" >&2; exit 1; }
  "${slot[@]}" dotnet build mods/tests/pets-clearance-lab/PetsClearanceLab.csproj -c Release -nologo -v q -nodeReuse:false \
    -o "$KILN_OUT/mods/pets-clearance-lab" --artifacts-path "$KILN_OUT/dotnet"
  if ((deploy)); then
    KILN_NO_BOOT_SNAPSHOT=1 "${slot[@]}" tools/deploy-server.sh "$dir" --policy any
  else
    mkdir -p "$dir/kiln/mods/pets-clearance-lab"
    rsync -a --delete --exclude='Kiln.*' --exclude='*.deps.json' "$KILN_OUT/mods/pets-clearance-lab/" "$dir/kiln/mods/pets-clearance-lab/"
  fi
  for m in "${mods[@]}"; do
    [[ -f $m/kiln.mod.json ]] || { echo "--mod $m: no kiln.mod.json" >&2; exit 2; }
    id=$(python3 -c 'import json,re,sys; t=re.sub(r"^\s*//.*$","",open(sys.argv[1]).read(),flags=re.M); print(json.loads(re.sub(r",(\s*[}\]])",r"\1",t))["id"])' "$m/kiln.mod.json")
    mkdir -p "$dir/kiln/mods/$id" && rsync -a --delete --exclude='Kiln.*' "$m/" "$dir/kiln/mods/$id/"
  done
  mkdir -p "$dir/kiln/data/kiln.creatures" && : > "$dir/kiln/data/kiln.creatures/force-ready"
  plan
}

run() {
  cd "$repo"
  [[ -f $dir/kiln/mods/pets-clearance-lab/PetsClearanceLab.dll ]] || { echo "lab $lab has no pets-clearance-lab: prepare first" >&2; exit 1; }
  log=$dir/kiln/logs/kiln.log
  stamp=$(date +%Y%m%d-%H%M%S)
  dest=$out/session-$stamp
  mkdir -p "$dest"
  rm -f "$dir/kiln/data/pets-clearance-lab/runs.jsonl"
  : > "$log" 2>/dev/null || true
  stop() { tools/lab/lab.sh stop "$lab" >/dev/null 2>&1 || true; }
  trap stop EXIT
  if [[ -n ${KILN_LABSTART:-} ]]; then $KILN_LABSTART "$lab"
  elif [[ -x /mnt/Dongus/kiln-wave/tools/labstart ]]; then /mnt/Dongus/kiln-wave/tools/labstart "$lab"
  else nice -n 19 tools/lab/lab.sh start "$lab"; fi
  rc=0
  # until the lab mod's DONE line; a server that ended (a crash, a Kiln that refused the mod) ends the wait at once instead
  # of holding the shared lab lock until the timeout
  timeout "$timeout" bash -c 'until grep -q "pets-clearance-lab: DONE" "$1" 2>/dev/null; do
      [[ -n $(tools/lab/lab.sh pid "$2" 2>/dev/null) ]] || exit 3; sleep 5; done' _ "$log" "$lab" || rc=$?
  ((rc == 3)) && echo "run: the lab server stopped before the lab mod's DONE line (see $dest/kiln.log and $dest/game.log)" >&2
  ((rc && rc != 3)) && echo "run: no DONE line within $timeout s" >&2
  stop
  trap - EXIT
  cp "$log" "$dest/kiln.log" 2>/dev/null || true
  cp "$dir/logs/enshrouded_server.log" "$dest/game.log" 2>/dev/null || true
  cp "$dir/kiln/data/pets-clearance-lab/plan.json" "$dest/" 2>/dev/null || true
  cp "$dir/kiln/data/pets-clearance-lab/runs.jsonl" "$dest/" 2>/dev/null || { echo "run: no runs.jsonl" >&2; rc=1; }
  grep -E "pets-clearance-lab: " "$dest/kiln.log" | cut -c1-300 | tail -n 40 || true
  echo "run: evidence in $dest"
  return $rc
}

profile() {
  python3 -I "$repo/tools/pets-clearance/profiles.py" "$out"/session-*/runs.jsonl --out "$out/clearance.json" --report "$out/report.md" --lab "$lab"
}

case $cmd in
  prepare) prepare ;;
  run) run ;;
  profile) profile ;;
  all)
    prepare
    if [[ -n ${KILN_LAB_WRAP:-} ]]; then $KILN_LAB_WRAP "$0" run --lab "$lab" --out "$out" --timeout "$timeout"; else run; fi
    profile ;;
  *) sed -n '2,30p' "$0"; exit 2 ;;
esac
