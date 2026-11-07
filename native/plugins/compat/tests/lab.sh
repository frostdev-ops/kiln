#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Puts the compatibility plugins' lab probes into a lab dedicated server that Kiln is already deployed to
# (tools/deploy-server.sh), each in its engine's own folder:
#   greeneye/glab/            greeneye_lab.dll (side server): find + read on the game's code, keep, poll; no hooks
#   mods/shroudtopia_lab.dll  a Shroudtopia mod with server support: config getters, lifecycle, read-only memory helpers
#   mods/<flame mod>/         optional: a Flame mod folder (mod.json, flame.json, its DLL and data), copied as is
#   tests/lab.sh <server dir> [<flame mod folder>]
# KILN_OUT names the build (bin/compat-tests). Then start the lab and read kiln/logs/kiln.log and kiln/state/*.json.
set -euo pipefail
server=${1:?usage: $0 <server dir> [<flame mod folder>]}
flame=${2:-}
bin=${KILN_OUT:?set KILN_OUT}/cmake/bin/compat-tests
[[ -f $server/enshrouded_server.exe && -f $server/kiln/plugins/kiln-greeneye.dll ]] || { echo "deploy Kiln to $server first" >&2; exit 1; }
mkdir -p "$server/greeneye/glab" "$server/mods"
install -m 644 "$bin/greeneye_lab.dll" "$server/greeneye/glab/"
printf '{"id": "glab", "name": "Greeneye Lab Probe", "version": "1.0", "dll": "greeneye_lab.dll", "side": "server", "api": 2}\n' \
  > "$server/greeneye/glab/mod.json"
install -m 644 "$bin/shroudtopia_lab.dll" "$server/mods/"
mkdir -p "$server/kiln/config/shroudtopia"
cat > "$server/kiln/config/shroudtopia/shroudtopia.json" <<'JSON'
{
  "active": true, "bootDelay": 3000, "updateDelay": 500, "enableLogging": true, "logLevel": "INFO",
  "mods": {
    "Kiln Lab Probe": {"active": true, "flag": true, "count": 5, "ratio": 1.5, "title": "kiln", "big": 12345678901,
                       "bools": [true, false], "ints": [1, 2, 3], "floats": [1.5, 2.5], "strings": ["a", "b"], "bigs": [1, 2]}
  }
}
JSON
if [[ -n $flame ]]; then
  [[ -f $flame/flame.json ]] || { echo "$flame has no flame.json" >&2; exit 1; }
  rm -rf "$server/mods/$(basename "$flame")"
  cp -r "$flame" "$server/mods/"
fi
echo "lab probes installed in $server"
