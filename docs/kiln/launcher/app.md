# Kiln Launcher app

The desktop app in `launcher/`: React 18 + TypeScript + Vite (bun) in a Tauri 2 shell (`launcher/src-tauri`, crate
`kiln-launcher`) on `crates/kiln-manage`. Contracts: [contracts.md](contracts.md) (formats, section 9 commands, section 10
events). Design system: [design-system.md](design-system.md).

**A build with Nexus off** (`KILN_NEXUS_ENABLED=0`, `app_info.nexusEnabled` false): Catalog, Nexus account/settings and
`nxm://` handling are off. Browser downloads and manual Library import remain available. Nexus routes below describe
the enabled implementation; no personal API key flow is supported. See [catalog.md](catalog.md).

## Layout
| Path | What |
|---|---|
| `src/main.tsx`, `src/App.tsx` | entry, routes (HashRouter). Server/admin pages load lazily; optional server slots via `import.meta.glob` (below) |
| `src/shell/` | AppShell (the stage, the header with nav, tasks and the game's state word, the Play page in front of the stage, the sheet for every other page, the mouth: the Play button and pilot light; toasts, splash, route error boundary, the rows' right click), `play.ts` (the Play control shared by the mouth and the Play page), `firstRun.tsx` (the automatic game detection, Install Kiln on Play, one loaded profile per game: `useEnsureProfile`), `drop.tsx` (a mod dropped anywhere on the window, the dropped-modpack consent), `mods.ts` + `useMods.ts` (the one list of every mod: rows, notes, the count, the switches), `removeMod.ts` (Remove from the list), `runtimeChange.ts` (the runtime install task shared by Runtime and Play), `gameState.ts`, `nav.ts`, `serverNav.ts` (a server's grouped list) |
| `src/ui/` | the design system |
| `src/i18n/` | the launcher's texts: `en.json` (every English string, by page and component), other `<tag>.json` files, `t`/`useT`/`rich` (`index.ts`), Settings' Language (`LanguageSync.tsx`, `components/LanguageSettings.tsx`); `scripts/i18n-check.ts` checks a translation ([translating.md](../translating.md)) |
| `src/components/` | `ConfigEditor`, `SaveBackups` (shared with server pages), `ProfilesSettings` and `LibrarySettings` (Settings' Profiles and Library) |
| `src/pages/` | client pages (Play with `mods/MainList.tsx`, the mods' views under `/mods` in `mods/` and `profiles/`, Catalog, Runtime, Logs, Saves, Editor, Settings, About) |
| `src/api/commands.ts` | typed wrappers for every command + `KilnEvents` (contract) |
| `src/api/hooks.ts` | TanStack Query hooks, query keys, the selected game (zustand), `useBackendEvents` |
| `src/api/mock.ts` | in-memory backend for the browser (stateful: profiles, library, tasks, a fake running game) |
| `src/api/fallback.ts` | dev aid: read-only `not_implemented` commands answer from the mock inside Tauri (Mock flag in the header) |
| `src/editor/` | the vendored Save Editor ([UPSTREAM.md](../../../launcher/src/editor/UPSTREAM.md)) |
| `src/data/kiln-json.snapshot.json` | schema snapshot of Kiln's own `kiln/kiln.json` (core + Kiln.Host `mods` section, `ui`, (dev.29.18) `graphics`) |
| `src/assets/fonts/` | Grenze Gotisch, Alegreya Sans, Alegreya Sans SC, Inconsolata (woff2 subsets) + OFL licences |
| `src-tauri/src/commands/` | one file per area; `app.rs` also holds the task runner `spawn_task` |
| `src-tauri/src/state.rs` | data dirs (`dirs()`: the process's data folder, refused while it is held), settings, game lookup, Steam roots (+ settings' extra folders), game status |
| `src-tauri/src/folder.rs` | the process's data folder: found once, its lock (shared), the hold during a move, the gate in front of every command |
| `src-tauri/src/commands/data_folder.rs` | the data folder's commands and its part of the start-up |
| `src-tauri/src/watch.rs` | the watcher thread: `kiln://game`, `kiln://last-server`, `kiln://log` |
| `src-tauri/src/assets.rs` | the `kiln-asset` protocol (editor game assets from a local folder) |
| `src-tauri/src/selftest.rs` | `--selftest` |
| `tests/` | Playwright smoke tests (mock mode) |
| `scripts/` | `gen-tokens.ts`, `check-schemas.ts`, `scope-editor-css.ts` |
| `tools/launcher/` | `build-linux.sh`, `build-windows.sh`, `fetch-editor-assets.sh` |

## Pages
| Route | Page | Backend |
|---|---|---|
| `/` | Play, in front of the stage. First start ("The first run" below): looking for the game by itself, then Install Kiln right here. Then: the game's name, one live sentence: the count over every kind ("12 mods on, 3 off.") and only the exceptions a player must know after it (Kiln mods that start with the player's own world, ones a game's kiln.json keeps off, mods that will not run with the reason, Ember's that do not run yet); "Just once": vanilla (warns first in the arch: Kiln gear can crash), without mods; **the list of every mod** (below); boot stages while it starts; four facts (Kiln with its build date and "update ready/failed", proxy, runs on, last boot: boot.json's errors, the mods that failed in mods.json/native.json and the `[E` lines of kiln.log, all of the same run, linking to Logs); the game folder with Open, Detect, Add (a game folder: any folder with `enshrouded.exe`; a folder no Steam library knows is a custom folder and the start dialog opens right after, with "Start the game directly" chosen), and "Starts through Steam" / "Starts directly" (dev.29.17, `components/GameStart.tsx`: the start dialog: Through Steam or Start the game directly; on Linux the umu-run, Proton or Wine (automatic: umu-run, else the newest Proton, else Wine, said under the picker), Kiln's own prefix or a picked one, and the command a direct start runs; a direct start never names Steam and needs none: with no Steam on the PC "Through Steam" is not offered; [custom-game-folders.md](custom-game-folders.md)). A profile other than the default ("My mods") is named in one small link in the top line ("Profile: Ember run", to Settings, Profiles); otherwise the word does not appear. Banners above the name: an unknown game build (`GameStatus.build`: "Kiln doesn't know this game build (<hash>). Kiln starts with the game but stays idle: no mods load." with Read more), for a direct start why it cannot start (no umu-run, Proton or Wine), refusal (`last-server.json`) with the modpack offer, Steam launch-option advice (never for a direct start), Steam updating, "Update the game's Kiln" (0.2.0-dev.31.12, `shell/updateApply.ts` `gameBehind`: the game's Kiln is older than the one this launcher has, `InstallState.upgradeAvailable`: "This game has Kiln <x>; this launcher has Kiln <y>." with Update game, the update notice's own install (`shell/runtimeChange.ts` `updateGame`); held with its reason while the game runs ("Close the game first."), while it starts, while a runtime change runs, and while Steam updates it (then only the button says why: the Steam strip is right there); a failed install of that version is said on it ("The last try failed: <reason>", with Send to support), in place of the "<change> failed" strip; after any install the cached status names no offer until the backend's next status does (a release build never offers a test build's Kiln left in the Library); none when the game's Kiln is equal or newer, after the install by itself, after a switch back or in a test build of the launcher, whose channel notices say it; in the Nexus build too, whose launcher ships its runtime but has no self-update; the small "update ready" by the Kiln fact stays), "Update Kiln" (`GameStatus.newerMods`: the loaded profile's mods whose `kilnBuild` is newer than the game's runtime: "<ids> needs a newer Kiln." with Update, which opens Runtime), OptiScaler next to the game (dev.29.18, `GameStatus.graphicsTools`: "OptiScaler is installed (<file>)" with what Kiln's in-game overlay does next to it, from its OptiScaler.ini; [ui.md](../ui.md#optiscaler)), safe idle. The mouth (shell) is the Play button | `game_*`, `launch_play`, `launch_state`, `profile_active/save/ensure_default`, `profile_resolve`, `library_*`, `found_*`, `config_list`, `engine_settings_*`, `modpack_pending/dismiss` |
| `/mods/order` | Load order, every kind: the mods that are on, in the order a start brings them up (architecture.md "Boot"): native mods in the proxy (dllmain), as Kiln starts (early), Kiln's plugins (Kiln plugins, and each other loader's plugin with its mods: EMF, EML, ShroudForge, SET, Greeneye, Flame, Shroudtopia, Enshwick, shroudlink, XHL-Core's companions), after the plugins (boot), Kiln mods at the main menu in the resolver's order, then with the player's own world (side "server" Kiln mods, `ResolvePreview.withWorld`; native world phase), and the game folder's mods: packages Kiln places there ("Placed by Kiln"), and the ones put there by hand that load by their kind's rules; then "On, but not loading" with each reason, and Off. Client or server side (aside). Each row: switch, kind, version, menu | `profile_resolve`, `profile_save` |
| `/mods/native` | Native and DLLs: native mods grouped by phase (dllmain/early/boot/world), drag-reorder inside a phase, plugins; the chain card (dev.21): each chain.ini entry with its "Load after Kiln and ShroudForge" switch, last session's hook overlaps with a one-click "load late", and the compat rules that apply ([engines/README.md](../engines/README.md#closed-source-dll-mods)) | same, `chain_view`, `chain_set_late` |
| `/mods/engines` | Engines: which loader runs which kind in this game, for the loaded profile: each engine plugin's state from the last start (EML's phase, ShroudForge hosted or its own and its mods' effects, the XHL-Core check, an engine's own loader and what it means for the mods that are on), the shared archive backup guidance (below), the other loaders found next to the game | `engine_status`, `eml_backup`, `found_scan` |
| `/mods/configs` | the loaded profile's config folder (SchemaForm/raw); files of mods that are not installed sit apart, read-only, with Clear | `config_*` |
| `/profiles`, `/profiles/:id/<tab>`, `/library` | old places, redirected (`pages/mods/OldRoutes.tsx`): `/profiles` → Settings, Profiles; a tab of the loaded profile → its new place (Mods, EMF → Play; Native → `/mods/native`; Engines → `/mods/engines`; Configs → `/mods/configs`); Saves → `/saves`; another profile's → Settings, Profiles (where it can be loaded); `/library` → Play | |
| `/catalog` | Enshrouded's mods on Nexus Mods ([catalog.md](catalog.md)): Kiln mods (default), All Enshrouded, Installed; search, sort; cards with endorsements, downloads, what the mod is and its state (Installed, Update <version>); an updates banner; `?mod=<id>` opens a mod's details (pictures, description, requirements, files, Add / Update / Download on Nexus Mods, Open on Nexus Mods). Mod Manager Download links are taken anywhere in the app (`shell/catalogInstall.tsx`, `NxmListener`) | `nexus_*` |
| `/runtime` | install/update/rollback per bundle (bundles of one version and day are ordered by when they were built, never by the git id), uninstall (typed), proxy chain (receipt + live `boot.json` chain), switch from ShroudForge (typed), add bundle | `runtime_*` |
| `/logs` | the support report above the tabs ("Save report..." through "What's in this report", where other mods' logs can be left out; "Copy summary": [support-report.md](support-report.md)); kiln.log, game log, proxy.log, mod logs (follow, filter), crash reports, tick stats (sparklines); Engines: one line per engine plugin that wrote `kiln/state/<engine>.json` or has mods in the active profile (EML `phase`, ShroudForge `mode`, `reason`, each mod's state and reason; ShroudForge's own per-mod runtime report while it is current, never shown as proof of a gameplay effect; the XHL-Core check when the profile has XHL companions or the host was recognized), plus read-only shared archive backup guidance | `logs_*`, `report_save`, `report_preview`, `report_summary`, `engine_status`, `eml_backup` |
| `/saves` | save files, backups, manual backup, restore (refused while running; Steam Cloud hint), delete | `saves_*` |
| `/editor` | the Save Editor (lazy chunk) | `editor_*`, `kiln-asset://` |
| `/settings`, `/settings/<section>` | (dev.30 redesign, `pages/SettingsPage.tsx`, `pages/settings/`) Six sections down the left, each with a live line ("English · fire on · Deck: Auto"), the open one's groups as links under it and the launcher's version at the bottom; one section at a time on the right as titled groups of rows: a name, one line of help with More for the rest, "next start" where a setting applies later, the control on the right. `/settings` opens the section last shown in this run (General first). **General**: Language (the launcher's; Kiln's in the game, `ui.language`), Look (Animated fire: off draws the plain Play button), Steam Deck (Start in, Open Deck mode), On this computer (the AppImage's app menu entry). **Mods & profiles**: **Profiles** (`components/ProfilesSettings.tsx`; `#profiles`): every saved profile with its mod count over every kind and the server it is for, which one is loaded, Load (the main list then shows it), "Save current as..." (`profile_create` with the loaded one as `copyFrom`; the loaded one stays loaded), Rename, Folders and notes (name, mod data, drop-in folders, notes), Delete (typed; never a loaded one), and "No profile" (the game folder decides: `profile_deactivate`); the modpack flow's "Server: <name>" profiles are listed too; Crucible's packs under it (`#crucible`). Adding and removing mods ("Switch new mods on", default on; folder notes; the daily mod update check; "Type a mod's name to remove it", default off). **Library** (`components/LibrarySettings.tsx`, `#library`): how many mods and versions, the size on disk, the data folder (Change, Open), Clean up (every version no profile and no game's profile file uses, one `library_remove_many`; typed; never a version kept for a roll back), "Earlier versions to keep" (`keepVersions`, 0 to 5, default 1) and (dev.29) the earlier versions an update kept for a roll back behind Show, each with its size, its settings files and Remove ([catalog.md](catalog.md), "Rolling back an update"). **Game & Kiln** (the selected game; "For" picks another client): Games (detect/add/forget; each row says "custom folder" for one no Steam library knows, how it starts with the start dialog behind it, and an unknown game build; Default game; Save backups to keep), Kiln in the game (kiln.json's `ui.look`, `ui.theme`, `ui.background`, `ui.scale`, `ui.chatHistory`, `ui.chatCompletion`, `ui.outlines` via SchemaForm), ShroudForge (`#shroudforge`). **Graphics** (dev.29.18, a client only): one line per tool next to which Kiln's frame cap, DLSS settings and upscaler step aside (`GameStatus.graphicsStepAside`) at the top; Kiln's graphics tools (`#graphics`: kiln.json's `graphics` section: frame cap, DLSS preset and render scale, readout; `config_kiln_*`), "Behind the game's FSR 3" (the upscaler choice, the offer of its files, what runs now or ran in the last game) and "Upscaler files" (`components/UpscalerSettings.tsx`, `#upscalers`; section "Upscaler behind FSR 3 and the upscaler files" below). **Updates & online**: Launcher updates, Nexus Mods in enabled builds only (`#nexus`: OAuth account sign-in/sign-out and "Mod Manager Download links"; `components/NexusSettings.tsx`), Discord ("Show the Community page"), What Kiln sends (Crucible's install counts). **Advanced** (rarely needed): Drawing the launcher (Linux: without the GPU, the X11 switch), Protection (WebKit's sandbox, http modpacks), Steam folders, Kiln's file (the rest of kiln.json: everything but the entries above and `shroudforge.*`, any key a newer core adds included), Game art, Developer (the desktop app). **Search** (Ctrl+F; Esc clears) reads every row's name, help and More text in the launcher's language (and kiln.json keys): the hits are the working controls under their place ("Graphics ›", "Open in Graphics") with a count per section in the list; Enter opens the first. Enter, "Open in" and every `#<group>` link (`pages/settings/model.ts`, the old ones included) open the section, scroll to the row or group and flush it with ember for 2 s | `profile_*`, `library_unused`, `library_remove_many`, `app_settings_*`, `game_*`, `config_kiln_*`, `nexus_account`, `nexus_sign_in`, `nexus_sign_out`, `nexus_handler_*`, `vendor_*` |
| `/about` | versions, CREDITS.md, "Theme and design: frostdev"; Updates (the state, Check now), in the Nexus Mods build "New versions: Nexus Mods" | `app_info` |
| `/servers` | every server, one line each (or a redirect to the first server when there is no `ServersPage.tsx`); a server's pages are in [servers.md](servers.md). Its Overview has "Open web panel" while it runs with Kilnwarden (`kilnwarden`) or Kiln Web Admin (`kiln-webadmin`, Kilnwarden's old name; 0.2.0-dev.31.14: either id, Kilnwarden first) enabled | `server_list`, `admin_open_web_panel` |
| `/community` | Kiln's Discord, all of it optional ([discord.md](discord.md)): Get help (Send a report, Save report instead), your support posts with their states and replies (Open in Discord; `···`: Copy link, It's fixed, Delete my post), the tester programme (Volunteer with the hardware shown first, the checklist), news and events, the Discord account (Sign in with Discord through frostdev.io, Sign out, Sign out and forget me, "Tell me when Kiln support replies"), Rich Presence with its preview card, links, "What is sent and kept". The last word of the header; new replies put a flame numeral after it. Settings, Updates & online, Discord: "Show the Community page" (default on): off hides the page, the numeral and every "Send to Kiln support", and the launcher stops everything it does with Discord. "Send to Kiln support" also sits on the Logs page's support report row, after the fix of Play's problem banners (Kiln is not loading, Kiln did not start, Kiln update failed, No mods loaded; ShroudForge's failed startup pass on Engines) and on Play's crash notice (a banner after the game closed while Kiln wrote a crash report in that run: See the crash report, then Send to Kiln support; it shows without the button when the page is hidden) | `community_*`, `tester_*` |

"Shared archive backup differs" (`components/EngineStatus.tsx`): read-only guidance on Logs > Engines and the mods'
Engines view when `eml_backup` reports a differing `.kfc` and no EML mod is enabled in the active profile (or no profile
is active). A difference does not identify its writer. Disable EML mods and start the game through Kiln to let its
archive writer restore only proven, unchanged EML output. When the last idle start's `kiln/state/eml.json` has a
`reason` (it could not prove ownership), the banner shows it (warning tone); otherwise it names the ways back for that
case too (enable any EML mod for one start and disable it again, or Steam's "Verify integrity of game files"). A
backup of an earlier game build (`staleBackup`) is said too, together with the `reason` when there is one. There
is no direct-copy action; legacy `eml_restore` calls refuse. The `reason` of the last start that finished (`idle`,
`done` or `failed`: data left because it is not provably EML's, extra containers kept, recovery files kept with their
sizes, containers moved to `kiln/eml/displaced/`) is shown as written whenever there is one, as "EML archive note" when
the backup banner's own conditions do not hold (no difference, or an EML mod is on). The main list labels every kind with its engine (a badge; a Kind filter); the Deck's
Mods says each row's engine ("ShroudForge mod"). Kind names and lists: `src/api/kinds.ts`.

## The list of every mod (Play)
`pages/mods/MainList.tsx` on `shell/mods.ts` (pure: rows, notes, the count) and `shell/useMods.ts` (the queries and the
switches). "Mods" means all mods everywhere in the launcher: no count, heading or sentence leaves a kind out; where a
text is about Kiln's own kind it says "Kiln mods".
- **Rows**: every Library entry of every kind (`library_list`), the loaded profile's entries the Library lacks ("Not in
  the library"), then the mods a player put in the game folder that the launcher did not place (`found_scan`), marked
  "Game folder". One row each, by name: a switch (the flame), the name, the kind badge (`KIND_SHORT`), the version, and
  one short note only when there is something to say: "Starts with your world", "Off in this game" (kiln.json
  `mods.hosted`), "Needs <id> <range>", "Will not run" (with the resolver's reason on hover), "Runs from the game
  folder" (a native package Kiln places in the game folder, which the game loads itself: engines.md "Files in the game
  folder"), "Does not run yet" (Ember),
  "Needs XHL-Core" (when `engine_status` found none), "Failed at the last start" (mods.json/native.json of the run that
  wrote boot.json), "Left in the game folder" (an off mod whose placed copy the last activation kept: `Activation.retained`).
- **On** means in the loaded profile and enabled. Switching on a mod the profile lacks adds the Library's newest
  version, enabled; switching off keeps the entry, disabled, so its version, order and settings stay. Edits go through
  the profile's save queue (`pages/profiles/useProfileEdit.ts`), and `profile_save` writes the game's profile.json again
  where the profile is loaded (not while the game runs: then at the next Play).
- **A game-folder mod** has no switch (nothing per mod could honour one): its state ("Loads from the game folder",
  "Does not load", "A copy is in your list", "Can't run", the reason on hover) and its fix: "Load them" (the profile's
  drop-in folder of that kind) or "Add it" (`found_add`: into the Library and the profile, on).
- **The menu** (the "More" button, or a right click anywhere on the row; `useRowContextMenu` in `ui/Menu.tsx` gives every
  row with a menu, in every list of the launcher, its menu at the pointer, fields keep the webview's own): Switch on/off,
  Settings (ShroudForge's `extended.mod.json`, Shroudtopia's `shroudtopia.json`; the window's "Open <file>" button,
  0.2.0-dev.31.25, shows the file the engine reads in Explorer (`EngineSettings.openFile`, `app_open_path`): Shroudtopia's
  in the profile's config folder once it exists, ShroudForge's copy in the game's mods folder, never the Library's; while
  there is none, a line says so), Configs (when the mod wrote a config:
  the profile's config editor, opened on its file), Use <version> (the Library's other versions), Roll back to <version>
  (dev.29: an earlier version an update kept, with its settings; `library_rollback`, Undo in the toast is
  `profile_revert`; `shell/rollback.ts`), Open folder, Remove <version> (one of several versions), Remove.
- **Remove** (`shell/removeMod.ts`): the mod leaves the loaded profile, then its versions leave the Library
  (`library_remove` past exactly the users `library_users` named; another profile that lists it is named in the
  confirmation and shows it as missing). One click with a plain confirmation; Settings "Type a mod's name to remove it"
  (off unless turned on) adds the typed name. Uninstalling Kiln, deleting a profile, a server or saves keep their own
  typed confirmations. Refused while the game runs. **"Remove its settings too"** (0.2.0-dev.30, owner 2026-11-20): a
  checkbox in the dialog, unchecked each time it opens, with one line under it ("Its settings in this game and your
  profiles go too. Kiln keeps a copy in backups."). Unchecked, the settings stay where Kiln keeps them, and adding the
  mod again brings them back (a removed mod's kept config files show in the Configs window as "Not installed", read-only, where they can be cleared: below). Checked, `library_remove` gets `settings: true`, and with the mod's last Library version
  its settings go too (`kiln_manage::mod_settings`): in every known client game the settings stash
  `kiln/state/placed-settings/<kind>/<id>/` and the settings files in `kiln/kept-mod-files/<kind>/<id>/`; in every
  config folder (each profile's `<data>/profiles/<pid>/config/`, a copied profile's `<game>/kiln/profiles/<pid>/config/`,
  the game's `kiln/config/`) Kiln's config files of the mod (`<id>.json`, `<id>-server.json`, `<id>-client.json` and
  every file a `.schema/` snapshot names as the mod's, with those snapshots; Kiln, native, plugin and Shroudtopia mods,
  and not while a mod of another kind has the same id), an engine's values file and schema (`<kind>/<id>.<ext>`,
  `.schema/<kind>/<id>.json`: EMF's `emf/<id>.ini`) and a Shroudtopia mod's entry in the shared
  `shroudtopia/shroudtopia.json` (that entry only). Never another mod's file, Kiln's own files (`native.json`,
  `keybinds.json`, ...), a folder of the player's own, servers or world data. Everything is first copied into
  `<data>/backups/mod-settings/<kind>-<id>-<time>/` (`games/<install id>/...` and `profiles/<pid>/config/...`, as it
  was laid out, and `removed.json` naming where each file came from), so a player can put it back by hand. The box
  shows only when the whole mod goes (not for Remove <version> of one of several); a row whose files were gone from
  the Library already uses `library_remove_settings`. What could not go is a notice ("Some settings stayed", with why).
  Remove… of several selected mods has the same box ("Remove their settings too"), and the Deck's remove dialogs a row
  above the buttons that A switches (Off by default).
- **Configs** (the window of a row's menu, `components/ConfigEditor.tsx`, and `/mods/configs`; HTR, #general, 31.29: the configs
  of mods he had removed were listed with switches that looked live, and he asked whether Remove really removes): the
  loaded profile's config folder, a file list beside the form (SchemaForm) and the raw file. For a profile, `config_list`
  says per file whether its mod is installed (`ConfigFileInfo.orphaned`, `kiln_manage::config_orphans`, 0.2.0-dev.31.31):
  the file's mod is the schema snapshot's `mod` (Kiln, native and plugin mods, kiln-emf), or, with no snapshot, the file
  name in `<kind>/<id>.<ext>` of an engine's kind (EMF, EML, XHL, ShroudForge, SET, ...). "Not installed" means absent
  everywhere the launcher can see a mod: not an entry of the profile (on or off: a mod that is only switched off is
  installed), not in the Library (any kind, any version), not in the game folder of a known game (what the Mods list shows
  as found, and a folder Kiln kept at the last activation). Compared by id without case, as the list's own Configs entry.
  Never a guess: a file whose mod is not known (no snapshot and not in an engine's folder), Kiln's own files (`native.json`,
  `keybinds.json`, ... and every config that names the mod `kiln`), Shroudtopia's shared file and EMF's own (`emf/emf_core.ini`,
  and `emf/lua.ini`, the switch of the Lua host Kiln bundles) are never marked, and
  nothing is marked while the profile, the Library or a game folder could not be read. A server's and a game's list is
  never marked. The window shows the installed files as before, then a heading "Not installed: kept for when you add it
  back" with "Clear all", then the marked files (an orphan never opens first). A marked file is read-only (form and raw
  file) with a hint that changes do nothing until the mod is back. **Clear settings** is a small bin on each such row and
  a button in the file's card; Clear all is on the heading. Each asks first (an inline strip naming the files and saying
  an installed mod's settings are never touched), then `config_clear_orphans` moves the files and their `.schema/`
  snapshots into `<data>/backups/mod-settings/config-<profile>-<time>/` (the folder "Remove its settings too" writes to,
  with its layout and `removed.json`) and a toast says where the copy is. The backend checks the orphan state again, so a
  stale window or a hand-made call cannot clear an installed mod's file: the whole request is refused (conflict, nothing
  touched), as it is while a game runs (`game_running`). The admin web Configs page (a running server, `admin/pages/
  AdminConfigsPage.tsx`) does not share the component and is unchanged.
- **Above the list**: Filter (words in the name or id) and Kind (when more than one kind is present), shown with more
  than six rows; Notes (N) when the last activation said something about mod folders (below); Load order, Engines,
  Catalog (Nexus builds), Add mods (the file dialog: zip, 7z, rar and DLL files, `shell/modFiles.ts`; folders are
  dropped; on Linux one glob per extension in every letter case, `*.[zZ][iI][pP]`, since its dialogs match
  case-sensitively; the drop anywhere and the Catalog stay). "All on (N)" and "All off (N)" (or "Shown on/off" while the filter
  or Kind narrows the list) switch every row shown whose own switch would change, N of them, as one profile save;
  game-folder rows have no switch and are left out (the button's title and the toast say how many and why), and the
  toast's Undo puts back what they changed, leaving every mod changed since on any page (`bulkPlan`, `applyBulk`,
  `revertBulk` in `shell/mods.ts`; the record in `shell/useMods.ts`, fed by the shared save queue's watchers in
  `pages/profiles/useProfileEdit.ts`). The Undo runs on the list as it is when pressed
  and says it cannot when another profile is loaded, a save still runs, or the profile changed elsewhere (the
  backend's generation is read first). With no mod at all the list says how to add one. The list scrolls inside the page.
- **Several mods at once** (`shell/selection.ts`): a box at the start of every row (seen on hover and focus, always
  once a row is selected); Shift+click selects the run from the last clicked row, Ctrl+A every row shown (the filter and
  Kind applied: the set All on / All off acts on), Escape none, Space or Enter on a box toggles it (Shift+Space: a
  range); these act only on keys pressed on the list itself, never on ones from a row's menu, a dialog or the filter.
  The selection belongs to one loaded profile of one game (its id and `created`) and is kept outside the list, so a
  start's progress bar or another page gives it back; a Load or "No profile" of the game ends it at once, and a list
  that finds another profile loaded drops it; it
  never holds a row the list does not show: a filter or Kind change lets go of what it hides and the bar says "N not
  shown any more left the selection". The bar above the list: "N selected", Switch on and Switch off (All on / All off's
  plan, save and Undo over the selected rows; the titles say what they skip and why), Remove… and Clear. Remove… (off
  while the game runs; a game-folder mod Kiln did not place is skipped, the title says so): one confirmation naming
  every mod (a long list scrolls) and, per mod, the other profiles that list it (library_users of every version, read
  in parallel, the single Remove's wording); with "Type a mod's name to remove it" on, the word is their number. Then
  one profile save takes them all out of the loaded profile (nothing is removed when it fails), and each version goes
  with `library_remove` past exactly the users the confirmation showed (not `library_remove_many`, which takes only a
  force flag); a refusal (a running server that uses it, a locked file) leaves that mod in the Library and out of the
  list's profile, the others go, and one notice names what was left and why. The Deck: L2 selects or unselects the
  focused row (its name gets a check), R2 "N selected" opens Switch on, Switch off, Remove… and Clear; Remove asks once
  with the names (focus on Cancel).
- **Profiles are a background feature**: one profile is always loaded per game. `useEnsureProfile` calls
  `profile_ensure_default` once per start for a game with Kiln and no loaded profile: "My mods" when there is none at
  all, or the one the launcher remembers for the game (a reinstall of Kiln); a player who chose "No profile" in
  Settings is left alone (the backend answers null), and Play then says "No profile is loaded: the mods in the game
  folder load as they are." with the switches off until one is loaded.
- **Layout**: the Play column takes the room left of the kiln (`--kiln` in base.css: 40% of the width, 62.5% of the
  height, at most 800 px; `shell/Stage.tsx` draws the mouth at the same size), up to 920 px; the title and the sentence
  shrink with the window, more so under 700 px of height.

## Notices about mod folders
Every activation (a mod switched, Play, a profile loaded, an add, a modpack, an update) says which folders Kiln left as
they are (`Activation.retained`), which mod files it kept or put back (`keptFiles`) and its notes on the mods
(`warnings`: a player's own folder that serves a mod, a ShroudForge value a new version refuses). It says them every
time, so the launcher raises each sentence once per game (`shell/notices.ts`):
- **Once.** `notices_unseen` answers which sentences are new for the game; asking remembers nothing. A sentence is
  remembered (`notices_shown`) only when the player closes its notice: its close button or one of its buttons on the
  desktop, OK or B on the Deck's notice dialog (`ui/Toast.tsx` `close`, which also runs the callbacks of the notices
  an entry took in: the same text again, notices folded into "N earlier notices"). Being on screen is not enough: a
  notice scrolled out of view, still waiting behind another on the Deck, or open when the launcher closes, comes again
  at a later activation. While a sentence is open, a later activation adds no second copy of it (`openLines`). A
  failed report is not retried: the sentence comes again later. The memory is in the data folder
  (`installs/<id>.notices-seen.json`: hashes only, at most 512, the longest unsaid go first), so it survives a
  restart and moves with the data folder. Two launchers on one data folder share it through a lock file next to it
  (dev.27), so one's closed notices are not shown again by the other. A new or changed sentence still comes. What does come stays until closed
  ("Some mod folders were kept" with its paths and recovery steps, "Mod settings kept", "Notes on your mods"; "Mod settings kept" is about files a mod wrote into its removed folder, which Kiln kept: a removed mod's config files, kept by Remove on purpose, are listed apart in the Configs window). A backend
  that cannot answer (an older one) gets the old behaviour: all of them.
- **Notes (N)** above the main list (`components/FolderNotes.tsx`) opens what the last activation said, in three
  parts: Left in the game folder, Mod files kept, Notes on your mods. It reads the newer of the last activation this
  window showed and `profile_active` (the backend's record, also after a restart). The Deck has the same list in
  Settings ("Mod notes", A: Read).
- **Settings, Mods & profiles, "Show notes about mod folders"** (`LauncherSettings.modFolderNotes`, on by default; the Deck's
  Settings "Notes about mod folders"). Off: none of these is raised, except the first notice of a folder that may still
  run although its mod is off (`retained` with `mayRun` and `off`, and the player's own folder of a mod that is off:
  `Activation.safetyNotes`). Notes (N) still lists everything.
- **A notice never takes the screen** (`ui/Toast.tsx`): a detail shows four lines, then "Show all" (an opened one scrolls
  inside its toast past 40% of the window); the stack is at most 60% of the window and scrolls. The Deck shows a notice
  that stays as a dialog (it scrolls); an error in its bar shows all of its text.

Where a player's own folder comes from: a mod already in ShroudForge's (or EML's) mods folder before Kiln, added with
"Add it" in the list, is served by that folder from then on (Kiln never takes over a folder it did not place by
itself; it places no second copy, since ShroudForge drops a package whose id it finds twice). Its ShroudForge settings
are editable in the launcher all the same (engines.md "Settings"); the settings dialog says "This is your own folder,
not a copy Kiln placed: Kiln changes only the setting you change here."

A player's own folder of a mod the profile has **off** (0.2.0-dev.31.12; a player's own EML folder in ShroudForge's mods
folder had run while he had the mod off). Its note is a safety note, raised even with the notes about mod folders off,
and says what happens to the folder and how to stop it where Kiln cannot (`engine_mods::own_off_in_shroudforge`, for a
ShroudForge entry and for an EML entry whose folder ShroudForge would run): "shroudforge:<id>: it is off in the profile,
and your own folder <path> stays as it is (Kiln never writes into a folder it did not place). While Kiln hosts
ShroudForge, Kiln has its runtime skip this folder at each start when that runtime takes Kiln's skip list (Kiln's
bundled runtime does from 0.2.0-dev.31.12 on); kiln.log says at each start whether it was skipped. Another runtime (your
installed ShroudForge, "Use my installed ShroudForge") or ShroudForge's own loader still runs it while its own
ShroudForge switch is on: to stop that,
switch it off in Kiln's in-game menu (Mods > ShroudForge) or in ShroudForge's Modloader, choose "Let Kiln manage this
folder" in Notes, or move the folder out of the mods folder." An EML entry's note leaves out "Let Kiln manage this
folder" (Kiln takes over ShroudForge folders only). The mod's row says what ran at the last start: the engine status
lists kiln-eml's row as `disabled` ("… is skipped this start; Kiln leaves its files as they are") or, where nothing
skipped it, `own-loader` ("runs … although it is off in the profile: …", with the same ways to turn it off).
Before, the note was "your own folder <path> stays as it is; Kiln never switches a folder it did not place".

**"Let Kiln manage this folder"** (dev.27, `components/OwnFolders.tsx`, `engine_own_folders` / `engine_adopt_folder`)
hands such a ShroudForge folder to Kiln, on the player's click only. It is offered where the own-folder note is: in the
Notes dialog under "Your own mod folders" (the folder's path, the mod and version, and the button), and in the mod's
settings beside the own-folder line. When the folder's files are not exactly the mod's as the Library has them, the
reason stands in place of the button and names the first file that differs ("Kiln cannot take this folder over:
src/mod.lua is not the mod's file (its content differs). …"). The button is off while the game runs. It opens a
confirmation that says what changes: "Kiln will update, switch and remove this folder with the mod from now on", the
three things that means (switched with the profile, replaced at an update with the setting values carried over, removed
when Kiln takes its mods out of the game unless it then holds values of the player's), the new folder name when Kiln
renames it, and that nothing in the folder is changed now except one small file. After "Let Kiln manage it" the
launcher writes the mods to the game again (`profile_activate`), so the folder is switched as the profile says and the
notes no longer name it; a refusal by the backend (the folder changed since the list was read) is shown as it is. The
Deck's Settings shows the notes without the button.

## Cooking mods

A mod with a cook recipe (`assets/cook.json`, `impact/cook.json`, or looks and sounds declared in its blocks) has files
that are made on each PC from that PC's game: models, animations, sounds, Impact programs. They are never shipped, and
the game loads only the ones a cook on this PC made ([../modding/cooking.md](../modding/cooking.md)). The launcher makes
them (dev.27, `shell/cook.ts`, `cook_plan` / `cook_run`, `kiln_manage::cook`):

- **Before the game starts.** Play (desktop and Deck: the shared `usePlayControl`) runs `cook_run` before `launch_play`
  for a start with the profile; so does Launch in a server's modpack offer (`ModpackOffer.tsx`), which starts the
  modpack's profile the install made active. The cook first writes the active profile again when it changed since its activation
  (kilnctl, another window), so it cooks the mods the start then loads, and ends at once when nothing needs a cook. A mod
  needs a cook when it was never cooked for this game (it was installed, or switched on), when it was updated (its
  identity changed), when the game was updated (cooked files are made from the game's data; Steam's build id tells,
  else the game's data file), when its last cook failed, or when a file its last cook made is gone or is not the bytes
  that cook made (its receipt in `kiln/state/cook-receipts` says). That rule is one function, `cook::needs_cook`; the
  record it reads is the game's `kiln/state/cooked.json`. A vanilla start and "play once" do not cook; "play once" with
  another profile still makes the game data for its OnBoot mods (below).
- **The game data for OnBoot mods** (dev.28, `kiln_manage::boot_snapshot`, `shell/bootSnapshot.ts`): Play's
  `cook_run` then makes the snapshot of the game's data that `Mod.OnBoot` reads, when an enabled Kiln mod of the start
  has `"boot": true` or `"required"` in its kiln.mod.json (a server leaves out client-only mods). Once per game build:
  about half a minute and about 200 MB the first time, with a progress line; later starts verify file checksums.
  Without such a mod the line says "no mod needs the boot snapshot" and nothing runs (a snapshot that is there stays).
  A producer failure does not refuse the start: one notice says why and the launcher continues without it. An OnBoot callback
  must handle unavailable data under the boot failure policy. Launch in a modpack offer runs it through the same
  `cook_run`. A single start with another profile ("play once", launch mode `once`; `beforeStart` in `shell/play.ts`)
  cooks nothing but runs the same step first, as the task `boot_snapshot_run`, for the mods of that start
  (`boot_snapshot::boot_mods_once`: that profile's enabled Kiln mods and, by its rule, the Kiln drop-ins), with the same
  progress line and notice ([../modding/boot-snapshot.md](../modding/boot-snapshot.md)).
- **From nothing, and nothing lost.** A mod that needs a cook gets its `assets/cooked` moved aside (to
  `assets/.kiln-cooked-prev-<hex>/cooked`) and every recipe run, also one whose own check calls it up to date (that check
  reads the mod's files, never the game). The same cook puts it back, with the receipts and the mod's own files it
  remembered, when the cook does not end well (a missing source, a cancel, a tool that fails). A set-aside an earlier
  cook left (its putting back failed, or it was stopped) is never read for anything else: when `assets/cooked` is
  missing, the newest one's plain `cooked` folder becomes `assets/cooked` again (without receipts: the next cook that
  ends well writes them), and every set-aside of the mod is deleted only after a cook of it that ended well. A mod whose
  cooked files no receipt lists (an older Kiln's cook) is due too.
- **What the player sees.** "Cooking <mod>" (or "Cooking N mods"), then the task with "Cooking <mod> (i of n)" and each
  line the tools print, then "Starting" as before. Nothing is said when nothing needs a cook.
- **Never stuck.** Each tool runs within limits: 30 minutes for one recipe, 10 minutes without a line, 2 minutes for a
  check. One that passes a limit is stopped with every process it started (its process group; on Windows its job), its
  mod is reported as not cooked, and the next mod and the start go on. The task's Cancel stops the tool that runs the
  same way; the game then starts with what was cooked. A tool's very long lines are cut and only the end of what it said
  is kept.
- **One at a time.** A cook holds the game's lock (`kiln/state/cook.lock`) for its whole run, and a start with mods holds
  it, and the lock of each mod folder it loads, until Steam was asked: the game never starts while a cook writes its mods
  (also one cooked for another game that loads the same Library entry), and a start that finds a cook running waits a
  moment, then says so. "Cook again" is refused while the game runs and in the two minutes after a start through the
  launcher (Steam may still be starting the game). Two cooks of one folder (two games that load one Library entry,
  `kiln cook` beside the launcher) wait for each other, and a Library entry another game loads is not cooked while that
  game runs or was just started (the mod is reported as not cooked: "… loads this mod's folder too …"). A cook stops
  when the game is started from Steam meanwhile.
- **A failed cook stops nothing.** The other mods are cooked and the game starts. One notice stays until closed:
  "<mod> was not cooked", each such mod's name with the tools' own last lines (a missing input after a game update, a
  glTF the cook refuses), how many others were cooked, and that a mod that could not be cooked keeps running on the
  files it has (those of its last cook, or none if it was never cooked here). On the Deck the notice is the dialog that holds long text and closes
  with B. When no tool can run at all (Linux without Python 3.10 or newer, or without a zstd decoder), every due mod
  gets the one sentence that says what to install. The failed mod is tried again at the next start.
- **"Cook again"** in a mod's menu (the main list's "More for <mod>", for a Kiln mod that is on and has something to
  cook; on the Deck's Mods screen: Y on that mod's row) cooks that one mod whatever its record says, and answers "<mod>
  cooked" or the same notice.
- **Where the files go.** Into `assets/cooked` of the folder the game loads the mod from (the Library entry where the
  game reads the Library through Z:, else Kiln's copy in the game), and nowhere else (the cook tools refuse any other
  place), so the mod keeps its identity: the next activation keeps the copy, and a server sees the same mod. The tools
  record each file in the game's receipts; kiln.assets warns about (default `"warn"`) or refuses (`"local"`) a cooked
  file they do not list ([../modding/cooking.md](../modding/cooking.md)).
- The tools are the ones `kiln cook` runs, carried by the launcher and unpacked once under `<data>/cooktools`
  (releasing.md, "The cook tools and the bundled Python"). On Windows the bundle's own Python runs them; the player's
  Python, if any, is never used.

Not cooked by the launcher: drop-in folders (`kiln/mods`, not in the profile; `kiln cook <folder> --game <game>`), and a
local server's mods (a server sync copies a mod without `assets/cooked`; `kiln cook <server>/kiln/mods/<mod> --game
<server>`, dedicated-servers.md).

## What a start is doing
From Play to the game's main menu the launcher shows one bar for the whole start, the step it is on, and how long it
still takes (0.2.0-dev.27; `kiln_manage::start_progress`, commands `launch_begin` and `launch_progress`).

- **The steps.** First the launcher's own: `cook` (the mods that need a cook, one after the other: the mod's name and
  the tool's current line), `boot-snapshot` ("Making the game data for OnBoot mods (once per game update)": only on a
  start where the boot game-data tool builds a snapshot, from its line that a build begins to its end, with the
  tool's current line; a start whose snapshot is there, or that needs none, never shows it; with no earlier start
  that had it, its usual time is its known cost, 60 s), `profile` (the active profile checked or written again, the
  save backup), `steam` (Steam asked to start the game, until the game's process exists). Play, a single start with
  another profile and Launch in a server's modpack offer go through these the same way. While the tool checks for a
  snapshot after the cook, the start is not taken for one that stalled. Then the steps Kiln itself reports from inside the game
  in `<game>/kiln/state/progress.json` (written by kiln-core: Kiln's settings, native mods, the early plugins such as
  ShroudForge, finding the game's functions, Kiln's services, plugins, native mods, .NET, the mods, their content,
  and the game's own loading), up to the main menu. A progress.json is shown only when it belongs to the running
  game (the rule of boot.json: written after the process started).
- **What the window shows.** On Play, in place of the boot stages and the mod list: the step's line ("Starting Kiln's
  services: kiln-vfx, building 85 materials (13 of 31)"), the bar, the time left, and every step with what it took,
  the one running, and what the ones to come usually take. Below 700 px of height only the bar and the line show. In
  the top bar (on other pages) and under the Play button: the time left (or the percentage) and the step; the Deck's
  Home shows the line, the bar and the time left. The bar is a `progressbar` whose text is the step and the time
  left; the line is a `status`. "Running" comes only when the main menu is there, not when Kiln's boot ends. While
  kiln-eml prepares the game data, its own progress is the line of the early plugins.
- **Where the time left comes from.** This PC's own earlier starts, in `<data>/state/start-times.json` (the last 10
  starts per game install that reached the main menu, with every step's time, each mod's cook time keyed by the mod
  and its identity). A step's usual time is the median of the last up to 5 starts with the same Kiln and the same
  mods; else of the last up to 5 starts of the game install, scaled by the number of things the step works through. A
  running step that counts (13 of 31) also has its own pace; with starts of other mods or another Kiln the larger of
  the two counts. The number is rounded ("about 40 s", to 5 s above 20 s, "a few seconds" below 5 s) and says on hover
  which starts it comes from.
- **The first start.** While any step still to come has no earlier start to go by (the first start with these mods
  on this PC, a mod cooked for the first time), no number is shown: "First start with these mods on this PC: Kiln is
  timing it", and the bar counts steps (gold) instead of time (ember).
- **Slower than usual.** A step at 1.5 times its usual time says "taking longer than usual (usually 19 s)", and the
  time left stays at what the steps after it take until it ends. The bar never moves back, and it is full only at the
  main menu.
- **Not progress.** Safe idle (with Kiln's reason), Kiln failing to go on, the game ending, "No Kiln" (Steam did not
  load Kiln's proxy), a vanilla start and Steam updating the game are said as before, without a bar (a vanilla start
  waiting for Steam holds Play for the same 5 minutes as a Kiln start, 0.2.0-dev.31.13: "One start at a time"). No word from Kiln
  for 60 s (120 s inside the early plugins) is "No word from Kiln" with its sentence (a game waiting at its menu for
  its mods says so; at the last step it says the game is still loading or at a menu Kiln did not see), without a bar
  or a countdown, and the mod list is back. A game that is in a world is not "No word from Kiln" (0.2.0-dev.29): its
  start is over for the window, and one whose menu mark never came ends at its first world. A Play that ends before the game exists
  (refused before Steam was asked) ends the start, and the page is as it was before Play. When Steam has taken the
  start and the game's process does not exist a minute later (a first start under Proton makes its prefix first),
  the bar stops counting and says "Steam is still preparing the game: this can take minutes the first time"; after
  5 minutes the window offers Play again (the launcher before 0.2.0-dev.27 did after 2), and the launcher keeps the
  start's own timings until the game appears (it goes on as one start), Play is pressed again, or 30 minutes pass.
  **Stop waiting** (0.2.0-dev.29.18): from the moment Steam was asked until the game's process exists, Play (after the
  time left, or under the boot stages of a start without a bar) and the Deck's Home offer "Stop waiting", for a launch
  cancelled in Steam (its "Processing Vulkan shaders" dialog, say) that would otherwise hold Play for those 5 minutes.
  It gives Play back at once and drops the launcher's start too (`launch_end`), so its timings go and no later look
  brings the wait back; a game Steam still starts afterwards is shown as running, without the launcher's steps. It is
  never offered before Steam was asked (the cook, the profile) nor once the game runs, and it touches no process: a game
  that appeared by the click is left as it is and its running views take over. The window keeps following a start while it is hidden or in the background (the game has the display then);
  the launcher's watcher keeps a start that reached the main menu even when no window asks. A game with a Kiln runtime older than
  0.2.0-dev.27 writes no progress.json: after the launcher's own steps the line says "Waiting for Kiln to boot" until
  its boot.json, then "Running" as before, and nothing is kept.
- **The history.** Starts are kept by the runtime's own version (progress.json's `kiln`, with its build stamp). A
  start of the launcher looks them up for its whole length by what its install's version identifies: the one runtime
  version kept under it, or the install's version itself when it carries a build stamp; when it identifies two builds,
  the start uses the other basis throughout (never a change of basis when progress.json appears). A file of a newer format, or one that cannot be
  read, is left as it is: no estimates, and one line in launcher.log. Times beyond a day in progress.json or the
  history make the file unreadable, never a crash. `start-times.json` is written under its own lock
  (`state/start-times.lock`) whenever a start reaches the main menu (the launcher's watcher keeps it even when no window asks), and goes with the data folder when
  it moves (on Windows not from the default folder, whose `state` is the system's: the history starts again there).
  Deleting it loses only the estimates; the next start is timed again.

## The first run and dropping a mod
For a player who wants one mod: install the launcher, it finds the game and offers Install Kiln, drop the mod on the
window, Play. The mod is in the list on Play, switched on.
- **Finding the game** (`shell/firstRun.tsx`, `useAutoDetect`): when no client is known, the shell runs `game_detect`
  once per start by itself, also when the initial game list fails. Play says "Looking for Enshrouded in your Steam libraries." (the mouth: "Looking"), then
  either the game, or "Kiln did not find Enshrouded…" with Add a folder and Look again.
- **Install Kiln on Play**: the game is there but Kiln is not: the mouth is lit and says Install; it (and the Install
  Kiln link) opens one confirm (`InstallKilnModal`: the version, Install), which runs the Runtime page's install task
  (`shell/runtimeChange.ts`: the newest client bundle, default parts) with its cones in place of the links. When it is
  done, `profile_ensure_default` makes "My mods" and activates it when there is no profile at all, and Play is ready
  ("No mods yet.", the list says how to add one). ShroudForge's switch stays on Runtime (typed).
- **Drop a mod anywhere** (`shell/drop.tsx`): files dragged over the window (Tauri's drag-drop events; HTML5 drag
  events in the browser mock) show the niche over a dimmed window, "Drop to add", "Added and switched on". The drop runs
  `library_add` (import, switch on in the loaded profile, write profile.json; contracts.md section 7 "Add a mod"). The toast
  is the mod's name and Undo (`profile_revert`; the library copy stays), "Needs <ids>." when requirements are missing,
  "Applies at the next start." while the game runs, and one short line about what was left out ("N files were left
  out.", or the reason when it is one sentence) with "Details" (`shell/importResult.tsx`: what was added, what was left
  out and why, what was not needed: `ImportReport.skipped` and `notes`). Before Kiln is installed: "Install Kiln first"
  with Install Kiln (the dropped files are added once the install is done). With profiles but none active in the game
  the drop goes to the profile the launcher remembers for it (after an uninstall and a reinstall of Kiln: that profile
  is loaded again, "Your mod list is loaded again."); with none remembered (the player chose "No profile") the toast says
  "Added <name>, switched off" with "Add to a profile": "Add to which profile?" lists every profile and "New profile…"
  (a name, then `profile_create` of an empty profile and `library_add_to_profile` into it); the toast after it has
  "Load it" (`profile_activate` in this game) when the picked profile is not the loaded one, and Undo. The dialog keeps
  the drop until the add worked (a profile made by "New profile…" whose add failed is listed, to pick again). One
  activation per game at a time (`api/oneActivation.ts`, inside `profileActivate`, `profileDeactivate`,
  `profileEnsureDefault`, `libraryAddToProfile` and `launchPlay`): another one while it runs is refused with "Kiln is
  still loading a profile in this game". Since 0.2.0-dev.29.18 Play does not wait for the click to say so: while an
  activation runs, whatever started it, the mouth (desktop and Deck) reads Play with "Applying profile…" under it
  ("Installing Kiln…" for `runtime_install` and `runtime_rollback`, "Removing Kiln…" for `runtime_uninstall`) and is
  off, and so are Vanilla and Without mods (a Vanilla or Play once dialog opened before says the guard's sentence
  instead of starting); it comes back when the activation ends or fails. `src/shell/applying.ts` decides it from two
  places only: the task list (`library_add`, `modpack_install`, `found_add`, `nexus_install`, `runtime_install`,
  `runtime_rollback`, `runtime_uninstall` queued or running; tasks name no game, so one holds Play in every game; the
  list is read once the `kiln://task` listener is in, so a task that ends in between is not left running) and the
  guard above (`useDirectActivations`, Play's own `launchPlay` aside; the guard lives in the page, so a reload while a
  Load runs forgets it). `tests/applying.spec.ts` holds each kind with `__kilnMock.holdTasks(kind, {gate, fail?})`. A
  drop's toast says "Updated <name>: <old> → <new>" (desktop and Deck) when the profile had another version of the mod
  (`AddReport.changes[].before`; `src/shell/addWords.ts`), "Added <name>" for a new one as before. A drop takes a
  zip, 7z or rar (`library::archive_kind`), a folder or a DLL. Modpack zips open the consent dialog (`ModpackDropOffer`: tick the box, then
  `modpack_install` with `{path}`). A drop on a DropZone (Runtime's bundle row) or while a dialog is
  open is not the window's; a covered or busy DropZone takes no drops. Overlapping identical drops run once; other
  window drops run in order. Settings "Switch new mods on" off: ordinary mods are only imported (in the list, off);
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
bun run test                        # unit tests (unit/, unit-tests/, scripts/*.test.ts*), no dev server
bun run test:schema                 # schema/tests/*.mjs (profile, closed-DLL and UI block schemas)
bun run test:smoke                  # Playwright, mock mode; screenshots to .out/launcher/shots/ (KILN_DEV_PORT: another port)
bun run build:admin-ui              # the admin pages' package @kiln/admin-ui into packages/admin-ui/dist (admin-pages.md)
bun x playwright test tests/catalog.spec.ts   # the catalog, desktop and Deck (?nexus=free|premium|nokey)
CARGO_TARGET_DIR=.out/launcher/cargo cargo test -p kiln-launcher
CARGO_TARGET_DIR=.out/launcher/cargo cargo test -p kiln-launcher --no-default-features   # the Nexus Mods build (no self-update)
tools/launcher/fetch-editor-assets.sh   # dev: local editor game assets for the vite dev server (gitignored)
```
Test runs against game copies: `KILN_DATA_DIR=<scratch>` and `KILN_STEAM_ROOTS=<fake Steam root>` keep the app away
from the real launcher data and Steam libraries, and `KILN_LAUNCH_DRY_RUN=1` makes `launch_play` do everything but open
`steam://` (which would start the real game). `KILN_LAUNCHER_NO_WEBGL=1` forces the fire's no-WebGL fallback (the plain
Play button) for one start, without touching the saved "Animated fire" setting. Linux: `KILN_LAUNCHER_RENDER=software`
or `--software-render` draws without the GPU ("Drawing without the GPU" below); `--no-webkit-sandbox` or
`KILN_LAUNCHER_WEBKIT_SANDBOX=off` turns WebKit's sandbox off ("WebKit's sandbox (Linux)"). GUI and Wine tests run on a private Xvfb display, never the desktop's. Debug builds only: `KILN_DEV_EVAL_DIR=<dir>` runs each `*.js`
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

## The data folder
Where the launcher keeps its data (the library, runtimes, modpacks, backups, profiles: one folder, moved as one;
contracts.md section 3 for the pointer, the journal, the markers and the lock).

**Settings, Mods & profiles, Library, "Data folder":** the path at once (mono, shortened in the middle), its size when measured
(`data_folder_size`), Change and Open. With `KILN_DATA_DIR` set, Change is off and says "Set by KILN_DATA_DIR.". Change
opens the folder dialog, then `data_folder_check`'s plan is one dialog: "Move the data folder?" (to, what moves, the
free space; a line only for a new folder inside the picked one, games that run in copy mode from there, entries Kiln
copies and leaves in the old folder with their sizes, and the crate's warnings), "Use this data folder?" (it already
holds Kiln data), or "Kiln cannot use this folder" with the crate's reason. The move runs as a task ("Move the data
folder": progress and Cancel, not dismissable); its last phase removes the old copy ("Removing the old copy. Cancel
leaves the rest for the next start."); a failed or canceled move says "Nothing changed"; done, the launcher restarts
(`data_folder_restart`), after the crate's warnings or "The data has moved. The rest of the old copy goes at the next
start." when there are any. Another data folder's profile in the game (`GameStatus.otherFolderProfile`: a launcher
before dev.26 after a move, `KILN_DATA_DIR`, another launcher) is said on Play's top line, in Settings' Profiles and on
the Deck's Profile row ("Another data folder's profile is loaded in this game"), never shown as loaded. Deck Settings has the same row and steps in the Deck's dialogs, with `deck_browse` as
the folder browser. Code: `src/components/DataFolderSettings.tsx`, `src/deck/DataFolder.tsx`, `src/shell/dataFolder.ts`
(the steps), `src/shell/dataFolderText.ts` (the words).

**The gate (`src/shell/DataFolderGate.tsx`, around both UIs in `main.tsx`):** `data_folder_get` is asked before anything
else mounts. A pointer to a folder that cannot be used: only "Kiln cannot open its data folder" with the path and the
reason, Choose folder (`data_folder_use`), Start fresh (`data_folder_reset`) and Quit. Another launcher moving the
folder (`elsewhere`): only "Kiln is moving its data in another window" and Quit. Neither can be dismissed, and with
nothing else mounted no page, toast or error boundary comes up. A backend without the command, or no answer in 5 s:
the launcher as before. At the first start after a move the notice says where the data is now, what Kiln left in the
old folder on purpose and what it could not remove (each with its size), and whether the removal runs or goes on next
time, until OK (`data_folder_notice_ack`).

**The process's data folder (`src-tauri/src/folder.rs`):** found once, the first time anything asks, and kept until
the process ends. `state::dirs()` refuses while it is held: a broken pointer (the crate's `data_folder` error), another
launcher's exclusive lock (`busy`), a move of this process (`busy`, until it fails or is canceled), or a committed move
(`busy` until the restart). In front of every Tauri command a gate (lib.rs `gated`) refuses what may not run then
(contracts.md section 9 lists what answers in each state), and `spawn_task` fails a new task at once while the folder is
held (except the move and the removal of an old copy). A move is refused while a task runs ("Kiln is still working on:
<title>."), and waits for the work already using the folder to end: closures in `state::blocking`, and every writer
outside it and outside a task, which takes `state::writing()` before the folder (the watcher's round, `launcher_log`,
the self-update's `update.json`, the Nexus inbox reader and start-up cleanup, the shipped runtime's import).

**Start-up, in this order (lib.rs setup), before any watcher:** the folder is located without failing (nothing is
created for a broken pointer; a missing default or `KILN_DATA_DIR` root is made, as before); the lock is taken shared
(held exclusively elsewhere: the dialog); a broken pointer or another launcher's move stops here: no watcher, no
import, no update check, no file written. Then a move the journal records. An unfinished one: nothing is removed
during setup; the folder is held as during a move (its proof is a file identical to the one in the folder in use, so
nothing may write that folder), the task "Remove the unfinished copy" starts two seconds later, and the window shows
only "Removing an unfinished copy" with the folder, the task's progress ("checking <file>") and Cancel ("Cancel
leaves the rest for the next start; Kiln opens as usual."). The task is `data_folder_move::remove_partial_copy_with`:
a file goes only when the folder in use holds the identical one; everything else stays, named with the crate's
reason. When the task ends, however it ends, the hold ends: the held log lines are written, the steps below run, the
launcher mounts, and the notice "Move not finished" names what is left (path, size, why), where the unfinished copy
is, and Open; a canceled one says the rest goes at the next start. While anything is left the journal stays, the
Settings row says so instead of Change, and every start tries again; once the player has taken the named files out
(or removed the folder) the next start finishes. A committed one not acknowledged
yet gets the task "Remove the old data folder" two seconds later (what the move's own removal left, or the lock files and the empty old folder
the moving process still held; in the task tray, can be canceled, goes on at the next start). A removal the crate
refuses (a game's profile still names the old folder) says its reason in the notice, and the ack keeps the journal;
an unfinished move whose partial copy could not be removed names what is left, and the next start tries again.
Then (lib.rs `after_folder`, at once, or after the unfinished copy's task) the file-hash cache, the watcher, the shipped
runtime, the modpack watcher, Nexus, the self-update. While an earlier move is not finished, the Settings row says why
(`DataFolder.pending`) instead of offering Change.

## Drawing without the GPU (Linux)
`src-tauri/src/render.rs`, `src/shell/started.ts`. The window's background is `#080504`: a window that stays that colour
is a webview that never painted the page (a player on NVIDIA / X11 with 0.2.0-dev.23; the cause there is not known).
- **The page says it started**: `app_started`, once per start, after React's first commit and two frames, in the
  desktop and the Deck UI alike (`<Started />` in `main.tsx`). A page that is hidden at the start, or hidden before its
  frames came (the window minimized or on another workspace), says so at once: it gets no frames until it is shown,
  and nobody is looking at a black window then.
- **Software mode**: before GTK starts the launcher sets `WEBKIT_DISABLE_COMPOSITING_MODE=1`,
  `WEBKIT_SKIA_ENABLE_CPU_RENDERING=1` and `LIBGL_ALWAYS_SOFTWARE=1` (a value the user set stays), and gives the webview
  WebKit's hardware acceleration policy Never and no WebGL (WebKitGTK 2.54 still offers WebGL without compositing,
  through the GPU's EGL driver). The page then draws the CSS mouth instead of the fire (`app_info.noWebgl`); the admin
  map says WebGL2 is missing. Programs the launcher starts (Steam when it was closed, the browser) do not get the
  variables the launcher set itself (`KILN_LAUNCHER_RENDER_SET` names them; `kiln_manage::hostenv` takes them back);
  a value the user set goes along as before.
- **WebKit's DMA-BUF renderer** (0.2.0-dev.29.19): on by default again, so WebKitGTK composites on the GPU (layers;
  a change repaints only itself). Up to 0.2.0-dev.29.18 the launcher always set `WEBKIT_DISABLE_DMABUF_RENDERER=1`
  (for "Gdk Error 71 (Protocol error)" at the start, seen on GNOME + NVIDIA), which turned compositing off: every change
  was painted again on the CPU with everything over and under it, the Linux launcher's lag in large windows
  ([perf.md](perf.md)). Now:
  - a start that uses it because nothing asked otherwise writes `<data>/state/dmabuf-pending.json` right before Tauri,
    GTK's window and WebKit start (after the sandbox message, whose restart or Quit is no crash), and removes it 10 s
    (`render::SETTLE`) after the page said it started, at a clean exit after that, or before any restart. A start that
    finds it left behind (that start died: the Gdk Error 71 case ends the process) draws without the DMA-BUF renderer,
    says so on stderr and in `launcher.log` (`PROBLEM: WebKit's DMA-BUF renderer off: the last start with it ended
    before its page had started ...`, with how to undo it) and, once the beta copy's gate opens, writes the sticky
    `<data>/state/dmabuf-off.json` (`{format, why, at, version}`, "why" names the dead start's time and version) and
    removes the pending one;
  - with the sticky marker a start draws with the GPU without the DMA-BUF renderer (`WEBKIT_DISABLE_DMABUF_RENDERER=1`,
    the old default, which worked for everyone who had a working launcher before);
  - precedence: the user's own `WEBKIT_DISABLE_DMABUF_RENDERER` (any value but `0` is off, as WebKit reads it), software
    mode (off, as before), `--dmabuf` / `--no-dmabuf` (the last one given wins), `KILN_LAUNCHER_DMABUF=on|off`, the
    watchdog's restart (`KILN_LAUNCHER_DMABUF_RESTARTED`), a pending marker left behind, the sticky marker, then on. Only
    the default start is guarded and may step down; a DMA-BUF renderer the user asked for is theirs;
  - Settings > Appearance, "Draw the launcher without the GPU" turned off removes the sticky marker too (GPU drawing
    back on means "try the default again"); no new switch;
  - like software mode's variables, a `WEBKIT_DISABLE_DMABUF_RENDERER` the launcher set itself is named in
    `KILN_LAUNCHER_RENDER_SET`: programs of the host never get it (`kiln_manage::hostenv`), a launcher it starts again
    decides afresh. (A first start after an update from 0.2.0-dev.29.18 or older, restarted by that launcher, still
    inherits its unnamed `=1` and reads it as the user's, for that one start.)
- **The fallback**: when the page has not said it started 15 s after the window was made (30 s after the start when no
  window came), a start with the DMA-BUF renderer by default (and the GPU by default) writes
  `<data>/state/dmabuf-off.json` and starts again the same way with `KILN_LAUNCHER_DMABUF_RESTARTED=1`: the GPU without
  the DMA-BUF renderer. Only a start already without it (that restart, the sticky marker, a choice), drawing with the GPU
  by default, writes `<data>/state/software-render.json`
  (`{format, why, at, version}`), logs why and starts again in software mode, in the same process (exec): `$APPIMAGE`
  when it is set (the AppImage file, with the environment a fresh start has), else the executable, with the same
  arguments. A start in software mode, or with a mode the user forced, never restarts: it writes
  `PROBLEM: the page did not start ...` to `launcher.log` and stderr and (0.2.0-dev.29.18) shows the launcher's own
  message instead of leaving an empty window, also when the restart itself failed ("WebKit's sandbox (Linux)" below,
  "The messages"). So the steps are: DMA-BUF on, then the GPU without DMA-BUF, then software; one restart per step at
  most, never a loop (each restart's variable says its step, so a marker that could not be written does not repeat it;
  the restart's own marker is then written once the gate opens).
- **X11 on Wayland** (0.2.0-dev.30; a player on KDE Wayland + NVIDIA: WebKit turned hardware acceleration off by itself,
  "PROBLEM: WebKit turned hardware acceleration off by itself", and with `GDK_BACKEND=x11` the lag was gone): on a
  Wayland session with XWayland (`WAYLAND_DISPLAY` and `DISPLAY` set), when the webview's policy reads Never in a start
  that draws with the GPU and no variable explains it (`WEBKIT_DISABLE_COMPOSITING_MODE` / `WEBKIT_DISABLE_DMABUF_RENDERER`),
  on a screen whose GDK scale factor is above 1, the launcher starts again once with `KILN_LAUNCHER_X11_RESTARTED=1`; that start sets `GDK_BACKEND=x11` itself (named in
  `KILN_LAUNCHER_RENDER_SET`: programs of the host, the game and the tools never get it).
  - X11 does not bring the GPU back (Oryx593's `GDK_BACKEND=x11` starts read policy Never and "WebKit composites: no" too):
    it is faster because XWayland draws a HiDPI window at scale 1 (1024 x 640 at devicePixelRatio 1 against 1280 x 800 at
    2; p50 16 ms against 43-73 ms a frame on Play), less sharp. So at scale 1 the launcher does not switch (as many pixels
    on the CPU; a marker would stick for nothing). The launcher never sets the policy Never itself with the GPU (only
    software mode does); before 0.2.0-dev.29.22 the policy line was logged in software mode only. The PROBLEM line names
    the DRM render nodes and `nvidia_drm modeset` (WebKitGTK 2.50 needs a GBM device on a render node);
  - that X11 start writes `<data>/state/x11.json`, later starts go straight to X11; also when WebKit still composites
    nothing through X11 (work-cm 2026-11-20: Oryx593's lag was gone under `GDK_BACKEND=x11` although the torch stood
    still, so X11's painting on the CPU was far better than Wayland's);
  - the page late through X11: `x11-no-help.json` and a restart on Wayland (`KILN_LAUNCHER_X11_RESTARTED=0`: X11 is not
    tried again in that chain), whose own late page then takes the steps above (DMA-BUF, then software);
  - precedence: the user's own `GDK_BACKEND` (any value, never changed), no Wayland session with XWayland, software mode,
    the way back's `=0`, Settings off, the switch's `=1`, the no-help marker, the X11 marker, then GTK's default.
    Settings > Appearance, "Switch to X11 when drawing is slow on Wayland" (`app_render_x11_set`, on by default): off
    writes `state/x11-auto-off.json` and removes `x11.json`; on removes it and `x11-no-help.json` (try again). Markers
    are written once the beta copy's gate opens. `launcher.log` has one `render: X11 ...` line per decision.
- **Ubuntu WebKit's NVIDIA block** (0.2.0-dev.30.7): the AppImage bundles Ubuntu's WebKitGTK (libwebkit2gtk-4.1-0
  2.50.4-0ubuntu0.22.04.1 in 0.2.0-dev.30.5), which carries Debian's `disable-nvidia-dmabuf.patch`: when GL_VENDOR names
  NVIDIA, its DMA-BUF renderer gets no buffer transport at all, so WebKit composites nothing (policy Never) and prints
  nothing about it (a player's RTX 5080 on Wayland, 0.2.0-dev.30.6). So:
  - the PROBLEM line's why names this cause first after the variables when the loaded libwebkit2gtk carries the patch
    (it has the string `WEBKIT_FORCE_DMABUF_RENDERER`; upstream WebKit reads no such variable) and GTK's GL context says
    an NVIDIA GL_VENDOR (without one: NVIDIA's kernel driver is loaded). The line also gives the GL vendor and the
    library's path. The support report's compositing line carries the same why;
  - GLib's warnings and criticals (GTK's, GDK's, WebKit's UI process) go into `launcher.log` as `glib:` lines from the
    start of `main`, 40 at most a start, one line each, as well as on stderr as before;
  - Settings > Advanced > Drawing the launcher, "Force GPU drawing on NVIDIA" (`app_render_force_set`, writes
    `state/force-dmabuf.json`), **off by default** (`render::FORCE_DEFAULT`, one constant: real NVIDIA driver bugs sit
    behind the patch's guard): on sets `WEBKIT_FORCE_DMABUF_RENDERER=1` with `WEBKIT_DMABUF_RENDERER_FORCE_SHM=1`
    (shared-memory buffers, no GBM buffers between WebKit's processes) from the next start. The plain forced renderer is
    not offered: it can fail with a Wayland protocol error before its page starts. Shared memory is the variant on X11
    too; wider hardware coverage is still needed.
  - Clean starts leave this off. On Linux with the NVIDIA kernel driver loaded and no explicit preference, a normal
    GPU start writes `state/nvidia-frame-pending.json`. If that start expected a visible window but never confirmed a
    painted frame, the **next** start tries both SHM variables once. Internal DMA-BUF fallback markers do not prevent
    this attempt; explicit software/no-DMA-BUF flags, environment overrides, a manual off choice, and non-NVIDIA
    machines do. A corrupt/unreadable preference is treated as off, and only a valid, matching success record can
    remember SHM. Once visible, the recovery attempt explains why it was chosen. A process-held
    `state/render-startup.lock` keeps a second launcher from treating a live attempt as a crash or changing its markers;
    if that lock is unavailable, this start applies no SHM override or marker changes. A blank default start may still
    restart once in software using only its restart variable, even when the state folder cannot be written. A data
    folder that is held at the start (a pointer to a folder that cannot be used, another launcher's move, an unfinished
    move's copy that this start removes first) is treated the same way: no lock, no guard, no recovery read from its
    files, nothing written there in that run.
  - The SHM attempt has a different guard, `state/force-dmabuf-pending.json`, written before either variable is set or
    WebKit is used (including the sandbox probe). If it cannot write that guard, it forces nothing. If this attempt
    also dies or times out, the next start forces nothing, turns the switch off, and writes
    `state/force-dmabuf-failed.json` with why and when. It does not alternate indefinitely between failed renderers.
  - `app_started` is only page readiness. `app_render_frame` reports visibility separately and confirms startup only
    after two consecutive visible animation frames, checking the native window is shown and not minimized as well as
    the document. Hiding between callbacks resets that confirmation; the IPC independently rejects a hidden window.
    Hidden/minimized starts are not considered failed visible attempts. While the data folder is held (its dialog over
    the window, or a move) the page sends no `app_render_frame` and the backend writes no renderer state: once the hold
    ends, the page reports its visibility and two fresh visible frames, and that confirmation clears the guards as
    usual; a hold that does not end in that run (a committed move, Quit) leaves them as they were. A
    successful frame clears the pending guards after 10 seconds (or on a clean exit). Successful automatic SHM recovery
    is remembered in `state/force-dmabuf-recovered.json`, separately from a manual opt-in, keyed by the stable executable
    or AppImage path and GTK display backend. That working automatic renderer is used on subsequent matching starts.
    An explicit manual off clears that memory and vetoes further automatic retries; manual on permits another try.
  - Turning the switch off or on removes the failure note and automatic memory. On also removes `dmabuf-off.json`
    (without that renderer there is nothing to force) and `x11.json` (the X11 switch can still act if WebKit composites
    nothing). The switch's More text explains automatic recovery and how to disable it.
  - The user's own `WEBKIT_FORCE_DMABUF_RENDERER` or `WEBKIT_DMABUF_RENDERER_FORCE_SHM` always wins; software mode and a
    DMA-BUF renderer that is off force nothing. The launcher never suggests `WEBKIT_DISABLE_DMABUF_RENDERER=1`, chmod or
    root for this. Both variables the launcher sets are named in `KILN_LAUNCHER_RENDER_SET`, so kiln-manage's host
    environment takes them away from every program it starts (the game through Steam, servers, Wine, SteamCMD).
- **Choosing the mode** (the DMA-BUF renderer has its own order, above): `--software-render` / `--gpu-render` (the last one given wins), then
  `KILN_LAUNCHER_RENDER=software|gpu`, then the marker, then the GPU. Settings > Appearance, "Draw the launcher without
  the GPU", sets or clears the marker (`app_render_set`) for the next start. A start with an nxm link takes no other
  argument (main.rs; neither `--dmabuf` nor `--no-dmabuf`). The marker is read from the data folder as found (`folder::found_dirs`, read only): 0.2.0-dev.29
  to 29.17 read it through `state::dirs()`, which refuses until the beta copy's gate opens (after the window exists),
  so those starts never saw it. A marker written then (by Settings, or by a fallback whose next starts drew with the
  GPU again) takes effect from 0.2.0-dev.29.18: such a start says on stderr and in `launcher.log` that it stays so until
  the switch is off, and the switch's hint says the same.
- **What it says**: at the start, one line on stderr with the log's path and the mode. `launcher.log`, `render:` lines:
  the mode and why, the WebKitGTK version, `XDG_SESSION_TYPE` and whether `DISPLAY` / `WAYLAND_DISPLAY` are set, the
  first line of `/proc/driver/nvidia/version` (or that there is none), `WEBKIT_DISABLE_DMABUF_RENDERER` with the DMA-BUF
  decision and why (default, yours, software mode, an override, the restart, a dead start, the marker with its sentence),
  WebKit's sandbox and the probe of bwrap, when the page started, a web process that ended. Lines from before the beta copy's gate opens (the start's own) go to stderr at
  once and into `launcher.log` when it opens (`flush_early_log`; a start that restarts or quits before that leaves them
  on stderr only). The support report's summary.txt has a `Launcher:` line with the mode and a `Launcher DMA-BUF:` line (the decision,
  why, the sticky marker's sentence); `app_info.render.dmabuf` has the same for the UI.
- **Tests**: `cargo test -p kiln-launcher render::`, `bun test unit/started.test.ts`. Debug and QA (`dev-eval`) builds
  only: `KILN_LAUNCHER_TEST_WITHHOLD_READY=gpu|software|all` ignores `app_started` in that mode, so the fallback can be
  watched on a machine that renders (on a private Xvfb display); `KILN_LAUNCHER_TEST_EXIT_AFTER_WINDOW=dmabuf|all` ends
  the process 2 s after the window was made without exit handlers (`dmabuf`: only a start that uses the DMA-BUF
  renderer), as the Gdk Error 71 crash does.
- **Troubleshooting a black or empty window** (what players are pointed to): start the launcher from a terminal; its
  first line names `launcher.log`. Within 15 s it should start again without the DMA-BUF renderer by itself, and 15 s
  after that, if still empty, without the GPU. A launcher that closes at once ("Gdk Error 71 (Protocol error)") starts
  without the DMA-BUF renderer the next time by itself; `--no-dmabuf` does it from the first start. If the window stays
  empty, the launcher shows a message (0.2.0-dev.29.18) with what it tried, where `launcher.log` is and what is left to
  try ("Start without the GPU", "Start without WebKit's sandbox" when WebKit runs it, "Open the log folder", "Quit").
  Without that message, start it with `--software-render` (`./Kiln*.AppImage --software-render`, or
  `kiln-launcher --software-render` for the .deb). Send `launcher.log` (the `render:`, `sandbox:` and `message:` lines),
  the terminal's output and, once the window works, a support report. The launcher ending at once with a `bwrap:` line
  and "Failed to start dbus proxy" in the terminal is WebKit's sandbox: see the next section. A black window that GNOME
  Web shares (it hangs too) is often a broken font cache: `fc-cache -r`, or move `~/.cache/fontconfig` away, then start
  the launcher again (0.2.0-dev.31.14 offers this itself: the subsection after next).

### When the page's web process ends (Linux) (0.2.0-dev.31.13)
`src-tauri/src/webproc.rs`, `src/shell/closeGuard.ts`. WebKitGTK runs the page in its own process (`WebKitWebProcess`).
When that process ends after the page started (WebKit's `web-process-terminated`: `Crashed`, `ExceededMemoryLimit` or
`TerminatedByApi`), up to 31.12 the window stayed black and its close button did nothing: the page's close guard listens
to `tauri://close-requested`, so Tauri holds every close for the page, and a dead page never answers (31.10, at load 54;
only SIGTERM ended it).
- **Recovery**: the first end loads the page again (a new web process). A second end within 60 s of that reload puts a
  native GTK screen in the window instead of the webview: "The launcher's page crashed.", where `launcher.log` is, and
  **Reload** / **Quit**. Reload gives the page one more try (another end within 60 s shows the screen again); an end
  long after a reload reloads by itself again. Never a loop. The screen's words are English only (the page and its
  translations are what crashed). A webview that is not in a GTK box (never seen with wry 0.57) gets the same words and
  buttons as a message over the window instead.
- **Closing always works** (Linux only: on Windows and macOS Tauri and the page's close guard close the window exactly as
  before 31.13): while the page is dead (ended, not yet started again) or the screen is up, closing the window quits at
  once. Otherwise the page gets the close and its listener answers first (`app_close_seen`): `asking` when it asks about
  unsaved work (the player decides), else it closes the window. Without an answer within 5 s, a second close before the
  answer, or a page that said it closes and did not: the launcher quits without the page (`close:` line in
  `launcher.log`). A page that has said it has unsaved work (`app_close_unsaved`, sent at its start and whenever that
  changes) is never quit that early: it gets 30 s to answer, and more clicks meanwhile neither force the close nor make
  the wait longer; a page that ended lost its work, so it closes at once. Both commands answer behind every data folder
  gate. The launcher quits once: a forced close, the screen's Quit or any other exit after one has begun quits nothing
  more. The launcher has no tray; closing the window quits it, as before.
- **What it says**: one `render:` line per end, beginning as before 31.13 (`PROBLEM: the page's web process ended
  (Crashed)`), then the web process's pid and resident memory at the last sample (every 10 s from `/proc`, also behind
  bwrap) with the largest this start, the launcher's and this page's uptime, `/proc/loadavg` with the CPU count,
  MemAvailable of MemTotal, the end's number and the next step. A core dump, if any, is WebKit's
  (`coredumpctl list WebKitWebProcess`).
- **Running work keeps running**: only the page loads again. The backend (tasks, downloads, installs, a dedicated server
  it started, the watchers) is the same process and never stops; the new page reads its state back through the same
  commands as at a start (task list, server status, the game's state). What only the dead page used goes with it at the
  end: its log follows (`logs_follow`) and server consoles (`server_console_open`), which a crashed page never closes.
  Starting a server or an install twice is refused by the backend as before (a running server's pid, the install lock).
  A start Steam was asked for and no game yet (or a direct start's) is the new page's wait too: it asks
  `launch_progress` once per game and shows the wait and "Stop waiting" instead of Play (`askedSteamAt` and
  `seededLaunch` in `shell/startProgress.ts`), with the start's mode: a Vanilla start is found too and shown as
  Vanilla, without a bar (0.2.0-dev.31.13: see "One start at a time" below). The update notice's Later
  (and a hold for good) is kept for this run of the launcher (sessionStorage), so the new page does not count down to a
  restart the player put off.
- **One start at a time** (0.2.0-dev.31.13, `commands/launch.rs` `refuse_pending`): the backend tracks a Vanilla
  start ("Just once": Vanilla) as it tracks a Kiln start (`launch_begin` with mode `vanilla`: its profile and Steam
  steps, the wait for its process; its record goes once its game is seen, and nothing of Kiln is looked for), and
  `launch_begin`, `launch_play` and `launch_confirm` refuse a second start of either kind while one is pending: Steam
  asked (or the direct start made) and no game process yet, within the window's 5-minute wait, or another Play inside
  its own start. `launch_begin` checks again as it records its start, under the same locks as `launch_play`'s claim, so
  a Play that asks Steam while the new record is built keeps its start; and a game whose process runs already is
  refused as `game_running` first, as `launch_play` refuses it (the watcher may not have seen it yet). The refusal is
  `busy` "The game is already starting." (`detail.kind` `already_starting`): Play shows it, does not end the pending
  start (no `launch_end`) and shows that start's wait again (`isAlreadyStarting`). The hold ends when the game is
  seen (then the game-running checks refuse a second start, as before), after the 5 minutes (Play is offered again and replaces the start, as before), with Stop waiting or a Play that failed
  (`launch_end`), and when the start itself fails. Nothing holds a start before Steam is asked (the cook, the warning
  before Play): the page that pressed Play holds it, as before. A Vanilla start waiting for Steam is said as before
  (no bar), now for the same 5 minutes as a Kiln start (it was 2), so Play is not offered while the backend would
  refuse it. The update's restart waits for a Vanilla start too, as the window's notice already did (the browser
  mock's too: `mockStartTracked`).
- **Tests**: `cargo test -p kiln-launcher --lib webproc` (the retry window, the close decisions, the line, `/proc`
  parsing); `bun test unit/close-guard.test.ts unit/startProgress.test.ts unit/update-apply.test.ts` (the answer before
  any question, the unsaved report, the seeded wait and its mode, the refusal told apart, Later);
  `cargo test -p kiln-launcher --lib guard_tests` (the hold of either kind and when it ends, launch_begin's second
  check, `game_running` first); `bun test unit/vanilla-guard-mock.test.ts` (the mock's restart waits for a Vanilla
  start); Playwright
  `tests/vanilla-guard.spec.ts` (a reload during a Vanilla start's wait for Steam shows the wait, not Play; a refused
  Play ends nothing). Debug and QA (`dev-eval`) builds only: `KILN_LAUNCHER_TEST_KILL_PAGE=<seconds>` ends the web process
  (SIGKILL: WebKit says `Crashed`) that long after each page start: the first end reloads, the second shows the screen.

### A window that can't draw: a broken font cache (Linux) (0.2.0-dev.31.14)
`src-tauri/src/fonthang.rs`, `src-tauri/src/dialog.rs` (`hint`), the watchdog in `src-tauri/src/render.rs`. A player
(ZorinOS 18.1, kernel 7.0) saw only a black window in the AppImage and the .deb: an eu-stack of the stuck
`WebKitWebProcess` showed one thread in fontconfig's `FcPatternGetString` (called from libwebkit2gtk-4.1) and the rest
in `pthread_cond_timedwait`. Clearing the fontconfig cache fixed it; GNOME Web hung the same way. No renderer change
helps there, and up to 31.13 the watchdog's restarts (and their sticky markers) were all the launcher did.
- **When it looks**: at the watchdog's late point, the page not started and the window made: 15 s after the window, as
  before, whatever the box's load (a renderer problem, the common black window, gets its restart as soon as before; the
  load goes into the `fonts:` line, since a thread on a busy box gets less than half the CPU and rule A stays away).
- **What it looks at** (cheap reads of each web process below the launcher, behind bwrap too; no ptrace, no eu-stack, no
  signal): two looks 2 s apart at `/proc/<pid>/task/*/stat` (state, CPU time, page faults), `status` (voluntary
  switches) and `io` (read/write system calls) of every thread; between them `/proc/<pid>/clear_refs` (`1`: below), then
  `smaps` (libfontconfig's code mapping and its Referenced kB, the font cache files mapped, `*.cache-N`), `fd` (a font
  cache file or its `.LCK`/`.NEW`/`.TMP` held open), `wchan` and `stack` (read when readable, usually not: for the log).
- **`clear_refs`** (`1`): the kernel clears the accessed bits of the process's page table entries and the referenced
  flag of the pages it maps, then flushes its TLB: one walk of its page tables under the mm's read lock, once per look
  (12 to 16 ms for a test process with 1 GB resident; a web process holds a few hundred MB). Nothing of the process's
  memory, data or soft-dirty bits changes; the only effect is that its pages look unused to reclaim for a moment (the
  referenced flag of shared library pages is the system's, so other processes' pages too). The file is mode 0200 and the
  process's own: the same user only, and not for a process that made itself non-dumpable (then root's: refused, so `io`
  is unreadable too and the verdict is not conclusive). It is kept because it discriminates: a python3 loop outside
  fontconfig with the library loaded showed 0 kB of its code touched in three looks of four (64 kB once, set by other
  processes), one inside `FcPatternGetString` 12 to 76 kB every look.
- **The rules** (`fonthang::decide`): the font cache hint only on evidence in fontconfig:
  - A: the process's main thread running at both looks, on the CPU at least half of the 2 s, without one read or write
    system call, without one voluntary switch and with at most 100 page faults in between (a font scan maps each font
    file: tens of thousands; a loop over a mapped cache: none), with 8 s of CPU in all; its other threads together at
    most 0.2 s; and libfontconfig's code touched in the gap. Referenced alone proves little (it also counts a page's
    shared flags, which other processes using the library set: a sleeping test process showed 32 kB), so the main
    thread's loop and the idle rest carry it;
  - B: a font cache file held open at both looks while all threads together ran at most 0.2 s;
  - anything else (no web process, a loop elsewhere, a font scan making system calls or faulting pages in, a thread that
    blocked, a busy box where the thread got less CPU, `io` or `clear_refs` not allowed) is not conclusive: the watchdog
    goes on as before (its restarts), and its last message lists the font cache step as one try ("Rebuild the font cache
    and restart", with "run fc-cache -r, or remove ~/.cache/fontconfig, then restart the launcher").
- **The hint** (native GTK over the window through its main loop; the desktop's tools when that loop does not answer
  within 5 s, zenity, then kdialog; English only, like the other messages): "The window can't draw: your font cache
  looks broken. Run fc-cache -r, or remove ~/.cache/fontconfig, then restart the launcher.", what the launcher saw, what
  the fix does, `sudo fc-cache -r` for the system's cache if it still can't draw, and `launcher.log`'s path. Buttons:
  - **Fix it and restart**: renames the user's font cache folder to `fontconfig.broken-<YYYYMMDD-HHMMSSZ>` next to it
    (`-2`, `-3` when taken; never deleted; a symlink is renamed itself, its target is left alone), runs the host's
    `fc-cache -r` when installed (without the AppImage's environment; 120 s at most, else stopped: fontconfig then
    builds what is missing at the next start), then starts the launcher again (exec, with the same arguments and the
    watchdog's restart variables, plus `KILN_LAUNCHER_FONTS_FIXED=1`). The folder is the one fontconfig uses:
    `$XDG_CACHE_HOME/fontconfig` when that is set and not empty (fontconfig takes a relative value from the current
    folder, unlike the XDG spec; so does the launcher), else `$HOME/.cache/fontconfig`;
  - **Keep waiting** (Enter; also closing it): the hint goes, the launcher looks again 30 s later (the hint again on the
    same evidence, else on as before);
  - **Quit**.
  A page that starts while the hint is up closes it by itself. The watchdog's restart is skipped on the specific hint
  only: a renderer marker is never written for a font cache.
- **Once per chain of starts**: a start after the fix (`KILN_LAUNCHER_FONTS_FIXED`, read and removed before anything
  else, carried along by the watchdog's later restarts) never shows the hint again: the page still not starting means
  the evidence was wrong or the cause lies elsewhere (the system's cache, another loop), so the watchdog goes on as
  before (its renderer steps, then its last message). Without that, a wrong verdict would offer the same fix forever and
  never reach the renderer step that may help.
- **Without buttons**: when only notify-send could show the hint (the main loop did not answer, no zenity or kdialog),
  the watchdog goes on as before at once instead of sending the notice again every 30 s.
- **What it says**: one `fonts:` line per look in `launcher.log` (the late point, then everything both looks saw:
  threads, the main thread's state, CPU, system calls, switches and page faults, the box's load, libfontconfig and how
  much of its code was touched, the font caches mapped and how many in the user's folder, files held open, wchan, kernel
  stack, the verdict and the next step), and one per choice (what was seen, the player's choice and, for the fix, what
  was moved where and how `fc-cache -r` went). The hint's text goes to stderr and `message:` lines as every message's.
- **Not on Windows or macOS**: `fonthang` is Linux only; the watchdog and the messages already were.
- **Tests**: `cargo test -p kiln-launcher --lib fonthang` (the decision table on fake `/proc` trees, the parsers, the
  XDG folder as fontconfig reads it, the hint once per chain of starts, what each answer leads to, the fix against a
  temp HOME with a stand-in for `fc-cache` (also through a symlink), fc-cache failing or hanging, the hint's words); `--
  --ignored live_processes_follow_the_rules` runs the rules on real processes of the test's own (python3 spinning in
  `FcPatternGetString`: the hint; a shell's loop and a sleeping python3: not conclusive);
  `dialog::tests::the_font_cache_hint_never_fixes_on_enter`,
  `sandbox::tests::the_late_message_lists_the_font_cache_step`. Debug and QA (`dev-eval`) builds only:
  `KILN_LAUNCHER_TEST_FONT_HINT=fonts|unclear` takes the probe's place, so the hint can be seen on a private Xvfb
  display with `KILN_LAUNCHER_TEST_WITHHOLD_READY`.

## WebKit's sandbox (Linux)
`src-tauri/src/sandbox.rs` (the choice, the probe, the messages' words), `src-tauri/src/dialog.rs` (the messages),
0.2.0-dev.29.18. The launcher never turns the sandbox off by itself: only the player's click, flag, variable or
Settings does.

**What WebKitGTK does** (read in its source at the tags webkitgtk-2.38.0 to webkitgtk-2.54.1: `BubblewrapLauncher.cpp`,
`ProcessLauncherGLib.cpp`, `WebProcessPoolGLib.cpp`, `WebKitWebContext.cpp`; and seen in the process tree):
- Its sandbox starts the web process, and `xdg-dbus-proxy`, inside bubblewrap: `bwrap` at the path WebKit was built
  with (`BWRAP_EXECUTABLE`: `/usr/bin/bwrap` on Debian, Ubuntu and Arch). bwrap is not setuid there, so it creates a user
  namespace, then uts, ipc, pid and (for the web process) net namespaces, binds /etc and the system's libraries read
  only and adds a seccomp filter. The network process is never sandboxed.
- WebKitGTK runs it by default only for its GTK 4 API (webkitgtk-6.0, `ENABLE(2022_GLIB_API)`). The launcher uses the
  GTK 3 API (webkit2gtk-4.1, through wry), where it is off unless the program calls
  `webkit_web_context_set_sandbox_enabled(TRUE)` (wry and Kiln never do) or `WEBKIT_FORCE_SANDBOX=1` is set. **A default
  start does not use the sandbox**: the 29.17 AppImage (its WebKitGTK 2.50.4) and a build against Arch's 2.54.1 run
  WebKitWebProcess as a plain child of the launcher. With `WEBKIT_FORCE_SANDBOX=1` the same build runs it under
  `/usr/bin/bwrap` (and the web context says so).
- `WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS` exists since 2.39.4 (2.40 stable): 2.40 to 2.44 read only the value `1`;
  since 2.46 any value but `0` turns the sandbox off, and it wins over `WEBKIT_FORCE_SANDBOX=1`. Before 2.40,
  `WEBKIT_FORCE_SANDBOX=0` turned it off; since then a value other than `1` only warns.
- When bwrap cannot start, WebKit ends the whole program (`g_error`: SIGTRAP, a core dump), with bwrap's own line on
  stderr ("bwrap: setting up uid map: Permission denied" under Ubuntu 24.04's restriction; "bwrap: Creating new
  namespace failed: Permission denied" where user namespaces are not allowed at all) and then "Failed to start dbus
  proxy" or "Failed to fully launch dbus-proxy". No empty window and no watchdog line: the launcher is gone. GNOME Web
  and other WebKitGTK programs crashed exactly so on Ubuntu 24.04 (Launchpad bug 2046844).
- Which bwrap: the .deb, the rpm and a plain build use the system's WebKitGTK, so the system's bwrap. The AppImage
  carries Ubuntu 22.04's WebKitGTK with every "/usr" in it replaced by "././" (Tauri's AppImage bundler; AppImageKit's
  AppRun changes into `$APPDIR/usr`, which is how WebKit finds its helper processes there), so its bwrap is
  `$APPDIR/usr/bin/bwrap`, and the AppImage has none. With `WEBKIT_FORCE_SANDBOX=1` the 29.17 AppImage ends at once:
  `Failed to start dbus proxy: Failed to spawn child process "././/bin/bwrap" (No such file or directory)`.
- Ubuntu 24.04 sets `kernel.apparmor_restrict_unprivileged_userns=1` at boot (`/usr/lib/sysctl.d/10-apparmor.conf`,
  with the AppArmor userspace). An unconfined program that creates a user namespace is moved into the
  `unprivileged_userns` profile, which denies every capability, so bwrap cannot write its uid map. A program needs an
  AppArmor profile with a `userns` rule; Ubuntu ships such profiles (unconfined, `userns,`) for its own WebKitGTK
  programs: `/etc/apparmor.d/epiphany`, `devhelp`, `geary`, `evolution`. Sources: AppArmor's wiki,
  "unprivileged_userns_restriction"; Ubuntu Discourse, "Understanding AppArmor User Namespace Restriction"; Ubuntu's
  blog on 23.10's restricted user namespaces; Launchpad bug 2046844.
- So in a default start the AppArmor restriction cannot stop the launcher's page, and
  `WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1` changes nothing. An empty window on such a system has another cause.

**The choice** (`sandbox::decide`, as the render mode's): `--no-webkit-sandbox` / `--webkit-sandbox` (the last one given
wins), then `KILN_LAUNCHER_WEBKIT_SANDBOX=off|on`, then a message's restart (`KILN_LAUNCHER_WEBKIT_SANDBOX_RESTARTED`,
set only by the launcher for that restart), then `<data>/state/webkit-sandbox.json` (`{format, why, at, version}`),
then on. Off: before GTK and WebKit start the launcher sets `WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1` and names it in
`KILN_LAUNCHER_SANDBOX_SET`, so a restart tells it from the player's own and programs the launcher starts (a browser,
Steam) never get it (`kiln_manage::hostenv`). A value the player set is left alone whatever the choice; the log says
"(yours)". Without a click, a flag, the variable or the marker, the launcher never sets it (tested).

**The probe** (every start on Linux, before the window, at most 2 s; about 4 ms for bwrap itself): the bwrap path is
read from the loaded libwebkit2gtk's `.rodata` (a relative one against the working directory, as the system resolves
it: the AppImage's `$APPDIR/usr/././/bin/bwrap`) and run as WebKit runs it, as a no-op (`--unshare-uts --unshare-ipc
--unshare-pid --unshare-net`, /etc, /usr and /lib read only, /dev, /proc, a /tmp, then `true`). It judges by bwrap itself,
not by the launcher calling `unshare`: AppArmor profiles are per program. Results: works; fails (bwrap's line); missing
(the path is not there); could not tell (why). Also `/proc/sys/kernel/apparmor_restrict_unprivileged_userns` when the
kernel has it. Whether WebKit runs its sandbox in this start comes from its version and the variables (the rules above,
`sandbox::webkit_use`); after the window the web context's own answer is logged, and a context that runs it although
nothing asked (a future WebKitGTK default) says so. On a system with the restriction, each probe leaves one AppArmor
denial in the kernel log.

**The messages** (native: GTK 3's MessageDialog in the launcher's process, the toolkit the window already uses, never
through the web page or WebKit's processes; when GTK cannot show one: zenity, then kdialog, then notify-send without
buttons, whose text names the flags instead; always the same text on stderr and in `launcher.log`, `message:` lines).
tauri-plugin-dialog shows the same GTK dialog underneath (rfd's `gtk3` backend), but only after Tauri is up and with at
most three custom buttons, so the launcher calls GTK itself.
- *At the start*, before the window: only when WebKit runs its sandbox in this start and the probe found bwrap failing
  or missing. It says what is wrong (bwrap's line, the AppArmor value), why WebKit runs it, what turning it off means and
  the proper fix (below). Buttons: "Turn the sandbox off and start" (a restart without it; the marker is written once the
  beta copy's gate is open, by the new start), "Start anyway", "Quit". Closing it starts as before. The first button is
  there only when its restart really runs without the sandbox: not with the player's own
  `WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=0` (which the launcher leaves alone) and not with a WebKitGTK before 2.40
  (no such switch); the message then says why, and the same holds for the late message's button.
- *When the page did not start* and the launcher has nothing left to try by itself (a software-mode start, a mode the
  player forced, a fallback whose restart failed): what was tried, WebKit's sandbox in this start, where `launcher.log`
  is, and that each button is a try (the cause is not known; offered also when the probe passed). Buttons: "Start
  without WebKit's sandbox" (only when WebKit runs it and no variable of the player's decides), "Start without the GPU"
  (only when this start drew with the GPU; the player's click wins over `--gpu-render`), "Rebuild the font cache and
  restart" (0.2.0-dev.31.14, with a paragraph on GNOME Web hanging the same way: "A window that can't draw: a broken
  font cache (Linux)" above), "Open the log folder" (the message stays), "Quit". Closing it leaves the window as it is. It goes up over the window through its main loop; with
  no answer from that loop within 5 s, through the tools above.
- Enter (GTK's default button, zenity's and kdialog's first) never turns the sandbox off and never moves the font cache
  aside: those are always buttons the player picks. A zenity that cannot open a display (it ends with 1, as for Cancel; its stderr says "open display") is
  not taken as an answer: the next tool is asked.
- What turning the sandbox off means (checked): the launcher's page is its own bundle. Its CSP (`tauri.conf.json`)
  allows scripts, styles, fonts and connections only from the bundle, the IPC and `kiln-asset:`, and from the internet
  only images from `staticdelivery.nexusmods.com` and `images.nexusmods.com`; the page renders no remote HTML and
  navigates nowhere (links open in the browser, `app_open_url`). Off removes WebKit's process sandbox around that page
  only.

**Settings > Appearance, "WebKit's sandbox"** (`app_sandbox_set`, next to "Draw the launcher without the GPU"): on
removes the marker, off writes it, for the next start; the hint says what off means, what WebKit does in this start and
why, who turned it off, and what the probe found when bwrap fails. `unsupported` off Linux; absent from `app_info` there.

**What it says**: `sandbox:` lines (the choice and why, the variable, WebKit's version and whether it runs its sandbox,
the probe with its path and time, the AppArmor value; the web context's answer; the marker a message's restart wrote),
the window's facts line (`render: WebKitGTK ...; WebKit's sandbox: ...`), `message:` lines. The support report's
summary.txt has a `WebKit sandbox:` line, and the finding `webkit-sandbox` when bwrap cannot start: a problem when this
start runs the sandbox, a note when it does not (then it is not why a window stays empty). The AppImage's own missing
bwrap (so on every system) is said in those words, and is a finding, a line in the late message or a sentence in Settings
only when the start runs the sandbox.

**The proper fix** (Ubuntu 24.04 and systems built on it, for a launcher that runs WebKit's sandbox: the .deb or any
install at `/usr/bin/kiln-launcher`): an AppArmor profile that allows user namespaces for the launcher, as Ubuntu ships
for GNOME Web. WebKit's bwrap is the launcher's child and runs under its profile; Ubuntu fixed GNOME Web, Devhelp and
Geary, whose WebKit starts bwrap the same way, with exactly such profiles (Launchpad bug 2046844).

**Not run on Ubuntu.** Nobody has tried these steps with the launcher on Ubuntu yet. The profile below is AppArmor 4.0's `epiphany`
profile, which Ubuntu 24.04 ships as `/etc/apparmor.d/epiphany`, with the name and the path changed; loading it with
`apparmor_parser -r` is AppArmor's usual way. Check it against Ubuntu's documentation before you point a player at it.
```sh
sudo tee /etc/apparmor.d/kiln-launcher >/dev/null <<'EOF'
abi <abi/4.0>,
include <tunables/global>

profile kiln-launcher /usr/bin/kiln-launcher flags=(unconfined) {
  userns,

  include if exists <local/kiln-launcher>
}
EOF
sudo apparmor_parser -r /etc/apparmor.d/kiln-launcher
```
Another path: put the launcher's own in the profile line. Not for the AppImage: its WebKit cannot run the sandbox on any
system (above); leave `WEBKIT_FORCE_SANDBOX` unset or use the .deb. A .deb without bwrap: install the system's
`bubblewrap` package. The blunt alternative, `kernel.apparmor_restrict_unprivileged_userns=0` (until the next boot:
`sudo sysctl -w kernel.apparmor_restrict_unprivileged_userns=0`; for good, AppArmor's wiki puts that line in
`/etc/sysctl.d/60-apparmor-namespace.conf`), lifts the restriction for every program on the system, not only the
launcher: the protection Ubuntu turned on in 24.04 is then gone.

**An AppArmor profile in the .deb** (a packaging decision, not built): `/etc/apparmor.d/kiln-launcher` as above, loaded
by the package's postinst (`apparmor_parser -r`, dh_apparmor) and removed with it. Today it would change nothing: the
launcher does not run WebKit's sandbox. It matters only if the launcher ever turns the sandbox on (a security gain for
the page). Costs: a conffile under /etc and maintainer scripts; `abi <abi/4.0>` needs AppArmor 4 (Ubuntu 24.04 and
later), so Ubuntu 22.04 needs another file or none; it lets the launcher and the programs it starts without a profile of
their own (Wine, Proton, the game) create user namespaces again, as they could before 24.04; it covers only
`/usr/bin/kiln-launcher`, never the AppImage.

**Tests**: `cargo test -p kiln-launcher sandbox::` (the decision with flags, the variable and the marker, nothing set
without a choice, the player's own variable, WebKit's rules by version, the bwrap path in a real libwebkit2gtk and in
`KILN_TEST_WEBKIT_LIB`, the probe against fake bwraps (works, fails, missing, silent, hangs), the marker, the messages'
words, the report's finding, the restart's arguments), `render::` (the restart a click asks for),
`snapshot::startup::tests::the_starts_lines_and_markers_do_not_wait_for_the_gate`, kiln-manage `hostenv::` and
`report::tests::the_callers_findings_are_listed_with_the_reports_own`, `bun x playwright test tests/sandbox.spec.ts`.
Debug and QA (`dev-eval`) builds only: `KILN_LAUNCHER_TEST_SANDBOX_PROBE=works|fails|missing|unknown` replaces the
probe's result, and `KILN_LAUNCHER_TEST_DIALOG=tools` skips GTK (the tools) or `=<button>@<seconds>` presses a GTK
message's button by itself, so the messages can be checked on a private Xvfb display.

## Antivirus
For players on Windows. Kiln works by putting DLL files into the game folder. Some antivirus programs flag files like
these and remove them or move them to quarantine. Kiln, or a part of it, then does not load. Since 0.2.0-dev.29.18 the
launcher's Runtime page says when one of Kiln's files is missing, for example "1 of Kiln's files is missing:
kiln\plugins\kiln-greeneye.dll", and offers **Repair**. The support report says it too (`files-missing`).

An antivirus can also stop Kiln while it installs: it holds a DLL to scan it, or moves it to quarantine, and Windows
answers Kiln's write with "Access is denied". The install is then undone and Kiln is not in the game. Since
0.2.0-dev.29.18 the launcher says so instead of only that error, on the Runtime page, on Play and in its message:
"Windows did not let Kiln write kiln\plugins\kiln-greeneye.dll. An antivirus or another program may be blocking it."
The error itself stays under it (Details). The support report says it too (`install-blocked`), also after the launcher
was closed. Add the exclusion below, restore what the antivirus took, then install Kiln again. If the antivirus also
took the launcher itself, or its uninstaller, restore those from the quarantine too, or install the launcher again.

**What Kiln puts in the game folder.** The receipt, `kiln\.install.json`, lists every file Kiln installed with its
SHA-256 checksum. On the game client these are:
- `dinput8.dll`, next to `enshrouded.exe`. This is Kiln's proxy: the game loads it when it starts, and it loads the
  rest of Kiln.
- `kiln\kiln-core.dll` and Kiln's services in `kiln\services\`.
- `kiln\plugins\*.dll`, the parts that run other loaders' mods (EMF, EML, ShroudForge, Greeneye, SET and others), and
  the folders `kiln\plugins\shroudforge\` and `kiln\plugins\enshwick\`.
- `kiln\host\`, Kiln's C# host and services, and `kiln\dotnet\`, the .NET runtime Kiln brings with it.
- Data files: `kiln\kiln.json` (Kiln's settings), `kiln\compat-rules.json` and `kiln\sigdb\`.

Mods can bring DLLs of their own. Kiln keeps your mods in the launcher's data folder (Settings, Library, Data folder)
and places some of them in the game folder.

**Why antivirus programs flag it.** Kiln's `dinput8.dll` has the name of a Windows file. Windows looks for that file
next to the program first, so the game loads Kiln's copy, and Kiln passes the game's calls on to the real one. Kiln then
loads its other DLLs into the game and changes the game's code while it runs. Some malware works the same way, so
antivirus programs watch for it. Kiln's files are also new and not many people have them yet, and they are not code
signed yet, so an antivirus cannot check who made them. That is why one antivirus can flag a file that another lets
through.

**Add an exclusion for the game folder.** Do this first, or the antivirus removes the file again. To find the game
folder: in Steam, right click Enshrouded, then Manage, then Browse local files.
- Windows Security: open Windows Security from the Start menu, then Virus & threat protection. Under Virus & threat
  protection settings, choose Manage settings. Under Exclusions, choose Add or remove exclusions, then Add an exclusion,
  then Folder, and pick the game folder.
- Other antivirus programs: look for a setting named Exclusions, Exceptions or Allowed list, and add the game folder
  there. The antivirus's help pages say where it is.

If your antivirus also removes files from your mods, add the launcher's data folder as an exclusion too.

**Get the file back.** Restore it from the antivirus's quarantine, or let the launcher put it back.
- Windows Security: Virus & threat protection, then Protection history. Open the entry for the Kiln file, then choose
  Actions, then Restore.
- Other antivirus programs: open the quarantine list (it may be called Quarantine or Virus Chest) and restore the file.
- In the launcher: open the Runtime page and choose **Repair** on the "Files missing" banner. It installs the same Kiln
  version again and puts back every missing file. Close the game first.

You can also report the file to your antivirus's maker as a false positive. They usually ask for the file or its
SHA-256 checksum, which the receipt lists.

## Taskbar icon and app menu
Taskbars, docks and app menus show the launcher with Kiln's icon (`launcher/src-tauri/src/desktop.rs`).
- **Linux, how the icon is found**: a Wayland bar or dock (GNOME's dash, Omarchy's and other Hyprland bars, Waybar,
  KDE) takes the window's app id, looks for the `.desktop` file of that name (or one whose `StartupWMClass` is that id)
  and shows that file's `Icon=` from the icon theme; GTK 3 sends no icon on Wayland. The app id (X11: WM_CLASS) is
  `kiln-launcher`: main.rs sets GLib's program name before GTK starts, so it does not depend on the executable's name.
  Tauri's `enableGTKAppId` (app id `io.frostdev.kiln.launcher`) stays off: it also registers the app on the session
  bus as a single instance. On X11 and XWayland the window also carries its icon (`_NET_WM_ICON`): Tauri sets the
  first PNG of `bundle.icon`, 256 px, on every window.
- **deb, rpm**: Tauri's `Kiln Launcher.desktop` (`Exec=kiln-launcher`, `StartupWMClass=kiln-launcher`,
  `Icon=kiln-launcher`) and `usr/share/icons/hicolor/{16,24,32,48,64,128,256,512}x…/apps/kiln-launcher.png` plus
  `scalable/apps/kiln-launcher.svg` (`bundle.linux.{deb,rpm}.files`).
- **AppImage**: it installs nothing, so the launcher adds itself when it runs as one (`$APPIMAGE`), on by default:
  every start (after the data folder is found) writes `$XDG_DATA_HOME/applications/kiln-launcher.desktop`
  (`~/.local/share/...`; `Exec` and `TryExec` the AppImage's path, `Icon=kiln-launcher`,
  `StartupWMClass=kiln-launcher`, `Categories=Game;`, the line `X-Kiln-Desktop-Entry=1`) and the same icons into
  `$XDG_DATA_HOME/icons/hicolor/`, or rewrites what differs: after the AppImage moved, the entry starts it from its new
  place. Menus hide the entry while the AppImage is gone (`TryExec`). Settings > Appearance, "Kiln Launcher in the
  app menu" (`app_desktop_set`): off removes Kiln's entry and icons and `state/desktop-entry.json` keeps it off; on
  writes them again. Unlike the opt-in nxm handler it changes no default of the system or another program: it adds
  Kiln's own files, the way AppImage integration tools do. Rules: only inside the home folder, never a system folder;
  a `kiln-launcher.desktop` without Kiln's line (the player's own, another tool's) stays as it is until the player
  turns the switch on; removal takes only Kiln's files (icons of the same name only when Kiln's entry was there or
  the bytes are Kiln's); an existing `hicolor/icon-theme.cache` is rebuilt. `launcher.log`: `desktop:` lines. The
  first start may show the icon only from the next start on (the shell matched the window before the entry was
  there). On a Steam Deck the entry also puts Kiln, with its icon, in Steam's "Add a Non-Steam Game" list; the
  launcher writes no Steam files ([deck.md](deck.md)).
- **AppImage under an integrator** (0.2.0-dev.31.25, kiln#156): Gear Lever, AppImageLauncher and appimaged add their
  own entry for the AppImage, so Kiln's made the menu (and Gear Lever's list) show "Kiln Launcher" twice. Kiln now
  writes no entry of its own, and removes its own marked one, when either holds:
  - `DESKTOPINTEGRATION` is set in the launcher's environment (not empty, `0`, `false` or `no`): the AppImage
    convention; Gear Lever's entry starts `env DESKTOPINTEGRATION=1 <AppImage>`, AppImageLauncher sets its own name;
  - another entry the menu shows (`Type=Application`, not `NoDisplay`/`Hidden`, the first file with its desktop id
    across `$XDG_DATA_HOME/applications` then each `$XDG_DATA_DIRS/applications` and their subfolders, not one with
    Kiln's line) has an `Exec` argument or `TryExec` that is the same file as `$APPIMAGE` once both are resolved: on
    Bazzite, Silverblue and Kinoite `/home` is a link to `/var/home`, so Gear Lever's `/var/home/<user>/AppImages/...`
    and the launcher's `/home/<user>/AppImages/...` are one file. Quotes, the spec's escapes and field codes are read.

  Someone else's file is never changed or removed (a `kiln-launcher.desktop` without Kiln's line included). A file under
  Kiln's own desktop id `kiln-launcher.desktop` never counts as the other entry (menus show one entry per id): someone
  else's there stays as before #156, left alone and offered for replacing by the switch. The icons
  stay up to date: the NoDisplay nxm and `kiln://` handler entries (`kiln-launcher-nxm.desktop`,
  `kiln-launcher-links.desktop`, written by their own code and untouched by this) name `Icon=kiln-launcher`, and they
  keep working whatever the menu entry does. The window's taskbar icon then comes from the integrator's entry:
  integrators copy the AppImage's own entry (Tauri's template: `StartupWMClass=kiln-launcher`, no `MimeType` for this
  app), so the bar matches the window to it. Settings > Appearance, "Kiln Launcher in the app menu": the switch is off
  and not offered, and the line says "Gear Lever (or another AppImage integrator) already added Kiln to the app menu"
  with the other entry's file (`DesktopInfo.integrator`). `launcher.log` says so once, when Kiln's entry went. The
  deb, the rpm, the Steam Deck and a plain AppImage without an integrator work as before.
- **Windows**: `icons/icon.ico` (16, 24, 32, 48, 64, 256 px) is the exe's icon resource (Tauri), the window's icon,
  the NSIS setup's and uninstaller's icon, and the Start menu and desktop shortcuts' (they point at the exe). The
  shortcuts carry the AppUserModelID `io.frostdev.kiln.launcher` (Tauri's NSIS template); main.rs gives the process
  the same one, so a pinned shortcut and the running window are one taskbar button.
- **Tests**: `cargo test -p kiln-launcher --lib desktop::` (a temporary home: the entry, moving, someone else's entry,
  removal, system folders refused; integrators: `DESKTOPINTEGRATION`, Gear Lever's entry through a linked home,
  another AppImage's entry, someone else's file kept, the XDG folders; the bundle's icon sizes; icon.ico's sizes).
  `bun test unit/settings-model.test.ts` covers the Settings line.

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

## Upscaler behind FSR 3 and the upscaler files (dev.29.18)
Settings, **Graphics** (`src/components/GraphicsSettings.tsx`, `src/components/upscalerStatus.ts`), part **Behind the
game's FSR 3**, then part **Upscaler files** (`src/components/UpscalerSettings.tsx`, `#upscalers`; API
`src/api/vendor.ts`; backend `src-tauri/src/commands/vendor.rs` on `kiln_manage::vendor`; contract
[contracts.md](contracts.md) section 5.15; what runs in the game: [../graphics.md](../graphics.md) "Behind the game's
FSR 3"). A client with Kiln in it only.
- **The choice** (kiln.json `graphics.upscaler` and `graphics.amdUpscaler`, through `config_kiln_set`, so the game
  reads it within 2 seconds while it runs): a select "Upscaler" with "The game's own FSR 3", "AMD FSR", "Intel XeSS",
  each with a line under it ("FSR 4 on graphics cards and drivers that run it (AMD's own selection), FSR 3.1 on the
  others. Needs AMD's files (70 MB)."), and with AMD FSR a select "AMD FSR version": "Auto (FSR 4 where it runs)" or
  "FSR 3.1". The hint says to choose FSR 3 in the game's own graphics settings and when a change applies. The kiln.json
  card leaves both keys out (all `graphics.*` are this card's).
- **The offer.** AMD FSR or Intel XeSS chosen while its files are not in the game (or are missing or changed): a
  banner "AMD FSR's files are not in this game yet. Until they are, the game's own FSR 3 runs." with **Download AMD
  FSR**, which takes the files' own steps (the licence dialog first when that vendor's text is not agreed to yet), then
  a progress bar and Cancel in the banner. Gone once the files verified.
- **What runs** (`upscalerView`): under a heading, one to four lines in the same words as the game's Graphics page.
  - **Now**: the game runs and wrote `kiln/state/graphics.json` after it started (`GameStatus.graphicsState.current`):
    what upscales ("AMD FSR 4 runs in place of the game's FSR 3."), the version, AMD's selection's note for FSR 3.1,
    the reason the choice does not run (the shared table, [../graphics.md](../graphics.md) "What runs now, and why
    not"), and "You have chosen Intel XeSS since that game started: ..." when the choice changed in the launcher
    meanwhile. The game's English
    detail goes on a muted "Details:" line.
  - **Last game**: the game closed; shown while the choice is the one that game had and its reason still holds (files
    missing then and downloaded since, or a tool gone since, give the plan).
  - **When you play**: no such file, or another choice: what Kiln will do from the choice, the files and
    `GameStatus.graphicsStepAside` ("With FSR 3 chosen in the game's graphics settings, Kiln runs AMD FSR in its place:
    FSR 4 where your graphics card and driver run it, else FSR 3.1."), or why it will not (files missing, files
    changed, not in this build, a tool Kiln steps aside for). Nothing about the files while their status is not read
    yet (`vendor_status` hashes downloaded files once per launcher run) or could not be read.
  - Nothing while the game's own is chosen and nothing ran.
- **Upscaler files.** Out of preview since dev.29.18: the preview `upscalers` (settings.json `preview`,
  `KILN_LAUNCHER_PREVIEW`) no longer exists, `vendor_status` has no `preview` field and `vendor_download` needs none
  (`app_info.preview` stays for a later preview). Texts `settings.upscalers.*`: "Upscaler files", "AMD's and Intel's
  upscaler files, for Kiln to run behind the game's FSR 3 option. Kiln downloads them only when you ask, into the game's
  kiln/vendor folder.", "Kiln checks every file against the size and SHA-256 it pinned, and the game checks them again
  before it loads them. Remove deletes them again." Then one row per component: its name and version, a state word, its
  sentence from the manifest, and one line:
  - not downloaded: "70 MB from raw.githubusercontent.com." and **Download** (an interrupted download: "… arrived
    already: the download goes on from there." and **Continue download**);
  - downloading: a progress bar, the percentage, "Downloading <file>: <done> of <size>", **Cancel**;
  - downloaded: "Version 2.4.0, 70 MB on disk." and **Remove** (a dialog: "Kiln deletes kiln/vendor/amd/2.4.0 (70 MB) from
    the game folder. You can download it again later.");
  - update available: "Version <old> is downloaded. Version <new> is out: <size> from <host>.", **Update**, **Remove**;
  - not available in this build: "This build of Kiln does not have the files' checksums yet, so it does not download
    them." (a manifest file not pinned), no button;
  - failed: the reason in a red line (the task's error, or "<file> of it is missing or changed. Download it again."),
    **Try again** / **Download again**, and Remove when files are there.
  - while the game runs: the same row with its buttons greyed out (Cancel aside) and "Close the game to download or
    remove these files: the launcher does not change the game's files while it runs." (`vendor_download` and
    `vendor_remove` refuse then); the offer's Download waits the same way.
- **The licence dialog** opens on Download when the player has not agreed to that vendor's text yet: "Download <name>",
  then What (the manifest's sentence), Size ("70 MB, 3 files"), From ("raw.githubusercontent.com: AMD's own repository
  on GitHub, version 2.4.0"), Goes to ("kiln/vendor/amd/2.4.0 in the game folder"), the licence's name with "Where this
  text is" (opens the vendor's page in the browser), "AMD licenses these files under the MIT licence. Here is its text in
  full. Read it before you agree.", the text in a scrolling box, and the small print "Kiln asks raw.githubusercontent.com
  for these files and sends nothing else. raw.githubusercontent.com sees your IP address and a user agent that names Kiln
  and its version." Buttons: Cancel, **I agree, download** (passes the hash of the text shown). Later downloads of the
  same vendor's text start from the row or the offer, whose line already names the size and the host.
- A download is a task (`kiln://task`, the header's tray too); the row keeps its task id for this window, and the offer
  and the Deck's rows read the same one (`useRowTasks`). Done: a toast "<name> is downloaded" ("Kiln checked every file.
  They are in <folder> in the game folder."). The status is asked again when the page comes back (no polling).
- **Step-aside lines** (end of the card): OptiScaler, Streamline and `EnshroudedVKFG.ini` now say "Kiln's frame cap,
  DLSS settings and upscaler choice step aside; the readout still works."; another tool's upscaler DLLs next to the game
  (`GraphicsStepAside` kind `upscaler`): "Another upscaler tool's files are next to the game (libxess.dll): Kiln's
  upscaler choice steps aside; the frame cap, DLSS settings and readout still work." Since 0.2.0-dev.31.25 a tool Kiln's
  overlay steps aside for on its own too (Streamline; OptiScaler with its own menu or frame generation, `kilnOverlay`
  false) says "..., and so does Kiln's overlay: its readout shows only while "Draw Kiln's overlay anyway" is on." (the
  readout is drawn by the overlay; `OverlayAnyway.tsx` `overlayAsideFor`).
- **The Deck** has the same in its Settings ([deck.md](deck.md), "Upscaler").
- Tests: `unit/upscalers.test.tsx` (the row of every state; no preview gate), `unit/upscaler-status.test.ts` (Now,
  Last game, the plan, the offer; one reason table with kiln_ui.h, the game's English and the docs),
  `unit/graphics-settings.test.tsx` (the card's entries, step-aside lines), `tests/upscalers.spec.ts` (mock: available
  without a preview, not pinned, the dialog, agree, progress, cancel, downloaded, remove, a failure and Try again, an
  update, missing files, a changed licence), `tests/upscaler-choice.spec.ts` (desktop and Deck: choose, the offer with
  the licence first, progress, the plan, Remove, Now and Last game, OptiScaler). Mock: `src/api/mockVendor.ts`
  (`?vendor=unpinned|pinned`, `__kilnVendorMock`), `__kilnMock.setGraphicsState(state)` (the game's
  `kiln/state/graphics.json`; "Now" while the mock game runs).

## Known limits
- Profiles/library depend on kiln-manage M3; until then those screens show `not_implemented` (or mock data in dev
  builds, flagged MOCK).
- HTML5 drag-and-drop inside the webview is replaced by pointer events (SortableList): Tauri's file-drop handler
  disables it on Windows.
- The editor's game assets are local only: the launcher extracts them from the installed client into
  `<data>/editor-assets/` ("Extract from game"; [gameart.md](gameart.md)); `$KILN_EDITOR_ASSETS` and
  `fetch-editor-assets.sh` stay as dev fallbacks.
