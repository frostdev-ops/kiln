#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Puts Enshwick mods into a lab dedicated server that Kiln is already deployed to (tools/deploy-server.sh ships
# kiln/plugins/enshwick/enshwick.dll when tools/build-runtime.sh has run), in Enshwick's own layout:
#   mods/you.material-stacks/   a data pack from Enshwick's examples (stacks of materials to 999)
#   mods/you.welcome/           a Luau mod from Enshwick's examples (greets players, counts who is online)
#   mods/<id>/<mod>.dll         any DLL mods given with --dll <id>=<file.dll> (built from enshwick/mods)
#
#   tests/lab.sh <server dir> <enshwick source checkout> [--dll <id>=<file.dll>]...
# Then start the lab and read kiln/logs/kiln.log ([enshwick] lines), logs/enshwick_*.log and kiln/state/enshwick.json.
set -euo pipefail
server=${1:?usage: $0 <server dir> <enshwick source> [--dll id=file.dll]...}
src=${2:?usage: $0 <server dir> <enshwick source> [--dll id=file.dll]...}
shift 2
[[ -f $server/enshrouded_server.exe && -f $server/kiln/plugins/kiln-enshwick.dll ]] || { echo "deploy Kiln to $server first" >&2; exit 1; }
[[ -f $server/kiln/plugins/enshwick/enshwick.dll ]] || echo "note: no kiln/plugins/enshwick/enshwick.dll; run tools/build-runtime.sh and deploy again" >&2
mkdir -p "$server/mods"
for ex in you.material-stacks you.welcome; do
  rm -rf "$server/mods/$ex"
  rsync -a --exclude='*.schema.json' --exclude='README.md' --exclude='types/' "$src/examples/$ex/" "$server/mods/$ex/"
done
while (($#)); do
  case $1 in
    --dll) id=${2%%=*}; file=${2#*=}; mkdir -p "$server/mods/$id"; install -m 644 "$file" "$server/mods/$id/"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
echo "Enshwick lab mods in $server/mods:"; ls "$server/mods"
