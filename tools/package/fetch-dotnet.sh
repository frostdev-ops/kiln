#!/usr/bin/env bash
# The private .NET runtime Kiln ships in kiln/dotnet/ (win-x64, from Microsoft's dotnet CDN), pinned by sha512.
#   tools/package/fetch-dotnet.sh [--check] [--refresh]
# Result: <repo>/.cache/dotnet-win-x64/runtime.zip (the pinned zip) and .cache/dotnet-win-x64/root/ (unpacked; what
# tools/deploy-server.sh, tools/client/client.sh and tools/package/stage.sh copy). A cached zip with the pinned sha512
# is used as is: nothing is downloaded. --check only verifies (exit 1 when the zip is missing or wrong); --refresh
# unpacks root/ again from the zip.
# The pin: https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json, runtime 10.0.14,
# rid win-x64, dotnet-runtime-win-x64.zip (the hash there is a sha512).
set -euo pipefail
repo=$(cd "$(dirname "$0")/../.." && pwd)
VERSION=10.0.14
URL=https://builds.dotnet.microsoft.com/dotnet/Runtime/$VERSION/dotnet-runtime-$VERSION-win-x64.zip
SHA512=a176095f1db69b42c81acc60940744078d6bf2d2ac643e418342aa9c81889f4bd13e42f4621b131f9e77e4bc781ad46e375956219df9a4f979031d2a5a8d93cd
cache=$repo/.cache/dotnet-win-x64
zip=$cache/runtime.zip
root=$cache/root
check=0 refresh=0
for a in "$@"; do
  case $a in
    --check) check=1 ;;
    --refresh) refresh=1 ;;
    *) echo "unknown option $a" >&2; exit 2 ;;
  esac
done
sha512() { sha512sum "$1" | cut -d' ' -f1; }
ok_zip() { [[ -f $zip && $(sha512 "$zip") == "$SHA512" ]]; }

if ! ok_zip; then
  if ((check)); then echo "fetch-dotnet: $zip missing or not the pinned .NET $VERSION (sha512)" >&2; exit 1; fi
  mkdir -p "$cache"
  tmp=$cache/runtime.zip.download
  echo "fetch-dotnet: downloading .NET $VERSION (win-x64) from $URL"
  curl -fL --retry 3 -o "$tmp" "$URL"
  got=$(sha512 "$tmp")
  [[ $got == "$SHA512" ]] || { rm -f "$tmp"; echo "fetch-dotnet: sha512 mismatch: got $got" >&2; exit 1; }
  mv "$tmp" "$zip"
  refresh=1
fi
if [[ ! -f $root/dotnet.exe || ! -d $root/shared/Microsoft.NETCore.App/$VERSION ]]; then refresh=1; fi
if ((check)); then
  ((refresh)) && { echo "fetch-dotnet: $root is not unpacked from the pinned zip (run without --check)" >&2; exit 1; }
  echo "fetch-dotnet: .NET $VERSION ok ($zip, $root)"
  exit 0
fi
if ((refresh)); then
  rm -rf "$root.tmp"
  mkdir -p "$root.tmp"
  unzip -q "$zip" -d "$root.tmp"
  rm -rf "$root"
  mv "$root.tmp" "$root"
  echo "fetch-dotnet: unpacked .NET $VERSION into $root"
fi
echo "fetch-dotnet: .NET $VERSION ok ($zip)"
