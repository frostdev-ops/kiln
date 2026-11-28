#!/usr/bin/env bash
# tools/check.sh: the check a worker runs before committing managed code. No labs, no native build, no Rust, no pack.
#   [KILN_OUT=<build dir>] [KILN_API_BASELINE=<last release Kiln.Api, folder or .nupkg>] [KILN_CHECK_JOBS=<n>] \
#     tools/check.sh [--no-baseline] [--no-docfx] [-v]
# Stages, each with one result line and its wall time:
#   record  the API record before anything builds (tools/release/api-gate.sh record --worktree): every *REMOVED* line
#           of a PublicAPI.Unshipped.txt beside its [KILNX...] line (made experimental) or its "(forwarded, contained
#           in <Assembly>)" line (a type moved, recorded by <Assembly>), and no merge conflict marker in a PublicAPI file
#   records tools/sigport/build_record.py check: the newest build record of each side lists every name sigdb declares,
#           once, with a level (sigdb/builds/README.md), and resolves every must-port name (tools/sigport/must_port.json);
#           a sigdb name added without a refresh, or a fail-level must-port name dropped or unresolved, fails here (a
#           warn-level one only prints a warning)
#   registry tools/registrygen/registrygen.py --check: the registry schemas (schema/registry/*.json) are valid, every name
#           is in ids.lock.json, and the files made from them (Kiln.Registry's Generated/*.g.cs, schema.g.h, the data
#           section's schema, docs/kiln/modding/registries/*.md) are current; tools/registrygen/regmap_check.py: regmap/
#           rows keep the routing rule (the schemas' "applies") and give reasons in Kiln words. Routing only in CI: the
#           committed type dumps hold none of the registries' resource types, so no row resolves against them here; the
#           full proof is regmap_check.py --exe against a copy of each game exe (regmap/README.md)
#   pytests the Python rule tests of tools/replinspect (python3 -m unittest)
#   bundle  tools/release/test_stage_bundle.py: tools/package/stage.sh's sigdb + regmap step run on a minimal stage (no
#           build, well under a second), judged by kilnctl bundle create's rule: every bundle path in exactly one
#           component, regmap/common in core only, each side's regmap beside its sigdb, and every side's install
#           holding regmap/common, regmap/<side> and sigdb/<side>.json; the other components (admin, emf, samples, eml,
#           shroudforge, enshwick, sfi) by their manifest entries. The real kilnctl bundle create runs on those stages
#           when a kilnctl is built: KILNCTL=<kilnctl>, else ${CARGO_TARGET_DIR:-target}/{release,debug}/kilnctl if newer
#           than crates/kiln-manage/src/bundle.rs; the result line names the one it ran, or why none
#   drift   tools/docs-site/drift.py: the skill and docs/kiln against the code, no build needed (a second or two): every
#           `Type.Member` they name is public API (inherited and C# 14 extension members count; tools/docs-site/drift-allow.txt
#           lists the false positives, a reason each), no "since 0.2.0-dev.N" claim is earlier than managed/api-history.tsv's
#           first release of the name, the skill's pinned build and mod API are the current ones (Kiln.Mods.ApiVersion) and
#           versions.md has a row for each mod API, and every page docs-map.md names exists. A finding fails the stage
#   snippets tools/docs-site/snippets.py --check: the C# fences of the pages tools/docs-site/snippets.json covers are all
#           listed, and managed/tests/Kiln.DocSnippets.Tests/Generated is made from the pages as they are now; the build stage
#           then compiles them with the Kiln analyzers, every warning an error (a gone member, a wrong signature, a result
#           thrown away, an unguarded capability: at the page's line)
#   build   every managed project with a PublicAPI.Shipped.txt (found by that file, so gate 1, the PublicApiAnalyzers
#           record, runs), Kiln.Host and the unit test projects: one Release build of one generated solution
#           ($KILN_OUT/check/check.slnx), so each project is built once
#   tests   the unit test projects (xunit v3 executables, run as they are): managed/tests/*,
#           managed/Kiln.Analyzers/tests/*, managed/services/Kiln.DevTools/tests
#   stage   Kiln.Api, Kiln.Testing and every managed/services/* DLL from that build into $KILN_OUT/check/stage, the set
#           managed/Kiln.Sdk/pack.sh stages and packs
#   apis    managed/Kiln.Analyzers/tools/gen-apis.sh --check: the analyzers' API tables (KilnApis.g.cs: tick-thread,
#           advanced, completed-task, capability-guard and service names) regenerated from the managed sources into a
#           temporary file (its builds are incremental over this check's) and compared with the committed copy; a stale
#           copy fails with the diff's first lines and the command that regenerates it (31.13 shipped one stale)
#   index   the agent API index (agent/skills/kiln-modding/references/api-index/): tools/docs-site/api_index.py --check regenerates
#           it from the PublicAPI record, the XML docs this build just wrote ($KILN_OUT/dotnet/bin/<project>/release) and
#           managed/api-history.tsv, in memory (a couple of seconds, no build of its own), and fails when the committed copy
#           differs. A changed API line, doc comment or ledger row makes the copy stale: the failure names the command that
#           regenerates it from this same build
#   docs    the doc gate, on the XML docs this build just wrote: tools/docs-site/record_params.py (a positional record has a
#           documented property for each parameter: the compiler's CS1591 never asks) and tools/docs-site/build.sh --gate-only
#           (docfx metadata over the build's DLLs and XML, then undocumented.py strict against undocumented-baseline.txt, which
#           names nothing: a public type or member with no summary fails). About a minute; the docfx tool is installed into
#           $KILN_OUT/tools on first use (NuGet), --no-docfx leaves that half out and says so on the last line
#   compat  gate 2: tools/release/apicompat.sh against KILN_API_BASELINE (-v lists every addition)
# On a failure the stage prints the failing diagnostics (record lines, compiler errors, failed tests, BREAK lines), not
# the whole log; the full logs are in $KILN_OUT/check/*.log. A failed record stage does not stop the build stages.
# The last line is one of:
#   check: PASSED in <s>s                                   exit 0
#   check: PASSED in <s>s without the API compat gate (--no-baseline)   exit 0
#   check: PASSED in <s>s without the docfx doc gate (--no-docfx)       exit 0 (with --no-baseline: both are named)
#   check: FAILED (<stages>) in <s>s                        exit 1
#   check: INCOMPLETE in <s>s: the API compat gate did not run (KILN_API_BASELINE unset); ...   exit 3
# Exit codes: 0 passed, 1 a stage failed, 2 bad usage or a broken environment, 3 incomplete (no baseline and no
# --no-baseline: a half-run check never exits 0).
# Needs: dotnet (SDK 10.0.1xx) and python3 (Kiln.Game's build generates its types with it once typegen lands;
# apicompat.sh unzips a .nupkg baseline with it). Builds into $KILN_OUT (default as in pack.sh: native/build).
# This wave runs it as tools/slot tools/check.sh (the script itself does not wait for a slot).
set -uo pipefail
repo=$(cd "$(dirname "$0")/.." && pwd)
out=${KILN_OUT:-$repo/native/build}
export KILN_OUT=$out
jobs=${KILN_CHECK_JOBS:-4}
no_baseline=0 no_docfx=0 verbose=()
for a in "$@"; do
  case $a in
    --no-baseline) no_baseline=1 ;;
    --no-docfx) no_docfx=1 ;;
    -v|--verbose) verbose=(-v) ;;
    -h|--help) sed -n '2,/^set -uo/p' "$0" | sed '$d; s/^# \{0,1\}//'; exit 0 ;;
    *) echo "check: unknown argument $a (see --help)" >&2; exit 2 ;;
  esac
done
command -v dotnet >/dev/null || { echo "check: dotnet is not on PATH" >&2; exit 2; }
command -v python3 >/dev/null || { echo "check: python3 is not on PATH" >&2; exit 2; }
work=$out/check
mkdir -p "$work"
t0=$SECONDS failed=()
result() { printf 'check: %-7s %-6s %4ss  %s\n' "$1" "$2" "$3" "$4"; }
errors() { # log: the distinct error lines, without the [project] suffix
  grep -E ': (error|fatal error) ' "$1" | sed -E 's# \[[^]]*\]$##' | LC_ALL=C sort -u | head -40
}

# The projects
gated=()
while IFS= read -r f; do
  d=${f%/*} p=("$d"/*.csproj)
  [[ ${#p[@]} == 1 && -f ${p[0]} ]] || { echo "check: $d has a PublicAPI.Shipped.txt but not exactly one .csproj" >&2; exit 2; }
  gated+=("${p[0]}")
done < <(find "$repo/managed" -name PublicAPI.Shipped.txt -not -path '*/bin/*' -not -path '*/obj/*' | LC_ALL=C sort)
((${#gated[@]})) || { echo "check: no managed project has a PublicAPI.Shipped.txt" >&2; exit 2; }
tests=()
for p in "$repo"/managed/tests/*/*.csproj "$repo"/managed/Kiln.Analyzers/tests/*/*.csproj "$repo"/managed/services/Kiln.DevTools/tests/*.csproj; do
  [[ -f $p ]] && tests+=("$p")
done
# Left out (fail on the untouched tree): none today. Add "<name>" here with the reason when one has to be.
skip_tests=()
projects=("${gated[@]}" "$repo/managed/Kiln.Host/Kiln.Host.csproj" "${tests[@]}")
{
  echo '<Solution>'
  for p in "${projects[@]}"; do echo "  <Project Path=\"$(realpath --relative-to="$work" "$p")\" />"; done
  echo '</Solution>'
} >"$work/check.slnx"

# record
t=$SECONDS
if "$repo/tools/release/api-gate.sh" record --worktree >"$work/record.log" 2>&1; then
  result record OK $((SECONDS - t)) "$(tail -1 "$work/record.log" | sed 's/^api-gate: //')"
else
  rc=$?
  result record FAILED $((SECONDS - t)) "$( ((rc == 1)) && echo "the API record is not sound:" || echo "api-gate.sh could not run (exit $rc):")"
  grep -E '^  ' "$work/record.log" | head -40
  ((rc == 1)) || tail -5 "$work/record.log"
  failed+=(record)
fi

# records
t=$SECONDS
if python3 "$repo/tools/sigport/build_record.py" check >"$work/records.log" 2>&1; then
  result records OK $((SECONDS - t)) "$(grep -c '^ok ' "$work/records.log") build records list every declared name with a level"
else
  result records FAILED $((SECONDS - t)) "a build record does not list what sigdb declares:"
  grep -vE '^ +(server|client) [0-9a-f]+… .*superseded' "$work/records.log" | head -40
  failed+=(records)
fi

# registry
t=$SECONDS
if python3 -I "$repo/tools/registrygen/registrygen.py" --check >"$work/registry.log" 2>&1 &&
   python3 -I "$repo/tools/registrygen/regmap_check.py" >>"$work/registry.log" 2>&1; then
  result registry OK $((SECONDS - t)) "$(grep '^registrygen: ' "$work/registry.log" | tail -1 | sed 's/^registrygen: //'); regmap routing and record digests checked"
else
  result registry FAILED $((SECONDS - t)) "the registry schemas or the files made from them:"
  head -40 "$work/registry.log"
  failed+=(registry)
fi

# pytests
t=$SECONDS
if python3 -m unittest discover -s "$repo/tools/replinspect" -p 'test_*.py' >"$work/pytests.log" 2>&1; then
  result pytests OK $((SECONDS - t)) "tools/replinspect: $(grep -oE '^Ran [0-9]+ tests' "$work/pytests.log" | sed 's/^Ran //')"
else
  result pytests FAILED $((SECONDS - t)) "tools/replinspect rule tests:"
  grep -E -A8 '^(FAIL|ERROR):' "$work/pytests.log" | head -40
  failed+=(pytests)
fi
# bundle
t=$SECONDS
# the real kilnctl bundle create runs when a kilnctl is built: KILNCTL, else the newest of CARGO_TARGET_DIR's (or
# target/'s) release and debug kilnctl, if it is newer than the bundle rule's source (an older one may judge another rule)
kilnctl=${KILNCTL:-} kilnctl_note=""
if [[ -z $kilnctl ]]; then
  for c in "${CARGO_TARGET_DIR:-$repo/target}"/{release,debug}/kilnctl; do
    [[ -x $c && ( -z $kilnctl || $c -nt $kilnctl ) ]] && kilnctl=$c
  done
  if [[ -n $kilnctl && $repo/crates/kiln-manage/src/bundle.rs -nt $kilnctl ]]; then
    kilnctl_note=", kilnctl not run: $kilnctl is older than crates/kiln-manage/src/bundle.rs"; kilnctl=""
  fi
fi
[[ -z $kilnctl && -z $kilnctl_note ]] && kilnctl_note=", kilnctl not run: none built (KILNCTL unset)"
[[ -n $kilnctl ]] && kilnctl_note=", kilnctl bundle create: $kilnctl"
if KILNCTL=$kilnctl python3 -I "$repo/tools/release/test_stage_bundle.py" >"$work/bundle.log" 2>&1; then
  result bundle OK $((SECONDS - t)) "stage.sh: one component per bundle path, every side's regmap and sigdb complete, every component declared ($(grep -oE '^Ran [0-9]+ tests' "$work/bundle.log" | sed 's/^Ran //')$kilnctl_note)"
else
  result bundle FAILED $((SECONDS - t)) "tools/release/test_stage_bundle.py (kilnctl bundle create would refuse the stage, or a side's install is incomplete):"
  grep -E -A14 '^(FAIL|ERROR):' "$work/bundle.log" | head -50
  failed+=(bundle)
fi
# drift
t=$SECONDS
python3 -I "$repo/tools/docs-site/drift.py" >"$work/drift.log" 2>&1
rc=$?
case $rc in
  0) result drift OK $((SECONDS - t)) "names, versions, facts and map: no drift$(grep -q 'no wiki folder' "$work/drift.log" && echo " (wiki page names not checked: no frostdev.io wiki folder)")" ;;
  1) result drift FAILED $((SECONDS - t)) "the docs or the skill differ from the code (tools/docs-site/drift.py, allow-list drift-allow.txt):"
     grep -vE '^drift: [a-z]+ +OK' "$work/drift.log" | head -60
     failed+=(drift) ;;
  *) result drift FAILED $((SECONDS - t)) "drift.py could not run (exit $rc):"
     tail -20 "$work/drift.log"
     failed+=(drift) ;;
esac
# snippets
t=$SECONDS
if python3 -I "$repo/tools/docs-site/snippets.py" --check >"$work/snippets.log" 2>&1; then
  result snippets OK $((SECONDS - t)) "$(tail -1 "$work/snippets.log" | sed 's/^snippets: //') (compiled in the build stage: Kiln.DocSnippets.Tests)"
else
  result snippets FAILED $((SECONDS - t)) "the docs' C# snippets and Generated/ differ (python3 -I tools/docs-site/snippets.py writes it):"
  head -40 "$work/snippets.log"
  failed+=(snippets)
fi

# build
t=$SECONDS
# ShouldUnsetParentConfigurationAndPlatform=false: projects outside the solution that a project references (the sample
# mods Kiln.Host.Tests builds) build in Release too, not in their default Debug.
if dotnet build "$work/check.slnx" -c Release -m:"$jobs" --artifacts-path "$out/dotnet" -v q -nologo \
    -p:ShouldUnsetParentConfigurationAndPlatform=false >"$work/build.log" 2>&1; then
  result build OK $((SECONDS - t)) "${#projects[@]} projects (${#gated[@]} with a PublicAPI record), $(grep -cE ': warning ' "$work/build.log") warning lines"
  built=1
else
  result build FAILED $((SECONDS - t)) "errors:"
  errors "$work/build.log"
  failed+=(build) built=0
fi

if ((built)); then
  # tests
  t=$SECONDS total=0 bad=0 runs=0
  : >"$work/tests.log"
  for p in "${tests[@]}"; do
    n=$(basename "$p" .csproj)
    for s in "${skip_tests[@]}"; do [[ $s == "$n" ]] && continue 2; done
    dir=$out/dotnet/bin/$n/release
    if [[ ! -f $dir/$n.dll ]]; then echo "check: tests: $n: no $dir/$n.dll"; bad=$((bad + 1)); continue; fi
    log=$work/test-$n.log
    (cd "$dir" && dotnet "$n.dll") >"$log" 2>&1
    rc=$?
    sed -E 's/\x1b\[[0-9;]*m//g' -i "$log"
    cat "$log" >>"$work/tests.log"
    sum=$(grep -E "^ +$n +Total: " "$log" | tail -1)
    runs=$((runs + 1))
    t_n=$(sed -nE 's/.*Total: ([0-9]+).*/\1/p' <<<"$sum")
    total=$((total + ${t_n:-0}))
    if ((rc != 0)) || [[ -z $sum ]]; then
      bad=$((bad + 1))
      echo "check: tests: $n FAILED (exit $rc): ${sum:-no summary}"
      grep -E -A6 '\[FAIL\]' "$log" | head -40
    fi
  done
  if ((bad)); then result tests FAILED $((SECONDS - t)) "$bad of $runs test projects failed ($total tests)"; failed+=(tests)
  else result tests OK $((SECONDS - t)) "$runs test projects, $total tests"; fi

  # stage
  t=$SECONDS stage=$work/stage
  rm -rf "$stage" && mkdir -p "$stage"
  missing=()
  for p in "$repo/managed/Kiln.Api/Kiln.Api.csproj" "$repo/managed/Kiln.Testing/Kiln.Testing.csproj" "$repo"/managed/services/*/*.csproj; do
    n=$(basename "$p" .csproj)
    if [[ -f $out/dotnet/bin/$n/release/$n.dll ]]; then cp "$out/dotnet/bin/$n/release/$n.dll" "$stage/"; else missing+=("$n"); fi
  done
  if ((${#missing[@]})); then result stage FAILED $((SECONDS - t)) "not built: ${missing[*]}"; failed+=(stage)
  else result stage OK $((SECONDS - t)) "$(find "$stage" -name 'Kiln.*.dll' | wc -l) DLLs in $stage"; fi

  # apis
  t=$SECONDS
  "$repo/managed/Kiln.Analyzers/tools/gen-apis.sh" --check >"$work/apis.log" 2>&1
  rc=$?
  case $rc in
    0) result apis OK $((SECONDS - t)) "KilnApis.g.cs matches the managed sources" ;;
    1) result apis FAILED $((SECONDS - t)) "KilnApis.g.cs is stale (regenerate with KILN_OUT=<build dir> managed/Kiln.Analyzers/tools/gen-apis.sh and commit it):"
       grep -vE '^(gen-apis: KilnApis.g.cs is STALE|  KILN_OUT=)' "$work/apis.log" | head -40
       failed+=(apis) ;;
    *) result apis FAILED $((SECONDS - t)) "gen-apis.sh --check could not run (exit $rc):"
       tail -20 "$work/apis.log"
       failed+=(apis) ;;
  esac

  # index
  t=$SECONDS
  python3 -I "$repo/tools/docs-site/api_index.py" --check --xml "$out/dotnet/bin" >"$work/index.log" 2>&1
  rc=$?
  case $rc in
    0) result index OK $((SECONDS - t)) "$(tail -1 "$work/index.log" | sed 's/^api-index: //')" ;;
    1) result index FAILED $((SECONDS - t)) "the agent API index is stale:"
       sed -n '1,40p' "$work/index.log"
       failed+=(index) ;;
    *) result index FAILED $((SECONDS - t)) "api_index.py could not run (exit $rc):"
       tail -20 "$work/index.log"
       failed+=(index) ;;
  esac

  # docs
  t=$SECONDS docstage=$work/docstage bad=0 docfx_note=""
  python3 -I "$repo/tools/docs-site/record_params.py" --xml "$out/dotnet/bin" >"$work/docs-records.log" 2>&1
  rc=$?
  if ((rc != 0)); then
    bad=1
    result docs FAILED $((SECONDS - t)) "$( ((rc == 1)) && echo "a positional record has an undocumented property:" || echo "record_params.py could not run (exit $rc):")"
    head -30 "$work/docs-records.log"
  fi
  if ((no_docfx)); then
    docfx_note="docfx gate skipped (--no-docfx)"
  else
    # the build's DLLs with their XML docs; a project that writes no XML (Kiln.Example) cannot be judged and is left out, as
    # is Kiln.Host (it only lets docfx resolve Kiln.TestKit's references)
    rm -rf "$docstage" && mkdir -p "$docstage"
    noxml=()
    for p in "${gated[@]}"; do
      n=$(basename "$p" .csproj) d=$out/dotnet/bin/$n/release
      if [[ -f $d/$n.dll && -f $d/$n.xml ]]; then cp "$d/$n.dll" "$d/$n.xml" "$docstage/"; else noxml+=("$n"); fi
    done
    [[ -f $out/dotnet/bin/Kiln.Host/release/Kiln.Host.dll ]] && cp "$out/dotnet/bin/Kiln.Host/release/Kiln.Host.dll" "$docstage/"
    KILN_DOCS_STAGE=$docstage KILN_DOCS_STRICT=1 "$repo/tools/docs-site/build.sh" --gate-only >"$work/docs-gate.log" 2>&1
    rc=$?
    if ((rc == 0)); then
      docfx_note="no undocumented public name ($(find "$docstage" -name 'Kiln.*.xml' | wc -l) assemblies${noxml[*]:+; no XML docs for ${noxml[*]}})"
    else
      bad=1
      if grep -q '^undocumented: ' "$work/docs-gate.log"; then
        result docs FAILED $((SECONDS - t)) "public types or members with no XML summary (write the doc comment; undocumented-baseline.txt stays empty):"
        sed -n '/^undocumented: /,/^KILN_DOCS_STRICT/p' "$work/docs-gate.log" | head -60
      else
        result docs FAILED $((SECONDS - t)) "the docfx gate could not run (exit $rc; offline? --no-docfx skips it):"
        tail -15 "$work/docs-gate.log"
      fi
    fi
  fi
  if ((bad)); then failed+=(docs)
  else result docs OK $((SECONDS - t)) "$(sed 's/^record-params: //' "$work/docs-records.log"); $docfx_note"; fi
fi

# compat
compat=skipped
if ((built)) && [[ ! " ${failed[*]} " =~ " stage " ]]; then
  t=$SECONDS
  "$repo/tools/release/apicompat.sh" "${verbose[@]}" "${KILN_API_BASELINE:-}" "$stage" >"$work/compat.log" 2>&1
  rc=$?
  case $rc in
    0) grep -E '^apicompat: (NEW|ADDED|ADD|EXPERIMENTAL|WARNING)|skipped' "$work/compat.log"
       result compat OK $((SECONDS - t)) "$(tail -1 "$work/compat.log" | sed 's/^apicompat: OK: //')"; compat=ran ;;
    1) grep -E '^apicompat: (BREAK|WARNING)' "$work/compat.log"
       result compat FAILED $((SECONDS - t)) "$(grep -c '^apicompat: BREAK' "$work/compat.log") breaking changes (BREAK lines above)"
       failed+=(compat) compat=ran ;;
    3) cat "$work/compat.log" >&2; result compat SKIPPED $((SECONDS - t)) "KILN_API_BASELINE is not set" ;;
    *) tail -20 "$work/compat.log"; result compat FAILED $((SECONDS - t)) "apicompat.sh could not run (exit $rc)"; failed+=(compat) compat=ran ;;
  esac
fi

el=$((SECONDS - t0))
if ((${#failed[@]})); then echo "check: FAILED (${failed[*]}) in ${el}s"; exit 1; fi
if [[ $compat == skipped ]]; then
  if ((no_baseline)); then
    echo "check: PASSED in ${el}s without the API compat gate (--no-baseline)$( ((no_docfx)) && echo " and without the docfx doc gate (--no-docfx)")"
    exit 0
  fi
  echo "check: INCOMPLETE in ${el}s: the API compat gate did not run (KILN_API_BASELINE unset); set it, or pass --no-baseline"
  exit 3
fi
if ((no_docfx)); then echo "check: PASSED in ${el}s without the docfx doc gate (--no-docfx)"; exit 0; fi
echo "check: PASSED in ${el}s"
