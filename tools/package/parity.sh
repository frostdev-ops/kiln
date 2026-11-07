#!/usr/bin/env bash
# Parity test: a runtime bundle installed into a fake server folder gives the same tree as tools/deploy-server.sh
# from the same build (diff -r; the installer's receipt and backups excepted).
#   tools/package/parity.sh <bundle.zip> [--samples] [--keep]
# KILN_OUT must be the build the bundle was staged from. Uses scratch folders under $KILN_OUT/parity; never a real
# server. Exit 0 when the trees are equal.
set -euo pipefail
repo=$(cd "$(dirname "$0")/../.." && pwd)
zip=$(readlink -f "${1:?usage: $0 <bundle.zip> [--samples] [--keep]}")
shift
samples=0 keep=0
for a in "$@"; do
  case $a in
    --samples) samples=1 ;;
    --keep) keep=1 ;;
    *) echo "unknown option $a" >&2; exit 2 ;;
  esac
done
: "${KILN_OUT:?set KILN_OUT to the build the bundle was staged from}"
kilnctl=${KILNCTL:-${CARGO_TARGET_DIR:-$repo/target}/debug/kilnctl}
work=$KILN_OUT/parity
rm -rf "$work"
mkdir -p "$work/deploy" "$work/bundle"
for d in deploy bundle; do printf 'MZ fake server exe for the parity test\n' > "$work/$d/enshrouded_server.exe"; done
args=(--no-build --policy verified-only)
components=core,sigdb-server
((samples)) && { args+=(--samples); components+=,samples; }
[[ -f $KILN_OUT/mods/kiln-admin/kiln.mod.json ]] && components+=,admin
[[ -f $KILN_OUT/cmake/bin/plugins/kiln-emf.dll ]] && components+=,emf
[[ -f $KILN_OUT/cmake/bin/plugins/kiln-eml.dll ]] && components+=,eml
[[ -f $KILN_OUT/shroudforge-build/shroudforge/shroudforge-runtime.dll ]] && components+=,shroudforge
[[ -f $KILN_OUT/enshwick-runtime/enshwick.dll ]] && components+=,enshwick
"$repo/tools/deploy-server.sh" "$work/deploy" "${args[@]}" >/dev/null
KILN_DATA_DIR=$work/data "$kilnctl" bundle install "$zip" --dir "$work/bundle" --components "$components" >/dev/null
if diff -r -x .install.json -x backups "$work/deploy" "$work/bundle"; then
  n=$(cd "$work/bundle" && find . -type f -not -name .install.json | wc -l)
  echo "parity: ok ($n files; components $components; diff -r against tools/deploy-server.sh ${args[*]})"
  ((keep)) || rm -rf "$work"
else
  echo "parity: DIFFERENT (trees kept in $work)" >&2
  exit 1
fi
