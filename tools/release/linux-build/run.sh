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
mounts=() envs=() nice=0
while (($#)); do
  case $1 in
    --mount) mounts+=(-v "$(realpath -e "$2"):$(realpath -e "$2")"); shift 2 ;;
    --ro) mounts+=(-v "$(realpath -e "$2"):$(realpath -e "$2"):ro"); shift 2 ;;
    --env) [[ -n ${!2+x} ]] && envs+=(-e "$2"); shift 2 ;;
    --nice) nice=1; shift ;;
    --) shift; break ;;
    -h|--help) sed -n '3,14p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*) echo "run.sh: unknown option $1" >&2; exit 2 ;;
    *) break ;;
  esac
done
(($#)) || { echo "usage: $0 [--mount DIR]... [--ro DIR]... [--env NAME]... [--nice] [--] COMMAND [ARG]..." >&2; exit 2; }
# the unresolved path (no realpath): when it is a link to another disk, paths inside stay under the repository
glibc=$(realpath -ms "${KILN_GLIBC_DIR:-$repo/.out/glibc}")
mkdir -p "$glibc"/{home,cargo-home,cargo}
image=$("$here/image.sh")
cmd=("$@")
((nice)) && cmd=(nice -n 19 ionice -c 3 "${cmd[@]}")
# --init: signals (Ctrl-C) reach the build; /tmp is a tmpfs that allows exec (linuxdeploy extracts and runs itself there)
exec docker run --rm --init --name "kiln-linux-build-$$" \
  --user "$(id -u):$(id -g)" --cap-drop ALL --security-opt no-new-privileges \
  --tmpfs /tmp:rw,exec,nosuid,size=8g \
  -v "$glibc:$glibc" "${mounts[@]}" \
  -e HOME="$glibc/home" -e USER="${USER:-kiln}" -e CARGO_HOME="$glibc/cargo-home" -e TERM=dumb \
  "${envs[@]}" -w "$PWD" "$image" "${cmd[@]}"
