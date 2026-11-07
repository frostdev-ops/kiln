#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Builds kiln-eml.dll (Windows x64, cargo xwin) into $KILN_OUT/cmake/bin/plugins/, next to Kiln's other plugins, so
# tools/package/stage.sh and tools/deploy-server.sh pick it up. Also builds the host runner (kiln-eml-run) with
# --host.
#   gpl/kiln-eml/build.sh [--host] [--test]
# KILN_OUT (default <repo>/native/build) and CARGO_TARGET_DIR (default $KILN_OUT/cargo-eml) as in Kiln's builds.
# The release passes CARGO_ENCODED_RUSTFLAGS (path remapping); the static CRT flag is added to it here.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
KILN_OUT=${KILN_OUT:-$repo/native/build}
export CARGO_TARGET_DIR=${CARGO_TARGET_DIR:-$KILN_OUT/cargo-eml}
host=0 test=0
for a in "$@"; do
  case $a in
    --host) host=1 ;;
    --test) test=1 ;;
    *) echo "unknown option $a" >&2; exit 2 ;;
  esac
done
cd "$here"
# Host regressions run even if the Windows cross toolchain is unavailable.
if ((test)); then cargo test --release --locked -p kiln-eml -p mod-loader-lua -p kfc-base; fi
win=x86_64-pc-windows-msvc
# the game ships no VC runtime for us: link the CRT statically (as kiln-core does)
flags=${CARGO_ENCODED_RUSTFLAGS:-}
flags=${flags:+$flags$'\x1f'}-Ctarget-feature=+crt-static
CARGO_ENCODED_RUSTFLAGS=$flags cargo xwin build --release --locked -p kiln-eml --lib --target $win
mkdir -p "$KILN_OUT/cmake/bin/plugins"
install -m 755 "$CARGO_TARGET_DIR/$win/release/kiln_eml.dll" "$KILN_OUT/cmake/bin/plugins/kiln-eml.dll"
echo "kiln-eml: $KILN_OUT/cmake/bin/plugins/kiln-eml.dll"
if ((host)); then
  cargo build --release --locked -p kiln-eml --bin kiln-eml-run
  echo "kiln-eml-run: $CARGO_TARGET_DIR/release/kiln-eml-run"
fi
