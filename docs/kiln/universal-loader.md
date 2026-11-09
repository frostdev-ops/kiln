# Kiln as the universal loader: every Enshrouded engine, and the plan to run them all

Status: research, 2026-11-07. No code was changed for this document.

This is a historical research snapshot. For current framework support and limitations, use the
[engine compatibility guides](engines/README.md) and [EMF parity report](emf-parity.md).

The user's direction: "we are the 'universal' mod loader and launcher, we need to support all the engines and runtimes."
The bar for each framework: **Kiln loads its mods through a Kiln plugin, and supports everything its API supports.**
Its API is read from source, and its functionality is ported into Kiln's own API as well.

Sources: each engine's GitHub repository and docs (read through `gh api`, 2026-11-07), Kiln's own docs and code, and
two third-party guides for download counts. Nexus Mods pages answer 403 to scripted reads, so the "mods on Nexus that
require it" figures are rough: they come from GitHub mod lists and the guides, not from Nexus requirement lists.
Creative Mode's loader has no public source. Its facts come from the static notes in
[docs/re/creative-mode-146.md](../re/creative-mode-146.md) and from reading its manifests and import/export tables
(never run).

> **GitHub hygiene.** A search for "enshrouded" on GitHub returns many look-alike repositories created in bulk
> (`*-tool-fix`, `*-dedicated-server-manager`, `*-forge-companion`, each with exactly 58 stars, "trainer", "undetected
> cheat"). They copy real project names, and some imitate ShroudForge's and Shroudtopia's READMEs
> (`Systemkill90/XHL-Enshrouded-Launcher`, `jamithlee5-surf/shroudtopia-manager`). Treat them as malware bait: never
> link them from Kiln's catalog, and never download from them. The real repositories are the ones listed below.

## 0. Findings that change current work (read first)

1. **Kiln treats EMF ≥ 0.4 as ShroudForge (bug).** EMF's loader now also exports `ShroudforgeSetDinput8Provider`,
   because it can host ShroudForge's chain (`matenm/emf-enshrouded` `src/loader/dinput8_loader.def`, next to
   `emf_loader_identity`). `kiln_manage::pe::classify_bytes` checks the ShroudForge export before the EMF exports, and
   `native/proxy/dinput8.cpp` (`ShroudforgeSetDinput8Provider` → `kShroudForge`) does the same. The result: the
   installer refuses to install over EMF ("ShroudForge's mod loader is installed"). If the player picks "switch from
   ShroudForge", EMF's loader is backed up and never chained, and the proxy refuses it as `shroudforge`. **Fix:** test
   the EMF exports before the ShroudForge export, in both places, and add a fixture. This touches the SF agent's
   installer work.
2. **"Switch from ShroudForge" moves files that may belong to other loaders.** `install.rs` backs up `dbghelp.dll` and
   `winmm.dll` without classifying them (`SHROUDFORGE_DLLS`). `dbghelp.dll` is also EML's proxy, and `winmm.dll` is
   also Shroudtopia's and Shroudweave's. Classify each file before moving it (ShroudForge's text `shroudforge`, EML's
   `EML_LOG_FILE_PATH`, Shroudtopia's `shroudtopia`; markers from EMF's `src/core/known_tools.txt`), and move only
   ShroudForge's. For the SF agent.
3. **EMF has moved on: 0.5.1, EmfApi v26, and it works on current builds.** Kiln implements 0.3.3 / v21 and refuses
   mods with `"emf"` > 21. The warning "EMF 0.3.3 is idle on current game builds" in [emf.md](emf.md) is now wrong for
   0.5.x. EMF 0.5 also builds EML content mods (a separate GPL-3.0 `emf_content.dll`), reads ModLoader Proxy's
   `chainloader.ini`, and writes the DLLs it renamed into a new `[EMF]` section of `dinput8-chain.ini`. Kiln's proxy
   reads only `[Chain]` and the lines before any section, so with kiln-emf in place of EMF's loader, the DLLs in
   `[EMF]` stop loading. Details in section 2.1.
4. **ShroudForge on GitHub is 1.8.0-dev** (`bonsaibauer/shroudforge`, releases `v1.8.0-dev.*` on 6 and 7 Nov). The
   local copy in `~/Work/kiln-shroudforge` is 1.7.7. The SF agent should diff 1.8 before freezing its API table.
5. **`powrprof.dll` is the most contested proxy name.** SET, Enshwick, Ember, Shared XP and Creative Mode's server
   build all ship a `powrprof.dll`. SET has its own `shrouded_engine_tweaks\plugins\` folder for other `powrprof.dll`
   mods. For the SET agent: when Kiln chains or displaces SET's `powrprof.dll`, the same code path must handle the four
   other owners. Classify by marker (section 3.1), and never take the name for Kiln itself.
6. **SET's API size, from its own reference** (`docs/api-reference.md`, 0.1.1): about 46 named functions in 13 modules
   (`Admin` 9, `ImGui` 6+, `Ecs` 5, `Res` 5, `Systems` 5, `Types` 4, `Keys` 3, `GameOptions` 3, `Hooks` 2, `Tick`,
   `Events`, `Overlay`, `Draw`). On top of that come name-based reflection over ~690 component types, 91,812
   resources and 861 systems, plus ImGui bindings. It runs in hosted worlds only (no dedicated server). For the SET
   agent's parity table.

## 1. Every engine, runtime and format

"Mods" counts what could be checked; "~" marks an estimate. Kiln status: **supported** / **partial** /
**building now** / **missing**.

| # | Engine / runtime | Author | Licence | Source (latest, activity) | How it loads | Mod format | API style and size | Mods that need it | Client / server | Kiln today |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | **EML** (Enshrouded Mod Loader) | Brabb3l | GPL-3.0 | [Brabb3l/kfc-parser](https://github.com/Brabb3l/kfc-parser) v0.2.0, 2026-10-16; 13 commits in 30 d | `dbghelp.dll` proxy (fallback `dinput8.dll`); runs **before the game starts**, patches assets | `mods/<id>/mod.json` + `src/mod.lua` | Lua, data only: `game` (assets/resources by GUID, content), `loader`, `buffer`, `image`, `io`, `hasher`, `guid`, `types` ≈ 94 functions + 512 integer helpers + generated game types | the most of any engine (~dozens): "many current mods require it"; also used by ShroudForge packages, EMF content, shroudlink's data mods, Nexus "Ember", Auto Loot Plus | both | **missing** (no EML runner; Kiln has its own `kiln.assets` + `tools/kfc`) |
| 2 | **EMF** (Enshrouded Modding Framework) | matenm, "EMF contributors" | MIT (`emf_content.dll` GPL-3.0) | [matenm/emf-enshrouded](https://github.com/matenm/emf-enshrouded) v0.5.1, 2026-11-06; 100+ commits in 30 d | `dinput8.dll` loader + `dinput8-chain.ini`; installer renames other `dinput8.dll`s | `emf/mods/<id>/mod.json` (`dll` or `main` Lua), loose DLLs; EML content | C API **v26, 85 functions**; Lua `emf.*` 64 functions (through its `lua` mod); in-game console, mod windows | 30 mods in its zip + creature packs; Nexus 246 | both | **partial**: kiln-emf = v21, 61 full / 9 partial / 3 no of 73; v22-v26 (12 functions) missing |
| 3 | **ShroudForge** (SF) | bonsaibauer | MIT | [bonsaibauer/shroudforge](https://github.com/bonsaibauer/shroudforge) 1.7.7 (Nexus 147) / 1.8.0-dev (GitHub, 2026-11-07); 100+ commits in 30 d | `dbghelp.dll` + `dinput8.dll` (same loader), `winmm.dll` helper, patches the exe's entry point | `mods/<id>/` EML package + `extended.mod.json` (settings) | Lua: EML-compatible data API + in-game runtime (named services through reflection), F9 menu | ~10-20 (EML mods count too) | both | **building now** (agent `sf`, `docs/kiln/shroudforge-compat.md`) |
| 4 | **SET** (Shrouded Engine Tweaks) | Cacaida | MIT | [Cacaida/Shrouded-Engine-Tweaks](https://github.com/Cacaida/Shrouded-Engine-Tweaks) 0.1.1, 2026-11-07; repository created 2026-11-05 | `powrprof.dll`; Vulkan-layer overlay | `shrouded_engine_tweaks/mods/<name>/init.lua` (+ `mod.json`) | Lua 5.4 + ImGui: ~46 functions in 13 modules + reflection by name (finding 6) | ~1-3 (new) | hosted worlds only | **building now** (agent `set`) |
| 5 | **Enshwick** | enshwick org | MIT | [enshwick/enshwick](https://github.com/enshwick/enshwick) v1.0.0-alpha.13, 2026-11-07; created 2026-10-19; 100+ commits in 30 d | `powrprof.dll` stub → `mods/enshwick/enshwick.dll` loader | `mods/<author.name>/` DLL (Rust / C / C++ / Zig), Luau `main.luau`, TOML data packs | Typed Rust API, ~70 modules (`items`, `inventory`, `ecs`, `ui`, `net`, `map`, `terrain`, `weather`, `survivors`, `content`, `hook`, `calls`...), C header, Luau; join check + `mods.lock`; per-world `settings.toml` | 8 ([enshwick/mods](https://github.com/enshwick/mods)) | both | **missing** |
| 6 | **Greeneye Loader** | Greeneye0 | MIT | [Greeneye0/greeneye-loader-enshrouded](https://github.com/Greeneye0/greeneye-loader-enshrouded) v1.2.2, 2026-11-05 | `dbgcore.dll` (or `umpdc.dll` / `cfgmgr32.dll`); starts mods at the exe entry point | `greeneye/<id>/mod.json` (`id, name, version, dll, side, api, enabled`) + DLL exporting `GreeneyeModStart` | Native C ABI, **API 2**: `GreeneyeHost` with 9 functions (`log`, `enter`/`leave` hook lease, `find`, `read`, `hook`, `redirectCall`, `poll`, `keep`) + optional hot reload (`prepare`/`ready`/`resume`) | 6: The Worst Map Mod (~6.7k downloads), Find, Town Portals, Embervale Teleport, Quick Deposit, Hollow Halls Progression | both (reported not to start under Wine) | **missing** |
| 7 | **Shroudtopia** / **Shroudweave** | s0t7x / Shroudweave fork | MIT | [s0t7x/shroudtopia](https://github.com/s0t7x/shroudtopia) 0.1.1, 2026-02-16 (stale); 1.0 fix [Aerox912/shroudtopia-1.0-compatibility](https://github.com/Aerox912/shroudtopia-1.0-compatibility) r2, 2026-10-16; Shroudweave (Nexus 216): no source found | `winmm.dll` | `mods/*.dll` + `shroudtopia.json` (per-mod settings) | Native **C++ ABI** (`class Mod` with `GetMetaData/Load/Unload/Activate/Deactivate/Update`; `ModContext` with ~21 `std::function`s: 10 config getters, 10 memory helpers, `Log`) | ~5: Minimap and Weather Clock (~10k), elxokker minimap, basics, flight, first-person view | both | **missing** |
| 8 | **Flame** (Creative Mode's loader) | unknown (Nexus 146's author) | **none** | no public source; PDB path `F:\enshrouded-dev\build-dist\sdk\loader` | `dinput8.dll` (client); a `powrprof.dll` build for servers, per a guide | `mods/<id>/mod.json` + `flame.json` `{"dll": ...}` + `sigs/<build>.json` + `symbols/*.json` | The loader calls `LoadLibraryExW(dll, 0, LOAD_WITH_ALTERED_SEARCH_PATH)`, then `FlameStartRuntime()` with **no arguments**. The mod imports nothing from the loader: the "Flame" runtime is statically linked into each mod DLL | 1 found: Creative Mode (~18k downloads) | both | **missing**: the Library mis-detects it (section 3.2) |
| 9 | **shroudlink** | bjck | MIT | [bjck/shroudlink](https://github.com/bjck/shroudlink) v0.8.0, 2026-11-02; ~100 commits in 30 d | server: `steam_api64.dll` proxy; client addon: `dinput8.dll` | Rust mods on `mod/sdk` (SDK v2) + EML data mods | Rust SDK (components, events, UI building blocks) | 5 + 2 data (Nexus 278-283: chestsort, assistants, storage, meter, gemforge) | server (+ optional client addon) | **missing** |
| 10 | **Ember** (server SDK) | zachthedev | MIT | [zachthedev/enshrouded-ember](https://github.com/zachthedev/enshrouded-ember), crates on crates.io, no loader release yet | `POWRPROF.dll` by default (also `IPHLPAPI.DLL`, `dbghelp.dll`) | Rust `ember-sdk` mods | Rust SDK | 1 (private-chests) | dedicated server only | **missing** (not urgent). Not to be confused with Nexus 79 "Ember", an EML mod |
| 11 | Stand-alone server DLL mods | oggii | MIT | [enshrouded-livemap](https://github.com/oggii/enshrouded-livemap) v1.7.0; [enshrouded-shared-xp](https://github.com/oggii/enshrouded-shared-xp) v1.1.3 | `iphlpapi.dll` (Live Map), `powrprof.dll` (Shared XP) | DLL + ini / web folder | none (self-contained) | 2 | server | **partial**: no proxy slot for these names |
| 12 | Chain loaders (no mod API) | Entrodor (ModLoader Proxy), Bigdaddy2I42 (Multi DLL Installer), XHL (XHL-Core), ThirteenAG (Ultimate ASI Loader, MIT) | varies | ModLoader Proxy, Multi DLL Installer, XHL-Core: no source found | `dinput8.dll`; read `chainloader.ini` / `dinput8-chain.ini` / `.asi` plugins | lists of DLLs | none | XHL mods, Auto Loot Plus, `.asi` plugins | both | **partial**: Kiln reads `dinput8-chain.ini` `[Chain]`, not `[EMF]`, `chainloader.ini` or `.asi` |
| 13 | Stand-alone `dinput8.dll` mods | many (Stat Sliders, Sprint Toggle, ShroudHud, Auto Loot Plus...) | varies | - | `dinput8.dll` | one DLL | none | ~10+ | mostly client | **supported**: chained (`kiln/chain/`) or imported as native `dllmain` |
| 14 | Data and file mods | various | - | - | replace or patch files | `.kfc` patched in place (Enshrouded Patcher / "Enshrouded Modder", Nexus 91, ~11.7k; incompatible with EML), save files (Nine's Sandbox, ~32k), ReShade (`dxgi.dll` + presets), Cheat Engine tables (`.CT`, Enshrouded Table ~75k) | none | - | - | **missing** (only Kiln's own asset packs) |
| 15 | Aldrich Mod Menu | AldrichDDD | ? | no source found | a `MODS` main-menu entry (F8) that lists loaders | - | - | - | client | **missing** (detect only) |
| 16 | Vortex extension | Nexus community | - | Nexus site mod 2453 | Vortex installs EML 0.2.0 and EML mods | - | - | - | - | n/a: Kiln's Nexus handler already remembers and forwards to Vortex/MO2 |

**Proxy names in use** (only one file per name can exist in the game folder):

| Name | Holders |
|---|---|
| `dinput8.dll` | **Kiln**, EMF, ShroudForge, Flame (Creative client), shroudlink addon, ModLoader Proxy, XHL-Core, stand-alone mods, EML (fallback) |
| `dbghelp.dll` | EML, ShroudForge |
| `winmm.dll` | ShroudForge (helper), Shroudtopia, Shroudweave, Shroudscaler |
| `powrprof.dll` | SET, Enshwick, Ember, Shared XP, Creative (server) |
| `dbgcore.dll` / `umpdc.dll` / `cfgmgr32.dll` | Greeneye Loader |
| `iphlpapi.dll` | Live Map (and Ember as an option) |
| `steam_api64.dll` | shroudlink server (replaces Steam's own DLL: a Steam file check or game update puts the original back) |
| `dxgi.dll` | ReShade |
| `xinput9_1_0.dll` | Ultimate ASI Loader, as EMF chains it |

## 2. Per engine: API surface, Kiln coverage, and the work to full parity

Sizes: **S** = hours, **M** = about a day, **L** = several days, **XL** = a week or more. Option letters follow the
brief: **A** host its runtime as a Kiln plugin, **B** chain its own loader through Kiln's proxy chain,
**C** re-implement its API on Kiln's services, **D** install data mods through the launcher. The user's bar is a Kiln
plugin plus full API parity, so every engine ends in A or C, with B only as a stopgap. "Kiln-side port" means the
engine's functionality also added to Kiln's own C#/native API, so Kiln mod authors get it too.

### 2.1 EMF: partial, closest to parity

- **API surface (v26, source `sdk/include/emf/emf_api.h`): 85 functions.** Groups: worlds/session (8), ECS
  (components, templates, entities, programs, events: ~16), hooks and memory (7), items and inventories (10), spawn
  and combat (7), chat and commands (8), saves (3), world settings (5), mod settings and info (8), HUD/UI (wheel, hud,
  notify, windows, images: 8), player level/XP (3), spawn offers (4). Lua: 64 `emf.*` functions, served by EMF's own
  `lua` mod on top of the C API.
- **Kiln today (kiln-emf, [emf.md](emf.md)): v21, 73 functions.**
  - 61 full.
  - 9 partial: `on_event` (server world only), `register_program` and `register_program_def` (server world),
    `read_events`, `spawn_ex` (no extra components), `attribute_get`/`attribute_add` (health only), `hud_set`,
    `notify`.
  - 3 not supported: `program_write_other`, `hook_program`, `wheel_open`.
  - All 13 shipped EMF 0.3.3 DLL mods, plus `lua` with 8 Lua mods and Kiln's probe (72/72), load on a lab server
    (svn 1285601). What a player must see in game is still on the client checklist.
- **Missing for v26:** 12 functions:
  - player level and XP: `player_xp_required`, `player_level_cap`, `player_level_set` (v22);
  - `change_component` (v23);
  - mod windows: `window_open`, `window_update`, `window_close` (v24);
  - spawn offers: `spawn_offer`, `offer`, `offer_remove`, `offer_active` (v25);
  - `image_load` (v26).

  Mods asking for `"emf"` 22-26 are refused today. EMF 0.5 also ships 9 mods Kiln has not tried (blessings,
  event_log, guid_dump, model_probe, model_survey, npc_explorer, sample_mod, sim_rate, write_test) and creature packs.
- **Approach: C, kept** (re-implement the table on Kiln services). It works and is MIT. Hosting EMF's own core (A) is
  possible, since the source is complete, but it would bring a second hook engine and EMF's own signature tables.
- **Work to full parity:**
  - Table v26 with the 12 new slots: player level/XP over `Kiln.Players` progression (S);
    `change_component` over `kiln.world` commands (M); windows and images over `kiln.ui`, which has overlay windows
    already (M); spawn offers (M, needs RE of the offer system).
  - The 3 unsupported: other-entity writes in `kiln.world` (M); program-update hooks (M); a radial wheel in
    `kiln.ui` (M; the five wheel mods need it).
  - The 9 partials: client-world programs and events (L); stamina and mana attributes (S).
  - `[EMF]` section in the proxy's `dinput8-chain.ini` reader, only when EMF's own loader is not chained (S).
  - The EML content path: covered by the EML plugin (2.2).
  - Kiln-side ports: wheel, spawn offers and mod windows become `Kiln.Ui` / `Kiln.Spawn` APIs.
- **Total:** L (about 4-6 days). **Risk:** low to medium; game updates go through Kiln's sigdb as today. **When EMF
  updates:** diff `emf_api.h` per release (it only grows by appending slots), add slots, bump the table version.
  Make it a CI check against the GitHub tag.

### 2.2 EML: missing, the largest data-mod base

- **API surface:** the Lua definitions in `crates/mod-loader-lua/definitions/`:
  - `game` (asset manager: resources and content by GUID, ~10 + resource/content accessors);
  - `buffer` (42), `io` (12), `TypeRegistry`/`types` (8), `image` (9), `hasher` (3), `guid` (3), `loader`/`runtime`
    (2);
  - `integer` helpers (512 generated overloads);
  - generated types for every game data structure.

  Capabilities `patch` and `export` work today; `runtime` is not done upstream. It runs once, before the game reads
  its data.
- **Kiln today:** nothing runs EML mods. Kiln has its own cooker path (`kiln.assets`, `tools/kfc`, `tools/modelcook`),
  so the KFC side is known ground.
- **Approach: A, as a separate GPL-3.0 plugin** `kiln-eml.dll`, built from `kfc-parser`'s `mod-loader*` crates, the
  way EMF did it (`emf_content.dll`, GPL, runs only at start). Kiln stays MIT: the plugin is a separate program that
  Kiln starts at the `early` phase, before any game data loads.
  - EML's output (patched data files or an in-memory overlay, depending on version) is fed through Kiln's asset layer,
    so Kiln asset packs and EML mods compose.
  - Pure re-implementation (C) is not worth it: 600+ functions and generated types that follow EML's own reflection
    parser.
- **Work:**
  - plugin wrapper and start-phase hook (M);
  - mod discovery from profiles plus `mods/` drop-ins, `mod.json` checks, ordering (S);
  - composing EML patches with `kiln.assets` layers and conflict reports (M);
  - Library kind `eml`, Linux/Proton overrides not needed (Kiln's proxy starts it) (S);
  - Kiln-side port: a Lua (or C#) "data patch" step in Kiln's own content pipeline that uses the same GUID/resource
    model (L, optional; Kiln's typed blocks already cover most cases).
- **Total:** L. **Risk:** medium. EML parses game types from the executable at start, so a game update needs an EML
  release (as it does without Kiln). Licence: keep the GPL plugin's source and notice in the bundle.
  **When EML updates:** rebuild the plugin from the new tag; its Lua API is the crate's, so no table to maintain.

### 2.3 ShroudForge: building now

- **API surface:** EML's API (it vendors `kfc-parser`) plus its in-game runtime: `runtime.services.get` (read-only
  views of the game's named services through reflection), mod lifecycle and reload, profile patches, settings from
  `extended.mod.json`, its F9 Modloader UI and DLL manager (it also reads `dinput8-chain.ini` `[Chain]`, only for
  `mods\<folder>\*.dll` lines).
- **Kiln today:** refused as a proxy; agent `sf` is building support (design options A/B/C in its brief).
- **Recommended:** A (SF's MIT Rust runtime as a Kiln plugin). It shares the EML core with 2.2: build `kiln-eml` and
  the SF runtime on one copy of the EML crates, or the GPL boundary gets drawn twice. Settings: map
  `extended.mod.json` to Kiln's schema snapshots (5.5).
- **Total:** L (agent's estimate governs). **When SF updates:** it releases several times a day; pin to tagged
  releases, not `-dev` builds.

### 2.4 SET: building now

- **API surface:** finding 6 (about 46 functions + reflection + ImGui).
- **Kiln today:** agent `set` is building a Lua-compat plugin (`native/plugins/set/`), chaining SET's `powrprof.dll`
  and stepping aside for real SET.
- **Kiln-side ports the API asks for:**
  - reflection access by name (components, resources, systems);
  - a systems on/off switch and profiler;
  - live game options;
  - ImGui mod windows (Kiln's overlay is ImGui already).

  These match the dev tools the user asked for. SET supports hosted worlds only, so a server-side SET is a Kiln
  bonus, not parity.
- **Total:** L. **When SET updates:** 0.x and two days old; expect API churn. Diff `docs/api-reference.md` per release.

### 2.5 Greeneye Loader: missing, small API

- **API surface:** 9 host functions + 3 hot-reload hooks + `mod.json` (7 fields). The loader owns every hook, so a mod
  never patches the game itself.
- **Approach: C** (a `kiln-greeneye` plugin that implements `GreeneyeHost` on Kiln's services):
  - `hook` / `redirectCall` → safetyhook detours in Kiln's hook chain;
  - `find` / `read` → Kiln's on-disk scan, which sees code as it was before hooks (Greeneye's own rule);
  - `enter` / `leave` → a generation lease;
  - `keep` → a named persistent block;
  - `log` → `kiln.log` `[greeneye:<id>]`.

  Load `greeneye/<id>/` folders, call `GreeneyeModStart` at the `early` phase (Greeneye starts at the entry point,
  before game code: Kiln's `early` phase is the same moment).
- **Why not A:** the loader's source is MIT and small, but hosting it would add a second hook owner; re-implementing
  9 functions is cheaper and chains with Kiln's hooks.
- **Work:**
  - plugin with the 9 functions (M);
  - hot reload: optional, behind a setting, needs a generation counter (M);
  - Library kind `greeneye`, `side` mapping, settings (Greeneye mods keep their own ini files) (S).
- **Total:** M-L. **Risk:** low. The mods carry their own patterns, so game updates are theirs to follow. Greeneye
  reports it does not start under Wine, but a Kiln plugin removes that problem: a Linux-native win for its mods.
- **Kiln-side port:** "hooks owned by the loader, with a lease for hot reload" is worth adding to Kiln's native hook
  API.

### 2.6 Shroudtopia / Shroudweave: missing, stale upstream

- **API surface:** a C++ `Mod` class (6 virtuals) with `ModContext`: 10 typed config getters over `shroudtopia.json`,
  10 memory helpers (`FindPattern`, `Read`, `Write`, `WriteToReadOnlyMemory`, `WriteJump`, `ApplyPatch`,
  `InjectShellcode`, `CreateDetour`, `AllocateMemoryNearAddress`, `GetModuleBaseAddress`), `Log`, and game/loader
  info. Mods are DLLs in `mods/`.
- **Approach: C.** A `kiln-shroudtopia` plugin builds a `ModContext` from Kiln services:
  - config from a per-mod Kiln config whose values are read from `shroudtopia.json` the first time;
  - detours and patches through Kiln's hook chain;
  - `Update` on Kiln's tick;
  - Activate/Deactivate from the launcher switch.

  **Risk: the ABI is C++** (`std::function`, `std::string` by value). It works only if the plugin is built with the
  same MSVC STL as the mods, which it is in practice (VS 2022, `/MD`). Document it and refuse mods that export a
  different `targetShroudtopiaVersion`. Shroudweave's changes are unknown (no source found): test its mods (Minimap and
  Weather Clock) one by one.
- **Total:** M-L. **When it updates:** upstream is quiet since February; the 1.0 fork is a snapshot.
- **Kiln-side port:** none needed (Kiln's hook API is a superset).

### 2.7 Flame / Creative Mode: missing, one mod, no licence

- **API surface:** none between loader and mod (`FlameStartRuntime()` only). The mod's runtime is inside its DLL and
  finds its game functions with its own `sigs/` and `symbols/` per build.
- **Approach: A, in its smallest form.** A `flame` mod kind in Kiln's native loader: for a folder with `flame.json`,
  `LoadLibraryExW(dll, LOAD_WITH_ALTERED_SEARCH_PATH)` and call `FlameStartRuntime` at the `early` phase. No source
  is needed, and nothing of the mod is copied. Kiln's creative features are already its own
  ([creative-mode-146.md](../re/creative-mode-146.md)).
- **Work:** the `flame` kind in `native.json` generation and the native loader (S); a server build check (its
  `powrprof.dll` server variant is the same contract, to be checked on a copy) (S); the conflict rule with Kiln's own
  creative (section 3.6) (S).
- **Risk:** medium. Its hooks sit on the same game functions as Kiln.Rules' creative hooks (payment, locomotion,
  daytime). **When it updates:** nothing to do; it brings its own signatures.

### 2.8 Enshwick: missing, the largest API, alpha

- **API surface:** ~70 modules in the `enshwick` crate:
  - gameplay: items, inventory, recipes/content, loot, buffs, damage, stats, survivors, altars, map, terrain, voxels,
    weather, daytime;
  - interface: ui, chat, console, input/controls, sound;
  - networking and calls: net, calls/services between mods;
  - hooks and raw access;
  - Luau scripts and TOML data packs.

  The loader (`crates/internal/loader`) finds its engine entries per build by "engine routes" and code patterns, with
  no file made for the build. Per-world `settings.toml` and a join check with `mods.lock`.
- **Approach: A** (host Enshwick's MIT loader as a Kiln plugin: started by Kiln instead of the `powrprof.dll` stub,
  with Kiln's paths and profile). It is a whole framework with its own hook ownership. Re-implementing its ABI (C) is
  XL+ and chases an alpha. Hook coexistence: Enshwick arms "hook chains" of its own, so it must chain over Kiln's
  safetyhook detours on shared targets (test the common ones: inventory, crafting, chat).
- **Work:**
  - plugin host (start the loader from Kiln, redirect `mods/` and `logs/`) (M);
  - Library kind `enshwick` (`<author>.<name>` ids, DLL / Luau / data pack) (S);
  - settings: map per-world `settings.toml` to the launcher's editor (M);
  - join check: carry `mods.lock` beside Kiln's mod-set fingerprint (M);
  - Kiln-side ports of what Kiln lacks (calls between mods, data packs in TOML, per-world settings) (L).
- **Total:** L to XL. **When it updates:** alpha, releases every two days; pin by tag and re-test the host contract.

### 2.9 shroudlink: missing, server-centred

- **API surface:** Rust SDK v2 (`mod/sdk`: components, events, UI building blocks, server↔addon link); EML data mods.
- **Approach: A** for the server runtime: start it from a Kiln plugin instead of the `steam_api64.dll` proxy. That
  removes the riskiest proxy name in the list, the one Steam's file check restores. Its client addon (`dinput8.dll`)
  is chained by Kiln's proxy (B) until it can also be hosted.
- **Work:** M-L. **Risk:** medium (server mods that move items in chests).

### 2.10 Ember (server SDK): missing, no release yet

Rust `ember-sdk`, dedicated server only, `POWRPROF.dll`. **Approach:** A, once a loader release exists. Until then,
detect it and explain. M later.

### 2.11 Stand-alone proxy mods and chain loaders

- **Proxy mods under other names** (Live Map `iphlpapi.dll`, Shared XP `powrprof.dll`, ReShade `dxgi.dll`): Kiln
  never needs their name. Leave them in place, list them in the Library as "runs next to Kiln", and, when two want one
  name, offer a **proxy slot** (3.1).
- **Chain loaders:**
  - read `chainloader.ini` (`ForwardTo`, `[Load]`, `ModsFolder`) like EMF does, so a ModLoader Proxy setup keeps
    working when Kiln takes `dinput8.dll` (S);
  - chain Ultimate ASI Loader as `xinput9_1_0.dll`, EMF's trick, so `.asi` plugins load (S).
  - XHL-Core reads `dinput8-chain.ini` `[Chain]`: already compatible.

### 2.12 Data and file mods

- **`.kfc` patchers** (Enshrouded Patcher / "Enshrouded Modder"): they rewrite game files in place, which conflicts
  with EML and Kiln's asset layer and is undone by Steam's file check. Kiln: detect modified `.kfc` files (hash vs.
  the build record), warn, and offer "verify game files". Long term: the same tweaks as EML or Kiln data patches (D).
- **Save files:** an "import world/character" flow in the launcher's save manager (`kiln-manage::saves`), with a backup
  first (S-M).
- **ReShade presets:** detect and leave alone.
- **Cheat Engine tables:** out of scope. Detect `.ct` / `.cetrainer` in an import and say so.

## 3. What has to be true across all engines

### 3.1 The installer never displaces a loader without chaining it

- Classify **every** proxy name, not only `dinput8.dll`:
  - names: `dinput8`, `dbghelp`, `dbgcore`, `umpdc`, `cfgmgr32`, `winmm`, `powrprof`, `iphlpapi`, `version`,
    `xinput1_4`, `dxgi`, `steam_api64`;
  - markers: start from EMF's `src/core/known_tools.txt` (MIT): text markers, "beside" files, "same-loader" and
    "helper" relations.

  Add `ProxyKind` values for EML, Greeneye, Shroudtopia, SET, Enshwick, Flame (export `FlameStartRuntime` in a mod
  next to it), ModLoader Proxy and Ultimate ASI Loader.
- Kiln owns only `dinput8.dll`. Every other name is left in place. When a mod brings a name that is taken (two
  `powrprof.dll`), Kiln does not overwrite it: it puts the newcomer in `kiln/chain/<kind>-<name>.dll` and loads it
  from its proxy. That is safe because these DLLs do their work in `DllMain` or a thread, not through the exports.
  Flag the ones that need their real name (Greeneye under `dbgcore.dll` starts at the entry point; others start late).
- Fix the EMF-as-ShroudForge order (finding 1). Replace the blanket ShroudForge file list with classified moves
  (finding 2).
- Prefer hosting over chaining. When a Kiln plugin hosts an engine (EMF, EML, SF, SET, Greeneye, Flame...), its own
  loader is set aside (backed up and recorded in the receipt, put back on uninstall). This is the "step aside" rule in
  reverse: when the real loader is wanted, Kiln's plugin steps aside, as kiln-emf does.

### 3.2 The Library detects every format and says why something cannot load

Add `DetectKind` values and checks, in this order (EMF's `classify.h` order is a tested reference):

| Kind | Marker |
|---|---|
| `eml` | `mod.json` + `src/mod.lua` |
| `shroudforge` | EML layout + `extended.mod.json` |
| `emf` | `mod.json` with `"emf"`, or `main` Lua under `emf/mods` |
| `greeneye` | `greeneye/<id>/mod.json`, export `GreeneyeModStart` |
| `shroudtopia` | DLL in `mods/` + `shroudtopia.json` |
| `set` | `shrouded_engine_tweaks/mods/<name>/init.lua` |
| `enshwick` | `mods/<author.name>/` (manifest per its book) |
| `flame` | `flame.json` |
| `proxy` | a root DLL with a proxy name: tell which tool owns it |
| `save` | `*.ksd`, a world save pair |
| `reshade` | preset |
| `cheat-table` | `.ct` |

**Today's mis-detection, Creative Mode:** the zip's `dinput8.dll` becomes a native `dllmain` mod, which starts the
Flame loader looking for `<game>/mods/`, a folder Kiln never fills. `mods/creative/creative.dll` becomes a plain
`early` DLL whose `FlameStartRuntime` is never called. Both "load" and nothing happens. Every unsupported kind must say
so plainly ("a Shroudtopia mod: Kiln cannot run these yet; it needs Shroudtopia's winmm.dll"), never import as a
plain DLL.

### 3.3 Profiles mix mods from all engines

- Extend `ModKind` / `profile.mods` with one list per engine kind (`eml`, `shroudforge`, `greeneye`, `shroudtopia`,
  `set`, `enshwick`, `flame`), each entry `{id, version, enabled}` as today.
- Activation renders each engine's own state files from the profile (EMF ini, `shroudtopia.json` mods block, Greeneye
  `enabled`, Enshwick folder presence), so the engines' own loaders, if present, agree with the profile.
- Play-once `no-mods` and `vanilla` must cover every engine. That is automatic for hosted ones; set-aside loaders
  stay set aside.

### 3.4 Settings UIs for every format

One editor (the launcher's schema-driven page, contract 5.5), fed by one adapter per format:

| Format | Schema source | Values file |
|---|---|---|
| EMF | `mod.json` settings | `<configDir>/emf/<id>.ini` (done) |
| ShroudForge | `extended.mod.json` | SF's config (agent `sf`) |
| EML | none (code-only) | - |
| Greeneye | none declared | mod's own ini: show as a text file |
| Shroudtopia | `shroudtopia.json` `mods.<key>` (types from the values) | same file |
| SET | `mod.json` | its settings (agent `set`) |
| Enshwick | its settings schema | per-world `settings.toml` |
| Flame | none | `settings.json` / `server.json`: text editor |

Every adapter writes a `.schema/<engine>/<id>.json` snapshot, so the launcher stays engine-agnostic.

### 3.5 Servers and modpacks carry non-Kiln mods

- `kiln-modpack.json` today carries `kiln/mods/*` and `kiln/native/*` only. Add every hosted kind (`emf/`, `eml/`,
  `shroudforge/`, `greeneye/`, ...) with kdh1 per folder, and their `side`/`clients` rule:
  - EML data mods are needed on both sides;
  - EMF `side: client` mods only on clients;
  - Enshwick join-check mods on both;
  - shroudlink and Ember are server-only, with an optional client addon.
- Kiln.Network's advertised list and the join check must include them, so a player without the server's EML data mod
  is told before joining. That matters most for item data: a server with EML stack mods and a vanilla client loses
  items (shroudlink's own warning).
- Server install (`servers.md`): the same set-aside and chain rules for the server folder, and no
  `WINEDLLOVERRIDES` beyond `dinput8` once Kiln hosts the engines.

### 3.6 Conflict policy: two engines touching one game system

- **One hook chain.** Kiln's hook chain (safetyhook, priorities) is the owner for every hosted engine re-implemented on
  Kiln (EMF, Greeneye, Shroudtopia, SET compat). Engines hosted whole (EML, SF, Enshwick, Flame, shroudlink) keep
  their own hooks. Kiln records, per game symbol, which owners hooked it (owner `emf:<id>`, `greeneye:<id>`, `flame`,
  native module names from a scan of hook prologues at the `boot` phase) and writes `state/hooks.json`.
- **Declared systems.** Each engine adapter tags its features with a system name from one shared list (`inventory.pay`,
  `locomotion`, `daytime`, `loot`, `stacks`, `fog_of_war`, `crafting`, `building.cost`, `chat`). Two enabled mods on
  one system:
  - **data** (EML / SF / Kiln asset layers on one resource): last in load order wins, the Library shows the clash;
  - **hooks that replace** (Creative Mode's free building and Kiln.Rules' `BuildCost`): Kiln refuses the second and
    tells the player which to switch off, unless both are on a known-compatible list;
  - **hooks that observe**: both run.
- **Defaults.** Kiln's own feature yields to a third-party mod the player switched on explicitly (Kiln's creative hooks
  go idle while Flame's Creative Mode runs), and says so in the log and the Library.
- **Duplicate engines.** Never two copies of one engine (EMF's own rule: one EMF loader per process). A hosted engine
  steps aside when the real one is chained, and vice versa.

## 4. Prioritised plan (full parity, engines with the most mods first)

| # | Step | Why first | Size |
|---|---|---|---|
| 1 | **Stop the damage**: classify EMF before ShroudForge (pe.rs + proxy), classified ShroudForge switch, `[EMF]` section, never import an unknown kind as a plain DLL (Creative Mode, Shroudtopia, Greeneye folders get a clear "not yet" reason) | today's installer can refuse or mangle EMF 0.5, EML and Shroudtopia setups | S-M |
| 2 | **Proxy-name classifier + Library detection for every kind** (known_tools markers, `DetectKind`s of 3.2, proxy slots for taken names) | every later engine needs it; players see what they have | M |
| 3 | **EML plugin** (`kiln-eml`, GPL, pre-start) shared with SF's EML core | the largest mod base; SF packages, EMF content, shroudlink data and Nexus "Ember" all sit on it | L |
| 4 | **EMF v26 parity** (12 new functions, wheel, program writes/hooks, client programs, 0.5's 9 new mods) | 30 shipped mods + creature packs; already 80% there | L |
| 5 | **ShroudForge** (in progress) on the same EML core | second-largest Lua base, very active | L |
| 6 | **Modpacks, profiles and settings for every kind** (3.3-3.5) | servers and mixed profiles are what makes "universal" real | L |
| 7 | **Greeneye** (9-function host) | 6 popular client mods (The Worst Map Mod), and Linux support they lack | M-L |
| 8 | **Flame kind** (Creative Mode) + its conflict rule with Kiln creative | ~18k downloads, one mod, small adapter | S-M |
| 9 | **Shroudtopia / Shroudweave** | ~10k-download minimap, older mods | M-L |
| 10 | **SET** (in progress) | new, few mods yet; its dev tools are wanted anyway | L |
| 11 | **Enshwick host** | 8 mods, fastest-moving framework; alpha | L-XL |
| 12 | **shroudlink server host** | 5 server mods; removes the `steam_api64.dll` proxy | M-L |
| 13 | Conflict registry and `state/hooks.json` (3.6), then chain loaders (`chainloader.ini`, ASI), data-mod detection, save import | polish across all engines | M each |
| 14 | Ember, once it ships a loader | 1 mod | M |

Keeping parity as engines update: one CI job per engine that fetches the latest tag (`gh api`) and diffs the API
definition file (`emf_api.h`, EML `definitions/*.lua`, SET `api-reference.md`, `greeneye.h`, Enshwick `abi.rs` and the
book's index, `shroudtopia.h`). A changed surface opens a task. The hosted runtimes (EML, SF, Enshwick) are rebuilt
from the new tag.
