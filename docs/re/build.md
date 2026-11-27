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
| Grid | level 6 of a 7-level baked pyramid; cell = 32 m / 2^6 = 0.5 m; origin (0, 0, 0); 20480 x 20480 cells (10240 m); streaming tiles of 256 x 256 cells (128 m), 80 x 80. Corrected dev.28 [static] [data]: below 0 falls back to `ff01`, but that is not an unbounded usable world; scene reset bounds and packed store coordinates impose separate limits (§9). |
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
  levels `{u32 tile_size_x_cells, tile_size_z_cells, count_x, count_z; …; u32* handles @+0x18}`,
  `+0x160` resources, `+0x174` reader lock (dev.28 correction [static]: the first pair are tile sizes, not coordinates),
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
  **[rt]**. Those are three different callers, and only one of them is the save: §12. Since kiln#136 Kiln's boxes go
  into the world save's list only, and only for kept chunks the base entries of that list do not save whole.

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

### Whole-stroke building-tool guard (since 0.2.0-dev.30.5)

The guard in `native/services/tools/hooks/build_area.cpp` is an additional restriction **inside an existing engine
impact**, not a replacement permission check and not a direct voxel-write authorisation. A sphere must fit inside
**one** current, non-abandoned base box on all three axes. Exact tangency is accepted, crossing is refused; overlapping
boxes are not merged. Unknown shape kinds, missing geometry, unreadable memory or missing mappings refuse. Exact
fixed-point bound residuals preserve tangency, and error-free addition residuals catch crossings smaller than one double ULP.

Independently checked on server 26430253 (`f0d824be…`) and client 26430625 (`5d9ef2f1…`), using read-only disassembly
and the offline resolver, not the third-party mod implementation [dis] [cap `tools.build_area`]:

- `TerraformImpact` S `0x18e0b0` / C `0x3ece10` gets its current program Access from impact context `+0x30`.
- That Access is a program iterator: its **qword 0 is the engine's ProgramContext**, not the world (corrected
  0.2.0-dev.30.8, kiln#133; the binding had compared it with the server world and refused every stroke). The context's
  job (`+0`) names the ECS storage of the world running the program at `job + world/job.storage` (`0x30`;
  `world/JobStorage` S `0x65ba10` / C `0x9bfb80` is `mov rax,[rcx+0x30]`). For the server world that storage is
  `world + world.storage` (`0xe179c0` on both executables), `world` being the address `KilnApi.server_world` returns:
  the server-world test kiln.items' tool wear and power scaling already make (`JobStorage(job) == world + world.storage`).
  TerraformImpact passes the same qword to its component reads (S `0x179e60` loads `[ctx]` and calls `0x65ba10`) and
  to `world/CurrentEntityId` (S `0x65b870`). [dis]
- Its result is the caller's `NativeResult` (`args[0]`): a code at `+0` and, with code 0, a value at `+8` that is the
  success flag it also hands `BuildFinish` (S `0x22a5f0`). A stroke whose building cursor flags are not clear ends at S
  `0x18ea6d` with code 0 **and value 0**; the finished operations write value 1 when their own call returned 0 (S
  `0x18f4e5`). Other codes: 6 cancelled (S `0x18ebb1`), 0x11 (S `0x18eb2b`), 0x18 a missing argument (S `0x18f792`).
  So code 0 alone does not mean the stroke did anything; Kiln's action trace (0.2.0-dev.30.8) reads both. [dis]
- S `0x18e870` / C `0x3ed5d0` loads `BuildZoneSystem*` from that Access `+0x88`, immediately before calling the
  game's base-area intersection helper, S `0x94af20` / C `0xea3cd0`, with the brush's float AABB.
- That helper reads base data `+0x10`, count `+0x18`, skips entry `+0x46` (abandoned), and dispatches shape `+0x44`
  (`0` box, `1` sphere). Owner kind is `+0x45`. The existing build captures qualify stride `0x48` and altar `+0x40`.
- `sigdb/symbols/server.d/tools_area.json` captures those fields from the impact's own call path on both binaries.
  Boot accepts only this exact layout. The guard reads under the caller's live program Access at execution, before
  the impact switch; it never calls the tick-only singleton API or remembers an earlier successful preview.

The original impact must still run unchanged after this guard: it owns the actor's permissions, server cursor/range
and forbidden-area checks. This guard does **not** grant visitor rights. Do not invoke its helper to authorise an
unrelated `Build.Write`. Client input, a stale altar preview or an earlier successful containment result is not authority.
The engine's Access contract must remain held through the guarded impact. Before accepting, the guard re-reads the
containing record byte-for-byte (including altar, identity, bounds and abandonment) and the list's data/count header;
any change or unreadable validation read refuses the whole stroke. All header and record address ranges are checked
for overflow and the production reader guards every copy with SEH. Only a containing record can admit a stroke, so
non-containing records do not need to be retained for validation.

The guard runs at the impact binder's entry, **before** the original impact's own base-intersection read at
S `0x18e870` / C `0x3ed5d0`; it is not a hook at that exact instruction. Both use the same invocation's Access and BZS
source. The vanilla helper also walks this live array without a visible local lock or version check [dis], but this
alone does not prove reader/writer exclusion by the engine scheduler. No qualified BZS version counter or lock has
been established. Re-reads conservatively detect observed changes; full concurrency, ABA and changes after the last
validation read are **not proven safe**. Do not describe the Access pointer itself as a synchronization primitive.
Real player visitor/protected/range behaviour and a live altar change remain owner-guided game checks; unit tests of
geometry and deterministic mid-read changes do not establish those outcomes.

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


## 9. Baked terrain and current call contracts (dev.28; server 26430253 / client 26430625)

S `f0d824be…`, C `5d9ef2f1…`. Source: dev.28 dossier chapter 19f, terrain REPORT/VERIFY (18 confirmed, one
corrected for ready-point alignment/in-out behavior). These named current-build RVAs supersede the older wrappers
and save-region builder above; unrelated older addresses retain their original build scope.

### Baked base, overlays and scale [static] [refl] [data] [lab]

The main terrain is **baked and streamed, not generated from a new-world seed**. Main SceneResource, solid/fog
VoxelWorldResource and BiomeMapResource are byte-identical on server/client. An independently decoded 2,000-cell
column matched a fresh live world byte-for-byte. This does not establish how the editor authored the terrain or
exclude randomness in spawners/decoration; no seed-to-terrain generator was identified on the traced path. [inferred]

Main GUID `509feadb-4c60-425f-9c7c-deeefd9b6920`:
- VoxelWorldResource type `0x75f48072`, reflected header **4360 B** (328648 B including arrays); part 0 Solid,
  part 1 Fog. Material GUIDs[256] `+0x1c`, default material `+0x101c`, CPU-displacement reference `+0x1020`, seven
  inline levels `+0x1040`, voxelHashes reference `+0x10e8`.
- Resource VoxelWorldLevel stride **24 B**: tileSize uint2 `+0`, tileCount uint2 `+8`, BlobArray of 32-byte content
  references `+0x10`. Runtime level stride is **40 B**, not 24.
- Finest level 6: 0.5 m cells, 256² cells / **128 m tile**, 80² = 6400 tiles. Pyramid levels 0..6 use
  32,16,8,4,2,1,0.5 m cells. Solid tile sizes at level 6 are **132098..5306764 B**, median 440227, not 150–360 KB.
  All-level referenced bytes: solid 4401626408, fog 1269065268 (not deduplicated archive sizes).
- Scene entities are a separate **256 m** partition: SceneResource type `0x2bc68ebe`, 208 B header,
  entityChunkCount `+0x6c=40²`; 1600 regular parts plus global part 1600. See [map.md](map.md) for quantization tolerance.
- BiomeMapResource type `0x6287e034`, 28 B header: origin int2 `+0`, size uint2 `+8=1280²`, scale `+0x10=8 m`,
  byte-enum array `+0x14`. Biome bytes alone neither reshape nor repaint terrain voxels.
- Solid size `(10240,2508,10240)`, fog `(10240,2188,10240)`; scene reset bounds `(0,0,0)..(10240,2560,10240)` m,
  horizontal/vertical margins 10/2. These are distinct height limits. Edited-store chunks are **32 cells / 16 m**;
  edit blocks **8 cells / 4 m**. Do not mix any of these scales.

VoxelWorldChunkResource (224 B, seven content references) and SceneScatterResource exist in reflection but have
**zero shipped instances on both sides**; this world references tiles directly. voxelHashes is 6553600 B,
CPU-displacement 98680 B; their generation/consumer contracts remain incomplete. No new LOD/nav/water/relief-map
regeneration is established by the base-tile experiment.

### Tile envelope and decoder [static] [data]

`u8 blocksX; u8 blocksZ; u24le offsets[blocksX*blocksZ]; payload[]`. Offsets are relative to **payload**, starting
at `2+3*blocksX*blocksZ`, not tile start. A finest 32²-column header is **3074 B**, not 3072. Column index is
`bz*blocksX+bx`; end is the next offset or payload end. Levels 1–4 use 16² columns (770 B); level 0's last row/column
has 8×16, 16×8 or 8² (386/194 B), so dimensions are per-tile, not constant for a level.

Column: `{u16 verticalBlockCount,u16 controlByteCount,controls[],data[]}`. LSB-first continuation/value run codes;
uniform material/fill runs or **10-bit compressed-block byte lengths**. The compressed 8³ codec separates material
and fill runs, reading control bits at the front and literal bytes at the tail; it is not zstd/generic RLE. Output
X-fastest, then Y, then Z. The independent verifier's decoder matched all 2000 live column cells.

### Kiln's tile codec, checker and lab pack (dev.32) [static] [data] [lab]

Tools only: nothing here reaches mods, and `terrain.baked` stays off. `tools/kfc/kfc/voxeltile.py` reads and writes
tiles (envelope, column runs, the 8³ block codec; its docstring is the format), `voxelworld.py` the
VoxelWorldResource root (layout hash `0xce3a352f`, refused otherwise; grid, edge dimensions, reference patching),
`terraincook.py` a lab qualification pack; `kfc.py terrain info|check|column|cook` runs them. Tests:
`tools/kfc/tests/test_voxeltile.py` (synthetic) and `test_voxeltile_game.py` (the game's tiles, server or client
folder). Details the bits settle, beyond the outline above:
- A column run is `gamma(count)`, then 0 = uniform: a "new value" bit (0: the column's last uniform value, at first
  air), after a 1 a "fill given" bit; the payload holds the material byte, then the fill byte. Without a fill byte the
  fill is 0 for air and 255 otherwise: the per-request reader (server 0x8369c0) derives 0xff for any nonzero material,
  building materials too [static]; the live column proves it for terrain, no lab a building material's, so Kiln's writer
  always writes a building material's fill byte. 1 = `count` compressed blocks, one 10-bit length each. Payload in run
  order. The reader fills its window to 25 bits per run header and does not refill inside one. The same reader does not
  check the block decoder's result (0x8514b0's 9): a corrupt block is served as far as it decoded, so a bad tile is
  refused offline, never handed to the game.
- A block's material run takes a literal or the material of the run **before the last one** (at first 0); a fill run
  a literal or the default (0 air, 255 terrain, the last literal building fill); a terrain partial fill (1..254) is one
  cell without a run length. A block longer than 1023 bytes cannot be written (10-bit length): Kiln's writer refuses it.
- **The game stores some one-value blocks compressed** (a 5-byte all-air block, mostly at a column's top; in fog a
  7-byte material-16/fill-0 block), next to identical blocks it stores uniform: a choice of its encoder the cells do
  not show. Kiln's writer rebuilds the game's columns byte for byte when it keeps that choice (`Column.compressed`);
  without it 81.5% of all columns come out the same (solid level 6 97.5%, its coarser levels all but 37 columns; fog
  59-80% per level, a tile 0-100%) and the rest differ only in those blocks.
- **Every shipped tile** passes (2026-11-27, the lab2 server's files, game version 1287369 of 2026-11-04;
  `kfc.py terrain check --all --canonical`, 2.5 h with 3 workers): 18,964 distinct tiles behind 20,268 references,
  both parts, all seven levels, 5.59 GB; **0 refused**. Every rule the reader relies on holds, every compressed block
  decodes, every tile's dimension bytes match the root's grid, the structural rebuild is byte-identical, and Kiln's
  writer rebuilds all 16,295,808 columns byte for byte from their cells and the game's block layout. No shipped column
  sets the unused bits of its last control byte, and no uniform run of a building material (128 and up) leaves out its
  fill byte.
- **Every tile reference's hash is the game's ContentHash of its bytes**: the GT review recomputed it
  (`kfc.contenthash`) for all 18,964 tiles, 5.59 GB, and every one equals its reference, the key the game reads the
  tile by (222 s with 3 workers: the pure-Python hash runs at about 7 MB/s, so `--hash` is cheap). The shipped
  7,170-byte all-air tile (`021c0000 89b408f3 7ff5bfd7 deb84d26`, fog level 6) is exactly `empty_tile(32, 32)`. The
  review also read 15 tiles (level-0 edge tiles, levels 2 to 6, both parts, 480 million cells) with the dossier's own
  research decoder (19f `evidence/column_decode.py`): every cell equals `voxeltile`'s.
- Coarser levels are **not** a simple function of level 6: over 894 mixed level-5 cells of tile 983's area, the fill is
  within 1 of the mean of its 8 level-6 cells in 80%, the material is the majority of the filled children in 76%, and
  level 5 keeps materials on empty cells (`0001` over `0000`/`0301`). A cooker's LOD filter has to be derived, not
  assumed.

[lab] `mods/tests/terrain-tile-test` (a fresh dedicated lab world, Kiln 0.2.0-dev.31.21 with this lane, 2026-11-27):
the server reads a tile Kiln's writer made, and a reference moved to another shipped blob, exactly as the pack holds
them. The pack (`kfc.py terrain cook`, the test's `plan.json`): solid level-6 tile 983 with a 64³ box of random cells
(terrain and building materials, partial fills) and a 16×600×16 pillar through the ground, served by kiln.assets as
one blob entry under the game's own ContentHash (an explicit `hash`); tile 1063 pointed at the shipped all-air blob (no
blob entry); the patched root as a boot resource override. Read back through kiln.build: **8 PASS, 0 FAIL**. Both
edited boxes, the re-encoded ground under the pillar, an untouched column (the dev.28 lab's `fde3693b…`), a box across
the 983/984 seam, the reused air tile and a column of untouched tile 984 match the pack's cells (one SHA-256 per box),
and the game's log has no "Could not find content". Server only: the coarser levels, voxelHashes and CPU displacement
stay stale, and no client joined.

kiln.assets reads a blob's `hash` text as `<size in decimal>-<h0>-<h1>-<h2>` (the words in hex). The first lab run
wrote the size in hex: kiln.assets could not parse it, served the tile under the key it derives itself with no error,
and the game logged `Could not find content '368de20f…' in kfc!` (tile 983 did not load).

### Current native anchors (Win64) [static]; Build calls also [lab]

| Role | S / C RVA | Contract / limitation |
|---|---|---|
| `build/VoxelGridRead` | `0xa3f520` / `0xa94c20` | `bool(ReadRequest*, VoxelWrapper*)`; wrapper +8 holds VoxelWorld |
| batch reader | `0x836910` / `0x10b20e0` | `bool(Span<ReadRequest>*, VoxelWorld*)`; ANDs base-column readiness |
| `build/VoxelGridReady` | `0xa44b50` / `0xa9a250` | `bool(VoxelWrapper*, float point4[4])`; **16-byte-aligned input**, fixed level 6 |
| ready implementation | `0x840b70` / `0x10bc130` | `bool(VoxelWorld*, float point4[4], u32 level)`; point is **in/out** |
| `build/VoxelWrite` | `0x83fa70` / `0x10bb030` | `void(VoxelWorld*, Span<const u16>*, const u32 dims[3], const i32 origin[3])`; whole 8³ blocks, level 6 |
| `build/StoreCommit` | `0x83f6e0` / `0x10baca0` | `void(Store*, const Int3*, u32 level, const SpanU16*, u8 layer)`; 512 cells |
| `build/SaveRegions` | `0x932a30` / `0xe8b8f0` | `KeenResult*(out, GameArray<Region>*, BuildZoneSystem*)` |
| `build/RetSaveRegionsWorldSave` | `0x7c3d99` / `0xe67709` | return address of the world save's call (§12); compared, never called |
| `build/RetSaveRegionsBaseZones` | `0x7bc7d5` / `0xe6031d` | return address of the ECS base-zone publish step's call (§12) |
| `build/RetSaveRegionsBaseQuery` | `0x7b4546` / `0xe58696` | return address of the ECS base query callback's call (§12) |
| VoxelWorld factory | `0x834510` / `0x10afdc0` | apparent `VoxelWorld*(opaque Config*)`; lifetime/config ABI not qualified |
| acquire column | `0x82f840` / `0x10ab270` | `ColumnCursor*(out, VoxelWorld*, u32 level, packed{u32 blockX,blockZ})`; borrowed locks/resources |
| touch tile span | `0x8450e0` / `0x10c06a0` | `Span<const u8>*(out, streamingResources*, u32 generationHandle)`; borrowed, locked |
| seek vertical block | `0x84adc0` / `0x10c6170` | low-byte bool `(columnIterator*, u32 blockY)`; mutates cursor |
| decode block | `0x8514b0` / `0x10cc7d0` | `KeenResult*(out, Span<u16>* dst, Span<const u8>* compressed)`; codes 0/9 |
| save/load edited store | `0x83d200` / `0x10b89b0`; `0x830e30` / `0x10ac730` | analysis anchors, raw stream/allocator ABI unqualified |
| replicate edited store | `0x83ba40` / `0x10b7200` | per-peer edited blocks, **not baked asset distribution**; raw ABI unqualified |

Factory/column/touch/seek/codec/save/load/replication are inspection anchors, not newly safe mod calls. Use existing
Build/Assets routes. Ready wrapper uses `movaps` and protects its caller with a stack copy; the inner implementation
writes origin-relative XYZ back. An ordinary unaligned float[4] can fault. Ready also gates initialized/global-task/
store availability, **not game Run state**. Build.Write separately requires Run; an early lab read succeeded while
its write was correctly refused. Raw writer checks neither multiples-of-8 nor span count: preserve caller guards.

ReadRequest 48 B: data pointer `+0`, count u64 `+8`, dims uint3 `+0x10`, level `+0x1c`, origin int3 `+0x20`, pad
`+0x2c`. Cell index `(z*dy+y)*dx+x`. Runtime world: initialized `+0x14`, origin `+0x30`, levels `+0x40` stride 0x28
(tile **sizes in cells**, counts, handles `+0x18`), resources `+0x160`, lock `+0x174`, edited store `+0x1220`, global
task `+0x1a34`; store lock `+0x118`, alternate layer `+0x2c0`, unavailable `+0x2c3`. [static]

Save-region builder **clears** the output list, skips abandoned zones, grows 0x30-byte records; success error 0,
grow failure `0x27`. Region `{BaseId[16],float3 min @0x10,pad,float3 max @0x20,pad}`; game array `{data,count,cap,grow}`
qwords, grow `(array*, negativeRequiredCount, u8 mode=1)`. Save writes version 6 and a **4096 B / 256-GUID** table;
load accepts versions 2..6 and remaps material bytes. [static]

### Read precedence, bounds and retained edits [static] [lab]

Reader acquires the base column, but chooses each block from alternate edited store (when selected), ordinary store,
then baked base. An unavailable in-grid base tile makes the result false **even when store hits filled all cells**.
Outside the tile grid, cursor/Ready can report true with air (or `ff01` below Y=0). **Readiness is not bounds validation.**
TouchTile requires a nonzero, current-generation handle: zero/stale can try to lock address `0x44` and fault; a
successful borrowed span retains its lock until release. Do not invoke this as a generic resource reader.

Store lookup S `0x845310` key is `(level<<48) | ((z&0xffe0)<<32) | ((y&0xffe0)<<16) | (x&0xffe0)`; coordinate
fields are 16-bit, chunk-aligned. Expanding tile bounds alone allows coordinate aliases. Failed decode of a stored
block is **fatal**, not a skipped edit. Store lookup is level-specific: level-6 precedence does not prove that a
commit masks coarser LODs (coarser propagation through `0x83ea70` was not checked).

[lab] Three boots, 16 PASS: one kept and one transient 512-cell block; restart kept the former, dropped the latter,
and untouched terrain matched its original hash. A 7170-byte air-only baked tile override removed the original
ground while the saved edit above it and adjacent tile remained. A stored block shadows all its cells, not only
changed ones; one-cell edits can retain old surrounding terrain across a base patch. This establishes existing-world
base replacement where saves do not mask it, **not** a complete cooker, rendered client result or per-world isolation.
Boot override was process-wide; no custom tile was distributed to a client. [inferred] Matching client data is needed.

The air fixture used **Assets' SHA-256-prefix key**, not native ContentHash. The same bytes already ship in 624 fog
references under native key `021c0000 89b408f3 7ff5bfd7 deb84d26` (the game's AES-round hash). Server acceptance of
the fixture does not prove a universal hash rule or client acceptance. See chapter 19f for native-hash qualification,
all eight unique S/C inspection signatures and remaining derived-data gaps; no production sigdb change is implied.

## 10. Server voxel rays (dev.28; server 26430253 / client 26430625)

S `f0d824be…`, C `5d9ef2f1…`. Dossier chapter 01; queries VERIFY confirmed ABI/layout and corrected the raw
outside-grid behavior. **No client-to-server port is needed:** the server already contains this ray. [static] [lab]

`queries/VoxelRay` S `0x6ac7e0` / C `0xa0f770` (Win64):
`void* f(void* hitOut, VoxelGrid*, const float fromAligned4[4], const float toAligned4[4], const u8 options[4])`.
**Output first, grid second**; returns hitOut, not bool. Absolute float metres/Y-up, both endpoints 16-byte aligned
and 16 readable bytes. Options are a **pointer**, not by-value: `{realm,0,1,1}`; option 1 unestablished/unused,
option 2 stops on unavailable, option 3 skips terrain material indices <0x80 with fill 1..8. Not a prop/water filter.

Grid realms pointer `+0x1bf8`, live u64 count `+0x1c00`, stride 0x3870; each realm's first qword is its ray-grid
pointer. No native realm-index bounds check. Cache `realm+0x3858..0x3868` is **mutated per call** and can queue
missing chunks at `realm+0x2030`. Serialize with engine jobs: Post tick was tested; a mod mutex does not exclude
engine workers. Resolve the current server world, including on a host, and do not retain pointers across sessions.
[static] [lab] Live counts were 10 dedicated / 8 hosted [seen], not fixed maxima.

Hit prefix: float XYZ `+0/+4/+8`, material u32 `+0xc` (lookup through grid `+0x60`, not raw material byte), packed
voxel u16 `+0x10`, available byte `+0x12`, hit byte `+0x13`, **last-cell-outside** byte `+0x14`.
21 meaningful bytes, not a proven C++ sizeof; an aligned zeroed 32-byte allocation is sufficient for this prefix.
No normal, exact cell, entity or distance is stored. Expanded cell boxes can put a forward hit at x=3846.999 for a
boundary at 3847: `floor(hit*2)` does not recover the exact cell. [static] [lab]

**Corrected by verification:** per-cell reader S `0x660de0` / C `0x9c4e30` returns 0 for data/known-empty, 3 for
missing/queued, **0x47 for outside the ray grid**. The ray treats 0x47 as **loaded and empty**; hit+0x14 remembers
only the last cell, not the whole segment. Thus available=1/hit=0 alone does not prove clear. [static]

Before native entry, validate realm<count and a live grid. Let `g=*(realms+realm*0x3870)`; ray-grid origin float3
`g+0x70`, shift u32 `g+0x10`, horizontal count u32 `+0x14` (X/Z), vertical `+0x18`. For **both actual narrowed float
endpoints**, per-axis `d=p-origin` must be finite, `0<=d<2^31`, and `(int32(d)>>shift)<count`; validate shift 0..30,
positive counts and finite origin. This is the lower-inclusive/upper-exclusive convex box from the reader; refuse an
outside endpoint before calling, not a clear ray. Last-cell flag is only an extra post-call guard. [static]

Also reject zero/float-collapsed segments: length<=`1e-20f` exits without testing anything, still available/no hit.
The captured minimum data is S `0xbd8628` / C `0x13d2a78`, bits `0x1e3ce508`. `@queries/ray.minimum`,
`@queries/ray_grid.bounds`, `@queries/realm_count.layout` guard these fields; realm-count anchors S `0x6c982c` /
C `0xa2cf7c`. Validating only the original double endpoints misses float collapse/boundary rounding. These are
safety checks derived from static code; outside-grid refusal wrapper was not itself lab-tested.

[lab] Explicitly loaded, no connected player: 1000/1000 ground hits, clear air misses, and terrain/building-wall
hits. Build.Ready and ray-cache readiness can differ across steps; unavailable is **not air**. [seen] Hosted session 2
confirmed ground/air calls, but wall writes were base-protection refusals (`write=6 flags=8`), so subsequent misses
were invalid obstruction tests. No hosted-wall or reload proof follows. This ray reads **voxels, not colliders**:
server `path_check=0x8ed50` has ColliderSystem/VoxelGrid access, and `0x66d2c0` calls both ray and collider query
`0x6a99d0`, but that collider ABI/filter/lifetime remains unqualified. Entity-origin overlaps are not collider overlap.
Path queries and their result lifetimes are in [creatures.md](creatures.md) §13.

## 11. Altar count and build-zone size (dev.28; server 26430253 / client 26430625)

S `f0d824be…`, C `5d9ef2f1…`; dossier chapter 18, survivors VERIFY claims 1–3/11–13. [static] [refl] [data]
Count is a world Mother-Flame-level rule, not per-player. Size is separately indexed by the altar's own level.

| Function | S / C RVA | Win64 contract |
|---|---|---|
| count limit | `0x93dea0` / `0xe96c80` | **`uint32_t(BalancingRegistry*, uint32_t flameLevel)`**, zero-extended EAX |
| full size | `0x933b90` / `0xe8ca50` | `SizeResult*(out /*16 B*/, BalancingRegistry*, uint8_t altarLevel)` |
| upgrade_building_range | `0x95400` / `0x283710` | `void(ProgramContext*)`; full size ×0.5 → TriggerShape ranges |
| add_buildzones_server | `0xee2f0` / `0x32e600` | real program context; rebuilds zones from transform/TriggerShape |
| add_buildzones_client | — / `0x32e0f0` | presentation program, flags `0x201` |
| base_player_action | `0xc0950` / `0x2d49b0` | real program context; reserve/deactivate altar input |

BalancingRegistry table pointer `+0x20`; table BlobArray<u8> altarsPerFlameLevel `+0x1b8`,
BlobArray<float3> buildzoneSizesPerAltarLevel `+0x1c8` (count `+0x1cc`). Count = min(table[level],16), null table
returns 1, **no level bounds check**; zero self-relative displacement is unsafe. Every caller consumes full EAX,
not AL: uint8_t was a decompiler/probe mistake corrected by verification. Size returns out, XYZ `+0/+4/+8`,
has byte `+0xc`; failure initializes **only has=0**, so do not read XYZ then. Both leaves lack .pdata and contain
initial short conditional branches; detour trampolines must relocate them. [static]

[data] Flame max 20, altar max 5. Flame-level counts 0..20: `[1,2,4,6,8,10,11,12,13,14,15,16,16,16,16,16,16,16,16,16,16]`.
Full cube sizes at altar levels 0..5: `[0,40,80,120,160,160]` m. **12 is level 7's value, not the hard maximum**;
16 is reached at level 11. Placement also adds nonempty entries in the 16 **peer-reservation** slots (24 B stride,
PlayerBasesSystemServer `+0x1bf0`, S helper `0x208df0`) to ServerProgressSystem.currentAltars `+0x1c`; those slots are
not a 16-base storage array. Client cursor helper `0x29ad70` calls its local getter at `0x29aebc` and sets
MaxAltarCountReached `0x40` when current>=limit. A server-only count override does not remove that refusal. [static]

TriggerShape is 56 B: kind `+0`, half-range XYZ `+4/+8/+0xc`, offset `+0x10`. BaseIdComponent is 48 B: BaseId `+0`,
**non-authoritative** buildZoneSize `+0x10`, endOfTimeout `+0x20`, abandoned `+0x28`. ClientBaseIdComponent 56 B has
BaseId `+8`, abandoned `+0x18`, bed count `+0x30`, **no size field**. ServerProgressSystem: progressEntity `+0x10`,
flame level `+0x14`, count `+0x1c`; NetworkServerProgress count is u16 `+4`. Do not set these mirrors to change policy.
[refl] [static]

[lab] Actual size consumers saw 40→80 full size, 20→40 TriggerShape half extents, and matching server zones. Count
hook installation/trampoline/pass-through worked, but **all four count calls were probe-owned**, zero from game
consumers. Direct spawning bypassed that gate; the old preflight log claim was false. Static consumer coverage
supports the seam (server calls `0x9e99c`, `0xc0d36`, `0xc6655`), not observed normal third-altar placement.

Runtime metadata [lab]: TriggerShape and ClientBaseIdComponent replicated/not saved; Level saved attribute/not
directly replicated. UpgradeRange has flags `0x0`, while client zone builder has client bit `0x1`. Larger shape
therefore **has a replication path to vanilla clients [inferred]**, but arrival/presentation (C18.3) was not tested;
metadata is not client evidence. >16 bases, per-base sizes and native UI remain unqualified. Full service/preservation/
synchronization designs stay in dossier chapter 18, not these engine contracts.

## 12. Save regions: three callers, what they feed, and the Flame Altar count (kiln#136; server 26430253 / client 26430625)

S `f0d824be…`, C `5d9ef2f1…`, svn 1287369. Ghidra headless decompiles of the read-only copies, capstone, the game's
reflection records. [static] unless tagged. Trigger: kiln#136 (thanks bisdat): a world with one 120 m altar showed 21
Flame Altars after a mod's 39,831 persistent removals inside it (113 kept chunks, 20 Kiln boxes); 41 for 2 on his
original world, with build-zone and storage behaviour changed; 2 with Kiln off.

**The builder.** `build/SaveRegions` (S `0x932a30` / C `0xe8b8f0`) clears the list, then writes one 0x30-byte entry
per BuildZoneSystem list-0 zone with `+0x46 == 0` (not abandoned): BaseId from zone `+0x30`, min/max from the zone's
six fixed-point coordinates (`+0x00..+0x28`) **[dec]**. Its neighbour `0x941d30` writes the same entries for every zone,
abandoned ones included; `0x93ea20` and `0x93fea0` write the no-build and playable-area lists.

**Its three callers** (sigdb names the return addresses, `sigdb/symbols/server.d/build.json`):

| Caller (S / C) | Returns to (S / C) | What the list becomes |
|---|---|---|
| world save `0x7c3aa0` / `0xe67410` | `0x7c3d99` / `0xe67709` | one base record per entry: BaseId, bounds, voxel blob (`0x83d200`), water blob (C `0x11631a0`); both log "Failed to save 'ServerSaveGame', while getting base zones" when it fails |
| step `0x7bbeb0` / `0xe5fa70`, when `0x94a850(bzs)` reports a change | `0x7bc7d5` / `0xe6031d` | the ECS **base-zone list**: `0x68eca0` / `0x9f2b00` ("[ecs] Build zones changed") |
| ECS callback `0x7b44c0` / `0xe58610` (registered with the ECS world by `0x7af7c0` / `0xe53a10`) | `0x7b4546` / `0xe58696` | bounds (0x20 each) for a per-entity "inside a base" answer (`0x729e70`) |

The same step publishes its other lists right before and after (`0x69ca70` no-build `ecs+0x15f5a40`, `0x68fc10`
build zones `ecs+0x15f8a68` from `0x941d30`, `0x69ce20` playable area `ecs+0x15fba88`).

**The ECS base-zone list** (`ecs+0x15fa278`, inline capacity 255, FNV hash of all bounds `+0x15f5a38`, version
`+0x15fd29c`) **[dec]**:
- every changed bound (added or gone) goes to `0x67a2c0`, which walks the ECS spatial grid cells (`ecs+0xe17bd0`,
  `+0xe17cb0`) under it (what it does per cell was not traced);
- `0x61a780` sends the three zone lists to each peer, each count as one byte; `0x6776e0` reads them on the client;
- accessor `0x657e30` / C `0x9bbfa0` feeds the base grouping `0x206510` / C `0x46dda0` (callers `0x20bd70`,
  `0x20be40` / C `0x473470`, `0x473540`): overlapping zones become one base area. Its users: per-area counts of
  entities by kind and flags (`0x20be40`, [inferred] the journal's `basesInfo*` beds and animals) and C `0x3bf7f0`,
  which assigns each factory to the base area containing it ("[factory_info] couldn't find base for factory", "found
  outside base, but list is full"; lookup `0x46fd00`). The grouping has 16 area slots of 0x130 bytes
  (`+0x30`..`+0x1330`, the zone span right after them), merges only zones with index < 16, seeds a new area with
  `0x2072e0` without a bound check, and builds its mask as `(1 << (n & 31)) - 1`. The game never has more than 16
  bases; with more zones the areas are wrong, and a 17th area would be written over the zone span.

**The serializer's rule** (S `0x83d200` via `0x83db00`, C `0x10b89b0` via `0x10b92b0`, the same code) **[dec, dis]**:
for each level-6 key of the store map (x/y/z i16 cells, multiples of 32) the chunk box is `key * 0.5` m to `+ 16` m, and
the chunk goes into the record's blob when `chunkMin <= max && min <= chunkMax` on every axis (touching counts). Not
whole: for its 64 blocks of 8 cells a side (4 m; index x + 4y + 16z, box `key * 0.5 + 4 * i` m to `+ 4` m, the 4.0 at
S `0xbd84b0`) the same test runs again (S `0x83d792..0x83d7e7`, `comiss`/`jb`), and a block whose box does not touch the
record's bounds is written as byte 0, its cells dropped (`0x83d962`). The loader (S `0x830e30`, C `0x10ac730`, called
for each record with no bounds) skips a 0 block (C `0x10acaf1`): those cells stay as the world generates them, and a
block another record saved keeps that record's cells. So a 16 m chunk only partly inside every record is saved only in
part, and blocks two records save between them are saved. There is no other terrain list in the save: a block outside
every record regrows.

**The writer keeps at most 4,096 chunks per record** **[dis, lab]** (lab, 2026-11-23: a counter on the writer's
candidate step, S `0x83d42f`). The voxel writer collects a record's stored chunks into a scratch array of 4,096 keys
on its own stack (S `0x83d2f8` in `0x83d200`, C `0x10b8aa8` in `0x10b89b0`) whose grow callback (S `0x832de0`,
C `0x10ae690`) has no allocator: from the 4,097th chunk on the grow returns 0, the caller skips that chunk
(S `je 0x83d466`) and goes on, with no message. It walks the store map in hash-slot order (S `0x21980` / `0x21b80`),
so the skipped chunks are scattered over the whole record, its centre included. A lab altar of 288 m had 6,498
candidate chunks: 4,096 saved, 2,402 skipped. 16 x 16 x 16 chunks is a 256 m cube: a zone side under
240 m touches at most 16 chunk positions per axis wherever it lies; a side of exactly 240 m touches 17 when a face lies
on the 16 m grid (a lab altar with 4,352 candidates lost 256). The game's own zones stop at 160 m; larger ones come from
mods that enlarge zones. The loader has no count limit, and the writer is the same for every record, Kiln's boxes too.

**Load (C `0xe63fb0`).** Case 5 reads the records (BaseId + bounds) into a list and hands their bounds to water
(`0x1165ef0`); case 6 applies each record's voxel and water blobs, and after the last one hands the list to `0x9f3a70`,
the build-zone publisher: until BuildZoneSystem's own step publishes again, every saved record, Kiln's included, is a
build zone. Water per record: a record outside the water simulation area is skipped with a debug line; an allocation
failure fails the whole save ("[server] Serialization of 'Water' failed", result 0x11).

**Flame Altars in the world list.** The world-selection / base-sharing loca record has `flameAltars` (`+0x40`) and
`noFlameAltarSavegameHeader`; the count shown follows the save's records (kiln#136: 1 altar + 20 Kiln boxes showed 21;
2 + 39 showed 41) [player]. The code that counts them was not traced; no record kind or flag that saves terrain without
being a record was found (the save writes records only from this list).

**Startup bounds.** An altar's TriggerShape is not saved (§11): after a load its zone starts at the template's size and
grows when `upgrade_building_range` applies the saved Level. kiln#136 measured the 120 m altar's entry at 40 m and 48 ms
later at 120 m [player probe]. Every altar above the first size does this at every load; a world save in those steps
writes the smaller bounds.

**Kiln (since kiln#136).** The hook reads its caller's return address before the original runs. World save: after the
game's entries, boxes for the kept chunks whose 64 blocks the entries of that same list do not all keep (the
serializer's rule above, so the decision is the save's own, whatever the bounds are at that instant: no time window).
Kiln knows only which 16 m chunks its edits touched, so a kept chunk on a base's edge that reaches outside it gets a box
even when the edits in it are all inside the base. An entry whose bounds touch more than 4,096 chunk positions (the
product of the chunks it touches per axis, edges included) counts as keeping none of its chunks, so the kept chunks
inside it get boxes too; and no Kiln box covers more than 4,096 chunks (a box's bounds sit 0.05 m inside its chunks, so
its chunks are its candidates): a larger one is cut into slabs across its longest axis (both since 0.2.0-dev.30.9).
Base zones and base query: the list stays as the builder wrote it. Another mod's detour in front of Kiln's (on the
builder, or on a caller of it) changes the return address the hook sees. So while the world save's site is known, a
return address that is no known site is placed by the stack below it: up to 8 return addresses from it down
(RtlCaptureStackBackTrace, which unwinds through every frame with unwind data), the first known site among them decides
(kiln.log notes the first such call once). The look ends at the first frame in one of the system's modules (`ntdll`,
`kernel32`, `kernelbase`, the C runtime's `ucrtbase`, `msvcrt`, `vcruntime140`, `msvcp140`; the same names under
Wine): that is a thread's start, never a mod's hook, and a worker thread's stack reaches it within a few frames.
Without a known site above it, the call gets the boxes, as it may be the world save, when a frame lies outside the
game exe, Kiln's own modules (`kiln-core.dll`, `kiln-*.dll`) and the system's: another module's code, or a relay in no
module; kiln.log warns once, naming it. It gets them too when the stack cannot be walked past the return
address (warned once). Only when all those frames are the game's own does it get none (warned once when all three
sites resolved). The three return addresses are sigdb names
(`build/RetSaveRegionsWorldSave`, `build/RetSaveRegionsBaseZones`, `build/RetSaveRegionsBaseQuery`). When the world
save's does not resolve (a game update broke its pattern), Kiln cannot tell the save from the others and every caller
gets the boxes, as up to 0.2.0-dev.30.8; when only the base-zone or base-query one does not, the world save alone still
gets them. kiln.build's start then logs a warning naming the missing names, and its kiln.caps row
`build.persist_callers` names them too (unavailable, `symbol-missing`, when every caller gets the boxes). The three
names are on sigport's must-port list (`tools/sigport/must_port.json`): a re-port or check that loses
`build/RetSaveRegionsWorldSave` fails, one that loses the base-zone or base-query name only warns (the build can
still be verified). Entries that cannot be read, bounds that are
not finite or have min > max, or a list with more than 256 entries or count > capacity: boxes for every kept chunk.
The chunk JSON is never filtered. Up to 0.2.0-dev.30.8 every caller got a box for every kept chunk: Kiln's boxes were ECS base zones (stations,
storage and beds grouped by them, clients sent them) and Flame Altars in the world list.
