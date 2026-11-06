#!/usr/bin/env bash
# Builds the Kiln Launcher for Linux: AppImage + deb.
#   tools/launcher/build-linux.sh [--debug] [--bundles appimage,deb] [--runtime ZIP|none] [--version V] [--features F]
#                                 [--config JSON|FILE]...
# --runtime: the Kiln runtime to ship (shipped-runtime.sh). Default: built from this tree.
# --version: the launcher's version (a release: tools/release/version.sh); default the crate's (VERSION).
# --features: cargo features (QA builds: dev-eval, update-test-feeds). --config: more Tauri config to merge, in order.
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
while (($#)); do
  case $1 in
    --debug) profile=(--debug); shift ;;
    --bundles) bundles=$2; shift 2 ;;
    --runtime) runtime=$2; shift 2 ;;
    --version) extra+=(--config "{\"version\":\"$2\"}"); shift 2 ;;
    --features) extra+=(--features "$2"); shift 2 ;;
    --config) extra+=(--config "$2"); shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
resources=$(shipped_runtime_resources "$runtime")
cd "$repo/launcher"
[[ -d node_modules ]] || bun install --frozen-lockfile
# AppImage: linuxdeploy runs as an AppImage itself; without FUSE it can extract and run
export APPIMAGE_EXTRACT_AND_RUN=${APPIMAGE_EXTRACT_AND_RUN:-1}
export NO_STRIP=${NO_STRIP:-true}
bun x tauri build "${profile[@]}" --bundles "$bundles" --config "{\"bundle\":{\"resources\":$resources}}" "${extra[@]}"
kind=release; ((${#profile[@]})) && kind=debug
out=$KILN_OUT/bundles/linux
mkdir -p "$out"
for f in "$CARGO_TARGET_DIR/$kind/bundle/appimage/"*.AppImage "$CARGO_TARGET_DIR/$kind/bundle/deb/"*.deb "$CARGO_TARGET_DIR/$kind/bundle/rpm/"*.rpm; do
  [[ -f $f ]] && cp -f "$f" "$out/" && echo "-> $out/$(basename "$f") ($(du -h "$f" | cut -f1))"
done
cp -f "$CARGO_TARGET_DIR/$kind/kiln-launcher" "$out/" && echo "-> $out/kiln-launcher"
