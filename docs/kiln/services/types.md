# kiln.types: the game's types, systems, events and components by name

The game describes every one of its data types (about 11,000 on the dedicated server, 12,600 in the game): names,
sizes, fields with offsets, base classes, enum values and default values. `kiln.types` reads that description from the
running exe, lists the game's ECS systems (652 on the server, 861 in the game), profiles them, permits two specific
gameplay systems to be disabled, and reads and posts the game's ECS events. On top of it, `Reflected` reads and writes components by
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
- Floats must be finite. Paths and base classes are bounded; a reflected layout cannot authorize access past the
  actual byte length returned by `kiln.world`. Static arrays support checked element paths; strings, dynamic arrays,
  optionals and variants cannot be assigned through this C# scalar setter.
- Installed C# and Lua mods run with the server owner's authority. They have no player identity to authorize;
  commands exposed to players must check that player's permission. `/component` checks admin status before writing.
- Writes skip the game's dirty-marking: some fields only reach players when the server next touches them. Write on
  the server; it is authoritative.

## Systems
```csharp
GameSystems.Search("fall damage");                          // fall_damage_infliction, ...
GameSystems.SetEnabled("stamina_depletion_sprint", false);  // sprinting costs no stamina, in every world
GameSystems.Profile(true);  ...  var top = GameSystems.Stats();   // costliest first: Milliseconds, Calls, World
GameSystems.TurnAllOn();                                   // release this caller's disable requests
```
- Disabling is restricted to `stamina_depletion_sprint` and `fall_damage_infliction`, and refused if a name shares
  an update function with another descriptor. Other systems can be listed and profiled but cannot be disabled.
- Disable requests belong to the calling mod (or the shared developer tools). A system runs again after every owner
  releases its request. Mod retirement/reload releases its requests; the native service also checks revoked owners
  before each server step and disabled runner call. World stop and service unload restore every system. Re-enabling does not roll back
  gameplay changes that already occurred while the system was skipped.
- C# switching and profiler controls require the tick thread; window actions are queued there. Type and system
  queries are available on any thread. Native profiler controls synchronize start/stop and in-flight samples.
- Native SET (a module with SET's markers; another tool's `powrprof.dll` is not SET) disables installation of Kiln's runner hook; type reflection and events remain available.
  If such a module arrives later, Kiln releases every disable request and stops profiling. Its existing runner stays
  passive and its DLL stays mapped until process exit, preserving trampolines the later hook may reference.
  A module inventory that cannot be read is not such a module: Kiln looks again one second later and goes passive
  only when SET is seen, or after five failed looks in a row (the detector kiln-set shares,
  [set.md](../set.md)). Passive for that second reason only, it looks again every 5 s and turns the runner back on
  at the first clean look; disable and hook requests made before it went passive are gone and must be made again,
  and `status()` says which of the two it is.
- A program that kiln.world wraps (dev.28: its update pointer in the world's program table swapped for a trampoline)
  keeps its name: switches and the profiler ask kiln.world's `wrap_original` for the game's update behind the
  trampoline. While native SET is loaded, kiln.world refuses every wrap (SET names programs by that pointer too);
  kiln.types tells it through `external_runner()`.
- `GameSystems.Hook` hooks the update function's code, so it runs for every world of the process that runs the
  program (on a host: the server world and the client world), and it runs inside any wrap of that program.
- The profiler measures CPU time summed over every thread (not frame time). `ms / ProfileSeconds` is ms per second.
- Idle runner cost: two atomic checks per program slice, with no clock reads or metadata lookup. Profiling adds
  clock reads, guarded job reads and synchronization per timed slice; its measurements include that instrumentation.

## Events
```csharp
Context.Tick.Post += (in TickInfo t) => GameEvents.Each("EntityDiedEvent", e => Log.Info($"died {e["deadEntityId"]}"));
GameEvents.Post("TestEvent1", new Dictionary<string, object?> { ["data"] = 7 });  // checked; the game handles it next step
```
`Each` gives copies of every event of the type from the step that just ended (call it in `Tick.Post`). `Post` requires
a registered ECS event, its complete reflected size, a matching world stream, valid scalar/enum data and an inline
layout. Game-owned strings, dynamic arrays and variants are refused. Unknown hashes would crash the distributor.
The C# API initializes unnamed fields to zero, including `timeStamp`; supply a timestamp explicitly when needed.
The Lua adapter fills `timeStamp` from the supported game clock. A full queue returns false. Tick thread.

## Commands (Kiln.DevTools)
| Command | Permission | What |
|---|---|---|
| `/types <words>` | everyone | types whose name holds every word |
| `/type <name>` | everyone | fields with offsets, base class, enum values |
| `/systems`, `/systems find <words>`, `/systems off` | everyone | list systems; `x` marks off ones |
| `/systems top [n]` | everyone | the profiler's costliest systems |
| `/systems profile on\|off`, `/systems allon` | admin | start/stop the profiler; release developer tools' disable requests |
| `/system <name> on\|off` | admin | request/release disabling an allowed system |
| `/component <entity> <Component> [path]` | everyone | read a component (all scalar fields, or one) |
| `/component <entity> <Component> <path> <value>` | admin | checked write |
| `/devtools systems\|types` | everyone | in the game: open the Systems or Types window |
| `/devtools open <window>` | everyone | in the game: toggle `bindings`, `resources`, `hooks`, `log`, `status` or `console` |
| `/devtools bindings\|hooks` | everyone | the same data as text (works on servers) |
| `/devtools status\|log [n]\|resources <type>` | admin | as text: state files, the game's log, a type's resources |

## Windows (in the game)
- **Kiln: Systems**: filter, Off only, a checkbox per system, Profile / Stop profiling, Turn all on, Log top 30, and
  the profiler's ms/s, calls/s and world columns. Open with `/devtools systems` or bind `kiln.devtools.systems` in
  the Kiln menu's Keys tab.
- **Kiln: Types**: search, then a type's fields, base class (click to follow) and enum values. `/devtools types` or
  `kiln.devtools.types`.

The windows below match Shrouded Engine Tweaks' overlay. Each is closed at start; bind `kiln.devtools.<window>` or
use `/devtools open <window>`. A closed window gathers nothing. An open one refreshes a few times a second at most.
Filters take SET's syntax: `a,b` matches either word, `-a` hides lines with `a`.
- **Kiln: Bindings**: every key binding by mod. Red keys clash with another binding. Pick one, then Set, Unbind or
  Default (saved to keybinds.json).
- **Kiln: Resources**: type a resource type (`keen::ItemInfo`) or hash (`0x...`), List, then pick a GUID. The detail
  is raw (size and first bytes) until the reflected view is plugged in (`ResourcesView.Detail`).
- **Kiln: Hooks**: kiln.hooks counts (detour and mid targets, handlers, patches) and calls per second.
- **Kiln: Game Log**: follows `enshrouded.log` (client) or `logs/enshrouded_server.log` (server); warnings yellow,
  errors red, "Hide noise" drops state-machine lines; keeps 5000 lines and draws the newest 1000 that match the
  filter. It reads only the bytes added since the last look (from a 64 KiB tail when it opens) and starts over when
  the file shrinks.
- **Kiln: Status**: each `kiln/state/*.json` file rated OK, `(!)` or ERR (errors, unverified exe, missing signatures,
  failed or faulting mods), with its top-level values.
- **Kiln: Console**: runs a line through `kiln/control/console` (as `kilnctl cmd` does) and shows the output; last
  2000 lines, history buttons.

## C
`kiln_types.h`: `status`, `type_count/type_at/type_by_hash/type_by_name/type_info/type_field/type_enum`,
`component_type_count/at`, `event_type_count/at`, `system_count/system_info/system_find/system_set_enabled/profile`,
`event_each`, `event_post`, `system_release_owner`, `system_hook`, `system_unhook` (appended; check table size).
`external_runner` (since 0.2.0-dev.28): 0 when kiln.types' runner is in charge, 1 when native SET owns the program
runner, 2 when the loaded modules could not be read (runner passive). Type handles are stable for the process.

## How it finds things
- The type table: a static table in the exe's `.rdata`, found by data alone (the `BlobString` and `uint32` descriptors
  sit next to each other at its start; a header `{table, count}` points at it, followed by the ECS component and event
  lists). The loaded image is copied through guarded reads after validating its PE headers and sections. Every
  descriptor hash and every accessed member extent is checked against that immutable snapshot. No code pattern,
  so the table can survive code moves. A missing anchor or rejected table exposes no types or ECS lists.
- Systems: `.rdata` descriptors `{name, length, update function}`; the program runner (sigdb `types/ProgramRunner`)
  is detoured to skip and time them.
- Events: the offsets of the world's event streams and the event map, captured by sigdb (`@types/EventStreams`).
Credit: Shrouded Engine Tweaks (MIT) documented these structures; Kiln's implementation is its own.

## Tests
- Host: `native/services/types/test-types.sh <server exe> <client exe>` (scanner over both exes);
  `test-system-hooks.sh` (hook lifetime, a wrapped program's identity, the external runner's answer).
- Unit: `managed/tests/Kiln.Api.Tests/GameValueCodecTests.cs` (checked writes, fail soft).
- Hostile-input tests: `native/services/types/test-types.sh` also covers truncated headers/tables, malformed fields,
  event maps/page chains/posters, payload validation, system ownership and profiler sessions.
- Commands: `managed/services/Kiln.DevTools/tests` checks read/write parsing and non-admin rejection.
- Lab: `mods/tests/types-test` (PASS/FAIL lines); `mods/tests/systems-test` (its native part: the profiler and a
  system hook on wrapped programs).
