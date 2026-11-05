#!/usr/bin/env bash
# Regenerates managed/Kiln.Analyzers/src/Kiln.Analyzers/KilnApis.g.cs (the analyzers' tick-thread, advanced and
# service tables) from Kiln's managed sources with tools/ApiScan (Roslyn). Run after changing Kiln.Api, Kiln.Testing
# or a service, and commit the result:
#   KILN_OUT=<build dir> managed/Kiln.Analyzers/tools/gen-apis.sh
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../../.." && pwd)
out=${KILN_OUT:-$repo/.out/analyzers}
stage=$out/apiscan-stage
rm -rf "$stage" && mkdir -p "$stage"
build() { dotnet build "$1" -c Release -o "$stage" --artifacts-path "$out/dotnet" -v q -nologo >/dev/null; }
build "$repo/managed/Kiln.Api/Kiln.Api.csproj"
build "$repo/managed/Kiln.Testing/Kiln.Testing.csproj"
for proj in "$repo"/managed/services/*/*.csproj; do
  build "$proj" || echo "gen-apis: WARNING: $(basename "$proj" .csproj) failed to build; scanning it against the others" >&2
done
dotnet_root=$(dirname "$(readlink -f "$(command -v dotnet)")")
refs=$( (ls -d "$dotnet_root"/packs/Microsoft.NETCore.App.Ref/10.*/ref/net10.0 \
              "$HOME"/.dotnet/packs/Microsoft.NETCore.App.Ref/10.*/ref/net10.0 2>/dev/null || true) | sort -V | tail -1)
[[ -d $refs ]] || { echo "gen-apis: no Microsoft.NETCore.App.Ref 10.x reference pack found" >&2; exit 1; }
dotnet run --project "$here/ApiScan/ApiScan.csproj" -c Release --artifacts-path "$out/dotnet" -- \
  --repo "$repo" --stage "$stage" --refs "$refs" --out "$repo/managed/Kiln.Analyzers/src/Kiln.Analyzers/KilnApis.g.cs"
