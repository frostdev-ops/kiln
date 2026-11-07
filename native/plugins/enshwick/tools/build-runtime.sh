#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Builds Enshwick's loader (enshwick.dll, MIT) from source at a pinned tag, for kiln-enshwick to host.
#
#   native/plugins/enshwick/tools/build-runtime.sh [--tag v1.0.0-alpha.13] [--out DIR] [--examples]
#
# Needs: git, Rust (the toolchain the tag's rust-toolchain.toml names), cargo-xwin off Windows.
# KILN_OUT (default native/build) holds the checkout and the cargo target dir. The result goes to
# DIR/enshwick.dll (default $KILN_OUT/enshwick-runtime/), with Enshwick's LICENSE next to it.
# --examples also builds the example mods the plugin's lab test runs (hello-c).
set -euo pipefail

TAG="v1.0.0-alpha.13"
REPO="https://github.com/enshwick/enshwick.git"
OUT=""
EXAMPLES=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --tag) TAG="$2"; shift 2 ;;
        --out) OUT="$2"; shift 2 ;;
        --examples) EXAMPLES=1; shift ;;
        *) echo "usage: $0 [--tag TAG] [--out DIR] [--examples]" >&2; exit 2 ;;
    esac
done

ROOT="$(cd "$(dirname "$0")/../../../.." && pwd)"
KILN_OUT="${KILN_OUT:-$ROOT/native/build}"
SRC="$KILN_OUT/runtime-src/enshwick-$TAG"
OUT="${OUT:-$KILN_OUT/enshwick-runtime}"
export CARGO_TARGET_DIR="${CARGO_TARGET_DIR:-$KILN_OUT/cargo}"
TARGET="x86_64-pc-windows-msvc"

if [[ ! -d "$SRC/.git" ]]; then
    mkdir -p "$(dirname "$SRC")"
    git clone --quiet --depth 1 --branch "$TAG" "$REPO" "$SRC"
fi
echo "enshwick source: $SRC ($(git -C "$SRC" rev-parse HEAD))"

CARGO=(cargo)
[[ "$(uname -s)" == MINGW* || "$(uname -s)" == MSYS* ]] || CARGO=(cargo xwin)
PKGS=(-p enshwick-loader)
[[ $EXAMPLES -eq 1 ]] && PKGS+=(-p hello-c)
(cd "$SRC" && nice -n 10 "${CARGO[@]}" build --release --locked --target "$TARGET" "${PKGS[@]}")

mkdir -p "$OUT"
cp "$CARGO_TARGET_DIR/$TARGET/release/enshwick_loader.dll" "$OUT/enshwick.dll"
cp "$SRC/LICENSE" "$OUT/LICENSE"
printf '%s\n%s\n' "$TAG" "$(git -C "$SRC" rev-parse HEAD)" > "$OUT/enshwick-version.txt"
echo "wrote $OUT/enshwick.dll"
