#!/usr/bin/env bash
# The Python the Windows launcher ships for the cook tools (kiln_manage::cooktools): python.org's embeddable package
# for Windows x64, pinned by version and sha256. Nothing is installed on a player's machine: the launcher runs
# <its folder>/python/python.exe, and a Python the player has is neither used nor changed. Linux uses the system's
# python3 (docs/kiln/launcher/releasing.md, "The bundled Python").
#   tools/package/fetch-python.sh [--check] [--refresh] [--print-root]
# Result: <repo>/.cache/python-win-x64/python.zip (the pinned zip) and .cache/python-win-x64/root/ (unpacked; what
# tools/launcher/build-windows.sh puts into the bundle as python/). A cached zip with the pinned sha256 is used as is:
# nothing is downloaded. --check only verifies (exit 1 when the zip is missing or wrong, or root/ is not from it);
# --refresh unpacks root/ again; --print-root prints root/'s path on success and nothing else on stdout.
# The pin: https://www.python.org/downloads/release/python-31410/, "Windows embeddable package (64-bit)",
# 12,619,664 bytes (downloaded and compared on 2026-11-12; a flat package with _zstd.pyd and _ctypes.pyd, and every
# module the cook tools import is in it or in its python314.zip, except the optional capstone and zstandard). The cook
# tools need 3.14 or newer here: its standard library reads zstd (compression.zstd, _zstd.pyd), which the game's
# archives use; the unpacked package is checked for it. To raise the pin: take VERSION and SHA256 from the release's
# page on python.org, run this script once (it downloads and verifies), then tools/release/test_cooktools.py.
# Tests never download: with KILN_FETCH_PYTHON_TEST=1 (refused in a release build) the zip's source, hash and cache
# come from KILN_FETCH_PYTHON_URL, KILN_FETCH_PYTHON_SHA256 and KILN_FETCH_PYTHON_CACHE.
set -euo pipefail
repo=$(cd "$(dirname "$0")/../.." && pwd)
VERSION=3.14.10
URL=https://www.python.org/ftp/python/$VERSION/python-$VERSION-embed-amd64.zip
SHA256=90905e64a840557d8c1bc4f5b74db3d7ee85e3ffb217611cac1c7813acea4159
cache=$repo/.cache/python-win-x64
if [[ ${KILN_FETCH_PYTHON_TEST:-} == 1 ]]; then
  [[ ${KILN_RELEASE_BUILD:-} == 1 ]] && { echo "fetch-python: KILN_FETCH_PYTHON_TEST is for tests, never a release build" >&2; exit 2; }
  URL=${KILN_FETCH_PYTHON_URL:?} SHA256=${KILN_FETCH_PYTHON_SHA256:?} cache=${KILN_FETCH_PYTHON_CACHE:?}
fi
zip=$cache/python.zip
root=$cache/root
check=0 refresh=0 print_root=0
for a in "$@"; do
  case $a in
    --check) check=1 ;;
    --refresh) refresh=1 ;;
    --print-root) print_root=1 ;;
    *) echo "unknown option $a" >&2; exit 2 ;;
  esac
done
say() { echo "fetch-python: $*" >&2; }
sha256() { sha256sum "$1" | cut -d' ' -f1; }
ok_zip() { [[ -f $zip && $(sha256 "$zip") == "$SHA256" ]]; }
# what the cook tools need of the package: the interpreter, its standard library, the path file that keeps it to
# itself (isolated: no registry, no PYTHONPATH, no user site), zstd, and the licence that must ship with it
whole() { # dir
  local d=$1 f
  for f in python.exe LICENSE.txt _zstd.pyd; do [[ -f $d/$f ]] || { echo "$f"; return 1; }; done
  compgen -G "$d/python3*.zip" >/dev/null || { echo "python3*.zip (the standard library)"; return 1; }
  compgen -G "$d/python3*._pth" >/dev/null || { echo "python3*._pth (the path file)"; return 1; }
}
# the unpacked folder is the verified zip, file for file (names, sizes, CRC-32) and nothing else: a file deleted,
# changed or added after the unpack means the folder is not what was pinned. Prints the first difference.
same_as_zip() { # dir
  python3 -I - "$zip" "$1" <<'PY'
import os, sys, zipfile, zlib
z, d = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(z) as f:
    want = {i.filename: i for i in f.infolist() if not i.is_dir()}
have = set()
for base, _, files in os.walk(d):
    for n in files:
        have.add(os.path.relpath(os.path.join(base, n), d).replace(os.sep, "/"))
for n in sorted(set(want) - have):
    print(n + " is missing"); sys.exit(1)
for n in sorted(have - set(want)):
    print(n + " is not in the package"); sys.exit(1)
for n, i in sorted(want.items()):
    p = os.path.join(d, n)
    if os.path.islink(p) or os.path.getsize(p) != i.file_size:
        print(n + " differs"); sys.exit(1)
    crc = 0
    with open(p, "rb") as g:
        for chunk in iter(lambda: g.read(1 << 20), b""):
            crc = zlib.crc32(chunk, crc)
    if crc != i.CRC:
        print(n + " differs"); sys.exit(1)
PY
}

if ! ok_zip; then
  if ((check)); then say "$zip is missing or not the pinned Python $VERSION (sha256)"; exit 1; fi
  mkdir -p "$cache"
  tmp=$cache/python.zip.download
  say "downloading Python $VERSION (Windows x64, embeddable) from $URL"
  curl -fL --retry 3 -o "$tmp" "$URL" >&2
  got=$(sha256 "$tmp")
  if [[ $got != "$SHA256" ]]; then
    rm -f "$tmp"
    say "sha256 mismatch: got $got, pinned $SHA256. The file is not the one that was pinned: nothing was kept."
    exit 1
  fi
  mv "$tmp" "$zip"
  refresh=1
fi
[[ -d $root && -f $cache/root.from && $(cat "$cache/root.from") == "$SHA256" ]] || refresh=1
# a folder that is no longer what the zip holds is unpacked again, or fails the check
if ((!refresh)) && ! differs=$(same_as_zip "$root"); then
  if ((check)); then say "$root is not the pinned package any more: $differs (run without --check to unpack it again)"; exit 1; fi
  say "$root is not the pinned package any more ($differs): unpacking it again"
  refresh=1
fi
if ((check)); then
  ((refresh)) && { say "$root is not unpacked from the pinned zip (run without --check)"; exit 1; }
  missing=$(whole "$root") || { say "$root lacks $missing"; exit 1; }
  say "Python $VERSION ok ($zip, $root)"
  ((print_root)) && echo "$root"
  exit 0
fi
if ((refresh)); then
  rm -rf "$root.tmp"
  mkdir -p "$root.tmp"
  unzip -q "$zip" -d "$root.tmp"
  if ! missing=$(whole "$root.tmp"); then
    rm -rf "$root.tmp"
    say "the package lacks $missing: the cook tools cannot run on it (the pin must be an embeddable Python 3.14 or newer)"
    exit 1
  fi
  rm -rf "$root" "$cache/root.from"
  mv "$root.tmp" "$root"
  echo "$SHA256" > "$cache/root.from"
  say "unpacked Python $VERSION into $root"
fi
say "Python $VERSION ok ($zip)"
((print_root)) && echo "$root"
exit 0
