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

`native/core/abi/services/kiln_queries.h`: `ray_voxels`, `entities`, and four `path_*` members that answer
unavailable until the path feature fills them.
