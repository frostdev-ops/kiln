#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Runs a command in Kiln's Linux build container (Dockerfile here: Ubuntu 22.04, glibc 2.35), as the calling user.
#   tools/release/linux-build/run.sh [--mount DIR]... [--ro DIR]... [--env NAME]... [--nice] [--] COMMAND [ARG]...
# --mount DIR / --ro DIR: a host folder the command needs, read-write / read-only, at the same path inside. The command
#   runs in the current folder, which must be inside one of them.
# --env NAME: pass that variable from this environment (unset ones are skipped).
# --nice: run at the lowest CPU and IO priority (KILN_RELEASE_NICE=1 in release.sh).
# Always there: KILN_GLIBC_DIR (default <repo>/.out/glibc), read-write at the same path. It holds the container's HOME
#   (home/: Tauri's downloaded AppImage tools), CARGO_HOME (cargo-home/: crates) and the cargo target dir release.sh
#   uses for Linux (cargo/), so later runs are incremental. Build files there and in the mounted folders belong to the
#   calling user. Nothing else of the host is visible: no ~/.cargo, no ~/.config, no keys.
# Example, a shell in the build environment of this tree: tools/release/linux-build/run.sh --mount "$PWD" -- bash
set -euo pipefail
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$here/../../.." && pwd)
source "$here/paths.sh"
mounts=() envs=() nice=0
while (($#)); do
  case $1 in
    --mount|--ro)
      (($# >= 2)) || { echo "run.sh: $1 needs a directory" >&2; exit 2; }
      safe_dir "$2"
      source_path=$(realpath -e -- "$2")
      [[ -d $source_path ]] || { echo "run.sh: not a directory: $2" >&2; exit 2; }
      target_path=$(realpath -ms -- "$2")
      mode=rw; [[ $1 == --ro ]] && mode=ro
      mounts+=(-v "$source_path:$target_path:$mode")
      shift 2 ;;
    --env)
      (($# >= 2)) && [[ $2 =~ ^[a-zA-Z_][a-zA-Z_0-9]*$ ]] || { echo "run.sh: --env needs a variable name" >&2; exit 2; }
      case $2 in TAURI_SIGNING_*|*PASSWORD*|*SECRET*|*TOKEN*|*PRIVATE_KEY*) echo "run.sh: refusing a secret environment variable" >&2; exit 2 ;; esac
      [[ -n ${!2+x} ]] && envs+=(-e "$2"); shift 2 ;;
    --nice) nice=1; shift ;;
    --) shift; break ;;
    -h|--help) sed -n '3,/^set -euo/p' "$0" | sed '$d; s/^# \{0,1\}//'; exit 0 ;;
    -*) echo "run.sh: unknown option $1" >&2; exit 2 ;;
    *) break ;;
  esac
done
(($#)) || { echo "usage: $0 [--mount DIR]... [--ro DIR]... [--env NAME]... [--nice] [--] COMMAND [ARG]..." >&2; exit 2; }
# the unresolved path (no realpath): when it is a link to another disk, paths inside stay under the repository
glibc=$(realpath -ms "${KILN_GLIBC_DIR:-$repo/.out/glibc}")
safe_dir "$glibc"
(( $(id -u) != 0 )) || { echo "run.sh: run as the release owner, not root" >&2; exit 1; }
mkdir -p "$glibc"/{home,cargo-home,cargo}
if [[ -n ${KILN_LINUX_IMAGE:-} ]]; then
  image=$KILN_LINUX_IMAGE
  [[ $image =~ ^sha256:[0-9a-f]{64}$ ]] || { echo "run.sh: KILN_LINUX_IMAGE must be an image ID" >&2; exit 2; }
else
  tag=$("$here/image.sh")
  image=$(docker image inspect --format '{{.Id}}' "$tag")
fi
cmd=("$@")
((nice)) && cmd=(nice -n 19 ionice -c 3 "${cmd[@]}")
scratch=$(mktemp -d -t kiln-linux-build.XXXXXXXX)
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
# --init: signals (Ctrl-C) reach the build; /tmp is a tmpfs that allows exec (linuxdeploy extracts and runs itself there)
docker run --rm --init --platform linux/amd64 --name "$name" \
  --user "$(id -u):$(id -g)" --cap-drop ALL --security-opt no-new-privileges \
  --tmpfs /tmp:rw,exec,nosuid,size=8g \
  -v "$glibc:$glibc" "${mounts[@]}" \
  -e HOME="$glibc/home" -e USER="${USER:-kiln}" -e CARGO_HOME="$glibc/cargo-home" -e TERM=dumb \
  "${envs[@]}" -w "$PWD" "$image" "${cmd[@]}" &
client=$!
wait "$client"
client=""
