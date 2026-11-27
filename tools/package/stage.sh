#!/usr/bin/env bash
# Stages Kiln's runtime for a bundle (docs/kiln/launcher/bundle.md): one folder per component holding its files at
# their game-relative paths, plus kiln-runtime.json without files[] (kilnctl bundle create hashes and zips them).
#   tools/package/stage.sh <stage dir> [--no-build] [--allow-missing]
# Components: core (proxy, kiln-core, services, host, .NET, kiln.json, compat-rules.json, Kiln's other plugins), sigdb-server,
# sigdb-client, admin (Kiln.Admin), emf (kiln-emf plugin and upstream Lua host), eml (kiln-eml, GPL-3), samples (hello mod +
# native hello plugin), shroudforge (ShroudForge's runtime, GPL-3, kiln/plugins/shroudforge/, from
# third_party/shroudforge), enshwick (Enshwick's own loader, MIT, kiln/plugins/enshwick/, built from the pinned
# upstream tag by native/plugins/enshwick/tools/build-runtime.sh). The layout rules are tools/deploy-server.sh's (same
# rsync filters), so a server install of core + sigdb-server + admin (+ emf, eml, shroudforge, enshwick, + samples)
# gives the tree deploy-server.sh gives (tools/package/parity.sh checks it).
# Every part is required: a plugin (native/plugins/*, kiln-eml, the ShroudForge runtime, the Enshwick loader), service,
# Kiln.Admin, Kiln.Game or the samples that fails to build stops the stage, naming it, and the staged tree must hold
# every plugin DLL (tools/release/lib/plugins.py). --allow-missing (local dev only; refused when KILN_RELEASE_BUILD=1,
# as in release.sh) warns and leaves such a part out instead.
# KILN_SF_CACHE=DIR: keep ShroudForge's built runtime by the hash of its inputs and reuse it (verified by sha256).
# KILN_NATIVE_PREBUILT=DIR: the native CMake targets' bin/ built elsewhere with the same toolchain (release.sh,
# KILN_BUILD_REMOTE=win1), with that build's generated kiln_build.h (its build stamp): copied in, and only what it lacks
# builds here.
# kiln-sfi.dll is built with the native build's stamp (its generated kiln_build.h: KILN_BUILD for
# crates/sfi/kiln-sfi/build.sh), so it exports the stamp of the core it ships with: the core makes it the
# ShroudForge-dialect owner only then. tools/sfi/sfi_stage.py check refuses a stage where the two differ.
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
# The stage guard (tools/sfi/public_cut.py): a file of a private build in the tree stops the stage before anything
# is built, and so does a missing guard.
cut_guard=$repo/tools/sfi/public_cut.py
[[ -f $cut_guard ]] || die "missing $cut_guard"
python3 -I "$cut_guard" source "$repo" || die "this tree holds files of a private build (see above)"
# Component sfi (MIT): kiln-sfi.dll, Kiln's interpreter for ShroudForge mods (crates/sfi), at kiln/plugins/kiln-sfi.dll,
# installed by default on both sides, requires core and eml; Settings' ShroudForge card chooses it per game
# (kiln.json shroudforge.backend), and ShroudForge's runtime (component shroudforge) stays the default backend. kiln-eml's read-only
# archive inspector (GPL-3) is part of component eml, never sfi: kiln/tools/eml-inspect/<linux-x86_64|windows-x86_64>/
# with its licence, notice, schema and version.json (its version and SHA-256, which the launcher and kilnctl check);
# the Linux helper is the one file packed and installed executable (crates/kiln-manage/src/bundle.rs). The stage of
# both is checked by tools/sfi/sfi_stage.py.
sfi_stage=$repo/tools/sfi/sfi_stage.py
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
# the ShroudForge cache (KILN_SF_CACHE, below): key, restore (verified), store
sf_cache_key() {
  local sfroot=${KILN_SHROUDFORGE_SRC:-$repo/third_party/shroudforge} xw=${XWIN_ROOT:-$HOME/.cache/cargo-xwin/xwin} work
  work=$(dirname "$out")
  {
    echo "sf-cache 1"
    (cd "$sfroot" && find . -type f -not -path './.git/*' -print0 | LC_ALL=C sort -z | xargs -0 sha256sum)
    rustc -vV; cargo xwin --version; clang-cl --version | head -1; cmake --version | head -1; ninja --version
    node --version; npm --version
    (cd "$xw" && find crt sdk -type f -not -path '*/aarch64/*' -not -path '*/arm64/*' -print0 | LC_ALL=C sort -z | xargs -0 sha256sum)
    printf '%s' "${CARGO_ENCODED_RUSTFLAGS:-}" | sed -e "s|$work|<work>|g" -e "s|$HOME|<home>|g" -e "s|$repo|<src>|g"
  } 2>&1 | sha256sum | cut -c1-24
}
sf_cache_restore() { # key: 0 when the cached component is complete and every file matches its recorded sha256
  local c=$KILN_SF_CACHE/$1
  [[ -s $c/MANIFEST.sha256 && -d $c/shroudforge ]] || return 1
  [[ $(cd "$c/shroudforge" && find . -type f | wc -l) == $(wc -l <"$c/MANIFEST.sha256") ]] || return 1
  (cd "$c/shroudforge" && sha256sum --quiet -c ../MANIFEST.sha256) >/dev/null 2>&1 || return 1
  rm -rf -- "$out/shroudforge-build/shroudforge"
  mkdir -p "$out/shroudforge-build"
  cp -a "$c/shroudforge" "$out/shroudforge-build/shroudforge"
  (cd "$out/shroudforge-build/shroudforge" && sha256sum --quiet -c "$c/MANIFEST.sha256") >/dev/null 2>&1 || return 1
  if [[ -n ${SOURCE_DATE_EPOCH:-} ]]; then find "$out/shroudforge-build/shroudforge" -exec touch -h -d "@$SOURCE_DATE_EPOCH" {} +; fi
}
sf_cache_store() { # key
  local c=$KILN_SF_CACHE/$1 tmp
  [[ -d $out/shroudforge-build/shroudforge ]] || return 1
  [[ -d $c ]] && return 0
  mkdir -p "$KILN_SF_CACHE"
  tmp=$(mktemp -d "$KILN_SF_CACHE/.store.XXXXXX")
  cp -a "$out/shroudforge-build/shroudforge" "$tmp/shroudforge"
  (cd "$tmp/shroudforge" && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum) >"$tmp/MANIFEST.sha256"
  mv -T -- "$tmp" "$c" 2>/dev/null || rm -rf -- "$tmp"
}
if ((build)); then
  # The Rust parts that need nothing from the native or managed builds (kiln-eml, Enshwick's loader, ShroudForge's
  # runtime) build in the background meanwhile, each into its own log; their results are collected below, with the same
  # messages and the same stop on a failure as when they built one after the other. (They share Cargo's build
  # directory with kiln-shroudlink: Cargo's lock lets one at a time into it.)
  [[ -x $repo/gpl/kiln-eml/build.sh ]] || die "missing kiln-eml build source"
  sfsrc=${KILN_SHROUDFORGE_SRC:-$repo/third_party/shroudforge}
  [[ -x $sfsrc/kiln/build.sh ]] || die "missing ShroudForge source: $sfsrc/kiln/build.sh"
  bg_eml="" bg_enshwick="" bg_sf=""
  kill_bg() { local p; for p in $bg_eml $bg_enshwick $bg_sf; do kill "$p" 2>/dev/null || true; done; }
  trap kill_bg EXIT
  # kiln-eml: the GPL-3 EML plugin, its own Cargo workspace in gpl/kiln-eml (docs/kiln/eml.md); lands in bin/plugins
  "$repo/gpl/kiln-eml/build.sh" >"$logs/kiln-eml.log" 2>&1 & bg_eml=$!
  # Enshwick's own loader (MIT), which kiln-enshwick hosts: built from the pinned upstream tag + commit
  env CARGO_TARGET_DIR="$out/cargo-enshwick" "$repo/native/plugins/enshwick/tools/build-runtime.sh" --out "$out/enshwick-runtime" \
    >"$logs/enshwick-loader.log" 2>&1 & bg_enshwick=$!
  # ShroudForge runtime: a separate GPL-3 component built from the frostdev-ops/shroudforge fork (staged below).
  # KILN_SF_CACHE=DIR: its staged output is kept in DIR/<key>, the key a hash of everything the build reads (the fork's
  # whole source tree as exported, the Rust, cargo-xwin, clang, CMake, Ninja, Node and npm versions, the xwin CRT/SDK's
  # content, the Rust flags with this build's folders taken out); a hit is used only when every file still matches the
  # sha256 recorded when it was stored, and gets this build's file times, as the build gives them.
  sf_key="" sf_hit=0
  if [[ -n ${KILN_SF_CACHE:-} ]]; then
    sf_key=$(sf_cache_key) || sf_key=""
    if [[ -n $sf_key ]] && sf_cache_restore "$sf_key"; then
      sf_hit=1
      echo "stage: ShroudForge runtime from the cache ($KILN_SF_CACHE/$sf_key)" >&2
    fi
  fi
  if ((!sf_hit)); then
    "$sfsrc/kiln/build.sh" "$out/shroudforge-build" >"$logs/shroudforge.log" 2>&1 & bg_sf=$!
  fi
  collect() { # name pid: 0 when the background build succeeded, else its log's tail and 1
    if wait "$2"; then return 0; fi
    tail -25 "$logs/$1.log" >&2
    return 1
  }
  # Every native target in one CMake build (Ninja runs the services and plugins side by side); when that fails, each
  # target builds alone, as before, so the failing ones are named (and left out with --allow-missing).
  targets=(kiln-core kiln-proxy kiln-native-hello)
  for dir in "$repo"/native/services/*/ "$repo"/native/plugins/*/; do
    [[ -f $dir/CMakeLists.txt ]] && targets+=("kiln-$(basename "$dir")")
  done
  # the native build's stamp: native/cmake/kiln_build_stamp.sh writes it there before the core and its DLLs build
  native_build_h=$out/cmake/generated/kiln_build.h
  if [[ -n ${KILN_NATIVE_PREBUILT:-} ]]; then
    # KILN_NATIVE_PREBUILT (tools/release/release.sh with KILN_BUILD_REMOTE=win1): the native targets' bin/ as
    # tools/release/lib/remote-win1-native.sh built it on kiln-win1 with this toolchain; a target it does not hold
    # (kiln-shroudlink: Rust, cargo xwin) builds here, alone.
    [[ -s $KILN_NATIVE_PREBUILT/kiln-core.dll && -s $KILN_NATIVE_PREBUILT/dinput8.dll ]] || die "KILN_NATIVE_PREBUILT=$KILN_NATIVE_PREBUILT holds no native build"
    [[ -s $KILN_NATIVE_PREBUILT/kiln_build.h ]] || die "KILN_NATIVE_PREBUILT=$KILN_NATIVE_PREBUILT holds no kiln_build.h (its build's stamp)"
    native_build_h=$KILN_NATIVE_PREBUILT/kiln_build.h
    mkdir -p "$out/cmake/bin"
    cp -a "$KILN_NATIVE_PREBUILT"/. "$out/cmake/bin"/
    rm -f -- "$out/cmake/bin/kiln_build.h"
    for dir in "$repo"/native/plugins/*/; do
      p=$(basename "$dir"); [[ -f $dir/CMakeLists.txt ]] || continue
      grep -q "Builds no plugin" "$dir/CMakeLists.txt" && continue
      [[ -s $out/cmake/bin/plugins/kiln-$p.dll ]] && continue
      built "kiln-$p" "$repo/native/build.sh" "kiln-$p" || miss "native plugin kiln-$p failed to build (log: $logs/kiln-$p.log)"
    done
  elif ! built native-all "$repo/native/build.sh" "${targets[@]}" 2>/dev/null; then
    echo "stage: the one native build failed (log: $logs/native-all.log): building each target alone" >&2
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
  fi
  dn "$repo/managed/Kiln.Host/Kiln.Host.csproj" -o "$out/host" >/dev/null
  for proj in "$repo"/managed/services/*/*.csproj; do
    [[ -f $proj ]] || continue
    name=$(basename "$proj" .csproj)
    built "$name" dn "$proj" -o "$out/host" || miss "managed service $name failed to build (log: $logs/$name.log)"
  done
  built Kiln.Game dn "$repo/managed/Kiln.Game/Kiln.Game.csproj" -o "$out/host" || miss "Kiln.Game failed to build (log: $logs/Kiln.Game.log)"
  # one Kiln.Game per verified game build: host/game/<side>-<sha12>/ (Kiln.Host picks the running exe's)
  built build-game "$repo/tools/typegen/build-game.sh" "$out/host" || miss "Kiln.Game per game build failed (tools/typegen/build-game.sh; log: $logs/build-game.log)"
  built Hello dn "$repo/mods/samples/Hello/Hello.csproj" -o "$out/mods/hello" || miss "Hello sample failed to build (log: $logs/Hello.log)"
  built Kiln.Admin dn "$repo/mods/Kiln.Admin/Kiln.Admin.csproj" -o "$out/mods/kiln-admin" || miss "Kiln.Admin failed to build (log: $logs/Kiln.Admin.log)"
  collect kiln-eml "$bg_eml" || miss "plugin kiln-eml failed to build (log: $logs/kiln-eml.log)"
  bg_eml=""
  # kiln-sfi.dll (crates/sfi, its own workspace and lock) into bin/plugins, and kiln-eml's archive
  # inspector for Linux (build.sh --host; its Windows .exe came with the build above)
  # kiln-sfi.dll exports this build's stamp: the KILN_BUILD the native build generated, passed to its cargo build (one
  # computation per build, also when kiln-win1 built the native part)
  if [[ -f $repo/crates/sfi/kiln-sfi/Cargo.toml ]]; then
    sfi_build=$(sed -n 's/^#define KILN_BUILD "\(.*\)"$/\1/p' "$native_build_h" 2>/dev/null || true)
    if [[ -z $sfi_build ]]; then
      miss "plugin kiln-sfi: the native build left no build stamp in $native_build_h"
    else
      built kiln-sfi env KILN_BUILD="$sfi_build" CARGO_TARGET_DIR="$out/cargo-sfi" "$repo/crates/sfi/kiln-sfi/build.sh" \
        || miss "plugin kiln-sfi failed to build (log: $logs/kiln-sfi.log)"
    fi
  fi
  # KILN_EML_INSPECT_LINUX=FILE: that Linux inspector instead (tools/release/release.sh builds it where the other
  # Linux files are built, for their glibc)
  if [[ -f $repo/gpl/kiln-eml/kiln-eml/src/kiln_eml_inspect.rs ]]; then
    if [[ -n ${KILN_EML_INSPECT_LINUX:-} ]]; then
      [[ -s $KILN_EML_INSPECT_LINUX && ! -L $KILN_EML_INSPECT_LINUX ]] || die "KILN_EML_INSPECT_LINUX=$KILN_EML_INSPECT_LINUX: no such file"
      install -D -m 755 "$KILN_EML_INSPECT_LINUX" "$out/eml-inspect/linux-x86_64/kiln-eml-inspect"
    else
      built kiln-eml-inspect "$repo/gpl/kiln-eml/build.sh" --host \
        || miss "kiln-eml's archive inspector for Linux failed to build (log: $logs/kiln-eml-inspect.log)"
    fi
  fi
  collect enshwick-loader "$bg_enshwick" \
    || miss "Enshwick loader (enshwick.dll for kiln-enshwick) failed to build (log: $logs/enshwick-loader.log)"
  bg_enshwick=""
  sf_rc=0
  if [[ -n $bg_sf ]]; then collect shroudforge "$bg_sf" || sf_rc=1; fi
  bg_sf=""
  trap - EXIT
  ((sf_rc == 0)) || die "ShroudForge runtime build failed (log: $logs/shroudforge.log)"
  if ((!sf_hit)) && [[ -n $sf_key ]]; then sf_cache_store "$sf_key" || warn "could not store the ShroudForge runtime in $KILN_SF_CACHE"; fi
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
# A successful command (or --no-build) is not evidence that its required output exists.
for dir in "$repo"/native/services/*/; do
  svc=$(basename "$dir"); [[ -f $dir/CMakeLists.txt ]] || continue
  # a folder name with "_" may build a DLL named with "-" (native/services/creature_control -> kiln-creature-control.dll)
  [[ -s $bin/services/kiln-$svc.dll || -s $bin/services/kiln-${svc//_/-}.dll ]] ||
    miss "native service kiln-$svc: no $bin/services/kiln-$svc.dll (nor kiln-${svc//_/-}.dll)"
done
for proj in "$repo"/managed/services/*/*.csproj; do
  [[ -f $proj ]] || continue
  name=$(basename "$proj" .csproj)
  [[ -s $out/host/$name.dll ]] || miss "managed service $name: no $out/host/$name.dll"
done
compgen -G "$out/host/game/*/Kiln.Game.dll" >/dev/null || miss "required build output: no $out/host/game/<side>-<sha12>/Kiln.Game.dll (tools/typegen/build-game.sh)"
for required in host/Kiln.Api.dll host/Kiln.Game.dll mods/kiln-admin/Kiln.Admin.dll mods/kiln-admin/kiln.mod.json \
                mods/hello/Hello.dll mods/hello/kiln.mod.json cmake/bin/kiln-native-hello.dll; do
  [[ -s $out/$required ]] || miss "required build output: no $out/$required"
done
dotnet_root=$repo/.cache/dotnet-win-x64/root

rm -rf "$stage"
mkdir -p "$stage"
# core
c=$stage/core k=$stage/core/kiln
mkdir -p "$k"/{services,plugins,host,dotnet}
install -m 644 "$bin/dinput8.dll" "$c/dinput8.dll"
install -m 644 "$bin/kiln-core.dll" "$bin/kiln-core.pdb" "$k/"
# Lab-only native services never go in a bundle: kiln-labbot (bot players on lab servers, native/services/labbot) is
# built and checked like any service, and tools/deploy-server.sh deploys it to labs, but no player's server or game ever
# loads it (it refuses off a lab anyway: kiln.json policy "any" and kiln.net's labPort).
rsync -a --exclude='kiln-labbot.*' --include='*.dll' --include='*.pdb' --exclude='*' "$bin/services/" "$k/services/" \
  || miss "could not stage native services from $bin/services"
rsync -a --include='Kiln.*' --include='game/***' --exclude='*' "$out/host/" "$k/host/"
rsync -a "$dotnet_root/" "$k/dotnet/"
printf '{\n  "policy": "verified-only",\n  "clr": true,\n  "plugins": true,\n  "log_level": "info"\n}\n' > "$k/kiln.json"
# Kiln's shipped compat rules (dev.29): not config, every update replaces them; the player's own go in <configDir>
install -m 644 "$repo/native/core/compat-rules.json" "$k/compat-rules.json"
# Kiln's own in-game texts (runtime/locales/<language>.json, docs/kiln/translating-game.md): every update replaces them
mkdir -p "$k/locales"
install -m 644 "$repo"/runtime/locales/*.json "$k/locales/"
# Kiln's own plugins (native/plugins/*): kiln-emf is the emf component, any other plugin is core
if [[ -d $bin/plugins ]]; then
  for f in "$bin"/plugins/*.dll "$bin"/plugins/*.pdb; do
    [[ -f $f ]] || continue
    case $(basename "$f") in
      kiln-emf.*) comp=emf ;;
      kiln-eml.*) comp=eml ;;
      # kiln-sfi (Kiln's interpreter) is its own component, never core's
      kiln-sfi.*) comp=sfi ;;
      *) comp=core ;;
    esac
    mkdir -p "$stage/$comp/kiln/plugins"
    install -m 644 "$f" "$stage/$comp/kiln/plugins/"
  done
fi
# EMF's unmodified Lua host (MIT): staged only with kiln-emf, in a private subfolder rather than a game drop-in.
if [[ -f $stage/emf/kiln/plugins/kiln-emf.dll ]]; then
  if built emf-lua python3 -I "$repo/tools/package/stage-emf-lua.py" "$out/emf-lua-runtime"; then
    install -d "$stage/emf/kiln/plugins/emf-lua"
    install -m 644 "$out/emf-lua-runtime/"{lua.dll,mod.json,EMF-LICENSE.txt,Lua-LICENSE.txt,SOURCE.txt} "$stage/emf/kiln/plugins/emf-lua/"
  else
    miss "EMF Lua host: could not stage the pinned release (log: $logs/emf-lua.log)"
  fi
fi
# kiln-eml (component eml): the GPL-3 licence and where its source is go next to the DLL
if [[ -f $stage/eml/kiln/plugins/kiln-eml.dll ]]; then
  install -m 644 "$repo/gpl/kiln-eml/LICENSE" "$stage/eml/kiln/plugins/kiln-eml.LICENSE.txt"
  install -m 644 "$repo/gpl/kiln-eml/NOTICE.txt" "$stage/eml/kiln/plugins/kiln-eml.NOTICE.txt"
fi
# kiln-eml's archive inspector (component eml, GPL-3) in its own folder per platform, with its licence,
# notice, schema and version.json (tools/sfi/sfi_stage.py version: its version, protocol and SHA-256); the Linux one 0755
if [[ -f $repo/gpl/kiln-eml/kiln-eml/src/kiln_eml_inspect.rs ]]; then
  for pair in linux-x86_64:kiln-eml-inspect windows-x86_64:kiln-eml-inspect.exe; do
    plat=${pair%%:*} exe=${pair#*:}
    if [[ -s $out/eml-inspect/$plat/$exe ]]; then
      d=$stage/eml/kiln/tools/eml-inspect/$plat
      mkdir -p "$d"
      if [[ $plat == linux-* ]]; then install -m 755 "$out/eml-inspect/$plat/$exe" "$d/$exe"; else install -m 644 "$out/eml-inspect/$plat/$exe" "$d/$exe"; fi
      install -m 644 "$repo/gpl/kiln-eml/LICENSE" "$d/LICENSE.txt"
      install -m 644 "$repo/gpl/kiln-eml/NOTICE.txt" "$d/NOTICE.txt"
      install -m 644 "$repo/gpl/kiln-eml/kiln-eml/schema/archive-inspection.schema.json" "$d/archive-inspection.schema.json"
      python3 -I "$sfi_stage" version "$repo" "$d" "$plat" || die "kiln-eml's archive inspector for $plat: no version.json"
    else
      miss "kiln-eml's archive inspector for $plat: no $out/eml-inspect/$plat/$exe (gpl/kiln-eml/build.sh --host)"
    fi
  done
fi
# ShroudForge runtime: a separate GPL-3 component built from the frostdev-ops/shroudforge fork (third_party/shroudforge,
# or KILN_SHROUDFORGE_SRC); kiln-shroudforge.dll (MIT, core) drives it. docs/kiln/shroudforge-compat.md
sfsrc=${KILN_SHROUDFORGE_SRC:-$repo/third_party/shroudforge}
# (built above, in the background, with the other Rust parts)
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
# the regmap (regmap/README.md: Kiln's registries mapped onto each game build; kiln.content reads
# <game>/kiln/regmap/common/*.json and <side>/<exe sha256>.json). The common rows serve both sides, so they are core's: a
# bundle path belongs to exactly one component (kilnctl bundle create refuses one in two). Each side's build records sit
# beside that side's sigdb.
mkdir -p "$stage/core/kiln/regmap/common"
install -m 644 "$repo"/regmap/common/*.json "$stage/core/kiln/regmap/common/"
# the advanced registry views' alias table (regmap/README.md "views.json"): both sides, so core's too
if [ -f "$repo/regmap/views.json" ]; then install -m 644 "$repo/regmap/views.json" "$stage/core/kiln/regmap/views.json"; fi
for side in server client; do
  r=$stage/sigdb-$side/kiln/regmap
  mkdir -p "$r/$side"
  cp "$repo"/regmap/$side/*.json "$r/$side/" 2>/dev/null || true
done
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
if [[ -f $out/mods/kiln-admin/kiln.mod.json ]]; then stage_mod "$out/mods/kiln-admin" admin kiln-admin; else miss "no Kiln.Admin build: no admin component"; fi
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
# the profile.json forms this core reads: the launcher writes a newer form only for a runtime that declares it
reader = int(re.search(r"#define KILN_PROFILE_READER (\d+)", open(os.path.join(repo, "native/core/src/profile.h")).read()).group(1))
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
            "description": "kiln-emf and the Lua host: run EMF DLL and Lua mods on Kiln's services"},
    "samples": {"sides": ["server"], "default": False, "requires": ["core"],
                "description": "the hello sample mod and the native hello plugin"},
    "eml": {"sides": ["server", "client"], "default": True, "requires": ["core"],
            "description": "kiln-eml (GPL-3.0, gpl/kiln-eml): runs EML mods (Enshrouded Mod Loader) before the game starts"},
    "shroudforge": {"sides": ["server", "client"], "default": True, "requires": ["core"],
                    "description": "ShroudForge's runtime (frostdev-ops/shroudforge fork, GPL-3.0): runs ShroudForge mods through kiln-shroudforge"},
    "enshwick": {"sides": ["server", "client"], "default": True, "requires": ["core"],
                 "description": "Enshwick's own loader (enshwick/enshwick, MIT), built from its pinned tag: runs Enshwick mods through kiln-enshwick"},
    # kiln-sfi (Kiln's interpreter, MIT); kiln-eml's archive inspector it needs is the eml component's
    "sfi": {"sides": ["server", "client"], "default": True, "requires": ["core", "eml"],
            "description": "kiln-sfi (MIT): runs ShroudForge mods in Kiln itself when Settings chooses it; ShroudForge's runtime stays the default"},
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
m = {"format": 1, "kiln": version, "abi": abi, "api": api, "profileReader": reader, "dotnet": nets[-1] if nets else None,
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
# the sfi component and kiln-eml's archive inspector are complete and where they belong
if [[ -f $repo/crates/sfi/kiln-sfi/Cargo.toml ]]; then
  if ((allow_missing)); then
    python3 -I "$sfi_stage" check "$repo" "$stage" || warn "the sfi component or the archive inspector is incomplete (--allow-missing)"
  else
    python3 -I "$sfi_stage" check "$repo" "$stage" || die "the sfi component or the archive inspector is incomplete (see above)"
  fi
fi
# never a developer test build in the stage, and nothing of a private build in a public one
python3 -I "$cut_guard" stage "$repo" "$stage" || { rm -rf "$stage"; die "the stage is refused (see above) and removed"; }
echo "staged Kiln $(cat "$repo/VERSION") in $stage: $(cd "$stage" && find . -type f -not -name kiln-runtime.json | wc -l) files in $(cd "$stage" && ls -d */ | tr -d / | tr '\n' ' ')"
