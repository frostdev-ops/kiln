# Kiln Launcher app

The desktop app in `launcher/`: React 18 + TypeScript + Vite (bun) in a Tauri 2 shell (`launcher/src-tauri`, crate
`kiln-launcher`) on `crates/kiln-manage`. Contracts: [contracts.md](contracts.md) (formats, section 9 commands, section 10
events). Design system: [design-system.md](design-system.md).

**Official 0.2.0-dev.20:** Catalog, Nexus account/settings and `nxm://` handling are disabled while OAuth approval is
pending. Browser downloads and manual Library import remain available. Nexus routes below describe the enabled
implementation for a future approved build; no personal API key flow is supported. See [catalog.md](catalog.md).

## Layout
| Path | What |
|---|---|
| `src/main.tsx`, `src/App.tsx` | entry, routes (HashRouter). Server/admin pages load lazily; optional server slots via `import.meta.glob` (below) |
| `src/shell/` | AppShell (the stage, the header with nav, tasks and the game's state word, the Play page in front of the stage, the sheet for every other page, the mouth: the Play button and pilot light; toasts, splash, route error boundary), `play.ts` (the Play control shared by the mouth and the Play page), `firstRun.tsx` (the automatic game detection, Install Kiln on Play), `drop.tsx` (a mod dropped anywhere on the window, the dropped-modpack consent), `runtimeChange.ts` (the runtime install task shared by Runtime and Play), `gameState.ts`, `nav.ts`, `serverNav.ts` (a server's grouped list) |
| `src/ui/` | the design system |
| `src/components/` | `ConfigEditor`, `SaveBackups` (shared with server pages) |
| `src/pages/` | client pages (Play, Profiles + tabs, Library, Catalog, Runtime, Logs, Saves, Editor, Settings, About) |
| `src/api/commands.ts` | typed wrappers for every command + `KilnEvents` (contract) |
| `src/api/hooks.ts` | TanStack Query hooks, query keys, the selected game (zustand), `useBackendEvents` |
| `src/api/mock.ts` | in-memory backend for the browser (stateful: profiles, library, tasks, a fake running game) |
| `src/api/fallback.ts` | dev aid: read-only `not_implemented` commands answer from the mock inside Tauri (Mock flag in the header) |
| `src/editor/` | the vendored Save Editor ([UPSTREAM.md](../../../launcher/src/editor/UPSTREAM.md)) |
| `src/data/kiln-json.snapshot.json` | schema snapshot of Kiln's own `kiln/kiln.json` (core + Kiln.Host `mods` section) |
| `src/assets/fonts/` | Grenze Gotisch, Alegreya Sans, Alegreya Sans SC, Inconsolata (woff2 subsets) + OFL licences |
| `src-tauri/src/commands/` | one file per area; `app.rs` also holds the task runner `spawn_task` |
| `src-tauri/src/state.rs` | data dirs, settings, game lookup, Steam roots (+ settings' extra folders), game status |
| `src-tauri/src/watch.rs` | the watcher thread: `kiln://game`, `kiln://last-server`, `kiln://log` |
| `src-tauri/src/assets.rs` | the `kiln-asset` protocol (editor game assets from a local folder) |
| `src-tauri/src/selftest.rs` | `--selftest` |
| `tests/` | Playwright smoke tests (mock mode) |
| `scripts/` | `gen-tokens.ts`, `check-schemas.ts`, `scope-editor-css.ts` |
| `tools/launcher/` | `build-linux.sh`, `build-windows.sh`, `fetch-editor-assets.sh` |

## Pages
| Route | Page | Backend |
|---|---|---|
| `/` | Play, in front of the stage. First start ("The first run" below): looking for the game by itself, then Install Kiln right here. Then: the active profile's name (opens the profile list), one live sentence (what loads), "Just once": vanilla (warns first in the arch: Kiln gear can crash), without mods, another profile; boot stages while it starts; four facts (Kiln with its build date and "update ready/failed", proxy, runs on, last boot: boot.json's errors, the mods that failed in mods.json/native.json and the `[E` lines of kiln.log, all of the same run, linking to Logs); the game folder with Open, Detect, Add. Banners above the name: refusal (`last-server.json`) with the modpack offer, Steam launch-option advice, Steam updating, "Update Kiln" (`GameStatus.newerMods`: the active profile's mods whose `kilnBuild` is newer than the game's runtime: "<ids> needs a newer Kiln." with Update, which opens Runtime), safe idle. The mouth (shell) is the Play button | `game_*`, `launch_play`, `launch_state`, `profile_active/activate/deactivate`, `profile_resolve`, `modpack_pending/dismiss` |
| `/profiles` | list, create, duplicate, delete (typed), activate | `profile_*` |
| `/profiles/:id/mods` | the Kiln mods as the resolver's load order (client or server side): numerals, flame toggles, why a mod stays out; add from the library, other versions, remove | `profile_save`, `profile_resolve`, `library_list` |
| `/profiles/:id/native` | native mods grouped by phase (dllmain/early/boot/world), drag-reorder inside a phase, plugins | same |
| `/profiles/:id/emf`, `/configs`, `/saves` | EMF mods; the profile's config folder (SchemaForm/raw); backups made with this profile | `config_*`, `saves_*` |
| `/profiles/:id/engines` | other engines' mods ([engines.md](../engines.md)): one card per engine with mods in the library or the profile (EML, ShroudForge, SET, Greeneye, Flame, Shroudtopia; reserved kinds only when present), the same flame switch, versions, add and remove as Kiln mods; Settings for ShroudForge (`extended.mod.json`) and Shroudtopia (`shroudtopia.json`) mods on the SchemaForm (one form per `x-kiln-group`); EML's "Restore vanilla game data" offer (below) | `profile_save`, `engine_settings_*`, `eml_backup`, `eml_restore` |
| `/library` | drop zip/folder/DLL: as a drop anywhere (into the active profile; below), or with Settings "Add new mods to the active profile" off, detection → import task; versions; remove (typed, force when used; refused while a game or server runs, `force` or not); clean up unused (never a version a game's `profile.json` still lists) | `library_*` |
| `/catalog` | Enshrouded's mods on Nexus Mods ([catalog.md](catalog.md)): Kiln mods (default), All Enshrouded, Installed; search, sort; cards with endorsements, downloads, what the mod is and its state (Installed, Update <version>); an updates banner; `?mod=<id>` opens a mod's details (pictures, description, requirements, files, Add / Update / Download on Nexus Mods, Open on Nexus Mods). Mod Manager Download links are taken anywhere in the app (`shell/catalogInstall.tsx`, `NxmListener`) | `nexus_*` |
| `/runtime` | install/update/rollback per bundle (bundles of one version and day are ordered by when they were built, never by the git id), uninstall (typed), proxy chain (receipt + live `boot.json` chain), switch from ShroudForge (typed), add bundle | `runtime_*` |
| `/logs` | kiln.log, game log, proxy.log, mod logs (follow, filter), crash reports, tick stats (sparklines); Engines: one line per engine plugin that wrote `kiln/state/<engine>.json` or has mods in the active profile (EML `phase`, ShroudForge `mode`, `reason`, each mod's state and reason), plus the restore offer | `logs_*`, `engine_status`, `eml_backup`, `eml_restore` |
| `/saves` | save files, backups, manual backup, restore (refused while running; Steam Cloud hint), delete | `saves_*` |
| `/editor` | the Save Editor (lazy chunk) | `editor_*`, `kiln-asset://` |
| `/settings` | launcher settings ("Add new mods to the active profile", default on), Appearance ("Animated fire": off draws the plain Play button), games (detect/add/forget), Nexus Mods in enabled builds only (OAuth account sign-in/sign-out and "Mod Manager Download links"; `components/NexusSettings.tsx`), Kiln's `kiln.json` via SchemaForm | `app_settings_*`, `game_*`, `config_kiln_*`, `nexus_account`, `nexus_sign_in`, `nexus_sign_out`, `nexus_handler_*` |
| `/about` | versions, CREDITS.md, "Theme and design: frostdev"; Updates (the state, Check now), in the Nexus Mods build "New versions: Nexus Mods" | `app_info` |
| `/servers` | every server, one line each (or a redirect to the first server when there is no `ServersPage.tsx`) | `server_list` |

"Restore vanilla game data" (`components/EngineStatus.tsx`): offered on Logs > Engines and the profile's Engines tab when
`eml_backup` reports a patched `.kfc` and the game's active profile has no EML mod switched on (or no profile is active),
so nothing would patch the data again. A confirm dialog, then `eml_restore` (refused while the game runs). The Library
labels every kind with its engine (badge, "Other engines" filter with an Engine select); the Deck's Mods lists engine
mods after Kiln's own with their engine ("ShroudForge mod"). Kind names and lists: `src/api/kinds.ts`.

## The first run and dropping a mod
For a player who wants one mod: install the launcher, it finds the game and offers Install Kiln, drop the mod on the
window, Play. Library and Profiles stay as they are for everyone else.
- **Finding the game** (`shell/firstRun.tsx`, `useAutoDetect`): when no client is known, the shell runs `game_detect`
  once per start by itself, also when the initial game list fails. Play says "Looking for Enshrouded in your Steam libraries." (the mouth: "Looking"), then
  either the game, or "Kiln did not find Enshrouded…" with Add a folder and Look again.
- **Install Kiln on Play**: the game is there but Kiln is not: the mouth is lit and says Install; it (and the Install
  Kiln link) opens one confirm (`InstallKilnModal`: the version, Install), which runs the Runtime page's install task
  (`shell/runtimeChange.ts`: the newest client bundle, default parts) with its cones in place of the links. When it is
  done, `profile_ensure_default` makes "My mods" and activates it when there is no profile at all, and Play is ready
  ("No mods yet. Drop one anywhere on this window."). ShroudForge's switch stays on Runtime (typed).
- **Drop a mod anywhere** (`shell/drop.tsx`): files dragged over the window (Tauri's drag-drop events; HTML5 drag
  events in the browser mock) show the niche over a dimmed window, "Drop to add", "Into <profile>". The drop runs
  `library_add` (import, add to the active profile, write profile.json; contracts.md section 7 "Add a mod"). The toast
  is the mod's name and Undo (`profile_revert`; the library copy stays), "Needs <ids>." when requirements are missing,
  "Applies at the next start." while the game runs. Before Kiln is installed: "Install Kiln first" with Install Kiln
  (the dropped files are added once the install is done). With profiles but none active: imported only, "No profile is
  active." with Profiles. Modpack zips open the consent dialog (`ModpackDropOffer`: tick the box, then
  `modpack_install` with `{path}`). A drop on a DropZone (Library's niche, Runtime's bundle row) or while a dialog is
  open is not the window's; a covered or busy DropZone takes no drops. Overlapping identical drops run once; other
  window drops run in order. Settings "Add new mods to the active profile" off: ordinary mods are only imported;
  modpack zips still ask for consent, one dialog per zip, tied to the game selected when dropped. A deferred drop
  before installation stays tied to that game, even if the selection changes during installation. Failures preparing
  the default profile are shown; a failed install offers Retry for the waiting drop.
- Browser mock: `?mock=fresh` starts as a first run (no game known, Detect finds the Steam one, no Kiln, no profiles,
  an empty library), `?mock=empty` finds nothing. `tests/firstrun.spec.ts` covers the flow (screenshots in
  `.out/simple/shots/`).

Optional slots for server pages (picked up by `import.meta.glob`, no route edit needed):
`src/pages/servers/ServersPage.tsx` (the `/servers` index), `ServerLayout.tsx` (wraps `/servers/:id/*`),
`ModpackOffer.tsx` (props `{gameId, lastServer, open, onClose}`; Play opens it from the refusal banner).

## The catalog and Mod Manager Download
[catalog.md](catalog.md): which mods count as Kiln mods, Nexus's rules for apps (headers, the key, rate limits, free
downloads through the site), adding with Undo, updates on a click, the opt-in nxm handler (`--nxm <link>`, the link
inbox, other games forwarded), tests and the real-API checklist. Mod authors: [catalog-authors.md](catalog-authors.md).
`tauri.conf.json`'s CSP allows images from `staticdelivery.nexusmods.com` and `images.nexusmods.com` (mod pictures).

## Commands beyond the contract
- `config_kiln_read(gameId)`, `config_kiln_set(gameId, path, value)`, `config_kiln_reset(gameId, path)` →
  `ConfigDocument` for `<game>/kiln/kiln.json` (JSONC value-token edits, comments kept; validated with
  `kiln_manage::config::check_value` against the built-in snapshot).
- `app_assets_status()` → `AssetsStatus {root, editor: {present, bytes, source, build, gameBuild, current, ...},
  admin: {map, catalog, bytes, ...}}`; `app_assets_import(kind: "editor" | "admin", from)` → TaskId (result
  AssetsStatus): `from` null extracts the art from the user's installed client ([gameart.md](gameart.md));
  a folder copies it from there (Save Editor `build-assets/game`, or `web/ext/map` + `data`) into
  `<data>/{editor,admin}-assets/`. Never downloads. Settings → "Game art", the editor's banner.

## Local game assets (`kiln-asset` protocol)
`kiln-asset://localhost/<prefix>/<rel>` (Windows `http://kiln-asset.localhost/…`), `src-tauri/src/assets.rs`:
`editor/` (roots `$KILN_EDITOR_ASSETS`, `<data>/editor-assets/`, debug `launcher/assets-local/editor/`) and `admin/`
(the admin map and catalogs: `$KILN_ADMIN_ASSETS`, `<data>/admin-assets/`, debug `launcher/assets-local/`).
WebKitGTK does not load custom-scheme images into a page from `http://localhost` (`tauri dev`), so dev builds read
`/assets-local/…` from the vite dev server; release builds use the protocol (checked on Linux: the editor's fonts and
textures load).

## Tasks and events
- `spawn_task(app, kind, title, work)` (commands/app.rs): runs `work(&mut dyn Progress)` on the blocking pool; emits the
  whole `Task` as `kiln://task` at start, per report (throttled ~80 ms unless the message changes) and at the end
  (`done` + `result`, `failed` + `error`, `canceled` for `ErrorCode::Canceled`; a panic is `internal`).
  `task_cancel` makes `canceled()` true; `task_list` keeps the last 50 finished. Every task's start and outcome (with
  the error code, message and last step) are also appended to `<data>/logs/launcher.log`, which outlives the process.
- The active profile follows its edits: Play (`launch::play_with`), `profile_save` and the watcher (an edit outside the
  launcher, such as `kilnctl profile add`, while the game is not running) write the game's `kiln/profile.json` again
  when the profile renders differently from it (`profile::refresh_active`), so Steam's own Play button starts it too.
- `launch_state.log`: the `[E` lines of kiln.log when kiln.log is the run that wrote boot.json (its start line names
  boot.json's pid, and boot.json is newer than kiln.prev.log); read incrementally.
- Runtime installs end with a check of the game folder: the receipt must name the bundle's version and parts and every
  listed file must have its bytes, else the task fails. Parts the game's side cannot take are refused before the
  bundle's verify. The page keeps the last change and its outcome until dismissed; Play shows a failed one. When the
  install replaced Kiln files changed by hand (`changedBackedUp` in its result), its toast adds "N changed files kept
  in backups".
- Frontend: `useTaskFeed()` (shell) fills the store; `runTask(() => cmd(...), {done, detail})` awaits a task's result
  (`detail(result)`: the success toast's second line, or none).
- The running clock (the mouth and the header word) counts from `GameStatus.started`, the game process's own start, so
  a game started from Steam shows its real time. Without one it counts from this launcher's launch when that was just
  now, else from boot.json, else from when the launcher first saw the game; a stopped game ends the launch's record.
- Watcher (1 s): process start/stop (`proc::find_game`), mtimes of `state/{boot,mods,native,last-server}.json`;
  computes the full status only when something changed. Log follows poll in the same loop.

## Running it
```sh
cd launcher
bun install
bun run dev                         # browser mock on http://localhost:1420
bun x tauri dev                     # the desktop app (debug), same dev server
bun x tsc --noEmit && bun run build # typecheck + production web build (tsc covers every page)
bun run check:schemas
bun run test:smoke                  # Playwright, mock mode; screenshots to .out/launcher/shots/ (KILN_DEV_PORT: another port)
bun x playwright test tests/catalog.spec.ts   # the catalog, desktop and Deck (?nexus=free|premium|nokey)
CARGO_TARGET_DIR=.out/launcher/cargo cargo test -p kiln-launcher
CARGO_TARGET_DIR=.out/launcher/cargo cargo test -p kiln-launcher --no-default-features   # the Nexus Mods build (no self-update)
tools/launcher/fetch-editor-assets.sh   # dev: local editor game assets for the vite dev server (gitignored)
```
Test runs against game copies: `KILN_DATA_DIR=<scratch>` and `KILN_STEAM_ROOTS=<fake Steam root>` keep the app away
from the real launcher data and Steam libraries, and `KILN_LAUNCH_DRY_RUN=1` makes `launch_play` do everything but open
`steam://` (which would start the real game). `KILN_LAUNCHER_NO_WEBGL=1` forces the fire's no-WebGL fallback (the plain
Play button) for one start, without touching the saved "Animated fire" setting. GUI and Wine tests run on a private Xvfb display, never the desktop's. Debug builds only: `KILN_DEV_EVAL_DIR=<dir>` runs each `*.js`
dropped there in the webview (headless checks on Xvfb drive the real app with it).

## Build prerequisites
- bun, Rust (stable), the WebKitGTK 4.1 / GTK 3 dev packages (Linux), `cargo-xwin` + clang/lld and `makensis`
  (Windows cross build), Wine (only for the Windows selftest).
- **The first bundle build downloads Tauri's own bundler tools** from tauri-apps' official binary releases into
  `~/.cache/tauri`: linuxdeploy and its AppImage/GTK plugins (AppImage), the NSIS bundle (Windows). Later builds reuse the cache. `cargo-xwin` downloads the MSVC CRT/SDK into `~/.cache/cargo-xwin` once.

## Packaging
- Both build scripts ship a Kiln runtime in the app ([bundle.md](bundle.md), "The bundle a launcher ships"): about 75 MB more.
- Linux: `tools/launcher/build-linux.sh` → AppImage + deb in `$KILN_OUT/bundles/linux/` (`bun x tauri build`;
  `beforeBuildCommand` is `bun run build:web`, so a type error does not block packaging; run tsc
  separately).
- Windows: `tools/launcher/build-windows.sh [--selftest]` → `kiln-launcher-setup.exe` (NSIS, per-user install) via
  `tauri build --runner cargo-xwin --target x86_64-pc-windows-msvc --bundles nsis`. `--selftest` installs it into a
  scratch Wine prefix (`wine kiln-launcher-setup.exe /S`) and runs `kiln-launcher.exe --selftest --out`.
- **WebView2 under Wine** (checked): Microsoft's Evergreen WebView2 runtime 156.0.4314.58
  installed into a scratch prefix (Wine 11.20 staging) runs the release UI on a private Xvfb display with
  `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--no-sandbox`: Detect, Install Kiln, server import/start/stop and the admin
  pages over KAP/1 worked; native `<select>` popups draw blank (keyboard selection works). Kill the runtime's
  `MicrosoftEdgeUpdate.exe` in such a prefix (it phones home). A real Windows 10/11 PC is still the reference.
- Wine test environments for servers: never `mscoree=` in `WINEDLLOVERRIDES` (the .NET host then fails with
  `0x8007007E`, linux-server.md); `mscoree=` is fine for `--selftest`, which starts no .NET.
- Both scripts take `--version V` (a release version, [releasing.md](releasing.md)), `--config JSON|FILE` (more Tauri
  config, merged in order), `--nexus` (the Nexus Mods build: no self-update; `bundles/{linux,windows}-nexus/`) and, on
  Linux, `--features` (QA builds: `dev-eval`, `update-test-feeds`) and an rpm in `--bundles`. A release is
  `tools/release/release.sh` (the whole release folder with both builds, signed and checked).

## Self-update
The launcher updates itself from frostdev.io, with GitHub Releases as the backup, signed with Kiln's updater key: a
word in the header and a notice under it when an update is found, Settings > Updates (automatic checks, channel,
Check now). Sources, channels, versions, keys and the release steps: [releasing.md](releasing.md). Code:
`src-tauri/src/selfupdate/`, `src/shell/UpdateNotice.tsx`, `src/components/UpdateSettings.tsx`.

The Nexus Mods build has none of it ([releasing.md](releasing.md), "The two builds"): built without the cargo feature
`self-update` (no updater plugin, no `selfupdate/`, no feeds) and with `src-tauri/tauri.nexus.conf.json` (no updater
key); `app_info.distribution` is "nexus" and the `update_*` commands answer `disabled`. The UI reads the distribution
(`useDistribution()`, `src/api/distribution.ts`): no header word or notice, no update feed, no Settings > Updates
controls, no Check now; Settings > Updates and About show "New versions: Nexus Mods", Deck Settings a "New versions"
row, each opening the Kiln page on Nexus Mods in the browser (`app_open_url`). Browser mock: `?dist=nexus`
(`tests/nexus.spec.ts`). The update command wrappers also check `app_info` and reject before invoking any `update_*`
command in a Nexus build (or if `app_info` cannot be read); `useUpdateFeed` itself is distribution-gated.

## `--selftest`
`kiln-launcher --selftest [--out report.json] [--keep]` runs before `tauri::Builder` (no window), in a temp folder:
data dirs + settings round trip; Steam discovery on a fake tree (schema/fixtures/vdf) + `game::add`; PE proxy
classification; a bundle built twice (deterministic) and verified; installed over an EMF proxy (chained, receipt) and
uninstalled byte-identically; the resolver parity fixtures (schema/fixtures/resolver, embedded at build time); kiln.json
JSONC edits (comments kept, range refused); zstd round trip; play-once render/decide/expiry; Z: path translation
(not on Windows); on Windows profile activation with native paths in folders with spaces and non-ASCII names
(profile.json, chain.ini [profile], UTF-8) and the process lookup by image path (+ the registry's Steam roots, listed).
Exit 0 when every check passes; the JSON report lists each check with its detail. Release builds are GUI programs on
Windows: `--selftest` attaches to the console it was started from, so `kiln-launcher.exe --selftest --out r.json` prints
there too (cmd does not wait for GUI programs; `start /wait` does).

## Known limits
- Profiles/library depend on kiln-manage M3; until then those screens show `not_implemented` (or mock data in dev
  builds, flagged MOCK).
- HTML5 drag-and-drop inside the webview is replaced by pointer events (SortableList): Tauri's file-drop handler
  disables it on Windows.
- The editor's game assets are local only: the launcher extracts them from the installed client into
  `<data>/editor-assets/` ("Extract from game"; [gameart.md](gameart.md)); `$KILN_EDITOR_ASSETS` and
  `fetch-editor-assets.sh` stay as dev fallbacks.
