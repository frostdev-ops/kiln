#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Builds Enshwick's loader (enshwick.dll, MIT) from source at a pinned tag and commit, for kiln-enshwick to host.
#
#   native/plugins/enshwick/tools/build-runtime.sh [--tag TAG --commit SHA] [--out DIR] [--examples]
#
# Needs: git, network (the clone; rustup fetches the toolchain the tag's rust-toolchain.toml names), Rust,
# cargo-xwin off Windows. The default pin is TAG/COMMIT below; another tag needs its full commit id too. The checkout
# must be that commit with no local changes, or the build stops (a reused checkout is checked the same way).
# KILN_OUT (default native/build) holds the checkout ($KILN_OUT/runtime-src/enshwick-<tag>); the cargo target dir is
# CARGO_TARGET_DIR (default $KILN_OUT/cargo). The result goes to DIR (default $KILN_OUT/enshwick-runtime/):
# enshwick.dll, Enshwick's LICENSE, enshwick-version.txt (tag, then commit; kiln-enshwick logs the first line).
# tools/package/stage.sh runs this for the runtime bundle's "enshwick" component (kiln/plugins/enshwick/).
# --examples also builds the example mods the plugin's lab test runs (hello-c).
set -euo pipefail

TAG="v1.0.0-alpha.13"
COMMIT="945a1b8f0c9d1971679d9f3bc3dcecc127a5bcad"
REPO="https://github.com/enshwick/enshwick.git"
OUT=""
EXAMPLES=0
tag_set=0 commit_set=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --tag) TAG="$2"; tag_set=1; shift 2 ;;
        --commit) COMMIT="$2"; commit_set=1; shift 2 ;;
        --out) OUT="$2"; shift 2 ;;
        --examples) EXAMPLES=1; shift ;;
        *) echo "usage: $0 [--tag TAG --commit SHA] [--out DIR] [--examples]" >&2; exit 2 ;;
    esac
done
die() { echo "enshwick build-runtime: $*" >&2; exit 1; }
((tag_set == commit_set)) || die "--tag and --commit go together (the pin is $TAG = $COMMIT)"
[[ $COMMIT =~ ^[0-9a-f]{40}$ ]] || die "--commit must be a full 40-hex commit id: $COMMIT"
[[ $TAG =~ ^[A-Za-z0-9._-]+$ ]] || die "bad tag: $TAG"

ROOT="$(cd "$(dirname "$0")/../../../.." && pwd)"
KILN_OUT="${KILN_OUT:-$ROOT/native/build}"
SRC="$KILN_OUT/runtime-src/enshwick-$TAG"
OUT="${OUT:-$KILN_OUT/enshwick-runtime}"
export CARGO_TARGET_DIR="${CARGO_TARGET_DIR:-$KILN_OUT/cargo}"
TARGET="x86_64-pc-windows-msvc"

if [[ ! -d "$SRC/.git" ]]; then
    rm -rf "$SRC"
    mkdir -p "$(dirname "$SRC")"
    git -c advice.detachedHead=false clone --quiet --depth 1 --branch "$TAG" "$REPO" "$SRC"
fi
head=$(git -C "$SRC" rev-parse --verify 'HEAD^{commit}')
[[ $head == "$COMMIT" ]] || die "$SRC is at $head, not the pinned $TAG = $COMMIT (a moved tag or another checkout; refusing to build)"
[[ -z $(git -C "$SRC" status --porcelain --untracked-files=all) ]] || die "$SRC has local changes; delete it and run again"
echo "enshwick source: $SRC ($TAG, $head)"

CARGO=(cargo)
[[ "$(uname -s)" == MINGW* || "$(uname -s)" == MSYS* ]] || CARGO=(cargo xwin)
PKGS=(-p enshwick-loader)
[[ $EXAMPLES -eq 1 ]] && PKGS+=(-p hello-c)
# Enshwick's .cargo/config.toml gives the Windows target a static CRT and x86-64-v2; CARGO_ENCODED_RUSTFLAGS (set by
# release.sh for path remapping) replaces config rustflags entirely, so carry them over when it is set
if [[ -n ${CARGO_ENCODED_RUSTFLAGS:-} ]]; then
    export CARGO_ENCODED_RUSTFLAGS="$CARGO_ENCODED_RUSTFLAGS"$'\x1f'"-Ctarget-feature=+crt-static"$'\x1f'"-Ctarget-cpu=x86-64-v2"
fi
(cd "$SRC" && nice -n 10 "${CARGO[@]}" build --release --locked --target "$TARGET" "${PKGS[@]}")

mkdir -p "$OUT"
install -m 644 "$CARGO_TARGET_DIR/$TARGET/release/enshwick_loader.dll" "$OUT/enshwick.dll"
install -m 644 "$SRC/LICENSE" "$OUT/LICENSE"
printf '%s\n%s\n' "$TAG" "$head" > "$OUT/enshwick-version.txt"
echo "wrote $OUT/enshwick.dll"
