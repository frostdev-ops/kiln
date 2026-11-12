#!/usr/bin/env bash
# The API compat gate: compares each staged Kiln.*.dll with the DLL of the same name in the last released Kiln.Api package,
# with Microsoft.DotNet.ApiCompat.Tool (default mode, not strict: removals and changed signatures break, additions don't).
#   tools/release/apicompat.sh [-v] <baseline> <stage dir>
# <baseline>  KILN_API_BASELINE: the unzipped Kiln.Api.<ver>.nupkg of the last release (each DLL is found by name anywhere
#             below it: lib/net10.0/Kiln.Api.dll, kiln/lib/Kiln.<Service>.dll), or that .nupkg itself (unzipped under
#             $KILN_OUT/apicompat-baseline). An empty <baseline> prints the "gate did not run" warning and exits 3.
# <stage dir> the folder managed/Kiln.Sdk/pack.sh builds into and packs from (its $KILN_OUT/mdk-stage); the same set of
#             files goes into the package: Kiln.*.dll without Kiln.Host.dll and Kiln.Sdk.Tasks.dll (Kiln.Api.Ref.csproj).
# -v          list every added member by name (default: a count per assembly).
# Output, one line each (stdout):
#   apicompat: BREAK Kiln.Time: member removed or its signature changed: void Kiln.Time.ResetWeather() [CP0002 M:Kiln.Time.ResetWeather]
#   apicompat: BREAK Kiln.Foo.dll: assembly removed (in the baseline, not staged; also when it failed to build)
#   apicompat: EXPERIMENTAL Kiln.Time: ... [CP0002 <doc id> KILNX001]   allowed: [Experimental] in the baseline
#   apicompat: NEW Kiln.Bar.dll (no baseline DLL): not compared, passes
#   apicompat: ADDED Kiln.Api: 3                                       (with -v also: apicompat: ADD Kiln.Api: <member>)
#   apicompat: OK: ... | apicompat: FAILED: ...                        the last line
# Experimental API: a member that carries [Experimental("KILNX…")] in the baseline DLL (itself, its property or event, or
# a type around it) may change or go: tools/release/apicompat-report (C#, built on demand into $KILN_OUT) reads the tool's
# findings and drops those. Kiln.Game.dll is never compared (see below).
# Exit codes: 0 no break, 1 one or more breaks, 2 bad arguments, unusable baseline or a tool failure, 3 no baseline given.
# Needs: dotnet (SDK 10.0.1xx); the tool (pinned) is restored into $KILN_OUT/tools on first use, the only download.
set -euo pipefail
repo=$(cd "$(dirname "$0")/../.." && pwd)
out=${KILN_OUT:-$repo/native/build}
jobs=${KILN_CHECK_JOBS:-4}
APICOMPAT_VERSION=10.0.113  # Microsoft.DotNet.ApiCompat.Tool, the version of the SDK this repository builds with
say() { echo "apicompat: $*"; }
die() { echo "apicompat: $*" >&2; exit 2; }

verbose=()
while (($#)); do
  case $1 in
    -v|--verbose) verbose=(--verbose); shift ;;
    --) shift; break ;;
    -*) die "unknown option $1" ;;
    *) break ;;
  esac
done
[[ $# -eq 2 ]] || die "usage: apicompat.sh [-v] <baseline folder or .nupkg> <stage dir>"
baseline=$1 stage=$2

if [[ -z $baseline ]]; then
  {
    echo "apicompat: WARNING: ************************************************************************"
    echo "apicompat: WARNING: KILN_API_BASELINE is not set: the API compat gate DID NOT RUN."
    echo "apicompat: WARNING: Nothing checked that this build keeps the public API of the last release."
    echo "apicompat: WARNING: Set KILN_API_BASELINE to the unzipped Kiln.Api.<ver>.nupkg of the last release"
    echo "apicompat: WARNING: (or to that .nupkg) to run it."
    echo "apicompat: WARNING: ************************************************************************"
  } >&2
  exit 3
fi
[[ -d $stage && -f $stage/Kiln.Api.dll ]] || die "stage dir $stage has no Kiln.Api.dll"
stage=$(cd "$stage" && pwd)

# The baseline: a folder, or a .nupkg unzipped under $KILN_OUT
if [[ -f $baseline && $baseline == *.nupkg ]]; then
  dir=$out/apicompat-baseline/$(basename "$baseline" .nupkg)
  rm -rf "$dir" && mkdir -p "$dir"
  python3 -I -m zipfile -e "$baseline" "$dir" || die "cannot unzip $baseline"
  baseline=$dir
fi
[[ -d $baseline ]] || die "KILN_API_BASELINE=$baseline is neither a folder nor a .nupkg"
baseline=$(cd "$baseline" && pwd)
[[ -n $(find "$baseline" -name Kiln.Api.dll -type f -print -quit) ]] || die "KILN_API_BASELINE=$baseline holds no Kiln.Api.dll"

# The tool (pinned) and its Roslyn, which the report helper compiles against
tooldir=$out/tools/apicompat-$APICOMPAT_VERSION
if [[ ! -x $tooldir/apicompat ]]; then
  dotnet tool install Microsoft.DotNet.ApiCompat.Tool --version "$APICOMPAT_VERSION" --tool-path "$tooldir" >/dev/null \
    || die "cannot restore Microsoft.DotNet.ApiCompat.Tool $APICOMPAT_VERSION into $tooldir"
fi
roslyn=$(find "$tooldir/.store" -path '*/codeanalysis/Microsoft.CodeAnalysis.dll' -print -quit)
[[ -n $roslyn ]] || die "no Roslyn in $tooldir"
roslyn=${roslyn%/*}
helper_src=$repo/tools/release/apicompat-report
helper=$out/tools/apicompat-report/ApiCompatReport.dll
if [[ ! -f $helper || -n $(find "$helper_src" -newer "$helper" -type f -print -quit) ]]; then
  dotnet build "$helper_src/ApiCompatReport.csproj" -c Release -m:"$jobs" -o "${helper%/*}" --artifacts-path "$out/dotnet" \
    -p:KilnRoslynDir="$roslyn" -v q -nologo >"$out/apicompat-report.build.log" 2>&1 \
    || { tail -20 "$out/apicompat-report.build.log" >&2; die "cannot build tools/release/apicompat-report"; }
fi

# The .NET reference pack, so the tool resolves System.* on both sides
droot=$(dirname "$(readlink -f "$(command -v dotnet)")")
refpack=$(find "$droot/packs/Microsoft.NETCore.App.Ref" -mindepth 3 -maxdepth 3 -type d -path '*/10.*/ref/net10.0' 2>/dev/null | sort -V | tail -1)
[[ -n $refpack ]] || die "no Microsoft.NETCore.App.Ref 10.x under $droot/packs"

# Pair the DLLs by name.
# Kiln.Game.dll is never compared, on either side: it is generated per verified game build (typegen), so its public
# surface follows the game, not Kiln's source; typegen's own tests guard it.
skip_name() { [[ $1 == Kiln.Host.dll || $1 == Kiln.Sdk.Tasks.dll || $1 == Kiln.Game.dll ]]; }
declare -A base=()
game_seen=0
while IFS= read -r f; do
  n=${f##*/}
  [[ $n == Kiln.Game.dll ]] && game_seen=1
  skip_name "$n" && continue
  [[ -n ${base[$n]:-} ]] && { echo "apicompat: WARNING: $n twice in the baseline; using ${base[$n]}" >&2; continue; }
  base[$n]=$f
done < <(find "$baseline" -name 'Kiln.*.dll' -type f | LC_ALL=C sort)
left=() right=() breaks=0 newn=0
for f in "$stage"/Kiln.*.dll; do
  n=${f##*/}
  [[ $n == Kiln.Game.dll ]] && game_seen=1
  skip_name "$n" && continue
  if [[ -n ${base[$n]:-} ]]; then left+=("${base[$n]}") right+=("$f"); else say "NEW $n (no baseline DLL): not compared, passes"; newn=$((newn + 1)); fi
done
((game_seen)) && say "Kiln.Game.dll skipped: generated per game build, not compared (typegen's tests guard it)"
for n in "${!base[@]}"; do
  [[ -f $stage/$n ]] && continue
  say "BREAK $n: assembly removed (in the baseline, not staged)"; breaks=$((breaks + 1))
done
((${#left[@]})) || die "no staged DLL has a baseline DLL to compare with"

# Run the tool both ways; its findings come back as suppression files (doc ids), the helper turns them into lines.
work=$out/apicompat-work
rm -rf "$work" && mkdir -p "$work"
bdirs=$(for f in "${left[@]}"; do echo "${f%/*}"; done | LC_ALL=C sort -u | paste -sd,)
run() { # name lefts... -- rights... -- lref rref
  local name=$1; shift
  if ! "$tooldir/apicompat" "$@" --generate-suppression-file --suppression-output-file "$work/$name.xml" >"$work/$name.log" 2>&1; then
    tail -20 "$work/$name.log" >&2; die "Microsoft.DotNet.ApiCompat.Tool failed ($name)"
  fi
  if grep -qE 'CP1001|CP1002' "$work/$name.log"; then
    grep -E 'CP1001|CP1002' "$work/$name.log" | sort -u | head -10 >&2; die "the tool could not resolve references ($name)"
  fi
}
run breaks -l "${left[@]}" -r "${right[@]}" --lref "$bdirs,$refpack" --rref "$stage,$refpack"
run adds -l "${right[@]}" -r "${left[@]}" --lref "$stage,$refpack" --rref "$bdirs,$refpack"
rc=0
dotnet "$helper" --baseline-refs "$bdirs,$refpack" --tree-refs "$stage,$refpack" --breaks "$work/breaks.xml" \
  --adds "$work/adds.xml" "${verbose[@]}" >"$work/report.txt" || rc=$?
((rc <= 1)) || die "tools/release/apicompat-report failed (exit $rc)"
sed 's/^/apicompat: /' "$work/report.txt"
breaks=$((breaks + $(grep -c '^BREAK ' "$work/report.txt" || true)))
adds=$(awk '/^ADDED /{s += $NF} END {print s + 0}' "$work/report.txt")
exp=$(grep -c '^EXPERIMENTAL ' "$work/report.txt" || true)
summary="${#left[@]} assemblies compared with $baseline, $newn new, $adds additions, $exp experimental changes"
if ((breaks)); then say "FAILED: $breaks breaking changes (BREAK lines above); $summary"; exit 1; fi
say "OK: no breaking change; $summary"
