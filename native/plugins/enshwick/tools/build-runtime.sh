#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Builds Enshwick's loader (enshwick.dll, MIT) from source at a pinned tag and commit, for kiln-enshwick to host.
#
#   native/plugins/enshwick/tools/build-runtime.sh [--tag TAG --commit SHA] [--out DIR] [--examples]
#
# Needs: git, Python 3, network (clone/fetch; rustup fetches the tag's rust-toolchain.toml toolchain), Rust,
# cargo-xwin off Windows. The default pin is TAG/COMMIT below; another tag needs its full commit id too. The checkout
# must be that commit with no local changes, or the build stops (a reused checkout is checked the same way).
# Re-fetch the tag on reuse, verify the remote, and compile a private copy of the pinned blobs, never the cached
# working tree. Submodules, symlinks and LFS inputs need a separate pinning policy and are refused here.
# KILN_OUT (default native/build) holds the checkout ($KILN_OUT/runtime-src/enshwick-<tag>); the cargo target dir is
# CARGO_TARGET_DIR (default $KILN_OUT/cargo). The result goes to DIR (default $KILN_OUT/enshwick-runtime/):
# enshwick.dll, Enshwick's LICENSE, enshwick-version.txt (tag, then commit; kiln-enshwick logs the first line).
# tools/package/stage.sh runs this for the runtime bundle's "enshwick" component (kiln/plugins/enshwick/).
# --examples also builds the example mods the plugin's lab test runs (hello-c).
set -euo pipefail

TAG="v1.0.0-alpha.14"
COMMIT="8fa382949551f0256a6505ed161cb28e5a0ea8f5"
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
KILN_OUT="$(realpath -m -- "${KILN_OUT:-$ROOT/native/build}")"
SRC="$KILN_OUT/runtime-src/enshwick-$TAG"
OUT="$(realpath -m -- "${OUT:-$KILN_OUT/enshwick-runtime}")"
CARGO_TARGET_DIR="$(realpath -m -- "${CARGO_TARGET_DIR:-$KILN_OUT/cargo}")"
export CARGO_TARGET_DIR
TARGET="x86_64-pc-windows-msvc"
export GIT_NO_REPLACE_OBJECTS=1 GIT_LFS_SKIP_SMUDGE=1

mkdir -p "$KILN_OUT/runtime-src"
[[ $(realpath -m -- "$SRC") == "$SRC" ]] || die "checkout path contains a symlink: $SRC"
# Concurrent invocations must not share a checkout while fetching. Never remove someone else's lock.
mkdir "$SRC.lock" 2>/dev/null || die "checkout is locked: $SRC.lock"
build_src=""
cleanup() { [[ -z $build_src ]] || rm -rf -- "$build_src"; rmdir -- "$SRC.lock"; }
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP
if [[ -e $SRC || -L $SRC ]]; then
    [[ -d $SRC/.git && ! -L $SRC/.git ]] || die "not a standalone checkout: $SRC"
else
    git -c advice.detachedHead=false -c core.hooksPath=/dev/null -c core.autocrlf=false \
        clone --quiet --no-local --depth 1 --branch "$TAG" "$REPO" "$SRC"
fi
# Anchor Git and file reads to this directory even if its cached pathname is replaced during the build.
cd -P -- "$SRC"
[[ $(git rev-parse --show-toplevel) == "$PWD" ]] || die "checkout worktree was redirected: $SRC"
head=$(git rev-parse --verify 'HEAD^{commit}')
[[ $head == "$COMMIT" ]] || die "$SRC is at $head, not the pinned $TAG = $COMMIT (a moved tag or another checkout; refusing to build)"
[[ $(git config --get-all remote.origin.url) == "$REPO" && $(git remote get-url --all origin) == "$REPO" ]] \
    || die "checkout remote changed; expected $REPO"
git -c core.hooksPath=/dev/null fetch --quiet --no-tags --depth 1 origin "refs/tags/$TAG"
fetched=$(git rev-parse --verify 'FETCH_HEAD^{commit}')
[[ $fetched == "$COMMIT" ]] || die "fetched $TAG is $fetched, not the pinned $COMMIT"
# Object names alone do not authenticate a reused object database: cat-file does not rehash a loose blob on read.
git fsck --full --no-reflogs >/dev/null || die "cached Git objects failed integrity verification: $SRC"
status=$(git -c core.fsmonitor=false status --porcelain --untracked-files=all --ignored)
[[ -z $status ]] \
    || die "$SRC has local changes (including ignored files); delete it and run again"
build_src=$(mktemp -d "$KILN_OUT/runtime-src/.enshwick-build.XXXXXX")
# Compare every tracked byte as well: status can hide assume-unchanged/skip-worktree changes. Populate the private
# build from Git objects so replacing the cached path after this check cannot inject code or a different licence.
python3 -I - "$COMMIT" "$build_src" <<'PY'
import os, pathlib, re, subprocess, sys
commit, dest = sys.argv[1:]
root = pathlib.Path.cwd()
seen = set()
for row in subprocess.check_output(["git", "ls-tree", "-rz", commit]).split(b"\0"):
    if not row:
        continue
    meta, raw = row.split(b"\t", 1)
    mode, kind, oid = meta.split()
    rel = os.fsdecode(raw)
    if mode not in (b"100644", b"100755") or kind != b"blob":
        sys.exit(f"enshwick build-runtime: submodule or symlink is not allowed: {rel}")
    path = pathlib.PurePosixPath(rel)
    if path.is_absolute() or ".." in path.parts or ".git" in path.parts:
        sys.exit(f"enshwick build-runtime: unsafe source path: {rel}")
    data = subprocess.check_output(["git", "cat-file", "blob", oid.decode("ascii")])
    if (path.name == ".gitmodules" or data.startswith(b"version https://git-lfs.github.com/spec/v1")
            or (path.name == ".gitattributes" and re.search(rb"filter\s*=\s*lfs\b", data))):
        sys.exit(f"enshwick build-runtime: submodule/LFS input is not allowed: {rel}")
    source = root / rel
    if source.resolve() != source or not source.is_file() or source.read_bytes() != data:
        sys.exit(f"enshwick build-runtime: local changes or symlink: {rel}")
    output = pathlib.Path(dest) / rel
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(data)
    output.chmod(int(mode, 8) & 0o777)
    seen.add(rel)
if "Cargo.lock" not in seen:
    sys.exit("enshwick build-runtime: pinned source has no Cargo.lock")
PY
echo "enshwick source: $SRC ($TAG, $head)"

CARGO=(cargo)
[[ "$(uname -s)" == MINGW* || "$(uname -s)" == MSYS* ]] || CARGO=(cargo xwin)
PKGS=(-p enshwick-loader)
[[ $EXAMPLES -eq 1 ]] && PKGS+=(-p enshwick-example-hello-c)
# Both forms override Enshwick's target config. Preserve Cargo's precedence and whitespace splitting for RUSTFLAGS,
# then restore its CRT/CPU settings. Remap the private random path too, keeping release builds reproducible.
CARGO_ENCODED_RUSTFLAGS="$(python3 -I - "$build_src" <<'PY'
import os, sys
flags = (os.environ["CARGO_ENCODED_RUSTFLAGS"].split("\x1f") if "CARGO_ENCODED_RUSTFLAGS" in os.environ
         else os.environ.get("RUSTFLAGS", "").split())
flags = [f for f in flags if f]
flags += ["-Ctarget-feature=+crt-static", "-Ctarget-cpu=x86-64-v2", "--remap-path-prefix=" + sys.argv[1] + "=/enshwick"]
print("\x1f".join(flags))
PY
)"
export CARGO_ENCODED_RUSTFLAGS
(cd "$build_src" && nice -n 10 "${CARGO[@]}" build --release --locked --target "$TARGET" "${PKGS[@]}")

mkdir -p "$OUT"
install -m 644 "$CARGO_TARGET_DIR/$TARGET/release/enshwick_loader.dll" "$OUT/enshwick.dll"
install -m 644 "$build_src/LICENSE" "$OUT/LICENSE"
printf '%s\n%s\n' "$TAG" "$head" > "$OUT/enshwick-version.txt"
echo "wrote $OUT/enshwick.dll"
