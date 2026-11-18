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
- **A water refill writes over `Fuel`.** A refill policy of kiln.items (`Items.Fuel.Refill`, `Items.Fuel.KeepFull`,
  or a player's `Water` rule: the Creative menu's rule set has one) puts every container it covers back to its level
  every step, after the game's programs, so a tracked write of such a container's `Fuel` lasts one step. The answer
  then names the refill instead of the game: `Kept == false` and `WrittenOverBy` = `"kiln.items' water refill 1 (keep
  full) of the inventories of 1, set by kiln"` (the number is kiln.log's `refill 1: ...`). A refill is named only from
  its own report of the step the write was checked in (not of the step it was applied in), changing exactly the units
  the write left into the units the check found, and only while it is set in the running server world and not disposed: one that covers the container but did not write (a refill capped below the value,
  one of a world that has closed) is never named, and when that cannot be shown `WrittenOverBy` is null. End the refill (dispose its
  `FuelKeep`; for a `Water` rule, end the rule set that carries it) or narrow it to other items before the write, or
  change the water through the refill's own policy. Without a refill on it, a can a player carries keeps a tracked
  write like any other (lab, a spawned player character: [Tests](#tests-lab)). Only refills set through
  `Items.Fuel.Refill` are known to the answer; a native plugin's own `fuel_keep` is not named.
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
  `EventFilter.ForEntity` works. A change the game makes itself raises nothing. To be told of every net change of
  named components, whoever made it, watch them: `ComponentChanges.Watch` (since 0.2.0-dev.29; a tracked write shows
  there with `Origin = Kiln` and the mod's id: [decisions.md](decisions.md#named-component-watches-componentchanges-since-020-dev29)).
- **Which world, which entity.** A write belongs to the server world that runs when you call: one made before the
  first world exists answers `Dropped`, and one that has not run when a hosted game opens another world answers
  `Dropped` too. It also carries its entity's generation, the entity's `SpawnTime` (the entities the game or Kiln.Spawn
  spawns into the world have one), read at the call and compared right before the write: another value means the id
  names a new entity, and the answer is `NoComponent` ("is not the entity the write was asked for"). An entity without
  a `SpawnTime` (one loaded from the save, an item given into an inventory; a player may have none) cannot be told
  apart that way, so a write to it is
  never kept waiting behind a full step: when the step's 512 places are taken, it answers `Dropped` at once ("has no
  SpawnTime"; ask again in the next step). A write whose entity loses the component while it waits never runs either.
  Ask on the tick thread between two steps (a mod's tick, a command, an event), not inside a world program's update
  (`InvalidOperationException`). An id you keep from an earlier step can name another entity by now; check before you
  write.
- **Limits.** 64 entries in a mod's `writes` (a repeated name counts each time), 91 component types for all mods
  together (one program's 92 accessors, one of which reads `SpawnTime`; a name the world does not have takes no place);
  4,096 bytes per write (`Update` takes components up to 64 KB); 512 writes run per step, the rest wait for the next
  steps; 4,096 writes whose answer has not been handed over yet (waiting, running, held for the check, answered, or
  being delivered; refusals included), past that the call answers `Dropped` at once; 1,024 answers per step. The
  server world only.
- **A list kiln.world cannot read** (`"writes": null`, a name with a stray character, more than 64 entries) costs the
  mod its tracked writes, not its loading: the mod loads with a warning in kiln.log, `WriteInfo` says `ListRefused`,
  and each write answers `NotDeclared` with what is wrong with the list.
- `World.WriteInfo("Health")` answers for any component, listed or not, once a world runs: look before you list. It
  reads what kiln.world published for the running world, tagged with that world (the game's writers at the world's
  setup, the save and replication bits at its first step), never the live world, so any thread may ask. While a world
  is being set up (or none runs) `FactsKnown` is false: the counts are 0 and the flags null, never the previous
  world's.

### Transform scale
Since 0.2.0-dev.28. `World.SetTransformScale(entity, new Scale3(1.25f, 0.75f, 1.5f))` sets the three scale factors of an
entity's `CurrentTransform` (X, Y up, Z) through the game's write tracking: one tracked write of 12 bytes
(`World.WriteAt<Scale3>`) at the place the game's own type table gives (`CurrentTransform.transform.scale`, a
`keen::float3` at +40 on the server build f0d824be), so position and orientation stay as they are. It is the C#
equivalent of ShroudForge's scale setter. Sample: `mods/samples/TransformScale` (`/tscale`).
```csharp
if (!Capabilities.Is(World.TransformScaleCapability)) return;       // "world.transform_scale"
EntityScaleResult r = await World.SetTransformScale(prop, new Scale3(1.25f, 0.75f, 1.5f));
if (!r.Ok) Log.Warn(r.Reason);                                       // NotDeclared, NoComponent, Dropped, ...
Log.Info($"{r.Before} -> {r.Now}, kept {r.Kept}, after a restart too: {r.Persisted}");
Events.On<EntityScaleChangedEvent>(e => Log.Info(e.ToString()));
```
| Member | Side, threads | Answers |
|---|---|---|
| `World.SetTransformScale(entity, scale)` | server world; tick thread | `Task<EntityScaleResult>`: `Entity`, `Before`, `Requested`, `Now`, `Kept`, `WorldWriteStatus`, `Reason`, `SaveTracked`, `ReplicationTracked`, `Persisted`, `Ok`, `Changed` |
| `Scale3(X, Y, Z)` | any | three sequential float32 (12 bytes); `One`, `Uniform(f)`, `IsValid`; JSON `{"x":…,"y":…,"z":…}` ([schema](../../../schema/scale3.schema.json)) |
| `EntityScaleChangedEvent` | tick thread | a committed write that changed the vector: `Entity`, `Before`, `Now`, `ModId`, `Result` |

- **What it is not.** The rendered transform only: colliders, attachments and `ScaleComponent` keep their size, and
  nothing is promised about physics. A creature's uniform size is `CreatureRef.Scale` (unchanged). What a player sees,
  and what a vanilla player sees, is check `13-world.transform-scale` of the guided game test; until it has run the docs
  claim the tracked write and its readback only. `world.transform_scale` is on on both sides since 0.2.0-dev.29;
  that check still has not confirmed what a player sees.
- **When the row is decided.** Kiln.World starts right before the mods load, so a mod's `OnLoad` reads
  `world.transform_scale` (and `world.tracked_writes`) already decided: available or off with its
  reason. While kiln.types is there but the game has not filled its type table yet, the row is `Pending` ("waits for the
  game's type table") and is decided within 25 steps of the table filling; on a dedicated server the table is filled
  before the mods load.
- **The place of the scale** is read from the game's type table when Kiln.World starts: `transform`, then
  `scale`, of one of two shapes (a struct of three float32 at 0, 4, 8, or a static array of three float32), 12 bytes,
  inside the component, 4-aligned. Any other shape turns `world.transform_scale` off with what was found; nothing is
  written at an assumed offset. kiln.types must be running.
- **Declare it.** The mod lists `CurrentTransform` under `"writes"`; Kiln.Sdk adds it from a `SetTransformScale` call at
  build time. Without it the answer is `NotDeclared` (`Ok = false`), never a raw write.
- **Arguments.** Each factor finite and above 0; `EntityId.None` or another value throws `ArgumentException` and sends
  nothing. ShroudForge's own Lua setter, hosted unchanged, keeps its broader rule.
- **The answer** is the tracked write's: `Before` and `Now` are the 12 bytes the write read and left in the step it ran
  (null on a refusal), `Kept` is the readback one step later where the game writes the type too, `SaveTracked` and
  `ReplicationTracked` are the game's own per-type bits from the same answer, and `Persisted` says whether replay was recorded (or the original scale restored): recorded, not replayed. `World.GetWaitingTransformScales()` (tick thread) lists your kept scales that are not on an entity now, each with mod, template, position, scale and the reason.
- **Across a save and restart.** The tested placed chest reloaded at 1, 1, 1 without Kiln replay despite being marked for saving ([lab evidence](../../re/world.md#transform-scale-and-the-save)); this does not establish how players, NPCs, mounts or other entity families save scale. Kiln records scale beside the save in kiln.saves' `entity/kiln:world` attachments and reapplies it through a tracked write when exactly one loaded entity of the template is at the saved position (lab, 0.2.0-dev.29: a placed chest's scale came back after a restart and was forgotten after its restore). Replay is asynchronous, with no promise that it finishes before players arrive. It runs in the last setting mod's name while that mod is loaded and declares `CurrentTransform` under `"writes"`. Restoring the original scale removes the record; removing a mod skips future replay but keeps the record for reinstall. A later save refreshes the recorded scale from the live component while the entity is still the one the record is on.
- **Limits: size and entity type.** Every finite positive factor is accepted, from subnormal values through `float.MaxValue`, on any entity with writable `CurrentTransform`, including players, occupied mounts and physics bodies. There is no safe size range or entity-family allowlist. Extreme scales can overflow or degenerate downstream calculations; safety for those entities has not been established. Colliders, interaction components, attachments, `ScaleComponent` and building voxels are not resized. Dirty tracking does not prove that a vanilla viewer receives or renders the scale.
- **Limits: replay identity and removal.** A kept scale is reapplied by template and position, not a durable entity identity. Since 0.2.0-dev.29 it is reapplied only when Kiln can tell the entity: after a restart exactly one entity of the template may be within 0.5 m (static) or 16 m (mobile) of the saved position. With two or more (two identical chests side by side, two NPCs of one kind in a camp) nothing is scaled: the record waits with the reason, kiln.log names it once, and it keeps waiting, also after a neighbour leaves, until your mod calls `SetTransformScale` on the entity it means (find it with the record's template and position) or three sessions pass without that, after which the record is dropped. While the game runs, an entity made again under the same id is told apart by its `SpawnTime` (an entity loaded from a save has none, one spawned in the session has one); where both have none, the entity must still be the only candidate at the recorded place when the scale is set. A destroyed entity's record is dropped. This is still positional identity: one entity alone at the place of another of the same template is taken for it. Removing a mod from a running world does not restore its live scales; restore them explicitly before unloading if needed. Removing the mod before the next load prevents replay, but does not prove every entity family returns to its original size in a vanilla game.
- **Capabilities:** `world.transform_scale` (Kiln.World's row: the layout) and `world.tracked_writes`; either off throws
  `KilnUnavailableException` naming it.

### Program budget
`World.GetProgramBudget(kind = CapacityWorldKind.Server)` (since 0.2.0-dev.29; any thread; both sides) answers how much
room is left for world programs, as a `ProgramBudgetResult`: `KilnSlots` (process-wide: Kiln's own 64 slots for its
services, compat loaders and mods together; `KilnUsed` the programs that run or wait for a world, `Reserved` the slots of
programs that cannot run: refused, faulted, a declaration no loaded mod bound; a slot is never freed in a process) and
`EngineSlots` (the world's own table of 896: `GameUsed` the game's programs, `KilnUsed` those Kiln registered there,
`Reserved` another loader's direct registrations), one revision for both; `PerFileMax` (32 declarations in a
`kiln.programs.json`) and `AccessLimits` (8, 42, 64, 64, 9, 92, 24 and 4,096 iterator bytes) are always filled. It is
counted from what kiln.world keeps (its slot ledger, and its directory of the world's table read in the world's own
step once the table is final), never from game memory, and nothing runs for it while nobody asks. `Ok` is false with
`NoWorld` when there is no world of that kind (the client world on a dedicated server), with `NotReady` while a world's
table is not read yet (its `World` token is set then) or on a build where programs are off; then both budgets are 0. A
budget read before the world starts is not a promise: registration still checks the live table at the world's start.
The prediction world is not a kind of its own here (`ArgumentException`). Without kiln.world.budget (an older kiln.world)
it throws `KilnUnavailableException("world.program_budget")`. A change posts `CapacityChangedEvent` (`Quota`
`world.program_budget`) at the next step, measured separately for Server and Client every 25 steps while something listens; switching query kinds does not itself report a change. Native code: the table
`kiln.world.budget` (`native/core/abi/services/kiln_world_budget.h`), a table of its own next to kiln.world.
`World.Status` stays what it was (a legacy diagnostic).

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

### Client world programs (since 0.2.0-dev.29)
A game client runs a **client world** (the local player, the camera, what this player sees; it steps on the game's main
thread and runs its programs' slices as tasks on the game's workers) and, inside it, the **prediction storage** (it runs
the client world's table every prediction step on the main thread). A `client` or `both` mod can run a program of its own
in either. It runs in this player's process only: no other player and no vanilla client sees what it does, so it is for
presentation; a program that must change what others see belongs in the server world.

- **Declare** it in the mod's `kiln.programs.json` with `"world"`: `"client"`, `"both"` (one program in each world: a
  server side and a client side, each bound on its own) or `"prediction"` (the client world's table without the client
  flag, which the client world skips and the prediction storage runs). A file without `"world"` means the server world,
  as before. A client or prediction entry without `"flags"` is `SYSTEM`; `hostOnly` is a server program's flag (refused
  for `client` and `prediction`; a `both` entry keeps it on its server side). kiln.world reads the file at the game's
  start (from every enabled `client` and `both` mod; a `server` mod's client entries are not taken), so a changed file
  needs a restart of the game.
- **Bind** it with `EcsPrograms.TryBind(key, callback, WorldKind.Client)` (or `WorldKind.Prediction`) on the game's main
  thread: in `OnLoad`, which runs there for a client mod in the main menu, or through `Client.Post` (a host's reload
  runs `OnLoad` on its server thread). The answer is an `EcsProgramBinding`: `Ok` with its `Handle`; `Ok = false` with a
  `Reason` and no handle when nothing was bound (not reserved for that world, another binding holds it); `Ok = false`
  with a handle when only the client world that runs now refused the program (it runs in a later client world that takes
  it), and also when a client world runs that registered before the program existed (`Reason` "registered from the next
  client world"). `KilnUnavailableException` when `world.programs.client` is off. A declaration changed since the game
  started (its lists, flags or `"world"`) is refused until a restart. `EcsPrograms.Bind` and `TryBind(…, Server)` keep
  the server world's tick-thread rule.
- **Each client world anew.** kiln.world registers every client and prediction declaration into each client world at
  its `RegisterAllPrograms` (hosted and joined alike), after the game's own programs, so it runs after every client
  program it conflicts with. Its names are checked against **that** world's maps first: a name the client world lacks
  refuses it there only, with the name (`EcsProgramRefusedEvent` for a world after the bind, also when the mod's callback
  threw there: the binding then stays off, and the event comes again in each later client world; `kiln.log`:
  `program … refused for the client world: …`). Registered: `program kiln_managed_… registered into the client world as #N`. At the client
  world's first step kiln.log lists its event types (`client world 0x… event map (question 4): …`; a host's server world's
  too) while such a program exists.
- **Threads.** The callback runs on the game's workers or the main thread, several slices at once: no tick-thread API in
  it, UI work through `Client.Post`; `EcsProgramContext.World` says which world it is in. kiln.spawn's create and
  destroy (spawn, spawn_item, destroy, cleanup) refuse inside it, as inside any Kiln program or wrap stage: post them. A client world's entity ids are
  its own: the same number can name another entity in the server world.
- **Stop and unload.** When the client world stops (leaving to the menu, joining elsewhere) no call starts, a slice
  already running stops before its next entity (your callback is not called for the rest of it), and inside a callback
  that still runs kiln.world's helpers refuse (iteration, the current entity, the command buffer, other-entity reads,
  events); the running callbacks are waited for, 2 s at most; each registration waits for the next client world, and
  the binding stays and runs again there. A slice is also checked against the world it belongs to: a job of an earlier
  client world that starts late is refused (`kiln.log`: `a slice of another world … was refused`). Since 0.2.0-dev.29 the
  core runs the game's WaitWorldJobs on the client world before its stop (capability `world.stop_drain.client`; kiln.log
  `tick: client world 0x…: its jobs are drained by the engine (WaitWorldJobs, T us) before STOPPING`), and the game lets no
  slice outlive the step that ran it: no callback is inside at the stop, and the stop line says `(0 call(s) running at the
  stop, 0 after … us; the engine drains the world's jobs first (world.stop_drain.client))`. The per-entity check, the
  slice's world and the 2 s wait stay as a safety net for a core without that row and for a drain that did not hold:
  a callback inside at the stop although the engine drained is an error (`… were inside although …`) and latches; so
  does one still running after the 2 s (kiln.log `client world stops: N call(s) of Kiln programs still running after …
  us; their bindings are kept`): every client and prediction program is refused with that reason, no client-world
  program registers again until the game restarts and `world.programs.client` goes unavailable (faulted) with the
  reason. Without the drain such a late callback may still read the world the game is destroying. The same latch follows a fault in the engine's
  RegisterProgram for a client world, or a table that does not add up after it: the programs registered before it in
  that world stay in the game's table but no longer run, and say so. `EcsProgramRefusedEvent` follows in each later
  client world, and `TryBind` throws `KilnUnavailableException` with the reason.
- **Disposing** the handle, or the mod's unload, unbinds: from then on no call of it starts once kiln.world has seen it
  close. A dispose made inside a call of the program itself, or one that finds a call still running (a host unloading
  the mod from its server thread while a client step runs it), is finished later: Kiln retries on its service tick and
  keeps the callback, its mod's context and its assembly alive until the last call has returned. Your `OnUnload` runs
  before that: do not free what a running callback still uses.
- **Writes** through `TryWrite` change this player's client world only: in a client or prediction program it does not
  mark the write for the game's save or replication (it does in the server world), as the game's own client programs
  write what they draw; native `mark_written` is a no-op there. `TryWriteOther` answers false there: kiln.world's
  `write_other` refuses it in a client program. Not because of the save (per the notes the game's WriteOther marks only a
  server-world block) but because the other-entity path of a client job has not been read in a client run; the foreign
  views' `foreign_write` and `foreign_attr_changed` refuse from a client program too. A `TryWrite` raises `ClientComponentWrittenEvent` (Kiln.World; entity,
  component, the bytes before and after, `Writer` `"<modid>/program:<key>"`) while it has listeners. Whether every game
  version leaves such an unmarked write alone has not been run in a game: guided check `programs.client-visual`.
  `World.Write` has no client world.
- **Events.** `EcsProgramRefusedEvent` and `ClientComponentWrittenEvent` arrive on the game's main thread, in the client
  world's next Pre (on a host too, never on the server's tick), where a handler may touch the client world; one of a
  client world that ended first is not delivered. Only Kiln raises them.
- **Room.** The same 64 Kiln program slots as the server world's programs (a `both` program takes one), and the client
  table's 896 (checked before and after each registration; `World.GetProgramBudget(CapacityWorldKind.Client)` counts Kiln's
  entries there).
- **Capability** `world.programs.client`: on in a game client whose build resolved kiln.world's program registration,
  the name maps and `world.lifecycle`, with the client world's tick; the text says "waiting for a world" in the menu and "a
  client world runs" in game, with the stop's guarantee ("at a world stop the engine drains the world's jobs first
  (world.stop_drain.client)", or a bounded wait only); `wrong-side` on a dedicated server; unavailable (faulted) for the
  rest of the process after the latch above.
- **What every player pays.** With no client declaration in any mod: at the game's start kiln.world reads the
  `kiln.programs.json` of each enabled `client` and `both` mod that has one; each client world's RegisterAllPrograms
  runs kiln.world's existing hook, which registers nothing and logs its old line; each client Pre enters kiln.world's
  client handler, whose program part is one atomic load; each client world's start and stop reports the row's text to
  kiln.caps. Nothing is added to the game's tables and the server world's programs dispatch as before. Once a client
  declaration was accepted at the start (or a native service registered one) every client world registers it, also
  after its mod unloaded (inert): the slot and that cost stay for the process.
- Not yet run in a game client: guided checks `programs.client-baseline`, `14-client-world-programs`,
  `programs.client-joined`, `14-q4`, `programs.client-emf`, `programs.client-visual`, `programs.client-persistence`,
  `programs.client-rejoin`, `programs.client-delayed-stop`, `programs.client-exit`
  (mods/tests/client-pass, with the fixture mods/tests/client-programs-fixture and the EMF probe mods/tests/emf-client-test).
- Native code: `register_program_world`, `program_world_state`, `bind_program_world` and `unbind_program_world` at the
  end of the kiln.world table (offsets 496 to 520, after the foreign views at 448 to 488; the table is 528 bytes;
  [kiln_world.h](../../../native/core/abi/services/kiln_world.h)).

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
  `RegisterAllPrograms` (before the world finalizes; ~79 free slots of 896; kiln.world holds 64 at most since 0.2.0-dev.29, 32 before: its own
  quota, for every service and mod together). Every registration checks the world table's live count against its 896
  first and refuses the program before calling the game when the table is full, and checks that the count rose after
  the game's `RegisterProgram` returned (a descriptor the engine answers "true" for without registering it is refused).
  Mods' declarations (`kiln.programs.json`, at most 32 per file) leave the last 8 of the 64 to Kiln's services. Later
  calls return `KILN_ERR_UNAVAILABLE`.
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

### World reads from task threads

A task thread can read entities and components through seven members appended to `kiln.world` (offsets 528 to 576; the
table is 584 bytes with them, 616 with the writes below; version stays 4, so check `header.size`) and `kiln.types`' `ecs_registry`. Every one of them runs
on the calling thread, the tick included: none posts work to a tick and none waits on one. Every engine byte goes
through `kiln.internal.engine`'s guarded copy, so a page the game freed or protected fails the read, never the game.

| Member | What it returns |
|---|---|
| `entity_open` | a `KilnObjectRef`: id, generation, world session, layout stamp and the caller's reload epoch. A value, not a handle; nothing is pinned |
| `entity_validate` | the same ref re-checked, as a `KilnObjectInfo` that names the generation and session seen now |
| `ecs_context_snapshot` | which worlds and sessions exist now, and whether this build has an exact engine profile |
| `ecs_query` | every entity that has all of the named components, optionally inside a radius, as refs in ascending entity id. Complete or nothing |
| `ecs_snapshot` | one component's bytes, checked before and after the copy |
| `ecs_snapshot_many` | several components, each with its own presence. Not one atomic read across components |
| `ecs_template_uuid` | the template the entity was spawned from, 16 bytes, checked before and after |
| `kiln.types` `ecs_registry` | the component catalog: index, hash, size, type identity, and tracking flags only when they were actually read |

**What works where.** Entity and component reads work on the two exact builds Kiln has profiles for, dedicated server
26430253 and client 26430625:

| World | Dedicated server | Client hosting a game | Client joined to a server |
|---|---|---|---|
| server (`KILN_WORLD_SERVER`) | yes | yes | no server world (unavailable) |
| client (`KILN_WORLD_CLIENT`) | no client world | yes | yes |
| prediction | unavailable (a storage no engine view names) | unavailable | unavailable |

On any other build, and on these two when a check below fails, every entity read answers unavailable and the
capability row `world.ecs_read` says why; `ecs_context_snapshot` still answers.

**How a read finds an entity.** From the exact profile's `ecsReadLayout`: the world's entity manager, its entity map
(the engine's own id hash and linear probe over the occupancy bits and keys, bounded), the entity record (id,
generation, archetype layout, chunk, row, definition), the layout's component bits, offsets and strides, and the
engine's component registration rows (each one's hash checked against its name; a stride must equal the registered
size). The view the engine gate resolves for the world kind must name the very world object Kiln sees for that kind.
The entity's layout pointer is its layout stamp, so a component added or removed makes its refs stale; a row that
moves inside the same archetype does not.

**When reads turn on.** At boot (`on_ready`) `world.ecs_read` is available only when kiln.engine matched an exact
profile, `kiln.internal.engine` is here, the profile's read section carries the entity id index and every offset the
reads use, and kiln.world's own resolution of the same fields on the running exe (its sigdb constants for the entity
map, the hash map's fields, the record's id, layout, chunk and row, the layout's offsets and strides) agrees with the
profile field by field. The row's text names the first check that failed. `tools/engine/validate.py` shows the same
fields statically in both exes (docs/kiln/services/engine.md).

**The refusals stay distinct.** **Absent**: no such entity, or the entity has no such component. **Stale**: the same
id now names a new entity (a new generation), or the session, the layout or the caller's reload epoch changed.
**Unreadable**: the copy faulted, the bytes changed during the read, or a query's membership changed while it was being
read (`KILN_WORLD_ERR_CHANGED`; nothing is copied); try again. **Unavailable**: no exact profile, no world of that kind,
or reads are off on this build. **Busy** and **unsupported**: frame-quiet reads only (below). A short buffer is
`KILN_ERR_BUFFER_TOO_SMALL` with `*required` set and nothing written; `ecs_snapshot_many` works out the size before it
copies anything.

**Reload epochs.** Kiln keeps each owner at the newest `reload_epoch` it opened a ref with (0 until then). Opening
under a newer epoch is the owner's reload: from then on its refs of every other epoch validate stale. Opening under an
older epoch is stale. `ecs_query` refs carry the caller's current epoch. A retiring owner's epoch is dropped.

**Two read modes for component bytes.**

- **Stable copy** (the default). The engine keeps no version per component, so Kiln copies the bytes twice between
  identity checks, compares the copies and hashes the bytes before and after each copy; a value that changes during the
  read is refused unreadable. The limit, plainly: a game writer that is descheduled half way through storing a value
  leaves a half-written value that stays put, and a stable copy cannot tell it from a whole one. The host test measures
  it with a writer that stores byte by byte and yields half way: some mixed values get through. A writer that stores
  whole values, as the game's systems do, never got one through.
- **Frame-quiet** (opt-in, server world only): `KILN_ECS_READ_FRAME_QUIET` on a `KilnEcsSnapshotSpec` (a single
  component is `ecs_snapshot_many` with one spec), `KILN_ECS_QUERY_FRAME_QUIET` on a query. The copies run only while no
  server frame runs: Kiln reads the server step, then the engine's Scene word (the word the GameServer frame holds for
  its whole update and the write gate takes), copies, and reads the word and the step again. The word must be free both
  times and the step must not have moved: a frame that overlapped the copies either held the word at one of the two
  looks or ran entirely between them and moved the step. Kiln only observes the word: it takes no gate, writes
  nothing, and the copy window allocates nothing and takes no lock. **Busy** (`KILN_ECS_ERR_BUSY`): a frame ran during
  the read, nothing was copied; retry after the step (`NextGateWindow` in C#, a `KILN_TASK_AFTER_STEP` task natively).
  **Unsupported** (`KILN_ECS_ERR_UNSUPPORTED`): a client or prediction world, which has no such word; Kiln never falls
  back to a stable copy for a frame-quiet request. It rests on two facts of the engine: every frame that writes
  components holds the Scene word, and runs Kiln's Pre (which moves the step).
- `ecs_snapshot` is always a stable copy. `ecs_template_uuid` has no mode: its 16 bytes are the template definition's,
  game data that no frame writes, reached through one aligned pointer of the entity record.

**Host and Windows tests, no game.** `KILN_OUT=… native/services/world/tests/run.sh` runs `ecs_reads_test` (the logic
over a scripted backend) and `ecs_engine_test` (the engine backend under that logic, over the real kiln.engine booted
with a fake core and a fake engine laid out from both exact profiles: a recycled id, a removed entity, an archetype
change, a moved row, faulting pages, a catalog that does not check out, a stride that is not the registered size, a
view naming another world, a session change, reload epochs, a resized table, a probe without a free slot, concurrent
writers, and frame-quiet reads against frames), each under ASan/UBSan and ThreadSanitizer.
`native/services/types/test-types.sh` runs `ecs_registry_test`. `native/build.sh world-ecs-fault` and
`WINEPREFIX=<private prefix> native/services/world/tests/wine.sh` run the fault paths over kiln.engine's real Windows
SEH copy: pages made unreadable, guarded, decommitted and flipped under the reads, every read refused cleanly.

**In-game check (the owner's).** Create the empty file `<game>/kiln/data/kiln.world/ecs-read-check` before starting.
While a server world runs, every 20 s (6 rounds) kiln.world takes the players and the nearest container through the
game's own `GetComponent` on the tick, then a `kiln.tasks` task reads the same entities through the members above and
compares: `world ecs-read check: ...` lines in `kiln.log`, one `PASS` or `FAIL` line per round.

1. Dedicated server: start it with the file in place, join with a client, stand still next to a chest for two minutes.
   Expect `world.ecs_read: on (enshrouded-server-26430253)` at boot and `world ecs-read check: PASS round N` lines: the
   player's position read from the task within 0.5 m of the tick's, the Inventory size the same, the chest in the
   bounded query, a frame-quiet read of the player's transform (`ok after N busy look(s)`), and `snapshot_many`.
2. Client hosting a game (single player or co-op host), the file in `<game>/kiln/data/kiln.world/`: the same lines,
   and `world.ecs_read: on (enshrouded-client-26430625)`; the client-world lines show entities near the player, and the
   frame-quiet client-world query is refused `unsupported`.
3. Send `kiln.log` if any round says `FAIL`, or if `world.ecs_read` is off (its text says why). Remove the file again
   afterwards.

#### From C#: `WorldConcurrent` (experimental)

`WorldConcurrent` (Kiln.World) is the C# face of these members, for code in a Kiln task (the guide:
[../modding/tasks.md](../modding/tasks.md#reading-the-world-from-a-task)). It is
`[Experimental("KILNX001")]`: opt in with `<NoWarn>$(NoWarn);KILNX001</NoWarn>` in the mod's project. It can change
until it leaves experimental, and that waits for the in-game check above to pass on both exact builds.

```csharp
// CurrentTransform starts with x, y, z as 32.32 fixed-point metres (layouts depend on the game build)
struct TransformHead { public long X, Y, Z; }

var scan = Context.Tasks.Run("chest-scan", async ctx =>
{
    var near = new ConcurrentEntity[64];
    var query = new EntityQuery(WorldKind.Server, "Inventory") { Center = playerPos, Radius = 10 };
    var found = WorldConcurrent.Query(query, near);
    if (found.Status == ReadStatus.Unavailable) { await ctx.OnTick(() => ScanOnTick()); return; }   // no exact profile
    for (var i = 0; i < found.Count; i++)
    {
        var t = WorldConcurrent.Read<TransformHead>(near[i], "CurrentTransform", ReadMode.FrameQuiet);
        while (t.Status == ReadStatus.Busy)                     // a server frame ran during the read
        {
            await ctx.NextGateWindow();
            t = WorldConcurrent.Read<TransformHead>(near[i], "CurrentTransform", ReadMode.FrameQuiet);
        }
        if (t.IsOk) Remember(near[i], t.Value);                  // the mod's own; Stale, Absent, Unreadable: skip this one
    }
});
```

| Member | Native member | Notes |
|---|---|---|
| `IsAvailable` | `capabilities` (`KILN_WORLD_CAP_ECS_READ`), `ecs_context_snapshot` | the members are here, their capability bit is set, and the build has an exact profile. Any thread |
| `IsPresent(world)` | `ecs_context_snapshot` | a world of that kind exists now. Any thread |
| `Open(id, world)` | `entity_open` | a `ConcurrentEntity`: a value, not a handle. `EntityId.None` is `Absent` |
| `Validate(entity)` | `entity_validate` | the ref's status now |
| `Read<T>(entity, component, mode)` | `ecs_snapshot`, or `ecs_snapshot_many` with the frame-quiet flag | the first `sizeof(T)` bytes of a verified whole-component copy, as `World.TryRead`; `SizeMismatch` when the component is smaller |
| `ReadBytes(entity, component, into, mode)` | the same | the whole component; `TooSmall` with `Required` and nothing copied when `into` is short |
| `ReadMany(entity, set, mode)` | `ecs_snapshot_many` | a `ConcurrentSnapshot`: each component's status and bytes; one frame-quiet window for all of them |
| `Query(query, into)` | `ecs_query` | refs in ascending id; `TooSmall` with `Required` (the match count), `Changed` when the membership moved; complete or nothing |
| `Template(entity)` | `ecs_template_uuid` | the template's GUID; `Absent` when it names none |
| `OrOnTick(ctx, offTick, onTick)` | | the fallback in one line: `offTick` in the task when `IsAvailable`, else `onTick` on the server tick through `ctx.OnTick` |

**Statuses.** `ReadStatus` 0 to 6 are the native `KILN_ECS_*` values (`Ok`, `Absent`, `Stale`, `Unreadable`,
`Unavailable`, `Busy`, `Unsupported`). Three more come from the C# side: `Changed` (a query whose membership changed,
`KILN_WORLD_ERR_CHANGED`), `TooSmall` (the caller's buffer) and `SizeMismatch` (a component smaller than `T`). An
ordinary miss never throws. Only misuse does: a `default` `ConcurrentEntity` or `EntityQuery`, an empty component name,
a query `Center` that is not finite or has no finite radius above 0, or the wrong thread.

**Where it may be called.** Inside a Kiln task (its work on a worker, or after `await ctx.ToTick()`) and on the tick,
from a mod's code. Anywhere else it throws `InvalidOperationException`: a `new Thread`, `Task.Run`, or code after
`ConfigureAwait(false)` (the message names the task the code came from). The analyzer reports such calls at build time
([KILN105](../modding/analyzers.md#kiln105)). The reads are made for the task's mod, or for the mod whose code runs on the
tick.

**Frame-quiet on the tick.** The server tick runs inside the server frame (tick handlers, and a task's tick hops), so a
`ReadMode.FrameQuiet` read there would always meet the frame. There it answers `Busy` at once, without reading the
engine (`Unsupported` for a client-world entity or query, which has no frame-quiet window anywhere). Read `Stable` on the tick, or retry from a worker: `await ctx.NextGateWindow()` takes the work back to one, so
the retry loop above also works when it starts in a tick hop.

**Reloads.** A mod's owner id is the reload epoch of its reads, and every load of a mod gets a new owner id. Refs taken
before a reload come back `Stale` after it: open the entities again.

### World writes from task threads

Four more members, appended after the reads (offsets 584 to 608; the table is 616 bytes; version stays 4, so check
`header.size` and `KILN_WORLD_CAP_ECS_WRITE`), let a task thread change one whole component of a server-world entity by
compare-exchange: Kiln compares the component with the bytes the caller expects and, only when they are equal, writes
the new bytes. The write happens under the engine's own write gate (`kiln.internal.engine`, the gate the GameServer
frame holds for its whole update), so it can only land between frames and a frame never sees a half-written component.
Nothing waits: a call that cannot take the gate now answers busy at once.

| Member | What it does |
|---|---|
| `ecs_write_policy(world_kind, component, &policy)` | whether a task may write this component, and why not. Any thread |
| `ecs_compare_exchange(owner, &ref, component, expected, mask, desired, bytes, confirmed, &reply)` | compare the whole component with `expected`; when equal, write `desired` under `mask`. Any thread but a world's own |
| `ecs_write_subscribe(owner, fn, user, &handle)` | `fn` once per committed write, on the server tick, after the write's gate was released |
| `ecs_write_unsubscribe(handle)` | no later event reaches it. From another thread it waits for a callback of it that is running to return, so a callback must never wait on a thread that may unsubscribe it (a deadlock); from inside a callback it returns at once; on the tick outside a delivery it never waits |

**What may be written.** Only what the policy admits, decided by rules, never by a list of names:

1. the server world (the gate is the local authority's): a dedicated server, or a client while it hosts;
2. an exact engine profile whose write gate is enabled, and a server world running;
3. a registered component whose two change-tracking bits, read from the engine, are both clear. A tracked component
   (one the game records for the save or for replication) stays on the tick: use `World.Write`;
4. plain bytes all the way down: walked through kiln.types' reflection (every base class, field, typedef and static
   array), only booleans, integers, floats, enums, bit masks and GUIDs. Dynamic arrays, strings, optionals and variants
   (they own heap memory), blob types (offsets into blob data), object references (pointers), and anything kiln.types
   cannot describe are refused: swapping a pointer could hand the engine memory it never allocated;
5. the reflected size is the registered size, and the component is at most the profile's write budget (64 KiB).

`KilnEcsWritePolicy` names the first rule that refused (`why_code` and the same in words). The answer is cached per
server session; the tracking bits are read again inside every write. Over sigdb's reflection of both exact builds, 535
of the 687 components are plain (the largest, `ClientCursor`, is 4,328 bytes); which of those are untracked is read
from the engine at run time, and the in-game check below lists them.

**One call, step by step.**

- Before the gate (the calling thread; this part may allocate and lock): the arguments; the caller's owner (a ref is
  the caller's when its `reload_epoch` is the caller's owner id, as Kiln's managed host opens every ref; a native
  caller that writes opens its refs with `entity_open(owner, owner, ...)`); the policy; `bytes` must be the registered
  size; the uncertain latch below; the ref through the reads' checks (session, reload epoch, generation, layout); the
  caller's buffers copied once; a place in the event queue; then the gate, taken without waiting.
- Inside the gate's hold (no wait, allocation, lock, log or call into the engine's code; every loop has a fixed
  bound): the gate validated; the entity found again from the ref (same generation and layout); the component's two
  tracking bits read again from the engine's registry, at the profile's offsets; the current bytes copied and
  compared in full with `expected`; `desired` merged under `mask` (a nonzero mask byte takes the byte from `desired`;
  no mask takes every byte); nothing to change ends here; the entity found again; the write; the bytes read back and
  the entity found once more. A write that does not read back is rolled back in the same hold, and the rollback is read
  back too: after the release the next frame may already have written the component, and a rollback then would undo
  the game's write.
- After the release: `confirmed`, the latch, the event, and the reply with the hold's time.

**Outcomes** (`KilnMutationReply.status`; the `KilnResult` says the same):

| Status | Result | When | Retry? |
|---|---|---|---|
| `OK` | `KILN_OK` | written and read back (`WRITTEN`), or nothing to change (`NO_OP`: no write, no event) | |
| `CONFLICT` | `KILN_ECS_ERR_CONFLICT` | the bytes are not `expected`; `confirmed` holds them; nothing written | yes, from the new value |
| `BUSY` | `KILN_ECS_ERR_BUSY` | a frame holds the gate (every frame), another Kiln writer holds it, this owner had a busy refusal from the gate in this server step, the call came from the server tick (`IN_FRAME`), the event queue is full, or an owner retired during the hold; nothing written | after the step: `NextGateWindow` in C#, a `KILN_TASK_AFTER_STEP` task natively |
| `STALE` | `KILN_ECS_ERR_STALE` | the generation, session, layout or reload epoch changed, the ref is another owner's, or the calling owner is unloading or unloaded | no: open the entity again |
| `NOT_WRITABLE` | `KILN_ECS_ERR_NOT_WRITABLE` | the policy refuses the component, `bytes` is not its size, or a tracking bit was set when the hold read it | no |
| `UNCERTAIN` | `KILN_ECS_ERR_UNCERTAIN` | a write was attempted and neither its readback nor a rollback proved the bytes; also every later call on that target this session (`TARGET_LATCHED`) | no |
| `UNAVAILABLE` | `KILN_ERR_UNAVAILABLE` | no exact profile or write gate, no kiln.types, not the server world, no server world, or the server world is stopping | use the tick |
| `ABSENT` | `KILN_ERR_NOT_FOUND` | the entity is gone, or has no such component | |
| `UNREADABLE` | `KILN_ECS_ERR_UNREADABLE` | a read faulted before any write; or a write was rolled back and the rollback proven (`ROLLED_BACK`): the bytes are as they were | yes |

Only the gate's own busy refusal latches the owner for the rest of the server step (the gate's busy latch); no other
outcome does, so several writes per step stay possible. Argument errors are `KILN_ERR_INVALID_ARGUMENT` and leave the
reply untouched.

**Uncertain latches its target.** A fixed table of 512 entries keyed by session, entity, generation and component.
A latched target answers uncertain without taking the gate. When the table is full, every compare-exchange of that
server session answers uncertain (one `kiln.log` line says so). A new session clears it.

**Events.** Every committed write gives exactly one `KilnEcsWriteEvent` (entity, generation, component, the owner that
wrote, session, step), delivered in the order of the holds, on the server tick in kiln.world's Pre, after the write's
gate was released; at the server world's STOPPING what is left is delivered on the stopping thread. Callbacks run under
kiln.world's fault containment: one that faults is logged, its subscription ends, and the others still get the event.
An owner's subscriptions end when it unloads or reloads (a callback already under way then may still finish). The queue holds 4,096 events; a full queue makes writes answer
busy until the next delivery.

**Threads.** From a task's worker, between frames. On the server tick (tick handlers, a task's tick hops) a
compare-exchange always answers busy (`IN_FRAME`) before touching an engine word: the frame holds the gate there. Use
`World.Write` on the tick. Kiln's tracked writes (`World.Write`) handle tracked components and this member refuses them,
so the two paths never write the same component type. Stable reads from other tasks may meet a write between their two
copies and answer unreadable (retry); frame-quiet reads see the gate taken and answer busy.

**How long the gate is held.** The work inside the hold is fixed: up to three lookups of the entity (about 70 guarded
reads with the gate's own checks when the entity's id is found at the first slot it hashes to) plus one read of the
component, its write and its readback; a rollback adds a write and a read. Each lookup's id probe stops at 8,192 slots,
so a long run of occupied slots in the entity table adds reads up to that bound, never beyond it. Measured with Kiln's real Windows primitives under Wine (the fake engine of the tests laid out from the server
profile, 1,000 writes each, three runs): the largest plain component (4,328 bytes) held the gate 12 to 15 µs at the
median and 24 to 92 µs at worst; a 64 KiB component 44 to 48 µs at the median and 93 to 176 µs at worst. The GameServer thread that wants its next frame meanwhile spins (pause, then `SwitchToThread`); the
holder runs above normal priority. On a heavily loaded host (load average about 45, the test at the lowest CPU
priority, three runs under Wine) the medians stayed the same (15 to 18 µs for 4,328 bytes, 68 to 72 µs for 64 KiB) and
the worst holds reached 0.5 to 2.5 ms: the host's scheduler preempting the holder inside the hold. How long a hold can
last on native Windows under load, with the priority raise, is measured by the tester kit (#58). The 64 KiB cap stays.

**Relies on, per build.** For the components the policy admits, the engine's change recorder only reads and returns on
both exact builds (docs/re/world.md, "The change recorder for untracked components"), so the write does not call it and
no engine code runs inside Kiln's hold. A new game build needs that re-checked; without an exact profile every write is
unavailable anyway.

**Host and Windows tests, no game.** `KILN_OUT=… native/services/world/tests/run.sh` runs `ecs_writes_test` over the
real kiln.engine (its write gate) and the fake engine of both exact profiles, under ASan/UBSan and ThreadSanitizer: every
outcome above forced (a frame holding the gate, the busy latch, a call from the server thread, a second Kiln writer, a
full queue, a generation bump inside the hold, a tracking bit set after the policy was cached, a rogue write between the
write and its readback, a write fault part-way through a page, pages that turn unreadable, the latch and its full
table), no allocation and no core call inside the hold, the id probe's bound, events (order, a faulting subscriber,
unsubscribing during a delivery, owners retiring), four writers racing on one counter against a simulated frame and a drain, and
the plain-bytes rule over sigdb's reflection of both builds against an independent walk of the same data.
`tests/lint_hold.py` checks the code that runs inside the hold; `tests/mutate_writes.py <scratch dir>` breaks each
check alone and expects the suite to fail. `native/build.sh world-ecs-fault` and `WINEPREFIX=<private prefix>
native/services/world/tests/wine.sh` run the write's fault paths over the real Windows SEH write and Interlocked words.
Not covered there: the priority raise against a real GameServer spin on native Windows. The tester kit for that is
[../priority-kit.md](../priority-kit.md): a native mod doing these compare-exchanges in a loop on a padding byte of the
host's character, a CPU-load helper, collection scripts and the analysis (GitHub #58).

**In-game check (the owner's, on a lab or test world only).** It changes a component of a live world, so it runs only
when its file names that world:

1. Create the empty file `<game>/kiln/data/kiln.world/ecs-write-check` and start the lab or test world: the check
   logs `world ecs-write check: this world is <GUID>` and does nothing else.
2. Put `world=<that GUID>` in the file and start again. With that line only, the check lists the components a task may
   write and which of them the first player has, and does a no-op exchange. On any other world it stays off.
3. To check a real write, add `component=<a name from that list>` and optionally `offset=<byte>` (default 0): the check
   flips the lowest bit of that byte, flips it back and reads the component again, which must equal the original. The
   change lasts a few frames at most; pick a component whose value does not matter on the test world.
4. Join with a client and wait about a minute: `world ecs-write check: ...` lines in `kiln.log`, a compare-exchange from
   the tick answering busy (in frame), two committed-write events, and one `PASS` or `FAIL` line. Send `kiln.log` and
   remove the file afterwards.

The write protocol is adapted from ShroudForge's `write_entity_components.cpp` (MIT, bonsaibauer), as the gate is.

#### From C#: compare-exchange (experimental)

`WorldConcurrent` (Kiln.World) has the C# face of these members, `[Experimental("KILNX001")]` like the reads. A task
reads a component, works out the new value off the tick, and writes it only if nothing changed it meanwhile:

```csharp
// A plain, untracked component; WritePolicy says which ones are (the in-game check lists them for this build)
struct Charge { public int Value; public int Max; }

var task = Context.Tasks.Run("recharge", async ctx =>
{
    var e = WorldConcurrent.Open(entityId).Value;
    while (true)
    {
        var now = WorldConcurrent.Read<Charge>(e, "Charge");
        if (!now.IsOk) return;                                    // Stale, Absent, Unavailable ...: give up or reopen
        var next = now.Value with { Value = Math.Min(now.Value.Max, now.Value.Value + 1) };
        var r = WorldConcurrent.TryCompareExchange(e, "Charge", now.Value, next);
        if (r.Status is ExchangeStatus.Busy or ExchangeStatus.Unreadable)
            await ctx.NextGateWindow();                           // a frame held the gate, or a read faulted: after the step
        else if (r.Status != ExchangeStatus.Conflict) return;     // Ok, or a refusal a retry can't change (NotWritable,
                                                                  // Uncertain, Stale, Absent, Unavailable)
        // Conflict: r.Current is the value now; the loop reads again and retries from it
    }
});
```

| Member | Native member | Notes |
|---|---|---|
| `CanWrite` | `capabilities` (`KILN_WORLD_CAP_ECS_WRITE`) | the members are here and on for this build. Any thread |
| `WritePolicy(component, world)` | `ecs_write_policy` | a `ComponentWritePolicy`: `IsWritable`, `Status`, `Size`, `Budget`, `TrackingKnown`, `IsTracked`, `IsPlain`, `Reason`, `Text`. Any thread |
| `TryCompareExchange<T>(entity, component, expected, desired)` | `ecs_compare_exchange` | `T` is the whole component (its size is the registered size, else `NotWritable` with `Reason.Size`), and every byte of it is compared and written, padding too: give `T` a field for every byte, or use the byte form; an `ExchangeResult<T>` with `Status`, `Flags`, `Reason`, `Current` (the value the write saw) and `HoldTime` |
| `TryCompareExchangeBytes(entity, component, expected, desired, mask, current)` | `ecs_compare_exchange` | the same with bytes and an optional per-byte mask; `current` (optional) gets the bytes the write saw |
| `ComponentExchangedEvent` | `ecs_write_subscribe` | on the server tick after each committed write: `Entity`, `Generation`, `ComponentHash` (`Is("Charge")`), `Session`, `Step`, `Mod` (the mod that wrote, null for a native service). Kiln's service tick posts them; should it stop for many steps, events beyond 65,536 waiting are dropped and kiln.log says how many |

`ExchangeStatus` has the native statuses (`Ok`, `Absent`, `Stale`, `Unreadable`, `Unavailable`, `Busy`, `Conflict`,
`NotWritable`, `Uncertain`), `ExchangeFlags` the `KILN_MUTATION_*` flags and `WriteReason` the `KILN_ECS_WRITE_WHY_*`
codes. A refusal never throws; misuse does (a `default` `ConcurrentEntity`, an empty component name, byte buffers of
different lengths, the wrong thread). The writes may be called where the reads may (inside a Kiln task, or on the tick
from a mod's code); on the server tick a write is always `Busy` with `Reason.InFrame` and reaches nothing, so write from a
task's worker and keep the tick for `World.Write` ([KILN105](../modding/analyzers.md#kiln105) reports a write in tick code). A mod writes with the refs it opened itself: a ref another mod opened
is `Stale`, as is every ref after the mod reloads.

**In the test kit.** `KilnHarness` has the writes on its fake world with the same rules. The kit cannot read the engine's
tracking bits, so a component is writable there only after the test says it is untracked:
`kiln.Entities.Track("Charge", save: false, replication: false)`. `kiln.Entities.BusyWrites = 1` makes the next write meet
a busy gate, for the retry loop; the event comes at the next `kiln.Step()`.

### Entities and teleport from native code
Tick thread (Pre/Post) or inside your program: `component(e, hash, &size)`, `exists`, `transform`, `position`,
`find(hash, buf, cap, &count)` (KILN_ERR_BUFFER_TOO_SMALL with the full count when `cap` is short),
`entity_count`. `teleport(owner, &req, &id)` from any thread; the callback runs once, on the tick thread in Pre.
`teleport_cancel(id)` drops the callback. `status(&s)` tells what works.

### Foreign views: another service's code inside a game program (since 0.2.0-dev.29)
For native code that runs inside one of the game's programs and holds its iterator (kiln.abilities' natives get
impact_simulation's at NativeCtx+0x30): `foreign_layout(world_kind, "impact_simulation", &layout)` (tick thread, once per
world session) computes the program's iterator layout with the wraps' view algorithm over its live descriptor;
`foreign_slot` names a qword; inside the update `foreign_check(layout, iter, counts)` compares the job's group counts with
the view (use nothing of the view on an iterator that fails it); `foreign_read` / `foreign_write` give another entity's
component through the program's own other_read / other_write accessor with the job's storage, tracker and write flag (the
tracked-write path: saved and replicated); every access checks the session, the job's counts, the storage's registry,
the requested name against its hash entry and the live accessor's type; `foreign_attr_changed` runs the attribute
formulas with the program's `impact::AttributeRegistry`, only for the block the same thread just wrote through
`foreign_write`. Explicit arguments, no thread-local state. What these helpers do not give: exclusion between two
threads on one entity (a caller must know its program's slices run one after another: kiln.abilities requires
impact_simulation's pin to the world's thread), a rollback of a write that faulted (SEH contains it), a generation for
an entity id (a reused id names its current occupant), or a check of a caller's struct beyond its size. Host tests:
`tests/wraps_test.cpp` (`foreign:` rows).

### Is this thread inside a program? (since 0.2.0-dev.28)
`in_program()`, appended after `wrap_original` (version stays 4: check `header.size` first), answers 1 while the
calling thread runs a Kiln world program's update, a wrap stage on a game program's slice, or a kiln.types system hook
callback (`GameSystems.Hook`; kiln.types' `in_system_callback`, 0 for that part with an older kiln.types), else 0. The
world is in the middle of its step there, maybe on a worker thread. Any thread, lock-free, no game call. A service whose calls must
not run mid-step refuses them with `KILN_ERR_WRONG_THREAD` when it answers 1 (kiln.queries does so for its rays and
path submits: [queries.md](queries.md)).

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
  (`NetworkFuel`, `Kept == false`), a type with no game writer, a can a spawned player character carries (kept with
  no refill on the character; written back one step later with `Items.Fuel.KeepFull` on it, the answer naming the
  refill; kept again once the refill ends: `CarriedPart.cs`); boot 2 a tracked and a raw write of `Fuel`, `UsedItem`
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
  at it, and shows half without your touching it; `world.tracked-water-rejoin` gives a can of its own, marks it with
  instance data only it sets, sets it to 23 of 100 and leaves it in the backpack, you note 23, leave to the menu and
  come back, and `world.tracked-water-rejoin-after` finds that can by its mark alone (none or two marked: it takes
  nothing and says so), checks 23 on the server, asks whether it shows 23, then takes back that can and no other. The
  first half's plan is one file per world and character and names the session it ran in; the second half refuses to
  judge in the same session ("leave to the menu and come back first"). Never run, the second half leaves the can in the
  backpack (the player's) and the plan; the next first half there reuses that can. Run them with no water rule on your
  player (Creative off): they refuse to start while one is.
  Until these pass, "players see a tracked write" is unproven.
  First try in a real game (13 Nov 2026, a hosting game, 0.2.0-dev.27 test build): `world.tracked-water` did not
  pass; the write to half answered `Written: Fuel of <entity> (9 game writers, written over by the game one step
  later, marked for the save)`. Not the game: the owner had Creative on, whose `Water` rule keeps every can the player
  carries full through a kiln.items refill (kiln.log `refill 1: entity 1 (its inventories) ... keep full`), and the
  answer then blamed any write that did not hold on the game. The lab reproduced both answers word for word on a
  spawned player character with a refill on it, and the write kept with none ([docs/re/world.md](../../re/world.md));
  the answer now names the refill (`WrittenOverBy`). `world.tracked-water-rejoin` could not run: it needed a can the
  first check takes back.

## Tracked writes for native services (v3)
The table's last five entries, appended in v3 (check `header.size` and `capabilities() & KILN_WORLD_CAP_TRACKED_WRITES`):
`write_submit(owner, &request, &ticket)` queues one write (bytes at an offset, or a `change` callback that edits a copy
of the component inside the step) and answers through `callback` in a later Pre; `write_cancel(ticket)`;
`write_info(mod_id, hash, &info)`; `write_status(&status)`; `write_declare(owner, who, names, count)` lists a native
service's own component types (from `kiln_plugin_main` or `on_ready`: the write program is made when the first server
world registers its programs). A mod's types come from `"writes"` in its kiln.mod.json, read at boot next to
`kiln.programs.json`; a name the world does not know is left out of the program alone and takes none of its 91
places (the first server world decides this, before the program is made). The program also reads `SpawnTime`
(`other_read`), the entities' generation: `write_submit` (tick thread, between steps) reads the entity's `SpawnTime`
and the write compares it right before writing; a request for an entity without one is answered `DROPPED` at once
when the next update's 512 places are taken. `write_info` answers from a catalog tagged with the world it describes
(`KILN_WORLD_INFO_NOT_READY` while the running world's is not published). A mod whose `"writes"` could not be read is
`KILN_WORLD_WRITES_LIST_REFUSED`: it loads, every write of it is refused with the reason. A request remembers the server world it
was made in (one of an earlier world is dropped when the next registers) and is watched while it waits: if its entity
lacks the component at an update, it never runs. Before each result callback kiln.world asks again whether the
request is still wanted and its owner alive, so a callback that cancels another request or unloads another owner
stops that one's delivery. `KILN_ERR_BUFFER_TOO_SMALL` counts every request not delivered yet, refusals included.
The program
(`kiln_world_writes`, SYSTEM | HOST_ONLY, the listed types as `other_write`, `impact::AttributeRegistry` as an optional
read singleton) exists only when something is listed: a server whose mods list nothing gets no extra program.
kiln.items keeps its own program for water and moves; nothing was moved onto this one.

## The contract (dev.27)

Kiln.World follows [the API contract](../modding/contract.md); its tracked writes and entity sets were written to it
(above). `World`, `Nearby` and `Teleport` are `[ServerOnly]` (the server world's entities).

- **Capabilities.** `kiln.world` (the core's row) and its sigdb features (`world.component_bits`, `dead_check`,
  `entities`, `find`, `names`, `names.events`, `names.singletons`, `programs`, `queue`, `read_other`, `teleport`,
  `write`), lowered at Kiln.World's first use when kiln.world is missing; `world.tracked_writes` (session, reported
  with them and again after a registry reset).
- **Threads.** `[TickThreadOnly]` on every read and write that checks it: `Exists`, `Position`, `TryGetPosition`,
  `Transform`, `Has`, `ComponentSize`, `TryRead`, `TryWrite`, `WriteRaw`, `ReadBytes`, `Find`, `EntityCount`,
  `Nearby.Entities`, `Nearby.Within`, `Teleport.To`, `ToEntity`, `ToSpawnPoint`, and the sets and tracked writes.
- **Errors, results, events.** The sets name `world.find` and the tracked writes `world.tracked_writes` (dev.27);
  `WorldWriteResult` and now `TeleportResult` are kiln results (`Ok`, new, is `Moved`); a tracked write posts
  `ComponentWrittenEvent`. Not changed: `WriteRaw` and `TryWrite` stay raw stores with no event (the table above);
  `World.Write` and its kin are the tracked path.
- Tested offline: `managed/tests/Kiln.Contract.Tests/WorldContractTests.cs` and the conformance rules.

## Programs v2

[Managed ECS programs and system hooks](../modding/ecs-programs.md) describes boot-time declarations and normal
`OnLoad` callback binding. Native consumers can append-check `write_other`, `unregister_program`, `bind_program`,
`capabilities`, `program_events` and `program_emit` in the v2 table. Existing v1 fields keep their positions.
Other-write accessors use the engine's tracker and write flag. Event inputs resolve in event type-index order,
independent of descriptor list order. Unknown access names refuse a program before engine registration.
