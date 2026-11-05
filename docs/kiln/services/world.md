# kiln.world

The server world's ECS for other services and mods: **programs** (your own ECS system in the game's scheduler),
**entity access** (components, positions, "who has component X") and **teleport** (the game's own fast-travel
mechanism). Only kiln.world hooks the game's program registration; every other service registers programs through it.

| Layer | File |
|---|---|
| C table | `native/core/abi/services/kiln_world.h` (`kiln.world` v1) |
| Native | `native/services/world/` → `kiln/services/kiln-world.dll` |
| Symbols | `sigdb/symbols/server.d/world.json` (features `world.programs`, `world.queue`, `world.entities`, `world.find`, `world.names*`, `world.teleport`, `world.dead_check`, `world.write`, `world.read_other`) |
| Managed | `managed/services/Kiln.World/` → `kiln/host/Kiln.World.dll` (`Kiln.World`, `Kiln.Teleport`) |
| Test mod | `mods/tests/world-test/` |
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

## Client checklist (needs a joined player)
- `Teleport.To(player, …)` snaps the client once (no rubber-band), `TeleportCount`/`RenderTeleportCount` change, and
  the game logs `Player EntityId N teleported From … to …`.
- FindGround / Checkpoint on a 1 km move: no fall, the destination streams in; `Replication Checkpoint … Set/Reached`
  in the game log with Checkpoint.
- A dead player (respawn screen) is refused with "the entity is dead".
- `ToEntity(player, otherPlayer)` lands next to the other player; `ToSpawnPoint(player, altar)` on a landing pad.
