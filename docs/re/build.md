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
X-fastest, then Y, then Z. The independent verifier's decoder matched all 2000 live column cells. Full codec and
bounded decoder stay in dossier chapter 19f, not a supported terrain encoder.

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
