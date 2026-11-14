# Voxel ray and the ray grid (kiln.queries)

What kiln.queries relies on, from the dev.28 research (server Steam build 26430253, client 26430625; the functions are
the same on both sides). Signatures: `sigdb/symbols/server.d/dev28-01-world-queries.json`.

## The ray
`queries/VoxelRay` (server RVA 0x6ac7e0, client 0xa0f770), Windows x64:

```c
void* VoxelRay(void* hit, void* voxelGrid, const float from[4], const float to[4], const uint8_t options[4]);
```

- Argument 1 is the output, argument 2 the `ecs::VoxelGrid` singleton of the world.
- `from`/`to` are world metres as floats, read with aligned loads: 16 readable bytes at a 16-byte boundary each.
- `options[0]` realm index (not checked by the function), `[1]` unknown (0), `[2]` stop at a cell whose data is not
  loaded (1), `[3]` skip thin terrain in the occupied-cell test (1, as the game's own ground and sight tests).
- A segment whose float length is at or below the constant at `queries/RayMinLength` (1e-20f) returns at once with
  "available, no hit": nothing was looked at.
- It walks half-metre cells, reads voxels only (no collider), and writes the realm's cell cache while it runs: tick
  thread only.

Hit record (32 aligned bytes are enough; 21 are written):

| Offset | | |
|---|---|---|
| +0x00 | float[3] | intersection point, valid with the hit flag |
| +0x0c | u32 | material id of the voxel |
| +0x10 | u16 | raw voxel |
| +0x12 | u8 | data was available |
| +0x13 | u8 | hit |
| +0x14 | u8 | the last cell visited was outside the realm's ray grid |

## Outside the grid reads as "loaded, empty"
The per-cell reader (`queries/VoxelCellRead`) answers 0 (data), 3 (chunk missing: queued, the ray stops and clears
"available") or 0x47 (outside the realm's ray grid). The ray treats 0x47 as an empty cell and goes on, so a segment
that leaves the grid can come back "available, no hit". +0x14 only remembers the last cell. kiln.queries therefore
tests both endpoints against the grid box before the call, with the reader's own arithmetic on the floats that are
passed: `d = p - origin` (float), `d >= 0`, `int(d) >> shift < count`.

## Layout (all captured by sigdb, none a literal in the service)
`ecs::VoxelGrid`: realm array pointer and u64 realm count (`queries/grid.realms`, `queries/grid.realm_count`); a realm
entry is `queries/realm.stride` bytes and starts with the pointer to its ray grid. Ray grid: float3 origin
(`queries/ray_grid.origin`), u32 chunk shift, horizontal and vertical chunk counts. These are the ray cache's grid
values, not the `VoxelWorld` tile geometry `Build.Grid` reports.

### The path query's fields
The path half (`sigdb/symbols/server.d/dev28-02-pathfinding.json`) writes nine fields of the game's path query, all
captured: start, target, the iteration limit and the moves from the base defaults (`@queries/base_query.layout`), and
five from two readers of the query:
- `@queries/query_normalize.layout`: the function that copies a query for one path grid and halves each search
  distance and free-space value unless that grid's cell size says otherwise, one block per field, horizontal and
  vertical in turn. Its second, fourth, fifth and sixth blocks give `queries/query.start_search_v`,
  `queries/query.target_search_v`, `queries/query.space_h` and `queries/query.space_v`. The pattern starts at the
  instruction before the first block, since the blocks differ only in their offsets.
- `@queries/query_target_type.layout`: the search driver's test of the target type against 1
  (PreferClosestTargetNode), which gives `queries/query.target_type`.

Both match once on the server and once on the client (0xf4, 0xfc, 0x100, 0x104 and 0x36 on Steam builds 26430253 and
26430625). kiln.queries requires the audited offsets before it writes. The normalizer's identical blocks can be
reordered without breaking the pattern; a changed capture therefore disables paths until the new layout is verified.

## Seen in the lab (dev28-01-world-queries, no player)
1000 of 1000 downward rays hit natural ground (material 0x2f2607aa); a 2-cell wall of terrain voxels 0xff01 and one of
building voxels 0xff81 both stop a ray 1 mm before their face, from either side; before `Build.Load` every ray is "not
loaded"; after a voxel write the ray sees the change a few steps later.
