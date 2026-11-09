# World status: where the game keeps a world's progress (server Steam 26430253 / client 26430625, svn 1287369)

Research for world progress (`docs/kiln/services/world-progress.md`): the flame level, the difficulty, bosses killed,
Ancient Spires, Flame Shrines and Sanctums, Shroud Roots and Elixir Wells, Ancient Obelisks, survivors, chests, and the
vanilla quests of a character. Knowledge, queries and the journal are in `docs/re/quests.md` (Q§); map markers and the
scene layout in `docs/re/map.md`.

Sources:
- **Data:** the client's and the dedicated server's KFC4 data read offline with `tools/kfc` (reflection from the client exe):
  `GameKnowledgeQueryResourceDb` (`aacd27f0-…`), `GameKnowledgeResource`, `GameKnowledgeQueryTriggerResource`,
  `JournalRegistryResource`, every `ecs::TemplateResource`, every `SceneEntityChunkResource` of the world scene
  (`509feadb-…`, 1,601 parts), English loca. The server's data has the same query db, templates and scene (checked: the
  same counts and ids below from both).
- **Reflection:** `tools/kfc types --show` for every layout used.
- **Runtime:** a lab (a copied save, no player) with `mods/tests/worldstatus-test`, section 9.

Tags: **[refl]** the game's reflection, **[data]** decoded game data (whole data set unless an example is named),
**[dis]** read in the code, **[rt]** seen in the lab, **[hyp]** hypothesis.

## Short answers

| Fact | Where the game keeps it | World or character | Tag |
|---|---|---|---|
| Flame level | `ecs::NetworkServerProgress.level` on the world's progress entity (template `3_MotherFlame`, `ServerProgressSystem.progressEntity`); the entity's `Level` attribute is what the admin's set-flame-level writes | world (replicated to clients) | refl, data, dis, rt |
| Flame altars | the same component: `currentAltarCount`, `currentHighestAltarLevel`, `currentWaterSourceCount` | world | refl, rt |
| Difficulty | the server's preset (`GameServer+0x1760`) and the live `ecs::GameSettings` block (kiln.settings, `docs/re/settings.md`) | world (server) | dis, rt |
| Ancient Spire unlocked | world Bool with a player twin: the global-chunk `MapMarker_Tower_<region>_FastTravel` placement's `KnowledgeQuery` (the query db calls it `…_FastTravelUnlocked_World`) | world (twin per character in personal progression) | data, rt |
| Spire top reached | a player Bool the same placement's trigger sets; the game's `Achievement_Tower_*` queries read it (`…_topReached`) | character | data |
| Spire beacon lit | world Bool with a twin: the `KnowledgeQuery` of the `Prop_Tower_Beacon` placement next to the spire | world | data |
| Flame Shrine / Sanctum | one player Bool each (XP on unlock), set when a character comes in range of its map marker (`KnowledgeTriggerRange`); listed by region in `achievement_all_FlameTemplesAndShrines_found_<nn>_<region>` | character | data, rt |
| Shroud Root / Elixir Well | one player Bool per root (skill points: 3 at an Elixir Well, 1 elsewhere; `GameKnowledgeResource.skillPoints`), set when it is mined (`MiningNodeKnowledge`); listed in `achievement_all_shroudRoots_destroyed_<nn>_<region>`; an Elixir Well's map marker shows "completed" by its root's Bool | character | data, rt |
| Ancient Obelisk read | player Bools in `achievement_all_Obelisks_read_<nn>_<region>` | character | data |
| Boss killed | a Bool in the spawner placement's `SceneKnowledgeTrigger`, copied onto the boss it spawns (`KnowledgeTriggerOnKill.useSceneKnowledgeTrigger`); 13 of the 30 boss placements have one | world with a twin (most), or character | data, rt |
| Survivors rescued | the NPC roster's unlock requirements (world Bools; Q§, `docs/kiln/services/knowledge.md`) | world | data, rt |
| Chest looted | the chest entity's `RandomLootContainer.spawnedLootVersion` (loot rolled) and `InventoryEmptyState.isEmpty`; the placement's id is its `SceneLootNodeId` (a `LootContainerId`). Only while the chunk is loaded; unloaded chests live in the save's `chunk_persistent_data` (not decoded) | world | refl, data, rt |
| Vanilla quest status | the journal's step queries, evaluated for the character (Q§3); "rewarded" is the quest's player Bool. No failed state | character (world objects in shared progression) | data, dis, rt |

## 1. The flame **[refl] [data] [dis]**
- `keen::ecs::NetworkServerProgress` (12 B): `level` u16 +0, `currentWaterSourceCount` +2, `currentAltarCount` +4,
  `currentHighestAltarLevel` +6, `connectedLogicEntityCount` +8, `usedFactoryBudget` +0xa. Only the template `3_MotherFlame`
  has it (`Crafting, Level, NetworkServerProgress, ServerProgress`).
- `keen::ecs::ServerProgressSystem` (singleton, 56 B): `preferredSpawnAltar` +0, `progressEntity` +0x10, `progressLevel`
  +0x14, `currentWaterSourceCount` +0x18, `currentAltarCount` +0x1c, `currentHighestAltarLevel` +0x20,
  `currentHighestPlayerLevel` +0x24, `medianPlayerLevel` +0x28, `connectedLogicEntityCount` +0x2c, `usedSaveBudget` +0x30,
  `usedFactoryBudget` +0x34.
- The admin's set-flame-level (`AdminSetFlameLevel {versionData, flameLevel u8}` in the admin player input) writes the
  `Level` attribute of `ServerProgressSystem.progressEntity` (`docs/re/progression.md` §2) **[dis]**.
- The knowledge evaluator's runtime queries `MotherFlameLevel`, `FlameAltarLevel`, `FlameAltarCount` (`…QueryAction
  {invert, operation, count u16}`) compare against these values; the query db names them `motherFlameLevel_02`,
  `flameAltarLevel_01`, `flameAltarCOUNT_04`… **[data]**.
- `keen::GameParameters {flameLevel u32, gameSettingsPreset u32, …}` is what the server advertises (server list).
- The maximum is `BalancingTable.flameLevelMax` (read by `WorldProgress.LimitsAt`).

## 2. The scene's global chunk: spires, shrines, wells **[data]**
Every map marker sits in the world scene's global chunk (part 1600, map.md §3). Per placement, two spawn components
matter here: `ecs::SceneKnowledgeTrigger` (132 B: 8 × `KnowledgeTriggerOperation {u8 context +0, knowledgeId +4, type
+8, value +12}`; contexts Invalid 0, Interact 1, Kill 2, Toggle 3, Range 4, MiningNode 5) and `ecs::KnowledgeQuery`
(124 B: `query: KnowledgeQueryData` +0). An entity's spawn components are `spawnData.components[first .. first+count]`,
with `count = indices[spawnIndexStart]` and `first` the next two bytes (map.md §3). The program
`knowledge_trigger_copy_scene_knowledge` (Q§7 #76) copies the trigger's operations into the entity's `KnowledgeTrigger`;
the template's trigger component (`KnowledgeTriggerRange`, `KnowledgeTriggerOnKill`, `MiningNodeKnowledge`, …) decides
when they fire.

**Ancient Spires.** One spire = these global-chunk placements (Springlands, the example):
```
MapMarker_Tower_Grassland_FastTravel  (3508, 1047, 1982)  trigger [0xed07beac, 0x36417d33, 0xc7c7b9e1, 0xfa130910]  query SimpleBool 0xed07beac
Prop_Tower_Beacon                     (3508, 1102, 1958)  query SimpleBool 0xf016af9f
MapLabel_general_Tower_Grassland      (3508,  921, 1957)  query Extern 8kAncientDungeon_tower_01_Grassland_MM_unlock
```
- The fast-travel marker's query is the spire's unlock: the query db calls 0xed07beac `Tower_Biome01_grasslandMain_
  FastTravelUnlocked_World` and `fastrtravel_unlock` (`tower_completed`). World Bool with a player twin.
- Its trigger also sets 0xfa130910, named `Tower_Biome01_grasslandMain_topReached` in `Achievement_Tower_Biome01_grassland_main`:
  a player Bool, the character's own record.
- The beacon's query (0xf016af9f) is what the region's map markers and NPC hints wait for (`tower`, `markers_unlocked`).
- 10 fast-travel markers: 8 spires with beacons and an unlock Bool; the two `AncientLands_FastTravel_0n_*` teleports have a
  named query as their unlock and no beacon. 7 have a top-reached Bool in an achievement query (`tallTrees` has none).

**Flame Shrines and Sanctums.** A marker template (`8kMapLabel_general_FlameShrine_<nn>_<region>`, `8kMapLabel_<region>_
FlameTemple_<nn>`) with `KnowledgeTriggerRange`; each placement's trigger sets one player Bool with an experience gain
(exploration XP) when a character comes in range; its query (`8kFlameShrine_<region>_MM_unlock_<nn>`: OR of that Bool and
the region's obelisk) shows the marker. The game's achievements list them by region with readable names
(`achievement_all_FlameTemplesAndShrines_found_01_grassland`: `grassland_flametemple_01`…, `grassland_flameshrine_01`…). 68 in
all (6 regions; a seventh list is `DEPRECATED_`). No other per-shrine state was found: the query db has no completion
query for a shrine or a sanctum, so "found" is the shrine's whole record **[data]**.

**Elixir Wells and Shroud Roots.** The well's map marker (`8kMapLabel_<region>_ElixirWell_<nn>`) has template-level
`RuntimeMapMarkerUnlockRequirements` whose `completedStateQuery` is a player Bool: the root at the well
(`Loot_Node_Well_Core_Object_SkillPointPlant_*`, `MiningNodeKnowledge`), whose placement's trigger sets the same Bool
when it is mined. That Bool is a skill-point object (3 points). Other roots give 1 point. The achievement lists name them
`well_<nn>` and `additional_skillpoint_<nn>` per region: 75 roots, 75 placed. One Cold Heights well has three roots of
1 point (`well_15.1` to `.3`).

## 3. Bosses **[data]**
- A boss is a template with `ecs::BossHealthBar {displayName: LocaTagReference, maxPlayerDistance}` (the game's boss
  health bar). `ecs::Boss` alone (no bar) marks elites; they are not counted.
- Placed directly, or by a spawner whose `TriggerSpawner.entityTemplate` / `RandomSpawner.spawnEntries[].entityTemplates`
  names one (30 placements on 1287369, 1,601 chunks, every template).
- The kill record: `KnowledgeTriggerOnKill {useSceneKnowledgeTrigger +0, unlockForGroup +1, triggerKnowledge +4,
  knowledgeUnlockOperation +0x5c}`. With `useSceneKnowledgeTrigger` (the usual case) the operations come from the
  spawner placement's `SceneKnowledgeTrigger` (context Invalid or Kill); without it the template's own operation.
  Placements often set two Bools, one a player object and one a world object with a twin (the Fell Dragon: player
  0xe1baf1f0 and world 0x2b1f9fc8, which the query db calls `boss_killed` / `dragon_defeated`). 13 of 30 placements have
  one; the others have no persistent kill state.
- The game's huntress trophies (`TX_Huntress_BossTrophy_*`) read item pickup flags, not kills.

## 4. Chests **[refl] [data] [rt]**
- Chest templates (`LootChest_Random_*`) carry `RandomLootContainer {spawnedLootVersion u32 +0, parentNodeId +4,
  defaultLabelsId +8}`, `InventoryEmptyState {toggleVFX +0, isEmpty +1}`, `RandomLootPlayerScale {playerCount}`,
  `RemoveInteractionOnEmptyInventory`. The loot is shared (one inventory per chest, scaled by the number of players).
- Each placement has `ecs::SceneLootNodeId {id: LootContainerId}` (62,016 placements with one, chests among them): the
  stable id of a loot container in a data build.
- State is on the entity, which exists while its area is loaded. Unloaded chunks keep persistent entities in the save's
  `chunk_persistent_data` block (`ChunkPersistenceInfo {persistentEntityInfo[] {components[] {name hash, signature, size},
  componentIsSetStartIndex, dataSize, dataOffset}, signature, totalComponents, totalComponentDataSize}` per chunk resource)
  **[refl]**; the block's own layout was not decoded.
- `ecs::LootInteractionEvent {guestId, hostId, offerId, lootAll, knowledgeUnlockOperations[8], bypassInteraction}` is the
  game's loot event (a program with it in `events_in` would see each take) **[refl]**; Kiln samples the components instead.

## 5. Quests per character **[data] [dis]**
Q§1-3 and `docs/re/characters.md` §5: a quest is visible when a step's visibility query holds, done when the last step's
completion query holds, and the game pays its reward once and sets the quest's own player Bool (`entryId`). The client's
journal forces every step before a visible one to visible and done (Q§12), so the current step is the last visible one.
There is no failed or abandoned state. 142 of 188 quests have a named query as their last step's completion or first
step's visibility (`NPC_Alchemist_Hint01_HAS_Shelter_Journal`); 2 share one.

## 6. Ids
World progress and the game-data catalog derive the same ids from the data:
- **spires**: `MapMarker_Tower_(.+)_FastTravel(.*)` → `(1)(2)` lower-case (`grassland`, `capital`, `ancientlands_01_gardenisland`).
- **shrines, roots, obelisks**: one per Knowledge action of the region's achievement query; id = the action name
  lower-case, prefixed with `<region>_` when it does not start with it (`grassland_flameshrine_01`, `wetlands_flameshrine_01`,
  `deepforest_additional_skillpoint_01`); a repeat gets `_2`. Sanctum when the name has `flametemple`; Elixir Well when it
  starts with `well`.
- **bosses**: the boss template's name lower-case, with `_<n>` (chunk, then entity order) when it is placed more than once.
  Kill record: the first Bool trigger (Kill or Invalid context) that is a world object, else the first Kill one, else the
  first; else the template's own.

## 7. Layouts used (1287369) **[refl]**
`SceneEntityChunkResource` (140 B): `templates` +0, `entities` +0x18 (20 B: `templateIndex` u16, `SceneEntityTransform`
+2: X0, X1, Y0, Y1, Z0, Z1 u16), `spawnData` +0x34 (`entries` +0 4 B `{spawnMask, spawnIndexStart}`, `components` +8 12 B
variants, `indices` +0x50). `ecs::TemplateResource` (24 B): `name` +0, `components` +0x10. `GameKnowledgeQueryResourceDb`
(44 B): `queries` +0x10 (48 B: `name` +0, `queryId` +8, `type` +0x10, `actions` +0x18 (36 B: `type` +0, `name` +4,
`query` +0xc)). Component sizes: `SceneKnowledgeTrigger` 132, `KnowledgeQuery` 124, `SceneLootNodeId` 4,
`TriggerSpawner` 56, `RandomSpawner` 168 (entries 36 B), `BossHealthBar` 20, `MapMarker` 40, `KnowledgeTriggerOnKill` 104.
Kiln skips a component whose size differs, so a changed layout drops that fact instead of misreading it.

## 8. What is not per world
- Fog of war / map regions discovered: the character's `fogofwar` block (`docs/re/characters.md` §1), not read here.
- Points of interest discovered: each location marker's range trigger sets knowledge like the shrines do; not listed
  separately (the same rule applies to any marker template).

## 9. Lab **[rt]**
A lab (a copied save, no player), `mods/tests/worldstatus-test`, 44 PASS, 0 FAIL (the run: `docs/kiln/services/world-progress.md`
"Lab"). Facts it adds:
- The server's own data gives the lists above through the resource service: places in 0.04 s, every template and chunk in
  0.21 s.
- Every fact's scope matches `GameKnowledgeResource` (world, character, both); spire and boss reads equal the world map.
- The progress entity is entity 1024 in that world; its `Level` attribute reads `{rootId 0x724bf568, storageOffset 8,
  flags 1, storageCount 2, values 3, 20}` with flame level 3 (the second value is the cap). Writing 4 into the first value
  made `NetworkServerProgress.level` read 4 within half a second.
- A spawned chest reads `spawnedLootVersion 0`, `isEmpty 0`, `toggleVFX 1` before it is opened; `Props_Cupboard_*` loot
  props have `RandomLootContainer` without an inventory (not chests).
- Without a player the game still reported the death of a spawned boss killed with `Combat.Kill`.

## 10. Open questions
1. `chunk_persistent_data`'s layout: chests (and other persistent entities) of unloaded chunks.
2. Whether a Flame Sanctum's end (its flame) records anything: no knowledge or named query was found for it.
3. Which program copies the `Level` attribute into `NetworkServerProgress` (not traced; the lab shows it does within half a second).

## 11. Mother Flame writes and derived limits

The authoritative store is the progress entity's `Level` attribute, not `NetworkServerProgress`. The game's
`AdminSetFlameLevel` handler takes its writable Level accessor, marks the component written, resolves the attribute slot
with `AttrFind`, writes the requested value and calls `AttrRecompute`. The formula clamps it to the maximum stored next
to it. `native/services/quests/flame.cpp` follows that path from an ECS program declaring a Level write and a
NetworkServerProgress read. It refuses to write without a change tracker. Unknown attribute functions or layouts make
the feature unavailable. The native service exposes `flame_status`, `flame_set`, and `flame_result`, appended to v1.
Formula or readback failures restore both Level storage words and verify them. A failed restoration reports
`ROLLBACK FAILED` rather than implying no change occurred. Mirror waits are bounded by program updates, not seconds;
with one active request and eight waiting, a non-stalled batch can take `9 * (180 + 2) = 1638` updates. The managed task
and SET watcher await native final outcomes so they cannot abandon queued writes which may still run.

`ServerProgressSystem` has the progress entity at +0x10 and the level its rules read at +0x14. The read is accepted only
when the entity matches the one the program found. The lab observed this level and the replicated level follow the
stored attribute. An outstanding request is canceled when its world ends; a later world cannot confirm it.

The balancing resource has `flameLevelMax` at +4, `playerBaseFogResistance` at +0x64,
`playerFogResistancePerFlameLevel` at +0x68, and byte arrays `altarsPerFlameLevel` / `waterSourcesPerFlameLevel` at
+0x1b8 / +0x1c0. Both arrays must have maximum + 1 entries. The fog threshold comes from the live server setting
`flameLevelFogRemoval`; the setting stays unchanged when the level changes.

dev20-wp15 lab evidence (server 26430253, 2026-11-08): the armed run finished 61 PASS / 0 FAIL and the restarted run
59 PASS / 0 FAIL. The marked write changed level 3 to 7, then the next process read
`Level = 7`, replicated level 7 and `ServerProgressSystem.progressLevel = 7` before restoring 3. At level 3 the balancing
table reported 6 altars, 1 water source and fog resistance 420; at 4 it reported 8, 5 and 480; at 7 it reported 12, 30 and
660. The fog threshold remained 11. These are server reads; placement, client menus and visible fog need a player.

## 12. Loca on a dedicated server

The English table is in `keen::LocaTagCollectionResource` and its referenced language blobs on the client. The inspected
server data has no collection resource among its 43,958 resources, and no matching client loca blob hashes in its
directory. A server-only install therefore cannot supply arbitrary English text directly from its resources.

Kiln's client catalog already exports `sections.loca[]` entries `{id, text}` from the owner's installed game.
`Kiln.Content.Texts` and SET's `Res.text` read that optional local catalog, with size/count limits, without redistributing
the texts. The id is FNV-1a-32 of the 16 GUID bytes in the resource's LocaTag reference. The lab loaded 16,740 entries from
the client catalog for build 26430625 / svn 1287369. Missing catalog data remains unavailable, with a diagnostic; it is
not replaced with guessed text. See [English game texts](../kiln/modding/texts.md).

The lab compared three actual ItemInfo name references against `Items.DisplayName`: Wood Logs, Superior Mana Potion and
Superior Health Potion all matched. A scan using the item registry compared 3,998 pairs and found 3,966 equal. The
remaining shipped names are not all current English (for example, old Ashwind Strider names versus the current Sky
Specter names); one goat name also has trailing whitespace in loca. `Texts.Get` preserves the current catalog text.
