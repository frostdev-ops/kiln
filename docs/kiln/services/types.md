# kiln.types: the game's types, systems, events and components by name

The game describes every one of its data types (about 11,000 on the dedicated server, 12,600 in the game): names,
sizes, fields with offsets, base classes, enum values and default values. `kiln.types` reads that description from the
running exe, lists the game's ECS systems (652 on the server, 861 in the game) and lets you switch them off and
profile them, and reads and posts the game's ECS events. On top of it, `Reflected` reads and writes any component by
name with checked writes.

Native: `native/services/types` (`native/core/abi/services/kiln_types.h`), sigdb `sigdb/symbols/server.d/types.json`.
Managed: `managed/Kiln.Api/Services/GameTypes.cs` (namespace `Kiln`, no extra reference), commands and windows in
`managed/services/Kiln.DevTools`. Evidence: [docs/re/shrouded-engine-tweaks.md](../../re/shrouded-engine-tweaks.md).

## Types
```csharp
var health = GameTypes.Find("Health");            // keen::ecs::Health (a short name that is unique, or the full name)
foreach (var f in health!.AllFields) Log.Info($"+0x{f.Offset:x} {f.Name}: {f.Type.QualifiedName}");
var rarity = GameTypes.Find("ItemRarity")!.EnumValues;     // (Common, 0) ... (Legendary, 4)
var defaults = GameTypes.Find("keen::ecs::GameSettings")?.Defaults;   // the game's default value, or null
foreach (var t in GameTypes.Search("player input")) ...    // every word, any order and case
GameTypes.Components; GameTypes.Events;                    // the game's ECS component and event types
```
Any thread. Fail soft: `GameTypes.Available` is false and every query returns nothing when the table is not found;
`GameTypes.Status` says why.

## Components by name, checked writes
```csharp
foreach (var e in Reflected.All("Health"))                 // loaded entities that have it (tick thread)
{
    var h = Reflected.Component(e, "Health")!;              // holds no pointer: every access looks it up again
    long hp = h.Get<long>("dataStorage[0]"), max = h.Get<long>("dataStorage[2]");
    h["dataStorage[0]"] = max;                              // checked: must fit the field
}
Reflected.Component(npc, "NpcCommon")?.Describe();         // every scalar field with its value
```
- Paths: `field`, `a.b.c`, `array[3]`, base-class fields included.
- Writes are checked against the field's type: in range, a whole number for integers, `true`/`false`/0/1 for bools,
  an enum value by number or by name (any case). A value that does not fit throws `ArgumentException` and nothing
  is written. Nested structs are read as copies; set their fields one by one.
- Writes skip the game's dirty-marking: some fields only reach players when the server next touches them. Write on
  the server; it is authoritative.

## Systems
```csharp
GameSystems.Search("fall damage");                          // fall_damage_infliction, ...
GameSystems.SetEnabled("stamina_depletion_sprint", false);  // sprinting costs no stamina, in every world
GameSystems.Profile(true);  ...  var top = GameSystems.Stats();   // costliest first: Milliseconds, Calls, World
GameSystems.TurnAllOn();
```
- Off means skipped entirely, for every player and entity, until switched on again or the process ends. Some systems
  are needed (network, colliders, `*_init`): switching them off can break the world.
- The profiler measures CPU time summed over every thread (not frame time). `ms / ProfileSeconds` is ms per second.
- Cost: one atomic load per program slice while nothing is off and the profiler is idle.

## Events
```csharp
Context.Tick.Post += (in TickInfo t) => GameEvents.Each("EntityDiedEvent", e => Log.Info($"died {e["deadEntityId"]}"));
GameEvents.Post("TestEvent1", new Dictionary<string, object?> { ["data"] = 7 });  // checked; the game handles it next step
```
`Each` gives copies of every event of the type from the step that just ended (call it in `Tick.Post`). `Post` refuses
a type the world has no stream for (an unknown hash would crash the game's event distributor) and returns false when
the queue is full this step. Tick thread.

## Commands (Kiln.DevTools)
| Command | Permission | What |
|---|---|---|
| `/types <words>` | everyone | types whose name holds every word |
| `/type <name>` | everyone | fields with offsets, base class, enum values |
| `/systems`, `/systems find <words>`, `/systems off` | everyone | list systems; `x` marks off ones |
| `/systems top [n]` | everyone | the profiler's costliest systems |
| `/systems profile on\|off`, `/systems allon` | admin | start/stop the profiler; switch everything back on |
| `/system <name> on\|off` | admin | switch one system |
| `/component <entity> <Component> [path]` | everyone | read a component (all scalar fields, or one) |
| `/component <entity> <Component> <path> <value>` | admin | checked write |
| `/devtools systems\|types` | everyone | in the game: open the Systems or Types window |

## Windows (in the game)
- **Kiln: Systems**: filter, Off only, a checkbox per system, Profile / Stop profiling, Turn all on, Log top 30, and
  the profiler's ms/s, calls/s and world columns. Open with `/devtools systems` or bind `kiln.devtools.systems` in
  the Kiln menu's Keys tab.
- **Kiln: Types**: search, then a type's fields, base class (click to follow) and enum values. `/devtools types` or
  `kiln.devtools.types`.

## C
`kiln_types.h`: `status`, `type_count/type_at/type_by_hash/type_by_name/type_info/type_field/type_enum`,
`component_type_count/at`, `event_type_count/at`, `system_count/system_info/system_find/system_set_enabled/profile`,
`event_each`, `event_post`. Type handles are stable for the process.

## How it finds things
- The type table: a static table in the exe's `.rdata`, found by data alone (the `BlobString` and `uint32` descriptors
  sit next to each other at its start; a header `{table, count}` points at it, followed by the ECS component and event
  lists). Every entry is checked (its hash must be FNV-1a of its qualified name). No code pattern, so it survives
  code moves.
- Systems: `.rdata` descriptors `{name, length, update function}`; the program runner (sigdb `types/ProgramRunner`)
  is detoured to skip and time them.
- Events: the offsets of the world's event streams and the event map, captured by sigdb (`@types/EventStreams`).
Credit: Shrouded Engine Tweaks (MIT) documented these structures; Kiln's implementation is its own.

## Tests
- Host: `native/services/types/test-types.sh <server exe> <client exe>` (scanner over both exes).
- Unit: `managed/tests/Kiln.Api.Tests/GameValueCodecTests.cs` (checked writes, fail soft).
- Lab: `mods/tests/types-test` (PASS/FAIL lines).
