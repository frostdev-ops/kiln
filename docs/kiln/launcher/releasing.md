# Releasing Kiln, and the launcher's self-update

A Kiln release is one folder with everything for players, server owners and mod authors, built from one commit.
The launcher in it comes in two builds from the same source: the direct build (frostdev.io, GitHub Releases) updates
itself from frostdev.io, with GitHub Releases as the backup; the Nexus Mods build has no self-update at all ("The two
builds" below).

## The two builds: direct and Nexus Mods
Nexus Mods does not allow programs that update themselves (or download and replace their own files). The launcher on
the Kiln page there (Enshrouded mod 301) is therefore its own build, made by `release.sh` from the same export, with
the same runtime and notices:

| | direct (`linux/`, `windows/`) | Nexus Mods (`nexus/linux/`, `nexus/windows/`) |
|---|---|---|
| Where | frostdev.io, GitHub Releases | Nexus Mods only (`nexus.py`); never GitHub or frostdev.io |
| Cargo feature `self-update` (default) | on | off (`--no-default-features`) |
| `tauri-plugin-updater`, `src/selfupdate/` | compiled in | not compiled in |
| Update feeds (frostdev.io, GitHub `updates-dev` / `latest`) | in the binary | not in the binary |
| Updater key (`plugins.updater` in the config) | `tauri.conf.json` | removed by `tauri.nexus.conf.json` (`"updater": null`) |
| `.sig` files, `latest.json` | yes | none |
| `app_info.distribution`, `--selftest` | `direct` | `nexus` |
| `update_*` commands | the self-update | phase `disabled`; `update_install`/`update_restart` reject `unsupported`; no network |
| UI | the Update word and notice, Settings > Updates, About's Check now, Deck Settings' Launcher updates | none of it: Settings, About and Deck Settings show one line, "New versions: Nexus Mods", a link to the Kiln page (opened in the browser, nothing fetched) |

- **The switch:** `launcher/src-tauri/Cargo.toml` feature `self-update` (`default`); `src/distribution.rs` turns it into
  the distribution (`direct` with it, `nexus` without). `tools/launcher/build-linux.sh --nexus` and
  `build-windows.sh --nexus` build without it (`tauri build ... -- --no-default-features`) and merge
  `src-tauri/tauri.nexus.conf.json` last; their files go to `$KILN_OUT/bundles/{linux,windows}-nexus/`.
- **Nothing else updates the launcher:** the only code that downloads or replaces the launcher is `src/selfupdate/`.
  The other network use, the server modpack download (`kiln_manage::modpack::download`, after the player's explicit
  consent), fetches mods, never the launcher. kilnctl and `kiln` (the SDK CLI) have no self-update: nothing in them
  downloads or replaces their own binary, and neither links an HTTP client (kiln-manage without its `http` feature).
  Their only network use is through other programs: kilnctl's `update` runs SteamCMD for the game server, `kiln build`
  runs `dotnet` (NuGet restore).
- **A Nexus user** gets new versions from the Files tab: the AppImage is replaced, the .deb installed, the setup run
  over the old install (the same app id and data folder as the direct build, so either replaces the other). Its
  README (`nexus/README.txt`, in the Nexus zips) says so and lists the files' SHA256.

## Versions
`VERSION` holds the version being worked on, for example `0.2.0-dev`. Releases take their version from it:

| Release | Version | Made with |
|---|---|---|
| dev | `X.Y.Z-dev.N`, N = a build number that only grows | `tools/release/version.sh dev [N]` (default N: the commit count of HEAD) |
| stable | `X.Y.Z` | `tools/release/version.sh stable` |

Semver orders them as numbers: `0.2.0-dev.9 < 0.2.0-dev.10 < 0.2.0 < 0.2.1-dev.11`. Build metadata (`+...`) is never
part of a release version (the updater ignores it). After a stable release, raise `VERSION` (for example to
`0.2.1-dev`), or the next dev builds sort below the release. A plain build (`tools/launcher/build-*.sh` without
`--version`) is `VERSION` itself, which is older than every dev release of that version.

The runtime keeps its own version, `<VERSION>+<yyyymmdd>.<commit>` (bundle.md). In a release folder its zip is named
after the release: `runtime/kiln-runtime-<release version>.zip`.

## The self-update
Direct builds only (the Nexus Mods build has none, above).
- **Sources, in order:** frostdev.io, then GitHub Releases.

  | Channel | frostdev.io | GitHub |
  |---|---|---|
  | stable | `https://frostdev.io/kiln/updates/stable/latest.json` | `https://github.com/frostdev-ops/kiln/releases/latest/download/latest.json` |
  | dev | `https://frostdev.io/kiln/updates/dev/latest.json` | `https://github.com/frostdev-ops/kiln/releases/download/updates-dev/latest.json` |

  GitHub's "latest" release skips pre-releases, so the dev channel has a fixed pre-release, `updates-dev`, whose
  `latest.json` each dev release replaces. Files: `https://frostdev.io/kiln/downloads/<version>/<path in the release
  folder>` and the GitHub release `v<version>`.
- **Fallback:** each source is tried with its own updater (`launcher/src-tauri/src/selfupdate/`). A check takes the
  first source that answers. A download falls back to GitHub when anything fails on frostdev.io: no manifest
  (network, 404, invalid JSON), the download, the signature, or the sha256 the manifest lists. `launcher.log` says
  which source failed and why; the launcher itself shows only "Update failed" when no source worked.
- **Signatures:** Tauri's updater (minisign, Ed25519). Every file is checked against the public key built into the
  launcher (`plugins.updater.pubkey` in `tauri.conf.json`), and with `requireSignedVersion` the version in the
  signature must equal the version the manifest announces, so an old signed file cannot be offered under a new
  number. A file that fails is never installed, whichever source served it.
- **Channels:** Settings > Updates. Without a choice, a build follows its own channel: a dev build follows dev, a
  stable build follows stable. Stable never offers a pre-release; dev offers both, whichever is newer. Nothing is
  ever downgraded.
- **Checks:** 8 s after the start and every 6 hours while the launcher runs (Settings > Updates > "Check
  automatically", on by default), and Check now in Settings and About. A check is one GET of a small JSON file: no
  ids, no version in the URL, no cookies, nothing else (the request carries the HTTP library's User-Agent). The
  frostdev.io locations keep no access log.
- **Installs:** the AppImage (Linux) is replaced in place, and the new version starts on Restart now. The NSIS install
  (Windows) runs the verified installer on Restart now (passive mode: it shows progress, closes the launcher and opens
  the new one). A .deb or .rpm install, a development build or an unpacked AppImage can't replace itself: the notice
  links to the download page instead. An AppImage in a folder the user can't write to counts as unpacked.
- **Windows temp folder:** the updater writes the installer to `%TEMP%\Kiln Launcher-<version>-updater-<random>\` and
  exits while it runs, so the folder stays. The next start removes folders with exactly that name shape, 20 s after
  the start and only when no update of its own is downloading or waiting; a folder still in use stays for the start
  after (`selfupdate/cleanup.rs`).
- **The notice** (header word Update, Updating, Restart, Updated; the notice under it is one line and its buttons):
  "Kiln <version>" Skip / Update (Download for an install that can't update itself), "Updating…" with a bar,
  "Ready to restart" Later / Restart (held, with the reason as its tooltip, while another task runs).
- **After an update:** `<data>/update.json` remembers it, and the next start shows "Updated to <version>". The
  launcher ships a runtime: when it is newer than the game's, "Update game" installs it into the game (the parts the
  game has), held with "Close the game first" while the game runs. Updating the launcher while the game runs is fine.

### Manifest (`latest.json`)
Tauri's static format, plus fields the updater ignores:
```json
{
  "version": "0.2.0-dev.42",
  "notes": "the first lines of RELEASE-NOTES.md",
  "pub_date": "2026-11-06T12:00:00Z",
  "platforms": {
    "linux-x86_64-appimage": { "url": "…/linux/kiln-launcher_0.2.0-dev.42_amd64.AppImage", "signature": "<.sig>", "sha256": "…", "size": 183500800 },
    "linux-x86_64": { "same as linux-x86_64-appimage" },
    "windows-x86_64-nsis": { "url": "…/windows/kiln-launcher_0.2.0-dev.42_x64-setup.exe", "signature": "…", "sha256": "…", "size": 98566144 },
    "windows-x86_64": { "same as windows-x86_64-nsis" }
  },
  "kiln": { "format": 1, "channel": "dev", "page": "https://frostdev.io/kiln", "runtime": "0.2.0-dev+20261106.0f1ffa6" }
}
```
`tools/release/lib/release.py manifest` writes it; `launcher/src-tauri/src/selfupdate/testdata/latest.json` is the
example the tests read.

## Keys
- **Updater key** (minisign): `~/.config/kiln-release/updater.key`, its password in `updater.key.password` (both
  `chmod 600`, never in a repository, never printed). The public key is `updater.key.pub` and is in
  `tauri.conf.json`. **Back the folder up** (a password manager or an offline copy): without the private key, no
  installed launcher can be updated again; they would need a manual download of a launcher with a new key.
- **Authenticode (optional):** Windows files are signed when `~/.config/kiln-release/authenticode.env` exists (Azure
  Artifact Signing, formerly Trusted Signing, through jsign). Keys:

  | Key | What |
  |---|---|
  | `AZURE_SIGNING_ENDPOINT` | the account's region endpoint, e.g. `https://weu.codesigning.azure.net` |
  | `AZURE_SIGNING_ACCOUNT` | the Artifact Signing account name |
  | `AZURE_SIGNING_PROFILE` | the certificate profile name |
  | `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET` | an Entra app registration with the "Artifact Signing Certificate Profile Signer" role on the profile |
  | `JSIGN` | optional: the path to `jsign.jar` (run with java) when there is no `jsign` command |

  It needs jsign (and osslsigncode for the check). Timestamps come from `http://timestamp.acs.microsoft.com`. Signed:
  the launcher exe (before NSIS packs it, through `bundle.windows.signCommand`), the NSIS uninstaller and setup,
  `kilnctl.exe`, `kiln.exe`, and the runtime's own PE files (`dinput8.dll`, `kiln-core.dll`, services, plugins, Kiln's
  assemblies, the sample and admin mods); never the bundled .NET runtime. Signing happens before the updater
  signatures and SHA256SUMS. Without the file the build prints "Windows files unsigned", and README.txt explains the
  SmartScreen warning.

## The release folder
`tools/release/release.sh VERSION` exports the commit with `git archive` (uncommitted work never gets in), builds
everything (`KILN_RELEASE_NICE=1`: at the lowest priority), signs, writes the manifests and checks the result:
```
kiln-<version>/
  README.txt  RELEASE-NOTES.md  SHA256SUMS  LICENSE  CREDITS.md  THIRD-PARTY-NOTICES.txt
  linux/     kiln-launcher_<v>_amd64.AppImage (+ .sig), kiln-launcher_<v>_amd64.deb, kiln-launcher-<v>.x86_64.rpm
  windows/   kiln-launcher_<v>_x64-setup.exe (+ .sig)
  runtime/   kiln-runtime-<v>.zip, kilnctl, kilnctl.exe, INSTALL-RUNTIME.txt
  sdk/       Kiln.Api, Kiln.Sdk, Kiln.Templates .<v>.nupkg (the release's version: NuGet never re-fetches a version), kiln, kiln.exe, kiln-vscode-<x>.vsix
  samples/   <mod>-<version>.zip for each sample that builds
  updates/   frostdev.io/<channel>/latest.json, github/latest.json
  source/    kiln-<v>-src.tar.gz
  nexus/     the Nexus Mods build (no self-update; only nexus.py uploads it): README.txt,
             linux/kiln-launcher_<v>_amd64.AppImage, linux/kiln-launcher_<v>_amd64.deb, windows/kiln-launcher_<v>_x64-setup.exe
```
SHA256SUMS lists every file, nexus/ included; README.txt's file list leaves nexus/ out (it describes what GitHub and
frostdev.io get). The Nexus build is never signed for the updater and has no manifest.
THIRD-PARTY-NOTICES.txt comes from the build: the Rust crates of the launcher, kilnctl and kiln (cargo metadata, both
platforms), the npm production dependencies of the launcher and the VS Code extension, the NuGet packages of the
shipped projects, `native/third_party`, the fonts, `licenses/`, the .NET runtime's notices, and the system libraries
inside the AppImage (owners and licences from the system's package manager). A copy is in the launcher packages
(resources) and in the runtime zip (`kiln/THIRD-PARTY-NOTICES.txt`).

The checks (all run; the script fails at the end if one failed): SHA256SUMS; the updater signatures against the
launcher's built-in key and version; the AppImage's `--selftest` on a private X display; the NSIS setup installed
silently under Wine into a scratch prefix, then `--selftest` (both also for the Nexus build, in its own folders and
prefix; each report must name its distribution, `direct` or `nexus`); `release.py updater --absent` on the Nexus
build: its AppImage (unpacked), its deb, the exe inside its setup (7z) and the exe the setup installed under Wine
hold none of the update feeds, the updater key (base64 and decoded) or the updater plugin (`tauri-plugin-updater` in
the launcher binary), and nexus/ holds no `.sig`; `release.py updater --present` on the direct build (the feeds, the
key and the plugin are in its binaries); the deb's control fields and files (both builds); the rpm's files;
`kilnctl bundle verify`; `kilnctl`/`kiln --version`, the Windows builds under Wine; every Linux ELF file (the
AppImage unpacked with `--appimage-extract`, the deb's and rpm's files, kilnctl, kiln, any inside the zips) needing no
glibc symbol version above 2.35 (`objdump -T -p`; `release.py glibc`, see "Linux builds"); kilnctl, kiln and the
AppImage started on a clean Ubuntu 22.04 in a throwaway container (`start-test.sh`: `--selftest`, then the launcher's
window on Xvfb, a screenshot in `<work>/checks/start-ubuntu22/screen.png`); a mod made with `dotnet new
kiln-mod` from the packed templates, built against the packed SDK from the folder (a scratch NuGet cache); the vsix's
files; the Authenticode signatures (when signed); and a scan inside every file and archive (AppImage, deb, rpm,
setup, zips, nupkgs, vsix, tarball) for game data, home paths, keys, the key's password, credentials and tokens.

Release builds keep build paths out of the binaries (`--remap-path-prefix` for Rust, `-ffile-prefix-map` and
`/pdbaltpath` for the native DLLs, `ContinuousIntegrationBuild` for .NET, `KILN_RELEASE_BUILD` for the "tree this
was built from" defaults of kilnctl and kiln). The native PDBs hold the build machine's paths, so the release runtime
ships without them; the script keeps them in `<work>/symbols/` for crash reports of that build. A dev release has no
rpm: rpm versions can't hold the `-` of `X.Y.Z-dev.N`.

Builds reuse `<repo>/.out/release-work` (`--work`): a fixed export path keeps the compiler caches warm. Pinned
downloads (the .NET runtime, netcoredbg) are cached in `~/.cache/kiln-release`.

## Linux builds (the Ubuntu 22.04 container)
Every Linux ELF file of a release (the launcher's AppImage, deb and rpm, `kilnctl`, `kiln`) is compiled and bundled
in a Docker container, Ubuntu 22.04 with glibc 2.35 (`tools/release/linux-build/`). Why: a Linux binary needs the
glibc it was linked against or newer, and the AppImage also carries the build system's own libraries (WebKitGTK,
GTK, sqlite, ...). Built on a rolling distro, everything needs that distro's brand-new glibc: 0.2.0-dev.13, built on
Arch with glibc 2.44, failed on a Steam Deck with ``/usr/lib/libm.so.6: version `GLIBC_2.44' not found (required by
.../usr/lib/libsqlite3.so.0)``, and its AppImage, deb, kilnctl and kiln needed 2.44, 2.39, 2.39 and 2.39. Ubuntu
22.04 is the oldest base with WebKitGTK 4.1 (which Tauri 2 needs) and Tauri's usual AppImage baseline; builds from it
run on SteamOS, Ubuntu 22.04+, Debian 12+, Fedora 36+ and Arch. The release check `release.py glibc` fails any
Linux file that needs a newer symbol version.

- **What runs where:** in the container: `cargo build` of kilnctl and kiln, `tools/launcher/build-linux.sh` (the
  Tauri CLI under bun, the web build, cargo, linuxdeploy with `APPIMAGE_EXTRACT_AND_RUN=1`, so no FUSE), and
  `notices.py` for the AppImage's libraries (their owners and licences come from the container's dpkg database).
  On the host: everything else, Windows included (cargo-xwin, NSIS, the runtime DLLs), the updater signatures and the
  checks (the start test in its own throwaway container). The updater key never enters the container: release.sh signs the finished AppImage on the host, as before.
- **The container:** `tools/release/linux-build/run.sh` runs a command in it as the calling user (files stay yours),
  with `--cap-drop ALL`, the export, the work folder and `tools/release` mounted at their own paths (tools/release
  read-only), and nothing else of the host: no `~/.cargo`, `~/.config` or keys. Its HOME, CARGO_HOME and cargo
  target dir live in `KILN_GLIBC_DIR` (default `<repo>/.out/glibc`: `home/` holds Tauri's downloaded AppImage tools,
  `cargo-home/` the crates, `cargo/` the Linux build), separate from the host's own cargo builds, so later runs are
  incremental. It needs ~3 GB, the image ~2.4 GB of Docker's disk; on the build machine `.out/glibc` is a link to `/mnt/Dongus/kiln-glibc`, off the root
  disk. A shell in it: `tools/release/linux-build/run.sh --mount "$PWD" -- bash`.
- **The image:** `tools/release/linux-build/Dockerfile`: `ubuntu:22.04` pinned by digest, Tauri's Linux packages,
  Rust (rustup-init checked by sha256, one pinned toolchain) and bun (checked by sha256), at the host's versions.
  `tools/release/linux-build/image.sh` prints its tag, `kiln-linux-build:<first 12 hex of the Dockerfile's sha256>`,
  and builds it when missing (a few minutes; release.sh does this itself). Editing the Dockerfile makes a new tag, and
  the older images are removed.
  - **Rebuild it** (`tools/release/linux-build/image.sh --rebuild`, no layer cache) now and then, and before a stable
    release: the apt packages are Ubuntu's as of the image's build, and the AppImage ships its WebKitGTK.
  - **Raise Rust or bun:** change `RUST_VERSION` (or `RUSTUP_VERSION` + `RUSTUP_SHA256`, from
    `https://static.rust-lang.org/rustup/archive/<v>/x86_64-unknown-linux-gnu/rustup-init.sha256`), or `BUN_VERSION`
    + `BUN_SHA256` (the release's `SHASUMS256.txt`). **Move the base** (a newer `ubuntu:22.04` build):
    `docker pull ubuntu:22.04 && docker inspect --format '{{index .RepoDigests 0}}' ubuntu:22.04` and put the digest in
    `FROM`. A newer Ubuntu raises the glibc floor: change `glibc_max` in release.sh with it, deliberately.
- **`--host-build`** (`release.sh VERSION --host-build`): builds the Linux files on the host instead, with no Docker,
  for quick local tests of the release script. Such a build needs the host's glibc; the glibc check fails for it and
  the folder must never be published.
- **Checking a build by hand:** `python3 tools/release/lib/release.py glibc <release folder>` prints the newest glibc
  version each Linux file needs. `tools/release/linux-build/start-test.sh <release folder> [--distro ubuntu22|arch2024]`
  starts it on a clean older system in a throwaway container: `ubuntu22` is `ubuntu:22.04` (glibc 2.35, the floor),
  `arch2024` an Arch Linux of 2024-01-01 (glibc 2.38, `/usr/lib` like SteamOS). It runs kilnctl and kiln
  `--version` on the bare image, unpacks the AppImage with `--appimage-extract`, lists the libraries it expects from
  the system (X11, xcb, EGL/GLX, gbm, drm, wayland-client, fontconfig, freetype, harfbuzz, fribidi, expat: what every
  desktop has and AppImages leave out), installs a minimal desktop (Xvfb, Mesa, fontconfig, a font, dbus), runs
  `--selftest` as a normal user, then the launcher itself on the Xvfb display, and saves a screenshot (`screen.png`),
  the window tree and the launcher's log in `--out` (default `./start-test-<distro>`). It needs network (apt/pacman).
  release.sh runs it for `ubuntu22` as a check. Rendering is Mesa's llvmpipe, named with `GALLIUM_DRIVER` (with no
  GPU, the Mesa of 2024-01 tries zink, which fails, and its Xvfb and GL clients crash: a container artefact).

## Steps
One-time setup (frostdev.io):
1. On the VPS: `sudo mkdir -p /opt/frostdev-downloads/kiln/{updates,downloads}` and give the deploy user write access.
2. Install the `/kiln/updates/` and `/kiln/downloads/` locations from the site repository's
   `ops/public-site.nginx.conf` into the frostdev.io server block, then `nginx -t` and reload.
3. The Kiln page says "no downloads until it is": change it to link `https://frostdev.io/kiln/downloads/<version>/`
   (or the GitHub release) when you publish; that is a site change and deploy of its own.

Each release:
1. Commit and push the release commit (`main`). For a stable release, `VERSION` is `X.Y.Z-dev`.
2. `V=$(tools/release/version.sh dev)` (or `stable`).
3. `tools/release/release.sh $V` → `~/Kiln-release/kiln-$V/`. All checks must say PASS. Read README.txt and
   RELEASE-NOTES.md (or pass `--notes FILE`), try the AppImage and the setup.
4. `tools/release/github-draft.sh ~/Kiln-release/kiln-$V` → a draft release `v$V` with the files (flat names, its own
   SHA256SUMS). Check it on GitHub.
5. `tools/release/publish.sh ~/Kiln-release/kiln-$V --host <the web server's ssh name>`: uploads to frostdev.io (files first, then the manifests),
   publishes the draft, points `updates-dev` at it. Launchers on that channel see it at their next check.
6. After a stable release: raise `VERSION` to the next `-dev` version.

7. Nexus Mods (optional): `python3 -I tools/release/nexus.py ~/Kiln-release/kiln-$V` shows what would go up (a dry
   run, GET calls only; release.sh runs it at the end when the key is there), then add `--publish` to upload. It
   uploads the Nexus build (nexus/), never the direct one.

github-draft.sh stages only linux/ and windows/ (the direct build); publish.sh uploads the folder to frostdev.io
without nexus/, with a SHA256SUMS that lists what goes up.

A hotfix stable release while a newer dev build is out: `publish.sh --no-dev-channel`, so dev users keep the newer
build. A bad release: publish a fixed, higher version; launchers never go back by themselves.

## Nexus Mods
`tools/release/nexus.py RELEASE_DIR` puts a release on the Kiln page (Enshrouded, mod 301; API v3, stdlib Python,
run it with `python3 -I`). Without `--publish` it is a dry run: it reads the page and its files and prints every
upload, file, version and changelog it would make. With `--publish` it does them. It never changes the page's
publishing status.

| File (kind) | Category | Name on Nexus | In the zip |
|---|---|---|---|
| `launcher-windows` | main | Kiln Launcher (Windows) | the Nexus build's NSIS setup (`nexus/windows/`), `nexus/README.txt` |
| `launcher-linux` | main | Kiln Launcher (Linux) | the Nexus build's AppImage (executable bit kept), `nexus/README.txt` |
| `launcher-deb` | optional | Kiln Launcher (Linux .deb) | the Nexus build's .deb, `nexus/README.txt` (the page description offers it) |
| `runtime` | optional | Kiln runtime (manual install) | `kiln-runtime-<v>.zip`, kilnctl, kilnctl.exe, INSTALL-RUNTIME.txt |
| `sdk` | optional | Kiln SDK (mod authors) | the nupkgs, kiln, kiln.exe, the vsix, `nexus/README.txt` |

- **The launchers are always the Nexus build** (no self-update; "The two builds"). A release folder without nexus/ is
  refused for the launcher and SDK kinds. The zips carry `nexus/README.txt` (no self-update; new versions from the
  Files tab), never the folder's README.txt, which describes the self-update.
- **Older versions:** a launcher's new version is created with `archive_existing_file: true`, so the version it
  replaces moves to the archived files (the runtime and SDK keep the default, false). The API has nothing else for
  older versions: no endpoint changes a version's category, hides or deletes it (`PUT /mod-files/{id}` only renames
  a file, `/mod-file-versions/move` and `move-to-new-mod-file` only reorder or regroup versions, quarantine is for
  Nexus moderation services). Versions uploaded before the Nexus build (0.2.0-dev.10 to dev.14 had the self-update)
  are archived or deleted by hand on the page (Files > Manage).

- Nexus takes archives (the API speaks of "the archive being uploaded" and repacks archives), so every file goes up
  as a zip, `kiln-<kind>-<version>.zip` (the runtime: `kiln-runtime-standalone-<version>.zip`), made in `RELEASE_DIR.nexus/`.
- Uploads up to 100 MiB go in one part with their MD5 (`md5` in `POST /uploads`, `Content-MD5` on the `PUT`); larger
  ones use `POST /uploads/multipart` (S3 parts, their ETags, `CompleteMultipartUpload`). Then `finalise`, and the
  upload is polled until it is `available`.
- The first release creates the files (`POST /mod-files`); later releases add versions (`POST /mod-files/{id}/versions`).
  The file ids are kept in `~/.config/kiln-release/nexus-state.json`; without it, a file of the same name on the page
  is reused. The changelog goes to `POST /mods/{id}/changelogs` (the release notes). A version is uploaded once.
- The main files carry `update_mod_version`, so the page's version field (it says "1.0" until the first upload)
  becomes the release version. The API can't set that field on its own.
- `--sync-description` compares the page's description with `docs/kiln/launcher/nexus-description.bbcode` (the site
  stores line breaks as `<br />`; those are ignored); with `--publish` it replaces it (`PATCH /mods/{id}`, an
  experimental endpoint).
- The key: `NEXUS_API_KEY` in `~/.config/kiln-release/nexus.env` (chmod 600). It is sent only to the API host (over
  https), never to the storage URLs the uploads go to, and never printed or written.
- `--only launcher-linux,runtime` limits the files. Tests: `python3 -I tools/release/test_nexus.py` (a local mock of the
  API and storage: single part, multipart, polling, new file and new version, a file found by name, the description,
  the dry run, a missing key; the launchers taken from nexus/ with its README, `archive_existing_file` on the
  launchers' new versions only, a folder without nexus/ refused).

## Testing the self-update
- The Nexus build: `cargo test -p kiln-launcher --no-default-features` (no selfupdate module; the `update_*` commands
  answer `disabled` and refuse to install), `bun x playwright test tests/nexus.spec.ts` (browser mock `?dist=nexus`:
  no update word, notice, settings or Check now in the desktop and Deck UI, no `update_*` call, the "New versions:
  Nexus Mods" link in Settings, About and Deck Settings; the direct build unchanged), and the release checks above.
- `cargo test -p kiln-launcher selfupdate`: source order and fallback, manifest parsing, versions and channels, bad and
  wrong-version signatures, sha256, install kinds (.deb is manual), against local HTTP servers with the real updater
  plugin on a mock app and a throwaway key (`selfupdate/testdata/`).
- `bun x playwright test tests/update.spec.ts` (launcher/, browser mock): the notice, skip, a .deb install, the game
  running, a failed download, Settings and About. `window.__kilnUpdateMock` drives it (`src/api/mockUpdate.ts`).
- `tools/release/e2e-update.sh`: two real AppImages (`0.2.0-dev.1001`, `0.2.0-dev.1002`) built with the QA features
  `dev-eval,update-test-feeds`, signed with a key made for the test, served by two local HTTP servers; scenarios:
  frostdev.io serves it, frostdev.io is down, frostdev.io serves a changed file, both serve a changed file. Each runs
  on its own X display with its own HOME, data dir and dbus session.
- Test feeds: debug builds and builds with the `update-test-feeds` feature read `KILN_UPDATE_FEEDS`
  (`name=url;name=url`, `{channel}` in the URL) and take it only when every URL is on the loopback interface. Release
  builds never read it. Plain http needs `plugins.updater.dangerousInsecureTransportProtocol` in a QA build's config.
