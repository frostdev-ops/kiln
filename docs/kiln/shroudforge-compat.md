# ShroudForge mods on Kiln (design, licence boundary, API coverage)

Owner: agent `sf` (2026-11-07). Players' guide: [shroudforge.md](shroudforge.md). Wiki note: [wiki-notes/sf.md](wiki-notes/sf.md).

ShroudForge (SF, [bonsaibauer/shroudforge](https://github.com/bonsaibauer/shroudforge), MIT) is the most common
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

Sources read: upstream `bonsaibauer/shroudforge` HEAD `823823a` (2026-11-05, "new version"; releases: **v1.7.9 =
Latest**, v1.8.0-dev.* pre-releases on 6 and 7 Nov), shallow clone in `/mnt/Dongus/kiln-wave/sf/src-ref/shroudforge`.
The local fork `~/Work/kiln-shroudforge` is older (1.7.7). The two have the same bootstrap and runtime interface:
`windows-bootstrap/` differs only by Kiln's CMake file. Lab tests used the SF **1.7.8** release binaries
(`~/Work/ew-lab/dl/sf178`) plus upstream's profile for the current server build (`server/26430253.json`, which ships in
1.7.9). Nothing of SF's DLLs was disassembled. The only binary check was the game exe's import table, because Kiln hooks
that exe.

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
| Proxy name | Both own `dinput8.dll`. SF's `dinput8.dll` is only an alternative bootstrap plus the DirectInput provider host for SF's DLL Manager. SF also starts through `dbghelp.dll` → `winmm.dll`. | Kiln keeps `dinput8.dll`. SF's `dinput8.dll` goes to the backup, unchained. `dbghelp.dll`, `winmm.dll` and `shroudforge/` **stay** (installer change: agent `detect`, see section 6). |
| Entry point | Both patch the exe's entry. | Compatible by design. Whoever patches second saves the first patch as "original" and jumps to it (LIFO). Kiln's proxy installs its gate after its chain. When kiln-shroudforge hosts SF there is only one gate (Kiln's), and SF's preparation runs inside Kiln's `on_ready`. |
| Double start | SF's `dbghelp.dll` starts SF on Windows (and under Wine with `dbghelp=n`). | kiln-shroudforge looks for a module exporting `ShroudforgeStop` in the process and **steps aside** ("self-hosted"). |
| Code hooks | SF: none on KFC4 builds. Kiln: many. | No double hooks. |
| Shared game state | SF writes ECS components, resources and named services (e.g. `ecs::GameSettings`) from its own thread, with claims and readback. Kiln services write some of the same (e.g. `kiln.settings`). | Last writer wins, as with any two mods. SF restores its baselines when a mod switches off. Documented for players. The lab shows SF's guarded write confirmed next to Kiln's services. |
| Tick | SF: its own 16 ms thread. Kiln: the server-world tick. | Kept separate. SF's timing stays exactly as in ShroudForge, so its mods behave the same. |
| UI / input | SF's windows are separate processes (`shroudforge.exe`). Its keybinds use its hotkeys module. | Client only. kiln-shroudforge starts the Modloader and World Editor windows like SF does, and they respect SF's module switches. No in-process overlay conflict. |
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
`sync-upstream.sh`, `UPSTREAM-VERSION` and `UPSTREAM-PATCHES.md`. One upstream file is patched: the app-icon build script
is skipped under `SHROUDFORGE_NO_APP_ICON`, because winres cannot run `rc.exe` on Linux. `kiln/README.md` documents
merges. A trial merge of upstream/1.8.0 into `kiln` had no conflicts, and the runtime exports and ABI 12 are unchanged.
In Wine, SF's own tests pass: `bundled_lua` 3/3 and `survival` 1/1. Kiln's `tools/package/stage.sh` builds it into the
`shroudforge` component (default on, both sides). `tools/deploy-server.sh` deploys an existing build.
`tools/release/release.sh` exports and ships its source (`tools/release/lib/sf-source.sh`) and appends its notice.

### 5.1 `native/plugins/shroudforge` → `kiln/plugins/kiln-shroudforge.dll` (MIT)

The runtime choice comes first. **Bundled**: `kiln/plugins/shroudforge/`, the default. **Installed**: `<game>/shroudforge/`,
used when `kiln.json` has `"shroudforge": {"useInstalled": true}` (the launcher Settings toggle "Use my installed
ShroudForge"; env `KILN_SHROUDFORGE_RUNTIME=installed|bundled` overrides). It logs which one runs
(`ShroudForge runtime: bundled 1.7.9 kiln.823823a from ...`). When both are present it says how to switch. A chosen
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
`x-kiln-enum-labels`, `x-kiln-enabled-when`), `settings_values`, `apply_switches` (profile activation writes SF's own
`enabled` flags), `status`, `effects` (SF's per-mod `runtime.report_effect` states from its `state.json`). Five unit
tests.

### 5.3 `mods/tests/sf-probe` (lab probe, MIT)

An SF mod that uses only SF's public API: `runtime.services.get("ecs::GameSettings")` +
`runtime.overrides.service(...,"enableDurability")` with the setting `disableDurability`. It proves a guarded write, SF's
readback and the restore on a live setting change, all without players.

## 6. What other agents need to do (detect, launcher)

- **Installer** (agent `detect`, `install.rs`): when ShroudForge is installed, install "next to ShroudForge" by default.
  Move only SF's `dinput8.dll` to the backup (`class: shroudforge, action: backed-up`, not chained). Keep `dbghelp.dll`,
  `winmm.dll` and `shroudforge/` in place, so no "switch and lose your mods". Uninstall restores `dinput8.dll`
  byte-identically, which the current receipt logic already does. A player whose SF was "switched" earlier (Steam Deck
  report) needs "Restore ShroudForge": put `winmm.dll` and `dbghelp.dll` back from the receipt's backups. At launch, a
  `dinput8.dll` classified as ShroudForge in a folder with a Kiln receipt means SF's updater replaced Kiln: offer to put
  Kiln back.
- **Library** (detect's kind framework, `library.rs`/`engines.rs`): add kind `shroudforge` through
  `shroudforge::is_sf_mod` and `read_mod`. The skip text comes from `shroudforge::skip_reason`. Import copies the package
  into SF's mods folder (`shroudforge::mods_dir`), because SF reads its packages only from there.
- **Profiles** (`schema/profile.schema.json` `mods`): add `"shroudforge": [{id, path, enabled}]`. The core already
  ignores unknown kinds with a warning (`profile.cpp:107`). Activation calls `shroudforge::apply_switches`.
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
column names what a Kiln mod uses today for the same effect. **Port** marks functionality to add to Kiln's C# API, as new
files or a new service (`Kiln.ShroudForge`-style helpers), not done yet. Tested: **lab** = run in `wv-sf-1` (server build
26430253) through kiln-shroudforge with SF 1.7.8; **unit** = kiln-manage tests; **client** = on the human checklist;
**—** = not run in this slice (it works because it is SF's own code, but nobody has watched it yet).

### 8.1 Package and launcher features

| SF feature | Under Kiln | Kiln C# / launcher equivalent | Status / tested |
|---|---|---|---|
| `mod.json` (id, name, version, authors, deps, capabilities) | plugin (SF reads it); kiln-manage `read_mod` | `kiln.mod.json` | done; unit |
| `extended.mod.json` `enabled` | plugin; `set_enabled`, `apply_switches` | profile entry `enabled` | done; unit, lab (enabled survival + probe) |
| `targets` client/server | plugin (SF skips a mod outside its targets) | `side` in kiln.mod.json | done; lab (server) |
| settings `value` + label/description/control/min/max/step/options/enabledWhen/applyAt | plugin; `settings_schema` → SchemaForm; `set_setting` | `[Config]` classes + SchemaForm | done (backend); unit; launcher UI pending |
| live settings (`shroudforge.settings.get` in callbacks) | plugin | `Config` reload events | done; **lab**: survival all off → "Runtime settings applied live", effect `no-change: All Survival settings are off`; probe off → restore |
| groups | plugin; `x-kiln-group` | config sections | done (backend) |
| keybind settings (`control: keybind`) | plugin (SF's hotkeys) | `Kiln.Ui.KeyBindings` | plugin; client |
| locales `locales/*.json`, `shroudforge.i18n.get`, `shroudforge.locale()` | plugin | Kiln mod localisation | plugin; — |
| dependencies (SemVer, optional) | plugin (SF's load order) | resolver | plugin; — |
| `patch` capability (startup asset changes, game-file backup) | plugin (`shroudforge_prepare_startup` in `on_ready`) | `kiln.assets` | plugin; lab: "Early startup asset pass completed" (no patch mod enabled) |
| DLL Manager (native DLL mods, `runtime-register-dll`) | plugin | Kiln native mods (`native.json`) | plugin; — (DirectInput providers need SF's dinput8: not supported) |
| Modloader window, World Editor window | plugin starts `shroudforge.exe` like SF does | Kiln launcher, Kiln build tools | plugin; client |
| Debug Console window | not started (kiln.log has SF's log as `[sf]`) | Kiln console | by design |
| SF self-update / repair | not run when hosted | Kiln launcher (pending: SF update source) | by design |
| notifications feed (`shroudforge.notifications.publish`) | plugin (SF's local feed file) | Kiln.Ui notices | plugin; — |
| effect reports (`runtime.report_effect`) | plugin; kiln-manage `effects` | mod status in `state/mods.json` | done; lab |

### 8.2 `shroudforge.*` (mod API, `lua/shroudforge/v1/definitions/mod_api.lua`)

| Function | Under Kiln | Kiln C# equivalent | Status |
|---|---|---|---|
| `shroudforge.settings.get(key, fallback)` | plugin | `Mod.Config<T>()` | lab |
| `shroudforge.ui.on_action`, `shroudforge.ui.toggle_modloader` | plugin (SF's Modloader window) | `Kiln.Ui` screens | client |
| `shroudforge.notifications.publish` | plugin | Kiln.Ui notices | — |
| `shroudforge.i18n.get`, `shroudforge.locale`, `shroudforge.clock` | plugin | localisation, `Tick` | — |
| `shroudforge.input.is_key_down`, `is_keybind_down`, `was_keybind_pressed`, `keybind_label` | plugin | `Kiln.Ui.KeyBindings` | client |
| `shroudforge.log.trace/debug/info/warn/error`, `version`, `mod_id`, `mod_kind` | plugin (also in kiln.log as `[sf]`) | `Mod.Log` | lab (probe's `log.info` lines) |

### 8.3 `runtime.*` (in-game API, `runtime_api.lua`)

| Area / functions | Under Kiln | Kiln C# equivalent | Status |
|---|---|---|---|
| `runtime.has/require/status`, `runtime.get_operations`, `runtime.is_main_menu` | plugin | `Runtime`, `Kiln.Api.Lifecycle` | lab (survival checks `runtime.ecs.read/write`) |
| `runtime.world.session_id/context_active/context_kind/operation_available` | plugin | `World`, `KilnSessionInfo` | lab (probe gates on `session_id`) |
| `runtime.player.name` | plugin (client) | `Kiln.Players` | client |
| `runtime.ecs.query/query_bounds/resolve/read/read_bytes/read_fields/write/write_fields/get_template/get_template_component` | plugin | entity/component access in Kiln services (`Kiln.World`, `Kiln.Players`, `Kiln.Gameplay`) | lab: `query("keen::ecs::PlayerInput")` (0 players → `no-target`); port: a generic reflected component read/write API |
| `runtime.ecs.get_registry/get_component(s)/get_catalog` | plugin | typegen / `kiln.assets` types | port |
| `runtime.ecs.get_attribute(s)/read_attribute(s)/evaluate_attributes/update_attribute/write_attribute_storage` | plugin | `Kiln.Players.PlayerStates`, `Kiln.Combat` | client (survival's stamina, mana, health, oxygen, heat); port: generic attribute API |
| `runtime.services.get` + `RuntimeService:read_fields/write_field` (named plain services, e.g. `ecs::GameSettings`) | plugin | `Kiln.Settings` (game settings), `Kiln.Rules` | **lab**: probe `write-confirmed: enableDurability=false \| writes=1`, restore on off |
| `runtime.overrides.new/attribute/component/resource/template/service`, claims, `RuntimeOverrides:begin_update/apply/restore_disabled/restore_unused/restore_all/summary/...` | plugin | none as such (Kiln services restore their own changes) | lab (apply + restore); port: a reversible-override helper on Kiln services |
| `runtime.values.decode/encode/normalize_field/new` | plugin | typegen value codecs | port |
| `runtime.functions.list/list_native/find/get/get_native/bind/get_operations/bind_modifier`, `runtime.impact.call` | plugin (profile-admitted operations only) | `Kiln.Hooks`, `Kiln.Abilities` (Impact) | — |
| `runtime.world.cursor.get`, `runtime.world.building.submit/status/cancel` | plugin (client) | `Kiln.Build` | client |
| `runtime.world.voxel.get_grid_spec/read/write` | plugin | `Kiln.Build` (schematics), `Kiln.World` | — |
| `runtime.world.entity.query_props(_in_bounds)/get_transform/set_scale/spawn/place/destroy/finish_building/register_prop_recipes` | plugin | `Kiln.Spawn`, `Kiln.Build` | — (world-editor mod: client) |
| `runtime.network.status/send/send_mod/accept/connected_peers/receive/receive_mod` | plugin (SF's Steam Networking Messages) | `Kiln.Network` (`ModCalls`) | lab: "Network service is ready ... role=server" |
| `runtime.patch.available/set_enabled` | plugin (profile-defined patches; none on KFC4) | `Kiln.Hooks` | n/a on current builds |
| `game.assets.get_loaded_resource(_by_field)/get_loaded_resources_by_type/prepare_loaded_index/resolve_loaded_resource/read_loaded_field/write_loaded_field` | plugin | `Kiln.Assets`, `Kiln.Items` | — ; port |

### 8.4 EML v1 API (SF's own implementation, `lua/eml/v1/definitions`)

| Module | Functions | Under Kiln | Kiln C# equivalent | Status |
|---|---|---|---|---|
| `game.assets` (AssetManager) | `get_resource(s_by_type)`, `get_resource_parts`, `get_resource_types`, `create_resource`, `get_content`, `get_all_contents`, `create_content`, `get_all_resources`, Resource/Content fields | plugin (startup and runtime) | `Kiln.Assets`, `tools/kfc` | plugin; — |
| `game.types` (TypeRegistry) | `resolve`, `get`, `get_by_index`, `find`, `find_by_hash`, `get_by_qualified_name`, `get_by_impact_name`, `get_all`, `of`, `Type:is_a` | plugin | typegen | plugin; port |
| `game.guid` (GuidHelper) | `from_content_hash`, `to_content_hash`, `hash` | plugin | `Kiln.Assets` | plugin |
| `buffer` | `create`, `wrap`, Buffer read/write of all scalar types, strings, resources | plugin | `System.Buffers` / `BinaryPrimitives` (C# has it) | plugin |
| `image` | `create`, `decode`, `decode_texture`, `encode`, `encode_texture`, pixel get/set | plugin | — | plugin; port (texture codec) |
| `io` | `read`, `read_to_string`, `list_files`, `exists`, `is_file`, `is_directory`, path helpers, `export*` (exports folder) | plugin (SF's sandbox) | `System.IO` + `Mod.DataDir` | plugin |
| `hasher` | `fnv`, `crc` | plugin | — | plugin; port (trivial) |
| `integer` | 512 helpers (u64/i64 math) | plugin | C# has native 64-bit integers | n/a |
| `loader` | `has_mod` | plugin | `Mods.Has` | plugin |
| globals | `typeof`, `require` (package-local) | plugin | — | plugin |

**Port to Kiln's C# API (point 3 of the brief), not started:** a generic reflected component and attribute read/write API,
a reversible override helper, named-service field access (partly covered: `Kiln.Settings`), loaded-resource lookup and
field write, voxel read/write, an image/texture codec. These must be clean implementations on Kiln's own type data, never
from kfc-parser. Size: large (weeks). The hosted plugin already gives SF mods full coverage, so the port only matters to
authors who want to write Kiln (C#) mods with the same reach.

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
