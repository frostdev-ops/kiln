#!/usr/bin/env bash
# Stages Kiln's runtime for a bundle (docs/kiln/launcher/bundle.md): one folder per component holding its files at
# their game-relative paths, plus kiln-runtime.json without files[] (kilnctl bundle create hashes and zips them).
#   tools/package/stage.sh <stage dir> [--no-build] [--allow-missing]
# Components: core (proxy, kiln-core, services, host, .NET, kiln.json, Kiln's other plugins), sigdb-server,
# sigdb-client, admin (Kiln.Admin), emf (kiln-emf plugin), eml (kiln-eml, GPL-3, gpl/kiln-eml), samples (hello mod +
# native hello plugin), shroudforge (ShroudForge's runtime, GPL-3, kiln/plugins/shroudforge/, from
# third_party/shroudforge), enshwick (Enshwick's own loader, MIT, kiln/plugins/enshwick/, built from the pinned
# upstream tag by native/plugins/enshwick/tools/build-runtime.sh). The layout rules are tools/deploy-server.sh's (same
# rsync filters), so a server install of core + sigdb-server + admin (+ emf, eml, shroudforge, enshwick, + samples)
# gives the tree deploy-server.sh gives (tools/package/parity.sh checks it).
# Every part is required: a plugin (native/plugins/*, kiln-eml, the ShroudForge runtime, the Enshwick loader), service,
# Kiln.Admin, Kiln.Game or the samples that fails to build stops the stage, naming it, and the staged tree must hold
# every plugin DLL (tools/release/lib/plugins.py). --allow-missing (local dev only; refused when KILN_RELEASE_BUILD=1,
# as in release.sh) warns and leaves such a part out instead.
# KILN_OUT (default <repo>/.out/package) holds the build; the .NET runtime comes from tools/package/fetch-dotnet.sh.
set -euo pipefail
repo=$(cd "$(dirname "$0")/../.." && pwd)
stage=${1:?usage: $0 <stage dir> [--no-build] [--allow-missing]}
shift
build=1 allow_missing=0
while (($#)); do
  case $1 in
    --no-build) build=0 ;;
    --allow-missing) allow_missing=1 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
  shift
done
out=$(realpath -m -- "${KILN_OUT:-$repo/.out/package}")
stage=$(realpath -m -- "$stage")
[[ $stage != / && $stage != "$repo" && $repo != "$stage/"* && $out != "$stage" && $out != "$stage/"* && $stage != "$out/"* ]] || { echo "stage: unsafe or overlapping stage/build paths" >&2; exit 2; }
export KILN_OUT=$out
warn() { echo "stage: WARNING: $*" >&2; }
die() { echo "stage: $*" >&2; exit 1; }
if ((allow_missing)) && [[ ${KILN_RELEASE_BUILD:-} == 1 ]]; then die "--allow-missing is for local dev staging, never a release (KILN_RELEASE_BUILD=1)"; fi
# a required part is missing: stop, unless --allow-missing
missing=()
miss() { if ((allow_missing)); then warn "$* (--allow-missing: left out)"; missing+=("$1"); else die "$*"; fi; }
dn() { dotnet build "$@" -c Release --artifacts-path "$out/dotnet" -v q -nologo; }
logs=$out/stage-logs
mkdir -p "$logs"
# runs a build quietly into $logs/<name>.log; on failure prints its tail and returns 1
built() { # name command...
  local name=$1; shift
  if "$@" >"$logs/$name.log" 2>&1; then return 0; fi
  tail -25 "$logs/$name.log" >&2
  return 1
}

"$repo/tools/package/fetch-dotnet.sh" >/dev/null
if ((build)); then
  "$repo/native/build.sh" kiln-core kiln-proxy kiln-native-hello >/dev/null
  for dir in "$repo"/native/services/*/; do
    svc=$(basename "$dir"); [[ -f $dir/CMakeLists.txt ]] || continue
    built "kiln-$svc" "$repo/native/build.sh" "kiln-$svc" || miss "native service kiln-$svc failed to build (log: $logs/kiln-$svc.log)"
  done
  # Kiln's compatibility plugins: each native/plugins/<p> is the target kiln-<p> (kiln-shroudlink needs cargo xwin)
  for dir in "$repo"/native/plugins/*/; do
    p=$(basename "$dir"); [[ -f $dir/CMakeLists.txt ]] || continue
    built "kiln-$p" "$repo/native/build.sh" "kiln-$p" || miss "native plugin kiln-$p failed to build (log: $logs/kiln-$p.log)"
  done
  dn "$repo/managed/Kiln.Host/Kiln.Host.csproj" -o "$out/host" >/dev/null
  for proj in "$repo"/managed/services/*/*.csproj; do
    [[ -f $proj ]] || continue
    name=$(basename "$proj" .csproj)
    built "$name" dn "$proj" -o "$out/host" || miss "managed service $name failed to build (log: $logs/$name.log)"
  done
  built Kiln.Game dn "$repo/managed/Kiln.Game/Kiln.Game.csproj" -o "$out/host" || miss "Kiln.Game failed to build (log: $logs/Kiln.Game.log)"
  built Hello dn "$repo/mods/samples/Hello/Hello.csproj" -o "$out/mods/hello" || miss "Hello sample failed to build (log: $logs/Hello.log)"
  built Kiln.Admin dn "$repo/mods/Kiln.Admin/Kiln.Admin.csproj" -o "$out/mods/kiln-admin" || miss "Kiln.Admin failed to build (log: $logs/Kiln.Admin.log)"
  # kiln-eml: the GPL-3 EML plugin, its own Cargo workspace in gpl/kiln-eml (docs/kiln/eml.md); lands in bin/plugins
  [[ -x $repo/gpl/kiln-eml/build.sh ]] || die "missing kiln-eml build source"
  built kiln-eml "$repo/gpl/kiln-eml/build.sh" || miss "plugin kiln-eml failed to build (log: $logs/kiln-eml.log)"
  # Enshwick's own loader (MIT), which kiln-enshwick hosts: built from the pinned upstream tag + commit
  built enshwick-loader env CARGO_TARGET_DIR="$out/cargo-enshwick" \
    "$repo/native/plugins/enshwick/tools/build-runtime.sh" --out "$out/enshwick-runtime" \
    || miss "Enshwick loader (enshwick.dll for kiln-enshwick) failed to build (log: $logs/enshwick-loader.log)"
fi
bin=$out/cmake/bin
[[ -f $bin/dinput8.dll && -f $bin/kiln-core.dll && -f $out/host/Kiln.Host.dll ]] || die "no Kiln build in $out (run without --no-build)"
# every plugin the bundle must hold, built now or before (--no-build): a build step that "succeeded" without its DLL
# is caught here too
for dir in "$repo"/native/plugins/*/; do
  p=$(basename "$dir"); [[ -f $dir/CMakeLists.txt ]] || continue
  grep -q "Builds no plugin" "$dir/CMakeLists.txt" && continue  # compat: shared code and host tests (plugins.py NO_PLUGIN)
  [[ -s $bin/plugins/kiln-$p.dll ]] || miss "native plugin kiln-$p: no $bin/plugins/kiln-$p.dll"
done
[[ -s $bin/plugins/kiln-eml.dll ]] || miss "plugin kiln-eml: no $bin/plugins/kiln-eml.dll"
[[ -s $out/host/Kiln.Eml.dll ]] || miss "managed service Kiln.Eml: no $out/host/Kiln.Eml.dll"
dotnet_root=$repo/.cache/dotnet-win-x64/root

rm -rf "$stage"
mkdir -p "$stage"
# core
c=$stage/core k=$stage/core/kiln
mkdir -p "$k"/{services,plugins,host,dotnet}
install -m 644 "$bin/dinput8.dll" "$c/dinput8.dll"
install -m 644 "$bin/kiln-core.dll" "$bin/kiln-core.pdb" "$k/"
rsync -a --include='*.dll' --include='*.pdb' --exclude='*' "$bin/services/" "$k/services/" 2>/dev/null || true
rsync -a --include='Kiln.*' --exclude='*' "$out/host/" "$k/host/"
rsync -a "$dotnet_root/" "$k/dotnet/"
printf '{\n  "policy": "verified-only",\n  "clr": true,\n  "plugins": true,\n  "log_level": "info"\n}\n' > "$k/kiln.json"
# Kiln's own plugins (native/plugins/*): kiln-emf is the emf component, any other plugin is core
if [[ -d $bin/plugins ]]; then
  for f in "$bin"/plugins/*.dll "$bin"/plugins/*.pdb; do
    [[ -f $f ]] || continue
    case $(basename "$f") in kiln-emf.*) comp=emf ;; kiln-eml.*) comp=eml ;; *) comp=core ;; esac
    mkdir -p "$stage/$comp/kiln/plugins"
    install -m 644 "$f" "$stage/$comp/kiln/plugins/"
  done
fi
# kiln-eml (component eml): the GPL-3 licence and where its source is go next to the DLL
if [[ -f $stage/eml/kiln/plugins/kiln-eml.dll ]]; then
  install -m 644 "$repo/gpl/kiln-eml/LICENSE" "$stage/eml/kiln/plugins/kiln-eml.LICENSE.txt"
  install -m 644 "$repo/gpl/kiln-eml/NOTICE.txt" "$stage/eml/kiln/plugins/kiln-eml.NOTICE.txt"
fi
# ShroudForge runtime: a separate GPL-3 component built from the frostdev-ops/shroudforge fork (third_party/shroudforge,
# or KILN_SHROUDFORGE_SRC); kiln-shroudforge.dll (MIT, core) drives it. docs/kiln/shroudforge-compat.md
sfsrc=${KILN_SHROUDFORGE_SRC:-$repo/third_party/shroudforge}
if ((build)); then
  [[ -x $sfsrc/kiln/build.sh ]] || die "missing ShroudForge source: $sfsrc/kiln/build.sh"
  "$sfsrc/kiln/build.sh" "$out/shroudforge-build" || die "ShroudForge runtime build failed"
fi
if [[ -d $out/shroudforge-build/shroudforge ]]; then
  for required in shroudforge-runtime.dll kfc-runtime.dll NOTICE.txt SOURCE.txt version.json LICENSES/kfc-parser-GPL-3.0.txt LICENSES/ShroudForge-MIT.txt; do
    [[ -s $out/shroudforge-build/shroudforge/$required ]] || die "incomplete ShroudForge component: $required"
  done
  mkdir -p "$stage/shroudforge/kiln/plugins"
  rsync -a --delete "$out/shroudforge-build/shroudforge/" "$stage/shroudforge/kiln/plugins/shroudforge/"
else
  ((build == 0)) || die "ShroudForge build failed to produce shroudforge-runtime.dll"
  miss "ShroudForge runtime: no build in $out/shroudforge-build ($sfsrc/kiln/build.sh)"
fi
# Enshwick's loader (component enshwick, MIT): kiln-enshwick (core) runs the game's own mods/enshwick/enshwick.dll
# when it is there, otherwise this copy (docs/kiln/engines/enshwick.md, "loader": "auto"). Its licence goes with it.
ew=$out/enshwick-runtime
if [[ -s $ew/enshwick.dll ]]; then
  for required in LICENSE enshwick-version.txt; do
    [[ -s $ew/$required ]] || die "incomplete Enshwick loader build: $ew/$required"
  done
  mkdir -p "$stage/enshwick/kiln/plugins/enshwick"
  install -m 644 "$ew/enshwick.dll" "$ew/enshwick-version.txt" "$stage/enshwick/kiln/plugins/enshwick/"
  install -m 644 "$ew/LICENSE" "$stage/enshwick/kiln/plugins/enshwick/LICENSE.txt"
else
  miss "Enshwick loader: no $ew/enshwick.dll (native/plugins/enshwick/tools/build-runtime.sh)"
fi
# sigdb per side
s=$stage/sigdb-server/kiln/sigdb
mkdir -p "$s/server.d" "$s/builds/server"
install -m 644 "$repo/sigdb/symbols/server.json" "$s/server.json"
cp "$repo"/sigdb/symbols/server.d/*.json "$s/server.d/" 2>/dev/null || true
cp "$repo"/sigdb/builds/server/*.json "$s/builds/server/" 2>/dev/null || true
s=$stage/sigdb-client/kiln/sigdb
mkdir -p "$s/builds/client"
install -m 644 "$repo/sigdb/symbols/client.json" "$s/client.json"
python3 "$repo/tools/sigport/sides.py" compose client "$s/client.d" >/dev/null
cp "$repo"/sigdb/builds/client/*.json "$s/builds/client/" 2>/dev/null || true
# a mod folder the way deploy-server.sh copies it: without the shared Kiln.* assemblies, *.deps.json and
# *.runtimeconfig.json, except the entry assembly and the manifest's exports (+ their pdbs)
stage_mod() {  # <built mod folder> <component> <id>
  local src=$1 comp=$2 id=$3 dst=$stage/$2/kiln/mods/$3 f
  mapfile -t keep < <(python3 - "$src/kiln.mod.json" <<'PY' 2>/dev/null || true
import json, re, sys
text = re.sub(r"^\s*//.*$", "", open(sys.argv[1]).read(), flags=re.M)
m = json.loads(re.sub(r",(\s*[}\]])", r"\1", text))
for f in [m.get("entryAssembly", "")] + list(m.get("exports", [])):
    if f and "/" not in f and "\\" not in f: print(f)
PY
)
  mkdir -p "$dst"
  rsync -a --delete --delete-excluded --exclude='Kiln.*' --exclude='*.deps.json' --exclude='*.runtimeconfig.json' "$src/" "$dst/"
  for f in "${keep[@]}"; do
    [[ -f $src/$f ]] && cp "$src/$f" "$dst/"
    [[ -f $src/${f%.dll}.pdb ]] && cp "$src/${f%.dll}.pdb" "$dst/"
  done
  return 0
}
if [[ -f $out/mods/kiln-admin/kiln.mod.json ]]; then stage_mod "$out/mods/kiln-admin" admin kiln-admin; else warn "no Kiln.Admin build: no admin component"; fi
for extra in "$out"/mods/*/; do
  id=$(basename "$extra")
  [[ $id == hello || $id == kiln-admin || ! -f $extra/kiln.mod.json ]] && continue
  warn "$out/mods/$id is not part of the runtime bundle (deploy-server.sh would copy it)"
done
# samples
hello=$out/mods/hello
if [[ -f $hello/Hello.dll ]]; then
  mkdir -p "$stage/samples/kiln/mods/hello" "$stage/samples/kiln/plugins"
  install -m 644 "$hello/Hello.dll" "$hello/Hello.pdb" "$hello/kiln.mod.json" "$stage/samples/kiln/mods/hello/"
  [[ -f $bin/kiln-native-hello.dll ]] && install -m 644 "$bin/kiln-native-hello.dll" "$stage/samples/kiln/plugins/"
fi
# kiln-runtime.json without files[]
python3 - "$repo" "$stage" "$dotnet_root" <<'PY'
import glob, json, os, re, sys
repo, stage, dotnet = sys.argv[1:]
version = open(os.path.join(repo, "VERSION")).read().strip()
abi = int(re.search(r"#define KILN_ABI_VERSION (\d+)", open(os.path.join(repo, "native/core/abi/kiln_api.h")).read()).group(1))
api = re.search(r'ApiVersion = "([^"]+)"', open(os.path.join(repo, "managed/Kiln.Api/Mods.cs")).read()).group(1)
nets = sorted(os.listdir(os.path.join(dotnet, "shared/Microsoft.NETCore.App")))
known = {
    "core": {"sides": ["server", "client"], "default": True,
             "description": "dinput8 proxy, kiln-core, services, Kiln.Host + Kiln.Api, the private .NET runtime, kiln.json"},
    "sigdb-server": {"sides": ["server"], "default": True, "requires": ["core"],
                     "description": "symbol definitions and verified builds of the dedicated server"},
    "sigdb-client": {"sides": ["client"], "default": True, "requires": ["core"],
                     "description": "symbol definitions (composed client.d) and verified builds of the game client"},
    "admin": {"sides": ["server"], "default": True, "requires": ["core"],
              "description": "Kiln.Admin: the file bridge and KAP/1 admin transport"},
    "emf": {"sides": ["server", "client"], "default": True, "requires": ["core"],
            "description": "kiln-emf: runs EMF mods on Kiln's services"},
    "samples": {"sides": ["server"], "default": False, "requires": ["core"],
                "description": "the hello sample mod and the native hello plugin"},
    "eml": {"sides": ["server", "client"], "default": True, "requires": ["core"],
            "description": "kiln-eml (GPL-3.0, gpl/kiln-eml): runs EML mods (Enshrouded Mod Loader) before the game starts"},
    "shroudforge": {"sides": ["server", "client"], "default": True, "requires": ["core"],
                    "description": "ShroudForge's runtime (frostdev-ops/shroudforge fork, GPL-3.0): runs ShroudForge mods through kiln-shroudforge"},
    "enshwick": {"sides": ["server", "client"], "default": True, "requires": ["core"],
                 "description": "Enshwick's own loader (enshwick/enshwick, MIT), built from its pinned tag: runs Enshwick mods through kiln-enshwick"},
}
present = {d for d in os.listdir(stage) if os.path.isdir(os.path.join(stage, d))}
unknown = present - set(known)
if unknown:
    sys.exit(f"stage: unknown component folders {sorted(unknown)}")
builds = {}
for side in ("server", "client"):
    out = []
    for f in sorted(glob.glob(os.path.join(repo, f"sigdb/builds/{side}/*.json"))):
        r = json.load(open(f))
        if not r.get("verified"):
            continue
        b = {"sha256": r["sha256"]}
        if r.get("steam_build") is not None: b["steamBuild"] = str(r["steam_build"])
        if r.get("svn") is not None: b["svn"] = int(r["svn"])
        b["verified"] = True
        out.append(b)
    builds[side] = out
m = {"format": 1, "kiln": version, "abi": abi, "api": api, "dotnet": nets[-1] if nets else None,
     "components": {k: v for k, v in known.items() if k in present}, "builds": builds, "config": ["kiln/kiln.json"]}
json.dump(m, open(os.path.join(stage, "kiln-runtime.json"), "w"), indent=2)
open(os.path.join(stage, "kiln-runtime.json"), "a").write("\n")
PY
# every plugin DLL the runtime must hold, in its default component (tools/release/lib/plugins.py)
if ((allow_missing)); then
  python3 -I "$repo/tools/release/lib/plugins.py" check --src "$repo" "$stage" >&2 || warn "the stage misses plugins (--allow-missing): ${missing[*]:-see above}"
else
  python3 -I "$repo/tools/release/lib/plugins.py" check --src "$repo" "$stage" >&2 || die "the stage misses required plugins (see above)"
fi
echo "staged Kiln $(cat "$repo/VERSION") in $stage: $(cd "$stage" && find . -type f -not -name kiln-runtime.json | wc -l) files in $(cd "$stage" && ls -d */ | tr -d / | tr '\n' ' ')"
