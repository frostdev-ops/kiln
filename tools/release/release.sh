#!/usr/bin/env bash
# Builds a complete Kiln release folder from a clean export of one commit, checks it, and stops: publishing is a
# separate, manual step (tools/release/github-draft.sh, tools/release/publish.sh; docs/kiln/launcher/releasing.md).
#   tools/release/release.sh VERSION [--commit REV] [--out DIR] [--work DIR] [--notes FILE] [--worktree]
#                                    [--skip-windows] [--no-checks] [--force] [--host-build] [--no-nexus]
# VERSION: X.Y.Z (stable) or X.Y.Z-dev.N (dev) for this tree's VERSION (tools/release/version.sh prints one).
# --commit: what to build (default HEAD); exported with `git archive`, so uncommitted work never gets in.
# --worktree: test runs only: export the working tree (tracked and untracked, not ignored) instead of a commit.
# --out: the release folder (default ~/Kiln-release/kiln-VERSION). --work: build dir (default <repo>/.out/release-work;
#   a fixed path, so later runs reuse the compiler caches). --notes: release notes (Markdown); default: the commit
#   subjects since the previous v* tag.
# Linux files (the launcher's AppImage, deb and rpm, kilnctl, kiln) are compiled and bundled in a Docker container,
#   Ubuntu 22.04 (glibc 2.35; tools/release/linux-build/), so they run on older distros and the Steam Deck: a build on
#   this machine needs its own, newer glibc. Cargo's Linux target dir: KILN_GLIBC_DIR (default <repo>/.out/glibc)/cargo.
# --host-build: quick local tests only: build the Linux files on this machine instead (fast, no Docker). They run only
#   on distros with this machine's glibc or newer; the glibc check fails, and such a folder must never be published.
# --no-nexus: skip the Nexus Mods dry run at the end (test builds).
#
# The folder: README.txt, RELEASE-NOTES.md, SHA256SUMS, LICENSE, CREDITS.md, THIRD-PARTY-NOTICES.txt,
#   linux/    the AppImage (+ .sig, the updater signature), .deb, .rpm
#   windows/  the NSIS setup (+ .sig)
#   runtime/  kiln-runtime-VERSION.zip, kilnctl, kilnctl.exe, INSTALL-RUNTIME.txt, kiln-server-VERSION.zip (the server
#             pack: the server components as a flat zip to unzip into a server folder, lib/serverpack.py), and the
#             systemd units for kilnctl (kiln@.service, kiln-backup@.service, kiln-backup@.timer)
#   sdk/      Kiln.Sdk, Kiln.Api, Kiln.Templates nupkgs, kiln, kiln.exe (the CLI), the VS Code extension .vsix;
#             feed/: the nupkgs as a static NuGet v3 feed (lib/feed.py; its URLs: KILN_FEED_URL, default this folder on
#             frostdev.io); api/: the modding site and API reference (tools/docs-site/build.sh, when the export has it).
#             Both are built only: publish.sh uploads them with --sdk-site, github-draft.sh never
#   samples/  sample mods as zips (Library > import)
#   updates/  latest.json per source and channel: frostdev.io/<channel>/latest.json, github/latest.json
#   source/   kiln-VERSION-src.tar.gz
#   nexus/    the Nexus Mods build of the launcher: the same launcher without its self-update (Nexus Mods does not allow
#             programs that update themselves): linux/ AppImage + deb, windows/ setup, README.txt; no .sig, no
#             manifest. Only nexus.py uploads it; github-draft.sh and publish.sh leave it out.
# Signing: the updater key ~/.config/kiln-release/updater.key (+ updater.key.password); Windows Authenticode only when
#   ~/.config/kiln-release/authenticode.env exists (tools/release/authenticode.sh). Neither is ever printed.
# Checks it runs on its own output (all of them; it fails at the end when one failed): SHA256SUMS, the updater
#   signatures against the launcher's built-in key, the AppImage's --selftest on a private display, the NSIS setup
#   installed silently under Wine + --selftest (both for the Nexus build too, which must say distribution nexus), the
#   Nexus builds holding none of the self-update (feeds, updater key, plugin) and the direct ones all of it, the deb's
#   control and files, the rpm's files, `kilnctl bundle verify`,
#   kilnctl/kiln --version (Windows builds under Wine), every Linux ELF (in the AppImage, deb and rpm too) needing no
#   glibc symbol version above 2.35, the server pack (only the server components, the runtime's bytes, rebuilt
#   byte-identically) and the systemd units, kilnctl/kiln/the AppImage started on a clean Ubuntu 22.04 (a throwaway
#   container: --selftest and the launcher's window),a mod from the packed templates built against the packed SDK,
#   the NuGet feed against the nupkgs, the API reference's index.html (when built), the vsix's files, Authenticode signatures (when signed), every plugin DLL in the runtime's manifest (lib/plugins.py), and a scan of every file and archive for game data, home
#   paths, keys, passwords and tokens.
# The API gate (tools/release/api-gate.sh), before any build: no shipped public API removed since the previous v* tag,
#   new API only with a raised Kiln.Mods.ApiVersion; after a release whose checks passed, the Unshipped.txt lines move
#   into Shipped.txt in the working tree (to commit). The builds get the full version (VERSION in the export).
# KILN_API_BASELINE: the last release's Kiln.Api .nupkg (or it unzipped) for pack.sh's API compat gate; unset: a loud
#   warning, at the start and in the closing text, and the release goes on.
# Builds run one at a time (they share Cargo's build directory); KILN_RELEASE_NICE=1 runs them at the lowest priority.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
lib=$here/lib
source "$here/linux-build/paths.sh"

version="" commit=HEAD out="" work="" notes="" worktree=0 skip_windows=0 checks=1 force=0 host_build=0 nexus=1
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
    --host-build) host_build=1; shift ;;
    --no-nexus) nexus=0; shift ;;
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
(( $(id -u) != 0 )) || { echo "release: run as the release owner, not root" >&2; exit 1; }
safe_dir "$out"
safe_dir "$work"
disjoint_dirs "$out" "$work"
for d in "$out" "$work"; do
  [[ $repo != "$d" && $repo != "$d/"* ]] || { echo "release: directory contains the repository: $d" >&2; exit 2; }
done
if ((!host_build)); then
  glibc=$(realpath -ms "${KILN_GLIBC_DIR:-$repo/.out/glibc}")
  safe_dir "$glibc"
  disjoint_dirs "$glibc" "$work"
  disjoint_dirs "$glibc" "$out"
fi
key=$HOME/.config/kiln-release/updater.key
[[ -f $key && -f $key.password ]] || { echo "no updater key at $key (+ .password): see releasing.md" >&2; exit 1; }
if [[ -e $out && -n $(ls -A "$out" 2>/dev/null) ]]; then
  ((force)) || { echo "$out is not empty (use --force to replace it)" >&2; exit 1; }
  rm -rf "$out"
fi
mkdir -p "$out" "$work"
cleanup_release() { rm -rf -- "$work/appimage-root"; }
trap cleanup_release EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP
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
tar --sort=name --owner=0 --group=0 --numeric-owner --mode='u=rwX,go=rX' --mtime="@$SOURCE_DATE_EPOCH" -C "$work" \
  --transform "s|^src|kiln-$version|" -czf "$work/source/kiln-$version-src.tar.gz" src
[[ $(sed 's/[-+].*//' "$src/VERSION") == "${version%%-*}" ]] || die "VERSION in the export is $(cat "$src/VERSION"), not for $version"
# The API gate (tools/release/api-gate.sh), before any build: no line of a PublicAPI.Shipped.txt removed since the
# previous v* release, and new API only with a raised API version (Kiln.Mods.ApiVersion), so a mod can ask for it.
if ((worktree)); then
  gate_new=(--worktree)
  prev=$(git -C "$repo" describe --tags --abbrev=0 --match 'v*' HEAD 2>/dev/null || true)
else
  # GitHub makes the v* tags when a release is published: fetch them, or the gate and the notes start too early
  git -C "$repo" fetch -q --tags origin 2>/dev/null || note "release: could not fetch tags; the API gate and the notes may start too early"
  gate_new=("$full")
  prev=$(git -C "$repo" describe --tags --abbrev=0 --match 'v*' "$full^" 2>/dev/null || true)
fi
if [[ -z $prev ]]; then
  note "WARNING: no previous v* tag: the API gate has nothing to compare with (removed API and the API version are not checked)"
else
  step "API gate: the public API since $prev"
  "$here/api-gate.sh" removed "$prev" "${gate_new[@]}" 2>&1 | tee -a "$log" || die "shipped API was removed since $prev (above): the mod API is additive"
  "$here/api-gate.sh" version-rule "$prev" "${gate_new[@]}" 2>&1 | tee -a "$log" || die "new API needs a higher API version (above)"
fi
# The API compat gate in pack.sh (tools/release/apicompat.sh) compares the SDK's assemblies with the last release's:
# KILN_API_BASELINE, a folder (an unzipped Kiln.Api.<ver>.nupkg) or that .nupkg. A wrong one stops here, not an hour
# later in pack.sh; none is a loud warning (pack.sh's own runs under quiet, into the log only) and the release goes on.
api_compat=1
if [[ -n ${KILN_API_BASELINE:-} ]]; then
  b=$(realpath -m -- "$KILN_API_BASELINE")
  if [[ -d $b ]]; then
    [[ -n $(find "$b" -name Kiln.Api.dll -type f -print -quit) ]] || die "KILN_API_BASELINE=$b holds no Kiln.Api.dll (an unzipped Kiln.Api.<ver>.nupkg of the last release)"
  elif [[ -f $b && $b == *.nupkg ]]; then
    python3 -I -c 'import sys, zipfile; sys.exit(0 if any(n.endswith("/Kiln.Api.dll") for n in zipfile.ZipFile(sys.argv[1]).namelist()) else 1)' "$b" 2>/dev/null \
      || die "KILN_API_BASELINE=$b is not a Kiln.Api .nupkg holding Kiln.Api.dll"
  else
    die "KILN_API_BASELINE=$b does not exist or is neither a folder nor a .nupkg"
  fi
  export KILN_API_BASELINE=$b
  note "API compat gate: pack.sh compares the SDK's assemblies with $b"
else
  api_compat=0
  note "WARNING: ****************************************************************************************"
  note "WARNING: KILN_API_BASELINE is not set: pack.sh's API compat gate will NOT run."
  note "WARNING: Nothing compares this release's SDK assemblies (Kiln.Api, Kiln.Testing, the services) with the last"
  note "WARNING: release's: a removed or changed public member would ship unnoticed (the PublicAPI record gate above"
  note "WARNING: still ran). Set KILN_API_BASELINE to the last release's Kiln.Api.<ver>.nupkg, or to it unzipped."
  note "WARNING: ****************************************************************************************"
fi
# The full release version in the builds: native/CMakeLists.txt (kiln_version.h: the core's and the proxy's version),
# managed/KilnVersion.props (the assemblies' informational version and Kiln.Api's Kiln.Build stamp) and
# tools/package/stage.sh (kiln-runtime.json, so the runtime zip's name) read the export's VERSION. Written after the
# source tarball and the check above: the tarball keeps VERSION as committed.
printf '%s\n' "$version" >"$src/VERSION"
export KilnVersion=$version
# ShroudForge's runtime (submodule third_party/shroudforge, a GPL-3 component): git archive leaves submodules out, so
# export the fork at the pinned commit with its own submodules, ship that as its corresponding source, and build from it
sfpin=$(git -C "$repo" ls-tree "${full:-HEAD}" third_party/shroudforge | awk '$2 == "commit" {print $3}')
if [[ -d $src/gpl/kiln-eml && -z $sfpin ]]; then die "release has no pinned ShroudForge submodule"; fi
# A release always builds the exported pin, regardless of a developer override.
unset KILN_SHROUDFORGE_SRC
if [[ -n $sfpin ]]; then
  "$lib/sf-source.sh" "$repo/third_party/shroudforge" "$sfpin" "$work/source/shroudforge-kiln-${sfpin:0:12}-src.tar.gz" \
    "$src/third_party/shroudforge" >>"$log" 2>&1 || die "cannot export ShroudForge's source at $sfpin (git submodule update --init --recursive third_party/shroudforge)"
  note "ShroudForge runtime source: source/shroudforge-kiln-${sfpin:0:12}-src.tar.gz"
fi
date_iso=$(date -u -d "@$SOURCE_DATE_EPOCH" +%Y-%m-%d)

# ---------------------------------------------------------------- 2. environment
export KILN_OUT=$work/out CARGO_TARGET_DIR=$work/cargo
# the SDK packages carry the release's version (managed/Kiln.Sdk/pack.sh), and kiln (the CLI) is built knowing it
export KILN_SDK_VERSION=$version
rm -rf "$KILN_OUT" && mkdir -p "$KILN_OUT"
# no build paths in the binaries: the home folder and the export become neutral prefixes; KILN_RELEASE_BUILD drops
# the "tree this was built from" defaults of kilnctl and kiln
unset RUSTFLAGS
export CARGO_ENCODED_RUSTFLAGS="--remap-path-prefix=$work=/build/work"$'\x1f'"--remap-path-prefix=$HOME=/build"$'\x1f'"--remap-path-prefix=$src=/kiln" KILN_RELEASE_BUILD=1
# Public, Nexus-approved desktop registration, shared by the direct/Nexus Linux and Windows builds.
# Launcher build.rs rejects official builds without valid values; approval/live-provider proof remains manual.
export KILN_NEXUS_ENABLED KILN_NEXUS_OAUTH_CLIENT_ID KILN_NEXUS_OAUTH_REDIRECT_URI
export ContinuousIntegrationBuild=true DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 NO_STRIP=${NO_STRIP:-true}
# Rust: one toolchain for everything this machine builds (the Windows cross-builds; with --host-build the Linux files
# too): the version the Linux build image has (linux-build/Dockerfile, RUST_VERSION). rustup's `stable` can move while
# a release builds: 0.2.0-dev.26's first build stopped with "not applicable to the 'stable' toolchain" when it did. A
# version number cannot move. KILN_RELEASE_RUST names another version for a trial build.
pin_rust() {
  local target=$1 want
  want=${KILN_RELEASE_RUST:-$(sed -n 's/^ARG RUST_VERSION=//p' "$here/linux-build/Dockerfile")}
  [[ $want =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] \
    || die "no Rust version to build with: '$want' (ARG RUST_VERSION in tools/release/linux-build/Dockerfile, or KILN_RELEASE_RUST)"
  if command -v rustup >/dev/null 2>&1; then
    rustup run "$want" rustc --version >/dev/null 2>&1 \
      || die "Rust $want is not installed here: rustup toolchain install $want --profile minimal --target $target"
    rustup target list --toolchain "$want" --installed 2>/dev/null | grep -qx "$target" \
      || die "Rust $want has no $target target here: rustup target add --toolchain $want $target"
    export RUSTUP_TOOLCHAIN=$want
  else
    [[ $(rustc --version 2>/dev/null) == "rustc $want "* ]] \
      || die "rustc here is '$(rustc --version 2>&1 | head -1)', but the release builds with Rust $want (install rustup, or that version)"
    # Without rustup nothing lists the installed targets: ask rustc where that target's library would be, and look.
    local lib
    lib=$(rustc --print target-libdir --target "$target" 2>/dev/null) || lib=
    [[ -n $lib ]] && compgen -G "$lib/libstd-*.rlib" >/dev/null \
      || die "rustc $want here has no $target library (looked in '${lib:-nowhere: rustc does not know the target}'): install that target for it, or use rustup (rustup toolchain install $want --profile minimal --target $target)"
  fi
  note "Rust $want for the files built on this machine (the Linux build image has the same)"
}
pin_rust x86_64-pc-windows-msvc
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
sign_pe() { if ((signing)); then "$here/authenticode.sh" sign "$@" | tee -a "$log"; fi; }
# linux COMMAND...: runs a Linux build command (cargo, build-linux.sh, notices.py for the AppImage) where the Linux files
# are built: the Ubuntu 22.04 container (tools/release/linux-build/run.sh; $src, $work and tools/release mounted at
# their own paths), or this machine with --host-build. $lin_target is its cargo target dir. The updater key never goes
# into the container: the finished AppImage is signed on this machine (8. below).
glibc_max=2.35
if ((host_build)); then
  lin_target=$CARGO_TARGET_DIR
  linux() { low "$@"; }
  note "--host-build: the Linux files are built on this machine (glibc $(ldd --version | sed -n '1s/.* //p')): NOT portable, never publish this folder"
else
  export KILN_GLIBC_DIR=$glibc
  lin_target=$glibc/cargo
  # the container's crate cache shows as /build/.cargo, like the host's ~/.cargo under the /build prefix above
  lin_rustflags="$CARGO_ENCODED_RUSTFLAGS"$'\x1f'"--remap-path-prefix=$glibc/cargo-home=/build/.cargo"
  linux() {
    local nice=()
    [[ ${KILN_RELEASE_NICE:-} == 1 ]] && nice=(--nice)
    CARGO_TARGET_DIR=$lin_target CARGO_ENCODED_RUSTFLAGS=$lin_rustflags "$here/linux-build/run.sh" "${nice[@]}" \
      --mount "$src" --mount "$work" --ro "$here" \
      --env KILN_GIT --env SOURCE_DATE_EPOCH --env KILN_OUT --env CARGO_TARGET_DIR --env KILN_SDK_VERSION --env CARGO_ENCODED_RUSTFLAGS \
      --env KILN_NEXUS_ENABLED --env KILN_NEXUS_OAUTH_CLIENT_ID --env KILN_NEXUS_OAUTH_REDIRECT_URI \
      --env KILN_RELEASE_BUILD --env ContinuousIntegrationBuild --env NO_STRIP -- "$@"
  }
  step "Linux build image (Ubuntu 22.04, glibc $glibc_max)"
  image=$("$here/linux-build/image.sh" 2>>"$log") || { tail -40 "$log" >&2; die "the Linux build image did not build"; }
  KILN_LINUX_IMAGE=$(docker image inspect --format '{{.Id}}' "$image")
  export KILN_LINUX_IMAGE
  note "Linux files: built in $image ($KILN_LINUX_IMAGE); cargo target $lin_target"
fi

# ---------------------------------------------------------------- 3. dependencies, command-line tools
step "dependencies (bun, npm)"
(cd "$src/launcher" && quiet low bun install --frozen-lockfile)
(cd "$src/tools/vscode" && quiet low npm ci --no-audit --no-fund)
step "kilnctl and kiln (Linux, Windows)"
(cd "$src" && quiet linux cargo build --release --locked -p kilnctl -p kiln-cli)
win=x86_64-pc-windows-msvc
(cd "$src" && quiet low cargo xwin build --release --locked -p kilnctl -p kiln-cli --target $win)
kilnctl=$lin_target/release/kilnctl

# ---------------------------------------------------------------- 4. runtime
step "runtime: native + managed build, stage"
stage=$work/stage
# the native DLLs name their PDB without a path (/pdbaltpath); the PDBs themselves hold the build machine's paths, so
# they stay out of the release and go to $work/symbols (keep them for crash reports of this build)
CFLAGS="/clang:-ffile-prefix-map=\"$work=/build/work\" /clang:-ffile-prefix-map=\"$HOME=/build\" /clang:-ffile-prefix-map=\"$src=/kiln\"" \
  CXXFLAGS="/clang:-ffile-prefix-map=\"$work=/build/work\" /clang:-ffile-prefix-map=\"$HOME=/build\" /clang:-ffile-prefix-map=\"$src=/kiln\"" LDFLAGS="/pdbaltpath:%_PDB%" \
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
# Enshwick's loader (component enshwick, MIT): its licence and crates come from the pinned checkout it was built from
ew_version=$stage/enshwick/kiln/plugins/enshwick/enshwick-version.txt
[[ -s $ew_version ]] || die "Enshwick loader has no provenance for THIRD-PARTY-NOTICES: $ew_version"
mapfile -t ew_pin < "$ew_version"
[[ ${#ew_pin[@]} == 2 && ${ew_pin[0]} =~ ^[A-Za-z0-9._-]+$ && ${ew_pin[1]} =~ ^[0-9a-f]{40}$ ]] \
  || die "invalid Enshwick loader provenance: $ew_version"
ew_src=$KILN_OUT/runtime-src/enshwick-${ew_pin[0]}
[[ $(git -C "$ew_src" rev-parse HEAD 2>/dev/null) == "${ew_pin[1]}" ]] || die "Enshwick's checkout $ew_src is not the commit the loader was built from"
ew_status=$(git -C "$ew_src" -c core.fsmonitor=false status --porcelain --untracked-files=all --ignored) \
  || die "cannot verify Enshwick's source for notices: $ew_src"
[[ -z $ew_status ]] || die "Enshwick's source changed since the loader build: $ew_src"
nuget_args+=(--enshwick-src "$ew_src")
python3 "$lib/notices.py" --src "$src" --out "$notices" --version "$version" --dotnet-root "$src/.cache/dotnet-win-x64/root" \
  "${nuget_args[@]}" | tee -a "$log"
if [[ -f $stage/eml/kiln/plugins/kiln-eml.dll ]]; then
  printf '\nkiln-eml corresponding source: source/kiln-%s-src.tar.gz, directory gpl/kiln-eml/.\n' "$version" >>"$notices"
fi
if [[ -d $stage/shroudforge/kiln/plugins/shroudforge ]]; then
  # the GPL-3 component's notice; its own licences ship in kiln/plugins/shroudforge/LICENSES
  { printf '\n%s\n' "ShroudForge runtime (component \"shroudforge\", kiln/plugins/shroudforge/)"
    cat "$stage/shroudforge/kiln/plugins/shroudforge/NOTICE.txt"
    for license in "$stage/shroudforge/kiln/plugins/shroudforge/LICENSES/"*; do
      printf '\n--- %s\n' "$(basename "$license")"
      cat "$license"
    done
    printf 'Corresponding source: source/shroudforge-kiln-%s-src.tar.gz in this release, and\nhttps://github.com/frostdev-ops/shroudforge at commit %s.\n' "${sfpin:0:12}" "$sfpin"; } >>"$notices"
fi
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
# Kiln is the universal loader: every compatibility plugin (native/plugins/*, kiln-eml, the ShroudForge runtime,
# Enshwick's loader) must be in the runtime, in a default component for both sides; a missing one stops the release
if ! python3 -I "$lib/plugins.py" check --src "$src" "$runtime_zip" 2>&1 | tee -a "$log"; then
  die "the runtime misses plugins (see above)"
fi
runtime_version=$(basename "$runtime_zip" .zip); runtime_version=${runtime_version#kiln-runtime-}
note "runtime $runtime_version"

# ---------------------------------------------------------------- 5. launcher
resources() { python3 -c 'import json,sys; print(json.dumps({"bundle":{"resources":{sys.argv[1]:"THIRD-PARTY-NOTICES.txt",sys.argv[2]:"LICENSE.txt"}}}))' "$1" "$src/LICENSE"; }
step "launcher: Linux"
# rpm versions can't hold "-": a dev build (X.Y.Z-dev.N) has no rpm
bundles=appimage,deb
[[ $channel == stable ]] && bundles+=,rpm
rm -rf "$lin_target/release/bundle"
if ! (cd "$src" && linux tools/launcher/build-linux.sh --bundles $bundles --runtime "$runtime_zip" --version "$version" \
    --config "$(resources "$notices")" >>"$log" 2>&1); then
  [[ $bundles == *,rpm ]] || die "the Linux launcher build failed (see $log)"
  note "the build with rpm failed; building AppImage and deb only"
  bundles=appimage,deb
  (cd "$src" && quiet linux tools/launcher/build-linux.sh --bundles $bundles --runtime "$runtime_zip" --version "$version" --config "$(resources "$notices")")
fi
lin=$KILN_OUT/bundles/linux
appimage=$(ls -t "$lin"/*.AppImage | head -1)
# the AppImage carries system libraries (GTK, WebKitGTK, ...): add them to the notices, then rebundle the AppImage.
# notices.py runs where the AppImage was built: the owners and licences come from that system's package database.
step "notices for the AppImage's bundled libraries"
rm -rf "$work/appimage-root" && mkdir -p "$work/appimage-root"
unsquashfs -q -n -o "$(python3 "$lib/release.py" offset "$appimage")" -d "$work/appimage-root/r" "$appimage" >/dev/null
(cd "$src" && linux python3 "$lib/notices.py" --src "$src" --out "$notices" \
  --append-appimage --appimage-root "$work/appimage-root/r") | tee -a "$log"
rm -rf "$work/appimage-root"
(cd "$src" && quiet linux tools/launcher/build-linux.sh --bundles appimage --runtime "$runtime_zip" --version "$version" --config "$(resources "$notices")")
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

# the Nexus Mods build: the same launcher, runtime and notices without the self-update. Nexus Mods does not allow
# programs that update themselves, so it has no self-update feature (cargo --no-default-features: no updater plugin, no
# update feeds) and no updater key in its config (launcher/src-tauri/tauri.nexus.conf.json); it is never signed for
# the updater and has no manifest. The bundle folders are emptied first: the direct build's files must not be taken.
step "launcher: Nexus Mods build, Linux (no self-update)"
rm -rf "$lin_target/release/bundle"
(cd "$src" && quiet linux tools/launcher/build-linux.sh --nexus --bundles appimage,deb --runtime "$runtime_zip" --version "$version" \
  --config "$(resources "$notices")")
nexus_appimage=$(ls -t "$KILN_OUT"/bundles/linux-nexus/*.AppImage | head -1)
nexus_deb=$(ls -t "$KILN_OUT"/bundles/linux-nexus/*.deb | head -1)
nexus_setup=""
if ((!skip_windows)); then
  step "launcher: Nexus Mods build, Windows (no self-update)"
  rm -rf "$CARGO_TARGET_DIR/$win/release/bundle"
  (cd "$src" && quiet low tools/launcher/build-windows.sh --nexus "${wargs[@]}")
  nexus_setup=$KILN_OUT/bundles/windows-nexus/kiln-launcher-setup.exe
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
# the server pack: the components kilnctl deploy --bundle installs on a server, as a flat zip a rented host's owner
# unzips into the server folder over FTP (docs/kiln/dedicated-servers.md); deterministic, like the runtime zip
python3 "$lib/serverpack.py" build "$out/runtime/kiln-runtime-$version.zip" --out "$out/runtime/kiln-server-$version.zip" \
  --readme "$here/templates/KILN-SERVER-README.txt" --version "$version" --epoch "$SOURCE_DATE_EPOCH" | tee -a "$log"
# kilnctl's systemd user units (docs/kiln/kilnctl.md "systemd"), next to kilnctl
install -m 644 "$src"/runner/systemd/*.service "$src"/runner/systemd/*.timer "$out/runtime/"
install -m 755 "$lin_target/release/kilnctl" "$out/runtime/kilnctl"
install -m 755 "$lin_target/release/kiln" "$out/sdk/kiln"
if ((!skip_windows)); then
  install -m 644 "$CARGO_TARGET_DIR/$win/release/kilnctl.exe" "$out/runtime/kilnctl.exe"
  install -m 644 "$CARGO_TARGET_DIR/$win/release/kiln.exe" "$out/sdk/kiln.exe"
fi
for f in "$KILN_OUT"/nuget/*."$sdk_version".nupkg; do cp "$f" "$out/sdk/"; done
cp "$vsix" "$out/sdk/"
# the modding site (guide + API reference, DocFX) in sdk/api/, when the export has its build script
if [[ -f $src/tools/docs-site/build.sh ]]; then
  quiet low "$src/tools/docs-site/build.sh" "$out/sdk/api"   # the modding site: guide + API reference (DocFX)
else
  note "no tools/docs-site/build.sh in the export: the API reference (sdk/api/) is left out"
fi
# the SDK's nupkgs as a static NuGet v3 feed in sdk/feed/ (lib/feed.py), for dotnet new install and a mod's
# nuget.config once the folder is served: its index names absolute URLs, by default where publish.sh would put it
# (downloads/<version>/sdk/feed/); publish.sh uploads sdk/feed/ (and sdk/api/) only with --sdk-site
feed_url=${KILN_FEED_URL:-https://frostdev.io/kiln/downloads/$version/sdk/feed/}
python3 -I "$lib/feed.py" build --out "$out/sdk/feed" --base-url "$feed_url" "$out/sdk" | tee -a "$log"
cp "$work"/samples/*.zip "$out/samples/" 2>/dev/null || true
cp "$src/LICENSE" "$src/CREDITS.md" "$notices" "$out/"
cp "$work/source/"*.tar.gz "$out/source/"
# the Nexus Mods build (nexus.py uploads these; never GitHub or frostdev.io)
mkdir -p "$out/nexus/linux"
cp "$nexus_appimage" "$out/nexus/linux/$A" && chmod 755 "$out/nexus/linux/$A"
cp "$nexus_deb" "$out/nexus/linux/$D"
[[ -n $nexus_setup ]] && mkdir -p "$out/nexus/windows" && cp "$nexus_setup" "$out/nexus/windows/$S"

# release notes
if [[ -n $notes ]]; then cp "$notes" "$out/RELEASE-NOTES.md"; else
  {
    echo "# Kiln $version"
    echo
    if ((worktree)); then echo "Test build of the working tree at $KILN_GIT."; else
      # $prev: the previous v* tag, found (after fetching the tags) for the API gate in 1.
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
  (set +x; cd "$src/launcher" && TAURI_SIGNING_PRIVATE_KEY_PASSWORD="$(cat "$pass_file")" \
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
# the Nexus build's README (in its Nexus zips): no self-update; the files' SHA256 in it (the zips carry no SHA256SUMS)
export R_NEXUS_SUMS
R_NEXUS_SUMS=$(cd "$out/nexus" && for f in linux/* windows/*; do [[ -f $f ]] && printf '  %s  %s\n' "$(sha256sum <"$f" | cut -d' ' -f1)" "${f#*/}"; done; true)
fill "$here/templates/README-nexus.txt" "$out/nexus/README.txt"
unset R_NEXUS_SUMS
fill "$here/templates/README.txt" "$out/README.txt.tmp"
# the file list: the downloads, not nexus/ (its own README) or sdk/api/ and sdk/feed/ (built, not published by default)
python3 - "$out" <<'PY'
import os, sys
root = sys.argv[1]
def row(rel, s):
    return f"  {rel:<64} {s / 1048576:8.1f} MB" if s >= 1048576 else f"  {rel:<64} {s / 1024:8.1f} KB"
rows = []
for d, dirs, files in os.walk(root):
    dirs.sort()
    reld = os.path.relpath(d, root).replace(os.sep, "/")
    if reld == "sdk":
        dirs[:] = [n for n in dirs if n not in ("api", "feed")]
    for n in sorted(files):
        rel = os.path.relpath(os.path.join(d, n), root)
        if rel in ("README.txt.tmp", "SHA256SUMS") or rel.startswith("nexus" + os.sep):
            continue
        rows.append(row(rel, os.path.getsize(os.path.join(d, n))))
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

# The API record: what this release added is shipped now, so its Unshipped.txt lines move into Shipped.txt in the
# repository's working tree (not committed: the closing text says so). Only after a release whose checks all ran and
# passed, never for a test build, and only while the working tree's API files are the released commit's.
api_moved=0
if ((worktree)); then
  note "API record: a test build of the working tree; the Unshipped.txt lines stay"
elif ((!checks)) || printf '%s\n' "${results[@]}" | grep -q '^FAIL'; then
  note "API record: not promoted (the checks failed or did not run); after a good release run tools/release/api-gate.sh promote"
else
  step "API record: Unshipped.txt lines into Shipped.txt"
  if "$here/api-gate.sh" promote --if-same-as "$full" 2>&1 | tee -a "$log"; then
    api_moved=$(git -C "$repo" status --porcelain -- ':(glob)managed/**/PublicAPI.*.txt' | wc -l)
  else
    note "API record: not promoted (above); the release itself is fine"
  fi
fi

# Nexus Mods: what would go up (GET calls only); the upload itself is publish.sh --nexus or nexus.py --publish
if ((nexus)) && [[ -f $HOME/.config/kiln-release/nexus.env ]]; then
  step "Nexus Mods (dry run)"
  python3 -I "$here/nexus.py" "$out" --work "$work/nexus" | tee -a "$log" || note "the Nexus dry run failed (the release is fine)"
fi

step "done"
du -sh "$out" | tee -a "$log"
# sdk/api/ and sdk/feed/ (many files each) as one row each in this local build listing only
(cd "$out" && find . -type f -not -path './sdk/api/*' -not -path './sdk/feed/*' | sort | while read -r f; do printf '%10s  %s\n' "$(du -h "$f" | cut -f1)" "${f#./}"; done
  for d in sdk/api sdk/feed; do [[ -d $d ]] && printf '%10s  %s/ (%s files)\n' "$(du -sh "$d" | cut -f1)" "$d" "$(find "$d" -type f | wc -l)"; done; true) | tee -a "$log"
cat <<EOF | tee -a "$log"

Release $version is in $out (log: $log). Nothing is published.
Next (by hand, see docs/kiln/launcher/releasing.md):
  tools/release/github-draft.sh $out        a DRAFT GitHub release with the files (needs the commit pushed)
  tools/release/publish.sh $out             publish: the GitHub release, the dev channel pointer, frostdev.io
  python3 -I tools/release/nexus.py $out    Nexus Mods (dry run; --publish uploads): the nexus/ build, no self-update
EOF
if ((!api_compat)); then
  note "WARNING: the SDK was NOT compared with the last release (KILN_API_BASELINE was not set): see the warning at the start."
fi
if ((api_moved)); then
  cat <<EOF | tee -a "$log"
  The API record: $api_moved PublicAPI file(s) changed in $repo (this release's API moved from Unshipped.txt into
  Shipped.txt). Commit them: git -C "$repo" commit -m "api: ship the API of $version" -- ':(glob)managed/**/PublicAPI.*.txt'
EOF
fi
if printf '%s\n' "${results[@]}" | grep -q '^FAIL'; then exit 1; fi
