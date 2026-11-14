#!/usr/bin/env bash
# Lays Kiln out in a dedicated-server folder (the folder with enshrouded_server.exe):
#   <server>/dinput8.dll            Kiln proxy (entry gate)
#   <server>/kiln/kiln-core.dll     native core
#   <server>/kiln/sigdb/            symbol definitions + verified build records
#   <server>/kiln/dotnet/           private .NET runtime (win-x64)
#   <server>/kiln/host/             Kiln.Host + Kiln.Api (+ the services, Kiln.Game, and game/<side>-<sha12>/Kiln.Game.dll
#                                   per verified game build: tools/typegen/build-game.sh)
#   <server>/kiln/mods/<id>/        managed mods (--samples: hello; --loader-samples: mods/samples/build.sh set;
#                                   plus every folder with a kiln.mod.json under $KILN_OUT/mods)
#   <server>/kiln/plugins/          native plugins (--samples: kiln-native-hello)
#   <server>/kiln/kiln.json         created if missing (--policy sets it; --cooked sets "cooked", below)
# The server must be stopped first; this script does not restart it.
#   tools/deploy-server.sh <server dir> [--policy any|verified-only] [--cooked local|warn|any] [--samples] [--loader-samples] [--no-build]
# --cooked local|warn|any: what kiln.assets does with a mod's cooked file (assets/cooked) no cook of this server made (a
# receipt in kiln/state/cook-receipts: kiln cook <mod> --game <server>; docs/kiln/modding/cooking.md): refuse it, load it
# with a warning (Kiln's default), or load it. Without --cooked the server's kiln.json keeps what it says.
# KILN_OUT (default <repo>/native/build) holds all build output; parallel builds each use their own.
set -euo pipefail
repo=$(cd "$(dirname "$0")/.." && pwd)
server=${1:?usage: $0 <server dir> [--policy any|verified-only] [--cooked local|warn|any] [--samples] [--loader-samples] [--no-build]}
shift
policy="" cooked="" samples=0 build=1 loader_samples=0
while (($#)); do
  case $1 in
    --policy) policy=$2; shift ;;
    --cooked) cooked=$2; shift; [[ $cooked == any || $cooked == local || $cooked == warn ]] || { echo "--cooked is local, warn or any" >&2; exit 2; } ;;
    --samples) samples=1 ;;
    --loader-samples) loader_samples=1 ;;  # dependency/exports/IMC/testing samples + resolver fixtures (mods/samples)
    --no-build) build=0 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
  shift
done
[[ -f $server/enshrouded_server.exe ]] || { echo "$server has no enshrouded_server.exe" >&2; exit 1; }
if pgrep -x enshrouded_serv >/dev/null; then
  for p in $(pgrep -x enshrouded_serv); do
    [[ $(readlink -f /proc/$p/cwd 2>/dev/null) == "$(readlink -f "$server")" ]] && { echo "server in $server is running (pid $p); stop it first" >&2; exit 1; }
  done
fi

out=${KILN_OUT:-$repo/native/build}
export KILN_OUT=$out
warn() { echo "deploy: WARNING: $*" >&2; }
if ((build)); then
  # Core targets must build; each service and mod builds on its own, so one broken service only drops
  # that service from this deploy.
  "$repo/native/build.sh" kiln-core kiln-proxy kiln-native-hello >/dev/null
  for dir in "$repo"/native/services/*/; do
    svc=$(basename "$dir"); [[ -f $dir/CMakeLists.txt ]] || continue
    "$repo/native/build.sh" "kiln-$svc" >/dev/null 2>&1 || warn "native service $svc failed to build; skipped"
  done
  for dir in "$repo"/native/plugins/*/; do
    plg=$(basename "$dir"); [[ -f $dir/CMakeLists.txt ]] || continue
    if [[ $plg == shroudforge ]]; then
      "$repo/native/build.sh" "kiln-$plg" || { echo "deploy: ShroudForge adapter build failed" >&2; exit 1; }
    else
      "$repo/native/build.sh" "kiln-$plg" >/dev/null 2>&1 || warn "native plugin $plg failed to build; skipped"
    fi
  done
  dotnet build "$repo/managed/Kiln.Host/Kiln.Host.csproj" -c Release -o "$out/host" --artifacts-path "$out/dotnet" -v q -nologo >/dev/null
  for proj in "$repo"/managed/services/*/*.csproj; do  # service assemblies live next to Kiln.Api (shared by every mod)
    [[ -f $proj ]] || continue
    if [[ $(basename "$proj") == Kiln.Eml.csproj ]]; then
      dotnet build "$proj" -c Release -o "$out/host" --artifacts-path "$out/dotnet" -v q -nologo \
        || { echo "deploy: Kiln.Eml build failed" >&2; exit 1; }
    else
      dotnet build "$proj" -c Release -o "$out/host" --artifacts-path "$out/dotnet" -v q -nologo >/dev/null 2>&1 \
        || warn "managed service $(basename "$proj" .csproj) failed to build; skipped"
    fi
  done
  # generated game types (tools/typegen), shared like the services; the build needs python3. A missing Kiln.Game would
  # leave every mod that uses it unloadable, so a failed build stops the deploy, with its error lines.
  game_log=$out/deploy-kiln-game.log
  if ! dotnet build "$repo/managed/Kiln.Game/Kiln.Game.csproj" -c Release -o "$out/host" --artifacts-path "$out/dotnet" -v q -nologo >"$game_log" 2>&1; then
    { grep -E '(^|: )error' "$game_log" | sort -u || tail -25 "$game_log"; } >&2
    echo "deploy: Kiln.Game failed to build (log: $game_log)" >&2
    exit 1
  fi
  # one Kiln.Game per verified game build: host/game/<side>-<sha12>/ (Kiln.Host picks the running exe's)
  "$repo/tools/typegen/build-game.sh" "$out/host" >/dev/null || { echo "deploy: tools/typegen/build-game.sh failed" >&2; exit 1; }
  dotnet build "$repo/mods/samples/Hello/Hello.csproj" -c Release -o "$out/mods/hello" --artifacts-path "$out/dotnet" -v q -nologo >/dev/null \
    || warn "Hello sample failed to build"
  if ((loader_samples)); then "$repo/mods/samples/build.sh" >/dev/null || warn "loader samples failed to build"; fi
  # kiln-eml, the GPL-3 EML plugin (gpl/kiln-eml, docs/kiln/eml.md): into bin/plugins like Kiln's own plugins
  if [[ -x $repo/gpl/kiln-eml/build.sh ]]; then "$repo/gpl/kiln-eml/build.sh" || { echo "deploy: kiln-eml build failed" >&2; exit 1; }; fi
fi
sfb=$out/shroudforge-build/shroudforge
if [[ -d $sfb ]]; then
  for required in shroudforge-runtime.dll kfc-runtime.dll NOTICE.txt SOURCE.txt version.json LICENSES/kfc-parser-GPL-3.0.txt LICENSES/ShroudForge-MIT.txt; do
    [[ -s $sfb/$required ]] || { echo "deploy: incomplete ShroudForge component: $required" >&2; exit 1; }
  done
fi
bin=$out/cmake/bin
k=$server/kiln
mkdir -p "$k"/{sigdb/builds/server,host,mods,plugins,services,logs,control,data}
install -m 644 "$bin/dinput8.dll" "$server/dinput8.dll"
install -m 644 "$bin/kiln-core.dll" "$bin/kiln-core.pdb" "$k/"
rsync -a --delete --include='*.dll' --include='*.pdb' --exclude='*' "$bin/services/" "$k/services/" 2>/dev/null || true
# Kiln's own plugins (native/plugins/*, e.g. kiln-emf.dll); other plugins already in kiln/plugins stay.
[[ -d $bin/plugins ]] && rsync -a --include='*.dll' --include='*.pdb' --exclude='*' "$bin/plugins/" "$k/plugins/"
# kiln-eml ships with its GPL-3 licence and notice (as the runtime bundle's eml component does)
if [[ -f $k/plugins/kiln-eml.dll ]]; then
  install -m 644 "$repo/gpl/kiln-eml/LICENSE" "$k/plugins/kiln-eml.LICENSE.txt"
  install -m 644 "$repo/gpl/kiln-eml/NOTICE.txt" "$k/plugins/kiln-eml.NOTICE.txt"
fi
# ShroudForge's runtime (GPL-3 component) when it was built: third_party/shroudforge/kiln/build.sh $KILN_OUT/shroudforge-build
if [[ -d $sfb ]]; then rsync -a --delete "$sfb/" "$k/plugins/shroudforge/"; fi
# Enshwick's loader (MIT) for kiln-enshwick when it was built (native/plugins/enshwick/tools/build-runtime.sh, default
# $KILN_OUT/enshwick-runtime), laid out as the runtime bundle's enshwick component
ewb=$out/enshwick-runtime
if [[ -s $ewb/enshwick.dll && -s $ewb/LICENSE ]]; then
  mkdir -p "$k/plugins/enshwick"
  install -m 644 "$ewb/enshwick.dll" "$ewb/enshwick-version.txt" "$k/plugins/enshwick/"
  install -m 644 "$ewb/LICENSE" "$k/plugins/enshwick/LICENSE.txt"
fi

install -m 644 "$repo/sigdb/symbols/server.json" "$k/sigdb/server.json"
rm -rf "$k/sigdb/server.d" && mkdir -p "$k/sigdb/server.d"
cp "$repo"/sigdb/symbols/server.d/*.json "$k/sigdb/server.d/" 2>/dev/null || true
cp "$repo"/sigdb/builds/server/*.json "$k/sigdb/builds/server/" 2>/dev/null || true
rsync -a --delete "$repo/.cache/dotnet-win-x64/root/" "$k/dotnet/"
rsync -a --delete --include='Kiln.*' --include='game/***' --exclude='*' "$out/host/" "$k/host/"
if ((samples)); then
  hello=$out/mods/hello
  mkdir -p "$k/mods/hello"
  install -m 644 "$hello/Hello.dll" "$hello/Hello.pdb" "$hello/kiln.mod.json" "$k/mods/hello/"
  install -m 644 "$bin/kiln-native-hello.dll" "$k/plugins/"
fi
# Every other mod folder (with kiln.mod.json) under $KILN_OUT/mods: the mod's own files, without the shared
# assemblies kiln/host provides (Kiln.Api, Kiln.<Service>, Kiln.Testing) and build leftovers. The entry assembly and the
# manifest's exports are always shipped, even when named Kiln.* (e.g. Kiln.Admin.dll). A manifest-only folder (resolver
# fixtures) is copied as is: the loader reports why it does not load.
for extra in "$out"/mods/*/; do
  [[ -d $extra ]] || continue
  id=$(basename "$extra"); [[ $id == hello || ! -f $extra/kiln.mod.json ]] && continue
  mapfile -t keep < <(python3 - "$extra/kiln.mod.json" <<'PY' 2>/dev/null || true
import json, re, sys
text = re.sub(r"^\s*//.*$", "", open(sys.argv[1]).read(), flags=re.M)  # kiln.mod.json allows // comments
m = json.loads(re.sub(r",(\s*[}\]])", r"\1", text))                   # and trailing commas
for f in [m.get("entryAssembly", "")] + list(m.get("exports", [])):
    if f and "/" not in f and "\\" not in f: print(f)
PY
)
  mkdir -p "$k/mods/$id" && rsync -a --delete --delete-excluded --exclude='Kiln.*' --exclude='*.deps.json' --exclude='*.runtimeconfig.json' "$extra" "$k/mods/$id/"
  for f in "${keep[@]}"; do
    [[ -f $extra/$f ]] && cp "$extra/$f" "$k/mods/$id/"
    [[ -f $extra/${f%.dll}.pdb ]] && cp "$extra/${f%.dll}.pdb" "$k/mods/$id/"
  done
done
if [[ -n $policy && -f $k/kiln.json ]]; then  # change only the policy key, keep other edits
  python3 - "$k/kiln.json" "$policy" <<'PY'
import json, sys
path, policy = sys.argv[1:]
cfg = json.load(open(path))
cfg["policy"] = policy
json.dump(cfg, open(path, "w"), indent=2)
PY
elif [[ -n $policy ]]; then
  printf '{\n  "policy": "%s",\n  "clr": true,\n  "plugins": true,\n  "log_level": "info"\n}\n' "$policy" > "$k/kiln.json"
elif [[ ! -f $k/kiln.json ]]; then
  printf '{\n  "policy": "verified-only",\n  "clr": true,\n  "plugins": true,\n  "log_level": "info"\n}\n' > "$k/kiln.json"
fi
if [[ -n $cooked ]]; then
  python3 - "$k/kiln.json" "$cooked" <<'PY'  # "cooked": change only that key, keep other edits
import json, sys
path, cooked = sys.argv[1:]
cfg = json.load(open(path))
cfg["cooked"] = cooked
json.dump(cfg, open(path, "w"), indent=2)
PY
fi
# The game data Mod.OnBoot reads (tools/bootsnap, docs/kiln/modding/boot-snapshot.md): made once per game build into
# kiln/data/boot-gamedata (about a minute and about 200 MB the first time, under a second after), before the server's
# first start lets EML patch the archive (it reads EML's backup pair after that). A failure is a warning: the server
# starts without it and kiln.boot says why. KILN_NO_BOOT_SNAPSHOT=1 skips the step (a lab that needs no OnBoot data).
if [[ ${KILN_NO_BOOT_SNAPSHOT:-} != 1 ]] && ! python3 -I "$repo/tools/bootsnap/bootsnap.py" make --game "$server"; then
  warn "the game data for OnBoot mods was not made (see above): the server starts without it"
fi
shown=$(grep -o '"cooked": "[^"]*"' "$k/kiln.json" | cut -d'"' -f4 || true)
echo "Kiln deployed to $server (policy: $(grep -o '"policy": "[^"]*"' "$k/kiln.json" | cut -d'"' -f4), cooked: ${shown:-warn, the default})"
