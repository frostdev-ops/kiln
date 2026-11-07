#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Starts a release's Linux files on a clean, older distro in a throwaway container: proof that they run there.
#   tools/release/linux-build/start-test.sh RELEASE_DIR [--distro ubuntu22|arch2024] [--out DIR] [--wait S]
# ubuntu22: ubuntu:22.04 (glibc 2.35, the floor; the same digest as the build image's base).
# arch2024: archlinux:base-20240101 (glibc 2.38, packages from the Arch Linux Archive of that day): an Arch system
#   with /usr/lib like SteamOS, older than the build machine.
# Rendering is Mesa's llvmpipe, named (GALLIUM_DRIVER): with no GPU, the Mesa of 2024-01 tries zink first, which
#   fails, and then both Xvfb and GL clients crash. A real machine (the Deck: radeonsi) never takes that path.
# Steps, all in the container: kilnctl and kiln --version on the bare image; the AppImage unpacked with
#   --appimage-extract; the libraries it expects from the system (ldd), bare; a minimal desktop installed (X server
#   Xvfb, Mesa, fontconfig + a font, dbus: what any desktop has and AppImages leave out); none missing then; the
#   launcher's --selftest as a normal user; the launcher itself on the Xvfb display (software rendering) until its
#   window is there, and a screenshot of the screen after --wait seconds (default 25).
# Output in --out (default ./start-test-<distro>): run.log, selftest.json, windows.txt (the X window tree),
#   launcher.log, screen.png (screen.xwd without ImageMagick). Exits 1 when a step failed.
set -euo pipefail
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
rel="" distro=ubuntu22 out="" wait=25
while (($#)); do
  case $1 in
    --distro) distro=$2; shift 2 ;;
    --out) out=$2; shift 2 ;;
    --wait) wait=$2; shift 2 ;;
    -h|--help) sed -n '3,16p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*) echo "unknown option $1" >&2; exit 2 ;;
    *) rel=$1; shift ;;
  esac
done
[[ -d $rel/linux ]] || { echo "usage: $0 RELEASE_DIR [--distro ubuntu22|arch2024] [--out DIR] [--wait S]" >&2; exit 2; }
rel=$(realpath "$rel")
out=$(realpath -m "${out:-start-test-$distro}")
case $distro in
  ubuntu22) image=$(sed -n 's/^FROM \(ubuntu:22.04@sha256:[0-9a-f]*\).*/\1/p' "$here/Dockerfile") ;;
  arch2024) image=archlinux:base-20240101.0.204074 ;;
  *) echo "--distro ubuntu22|arch2024" >&2; exit 2 ;;
esac
rm -rf "$out" && mkdir -p "$out"
chmod 777 "$out"  # the container's normal user writes results here too; root hands them back to you at the end
name=kiln-start-test-$$
trap 'docker rm -f "$name" >/dev/null 2>&1 || true' EXIT
set +e
docker run --rm --name "$name" -v "$rel:/r:ro" -v "$out:/out" -e DISTRO="$distro" -e WAIT="$wait" -e OWNER="$(id -u):$(id -g)" \
  --tmpfs /tmp:rw,exec,size=4g "$image" bash -c "$(cat <<'IN'
set -u
fail=0
step() { echo; echo "== $*"; }
bad() { echo "FAIL: $*"; fail=1; }
# everything runs as a normal user (the AppImage is unpacked by that user, as a player would); root only installs
useradd -m player >/dev/null 2>&1
mkdir -p /tmp/xdg /tmp/p && chown player /tmp/xdg /tmp/p && chmod 700 /tmp/xdg
as() { runuser -u player -- env -i PATH=/usr/bin:/bin HOME=/home/player TMPDIR=/tmp XDG_RUNTIME_DIR=/tmp/xdg \
  KILN_DATA_DIR=/tmp/kiln-data KILN_STEAM_ROOTS=/tmp/no-steam "$@"; }
cd /tmp/p
step "system: $DISTRO, $(ldd --version 2>&1 | head -1)"
step "bare image: kilnctl and kiln --version"
as /r/runtime/kilnctl --version 2>&1 | tee /tmp/v.txt; grep -q "not found" /tmp/v.txt && bad "kilnctl"
as /r/sdk/kiln --version 2>&1 | tee /tmp/v.txt; grep -q "not found" /tmp/v.txt && bad "kiln"
step "unpack the AppImage (--appimage-extract)"
as sh -c 'cp /r/linux/*.AppImage k.AppImage && chmod 755 k.AppImage && ./k.AppImage --appimage-extract >/dev/null' || bad "--appimage-extract"
root=/tmp/p/squashfs-root
elfs=$(find "$root/usr" -type f \( -perm -u+x -o -name '*.so*' \) 2>/dev/null | while read -r f; do [[ $(head -c4 "$f") == $'\x7fELF' ]] && echo "$f"; done)
echo "$(echo "$elfs" | wc -l) ELF files"
lddall() { for f in $elfs; do LD_LIBRARY_PATH=$root/usr/lib ldd "$f" 2>&1; done; }
missing() { lddall | awk '/=> not found/ {print $1}' | sort -u; }
step "glibc symbol versions this system lacks (ldd, every ELF file of the AppImage)"
v=$(lddall | grep -o "version .GLIBC_[A-Z0-9_.]*. not found" | sort | uniq -c)
echo "${v:-none}"; [[ -z $v ]] || bad "glibc too old for the AppImage"
step "libraries the AppImage expects from the system, on the bare image"
missing | tr '\n' ' '; echo
step "install a minimal desktop (X server, Mesa, fontconfig, a font, dbus) and the probe tools"
case $DISTRO in
  ubuntu22)
    export DEBIAN_FRONTEND=noninteractive
    { apt-get update && apt-get install -y --no-install-recommends xvfb x11-utils dbus libgl1 libegl1 libgles2 libgbm1 \
        libgl1-mesa-dri libx11-xcb1 libdrm2 libwayland-client0 fontconfig fonts-dejavu-core libfreetype6 libharfbuzz0b \
        libfribidi0 libexpat1; } >/tmp/install.log 2>&1 || { tail -20 /tmp/install.log; bad "apt-get"; } ;;
  arch2024)
    echo 'Server = https://archive.archlinux.org/repos/2024/01/01/$repo/os/$arch' >/etc/pacman.d/mirrorlist
    # a throwaway test container: the archive's packages of that day, without today's keyring checks
    sed -i 's/^SigLevel.*/SigLevel = Never/' /etc/pacman.conf
    pacman -Sy --noconfirm --needed xorg-server-xvfb xorg-xwininfo dbus mesa fontconfig ttf-dejavu libx11 libxcb libdrm wayland \
      freetype2 harfbuzz fribidi expat \
      >/tmp/install.log 2>&1 || { tail -20 /tmp/install.log; bad "pacman"; } ;;
esac
step "libraries still missing (must be none)"
m=$(missing | tr '\n' ' '); echo "${m:-none}"; [[ -z $m ]] || bad "libraries missing: $m"
step "kiln-launcher --selftest (as a normal user)"
as $root/AppRun --selftest --out /out/selftest.json >/tmp/selftest.out 2>&1; code=$?
[[ -f /out/selftest.json ]] && grep -E '^  "(passed|failed)":' /out/selftest.json | tr -d ' \n'; echo
echo "exit $code"; ((code == 0)) || { tail -20 /tmp/selftest.out; bad "--selftest exit $code"; }
step "the launcher on a virtual display (Xvfb, software rendering)"
export GALLIUM_DRIVER=llvmpipe
mkdir -p /tmp/fb
Xvfb :99 -screen 0 1600x1000x24 -fbdir /tmp/fb -nolisten tcp >/tmp/xvfb.log 2>&1 &
xvfb=$!
sleep 2
as dbus-run-session -- env DISPLAY=:99 GALLIUM_DRIVER=llvmpipe RUST_BACKTRACE=1 $root/AppRun >/out/launcher.log 2>&1 &
app=$!
win=""
for i in $(seq 1 60); do
  win=$(DISPLAY=:99 xwininfo -root -tree 2>/dev/null | grep -E '"Kiln"' | head -1)
  [[ -n $win ]] && break
  kill -0 $app 2>/dev/null || break
  sleep 1
done
if [[ -n $win ]]; then echo "window after ${i}s: $win"; else bad "no Kiln window (the launcher exited or did not map one)"; fi
kill -0 $app 2>/dev/null && sleep "$WAIT"
if kill -0 $app 2>/dev/null; then echo "still running after ${WAIT}s more"; else wait $app; bad "the launcher exited early (exit $?; launcher.log)"; fi
DISPLAY=:99 xwininfo -root -tree >/out/windows.txt 2>&1
cp /tmp/fb/Xvfb_screen0 /out/screen.xwd 2>/dev/null || bad "no screen to save (see xvfb.log)"
cp /tmp/xvfb.log /out/xvfb.log
kill $app $xvfb >/dev/null 2>&1  # the rest ends with the container
chown -R "$OWNER" /out
echo; ((fail)) && echo "RESULT: FAILED" || echo "RESULT: OK"
exit $fail
IN
)" 2>&1 | tee "$out/run.log"
code=${PIPESTATUS[0]}
set -e
if [[ -f $out/screen.xwd ]] && command -v magick >/dev/null; then
  magick "xwd:$out/screen.xwd" "$out/screen.png" && rm -f "$out/screen.xwd"
fi
echo "start-test: $distro: results in $out"
exit "$code"
