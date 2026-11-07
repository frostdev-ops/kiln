#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Starts a release's Linux files in a clean older distro, with the release mounted read-only.
#   start-test.sh RELEASE_DIR [--distro ubuntu22|arch2024] [--distribution direct|nexus] [--out EMPTY_DIR] [--wait SECONDS]
# Output (owned by the caller): run.log, selftest.json, launcher.log, windows.txt, xvfb.log,
# screen.png (or screen.xwd without ImageMagick). The output directory must be empty and outside the release.
# ubuntu22 uses the build's digest-pinned Ubuntu base; arch2024 uses the 2024-01-01 Arch archive.
set -euo pipefail
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
source "$here/paths.sh"
rel="" distro=ubuntu22 distribution=direct out="" wait=25
while (($#)); do
  case $1 in
    --distro|--distribution|--out|--wait)
      (($# >= 2)) || { echo "start-test: $1 needs a value" >&2; exit 2; }
      case $1 in --distro) distro=$2 ;; --distribution) distribution=$2 ;; --out) out=$2 ;; --wait) wait=$2 ;; esac
      shift 2 ;;
    -h|--help) sed -n '3,7p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*) echo "unknown option $1" >&2; exit 2 ;;
    *) [[ -z $rel ]] || { echo "start-test: one release directory only" >&2; exit 2; }; rel=$1; shift ;;
  esac
done
[[ -d $rel/linux && $wait =~ ^[0-9]+$ ]] || { echo "usage: $0 RELEASE_DIR [--distro ubuntu22|arch2024] [--distribution direct|nexus] [--out EMPTY_DIR] [--wait SECONDS]" >&2; exit 2; }
case $distribution in direct|nexus) ;; *) echo "--distribution direct|nexus" >&2; exit 2 ;; esac
[[ $distribution == direct || -d $rel/nexus/linux ]] || { echo "start-test: no Nexus Linux build" >&2; exit 2; }
rel=$(realpath -e -- "$rel")
default_out=start-test-$distro
[[ $distribution == direct ]] || default_out+=-nexus
out=$(realpath -m -- "${out:-$default_out}")
safe_dir "$rel"
safe_dir "$out"
disjoint_dirs "$rel" "$out"
(( $(id -u) != 0 )) || { echo "start-test: run as the release owner, not root" >&2; exit 1; }
case $distro in
  ubuntu22) image=$(sed -n 's/^FROM \(ubuntu:22.04@sha256:[0-9a-f]*\).*/\1/p' "$here/Dockerfile") ;;
  arch2024) image=archlinux:base-20240101.0.204074 ;;
  *) echo "--distro ubuntu22|arch2024" >&2; exit 2 ;;
esac
[[ -n $image ]] || { echo "start-test: no Ubuntu base digest found" >&2; exit 1; }
docker info >/dev/null 2>&1 || { echo "start-test: cannot reach the Docker daemon" >&2; exit 1; }
mkdir -p -- "$out"
[[ -z $(ls -A -- "$out") ]] || { echo "start-test: --out must be empty: $out" >&2; exit 2; }
chmod 700 "$out"
scratch=$(mktemp -d -t kiln-start-test.XXXXXXXX)
name=${scratch##*/}
client=""
cleanup() {
  docker rm -f "$name" >/dev/null 2>&1 || true
  [[ -z $client ]] || { kill "$client" 2>/dev/null || true; wait "$client" 2>/dev/null || true; }
  rm -rf -- "$scratch"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP
(
docker run --rm --init --platform linux/amd64 --name "$name" -v "$rel:/r:ro" -v "$out:/out" \
  -v "$here/start-test-container.sh:/start-test.sh:ro" \
  -e DISTRO="$distro" -e DISTRIBUTION="$distribution" -e WAIT="$wait" -e OWNER_UID="$(id -u)" -e OWNER_GID="$(id -g)" \
  --tmpfs /tmp:rw,exec,nosuid,size=4g "$image" bash /start-test.sh 2>&1 | tee "$out/run.log"
) &
client=$!
if wait "$client"; then code=0; else code=$?; fi
client=""
if [[ -f $out/screen.xwd ]] && command -v magick >/dev/null; then
  if magick "xwd:$out/screen.xwd" "$out/screen.png"; then rm -f "$out/screen.xwd"; else code=1; fi
fi
echo "start-test: $distro/$distribution: results in $out"
exit "$code"
