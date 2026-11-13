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
| `src/components/` | `ConfigEditor`, `SaveBackups` (shared with server pages), `ProfilesSettings` and `LibrarySettings` (Settings' Profiles and Library) |
| `src/pages/` | client pages (Play with `mods/MainList.tsx`, the mods' views under `/mods` in `mods/` and `profiles/`, Catalog, Runtime, Logs, Saves, Editor, Settings, About) |
| `src/api/commands.ts` | typed wrappers for every command + `KilnEvents` (contract) |
| `src/api/hooks.ts` | TanStack Query hooks, query keys, the selected game (zustand), `useBackendEvents` |
| `src/api/mock.ts` | in-memory backend for the browser (stateful: profiles, library, tasks, a fake running game) |
| `src/api/fallback.ts` | dev aid: read-only `not_implemented` commands answer from the mock inside Tauri (Mock flag in the header) |
| `src/editor/` | the vendored Save Editor ([UPSTREAM.md](../../../launcher/src/editor/UPSTREAM.md)) |
| `src/data/kiln-json.snapshot.json` | schema snapshot of Kiln's own `kiln/kiln.json` (core + Kiln.Host `mods` section) |
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
| `/` | Play, in front of the stage. First start ("The first run" below): looking for the game by itself, then Install Kiln right here. Then: the game's name, one live sentence: the count over every kind ("12 mods on, 3 off.") and only the exceptions a player must know after it (Kiln mods that start with the player's own world, ones a game's kiln.json keeps off, mods that will not run with the reason, Ember's that do not run yet); "Just once": vanilla (warns first in the arch: Kiln gear can crash), without mods; **the list of every mod** (below); boot stages while it starts; four facts (Kiln with its build date and "update ready/failed", proxy, runs on, last boot: boot.json's errors, the mods that failed in mods.json/native.json and the `[E` lines of kiln.log, all of the same run, linking to Logs); the game folder with Open, Detect, Add (a game folder). A profile other than the default ("My mods") is named in one small link in the top line ("Profile: Ember run", to Settings, Profiles); otherwise the word does not appear. Banners above the name: refusal (`last-server.json`) with the modpack offer, Steam launch-option advice, Steam updating, "Update Kiln" (`GameStatus.newerMods`: the loaded profile's mods whose `kilnBuild` is newer than the game's runtime: "<ids> needs a newer Kiln." with Update, which opens Runtime), safe idle. The mouth (shell) is the Play button | `game_*`, `launch_play`, `launch_state`, `profile_active/save/ensure_default`, `profile_resolve`, `library_*`, `found_*`, `config_list`, `engine_settings_*`, `modpack_pending/dismiss` |
| `/mods/order` | Load order, every kind: the mods that are on, in the order a start brings them up (architecture.md "Boot"): native mods in the proxy (dllmain), as Kiln starts (early), Kiln's plugins (Kiln plugins, and each other loader's plugin with its mods: EMF, EML, ShroudForge, SET, Greeneye, Flame, Shroudtopia, Enshwick, shroudlink, XHL-Core's companions), after the plugins (boot), Kiln mods at the main menu in the resolver's order, then with the player's own world (side "server" Kiln mods, `ResolvePreview.withWorld`; native world phase), and the game folder's mods that load by their kind's rules; then "On, but not loading" with each reason, and Off. Client or server side (aside). Each row: switch, kind, version, menu | `profile_resolve`, `profile_save` |
| `/mods/native` | Native and DLLs: native mods grouped by phase (dllmain/early/boot/world), drag-reorder inside a phase, plugins; the chain card (dev.21): each chain.ini entry with its "Load after Kiln and ShroudForge" switch, last session's hook overlaps with a one-click "load late", and the compat rules that apply ([engines/README.md](../engines/README.md#closed-source-dll-mods)) | same, `chain_view`, `chain_set_late` |
| `/mods/engines` | Engines: which loader runs which kind in this game, for the loaded profile: each engine plugin's state from the last start (EML's phase, ShroudForge hosted or its own and its mods' effects, the XHL-Core check, an engine's own loader and what it means for the mods that are on), the shared archive backup guidance (below), the other loaders found next to the game | `engine_status`, `eml_backup`, `found_scan` |
| `/mods/configs` | the loaded profile's config folder (SchemaForm/raw) | `config_*` |
| `/profiles`, `/profiles/:id/<tab>`, `/library` | old places, redirected (`pages/mods/OldRoutes.tsx`): `/profiles` → Settings, Profiles; a tab of the loaded profile → its new place (Mods, EMF → Play; Native → `/mods/native`; Engines → `/mods/engines`; Configs → `/mods/configs`); Saves → `/saves`; another profile's → Settings, Profiles (where it can be loaded); `/library` → Play | |
| `/catalog` | Enshrouded's mods on Nexus Mods ([catalog.md](catalog.md)): Kiln mods (default), All Enshrouded, Installed; search, sort; cards with endorsements, downloads, what the mod is and its state (Installed, Update <version>); an updates banner; `?mod=<id>` opens a mod's details (pictures, description, requirements, files, Add / Update / Download on Nexus Mods, Open on Nexus Mods). Mod Manager Download links are taken anywhere in the app (`shell/catalogInstall.tsx`, `NxmListener`) | `nexus_*` |
| `/runtime` | install/update/rollback per bundle (bundles of one version and day are ordered by when they were built, never by the git id), uninstall (typed), proxy chain (receipt + live `boot.json` chain), switch from ShroudForge (typed), add bundle | `runtime_*` |
| `/logs` | the support report above the tabs ("Save report...", "Copy summary": [support-report.md](support-report.md)); kiln.log, game log, proxy.log, mod logs (follow, filter), crash reports, tick stats (sparklines); Engines: one line per engine plugin that wrote `kiln/state/<engine>.json` or has mods in the active profile (EML `phase`, ShroudForge `mode`, `reason`, each mod's state and reason; ShroudForge's own per-mod runtime report while it is current, never shown as proof of a gameplay effect; the XHL-Core check when the profile has XHL companions or the host was recognized), plus read-only shared archive backup guidance | `logs_*`, `report_save`, `report_summary`, `engine_status`, `eml_backup` |
| `/saves` | save files, backups, manual backup, restore (refused while running; Steam Cloud hint), delete | `saves_*` |
| `/editor` | the Save Editor (lazy chunk) | `editor_*`, `kiln-asset://` |
| `/settings` | **Profiles** (`components/ProfilesSettings.tsx`; `#profiles`): every saved profile with its mod count over every kind and the server it is for, which one is loaded, Load (the main list then shows it), "Save current as..." (`profile_create` with the loaded one as `copyFrom`; the loaded one stays loaded), Rename, Folders and notes (name, mod data, drop-in folders, notes), Delete (typed; never a loaded one), and "No profile" (the game folder decides: `profile_deactivate`); the modpack flow's "Server: <name>" profiles are listed too. **Library** (`components/LibrarySettings.tsx`): how many mods and versions, the size on disk, its folder (Open), Clean up (every version no profile and no game's profile file uses, one `library_remove_many`; typed). Launcher settings ("Switch new mods on", default on; "Type a mod's name to remove it", default off), Appearance ("Animated fire": off draws the plain Play button), games (detect/add/forget), Nexus Mods in enabled builds only (OAuth account sign-in/sign-out and "Mod Manager Download links"; `components/NexusSettings.tsx`), Kiln's `kiln.json` via SchemaForm | `profile_*`, `library_unused`, `library_remove_many`, `app_settings_*`, `game_*`, `config_kiln_*`, `nexus_account`, `nexus_sign_in`, `nexus_sign_out`, `nexus_handler_*` |
| `/about` | versions, CREDITS.md, "Theme and design: frostdev"; Updates (the state, Check now), in the Nexus Mods build "New versions: Nexus Mods" | `app_info` |
| `/servers` | every server, one line each (or a redirect to the first server when there is no `ServersPage.tsx`) | `server_list` |

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
  `mods.hosted`), "Needs <id> <range>", "Will not run" (with the resolver's reason on hover), "Does not run yet" (Ember),
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
  Settings (ShroudForge's `extended.mod.json`, Shroudtopia's `shroudtopia.json`), Configs (when the mod wrote a config:
  the profile's config editor, opened on its file), Use <version> (the Library's other versions), Open folder, Remove
  <version> (one of several versions), Remove.
- **Remove** (`shell/removeMod.ts`): the mod leaves the loaded profile, then its versions leave the Library
  (`library_remove` past exactly the users `library_users` named; another profile that lists it is named in the
  confirmation and shows it as missing). One click with a plain confirmation; Settings "Type a mod's name to remove it"
  (off unless turned on) adds the typed name. Uninstalling Kiln, deleting a profile, a server or saves keep their own
  typed confirmations. Refused while the game runs.
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
  ("Some mod folders were kept" with its paths and recovery steps, "Mod settings kept", "Notes on your mods"). A backend
  that cannot answer (an older one) gets the old behaviour: all of them.
- **Notes (N)** above the main list (`components/FolderNotes.tsx`) opens what the last activation said, in three
  parts: Left in the game folder, Mod files kept, Notes on your mods. It reads the newer of the last activation this
  window showed and `profile_active` (the backend's record, also after a restart). The Deck has the same list in
  Settings ("Mod notes", A: Read).
- **Settings, Launcher, "Show notes about mod folders"** (`LauncherSettings.modFolderNotes`, on by default; the Deck's
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
  for a start with the profile. The cook first writes the active profile again when it changed since its activation
  (kilnctl, another window), so it cooks the mods the start then loads, and ends at once when nothing needs a cook. A mod
  needs a cook when it was never cooked for this game (it was installed, or switched on), when it was updated (its
  identity changed), when the game was updated (cooked files are made from the game's data; Steam's build id tells,
  else the game's data file), when its last cook failed, or when a file its last cook made is gone or is not the bytes
  that cook made (its receipt in `kiln/state/cook-receipts` says). That rule is one function, `cook::needs_cook`; the
  record it reads is the game's `kiln/state/cooked.json`. A vanilla start and "play once" do not cook.
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
  the tool's current line), `profile` (the active profile checked or written again, the save backup), `steam` (Steam
  asked to start the game, until the game's process exists). Then the steps Kiln itself reports from inside the game
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
  load Kiln's proxy), a vanilla start and Steam updating the game are said as before, without a bar. No word from Kiln
  for 60 s (120 s inside the early plugins) is "No word from Kiln" with its sentence (a game waiting at its menu for
  its mods says so), without a bar or a countdown, and the mod list is back. A Play that ends before the game exists
  (refused, or Steam took it and no game came within 5 minutes) ends the start, and the page is as it was before
  Play. The window keeps following a start while it is hidden or in the background (the game has the display then);
  the launcher's watcher keeps a start that reached the main menu even when no window asks. A game with a Kiln runtime older than
  0.2.0-dev.27 writes no progress.json: after the launcher's own steps the line says "Waiting for Kiln to boot" until
  its boot.json, then "Running" as before, and nothing is kept.
- **The history.** Starts are kept and looked up by the runtime's own version (progress.json's `kiln`, with its build
  stamp; before progress.json, found through the install's version). A file of a newer format, or one that cannot be
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
  still loading a profile in this game". A drop takes a
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
bun x playwright test tests/catalog.spec.ts   # the catalog, desktop and Deck (?nexus=free|premium|nokey)
CARGO_TARGET_DIR=.out/launcher/cargo cargo test -p kiln-launcher
CARGO_TARGET_DIR=.out/launcher/cargo cargo test -p kiln-launcher --no-default-features   # the Nexus Mods build (no self-update)
tools/launcher/fetch-editor-assets.sh   # dev: local editor game assets for the vite dev server (gitignored)
```
Test runs against game copies: `KILN_DATA_DIR=<scratch>` and `KILN_STEAM_ROOTS=<fake Steam root>` keep the app away
from the real launcher data and Steam libraries, and `KILN_LAUNCH_DRY_RUN=1` makes `launch_play` do everything but open
`steam://` (which would start the real game). `KILN_LAUNCHER_NO_WEBGL=1` forces the fire's no-WebGL fallback (the plain
Play button) for one start, without touching the saved "Animated fire" setting. Linux: `KILN_LAUNCHER_RENDER=software`
or `--software-render` draws without the GPU ("Drawing without the GPU" below). GUI and Wine tests run on a private Xvfb display, never the desktop's. Debug builds only: `KILN_DEV_EVAL_DIR=<dir>` runs each `*.js`
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

**Settings, Library card, "Data folder":** the path at once (mono, shortened in the middle), its size when measured
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
- **The fallback**: when the page has not said it started 15 s after the window was made (30 s after the start when no
  window came), a start that drew with the GPU by default writes `<data>/state/software-render.json`
  (`{format, why, at, version}`), logs why and starts again in software mode, in the same process (exec): `$APPIMAGE`
  when it is set (the AppImage file, with the environment a fresh start has), else the executable, with the same
  arguments. A start in software mode, or with a mode the user forced, never restarts: it writes
  `PROBLEM: the page did not start ...` to `launcher.log` and stderr. One restart at most, never a loop.
- **Choosing the mode**: `--software-render` / `--gpu-render` (the last one given wins), then
  `KILN_LAUNCHER_RENDER=software|gpu`, then the marker, then the GPU. Settings > Appearance, "Draw the launcher without
  the GPU", sets or clears the marker (`app_render_set`) for the next start. A start with an nxm link takes no other
  argument (main.rs).
- **What it says**: at the start, one line on stderr with the log's path and the mode. `launcher.log`, `render:` lines:
  the mode and why, the WebKitGTK version, `XDG_SESSION_TYPE` and whether `DISPLAY` / `WAYLAND_DISPLAY` are set, the
  first line of `/proc/driver/nvidia/version` (or that there is none), when the page started, a web process that
  ended. The support report's summary.txt has a `Launcher:` line with the mode.
- **Tests**: `cargo test -p kiln-launcher render::`, `bun test unit/started.test.ts`. Debug and QA (`dev-eval`) builds
  only: `KILN_LAUNCHER_TEST_WITHHOLD_READY=gpu|software|all` ignores `app_started` in that mode, so the fallback can be
  watched on a machine that renders (on a private Xvfb display).
- **Troubleshooting a black window**: start the launcher from a terminal; its first line names `launcher.log`. Within
  15 s it should start again without the GPU by itself. If the window stays black, start it with `--software-render`
  (`./Kiln*.AppImage --software-render`, or `kiln-launcher --software-render` for the .deb) and send `launcher.log`
  (the `render:` lines) and, once the window works, a support report.

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
