#!/usr/bin/env bash
# The launcher's release version, from VERSION (docs/kiln/launcher/releasing.md, "Versions"):
#   tools/release/version.sh dev [N]     X.Y.Z-dev.N   N: the given number, else the commit count of HEAD
#   tools/release/version.sh stable      X.Y.Z         (VERSION without its pre-release part)
#   tools/release/version.sh check V     exits 1 unless V is a release version for this VERSION (X.Y.Z or X.Y.Z-dev.N)
# VERSION holds the version being worked on (e.g. 0.2.0-dev). Dev releases number their builds; semver orders the
# number as a number (0.2.0-dev.9 < 0.2.0-dev.10 < 0.2.0). Build metadata (+...) is never used: the updater would
# ignore it.
set -euo pipefail
repo=$(cd "$(dirname "$0")/../.." && pwd)
base=$(sed 's/[-+].*//' "$repo/VERSION" | tr -d '[:space:]')
[[ $base =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "VERSION: cannot read X.Y.Z from '$(cat "$repo/VERSION")'" >&2; exit 2; }
case ${1:-} in
  dev)
    n=${2:-$(git -C "$repo" rev-list --count HEAD 2>/dev/null || true)}
    [[ $n =~ ^[1-9][0-9]*$ ]] || { echo "dev number: a positive integer, got '${n}'" >&2; exit 2; }
    echo "$base-dev.$n" ;;
  stable) echo "$base" ;;
  check)
    v=${2:?usage: $0 check VERSION}
    [[ $v == "$base" || $v =~ ^${base//./\\.}-dev\.[1-9][0-9]*$ ]] || {
      echo "version '$v' is not $base or $base-dev.<n> (VERSION is $(cat "$repo/VERSION"))" >&2; exit 1; } ;;
  *) echo "usage: $0 dev [N] | stable | check VERSION" >&2; exit 2 ;;
esac
