#!/usr/bin/env bash
# Builds the runtime bundle kiln-runtime-<VERSION>+<yyyymmdd>.<gitsha>.zip (docs/kiln/launcher/bundle.md):
#   tools/package/package.sh [--out DIR] [--stage DIR] [--no-build]
# = tools/package/stage.sh <stage> + kilnctl bundle create <stage> --out <DIR> (+ verify). Deterministic: entry times
# and "created" are the commit time (SOURCE_DATE_EPOCH, default `git log -1 --format=%ct`), the git id is
# KILN_GIT (default `git rev-parse --short=7 HEAD`); the same tree gives a byte-identical zip.
# KILN_OUT (default <repo>/.out/package) holds the build, CARGO_TARGET_DIR the kilnctl build.
set -euo pipefail
repo=$(cd "$(dirname "$0")/../.." && pwd)
out=$repo/dist stage="" stage_args=()
while (($#)); do
  case $1 in
    --out) out=$2; shift ;;
    --stage) stage=$2; shift ;;
    --no-build) stage_args+=(--no-build) ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
  shift
done
export KILN_OUT=${KILN_OUT:-$repo/.out/package}
# beside the build folder, not inside it: stage.sh refuses a stage folder that overlaps KILN_OUT
stage=${stage:-${KILN_OUT%/}-stage}
epoch=${SOURCE_DATE_EPOCH:-$(git -C "$repo" log -1 --format=%ct)}
git=${KILN_GIT:-$(git -C "$repo" rev-parse --short=7 HEAD)}
if [[ -n $(git -C "$repo" status --porcelain --untracked-files=no 2>/dev/null) ]]; then
  echo "package: note: the tree has uncommitted changes; the bundle is labelled $git but holds them" >&2
fi
"$repo/tools/package/stage.sh" "$stage" "${stage_args[@]}"
cargo build -q --release -p kilnctl --manifest-path "$repo/Cargo.toml"
kilnctl=${CARGO_TARGET_DIR:-$repo/target}/release/kilnctl
"$kilnctl" bundle create "$stage" --out "$out" --git "$git" --epoch "$epoch"
