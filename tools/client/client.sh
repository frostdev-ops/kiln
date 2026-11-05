#!/usr/bin/env bash
# Kiln in the Enshrouded game client (enshrouded.exe, Steam + Proton). See docs/kiln/client.md.
#
#   tools/client/client.sh status        [--dir D]   what is installed, the exe's build record, game running?, launch
#                                                     options (read-only from Steam), the last boot in kiln.log
#   tools/client/client.sh deploy        [--dir D] [--real] [--policy any|verified-only] [--core-only] [--probe]
#                                                     [--mod <built mod folder>]... [--no-build]
#   tools/client/client.sh undeploy      [--dir D] [--real] [--purge]
#   tools/client/client.sh logs          [--dir D] [lines]
#   tools/client/client.sh report        [--dir D] [lines]  runtime summary: worlds, rates per world, probe lines, errors
#   tools/client/client.sh check         [--dir D]   offline: resolve sigdb/symbols/client.json (+ composed client.d)
#   tools/client/client.sh backup-saves              read-only copy of the client's save files (Steam cloud folder and
#                                                     the prefix's Saved Games) to $KILN_CLIENT_BACKUPS/saves-<time>/
#
# Layout written by deploy (nothing else in the game folder is touched; saves live elsewhere and are never touched):
#   <client>/dinput8.dll                 Kiln proxy (entry gate). A dinput8.dll that is not Kiln's is backed up first.
#   <client>/kiln/                       core, services, Kiln's own plugins (kiln/plugins/kiln-*.dll, e.g. kiln-emf;
#                                        listed in kiln/.kiln-plugins), sigdb (client.json + composed client.d +
#                                        builds/client), .NET, host, mods, kiln.json (created if missing; --policy sets
#                                        "policy")
# Backups: $KILN_CLIENT_BACKUPS (default ~/.local/share/kiln/client-backups)/<time>/: the replaced dinput8.dll and the
# previous kiln.json. undeploy removes Kiln's dinput8.dll and restores the original one; --purge also moves kiln/ into
# a backup folder (never deleted). Refuses while the game runs. The real Steam folder needs --real.
#   --core-only   no service DLLs (kiln/services/ emptied): boot, sigdb, tick and the managed host only
#   --probe       also installs the client probe mod (tools/client/probe, side "both") for the runtime checks
#   --mod D       also installs the built mod folder D (with kiln.mod.json) as kiln/mods/<its id>; repeatable. Shared
#                 assemblies (Kiln.*) and *.deps.json are left out, as deploy-server.sh does. Recorded in
#                 kiln/.extra-mods; undeploy removes them again. kiln/mods is a drop-in folder: when a launcher profile
#                 (kiln/profile.json) is active with drop-ins off, deploy turns its "dropins" on for kiln mods (the
#                 previous value is kept in kiln/.extra-mods-dropins and put back by undeploy); an unreadable
#                 profile.json is only warned about (no mod loads then).
# Profiles (docs/kiln/profiles.md): status shows kiln/profile.json (the launcher's active profile), a pending
# play-once.ini, chain.ini, and what the last boot did (kiln/state/boot.json, native.json, mods.json).
set -euo pipefail
repo=$(cd "$(dirname "$0")/../.." && pwd)
STEAM=${STEAM_ROOT:-$HOME/.local/share/Steam}
REAL=$STEAM/steamapps/common/Enshrouded
BACKUPS=${KILN_CLIENT_BACKUPS:-$HOME/.local/share/kiln/client-backups}
cmd=${1:-}; shift || true
dir=$REAL real=0 policy="" core_only=0 probe=0 build=1 purge=0 lines=40 extra_mods=()
while (($#)); do
  case $1 in
    --dir) dir=$2; shift ;;
    --real) real=1 ;;
    --policy) policy=$2; shift ;;
    --core-only) core_only=1 ;;
    --probe) probe=1 ;;
    --mod) extra_mods+=("$(readlink -f "$2")"); shift ;;
    --no-build) build=0 ;;
    --purge) purge=1 ;;
    [0-9]*) lines=$1 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
  shift
done
dir=$(readlink -f "$dir")
k=$dir/kiln
die() { echo "client: $*" >&2; exit 1; }
sha() { sha256sum "$1" | cut -d' ' -f1; }

game_pids() {  # the game (Wine shows the exe name as comm) or any Wine process running from this folder
  local p
  for p in $(pgrep -x enshrouded.exe || true); do
    [[ $(readlink -f "/proc/$p/cwd" 2>/dev/null) == "$dir" ]] && echo "$p"
  done
  for p in /proc/[0-9]*; do  # Wine processes started in the folder (not shells that merely cd'ed there)
    [[ $(readlink -f "$p/cwd" 2>/dev/null) == "$dir" ]] && tr '\0' ' ' < "$p/cmdline" 2>/dev/null | grep -qi '\.exe' \
      && echo "${p#/proc/}"
  done | sort -u
}
running() { [[ -n $(game_pids) ]]; }

need_dir() {
  [[ -f $dir/enshrouded.exe ]] || die "$dir has no enshrouded.exe"
  [[ ! -f $dir/enshrouded_server.exe ]] || die "$dir is a dedicated server folder; use tools/deploy-server.sh"
}
need_write() {
  need_dir
  if [[ $dir == "$(readlink -f "$REAL")" && $real -eq 0 ]]; then
    die "$dir is the real Steam client folder: add --real"
  fi
  if running; then die "the game is running (pid $(game_pids | tr '\n' ' ')); quit it first"; fi
}

kiln_proxy() {  # is <file> Kiln's dinput8 proxy? (UTF-16 "\kiln\kiln-core.dll" is in its image)
  python3 - "$1" <<'PY'
import sys
b = open(sys.argv[1], 'rb').read()
sys.exit(0 if '\\kiln\\kiln-core.dll'.encode('utf-16-le') in b else 1)
PY
}

launch_options() {  # read-only: Steam's per-user localconfig.vdf, app 1203620
  local f
  for f in "$STEAM"/userdata/*/config/localconfig.vdf; do
    [[ -f $f ]] || continue
    python3 - "$f" <<'PY'
import re, sys
t = open(sys.argv[1], encoding='utf-8', errors='replace').read()
m = re.search(r'"1203620"\s*\{', t)
if not m:
    sys.exit(0)
depth, i = 1, m.end()
while i < len(t) and depth:
    depth += {'{': 1, '}': -1}.get(t[i], 0)
    i += 1
block = t[m.end():i]
lo = re.search(r'"LaunchOptions"\s*"((?:[^"\\]|\\.)*)"', block)
print(f"{sys.argv[1].split('/userdata/')[1].split('/')[0]}: " + (repr(lo.group(1)) if lo else "(none)"))
PY
  done
}

profile_status() {  # kiln/profile.json, play-once.ini, chain.ini and the last boot's state files (read-only)
  python3 - "$k" <<'PY'
import json, os, sys, time
k = sys.argv[1]
def load(p):
    try:
        return json.load(open(p, encoding="utf-8"))
    except FileNotFoundError:
        return None
    except Exception as e:
        return {"__error__": str(e)}
p = load(os.path.join(k, "profile.json"))
if p is None:
    print("profile: none (legacy layout: kiln/mods, kiln/native, kiln/plugins)")
elif "__error__" in p:
    print(f"profile: kiln/profile.json is UNREADABLE ({p['__error__']}): no mods load")
else:
    mods = p.get("mods") or {}
    counts = ", ".join(f"{len(mods.get(kind) or [])} {kind}" for kind in ("kiln", "native", "plugin", "emf"))
    off = sum(1 for kind in mods.values() for m in (kind or []) if m.get("enabled") is False)
    print(f"profile: '{p.get('id')}' ({p.get('name', '')}) generation {p.get('generation')}, {p.get('pathMode', 'native')} paths: "
          f"{counts}{f' ({off} disabled)' if off else ''}; drop-ins {p.get('dropins', False)}")
    print(f"  configDir {p.get('configDir') or 'kiln/config'}, dataDir {p.get('dataDir') or 'kiln/data'}"
          + (f", server {p['server'].get('name')}" if p.get("server") else ""))
po = os.path.join(k, "play-once.ini")
if os.path.exists(po):
    print(f"play-once.ini: PENDING ({' '.join(l.strip() for l in open(po, errors='replace') if l.strip().startswith('mode'))}, "
          f"{int(time.time() - os.path.getmtime(po))} s old; the proxy consumes it at the next start)")
ci = os.path.join(k, "chain.ini")
if os.path.exists(ci):
    lines = [l.strip() for l in open(ci, encoding="utf-8-sig", errors="replace")]
    n = sum(1 for l in lines if l.lower().endswith(".dll") and not l.startswith((";", "#", "//")))
    print(f"chain.ini: {n} DLL line(s)")
b = load(os.path.join(k, "state", "boot.json"))
if b and "__error__" not in b:
    bp = b.get("profile", {})
    print(f"last boot ({b.get('written')}): {b.get('side')}, hooks {'on' if b.get('hooks') else 'OFF (' + str(b.get('safeIdle')) + ')'}, "
          f"profile {bp.get('mode')}{' ' + repr(bp.get('id')) + ' gen ' + str(bp.get('generation')) if bp.get('id') else ''}"
          f"{' ERROR: ' + bp['error'] if bp.get('error') else ''}, play-once {b.get('playOnce', {}).get('mode')}")
    for c in b.get("chain", []):
        print(f"  chain {c.get('source')}: {c.get('status')}{' (' + c['detail'] + ')' if c.get('detail') else ''} {c.get('path')}")
    nat = b.get("native", {})
    print(f"  native mods: {nat.get('loaded', 0)} loaded, {nat.get('failed', 0)} failed, {nat.get('pending', 0)} pending; "
          f"plugins {len(b.get('plugins', []))}; .NET {'up' if b.get('clr', {}).get('started') else 'off'}")
    for e in b.get("errors", []):
        print(f"  error: {e}")
m = load(os.path.join(k, "state", "mods.json"))
if m and "__error__" not in m:
    from collections import Counter
    c = Counter(x.get("state") for x in m.get("mods", []))
    print(f"  managed mods: " + (", ".join(f"{v} {s}" for s, v in c.items()) or "none"))
PY
}

status() {
  need_dir
  echo "client folder: $dir"
  local s; s=$(sha "$dir/enshrouded.exe")
  echo "enshrouded.exe: $s"
  local rec=$repo/sigdb/builds/client/$s.json
  if [[ -f $rec ]]; then
    python3 -c "import json,sys;d=json.load(open(sys.argv[1]));print('  build record:', 'VERIFIED' if d.get('verified') else 'unverified', '-', d.get('steam_build',''), d.get('note',''))" "$rec"
  else
    echo "  build record: none (unknown build: Kiln stays idle under verified-only)"
  fi
  if [[ -f $dir/dinput8.dll ]]; then
    if kiln_proxy "$dir/dinput8.dll"; then echo "dinput8.dll: Kiln proxy"; else echo "dinput8.dll: NOT Kiln's (another mod loader?)"; fi
  else
    echo "dinput8.dll: none (Kiln not installed)"
  fi
  if [[ -f $k/kiln-core.dll ]]; then
    echo "kiln/: core $(sha "$k/kiln-core.dll" | cut -c1-12), $(ls "$k/services" 2>/dev/null | grep -c '\.dll$' || true) service DLLs, $(ls -d "$k"/mods/*/ 2>/dev/null | wc -l) mod folders"
    [[ -f $k/kiln.json ]] && echo "kiln.json: $(tr -d '\n' < "$k/kiln.json" | tr -s ' ')"
  fi
  profile_status
  if running; then echo "game: RUNNING (pid $(game_pids | tr '\n' ' '))"; else echo "game: not running"; fi
  echo "Steam launch options (app 1203620):"; launch_options | sed 's/^/  /'
  if [[ -f $k/logs/kiln.log ]]; then
    echo "last boot ($k/logs/kiln.log, $(stat -c %y "$k/logs/kiln.log" | cut -d. -f1)):"
    tr -d '\r' < "$k/logs/kiln.log" | grep -E "starting \(pid|side:|sigdb: [0-9]+/|build [0-9a-f]+:|safe idle|SimStep hooked|WorldDestroy|boot complete|world 0x[0-9a-f]+ (started|stopping)|local player|\[E " | tail -n 25 | sed 's/^/  /'
  fi
  ls -d "$BACKUPS"/*/ 2>/dev/null | tail -n 5 | sed 's/^/backup: /' || true
}

deploy() {
  need_write
  local out=${KILN_OUT:-$repo/.out/client}
  export KILN_OUT=$out
  if ((build)); then
    "$repo/native/build.sh" kiln-core kiln-proxy >/dev/null
    if ((!core_only)); then
      local d svc
      for d in "$repo"/native/services/*/; do
        svc=$(basename "$d"); [[ -f $d/CMakeLists.txt ]] || continue
        "$repo/native/build.sh" "kiln-$svc" >/dev/null 2>&1 || echo "client: WARNING: native service $svc failed to build; skipped" >&2
      done
      local plg
      for d in "$repo"/native/plugins/*/; do  # Kiln's own plugins (e.g. kiln-emf.dll), as deploy-server.sh does
        plg=$(basename "$d"); [[ -f $d/CMakeLists.txt ]] || continue
        "$repo/native/build.sh" "kiln-$plg" >/dev/null 2>&1 || echo "client: WARNING: native plugin $plg failed to build; skipped" >&2
      done
    fi
    dotnet build "$repo/managed/Kiln.Host/Kiln.Host.csproj" -c Release -o "$out/host" --artifacts-path "$out/dotnet" -v q -nologo >/dev/null
    local proj
    for proj in "$repo"/managed/services/*/*.csproj; do
      dotnet build "$proj" -c Release -o "$out/host" --artifacts-path "$out/dotnet" -v q -nologo >/dev/null 2>&1 \
        || echo "client: WARNING: managed service $(basename "$proj" .csproj) failed to build; skipped" >&2
    done
    if ((probe)); then
      dotnet build "$repo/tools/client/probe/ClientProbe.csproj" -c Release -o "$out/mods/kiln-client-probe" \
        --artifacts-path "$out/dotnet" -v q -nologo >/dev/null
    fi
  fi
  local bin=$out/cmake/bin
  [[ -f $bin/dinput8.dll && -f $bin/kiln-core.dll ]] || die "no build in $out (run without --no-build)"
  [[ -d $repo/.cache/dotnet-win-x64/root ]] || die "no private .NET runtime in $repo/.cache/dotnet-win-x64/root"

  local stamp; stamp=$(date +%Y%m%d-%H%M%S)
  local bk=$BACKUPS/$stamp
  if [[ -f $dir/dinput8.dll ]] && ! kiln_proxy "$dir/dinput8.dll"; then
    mkdir -p "$bk"; cp -a "$dir/dinput8.dll" "$bk/dinput8.dll"
    echo "$dir/dinput8.dll" > "$bk/dinput8.from"
    echo "client: backed up the existing (not Kiln) dinput8.dll to $bk/"
  fi
  if [[ -f $k/kiln.json ]]; then mkdir -p "$bk"; cp -a "$k/kiln.json" "$bk/kiln.json"; fi

  mkdir -p "$k"/{sigdb/builds/client,host,mods,plugins,services,logs,control,data}
  install -m 644 "$bin/kiln-core.dll" "$bin/kiln-core.pdb" "$k/"
  if ((core_only)); then
    find "$k/services" -maxdepth 1 -name '*.dll' -delete
    find "$k/services" -maxdepth 1 -name '*.pdb' -delete
  else
    rsync -a --delete --include='*.dll' --include='*.pdb' --exclude='*' "$bin/services/" "$k/services/" 2>/dev/null || true
    # Kiln's own plugins (bin/plugins/kiln-*.dll); other plugins already in kiln/plugins stay. Recorded in
    # kiln/.kiln-plugins so undeploy removes exactly these.
    if [[ -d $bin/plugins ]]; then
      rsync -a --include='*.dll' --include='*.pdb' --exclude='*' "$bin/plugins/" "$k/plugins/"
      local f
      for f in "$bin"/plugins/*.dll "$bin"/plugins/*.pdb; do
        [[ -f $f ]] || continue
        grep -qx "$(basename "$f")" "$k/.kiln-plugins" 2>/dev/null || basename "$f" >> "$k/.kiln-plugins"
      done
    fi
  fi
  install -m 644 "$repo/sigdb/symbols/client.json" "$k/sigdb/client.json"
  python3 "$repo/tools/sigport/sides.py" compose client "$k/sigdb/client.d" >/dev/null
  rm -rf "$k/sigdb/builds/client" && mkdir -p "$k/sigdb/builds/client"
  cp "$repo"/sigdb/builds/client/*.json "$k/sigdb/builds/client/" 2>/dev/null || true
  rsync -a --delete "$repo/.cache/dotnet-win-x64/root/" "$k/dotnet/"
  rsync -a --delete --include='Kiln.*' --exclude='*' "$out/host/" "$k/host/"
  if ((probe)); then
    mkdir -p "$k/mods/kiln-client-probe"
    rsync -a --delete --include='ClientProbe.dll' --include='ClientProbe.pdb' --include='kiln.mod.json' --exclude='*' \
      "$out/mods/kiln-client-probe/" "$k/mods/kiln-client-probe/"
  fi
  if ((${#extra_mods[@]})); then dropins_on; fi
  local m id
  for m in "${extra_mods[@]}"; do
    [[ -f $m/kiln.mod.json ]] || die "--mod $m: no kiln.mod.json (pass the built mod folder)"
    id=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['id'])" "$m/kiln.mod.json")
    [[ $id =~ ^[a-z0-9][a-z0-9._-]*$ ]] || die "--mod $m: bad id '$id'"
    mkdir -p "$k/mods/$id"
    rsync -a --delete --exclude='Kiln.*' --exclude='*.deps.json' "$m/" "$k/mods/$id/"
    grep -qx "$id" "$k/.extra-mods" 2>/dev/null || echo "$id" >> "$k/.extra-mods"
    echo "client: mod $id installed from $m"
  done
  if [[ -n $policy && -f $k/kiln.json ]]; then
    python3 - "$k/kiln.json" "$policy" <<'PY'
import json, sys
path, policy = sys.argv[1:]
cfg = json.load(open(path))
cfg["policy"] = policy
json.dump(cfg, open(path, "w"), indent=2)
PY
  elif [[ ! -f $k/kiln.json ]]; then
    printf '{\n  "policy": "%s",\n  "clr": true,\n  "plugins": true,\n  "log_level": "info"\n}\n' "${policy:-verified-only}" > "$k/kiln.json"
  fi
  install -m 644 "$bin/dinput8.dll" "$dir/dinput8.dll"  # last: Kiln is live only once everything is in place
  echo "$stamp $(sha "$dir/dinput8.dll") core-only=$core_only probe=$probe" >> "$k/.deployed"
  echo "Kiln deployed to $dir (policy: $(python3 -c "import json,sys;print(json.load(open(sys.argv[1])).get('policy'))" "$k/kiln.json")$( ((core_only)) && echo ', core only'))"
}

dropins_on() {  # --mod installs into kiln/mods, a drop-in folder: make an active profile load it
  [[ -f $k/profile.json ]] || return 0
  python3 - "$k" <<'PY'
import json, os, sys
k = sys.argv[1]
path = os.path.join(k, "profile.json")
try:
    p = json.load(open(path, encoding="utf-8"))
except Exception as e:
    print(f"client: WARNING: kiln/profile.json is unreadable ({e}): Kiln loads NO mods (also not --mod ones) until it is fixed", file=sys.stderr)
    sys.exit(0)
d = p.get("dropins", False)
if d is True or (isinstance(d, list) and "kiln" in d):
    sys.exit(0)
keep = os.path.join(k, ".extra-mods-dropins")
if not os.path.exists(keep):
    json.dump({"dropins": d, "generation": p.get("generation")}, open(keep, "w"))
p["dropins"] = (d + ["kiln"]) if isinstance(d, list) else True
p["generation"] = int(p.get("generation") or 0) + 1
json.dump(p, open(path + ".tmp", "w"), indent=2)
os.replace(path + ".tmp", path)
print(f"client: profile '{p.get('id')}' had kiln drop-ins off: set \"dropins\": {json.dumps(p['dropins'])} so --mod folders load (undeploy restores it)")
PY
}

dropins_restore() {
  [[ -f $k/.extra-mods-dropins ]] || return 0
  [[ -f $k/profile.json ]] && python3 - "$k" <<'PY'
import json, os, sys
k = sys.argv[1]
path = os.path.join(k, "profile.json")
try:
    p = json.load(open(path, encoding="utf-8"))
    old = json.load(open(os.path.join(k, ".extra-mods-dropins")))
except Exception as e:
    print(f"client: WARNING: could not restore the profile's dropins ({e})", file=sys.stderr)
    sys.exit(0)
p["dropins"] = old.get("dropins", False)
p["generation"] = int(p.get("generation") or 0) + 1
json.dump(p, open(path + ".tmp", "w"), indent=2)
os.replace(path + ".tmp", path)
print(f"client: profile '{p.get('id')}': \"dropins\" back to {json.dumps(p['dropins'])}")
PY
  rm -f "$k/.extra-mods-dropins"
}

undeploy() {
  need_write
  local stamp; stamp=$(date +%Y%m%d-%H%M%S)
  local ours=0
  if [[ -f $dir/dinput8.dll ]]; then
    if kiln_proxy "$dir/dinput8.dll"; then
      rm -f "$dir/dinput8.dll"; ours=1
      echo "client: removed Kiln's dinput8.dll (the game runs vanilla)"
    else
      echo "client: $dir/dinput8.dll is not Kiln's; leaving it alone"
    fi
  fi
  local orig
  orig=$(grep -lx "$dir/dinput8.dll" "$BACKUPS"/*/dinput8.from 2>/dev/null | sort | head -n 1 || true)
  if ((ours)) && [[ -n $orig ]]; then
    cp -a "$(dirname "$orig")/dinput8.dll" "$dir/dinput8.dll"
    echo "client: restored the original dinput8.dll from $(dirname "$orig")/"
  fi
  if [[ -f $k/.kiln-plugins ]]; then  # Kiln's own plugins added by deploy
    local p
    while read -r p; do
      [[ $p =~ ^[A-Za-z0-9._-]+\.(dll|pdb)$ && -f $k/plugins/$p ]] && rm -f "$k/plugins/$p" && echo "client: plugin $p removed"
    done < "$k/.kiln-plugins"
    rm -f "$k/.kiln-plugins"
  fi
  if [[ -f $k/.extra-mods ]]; then  # mods added with deploy --mod
    local id
    while read -r id; do
      [[ $id =~ ^[a-z0-9][a-z0-9._-]*$ && -d $k/mods/$id ]] && rm -rf "${k:?}/mods/$id" && echo "client: mod $id removed"
    done < "$k/.extra-mods"
    rm -f "$k/.extra-mods"
  fi
  dropins_restore
  if ((purge)) && [[ -d $k ]]; then
    mkdir -p "$BACKUPS/$stamp"; mv "$k" "$BACKUPS/$stamp/kiln"
    echo "client: moved $k to $BACKUPS/$stamp/kiln"
  fi
}

backup_saves() {
  local dst=$BACKUPS/saves-$(date +%Y%m%d-%H%M%S) n=0 src
  for src in "$STEAM"/userdata/*/1203620/remote \
             "$STEAM/steamapps/compatdata/1203620/pfx/drive_c/users/steamuser/Saved Games/keen games/Enshrouded"; do
    [[ -d $src ]] || continue
    mkdir -p "$dst/$n"; cp -a "$src/." "$dst/$n/"; echo "$src" > "$dst/$n.from"; n=$((n + 1))
  done
  echo "client: $n save folder(s) copied to $dst"
}

case $cmd in
  status) status ;;
  deploy) deploy ;;
  undeploy) undeploy ;;
  logs) need_dir; tr -d '\r' < "$k/logs/kiln.log" | tail -n "$lines" ;;
  check) need_dir; python3 "$repo/tools/sigport/sigdb_check.py" "$repo/sigdb/symbols/client.json" "$dir/enshrouded.exe" | tail -n 3 ;;
  backup-saves) backup_saves ;;
  report)  # runtime summary: worlds, sessions, rates per world, probe lines, errors
    need_dir
    tr -d '\r' < "$k/logs/kiln.log" | grep -E "side:|build [0-9a-f]+:|safe idle|hooked|world 0x[0-9a-f]+ (started|stopping)|local player|LocalPlayerData|probe:|faulted|\[E " | tail -n "$lines"
    [[ -s $k/logs/tick-stats.jsonl ]] && python3 - "$k/logs/tick-stats.jsonl" <<'PY'
import json, sys
for line in open(sys.argv[1]):
    d = json.loads(line)
    print(f"stats {d['time']} {d.get('world', 'server'):10s} {d['rate']:6.2f} steps/s clock {d['clock_rate']:6.2f}/s "
          f"step p50 {d['step_ms']['p50']:.2f} ms p99 {d['step_ms']['p99']:.2f} ms, Kiln pre p50 {d['pre_ms']['p50']:.3f} ms, "
          f"subs {d['subs']}")
PY
    ;;
  *) sed -n '2,30p' "$0"; exit 2 ;;
esac
