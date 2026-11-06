#!/usr/bin/env bash
# End-to-end test of the launcher's self-update with real AppImages, on a private X display:
#   tools/release/e2e-update.sh [--rebuild] [--only ok|down|tampered|refused]
# 1. Builds two QA AppImages (features dev-eval + update-test-feeds) at 0.2.0-dev.1001 ("old") and 0.2.0-dev.1002
#    ("new", shipping a Kiln runtime), signed with a throwaway key made for the test (never the release key).
# 2. Serves a "frostdev.io" feed and a "GitHub" feed from two local HTTP servers (KILN_UPDATE_FEEDS; loopback only).
# 3. For each scenario runs a copy of the old AppImage with its own HOME, data dir, dbus session and Xvfb display,
#    waits for the notice, presses Update, then Restart now (through KILN_DEV_EVAL_DIR), and checks the result:
#      ok        frostdev.io serves the update: the new version runs; the notice offers the runtime it brought
#                (held back while a fake game process runs, then Install into the game)
#      down      frostdev.io is down: the check and the download fall back to GitHub
#      tampered  frostdev.io serves a changed file: its signature fails, GitHub's copy is installed
#      refused   both serve the changed file: nothing is installed, the AppImage stays the same
# Output: $KILN_OUT/e2e/ (logs, launcher.log per scenario), screenshots in $KILN_OUT/shots/e2e-*.png.
# KILN_OUT defaults to <repo>/.out/e2e-update. Ports: KILN_E2E_PORT (default 26721) and the next one.
# Needs: bun, cargo, Xvfb, dbus-daemon, ImageMagick (import), python3. KILN_RELEASE_NICE=1 builds at low priority.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
export KILN_OUT=${KILN_OUT:-$repo/.out/e2e-update}
export CARGO_TARGET_DIR=${CARGO_TARGET_DIR:-$KILN_OUT/cargo}
W=$KILN_OUT/e2e
SHOTS=$KILN_OUT/shots
port=${KILN_E2E_PORT:-26721}
F_PORT=$port G_PORT=$((port + 1))
OLD=0.2.0-dev.1001 NEW=0.2.0-dev.1002
rebuild=0 only=""
while (($#)); do
  case $1 in
    --rebuild) rebuild=1; shift ;;
    --only) only=$2; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
mkdir -p "$W" "$SHOTS"
if [[ ${KILN_RELEASE_NICE:-} == 1 ]]; then low() { nice -n 19 ionice -c 3 "$@"; }; else low() { "$@"; }; fi
say() { printf '\n== %s\n' "$*"; }
pids=()
cleanup() {
  for p in "${pids[@]}"; do kill "$p" 2>/dev/null || true; done
}
trap cleanup EXIT

# ---------------------------------------------------------------- builds
key=$W/key/test.key
if [[ ! -f $key ]]; then
  mkdir -p "$W/key"
  (cd "$repo/launcher" && TAURI_SIGNING_PRIVATE_KEY_PASSWORD="" bun x tauri signer generate --ci -w "$key" >/dev/null 2>&1)
fi
pub=$(cat "$key.pub")
qa_config="{\"plugins\":{\"updater\":{\"pubkey\":\"$pub\",\"dangerousInsecureTransportProtocol\":true}}}"
runtime_zip=${KILN_E2E_RUNTIME:-}
if [[ -z $runtime_zip ]]; then
  runtime_zip=$(ls -t "$repo"/.out/*/runtime-dist/kiln-runtime-*.zip 2>/dev/null | head -1 || true)
fi
[[ -f $runtime_zip ]] || { echo "no Kiln runtime zip to ship in the new AppImage: set KILN_E2E_RUNTIME" >&2; exit 1; }

build() { # version runtime out
  local v=$1 rt=$2 out=$3
  if ((rebuild)) || [[ ! -f $out ]]; then
    say "building AppImage $v"
    rm -f "$CARGO_TARGET_DIR/release/bundle/appimage/"*.AppImage
    low "$repo/tools/launcher/build-linux.sh" --bundles appimage --runtime "$rt" --version "$v" \
      --features dev-eval,update-test-feeds --config "$qa_config" >"$W/build-$v.log" 2>&1 \
      || { tail -30 "$W/build-$v.log"; exit 1; }
    cp -f "$(ls -t "$CARGO_TARGET_DIR/release/bundle/appimage/"*.AppImage | head -1)" "$out"
  fi
}
build "$OLD" none "$W/old.AppImage"
build "$NEW" "$runtime_zip" "$W/new.AppImage"
if [[ ! -f $W/new.AppImage.sig || $W/new.AppImage -nt $W/new.AppImage.sig ]]; then
  (cd "$repo/launcher" && TAURI_SIGNING_PRIVATE_KEY_PASSWORD="" bun x tauri signer sign -f "$key" --app-version "$NEW" "$W/new.AppImage" >/dev/null)
fi
python3 "$here/lib/release.py" verify --pubkey "$key.pub" --version "$NEW" "$W/new.AppImage"
# the tampered copy: one byte near the end changed, the signature kept
cp -f "$W/new.AppImage" "$W/tampered.AppImage"
python3 - "$W/tampered.AppImage" <<'PY'
import sys
p = sys.argv[1]
with open(p, "r+b") as f:
    f.seek(-4096, 2)
    b = f.read(1)
    f.seek(-4096, 2)
    f.write(bytes([b[0] ^ 0x5A]))
PY
cp -f "$W/new.AppImage.sig" "$W/tampered.AppImage.sig"
sha() { sha256sum "$1" | cut -d' ' -f1; }
OLD_SHA=$(sha "$W/old.AppImage") NEW_SHA=$(sha "$W/new.AppImage")

# ---------------------------------------------------------------- feeds
feed() { # dir port file(new|tampered)
  local dir=$1 p=$2 file=$3
  rm -rf "$dir" && mkdir -p "$dir/files"
  ln "$W/$file.AppImage" "$dir/files/Kiln_${NEW}_amd64.AppImage" 2>/dev/null || cp "$W/$file.AppImage" "$dir/files/Kiln_${NEW}_amd64.AppImage"
  printf 'Self-update test build.\nFrom the %s feed.\n' "$(basename "$dir")" >"$dir/notes.txt"
  for ch in dev stable; do
    python3 "$here/lib/release.py" manifest --version "$NEW" --channel dev --notes "$dir/notes.txt" \
      --runtime "$(basename "$runtime_zip" .zip | sed 's/^kiln-runtime-//')" \
      --entry "linux-x86_64-appimage,linux-x86_64=http://127.0.0.1:$p/files/Kiln_${NEW}_amd64.AppImage|$W/$file.AppImage.sig|$W/$file.AppImage" \
      --out "$dir/$ch/latest.json"
  done
}
serve() { # dir port
  python3 -m http.server "$2" --bind 127.0.0.1 --directory "$1" >"$1.http.log" 2>&1 &
  pids+=($!)
  echo $!
}
FEEDS="frostdev.io=http://127.0.0.1:$F_PORT/{channel}/latest.json;GitHub=http://127.0.0.1:$G_PORT/{channel}/latest.json"

# ---------------------------------------------------------------- one scenario
free_display() { local n=93; while [[ -e /tmp/.X11-unix/X$n || -e /tmp/.X$n-lock ]]; do n=$((n + 1)); done; echo $n; }
wait_log() { # log regex seconds
  local i
  for ((i = 0; i < $3 * 2; i++)); do
    grep -qE "$2" "$1" 2>/dev/null && return 0
    sleep 0.5
  done
  echo "timed out waiting for /$2/ in $1" >&2
  tail -20 "$1" >&2 || true
  return 1
}
click() { # run-dir dialog-label button-text
  local js="(()=>{const d=document.querySelector('[role=dialog][aria-label=\"$2\"]');const b=d&&[...d.querySelectorAll('button')].find(x=>x.textContent.trim()==='$3');if(b&&!b.disabled)b.click();})();"
  printf '%s\n' "$js" >"$1/eval/$(date +%s%N).js"
}
shot() { sleep "${3:-1.5}"; import -display ":$1" -window root "$SHOTS/e2e-$2.png" 2>/dev/null && echo "screenshot $SHOTS/e2e-$2.png"; }

scenario() { # name frostdev(new|tampered|down) github(new|tampered)
  local name=$1 fmode=$2 gmode=$3
  [[ -z $only || $only == "$name" ]] || return 0
  say "scenario $name: frostdev.io $fmode, GitHub $gmode"
  local R=$W/run-$name
  rm -rf "$R" && mkdir -p "$R"/{home,data,tmp,eval,app,steam,game/kiln}
  cp "$W/old.AppImage" "$R/app/Kiln.AppImage" && chmod +x "$R/app/Kiln.AppImage"
  local fp="" gp
  if [[ $fmode != down ]]; then feed "$R/frostdev" "$F_PORT" "$fmode"; fp=$(serve "$R/frostdev" "$F_PORT"); fi
  feed "$R/github" "$G_PORT" "$gmode"; gp=$(serve "$R/github" "$G_PORT")
  # a fake game folder with Kiln "installed" (an older runtime than the new launcher ships)
  cp /usr/bin/sleep "$R/game/enshrouded.exe"
  printf '{"format":1,"installId":"e2e","installed":"2026-11-01T00:00:00Z","runtime":{"version":"0.2.0-dev+20261101.0000000","components":["core"]},"side":"client","files":[]}\n' >"$R/game/kiln/.install.json"
  local n; n=$(free_display)
  Xvfb ":$n" -screen 0 1360x860x24 -nolisten tcp >"$R/xvfb.log" 2>&1 &
  local xvfb=$!; pids+=($xvfb)
  sleep 1
  local bus; bus=$(dbus-daemon --session --fork --print-address=1 --print-pid=1 | tr '\n' ' ')
  local bus_addr bus_pid
  bus_addr=$(echo "$bus" | awk '{print $1}') bus_pid=$(echo "$bus" | awk '{print $2}')
  pids+=($bus_pid)
  local log=$R/data/logs/launcher.log
  (
    cd "$R/app"
    env -i PATH="/usr/bin:/bin" HOME="$R/home" XDG_CONFIG_HOME="$R/home/.config" XDG_DATA_HOME="$R/home/.local/share" \
      XDG_CACHE_HOME="$R/home/.cache" XDG_RUNTIME_DIR="$R/tmp" TMPDIR="$R/tmp" DISPLAY=":$n" GDK_BACKEND=x11 \
      DBUS_SESSION_BUS_ADDRESS="$bus_addr" NO_AT_BRIDGE=1 LIBGL_ALWAYS_SOFTWARE=1 WEBKIT_DISABLE_COMPOSITING_MODE=1 \
      KILN_DATA_DIR="$R/data" KILN_STEAM_ROOTS="$R/steam" KILN_LAUNCHER_NO_WEBGL=1 KILN_LAUNCH_DRY_RUN=1 \
      KILN_DEV_EVAL_DIR="$R/eval" KILN_UPDATE_FEEDS="$FEEDS" \
      ./Kiln.AppImage >"$R/stdout.log" 2>&1
  ) &
  pids+=($!)
  wait_log "$log" "Kiln Launcher $OLD \(pid [0-9]+, Appimage, updates in place\)" 60
  if [[ $fmode == down ]]; then
    wait_log "$log" "automatic check \(dev\): GitHub offers $NEW" 40
    wait_log "$log" "tried before: frostdev.io: not reachable" 5
  else
    wait_log "$log" "automatic check \(dev\): frostdev.io offers $NEW" 40
  fi
  # register the fake game (as Settings > Add a folder would); the new launcher reads it after the restart
  printf '%s\n' "window.__TAURI_INTERNALS__.invoke('game_add',{path:'$R/game'}).catch(e=>console.error(e));" >"$R/eval/0-game.js"
  shot "$n" "$name-1-notice"
  click "$R" "Kiln $NEW" "Update"
  wait_log "$log" "update: install requested" 15
  if [[ $name == refused ]]; then
    wait_log "$log" "install failed: frostdev.io: the signature does not match the file; GitHub: the signature does not match the file" 120
    shot "$n" "$name-2-refused"
    [[ $(sha "$R/app/Kiln.AppImage") == "$OLD_SHA" ]] || { echo "FAIL: the AppImage changed although nothing verified" >&2; return 1; }
    echo "PASS $name: both sources refused (bad signature), the AppImage is unchanged"
  else
    wait_log "$log" "$NEW from (frostdev.io|GitHub) is in place" 180
    if [[ $fmode == tampered ]]; then wait_log "$log" "$NEW: fell back to GitHub after: frostdev.io: the signature does not match the file" 2; fi
    if [[ $fmode == down ]]; then wait_log "$log" "$NEW: fell back to GitHub after: frostdev.io: not reachable" 2; fi
    shot "$n" "$name-2-ready"
    [[ $(sha "$R/app/Kiln.AppImage") == "$NEW_SHA" ]] || { echo "FAIL: the AppImage is not the new one" >&2; return 1; }
    local game_pid=""
    if [[ $name == ok ]]; then
      # the game "runs" across the restart: the notice must hold the runtime install back
      (cd "$R/game" && exec ./enshrouded.exe 600) &
      game_pid=$!; pids+=($game_pid)
    fi
    click "$R" "Ready to restart" "Restart"
    wait_log "$log" "restarting into $NEW" 15
    wait_log "$log" "Kiln Launcher $NEW \(pid [0-9]+, Appimage, updates in place\)" 90
    wait_log "$log" "updated from $OLD to $NEW" 5
    local src; src=$(grep -oE "$NEW from [A-Za-z.]+ is in place" "$log" | tail -1 | awk '{print $3}')
    echo "PASS $name: restarted into $NEW (installed from $src); the AppImage's sha256 is the new build's"
    if [[ $name == ok ]]; then
      shot "$n" "$name-3-updated-game-running" 12
      kill "$game_pid" 2>/dev/null || true
      shot "$n" "$name-4-updated-install-runtime" 10
      click "$R" "Updated to $NEW" "Update game"
      wait_log "$R/data/logs/launcher.log" "runtime_install-[0-9]+-[0-9a-f]+ done" 180
      grep -q "\"version\": *\"$(basename "$runtime_zip" .zip | sed 's/^kiln-runtime-//')\"" "$R/game/kiln/.install.json" \
        || { echo "FAIL: the game's receipt does not name the new runtime" >&2; return 1; }
      echo "PASS $name: Update game installed the runtime the new launcher brought into the (fake) game folder"
      shot "$n" "$name-5-game-updated" 3
    else
      shot "$n" "$name-3-updated" 8
    fi
    grep -E "update:" "$log" | sed 's/^/  /' >"$R/update-lines.log"
  fi
  # stop this scenario's processes (exact pids: the launchers named in the log, then the servers and the display)
  for p in $(grep -oE "\(pid [0-9]+" "$log" | grep -oE "[0-9]+"); do kill "$p" 2>/dev/null || true; done
  sleep 2
  for p in $fp $gp $xvfb $bus_pid; do kill "$p" 2>/dev/null || true; done
  sleep 1
  echo "requests: frostdev.io $(grep -c '" 200 ' "$R/frostdev.http.log" 2>/dev/null || echo 0) ok, GitHub $(grep -c '" 200 ' "$R/github.http.log") ok"
}

result=0
scenario ok new new || result=1
scenario down down new || result=1
scenario tampered tampered new || result=1
scenario refused tampered tampered || result=1
((result == 0)) && echo "e2e: all scenarios passed" || echo "e2e: FAILED"
exit $result
