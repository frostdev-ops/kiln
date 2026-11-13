# The world map's markers in the game data

Where the in-game map's markers, place names and the world objects a map shows (chests, grapple points, lore notes, …)
live in a client's KFC4 data, and how Kiln reads them offline. Client Steam 26430625, data build 1287369
(`enshrouded.exe` sha256 `5d9ef2f1…`). Sources: the game's own reflection (`tools/kfc types`), `tools/kfc decode` of the
resources named below, and whole-world scans (scratch scripts over `tools/kfc`; the shipped reader is
`tools/kfc/kfc/mapmarkers.py` and its Rust port `crates/kiln-manage/src/gameart/map/markers.rs`). No kfc-parser code or
docs were used. Tags: **[refl]** reflection, **[data]** decoded and checked across the whole data set, **[cmp]**
compared with the Enshrouded Interactive Map's third-party data.

Relief, Shroud zones and the map tiles are in docs/kiln/launcher/gameart.md (`UiTextureResource` parts).

## Short answers
- **A map marker is an ECS component on a placed entity.** `keen::ecs::MapMarker {markerId: LocaTagReference,
  markerType: MapMarkerTypeReference, dungeonId: LocaTagId, isInBigDungeon}` (40 B) sits in an `ecs::TemplateResource`;
  753 of the 12,269 templates have one **[data]**. The marker's name is `markerId`'s loca text.
- **Marker types** are one `MapMarkerRegistryResource` (`d165e819…`, 4,652 B): a `MapMarkerMap {entries, keys:
  BlobArray<MapMarkerTypeId>, values: BlobArray<MapMarkerType>}` with 49 types **[refl] [data]**.
  `MapMarkerTypeId` = FNV-1a-32 over the type GUID's 16 bytes (every one of the 33 non-null types the templates use
  matches a registry key) **[data]**.
- **Placements** are `SceneEntityChunkResource` entities of the world scene; every map marker (999 placements, 611
  templates) is in the scene's **global chunk** (part 1600), world objects in the 1,600 regular chunks **[data]**.
- **Positions** are absolute world metres in 16.16 fixed point, split in two u16 (`X = (X1 << 16 | X0) / 65536`), the
  same frame as the admin API's player positions and the Interactive Map's data (Thornhold 4592.7, 3153.4 vs 4593, 3153) **[cmp]**.
- **Names** come from `LocaTagCollectionResource` (one resource, `163bb0a2…`): per language a content blob of sorted
  24-byte `LocaTagResource {LocaTagId id, string text, arguments, genericArguments}`; a reference's tag id = FNV-1a-32
  of its GUID bytes (741 of 753 marker names resolve; the rest are null or debug markers) **[data]**.

## 1. Marker types (`keen::MapMarkerType`, 80 B) **[refl]**
`typeName: LocaTagReference` +0 (null for 44 of 49), `markerId: MapMarkerTypeId` +0x10, `sortingCategory:
MarkerTypeSorting` +0x14 (None, Locations, Dungeons, FlameRelated, CustomMarker), `sortingWeight` +0x15,
`isHighlightable` +0x16, `canBeFiltered` +0x17, `isFastTravelDestination` +0x18, `isCustomMarkerType` +0x19 (the
player's own pins), `icon: UiTexture {image: UiImageReference}` +0x1c (a `UiTextureResource` GUID), `mutedIcon` +0x2c,
`iconDisplaySize` +0x3c, `showAboveFogOfWar` +0x40, `showLabel` +0x41, `showRange` +0x42, `minimizeAboveZoomStep`
+0x43, `hideAboveZoomStep` +0x44, `minimizedColor` +0x48, `scaleWithZoom` +0x4c.

The icon texture's `debugName` identifies the type in practice (`mapmarker_village`, `mapmarker_tower`, …). Types
without an icon are either invisible helpers (`77997dbc…`: teleport platforms, dragon braziers' range markers; 160
placements) or **labels** (`showLabel`): area names (lakes, peaks, biome entries, the capital's districts).

| icon (type GUID) | placements | game names (examples) |
|---|---|---|
| `mapmarker_investigation` (`fffa09d9…`) | 111 | "Hand Drum Location", "Scavenger Stash": quest search areas |
| `mapmarker_special_enemy_camp` (`fe05bfcd…`) | 92 | "Treetop Vukah Settlement", "Scavenger Camp" |
| `mapmarker_well_plant` (`8cc3aae1…`) | 76 | "Shroud Root" (55, `FogWellPlant` labels), "Elixir Well" (21) |
| `mapmarker_village` (`8e313547…`) | 67 | "Thornhold", "Springlands Sky Port" |
| `mapmarker_flame_essence` (`d486f2ce…`) | 63 | "Flame Shrine", "Flame Sanctum" |
| `mapmarker_cave` (`a9ea4318…`) | 55 | "Cave Passage", "Grind Teeth Grotto" |
| `mapmarker_survivorcamp` (`c85fd053…`) | 44 | "Hero's Grave", "Northern Caravan Camp" |
| `mapmarker_farm`, `_dungeon`, `_nighttemple`, `_challenge_chamber`, `_shroudforge`, `_mine`, `_fognest`, `_obelisk`, `_balefire`, `_tower`, `_tower_locked`, `_bigDungeon`, `_tomb`, `_fastTravel` | 37, 37, 30, 26, 21, 21, 15, 14, 9, 8, 8, 6, 4, 2 | |
| no icon, label (`58c32c26…`, `61342e2e…`, `0d43c5d0…`, `526637e1…`) | 26 | "Howling Peak", "Umber Hollow", "Pike's Port" |

The locked (`mapmarker_tower_locked`) and unlocked (`mapmarker_tower`, fast travel) Ancient Spire markers stand at the
same spot (1-2 m apart, different heights). `PlaceholderMapMarker` templates and the type `77997dbc…` are logic helpers the game
never draws. A `_noUI` name is not one of those by itself: the client's marker list (`replicate_map_markers_for_ui`, [`0x3161b0`]) filters by marker type and visibility, not by name, and 26 of the
journal's step markers have that name with the investigation type. What they lack is the `Location` component, which is what names an area when a player walks in **[dis] [data]**.

## 2. Templates (`keen::ecs::TemplateResource`, 24 B) **[refl]**
`name: string` +0, `family` +8, `predictEntity` +9, `farCulling` +0xa, `omitTransformReplication` +0xb,
`proxyModelId` +0xc, `components: BlobArray<BlobVariant<ecs::Component>>` +0x10. A `BlobVariant` is 12 B
`{u32 type hash, i32 offset (from +4), u32 size}`; the type hash is FNV-1a of the component's qualified name
(`keen::ecs::MapMarker` 0x8af070c5), so a scan reads only the 12-byte headers (12,269 templates in 0.2 s, Python).

Other components the map uses: `ecs::LocaTagComponent {locaTag}` (16 B; a second name on most marker templates),
`ecs::LoreReading {description, entryName, unlockMapMarker: LocaTagReference, voiceover, displayedItem}` (80 B; the
lore note's title is `entryName`), `ecs::Location`, `ecs::MapMarkerUnlockRequirements` / `RuntimeMapMarkerUnlockRequirements`
(knowledge queries for hint / visible / completed; not needed for an admin map that shows everything).

## 3. The world scene and its chunks **[refl] [data]**
- `keen::SceneResource` (208 B; 5 in the client): the world is the one with the largest `entityChunkCount: uint2`
  (+0x6c) = 40 x 40 (`509feadb…`); the other four are 1 x 1 menu/character scenes. `resetPlayersOutOfBounds.worldMax`
  is `{10240, 2560, 10240}` m in 32.32 fixed point (43980465111040 = 10240 << 32): a 10,240 m world of 256 m chunks.
  `playableAreas[]` lists the big dungeons' boxes with their `mapMarkerId`.
- `keen::SceneEntityChunkResource` (140 B): parts 0..1599 are the world's chunks (part = z * 40 + x, 256 m;
  1,616,195 regular-chunk entities). Corrected 2026-11 (dev.28 RE terrain, server 26430253 `f0d824be…` / client
  26430625 `5d9ef2f1…`) **[data]**: 17 placements are 0.0000305–0.0002441 m below a lower chunk boundary;
  “all inside” was too strict. Use quantization tolerance, not reassigning their parts (dossier chapter 19f).
  Part 1600 is the
  global chunk (1,300 entities, 618 templates: every map marker, area labels, teleporters). 1,617,495 entities in all.
  - `templates: BlobArray<TemplateReference>` +0 (16-byte GUIDs), `templatesDoNotSave` +8, `hasPersistentData` +0x10,
    `entities: BlobArray<SceneEntityChunkSpawnEntry>` +0x18, `chunkPersistenceInfo` +0x20, `spawnData` +0x34.
  - `SceneEntityChunkSpawnEntry` (20 B): `u16 templateIndex` (into `templates`), `SceneEntityTransform` (18 B:
    `positionX0, X1, Y0, Y1, Z0, Z1: u16`, `orientation0, 1: u16`, `scale: u16`). Position = `(P1 << 16 | P0) /
    65536` m; chunk 5's entities have X in [1280, 1536): absolute, not chunk-relative.
- `spawnData: SceneEntityChunkSpawnData` (88 B): `entries[]` (one `{spawnMask: Bitmask16<EntitySpawnValue>,
  u16 spawnIndexStart}` per entity), `components: BlobArray<BlobVariant<SpawnComponent>>`, `triggerRanges`, `levels:
  BlobArray<{s32 level, s32 levelOffset}>`, `jumps`, `enemySettings`, `randomSpawnerSettings`, `enemyMarkerTags`,
  `locaTags`, `testFlags`, `indices: BlobArray<u8>`.
- **The index bytes** of an entity start at `indices[spawnIndexStart]`: a u8 count of spawn components, a u16 first
  component when the count is not 0, then one index per `EntitySpawnValue` group set in the mask, in bit order:
  TriggerRange/2/3 (bits 0-2) share **one u16** index into `triggerRanges`, EnemySettings (3) u8,
  RandomSpawnerSetting_* (4-6) share one u8, EnemyTestFlags (7) u8, Level (8) u8 into `levels`, EnemyMarkerTag (9) u8,
  TeleporterId (10) u8, TeleportTargetId (11) u8, JumpParameters (12) u8, Npc/AnimalTestFlags (13, 14) u8. The widths
  are fixed (not sized by the array): this rule consumes exactly up to the next entity's start for **all 686,371
  entities with spawn data** (0 mismatches); an array-length-sized rule fails 59,635 of them **[data]**.
- A chest's level is its Level spawn value: monotonic with the map data's chest levels, which are coarser bands of it
  (game 11-15 -> 13 or 15, 21-27 -> 20 or 27, 46-48 -> 45 or 50; 2,653 of the map data's 2,654 chests have a game chest within 2 m) **[cmp]**.

## 4. Loca **[refl] [data]**
`LocaTagCollectionResource` (40 B): `keenglishData: ResourceContentReference` +0 (the source language), `languages:
BlobArray<LocaTagCollectionResourceLanguage {LanguageId language (s32), ResourceContentReference data}>` +0x20: 15
languages in 1287369 (en-US, de, fr, it, es, ru, pl, ja, ko, pt-BR, th, tr, zh-CN, zh-TW, uk; 1.6-3.9 MB each). The
blob is a `LocaTagCollectionResourceData {tags: BlobArray<LocaTagResource>}` (16,740 tags, sorted by id). Texts carry
markup (`<location>Thornhold</location>`, `<b>`, `<lorename>`), stripped for a map. docs/re/content.md §8.1 has the
runtime side (the language slot, `LanguageId` values).

## 5. What the admin map makes of it (recipe: docs/kiln/launcher/gameart.md "Map markers")
- POI categories from the marker types (by icon name), place labels from the label types plus every location marker
  whose name is used at most twice (villages tier 1; camps, farms, mines, dungeons, vaults, balefires, tombs tier 2;
  caves, enemy camps tier 3): all 276 distinct place names of the map data are game marker names **[cmp]**.
- World-object categories by template name: `LootChest_Random_*` (gold / silver / wood / shell), `LoreText_*`,
  `Loot_Node_Well_Core_Object_SkillPointPlant*` (Shroud Roots: 78, map data 78), `Hookshot_Target*`,
  `Upstream_Zone*`, `Zone_ThrowIntoAir_JumpPad*`, `Door_Secret*` / `Shelf_Secret*`, `Prop_Logic_Puzzle_Solved_Notifier`,
  `_FishingLocation*`, `LootPickup_Collections_Fossils*`, `*DragonBrazier*`.
- Not found as such in the data (open): the map data's "Bosses" (131, mostly "Fell" elite spawns: spawners reference the
  creature template through `TriggerSpawner.entityTemplate` / `RandomSpawner.spawnEntries`; 54 templates carry
  `ecs::Boss` / `BossHealthBar {displayName}`), its collectibles (music sheets, cyclops skeletons, Drak reliefs, dragon
  map pieces, statues, tapestries, flame mosaics), "Treasure Clues" and "Unique Finds" (item pickups), and the finder
  data (creature spawn points, gatherables, ore volumes, chest loot tables).

## 6. Open questions
- Which spawner/template chain makes a "Fell" boss spawn (for a Bosses category with the game's own names).
- Lore categories: The map data splits lore into 13 journal categories (`journal_lore_category_*` textures exist); the
  link from a `LoreReading` to its journal category was not looked for (`JournalRegistryResource`).
- Whether the investigation (search area) markers' radius is the `triggerRanges` spawn value or a component field.

## 7. Runtime markers, waypoints and compass (dev.28; server 26430253 / client 26430625)

S `f0d824be…`, C `5d9ef2f1…`. Source: dev.28 dossier chapter 04, markers REPORT/NOTES/VERIFY and client sessions
1/2/4/5. VERIFY: 24 confirmed, two corrected (marker lookup identity and null-GUID hashing), incorporated below.
The offline census above was independently confirmed; it is not a server component/relevance guarantee.

### Three different pin paths [static] [refl] [lab]

- **World marker:** template MapMarker (40 B), markerId LocaTagReference GUID `+0`, type GUID `+0x10`, dungeonId
  u32 `+0x20`, inBigDungeon bool `+0x24`. A server-spawned shipped HintMarker existed, but live server MapMarker
  storage was absent even after an explicit extra-component override. Client reconstruction from a known template
  is distinct from component replication.
- **Waypoint:** PlayerWaypoint 32 B, enabled bool `+0`, signed 32.32 XYZ `+8`. WaypointAction 32 B, position `+0`,
  enable `+0x18`, isPing `+0x19`, version `+0x1c`. Program `player_waypoint_selection` S `0xd64f0` / C `0x2ea550`
  reads PlayerInput, writes waypoint/consumed input and emits UiPingEvent. Client `player_waypoints_ui=0x314000`
  copies position/enabled to player UI record `+0x50/+0x68`; records at UiPlayData `+0x4642c0`, count `+0x4642c8`,
  stride 0x240. This is one waypoint per player, not the many-pin collection.
- **Custom pins:** client MapMarkerRegistry, copied by `custom_map_markers_ui=0x3087f0` when generation `+0x38`
  changes. Head accessor `0xe8ecb0` returns registry `+0x80`; generation getter `0xe8ecc0`. Nodes: position `+0`,
  color `+0x18`, type `+0x1c`, inline text[128] `+0x20`, length byte `+0xa0`, handle `+0xa8`, next `+0xb8`.
  Decoder `0xdecf20` rebuilds 0xa8-byte save records; exact save-file ownership/mutation ABI remains unqualified.

Late live registry bits [lab]: MapMarker/BossHealthBar neither saved nor replicated; PlayerWaypoint/BossDisplay
replicated, not saved; CustomPlayerString saved and replicated. These are not delivery or relevance observations.

### Client ECS-to-UI copy and storage [static], capacities also [seen]

`replicate_map_markers_for_ui` C `0x3161b0` is **not a network sender**. Dedicated descriptor has null update.
It is CHUNKED, flags `0x201`, requires MapMarker+RenderTransform, optionally reads CustomPlayerString, visibility,
NetworkLevel, SpawnTime, TriggerShape, UsedItem; template reads EntityTagContainer/LocaTagComponent; reads string
system/other entity strings and writes G38UiPlayData. Hidden visibility is skipped; absent visibility defaults to
Visible 2. Visibility component 24 B: enum `+0` (Hidden 0 / Hint 1 / Visible 2 / Completed 3), Time `+8`, u16 counter
`+0x10`. Runtime unlock requirements hold three 12-byte KnowledgeQueryData records.

`clear_map_markers_for_ui=0x304470` clears the three per-step lists, **not** the custom cache:

| Header relative to G38UiPlayData | List | Refresh |
|---|---|---|
| `+0x36ea00` | normal | per step |
| `+0x396a28` | custom-pin cache | generation change |
| `+0x3bea50` | label/type | per step |
| `+0x3e6a78` | special/type | per step |

Each header `{data,count,capacity,grow,allocator}` is 40 B; **data=header+0x28**, inline 1024×0xa0 payload.
Capacity stores C `0x83761d/3d/5d/7d` set 1024, not EMF's 0x4000 corruption-screen ceiling. Grow callback
`0x840d40` → `0x86b070` refuses because allocator `+0x20=0`. `MapEntry* append(MapList*)` C `0x302970` returns null
when full, increments count on success, and does not zero every byte. Mod-owned appends must initialize the whole
entry and synchronize after completed client jobs, not in a render callback/parallel copier.

MapEntry 0xa0 B: entity u32 `+0`, opaque second key `+4`, **marker loca ID** u32 `+8`, signed 32.32 XYZ
`+0x10/+0x18/+0x20`, range floats `+0x28/+0x2c`, typeId u32 `+0x30`, visibility byte `+0x34`, LocaTagComponent
hash `+0x38`, visibility lastChange/SpawnTime `+0x40`, grouping byte `+0x48`, text ptr/u64 length/hash
`+0x50/+0x58/+0x60`, separate custom-pin text view/hash `+0x68/+0x70/+0x78`, color byte `+0x80`, custom handle
`+0x84` with presence `+0x88`, level `+0x8c/+0x90`, dungeon `+0x94/+0x98`.

Corrected by VERIFY [static]: `+8` is FNV-1a-32 of **MapMarker.markerId**, not a generic tag. markerId/type GUID
hashing uses unsigned bytes in Windows GUID order, **except null GUID→0** (not FNV32(zero16)=`0x69691905`).
A present LocaTagComponent GUID has no null special case; absent component→0. String hashes instead sign-extend
**each byte** (MOVSX) into wrapping u64 FNV, seed `0xcbf29ce484222325`, multiplier `0x100000001b3`.
UTF-8 `c3 a9` yields `0x080d7307b4cc3001`, unlike unsigned FNV. Text views are borrowed; neither removal nor a
guessed frame delay establishes that native UI has finished reading their bytes.

Custom cache is reset and fully rebuilt on each generation change; entries above 1024 drop silently. [seen, session 1]
Counts were normal/label/special 833/6/1; custom 0→528 on opening map→529 after adding a pin. Initial zero was lazy
cache state, not an empty save. [seen, session 2] Local append produced a copied icon and yellow diamond, removed
within 3 s after stopping injection; no hover-name tooltip. Existing Render world label stayed world-locked.

### Compass, projection and names [static] [refl]

`void doHudCompass(const void* buildInput)` C `0xbe34c0`, sole caller `0xbe510f` in `0xbe4f40`, does not draw all
map entries. Selected-marker loop calls `const MapEntry* lookup(u32 markerLocaId)` C `0xd3cbf0`, searching normal/
special/label lists by **entry+8**; ID 0 returns null. It also requires that ID in a selected-u32 set whose producer
is not mapped, skips horizontal distance<40 m and keeps at most 16 bearings. Thus the ID-0 injection is unreachable
through native compass; moving it farther is not a meaningful test. Player waypoints use a separate player loop.

`bool worldToUi(UiState*,out16*,view,projection,const float point[3],const Margins*)` C `0xa52450`, caller
`0xcf23be`: state viewport pointer `+0x10`, view/projection at UiPlayData `+0x1d20/+0x1d60`. Output x/y floats
`+0/+4`, visible/clamped bytes `+8/+9`, angle float `+0xc`; margins four floats plus enabled byte `+0x10` (20 B).
Point is float XYZ, not fixed-point. MapCanvasDraw C `0xd7c690` is `void(UiScope*,const void* view)`; full ownership/
clip ABI is unqualified. FogSample S `0xa484b0` / C `0x1222180` returns out80 from `(out,fogQuery,const s64 XYZ[3])`:
**Shroud weights, not map exploration**. showAboveFogOfWar is type `+0x40`; explored-mask reader remains unmapped.

Boss programs `boss_display` S `0x891c0` / C `0x277310`, `boss_health_bar_display` S `0xc13c0` / C `0x2d5420`,
all `void(ProgramContext*)`. BossDisplay 16 B: queryId `+0`, entityId `+4`, visible/engaged `+8/+9`, locaId `+0xc`;
BossHealthBar 20 B: loca GUID `+0`, distance float `+0x10`. Client health copier `0x30bd70` assigns health-row `+8`
name only for selected visible boss. Boss name is recomputed each program run. Cached widget draw `0xb6d5b0` uses
0x300-byte entries/count byte `+0xc000`; entry renderer `0xba7230` consumes text view `+0x1a0/+0x1a8/+0x1b0`.
CustomPlayerString is **u64 ID**, not text pointer; lookup C `0xe8ecd0` returns status/ptr/len at `+0/+8/+0x10`
(status 0 hit, 0x1f miss). These paths do not establish arbitrary NPC rename or text synchronization to vanilla.

### Vanilla results and bounds [seen], remaining distinctions

Session 4: actual server-written waypoint appeared as a vanilla compass diamond at 304–321 m. Exact component
readback matched; restoration was not logged, and the map well/root icon was ambiguous. The three server program
hooks also ran [lab], waypoint/boss selection on workers, boss-health on world thread; never assume arbitrary program
detours own Pre/Post or fabricate their contexts.

Session 5: **new** shipped HintMarker_Castle (`a0e7c47e-4c75-4653-88c8-92a7e1da275a`) at (3800,905,1100)
produced a new vanilla red crossed-swords camp icon and destroy removed it. No waypoint was written; nearest shipped
marker was 154.59 m away. Basic known-template map appearance/removal is proved; global range, all-peer/per-player
control and spawned-marker compass inclusion are not. Unknown cloned templates still require client data.

[data] Dedicated archives contain **zero** MapMarkerRegistryResource/UiTextureResource assets; the client has
49 type rows, 43 non-null icon images (six fast-travel). Do not bootstrap server icon metadata by traversing nonexistent
server UI resources. Full scratch signatures, catalog, explicit-world entity-incarnation watcher and protocol designs
remain in dossier chapter 04; no native persistent-pin API is implied here.

### World-aware anchor identity (same dev.28 builds) [static] [lab] [refl]

GetComponent S `0x64ed50` / C `0x9b2ec0` takes **World**, not Storage; copy CurrentTransform 56 B on server or
RenderTransform 176 B first pose on client in the owning world's completed Post. The public server-bound wrapper
is not a joined-client reader. CreateEntity S `0x623ce0` / C `0x987e50` reconstructs as
`u32(Storage*,u32 requestedId,const SpawnRequest*,const void* mask)`: zero id allocates, nonzero requests exact id;
return zero refuses. Observer hooks forward opaque request/mask unchanged, not fabricate spawn calls.
Dedicated probe destroyed/recreated **the same id without SpawnTime**, proving that field is not a unique incarnation.

Client network-create call C **`0x961d07`** reaches template helper `0x987910`, not the canonical immediate-create
path in the bounded traced graph. At this MID, entity/parent are u32 `[RBP+0x3cb0]/[RBP+0x3cc0]`; read-only
observation must coexist with the unknown-template guard, including skipped creates. A watcher relying only on
canonical CreateEntity can miss this reuse. Runtime client watcher coverage was not proved; lifecycle reuse and
serial/handle designs are in chapter 04, not an engine-owned generation-field claim.
