#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Builds one Kiln.Game.dll per verified game build into <host dir>/game/<side>-<sha12>/ (docs/kiln/typegen.md,
# "Shipping and staying loadable"): for every schema-2 dump sigdb/types/<side>/<sha256>.json whose build record
# sigdb/builds/<side>/<sha256>.json says "verified": true, Kiln.Game built from that dump (KilnGameDump) as the
# variant <side>-<sha12> (KilnGameVariant; sha12 = the first twelve hex digits of the exe's SHA-256, which is the
# dump's file name; no exe is read or named here). Kiln.Host loads the one for the running exe, else the
# flat <host dir>/Kiln.Game.dll. <host dir>/game/ is rebuilt as a whole: a build that is no longer verified, or whose
# dump is gone, loses its folder. Any variant that fails to build stops the script (exit 1), naming it.
#   KILN_OUT=<build dir> tools/typegen/build-game.sh <host dir>
# KILN_OUT (default <repo>/native/build) holds the build ($KILN_OUT/dotnet, logs in $KILN_OUT/build-game/). Needs
# dotnet and python3 (3.8 or newer), as every Kiln.Game build does.
set -euo pipefail
repo=$(cd "$(dirname "$0")/../.." && pwd)
host=${1:?usage: KILN_OUT=<build dir> $0 <host dir>}
out=${KILN_OUT:-$repo/native/build}
export KILN_OUT=$out
logs=$out/build-game
mkdir -p "$host" "$logs"
command -v python3 >/dev/null || { echo "build-game: needs python3 (3.8 or newer) to read the dumps and build Kiln.Game" >&2; exit 1; }

# side, sha256 and dump path of every verified schema-2 dump, one per line, in path order
# Capture the inventory before splitting it: process substitution would hide a Python failure after partial output.
inventory=$(python3 - "$repo" <<'PY'
import glob, json, os, re, sys
repo = sys.argv[1]
for path in sorted(glob.glob(os.path.join(repo, "sigdb", "types", "*", "*.json"))):
    side, sha = os.path.basename(os.path.dirname(path)), os.path.basename(path)[:-5]
    if side not in ("server", "client") or not re.fullmatch(r"[0-9a-f]{64}", sha):
        continue
    with open(path, encoding="utf-8") as f:
        dump = json.load(f)
    if dump.get("schema") != 2 or dump.get("side") != side or dump.get("exe_sha256") != sha:
        continue
    record = os.path.join(repo, "sigdb", "builds", side, sha + ".json")
    if not os.path.isfile(record):
        continue
    with open(record, encoding="utf-8") as f:
        if json.load(f).get("verified") is not True:
            continue
    print(side, sha, path)
PY
)
[[ -n $inventory ]] || { echo "build-game: no verified schema-2 dump under $repo/sigdb/types" >&2; exit 1; }
mapfile -t builds <<<"$inventory"

next=$host/game.new
rm -rf "$next" && mkdir -p "$next"
for line in "${builds[@]}"; do
  read -r side sha dump <<<"$line"
  variant=$side-${sha:0:12}
  log=$logs/$variant.log
  if ! dotnet build "$repo/managed/Kiln.Game/Kiln.Game.csproj" -c Release --artifacts-path "$out/dotnet" -v q -nologo \
       -p:KilnGameDump="$dump" -p:KilnGameVariant="$variant" >"$log" 2>&1; then
    { grep -E '(^|: )error' "$log" | sort -u || tail -25 "$log"; } >&2
    echo "build-game: Kiln.Game for $variant (${dump#"$repo"/}) failed to build (log: $log)" >&2
    rm -rf "$next"
    exit 1
  fi
  built=$out/dotnet/bin/Kiln.Game/release_$variant
  [[ -s $built/Kiln.Game.dll ]] || { echo "build-game: no $built/Kiln.Game.dll after the build of $variant" >&2; rm -rf "$next"; exit 1; }
  mkdir -p "$next/$variant"
  install -m 644 "$built/Kiln.Game.dll" "$next/$variant/"
  [[ -f $built/Kiln.Game.pdb ]] && install -m 644 "$built/Kiln.Game.pdb" "$next/$variant/"
  echo "build-game: $variant <- ${dump#"$repo"/}"
done
rm -rf "$host/game"
mv "$next" "$host/game"
