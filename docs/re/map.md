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
