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
`112d93986392acc9f69ee3e90d1036b4bed2a950` (dev.24, branch `kiln-dev24-compat`): the host key feed for mod shortcuts
(section 5.6) and outdated working backups set aside without SF's Updater (section 5.0), on top of dev.21's engine-hook
host interface (`724a884`) and dev.20's archive-reader/publication and recovery-path fixes (`1ef3896`). Its component
records that commit and all three submodule revisions in `SOURCE.txt`, identifies itself as `1.7.9 kiln.112d939`
(`version.json` build `kiln.112d939`), and includes the component's notices and licenses. The dev.20 mixed-server
evidence is separate from the older observations below; neither is a client rendering or player-interaction test.

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
| Shared game state | SF writes ECS components, resources and named services (e.g. `ecs::GameSettings`) from its own thread, with claims and readback. Kiln services write some of the same (e.g. `kiln.settings`). | Last writer wins, as with any two mods. SF restores its baselines when a mod switches off. Documented for players. The lab shows SF's guarded write confirmed next to Kiln's services. |
| Tick | SF: its own 16 ms update loop. Kiln: the server-world tick. | Kept separate. Hosted SF uses a serial native/Lua worker and a separate filesystem-only writer thread for the heartbeat, `shroudforge.json` and the `[sf]` log mirror, so slow file I/O no longer blocks its updates. The heartbeat records the last update turn, not writer activity. Runtime work in the loop (the native status read every 2 s) can still delay it; this is not a guarantee of gameplay or input parity. |
| UI / input | SF's windows require a separate installed `shroudforge.exe`, absent from Kiln's bundled runtime. Mod keybinds (`shroudforge.input.is_keybind_down`/`was_keybind_pressed`) get their key state from the same exe, started by the runtime as a hidden `--keyboard-input` child that reports keys over a pipe. | Client only. The bundled runtime (fork `112d939`) has an optional host key feed: Kiln passes it the game window's own key, button and focus records (kiln.ui v5), and the runtime starts no helper (section 5.6). Without the feed (an installed upstream SF, an older bundle, kiln.ui off, safe idle, `KILN_SF_KEY_FEED=off`, or SF's `shroudforge.exe` installed: the helper then keeps the key state) the runtime starts the installed `shroudforge.exe --keyboard-input` itself, and with no SF installed mod keybinds never fire (`Keyboard shortcuts unavailable`). `kiln/state/shroudforge.json` `keys` says which (`kiln`, `waiting` until Kiln's plugin step, `helper`, `none`) and why. Kiln's existing Mods screen edits SF switches/settings without a helper. Installed helpers still start and respect module switches. Only when no hosted Modloader helper runs does Kiln own the auto-reset public toggle event and F9/configured keyboard fallback. Self-hosted SF is untouched. |
| Native DLL mods | Both read `<game>/dinput8-chain.ini`. | The same file loads once (same canonical path, `LoadLibrary` reference count). SF reports an issue for a DirectInput provider without its `dinput8.dll` host. |
| Updates | SF's updater replaces its managed paths, **`dinput8.dll` included**, and its repair check requires them to exist. | In hosted mode kiln-shroudforge runs neither the startup update check nor the pending install, so SF never overwrites Kiln. In self-hosted mode an SF update can replace Kiln's `dinput8.dll`. The launcher must then put Kiln's proxy back (detect: classify at launch). The repair check only tests that the file exists, so Kiln's `dinput8.dll` satisfies it. |
| Wine / Proton | SF needs `dbghelp=n,b;winmm=n,b`. | Not in hosted mode. kiln-shroudforge loads `shroudforge-runtime.dll` (not a system DLL name) by full path. The lab ran with Kiln's default `mshtml=;dinput8=n,b` only. This fixes the Steam Deck case. |
| `winmm.dll` owners | Shroudtopia and Shroudweave also ship a `winmm.dll` (universal-loader.md). | Self-hosted detection checks the `ShroudforgeStop` export, not the file name. The installer must classify `winmm.dll` before treating it as SF's (detect). |
| EML packages | SF runs EML v1 Lua packages (`launcher: "EML"`, or no `extended.mod.json`) in its mods folder. | When SF is hosted, the packages in SF's mods folder are SF's. kiln-eml must skip that folder (`kiln/state/shroudforge.json` `modsDir`), see section 7. |

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

### 5.1 `native/plugins/shroudforge` → `kiln/plugins/kiln-shroudforge.dll` (MIT)

The runtime choice comes first. **Bundled**: `kiln/plugins/shroudforge/`, the default. **Installed**: `<game>/shroudforge/`,
used when `kiln.json` has `"shroudforge": {"useInstalled": true}` (the launcher Settings toggle "Use my installed
ShroudForge"; env `KILN_SHROUDFORGE_RUNTIME=installed|bundled` overrides). It logs which one runs
(`ShroudForge runtime: bundled 1.7.9 kiln.112d939 from ...` for the current pin). When both are present it says how to switch. A chosen
runtime that is missing, unsafe, fails to load, has an unsupported version or lacks compatible exports falls back to
the other one with a warning. Supported unversioned bootstrap contracts are SF 1.7.8 and 1.7.9; the native DLL must
provide all five required exports and ABI 12. Missing `version.json` is incompatible. The runtime directory and its
children cannot be reparse points; ancestors above that authorized root are the player's Steam/Wine layout and are
not judged. Resolved paths must stay inside the root; Wine file links are excluded by the installer. DLL loads use
restricted dependency search paths and verify the loaded file identity. In bundled mode
it copies the bundled game profiles that are missing into SF's runtime folder and never overwrites a file. The data
folder is SF's own `<game>/shroudforge/` (config, logs, state, cache, profiles). Mods come from SF's mods folder.
`kiln/state/shroudforge.json` gains `source` (bundled|installed), `runtimeDir`, `bundled`, `installed` and
`useInstalled`. Then, for whichever runtime was chosen:

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

The native adapter reports `menu: helper|kiln|off|server|unavailable` and `menuKey` (`SDL:66`, F9, by default).
Only without a running hosted helper does it hold `Local\\ShroudForge.ModMenu.Open.<game-pid>`; the managed worker
waits on that auto-reset event. This is SF's real `shroudforge.ui.toggle_modloader()` contract. It never competes
with the installed helper for events. The fallback key is remappable in Kiln's Keys tab; unsupported/mouse-only/Meta
or empty configured bindings do not silently become F9, nor does a `toggleKey` that is not text (null, a number:
ShroudForge refuses those too), which reports `menuKey` "" and a warning in kiln.log. Helper exit hands ownership to Kiln; stopped runtime removes
the fallback binding. SF module-off and self-hosted behavior stay intact.

Windowless native Wine tests exercise absent/installed/exited/disabled/self-hosted helpers and repeated auto-reset
signals. Managed tests exercise settings, writer coalescing/failure recovery, state freshness and shortcut ownership.
A headless software preview renders the shared-fixture SF panel at 1280×800 and its main-menu page at 1920×1080.
In an isolated server lab, the actual managed UI writer queue changed `sf-probe` live: setting on confirmed a guarded
write, setting off restored the baseline, and the enabled switch deactivated/reactivated its runtime without errors.
Neither proof is rendered game-client or Steam Deck interaction. Text-based keybind/color editing, combos in place
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
   folder. When `kiln/state/shroudforge.json` says `mode` = `hosted` or `self-hosted`, kiln-eml must skip packages in
   `modsDir`, or the same mod runs twice. Both run during startup (Kiln `on_ready`), so kiln-eml can check whether SF is
   installed (`shroudforge/shroudforge-runtime.dll`) and not switched off, or read the profile's `mods.shroudforge` list.
   Proposed rule: SF installed and hosted → SF owns `<SF modsDir>`; kiln-eml owns everything else.
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
asset mods and the EML packages in its mods folder in one pass (the launcher copies the profile's EML mods there, so
the game has both). kiln-eml writes only in a start where SF does not run (its mods off in the active profile,
`KILN_SHROUDFORGE=0`, or SF could not start). Then, if SF's state says it prepared asset mods in an earlier start, that
start's game data has the EML mods only: kiln-eml sets SF's state to `failed` and names the SF mods that are missing
(a warning in kiln.log and `reason` plus `sfDisplaced` in `kiln/state/eml.json`). The next start in which SF runs
prepares them again. EML mods that are only in the Library, not in SF's mods folder, do not load while SF is hosted
(`not-loaded` in `eml.json`).

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
| notifications feed (`shroudforge.notifications.publish`) | plugin (SF's local feed file) | Kiln.Ui notices | plugin; — |
| effect reports (`runtime.report_effect`) | plugin; kiln-manage `effects` | mod status in `state/mods.json` | done; lab |

### 8.2 `shroudforge.*` (mod API, `lua/shroudforge/v1/definitions/mod_api.lua`)

| Function | Under Kiln | Kiln C# equivalent | Status |
|---|---|---|---|
| `shroudforge.settings.get(key, fallback)` | plugin | `Mod.Config<T>()` | lab |
| `shroudforge.ui.on_action`, `shroudforge.ui.toggle_modloader` | public action request files; toggle event opens installed helper, or Kiln's SF page when hosted without it | `Kiln.Ui` screens | action-file managed tests, event native Wine tests; actual action execution/rendering client check |
| `shroudforge.notifications.publish` | plugin | Kiln.Ui notices | — |
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
| `runtime.world.voxel.get_grid_spec/read/write` | plugin | `Build.Grid/Read/Write` (`Kiln.Build`) | API exists; SF voxel path unchecked here |
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
| `image` | `create`, `decode`, `decode_texture`, `encode`, `encode_texture`, pixel get/set | plugin | `DataImage` (PNG and RGBA8 pixels), `Assets.OverrideTexture` (PNG to existing texture format) | plugin; C# unit tests; no general texture decoder |
| `io` | `read`, `read_to_string`, `list_files`, `exists`, `is_file`, `is_directory`, path helpers, `export*` (exports folder) | plugin (SF's sandbox) | `System.IO` + `Mod.DataDir` | plugin |
| `hasher` | `fnv`, `crc` | plugin | `DataHash.Fnv1a32/Crc32/Crc64` (`Kiln.Eml`) | plugin; C# unit tests |
| `integer` | 512 helpers (u64/i64 math) | plugin | C# has native 64-bit integers | n/a |
| `loader` | `has_mod` | plugin | `Mods.Has` | plugin |
| globals | `typeof`, `require` (package-local) | plugin | — | plugin |

**Kiln C# coverage:** component reflection, attributes, reversible claims, reflected resource lookup and field writes,
voxel access, hashing, PNG images and raw RGBA8 pixels have APIs. Blob enumeration covers managed additions and
overrides only (`Assets.ListBlobs/ListAllBlobs`), not every game blob. General named-service reflection, Impact-name
type lookup and a general texture decoder are outside those helpers; PNG/RGBA8 are implemented.
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
