#!/usr/bin/env bash
# Drives the multi-boot lab checks of task t4 (LoaderOrder.cs, DataChecks.cs) in a lab with a copied world and no
# player. Run from the root of the worktree, with the lab created and Kiln deployed into it:
#   mods/tests/instances-test/run-t4.sh <lab> <out dir> <check name>[,<check name>...] [last boot] [first boot - 1]
# (a run can be split: "... t4.data 1" then "... t4.data 6 1" goes on with boot 2; the mod keeps its phase)
# Each boot it starts the lab (through $LABSTART if set, else tools/lab/lab.sh start), waits for the mod's line
# "t4: READY <action>" in kiln.log, copies kiln.log to <out>/<check>-boot<N>-kiln.log and acts:
#   stop     SIGINT (the game's shutdown save), then start again
#   kill     kill -9 the server, then start again
#   rider-off / rider-on   move the instances-rider mod out of / back into kiln/mods, stop (SIGINT), start again
#   drop-gen <revision>    kill -9, delete the generation manifests of that revision and every newer one (the world file
#                          is that revision or a later autosave; an older generation loads), start
#   reload <n>             write "reload-test instances-test <n>" into kiln/control, wait for its result, keep running
#   console <line>         run one console line through kiln/control/console, keep running
#   end      stop, and the run is over
set -euo pipefail
lab=${1:?lab}; out=${2:?out dir}; checks=${3:?check names}; max=${4:-12}
LABS=${KILN_LABS:-$HOME/srv/enshrouded/labs}
GE=${KILN_GE:-$HOME/.local/share/Steam/compatibilitytools.d/GE-Proton11-11-x86_64/files/bin}
dir=$LABS/$lab
log=$dir/kiln/logs/kiln.log
data=$dir/kiln/data/instances-test
mkdir -p "$out" "$data"
tr ',' '\n' <<< "$checks" > "$data/only"
tag=${checks//,/+}
start() {
  if [[ -n ${LABSTART:-} ]]; then "$LABSTART" "$lab"; else tools/lab/lab.sh start "$lab"; fi
}
stop_lab() { tools/lab/lab.sh stop "$lab"; }
kill_lab() {
  local p; p=$(tools/lab/lab.sh pid "$lab")
  if [[ -n $p ]]; then kill -9 "$p"; fi
  sleep 2
  WINEPREFIX=$LABS/$lab.pfx "$GE/wineserver" -k 2>/dev/null || true
  echo "lab $lab killed (-9, pid $p)"
}
wait_ready() {  # $1: byte offset of kiln.log at the start; prints the action
  local from=$1 line
  for _ in $(seq 1 1800); do
    if [[ -f $log ]]; then
      local size; size=$(stat -c %s "$log")
      ((size < from)) && from=0
      line=$(tail -c +"$((from + 1))" "$log" | tr -d '\r' | grep -m1 -o 't4: READY .*' || true)
      if [[ -n $line ]]; then echo "${line#t4: READY }"; return 0; fi
      if [[ -z $(tools/lab/lab.sh pid "$lab") ]]; then echo "exited"; return 0; fi
    fi
    sleep 1
  done
  echo "timeout"
}
boot=${5:-0}
while ((boot < max)); do
  boot=$((boot + 1))
  from=0; [[ -f $log ]] && from=$(stat -c %s "$log")
  start
  while :; do
    action=$(wait_ready "$from")
    cp "$log" "$out/$tag-boot$boot-kiln.log"
    echo "boot $boot: $action"
    case $action in
      reload\ *)
        n=${action#reload }
        mkdir -p "$dir/kiln/control"
        echo "reload-test instances-test $n" > "$dir/kiln/control/t4-reload.cmd"
        for _ in $(seq 1 120); do [[ -f $dir/kiln/control/t4-reload.result.json ]] && break; sleep 1; done
        cat "$dir/kiln/control/t4-reload.result.json" 2>/dev/null > "$out/$tag-boot$boot-reload.json" || true
        from=$(stat -c %s "$log")
        touch "$data/t4/reload.done"
        continue ;;
      console\ *)
        mkdir -p "$dir/kiln/control/console"
        rm -f "$dir/kiln/control/console/t4.result.json"
        echo "${action#console }" > "$dir/kiln/control/console/t4.cmd"
        for _ in $(seq 1 60); do [[ -f $dir/kiln/control/console/t4.result.json ]] && break; sleep 1; done
        cp "$dir/kiln/control/console/t4.result.json" "$out/$tag-boot$boot-console.json" 2>/dev/null || true
        from=$(stat -c %s "$log")
        touch "$data/t4/console.done"
        continue ;;
    esac
    break
  done
  case $action in
    stop) stop_lab ;;
    kill) kill_lab ;;
    rider-off) stop_lab; mkdir -p "$dir/kiln/mods-off"; mv "$dir/kiln/mods/instances-rider" "$dir/kiln/mods-off/" ;;
    rider-on) stop_lab; mv "$dir/kiln/mods-off/instances-rider" "$dir/kiln/mods/" ;;
    drop-gen\ *)
      kill_lab
      rev=${action#drop-gen }
      for m in "$dir"/kiln/data/worlds/*/manifests/*.json; do
        r=$(basename "$m"); r=${r%%[-.]*}
        if ((10#$r >= rev)); then echo "drop generation: $m"; mv "$m" "$out/"; fi
      done ;;
    end) stop_lab; break ;;
    *) echo "boot $boot ended with '$action'; stopping" >&2; stop_lab; break ;;
  esac
done
cp -r "$data/t4" "$out/data-t4" 2>/dev/null || true
