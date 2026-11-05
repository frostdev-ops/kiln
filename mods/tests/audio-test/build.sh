#!/usr/bin/env bash
# Builds audio-test into $KILN_OUT/mods/audio-test and generates its test tones there (fixtures/make_tones.py; reads
# the game's reflection from the client exe, writes assets/ in the build folder only).
#   KILN_OUT=<build dir> mods/tests/audio-test/build.sh [--exe <enshrouded.exe>]
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:?set KILN_OUT}"
exe=${HOME}/.local/share/Steam/steamapps/common/Enshrouded/enshrouded.exe
[[ ${1:-} == --exe ]] && exe=$2
out=$KILN_OUT/mods/audio-test
dotnet build "$here/AudioTest.csproj" -c Release -o "$out" --artifacts-path "$KILN_OUT/dotnet" -v q -nologo
# a private copy of the exe: the fixture reads its reflection only (never the game folder's file in place)
copy=$KILN_OUT/audio-test-exe/enshrouded.exe
mkdir -p "$(dirname "$copy")"
if [[ ! -f $copy ]] || ! cmp -s "$exe" "$copy"; then cp "$exe" "$copy"; fi
python3 "$here/fixtures/make_tones.py" --exe "$copy" --out "$out"
