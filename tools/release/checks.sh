# Sourced by release.sh: the checks a release folder must pass. Each check's output goes to the release log; the
# summary is PASS/FAIL per check. Everything runs in $work/checks with its own HOME, data dir, Wine prefix, NuGet
# cache and dbus session, on a private X display: nothing reaches the desktop or the user's own setup.
# The Nexus Mods build (nexus/) gets the same --selftest runs as the direct one (in their own folders and Wine prefix),
# and both must say which distribution they are: direct (updates itself) or nexus (no self-update at all).

ck=""
ck_display() { # starts Xvfb on a free display; prints the display number
  local n=94
  while [[ -e /tmp/.X11-unix/X$n || -e /tmp/.X$n-lock ]]; do n=$((n + 1)); done
  Xvfb ":$n" -screen 0 1280x800x24 -nolisten tcp >/dev/null 2>&1 &
  echo "$n $!"
}

# A --selftest report must match the distribution and Nexus capability/registration requested by this release.
ck_report() { # report.json distribution
  python3 -c 'import json,os,re,sys
flag = os.environ.get("KILN_NEXUS_ENABLED")
if flag not in (None, "1", "0"):
    sys.exit("KILN_NEXUS_ENABLED must be 0 or 1.")
enabled = flag != "0"
client_id = os.environ.get("KILN_NEXUS_OAUTH_CLIENT_ID") if enabled else None
redirect_uri = os.environ.get("KILN_NEXUS_OAUTH_REDIRECT_URI") if enabled else None
if enabled:
    # Keep the public registration shape in sync with nexus/oauth/registration.rs.
    callback = re.fullmatch(r"http://127\.0\.0\.1:([1-9][0-9]{0,4})/([A-Za-z0-9_./-]{1,128})", redirect_uri or "")
    if not (re.fullmatch(r"[A-Za-z0-9_.-]{1,128}", client_id or "") and callback
            and int(callback[1]) <= 65535 and all(p not in ("", ".", "..") for p in callback[2].split("/"))):
        sys.exit("Enabled release requires valid expected Nexus OAuth public registration.")
r = json.load(open(sys.argv[1]))
print("\n".join(("PASS " if c["ok"] else "FAIL ") + c["name"] + ": " + c["detail"] for c in r["checks"]))
print("distribution:", r.get("distribution"), "(expected " + sys.argv[2] + ")")
oauth = r.get("nexusOAuth")
if not isinstance(oauth, dict):
    oauth = {}
print("Nexus enabled:", oauth.get("enabled"), "(expected", enabled, ") configured:", oauth.get("configured") is True)
valid_oauth = ({"enabled", "configured", "clientId", "redirectUri"} <= oauth.keys()
    and oauth["enabled"] is enabled and oauth["configured"] is enabled
    and oauth["clientId"] == client_id and oauth["redirectUri"] == redirect_uri)
if not valid_oauth:
    print("Nexus capability/registration does not match the release environment.", file=sys.stderr)
marker = "KILN-LAUNCHER-DISTRIBUTION:" + sys.argv[2] + ":self-update=" + ("1" if sys.argv[2] == "direct" else "0")
sys.exit(0 if r["ok"] is True and all(c["ok"] is True for c in r["checks"])
    and sys.argv[2] in ("direct", "nexus") and r.get("distribution") == sys.argv[2] and r.get("buildMarker") == marker
    and r.get("releaseBuild") is True and valid_oauth else 1)' "$1" "$2"
}

check_appimage() { # [AppImage folder-name distribution]: default the direct build
  local app=${1:-$out/linux/$A} name=${2:-appimage} dist=${3:-direct} n xvfb code
  read -r n xvfb < <(ck_display)
  sleep 1
  mkdir -p "$ck/$name"/{home,data,tmp}
  set +e
  env -i PATH=/usr/bin:/bin HOME="$ck/$name/home" TMPDIR="$ck/$name/tmp" XDG_RUNTIME_DIR="$ck/$name/tmp" DISPLAY=":$n" \
    KILN_DATA_DIR="$ck/$name/data" KILN_STEAM_ROOTS="$ck/$name/steam" \
    dbus-run-session -- "$app" --selftest --out "$ck/$name/selftest.json"
  code=$?
  set -e
  kill "$xvfb" 2>/dev/null || true
  ck_report "$ck/$name/selftest.json" "$dist" && ((code == 0))
}

ck_pfx=wine-pfx
ck_wine() { # wine in the check prefix ($ck/$ck_pfx), on the private display $ck_n
  env WINEPREFIX="$ck/$ck_pfx" WINEDEBUG=-all WINEDLLOVERRIDES="mscoree,mshtml=;winemenubuilder.exe=d" DISPLAY=":$ck_n" \
    HOME="$ck/wine-home" wine "$@"
}

check_nsis() { # [setup prefix-name distribution]: default the direct build in $ck/wine-pfx
  local setup_exe=${1:-$out/windows/$S} dist=${3:-direct} xvfb exe report code
  ck_pfx=${2:-wine-pfx}
  read -r ck_n xvfb < <(ck_display)
  sleep 1
  mkdir -p "$ck/$ck_pfx" "$ck/wine-home"
  ck_wine wineboot -i >/dev/null 2>&1
  # tell the installer WebView2 is there, so it downloads nothing (the selftest does not use it)
  ck_wine reg add 'HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}' \
    /v pv /t REG_SZ /d 120.0.0.0 /f >/dev/null 2>&1
  ck_wine "$setup_exe" /S
  env WINEPREFIX="$ck/$ck_pfx" wineserver -w
  exe=$(find "$ck/$ck_pfx/drive_c" -iname kiln-launcher.exe -path '*Kiln*' | head -1)
  [[ -n $exe ]] || { echo "the setup installed no kiln-launcher.exe"; kill "$xvfb"; ck_pfx=wine-pfx; return 1; }
  echo "installed: ${exe#"$ck"/}"
  report=$ck/selftest-windows-$dist.json
  set +e
  ck_wine "$exe" --selftest --out "$(env WINEPREFIX="$ck/$ck_pfx" winepath -w "$report")"
  code=$?
  set -e
  env WINEPREFIX="$ck/$ck_pfx" wineserver -w
  kill "$xvfb" 2>/dev/null || true
  ck_pfx=wine-pfx
  ck_report "$report" "$dist" && ((code == 0))
}

# The Nexus Mods build has none of the self-update: no update feed, no updater key, no updater plugin in its AppImage
# (unpacked), its deb, the exe inside its setup and the exe the setup installed under Wine; no .sig in nexus/. The
# direct build has all of it (the same scan, the other way round).
check_nexus_updater() {
  local files=("$out/nexus/linux/$A" "$out/nexus/linux/$D") installed
  [[ -n $nexus_setup ]] && files+=("$out/nexus/windows/$S")
  installed=$(find "$ck/wine-pfx-nexus/drive_c" -iname kiln-launcher.exe -path '*Kiln*' 2>/dev/null | head -1)
  [[ -n $installed ]] && files+=("$installed")
  [[ -z $nexus_setup || -n $installed ]] || { echo "no installed Nexus kiln-launcher.exe (the Wine install check failed)"; return 1; }
  python3 "$lib/release.py" updater --absent --pubkey "$ck/embedded.pub" --no-sig "$out/nexus" --tmp "$ck" "${files[@]}"
}

check_direct_updater() {
  local files=("$out/linux/$A" "$out/linux/$D")
  [[ -n $setup ]] && files+=("$out/windows/$S")
  python3 "$lib/release.py" updater --present --pubkey "$ck/embedded.pub" --tmp "$ck" "${files[@]}"
}

check_wine_cli() {
  local xvfb v1 v2
  read -r ck_n xvfb < <(ck_display)
  mkdir -p "$ck/wine-pfx" "$ck/wine-home"
  v1=$(ck_wine "$out/runtime/kilnctl.exe" --version | tr -d '\r')
  v2=$(ck_wine "$out/sdk/kiln.exe" --version | tr -d '\r')
  env WINEPREFIX="$ck/wine-pfx" wineserver -w
  kill "$xvfb" 2>/dev/null || true
  echo "kilnctl.exe: $v1; kiln.exe: $v2"
  [[ $v1 == kilnctl* && $v2 == kiln* ]]
}

check_rpm() {
  python3 -I - "$lib/release.py" "$out/linux/$R" "$ck" <<'PY'
import importlib.util, os, sys, tempfile
spec = importlib.util.spec_from_file_location("release", sys.argv[1])
r = importlib.util.module_from_spec(spec)
spec.loader.exec_module(r)
with tempfile.TemporaryDirectory(dir=sys.argv[3]) as d:
    r.extract("rpm", sys.argv[2], d)
    if not any(os.path.isfile(os.path.join(base, "kiln-launcher")) and base.endswith("/usr/bin")
               for base, _, _ in os.walk(d)):
        sys.exit("rpm has no usr/bin/kiln-launcher")
PY
}

# the server pack: only the components kilnctl deploy --bundle installs on a server, every file the runtime zip's bytes
# at its manifest path, safe names, sorted, one time, and a second build byte-identical; the systemd units beside it
check_server_pack() {
  python3 "$lib/serverpack.py" check "$out/runtime/kiln-server-$version.zip" --runtime "$out/runtime/kiln-runtime-$version.zip" \
    --readme "$here/templates/KILN-SERVER-README.txt" --version "$version" --epoch "$SOURCE_DATE_EPOCH" || return
  local u
  for u in kiln@.service kiln-backup@.service kiln-backup@.timer; do
    cmp "$src/runner/systemd/$u" "$out/runtime/$u" || { echo "runtime/$u missing or not the source's"; return 1; }
  done
  echo "systemd units: OK"
}

check_cli_versions() {
  local a b
  a=$("$out/runtime/kilnctl" --version) || return
  b=$("$out/sdk/kiln" --version) || return
  echo "kilnctl: $a; kiln: $b"
  [[ $a == 'kilnctl '* && $b == 'kiln '* ]]
}

check_start_ubuntu() { # the Linux files on a clean Ubuntu 22.04 (start-test.sh: a throwaway container, needs network)
  local dist=${1:-direct} suffix=""
  [[ $dist == direct ]] || suffix=-nexus
  "$here/linux-build/start-test.sh" "$out" --distro ubuntu22 --distribution "$dist" --out "$ck/start-ubuntu22$suffix" --wait 5
}

# The tripwire tests of a build with Nexus off return at once in a build with Nexus on, so a plain cargo test checks
# nothing of them. A release built with KILN_NEXUS_ENABLED=0 runs them compiled that way (own target folders under
# the release's cargo folder). A release with Nexus on has no disabled build to test.
check_disabled_nexus_tests() {
  if [[ ${KILN_NEXUS_ENABLED:-1} != 0 ]]; then
    echo "Nexus is on in this release: the disabled-build tests do not apply"
    return 0
  fi
  (cd "$src" && linux "$src/tools/test-nexus-disabled.sh")
}

check_template() {
  local d=$ck/template
  rm -rf "$d" && mkdir -p "$d"
  # a scratch package cache: the user's ~/.nuget/packages never sees these test packages
  export NUGET_PACKAGES=$d/packages DOTNET_CLI_HOME=$d/home
  dotnet new install "$out/sdk/Kiln.Templates.$sdk_version.nupkg" --debug:custom-hive "$d/hive"
  (cd "$d" && dotnet new kiln-mod -n CheckMod --feed "$out/sdk" --debug:custom-hive "$d/hive")
  # the new project names this release's Kiln.Sdk, so it restores this release's packages and no cached older ones
  grep -q "Sdk=\"Kiln.Sdk/$sdk_version\"" "$d/CheckMod/CheckMod.csproj" || { echo "CheckMod.csproj does not name Kiln.Sdk/$sdk_version"; return 1; }
  (cd "$d/CheckMod" && dotnet build -c Release -nologo -v q)
  local m; m=$(find "$d/CheckMod" -path '*/bin/*' -name kiln.mod.json | head -1)
  unset NUGET_PACKAGES DOTNET_CLI_HOME
  [[ -n $m ]] || { echo "no kiln.mod.json in the build output"; return 1; }
  echo "built: ${m#"$d"/}"; cat "$m"
}

check_vsix() {
  local v; v=$(ls "$out"/sdk/*.vsix)
  unzip -l "$v"
  unzip -l "$v" | grep -q 'extension/package.json' && unzip -l "$v" | grep -q 'extension/dist/extension.js'
}

check_authenticode() {
  local d; d=$(mktemp -d -p "$ck")
  local files=("$out/runtime/kilnctl.exe" "$out/sdk/kiln.exe")
  [[ -n $setup ]] && files+=("$out/windows/$S")
  [[ -n $nexus_setup ]] && files+=("$out/nexus/windows/$S")
  (cd "$d" && 7z x -y "$out/windows/$S" >/dev/null 2>&1) || true
  [[ -n $nexus_setup ]] && { (mkdir -p "$d/nexus" && cd "$d/nexus" && 7z x -y "$out/nexus/windows/$S" >/dev/null 2>&1) || true; }
  mapfile -t inner < <(find "$d" -iname '*.exe' -not -iname '*webview*' | sort)
  (cd "$d" && mkdir rt && cd rt && unzip -q "$out/runtime/kiln-runtime-$version.zip")
  mapfile -t rt < <(find "$d/rt" -type f \( -iname '*.dll' -o -iname '*.exe' \) -not -path '*/kiln/dotnet/*' | sort)
  "$here/authenticode.sh" verify "${files[@]}" "${inner[@]}" "${rt[@]}"
}

run_checks() {
  ck=$work/checks
  rm -rf "$ck" && mkdir -p "$ck"
  check "SHA256SUMS matches the folder" python3 "$lib/release.py" sums "$out" --check
  python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["plugins"]["updater"]["pubkey"])' \
    "$src/launcher/src-tauri/tauri.conf.json" >"$ck/embedded.pub"
  check "updater signatures verify against the launcher's built-in key, signed for $version" \
    python3 "$lib/release.py" verify --pubkey "$ck/embedded.pub" --version "$version" "${updater_files[@]}"
  check "AppImage --selftest on a private display (distribution direct)" check_appimage
  check "Nexus AppImage --selftest on a private display (distribution nexus)" check_appimage "$out/nexus/linux/$A" appimage-nexus nexus
  if [[ -n $setup ]]; then
    check "NSIS setup installs silently under Wine and passes --selftest (distribution direct)" check_nsis
    check "kilnctl.exe and kiln.exe --version under Wine" check_wine_cli
  fi
  if [[ -n $nexus_setup ]]; then
    check "Nexus NSIS setup installs silently under Wine and passes --selftest (distribution nexus)" \
      check_nsis "$out/nexus/windows/$S" wine-pfx-nexus nexus
  fi
  check "deb control and files (dpkg-deb -I/-c equivalent)" python3 "$lib/release.py" deb "$out/linux/$D" --version "$version" --need usr/bin/kiln-launcher
  check "Nexus deb control and files" python3 "$lib/release.py" deb "$out/nexus/linux/$D" --version "$version" --need usr/bin/kiln-launcher
  check "the Nexus builds hold none of the self-update (update feeds, updater key, updater plugin; no .sig): AppImage, deb, setup, installed exe" \
    check_nexus_updater
  check "the direct builds still hold their self-update (feeds, updater key, plugin)" check_direct_updater
  [[ -n $rpm ]] && check "rpm contains usr/bin/kiln-launcher" check_rpm
  check "kilnctl bundle verify on the runtime zip" "$out/runtime/kilnctl" bundle verify "$out/runtime/kiln-runtime-$version.zip"
  check "the runtime manifest lists every plugin DLL (native/plugins/*, kiln-eml, ShroudForge runtime, Enshwick loader), default on both sides" \
    python3 -I "$lib/plugins.py" check --src "$src" "$out/runtime/kiln-runtime-$version.zip"
  check "kilnctl and kiln --version (Linux)" check_cli_versions
  check "server pack: only the server components, the runtime's files, rebuilt byte-identically; systemd units" \
    check_server_pack
  # every Linux ELF (the AppImage's own files, the deb's, the rpm's, kilnctl, kiln) must run on glibc $glibc_max: the
  # Steam Deck and Ubuntu 22.04 (release.sh builds them in the Ubuntu 22.04 container; --host-build fails here)
  check "every Linux ELF needs glibc ${glibc_max:-2.35} at most (objdump -p requirements, archives unpacked)" \
    python3 "$lib/release.py" glibc "$out" --max "${glibc_max:-2.35}" --tmp "$ck"
  check "kilnctl, kiln and the AppImage start on a clean Ubuntu 22.04 (--selftest, the launcher's window; screenshot in checks/start-ubuntu22)" \
    check_start_ubuntu
  check "Nexus AppImage starts on a clean Ubuntu 22.04 (distribution nexus; screenshot in checks/start-ubuntu22-nexus)" \
    check_start_ubuntu nexus
  check "the disabled-Nexus tripwire tests run with KILN_NEXUS_ENABLED=0 and pass (skipped when the release has Nexus on)" \
    check_disabled_nexus_tests
  check "dotnet new kiln-mod from the packed templates builds against the packed SDK (local feed)" check_template
  check "the vsix lists its files" check_vsix
  if ((signing)); then check "Authenticode signatures verify" check_authenticode; fi
  check "no game data, home paths, keys, passwords or tokens in any file or archive" \
    python3 "$lib/release.py" scan "$out" --tmp "$ck" --secret-file "$key.password" --secret-file "$key" \
    --secret-file "${KILN_AUTHENTICODE_ENV:-$HOME/.config/kiln-release/authenticode.env}" \
    --secret-file "$HOME/.config/kiln-release/nexus.env"
}
