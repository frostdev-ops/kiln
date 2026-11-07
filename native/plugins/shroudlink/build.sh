#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Builds kiln-shroudlink.dll (shroudlink's server core hosted as a Kiln plugin; Windows x64, cargo xwin) into
# <out>/plugins/ (default $KILN_OUT/cmake/bin/plugins/), next to Kiln's other plugins, so tools/deploy-server.sh and
# tools/package/stage.sh pick it up. CMakeLists.txt runs this for the target kiln-shroudlink.
#   native/plugins/shroudlink/build.sh [--out <bin dir>] [--test]
# --test also runs the host tests (cargo xwin test under Wine). With SHROUDLINK_SRC=<clone of bjck/shroudlink v0.8.0>
# it first builds shroudlink's example mod there, which the loader tests load.
# KILN_OUT (default <repo>/native/build) and CARGO_TARGET_DIR (default $KILN_OUT/cargo-shroudlink) as in Kiln's builds.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../../.." && pwd)
KILN_OUT=${KILN_OUT:-$repo/native/build}
export CARGO_TARGET_DIR=${CARGO_TARGET_DIR:-$KILN_OUT/cargo-shroudlink}
out=$KILN_OUT/cmake/bin test=0
while (($#)); do
  case $1 in
    --out) out=$2; shift ;;
    --test) test=1 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
  shift
done
cd "$here"
win=x86_64-pc-windows-msvc
# retour's libudis86 calls memset without <string.h>; its gcc-style -include is ignored by clang-cl, so a clang-cl
# wrapper force-includes it (cargo xwin always sets CC to clang-cl, so the wrapper goes first on PATH)
wrap=$CARGO_TARGET_DIR/ccwrap
mkdir -p "$wrap"
real=$(PATH=${PATH//$wrap:/} command -v clang-cl)
printf '#!/bin/sh\nexec "%s" /FIstring.h "$@"\n' "$real" > "$wrap/clang-cl"
chmod +x "$wrap/clang-cl"
export PATH=$wrap:$PATH
# the game ships no VC runtime for us: link the CRT statically (as kiln-core does)
flags=${CARGO_ENCODED_RUSTFLAGS:-}
flags=${flags:+$flags$'\x1f'}-Ctarget-feature=+crt-static
CARGO_ENCODED_RUSTFLAGS=$flags cargo xwin build --release --locked -p shroudlink-server --features kiln --target $win
mkdir -p "$out/plugins"
install -m 755 "$CARGO_TARGET_DIR/$win/release/shroudlink_server.dll" "$out/plugins/kiln-shroudlink.dll"
echo "kiln-shroudlink: $out/plugins/kiln-shroudlink.dll"
if ((test)); then
  # shroudlink's own example mod, which upstream's loader test and Kiln's loader test load (release/hello_mod.dll)
  ref=${SHROUDLINK_SRC:-}
  if [[ -n $ref && -d $ref/mod/examples/hello_mod ]]; then
    (cd "$ref/mod" && cargo xwin build --release --locked -p hello_mod --target $win)
  fi
  CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_RUNNER=${CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_RUNNER:-wine} \
    CARGO_ENCODED_RUSTFLAGS=$flags cargo xwin test --release --locked -p shroudlink-server --features kiln --target $win
fi
