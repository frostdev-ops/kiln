# Writing a Kiln service

No capability ships held by the client gate in 0.2.0-dev.29. Open rows still depend on their services, signatures and side; the [guided checks](../client-pass.md) remain the record of real-game behavior not yet confirmed.


A service is one capability (teleport, items, chat, ...) with three layers. Which services and features a build has
available is generated from a boot's `capabilities.json`: [../status.generated.md](../status.generated.md). The template is `example`:

| Layer | Where | Template |
|---|---|---|
| Symbols | `sigdb/symbols/server.d/<svc>.json` (merged with `server.json` at boot) | `sigdb/symbols/server.json` |
| C table | `native/core/abi/services/kiln_<svc>.h` | `kiln_example.h` |
| Native | `native/services/<svc>/` → `kiln/services/kiln-<svc>.dll` | `native/services/example/` |
| Managed | `managed/services/Kiln.<Svc>/` → `kiln/host/Kiln.<Svc>.dll`, shared by all mods | `managed/services/Kiln.Example/` |
| Test mod | `mods/tests/<svc>-test/` (prints `PASS`/`FAIL` lines) | `mods/samples/Hello/` |

## Service pages
| Page | What |
|---|---|
| [abilities.md](abilities.md) | kiln.abilities: scripted abilities, per-cast tweaks of the game's spells, Kiln natives for Impact programs, action sequences |
| [assets.md](assets.md) | kiln.assets: mod resources, overrides, icons and retextures |
| [audio.md](audio.md) | kiln.audio / Kiln.Audio: sounds, music, voice-over and narration |
| [build.md](build.md) | kiln.build: world edits the game saves and replicates |
| [caps.md](caps.md) | kiln.caps / Capabilities: what works here, and why not (in-core; every service has a row) |
| [character-data.md](character-data.md) | Character data: per-character mod data, and what travels with a character |
| [chat.md](chat.md) | kiln.chat: chat lines and commands |
| [combat.md](combat.md) | kiln.combat: health, damage, healing, kills, god mode and combat events |
| [content.md](content.md) | kiln.content: registries and data packs |
| [creatures.md](creatures.md) | kiln.creatures: one creature's values, variants, AI events, commands and arsenal rules |
| [engine.md](engine.md) | kiln.engine: which exact game build runs (`identity`, `gate_stats`) against per-build engine profiles (`sigdb/engine/`); the Kiln-internal kiln.internal.engine table (engine reads and the engine's own write gate) for Kiln's own services |
| [gamedata.md](gamedata.md) | kiln.gamedata: spells, abilities, creatures and their attacks as data |
| [gems.md](gems.md) | Gems: how a gem works, changing one with a data pack, the `Gems` read API and gem events; with [gems-new.md](gems-new.md) (new gems, `Gems.Register`), [gems-forge.md](gems-forge.md) (forge actions from code) and [gems-sockets.md](gems-sockets.md) (sockets on rings, armour, shields) |
| [gameplay.md](gameplay.md) | kiln.gameplay: player/world interaction events and protection zones |
| [hooks.md](hooks.md) | kiln.hooks v2: one registry for every hook |
| [item-costs.md](item-costs.md) | kiln.item_costs: exact costs from a selected slot, with receipts (since 0.2.0-dev.32) |
| [items.md](items.md) | kiln.items: items in inventories |
| [knowledge.md](knowledge.md) | kiln.knowledge: the game's knowledge, the vanilla journal, dialogue events, the client veto |
| [client-building-input.md](client-building-input.md) | kiln.client / BuildingInput: the local player's building input (select, place, remove, undo, dismantle) on game client 26430625, hooked at the first request (since 0.2.0-dev.30.3) |
| [markers.md](markers.md) | kiln.markers / Kiln.Markers: markers on the game's map, Kiln's compass strip and world labels, following an entity, server markers to players with Kiln |
| [net-steam-messages.md](net-steam-messages.md) | kiln.net / SteamMessages: Steam peer-to-peer messages on leased Steam channels, each lease its owner's (since 0.2.0-dev.30.3; C# mods opt in with `"steamMessages": true`) |
| [npcs.md](npcs.md) | Friendly NPCs and quests in the game's own UI |
| [npc-speech.md](npc-speech.md) | kiln.npcspeech / Kiln.Npcs: a mod's lines in the game's speech bubble over a friendly NPC (`Npcs.Say`, `Npcs.Speech` Suppress / Replace), host-decided, shown by Kiln clients (since 0.2.0-dev.31.16, mod API 0.2.23) |
| [survivors.md](survivors.md) | kiln.survivors: recruit roster survivors, give their summon tool, summon them into a base, assign beds (since 0.2.0-dev.31) |
| [pacing.md](pacing.md) | kiln.pacing: where the game waits, and its task pools |
| [player-state.md](player-state.md) | Player state and actions |
| [playeranim.md](playeranim.md) | kiln.playeranim / PlayerAnimStates: mods' states in the player's animation graph, merged at boot, the same on the server and every client |
| [players.md](players.md) | kiln.players: who is on the server |
| [progression.md](progression.md) | kiln.progression: levels, experience, skill tree, skill points |
| [quests.md](quests.md) | Kiln.Quests: the quest engine behind the game's own quest UI |
| [replication.md](replication.md) | kiln.repl / Kiln.Replication: the network inspector: what the server sends each player (counters, watches on one entity, captures), read only |
| [registry.md](registry.md) | kiln.registry / kiln.registry.live: Kiln-owned data fields, staged edits and explicit live changes with compare-before-write/revert |
| [rules.md](rules.md) | Kiln.Rules: what players may do, and what it costs them |
| [saves.md](saves.md) | kiln.saves: save events and persistent mod data |
| [screen.md](screen.md) | kiln.screen / ScreenEffects: full-screen effects |
| [settings.md](settings.md) | kiln.settings: the server's game settings, live |
| [skills.md](skills.md) | kiln.skills: the game's skill tree for mods, and Kiln talent trees |
| [spawn.md](spawn.md) | kiln.spawn: create and destroy entities from templates (creatures, item drops, props) |
| [steamguard.md](steamguard.md) | kiln.steamguard / SteamGuard: the achievement guard (game client only, experimental) |
| [surfaces.md](surfaces.md) | kiln.surfaces: building surfaces |
| [tasks.md](tasks.md) | kiln.tasks: Kiln-owned worker threads for off-tick work (`submit`/`cancel`/`status`/`release`/`drain_owner`), states, quiescence, AFTER_STEP sessions, exit and the `tasks.pool` capability |
| [terrain-tool-carving.md](terrain-tool-carving.md) | Terrain-tool removal: per-tool material filters, terrain-only carving and native mining yields (since 0.2.0-dev.30.5) |
| [time.md](time.md) | kiln.time: time of day and weather |
| [tool-actions.md](tool-actions.md) | kiln.tools: declarative terrain actions, native authority checks, capabilities and resident lifecycle (since 0.2.0-dev.30.5) |
| [tool-payment.md](tool-payment.md) | Terrain-tool placement: pool-item costs, normal building supplies and exact native undo refunds (since 0.2.0-dev.30.5) |
| [tools.md](tools.md) | Building tools: typed tool/brush/pool blocks, owned material observations and presentation qualification limits (since 0.2.0-dev.30.5) |
| [types.md](types.md) | kiln.types: the game's types, systems, events and components by name |
| [vfx-cook.md](vfx-cook.md) | VFX blocks: the builder |
| [vfx.md](vfx.md) | kiln.vfx / Kiln.Vfx: play any VFX from code |
| [world-progress.md](world-progress.md) | World progress: the world's flame, difficulty, spires, shrines, bosses, chests and a character's quests |
| [world.md](world.md) | kiln.world: ECS programs, entity and component access, teleport |
| [worldtables.md](worldtables.md) | kiln.worldtables: the game's shared building-material tables |

### Service pages added in dev.28
Each dev.28 package adds the page of its service here (sorted by page name); a service that existed before keeps its
row above and grows its own page.

| Page | What |
|---|---|
| [../ui.md](../ui.md#values-in-the-games-own-ui-dev28) | kiln.ui.values / `Ui.Values` (in-core, client): values in the game's own UI, tooltip lines in Kiln's Item details window, the slots of each UI frame; the player-UI stage for 09 |
| [queries.md](queries.md) | `Kiln.Queries`: voxel rays, ground below a point, voxel line of sight, entity origins in a sphere or box; the shared ray budget |
| [terrain.md](terrain.md) | `Kiln.Terrain`: bounded terrain overlays (a vein, a mound, a tunnel) applied once per world with a ledger of owned blocks; managed only, over kiln.build |
| [boot.md](boot.md) | kiln.boot (in-core): `Mod.OnBoot`'s one generation per process; the table, the collector contract (bind, prepare, destroy), `activation_fault`, exit code 0x4B01, `state/boot.json` |
| [creature-control.md](creature-control.md) | kiln.creature_control / CreatureControl: the one broker that owns a controlled creature body's AI-off layer and SteeringAgent; leases for pets, mounts, vehicles and NPC brains |
| [pets.md](pets.md) | Kiln.Pets / kiln.pets (dev.32): the pet framework: owned records, bodies through the broker, the native guard and target rule, revive with cost receipts, the owner's view and bar; every behaviour choice is the mod's |
| [systems.md](systems.md) | `Kiln.Systems` / kiln.world v4: Before, After and Replace on the game's own ECS programs, per slice or per entity; entity views, write events, attribute formulas |

### Service pages added in dev.29

| Page | What |
|---|---|
| [decisions.md](decisions.md) | kiln.decisions / kiln.window_clock: refusing a death (`EntityDyingEvent`), a walk-over pickup (`ItemPickingUpEvent`) and an equipment input (`PlayerEquippingEvent`), each ending in one `DecisionCompletedEvent`; the native death guard and the native adapters' verdict route |
| [stations.md](stations.md) | kiln.stations / `Stations`, `Bases` (Kiln.Blocks): the production stations of a base, its Magic chests, and the game's own fill from chests, stop, resume and select for code; station events |

### Service pages added in dev.31

| Page | What |
|---|---|
| [camera.md](camera.md) | kiln.camera / `Kiln.Camera`: maintained stock-mode locks, render-only paths, follow profiles, lens and shake, final visual snapshots and projection helpers (since mod API 0.2.17) |
| [client-crosshair.md](client-crosshair.md) | kiln.client.crosshair / `Crosshair` (Kiln.Client): a read-only copy of the game client's own crosshair hit (hit or miss, position, normal, distance, entity, voxel material) with session, step, sequence and age; any thread (since mod API 0.2.22) |
| [mod-values.md](mod-values.md) | `Kiln.ModValues` / kiln.mod-values: mods' own per-character counters, currencies and experience with levels; server ledger saved with the character, the owner's synced copy, carried progress |
| [routines.md](routines.md) | kiln.routines / `Routines`, `RoutineBlock` (Kiln.Blocks): villager day plans (windows of the game day with the game's own idle, roam, sit, inspect and sleep tasks), keep-awake, manual commands that suspend a plan until `Resume`, assignments saved with the world |

## Native rules
- `kiln_plugin_main(api, owner, info)`: check `api->abi_version == KILN_ABI_VERSION`, resolve symbols with
  `api->symbol` / constants with `api->constant` / features with `api->feature`, install hooks through `kiln.hooks`
  (below), subscribe ticks, then `api->register_service(owner, name, &table)`. safetyhook is still linked into every
  service: mid handlers take its `safetyhook::Context&`, and hooks on system DLL exports (outside the game exe) use it
  directly.
  Missing pieces: return `KILN_ERR_UNAVAILABLE` (or publish a table whose functions return it). Never crash.
- The server world and its singletons: `api->server_world()` (0 before the first server-world step) and
  `api->singleton(world, "ServerPlayers")` (exact registered name; tick thread). Use these instead of hooking
  RegisterSingleton yourself (core feature `singletons`, layout from docs/re/players.md).
- Hooks: place them through `kiln.hooks` v2 with `native/core/abi/kiln_hook.hpp` (`kiln::RawHook`, `kiln::MidHook2`),
  not with your own safetyhook calls. One registry owns one detour per game address, orders the links, gives you the
  game's call site with `caller()` (use it instead of `_ReturnAddress()`), and lists every hook in
  `state/hooks.json`. See [hooks.md](hooks.md).
- Other services: look them up in `info->on_ready` (`api->service("kiln.world", 1)`), not in `kiln_plugin_main`.
  `kiln.internal.*` tables resolve only for Kiln's own services ([Kiln-internal tables](#kiln-internal-tables)).
- Capabilities: the core gives your service a row (`kiln.<svc>`) and one per sigdb feature. Report what else it can or
  cannot do through `kiln.caps` (`<svc>.<thing>`; [caps.md](caps.md)), and a session-dependent row as pending before
  the mods load.
- Threads: game state only on the tick thread, in Pre (no world jobs running) or Post (after `WaitWorldJobs`), or
  inside an ECS program update with declared access. Calls from managed mods arrive on the tick thread.
- Tables only grow at the end; bump the version for additions. Strings are UTF-8 `KilnStr`. Entities are
  `KilnEntity` (u32). Positions `KilnVec3` in metres (engine: 32.32 fixed, `round(m * 2^32)`).
- Names you define in sigdb are prefixed with your service: symbols `items/InventoryCommit`, constants
  `items/tx.size`. Unprefixed names belong to the core file (`SimStep`, `world.storage`, `GetEntityTransform`, ...)
  and may be used, never redefined.

## Kiln-internal tables

A service name that starts with `kiln.internal.` is Kiln-internal:

- only an owner the core created for a DLL loaded from `kiln/services` **of this very build** may publish one: every DLL
  built with `kiln_service`/`kiln_plugin` exports `kiln_build_stamp()` (`native/cmake/kiln_build_stamp.cpp`, the
  version plus the build metadata), and the core gives internal access only when it equals its own build identity. The
  build metadata comes from `native/cmake/kiln_build_stamp.sh`, which CMake runs at configure time and before every
  build of a stamped target (`native/build.sh` builds through it): `+<yyyymmdd>.<commit>` in a git work tree, or
  `+nogit.<nonce>` in a source export without git (one random nonce per build directory, shared by that build's core
  and DLLs). A core without build metadata grants no DLL internal access (a matching version alone is not a build). A
  stale or foreign DLL in `kiln/services`, or any DLL under such a core, loads as before, without internal access, with
  a warning in kiln.log and the row `kiln.<svc>.internal` unavailable (`service-older`) saying why;
- `KilnApi.register_service` publishes it and `KilnApi.service` resolves it only through the `KilnApi` the core hands to
  those DLLs (whose `register_service` also checks the owner). Plugins, managed mods, early plugins and foreign DLLs get
  the other `KilnApi`: its `register_service` refuses every `kiln.internal.*` name with `KILN_ERR_INVALID_ARGUMENT` and
  a warning, whatever owner it is given (owner IDs are guessable), and its `service` answers NULL for one;
- its header lives with its owner (a service's `native/services/<svc>/include/`, or the core's `native/core/src/`),
  never in `native/core/abi`, `kiln-abi` or the SDK, and it may change between Kiln builds: consumers check
  `header.version` for an exact match and `header.size` for each member they call, and on a mismatch log it and report
  their own row unavailable with that reason;
- Kiln-internal tables get no public capability row and do not appear in `capabilities.json`.

Two tables are the core's own and named for one consumer each: `kiln.internal.tasks.host`, the managed host's task lease
table ([tasks.md](tasks.md)), and `kiln.internal.mod-values.backend`, through which Kiln.ModValues binds the native
facade's backend once ([mod-values.md](mod-values.md); `native/core/src/mod_values_host.h`, taken only at exactly its
version and size). Nobody may publish those names, and only the `KilnApi` the core hands to the managed host resolves
them. Owners the managed host creates through that `KilnApi` are marked as the host's: kiln.mod-values gives an owner
named `mod:<manifest id>` that id as its namespace only then. Two more are the core's own and resolve through the
services' `KilnApi` only, and nobody may publish them either: `kiln.internal.launch` ([launch.md](launch.md)) and
`kiln.internal.hooks`, the code ranges Kiln's services protect for their engine calls
([hooks.md](hooks.md#protected-code-ranges-kilninternalhooks-kiln-internal)).
One more is the core's own and resolves through the services' `KilnApi` and the managed host's `KilnApi`, and nobody may
publish it: `kiln.internal.owners` (`native/core/src/owners_internal.h`), what the managed host admitted an owner to. Kiln.Host
grants the owner of a C# mod whose kiln.mod.json says `"steamMessages": true`, and kiln.net's `steam_open` admits it
([net-steam-messages.md](net-steam-messages.md#who-opens-a-lease)); the grant goes with the owner. Since dev.31 the same table (v2, appended at offset 24) also has
`owner_id(owner, buf, cap, out_len)`: an owner's id as the core keeps it (`mod:<manifest id>` for a Kiln mod's owner from
Kiln.Host, a native mod's or a plugin's own id); kiln.registry resolves it with min_version 2 and binds a caller to its mod
with it (the id without its kind prefix, lower case). An owner that is not alive is `KILN_ERR_REVOKED`, a buffer too small
`KILN_ERR_BUFFER_TOO_SMALL` with the needed length.

One table is a service's and has one consumer, the managed host: `kiln.internal.registry`
(`native/services/content/include/kiln_registry_host.h`), published by kiln-content.dll. Kiln.Host calls
`set_order` with its runtime load order (`[{"mod", "version"}]`, the order players see in the launcher profile; optional
`"registry": true` and, since 0.2.0-dev.31.17, `"side": "server" | "client"` for a one-side mod, whose boot fields the join's
registry fingerprint leaves out; an older kiln-content.dll ignores both) before the
boot phase, and `set_mode` when kiln.json says `"registry": {"stage": "onBoot"}` (`managed/Kiln.Host/RegistryHost.cs`). The
registry stage runs every mod's registry edits in that order; without the table (an older kiln-content.dll) it runs in the mod
folders' order and registry.json says `"orderSource": "folders"`. Kiln.Host checks `header.version == 1` and
`header.size == 24` and logs a mismatch. Besides the core's own two, it is the one internal name the managed host's
`KilnApi` resolves, and only the published table of a Kiln service owner.

This is a reach rule, not a sandbox: it keeps tables that hand out game addresses or Kiln's own lock primitives away from
plugins and from stale or foreign service DLLs. It is no defence against arbitrary native code already in the process,
which can read memory anyway.

```sh
KILN_OUT=/path/to/build native/core/tests/test-service-access.sh   # the kiln.internal.* rule (real api.cpp block, stamped and without build metadata)
KILN_OUT=/path/to/build native/core/tests/test-build-stamp.sh      # the build metadata script (git, overrides, no git, nested export)
```

## Managed rules
- Public API as static classes in namespace `Kiln` (e.g. `Kiln.Teleport`), no pointers in public signatures.
- `Services.Get<T>(name, version)` gives the native table (null = unavailable: throw `KilnUnavailableException`
  naming the capability, `ServiceCaps.Unavailable("kiln.<svc>", ...)`; expose `Available`).
  `Services.RequireTickThread(api)` for game-state calls.
- The contract ([../modding/contract.md](../modding/contract.md)), as `Kiln.Example` and `Kiln.Spawn` show it: rows
  for what can be off beyond the table itself (`ServiceCapRows` with `ServiceCaps.Static` / `Session`, reported at the
  service's start when the service has rows of its own: a startup type in `ServiceTick.AutoStart` whose static
  constructor calls its rows' `ServiceCapRows.Ensure()`, like `GameplayStartup`; a row says "available" exactly when the
  call works, so a hook that can be off is part of it),
  `[RequiresCapability]` on the members that throw for one, `[ServerOnly]` / `[ClientOnly]` and
  `[TickThreadOnly]` / `[AnyThread]` on every member that has a side or a thread, the game's refusal as a result
  record that implements `IKilnResult`, a bus event for every write that changed something (posted through
  `ChangeNotices`, so a handler that writes again cannot loop), and a name of its own for a new shape rather than an
  overload of a released method. `managed/tests/Kiln.Contract.Tests` checks the rules that can be checked by
  reflection and the service's start on fresh assemblies: add the service to `Adopted.cs`.
- Async work: return `Task`/`Task<T>` completed on the tick thread. Anything that holds a mod's delegates or
  continuations must be dropped when that mod unloads: get the caller with `ModContext.CurrentMod` and subscribe to its
  `Retiring` event.
- Mods reference service assemblies with `Private="false" ExcludeAssets="runtime"`.

## Build, deploy, test
```sh
export KILN_OUT=<build dir>          # your own build dir
native/build.sh                                  # all native targets incl. your service
tools/lab/lab.sh new <lab> <port> [--save <savegame dir>]   # once
tools/deploy-server.sh ~/srv/enshrouded/labs/<lab> --policy any --samples  # builds host, services, mods in $KILN_OUT
tools/lab/lab.sh start <lab>; less ~/srv/enshrouded/labs/<lab>/kiln/logs/kiln.log; tools/lab/lab.sh stop <lab>
```
Test mods: `dotnet build mods/tests/<svc>-test -c Release -o $KILN_OUT/mods/<id> --artifacts-path $KILN_OUT/dotnet`;
the deploy copies every mod folder under `$KILN_OUT/mods/`. Offline symbol check:
`python3 tools/sigport/sigdb_check.py sigdb/symbols/server.json <exe>` (merge your `server.d` file into a temp copy, or
extend the tool to read `server.d`).
