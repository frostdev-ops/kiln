#!/usr/bin/env bash
# Builds the loader samples into $KILN_OUT/mods/<id> (tools/deploy-server.sh copies every folder there into a server):
#   sample-lib (exports SampleLib.Api.dll), sample-consumer (depends on it; optional absent dependency; IMC),
#   sample-tests (Kiln.Testing, auto-run), sample-faulty (fault policy demo), and the manifest-only resolver fixtures
#   in resolver/ (missing dependency, cycle, version mismatch, incompatible, client-only, feature, API, bad manifest).
# The mods build with Kiln.Sdk in repository mode and land in place through its KilnDeploy target.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:?set KILN_OUT (e.g. <repo>/.out/loader)}
mkdir -p "$out/mods"
for p in SampleLib SampleConsumer SampleTests SampleFaulty; do
  dotnet build "$here/$p/$p.csproj" -c Release --artifacts-path "$out/dotnet" -v q -nologo -t:KilnDeploy -p:KilnModsDir="$out/mods" \
    | grep -E ' -> .*kiln|error|warning KILN' || true
done
for f in "$here"/resolver/*/; do
  rm -rf "$out/mods/$(basename "$f")" && cp -r "$f" "$out/mods/"
done
echo "samples in $out/mods: $(ls "$out/mods" | tr '\n' ' ')"
