#!/usr/bin/env bash
# Builds the Kiln MDK NuGet packages into $KILN_OUT/nuget (a local feed):
#   Kiln.Api        reference assemblies: Kiln.Api, every managed service (Kiln.Players, Kiln.Chat, ...), Kiln.Testing
#   Kiln.Sdk        MSBuild SDK: <Project Sdk="Kiln.Sdk/<version>">, kiln.mod.json, KilnDeploy, KilnPack, and the
#                   Roslyn analyzers (Kiln.Analyzers, KILN1xx) + game-id generator (Kiln.GameIds) in analyzers/dotnet/cs,
#                   and the native SDK in native/ (KilnPlugin.cmake, the ABI headers, the Linux toolchain)
#   Kiln.Templates  dotnet new kiln-mod, kiln-items, kiln-native, ... (templates/kiln-*)
# A service that fails to build is left out of Kiln.Api (warning), like tools/deploy-server.sh does.
#   KILN_OUT=<build dir> managed/Kiln.Sdk/pack.sh
# Use the feed:  dotnet new install Kiln.Templates --add-source $KILN_OUT/nuget
#                dotnet new kiln-mod -n MyMod --feed $KILN_OUT/nuget
set -euo pipefail
repo=$(cd "$(dirname "$0")/../.." && pwd)
out=${KILN_OUT:-$repo/native/build}
feed=$out/nuget stage=$out/mdk-stage
export KILN_OUT=$out
rm -rf "$stage" && mkdir -p "$feed" "$stage"
build() { dotnet build "$1" -c Release -o "$stage" --artifacts-path "$out/dotnet" -v q -nologo -p:GenerateDocumentationFile=true -p:NoWarn='CS1591%3BCS1573' >/dev/null; }
build "$repo/managed/Kiln.Api/Kiln.Api.csproj"
build "$repo/managed/Kiln.Testing/Kiln.Testing.csproj"
for proj in "$repo"/managed/services/*/*.csproj; do
  build "$proj" || echo "pack: WARNING: $(basename "$proj" .csproj) failed to build; not in Kiln.Api" >&2
done
# Roslyn components shipped inside Kiln.Sdk (analyzers/dotnet/cs): KILN1xx analyzers + code fixes, the game-id generator.
analyzers=$out/mdk-analyzers
rm -rf "$analyzers" && mkdir -p "$analyzers"
build_an() { dotnet build "$1" -c Release -o "$analyzers" --artifacts-path "$out/dotnet" -v q -nologo >/dev/null; }
build_an "$repo/managed/Kiln.Analyzers/src/Kiln.Analyzers/CodeFixes/Kiln.Analyzers.CodeFixes.csproj"  # + Kiln.Analyzers
gameids=$repo/managed/Kiln.Analyzers/src/Kiln.GameIds/Kiln.GameIds.csproj
if [[ -f $gameids ]]; then
  build_an "$gameids" || echo "pack: WARNING: Kiln.GameIds failed to build; not in Kiln.Sdk" >&2
fi
pack() { dotnet pack "$1" -c Release -o "$feed" --artifacts-path "$out/dotnet" -v q -nologo "${@:2}" >/dev/null; }
pack "$repo/managed/Kiln.Sdk/packages/Kiln.Api.Ref.csproj" -p:KilnStage="$stage"
pack "$repo/managed/Kiln.Sdk/Kiln.Sdk.csproj" -p:KilnAnalyzersStage="$analyzers"
pack "$repo/templates/Kiln.Templates.csproj"
version=$(sed -n 's:.*<KilnSdkVersion>\(.*\)</KilnSdkVersion>.*:\1:p' "$repo/managed/Kiln.Sdk/Sdk/Kiln.Version.props")
for t in "$repo"/templates/*/.template.config/template.json; do
  grep -q "\"defaultValue\": \"$version\"" "$t" || echo "pack: WARNING: $t does not default to Kiln.Sdk $version" >&2
done
echo "Kiln MDK $version -> $feed:"; ls -1 "$feed" | grep -F "$version"
