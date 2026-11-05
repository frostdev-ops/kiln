#!/usr/bin/env bash
# Stages Kiln's runtime for a bundle (docs/kiln/launcher/bundle.md): one folder per component holding its files at
# their game-relative paths, plus kiln-runtime.json without files[] (kilnctl bundle create hashes and zips them).
#   tools/package/stage.sh <stage dir> [--no-build]
# Components: core (proxy, kiln-core, services, host, .NET, kiln.json, Kiln's other plugins), sigdb-server,
# sigdb-client, admin (Kiln.Admin), emf (kiln-emf plugin), samples (hello mod + native hello plugin). The layout
# rules are tools/deploy-server.sh's (same rsync filters), so a server install of core + sigdb-server + admin (+ emf,
# + samples) gives the tree deploy-server.sh gives (tools/package/parity.sh checks it).
# KILN_OUT (default <repo>/.out/package) holds the build; the .NET runtime comes from tools/package/fetch-dotnet.sh.
set -euo pipefail
repo=$(cd "$(dirname "$0")/../.." && pwd)
stage=${1:?usage: $0 <stage dir> [--no-build]}
shift
build=1
while (($#)); do
  case $1 in
    --no-build) build=0 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
  shift
done
out=${KILN_OUT:-$repo/.out/package}
export KILN_OUT=$out
warn() { echo "stage: WARNING: $*" >&2; }
die() { echo "stage: $*" >&2; exit 1; }
dn() { dotnet build "$@" -c Release --artifacts-path "$out/dotnet" -v q -nologo; }

"$repo/tools/package/fetch-dotnet.sh" >/dev/null
if ((build)); then
  "$repo/native/build.sh" kiln-core kiln-proxy kiln-native-hello >/dev/null
  for dir in "$repo"/native/services/*/; do
    svc=$(basename "$dir"); [[ -f $dir/CMakeLists.txt ]] || continue
    "$repo/native/build.sh" "kiln-$svc" >/dev/null 2>&1 || warn "native service $svc failed to build; skipped"
  done
  for dir in "$repo"/native/plugins/*/; do
    p=$(basename "$dir"); [[ -f $dir/CMakeLists.txt ]] || continue
    "$repo/native/build.sh" "kiln-$p" >/dev/null 2>&1 || warn "native plugin $p failed to build; skipped"
  done
  dn "$repo/managed/Kiln.Host/Kiln.Host.csproj" -o "$out/host" >/dev/null
  for proj in "$repo"/managed/services/*/*.csproj; do
    [[ -f $proj ]] || continue
    dn "$proj" -o "$out/host" >/dev/null 2>&1 || warn "managed service $(basename "$proj" .csproj) failed to build; skipped"
  done
  dn "$repo/managed/Kiln.Game/Kiln.Game.csproj" -o "$out/host" >/dev/null 2>&1 || warn "Kiln.Game failed to build; skipped"
  dn "$repo/mods/samples/Hello/Hello.csproj" -o "$out/mods/hello" >/dev/null || warn "Hello sample failed to build"
  dn "$repo/mods/Kiln.Admin/Kiln.Admin.csproj" -o "$out/mods/kiln-admin" >/dev/null || warn "Kiln.Admin failed to build"
fi
bin=$out/cmake/bin
[[ -f $bin/dinput8.dll && -f $bin/kiln-core.dll && -f $out/host/Kiln.Host.dll ]] || die "no Kiln build in $out (run without --no-build)"
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
    case $(basename "$f") in kiln-emf.*) comp=emf ;; *) comp=core ;; esac
    mkdir -p "$stage/$comp/kiln/plugins"
    install -m 644 "$f" "$stage/$comp/kiln/plugins/"
  done
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
    "emf": {"sides": ["server", "client"], "default": False, "requires": ["core"],
            "description": "kiln-emf: runs EMF mods on Kiln's services"},
    "samples": {"sides": ["server"], "default": False, "requires": ["core"],
                "description": "the hello sample mod and the native hello plugin"},
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
echo "staged Kiln $(cat "$repo/VERSION") in $stage: $(cd "$stage" && find . -type f -not -name kiln-runtime.json | wc -l) files in $(cd "$stage" && ls -d */ | tr -d / | tr '\n' ' ')"
