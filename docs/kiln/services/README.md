# Writing a Kiln service

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
| [gamedata.md](gamedata.md) | kiln.gamedata: spells, abilities, creatures and their attacks as data |
| [gameplay.md](gameplay.md) | kiln.gameplay: player/world interaction events and protection zones |
| [hooks.md](hooks.md) | kiln.hooks v2: one registry for every hook |
| [items.md](items.md) | kiln.items: items in inventories |
| [knowledge.md](knowledge.md) | kiln.knowledge: the game's knowledge, the vanilla journal, dialogue events, the client veto |
| [npcs.md](npcs.md) | Friendly NPCs and quests in the game's own UI |
| [pacing.md](pacing.md) | kiln.pacing: where the game waits, and its task pools |
| [player-state.md](player-state.md) | Player state and actions |
| [playeranim.md](playeranim.md) | kiln.playeranim / PlayerAnimStates: mods' states in the player's animation graph, merged at boot, the same on the server and every client |
| [players.md](players.md) | kiln.players: who is on the server |
| [progression.md](progression.md) | kiln.progression: levels, experience, skill tree, skill points |
| [quests.md](quests.md) | Kiln.Quests: the quest engine behind the game's own quest UI |
| [rules.md](rules.md) | Kiln.Rules: what players may do, and what it costs them |
| [saves.md](saves.md) | kiln.saves: save events and persistent mod data |
| [screen.md](screen.md) | kiln.screen / ScreenEffects: full-screen effects |
| [settings.md](settings.md) | kiln.settings: the server's game settings, live |
| [skills.md](skills.md) | kiln.skills: the game's skill tree for mods, and Kiln talent trees |
| [spawn.md](spawn.md) | kiln.spawn: create and destroy entities from templates (creatures, item drops, props) |
| [steamguard.md](steamguard.md) | kiln.steamguard / SteamGuard: the achievement guard (game client only, experimental) |
| [surfaces.md](surfaces.md) | kiln.surfaces: building surfaces |
| [time.md](time.md) | kiln.time: time of day and weather |
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
| [creature-control.md](creature-control.md) | kiln.creature_control / CreatureControl: the one broker that owns a controlled creature body's AI-off layer and SteeringAgent; leases for pets, mounts, vehicles and NPC brains |

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

## Managed rules
- Public API as static classes in namespace `Kiln` (e.g. `Kiln.Teleport`), no pointers in public signatures.
- `Services.Get<T>(name, version)` gives the native table (null = unavailable: throw `KilnUnavailableException`
  naming the capability, `ServiceCaps.Unavailable("kiln.<svc>", ...)`; expose `Available`).
  `Services.RequireTickThread(api)` for game-state calls.
- The contract ([../modding/contract.md](../modding/contract.md)), as `Kiln.Example` and `Kiln.Spawn` show it: rows
  for what can be off beyond the table itself (`ServiceCapRows` with `ServiceCaps.Static` / `Session`, reported at the
  service's start when the service has rows of its own: a startup type in `ServiceTick.AutoStart` whose static
  constructor calls `Rows.Ensure()`, like `GameplayStartup`; a row says "available" exactly when the call works, so a
  hook that can be off is part of it),
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
