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
used SF 1.7.8 and the current server profile. The dev.20 bundled fork is pinned at
`1ef3896068b9106a96743e55850b599d8cd08aad`, including the archive-reader/publication and recovery-path fixes. Its clean component
records that commit and all three submodule revisions in `SOURCE.txt`, identifies itself as `1.7.9 kiln.1ef3896`,
and includes the component's notices and licenses. The dev.20 mixed-server evidence is separate from the older
observations below; neither is a client rendering or player-interaction test.

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
| Tick | SF: its own 16 ms update loop. Kiln: the server-world tick. | Kept separate. Hosted SF uses a serial native/Lua worker and a separate filesystem-only heartbeat writer, so slow heartbeat writes no longer block its updates. The heartbeat records the last update turn, not writer activity. Other runtime/status work can still delay the loop; this is not a guarantee of gameplay or input parity. |
| UI / input | SF's windows require a separate installed `shroudforge.exe`, absent from Kiln's bundled runtime. Its keybinds use its hotkeys module. | Client only. Kiln's existing Mods screen edits SF switches/settings without a helper. Installed helpers still start and respect module switches. Only when no hosted Modloader helper runs does Kiln own the auto-reset public toggle event and F9/configured keyboard fallback. Self-hosted SF is untouched. |
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

### 5.1 `native/plugins/shroudforge` → `kiln/plugins/kiln-shroudforge.dll` (MIT)

The runtime choice comes first. **Bundled**: `kiln/plugins/shroudforge/`, the default. **Installed**: `<game>/shroudforge/`,
used when `kiln.json` has `"shroudforge": {"useInstalled": true}` (the launcher Settings toggle "Use my installed
ShroudForge"; env `KILN_SHROUDFORGE_RUNTIME=installed|bundled` overrides). It logs which one runs
(`ShroudForge runtime: bundled 1.7.9 kiln.1ef3896 from ...` for the current pin). When both are present it says how to switch. A chosen
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
   Foreign export faults and C++ worker exceptions stop SF and report failure. The unload callback and process detach only signal,
   with no loader-lock wait; worker handles and runtime modules remain alive until process teardown. Callback code stays mapped for the worker lifetime.
6. Mirror SF's log and publish heartbeat/status. The adapter skips SF's bootstrap update check/pending-update install
   and Debug Console. SF's own exported preparation can still start its config controller.

Startup preparation and SF's loop can run in Kiln safe idle too: the adapter uses SF's own exact-build profiles and
needs no Kiln hook. A failed preparation is reported; the adapter does not attempt another writer after startup side effects.

### 5.2 `crates/kiln-manage/src/shroudforge.rs`

`detect`, `mods_dir` (SF's `paths.mods`), `is_sf_mod` (tells SF/EML Lua packages from EMF mods),
`read_mod`/`list_mods`, `skip_reason` (plain-words skip messages), `set_enabled`, `set_setting` (checks type, range and
options; it splices only the value with `jsonc::set_value`, so the file stays byte-identical otherwise),
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
or empty configured bindings do not silently become F9. Helper exit hands ownership to Kiln; stopped runtime removes
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

The sustained W/S steering report for SF Unlimited Flight is **not confirmed fixed**. The heartbeat change fixes
reproduced update-loop stalls; native data-flow analysis and synthetic input/override tests do not establish the
mode, input, selected resource or rate seen by an affected client's actual rotation consumer. A `write-confirmed`
effect report alone is not that evidence. Exact ECS/resource profile support also does not establish availability
of SF's separate legacy execution-hook contracts.

For a controlled client observation, keep the game/SF versions, glider, world and settings the same, with **Upward
Gliding enabled in pitch-speed controls**:

1. Close all Kiln menus, screens, interactive windows and wheels; release W/S.
2. Check ordinary on-foot W/S movement.
3. During a normal glide, hold S for about two seconds, release briefly, then hold W for two seconds. Note which
   response fails. Do not open a capturing menu during this check.
4. Afterward, preserve a fresh support report, the SF Unlimited Flight effect detail, exact versions and settings.

Kiln's UI can capture input while an interactive surface or consuming key subscriber is active, but there is no
evidence that this caused the reported failure. Working on-foot movement makes unconditional capture less likely,
not impossible. Definitive localization still needs affected-session, same-frame mode/input and selected ItemInfo
pointer/rate at the native consumer; a Lua replay or aggregate memory-write observation cannot substitute for it.

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
| keybind settings (`control: keybind`) | plugin (SF's hotkeys) | `Kiln.Ui.KeyBindings` | plugin; client |
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
| `shroudforge.input.is_key_down`, `is_keybind_down`, `was_keybind_pressed`, `keybind_label` | plugin | `Kiln.Ui.KeyBindings` | client |
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

The current clean component was rebuilt from `1ef3896` with matching `version.json`, `SOURCE.txt`, all three submodule
revisions and six licenses. Recovery hardening passed seven host and five Windows checks. The mixed boots above predate
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
`tests/run.py` drives fake DLLs under Wine to check fallback, ABI rejection, writer reservation, update faults and
serial shutdown; it never loads a game binary. A source-archive release also needs the reviewed fork's `kiln/build.sh`
changes committed and its Kiln gitlink updated by the maintainer; the review itself does neither.
