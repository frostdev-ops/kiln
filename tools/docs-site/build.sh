#!/usr/bin/env bash
# Builds the modding site locally: the API reference from the assemblies and XML docs of Kiln.Api, Kiln.Testing and the
# services (managed/Kiln.Sdk/pack.sh's stage), plus docs/kiln/modding/*.md. It publishes nothing.
#   KILN_OUT=<build dir> tools/docs-site/build.sh     -> $KILN_OUT/docs-site/index.html
# The tool goes into $KILN_OUT/tools (dotnet tool install, from NuGet). Generated files stay out of the repository: the
# config is copied to $KILN_OUT/docs-work, where docfx writes api/.
# docfx's runtimeconfig also asks for ASP.NET Core, which only `docfx serve` needs and which an SDK-only machine lacks, so
# it runs from a private copy that names just Microsoft.NETCore.App.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
out=${KILN_OUT:?set KILN_OUT to your build folder}
tools=$out/tools/docfx
work=$out/docs-work
stage=$out/mdk-stage
version=2.78.3
# the API reference reads the assemblies and XML docs pack.sh builds: no MSBuild workspace, whose assemblies docfx does
# not find on this SDK
[[ -f $stage/Kiln.Api.xml ]] || KILN_OUT=$out "$repo/managed/Kiln.Sdk/pack.sh"
[[ -x $tools/docfx ]] || dotnet tool install docfx --version "$version" --tool-path "$tools" >/dev/null
rm -rf "$work" && mkdir -p "$work"
cp "$here"/index.md "$here"/toc.yml "$work"/
cp -r "$repo/docs/kiln/modding" "$work/modding"      # docfx wants its sources below the config
sed -e "s#MDK_STAGE#$stage#" "$here/docfx.json" > "$work/docfx.json"
cd "$work"
# A private copy of the tool: without its deps.json every dll next to it is found, and the SDK's Microsoft.Extensions.*
# stand in for the ones ASP.NET Core would provide.
tool=$(dirname "$(find "$tools/.store" -name docfx.dll | head -1)")
sdk=$(dotnet --list-sdks | tail -1 | sed -E 's/^([^ ]+) \[(.*)\]$/\2\/\1/')
run=$out/tools/docfx-run
rm -rf "$run" && cp -r "$tool" "$run"
rm -f "$run/docfx.deps.json"
cp -n "$sdk"/Microsoft.Extensions.*.dll "$run"/
cp -r "$(find "$tools/.store" -maxdepth 7 -type d -name templates | head -1)" "$run/templates"
python3 - "$run/docfx.runtimeconfig.json" <<'PY'
import json, sys
c = json.load(open(sys.argv[1]))
o = c["runtimeOptions"]
o.pop("frameworks", None)
o["framework"] = {"name": "Microsoft.NETCore.App", "version": "8.0.0"}
o["rollForward"] = "Major"
o["configProperties"].pop("Docfx.DotnetToolMode", None)   # it then looks for templates/ next to itself
json.dump(c, open(sys.argv[1], "w"), indent=1)
PY
docfx() { dotnet exec "$run/docfx.dll" "$@"; }
docfx metadata docfx.json
docfx build docfx.json --output "$out/docs-site"
echo "site: $out/docs-site/index.html"
