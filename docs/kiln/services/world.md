# kiln.world

The server world's ECS for other services and mods: **programs** (your own ECS system in the game's scheduler),
**entity access** (components, positions, "who has component X"), **tracked writes** (changes to a component that the
game's save and replication see) and **teleport** (the game's own fast-travel mechanism). Only kiln.world hooks the
game's program registration; every other service registers programs through it.

| Layer | File |
|---|---|
| C table | `native/core/abi/services/kiln_world.h` (`kiln.world` v3) |
| Native | `native/services/world/` → `kiln/services/kiln-world.dll` |
| Symbols | `sigdb/symbols/server.d/world.json` (features `world.programs`, `world.queue`, `world.entities`, `world.find`, `world.names*`, `world.teleport`, `world.dead_check`, `world.write`, `world.read_other`, `world.component_bits`); tracked writes also use `items.fuel`'s symbols |
| Managed | `managed/services/Kiln.World/` → `kiln/host/Kiln.World.dll` (`Kiln.World`, `Kiln.Teleport`, `Kiln.EntitySet`) |
| Capability | `world.tracked_writes` (managed, follows the session; [caps.md](caps.md)) |
| Test mods | `mods/tests/world-test/`, `mods/tests/world-writes-test/` (tracked writes, three boots) |
| Evidence | `docs/re/world.md` |

Fails soft: a missing piece turns off only what needs it (logged once at boot: `kiln.world: programs on, entities on,
…`). If neither programs nor entity access work, the service is not published (`api->service` returns NULL).

## Managed (mods)

Reference it like the API: `<ProjectReference Include="…/managed/services/Kiln.World/Kiln.World.csproj"
Private="false" ExcludeAssets="runtime" />`. Everything below runs on the tick thread (mod callbacks, after `await
NextTick()`); other threads get `InvalidOperationException`.

```csharp
using Kiln;

if (!World.Available) return;                       // kiln.world not on this build
var villagers = World.Find("NpcCommon");            // IReadOnlyList<EntityId>, one pass over the entity map
var pos = World.Position(villagers[0]);             // WorldPosition? in metres
var t = World.Transform(villagers[0]);              // EntityTransform? (Position, Rotation, Scale, YawDegrees)
bool canMove = World.Has(villagers[0], "TeleportCount");
World.TryRead<byte>(villagers[0], "TeleportCount", out var counter);   // copy of the first sizeof(T) bytes
EntitySet armed = World.Query(["NpcCommon", "Health"], without: ["PlayerInput"]);   // a snapshot; armed.Refresh() reads again
int n = World.EntityCount;
uint h = World.ComponentHash("CurrentTransform");   // 0x027ac564

TeleportResult r = await Teleport.To(villagers[0], pos!.Value with { X = pos.Value.X + 3 });
if (!r.Moved) Log.Warn(r.Reason);                   // refusals come back as Failed results, not exceptions
await Teleport.To(player, target, new TeleportOptions { FindGround = true, YawDegrees = 90, Checkpoint = true });
await Teleport.ToEntity(player, otherPlayer);       // 1.5 m beside it, ground search on
await Teleport.ToSpawnPoint(player, flameAltar);    // a landing pad of a PlayerSpawnPoint entity
```

- The task completes on the tick thread 2-3 steps after the call (about 80-120 ms on a dedicated server; FindGround
  waits for the destination to stream: up to ~1.3 s for 1 km per EMF). `Status`, `Position`, `Distance`, `Flags`
  (`OffTarget`, `RawTarget`, `TimedOut`), `Steps`, `Reason`.
- Refused (Failed): no such entity, no `CurrentTransform`/`LastTransform`/`TeleportCount` (props, chests, altars), dead
  (`Actor` Dead flag), a teleport already in flight for that entity, a target outside x/z -1000..12000 or y -1000..5000,
  NaN. After 10 s without a move kiln.world removes the stuck `Teleport` component and fails with `TimedOut`.
- When the mod unloads, its pending teleports are cancelled (the move may still happen; the task is cancelled).
- FindGround corrects only about ±10 m vertically; otherwise the entity lands on the raw target (`RawTarget`). Enemies
  moved far from every player are despawned by the game within a second (EMF); villagers are fine.

### Tracked writes
Since 0.2.0-dev.27. `World.TryRead` hands out a copy and `World.WriteRaw` (before: `TryWrite`) stores bytes straight
into component memory. The game is not told about such a store: its save skips the change on an entity it has already
written, and players may never be sent it. A tracked write goes through the game's own write tracking instead, the
path the game's programs use when they change another entity's component.

```csharp
using Kiln;                                    // KilnServices: World;Game

WorldWriteResult r = await World.Update<Ecs.Fuel>(can, (ref Ecs.Fuel f) => f.Values[0] = 3200);   // read, change, write
if (!r.Ok) Log.Warn(r.Reason);                 // "entity 1042 has no Fuel", "... add \"writes\": [\"Fuel\"] ..."
await World.Write(prop, new Ecs.ScaleComponent { uniformScale = 1.5f });                      // the whole component
await World.WriteField(npc, "Health", "dataStorage[0]", 40);     // one field by its path in the game's type (kiln.types)
await World.WriteAt(e, "ServerToggleState", 0, (byte)1);         // one field by offset: the layout is yours to know

WorldWriteInfo info = World.WriteInfo<Ecs.Fuel>();               // CanWrite, GameWriters, SaveTracked, Attribute
using var sub = Events.On<ComponentWrittenEvent>(e => Log.Info($"{e.Component} of {e.Entity} changed"), EventFilter.ForEntity(can));
```

| Call | Writes | Use it for |
|---|---|---|
| `World.Update<T>(entity, (ref T v) => …)` | what the change leaves in the value the entity holds at that moment | part of a component; every attribute (Health, Stamina, Fuel) |
| `World.Write<T>(entity, value)` | the whole component | a component you build from nothing |
| `World.WriteField(entity, component, path, value)` | one scalar field, checked against the game's reflected type | no generated type at hand; a console command |
| `World.WriteAt<T>(entity, component, offset, value)` | bytes at an offset | a field no type names; the caller knows the layout |
| `World.WriteRaw<T>(…)` | bytes at an offset, at once, **untracked** | an input the game reads in the coming step and neither saves nor sends (`PlayerInput`, `ActorInput`) |

- **When.** The write happens in the next sim step, inside Kiln's program `kiln_world_writes`, after the game's own
  programs of that step. The task completes on the tick thread one step later; two steps later for a component the
  game's programs write too, because the answer then says whether the value was still there after they ran again.
- **`writes` in kiln.mod.json.** The game fixes a program's access when the world starts, so kiln.world has to know
  the component types at boot: [`"writes": ["Fuel"]`](../modding/manifest.md#components-the-mod-writes-writes-since-020-dev27).
  Kiln.Sdk adds the types your code passes to `Write<T>` and `Update<T>` when it builds the mod; list by hand what
  you name as a string. A type that is not listed is refused (`Status == NotDeclared`) with the sentence that says
  what to add, and takes effect at the next server start.
- **The answer** (`WorldWriteResult`, an `IKilnResult`): `Ok` is true for `Written` and for `Unchanged` (the
  component already held the value: nothing is written or marked). Everything the game or the entity says no to
  comes back as `Ok == false` with a `Reason`: `NoComponent`, `OutOfRange`, `NotDeclared`, `Refused`, `Faulted`
  (the change threw: nothing is written), `Dropped` (no server world), `LayoutMismatch` (the generated type is not
  this game build's). A none entity throws `ArgumentException`; a capability that is off throws
  `KilnUnavailableException("world.tracked_writes")`.
- **Who else writes it.** `GameWriterCount` and `GameWriters` name the game's own programs that write the type on
  the server, read from the running game's program table. With none, Kiln's write is the only one and the value
  stays. With some, Kiln's write is the last of its step, and from the next step on those programs see it and may
  change it: `Kept` says whether the bytes were still there one step later. `Kept == false` on a value the game
  derives every step (`NetworkFuel` from `Fuel`) means: write the source instead.
- **`SaveTracked`, `ReplicationTracked`.** Whether the game's write tracking records a change of that type for the
  save and for replication: the game's own per-type bits, 40 and 91 of its 687 component types on this build (null
  where a build does not tell). `SaveTracked == true`: the write marked the entity and the next save writes it.
  `false`: no write of that type marks anything, the game's own included, so on an entity a save has already
  written the value is gone after a restart, tracked or not (`ScaleComponent`). `ReplicationTracked == false` does
  not mean players never see the value: the game sends many values through another component it derives from this
  one (`Fuel` through `NetworkFuel`).
- **Attributes** (`Attribute == true`: Health, Stamina, Mana, Fuel, Level…): after the write the game's own formulas
  run, as after its own changes, so `After` may be clamped. The first 8 bytes of an attribute (group, values offset,
  flags, count) are the game's: a write that would change them is `Refused`. Use `Update<T>` and change `Values`.
- **`Update`'s callback** runs inside the game's step, on the tick thread on a dedicated server and maybe on a game
  worker thread in a hosted game. It gets a copy: touch only the value, no Kiln or game API. Its exception becomes
  `Faulted` and is logged for the mod.
- **Events.** `ComponentWrittenEvent` is posted on the tick thread for every write a mod made through these calls
  that changed bytes (not for `Unchanged`, not for a refusal), with the result; it is an `IEntityEvent`, so
  `EventFilter.ForEntity` works. A change the game makes itself raises nothing.
- **Which world, which entity.** A write belongs to the server world that runs when you call: one made before the
  first world exists answers `Dropped`, and one that has not run when a hosted game opens another world answers
  `Dropped` too. A write whose entity is removed while it waits never runs (`NoComponent`, "lost its … while the write
  waited"), also when the id comes back: a player's entity is slot + 1, and the next player in the slot gets it. An id
  you keep from an earlier step can name another entity by now; check before you write.
- **Limits.** 64 entries in a mod's `writes` (a repeated name counts each time), 92 component types for all mods
  together (one program's accessors; a name the world does not have takes no place); 4,096 bytes per write (`Update`
  takes components up to 64 KB); 512 writes run per step, the rest wait for the next steps; 4,096 writes not yet
  answered (waiting, held for the check, or answered and waiting for delivery, refusals included), past that the call
  answers `Dropped` at once; 1,024 answers per step. The server world only.
- `World.WriteInfo("Health")` answers for any component, listed or not, once a world runs: look before you list. It
  reads what kiln.world published for the running world (the game's writers at the world's setup, the save and
  replication bits at its first step), never the live world, so any thread may ask.

### Entity sets
`World.Query(with…)` and `World.Query(with, without)` give an `EntitySet`: the entities that have every component of
one list and none of the other, read at that moment (`Query<T>()`, `Query<T1, T2>()` take generated types). A set is a
snapshot; `Refresh()` reads it again, `Contains`, `Count` and enumeration read the snapshot. The cost is one pass over
the world's entities for the rarest listed component, then one lookup per candidate and other component. Sets need
`world.find` (`EntitySet.Capability`): without kiln.world, or on a build where it cannot walk the entity map, `Query`
and `Refresh` throw `KilnUnavailableException("world.find")` (`World.Find` keeps its own behaviour).

### The admin contract (Kiln.Admin)
| Admin op | kiln.world |
|---|---|
| `teleport {entity, x, y, z, safe}` | `Teleport.To(new EntityId(entity), new(x, y, z), new() { FindGround = safe != false })`; refuse non-players with `World.Has(e, "PlayerInput")` (or kiln.players) |
| teleport to another player | `Teleport.ToEntity(player, other)` (reads the other's `CurrentTransform`) |
| teleport to a marker | `Teleport.To(player, markerPosition, new() { FindGround = true })`; to a flame altar: `ToSpawnPoint` |
| players' positions (`players` op) | `World.Find("PlayerInput")` + `World.Position` (or kiln.players) |

The Lua version writes the `Teleport` component's fields of an entity that has none: that path never adds the
component. kiln.world adds it the way the game does (a kind-3 record from a program).

## Native (other services)

Look the table up in `on_ready` (services load in file-name order, so `kiln.world` may not exist yet in your
`kiln_plugin_main`):

```c
#include "services/kiln_world.h"
static const KilnWorldApi* W;
static void KILN_CALL on_ready(void* user) {
    W = (const KilnWorldApi*)api->service((KilnStr){KILN_WORLD_SERVICE, sizeof KILN_WORLD_SERVICE - 1}, 1);
    if (!W) return;                     /* unavailable on this build: your feature is off */
    /* register programs here (before the game's entry point) */
}
```

### Registering a program

```c
static const KilnStr kRead[] = {{"CurrentTransform", 16}, {"TeleportCount", 13}};
static const KilnStr kWith[] = {{"NpcCommon", 9}};
static KilnHandle g_prog;
static int32_t g_slot_ct;

static void KILN_CALL each(KilnProgramCtx* ctx, uint64_t* it, KilnEntity e, void* user) {
    const uint8_t* ct = (const uint8_t*)it[g_slot_ct];      /* the current entity's CurrentTransform */
    /* read-only work, or queue changes: W->queue_add(ctx, e, hash, &data, size) */
}

KilnProgramDesc d = {0};
d.size = sizeof d;
d.flags = KILN_PROGRAM_HOST_ONLY;
d.name = (KilnStr){"myservice_scan", 14};
d.each = &each;
d.read = (KilnNames){kRead, 2, 0};
d.with = (KilnNames){kWith, 1, 0};   /* filters without data access use template names (tpl map) */
if (W->register_program(owner, &d, &g_prog) == KILN_OK)
    g_slot_ct = W->program_slot(g_prog, KILN_SLOT_READ, (KilnStr){"CurrentTransform", 16});
```

- **When**: `kiln_plugin_main` or `on_ready` only. kiln.world registers everything right after the game's
  `RegisterAllPrograms` (before the world finalizes; ~79 free slots of 896; kiln.world holds 32 at most). Later calls
  return `KILN_ERR_UNAVAILABLE`.
- **Names** are checked against the world's maps at that moment; one unknown component/event → the program is
  `KILN_PROGRAM_REFUSED` (logged: `program X refused: read: unknown component 'Y'`). A singleton that is not registered
  yet only warns; if it is still missing when the world starts, the engine skips the program (`KILN_PROGRAM_SKIPPED`).
  Check `program_info` after the first step if your feature depends on it.
- **Modes**: `each` (kiln.world iterates, calls you per entity; a fault disables the program but the slice is still
  completed) or `update` (raw: you call `iter_begin` / `iter_next` / `current_entity` and must iterate all or none). A
  `KILN_PROGRAM_SYSTEM` program has no entity query: one call per step, entity 0, with singletons, other-entity
  accessors and the command buffer. An entity query with no required list is refused (it would match every entity).
- **Slots**: `program_slot(handle, kind, name)` gives the qword index into the iterator; it is computed from the
  descriptor at registration (the engine orders TPL/READ/WRITE/OTHER by type index = name order; kiln.world verifies
  this against the real type indices and refuses the program if they differ). `program_info.iter_size` is the
  iterator size. kiln.world also compares the engine's group counts with its own on every update.
- **Inside the update** (game worker thread, in parallel with every program you do not conflict with): only touch what
  you declared. `mark_written(ctx, &it[slot])` before writing through a WRITE slot; `read_other(ctx, it[slot], entity,
  &size)` for OTHER_READ; `queue_add` / `queue_remove` (kind 3 / 4 records, applied at the end of the step; an existing
  component keeps its data) or raw `queue_push`. `component`, `transform`, `exists` also work there for data you
  declared. Never create/destroy entities or call `find` from inside an update.
- Limits: tpl 8, read+write 42, singletons 64 per list, other_tpl 9, other 92, events 24, iterator 4096 bytes; a name
  may appear once per access group. events_in slots resolve only when the list has a single event.

### Entities and teleport from native code
Tick thread (Pre/Post) or inside your program: `component(e, hash, &size)`, `exists`, `transform`, `position`,
`find(hash, buf, cap, &count)` (KILN_ERR_BUFFER_TOO_SMALL with the full count when `cap` is short),
`entity_count`. `teleport(owner, &req, &id)` from any thread; the callback runs once, on the tick thread in Pre.
`teleport_cancel(id)` drops the callback. `status(&s)` tells what works.

## Tests (lab)
- `mods/tests/world-test`: waits for the world, checks hashes/find/positions, teleports the first teleportable villager
  3 m and back, a duplicate in flight, FindGround from +3 m, ToEntity next to another villager, and the refusals
  (prop, NaN, unknown entity). `world-test: done, 24 passed, 0 failed` on the copied world.
- Native self-test, labs only: create `<server>/kiln/data/kiln.world/selftest` before start. Registers three programs
  (each-mode, raw CHUNKED, SYSTEM with a singleton and an other-read accessor) and compares every slot with
  `GetComponent`; tests `queue_add`/`queue_remove` on a prop; logs `world selftest: PASS|FAIL …`.
  Also a forced stuck teleport on a prop to exercise the 10 s watchdog (`PASS watchdog`).
- Hot reload: `reload-test world-test 6` with a teleport in flight each time: 0 leaked contexts.
- Idle: no measurable cost in tick-stats.jsonl (step and Pre p50 within noise of a run without kiln-world.dll).
- `mods/tests/world-writes-test` (tracked writes; a lab made with `--save` from a world with a base; start it three
  times, stopping after each `READY restart`): boot 1 the build's table of all 687 component types, every refusal, a
  tracked `Fuel` write with the game's formulas and its mirror program, the event, a value the game derives
  (`NetworkFuel`, `Kept == false`), a type with no game writer; boot 2 a tracked and a raw write of `Fuel`, `UsedItem`
  and `ServerToggleState.state` on twin entities a save has already written, and of `ScaleComponent`; boot 3 reads
  them. Lab `dev27-world`: 27, 9 and 11 PASS, 0 FAIL:
  ```
  PASS Fuel kept the tracked write across the save and restart: 4321 (written 4321)
  PASS the raw Fuel control was lost: the save skipped the untracked write: 1000 (raw 3000, saved before it 1000)
  PASS UsedItem kept the tracked write across the save and restart: 1032 0x8ec15337 (written 0x8ec15337, before 0xf1147751)
  PASS the raw UsedItem control was lost: 1044 0x0cc076a8 (raw 0x8ec15337, saved before it 0x0cc076a8)
  PASS ScaleComponent, which the save does not track, was not kept either way: tracked 1.25 reads 1 (before 1), raw 1.5 reads 1 (before 1)
  PASS ServerToggleState.state kept the tracked write across the save and restart: 1175 0 (written 0, before 1)
  PASS the raw ServerToggleState control was lost: 1177 1 (raw 0, saved before it 1)
  INFO ServerToggleState.lastChange, written tracked with the same component: reads 0 (written 12345, before 0): the game's save record of the type does not carry this field
  ```
  The full log and what it shows about the save: [docs/re/world.md](../../re/world.md#tracked-writes-dev27-server-26430253-exe-f0d824be-client-26430625-exe-5d9ef2f1).
- Offline, in a mod's own unit tests: Kiln.TestKit's fake world (`kiln.Entities`) runs the same `World` calls under
  `dotnet test`, tracked writes with kiln.world's rules included ([../modding/unit-tests.md](../modding/unit-tests.md)).
- Host tests without a game: `KILN_OUT=… native/services/world/tests/run.sh` (boot declarations, program boundaries,
  tracked writes: 26 + 26 + 174 checks, under ASan: the queue's limits, world identity and the watch over waiting
  requests' entities, the delivery that checks each callback right before it runs, the watchdog, the catalog under
  concurrent readers, a consumer compiled against the dev.26 header on the v3 table, and the cases of
  `tests/parity.json` and `tests/abi.json`); `managed/tests/Kiln.World.Tests` (33 tests: the native mirrors field for
  field against `abi.json`, Kiln.Api's v2 table and Kiln.World's v1 table on a v3 and on a v2 provider, the exception
  contract of the dev.27 members, results, the capability row, the SDK's `writes` fill on a fixture mod, Kiln.Game's
  forwards of the moved marker interfaces). `parity.json` is also read by `Kiln.Host.Tests` (the manifest reader) and
  `Kiln.TestKit.Tests` (the fake world), so the three agree on every boundary in it.

## Client checklist (needs a joined player)
- `Teleport.To(player, …)` snaps the client once (no rubber-band), `TeleportCount`/`RenderTeleportCount` change, and
  the game logs `Player EntityId N teleported From … to …`.
- FindGround / Checkpoint on a 1 km move: no fall, the destination streams in; `Replication Checkpoint … Set/Reached`
  in the game log with Checkpoint.
- A dead player (respawn screen) is refused with "the entity is dead".
- `ToEntity(player, otherPlayer)` lands next to the other player; `ToSpawnPoint(player, altar)` on a landing pad.
- Tracked writes (guided checks of `mods/tests/world-writes-test`, since 0.2.0-dev.27; a lab has no screen):
  `world.tracked-water`: a can in your backpack is set from full to half by `World.Update<Ecs.Fuel>` while you look
  at it, and shows half without your touching it; `world.tracked-water-rejoin`: a can set to 23 of 100 shows 23
  before you leave to the menu and after you come back. Until these pass, "players see a tracked write" is unproven.


## Tracked writes for native services (v3)
The table's last five entries, appended in v3 (check `header.size` and `capabilities() & KILN_WORLD_CAP_TRACKED_WRITES`):
`write_submit(owner, &request, &ticket)` queues one write (bytes at an offset, or a `change` callback that edits a copy
of the component inside the step) and answers through `callback` in a later Pre; `write_cancel(ticket)`;
`write_info(mod_id, hash, &info)`; `write_status(&status)`; `write_declare(owner, who, names, count)` lists a native
service's own component types (from `kiln_plugin_main` or `on_ready`: the write program is made when the first server
world registers its programs). A mod's types come from `"writes"` in its kiln.mod.json, read at boot next to
`kiln.programs.json`; a name the world does not know is left out of the program alone and takes none of its 92
places (the first server world decides this, before the program is made). A request remembers the server world it
was made in (one of an earlier world is dropped when the next registers) and is watched while it waits: if its entity
lacks the component at an update, it never runs. Before each result callback kiln.world asks again whether the
request is still wanted and its owner alive, so a callback that cancels another request or unloads another owner
stops that one's delivery. `KILN_ERR_BUFFER_TOO_SMALL` counts every request not delivered yet, refusals included.
The program
(`kiln_world_writes`, SYSTEM | HOST_ONLY, the listed types as `other_write`, `impact::AttributeRegistry` as an optional
read singleton) exists only when something is listed: a server whose mods list nothing gets no extra program.
kiln.items keeps its own program for water and moves; nothing was moved onto this one.

## Programs v2

[Managed ECS programs and system hooks](../modding/ecs-programs.md) describes boot-time declarations and normal
`OnLoad` callback binding. Native consumers can append-check `write_other`, `unregister_program`, `bind_program`,
`capabilities`, `program_events` and `program_emit` in the v2 table. Existing v1 fields keep their positions.
Other-write accessors use the engine's tracker and write flag. Event inputs resolve in event type-index order,
independent of descriptor list order. Unknown access names refuse a program before engine registration.
