#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# The priority kit's setup for Linux and Proton (docs/kiln/priority-kit.md); kit.ps1 is the Windows one. Every command
# takes --game <the Enshrouded folder>; without it the Steam libraries are searched.
#
#   kit.sh install      copy the test mod into <game>/kiln/native/gate-priority-test and create its empty run file
#   kit.sh world        name the world to test: the newest GUID the mod logged (or --guid <32 hex digits>)
#   kit.sh raise off    the measurement switch: the next game start runs with the gate's priority raise off
#   kit.sh raise on     delete the switch again (the normal state)
#   kit.sh status       what is installed, the run file and the switch
#   kit.sh uninstall    remove the mod, its run file and the switch (--logs: its measurements in kiln/logs too)
#
# Nothing here starts or stops the game. The kit changes only these files under <game>/kiln:
# native/gate-priority-test/, data/gate-priority-test/run.txt and data/kiln.engine/gate-priority-raise.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
command=${1:-}; shift || true
value= game= guid= logs=0
if [[ $command == raise ]]; then value=${1:-}; shift || true; fi
while (($#)); do
  case $1 in
    --game) game=$2; shift 2 ;;
    --guid) guid=$2; shift 2 ;;
    --logs) logs=1; shift ;;
    *) printf 'unknown option %s\n' "$1" >&2; exit 2 ;;
  esac
done
usage() { sed -n '3,15p' "$0" | sed 's/^# \{0,1\}//'; exit 2; }
[[ $command =~ ^(install|world|raise|status|uninstall)$ ]] || usage

find_game() {
  if [[ -n $game ]]; then
    [[ -f $game/enshrouded.exe ]] || { printf 'No enshrouded.exe in %s\n' "$game" >&2; exit 1; }
    (cd "$game" && pwd); return
  fi
  local root lib libs
  for root in "$HOME/.local/share/Steam" "$HOME/.steam/steam" "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"; do
    libs=("$root")  # one library per line of the vdf: a path with spaces stays one path
    [[ -f $root/steamapps/libraryfolders.vdf ]] &&
      mapfile -t -O 1 libs < <(sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"/\1/p' "$root/steamapps/libraryfolders.vdf")
    for lib in "${libs[@]}"; do
      if [[ -f $lib/steamapps/common/Enshrouded/enshrouded.exe ]]; then (cd "$lib/steamapps/common/Enshrouded" && pwd); return; fi
    done
  done
  printf 'The Enshrouded folder was not found in the Steam libraries; pass --game "<the folder with enshrouded.exe>".\n' >&2
  exit 1
}

game_dir=$(find_game)
kiln=$game_dir/kiln
mod_dir=$kiln/native/gate-priority-test
run_file=$kiln/data/gate-priority-test/run.txt
switch=$kiln/data/kiln.engine/gate-priority-raise
template='# The priority kit'"'"'s run file (docs/kiln/priority-kit.md). One key=value per line.
# world=      the world to test: kit.sh world fills it in (the mod does nothing on any other world)
# minutes=15  how long the writes run; warmup=30 the seconds of steps recorded first
# rate=50     change-and-restore pairs a second; label=  free text for the analysis'
[[ -f $kiln/kiln-core.dll ]] || { printf 'Kiln is not installed in %s (no kiln/kiln-core.dll). Install the test build of Kiln first.\n' "$game_dir" >&2; exit 1; }

case $command in
  install)
    src=$here/mod/gate-priority-test
    [[ -f $src/gate-priority-test.dll ]] || { printf 'No mod/gate-priority-test/gate-priority-test.dll next to this script\n' >&2; exit 1; }
    mkdir -p "$mod_dir"
    cp -f "$src"/* "$mod_dir/"
    if [[ ! -f $run_file ]]; then mkdir -p "${run_file%/*}"; printf '%s\n' "$template" > "$run_file"; fi
    printf 'Installed the test mod in %s\n' "$mod_dir"
    if [[ -f $kiln/profile.json ]] && ! python3 -I -c 'import json,sys; d=json.load(open(sys.argv[1])).get("dropins", False); sys.exit(0 if d is True or (isinstance(d, list) and "native" in d) else 1)' "$kiln/profile.json"; then
      printf 'WARNING: a Kiln launcher profile is active and does not load drop-in native mods. In the launcher, turn on drop-ins for native mods in the profile'"'"'s settings (or deactivate the profile), or the mod will not load.\n'
    fi
    printf 'Next: start the game and load the world you test on (your own save), wait a minute, quit, then run: kit.sh world\n'
    ;;
  world)
    worlds() {  # every GUID the mod logged, oldest first
      local f
      for f in "$kiln/logs/kiln.prev.log" "$kiln/logs/kiln.log"; do
        [[ -f $f ]] && sed -n 's/.*gate-priority-test: this world is \([0-9a-f]\{32\}\).*/\1/p' "$f"
      done
      return 0
    }
    if [[ -z $guid ]]; then
      guid=$(worlds | tail -1)
      [[ -n $guid ]] || { printf "No 'gate-priority-test: this world is ...' line in kiln/logs/kiln.log. Start the game with the mod installed, load your world, wait a minute, quit, and try again.\n" >&2; exit 1; }
      n=$(worlds | sort -u | wc -l)
      ((n <= 1)) || printf 'WARNING: the logs name more than one world; the newest is used: %s. Pass --guid to choose.\n' "$guid"
    fi
    guid=$(printf '%s' "$guid" | tr -d '-' | tr 'A-F' 'a-f')
    [[ $guid =~ ^[0-9a-f]{32}$ ]] || { printf 'A world GUID is 32 hex digits.\n' >&2; exit 1; }
    mkdir -p "${run_file%/*}"
    rest=$( [[ -f $run_file ]] && grep -v -E '^[[:space:]]*world[[:space:]]*=' "$run_file" || printf '%s\n' "$template" )
    printf 'world=%s\n%s\n' "$guid" "$rest" > "$run_file"
    printf 'The test runs on world %s (and on no other). Run file: %s\n' "$guid" "$run_file"
    ;;
  raise)
    case $value in
      off) mkdir -p "${switch%/*}"; printf 'off' > "$switch"
           printf "The measurement switch is set: the next game start runs with the gate's priority raise OFF. Delete it after that run (kit.sh raise on; collect.sh also deletes it).\n" ;;
      on) if [[ -e $switch ]]; then rm -f "$switch"; printf 'The measurement switch is deleted: the raise is on again (the normal state).\n'
          else printf 'No measurement switch: the raise is on (the normal state).\n'; fi ;;
      *) printf 'Use: kit.sh raise off  or  kit.sh raise on\n' >&2; exit 2 ;;
    esac
    ;;
  status)
    printf 'Game:      %s\n' "$game_dir"
    if [[ -f $mod_dir/gate-priority-test.dll ]]; then printf 'Mod:       installed (%s)\n' "$mod_dir"; else printf 'Mod:       not installed\n'; fi
    if [[ -f $run_file ]]; then
      w=$(grep -m1 -E '^[[:space:]]*world[[:space:]]*=' "$run_file" || true)
      printf 'Run file:  %s\n' "${w:-no world named yet (kit.sh world)}"
    else printf 'Run file:  none\n'; fi
    if [[ -e $switch ]]; then printf 'Switch:    present: the raise is OFF at the next start\n'; else printf 'Switch:    none: the raise is on\n'; fi
    if [[ -f $kiln/state/capabilities.json ]]; then
      python3 -I -c 'import json,sys
rows={r["name"]: r for r in json.load(open(sys.argv[1])).get("capabilities", [])}
for n in ("world.ecs_write", "engine.gate_priority_raise"):
    r = rows.get(n)
    print("%-28s%s at the last start %s" % (n + ":", r["state"] if r else "not reported", "(%s)" % r["reasonText"] if r and r.get("reasonText") else ""))' "$kiln/state/capabilities.json"
    fi
    ;;
  uninstall)
    for p in "$mod_dir" "${run_file%/*}" "$switch"; do
      if [[ -e $p ]]; then rm -rf "$p"; printf 'Removed %s\n' "$p"; fi
    done
    out=$kiln/logs/gate-priority-test
    if ((logs)) && [[ -d $out ]]; then rm -rf "$out"; printf 'Removed %s\n' "$out"
    elif [[ -d $out ]]; then printf 'Kept the measurements in %s (--logs removes them too)\n' "$out"; fi
    printf 'The kit is removed. Kiln itself stays; reinstall your usual Kiln build if you installed the test build for this.\n'
    ;;
esac
