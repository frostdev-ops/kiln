#!/usr/bin/env bash
# The complete source of Kiln's ShroudForge component (the frostdev-ops/shroudforge fork, submodule
# third_party/shroudforge, GPL-3 because it links kfc-parser): the fork at the commit Kiln pins plus every submodule at
# the commit the fork pins (git archive skips submodules). Writes a reproducible tarball and, optionally, extracts the
# same tree for the build. Used by release.sh; docs/kiln/shroudforge-compat.md section 10.
#   tools/release/lib/sf-source.sh <fork checkout> <commit> <out .tar.gz> [extract dir]
set -euo pipefail
fork=${1:?fork checkout}
commit=${2:?commit}
tarball=${3:?out tarball}
extract=${4:-}
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
name=shroudforge-kiln-${commit:0:12}
dst=$tmp/$name
mkdir -p "$dst"
git -C "$fork" cat-file -e "$commit^{commit}" 2>/dev/null || git -C "$fork" fetch -q origin "$commit"
git -C "$fork" archive --format=tar "$commit" | tar -xf - -C "$dst"
# each gitlink of the commit: archive the submodule's checkout at its pinned commit
git -C "$fork" ls-tree -r "$commit" | awk '$2 == "commit" {print $3, $4}' | while read -r sub path; do
  repo=$fork/$path
  [[ -e $repo/.git ]] || { echo "sf-source: submodule $path is not checked out in $fork (git submodule update --init --recursive)" >&2; exit 1; }
  git -C "$repo" cat-file -e "$sub^{commit}" 2>/dev/null || git -C "$repo" fetch -q origin "$sub"
  mkdir -p "$dst/$path"
  git -C "$repo" archive --format=tar "$sub" | tar -xf - -C "$dst/$path"
done
epoch=$(git -C "$fork" log -1 --format=%ct "$commit")
printf 'frostdev-ops/shroudforge %s\n' "$commit" > "$dst/KILN-SOURCE-COMMIT"
mkdir -p "$(dirname "$tarball")"
tar --sort=name --owner=0 --group=0 --numeric-owner --mtime="@$epoch" -C "$tmp" -czf "$tarball" "$name"
if [[ -n $extract ]]; then
  mkdir -p "$extract"
  tar -xzf "$tarball" -C "$extract" --strip-components=1
fi
echo "sf-source: $tarball ($(du -h "$tarball" | cut -f1))"
