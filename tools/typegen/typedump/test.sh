#!/usr/bin/env bash
# Host test for typedump. Builds the tool and a synthetic image, checks the exact JSON the tool writes for it and that
# the image's default-value bytes and flags are nowhere in it. With KILN_SERVER_EXE / KILN_CLIENT_EXE set it also
# dumps those exes (read where they are) into $KILN_OUT/typedump/real and compares them byte for byte with the dumps
# committed under sigdb/types/. Output goes under $KILN_OUT/typedump only.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../../.." && pwd)
export KILN_OUT=${KILN_OUT:-$repo/build}
out=$KILN_OUT/typedump
cxx=${CXX:-$(command -v clang++ || command -v g++)}
"$here/build.sh" >/dev/null
tool=$out/typedump
"$cxx" -std=c++20 -O2 -Wall -Wextra -o "$out/mkimage" "$here/mkimage.cpp"

fails=0
ok() { echo "ok   $*"; }
bad() { echo "FAIL $*"; fails=$((fails + 1)); }

t=$out/test
rm -rf "$t"
mkdir -p "$t"
"$out/mkimage" "$t/synthetic.exe"
sha=$(sha256sum "$t/synthetic.exe" | cut -d' ' -f1)
got=$("$tool" --side server --out "$t/a" "$t/synthetic.exe" 2>/dev/null)
[[ $got == "$t/a/server/$sha.json" ]] && ok "output named <out>/<side>/<sha256>.json" || bad "output path $got"
sed "s/@SHA@/$sha/" "$here/expected-synthetic.json" > "$t/expected.json"
if cmp -s "$t/expected.json" "$got"; then ok "synthetic dump equals expected-synthetic.json"
else bad "synthetic dump differs:"; diff "$t/expected.json" "$got" || true; fi
marker=KILN-DEFAULT-VALUE-BYTES-DO-NOT-DUMP
hexm=$(printf %s "$marker" | od -An -tx1 | tr -d ' \n')
if grep -qiF -e "$marker" -e "${marker:0:8}" -e "$hexm" -e "${hexm:0:16}" "$got"; then bad "default-value bytes in the dump"
else ok "default-value bytes are nowhere in the dump"; fi
if grep -qE '"(defaults|default|flags|rva|address|base|index)"|165|0xA5' "$got"; then bad "flags or address keys in the dump"
else ok "no flags byte, no address keys"; fi
if grep -qE 'Hidden|Unused|secret' "$got"; then bad "a type behind a leaf or outside the closure was dumped"
else ok "nothing behind a leaf, nothing outside the closure"; fi
"$tool" --side server --out "$t/b" "$t/synthetic.exe" >/dev/null 2>&1
cmp -s "$got" "$t/b/server/$sha.json" && ok "two runs give the same bytes" || bad "two runs differ"
head -c 8192 "$t/synthetic.exe" > "$t/truncated.exe"
if "$tool" --side server --out "$t/c" "$t/truncated.exe" >/dev/null 2>&1 || [[ -e $t/c ]]; then bad "a truncated image was dumped"
else ok "a truncated image fails and writes nothing"; fi
if "$tool" --side lab --out "$t/c" "$t/synthetic.exe" >/dev/null 2>&1; then bad "an unknown side was accepted"
else ok "an unknown side is refused"; fi

for side in server client; do
  var=KILN_${side^^}_EXE
  exe=${!var:-}
  [[ -n "$exe" ]] || { echo "skip $side exe ($var not set)"; continue; }
  [[ -f "$exe" ]] || { bad "$var is not a file"; continue; }
  r=$out/real
  f=$("$tool" --side "$side" --out "$r/1" "$exe") || { bad "dump of the $side exe failed"; continue; }
  "$tool" --side "$side" --out "$r/2" "$exe" >/dev/null 2>&1
  name=$(basename "$f")
  cmp -s "$f" "$r/2/$side/$name" && ok "$side: two runs give the same bytes" || bad "$side: two runs differ"
  committed=$repo/sigdb/types/$side/$name
  if [[ ! -f $committed ]]; then bad "$side: no committed dump sigdb/types/$side/$name"
  elif cmp -s "$f" "$committed"; then ok "$side: equals the committed sigdb/types/$side/$name"
  else bad "$side: differs from the committed sigdb/types/$side/$name"; fi
done

((fails == 0)) && echo "typedump: all checks passed" || { echo "typedump: $fails check(s) failed"; exit 1; }
