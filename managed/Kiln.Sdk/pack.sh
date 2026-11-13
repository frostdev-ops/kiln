#!/usr/bin/env bash
# Builds the Kiln MDK NuGet packages into $KILN_OUT/nuget (a local feed):
#   Kiln.Api        reference assemblies: Kiln.Api, every managed service (Kiln.Players, Kiln.Chat, ...), Kiln.Testing,
#                   Kiln.Game (the generated game types, from the newest dump; needs python3)
#   Kiln.Sdk        MSBuild SDK: <Project Sdk="Kiln.Sdk/<version>">, kiln.mod.json, KilnDeploy, KilnPack, and the
#                   Roslyn analyzers (Kiln.Analyzers, KILN1xx) + game-id generator (Kiln.GameIds) in analyzers/dotnet/cs,
#                   and the native SDK in native/ (KilnPlugin.cmake, the ABI headers, the Linux toolchain), and the
#                   bake in bake/ (KilnBake: definitions baked at build, docs/kiln/modding/baked.md)
#   Kiln.Templates  dotnet new kiln-mod, kiln-items, kiln-native, ... (templates/kiln-*)
#   Kiln.TestKit    mod tests under dotnet test (docs/kiln/modding/unit-tests.md)
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
# The two doc warnings are passed as KilnPackNoWarn, which managed/Directory.Build.props adds to each project's own NoWarn: a
# -p:NoWarn on the command line would replace the projects' lists (their KILNX opt-ins) and fail the pack.
build() {
  local log; log=$(mktemp)
  if dotnet build "$1" -c Release -o "$stage" --artifacts-path "$out/dotnet" -v q -nologo -p:GenerateDocumentationFile=true \
       -p:KilnPackNoWarn='CS1591%3BCS1573' >"$log" 2>&1; then rm -f "$log"; return 0; fi
  { grep -E '(^|: )error' "$log" | sort -u | head -20 || tail -25 "$log"; } >&2
  rm -f "$log"; return 1
}
build "$repo/managed/Kiln.Api/Kiln.Api.csproj"
build "$repo/managed/Kiln.Testing/Kiln.Testing.csproj"
# Kiln.Game (KilnServices "Game") is part of the SDK: a failed build stops the pack, with its error lines.
game_log=$out/pack-kiln-game.log
if ! dotnet build "$repo/managed/Kiln.Game/Kiln.Game.csproj" -c Release -o "$stage" --artifacts-path "$out/dotnet" -v q -nologo \
     -p:GenerateDocumentationFile=true -p:KilnPackNoWarn='CS1591%3BCS1573' >"$game_log" 2>&1; then
  { grep -E '(^|: )error' "$game_log" | sort -u || tail -25 "$game_log"; } >&2
  echo "pack: Kiln.Game failed to build (log: $game_log); it is part of Kiln.Api, so the pack stops" >&2
  exit 1
fi
for proj in "$repo"/managed/services/*/*.csproj; do
  build "$proj" || echo "pack: WARNING: $(basename "$proj" .csproj) failed to build; not in Kiln.Api" >&2
done
# The API compat gate (tools/release/apicompat.sh): the staged DLLs against the last release's (KILN_API_BASELINE, a folder
# or a Kiln.Api .nupkg). A break stops the pack before any package is written; unset, it warns loudly and the pack goes on.
# A service of the last release that failed to build above (a PublicAPI record error is a build error) is not staged, and
# the gate counts that as a break: with a baseline, a pack never drops a released assembly.
if [[ -n ${KILN_API_BASELINE:-} ]]; then
  "$repo/tools/release/apicompat.sh" "$KILN_API_BASELINE" "$stage" \
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
# The bake (Sdk/Kiln.Bake.targets, docs/kiln/modding/baked.md): Kiln.Sdk.Bake with what it needs that Kiln.Api's kiln/lib does not
# carry (Kiln.Host, Kiln.TestKit, their dependencies), in the package's bake/; at build time the target puts kiln/lib beside it.
bake=$out/bake-stage
rm -rf "$bake" && mkdir -p "$bake/bake"
dotnet build "$repo/managed/Kiln.Sdk/bake/Kiln.Sdk.Bake.csproj" -c Release -o "$bake/all" --artifacts-path "$out/dotnet" -v q -nologo \
  -p:KilnBakeNoCopy=true -p:KilnPackNoWarn='CS1591%3BCS1573' >/dev/null
for f in "$bake"/all/*.dll; do [[ -f $stage/$(basename "$f") ]] || cp "$f" "$bake/bake/"; done
cp "$bake/all/Kiln.Sdk.Bake.runtimeconfig.json" "$bake/bake/"
pack "$repo/managed/Kiln.Sdk/Kiln.Sdk.csproj" -p:KilnAnalyzersStage="$analyzers" -p:KilnBakeStage="$bake/bake"
# Kiln.TestKit (mod tests under dotnet test): built into a stage of its own, never $stage (Kiln.Api.Ref.csproj packs every
# Kiln.*.dll there); the package takes Kiln.TestKit.dll and Kiln.Host.dll from it, the rest from Kiln.Api of this version.
tkstage=$out/testkit-stage
rm -rf "$tkstage" && mkdir -p "$tkstage"
dotnet build "$repo/managed/Kiln.TestKit/Kiln.TestKit.csproj" -c Release -o "$tkstage" --artifacts-path "$out/dotnet" -v q -nologo -p:KilnPackNoWarn='CS1591%3BCS1573' >/dev/null
pack "$repo/managed/Kiln.TestKit/pack/Kiln.TestKit.Pack.csproj" -p:KilnTestKitStage="$tkstage" -p:RestoreAdditionalProjectSources="$feed"
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
