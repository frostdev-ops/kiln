#!/usr/bin/env bash
# Builds the Kiln MDK NuGet packages into $KILN_OUT/nuget (a local feed):
#   Kiln.Api        reference assemblies: Kiln.Api, every managed service (Kiln.Players, Kiln.Chat, ...), Kiln.Testing
#   Kiln.Sdk        MSBuild SDK: <Project Sdk="Kiln.Sdk/<version>">, kiln.mod.json, KilnDeploy, KilnPack, and the
#                   Roslyn analyzers (Kiln.Analyzers, KILN1xx) + game-id generator (Kiln.GameIds) in analyzers/dotnet/cs,
#                   and the native SDK in native/ (KilnPlugin.cmake, the ABI headers, the Linux toolchain)
#   Kiln.Templates  dotnet new kiln-mod, kiln-items, kiln-native, ... (templates/kiln-*)
# A service that fails to build is left out of Kiln.Api (warning), like tools/deploy-server.sh does.
#   KILN_OUT=<build dir> [KILN_SDK_VERSION=<version>] [KILN_API_BASELINE=<last release Kiln.Api, folder or .nupkg>] managed/Kiln.Sdk/pack.sh
# Version: KILN_SDK_VERSION (tools/release/release.sh sets the release's, e.g. 0.2.0-dev.12), else a new one per pack:
# <base>-dev.local.<yyyymmdd>.<hhmmss> (UTC; base from Sdk/Kiln.Version.props). NuGet caches a package by id and version and
# never looks again, so the same version packed twice would leave mods on the first one; a +build suffix doesn't help, as
# NuGet ignores it. All three packages, the packed Kiln.Version.props and the templates' default carry this version.
# Use the feed:  dotnet new install $KILN_OUT/nuget/Kiln.Templates.<version>.nupkg   (the version this script prints)
#                dotnet new kiln-mod -n MyMod --feed $KILN_OUT/nuget                  (the project names that Kiln.Sdk version)
set -euo pipefail
repo=$(cd "$(dirname "$0")/../.." && pwd)
out=${KILN_OUT:-$repo/native/build}
feed=$out/nuget stage=$out/mdk-stage
export KILN_OUT=$out
base=$(sed -n 's:.*<KilnSdkVersion>\(.*\)</KilnSdkVersion>.*:\1:p' "$repo/managed/Kiln.Sdk/Sdk/Kiln.Version.props")
version=${KILN_SDK_VERSION:-$base-dev.local.$(date -u +%Y%m%d).$((10#$(date -u +%H%M%S)))}
[[ ${version%%[-+]*} == "$base" ]] || { echo "pack: KILN_SDK_VERSION $version is not a $base version (Sdk/Kiln.Version.props)" >&2; exit 2; }
rm -rf "$stage" && mkdir -p "$feed" "$stage"
build() { dotnet build "$1" -c Release -o "$stage" --artifacts-path "$out/dotnet" -v q -nologo -p:GenerateDocumentationFile=true -p:NoWarn='CS1591%3BCS1573' >/dev/null; }
build "$repo/managed/Kiln.Api/Kiln.Api.csproj"
build "$repo/managed/Kiln.Testing/Kiln.Testing.csproj"
not_built=()
for proj in "$repo"/managed/services/*/*.csproj; do
  build "$proj" || { echo "pack: WARNING: $(basename "$proj" .csproj) failed to build; not in Kiln.Api" >&2; not_built+=(--not-built "$(basename "$proj" .csproj)"); }
done
# The API compat gate (tools/release/apicompat.sh): the staged DLLs against the last release's (KILN_API_BASELINE, a folder
# or a Kiln.Api .nupkg). A break stops the pack before any package is written; unset, it warns loudly and the pack goes on.
if [[ -n ${KILN_API_BASELINE:-} ]]; then
  "$repo/tools/release/apicompat.sh" "${not_built[@]}" "$KILN_API_BASELINE" "$stage" \
    || { echo "pack: the API compat gate failed (above); no package written" >&2; exit 1; }
else
  "$repo/tools/release/apicompat.sh" "" "$stage" || [[ $? == 3 ]]
fi
# Roslyn components shipped inside Kiln.Sdk (analyzers/dotnet/cs): KILN1xx analyzers + code fixes, the game-id generator.
analyzers=$out/mdk-analyzers
rm -rf "$analyzers" && mkdir -p "$analyzers"
build_an() { dotnet build "$1" -c Release -o "$analyzers" --artifacts-path "$out/dotnet" -v q -nologo >/dev/null; }
build_an "$repo/managed/Kiln.Analyzers/src/Kiln.Analyzers/CodeFixes/Kiln.Analyzers.CodeFixes.csproj"  # + Kiln.Analyzers
gameids=$repo/managed/Kiln.Analyzers/src/Kiln.GameIds/Kiln.GameIds.csproj
if [[ -f $gameids ]]; then
  build_an "$gameids" || echo "pack: WARNING: Kiln.GameIds failed to build; not in Kiln.Sdk" >&2
fi
pack() { dotnet pack "$1" -c Release -o "$feed" --artifacts-path "$out/dotnet" -v q -nologo -p:KilnSdkVersion="$version" "${@:2}" >/dev/null; }
pack "$repo/managed/Kiln.Sdk/packages/Kiln.Api.Ref.csproj" -p:KilnStage="$stage"
pack "$repo/managed/Kiln.Sdk/Kiln.Sdk.csproj" -p:KilnAnalyzersStage="$analyzers"
# The templates, staged with their Kiln.Sdk default set to this version (a new project restores the packages of this pack).
# The stage keeps the repository's layout, for Kiln.Templates.csproj's import of Kiln.Version.props.
tstage=$out/mdk-templates
rm -rf "$tstage" && mkdir -p "$tstage/templates" "$tstage/managed/Kiln.Sdk/Sdk"
cp "$repo/managed/Kiln.Sdk/Sdk/Kiln.Version.props" "$tstage/managed/Kiln.Sdk/Sdk/"
cp "$repo/templates/Kiln.Templates.csproj" "$tstage/templates/"
for d in "$repo"/templates/kiln-*/; do cp -a "$d" "$tstage/templates/"; done
python3 - "$tstage/templates" "$base" "$version" <<'PY'
import glob, json, os, re, sys
root, base, version = sys.argv[1:]
for t in sorted(glob.glob(os.path.join(root, "kiln-*", ".template.config", "template.json"))):
    text = open(t, encoding="utf-8").read()
    if json.loads(text).get("symbols", {}).get("kilnSdkVersion", {}).get("defaultValue") != base:
        print(f"pack: WARNING: {t} does not default to Kiln.Sdk {base}", file=sys.stderr)
        continue
    # only the kilnSdkVersion symbol's default; the rest of the file stays byte for byte
    text = re.sub(r'("kilnSdkVersion"\s*:\s*\{[^{}]*?"defaultValue"\s*:\s*")[^"]*(")', lambda m: m.group(1) + version + m.group(2), text, count=1)
    assert json.loads(text)["symbols"]["kilnSdkVersion"]["defaultValue"] == version, t
    open(t, "w", encoding="utf-8").write(text)
PY
find "$tstage/templates" \( -name bin -o -name obj -o -name build \) -prune -exec rm -rf {} +
pack "$tstage/templates/Kiln.Templates.csproj"
echo "Kiln MDK $version -> $feed:"; ls -1 "$feed" | grep -F ".$version.nupkg"
