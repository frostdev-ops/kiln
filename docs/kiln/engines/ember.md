# Ember mods on Kiln (kiln-ember)

**Ember** ([zachthedev/enshrouded-ember](https://github.com/zachthedev/enshrouded-ember), MIT) is a Rust modding SDK
for the Enshrouded dedicated server: a loader DLL (`POWRPROF.dll` by default, or `IPHLPAPI.DLL` / `dbghelp.dll`) and
the crates `ember-sdk`, `ember-holistic`, `ember-enshrouded` on crates.io. It is not Nexus mod 79 "Ember", which is an
EML data mod (kiln-eml runs that one).

## State of Ember (checked 2026-11-07)

Ember is designed but not implemented yet:
- the repository (last code change 2026-10-16, commit `f7434d87`) and the published crates 0.1.0 hold **module
  documentation only**. `ember_sdk::declare`, the ABI types in `ember_sdk::abi`, the host table in
  `ember_loader::host_api` and the proxy forwarding are doc comments without code;
- no loader release exists;
- its one mod, private-chests ([zachthedev/enshrouded-mods](https://github.com/zachthedev/enshrouded-mods)), is a
  description: its `lib.rs` is `use ember_sdk as _;`. Built from source it is a DLL that exports nothing.

So there is **no mod ABI a host can implement**: export names, the version value and the table layouts are not
written down anywhere. Starting a mod library without them would mean guessing what to call. Kiln does not guess.

## What kiln-ember does

`kiln/plugins/kiln-ember.dll` (`native/plugins/ember`, Kiln's own code from Ember's documents):
- finds Ember mods where Ember will look for them, `<server>/ember/mods/<mod>/`, one library per folder (Ember's rule),
  and in a Kiln profile's `mods.ember` list (an entry's `path` is the mod folder or its library);
- reads each library's export table from the file, without loading it, and logs it, so the first real Ember mods
  can be checked against the ABI when it is published;
- reports each mod: kiln.log `[ember:<id>] folder: not loaded: Ember has not published its mod ABI yet ...`, and
  `kiln/state/ember.json` (status `waiting-for-abi`, `skipped` with a reason, or run by Ember's loader);
- **steps aside** when Ember's own loader is in the process: a DLL from the server folder or `kiln/chain/` whose file
  carries `ember-loader`/`ember_loader` (the marker Kiln's proxy classification uses). Ember's loader then runs its
  mods; `kiln-ember steps aside: ...` in kiln.log;
- hooks nothing. The server runs on.

Folders and DLLs are checked from the authorized root down and held open while the file is inspected, including
single-DLL profile entries. Traversal, aliases, directory links and reparse points within the root are refused;
ancestors above it follow the shared [path policy](README.md). The PE reader validates file-backed bounds, export
tables, ordinals and callable targets without executing the DLL.

Ember's loader takes the `POWRPROF.dll` name, which SET, Enshwick and Shared XP also use. Kiln never takes that name, so
Ember's loader can stay next to Kiln when it ships (on Wine with `WINEDLLOVERRIDES="powrprof=n,b"`, as Ember says).

## Coverage

Ember's documented surface (README, `docs/usage.md`, module docs). "Kiln C# equivalent" is what a Kiln mod uses for
the same capability today.

| Ember item | Kiln implementation | Kiln C# equivalent | Status / tested |
|---|---|---|---|
| mod layout `ember/mods/<mod>/` with one library | kiln-ember discovery | `kiln/mods/<id>/` + `kiln.mod.json` | full; lab (two mods found, the two-library folder refused) |
| profile list | `mods.ember` (compat.h rule) | profile `mods.kiln` | full; lab plan line |
| ABI-version export, read first; mismatch refused by name | not implementable: not published | `kiln.mod.json` `api` range | **blocked on Ember** |
| register export returning the manifest | not implementable: not published | `kiln.mod.json` | **blocked on Ember** |
| `Mod::load` / `Mod::ready` lifecycle | - | `Mod.OnLoad`, `Lifecycle` events | blocked on Ember |
| chained hooks, order `First`/`Normal`/`Last`, continue / stop with substitute / fail-and-skip | - | `Hooks.Detour` with priorities (Kiln.Hooks) | blocked on Ember; Kiln has it |
| events = observing/intercepting links | - | `[Subscribe]`, `CancelableEvent` | blocked on Ember; Kiln has it |
| required symbols, mod disabled by name when missing | - | `Game.HasFeature`, sigdb features (fail soft) | blocked on Ember; Kiln has it |
| dependency ordering of mods | - | `kiln.mod.json` `dependencies` | blocked on Ember; Kiln has it |
| per-mod JSON config written from defaults; unknown key disables the mod | - | `Config` (Kiln.Api, schema + defaults) | blocked on Ember; Kiln writes defaults, does not disable on unknown keys |
| per-mod log through the host's sink | - | `ModContext.Log` | blocked on Ember; Kiln has it |
| save-cycle hooks: stage a payload, committed only when the game's save succeeds | - | `Kiln.Saves` | blocked on Ember |
| engine access: reflection names (`ecs.<Name>`, `keen::<Name>`), programs, components | - | `World.ComponentHash`, `World.ReadBytes`, `kiln.types` | blocked on Ember; Kiln has it |
| game surface: sessions, chat/HUD, items, recipes, crafting stock, inventory, placement, interaction | - | `Players`, `Chat`, `Items`, `Gameplay.Recipe`, `Build` | blocked on Ember; Kiln has most |
| loader proxy (`POWRPROF.dll`, forwarding every export) | not needed: Kiln's proxy is `dinput8.dll`; step aside when Ember's loader runs | - | step-aside implemented; not lab-tested (no Ember loader exists) |
| startup report: build, symbols, mods, links | `kiln/state/ember.json` | `kiln/state/*.json`, `hooks.json` | partial |

Counts: 2 of 16 items implemented now (layout, profile list), 1 partial (report), 1 step-aside path; the other 12 wait
for Ember to publish its ABI. Kiln's own API already covers 10 of those 12 capabilities.

When Ember publishes its loader and SDK (both MIT), the plan is the shroudlink route: build its loader crate from source
as a Kiln plugin (option A) inside kiln-ember, so Ember mods run unchanged. Size: M once the ABI exists.

## Tests

`KILN_OUT=<out> native/build.sh kiln-ember` also builds `bin/ember-tests/ember-contract-test.exe`. Run it under
Wine/Windows for profile path/link refusals, inspection without loading, truncated PE files and malformed exports.

Lab `wv-rustsdk-sl`, 2026-11-07: private-chests built from its source (crates.io `ember-sdk = 0.1.0`) in
`ember/mods/private-chests/`, plus a folder with two DLLs:
```
[ember] kiln-ember 0.1.0 on the server: profile 'sl-test': 0 listed, plus the engine's folder (the profile does not manage this engine); 2 Ember mod(s)
[ember:private-chests] folder: not loaded: Ember has not published its mod ABI yet (ember-sdk 0.1.0 holds documentation only), so no host can start this library safely (...\private_chests.dll; exports: none)
[ember:two-libs] folder: more than one library in the mod folder (Ember allows one)
```
