#!/usr/bin/env bash
# Chapter 08's forced-drop proof (review B2/B3): one dedicated-server lab, one boot mod (dropped/, id boot-drop-test) that brings
# something to every collector, and the lab switch kiln.json "boot": {"testRefuse": {"boot-drop-test": "<collector>"}} (policy any
# only): that collector refuses the mod, every collector that prepared before it destroys its prepared state, and the generation is
# prepared again without the mod. Each such start must leave the services as a start without the mod does.
#
# Run from the root of the worktree. Environment:
#   LAB       lab name (tools/lab/lab.sh new <name> <port> made it)        KILN_OUT  the build (deploy-server.sh's)
#   LOGS      where each start's logs and state go                         SLOT      a prefix for heavy commands (may be empty)
#   LABSTART  the command that starts the lab (default: tools/lab/lab.sh start)
#
#   run.sh build           build the mods (dropped/ to $KILN_OUT/drop/, the rest into $KILN_OUT/mods) and deploy into the lab
#   run.sh snapshot        one start without the dropped mod; its world and kiln/data become the state every case starts from
#   run.sh case <case> [<name>]  absent | present | assets | worldtables | content | skills | knowledge | vfx | patches;
#                          the start's folder is $LOGS/<name> (default: the case)
#   run.sh compare <a> <b> python3 compare.py over two cases' folders
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${LAB:?}" "${KILN_OUT:?}" "${LOGS:?}"
SLOT=${SLOT:-}
LABSTART=${LABSTART:-tools/lab/lab.sh start}
L=$HOME/srv/enshrouded/labs/$LAB
P=$LOGS/stateP
mkdir -p "$LOGS"

b() { $SLOT dotnet build "$1" -c Release -m:4 -o "$2" --artifacts-path "$KILN_OUT/dotnet" -v q -nologo 2>&1 | grep -E ' error |rror\(s\)' | sort -u; echo "built $2"; }

restore() {
  rm -rf "$L/savegame" "$L/kiln/data"
  cp -a "$P/savegame" "$L/savegame" && cp -a "$P/data" "$L/kiln/data"
}

# kiln.json's boot.testRefuse: set for one collector, or removed
refuse() {
  python3 -I - "$L/kiln/kiln.json" "${1:-}" <<'PY'
import json, sys
p, c = sys.argv[1], sys.argv[2]
j = json.load(open(p))
j.pop("boot", None)
if c:
    j["boot"] = {"testRefuse": {"boot-drop-test": c}}
json.dump(j, open(p, "w"), indent=2)
PY
}

start_and_collect() {
  local D=$1
  rm -rf "$D" && mkdir -p "$D"
  rm -f "$L/kiln/logs/kiln.log"
  $LABSTART "$LAB" 2>&1 | tail -n 1
  local log=$L/kiln/logs/kiln.log n=0
  for i in $(seq 1 150); do
    n=0
    for m in "boot-drop-observer: done" "assets-test done" "content-test done" "quests-test: done:" "skills-test done" "quest-native-test: done:" "wt: done"; do
      grep -q "$m" "$log" 2>/dev/null && n=$((n + 1))
    done
    [[ $n == 7 ]] && break
    [[ -z $(tools/lab/lab.sh pid "$LAB" 2>/dev/null) ]] && { echo "the lab is not running"; break; }
    sleep 5
  done
  echo "done markers: $n of 7"
  tools/lab/lab.sh stop "$LAB" | tail -n 1
  cp "$log" "$D/kiln.log"
  cp -a "$L/kiln/state" "$D/state"
  ( cd "$L/kiln/data" && find . -type f -print0 | sort -z | xargs -0 sha256sum ) > "$D/data.sha256"
  cp -a "$L/kiln/data/skills" "$D/data-skills" 2>/dev/null
  cp -a "$L/kiln/data/kiln-quests" "$D/data-kiln-quests" 2>/dev/null
  cp -a "$L/kiln/data/worlds" "$D/data-worlds" 2>/dev/null
  cp -a "$L/kiln/data/worldtables" "$D/data-worldtables" 2>/dev/null
}

case ${1:?build|snapshot|case|compare} in
  build)
    b "$here/dropped/BootDropTest.csproj" "$KILN_OUT/drop/boot-drop-test"
    b "$here/observer/BootDropObserver.csproj" "$KILN_OUT/mods/boot-drop-observer"
    b mods/tests/content-test/ContentTest.csproj "$KILN_OUT/mods/content-test"
    b mods/tests/assets-test/AssetsTest.csproj "$KILN_OUT/mods/assets-test"
    b mods/tests/skills-test/SkillsTest.csproj "$KILN_OUT/mods/skills-test"
    b mods/tests/quests-test/quests-test.csproj "$KILN_OUT/mods/quests-test"
    b mods/tests/quest-native-test/quest-native-test.csproj "$KILN_OUT/mods/quest-native-test"
    b mods/tests/worldtables-test/WorldTablesTest.csproj "$KILN_OUT/mods/worldtables-test"
    b mods/tests/worldtables-test/fixture/WtFixture.csproj "$KILN_OUT/wt-fixture"
    for f in wt-tweaks wt-mat-a; do
      rm -rf "$KILN_OUT/mods/$f" && cp -r "mods/tests/worldtables-test/fixtures/$f" "$KILN_OUT/mods/$f" && cp "$KILN_OUT/wt-fixture/WtFixture.dll" "$KILN_OUT/mods/$f/"
    done
    $SLOT tools/deploy-server.sh "$L" --policy any > "$LOGS/deploy.log" 2>&1; echo "deploy exit $?"
    ;;
  snapshot)
    rm -rf "$L/kiln/mods/boot-drop-test"
    refuse ""
    touch "$L/kiln/mods/boot-drop-observer/define-cached-node"  # one code definition into kiln.skills' cache (review B3)
    start_and_collect "$LOGS/snapshot"
    rm -f "$L/kiln/mods/boot-drop-observer/define-cached-node"
    rm -rf "$P" && mkdir -p "$P" && cp -a "$L/savegame" "$P/savegame" && cp -a "$L/kiln/data" "$P/data"
    echo "state P: $P"
    ;;
  case)
    c=${2:?case}; name=${3:-$c}
    restore
    rm -rf "$L/kiln/mods/boot-drop-test"
    [[ $c != absent ]] && cp -r "$KILN_OUT/drop/boot-drop-test" "$L/kiln/mods/boot-drop-test"
    case $c in absent | present) refuse "" ;; *) refuse "$c" ;; esac
    start_and_collect "$LOGS/$name"
    refuse ""
    grep -E "boot: (lab switch|generation|collector .* refused|.*dropped|.*RejectedPrepare)|refuses its mod|prepared game-tree definitions are dropped|boot-drop-test" "$LOGS/$name/kiln.log" | cut -c1-260 | head -40
    ;;
  compare)
    python3 -I "$here/compare.py" "$LOGS/${2:?}" "$LOGS/${3:?}"
    ;;
esac
