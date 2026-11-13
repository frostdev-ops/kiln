#!/usr/bin/env bash
# Data-only mods that try what kiln.vfx's boot reader must refuse in a mod's folder (native/services/vfx/generated.h): a
# blocks folder that is a link to elsewhere, a baked file that is a link, more than 16 baked files, a block file over 16 MiB
# and JSON nested 70 deep. Each refusal is a kiln.log line "vfx: probe-...: ... not read"; the server keeps running.
#   mods/tests/bake-test/fixtures/make-limit-probes.sh <a lab's kiln/mods> <a scratch folder outside it>
set -euo pipefail
mods=${1:?kiln/mods folder}; outside=${2:?a scratch folder outside the mods folder}
mkdir -p "$outside/blocks/generated"
echo '{"vfx": {"outside_spark": {"particles": [{"burst": 2, "color": "#ffffff"}]}}}' > "$outside/blocks/outside.json"
echo '{"format": 1, "vfx": {"outside_baked": {"particles": [{"burst": 2, "color": "#ffffff"}]}}}' > "$outside/blocks/generated/x.json"
manifest() { printf '{"id": "%s", "name": "%s", "version": "1.0.0", "apiVersion": "0.2", "side": "both", "clients": "mod"}\n' "$1" "$1" > "$mods/$1/kiln.mod.json"; }
for m in probe-link probe-file-link probe-many probe-big probe-deep; do rm -rf "${mods:?}/$m"; mkdir -p "$mods/$m"; manifest "$m"; done
ln -s "$outside/blocks" "$mods/probe-link/blocks"
mkdir -p "$mods/probe-file-link/blocks/generated" && ln -s "$outside/blocks/generated/x.json" "$mods/probe-file-link/blocks/generated/x.json"
mkdir -p "$mods/probe-many/blocks/generated"
for i in $(seq -w 1 20); do echo '{"format": 1, "vfx": {}}' > "$mods/probe-many/blocks/generated/f$i.json"; done
mkdir -p "$mods/probe-big/blocks"
python3 -c "import sys; sys.stdout.write('{\"vfx\": {}, \"pad\": \"' + 'x' * (17 << 20) + '\"}')" > "$mods/probe-big/blocks/big.json"
mkdir -p "$mods/probe-deep/blocks"
python3 -c "import sys; sys.stdout.write('{\"vfx\": {}, \"deep\": ' + '[' * 70 + ']' * 70 + '}')" > "$mods/probe-deep/blocks/deep.json"
echo "probes in $mods: probe-link probe-file-link probe-many probe-big probe-deep"
