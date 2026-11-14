# kiln.queries: questions about the server world

`Kiln.Queries` (native service `kiln.queries`, `kiln-queries.dll`) answers read-only questions on the tick thread of
the server world: a dedicated server, or the server world of a game that hosts. Nothing here reads a joined client's
world.

Two things to know before anything else:

- The rays are **voxel rays**. They see terrain and building voxels. They do not see props, chests, doors, creatures,
  players or any collider: a clear voxel ray is not a clear path.
- The overlaps look at **entity origins**: one point per entity, its position. A large thing whose origin is outside
  the shape is not found, however much of it reaches in.

## Members

| Member | Capability | Answers |
|---|---|---|
| `Queries.RaycastVoxels(from, to, realm = 0)` | `queries.voxel_ray` | the first voxel surface on the segment, or that the segment is clear |
| `Queries.GroundBelow(from, maxDown = 100, realm = 0)` | `queries.ground` | the first voxel surface below a point (a roof counts), at most `maxDown` metres down |
| `Queries.VoxelLineOfSight(from, to, options = null)` | `queries.line_of_sight` | whether voxels stand between two entities (each position plus an offset, 1.5 m up by default) |
| `Queries.EntitiesInSphere(center, radius, exclude = default, max = 4096)` | `queries.overlap_origins` | entities whose origin is within the sphere, nearest first |
| `Queries.EntitiesInBox(center, halfExtents, exclude = default, max = 4096)` | `queries.overlap_origins` | entities whose origin is within the axis-aligned box, nearest to its centre first |

All are `[ServerOnly]`, `[TickThreadOnly]`. They change nothing in the world, so there is no change event.

**Not from inside a world program, a wrap or a system hook.** A ray (`RaycastVoxels`, `GroundBelow`,
`VoxelLineOfSight`) and a path request (`RequestPath`, `FindPath`, `CanReach`) are refused inside a world program's
update (`EcsPrograms`), a wrap stage on a game program, or a game system hook's callback (`GameSystems.Hook`), also when
that runs on the world's own thread, which is the tick thread: the world is in the middle of its step there, and the
game's own jobs may use the voxel data the ray reads. The member throws `InvalidOperationException` ("cannot be called
from inside a world program's update, a wrap stage or a game system hook"), as it does off the tick thread; nothing is
cast, nothing is queued, and no capability row changes. Cast the ray or ask for the path in a tick handler and hand the
result in (a field the program reads, or `Scheduler.Post` from the program). Natively, `ray_voxels` and `path_submit`
answer `KILN_ERR_WRONG_THREAD` with the reason `InsideProgram`; kiln.world's `in_program()` (0.2.0-dev.28) tells
kiln.queries where it is, and with an older kiln.world the members behave as before.

## Three answers, never two

A ray result (`VoxelRayResult`) is one of:

| `Ok` | `Hit` | Meaning |
|---|---|---|
| `true` | a `VoxelRayHit` | the first voxel surface: position, distance from the start, material id, raw voxel |
| `true` | `null` | the whole segment was read and is clear |
| `false` | `null` | nothing is known; `Reason` says why |

Reasons of a refusal:

| Reason | What to do |
|---|---|
| `NotLoaded` | voxel data on the segment is not loaded. Load the area (`Build.Load`) and ask again in a later step: the ray's own cell cache follows some steps after `Build.Load` says the tiles are there, and after a voxel write |
| `OutOfGrid` | an endpoint is outside the realm's voxel grid. The game is not asked |
| `BudgetExceeded` | the step's ray budget is used up. Ask again next step |
| `WorldNotReady` | no server world, or its first step has not been published yet |
| `ReentrantQuery`, `InvalidHit` | a ray asked from inside a ray; an answer of the game that cannot be read |

`VoxelLineOfSight` adds `EntityGone` (one of the two entities has no position). Its `Visible` is `false` for every
refusal: read `Ok` first. "Not known" is not "hidden".

A ray whose numbers are no ray throws `ArgumentException`: a coordinate that is not finite, no length, longer than
256 m, also when two different points are the same point as the floats the game works with
(`(1000, 0, 0)` to `(1000.000001, 0, 0)`), or a realm the world does not have.

## The budget

Every ray of every mod, `GroundBelow`, sight and a brain's sight share **64 rays and 500 microseconds of ray time per
server step**. The count is hard; the time is checked before a ray, so a step can run over by one ray's time. Rays into
cells that were not asked before are slower, so the time can end a step before the count does. A refusal that asks
the game nothing (bad numbers, `OutOfGrid`, `WorldNotReady`, `EntityGone`) uses none of it. Overlaps do not use it.

Spread work over steps (a placement search with 24 candidates is several steps of work), and treat `BudgetExceeded`
as "next step".

A server operator changes the limits in `<config>/kiln.queries.json`, read once at start:

```json
{ "format": 1, "maxRaysPerStep": 64, "maxRayMicrosecondsPerStep": 500 }
```

`maxRaysPerStep` is 1 to 4096, `maxRayMicrosecondsPerStep` 1 to 10000. A file that is wrong in any of these gives
both defaults and one warning in `kiln.log`; the chosen limits are logged at start.

## Paths

`Queries.RequestPath`, `FindPath` and `CanReach` ask the game's own path search (the one its creatures use) for a
**walking** route between two points at most 200 m apart. Nothing is moved: the answer is a copy of the route's points
as they were at the step the game answered. It arrives some server steps later (four to six observed for a short walk).
A pending request times out once both 10 seconds and 150 server steps have passed.

```csharp
var result = await Queries.FindPath(new QueryPathRequest(anchor, from, to));
if (result.Ok && result.ReachesTarget) foreach (var p in result.Points) { /* walk it yourself */ }
```

| Member | Capability | |
|---|---|---|
| `Queries.RequestPath(request)` | `queries.path` | a ticket: `Request` (the id) and `Completion` (the task) |
| `Queries.FindPath(request, cancellationToken)` | `queries.path` | the task alone; cancelling the token cancels the request |
| `Queries.CancelPath(request)` | `queries.path` | ends a request of the calling mod as `Cancelled` |
| `Queries.CanReach(request, cancellationToken)` | `queries.reachability` | the same search, asked only whether it arrives; repeated questions without a cancellable token share one search in a step |

**Walking a route.** Nothing here walks for you. `mods/samples/NpcSample/PathWalker.cs` shows the whole loop for a
creature with a brain ([npc-blocks.md](../modding/npc-blocks.md)): ask `FindPath` with the creature as its own anchor,
keep the copied points, give them to `NpcControl.MoveTo` one after another, and treat the route as out of date when
the goal moved, when it is some seconds old or when the creature is no longer near it. On `Ok = false`, or where
`queries.path` is off, it stands still and asks again a few seconds later; it never walks straight at the goal
instead. In game: `/npcsample walker`, `/npcsample walkto`.

**The anchor.** A request names an entity the game's query is made for. It is not moved, and it need not be what
will walk. It must carry the game's spawn marker (SpawnTime): entities the game or `Spawn` put into the world have it,
saved entities, items and players may not (`OwnerIdentityUnavailable`). For those, spawn a helper pickup
(`Base_LootPickup_Generic_Gravity`) at the start, use it as the anchor and destroy it afterwards. If the anchor goes
while the request waits, the request ends `OwnerGone`.

**What a result says.**

| `Status` | `Ok` | Meaning |
|---|---|---|
| `Found` | yes | a walking route whose last point is at the target (within 1.5 m across, 3 m up or down) |
| `Partial` | yes | a walking route that ends elsewhere (`ReachesTarget` is false) |
| `NoPath`, `NoStartNode`, `NoTargetNode`, `IterationLimit`, `OutOfMemory` | no | the game's own answers; `Reason` has its word |
| `NotLoadedOrUnknown` | no | the game gave no answer it stands behind: load the area (`Build.Load`) and ask again |
| `UnsupportedTraversal` | no | the route needs a jump, a door or swimming; its points are given for diagnosis only |
| `QueueFull`, `OwnerIdentityUnavailable`, `OutOfGrid` | no | refused at once: the ticket is `Ok = false` and its task is complete |
| `Cancelled`, `OwnerGone`, `TimedOut`, `WorldClosed`, `OwnerRetired`, `Refused`, `Truncated`, `MalformedResult` | no | how else a request ends |

`CanReach` is `Ok` with `Reachable = true` for `Found`, `Ok` with `Reachable = false` for `Partial` and `NoPath`, and
not `Ok` for everything else: "not known" is not "no".

**Limits.** 8 requests open per mod and 32 in all, counting results that were not delivered yet; the game is asked for
at most 8 new searches a step. The space a route needs is `HorizontalSpace` and `VerticalSpace`, in metres as the game's
query takes them (0.5 and 2 by default). `PathQueryChangedEvent` follows each request (`Created -> Queued`,
`Queued -> Searching`, its end); the event of the end is posted before the task completes. World close completes tasks
immediately and defers its event to a later tick; retiring code receives no further events.

A route is geometry at one moment: the world may change before a creature has walked it, and `NpcControl.MoveTo` is
a straight step, not a follower.

On a game client `queries.path` and `queries.reachability` are held until the guided check `02-queries.path-hosted`.

In unit tests: `kiln.Queries.Paths = request => FakePathAnswer.NoPath;` (or `Route(...)`), `PathSteps`,
`PathRequests`, `WithoutSpawnTime(entity)`. An anchor made with `kiln.Entities.Create` needs an 8-byte `SpawnTime`
component (`kiln.Entities.SetBytes(anchor, "SpawnTime", BitConverter.GetBytes(1ul))`), just as the native service requires.

## What the hit does not say

No surface normal, no cell coordinate, no entity. The position lies a hair before the voxel's face
(`3025.999` for a wall that starts at `3026.0`): do not derive the cell from it by rounding.

## Capabilities

| Row | Says |
|---|---|
| `kiln.queries` | the service's table is here |
| `queries.voxel_ray` | the game's voxel ray resolved on this build (a sigdb feature). Turned off for the process if the ray ever faults or the game's data does not look as this build's should |
| `queries.ground`, `queries.line_of_sight` | built on the ray; off with it |
| `queries.overlap_origins` | entity origins, from `kiln.world` |

On a game client all four `queries.*` rows are held until the guided check `01-queries.voxel-ray-hosted` passes in
the real game ([caps.md](caps.md#the-client-gate-dev28)); a dedicated server is not held.

## Unit tests

The test kit fakes `kiln.queries` (`KilnHarness.Queries`): a test scripts what a voxel ray answers
(`kiln.Queries.Voxels = ray => FakeVoxelAnswer.Hit(...)`, `Clear` or `Refused("NotLoaded")`), sets the step's ray
budget (`MaxRaysPerStep`) and reads the rays that were asked (`Rays`). Overlaps read the harness's fake world. There
are no voxels offline: a test says what the game would answer, it does not find it out. Without a `Voxels` script the
ray capability is unavailable; use `ray => FakeVoxelAnswer.Clear` when a test needs a loaded, clear segment.

## Native table

`native/core/abi/services/kiln_queries.h`: `ray_voxels`, `entities`, and the path queue (`path_submit`, `path_cancel`,
`path_poll`, `path_retire`): owner-scoped, pulled by the caller, with no callback into caller code.
