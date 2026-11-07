#!/usr/bin/env bash
# Builds the Kiln Launcher for Linux: AppImage + deb.
#   tools/launcher/build-linux.sh [--debug] [--bundles appimage,deb] [--runtime ZIP|none] [--version V] [--features F]
#                                 [--config JSON|FILE]... [--nexus]
# --runtime: the Kiln runtime to ship (shipped-runtime.sh). Default: built from this tree.
# --version: the launcher's version (a release: tools/release/version.sh); default the crate's (VERSION).
# --features: cargo features (QA builds: dev-eval, update-test-feeds). --config: more Tauri config to merge, in order.
# --nexus: the Nexus Mods build: no self-update at all (cargo --no-default-features drops the self-update feature and
#   tauri-plugin-updater; src-tauri/tauri.nexus.conf.json drops the updater key from the config). Output in
#   bundles/linux-nexus/. docs/kiln/launcher/releasing.md, "The two builds".
# Output: $KILN_OUT/bundles/linux/ (KILN_OUT defaults to .out/launcher; cargo target $KILN_OUT/cargo).
# Needs: bun, cargo, the WebKitGTK dev packages; Tauri's AppImage tools (linuxdeploy) are cached in ~/.cache/tauri.
set -euo pipefail
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$here/../.." && pwd)
export KILN_OUT=${KILN_OUT:-$repo/.out/launcher}
export CARGO_TARGET_DIR=${CARGO_TARGET_DIR:-$KILN_OUT/cargo}
source "$here/shipped-runtime.sh"
bundles=appimage,deb
profile=()
runtime=
extra=()
nexus=0
features=
while (($#)); do
  case $1 in
    --debug) profile=(--debug); shift ;;
    --bundles) bundles=$2; shift 2 ;;
    --runtime) runtime=$2; shift 2 ;;
    --version) extra+=(--config "{\"version\":\"$2\"}"); shift 2 ;;
    --features) features+=" $2"; extra+=(--features "$2"); shift 2 ;;
    --config) extra+=(--config "$2"); shift 2 ;;
    --nexus) nexus=1; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
# the Nexus Mods build: no self-update feature (no updater plugin, no feeds) and no updater key in the config; its
# config goes last, so nothing passed before can put the key back
runner_args=()
variant=linux
if ((nexus)); then
  [[ $features == *update-test-feeds* || $features == *self-update* || $features == *tauri-plugin-updater* ]] && { echo "--nexus has no self-update: drop --features $features" >&2; exit 2; }
  extra+=(--config "$repo/launcher/src-tauri/tauri.nexus.conf.json")
  runner_args=(-- --no-default-features)
  variant=linux-nexus
fi
resources=$(shipped_runtime_resources "$runtime")
cd "$repo/launcher"
[[ -d node_modules ]] || bun install --frozen-lockfile
# AppImage: linuxdeploy runs as an AppImage itself; without FUSE it can extract and run
export APPIMAGE_EXTRACT_AND_RUN=${APPIMAGE_EXTRACT_AND_RUN:-1}
export NO_STRIP=${NO_STRIP:-true}
# Tauri reuses these paths across distributions. Copy only packages from this invocation.
kind=release; ((${#profile[@]})) && kind=debug
rm -rf "$CARGO_TARGET_DIR/$kind/bundle"
bun x tauri build "${profile[@]}" --bundles "$bundles" --config "{\"bundle\":{\"resources\":$resources}}" "${extra[@]}" "${runner_args[@]}"
out=$KILN_OUT/bundles/$variant
mkdir -p "$out"
rm -f "$out/"*.AppImage "$out/"*.deb "$out/"*.rpm "$out/"*.sig
for f in "$CARGO_TARGET_DIR/$kind/bundle/appimage/"*.AppImage "$CARGO_TARGET_DIR/$kind/bundle/deb/"*.deb "$CARGO_TARGET_DIR/$kind/bundle/rpm/"*.rpm; do
  [[ -f $f ]] && cp -f "$f" "$out/" && echo "-> $out/$(basename "$f") ($(du -h "$f" | cut -f1))"
done
cp -f "$CARGO_TARGET_DIR/$kind/kiln-launcher" "$out/" && echo "-> $out/kiln-launcher"
