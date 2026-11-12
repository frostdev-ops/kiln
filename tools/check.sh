#!/usr/bin/env bash
# tools/check.sh: the check a worker runs before committing managed code. No labs, no native build, no Rust, no pack.
#   [KILN_OUT=<build dir>] [KILN_API_BASELINE=<last release Kiln.Api, folder or .nupkg>] [KILN_CHECK_JOBS=<n>] \
#     tools/check.sh [--no-baseline] [-v]
# Stages, each with one result line and its wall time:
#   build   every managed project with a PublicAPI.Shipped.txt (found by that file, so gate 1, the PublicApiAnalyzers
#           record, runs), Kiln.Host and the unit test projects: one Release build of one generated solution
#           ($KILN_OUT/check/check.slnx), so each project is built once
#   tests   the unit test projects (xunit v3 executables, run as they are): managed/tests/*,
#           managed/Kiln.Analyzers/tests/*, managed/services/Kiln.DevTools/tests
#   stage   Kiln.Api, Kiln.Testing and every managed/services/* DLL from that build into $KILN_OUT/check/stage, the set
#           managed/Kiln.Sdk/pack.sh stages and packs
#   compat  gate 2: tools/release/apicompat.sh against KILN_API_BASELINE (-v lists every addition)
# On a failure the stage prints the failing diagnostics (compiler errors, failed tests, BREAK lines), not the whole log;
# the full logs are in $KILN_OUT/check/*.log.
# The last line is one of:
#   check: PASSED in <s>s                                   exit 0
#   check: PASSED in <s>s without the API compat gate (--no-baseline)   exit 0
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
no_baseline=0 verbose=()
for a in "$@"; do
  case $a in
    --no-baseline) no_baseline=1 ;;
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
  if ((no_baseline)); then echo "check: PASSED in ${el}s without the API compat gate (--no-baseline)"; exit 0; fi
  echo "check: INCOMPLETE in ${el}s: the API compat gate did not run (KILN_API_BASELINE unset); set it, or pass --no-baseline"
  exit 3
fi
echo "check: PASSED in ${el}s"
