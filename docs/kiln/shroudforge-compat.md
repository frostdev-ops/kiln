# ShroudForge mods on Kiln (design, licence boundary, API coverage)

Owner: agent `sf` (2026-11-07). Players' guide: [shroudforge.md](shroudforge.md). Wiki note: [wiki-notes/sf.md](wiki-notes/sf.md).

ShroudForge (SF, [bonsaibauer/shroudforge](https://github.com/bonsaibauer/shroudforge), MIT) is a
mod loader for Enshrouded. Kiln started as a fork of it. SF mods are Lua packages: `mod.json` (EML's manifest),
`extended.mod.json` (switch, targets, settings with labels and controls, groups), `src/*.lua` and `locales/*.json`.
This page covers how SF loads, what conflicts with Kiln, the options and the chosen design, the licence boundary, and the
coverage of SF's API under Kiln.

**Decision (user, 2026-11-07).** Kiln officially forks ShroudForge: [frostdev-ops/shroudforge](https://github.com/frostdev-ops/shroudforge)
branch `kiln`, based on upstream 1.7.9, with Kiln's layer in its `kiln/` folder so that upstream merges stay cheap. Kiln
ships the runtime built from that fork **by default**, as a separate GPL-3 component in `kiln/plugins/shroudforge/`.
When a player has ShroudForge installed, the same adapter can drive the player's copy instead. A setting toggles this,
and a missing or broken copy falls back to the bundled one. Kiln's own `kiln-shroudforge.dll` stays MIT. Sections 5,
7 and 10 describe the result.

The original source inventory used upstream `823823a` (1.7.9, recorded 2026-11-05). Historical lab evidence below
used SF 1.7.8 and the current server profile. The bundled fork is pinned at
`63b88625c5539969f11fe9b21e6c31ca4e1ef260` (dev.24 hotfix, branch `kiln-sf-eml`): writes into values a mod created
are checked against the field's type (section 5.0), on top of `112d939` (dev.24, branch `kiln-dev24-compat`: the host
key feed for mod shortcuts, section 5.6, and outdated working backups set aside without SF's Updater, section 5.0),
dev.21's engine-hook host interface (`724a884`) and dev.20's archive-reader/publication and recovery-path fixes
(`1ef3896`). Its component records that commit and all three submodule revisions in `SOURCE.txt`, identifies itself as
`1.7.9 kiln.63b8862` (`version.json` build `kiln.63b8862`), and includes the component's notices and licenses. The dev.20 mixed-server
evidence is separate from the older observations below; neither is a client rendering or player-interaction test.

**ShroudForge 1.8.1 (0.2.0-dev.30, 2026-11-21).** The bundled runtime is upstream's 1.8.1 dev line (branch `1.8.1` at
`d35cea18`, tag `v1.8.1-dev.44571709069`) merged into the fork's `kiln` (`2df1de68`, 1.7.9 with every Kiln patch) with
`kiln/sync-upstream.sh 1.8.1`: fork branch `kiln-1.8.1` (a branch `kiln/1.8.1` cannot exist beside `kiln`), pin `f1b777b5`,
`version.json` version `1.8.1`. Runtime ABI 12, the four bootstrap exports and the client (26430625) and server (26430253)
profiles are unchanged. The merge's six conflicts and what each kept: the fork's `kiln/UPSTREAM-PATCHES.md`. The adapter
accepts 1.8.0 and 1.8.1 beside 1.7.8 and 1.7.9, bundled and installed (`src/contract.h`). See section 11 for what 1.8.1
adds next to Kiln. 0.2.0-dev.30.7 pins fork branch `kiln-30.7-fixes` on top of `5bb133f0`: a working baseline that is not
whole or not the source of the game files is never restored, and EML manifests may leave optional metadata null
(section 5.0). 0.2.0-dev.31 pins fork branch `kiln-dev31/bodyheat-signed` (`465aad9f`, on top of `90b76e3e`, the
30.8/30.9 pin): BodyHeat's attribute calculation runs as the resource's sint32 (section 5.0).
0.2.0-dev.31.12 pins fork branch `kiln-dev31-exports-reuse` (`1672d8af`): the host's skip list (`c115e6c3`, section 5.8)
and, on top of it, a start reuses the last pass while runtime mods write into the export folder (`86c2f8db` and its
review fix `1672d8af`, section 5.0). 0.2.0-dev.31.13 pins fork branch `kiln-dev31-baseline` (`e3edecc3`, on top of
`1672d8af`): after a game update a failed pass's recovery takes the updated files as the new clean copy, Steam's
manifest proves or refuses game files before a new working baseline is taken from them, and a start without the
Updater converts ShroudForge 1.7's numeric toggle keys (`81b26c61`); its review (`b232119b`) makes a new clean copy
whole before either backup gets its name and keeps a set-aside whole pair beside a half one (section 5.0, "After a
game update, when the pass fails"), and on top of it fork branch `kiln-dev31-fast-encode` (`6cb84f34`): a pregame
pass encodes block-compressed textures on the PC's threads with the serial encode's bytes, and `game.assets.get_content`
answers a blob created earlier in the same pass (section 5.0, "Textures on every core, and blobs made earlier in the pass").
The stale-game-data patch (0.2.0-dev.31.16) pins fork branch `kiln-dev31-stale-data` at `a9dc2ad2` (`26b9fece`, on top of
`6cb84f34`, with its review's commits on top, fast-forward): a clean copy of the game data goes back without
a word only when Steam's manifest of the installed build lists it byte for byte, and is refused only when it is proven
to carry a mod's changes (section 5.0, "A clean copy that carries an uninstalled mod's changes").
The voxel-write patch (0.2.0-dev.31.18) pins fork branch `kiln-dev31-voxel-write` at `6cf01443` (on top of `a9dc2ad2`):
`runtime.world.voxel.write` through the host's voxel writer (section 8, "Voxel writes through kiln.build").

## 1. How ShroudForge loads (source facts)

| Step | Where (upstream source) | What happens |
|---|---|---|
| Entry | the game imports `dbghelp.dll` (`MiniDumpWriteDump`) and `DINPUT8.dll` (`DirectInput8Create`), client and server (import tables of `enshrouded.exe` 26430625 and `enshrouded_server.exe` 26430253). It does not import `winmm.dll`. | SF ships three DLLs in the game folder. `dbghelp.dll` and `dinput8.dll` come from kfc-parser's `dbghelp-proxy`/`dinput8-proxy` crates (EML's proxies, GPL-3) with `mod-loader-bridge`. Each reads `shroudforge/config/modloader-config.json` `"modloader"` (`"shroudforge"`, the default, or `"eml"`) and, for ShroudForge, does `LoadLibraryW(<game>\winmm.dll)` (`kfc-parser/crates/mod-loader-bridge/src/lib.rs`). |
| Bootstrap | `modules/entrypoints/windows-bootstrap/` → `winmm.dll` (SF's own C++, MIT) | `DllMain`: only in `enshrouded.exe`/`enshrouded_server.exe`. It installs the **startup gate**: the exe's 14 entry bytes become `jmp [rip] → Run`, the same technique as Kiln's gate, which was taken from here. It also starts a thread. `Run` (main thread, at the entry) waits until preparation is done, puts the bytes back and jumps. |
| Preparation thread | `runtime_lifecycle.cpp` | It waits for the gate, writes a log banner and runs the startup update check (`shroudforge-updater.exe --startup-check`, up to 45 s, skipped when the updater is missing or the module is off). On the client it starts the Debug Console, the Modloader window and the World Editor window (`shroudforge.exe --debug-console / --module-ui / --world-editor-ui`). Then it loads `shroudforge/shroudforge-runtime.dll` and calls `shroudforge_prepare_startup(root)`: startup asset mods (`patch` capability) that write game files with a backup, plus the DLL Manager plan. Next it calls `EcsRuntime::Initialize` (`kfc-runtime.dll`, `KfcRuntimeInitialize`). After that it **releases the entry**. |
| Mods loop | same | `shroudforge_create(root)`, then every 16 ms on SF's own thread: `KfcRuntimeTick()` and `shroudforge_update(handle, dt)`. A heartbeat file goes to `shroudforge/runtime/heartbeat.json`. At exit: `shroudforge_destroy`, `KfcRuntimeShutdown`, then the pending update is installed (updater, after the game exits). |
| Native runtime | `modules/game-runtime/native/` → `kfc-runtime.dll` (C++, MIT; zydis, nlohmann) | It needs an exact-image **game profile** (`shroudforge/runtime/profiles/enshrouded/{client,server}/<build>.json`, data that updates with SF; beta profiles are derived from the cached previous exe). It reads the engine's registries, ECS storage, named services and the ResourceService through memory reads with validation. Writes are guarded data writes with readback. **Code hooks**: `EngineHooks::Install()` runs only for an "execution profile", which needs the legacy `ecsLayout` + `hooks` keys (`select_game_profile.cpp:31`). Every KFC4 profile (client 25966634 through 26430625, server 25988414 through 26430253) has neither, so **on current builds SF installs no inline code hooks**. Its only code patch is the temporary startup gate on the entry point. Confirmed in the lab: `execution_profile=missing-or-ambiguous-profile game_thread=unavailable`. |
| DLL Manager | `mod-runtime/native_dlls.rs`, `modloader/dll_manager.rs` | Native DLL mods in `mods/<folder>/` (approved per folder) and `<game>/dinput8-chain.ini` (EMF's chain file) load on an "sf-native-dlls" worker during preparation. DirectInput providers need SF's `dinput8.dll` (`ShroudforgeSetDinput8Provider`). |
| Network | `modules/transport` | Steam Networking Messages: a session-request handler and channel 65535 for health probes. It never pumps Steam callbacks. |

## 2. Conflicts with Kiln, both ways

| Area | Finding | Handling |
|---|---|---|
| Proxy name | Both own `dinput8.dll`. SF's `dinput8.dll` is only an alternative bootstrap plus the DirectInput provider host for SF's DLL Manager. SF also starts through `dbghelp.dll` → `winmm.dll`. | Kiln keeps `dinput8.dll`. SF's `dinput8.dll` goes to the backup, unchained. `dbghelp.dll`, `winmm.dll` and `shroudforge/` **stay**. Reinstall recovers missing helpers displaced by legacy receipts; current player replacements are never overwritten (section 6). |
| Entry point | Both patch the exe's entry. | Compatible by design. Whoever patches second saves the first patch as "original" and jumps to it (LIFO). Kiln's proxy installs its gate after its chain. When kiln-shroudforge hosts SF there is only one gate (Kiln's), and SF's preparation runs inside Kiln's `on_ready`. |
| Double start | SF's `dbghelp.dll` starts SF on Windows (and under Wine with `dbghelp=n`). | kiln-shroudforge looks for a module exporting `ShroudforgeStop` in the process and **steps aside** ("self-hosted"). |
| Code hooks | SF: none on KFC4 builds (a legacy profile names six). Kiln: many. | No double hooks. Where SF has hooks, they go through `kiln.hooks` ("Engine hooks through `kiln.hooks`" below), so another DLL's jump on one of them no longer turns SF's native runtime off. |
| Main menu | SF's native runtime (`kfc-runtime.dll`, hosted or SF's own) rewrites the game's two Settings calls in the main menu to enter its own function, which calls the entry renderer itself: for a "ShroudForge" row while SF's Modloader window is there (never under Kiln's bundled runtime), then for the Settings entry. Kiln draws its menu entries (**Mods**, mods' entries) before the Settings entry and knew that entry by the renderer's return address, so with SF's native runtime in the process the main menu had none of Kiln's entries (a real client, 2026-11-11; the pause menu and the menu key in a world were not affected). | kiln.ui also takes a renderer call from outside the game's image whose stack has one of the two Settings return addresses above it (`native/services/ui/menu_site.h`; 0x4a8 bytes up in Kiln's build of the 1.7.9 runtime, looked for within 4 KiB), draws its entries before the first such call of a UI frame, and says once in `kiln.log` through which module the entry is drawn. Host-tested logic only; not yet seen in a client. Until dev.27 it could not run at all: SF writes the two calls in `KfcRuntimeInitialize`, in Kiln's early step and so before the signature scan, and the scan's difference check (`pe::only_hook_differences`) knew inline jumps only. The two changed operands sent the whole scan to the live code, where the captures of `ui/MainMenuEntry` disagree (two point at SF's relay, the others at the function), so `ui.main_menu` and `ui.menu_page` were off in every client start that hosted SF, bundled or installed (players' reports of 10 and 13 Nov 2026: `sigdb: scanning the live code (file/live differences are not recognised inline hooks...)`, `symbol ui/MainMenuEntry unresolved: captures disagree`). dev.27 knows the form: an `E8` whose opcode is in place, that calls into the image in the file and to readable memory outside it in the live code. The scan then reads the file's bytes, and `sigdb` names such a place `call redirected to <module>`. Host tests on the client exe's own bytes (`native/core/tests/test-sigdisk.sh --client`); guided check `menu.mods-with-shroudforge`. |
| Shared game state | SF writes ECS components, resources and named services (e.g. `ecs::GameSettings`) from its own thread, with claims and readback. Kiln services write some of the same (e.g. `kiln.settings`). | Last writer wins, as with any two mods. SF restores its baselines when a mod switches off. Documented for players. The lab shows SF's guarded write confirmed next to Kiln's services. |
| Tick | SF: its own 16 ms update loop. Kiln: the server-world tick. | Kept separate. Hosted SF uses a serial native/Lua worker and a separate filesystem-only writer thread for the heartbeat, `shroudforge.json` and the `[sf]` log mirror, so slow file I/O no longer blocks its updates. The heartbeat records the last update turn, not writer activity. Runtime work in the loop (the native status read every 2 s) can still delay it; this is not a guarantee of gameplay or input parity. |
| UI / input | SF's windows require a separate installed `shroudforge.exe`, absent from Kiln's bundled runtime. Mod keybinds (`shroudforge.input.is_keybind_down`/`was_keybind_pressed`) get their key state from the same exe, started by the runtime as a hidden `--keyboard-input` child that reports keys over a pipe. | Client only. The bundled runtime (fork `112d939`) has an optional host key feed: Kiln passes it the game window's own key, button and focus records (kiln.ui v5), and the runtime starts no helper (section 5.6). Without the feed (an installed upstream SF, an older bundle, kiln.ui off, safe idle, `KILN_SF_KEY_FEED=off`, or SF's `shroudforge.exe` installed: the helper then keeps the key state) the runtime starts the installed `shroudforge.exe --keyboard-input` itself, and with no SF installed mod keybinds never fire (`Keyboard shortcuts unavailable`). `kiln/state/shroudforge.json` `keys` says which (`kiln`, `waiting` until Kiln's plugin step, `helper`, `none`) and why. Kiln's existing Mods screen edits SF switches/settings without a helper. Installed helpers still start and respect module switches. Only when no hosted Modloader helper runs does Kiln own the auto-reset public toggle event and F9/configured keyboard fallback. Self-hosted SF is untouched. |
| Native DLL mods | Both read `<game>/dinput8-chain.ini`. | The same file loads once (same canonical path, `LoadLibrary` reference count). SF reports an issue for a DirectInput provider without its `dinput8.dll` host. |
| Updates | SF's updater replaces its managed paths, **`dinput8.dll` included**, and its repair check requires them to exist. | In hosted mode kiln-shroudforge runs neither the startup update check nor the pending install, so SF never overwrites Kiln. In self-hosted mode an SF update can replace Kiln's `dinput8.dll`. The launcher must then put Kiln's proxy back (detect: classify at launch). The repair check only tests that the file exists, so Kiln's `dinput8.dll` satisfies it. |
| Wine / Proton | SF needs `dbghelp=n,b;winmm=n,b`. | Not in hosted mode. kiln-shroudforge loads `shroudforge-runtime.dll` (not a system DLL name) by full path. The lab ran with Kiln's default `mshtml=;dinput8=n,b` only. This fixes the Steam Deck case. |
| `winmm.dll` owners | Shroudtopia and Shroudweave also ship a `winmm.dll` (universal-loader.md). | Self-hosted detection checks the `ShroudforgeStop` export, not the file name. The installer must classify `winmm.dll` before treating it as SF's (detect). |
| EML packages | SF runs EML v1 Lua packages (`launcher: "EML"`, or no `extended.mod.json`) in its mods folder, but only those whose `extended.mod.json` says `enabled: true`, on the sides its `targets` name (`modloader/parse_mod_manifest.rs`: `enabled` defaults to false, "this also keeps EML packages without an extension safe by default"). A legacy EML package has no such file until SF's Modloader writes one at the first change of its switch (`ensure_eml_extension`), so SF keeps it off. | When SF runs, the packages in SF's mods folder are SF's and kiln-eml stands aside (section 7). The launcher gives Kiln's copies of the profile's EML mods there SF's switch from the profile (section 7). kiln-eml, the launcher's notes and Kiln's Mods page say when SF has a package off (`off-in-shroudforge`). |

## 3. Options compared

| | (A) SF's runtime as a Kiln plugin | (B) chain SF's loader DLLs | (C) re-implement SF's Lua API on Kiln |
|---|---|---|---|
| What | A Kiln plugin drives SF's runtime. Kiln provides the start, the profile, paths and the log, and SF's runtime keeps its Lua API, reflection and profiles. **A-bin** (built): host the player's installed `shroudforge-runtime.dll` + `kfc-runtime.dll` through their C exports. **A-src**: build SF's runtime from source into Kiln. | Leave SF's `dbghelp.dll`/`winmm.dll` and let SF start itself next to Kiln. | Write a Lua host and SF's ~150 functions plus EML's ~600 on Kiln's services. |
| Cost | A-bin: ~500 lines (done). A-src: build plumbing, plus a GPL plugin (it links kfc). | installer change only | very large (months): reflection, type registry, resource codecs, overrides, building and voxel, network |
| Risk | A-bin: SF's C bootstrap interface (4 + 5 functions, stable since 1.6, ABI 12 in 1.8.0-dev) | Wine needs overrides, and SF's updater can overwrite Kiln's proxy. Kiln has no control over SF. | divergence, subtle behaviour gaps |
| When SF updates | A-bin: **players keep SF's updates** (profiles for new game builds, API additions) with no Kiln release, as long as the four exports stay | same | every SF change must be ported |
| Licence | A-bin: Kiln ships nothing GPL. A-src: GPL (kfc) | nothing ships | MIT if written clean (never from kfc-parser) |

**Chosen (after the user's decision): A-src as a bundled GPL-3 component plus A-bin, behind one adapter, with B as the
automatic fallback.** kiln-shroudforge drives Kiln's bundled build of the fork by default. With "Use my installed
ShroudForge" it drives the player's SF. It steps aside when SF's own `dbghelp.dll` already started SF. Either way the
player's SF mods run, and coverage is complete because every SF and EML function is SF's own code. Upstream releases
reach players through `kiln/sync-upstream.sh` in the fork and a Kiln release. A player who prefers SF's own update
cadence uses the installed copy.

## 4. Licence boundary

- SF's repository is MIT, but **kfc-parser (Brabb3l's EML, GPL-3) is a git submodule** (`README.md`: "kfc-parser/ #
  Direct Git submodule"). Facts from the Cargo files:
  - `shroudforge-runtime.dll` (`modules/entrypoints/loader`, crate `shroudforge-modloader`, a cdylib), `shroudforge.exe`
    and the updater statically link kfc-parser's **`kfc` crate**. It provides reflection, guid, hash, kfc_format,
    container and resource formats: 68 of 187 `mod-runtime` files use `kfc::`, and so do `game-runtime/bridge` and
    `compatibility`. These binaries are GPL-3 combined works.
  - SF's `dbghelp.dll` and `dinput8.dll` are built from kfc-parser crates (EML's proxies): GPL-3.
  - `winmm.dll` (bootstrap) and `kfc-runtime.dll` (native runtime) are SF's own MIT code with MIT dependencies (zydis,
    nlohmann).
  - SF's EML v1 Lua API (`mod-runtime/lua/eml/v1`) is SF's own MIT implementation, but it runs on the GPL `kfc` types. SF
    does not reuse EML's GPL Lua host (`mod-loader-lua`).
- **kiln-shroudforge.dll is MIT and contains no GPL implementation.** Its helper-process launcher is adapted from
  SF's MIT bootstrap with attribution. It loads the player's installed SF files at run time
  through their exported C functions, which is what SF's MIT bootstrap does. Kiln ships the GPL runtime as the separate component described below; the adapter does not link its implementation.
- The user decided (2026-11-07) that EML is supported as a separate GPL-3 plugin, `kiln-eml.dll`, in its own directory,
  and that Kiln stays MIT. The bundled ShroudForge runtime is shipped the same way: `kiln/plugins/shroudforge/` holds
  `shroudforge-runtime.dll` (GPL-3 through kfc), `kfc-runtime.dll` (MIT), `LICENSES/`, `NOTICE.txt` and `SOURCE.txt`.
  Each Kiln release carries `source/shroudforge-kiln-<commit>-src.tar.gz`, the fork at the pinned commit with its
  submodules (kfc-parser, nlohmann-json, SDL3). The fork is public on GitHub at that commit.

## 5. What is built

### 5.0 The fork: `frostdev-ops/shroudforge` branch `kiln` (Kiln submodule `third_party/shroudforge`)

The Kiln layer lives in `kiln/` (MIT): `build.sh` (SF's web UI, `shroudforge-runtime.dll` through `cargo xwin`,
`kfc-runtime.dll` through CMake with clang-cl, then the staged component), `native/CMakeLists.txt`, `cmake/`,
`sync-upstream.sh`, `UPSTREAM-VERSION` and `UPSTREAM-PATCHES.md`. The app-icon build script
is skipped under `SHROUDFORGE_NO_APP_ICON`, because winres cannot run `rc.exe` on Linux. The fork also fixes startup
archive publication on Windows: release the component's own readers/writers before commit or abort, and keep the
previous target intact when a rename fails. Recovery validates staging, rollback and journal paths before deleting or replacing files;
linked paths and malformed or duplicate journal entries are refused. Focused recovery tests pass on the host (7) and under Wine (5).
`kiln/README.md` documents merges. A trial merge of upstream/1.8.0 into `kiln` had no conflicts, and the runtime exports and ABI 12 are unchanged.
In Wine, SF's own tests pass: `bundled_lua` 3/3 and `survival` 1/1. Kiln's `tools/package/stage.sh` builds it into the
`shroudforge` component (default on, both sides). `tools/deploy-server.sh` deploys an existing build.
`tools/release/release.sh` exports and ships its source (`tools/release/lib/sf-source.sh`) and appends its notice.

**The pass's change journal** (since 0.2.0-dev.32; fork branch `dev32/review-j3` at `6e01cd74`: `24496270` of
`dev32/j3-shroudforge-registries`, on top of `6cf01443`, and the review's two commits, the digest and the budget; `kiln/README.md` "The pass's
change journal"): after each package's entrypoint the pass compares the resources
it edited with what they held before (as the commit encodes them; at most 3 s of encoding and comparing per pass, then
the rest is neither encoded nor compared for it and is recorded as no package's) and writes
`<stem>.kfc.change-journal.json` beside its record of the pass: per package, the members it changed at their published
places, its creations and contents, and the packages the retry ran without, as Kiln's change journal format 2 bound to
the published `<stem>.kfc`'s SHA-256, each package with the digest of its files (`contentSha256`: the files the pass
fingerprint hashes, `kiln.shroudforge.package-files.v1`), so effect replay matches a recorded effect by id, version and
files. Kiln's registry names hosted ShroudForge packages from it per field on the Conflicts page, and reads it for effect
replay ([registries guide](modding/registries/README.md#who-else-wrote-a-field)). Nothing
the pass writes changes.

**After a game update, without SF's Updater** (fork `2eb8f0b` and `112d939`, read from the fork's source, not run
against a real game update). When the working asset backups belong to the previous game version, upstream SF has its
Updater move them to its recycle bin before it captures new ones; the bundled component has no Updater, so that step,
and with it every startup asset preparation after a game update, failed. In a game folder with neither
`shroudforge/shroudforge-updater.exe` nor an installed `shroudforge/shroudforge-runtime.dll`, the fork now moves the
outdated `<stem>.kfc_resources.bak`, kiln-eml's `<stem>.kfc.bak.integrity` and then `<stem>.kfc.bak` (header backup
last, so an interrupted move is resumed in the same folder at the next start) to
`<game>/shroudforge/backups/outdated-working-baseline/<stem>/<size>-<mtime>/`, named from the header backup's size and
modification time. One generation is kept; a finished generation of the same name is replaced whole, never mixed.
The backups, the seal and the folders from `<game>/shroudforge` down must be regular files and folders: a link among
them is refused before a file moves.
Then new backups are captured from the live files, as upstream does. A new backup appears under its name only when
its copy is complete: until then it is `<stem>.kfc.bak.partial` / `<stem>.kfc_resources.bak.partial` in the game
folder, flushed and then renamed, so a start that ends during the copy (the resource file is about 200 MiB) leaves a
`.partial` file that nothing restores from, and one can stay behind after a killed start. With an SF installation in
the game folder nothing changes: its Updater and recycle bin are used.

**After a game update, when the pass fails** (fork branch `kiln-dev31-baseline`, 0.2.0-dev.31.13). On kiln-win1
(Kiln 0.2.0-dev.31.11, 2026-11-25) no ShroudForge mod ran at any start since the game's update of 2026-11-03/04:
- the pass failed at its first read of SF's settings: `debugConsole.toggleKey: Hotkey must be an SDL binding string`.
  `shroudforge/config/modloader-config.json` came from ShroudForge 1.7 and keeps the windows' toggle keys as Windows
  virtual-key numbers (121, 113, 120). Upstream converts them in its Updater's first post-install job
  (`MigrateLegacyToggleKeys`); the bundled component has no Updater (`run_post_install_migrations` returned at once),
  so nothing did. Now a start without an Updater runs upstream's own `migrate_legacy_toggle_key_settings` first
  (`embedded.rs` `run_hosted_migrations`; F10, F2 and F9 become `SDL:` bindings, a mouse button `Mouse:<n>`).
- the recovery after the failed pass only knew how to restore the working baseline. The backups were of the previous
  build (`1273043 ... 2026-10-20` beside the game's `1287369 ... 2026-11-04`), so the check refused them (right) and
  the recovery failed with "could not restore both baseline asset files": the adapter then starts the game without any
  ShroudForge mod, not only without the pass's. Now the recovery asks `check_working_baseline` (with the last-output
  proof) as the pass does (`recover_failed_startup_assets.rs`): a pair that may go back is restored; game files that
  are a clean copy of their own are the ones the failed pass did not touch, so the game starts on them, the old pair is
  set aside (`outdated-working-baseline`) and a new one is made from them (log: `the game's own files were kept as
  the new clean copy`); a refusal fails the recovery with its own text, which the record keeps as `recoveryError`.
  The record also says what the recovery did: `recovery` is `restored` (the pair went back) or `kept` (the game's own
  files were kept), so the launcher never says the data was put back when it was not.
- a new clean copy (`capture_working_baseline`) is copied whole before either backup gets its name, the companion
  first and the header backup last; a copy that cannot be finished (a full disk while the 215 MB resource file is
  copied) leaves no backup and removes its `.partial` files. Before the review it left a header backup without its
  companion, and the recovery right after (or the next start) set that half aside, and the set-aside's housekeeping
  removed the generation that held the old pair. The housekeeping now lets only a whole pair replace the generations
  kept before it: a half set aside is kept beside the newest whole pair (at most two generations).
- before any new working baseline is taken from the game files (after an update, Steam's file check, a first start),
  Steam's own record decides where it is there (`modules/game-files/check_steam_installed_files.rs`):
  `steamapps/appmanifest_<app>.acf` names the installed depot manifests, `<Steam>/depotcache/<depot>_<manifest>.manifest`
  lists every file with its size and SHA-1 (on Windows also the depotcache of the Steam installation the registry
  names, for a library on another drive). Both asset files as listed: taken, and the log's reason ends `(Steam's
  manifest of build <id> lists the game's files as they are: they are the ones Steam installed)`. Either one not as
  listed: refused, "... are not the files Steam installed for build <id> (...) ... Verify the game files in Steam (or
  update the server files with SteamCMD's validate), then start again", and the backups stay where they are. No
  manifest (a SteamCMD server, a folder outside a Steam library): the archive's own structure decides, as before. On
  kiln-win1 the manifest of build 26430625 lists both live files byte for byte (read from a copy of its depotcache).
- kiln-eml takes the same evidence where it takes a new pair itself (kiln mode, `load.rs` `create_backup_with`), and
  the launcher's engine list now reads a failed recovery: kiln-shroudforge adds `startupPrepared: false` to `failed`,
  and ShroudForge's line says that no ShroudForge mod ran, why, and (for a refusal) how to get a clean copy back:
  "in Steam, right-click Enshrouded, Properties, Installed Files, Verify integrity of game files, then start the game
  again from the Kiln launcher" (`crates/kiln-manage/src/sf_startup.rs`).
Valve's key-value files (the appmanifest) are read 16 tables deep at most: one nested without end overflowed the stack
of the game's start (fork and kiln-eml readers alike).
Tests: the fork's `verify_working_baseline` (12, 3 new), `steam_files` (6), `kiln_baseline_tests.rs` (8, 5 new:
the failed pass after an update, files that are not Steam's, kiln-win1's whole case with legacy keys, a clean copy that
cannot be finished, the hosted toggle-key migration), `set_aside_working_baseline.rs` (a half beside a kept whole
pair), red before; Linux and native Windows (kiln-win1). kiln-eml `load::kiln_tests` (2 new) and `steam_files` (6),
kiln-manage `tests/sf_startup.rs` (2 new), red before; native `run.py` `prepare-fails`. Not run against a real game
update or a real client.

**A clean copy that carries an uninstalled mod's changes** (fork branch `kiln-dev31-stale-data`, `26b9fece`
and its review to `a9dc2ad2`, 0.2.0-dev.31.16). HadToRegister (2026-11-25/26) saw props break on their own within 15 m, through walls, with
20,000/200,000 damage numbers, for weeks, with Kiln Auto Loot off; Steam's "Verify integrity of game files" ended it.
The cause was Auto Loot Plus (his first mod, long removed), whose Lua part creates an impact program
(`a5c0de5a-1e00-4d0d-9a11-0000000a5000`, `autosmash.impact`: every second, `ForEachEntityWithFactionInRange` over
Environment within 15 m, `ApplyDamage`) and an aura template (`...0a5002`) in the game data. That program was in the
working baseline, not only in the live files:
- his game folder lists `enshrouded_055.dat` (3,977,216 bytes) and the empty `_056` to `_063` written 2026-11-13 15:18
  and `_064` to `_127` written by the pass of 11-26 01:50. A pass's writer appends its containers after the
  baseline's, so the baseline (`enshrouded.kfc.bak`, `enshrouded.kfc_resources.bak`, both 2026-11-13 18:00) lists
  `_000` to `_063`: nine containers, one of them with 3.9 MB of content, that the game's own build (`_000` to `_054`,
  10-24 to 11-03) does not have. `enshrouded_055.dat (2).DERP` of 11-12 02:32 (right after `.kiln-eml.lock`, 11-12 02:31)
  has the same 3,977,216 bytes: the same mods' output, Auto Loot Plus's among them (its log runs to 11-19). The writer
  pads to a power of two (55 + 1 = 56, padded to 64), so `_055` to `_063` are one pass over the game's own 55. His
  `enshrouded.kfc_resources.bak` has 216,432,640 bytes; Steam's manifest of the same build (26430625, read on
  kiln-win1) lists `enshrouded.kfc_resources` at 215,052,288: the copy is 1,380,352 bytes of resource data longer.
- the pair was taken on 11-13 18:00 of game files that already carried that output, and sealed by kiln-eml
  (`enshrouded.kfc.bak.integrity`, 18:00:32). No start before 0.2.0-dev.31.13 asked Steam whether game files a new
  copy is made of are Steam's. On 11-23 21:01 the first start with baseline records took the whole pair up and
  recorded it (`enshrouded.kfc.baseline.json`: "a whole pair without a record is taken up").
- from then on `check_working_baseline` found the pair whole (its record) and the game's files built over it, and
  returned `Restore` (`verify_working_baseline.rs:132` at `6cb84f34`) before anything asked Steam. 0.2.0-dev.31.13's
  Steam proof sat only in `replacement()` (`:173`), the path that takes a new copy from the game's files; a pair that
  goes back was never compared with Steam's manifest. The pass restored the pair and built today's mods over it, and
  the starts between reused that pass (`kiln-sf-prepared 2`; his last start's pass took 380 ms).
- Steam's file check wrote Steam's own `enshrouded.kfc`; the pair then listed more than the game's header, so the game's
  files were no longer built over it, `replacement()` found them Steam's and the pass retook the copy from them. That is
  why the check ended it, and the old pair is in `shroudforge/backups/outdated-working-baseline/`.
Now (`proven` in `verify_working_baseline.rs`) a pair that would go back is first compared with Steam's manifest of the
installed build (`steam_files::installed_backups`: the backups in the place of `enshrouded.kfc` and
`enshrouded.kfc_resources`; the baseline record keeps each backup's SHA-1 since this patch, so a recorded pair is not read
again, and an older record is hashed once and recorded anew). Listed byte for byte: restored. Listed otherwise, the
pair is not the installed build's own:
- when the game's files are Steam's (and no pass of this runtime wrote them), the pair is set aside and a new one is
  taken of them;
- otherwise the pass refuses only where the pair is proven to carry a mod's changes: it holds a known mod's program
  (`KNOWN_LEFTOVERS`: Auto Loot Plus's auto-smash program `...0a5000` and aura `...0a5002`, the resources its Lua part
  creates; Kiln Auto Loot's own smash program is `86bdce19-...` and is not on the list), or its resource file is the one
  Steam installed with more data after it (`steam_files::extends`: every archive writer Kiln knows keeps the resource
  file up to its last chunk and only appends). The words are "Your game files still contain changes from a mod that
  isn't installed. Verify the game files in Steam (Properties > Installed Files > Verify integrity), then start again."
  followed by why (the server's words name SteamCMD's `app_update 2278520 validate`);
- without such proof the pair goes back as before and the pass logs (an `[sf]` line of kiln.log) why it is in doubt and
  the one thing to do if a removed mod still acts in the game (`Baseline::Restore`'s `doubt`, `doubt_words`; review of
  this patch). Steam's appmanifest can name a build whose files are not the ones in the folder (an update it has not
  finished, a folder copied from another PC), and a refusal there would have stopped every ShroudForge mod on a guess.

No manifest (a SteamCMD server, a folder outside a Steam library): restored as before, unless the pair holds a known
mod's program, which is refused and named. A prepared record of scheme 2 no longer lets a start skip its pass (scheme
3), so the first start after the update runs the check once; later starts reuse the pass as before. A refusal changes
no file; its recovery refuses the same and the game starts without ShroudForge's mods, the record's `recoveryError`
keeps the words, and the launcher's engine line and the Deck's note lead with them (`crates/kiln-manage/src/sf_startup.rs`,
`staleGameData`). kiln-eml decides the same way in EML mode before it builds from or restores its pair (`load.rs`
`judge`, README item 43), sets a replaced pair aside in `kiln/eml/backups/outdated/<stem>/` (one kept) instead of
overwriting it, and a kiln-mode start refuses a working baseline, or game files it would take as one, that holds a
known mod's program (`baseline-stale-data`; the header only, no extra read); it accepts the fork's scheme 3 record as
hosted ShroudForge output. Tests: the fork's `verify_working_baseline` (6 new), `kiln_baseline_tests.rs` (2),
`prepared_stamp`, `baseline_record` (1 new each), `steam_files` (2 new); kiln-eml `load::kiln_tests` (4 new),
`ownership` (scheme 3), `steam_files` (2 new), `m6_stale_backup` (1 new); kiln-manage `tests/sf_startup.rs` (1 new);
red before. Not run against his files or a real client.

**Textures on every core, and blobs made earlier in the pass** (fork branch `kiln-dev31-fast-encode`, `6cb84f34`,
0.2.0-dev.31.13). Players with EMF Beastmaster's creature packs, and any mod that needs ShroudForge's runtime beside
them (ammo-slots' native DLL), waited several minutes on their first ShroudForge pass (414 s offline here, below).
- **Where the time went:** BC7 sRGB encode on one thread, 397 of the 401 s of Lua of the three packs (kiln-eml's
  profile of the same packs). In a pregame pass `image.encode_texture` now cuts a BC1-BC7 texture into strips of whole
  block rows (at least 64 blocks each) and encodes each strip with upstream's own `kfc::content::image::encode`: every
  block depends only on its own 4x4 pixels, so the output is byte for byte the serial encode. It follows kiln-eml's
  `encode_with_threads` (0.2.0-dev.31.13), but at the call site, since the fork's kfc-parser is upstream's submodule;
  both use detached helpers, so that the calling thread never waits for a helper that has not started (below). The helpers therefore get their own copy of the texture's RGBA8 pixels, and each strip
  is encoded into its own buffer: while one texture encodes it holds about twice the serial encode's memory for it
  (an 8192x8192 BC7 texture: 320 MiB more, 256 MiB of pixels and 64 MiB of strips), whatever the thread count, freed
  when that texture is done.
- **When:** `run_with_api` sets the count for a pregame pass only and puts it back when the pass ends: the PC's
  threads, at most 16, or `KILN_SF_ENCODE_THREADS` (1 = the serial encode; a count at most 64). The in-game runtime
  keeps the serial encode. In a Kiln-hosted start the pass runs inside `shroudforge_prepare_startup`, which
  kiln-shroudforge calls from its `kiln_plugin_early` (`on_ready` with `g_early`) in kiln-core's early step: on the
  game's main thread at Kiln's entry gate, after the early native mods, before sigdb and hooks, outside DllMain and the
  loader lock. ShroudForge's own bootstrap calls it on its preparation thread while its gate holds the entry, also
  outside the loader lock. Another thread can still hold the lock then (a deferred native mod, an injected overlay), and
  a thread created meanwhile only starts once the lock is free. So the calling thread takes strips from the same counter
  as its helpers and waits only for strips a helper has begun; with no helper running it encodes the whole texture
  itself, in the serial time. While a helper of an earlier texture has not started, no new helpers are made.
- **The blob:** ShroudForge's `get_content` (`access_file_content.rs`) answered only blobs listed in the game's own
  header; EML and kiln-eml also answer blobs created earlier in the same pass. The beasts pack writes the English text
  table back as a new blob and the wilds pack's harness script reads it: under ShroudForge it got nil, and the Llama
  Harness, Camel Saddle and Mule Harness were not added. It now asks the pass's new contents first
  (`AppState::has_content`, EML's rule); `get_all_contents` still lists the header only, as in EML.
- **Offline proof** (2026-11-25; reflink copies of the vanilla client data of build 26430625, the three packs, no game
  started; ShroudForge's startup pass `run_with_local_schema` as its loader calls it, through the dev31 slot at nice 19
  on a PC at load average about 16-19): the fork at `e3edecc3` took 414 s (398 s CPU) and left out the harness
  items; at `6cb84f34`, 16 threads, 95 s (461 s CPU). Its game data equals kiln-eml's (`45575990`, 97 s) byte for
  byte: all 92 229 resources (320 new, 5 game resources changed) and all 209 new blobs (87.6 MB), the harness's text
  blob included. Against `e3edecc3` the 208 shared blobs (every texture) are identical: the threads change no byte.
Tests (red before): the fork's `kiln_encode_in_strips_tests.rs` (9: every BC format against the serial bytes for
uneven strips and more threads than rows, sizes that are not whole blocks, helpers that never start, the thread count,
its env value and its scope) and `kiln_same_pass_content_tests.rs` (2 whole passes: package A creates a blob and B reads
it; a texture the pass encodes on several threads is the serial encode). Not run in a real client.

**A working baseline that is not the source of the game files** (fork branch `kiln-30.7-fixes`, 0.2.0-dev.30.7). Before
every startup pass SF copies `<stem>.kfc.bak` and `<stem>.kfc_resources.bak` over the game files. It checked only the
header backup's version tag and archive format: a player's header backup from before a game update that kept the tag
went back over the updated files, and the pass failed with "failed to fill whole buffer" (the old header pointed past
the end of the resource file). Nothing tied the companion to its header either. Now `check_working_baseline`
(`modules/mod-runtime/startup/verify_working_baseline.rs`) decides first:
- it restores only a whole pair that is the source of the game files. Whole: both backups are the pair
  `<stem>.kfc.baseline.json` recorded when it was made (size and SHA-256 of each; changing the recorded last-write times
  neither discards the hashes nor invalidates identical bytes); a pair from before
  that record must hold its header's resource data in its companion, and is recorded once restored. Source: the live
  header is the header backup, or was built over it (its containers, resource chunks and contents unchanged, additions
  only after them, as every incremental writer SF, EML and Kiln use writes).
- it sets the pair aside (as after a game update; the record moves with it) and copies a new pair from the game files
  when the pair is not their source (an update or Steam's file check wrote them), is of another game version (SF's own
  rule), or is not whole while the game's header is the backup's own. SF's log, and so kiln.log's `[sf]` lines, says
  `ShroudForge: its clean copy of the game data (<stem>.kfc.bak, <stem>.kfc_resources.bak) was not used: <why>. It was
  set aside and a new clean copy was made from the game's current files`.
- it refuses the pass when no clean copy is left: the game files carry mods built from a pair that is not whole, are
  what SF's last pass left (its record of the pass, read before the pass removes it), or are not whole themselves. The
  error ends "Verify the game files in Steam (or update the server files with SteamCMD's validate), then start again";
  the recovery after the failed pass refuses the same pair and changes no file. Missing or incompatible backups pass
  the same live-file checks before a new pair is taken. A missing backup companion is never borrowed from modded live
  files, and a refused pass keeps the last-output proof so another start cannot mistake those files for a clean copy.
- restoration stages both files before publishing either, using SF's recoverable asset transaction. A failed copy
  leaves the live pair untouched; interrupted publication keeps rollback data for the next start.
Tests (cross-built with `cargo xwin test --no-run`, executed on native Windows): `verify_working_baseline` (9),
`baseline_record` (4), and three whole startup passes in `kiln_baseline_tests.rs` (red before the fixes). Not run
against a real game update or a real client.

**EML manifests with null optional metadata** (fork branch `kiln-30.7-fixes`, 0.2.0-dev.30.7). EML reads its manifest
into a type whose `description`, `license`, `icon` and `dependencies[].optional` are optional, so `null` there means
"not given". SF's manifest schema allowed null only for `license` and `icon`: a player's EML package with
`"description": null` was refused with "mod: null is not of type string". The schema
(`modules/modloader/manifest/manifest.schema.json`) now takes null for `description` and `dependencies[].optional` too,
and a schema error names the field after the document (`modules/loader-config/validate_document.rs`), so the launcher
and SF's log say, for example, `JSON error in <package>/mod.json: mod: /description: false is not of type "string"`.
Errors of the whole document (a missing required field) read as before; `authors`, `capabilities` and `dependencies`
stay non-null, as in EML. Tests (Wine): `kiln_manifest_tests.rs` (2, red before).

**Large asset walks** (fork branch `kiln-30.7`, 0.2.0-dev.30.7). Every `mlua::Value` that holds a Lua object (a string,
table, function or userdata) pins one slot of mlua's auxiliary stack while it lives, and that stack has about a million
slots. The values of an asset tree kept one such value for each child a mod visited (a value a mod made, one for each
field and element; a resource, one for its `data`), so a startup mod that walks a large asset (a player's Forged and
Found 0.16.1 on a Steam Deck) ran the pass into "cannot create a Lua reference, out of auxiliary stack space (used
1000194 slots)", and the game started without its asset mods. A walk over 350,000 plain recipe records, with no mod
code, reproduces that count; upstream 1.8.1 has the same code. The fork keeps such values in Lua's registry
(`modules/mod-runtime/lua/stored_lua_value.rs`) and loads one for each operation: the same Lua object every time, so
identity and edits hold; nil, booleans and numbers stay plain; a released tree is collected as before. The type
metadata `game.types` hands out, which lives as long as the session, is held the same way (each type, its
`struct_fields`, `enum_fields` and `attributes`): read whole for the game's 12,658 types it held about 695,000 of the
million slots. Tests (host and Wine): a walk over and a mod-made copy of 350,000 recipes (red before, with the player's
panic), the references a walk holds (red before: 3002 for 1,000 recipes, 150002 for 50,000; now under 64 at any size,
and the registry back to its count once the tree is released), identity, edits, copies and removal; all the metadata of
2,000 struct types read (red before: 254,037 references; now under 64), its values, order and identity.

**A panic in the startup pass** (fork branch `kiln-30.7`, 0.2.0-dev.30.7). The pass logged "Rust panic during startup
asset preparation: " and the panic's message, which named no mod and could run over several lines. Now kiln.log's
`[sf]` error line, and the pass's recorded error, read `ShroudForge stopped preparing mod assets because of an internal
error while running <name>`: the name of the package whose entrypoint ran, when the panic came from there, on one line,
at most 80 characters, with no version, path or message. The panic's message follows on an info line
(`Internal error detail, for a bug report: ...`, one line, each absolute path in it cut to its last two components,
such as `state/extra.rs:287`, so no folder of the player's or of the build machine reaches a shared kiln.log). A panic
is SF's own failure, so the pass is not run again
without the package; the recovery is upstream's (the saved baseline back, the game starts without asset mods). Tests
(host and Wine): `name_failing_mod`, `report_startup_panic`, `kiln_retry_tests`.

**A start reuses the last pass while runtime mods write into the export folder** (fork branch
`kiln-dev31-exports-reuse`, `86c2f8db` and `1672d8af`, 0.2.0-dev.31.12). Since 0.2.0-dev.29.16 the fork skips an
unchanged startup pass: after a successful pass it writes `<game>/<stem>.kfc.prepared.json` (the preparation
fingerprint, the export switch, Steam's build id, `.kfc` and `.kfc_resources` by size, time and SHA-256, both working
backups by size and time), and a start applies again only when that no longer holds
(`modules/game-files/record_prepared_game_files.rs`, `check_asset_preparation.rs` `already_applied`). With a pregame
exporter in the set (a package with `patch` and `export`, or `export` alone; `export_pass_needed`), that record (scheme
1) also listed every entry of `<game>/shroudforge/exports` with its size and time. Runtime mods write there while the
game runs, so every start said
`[startup] Startup asset mods are applied again target=enshrouded reason=exports differs from the last pass` and ran the
whole pass: 17 to 20 s without, 623 to 638 s with the Beastmaster EML packs, "unusable" (a player's report, 2026-11-25).
His export folder: flat files at its top from pregame exporters (XHL Area Harvest's `xhl-area-harvest-*.tsv`/`.txt`,
Auto Loot Plus's `auto-loot-plus-report.txt`), Unified EML Tweaks' own folder `unified-tweaks/` (a pregame exporter:
`patch` and `export`), and Unified Tweaks Live's `unified-tweaks-live/` (`runtime` and `export`: written while the game
runs). Pregame exporters are not scoped to a folder of their own, so the pass now notes what it uses: the loader hands
the pregame session a note (`RunOptions::export_touches`; the in-game phase gets none), and `io.export`,
`export_exists`, `read_export_to_string`, `export_rename`, `export_copy` and `export_delete` add each path,
`export_list` the folder. The record (scheme `kiln-sf-prepared 2`) keeps each path's state (a file's size and SHA-256,
over 16 MiB of its first and last MiB plus its time; a folder; nothing there; a link by where it leads and what is
there, as the export API follows links) and each listed folder's names (a link entry with where it leads), and nothing
else of the export folder. It keeps that whenever exports are on (`exports.enabled`), with or without a pregame
exporter: the export API checks the switch, not the package's capability, so a pregame package with `patch` alone can
read and write exports too. A pass that used a path the record cannot name (a part with `:`, such as an NTFS stream)
leaves no record. So an exporter's output changed or deleted, a file it read or looked for changed or appearing, or a
file added to a folder it listed runs the pass again; a runtime mod's file, a native DLL's (Weapon Holster's
`weapon_holster/`), World Editor's or the diagnostics' do not. kiln-eml's proof of hosted ShroudForge output
(`hosted_shroudforge_output` in `mod-loader-lua/src/ownership.rs`, which keeps the witness from taking that output for
the game's own data) reads only the record's `files`, `target` and `build`, which scheme 2 kept as they were, and
accepts both schemes; it took scheme 1 alone, so a scheme 2 record would have proved nothing. A scheme 1 record means
one pass at the first start of 0.2.0-dev.31.12, then reuse; `left_live_header` still reads scheme 1, so the
working-baseline check keeps its proof across the update. Back on an older Kiln, a scheme 2 record is one of another
scheme there: the pass runs, and that Kiln's kiln-eml takes the record for no proof, as when there is none. The other
parts of the record were checked for changes between starts and have none in a Kiln-hosted start: nothing writes the
game files or the backups once the pass is done (kiln-eml stands aside and patches nothing, and marks the record stale
only for a handoff an earlier kiln-mode start owes), the build id is Steam's, and the fingerprint skips dotfiles and
`*.log` in package folders (a native DLL that writes other files into its own package folder at run time would still
cost one pass a start; none of the player's does). Tests (host): the fork's `prepared_stamp` (16) and
`kiln_exports_reuse_tests` (5 whole passes on a small archive), both shaped on the player's export folder, red before,
and kiln-eml's `hosted_shroudforge_output_is_proven_by_its_whole_record_only` (a scheme 2 record proves the output, red
before). Not seen in a game client.

**BodyHeat's calculation is signed** (fork branch `kiln-dev31/bodyheat-signed`, `465aad9f`, 0.2.0-dev.31). SF
Survival's Infinite Frost Timer (`BodyHeat_Min = BodyHeat_Max` through `runtime.overrides.attribute`) always answered
"signedness-dependent program with conflicting resource/storage scalar types": the attribute resource types the BodyHeat
root (0xC7AEAF1D) `sint32`, its component `keen::ecs::BodyHeat` stores `keen::uint32` (a `UInt32Attribute`), and its
programs are sign-dependent (Clamp, and BodyHeat_Max's ScaleToNewMax), so `bind_storage` refused every calculation of
it. The engine runs that root on its signed interpreter only (`docs/re/combat.md`, "BodyHeat runs on the signed
interpreter"), which is the interpreter ShroudForge's model runs for it. `bind_storage` and `from_resources` admit
exactly that root with exactly that pair (`ENGINE_SIGNED_ROOTS`, `engine_signed_root`) as a `sint32` calculation; the
common-range write domain still holds for every word before and after, and the reverse pair, any other root and a float
storage keep the refusal. kiln-sfi (`catalog.rs` `bind_storage`) and kiln-eml (`sf_startup/attributes` `from_resources`)
carry the same rule for kiln mode. Tests: the fork's `reflection/build_attribute_catalog.rs` and
`lua/ingame/ecs/attributes/tests.rs` (host).

**Writes into values a mod created** (fork `63b8862`, branch `kiln-sf-eml`, on top of `112d939`). In the pregame pass a
value a mod builds (a plain table put into game data, such as a recipe inserted into the recipe registry, or a whole
`resource.data` replaced with a copy) becomes an owned value (`modules/mod-runtime/lua/resource_values/owned_values.rs`).
Upstream (1.7.9 and 1.8.0, and EML's `mod-loader-lua` `simple.rs`, where the code comes from) checked every write into
such a struct or array against the struct's or array's own type instead of the written field's or element's type; the
game's mapped values check the field type. So `recipe.knowledgeRequirement.knowledgeOrQueryId.value = id` worked on the
game's recipes and failed on one another mod had added (`at .: expected keen::HashKey32, got 1715248921`). With More
Gliders (EML, adds two recipes) and SF Unlock Blueprints on together, the whole pass failed, SF restored the baseline
and no data mod applied (dev.24 player report). The fork checks the field and element type, as the mapped values do;
an unknown field and a wrong type are still refused. Tests: `lua/resource_values/tests.rs` (4, under Wine; all failed
before). Lab `sfeml` (server 26430253, dev.24 server pack with this component): More Gliders + Unlock Blueprints
`Unlock Blueprints updated 2544 recipes` (2542 alone), and the player's full set (nine packages; on a server the pass
applies aldrich-bare-body, auto-loot-plus, more-gliders, sf-crafting-essentials and sf-unlock-blueprints) one applied
pass, `Committed 3965 changed resources`. With auto-loot-plus (capability `export`) in the set and SF's
`exports.enabled` on, SF runs the pass again at every start by design (`export_pass_needed`); without it the second
start finds the pass current. (Since 0.2.0-dev.29.16 a record of the pass lets such a start skip it, and since
0.2.0-dev.31.12 runtime mods' writes into the export folder no longer undo that; see "A start reuses the last pass"
above.) kiln-eml's own copy of EML (`gpl/kiln-eml/eml/mod-loader-lua`) still has the old check.

**One failing startup mod no longer costs the others** (fork branch `kiln-sf-retry`: `42b59dd`, `c20c0c5`, `1ab36f8`,
`33c0d06`, `4b1ab16`, `1073446`, `dc8b392`, `e625c2f`, `430e287`, `16de6f2`, `2df1de6` on `ec75fb6`; Kiln
0.2.0-dev.29.18). Upstream puts the game data back after a failed startup pass
and starts the game with none of the packages the pass writes (a `patch` part, or `export` without a runtime part, and
the EML packages it runs). In inbox 79 one package's own check failed ("Missing base localization: keenglish", after a
third-party EML mod had changed that data) and the player lost every startup mod. Now, when the error came from one
package's own entrypoint (`LuaModRunner::run` names it, `PregameModError`), the pass runs once more
(`asset_preparation/retry_without_failed_mod.rs`): from the clean baseline, as every pass starts, with that package
treated as switched off (`ModEnvironment::without`), so the packages that need it drop out too. Once only, no search.
Not retried, upstream's behaviour unchanged: an I/O error anywhere in the error's chain (`PregameModError::io`, read
from the error itself, so in any language and also without an OS code, such as a short read of a replaced archive), an
error text with `(os error N)` or access denied (an I/O error the API or the package put into words), and every failure
no package raised (the working baseline, staging, the type model, commit and publication, a replaced `.kfc`). When the
second pass fails as well, no startup package loads, as before. SF's record (state.json `assets`) gets two optional
fields: `skipped` (`id`, `name`, `version` and the error on one line without the Lua traceback, for the package, then
for the packages left out with it: "needs <id>, which failed in the pregame pass") and, after a second failure,
`retryError`; `error` stays the first failure's, so a reader that knows neither field reads the record as before.
A pass that ran without a package is `applied` with the configuration's own fingerprint: the next start keeps the data
and the record while nothing changes; switching the package off, or any other change, runs the pass again. SF's log
(`startup-assets`, W, so kiln.log's `[sf]` lines, as warnings from 0.2.0-dev.29.18: the mirror reads the level from
the start of ShroudForge's line, `[W <date> <time> UTC]`, `native/plugins/shroudforge/src/log_level.h`) says `ShroudForge: <name> (<id> <version>) failed in the pregame pass
(<error>); running the pass once more without it`, then `...; started the game without it, the other N mods loaded` or
`ShroudForge: the pregame pass failed again without <name> (<error>); the game starts without the mods of the pregame
pass`. SF's in-game Modloader page shows the package failed (`asset-preparation-skipped`), the packages left out with it
blocked, and the pass partial. The
launcher's side is in 5.7 below. Tests (Wine, `cargo xwin test`): `kiln_retry_tests.rs` runs whole passes on a small
archive pair with made-up packages (7), plus `retry_without_failed_mod.rs` (2), `name_failing_mod.rs` (2, I/O errors
raised through a real Lua call), `plan_mod_load_order.rs` (1) and `read_configuration_status.rs` (1). Not yet run against a real game client.

### 5.1 `native/plugins/shroudforge` → `kiln/plugins/kiln-shroudforge.dll` (MIT)

The runtime choice comes first. **Bundled**: `kiln/plugins/shroudforge/`, the default. **Installed**: `<game>/shroudforge/`,
used when `kiln.json` has `"shroudforge": {"useInstalled": true}` (the launcher Settings toggle "Use my installed
ShroudForge"; env `KILN_SHROUDFORGE_RUNTIME=installed|bundled` overrides). It logs which one runs
(`ShroudForge runtime: bundled 1.8.1 kiln.<commit> from ...` since dev.30). When both are present it says how to switch. A chosen
runtime that is missing, unsafe, fails to load, has an unsupported version or lacks compatible exports falls back to
the other one with a warning. Supported unversioned bootstrap contracts are SF 1.7.8, 1.7.9, 1.8.0 and 1.8.1; the native DLL must
provide all five required exports and ABI 12. Missing `version.json` is incompatible. The runtime directory and its
children cannot be reparse points; ancestors above that authorized root are the player's Steam/Wine layout and are
not judged. Resolved paths must stay inside the root; Wine file links are excluded by the installer. DLL loads use
restricted dependency search paths and verify the loaded file identity. In bundled mode
it copies the bundled game profiles that are missing into SF's runtime folder and never overwrites a file. The data
folder is SF's own `<game>/shroudforge/` (config, logs, state, cache, profiles). Mods come from SF's mods folder.
`kiln/state/shroudforge.json` gains `source` (bundled|installed), `runtimeDir`, `bundled`, `installed` and
`useInstalled`. Then, for whichever runtime was chosen:
Since dev.24 it also has `startupPrepared: true` once `shroudforge_prepare_startup` returned success in this process
(absent when it failed or did not run). ShroudForge returns success when it applied its startup asset mods, when it
found an earlier pass still current, and also when a pass failed and it restored the saved baseline (it writes a fresh
`failed` record first: fork `embedded.rs`, `recover_failed_startup`). So the flag means only that its own record of
the pass (`shroudforge/state.json` `assets`) describes this start's game data, not that any asset mod applied: a
reader needs the record's own `status` (`applied`, `failed`) beside it. Kiln's menu reads both (ui.md). kiln.log says
`ShroudForge startup preparation returned (... ms): the [sf] lines and its record of the pass ... say whether its
startup asset mods applied` (before this, `ShroudForge startup preparation done`, the line in the evidence below).

Its early export runs preparation on the main thread while Kiln's gate holds the game entry point. Both runtime DLLs
are validated before preparation. The `kiln.eml` v2 reservation prevents two startup KFC writers:

1. Detect a loaded SF bootstrap/runtime first, even when files have disappeared from disk: status `self-hosted`.
2. Decide whether to host. `KILN_SHROUDFORGE=0/1` overrides. No-mods/invalid profiles are off. Active profiles need an
   enabled `mods.shroudforge` entry. Legacy layouts need an enabled SF package or previous SF startup patch state;
   the presence of bundled binaries alone leaves EML-only installs with kiln-eml.
3. Try the preferred runtime pair and then the other pair. Validate versions, paths, exports and native ABI before
   startup side effects. Copy missing bundled game profiles using checked paths.
4. Reserve the EML writer through `stand_aside` while collection is open, then call startup preparation synchronously.
   Old/incompatible EML tables or an already-finished EML writer make SF refuse preparation. When SF owns startup,
   EML packages use SF's configured mods folder; Kiln-only EML profile/private-root folders are not forwarded.
5. Initialize the native runtime. Failed native initialization disables ticking and cleans up the provider. On early
   `on_ready`, start the worker: create, then every 16 ms tick + update, then destroy + shutdown serially on that worker.
   Foreign export faults and C++ worker exceptions stop SF and report failure. The unload callback (kiln-core never calls it) and process detach only signal,
   with no loader-lock wait; worker handles and runtime modules remain alive until process teardown. Callback code stays mapped for the worker lifetime.
6. On a writer thread next to the loop, every 2 s: mirror SF's log and publish heartbeat/status. The adapter skips SF's bootstrap update check/pending-update install
   and Debug Console. SF's own exported preparation can still start its config controller.

Startup preparation and SF's loop can run in Kiln safe idle too: the adapter uses SF's own exact-build profiles and
needs no Kiln hook. A failed preparation is reported; the adapter does not attempt another writer after startup side effects.

### 5.2 `crates/kiln-manage/src/shroudforge.rs`

`detect`, `mods_dir` (SF's `paths.mods`), `is_sf_mod` (tells SF/EML Lua packages from EMF mods),
`read_mod`/`list_mods`, `skip_reason` (plain-words skip messages), `set_enabled`, `set_setting` (checks type, range and
options; it splices only the value, typed as SF reads it back (a fractional setting's numbers keep a fraction or an
exponent), so the file stays byte-identical otherwise),
`settings_schema` (JSON Schema for the launcher's SchemaForm with `x-kiln-control`, `x-kiln-group`, `x-kiln-restart`,
`x-kiln-enum-labels`, `x-kiln-enabled-when`), `settings_values`, `status`, `effects` (SF's per-mod `runtime.report_effect` states from its `state.json`). Unit
tests and shared managed/Rust package fixtures cover parsing and byte-preserving edits. Runtime reports require a fresh
heartbeat and active runtime; a confirmed memory write is not proof of the gameplay effect.

### 5.3 In-game settings (`managed/services/Kiln.Ui/ShroudForge*.cs`, MIT)

The existing Mods details panel reads SF's package metadata, enabled switch, settings, groups, conditional controls
and public group actions. It edits SF's own files with a JSONC span writer, not a parallel Kiln config. Shared fixtures
in `schema/fixtures/shroudforge/cases.json` exercise the Rust launcher and managed implementation, including comments,
CRLF, typed numeric/bool/list options, chained/cyclic conditions, canonical SDL shortcut text, live/restart rules,
refused edits and exact written bytes. No SF fork change or second UI executable is required.

A `keybind` setting is a capture box (since 0.2.0-dev.31.18, `SfShortcuts.cs`): click it and press a key or the middle
or a side mouse button, with Ctrl, Shift or Alt. Kiln stores SF's canonical text for the key's place
(`Ctrl+Alt+Shift+Meta+SDL:<n>`, `Mouse:<n>`): the SDL code comes from the press's make code and E0 flag through SDL3's
`windows_scancode_table` (Pause, Print Screen and Num Lock by their virtual key, as SF's own conversion), the same table
the hosted runtime's feed and kiln-sfi's port use, so the captured code is the one SF sees for that key on any layout
(the Ü key of a German keyboard is `SDL:47`). The box takes the press on the window thread like the Keys page's rebind
(kept from the game and from key bindings, dropped when the page is not drawn or a Kiln text field takes the keyboard;
Escape closes the menu inside kiln.ui);
Backspace, Delete or Unbind clear it; the wheel and controller buttons are refused with the reason (SF shortcuts read
the keyboard and the mouse only). The text field below still takes SF's own form, and the page converts readable forms
(`Ctrl+G`, `F5`, `Mouse4`, a layout character such as `Ü`) before it saves; the data layer and the shared fixtures stay
strict. Shown names follow the player's keyboard layout for letter and punctuation keys (`KeyLayout.cs`, also on Kiln's
Keys page); other keys keep Kiln's names.

The ShroudForge group lists the profile's SF mods and every other package of SF's folder that SF reads, except
Kiln's copies of the profile's EML mods: those are rows of the EML group, which say whether SF's switch runs them, and
the launcher sets their switch from the profile. While SF runs, a player's own legacy EML folder (mod.json and
`src/mod.lua`, no `extended.mod.json`) is listed too, switched off, as SF's own Modloader lists it: switching it on
writes the `extended.mod.json` SF's Modloader writes at the first change.

The native adapter reports `menu: helper|kiln|off|server|unavailable` and `menuKey` (`SDL:66`, F9, by default).
Only without a running hosted helper does it hold `Local\\ShroudForge.ModMenu.Open.<game-pid>`; the managed worker
waits on that auto-reset event. This is SF's real `shroudforge.ui.toggle_modloader()` contract. It never competes
with the installed helper for events. The fallback key is remappable in Kiln's Keys tab; unsupported/mouse-only/Meta
or empty configured bindings do not silently become F9, nor does a `toggleKey` that is not text (null, a number:
ShroudForge refuses those too), which reports `menuKey` "" and a warning in kiln.log. Helper exit hands ownership to Kiln; stopped runtime removes
the fallback binding. SF module-off and self-hosted behavior stay intact. An installed `shroudforge.exe` older than the
runtime Kiln runs (by its install's `<game>/shroudforge/version.json`; the exe has no version resource) opens neither
window, since it refuses the newer settings that runtime writes (a 1.7.9 app beside the bundled 1.8.1: `Additional
properties are not allowed ('uiTargets' was unexpected)`): kiln.log says so in one warning, Kiln's page takes the
shortcut and mods load as before (0.2.0-dev.30.7, `src/helper_version.h`). One whose version cannot be read (no
`version.json`, no version in it, or text that is no version) starts its windows as before, and kiln.log says in one
info line that they may fail if the app is older.

Windowless native Wine tests exercise absent/installed/exited/disabled/self-hosted helpers, an app older, equal,
newer or of unreadable version (`run.py` `app-*`; the comparison itself: `tests/test-helper-version.sh`) and repeated
auto-reset signals. Managed tests exercise settings, writer coalescing/failure recovery, state freshness and shortcut ownership.
A headless software preview renders the shared-fixture SF panel at 1280×800 and its main-menu page at 1920×1080.
In an isolated server lab, the actual managed UI writer queue changed `sf-probe` live: setting on confirmed a guarded
write, setting off restored the baseline, and the enabled switch deactivated/reactivated its runtime without errors.
Neither proof is rendered game-client or Steam Deck interaction. Keybind capture and text editing, color editing, combos in place
of radio/segmented layouts, action execution, gamepad focus and gamescope presentation still need client checks.

### 5.4 `mods/tests/sf-probe` (lab probe, MIT)

An SF mod that uses only SF's public API: `runtime.services.get("ecs::GameSettings")` +
`runtime.overrides.service(...,"enableDurability")` with the setting `disableDurability`. It proves a guarded write, SF's
readback and the restore on a live setting change, all without players.

### 5.5 Held W/S glider steering: unresolved client check

The sustained W/S steering report for SF Unlimited Flight (a Windows player, 10 Nov 2026: steering works with
standalone SF, not with Kiln) is **not confirmed fixed**, and the host key feed (section 5.6) does not change it.
No real game client has been run for it. SF source lines below are the fork pin `112d939` (the cited files outside
`modules/hotkeys/` are unchanged since `724a884`, upstream 1.7.9), paths under SF's `modules/` unless they start with
`mods/`; the mod's `mods/sf-unlimited-flight/src/mod.lua` is the same file from SF 1.7.7 to the 1.8.0 dev line.
Moving the adapter's periodic file I/O (heartbeat, status file, log mirror) off the mods loop fixes reproduced
update-loop stalls under slow file I/O. That is not evidence about steering: the loop still reads the native
status every 2 s, and a `write-confirmed` effect report alone does not show what the game's rotation code saw.

**Where the steering input comes from.** On each of its updates (every 100 ms, `mod.lua:308`) the mod reads
`keen::ecs::PlayerInput` `fromClient.movementStickInputMode` and `keen::ecs::ActorInput` `desiredLocalMoveInput.z`
with `runtime.ecs.read_fields` (`mod.lua:148-158`). It then writes the active glider's ItemInfo
`equipment.gliderConfig.pitchAngleSpeed` (`mod.lua:256-266`): held S (z < 0) gives the negated native rate (climb),
no key gives 0 (hold), held W (z > 0) gives the native rate (dive). `read_fields` copies the game's component memory
in process, on SF's worker thread (`mod-runtime/lua/ingame/ecs/access_component_fields.rs:97`, `KfcRuntimeEcsRead`
in `game-runtime/native/ecs/read_entity_components.cpp:86-106`, a direct read under the read-only binding,
`game-runtime/native/ecs/bind_world_entities.cpp:84-101`). There is no key API, no helper process and no OS key
polling on this path. The game reads the keyboard through its own raw input (Kiln's
`native/services/ui/input.cpp:3-7`) and fills that component itself.

**SF's key helper is a different path.** `shroudforge.input.is_keybind_down` and `was_keybind_pressed` (for mods
with a `control: "keybind"` setting) take key state from a hidden child,
`<game>/shroudforge/shroudforge.exe --keyboard-input --parent-pid <pid>`. The runtime starts it itself when such a mod
loads in game and reads JSON lines from its stdout pipe (`mod-runtime/lua/shroudforge/v1/create_mod_api.rs:74-91`,
`hotkeys/observe_keyboard.rs:389-425`, `loader-config/resolve_installation_paths.rs:62-64`). Kiln's bundled
component has no `shroudforge.exe`. Before dev.24, without an installed SF, the runtime logged
`[hotkeys] SDL input stopped ... Could not start isolated SDL input` and `Keyboard shortcuts unavailable`, and those
queries answered "not held" for the whole session (`observe_keyboard.rs:612-630`); a support report from another
Windows player with this mod enabled under hosted SF shows both lines (the steering reporter sent no report). Since
dev.24 a bundled runtime takes that key state from Kiln (section 5.6); without the feed, an SF installed next to
Kiln still has its exe started by the runtime (bundled or installed runtime alike; the message format is unchanged in
1.8.0), and Kiln's helper start (`processes.cpp`) does not touch it. sf-unlimited-flight declares no keybind and
never calls `shroudforge.input`, so neither the helper nor the feed is the cause of the steering report. They matter
for SF mods with shortcuts (section 2).

**Check.** `lua5.4 native/plugins/shroudforge/tests/held_key_flight.lua <SF source> [<staged component>]` runs the
real mod with SF's own override helpers and synthetic flight fixture, with `shroudforge.input` answering as it does
without the helper. S and W held for 2 s each reach the steering through ActorInput (rates -70, 0, 70), with no
`shroudforge.input` access. A mutant mod that steers from `shroudforge.input` fails it with the reported symptom.
It is a Lua replay: it shows where the mod takes its input from, not what an affected client's game wrote there or
what the game's rotation consumer read; that needs the affected session. The same check shows three of the four ways
the mod itself can produce a "neither key works" or "no climb" glider; the fourth is from the source (it has no
session change in the check):

- The ActorInput the mod reads stays 0 while a key is held: neither key does anything. The effect report is then
  identical to working steering (`no-change: Native glider controls`), so the effect detail cannot reveal this case.
- Target-angle input mode, or more than one `PlayerInput` entity in the authoritative world: the mod leaves the
  game's own rate (W dives, S does not climb). Its effect detail names the reason ("Upward tilt requires pitch-speed
  input controls" or "requires one authoritative player").
- The mod's override tracker faults: a world or layout change (a new session id, for example after one failed
  `ResolveEcs` of SF's native tick) while the mod holds its shared glider override
  (`mod-runtime/lua/ingame/overrides/track_runtime_overrides.lua:11-13`). From then on the mod returns on every update
  (`mods/sf-unlimited-flight/src/mod.lua:213-214`) and leaves the last rate in place, usually 0: neither key works,
  until the game restarts. Its effect report is `write-failed` "World changed before shared values were restored.
  Restart the game before writing again".

**Kiln on the same path.** kiln-rules wraps the glide step and its gravity and stamina sites. Unless a Kiln gliding
rule applies to that player it returns the game's result unchanged (`native/services/rules/rules.cpp:407-413`), and
it only ever changes velocity, never the rotation or the pitch rate (`rules.cpp:439-502`). kiln.ui keeps W/S from
the game only while Kiln captures input (logged as `ui: input capture on`) or a Kiln binding consumes the key. That
would stop walking too. A stall of SF's loop on Kiln's worker leaves the last rate in place until the next update;
the adapter's file I/O (heartbeat, status file, log mirror) runs on its writer thread, but the native status read
every 2 s still runs on the loop.

**Player check** (about two minutes; same game, glider, world and settings as when it failed):

1. Start the game through Kiln and load the world. Close every Kiln menu and window. SF Unlimited Flight is on, with
   its Upward Gliding setting on (the default).
2. Walk with W, then with S. Note whether both move you.
3. Glide. Hold S for two seconds, let go for one, then hold W for two seconds. For each key, note: nose up, nose
   down, or nothing.
4. Open **Mods → ShroudForge → SF Unlimited Flight** and switch the mod off (it switches while you play). Close the
   menu, glide again and hold W for two seconds. Note whether the nose goes down. Switch the mod back on afterwards.
5. If you can look at the Kiln launcher's Logs page while gliding: when SF Unlimited Flight's line says "Upward tilt
   requires pitch-speed input controls" or "requires one authoritative player", note that instead. The mod then
   leaves steering to the game by design, and this check does not apply. When it says `write-failed` "World changed
   before shared values were restored. Restart the game before writing again", note that too.
6. Make a support report straight afterwards.

Reading it: step 3 works, so the report is not reproduced. Neither key works in step 3, but W dives in step 4: the
game gets the key, but the value SF reads does not change. The next step is then an SF-side trace in the affected
session of `ActorInput.desiredLocalMoveInput.z`, `PlayerInput.fromClient.movementStick` and the world and entity SF
reads, while a key is held. W does nothing in step 4 either: the game's own glider ignores W under Kiln. The next
step is then on Kiln's side (`ui: input capture` lines in kiln.log, the kiln-rules glide hooks), not SF's. Only W
works in step 3: see step 5. Step 5 shows the `write-failed` "World changed ..." text: the mod stopped writing after
a world or layout change and holds the last rate (the fourth case above), whatever steps 3 and 4 showed; steps 3 and 4
then say nothing about Kiln or about the value SF reads. Restart the game, check again, and send the support report
of the session in which it appeared.

### 5.6 Mod shortcuts: Kiln's host key feed

Since fork `9d586f1` (pin `112d939`) `shroudforge-runtime.dll` has seven optional exports, declared in the fork's
`kiln/abi/input_host.h` (MIT, version 1): `shroudforge_input_host_version`, `_begin`, `_raw_key(make_code, flags,
vkey)`, `_window_key(wparam, lparam)`, `_mouse_button(button, down)`, `_reset` and `_end`. After `begin` the runtime
starts no helper process, and `shroudforge.input` answers from what the host passes, translated with SDL's own Windows
scan-code table (a key gets the `SDL:<n>` code SF's settings store). Calls run one at a time inside the runtime, from
any thread; the mods' queries never wait for them.

kiln-shroudforge feeds it (`native/plugins/shroudforge/src/key_feed.cpp`):

- **When.** A client start that Kiln hosts (bundled or installed runtime), when the chosen DLL has all seven exports
  and answers version 1, kiln.ui v5 comes (`subscribe_window_input`, [ui.md](ui.md#c-native-plugins)), no
  `<game>/shroudforge/shroudforge.exe` is installed, and `KILN_SF_KEY_FEED` is not `off`. Otherwise no export is called
  and the runtime's own path is unchanged: an installed `shroudforge.exe` helper, or no shortcuts. Upstream SF and fork
  builds before `9d586f1` have no exports; a dedicated server has no window.
- **Begin, then attach.** The adapter starts SF's mods worker, and so `shroudforge_create`, from the `on_ready` of its
  early pass (`kiln_plugin_early`): the core runs it right after the early phase, before it loads any service DLL
  (`native/core/src/early_plugins.cpp`, `boot.cpp`), and in safe idle nothing of Kiln's comes after it. kiln.ui is
  published by `kiln-ui.dll` only at Kiln's plugin step, whose `on_ready` (the adapter's second one) comes later on the
  same boot thread (a real start: early `on_ready` 14:37:19.570, `kiln.ui v5 published` 14:37:20.008). `begin` has to
  come before `shroudforge_create` (mods ask for input while they load), so it is decided at the early `on_ready`, and
  the subscription is made at the plugin step:
  - no `shroudforge.exe`, and kiln.hooks served to the early plugins (the core serves it on every start that may hook,
    so this start has a plugin step): `begin` at once (`waiting`). At the plugin step the adapter subscribes (`kiln`), or
    says why it cannot (`none`: no kiln.ui v5 there either, or it refused the subscription). No record is passed in
    between; the game window does not exist yet. Nothing is lost by beginning: without the feed and the helper there
    are no shortcuts at all.
  - `shroudforge.exe` installed: no `begin`; the helper keeps the key state (`helper`). `begin` gives the helper up for
    the session (`end` does not bring it back), and kiln.ui may not come at the plugin step (a `kiln-ui.dll` that did not
    load or is older, policy `any` on a game build whose UI places do not resolve).
  - kiln.hooks not served at the early `on_ready`: a safe idle start (an unverified build with policy `verified`), which
    has no plugin step. No `begin`; `none` (or `helper`) at once.
  - kiln.ui already there when the worker starts (a start in which the early pass did not start it, the old test
    order): subscribe with forwarding shut, then `begin`, as one step.

  A `begin` that answers 0 means SF's own input already runs (`helper`): the adapter unsubscribes, if it had subscribed,
  and feeds nothing.
- **Records.** Each key goes through one of the two key functions, as `input_host.h` asks: every raw keyboard record,
  and window key messages only while the window has given no raw keyboard record. The game registers raw input without
  `RIDEV_NOLEGACY`, so most keys arrive both ways, and the window can take `WM_INPUT` ahead of the messages
  ([docs/re/client-render.md](../re/client-render.md) "Input": clicks reached Kiln's overlay twice that way). Forwarded
  both ways, a quick tap could count as two presses, and so could Print Screen (the runtime counts its lone `WM_KEYUP`
  as a press). Buttons the same way: raw button records, else the button messages. If the corresponding raw-input
  registration is removed or targets another focused window, legacy input resumes (including releases of held raw keys
  and buttons). A failed registration query keeps the existing source. A focus loss calls `reset` once
  (kiln.ui sends one record for `WM_ACTIVATEAPP` 0 and `WM_KILLFOCUS` together; `reset` re-reads about 500 key names
  on the window thread).
- **While Kiln has the keyboard** (since 0.2.0-dev.31.18): while kiln.ui's status says `capturing` (Kiln's menu, a Kiln
  screen or text field, a ShroudForge shortcut's capture box), no press of a key or button is passed; releases always
  are, so a key held before Kiln took the keyboard is let go. Kiln's interpreter (kiln-sfi `input/feed.rs`) counts no
  press then either, and SF's own Modloader answers "not pressed" while it records a shortcut. Before, a key typed in
  Kiln's search or pressed in a mod's own shortcut box also ran that mod's shortcut (likely netjunk's Altar Cleanup
  "nothing to undo", logged the moment his Kiln menu opened). A status that cannot be read counts as not capturing. Known
  limit: a modifier pressed while Kiln has the keyboard and still held after it lets go is not seen by SF until it is
  pressed again. Print Screen's lone `WM_KEYUP` (window messages only: Windows sends no `WM_KEYDOWN` for it), which the
  runtime counts as a press and its release, is held back too unless the runtime holds the key. `run.py`
  `keys-captured`.
- **Teardown** (the worker, also after a failed create or update): unsubscribe (kiln.ui waits for a record in flight),
  then `end` under the adapter's lock, then `shroudforge_destroy`. The chosen runtime DLL is never unloaded. No export
  is called after `end` or destroy, even when the window thread delivers a record during teardown or kiln.ui's
  unsubscribe fails. The fork keeps the feed's state in process-wide statics, so `end` and destroy could come in either
  order; `end` first is what the fork advises. A feed still `waiting` (the runtime stopped before the plugin step) is
  ended the same way and gets `none`.
- **Threads.** The window thread holds the adapter's lock for one record and the runtime's feed lock inside the export.
  The mods loop takes neither; its queries take only the runtime's short state locks. Kiln calls the feed from the
  window thread, and once from the worker (`end`) after it unsubscribed, so the window thread never waits for the mods
  loop. The attach runs on the boot thread while the worker may already be inside `shroudforge_create` or its loop: it
  subscribes holding nothing and makes the subscription live under the adapter's lock only while the teardown has not
  begun. An attach that loses to a teardown removes its own subscription again (holding nothing), and the teardown's
  word stands; no record is passed after `end` either way.
- **Status.** `kiln/state/shroudforge.json` `keys`: `{"source": ..., "reason": "..."}`, one of:
  - `waiting`: `begin` was called at the early `on_ready`; nothing is passed until the plugin step attaches the
    subscription, so no shortcut fires yet. Replaced at the plugin step, or when the runtime stops. It stays only in a
    start that served kiln.hooks to the early plugins and still turned out safe idle (the symbol definitions are
    missing: boot.json `safeIdle`).
  - `kiln`: Kiln passes the game window's records (since the attach). It is not a check that a mod's shortcut fires.
  - `helper`: SF's own `shroudforge.exe --keyboard-input` gives the key state, or SF's own input already runs.
  - `none`: no shortcuts this start; the reason says why (a server, no feed in this runtime, safe idle, no kiln.ui at
    the plugin step, the runtime stopped before the attach, the switch, a fault).

  kiln.log has one line per word: `ShroudForge mod shortcuts: key state from Kiln, waiting for kiln.ui: ...` at the early
  `on_ready`, then `key state from Kiln: ...` (or `from nowhere: ...`) at the plugin step; a final answer at the early
  `on_ready` is `from ShroudForge's own input: ...` or `from nowhere: ...`. SF's own log says `[hotkeys] Keyboard and
  mouse input is fed by the host` after `begin`, and `Keyboard shortcuts unavailable` without it and without a helper.
  Kiln.Ui and the launcher do not read `keys` yet.
- **Switch.** `KILN_SF_KEY_FEED=off` in the launch environment gives the runtime its own path back (an installed
  `shroudforge.exe`, else no shortcuts). With SF installed next to Kiln the helper keeps the key state in a real start
  (above); the feed replaces it only when kiln.ui is already there as the worker starts.

The first dev.24 builds asked for kiln.ui at the early `on_ready` only, where it never is: every real start said `none`
(`kiln.ui v5 ... is not available in this start`), the runtime tried its helper (`Could not start isolated SDL input`,
`Keyboard shortcuts unavailable`), and the feed never began. Their test host had offered its fake kiln.ui from the first
call.

Not covered, by design: recording a new shortcut (press to capture) is still SF's Modloader window, or the setting's
text on Kiln's page (editable `SDL:` strings). AltGr through window messages counts as Ctrl+Alt (Windows adds a fake
left Ctrl there); raw input has no fake Ctrl, so with the game's raw keyboard input AltGr is the right Alt alone. Keys
the game window does not get are not seen: another window has the focus, or a module that subclassed the window above
Kiln keeps the message (SF's own helper read the keyboard in the background). SF answers shortcut queries only while a
window of the game's process is in front, with or without the feed.

Checks (none with a game window):

- `native/services/ui/tests/test-window-input-wine.sh`: kiln.ui's production window procedure on a message-only window
  under Wine, with made-up raw input records (76 checks: every kind, unchanged and in order, including extended keys,
  repeats, double clicks and button press-before-release; what the game and Kiln's own key handling get is the same
  with a subscriber; captured or consumed records still arrive; oversized HID records are not decoded; raw registration
  removal and target changes; per-subscriber focus coalescing and clearing Kiln's held keys; subscribe/cancel during
  delivery, cross-thread and reentrant unsubscribe; a faulting subscriber is removed).
- `native/plugins/shroudforge/tests/run.py`, cases `keys-*` (34): a fake runtime and a fake kiln.ui. Most replay the
  real boot order (`host.cpp` `KILN_SF_REAL_ORDER`: the early pass and its `on_ready` with kiln.hooks and no kiln.ui,
  then kiln.ui appears, then `kiln_plugin_main` and `on_ready` of the plugin step, then the window's records; the host
  prints `shroudforge.json` after each step). Begin before create and before the subscription, `waiting` then `kiln`;
  each kind forwarded unchanged and in order under the rule above, reset, unsubscribe before end before destroy; raw
  registration removal resumes legacy keys and buttons without duplicating returning raw taps; one export missing,
  another version, begin refused, a dedicated server, the switch; kiln.ui missing or older at the plugin step (begun,
  never fed, ended, `none`), the same with `shroudforge.exe` installed (no begin, `helper` from the start), safe idle
  without kiln.hooks (no begin, the final word at once, with and without the helper), no plugin step although kiln.hooks
  was served (`waiting` until the runtime stops); the worker stopping right before the attach and while the attach is
  inside kiln.ui's subscribe with a record handed over at once (no record after end, no subscription left, the
  teardown's `none`; a copy of the adapter that kept that subscription fails it); faults in all seven exports; a
  teardown while the window thread sends records without a pause, with and without a working unsubscribe (no export
  after end or destroy; a copy that never shut the gate and one that ended first both fail it). Six cases keep the old
  order, kiln.ui from the first call (`kiln_plugin_main` alone, or the early pass with kiln.ui already there).
- `native/plugins/shroudforge/tests/real_keys.py`: the component built from `112d939` by its `kiln/build.sh` behind
  the adapter, in a scratch game folder with no SF installed and a probe mod with three shortcut settings (`SDL:66`,
  `Ctrl+SDL:66`, `Mouse:4`). SF's API needs the game's type registry and archive reader, so copies of the client's
  `enshrouded.exe`, `.kfc` and `.kfc_resources` sit in that folder (read, never run); the test host runs as
  `enshrouded.exe` from another folder, and the runtime's `GetForegroundWindow` import answers with a message-only
  window of the host, because Wine without a display has no top-level window. Records through the adapter: F9 down/up,
  F9 again as window messages (not forwarded: no change), Ctrl+F9 (F9 alone stays not held), XBUTTON1, F9 held across
  a focus loss. The probe logs exactly those changes of `is_keybind_down` / `was_keybind_pressed`. With
  `KILN_SF_KEY_FEED=off`, and with the adapter before this change: `Keyboard shortcuts unavailable` and no shortcut
  ever fires.

A real client has to show: an SF mod's shortcut (any `keybind` setting, for example World Editor's F3..F8) reacts in game on an
install without ShroudForge, `keys` says `kiln`, kiln.log has `key state from Kiln, waiting for kiln.ui` and then, after
`service kiln.ui v5 published`, `key state from Kiln:`, and no `Keyboard shortcuts unavailable`; a held shortcut
lets go after Alt+Tab; one tap is one action (a toggle does not toggle twice); a key Kiln's menu or a Kiln binding
takes (Insert, a bound key) still reaches SF's shortcut state; Print Screen bound as a shortcut fires once.

### 5.7 ShroudForge's own loader under Wine and Proton

A self-hosted ShroudForge (its own `dbghelp.dll` loaded through `WINEDLLOVERRIDES=dbghelp=n,b`) is upstream's code,
not the fork's. Upstream 1.7.9 publishes its startup pass with its reader still open on `<exe>.kfc_resources`
(`KFCCursor::new` in kfc-base's `container/reader.rs` opens it for the session's lifetime; `commit_assets` calls
`transaction::commit`, whose `replace` removes the target and renames the staged file onto its path). Windows deletes
the open file at once and the rename succeeds. Under Wine and Proton the removal returns success but the name stays
until the last handle closes, so the rename is refused with "Access denied (os error 5)", the recovery in `commit` is
refused the same way, and the abort in the session's `Drop` (still before the reader closes) a third time. ShroudForge's log then has
`Failed to abort typed asset transaction error=... (os error 5)`, `pregame phase failed: ... (os error 5)`,
`Asset mods failed; saved baseline restored` and, per mod, `startup-asset-apply-required: the early startup transaction
did not publish this asset configuration`. The game data is put back (the reader is closed by then), the game starts,
and every startup mod (capability `patch`, and the EML packages ShroudForge runs) is missing. Because a failed pass is
not "prepared", this repeats at every start. The fork closes the reader before publishing (`03b4950`, section 5.0), and
upstream's 1.8.0 branch does the same with this cause in its comment.

Evidence (player report 327, 13 Nov 2026, a client under Proton with ShroudForge 1.7.9 next to Kiln 0.2.0-dev.25): the three log lines above with `os error 5`, `kiln/state/shroudforge.json` `mode: self-hosted`. The
two publication modules were run as they are on made-up files under Proton Experimental's and GE-Proton11-11's Wine
(both 11.0), with a second handle on the resource file as the session has it: upstream 1.7.9 fails in `commit` and in
the abort with os error 5 and recovers once the handle is closed; with the handle closed first, upstream's and the
fork's `commit` both publish. The two calls of `replace` alone, under Proton Experimental: `remove_file` of the open
target returns success and the file still exists, `rename` onto it is refused with os error 5, and after the handle is
closed the file is gone and the rename succeeds. Not run: the player's mod (it carries Lua), and a game with upstream's
own loader.

What Kiln does about it (it cannot fix the other loader's process, and it never moves a loader the player installed):
`crates/kiln-manage/src/sf_startup.rs` reads ShroudForge's own record of the pass (`<paths.state>/state.json`
`assets`). The record names no process, so it counts only when it belongs to Kiln's last start (`kiln/state/boot.json`):
written no more than two minutes before that start began (ShroudForge's own loader holds the game's entry point for its
pass, and Kiln boots at that entry point), and run by something at that start: kiln-shroudforge's
`kiln/state/shroudforge.json` says `hosted` or `self-hosted` and was written in that start (its `pid` and `updated`
against boot.json's `pid` and `started`; under Proton the pid repeats, so the time decides), or ShroudForge's own loader
is installed and wrote the record after the adapter's last word. A start that skips Kiln's plugins ("Play once: without
mods") leaves both files as the start before it left them, and nothing is said about it.

**Where this rule is wrong (known limits, from the review of dev.27).** It ties the record to a start by times and by
the game's process id, because ShroudForge's record names neither. So the notice can be missing after a start that
crashed before Kiln wrote its boot record (a failure of that start is then taken for an older one), and after the
machine's clock was set back during a start (a record more than two minutes "before" the start is taken for stale);
and it can show an old failure again after the clock was set back between two starts, or when two game installs share
one state folder. None of this touches what the game loads: it is the notice only. The cure is a launch identity that
Kiln's core writes before any plugin runs and that the adapter and the record are matched against; that is a change in
the core's boot and is not in this release.

A `failed` record of the last start becomes

- a sentence on ShroudForge's row of the launcher's engine list, whoever ran the pass and whatever other loaders' files
  are in the game (`engine_status` `startupFailed`, `sf_startup::EngineNotes`, shown as a warning), and a pointer on the
  EML row while ShroudForge runs the EML mods: who ran the pass, ShroudForge's own words, the switched-on packages that
  need the pass on this side, what to change;
- on the Deck, a line on Home ("ShroudForge mods did not load") and a button to the whole text in a dialog the right
  stick scrolls, and
- the support report's finding `sf-startup-failed` with the record's line under "The last start" (ShroudForge's words
  are redacted whole before they are put on one line and cut).

When Kiln's ShroudForge ran the pass once more without the package it failed in and that failed too (dev.29.18, 5.0),
the sentence adds, after the first error, "It tried again without <mod>, and that failed too: <second error>"; the
report's line adds "; again without <id>: <second error>". Without those fields the text is as before.

A pass that failed in one package and ran again without it (the record `applied`, with `skipped`) is a warning, not a
failure: the other mods loaded. `sf_startup::skipped` reads it, and it counts even when the record is older than Kiln's
last start, because ShroudForge keeps an `applied` record while the next starts' passes are unchanged, as long as Kiln's
adapter says it hosted ShroudForge in that start and its preparation returned (`startupPrepared`); a fresh record counts
as a failed one does. A package switched off since is not named (the next pass runs without it anyway). It becomes
`engine_status` `startupSkipped` on ShroudForge's row (a warning naming the package, its error, how many other mods of
the pass loaded, and to turn it off or check it for a clash with another mod), the same line and dialog on the Deck's
Home ("one mod left out, the others loaded", or "N mods left out" with those that need it), and the report's finding
`sf-startup-skipped` (Warning) with the record's line under "The last start".

For this fault (Wine or Proton, `os error 5`, a ShroudForge build without the fix, Kiln's own ShroudForge with the fix
installed) the sentence names the cure: take `dbghelp` and `winmm` out of `WINEDLLOVERRIDES` in the game's Steam launch
options and keep `dinput8=n,b`; Kiln then hosts its own ShroudForge for the profile's ShroudForge mods. When Kiln hosted
the player's installed 1.7.9 ("Use my installed ShroudForge"), which runs the same code, the cure is that setting. A
build has the fix when its `version.json` names 1.8.0 or later, or Kiln's fork (`host: kiln`, `build: kiln.<commit>`;
the fork calls itself 1.7.9 too) other than the two pins Kiln shipped before `03b4950` (`kiln.e4c6535`,
`kiln.3586804`). A version that is not `major.minor.patch` gets no cure. The in-game Mods page reads the same record
and ShroudForge's per-mod errors while the game runs ([ui.md](ui.md)); what was missing is the launcher and the report,
which the player sees once the game is closed.

### 5.8 Packages the profile has off: the host's skip list (0.2.0-dev.31.12)

A player's report (0.2.0-dev.31.9): he had put an EML package (a DLL, mod.json, native-plugin.ini, ShroudForge's switch
on) into ShroudForge's mods folder himself, switched the mod off in his Kiln profile, and Kiln-hosted ShroudForge ran
it (its DLL loaded by the DLL Manager, its runtime mod "initialized and active") while kiln-eml reported
it `disabled`. Kiln never writes into, switches, replaces or removes a folder the player installed, and ShroudForge
reads only its own switch, so nothing stopped it.

The fork now takes a list from its host: one optional export of `shroudforge-runtime.dll`, declared in the fork's
`kiln/abi/host_skip.h` (MIT): `bool shroudforge_host_skip_packages(const uint16_t* ids)`, mod.json ids (or the folder
name of a package without mod.json) one per line, before `shroudforge_prepare_startup`. Every manifest read in the
process then has those packages switched off (`modules/modloader/kiln_host_skip.rs`, checked in `read_manifest`; the
DLL Manager also plans no DLL of such a folder without mod.json), so the startup asset pass, the DLL Manager and the
live runtime leave them out; nothing on disk changes. Additive; refuses an invalid id as a whole; false once the
startup pass began, which fixes the list and logs one `[host-skip]` line per package. A host that never calls it gets
upstream's behaviour.

Kiln's callers, each for its own entries, before the startup pass: kiln-shroudforge for the profile's ShroudForge
entries (`skip_profile_off`, `src/own_skip.h`; just before it asks kiln-eml to stand aside) and kiln-eml for its EML
entries (`host_skip` in `plugin.rs`, from `eml_stand_aside`; it finds the loaded `shroudforge-runtime.dll` with
`GetModuleHandleW`, as `sf_in_process` does). Both name one package per call, so one name the runtime refuses keeps
only its own package running; neither names a package that an entry the profile has on stands for. Each says per package, in kiln.log, whether it was skipped or why it runs
(engines.md, "A player's own ShroudForge folder"; eml.md). A runtime without the export (an installed ShroudForge, or a
bundled build before this) runs them, and the lines and `eml.json` say so. Tests: the fork's `cargo test -p
shroudforge-package kiln_host_skip` and `cargo test -p shroudforge-modloader --lib kiln_host_skip`; Kiln's
`native/plugins/shroudforge/tests/test-own-skip.sh` (host, `own_skip.h`) and kiln-eml's `own_folder_off_tests`.

**Pin.** The fork change (`c115e6c3`, the same bytes as the dev.31 task `sf-own-folder-off`'s `8f9358d`) rides in fork
branch `kiln-dev31-exports-reuse` at `1672d8af`, which 0.2.0-dev.31.12 pins (with the pass reuse of section 5.0). A
runtime without the export (an installed ShroudForge, or a bundled build before 0.2.0-dev.31.12) reports such folders as
running, as above.

## 6. What other agents need to do (detect, launcher)

- **Installer** (agent `detect`, `install.rs`): when ShroudForge is installed, install "next to ShroudForge" by default.
  Move only SF's `dinput8.dll` to the backup (`class: shroudforge, action: backed-up`, not chained). Keep `dbghelp.dll`,
  `winmm.dll` and `shroudforge/` in place, so no "switch and lose your mods". Uninstall restores `dinput8.dll`
  byte-identically when it is still Kiln's proxy; a player-installed replacement stays in place with a recovery warning.
  The first backup survives repeat installs and upgrades. Reinstall automatically recovers missing `winmm.dll` and
  `dbghelp.dll` from legacy switch receipts after verifying their original hashes, without overwriting newer helpers.
  Deactivate/uninstall also remove verified, unchanged profile placements outside `kiln/`. An exact SF extension receipt
  distinguishes Kiln's profile switches from player settings edits; edited/ambiguous copies are retained with paths,
  reasons and a warning that standalone SF may still run them. Old normalized-only receipts cannot authorize deleting
  settings. Game archives and previous writers' `.bak` / `.bak.integrity` are not reset to vanilla. At launch, a
  `dinput8.dll` classified as ShroudForge in a folder with a Kiln receipt means SF's updater replaced Kiln: offer to put
  Kiln back.
- **Library** (detect's kind framework, `library.rs`/`engines.rs`): add kind `shroudforge` through
  `shroudforge::is_sf_mod` and `read_mod`. The skip text comes from `shroudforge::skip_reason`. Import copies the package
  into SF's mods folder (`shroudforge::mods_dir`), because SF reads its packages only from there.
- **Profiles** (`schema/profile.schema.json` `mods`): add `"shroudforge": [{id, path, enabled}]`. The core already
  ignores unknown kinds with a warning (`profile.cpp:107`). Activation uses `engine_mods::place`: only Kiln's
  receipt-owned copies get their `enabled` flag changed. Original player folders, even when listed with the opposite
  profile switch, are never changed.
- **Launcher UI**: SF mods on the Library and Profile pages; a settings screen from `settings_schema` + `settings_values`,
  saved through `set_setting` (SF applies `live` settings at once); status from `shroudforge::status` + `effects`.
  Proposed texts are in [wiki-notes/sf.md](wiki-notes/sf.md). Replace "Kiln replaces ShroudForge" with "Kiln runs next to
  ShroudForge: your ShroudForge mods keep working".
- **Servers**: instance creation skips `shroudforge` (`server/instance.rs` reflink SKIP). For SF mods on a Kiln server
  instance, copy the base install's `shroudforge/`, `winmm.dll` and SF's mods folder. Modpacks: add SF packages as files
  under `mods/`. No Wine override is needed in hosted mode.

## 7. What ShroudForge needs from kiln-eml

Nothing at run time. Both the bundled and the installed runtime carry SF's own EML v1 implementation and link kfc
statically. kiln-eml becomes relevant in three places:

1. **Ownership of EML packages.** SF runs EML packages (no `extended.mod.json`, or `launcher: "EML"`) from its mods
   folder, but only those it has switched on: `enabled` comes from `extended.mod.json` and is false without it, so a
   legacy EML package without that file is never run until its switch is persisted (SF's own Modloader writes the
   file at the first change, `update_mod_configuration` / `ensure_eml_extension`). In a start where SF runs, kiln-eml
   stands aside (below) and runs nothing, so an EML mod in SF's folder that SF has off is applied by nobody. A real
   client (2026-11-11) had four profile EML mods there and one such file: SF ran one, and every status said all four
   ran. Since then:
   - The launcher (`engine_mods::place`) gives Kiln's copy of each profile EML mod in SF's mods folder SF's switch
     from the profile, while SF is the loader that runs the profile's EML mods (Kiln hosts it, or SF's own loader is
     installed): a copy without `extended.mod.json` gets the file `ensure_eml_extension` writes for a legacy EML
     package (`$schema`, `schemaVersion` 1, `enabled`, `targets` client and server, `launcher: "EML"`, `settings` {}), a
     shipped file only its `enabled` value, byte for byte otherwise (the same writer as SF mods' switch). The EML mods
     that carry a DLL, in `<game>/mods/<folder>`, get it too when that is SF's folder. The file is part of what Kiln
     placed: the receipt lists it without its switch and settings and records Kiln's bytes. Setting values changed in
     it later (in the game, through Kiln's page or SF's Modloader) are the player's: carried to the next version as an
     SF mod's are, and a copy holding them is kept and switched off instead of removed when the profile turns the mod
     off. The profile is the switch, as for SF mods. A copy an older Kiln placed gets the switch at the next Play. A
     player's own folder is never written; its note says when SF does not run it and what helps (a switch helps only a
     package SF has off, not one for the other side or one SF refuses). The Library entry is never written.
   - Without SF (no SF mods in the profile) nothing is written: kiln-eml runs the mods and reads only `launcher` from
     that file, so `enabled: false` there never switches a mod off for kiln-eml.
   - kiln-eml, standing aside for SF, reads that switch and the targets: a profile mod in SF's folder that SF has off
     is `off-in-shroudforge` in `kiln/state/eml.json` (with what to do; for a file SF refuses, such as another
     `schemaVersion`, the reason says that no switch helps), one for the other side `other-side`, and
     only the mods that run are counted in kiln.log's "N of M run under ShroudForge". Kiln's Mods page reads the
     packages as SF does while SF runs (Off, other side, applied when SF's record of this start's startup pass lists
     it), and lists a player's own legacy EML folder under ShroudForge with its switch, as SF's Modloader does.
   Rule: SF installed and running → SF owns `<SF modsDir>`; kiln-eml owns everything else.
2. **Shared kfc.** The bundled runtime builds kfc-parser from the fork's own submodule (tag `sf-v0.2.0`, SF's pin). If
   kiln-eml's shared core uses the same kfc-parser revision, the two could share one build. Today they are separate
   DLLs, each statically linked, which is simpler and needs nothing from kiln-eml. To wire them later, the fork's
   `Cargo.toml` would point `kfc` at kiln-eml's checkout through a Kiln-layer `[patch]` in `kiln/`. That would need a
   matching kfc API (`reflection`, `guid`, `hash`, `kfc_format`, `container`, `resource`, `content`).
3. **SF's in-game API ported to Kiln's C# API (section 8).** It must not use kfc. Kiln's own reflection and type data
   (`kiln.assets`, typegen) cover it.

**SF asset mods and EML mods together.** Both write the game's data files from the same vanilla backup
(`<exe>.kfc.bak`), so there must be one writer per start. When Kiln hosts SF, SF is that writer: its early step reserves
`kiln.eml` (`stand_aside`) before it prepares, kiln-eml publishes `aside` and writes nothing, and SF builds its startup
asset mods and the EML packages in its mods folder that it has switched on in one pass (the launcher copies the
profile's EML mods there with SF's switch on, so the game has both). kiln-eml writes only in a start where SF does not run (its mods off in the active profile,
`KILN_SHROUDFORGE=0`, or SF could not start). Then, if SF's state says it prepared asset mods in an earlier start, that
start's game data has the EML mods only: kiln-eml sets SF's state to `failed` and names the SF mods that are missing
(a warning in kiln.log and `reason` plus `sfDisplaced` in `kiln/state/eml.json`). The next start in which SF runs
prepares them again. EML mods that are only in the Library, not in SF's mods folder, do not load while SF is hosted
(`not-loaded` in `eml.json`), and those in its folder that SF has switched off do not run either
(`off-in-shroudforge`).

## 8. API coverage

Under Kiln every SF function runs as SF's own code (column "Under Kiln": **plugin** = hosted SF runtime). The Kiln C#
column names what a Kiln mod uses today for the same effect. The C# column states the actual scope, including remaining differences. Tested: **lab** = run in `wv-sf-1` (server build
26430253) through kiln-shroudforge with SF 1.7.8; **unit** = kiln-manage tests; **client** = on the human checklist;
**—** = not run in this slice (it works because it is SF's own code, but nobody has watched it yet).

### 8.1 Package and launcher features

| SF feature | Under Kiln | Kiln C# / launcher equivalent | Status / tested |
|---|---|---|---|
| `mod.json` (id, name, version, authors, deps, capabilities) | plugin (SF reads it); kiln-manage `read_mod` | `kiln.mod.json` | done; unit |
| `extended.mod.json` `enabled` | plugin; `set_enabled` | profile entry `enabled` | done; unit, lab (enabled survival + probe) |
| `targets` client/server | plugin (SF skips a mod outside its targets) | `side` in kiln.mod.json | done; lab (server) |
| settings `value` + label/description/control/min/max/step/options/enabledWhen/applyAt | plugin; launcher SchemaForm and in-game Mods panel edit the same file | `[Config]` classes + SchemaForm | shared Rust/managed fixtures; rendered SF panel still a client check |
| live settings (`shroudforge.settings.get` in callbacks) | plugin | `Config` reload events | done; **lab**: survival all off → "Runtime settings applied live", effect `no-change: All Survival settings are off`; probe off → restore |
| groups | launcher `x-kiln-group`; in-game labels, descriptions, settings and action buttons | config sections | managed package/action tests; execution client check |
| keybind settings (`control: keybind`) | plugin (SF's hotkeys); key state from Kiln's host key feed (bundled runtime, client, no `shroudforge.exe` installed), else from an installed `shroudforge/shroudforge.exe` (section 5.6) | `Kiln.Ui.KeyBindings` | feed: Wine harnesses and the real runtime with fed records (section 5.6), not a game client; before dev.24 bundled-only: never pressed (player log r1110); installed: client |
| locales `locales/*.json`, `shroudforge.i18n.get`, `shroudforge.locale()` | plugin | Kiln mod localisation | plugin; — |
| dependencies (SemVer, optional) | plugin (SF's load order) | resolver | plugin; — |
| `patch` capability (startup asset changes, game-file backup) | plugin (`shroudforge_prepare_startup` in `on_ready`) | `kiln.assets` | mixed-server lab: 89 resources committed, item count +2; second boot reused identical patched archive hashes |
| DLL Manager (native DLL mods, `runtime-register-dll`) | plugin | Kiln native mods (`native.json`) | plugin; — (DirectInput providers need SF's dinput8: not supported) |
| Modloader window, World Editor window | starts installed `shroudforge.exe` only; bundled runtime has no helper windows | in-game Mods → ShroudForge handles switches/settings, not the World Editor | helper lifecycle windowless tests; rendering client check |
| Debug Console window | not started (kiln.log has SF's log as `[sf]`) | Kiln console | by design |
| SF self-update / repair | not run when hosted | Kiln launcher (pending: SF update source) | by design |
| notifications feed (`shroudforge.notifications.publish`) | plugin (SF's state.json `events`); hosted without a ShroudForge window: Kiln.Ui toasts each new notice and lists a mod's newest three in its details (`ShroudForgeNews.cs`, 0.2.0-dev.31.19) | Kiln.Ui toasts | managed tests (`ShroudForgeNewsTests`); client: — |
| effect reports (`runtime.report_effect`) | plugin; kiln-manage `effects` | mod status in `state/mods.json` | done; lab |

### 8.2 `shroudforge.*` (mod API, `lua/shroudforge/v1/definitions/mod_api.lua`)

| Function | Under Kiln | Kiln C# equivalent | Status |
|---|---|---|---|
| `shroudforge.settings.get(key, fallback)` | plugin | `Config.Register<T>()` | lab |
| `shroudforge.ui.on_action`, `shroudforge.ui.toggle_modloader` | public action request files; toggle event opens installed helper, or Kiln's SF page when hosted without it | `Kiln.Ui` screens | action-file managed tests, event native Wine tests; actual action execution/rendering client check |
| `shroudforge.notifications.publish` | plugin; Kiln.Ui reads state.json `events`: toasts, the mod's details, toggle_modloader() opens the page on the mod that just published | Kiln.Ui toasts | managed tests; client: — |
| `shroudforge.i18n.get`, `shroudforge.locale`, `shroudforge.clock` | plugin | `Context.Locale`, `Mod.Text`, `Tick` | C# localisation unit tests; SF client text unchecked |
| `shroudforge.input.is_key_down`, `is_keybind_down`, `was_keybind_pressed`, `keybind_label` | plugin (`is_key_down` raises since SF 1.7.6); held/pressed state from Kiln's host key feed (no `shroudforge.exe` installed), else from an installed `shroudforge.exe` (section 5.6) | `Kiln.Ui.KeyBindings` | feed: real runtime with fed records under Wine, not a game client; before dev.24 bundled-only: always false (player log r1110); installed: client |
| `shroudforge.log.trace/debug/info/warn/error`, `version`, `mod_id`, `mod_kind` | plugin (also in kiln.log as `[sf]`) | `Mod.Log` | lab (probe's `log.info` lines) |

### 8.3 `runtime.*` (in-game API, `runtime_api.lua`)

| Area / functions | Under Kiln | Kiln C# equivalent | Status |
|---|---|---|---|
| `runtime.has/require/status`, `runtime.get_operations`, `runtime.is_main_menu` | plugin | `Runtime`, `Kiln.Api.Lifecycle` | lab (survival checks `runtime.ecs.read/write`) |
| `runtime.world.session_id/context_active/context_kind/operation_available` | plugin | `World`, `KilnSessionInfo` | lab (probe gates on `session_id`) |
| `runtime.player.name` | plugin (client) | `Kiln.Players` | client |
| `runtime.ecs.query/query_bounds/resolve/read/read_bytes/read_fields/write/write_fields/get_template/get_template_component` | plugin | `Reflected.Component/All`, `ReflectedValue` checked setter | lab: `query("keen::ecs::PlayerInput")` (0 players → `no-target`); C# reflection host tests |
| `runtime.ecs.get_registry/get_component(s)/get_catalog` | plugin | `GameTypes.All/Components/Find`, `GameType.Fields` | C# reflection host tests |
| `runtime.ecs.get_attribute(s)/read_attribute(s)/evaluate_attributes/update_attribute/write_attribute_storage` | plugin | `Attributes.Get/Set/Add` (`Kiln.Combat`) | C# host tests; SF survival needs a client for stamina, mana, health, oxygen and heat |
| `runtime.services.get` + `RuntimeService:read_fields/write_field` (named plain services, e.g. `ecs::GameSettings`) | plugin | `Kiln.Settings` (game settings), `Kiln.Rules` | **lab**: probe `write-confirmed: enableDurability=false \| writes=1`, restore on off |
| `runtime.overrides.new/attribute/component/resource/template/service`, claims, `RuntimeOverrides:begin_update/apply/restore_disabled/restore_unused/restore_all/summary/...` | plugin | `Overrides.Claim/Release/RestoreAll`, `ReflectedResource.Set/Restore` | SF lab (apply + restore); C# unit tests; helper is not an automatic wrapper for every SF operation |
| `runtime.values.decode/encode/normalize_field/new` | plugin | `GameValueCodec`, `GameType.Defaults`, checked reflected values | C# host tests |
| `runtime.functions.list/list_native/find/get/get_native/bind/get_operations/bind_modifier`, `runtime.impact.call` | plugin (profile-admitted operations only) | `Kiln.Hooks`, `Kiln.Abilities` (Impact) | — |
| `runtime.world.cursor.get`, `runtime.world.building.submit/status/cancel` | plugin (client) | `Kiln.Build` | client |
| `runtime.world.voxel.get_grid_spec/read/write` | plugin; `write` through Kiln's host (see "Voxel writes through kiln.build" below) | `Build.Grid/Read/Write` (`Kiln.Build`) | lab dev31-sf-voxel-write (Altar Cleanup's shape); client unchecked |
| `runtime.world.entity.query_props(_in_bounds)/get_transform/set_scale/spawn/place/destroy/finish_building/register_prop_recipes` | plugin | `Kiln.Spawn`, `Kiln.Build` | — (world-editor mod: client) |
| `runtime.network.status/send/send_mod/accept/connected_peers/receive/receive_mod` | plugin (SF's Steam Networking Messages) | `Kiln.Network` (`ModCalls`) | lab: "Network service is ready ... role=server" |
| `runtime.patch.available/set_enabled` | plugin (profile-defined patches; none on KFC4) | `Kiln.Hooks` | n/a on current builds |
| `game.assets.get_loaded_resource(_by_field)/get_loaded_resources_by_type/prepare_loaded_index/resolve_loaded_resource/read_loaded_field/write_loaded_field` | plugin | `ReflectedResources.Get/All/FindByField`, `ReflectedResource` checked setter, `Assets.Reflect` | C# reflected-resource unit tests |

### 8.4 EML v1 API (SF's own implementation, `lua/eml/v1/definitions`)

| Module | Functions | Under Kiln | Kiln C# equivalent | Status |
|---|---|---|---|---|
| `game.assets` (AssetManager) | `get_resource(s_by_type)`, `get_resource_parts`, `get_resource_types`, `create_resource`, `get_content`, `get_all_contents`, `create_content`, `get_all_resources`, Resource/Content fields | plugin (startup and runtime) | `Kiln.Assets`, `tools/kfc` | plugin; — |
| `game.types` (TypeRegistry) | `resolve`, `get`, `get_by_index`, `find`, `find_by_hash`, `get_by_qualified_name`, `get_by_impact_name`, `get_all`, `of`, `Type:is_a` | plugin | `GameTypes.Find/ByHash/All/TypeOf`; no Impact-name lookup | plugin; C# equivalent partial |
| `game.guid` (GuidHelper) | `from_content_hash`, `to_content_hash`, `hash` | plugin | `ContentHash`, `GuidHash.Of` (`Kiln.Eml`) | plugin; C# unit tests |
| `buffer` | `create`, `wrap`, Buffer read/write of all scalar types, strings, resources | plugin | `System.Buffers` / `BinaryPrimitives` (C# has it) | plugin |
| `image` | `create`, `decode`, `decode_texture`, `encode`, `encode_texture`, pixel get/set | plugin | `DataImage` (PNG and RGBA8 pixels), `GameTexture.Decode/DecodeMip/Encode` (game texture formats, BC1 to BC7, the same answers as `decode_texture`/`encode_texture`; since 0.2.0-dev.30.3, [modding/textures.md](modding/textures.md)), `Assets.OverrideTexture` (PNG to existing texture format) | plugin; C# tests on the texture fixtures' goldens |
| `io` | `read`, `read_to_string`, `list_files`, `exists`, `is_file`, `is_directory`, path helpers, `export*` (exports folder) | plugin (SF's sandbox) | `System.IO` + `Context.DataDirectory` | plugin |
| `hasher` | `fnv`, `crc` | plugin | `DataHash.Fnv1a32/Crc32/Crc64` (`Kiln.Eml`) | plugin; C# unit tests |
| `integer` | 512 helpers (u64/i64 math) | plugin | C# has native 64-bit integers | n/a |
| `loader` | `has_mod` | plugin | `Mods.IsLoaded` | plugin |
| globals | `typeof`, `require` (package-local) | plugin | — | plugin |

**Kiln C# coverage:** component reflection, attributes, reversible claims, reflected resource lookup and field writes,
voxel access, hashing, PNG images and raw RGBA8 pixels have APIs. Blob enumeration covers managed additions and
overrides only (`Assets.ListBlobs/ListAllBlobs`), not every game blob. General named-service reflection, Impact-name
type lookup are outside those helpers; PNG/RGBA8 and game texture formats (`GameTexture`, since 0.2.0-dev.30.3) are implemented.
`Kiln.Settings` covers world settings. Entity scale is not
provided by these APIs. The ports use Kiln's own type data and MIT helpers. The hosted plugin's lab evidence is separate
from C# unit tests, and neither proves a client drew a result or accepted input.

### Engine hooks through `kiln.hooks`

SF's native runtime (`kfc-runtime.dll`) can hook up to six places in the game's code. Alone it scans the live code for
each place and writes a jump only when the live bytes are still the profile's originals. A DLL that hooked a place first
(XHL, a closed mod) made it refuse, and with it the whole native runtime. Since dev.21 the bundled runtime has an optional
export, `KfcRuntimeSetHost(table)` (`modules/game-runtime/native/abi/runtime_host.h` in the fork; the ABI stays 12). The
adapter (`src/host_hooks.cpp`, `src/kfc_host.h`) calls it before `KfcRuntimeInitialize` and offers:

| Function | What Kiln does |
|---|---|
| `find_signature(signature, expected, n, &address)` | `kiln.hooks` `scan`: sigdb's scanner over the **exe file's** code, so another module's jump hides nothing. The profile's original bytes must match the file's bytes there. |
| `install_hook(name, target, kind, callback, extra)` | a `kiln.hooks` v2 **mid link** at the place, owner `early:kiln-shroudforge.dll`, label `shroudforge/<name>`. The callback gets the registers of its kind: plain `(r15, rbp)`, context `(rcx, 0)`, cursor `(r14 + extra, r13, rbp)`. Kiln relocates the displaced instructions and chains over a foreign jump. |
| `remove_hook(handle)` | revoke (`KfcRuntimeShutdown`). |

Without the export (the player's installed ShroudForge, an older bundle), without `kiln.hooks` v2 (safe idle), or with
`KILN_SF_ENGINE_HOOKS=self`, the runtime hooks the game itself as before. A self-hosted ShroudForge (its own `winmm.dll`
bootstrap) never calls the export, so its behaviour is unchanged. `kiln/state/shroudforge.json` has `engineHooks`:
`kiln.hooks` or `self`, and kiln.log says which and why.

The six places are `game_thread`, `entity_manager`, `world_prop_update`, `world_actor_placement`,
`world_building_dispatch` and `world_cursor`. They are named by a **legacy** game profile (`ecsLayout` + `hooks`); on
current builds no shipped profile has them (see section 1), so today the runtime installs none and the host interface
costs nothing. It matters on a build with such a profile, and for any profile that names hooks later. The core change
behind it: `kiln.hooks` is served to early plugins ([services/hooks.md](services/hooks.md#early-plugins)), because the
runtime installs its hooks while it initialises, in the early phase.

Tests: `native/plugins/shroudforge/tests/run.py` (fake runtime and fake `kiln.hooks`: offer, refusal, old runtime,
switch), `native/plugins/shroudforge/tests/test-host-chain.sh` (the adapter against the real registry with a foreign
`jmp` on one place) and, in the fork, `kfc-runtime-test-engine-hooks` (the installer with and without a host).

### Voxel writes through `kiln.build` (dev.31.18)

ShroudForge's own `runtime.world.voxel.write` (`voxels/write_registered_voxel_grid.cpp`) finds the game's block writer
(`build/VoxelWrite`) by reading the game's code from the registered terraforming impact (`gameplay/TerraformImpact`) down
to it, keeps those bytes as evidence and compares them again before every write. It refuses (outcome 1, "voxel write was
rejected before changing the world") when a function on that path carries an entry jump (a detoured writer is never
selected; the evidence path is read from live code) and, once the exe file cannot be compared with the loaded code
(another module's call-site patches; "executable file refused ... live-only resolution" in the [sf] lines), as soon as any
function it walks is hooked. Under Kiln the path is always hooked: `kiln.rules` (`rules.dig`) and the tools service's
impact binding hook `TerraformImpact`, `kiln.tools.carve` the carve dispatcher that calls the writer, and mods hook more
(XHL Vein Mining: `TerraformImpact`, the dispatcher and the block writer itself). So every write failed (netjunk,
Altar Cleanup, 0.2.0-dev.31.17: "cleaning up 41640 cells in 28 chunks", then "write failed: voxel write was rejected
before changing the world (0 cells changed so far, undo still works)").

Since dev.31.18 the host table has two optional members, `voxel_write_ready` and `voxel_write` (fork `kiln-dev31-voxel-write`;
`kiln/UPSTREAM-PATCHES.md`). With both, `kfc-runtime` passes the write and `runtime.has("runtime.world.voxel.write")` to
the host and never resolves its own writer. The adapter (`src/host_voxels.cpp`, `src/voxel_write_core.h`) posts the write
from ShroudForge's mods thread to the server world's thread and waits for it (a write the world has not begun within 2 s
is cancelled there and refused). A write is for the server world the mod saw when it asked: one that reaches the world
thread only after another server world began (kiln-core's count of server worlds) is refused there. The box must lie in
the voxel grid as ShroudForge's own writer requires (no coordinate below 0, x and z inside the grid's tiles), because
`kiln.build` checks no grid bounds. After an uncertain outcome (3) that world takes no further ShroudForge voxel write
until it is loaded again, as ShroudForge's own writer marks its session's writes uncertain. On the world's thread it
reads the box, returns success for an unchanged box, writes through `kiln.build` with
`KILN_BUILD_F_IN_BASES | KILN_BUILD_F_GAME_SAVE` (no base guard and no Kiln save region, as ShroudForge: the game saves
the cells inside base build zones, and in a chunk another Kiln edit keeps), and checks the read-back. A mismatch is written back once (outcome 2);
a fault or a failed put-back is outcome 3. Every refusal or failure goes to kiln.log as "ShroudForge voxel write refused
before changing the world: <box>: <why>" (or "failed"): the first ten of each reason, then every 100th; an uncertain
outcome always. Differences from
ShroudForge's writer: `kiln.build` commits only the 8x8x8 blocks whose cells change, and it asks the game for tiles that
are not streamed in (the write is then refused and the mod may try again). Calls go through the game's hooks as the
game's own do. ShroudForge's own loader, the player's installed ShroudForge, and an older bundle keep upstream's writer.

In a game that joined a server (a client world and no server world) the writer refuses with "this game joined a server,
and only the server or the player hosting can change the world's blocks; ...", and `runtime.world.voxel.write` is not
available there (ShroudForge's own rule: a joined client's world is read-only). `src/joined_notice.h` tells it once per
joined session (the client world's session count): a kiln.log line naming the mod, from the mod's own following `[sf]`
line or, when none comes within two log-mirror polls, unnamed; and `shroudforge.json` `joinedWorldEdit` `{session, mod,
at}`, from which Kiln.Ui (`ShroudForgeJoinedNotice.cs`) shows one toast. The log mirror also fires it on a mod's line
with ShroudForge's grid answer in a joined game ("original registered voxel grid contract is unavailable", status 5 of
`KfcRuntimeWorldGridGetSpecForSession`: the entity session guard takes only a LocalAuthority world), so a mod that stops
at the grid is told as well; a mod that never logs that answer is told only when it writes, and one that only reads the
grid (or edits through its server half) and logs the answer is told too. "Joined" is a client world and no server world
in kiln-core's session answer, the client world counted only while the session state says a world runs (without the
core's world destroy hook a left joined game's pointer stays behind at the main menu). Kiln mode has no voxel write yet
and no notice. ShroudForge's texts to the mod are unchanged. Tests: `tests/test-joined-notice.sh` (Linux) and
`test-host-chain.sh`'s joined-game cases (Wine).

Tests: `native/plugins/shroudforge/tests/test-voxel-write.sh` (the core over a fake `kiln.build`, Linux),
`test-host-chain.sh` section "the voxel writer" (posting, the wait and the cancel, the grid's bounds, a changed world,
no write after an uncertain outcome; Wine), the fork's
`kfc-runtime-test-engine-hooks` section "voxel writer", and `native/services/build` `build-block-writer`
(`KILN_BUILD_F_GAME_SAVE`).

## dev.20 mixed-server evidence

The fixed fork completed two boots with enabled startup asset mods alongside unmodified EMF Lua 1.19 and
Beastmaster 1.13. The first committed 89 resources and changed the item count from 4,213 to 4,215. The next boot
used the cached result with identical patched KFC/resource hashes. Lua and Beastmaster each loaded once, and the
API 27 network probe passed 11 checks on each boot. These are dedicated-server observations, with no joined player.

The dev.20 clean component was rebuilt from `1ef3896` with matching `version.json`, `SOURCE.txt`, all three submodule
revisions and six licenses. (The dev.24 component, built from `112d939` by its `kiln/build.sh`, has `version.json`
`{"version":"1.7.9","build":"kiln.112d939","host":"kiln"}`, a `SOURCE.txt` naming that commit and the same three
submodule revisions, and the same six licenses; it has run only in the Wine checks of section 5.6, on no lab server.) Recovery hardening passed seven host and five Windows checks. The mixed boots above predate
that final recovery guard; they are separate evidence from the clean rebuild and recovery checks, not a fresh test of
every mod pairing. Final native/GPL source review is complete. Windows DLL portrait archive/cache checks cover read-only
C: and mounted Z: inputs and refusal of symbolic/junction redirects without external mutation. SF's client windows,
custom portraits in game, taming, stable interactions and multiplayer behaviour remain unchecked.

## 9a. Evidence, bundled runtime (lab `wv-sf-2`, UDP 27061, server build 26430253, no ShroudForge installed)

```
[plugin:kiln-shroudforge.dll] ShroudForge runtime: bundled 1.7.9 kiln.823823a from Z:\...\wv-sf-2\kiln\plugins\shroudforge
[plugin:kiln-shroudforge.dll] copied 13 bundled game profile(s) to Z:\...\wv-sf-2\shroudforge/runtime\profiles
[plugin:kiln-shroudforge.dll] ShroudForge startup preparation done (3701 ms)
[plugin:kiln-shroudforge.dll] ShroudForge's native runtime (ABI 12) initialized: profile=enshrouded-server-26430253 ...
[plugin:kiln-shroudforge.dll] ShroudForge runtime ready (1204 ms): its mods loop runs every 16 ms
[sf] [sf-survival] Runtime mod initialized and active
[sf] [kiln-sf-probe] probe write-confirmed: enableDurability=false | writes=1
```
Adapter modes, on the same lab with SF 1.7.8 also installed:
- **Default:** `an installed ShroudForge 1.7.8 is here too; Kiln uses its bundled runtime (switch: launcher Settings "Use
  my installed ShroudForge", kiln.json shroudforge.useInstalled)`, then status `hosted bundled 1.7.9`.
- **`useInstalled: true`:** `ShroudForge runtime: installed 1.7.8 41706360752 from ...\shroudforge`, then status
  `hosted installed 1.7.8`.
- **Installed copy broken** (a garbage `shroudforge-runtime.dll`): `installed shroudforge-runtime.dll could not be loaded
  (error 193)`, `falling back to the bundled ShroudForge runtime`, then status `hosted bundled 1.7.9`.

## 9. Evidence, installed runtime (lab `wv-sf-1`, UDP 27060, server Steam build 26430253, SF 1.7.8 + upstream profile 26430253)

```
[I 14:10:23.498] [kiln] plugin kiln-shroudforge.dll loaded: shroudforge 0.1.0
[I 14:10:24.282] [plugin:kiln-shroudforge.dll] [sf] [I ...] [startup-assets] Early startup asset pass completed in 557 ms
[I 14:10:24.282] [plugin:kiln-shroudforge.dll] ShroudForge startup preparation done (744 ms)
[I 14:10:24.729] [plugin:kiln-shroudforge.dll] ShroudForge's native runtime (ABI 12) initialized: profile=enshrouded-server-26430253 ... execution_profile=missing-or-ambiguous-profile game_thread=unavailable ...
[I 14:10:25.083] [plugin:kiln-shroudforge.dll] ShroudForge runtime ready (352 ms): its mods loop runs every 16 ms
[I 14:10:25.173] [plugin:kiln-shroudforge.dll] [sf] [I ...] [sf-survival] Runtime mod initialized and active
[I 14:10:27.124] [plugin:kiln-shroudforge.dll] [sf] [I ...] [network] Network service is ready. ... role=server
```
SF's state.json: `sf-survival: no-target "No live player entity is available"`. All its settings off (live) →
`[sf-survival] Runtime settings applied live` and `no-change "All Survival settings are off | writes=0"`.
Probe (after restart):
```
[kiln-sf-probe] probe write-confirmed: enableDurability=false | writes=1
[kiln-sf-probe] Runtime settings applied live            (disableDurability set to false in extended.mod.json)
[kiln-sf-probe] probe no-change: disableDurability is off | writes=1   (the restore write)
```
The off switch: `KILN_SHROUDFORGE=0` → `ShroudForge mods are off for this start: KILN_SHROUDFORGE=0`, state `off`, and
the world reaches Run. `kiln/state/shroudforge.json` while hosting: `"mode": "hosted", "runtime": "running", "nativeAbi":
12, "version": "1.7.8", "turns": 1080`. One observation: the probe's service read sometimes reports SF's own retry
(`guarded service snapshot is unavailable or changed during retrieval`). The value stays written and SF retries.

## 10. Build and release (Kiln side)

- The checked-in submodule is pinned at a commit. To initialize an absent checkout: `git submodule update --init --recursive third_party/shroudforge`. Developer staging can
  override the source with `KILN_SHROUDFORGE_SRC`; release builds always use the exported pin.
- `tools/package/stage.sh` runs `<src>/kiln/build.sh $KILN_OUT/shroudforge-build` and stages
  `shroudforge/kiln/plugins/shroudforge/` (component `shroudforge`, default on, both sides, requires core).
  `parity.sh` includes it when built, and `deploy-server.sh` deploys it when built.
- `tools/release/release.sh` reads the gitlink of `third_party/shroudforge` at the release commit, exports the fork
  with its submodules (`lib/sf-source.sh`) to `source/shroudforge-kiln-<commit12>-src.tar.gz`, extracts the same tree
  into the build source, and appends the component's notice and source pointer to `THIRD-PARTY-NOTICES.txt`.
- Build needs: cargo-xwin, clang-cl/lld-link/llvm-rc, cmake, ninja, node/npm. The first CMake configure fetches zydis
  (SF's pinned commit) with FetchContent.

Review regressions: `native/plugins/shroudforge/tests/review_test.py` uses isolated Git/source/build fixtures for
recursive source export (including nested gitlinks and spaces), reproducibility, incomplete notices, stale outputs,
failed EML/SF builds and archive builds without Git. `tests/early_test.cpp` checks PE bounds and runtime versions.
`tests/run.py` drives fake DLLs under Wine to check fallback, ABI rejection, writer reservation, update faults,
serial shutdown and the host key feed (`KILN_SF_ONLY=<prefix>` runs some cases only); it never loads a game binary.
`tests/real_keys.py` runs the built component behind the adapter (section 5.6); it reads copies of the client's
`enshrouded.exe` and archive and never runs them:
`real_keys.py <tests bin> <kiln-shroudforge.dll> <out>/shroudforge-build/shroudforge <game client folder>`. A source-archive release also needs the reviewed fork's `kiln/build.sh`
changes committed and its Kiln gitlink updated by the maintainer; the review itself does neither.

## 11. ShroudForge 1.8.1 next to Kiln (dev.30)

What upstream 1.8.1 adds that touches Kiln, checked when the bundled runtime moved to it:

- **`runtime.patch.glider_pitch`** (native code patch, used by the bundled SF Unlimited Flight 1.3.0, off by default): on
  the first `runtime.patch.available`/`set_enabled` call it finds the glider pitch clamp in actor_rotation's live code
  (`movss xmm0,[rip+X]` loading 5 degrees, then the clamp to 80 degrees), and `set_enabled(true)` writes 8 bytes there
  (`E9` to a near trampoline that loads -1.57 rad, 3 `NOP`s). The site is rva `0x26f6fe` in the client
  (26430625) and `0x817fe` in the server (26430253): 0xF3 bytes before Kiln's glider steering mid hook
  (`rules/GlideSteer`, the roll-rate load, `0x26f7f1` / `0x818f1`), so the two never write the same bytes; no other
  sigdb symbol is within 0x800 bytes. Static check: `glider_site_check.py` (dev30 wave, out/sf-181). **In 0.2.0-dev.30 the
  patch is unavailable under Kiln** (lab `dev30-sf181`, server 26430253, 2026-11-21): Kiln's glider steering mid hook
  went into actor_rotation at boot, and SF's live code reader refuses a procedure with a relative jump out of the game
  image (`target()` in `resolve_original_call_graph.cpp`), so `ResolveNamedSystemCode("actor_rotation")` failed
  and `runtime.patch.available` answered false (nothing written). **Since 0.2.0-dev.30.1** the mid hook goes in only
  when a rule set first asks for `GlideSteer`, on the tick after the first world (`steer.cpp` `tick()`); until then
  actor_rotation stays unhooked and glider_pitch resolves (lab: `runtime.patch.glider_pitch resolved: the pitch clamp at
  rva 0x817fe`, enable, disable and re-enable succeed with `kiln-rules.dll` loaded). Once a player steers with Kiln
  (`GlideSteer`, also Kiln's Creative Mode glider), SF refuses its next enable cleanly
  (`actor-rotation-code-changed`); a patch already enabled stays, the bytes are disjoint, and Kiln's step replaces the
  pitch change after SF's clamp for that player. The fork logs why the patch is unavailable
  (`runtime.patch.glider_pitch unavailable: ...`, branch `kiln-1.8.1-glider`).
- **The fork's whole-file check under Kiln** (independent of glider_pitch): up to 0.2.0-dev.30 it refused the exe for
  Kiln's entry hook on `content/LootItems` (a call-free leaf without unwind data, taken for code of the function
  before it, rva 0x93d310), and the first read of a function holding one of Kiln's mid hooks (items/CreatePide) would
  have refused it as well; ShroudForge then read every procedure live, where an entry-detoured one is unreadable (the
  detoured call-graph walk needs the file). Since 0.2.0-dev.30.1 (fork `5bb133f0`) the check admits a leaf
  function's entry detour (`admit_leaf`: 16-byte aligned, a boundary of the gap's linear decode after padding,
  target outside the image) and refuses a mid-hooked function's file read without switching file reads off (lab:
  `executable file accepted ... beside 54 entry detour(s), 17 interior jump(s)`, no refusal after it).
- **Unified Tweaks 2.1.2 and its Glider Flight 1.0.1** (Nexus 185; lab boot above, their DLLs off): the packages load
  under the bundled 1.8.1 (the startup pass with `unifiedemltweaks`, `unified-tweaks-live` and `glider-flight`
  active, no ShroudForge error). They use no 1.8 DLL bridge, `runtime.patch` or player permissions. Glider Flight's own
  DLL patches the same 5-degree clamp itself (and three diving sites, none within 0x40 bytes of a Kiln sigdb site), so
  it and SF Unlimited Flight exclude each other.
- **Steam P2P channels 65534 (Modloader server mirroring, server log mirror) and 65535 (health)**: ISteamNetworkingMessages,
  not the game's socket. Kiln's own frames use the game's channel 12 (`kiln.net`), so they do not meet. The server log
  and archive mirror answer only an admin session (a password checked against `userGroups` in
  `enshrouded_server.json`).
- **Loader and mod version check between client and server**: the server compares each SF client's report (loader version
  and mod versions) with its own and logs and shows the differences; it does not kick. A Kiln-hosted runtime reads its
  version from SF's own `version.json` path, which a bundled component does not have, so both sides of a Kiln host and a
  Kiln client report the same fallback version; a client with an installed SF of another version shows a
  `loader-version` line in SF's log.
- **Vanilla startup mode** (`vanillaClient`/`vanillaServer` in SF's Modloader settings): read only by SF's own
  `winmm.dll` bootstrap (`shroudforge_prepare_vanilla`); Kiln's adapter never calls it, so the switch has no effect
  when Kiln hosts SF.

## 12. Kiln's interpreter (kiln mode): the release hold and its guided checks

A ShroudForge mod runs one of three ways: hosted (Kiln starts the bundled runtime, the default), installed (Kiln starts
the player's own ShroudForge copy) or kiln mode (Kiln's own interpreter, `kiln.json` `"shroudforge.backend": "kiln"`;
players' page [kiln mode](shroudforge-kiln-mode.md)). The backend is frozen once per start (`kiln/state/launch.json`);
a package that cannot run whole in kiln mode is skipped with its reason and the loud warning.
ShroudForge's own loader in the game folder sets both Kiln backends aside.

Kiln mode ships held: the client gate's row `kiln-sfi.interpreter` (`native/core/gates/sfi.json`)
keeps it off until its seven guided checks have passed in a real game; `"preview"` in `kiln.json` lifts it for the
pass. `kiln-sfi.sf18` (anything that needs ShroudForge 1.8) stays held in every build by the release policy's veto,
whatever `"preview"` says. kiln-manage reads the same list (`crates/kiln-manage/src/sfi_release.rs`) for the
launcher's ShroudForge card and the launch plan's warnings; neither reads the verdicts: the row goes when a commit that
records the pass removes it.

The checks are `mods/tests/client-pass/Sfi.cs` (area "Kiln's interpreter", how to play them:
[client pass](client-pass.md#kilns-interpreter)). Each asserts what the interpreter wrote for this start
(`kiln/state/sfi.json`: this game process, backend `kiln`, phase `running`; `kiln-sfi: Runtime initialized` in
`kiln.log`) before it asks its question, and ends with skip in any other start. Verdicts go to
`kiln/state/client-checks.json` like every guided check's; `tools/client-checks/report.py --rows` shows them under
"Kiln's interpreter (kiln-sfi.interpreter)".

| Check | Confirms |
|---|---|
| `sfi-ui-page` | ShroudForge's mod-menu key (F9) opens Kiln's ShroudForge page (`sfi.json` `menu.by`: `kiln`), listing this start's mods with the interpreter's state; it closes again; no Modloader window |
| `sfi-ui-settings` | a setting changed on the page is kept, applies at the next start, and the mod uses it after a restart |
| `sfi-ui-actions-notices` | an action button's "Requested" and one reaction; a mod's notification readable; nothing written to `<game>/shroudforge` |
| `sfi-save-sensitive` | a save-sensitive package (Quiver & Spellbook 0.8.2) off after a start with it on: its `save-sensitive-off` warning, named before Play |
| `sfi-three-surface-warning` | the same warning before Play, in the game (banner, footer, DO NOT SAVE badge, pause-menu marker) and in `sfi.json` |
| `sfi-warning-accessibility` | the dialog before Play with the keyboard alone and at large text size; the in-game banner readable |
| `sfi-backend-roundtrip` | kiln, hosted, kiln on one game: hosted runs every mod again, settings kept, the save-sensitive package carried through the hosted start, no mod in two folders of `<game>/mods` |
| `sfi-keybinds` | with the keys probe (`mods/tests/sf-keys-probe`): a mod's shortcut read from the game window's keys (`kiln-sfi: keyboard shortcuts of ShroudForge-format mods read the game window's keys`), its label, one press per press, held and released, nothing while Kiln's menu has the keyboard |

What these checks do not cover: native Windows (the pass is played under Proton), a joined or dedicated server in kiln
mode and the ECS/world calls that answer unavailable in this build.
