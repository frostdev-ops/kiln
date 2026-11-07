#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Internal half of start-test.sh. Root installs packages; player (the host UID/GID) owns every result file.
set -euo pipefail
export LC_ALL=C
app="" xvfb=""
cleanup() {
  [[ -z $app ]] || { kill "$app" 2>/dev/null || true; wait "$app" 2>/dev/null || true; }
  [[ -z $xvfb ]] || { kill "$xvfb" 2>/dev/null || true; wait "$xvfb" 2>/dev/null || true; }
}
trap cleanup EXIT
step() { printf '\n== %s\n' "$*"; }
bad() { echo "RESULT: FAILED: $*" >&2; exit 1; }
getent group "$OWNER_GID" >/dev/null || groupadd -g "$OWNER_GID" player
useradd -m -o -u "$OWNER_UID" -g "$OWNER_GID" player
install -d -o "$OWNER_UID" -g "$OWNER_GID" -m 700 /tmp/xdg /tmp/p
as() { runuser -u player -- env -i PATH=/usr/bin:/bin HOME=/home/player TMPDIR=/tmp XDG_RUNTIME_DIR=/tmp/xdg \
  KILN_DATA_DIR=/tmp/kiln-data KILN_STEAM_ROOTS=/tmp/no-steam "$@"; }
cd /tmp/p
step "bare image: kilnctl and kiln --version"
for name in kilnctl kiln; do
  file=/r/runtime/kilnctl; [[ $name == kilnctl ]] || file=/r/sdk/kiln
  v=$(as "$file" --version) || bad "$name --version failed"
  echo "$v"
  [[ $v == "$name "* ]] || bad "unexpected $name --version output"
done
step "unpack exactly one AppImage (--appimage-extract), as the player"
shopt -s nullglob
image_dir=/r/linux
[[ $DISTRIBUTION == direct ]] || image_dir=/r/nexus/linux
images=("$image_dir/"*.AppImage)
((${#images[@]} == 1)) || bad "expected exactly one AppImage"
as cp "${images[0]}" k.AppImage
as chmod 755 k.AppImage
as ./k.AppImage --appimage-extract >/dev/null
root=/tmp/p/squashfs-root
[[ -x $root/AppRun ]] || bad "no AppRun after extraction"
# Use a NUL-delimited array, and inspect all regular files rather than a filename/execute-bit heuristic.
elfs=()
while IFS= read -r -d '' f; do
  if [[ $(od -An -tx1 -N4 "$f") == ' 7f 45 4c 46' ]]; then elfs+=("$f"); fi
done < <(find "$root" -type f -print0)
((${#elfs[@]})) || bad "no ELF files in the AppImage"
echo "${#elfs[@]} ELF files"
lddall() {
  local f output
  for f in "${elfs[@]}"; do
    if output=$(as env LD_LIBRARY_PATH="$root/usr/lib" ldd "$f" 2>&1); then
      printf '%s\n' "$output"
    else
      printf '%s\n' "$output"
      # A static runtime and unresolved libraries on the bare image are expected diagnostics.
      if ! grep -E 'not a dynamic executable|statically linked|=> not found|version .* not found' <<<"$output" >/dev/null; then
        echo "unexpected ldd failure: $f"
        return 1
      fi
    fi
  done
}
step "glibc diagnostics on the bare image"
lddall >/tmp/ldd.txt || { cat /tmp/ldd.txt; bad "ldd"; }
if grep -E 'version .GLIBC_[A-Z0-9_.]*. not found' /tmp/ldd.txt; then bad "glibc too old"; fi
step "libraries expected from the system on the bare image"
awk '/=> not found/ {print $1}' /tmp/ldd.txt | sort -u
step "install a minimal desktop and probe tools"
case $DISTRO in
  ubuntu22)
    export DEBIAN_FRONTEND=noninteractive
    if ! { apt-get update && apt-get install -y --no-install-recommends xvfb x11-utils dbus python3 libgl1 libegl1 libgles2 libgbm1 \
        libgl1-mesa-dri libx11-xcb1 libdrm2 libwayland-client0 fontconfig fonts-dejavu-core libfreetype6 libharfbuzz0b \
        libfribidi0 libexpat1; } >/tmp/install.log 2>&1; then tail -20 /tmp/install.log; bad "apt-get"; fi ;;
  arch2024)
    echo 'Server = https://archive.archlinux.org/repos/2024/01/01/$repo/os/$arch' >/etc/pacman.d/mirrorlist
    # The archive keyring is from that day; signatures remain enabled.
    if ! pacman -Sy --noconfirm --needed xorg-server-xvfb xorg-xwininfo dbus python mesa fontconfig ttf-dejavu libx11 libxcb libdrm wayland \
      freetype2 harfbuzz fribidi expat >/tmp/install.log 2>&1; then tail -20 /tmp/install.log; bad "pacman"; fi ;;
  *) bad "unknown distro" ;;
esac
step "no unresolved libraries after installing the desktop"
lddall >/tmp/ldd.txt || { cat /tmp/ldd.txt; bad "ldd"; }
if grep -E '=> not found|version .* not found' /tmp/ldd.txt; then bad "unresolved libraries"; fi
step "launcher --selftest and its JSON report"
as "$root/AppRun" --selftest --out /out/selftest.json >/tmp/selftest.out 2>&1 || { tail -20 /tmp/selftest.out; bad "--selftest"; }
as python3 -c 'import json,sys
r=json.load(open("/out/selftest.json"))
assert r["ok"] is True and r["failed"] == 0 and r["passed"] > 0
assert r["distribution"] == sys.argv[1]
assert r["checks"] and all(c["ok"] is True for c in r["checks"])
print("selftest:", r["passed"], "passed; distribution", sys.argv[1])' "$DISTRIBUTION"
step "launcher window on Xvfb, with software rendering"
export GALLIUM_DRIVER=llvmpipe
mkdir -p /tmp/fb
Xvfb :99 -screen 0 1600x1000x24 -fbdir /tmp/fb -nolisten tcp >/tmp/xvfb.log 2>&1 &
xvfb=$!
for ((i=0; i<20; i++)); do
  if DISPLAY=:99 xwininfo -root >/dev/null 2>&1; then break; fi
  kill -0 "$xvfb" 2>/dev/null || bad "Xvfb exited"
  sleep 1
done
DISPLAY=:99 xwininfo -root >/dev/null 2>&1 || bad "Xvfb did not become ready"
as bash -c 'exec dbus-run-session -- env DISPLAY=:99 GALLIUM_DRIVER=llvmpipe RUST_BACKTRACE=1 "$1" >/out/launcher.log 2>&1' bash "$root/AppRun" &
app=$!
window() {
  local win
  win=$(DISPLAY=:99 xwininfo -root -tree | awk '/"Kiln"/ && !seen++ {print $1}') || return
  [[ -n $win ]] && DISPLAY=:99 xwininfo -id "$win" | grep 'Map State: IsViewable' >/dev/null
}
for ((i=0; i<60; i++)); do
  if window; then break; fi
  kill -0 "$app" 2>/dev/null || bad "launcher exited before mapping a window"
  sleep 1
done
window || bad "no mapped Kiln window"
sleep "$WAIT"
# dbus-run-session/runuser may survive a failed child. Require the mapped window again after the wait as well.
kill -0 "$app" 2>/dev/null || bad "launcher exited early"
window || bad "Kiln window disappeared"
as bash -c 'DISPLAY=:99 xwininfo -root -tree >/out/windows.txt'
as cp /tmp/fb/Xvfb_screen0 /out/screen.xwd
as cp /tmp/xvfb.log /out/xvfb.log
[[ -s /out/screen.xwd ]] || bad "no screen to save"
echo "RESULT: OK"
