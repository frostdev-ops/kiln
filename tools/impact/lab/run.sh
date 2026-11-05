#!/usr/bin/env bash
# tools/impact lab check (player-free): cook the override recipe, build the ImpactLab mod and copy it into a lab.
#   tools/impact/lab/run.sh [lab name]        (default: impact; the lab must exist and have Kiln deployed)
# Then: tools/lab/lab.sh start <lab>; grep 'impact-lab:' in the lab's kiln.log; tools/lab/lab.sh stop <lab>.
# The game is only read (the cook reads the server's own data in the lab's folder).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../.." && pwd)
lab=${1:-impact}
server=$HOME/srv/enshrouded/labs/$lab
export KILN_OUT=${KILN_OUT:-$root/.out/impact}
mod=$here/ImpactLab
[[ -f $server/enshrouded_server.exe ]] || { echo "no lab $lab" >&2; exit 1; }
python3 "$root/tools/impact/impact.py" --game "$server" cook "$mod/impact/cook.json" --update-manifest
dotnet build "$mod/ImpactLab.csproj" -c Release --artifacts-path "$KILN_OUT/dotnet" -v q -nologo -o "$KILN_OUT/mods/impact-lab"
mkdir -p "$KILN_OUT/mods/impact-lab/assets"
cp -r "$mod/assets/." "$KILN_OUT/mods/impact-lab/assets/"
rm -rf "$server/kiln/mods/impact-lab"
cp -r "$KILN_OUT/mods/impact-lab" "$server/kiln/mods/impact-lab"
echo "impact-lab deployed to $server/kiln/mods/impact-lab"
