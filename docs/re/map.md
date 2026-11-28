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
entry and synchronize after completed client jobs, not in a render callback/parallel copier, and at every client
SimStep, not only the ticked ones ("The list in a frame" below).

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
within 3 s after stopping injection; no hover-name tooltip (its text view was empty: "The tooltip's name" below).
Existing Render world label stayed world-locked.

### The list in a frame (GitHub #159; client 26430625 `5d9ef2f1…`) [static]

Read 2026-11-28 from the client exe (objdump and a .pdata/xref scan, never run), for cykel-far's report that Kiln's map
icons flickered. Pinned by `tools/sigport/tests/test_markers_map_steps.py`. In one frame, on the main thread:

1. **The client world update** `0xa1fae0` (reached from `0xa1fad0` for a world in state 2; the client frame `0x87a290`
   calls it at `0x87a8f9`, docs/re/client.md): `ticked = step counter [world+0xe17e9c] != previous [world+0xe17ea0]`
   (`0xa1fb2e`). When ticked it steps the prediction world first (`0xa00de0`). Then `SimStep(storage, ticked)` at
   `0xa1fc9e`, **on frames without a new step too** (ticked 0); only when byte `[world+0x15f55cc]` is set is that
   SimStep skipped (`0xa1fc8d`; the byte's meaning was not followed).
2. **SimStep** `0xa2c660` uses ticked once: the input apply `0x9653e0` (`0xa2c6a5`), a server-world function. The
   programs run when the world's byte `+0xe17959` is set (`0xa2c6c7`, as storage `+0xe17031`), which createWorld copies
   from its config `+0x44` (`0x983213`) and the client world's config sets to 1 (`0x84d064`). StepPrograms `0x9ade50`
   runs until it returns true (`0xa2c87a`) when SimStep's storage byte `+0x518` is still set (`0xa2c85f`, else
   `0x9af3d0`): SimStep sets it at entry (`0xa2c699`) and none of its direct callees before the test writes it.
3. **StepPrograms** selects programs by their flags (client bit `0x1`; SYSTEM always; the others when an archetype
   matches) with no clock or interval test. clear_map_markers_for_ui (`0x304470`, flags 5, CLIENT | SYSTEM) and
   replicate_map_markers_for_ui (`0x3161b0`, flags `0x201`, CLIENT | CHUNKED: slices on the world's task queue) both
   sys-write G38UiPlayData, so they never overlap. Before its only return StepPrograms of the client world waits for
   the whole queue (`WaitTaskGroup` `0x8f0b30` at `0x9ae686`, on `world+0xe17960`, the queue WaitWorldJobs waits for):
   the wait is taken when a flag made from the storage's kind byte is set, 1 for the client world (kind 2) and 0 for
   the prediction world (kind 1; `0x9ade72..0x9adeeb`, read back at `0x9ae642`).
4. **Kiln's Post** of the client world runs when SimStep returns, and only for a ticked step: the core's tick link
   passes the others through without Pre or Post (`native/core/src/tick.cpp`, `hk_sim_step`). The client clock is
   60 Hz.
5. **The UI frame** runs outside SimStep on the same thread: the GameUiSystem update `0xa4fb40` calls GameUiFrame
   `0xbd0f90` through `[system+0xf60]` (registered at `0x85333d`). UI code reaches the list through UiApp `0x283f4a8`
   (`+0xd0` GameUiData, its `+0x38` the G38UiPlayData). PlayStateUi `0xc603d0` → the HUD screen dispatcher `0xbe87d0`
   → the map tab `0xc2e190` (reads count and data at `0xc2e418`, `0xc2e5c7`, `0xc2e9c0`) → the map canvas `0xc36070`.
   There, when the normal count grew past the largest it saw (`0xc36280`), `0xd76830` walks the normal, special and
   label lists; the **marker layer** `0xda4770` (called at `0xc362db`) is rebuilt only when one of the four list counts
   or the custom-pin generations (`+0x36e9f8`, `+0x36e9fc`) differ from the ones it was built from (`0xda47a4..0xda47fd`),
   and it keeps **pointers to the entries** (`0xda4a43`): positions are read from the slots each frame. Other readers
   in the UI frame: `0xd24f50` and `0xd61370` (HUD), the compass lookup `0xd3cbf0` (by entry `+8`, so Kiln's id-0 rows
   never match).

So above 60 fps most frames ran a SimStep that cleared and refilled the list without Kiln's rows, and no Post followed.
The normal count went N+k at a ticked step and N at the next, the marker layer was rebuilt each time, and Kiln's icons
were in some frames only (cykel-far's count check, N against N+2, is what this predicts). It was not a race inside a
step: the Post was right after the refill, but it skipped most of the steps. Kiln's own compass strip and world labels
do not share this: they draw from a snapshot that lasts until the next Post.

**The fix (0.2.0-dev.31.28):** kiln.markers keeps the rows its last Post wrote and writes them again after every client
SimStep returns: a kiln.hooks link on SimStep before the core's own (sigdb feature `markers.client_map_steps`), so after
the step's join and before a UI frame can read the list again. The count stays N+k, the layer's entry pointers stay on Kiln's slots while N
is stable, and a moved marker's slot is written again at every step. At a ticked step the link finds the Post's rows at
the list's end and `append_rows` writes them over in place, so they are never there twice; the same holds for a step
whose clear did not run. A ticked step without Kiln's Post (the core removes a tick subscriber that faults) drops the
kept rows, so they never outlive the Post that would move or remove them. Without the feature the rows are written at
the Post only.

### The tooltip's name (GitHub #160; client 26430625 `5d9ef2f1…`) [static]

Read 2026-11-28 from the client exe (capstone over `.pdata` and call/rip xrefs, never run), for cykel-far's request that
`MapLabel.NativeTooltip` show a marker's `Text` when the map's cursor is on it. Pinned by
`tools/sigport/tests/test_markers_map_text.py`; sigdb feature `markers.map_tooltip_text`
(`sigdb/symbols/client.d/dev31-markers-map-text.json`). All of it runs in the UI frame, on the main thread.

1. **The hover.** The map canvas `0xc36070` rebuilds its marker layer `0xda4770` when a list count changed (`0xc362db`),
   then clears the hovered entry (canvas `+0x28` and `+0x20`, `0xc36566`) and hit-tests the layer's items again
   (`0xd9ee20`, `0xc3656e`), every frame. So the hovered entry is a slot below the list's current count. Later it calls
   the **crosshair window** `0xc2eea0` ("CrosshairWindow", `0xc37b6b`) with it.
2. **The tooltip.** The crosshair window takes the entry's title and the line under it from `TooltipTexts` `0xd1df80`
   (`0xc2f4f7`) and draws **no tooltip when the title is empty** (`0xc2f4fc` to `0xc2fabc`). The title is a native text
   line (`ui/NativeTextLine` `0xb1da10` at `0xc2f74c`, the view at `rbp-0x80`), the line under it another (`0xc2fa77`).
   `TooltipTexts` starts from the name resolver (`0xd1dfb4`) and changes it for a few types only:
   - Ancient Spire (`0xa17072be` fast travel, `0x98b2d65d` locked, `mapmarker_tower_locked`): the type's own name
     ("Ancient Spire") is the title and the entry's name goes to the line under it (`0xd1e007..0xd1e060`).
   - Water Source (`0x4293eae5`, `mapmarker_playerWaterSource`): always the type's own name; the entry's name is not
     shown (`0xd1e062`).
   - A Hint-visibility or investigation (`0xafce0c5f`) entry can take its quest's text by marker id (`+8`); id 0 finds
     none (`0xd3a1f0` returns null for 0, `0xd39cc0` an empty view).
3. **The name resolver** `0xd3d350` (`void(Name* out, const MapEntry*)`, `Name` = `{ptr, u64 len, fnv64}`), in this
   order:
   1. **the entry's text view**: when `+0x58` (length) is not 0, `PlayerText(&tmp, entry + 0x50)` (`0xd3d394`), its
      result when its status byte is 0, else an empty name;
   2. the custom-pin text view (`+0x70` length not 0): `{+0x68, +0x70, +0x78}` as it is (`0xd3d3f0`);
   3. the attention marker (`0xeb250f27`) at the entity and position of the record at GameUiData `+0x14020`: the loca
      text at `+0x4d30`;
   4. byte `+0x9c` set: a name by the entity (`0xd44530`, `0xd44230`);
   5. the type (`0xd3cf90`): a custom-pin type (`isCustomMarkerType`, `+0x19`) is the loca text at `+0xaea0`
      ("Custom marker"; `0xd3d4e8`);
   6. the LocaTagComponent hash `+0x38`: 0 is an empty name (`0xd3d529`); Hint or investigation entries the quest's
      name (above), else the loca text of `+0x38`.
   The layer calls it too (four sites) and keeps the result in its items (`+0x18`), but the canvas does not draw that
   copy (`0xc34650` reads the item's flags, type and icon); the hover path calls the resolver again every frame.
4. **PlayerText** `0xd28af0` (`Result*(Result* out, const StringView* text)`, 40 call sites in 36 UI functions: the
   game's path for text players write). `0xa49b40` copies the bytes into a 1 KiB buffer (the heap beyond), through the
   platform's text filter when the game has one (UiApp `+0x10` → `+0x110`, `0x1091f30`: a virtual call; more than
   1024 bytes refused there), else as they are; then `"%k"` is formatted into the **UI's
   string buffer** (`0x11b0da0`: UiApp `+0x18`, its array at `+0x6b10`; "!!! String buffer out of memory !!!" when it
   is full) with the escape argument `0x89a9e0`: a `\` before every `\` and `<` (`0xd28b85`, `0xd28bc9`), so a text
   carries no markup. Byte by byte: UTF-8's lead and continuation bytes are never `\` or `<`. The result is that copy
   with its 64-bit FNV-1a of sign-extended bytes.
5. **The game's writers of the two views.** The append `0x302970` clears `+0x50`, `+0x58`, `+0x60` and `+0x68`,
   `+0x70`, `+0x78` of every entry it adds. `replicate_map_markers_for_ui` fills `+0x50` (view) and `+0x60` (hash) only
   from the entity's CustomPlayerString (its string id through `0xe8ecd0`; no string, no write: `0x3167d7`), the hash
   a wrapping 64-bit FNV-1a over each byte sign-extended (`0x316830`). `custom_map_markers_ui` `0x3087f0` fills
   `+0x68`/`+0x78` from the registry node's inline text (`+0x20`, length byte `+0xa0`): **the player's own pins carry
   their own names** in that second view, and an unnamed one falls to "Custom marker" (step 5 above).

So the normal list already has a per-row text field the tooltip honours first: route (a). A row's text view is its name
everywhere the game names a map entry (the tooltip, the locator lists `0xbb9b80`/`0xcf1fd0`/`0xcf2900`, the panel
`0xc2fb40`), through the same filter and escaping as a name another player wrote. What kiln.markers makes of it is
below; for modders: docs/kiln/services/markers.md, "The map tooltip".

**The tooltip text (0.2.0-dev.31.30, GitHub #160).** A marker with `MarkerMapLabel.NativeTooltip` and `markers.map_text`
on gets its text into its own entry's view: `+0x50` the record's bytes, `+0x58` their length, `+0x60` the game's hash of
them (`map_list.cpp` `map_row`, `encode_entry`); everything else in the entry is as before, and `MapLabel.None` rows are
byte for byte the rows of 31.29. `markers.map_text` is on when `markers.map` is and the sigdb feature
`markers.map_tooltip_text` resolved with the text view at `+0x50`/`+0x58`/`+0x60`; else it is off with its reason, kiln.log
says so once, and `Set` keeps refusing NativeTooltip. What that rests on:
- **Lifetime.** The view points into kiln.markers' text arena: interned once, in 64 KiB chunks that never move and are
  never freed while the process lives. A removed marker's or an old text's bytes stay valid, so a view the game still
  holds is never a dangling pointer. The game copies the bytes (PlayerText) each time it names the entry and keeps only
  its copy, in its own string buffer.
- **Encoding.** Both sides are UTF-8 bytes with a length, no terminator. Kiln's text rule (at most 128 bytes, strict
  UTF-8, no control characters) is inside PlayerText's 1 KiB buffer and the filter's 1024-byte limit; the escaping can at
  most double it. CJK goes through as bytes; the game draws it with its own font group (its default group carries Noto
  Serif SC and KR), not with Kiln's overlay fonts of 0.2.0-dev.31.24.
- **Thread.** Kiln writes the entries at the client Post and after every client SimStep (MapKeep, "The list in a frame"),
  on the main thread after the step's jobs joined; the UI frame that names the hovered entry runs on the same thread
  outside SimStep. The two never overlap.
- **No stale text.** The text, position and type of a row come from the same Post's snapshot and are written together,
  whole (the entry is cleared first). The hover is a slot below the current count, hit-tested again every frame, and the
  tooltip names it from the slot each frame (step 1). A removed marker is in no row the next Post writes; the slot it
  had is then another Kiln row (with that row's text), a game row (the append cleared the view) or beyond the count.
  Turning NativeTooltip off (or the row going off) writes the row without a view from the next Post on.
- **Game rows untouched.** Kiln writes only beyond the game's count, as before; the game's own rows get their views from
  the append (cleared) and replicate (an entity's CustomPlayerString), never from Kiln.
- **Cost.** Kiln: one 64-bit FNV over at most 128 bytes per NativeTooltip row and Post; the steps in between copy the
  kept rows as before. The game: per frame while the cursor is on the icon, one copy, filter and escape of at most 128
  bytes (what it does for any named entry).

**The layer keeps a slot's icon (0.2.0-dev.31.30).** The marker layer `0xda4770` is rebuilt only when one of
the four list counts or the custom-pin generation (`+0x36e9fc`) changed, or the map's filter and locator menus ask for
it (the writers of canvas `+0xc1b4`; the canvas constructor `0xb20760` sets it too). The rebuild looks up each entry's
type (`0xd32e90`) and keeps it in the item (`+8`); the canvas draws the item through `0xb20a40` with that kept type and
the entry's live position (`+0x10`, `+0x20`). So a slot shows the icon of the row it held at the last rebuild. Until
this review Kiln wrote its map rows best first (priority, then camera distance), so two rows of different icon types
swapped slots, and icons, whenever the camera or a followed marker changed which was nearer, while the tooltip (named
from the slot every frame) followed the row. Since 0.2.0-dev.31.30 the map's rows go into the list in a slot order
without priority or distance (source, namespace, generation, key; `select.cpp` `admit`), so a slot keeps its row while
the set of admitted rows stays the same. Still open (pre-existing, not changed): a marker set again with another icon,
or one marker replaced by another in the same Post, at the same count, keeps the old icon on an open map until the next
rebuild; the tooltip follows at once. A fix would have to make the layer rebuild (a count change or one of the game's
own triggers), which is not a small change for a 31.x patch.

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
