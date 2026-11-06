#!/usr/bin/env bash
# Creates a DRAFT GitHub release from a release folder (tools/release/release.sh) and uploads its files. A draft is
# visible only to the repository's maintainers; nothing is published (tools/release/publish.sh does that).
#   tools/release/github-draft.sh RELEASE_DIR [--repo OWNER/NAME] [--target COMMIT] [--dry-run]
# The tag is v<version>, created by GitHub on publish at --target (default: the commit the folder was built from,
# which must be pushed). GitHub release files have one flat namespace, so the upload set is staged first in
# <RELEASE_DIR>.github/ with unique names and its own SHA256SUMS:
#   the AppImage, deb, rpm, setup.exe and their .sig files, latest.json (the update manifest with GitHub URLs),
#   kiln-runtime-<v>.zip, kilnctl-<v>-linux-x86_64, kilnctl-<v>-windows-x86_64.exe, kiln-<v>-linux-x86_64,
#   kiln-<v>-windows-x86_64.exe, the nupkgs, the vsix, kiln-samples-<v>.zip, the source tarball, README.txt,
#   INSTALL-RUNTIME.txt, THIRD-PARTY-NOTICES.txt, SHA256SUMS.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo_dir=$(cd "$here/../.." && pwd)
dir="" gh_repo=frostdev-ops/kiln target="" dry=0
while (($#)); do
  case $1 in
    --repo) gh_repo=$2; shift 2 ;;
    --target) target=$2; shift 2 ;;
    --dry-run) dry=1; shift ;;
    -*) echo "unknown option $1" >&2; exit 2 ;;
    *) dir=$(realpath "$1"); shift ;;
  esac
done
[[ -d $dir ]] || { echo "usage: $0 RELEASE_DIR [--repo OWNER/NAME] [--target COMMIT] [--dry-run]" >&2; exit 2; }
version=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$dir/updates/github/latest.json")
built=$(sed -n 's/.*from commit \([0-9a-f]\{7,40\}\).*/\1/p' "$dir/README.txt" | head -1)
[[ -n $built ]] || { echo "$dir was not built from a commit (a test build of a working tree?): not for GitHub" >&2; exit 1; }
target=${target:-$(git -C "$repo_dir" rev-parse --verify "$built^{commit}")}
[[ $(git -C "$repo_dir" rev-parse --short=7 "$target") == "$built" ]] || { echo "--target $target is not the commit $built the folder was built from" >&2; exit 1; }
python3 "$here/lib/release.py" sums "$dir" --check
tag=v$version
pre=(); [[ $version == *-* ]] && pre=(--prerelease)

stage=$dir.github
rm -rf "$stage" && mkdir -p "$stage"
put() { ln "$1" "$stage/$2" 2>/dev/null || cp "$1" "$stage/$2"; }
for f in "$dir"/linux/* "$dir"/windows/*; do put "$f" "$(basename "$f")"; done
put "$dir/updates/github/latest.json" latest.json
put "$dir/runtime/kiln-runtime-$version.zip" "kiln-runtime-$version.zip"
put "$dir/runtime/INSTALL-RUNTIME.txt" INSTALL-RUNTIME.txt
put "$dir/runtime/kilnctl" "kilnctl-$version-linux-x86_64"
put "$dir/sdk/kiln" "kiln-$version-linux-x86_64"
[[ -f $dir/runtime/kilnctl.exe ]] && put "$dir/runtime/kilnctl.exe" "kilnctl-$version-windows-x86_64.exe"
[[ -f $dir/sdk/kiln.exe ]] && put "$dir/sdk/kiln.exe" "kiln-$version-windows-x86_64.exe"
for f in "$dir"/sdk/*.nupkg "$dir"/sdk/*.vsix "$dir"/source/*; do put "$f" "$(basename "$f")"; done
(cd "$dir" && zip -qr -X "$stage/kiln-samples-$version.zip" samples)
for f in README.txt THIRD-PARTY-NOTICES.txt; do put "$dir/$f" "$f"; done
(cd "$stage" && sha256sum -- * >SHA256SUMS)
echo "staged $(ls "$stage" | wc -l) files in $stage"

if gh release view "$tag" --repo "$gh_repo" >/dev/null 2>&1; then
  echo "$gh_repo already has a release $tag; delete the draft first if you mean to replace it" >&2
  exit 1
fi
cmd=(gh release create "$tag" --repo "$gh_repo" --draft "${pre[@]}" --target "$target" --title "Kiln $version"
  --notes-file "$dir/RELEASE-NOTES.md")
if ((dry)); then
  printf 'would run: %q ' "${cmd[@]}"; echo "$stage/*"
  exit 0
fi
"${cmd[@]}" "$stage"/*
echo "draft $tag created on $gh_repo (not published): gh release view $tag --repo $gh_repo --web"
