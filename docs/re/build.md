# The voxel world, building props and the terrain save (server svn 1285601): evidence for kiln.build

Build: dedicated server Steam 26404334 / svn 1285601, exe sha256 `c5c9fe50…`, image base `0x140000000`; addresses are
RVAs. Old build 1273043 in brackets where checked (every sigdb entry resolves on both).
Sources: EMF 0.3.3 docs (MIT; `engine_parts/world_building_daytime.md` §3-5), ShroudForge's world editor (MIT;
`modules/game-runtime/native/voxels/*`: the VoxelGrid interface roles and the block writer's data-flow contract), the game's reflection
records (`keen::impact_nodes::Terraforming`, `ItemInfo`, `EquipmentSetup`, `BaseIdComponent`), capstone, Ghidra 12.1.2
headless decompiles (no auto-analysis), and the lab `build` (a copied save, no player).
Tags: **[dis]** disassembly, **[dec]** decompile, **[refl]** reflection, **[cap]** sigdb capture
(`sigdb/symbols/server.d/build.json`), **[rt]** runtime (kiln.log quoted).

## 1. Summary

| Question | Answer |
|---|---|
| Cell format | u16 per 0.5 m cell: low byte material, high byte fill 0..255 (255 solid, 0 no matter). Materials 1..127 terrain, 128..255 building-block materials. The game writes a surface's material on the air cells next to it (fill 0). |
| Grid | level 6 of a 7-level pyramid; cell = 32 m / 2^6 = 0.5 m; origin (0, 0, 0); 20480 x 20480 cells (10240 m); streaming tiles of 256 x 256 cells (128 m), 80 x 80 of them; y has no engine bound (below 0 reads solid `ff01`). |
| Read | the `ecs::VoxelGrid` singleton's published interface, slot `read(Request*, wrapper)` |
| Write | the engine's block writer `0x83fa70(VoxelWorld*, Span*, dims, origin)`, which commits 8x8x8 blocks through the store commit `0x83f6e0`, the sink shared by the building and terraforming kernels; it tracks changes per peer (replication). |
| Streaming | a dedicated server keeps only tiles near bases (and players) resident. Reading a cell touches its tile's chunk resource and the resource system loads it within ~1-3 s; an unloaded tile reads only the store's edited blocks (generated terrain reads as air). |
| Saved? | **only inside base build zones**: the save writes one terrain blob per region of a list built from BuildZoneSystem's base zones; everything else regrows at the next restart (the game's rule). Kiln appends its own regions to that list (hook on `0x932a40`). |
| Props | the building helper `0x2248e0` spawns the template with CurrentTransform, DisableRandomLootOnDestroy (1 byte) and UsedItem (4 bytes, when an item is known). Kiln spawns the same components through the core's SpawnWrapper. |
| Bases | `BuildZoneSystem` list 0: `{data, count, capacity, grow}`, entries of 0x48 bytes. |

## 2. The VoxelGrid interface (reads, readiness)

- `ecs::VoxelGrid` is registered as a world singleton by `0x61efd0` from `world+0xe17e38` **[dis]**. The GameServer
  setup (`0x7af7c0`) builds a 0x48-byte interface on its stack and hands it to `SetVoxelInterface` `0x67f4e0`, which
  copies it to `grid+0x28` (`movups [rax+0x28], xmm0` …) **[dis] [cap build/grid.interface]**:

  | qword | value | role (ShroudForge's naming) |
  |---|---|---|
  | 0 | `GameServer+0x1770` | wrapper (`wrapper+8` = the VoxelWorld) **[cap build/wrapper.world]** |
  | 1 | `0xa3f530` | read: `bool (Request*, wrapper)` → `0x836910(Slice<Request>{req, 1}, VoxelWorld)` |
  | 2 | `0xa44870` | origin: copies `VoxelWorld+0x30` (float3) |
  | 3 | `0xa44b60` | ready: `bool (wrapper, float4* point)` → `0x840b70(VoxelWorld, copy, level 6)` |
  | 4 | `0xa45c20` | occupied (byte-identical clone of ready → `0x848290`) |
  | 5 | `0xa45c50` | slot 4 (`0x847fc0(world, ?, 6)`) |
  | 6 | `0xa44970` | region (`0x840280(world, ?, int3, int3)`) |
  | 7, 8 | `GameServer+0x1730 + 0x290`, `0x100` | allocator |

  Kiln checks at run time that slots 1 and 3 equal the sigdb symbols `build/VoxelGridRead` / `build/VoxelGridReady`.
- Request (48 bytes): `{u16* data; u64 count; u32 dims[3]; u32 level; i32 origin[3]; u32 pad}` **[dec 0x836910]**. Per
  8³ block (`0x8369c0`): the store's map (`store+0x120`, or `+0x1f0` first when `store+0x2c0` is set) else the
  generated tile (`0x82f840`, the chunk resource via `0x8450e0`); y < 0 → `0xff01`. Output index `(z*dy + y)*dx + x`.
  Returns false when a tile was not resident (the cells are still filled from what was).
- VoxelWorld layout, from `0x840b70` **[dec] [cap]**: `+0x14` initialized, `+0x30` origin float3, `+0x40 + level*0x28`
  levels `{u32 tile_x, tile_z, count_x, count_z; …; u32* handles @+0x18}`, `+0x160` resources, `+0x174` reader lock,
  `+0x1220` store, `+0x1a34` global task; store `+0x118` lock, `+0x2c0` layer flag, `+0x2c1` "resync all peers",
  `+0x2c3` unavailable. Cell size = `32.0` (`0xbe8e5c`) / 2^level **[cap build/voxel.base_size]**.
- **[rt]** `voxel world: VoxelWorld 00000000AE4119D0, origin (0.00, 0.00, 0.00), level 6 cell 0.500 m, tiles 80 x 80 of
  256 x 256 cells (10240 x 10240 m)`; store flags in the lab: `+0x2c0 = 0`, `+0x2c1 = 1`, `+0x2c3 = 0`.

### Cells as the lab world stores them [rt]
```
column (3800, 1430) = a player's house:       column (3024, 1552) = an empty plateau:
  841.0.. air 0000                              903.0.. air 0000
  840.5 2801 grass, fill 40 (roof surface)      902.5   0001 grass material, no fill (air above the surface)
  839.5 ff01 grass (roof)                       902.0   8201 grass, fill 130 (the surface)
  837.5 ff42 material 66 x4 (ceiling)           901.5   ff01 grass
  837.0 008f material 143, fill 0 (room air)    ...     ff02 dirt
  833.5 0000 x7 (the room)
  833.0 008f
  ..    ff02 dirt
```
- Item → material: ItemInfo is the reflected resource struct; `EquipmentSetup @ +0x104`, its
  `voxelData: ChangeVoxelData @ +0x33c` → `isBuildingVoxel @ +0x440`, `placeVoxelMaterial @ +0x444` (16-byte ref),
  `placeVoxelMaterialId u8 @ +0x454` **[refl]**; PlaceBlueprint reads exactly these (`or rcx,[rax+0x444]`,
  `movzx eax, byte [r14+0x454]`) **[dis] [cap build/item.material_ref, item.material_id]**. All 74 block items map to
  128..212, e.g. Refined Stone Block 128, Rough Wood Block 143, Rough Stone Block 155, Straw Roof Block 169, Granite
  Block 192, Moonstone Block 212 **[rt]** (`build debug: item 2012474258 material 143 …`).
- `placedEntity @ EquipmentSetup+0x2bc` = ItemInfo `+0x3c0`: the template a prop item places (`or rcx,[rax+0x3c0]` in
  `0x22b260`) **[refl] [dis] [cap build/item.placed_entity]**.
- The store commit's "uniform block" test **[dis 0x83f6e0]**: a block compresses to one value only when every cell is
  `0x0000`, or material 1..127 at fill 255: building materials and partial cells are always stored per cell.

## 3. Writing: the block writer and the store commit

- `0x83fa70` (`build/VoxelWrite`) `void (VoxelWorld*, Span{u16* data; u64 count}*, u32 dims[3], i32 origin[3])`
  **[dis]**: loops z, y, x in steps of 8 (dims must be multiples of 8), copies each 8³ block from the source (index
  `((z)*dy + y)*dx + x`) into a stack block and calls the commit `0x83f6e0(store = world+0x1220, &blockOrigin, level 6,
  &Span{block, 512}, flag = store+0x2c0)`. It is reached from the terraforming impact (`0x18e0b0 → 0x2284a0 →
  0xa3dae0 → 0xa32c00 → 0x83fa70`) and from 11 more brush kernels (`0xa30180 … 0xa35b40`) **[dis]**: ShroudForge's
  contract (one call; `[rcx+0x1220]`; `[store+0x2c0]`; count 512; level 6) matches unchanged.
- `0x83f6e0` (`build/StoreCommit`): takes the store write lock (`lock add [store+0x118], 0x10000` … `0x7fff0000`),
  checks uniformity, compresses (`0x845960`, fallback `0x7f7c40`) and inserts with `0x83ea70` into the 32-cell chunk map
  (`key & 0xffe0`, 64-entry LRU of hot chunks). Its other callers: `0x84aed0`, `0x851820`, `0x852ee0`, `0x8535c0` (the
  game's own edit paths). **[dis] [dec]**
- Replication: `0x83ba40(store, stream, peer, …)` serializes changed blocks per peer (32 peer states of 0x138 bytes at
  `store+0x2c8`; `store+0x2c1` set → every peer resyncs) **[dec]**: any store commit reaches clients through it.
- Kiln widens every write to whole blocks (reading the cells around it), commits **only the blocks whose cells
  changed** (an unchanged block would turn generated terrain into a stored edit) and reads the box back:
  `shape rc 0 flags 0x43 changed 144 blocks 8 …` (CHANGED | VERIFIED | KEPT) **[rt]**.
- Thread: the tick thread (Pre/Post): no world job runs; the functions take their own locks.

## 4. Streaming on a dedicated server

- Right after boot only the 2 x 2 tiles around the base are resident (`tiles` map: `4 of 6400 tiles loaded`) **[rt]**.
- `0x8450e0` (chunk-resource lookup used by the reader) stamps the entry with the resource system's frame counter
  (`resources+0x1f8`) even when it is not loaded, and the resource system then loads it: a tile read once at
  (3000, 1430) was resident 2.5 s later and stayed while touched **[rt]**. Requesting 256 tiles loaded 199 in ~12 s and
  evicted older ones (the base tiles were reloaded by the game shortly after) **[rt]**.
- Without the tile, reads return only the store's blocks: below a player's base the column read air under 772 m
  **[rt]**. Kiln therefore refuses edits whose box is not resident (`KILN_BUILD_R_NOT_LOADED`), asks for the tiles, and
  the managed API waits for them.

## 5. What the game saves: base regions only

- Save state machine `0x7c3aa0` (server thread, serverSaveTick): `0x932a40(&result, Array<Region>* list,
  BuildZoneSystem* = [ctx+0x30])` builds the list: for every BuildZoneSystem base zone with `+0x46 == 0` a 0x30-byte
  region `{BaseId[16] (zone +0x30), float3 min, pad, float3 max, pad}` **[dec] [cap build/SaveRegions,
  save.zones, zone.abandoned]**. For each region it calls `0x83db00 → 0x83d200(stream, store, aabb, world+0x1bc)`,
  which writes the store's level-6 chunks (`store+0x128` map, key top u16 == 6) overlapping the region (touching
  counts), with a 0x1000-byte material table **[dec]**.
- Load (`0x7c0560`, case 6): every per-region blob in the save is applied (`0x831f90 → 0x830e30` into the store),
  without checking for a base **[dec]**.
- **[rt]** a pillar written on the plateau (790 m from the base) was gone after a SIGINT restart; the same write inside
  the base zone's corner (3836, 797, 1470) was back. That is the game's own rule: terrain outside bases resets.
- Kiln keeps the 16 m chunks its writes touch (unless `KILN_BUILD_F_TRANSIENT`) in
  `kiln/data/kiln.build/regions/<world GUID>.json` (world GUID from kiln.saves) and its hook on `0x932a40` appends greedy-merged boxes after the game's own
  entries (`BaseId = "KILNBILD" + index`), growing the array with the game's grow callback. **[rt]**
  `save regions: the game's save now writes 1 Kiln box(es) (4 chunk(s)) after its 1 base region(s)` and the plateau
  pillar survived the restart (`column (3024, 1552)` identical before and after).
- The builder is called twice per tick for some seconds after boot and at each save (245 calls in the first minute)
  **[rt]**: the hook caches its boxes.

## 6. Props: the building helper

- `Building_spawnProp` `0x2248e0` (PlaceProp `0x2253d0` calls it) builds the spawn components **[dec] [dis]**:
  `CurrentTransform 0x027ac564` (0x38), `DisableRandomLootOnDestroy 0x72462ce1` (1), `UsedItem 0x0b02361d` (4, when
  the item is non-zero), `CollisionFeedbackMaterialComponent 0x38349d86` (0x2c, only when its GUID argument is
  non-zero; PlaceProp passes zeros), then the queued create `0x5fb340`. Component flags from EMF's components list:
  UsedItem `is_dynamic, savegame_relevant, hidden`; DisableRandomLootOnDestroy `server_only, is_dynamic`.
- Kiln spawns the same set with SpawnWrapper (immediate, tick thread). **[rt]** `place prop: 2542
  Prop_Decoration_T1_Storage_24 item 213939880 … flags Placed, Saved`; after SIGINT and kill -9 restarts `6 of 6 props
  back` (props are entities: the game saves them anywhere).
- Player-placed props carry UsedItem (a player's chests, workbench, torches, doors: `item 4044650321` …); world props
  (scatter loot, stones) do not.
- Child entities (lights, offers) are created inside the parent's create; Kiln lists only root entities (record
  `+0x14` parent = 0).

## 7. Bases: BuildZoneSystem

- `BuildZoneSystem_addZone` `0x916320(bzs, list, flag, ownerKind, WorldAABB*, entity, abandoned, BaseId*)`: list 0 →
  `bzs+0x10` (bases), 1 → `+0x38` (no-build), 2 → `+0x60` (dynamic, around creatures/NPCs) **[dis] [cap bzs.*]**;
  append `0x952ae0`: entries of 9 qwords (`lea rcx,[rax+rax*8]`): `i64 min[3], i64 max[3]` (32.32 metres),
  `BaseId[16]`, `u32 entity @+0x40`, flag, owner kind, abandoned-derived byte **[dis] [cap zone.entity]**.
- **[rt]** `altar 1025 at (3800.0, 832.5, 1434.0) zone half (60.0, 60.0, 60.0)` (the altar's own BaseIdComponent has
  buildZoneSize 0 on the server; the zone list is the authority). List 1 held 300 no-build boxes, list 2 six NPC boxes.

## 8. Not done / open

- Calling the game's blueprint shape builder (`0x22a2a0`) and `VoxelWorld_placeBlueprint` `0xa3cbd0` directly (block
  items with their exact shapes and the player's material) is not implemented: Kiln writes cells. Blocks from a
  schematic captured from player buildings carry the exact cells the game wrote.
- The terraform impact `0x18e0b0` needs a full impact context (program-context pointers, item context, output
  queues; ShroudForge reached the same conclusion): not called. Kiln's shape edits write cells into the same commit.
- Material-only air cells (fill 0) next to surfaces: Kiln's shapes write them where the game would; whether the client
  needs them for texturing is unverified (client checklist).
- The second store layer (`store+0x1f0`, used when `+0x2c0` is set) is unused on the dedicated server.
- Client exe (build 26404966): every `build.*` feature resolves offline from the shared `server.d/build.json` (the
  interface builder exists twice there, the client world's and the hosted GameServer's; the pattern is anchored on the
  GameServer's `cmp [rdi+0x1770], 0` after the call). Not run in a hosted world yet.
