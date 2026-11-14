#!/usr/bin/env bash
# Disposable lab servers for Kiln development under $KILN_LABS (default ~/srv/enshrouded/labs)/<name>/.
# Game files are reflinked (btrfs, free) from a vanilla SteamCMD install, $KILN_LAB_ROOT/lab2 (KILN_LAB_ROOT default
# ~/srv/enshrouded); each lab has its own Wine prefix (<name>.pfx, reflinked from $KILN_LAB_ROOT/ge-pfx), query port,
# config and save. Runs under GE-Proton's bundled wine directly (no Proton script, no Steam client; KILN_GE names its
# bin folder). Never touches a Steam-installed server.
#
#   tools/lab/lab.sh new <name> <queryPort> [--save <savegame dir to copy>]
#   tools/lab/lab.sh start|stop|pid|status <name>   start keeps the lab at the caller's priority (nice -n 19 ... start);
#                                                the server's exit status goes to <lab>/kiln/state/exit-code.txt when it ends
#   tools/lab/lab.sh log <name> [lines]          tail the game log
#   tools/lab/lab.sh rm <name>                   stop and delete the lab (game files, prefix, save)
#   tools/lab/lab.sh list
# Deploy Kiln into a lab with: KILN_OUT=<your build dir> tools/deploy-server.sh ~/srv/enshrouded/labs/<name> --policy any
set -euo pipefail
ROOT=${KILN_LAB_ROOT:-$HOME/srv/enshrouded}
LABS=${KILN_LABS:-$ROOT/labs}
BASE=$ROOT/lab2
BASE_PFX=$ROOT/ge-pfx
GE=${KILN_GE:-$HOME/.local/share/Steam/compatibilitytools.d/GE-Proton11-11-x86_64/files/bin}
cmd=${1:-}; name=${2:-}

env_for() {
  # KILN_LAB_DLLOVERRIDES adds overrides for one start (another loader's own proxies: "dbghelp=n,b;winmm=n,b" lets
  # ShroudForge or EML start themselves, as on Windows); the display rules stay
  export WINEPREFIX=$LABS/$1.pfx WINEDEBUG=-all WINEDLLOVERRIDES="mshtml=;dinput8=n,b;winedbg.exe=d${KILN_LAB_DLLOVERRIDES:+;$KILN_LAB_DLLOVERRIDES}"
  # no window may reach the desktop: an unset WAYLAND_DISPLAY means the desktop's wayland-0
  unset DISPLAY; export WAYLAND_DISPLAY=kiln-no-display
}

pid_of() {
  local pfx=$LABS/$1.pfx p
  for p in $(pgrep -x enshrouded_serv || true); do
    # no pipe: under pipefail, grep -q exiting early gives tr SIGPIPE and the pipeline "fails" at random
    grep -qx "WINEPREFIX=$pfx" < <(tr '\0' '\n' < /proc/$p/environ 2>/dev/null) && { echo "$p"; return; }
  done
  return 0
}

# Every command takes only a lab name, never a path: "rm ../lab2" must not reach the base install.
valid() { [[ $1 =~ ^[a-z0-9-]+$ ]]; }
need() {
  valid "$name" || { echo "no lab '$name': a lab name is [a-z0-9-]+ (tools/lab/lab.sh list)" >&2; exit 2; }
  [[ -d $LABS/$name ]] || { echo "no lab '$name' (tools/lab/lab.sh list)" >&2; exit 1; }
}

# Wine gives its threads nice values of its own (Windows thread priorities mapped to nice -10..10 when RLIMIT_NICE
# allows it), whatever niceness the server was started with. Capping RLIMIT_NICE at this shell's niceness keeps every
# process and thread of the lab at the priority it was started with (`nice -n 19 lab.sh start` stays at 19).
keep_priority() {
  local n want cur
  n=$(nice); want=$((20 - n)); ((want < 1)) && want=1
  cur=$(ulimit -H -e)
  if [[ $cur == unlimited ]] || ((cur > want)); then ulimit -e "$want"; fi
}

case $cmd in
  new)
    port=${3:?usage: lab.sh new <name> <queryPort> [--save dir]}
    valid "$name" || { echo "name must be [a-z0-9-]+" >&2; exit 2; }
    [[ -e $LABS/$name ]] && { echo "lab $name exists" >&2; exit 1; }
    ss -uln | grep -q ":$port " && { echo "UDP port $port is in use" >&2; exit 1; }
    mkdir -p "$LABS/$name"
    for f in "$BASE"/*; do
      case $(basename "$f") in kiln|savegame|logs|dinput8.dll|enshrouded_server.json|backup) continue ;; esac
      cp -a --reflink=auto "$f" "$LABS/$name/"
    done
    cp -a --reflink=auto "$BASE_PFX" "$LABS/$name.pfx"
    python3 - "$BASE/enshrouded_server.json" "$LABS/$name/enshrouded_server.json" "$name" "$port" <<'EOF'
import json, sys
src, dst, name, port = sys.argv[1:]
cfg = json.load(open(src))
cfg["name"] = f"Kiln lab {name}"
cfg["queryPort"] = int(port)
cfg["saveDirectory"] = "./savegame"
cfg["logDirectory"] = "./logs"
json.dump(cfg, open(dst, "w"), indent="\t")
EOF
    if [[ ${4:-} == --save ]]; then
      cp -a --reflink=auto "${5:?--save needs a directory}" "$LABS/$name/savegame"
    fi
    echo "lab $name: $LABS/$name, UDP $port, prefix $LABS/$name.pfx"
    ;;
  start)
    need; env_for "$name"
    [[ -n $(pid_of "$name") ]] && { echo "already running ($(pid_of "$name"))"; exit 0; }
    cd "$LABS/$name"; mkdir -p logs
    mark=$(mktemp)
    keep_priority
    # the server's exit status as its Linux parent gets it, in kiln/state/exit-code.txt once the process has ended
    # (Wine passes on the low byte of the Windows exit code: kiln.boot's 0x4B01 arrives as 1, a stop with SIGINT as 0).
    # The waiting shell holds none of this script's output open, so a caller that pipes it is not kept waiting.
    rm -f kiln/state/exit-code.txt
    setsid bash -c '"$1" ./enshrouded_server.exe > "$2" 2>&1 < /dev/null; rc=$?; mkdir -p kiln/state; echo "$rc" > kiln/state/exit-code.txt' \
      lab-server "$GE/wine" "$LABS/$name.wine.log" > /dev/null 2>&1 < /dev/null &
    for i in $(seq 1 360); do
      if [[ logs/enshrouded_server.log -nt $mark ]] && tr -d '\r' < logs/enshrouded_server.log | grep -q 'LoadEcsScene to Run'; then
        rm -f "$mark"; echo "lab $name up after $((i / 2)) s (pid $(pid_of "$name"))"; exit 0
      fi
      if (( i > 40 )) && [[ -z $(pid_of "$name") ]]; then
        rm -f "$mark"; echo "lab $name exited during start; see $LABS/$name.wine.log and logs/" >&2; exit 1
      fi
      sleep 0.5
    done
    rm -f "$mark"; echo "lab $name: timeout waiting for the world" >&2; exit 1
    ;;
  stop)
    need; env_for "$name"
    p=$(pid_of "$name"); [[ -z $p ]] && { echo "not running"; exit 0; }
    kill -INT "$p"
    for _ in $(seq 1 180); do kill -0 "$p" 2>/dev/null || break; sleep 0.5; done
    if kill -0 "$p" 2>/dev/null; then echo "still running after 90 s; kill -9 $p" >&2; kill -9 "$p"; fi
    "$GE/wineserver" -k 2>/dev/null || true
    echo "lab $name stopped"
    ;;
  pid) need; pid_of "$name" ;;
  status)
    need; p=$(pid_of "$name")
    port=$(python3 -c "import json;print(json.load(open('$LABS/$name/enshrouded_server.json'))['queryPort'])")
    echo "lab $name: $([[ -n $p ]] && echo "running pid $p" || echo stopped), UDP $port"
    ;;
  log) need; tr -d '\r' < "$LABS/$name/logs/enshrouded_server.log" | tail -n "${3:-40}" ;;
  rm)
    need; "$0" stop "$name" >/dev/null || true
    rm -rf "${LABS:?}/$name" "${LABS:?}/$name.pfx" "${LABS:?}/$name.wine.log"
    echo "lab $name removed"
    ;;
  list)
    mkdir -p "$LABS"
    for d in "$LABS"/*/; do [[ -d $d ]] && valid "$(basename "$d")" && "$0" status "$(basename "$d")"; done; true
    ;;
  *) sed -n '2,15p' "$0"; exit 2 ;;
esac
