#!/usr/bin/env bash
# Cross-builds the Kiln Launcher for Windows from Linux: NSIS installer via cargo-xwin.
#   tools/launcher/build-windows.sh [--debug] [--selftest] [--webview bootstrapper|skip] [--runtime ZIP|none] [--version V]
#                                   [--python DIR|none] [--config JSON|FILE]... [--nexus]
# --nexus: the Nexus Mods build: no self-update at all (cargo --no-default-features drops the self-update feature and
#   tauri-plugin-updater; src-tauri/tauri.nexus.conf.json drops the updater key from the config). Output in
#   bundles/windows-nexus/. docs/kiln/launcher/releasing.md, "The two builds".
# --version: the launcher's version (a release: tools/release/version.sh). --config: more Tauri config to merge, in order
#   (a release passes bundle.windows.signCommand when it signs with Authenticode).
# --runtime: the Kiln runtime to ship (shipped-runtime.sh). Default: built from this tree; --selftest ships none.
# --python: the Python to ship as python/ for the cook tools (kiln_manage::cooktools; a player's own Python is never
#   used). Default: the pinned embeddable package (tools/package/fetch-python.sh: cached, else downloaded and checked
#   by sha256). DIR: an unpacked one. none: no Python (--selftest's default; a release build refuses it): cooking in
#   that launcher then needs KILN_PYTHON.
# Output: $KILN_OUT/bundles/windows/ (kiln-launcher-setup.exe, kiln-launcher.exe).
# --webview: what the installer does about the WebView2 runtime. bootstrapper (default for releases) downloads
#   Microsoft's WebView2 bootstrapper at install time when the runtime is missing; skip installs nothing (Windows 11
#   and current Windows 10 ship WebView2). --selftest builds with skip, so the Wine run downloads nothing.
# Needs: cargo-xwin (MSVC CRT/SDK cache in ~/.cache/cargo-xwin), clang/lld, makensis + Tauri's NSIS bundle
# (~/.cache/tauri/NSIS; Tauri downloads it on first use).
# --selftest: afterwards installs the setup into a scratch Wine prefix ($KILN_OUT/wine-pfx) on a private Xvfb display
#   (nothing appears on the desktop), runs `kiln-launcher.exe --selftest --out` there and prints the result. The
#   WebView2 UI does not run under Wine; the selftest covers the Rust side.
set -euo pipefail
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$here/../.." && pwd)
export KILN_OUT=${KILN_OUT:-$repo/.out/launcher}
export CARGO_TARGET_DIR=${CARGO_TARGET_DIR:-$KILN_OUT/cargo}
target=x86_64-pc-windows-msvc
profile=()
selftest=0
webview=
runtime=
python=
extra=()
nexus=0
source "$here/shipped-runtime.sh"
while (($#)); do
  case $1 in
    --debug) profile=(--debug); shift ;;
    --version) extra+=(--config "{\"version\":\"$2\"}"); shift 2 ;;
    --config) extra+=(--config "$2"); shift 2 ;;
    --selftest) selftest=1; shift ;;
    --webview) webview=$2; shift 2 ;;
    --runtime) runtime=$2; shift 2 ;;
    --python) python=$2; shift 2 ;;
    --nexus) nexus=1; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
((selftest)) && runtime=${runtime:-none} python=${python:-none}
[[ $python == none && ${KILN_RELEASE_BUILD:-} == 1 ]] && { echo "--python none: a release build ships the pinned Python" >&2; exit 2; }
# the Nexus Mods build: no self-update feature and no updater key in the config (its config goes last)
runner_args=()
variant=windows
if ((nexus)); then
  extra+=(--config "$repo/launcher/src-tauri/tauri.nexus.conf.json")
  runner_args=(-- --no-default-features)
  variant=windows-nexus
fi
resources=$(shipped_runtime_resources "$runtime")
resources=$(shipped_python_resources "$python" "$resources")
cd "$repo/launcher"
[[ -d node_modules ]] || bun install --frozen-lockfile
[[ -z $webview ]] && { ((selftest)) && webview=skip || webview=bootstrapper; }
case $webview in
  skip) config='{"bundle":{"resources":'"$resources"',"windows":{"webviewInstallMode":{"type":"skip"}}}}' ;;
  bootstrapper) config='{"bundle":{"resources":'"$resources"',"windows":{"webviewInstallMode":{"type":"downloadBootstrapper","silent":true}}}}' ;;
  *) echo "--webview bootstrapper|skip" >&2; exit 2 ;;
esac
echo "WebView2 install mode: $webview"
kind=release; ((${#profile[@]})) && kind=debug
rm -rf "$CARGO_TARGET_DIR/$target/$kind/bundle"
bun x tauri build "${profile[@]}" --runner cargo-xwin --target "$target" --bundles nsis --config "$config" "${extra[@]}" "${runner_args[@]}"
out=$KILN_OUT/bundles/$variant
mkdir -p "$out"
rm -f "$out/"*.sig
setup=$(ls -t "$CARGO_TARGET_DIR/$target/$kind/bundle/nsis/"*-setup.exe | head -1)
cp -f "$setup" "$out/kiln-launcher-setup.exe"
cp -f "$CARGO_TARGET_DIR/$target/$kind/kiln-launcher.exe" "$out/kiln-launcher.exe"
echo "-> $out/kiln-launcher-setup.exe ($(du -h "$out/kiln-launcher-setup.exe" | cut -f1)), $out/kiln-launcher.exe"

if ((selftest)); then
  # a private X display for Wine (prefix setup dialogs never reach the desktop)
  if [[ -z ${KILN_WINE_NO_XVFB:-} ]] && command -v Xvfb >/dev/null; then
    n=92; while [[ -e /tmp/.X11-unix/X$n || -e /tmp/.X$n-lock ]]; do n=$((n + 1)); done
    disp=:$n
    Xvfb "$disp" -screen 0 1024x768x24 -nolisten tcp >/dev/null 2>&1 &
    xvfb=$!
    trap 'kill $xvfb 2>/dev/null' EXIT
    export DISPLAY=$disp
    unset WAYLAND_DISPLAY
  fi
  export WINEPREFIX=$KILN_OUT/wine-pfx WINEDEBUG=-all WINEDLLOVERRIDES="mscoree,mshtml=;winemenubuilder.exe=d"
  mkdir -p "$WINEPREFIX"
  wine "$out/kiln-launcher-setup.exe" /S
  wineserver -w
  exe=$(find "$WINEPREFIX/drive_c" -iname kiln-launcher.exe -path '*Kiln*' | head -1)
  [[ -n $exe ]] || { echo "installed kiln-launcher.exe not found in $WINEPREFIX" >&2; exit 1; }
  echo "installed: $exe"
  report=$KILN_OUT/selftest-windows.json
  rm -f "$report"
  set +e
  wine "$exe" --selftest --out "$(winepath -w "$report")"
  code=$?
  set -e
  wineserver -w
  [[ -f $report ]] && python3 -c 'import json,sys; r=json.load(open(sys.argv[1])); print("\n".join(("PASS " if c["ok"] else "FAIL ")+c["name"]+": "+c["detail"] for c in r["checks"])); print("ok" if r["ok"] else "FAILED", r["platform"], r["arch"])' "$report"
  echo "selftest exit $code, report $report"
  exit $code
fi
