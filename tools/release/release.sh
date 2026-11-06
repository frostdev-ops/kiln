#!/usr/bin/env bash
# Builds a complete Kiln release folder from a clean export of one commit, checks it, and stops: publishing is a
# separate, manual step (tools/release/github-draft.sh, tools/release/publish.sh; docs/kiln/launcher/releasing.md).
#   tools/release/release.sh VERSION [--commit REV] [--out DIR] [--work DIR] [--notes FILE] [--worktree]
#                                    [--skip-windows] [--no-checks] [--force]
# VERSION: X.Y.Z (stable) or X.Y.Z-dev.N (dev) for this tree's VERSION (tools/release/version.sh prints one).
# --commit: what to build (default HEAD); exported with `git archive`, so uncommitted work never gets in.
# --worktree: test runs only: export the working tree (tracked and untracked, not ignored) instead of a commit.
# --out: the release folder (default ~/Kiln-release/kiln-VERSION). --work: build dir (default <repo>/.out/release-work;
#   a fixed path, so later runs reuse the compiler caches). --notes: release notes (Markdown); default: the commit
#   subjects since the previous v* tag.
#
# The folder: README.txt, RELEASE-NOTES.md, SHA256SUMS, LICENSE, CREDITS.md, THIRD-PARTY-NOTICES.txt,
#   linux/    the AppImage (+ .sig, the updater signature), .deb, .rpm
#   windows/  the NSIS setup (+ .sig)
#   runtime/  kiln-runtime-VERSION.zip, kilnctl, kilnctl.exe, INSTALL-RUNTIME.txt
#   sdk/      Kiln.Sdk, Kiln.Api, Kiln.Templates nupkgs, kiln, kiln.exe (the CLI), the VS Code extension .vsix
#   samples/  sample mods as zips (Library > import)
#   updates/  latest.json per source and channel: frostdev.io/<channel>/latest.json, github/latest.json
#   source/   kiln-VERSION-src.tar.gz
# Signing: the updater key ~/.config/kiln-release/updater.key (+ updater.key.password); Windows Authenticode only when
#   ~/.config/kiln-release/authenticode.env exists (tools/release/authenticode.sh). Neither is ever printed.
# Checks it runs on its own output (all of them; it fails at the end when one failed): SHA256SUMS, the updater
#   signatures against the launcher's built-in key, the AppImage's --selftest on a private display, the NSIS setup
#   installed silently under Wine + --selftest, the deb's control and files, the rpm's files, `kilnctl bundle verify`,
#   kilnctl/kiln --version (Windows builds under Wine), a mod from the packed templates built against the packed SDK,
#   the vsix's files, Authenticode signatures (when signed), and a scan of every file and archive for game data, home
#   paths, keys, passwords and tokens.
# Builds run one at a time (they share Cargo's build directory); KILN_RELEASE_NICE=1 runs them at the lowest priority.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
lib=$here/lib

version="" commit=HEAD out="" work="" notes="" worktree=0 skip_windows=0 checks=1 force=0
while (($#)); do
  case $1 in
    --commit) commit=$2; shift 2 ;;
    --out) out=$2; shift 2 ;;
    --work) work=$2; shift 2 ;;
    --notes) notes=$(realpath "$2"); shift 2 ;;
    --worktree) worktree=1; shift ;;
    --skip-windows) skip_windows=1; shift ;;
    --no-checks) checks=0; shift ;;
    --force) force=1; shift ;;
    -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d; s/^# \{0,1\}//'; exit 0 ;;
    -*) echo "unknown option $1" >&2; exit 2 ;;
    *) [[ -z $version ]] || { echo "one version only" >&2; exit 2; }; version=$1; shift ;;
  esac
done
[[ -n $version ]] || { echo "usage: $0 VERSION [options] (tools/release/version.sh dev|stable prints one)" >&2; exit 2; }
"$here/version.sh" check "$version"
case $version in *-*) channel=dev ;; *) channel=stable ;; esac
out=$(realpath -m "${out:-$HOME/Kiln-release/kiln-$version}")
work=$(realpath -m "${work:-$repo/.out/release-work}")
key=$HOME/.config/kiln-release/updater.key
[[ -f $key && -f $key.password ]] || { echo "no updater key at $key (+ .password): see releasing.md" >&2; exit 1; }
if [[ -e $out && -n $(ls -A "$out" 2>/dev/null) ]]; then
  ((force)) || { echo "$out is not empty (use --force to replace it)" >&2; exit 1; }
  rm -rf "$out"
fi
mkdir -p "$out" "$work"
log=$work/release.log
: >"$log"
# KILN_RELEASE_NICE=1: build at the lowest CPU and IO priority (on a machine someone is using)
if [[ ${KILN_RELEASE_NICE:-} == 1 ]]; then low() { nice -n 19 ionice -c 3 "$@"; }; else low() { "$@"; }; fi
t0=$(date +%s)
step() { printf '\n== %s (%ss)\n' "$*" $(($(date +%s) - t0)) | tee -a "$log"; }
note() { echo "$*" | tee -a "$log"; }
die() { echo "release: $*" | tee -a "$log" >&2; exit 1; }
quiet() { "$@" >>"$log" 2>&1 || { tail -40 "$log" >&2; die "failed: $*"; }; }

# ---------------------------------------------------------------- 1. export
src=$work/src
step "export $( ((worktree)) && echo "working tree" || echo "$commit") to $src"
rm -rf "$src" && mkdir -p "$src"
if ((worktree)); then
  sha=$(git -C "$repo" rev-parse --short=7 HEAD)
  export KILN_GIT=$sha
  export SOURCE_DATE_EPOCH=$(date +%s)
  built_from="the working tree at $sha (a test build, not a release)"
  (cd "$repo" && git ls-files -z -co --exclude-standard | python3 -c '
import os, sys
for p in sys.stdin.buffer.read().split(b"\0"):
    if p and os.path.isfile(p) and not os.path.islink(p): sys.stdout.buffer.write(p + b"\0")' | tar --null -T - -cf -) | tar -xf - -C "$src"
  note "working tree of $sha (with uncommitted and untracked files): a test build, never a release"
else
  full=$(git -C "$repo" rev-parse --verify "$commit^{commit}")
  export KILN_GIT=${full:0:7}
  export SOURCE_DATE_EPOCH=$(git -C "$repo" log -1 --format=%ct "$full")
  git -C "$repo" archive --format=tar "$full" | tar -xf - -C "$src"
  built_from="commit $KILN_GIT"
fi
# the source as exported, before any build writes into it
mkdir -p "$work/source"
rm -f "$work"/source/*.tar.gz
tar --sort=name --owner=0 --group=0 --numeric-owner --mtime="@$SOURCE_DATE_EPOCH" -C "$work" \
  --transform "s|^src|kiln-$version|" -czf "$work/source/kiln-$version-src.tar.gz" src
[[ $(sed 's/[-+].*//' "$src/VERSION") == "${version%%-*}" ]] || die "VERSION in the export is $(cat "$src/VERSION"), not for $version"
date_iso=$(date -u -d "@$SOURCE_DATE_EPOCH" +%Y-%m-%d)

# ---------------------------------------------------------------- 2. environment
export KILN_OUT=$work/out CARGO_TARGET_DIR=$work/cargo
# the SDK packages carry the release's version (managed/Kiln.Sdk/pack.sh), and kiln (the CLI) is built knowing it
export KILN_SDK_VERSION=$version
rm -rf "$KILN_OUT" && mkdir -p "$KILN_OUT"
# no build paths in the binaries: the home folder and the export become neutral prefixes; KILN_RELEASE_BUILD drops
# the "tree this was built from" defaults of kilnctl and kiln
export RUSTFLAGS="--remap-path-prefix=$HOME=/build --remap-path-prefix=$src=/kiln" KILN_RELEASE_BUILD=1
export ContinuousIntegrationBuild=true DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 NO_STRIP=${NO_STRIP:-true}
cache=${KILN_RELEASE_CACHE:-$HOME/.cache/kiln-release}
mkdir -p "$cache"
# pinned downloads (each script checks its hash): .NET for the runtime, netcoredbg for the vsix; reused when cached
seed() { # name, destination, candidates...
  local name=$1 dest=$2; shift 2
  if [[ ! -f $cache/$name ]]; then for c in "$@"; do [[ -f $c ]] && { cp "$c" "$cache/$name"; break; }; done; fi
  if [[ -f $cache/$name ]]; then mkdir -p "$(dirname "$dest")" && cp "$cache/$name" "$dest"; fi
}
seed dotnet-runtime-win-x64.zip "$src/.cache/dotnet-win-x64/runtime.zip" "$repo/.cache/dotnet-win-x64/runtime.zip"
nc=$(sed -n 's/^const VERSION = "\(.*\)";/\1/p' "$src/tools/vscode/scripts/fetch-netcoredbg.mjs")
seed "netcoredbg-$nc-win64.zip" "$KILN_OUT/vscode/netcoredbg/$nc/netcoredbg-win64.zip" "$repo"/.out/*/netcoredbg/"$nc"/netcoredbg-win64.zip
signing=0
if "$here/authenticode.sh" available; then signing=1; note "Authenticode: signing Windows files"; else note "Windows files unsigned (no authenticode.env)"; fi
sign_pe() { ((signing)) && "$here/authenticode.sh" sign "$@" | tee -a "$log"; return 0; }

# ---------------------------------------------------------------- 3. dependencies, command-line tools
step "dependencies (bun, npm)"
(cd "$src/launcher" && quiet low bun install --frozen-lockfile)
(cd "$src/tools/vscode" && quiet low npm ci --no-audit --no-fund)
step "kilnctl and kiln (Linux, Windows)"
(cd "$src" && quiet low cargo build --release --locked -p kilnctl -p kiln-cli)
win=x86_64-pc-windows-msvc
(cd "$src" && quiet low cargo xwin build --release --locked -p kilnctl -p kiln-cli --target $win)
kilnctl=$CARGO_TARGET_DIR/release/kilnctl

# ---------------------------------------------------------------- 4. runtime
step "runtime: native + managed build, stage"
stage=$work/stage
# the native DLLs name their PDB without a path (/pdbaltpath); the PDBs themselves hold the build machine's paths, so
# they stay out of the release and go to $work/symbols (keep them for crash reports of this build)
CFLAGS="/clang:-ffile-prefix-map=$HOME=/build /clang:-ffile-prefix-map=$src=/kiln" \
  CXXFLAGS="/clang:-ffile-prefix-map=$HOME=/build /clang:-ffile-prefix-map=$src=/kiln" LDFLAGS="/pdbaltpath:%_PDB%" \
  quiet low "$src/tools/package/stage.sh" "$stage"
rm -rf "$work/symbols"
while IFS= read -r -d '' pdb; do
  dll=${pdb%.pdb}.dll
  [[ -f $dll ]] && python3 -c 'import sys; d=open(sys.argv[1],"rb").read(); sys.exit(0 if b"RSDS" in d and b"LLD" in d else 1)' "$dll" || continue
  rel=${pdb#"$stage"/}
  mkdir -p "$work/symbols/$(dirname "$rel")"
  mv "$pdb" "$work/symbols/$rel"
done < <(find "$stage" -name '*.pdb' -print0)
note "native symbols (PDBs) kept out of the release: $(find "$work/symbols" -name '*.pdb' 2>/dev/null | wc -l) in $work/symbols"
step "third-party notices"
notices=$work/notices/THIRD-PARTY-NOTICES.txt
mapfile -t assets < <(find "$KILN_OUT/dotnet/obj" -name project.assets.json -not -path '*Tests*' 2>/dev/null | sort)
nuget_args=()
for a in "${assets[@]}"; do nuget_args+=(--nuget-assets "$a"); done
python3 "$lib/notices.py" --src "$src" --out "$notices" --version "$version" --dotnet-root "$src/.cache/dotnet-win-x64/root" \
  "${nuget_args[@]}" | tee -a "$log"
install -m 644 "$notices" "$stage/core/kiln/THIRD-PARTY-NOTICES.txt"
install -m 644 "$src/LICENSE" "$stage/core/kiln/LICENSE.txt"
if ((signing)); then
  mapfile -t pes < <(find "$stage" -type f \( -iname '*.dll' -o -iname '*.exe' \) -not -path '*/kiln/dotnet/*' | sort)
  step "Authenticode: ${#pes[@]} runtime files"
  sign_pe "${pes[@]}"
fi
step "runtime bundle"
rm -rf "$work/dist" && mkdir -p "$work/dist"
quiet "$kilnctl" bundle create "$stage" --out "$work/dist" --git "$KILN_GIT" --epoch "$SOURCE_DATE_EPOCH"
runtime_zip=$(ls "$work"/dist/kiln-runtime-*.zip)
runtime_version=$(basename "$runtime_zip" .zip); runtime_version=${runtime_version#kiln-runtime-}
note "runtime $runtime_version"

# ---------------------------------------------------------------- 5. launcher
resources() { printf '{"bundle":{"resources":{"%s":"THIRD-PARTY-NOTICES.txt","%s":"LICENSE.txt"}}}' "$1" "$src/LICENSE"; }
step "launcher: Linux"
# rpm versions can't hold "-": a dev build (X.Y.Z-dev.N) has no rpm
bundles=appimage,deb
[[ $channel == stable ]] && bundles+=,rpm
rm -rf "$CARGO_TARGET_DIR/release/bundle"
if ! (cd "$src" && low tools/launcher/build-linux.sh --bundles $bundles --runtime "$runtime_zip" --version "$version" \
    --config "$(resources "$notices")" >>"$log" 2>&1); then
  note "the rpm did not build; building AppImage and deb only"
  bundles=appimage,deb
  (cd "$src" && quiet low tools/launcher/build-linux.sh --bundles $bundles --runtime "$runtime_zip" --version "$version" --config "$(resources "$notices")")
fi
lin=$KILN_OUT/bundles/linux
appimage=$(ls -t "$lin"/*.AppImage | head -1)
# the AppImage carries system libraries (GTK, WebKitGTK, ...): add them to the notices, then rebundle the AppImage
step "notices for the AppImage's bundled libraries"
rm -rf "$work/appimage-root" && mkdir -p "$work/appimage-root"
unsquashfs -q -n -o "$(python3 "$lib/release.py" offset "$appimage")" -d "$work/appimage-root/r" "$appimage" >/dev/null
python3 "$lib/notices.py" --src "$src" --out "$notices" --version "$version" --dotnet-root "$src/.cache/dotnet-win-x64/root" \
  "${nuget_args[@]}" --appimage-root "$work/appimage-root/r" | tee -a "$log"
rm -rf "$work/appimage-root"
(cd "$src" && quiet low tools/launcher/build-linux.sh --bundles appimage --runtime "$runtime_zip" --version "$version" --config "$(resources "$notices")")
appimage=$(ls -t "$lin"/*.AppImage | head -1)
deb=$(ls -t "$lin"/*.deb | head -1)
rpm=$(ls -t "$lin"/*.rpm 2>/dev/null | head -1 || true)

setup=""
if ((!skip_windows)); then
  step "launcher: Windows (NSIS)"
  wargs=(--runtime "$runtime_zip" --version "$version" --config "$(resources "$notices")")
  ((signing)) && wargs+=(--config "{\"bundle\":{\"windows\":{\"signCommand\":{\"cmd\":\"$here/authenticode.sh\",\"args\":[\"sign\",\"%1\"]}}}}")
  rm -rf "$CARGO_TARGET_DIR/$win/release/bundle"
  (cd "$src" && quiet low tools/launcher/build-windows.sh "${wargs[@]}")
  setup=$KILN_OUT/bundles/windows/kiln-launcher-setup.exe
  sign_pe "$CARGO_TARGET_DIR/$win/release/kilnctl.exe" "$CARGO_TARGET_DIR/$win/release/kiln.exe"
fi

# ---------------------------------------------------------------- 6. SDK, VS Code, samples
step "SDK packages"
quiet low "$src/managed/Kiln.Sdk/pack.sh"
sdk_version=$KILN_SDK_VERSION
step "VS Code extension"
(cd "$src/tools/vscode" && KILN_OUT="$KILN_OUT/vscode" quiet low node scripts/package.mjs)
vsix=$(ls -t "$KILN_OUT"/vscode/kiln-vscode-*.vsix | head -1)
step "sample mods"
rm -rf "$work/samples" && mkdir -p "$work/samples"
skipped_samples=()
# a sample on Kiln.Sdk packs itself (KilnPack); an older one builds its output folder (a deployable mod folder), which
# is zipped the way KilnPack does: <id>-<version>.zip holding <id>/, without the shared Kiln.* assemblies, *.deps.json
# and cooked game files
pack_folder() { # project
  local proj=$1 o=$work/samples-build/$(basename "$1" .csproj)
  rm -rf "$o"
  low dotnet build "$proj" -c Release --artifacts-path "$KILN_OUT/dotnet" -v q -nologo -o "$o" >>"$log" 2>&1 || return 1
  python3 - "$o" "$work/samples" <<'PY'
import json, os, re, sys, zipfile
o, dest = sys.argv[1:]
text = re.sub(r"^\s*//.*$", "", open(os.path.join(o, "kiln.mod.json"), encoding="utf-8").read(), flags=re.M)
m = json.loads(re.sub(r",(\s*[}\]])", r"\1", text))
mid, ver = m["id"], m.get("version", "0.0.0")
with zipfile.ZipFile(os.path.join(dest, f"{mid}-{ver}.zip"), "w", zipfile.ZIP_DEFLATED) as z:
    for d, _, files in os.walk(o):
        for n in sorted(files):
            rel = os.path.relpath(os.path.join(d, n), o).replace(os.sep, "/")
            if (n.startswith("Kiln.") and n != "Kiln.Admin.dll") or n.endswith((".deps.json", ".runtimeconfig.json")) or rel.startswith("assets/cooked/"):
                continue
            z.write(os.path.join(d, n), f"{mid}/{rel}")
PY
}
for proj in "$src"/mods/samples/*/*.csproj; do
  name=$(basename "$proj" .csproj)
  if grep -q 'Sdk="Kiln.Sdk' "$proj"; then
    low dotnet build "$proj" -c Release --artifacts-path "$KILN_OUT/dotnet" -v q -nologo -t:KilnPack \
      -p:KilnPackageOutputPath="$work/samples/" >>"$log" 2>&1 || skipped_samples+=("$name")
  else
    pack_folder "$proj" || skipped_samples+=("$name")
  fi
done
note "sample mods: $(ls "$work"/samples/*.zip 2>/dev/null | wc -l) zips"
((${#skipped_samples[@]})) && note "samples that did not build (left out): ${skipped_samples[*]}"

# ---------------------------------------------------------------- 7. the folder
step "assemble $out"
mkdir -p "$out"/{linux,windows,runtime,sdk,samples,updates,source}
A=kiln-launcher_${version}_amd64.AppImage D=kiln-launcher_${version}_amd64.deb R=kiln-launcher-${version}.x86_64.rpm S=kiln-launcher_${version}_x64-setup.exe
cp "$appimage" "$out/linux/$A" && chmod 755 "$out/linux/$A"
cp "$deb" "$out/linux/$D"
[[ -n $rpm ]] && cp "$rpm" "$out/linux/$R"
[[ -n $setup ]] && cp "$setup" "$out/windows/$S"
cp "$runtime_zip" "$out/runtime/kiln-runtime-$version.zip"
install -m 755 "$CARGO_TARGET_DIR/release/kilnctl" "$out/runtime/kilnctl"
install -m 755 "$CARGO_TARGET_DIR/release/kiln" "$out/sdk/kiln"
if ((!skip_windows)); then
  install -m 644 "$CARGO_TARGET_DIR/$win/release/kilnctl.exe" "$out/runtime/kilnctl.exe"
  install -m 644 "$CARGO_TARGET_DIR/$win/release/kiln.exe" "$out/sdk/kiln.exe"
fi
for f in "$KILN_OUT"/nuget/*."$sdk_version".nupkg; do cp "$f" "$out/sdk/"; done
cp "$vsix" "$out/sdk/"
cp "$work"/samples/*.zip "$out/samples/" 2>/dev/null || true
cp "$src/LICENSE" "$src/CREDITS.md" "$notices" "$out/"
cp "$work/source/kiln-$version-src.tar.gz" "$out/source/"

# release notes
if [[ -n $notes ]]; then cp "$notes" "$out/RELEASE-NOTES.md"; else
  {
    echo "# Kiln $version"
    echo
    if ((worktree)); then echo "Test build of the working tree at $KILN_GIT."; else
      # GitHub makes the v* tags when a release is published: fetch them, or every release lists the whole history
      git -C "$repo" fetch -q --tags origin 2>/dev/null || echo "release: could not fetch tags; the notes may start too early" >&2
      prev=$(git -C "$repo" describe --tags --abbrev=0 --match 'v*' "$full^" 2>/dev/null || true)
      echo "Changes since ${prev:-the start of the project}:"
      echo
      git -C "$repo" log --no-merges --format='- %s' ${prev:+"$prev.."}"$full" | head -60
    fi
  } >"$out/RELEASE-NOTES.md"
fi
# the manifest's notes: the first lines of RELEASE-NOTES.md after its title
short=$work/notes-short.txt
sed '1{/^# /d}' "$out/RELEASE-NOTES.md" | sed '/./,$!d' | head -12 >"$short"

# ---------------------------------------------------------------- 8. updater signatures, manifests
step "updater signatures"
pass_file=$key.password
updater_files=("$out/linux/$A")
[[ -n $setup ]] && updater_files+=("$out/windows/$S")
for f in "${updater_files[@]}"; do
  (cd "$src/launcher" && TAURI_SIGNING_PRIVATE_KEY_PASSWORD="$(cat "$pass_file")" \
    bun x tauri signer sign -f "$key" --app-version "$version" "$f" >/dev/null 2>&1) || die "signing $f failed"
  note "signed $(basename "$f")"
done
step "update manifests"
fd=https://frostdev.io/kiln/downloads/$version
gh=https://github.com/frostdev-ops/kiln/releases/download/v$version
manifest() { # base-url layout(folders|flat) out channel
  local base=$1 flat=$2 dest=$3 ch=$4 e=()
  local la="linux/$A" ws="windows/$S"
  [[ $flat == flat ]] && la=$A ws=$S
  e+=(--entry "linux-x86_64-appimage,linux-x86_64=$base/$la|$out/linux/$A.sig|$out/linux/$A")
  [[ -n $setup ]] && e+=(--entry "windows-x86_64-nsis,windows-x86_64=$base/$ws|$out/windows/$S.sig|$out/windows/$S")
  python3 "$lib/release.py" manifest --version "$version" --channel "$ch" --notes "$short" --runtime "$runtime_version" \
    --date "$(date -u -d "@$SOURCE_DATE_EPOCH" +%Y-%m-%dT%H:%M:%SZ)" "${e[@]}" --out "$dest"
}
manifest "$fd" folders "$out/updates/frostdev.io/$channel/latest.json" "$channel"
manifest "$gh" flat "$out/updates/github/latest.json" "$channel"
if [[ $channel == stable ]]; then
  # a release is also the newest build of the dev channel (dev builds of the next version replace it)
  manifest "$fd" folders "$out/updates/frostdev.io/dev/latest.json" dev
fi

# ---------------------------------------------------------------- 9. README, sums
step "README.txt, INSTALL-RUNTIME.txt, SHA256SUMS"
smart="           Windows SmartScreen may warn that the publisher is unknown: the setup program is not code-signed yet.
           Choose More info > Run anyway, after checking its SHA256 below."
((signing)) && smart="           The setup program is code-signed (Authenticode)."
rpm_line=""
[[ -n $rpm ]] && rpm_line=$'\n'"           or linux/$R (Fedora, openSUSE)"
fill() { # template out: @KEY@ from the R_KEY variables
  R_VERSION=$version R_CHANNEL=$channel R_DATE=$date_iso R_SOURCE="$built_from" R_RUNTIME_VERSION=$runtime_version \
  R_SDK_VERSION=$sdk_version R_APPIMAGE=$A R_DEB=$D R_SETUP=$S R_VSIX=$(basename "$vsix") R_RPM_LINE=$rpm_line \
  R_SMARTSCREEN=$smart R_RULE=$(printf '=%.0s' $(seq 1 $((5 + ${#version})))) python3 - "$1" "$2" <<'PY'
import os, sys
t = open(sys.argv[1], encoding="utf-8").read()
for k, v in os.environ.items():
    if k.startswith("R_"):
        t = t.replace("@" + k[2:] + "@", v)
open(sys.argv[2], "w", encoding="utf-8").write(t)
PY
}
fill "$here/templates/INSTALL-RUNTIME.txt" "$out/runtime/INSTALL-RUNTIME.txt"
fill "$here/templates/README.txt" "$out/README.txt.tmp"
python3 - "$out" <<'PY'
import os, sys
root = sys.argv[1]
rows = []
for d, dirs, files in os.walk(root):
    dirs.sort()
    for n in sorted(files):
        rel = os.path.relpath(os.path.join(d, n), root)
        if rel in ("README.txt.tmp", "SHA256SUMS"):
            continue
        s = os.path.getsize(os.path.join(d, n))
        rows.append(f"  {rel:<64} {s / 1048576:8.1f} MB" if s >= 1048576 else f"  {rel:<64} {s / 1024:8.1f} KB")
rows.append(f"  {'README.txt':<64}")
rows.append(f"  {'SHA256SUMS':<64}")
t = open(os.path.join(root, "README.txt.tmp"), encoding="utf-8").read().replace("@FILES@", "\n".join(rows))
open(os.path.join(root, "README.txt"), "w", encoding="utf-8").write(t)
os.remove(os.path.join(root, "README.txt.tmp"))
PY
python3 "$lib/release.py" sums "$out"

# ---------------------------------------------------------------- 10. checks
results=()
check() { # name command...
  local name=$1; shift
  if "$@" >>"$log" 2>&1; then results+=("PASS  $name"); else results+=("FAIL  $name (see $log)"); fi
}
if ((checks)); then
  step "checks"
  # shellcheck source=checks.sh
  source "$here/checks.sh"
  run_checks
  printf '%s\n' "${results[@]}" | tee -a "$log"
fi

# Nexus Mods: what would go up (GET calls only); the upload itself is publish.sh --nexus or nexus.py --publish
if [[ -f $HOME/.config/kiln-release/nexus.env ]]; then
  step "Nexus Mods (dry run)"
  python3 -I "$here/nexus.py" "$out" --work "$work/nexus" | tee -a "$log" || note "the Nexus dry run failed (the release is fine)"
fi

step "done"
du -sh "$out" | tee -a "$log"
(cd "$out" && find . -type f | sort | while read -r f; do printf '%10s  %s\n' "$(du -h "$f" | cut -f1)" "${f#./}"; done) | tee -a "$log"
cat <<EOF | tee -a "$log"

Release $version is in $out (log: $log). Nothing is published.
Next (by hand, see docs/kiln/launcher/releasing.md):
  tools/release/github-draft.sh $out        a DRAFT GitHub release with the files (needs the commit pushed)
  tools/release/publish.sh $out             publish: the GitHub release, the dev channel pointer, frostdev.io
EOF
if printf '%s\n' "${results[@]}" | grep -q '^FAIL'; then exit 1; fi
