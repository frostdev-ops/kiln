#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Kiln's Linux build image (Dockerfile here: Ubuntu 22.04, glibc 2.35): prints its tag, building it when it is missing.
#   tools/release/linux-build/image.sh [--rebuild]
# The tag is kiln-linux-build:<first 12 hex of the Dockerfile's sha256>, so editing the Dockerfile makes a new image;
# release.sh and run.sh call this first. Build output goes to stderr, the tag alone to stdout.
# --rebuild: build it again from scratch under the same tag (no layer cache): Ubuntu's current security updates, above
#   all WebKitGTK's, which the AppImage carries. Do this now and then, and before a stable release.
# After a build, older kiln-linux-build images are removed (each is ~2 GB on Docker's disk).
set -euo pipefail
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
rebuild=0
case ${1:-} in
  --rebuild) rebuild=1 ;;
  "") ;;
  -h|--help) sed -n '3,9p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
  *) echo "usage: $0 [--rebuild]" >&2; exit 2 ;;
esac
command -v docker >/dev/null || { echo "image.sh: docker is not installed (release builds need it; see releasing.md)" >&2; exit 1; }
docker info >/dev/null 2>&1 || { echo "image.sh: cannot reach the Docker daemon (is it running, are you in the docker group?)" >&2; exit 1; }
sum=$(sha256sum "$here/Dockerfile" | cut -c1-12)
tag=kiln-linux-build:$sum
if ((rebuild)) || ! docker image inspect "$tag" >/dev/null 2>&1; then
  echo "image.sh: building $tag$( ((rebuild)) && echo " from scratch") (first time: a few minutes)" >&2
  args=(--tag "$tag" --label "io.frostdev.kiln.dockerfile-sha256=$(sha256sum "$here/Dockerfile" | cut -d' ' -f1)")
  ((rebuild)) && args+=(--no-cache --pull)
  # no build context: the Dockerfile copies nothing in
  docker build "${args[@]}" - <"$here/Dockerfile" >&2
  # older images of this name: superseded
  docker image ls --format '{{.Repository}}:{{.Tag}}' kiln-linux-build | while read -r old; do
    [[ $old == "$tag" || $old == *:"<none>" ]] && continue
    echo "image.sh: removing the older image $old" >&2
    docker image rm "$old" >/dev/null 2>&1 || true
  done
  docker image prune -f --filter label=io.frostdev.kiln.dockerfile-sha256 >/dev/null 2>&1 || true
fi
echo "$tag"
