#!/usr/bin/env bash
# Builds the modding site as a folder: the API reference from the assemblies and XML docs of Kiln.Api, Kiln.Testing,
# Kiln.Game and the services (managed/Kiln.Sdk/pack.sh's stage) and of Kiln.TestKit (its testkit-stage), plus
# docs/kiln/modding/*.md. It publishes nothing: the release puts the folder into <release>/sdk/api/, putting it online
# is the owner's decision. filterConfig.yml leaves out what DocFX cannot take (it says why).
#   KILN_OUT=<build dir> tools/docs-site/build.sh [<site folder>]   -> <site folder>/index.html
# The site folder defaults to $KILN_OUT/docs-site; an existing one is replaced only when it holds a site from here.
# The tool goes into $KILN_OUT/tools (dotnet tool install, from NuGet). Generated files stay out of the repository: the
# config is copied to $KILN_OUT/docs-work, where docfx writes api/ and docfx.log.
# Steps: docfx metadata over the stage; annotate.py turns the thread, side and [RequiresCapability] attributes it finds
# in that metadata into a visible line in a copy of the XML docs; docfx metadata again over the copy (api/);
# undocumented.py against undocumented-dev24.txt, or the file KILN_DOCS_BASELINE names (a warning; with
# KILN_DOCS_STRICT=1 an error, before docfx build); docfx build.
# docfx's runtimeconfig also asks for ASP.NET Core, which only `docfx serve` needs and which an SDK-only machine lacks, so
# it runs from a private copy that names just Microsoft.NETCore.App.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
out=${KILN_OUT:?set KILN_OUT to your build folder}
out=$(mkdir -p "$out" && cd "$out" && pwd)
site=${1:-$out/docs-site}
[[ $site == /* ]] || site=$PWD/$site
tools=$out/tools/docfx
work=$out/docs-work
stage=$out/mdk-stage
version=2.78.3
if [[ -d $site && -n $(command ls -A "$site") && ! -f $site/xrefmap.yml ]]; then
  echo "$site is not empty and holds no docs site: not replacing it" >&2
  exit 1
fi
# the API reference reads the assemblies and XML docs pack.sh builds: no MSBuild workspace, whose assemblies docfx does
# not find on this SDK
[[ -f $stage/Kiln.Api.xml ]] || KILN_OUT=$out "$repo/managed/Kiln.Sdk/pack.sh"
[[ -x $tools/docfx ]] || dotnet tool install docfx --version "$version" --tool-path "$tools" >/dev/null
rm -rf "$work" && mkdir -p "$work"
cp "$here"/index.md "$here"/toc.yml "$here"/filterConfig.yml "$work"/
cp -r "$repo/docs/kiln/modding" "$work/modding"      # docfx wants its sources below the config
cp -r "$stage" "$work/stage"                          # annotate.py rewrites the XML docs of this copy
# Kiln.TestKit (mod tests) is packed from a stage of its own; its dll and docs join, with Kiln.Host.dll only for docfx to
# resolve its references (docfx.json leaves Kiln.Host out of the reference)
for f in "$out"/testkit-stage/Kiln.{TestKit.dll,TestKit.xml,Host.dll}; do [[ -f $f ]] && cp "$f" "$work/stage/"; done
[[ -f $work/stage/Kiln.TestKit.dll ]] || echo "no Kiln.TestKit in $out/testkit-stage: the reference leaves it out" >&2
sed -e "s#MDK_STAGE#$work/stage#" "$here/docfx.json" > "$work/docfx.json"
sed -e "s#MDK_STAGE#$work/stage#" -e 's#"dest": "api"#"dest": "api-raw"#' "$here/docfx.json" > "$work/docfx-raw.json"
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
log=$work/docfx.log
docfx() { NO_COLOR=1 dotnet exec "$run/docfx.dll" "$@"; }
# pass one only tells annotate.py the attributes; its warnings come again in pass two
docfx metadata docfx-raw.json >"$work/docfx-raw.log" 2>&1 || { tail -40 "$work/docfx-raw.log" >&2; exit 1; }
python3 -I "$here/annotate.py" "$work/api-raw" "$work/stage"
rm -rf "$work/api-raw"
docfx metadata docfx.json 2>&1 | tee "$log"
baseline=${KILN_DOCS_BASELINE:-$here/undocumented-dev24.txt}
[[ -f $baseline ]] || { echo "no baseline file $baseline" >&2; exit 1; }
missing=$(python3 -I "$here/undocumented.py" --baseline "$baseline" "$work/api" || true)
n=$(grep -c . <<<"$missing" || true)
if ((n)); then
  printf 'undocumented: %s public types and members have no XML summary and are not in %s:\n%s\n' \
    "$n" "$(basename "$baseline")" "$missing" >&2
  [[ ${KILN_DOCS_STRICT:-} == 1 ]] && { echo "KILN_DOCS_STRICT=1: failing on them" >&2; exit 1; }
else
  echo "undocumented: none beyond $(basename "$baseline")"
fi
rm -rf "$site"
docfx build docfx.json --output "$site" 2>&1 | tee -a "$log"
# the manifest names this build folder and only docfx's own tools read it; the source maps are a third of the size
rm -f "$site/manifest.json" "$site"/public/*.map
python3 - "$log" <<'PY'
import collections, re, sys
kinds = collections.Counter()
for line in open(sys.argv[1], encoding="utf-8", errors="replace"):
    m = re.search(r"(?:^|: )warning:? ([A-Za-z]+):", line) or re.search(r"^warning: (.{0,24})", line)
    if m:
        kinds[m.group(1)] += 1
print(f"docfx: {sum(kinds.values())} warnings" + (" (" + ", ".join(f"{k} {n}" for k, n in kinds.most_common()) + ")"
                                                   if kinds else "") + f", log {sys.argv[1]}")
PY
echo "site: $site/index.html"
