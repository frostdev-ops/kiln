# Quests, the journal, knowledge, quest markers (server Steam 26430253 / client 26430625, svn 1287369)

Research on quests (quest chains, friendly NPCs and dialogue in mods). Dialogue and recruitable
NPCs are in `docs/re/creatures.md` §7; this file owns quests, the journal, knowledge and markers.

Sources:
- **Data:** the client's KFC4 data read offline with `tools/kfc` (reflection from the client exe 26430625,
  `enshrouded.exe` sha256 `5d9ef2f1…`): `JournalRegistryResource` `33701b26-…`, `GameKnowledgeResource` `cb1eeb8b-…`,
  `GameKnowledgeQueryResourceDb` `aacd27f0-…`, `GameKnowledgeQueryTriggerResource` `ead77791-…`, `NpcCollection`
  `377f61dc-…`, every `ecs::TemplateResource` and the world scene's `SceneEntityChunkResource` spawn data, English
  loca.
- **Code:** the dedicated server `enshrouded_server.exe` (sha256 `f0d824be…`, image base 0x140000000, RVAs below) and the
  client exe 26430625 (RVAs in brackets), scratch copies, Ghidra 12.1.2 headless projects of both, capstone, the
  program-descriptor table (758 programs on both exes; 0x188-byte descriptors including their event lists).
- **Runtime:** a lab (a copied save, no player), a read-only probe mod (three register-only mid hooks, then memory
  reads). Quoted in §9.
- Nothing from kfc-parser. EMF's docs were not needed here.

Tags: **[refl]** the game's reflection, **[data]** decoded game data (whole data set unless an example is named),
**[dis]** read in the code at the RVA given, **[rt]** seen in the lab (kiln.log quoted), **[hyp]** hypothesis.

## Short answers

| Question | Answer | Tag |
|---|---|---|
| Is there a quest system? | Yes, a thin one: the **journal**. `JournalRegistryResource` holds 188 quests (`JournalQuestResource`: source NPC/biome, type Auto/WorldQuest/PlayerQuest, isMainQuest, rewards) with 406 steps (`JournalEntryResource`). Every step is three **knowledge queries**: visible (`knowledgeRequirement`), done (`completionRequirement`), optional progress steps. Nothing else: no quest state machine, no quest objects at run time. | refl, data |
| How does a quest start, advance, end? | By **knowledge**. A dialogue option, a world trigger or another query sets a knowledge id; the journal shows the steps whose visibility query is true; the quest is complete when its last step's completion query is true. The server then gives the XP reward once and sets the quest's own player knowledge (id = quest `entryId`) as "rewarded". | data, dis |
| Where is knowledge defined? | `GameKnowledgeResource`: 5,825 world objects + 10,744 player objects (`KnowledgeId` → type Bool / Counter / Flags, XP gain, skill points, achievements), static hash maps. Composite conditions are 5,296 named queries in `GameKnowledgeQueryResourceDb` (AND/OR/XOR over knowledge, inventory, NPC state, flame level, player level); 255 query triggers unlock knowledge when a query becomes true. | refl, data, rt |
| Where does a player's knowledge live? | While the player is on the server: `G38Knowledge` → registry: one world map (shared progress) + 16 per-player blocks (hash maps id → state). Saved apart: world knowledge in the **world save**, each player's in **that player's character save** (`player_knowledge`; docs/re/characters.md). Clients get copies over network channel 10 (`KnowledgeServerMessage`). | dis, rt, save |
| How is knowledge granted? | Every server program pushes `{KnowledgeUnlockOperation, target}` into the lock-free `KnowledgeTriggerQueue` (2,048 per step); program `update_knowledge_system_server` applies them, gives XP, runs query triggers and replicates. Ids that are not in the static maps are ignored. **Bool can only become true** through the queue; there is no revoke operation. | dis, rt |
| Does the server check what clients ask? | No. A selected dialogue option's knowledge operations run without checking the option's requirement, and `TriggerKnowledgeAction` (player input) pushes any non-zero knowledge operation the client sends. | dis |
| What does the journal show and where from? | Client program `journal_ui` builds the quest log from the client's own `JournalRegistry` and its replicated knowledge (rebuilt when the knowledge versions change). Names, texts and step texts are loca tags (client data). | dis |
| Quest map/compass markers? | A step names a map marker by its loca tag (`mapMarkerReference`; 238 of 240 match a placed `MapMarker.markerId`). Markers are world entities whose visibility (hint / visible / completed) is a knowledge query evaluated on the client. Players' own waypoints (`PlayerWaypoint`) are a server component. | data, dis |
| What can a mod do with vanilla clients? | Server-side knowledge grants, reading progress, rewards, reacting to dialogue and NPC interaction, objectives from game events, vanilla quests/NPCs advanced by code; text through chat. New journal entries, new dialogue lines/options and new knowledge-driven UI need Kiln (and the mod's data) on the client. | dis, hyp |

## 1. The journal data **[refl] [data]**

### 1.1 `JournalRegistryResource` (one resource, `33701b26-ec1d-423f-8e06-49f023b91b7f`, 190 KB)
`{quests: BlobArray<JournalQuestResource>, collections: BlobArray<JournalCollectionResource>, itemSetCategories,
itemSetRewards}`. The same GUID is also typed `DevJournalRegistryResource` (editor).

- **`JournalQuestResource`** (44 B) = `JournalCollectionResource` (32 B: `entryId: JournalEntryId` +0, `loreCategory`
  +4, `name` +8, `referencedDocumentName` +0xc (LocaTagIds), `priority` +0x10, `isTutorial` +0x14, `entries:
  BlobArray<JournalEntryResource>` +0x18) + `source: QuestSourceType` +0x20 (None, Flame, Blacksmith, Alchemist,
  Huntress, Farmer, Carpenter, CryptKeeper, Bard, ChineseNewYearTrader, Barber, Fisher, AncientResearcher, and the
  biomes Grassland…AncientLands), `type: QuestType` +0x21 (Auto 0, WorldQuest 1, PlayerQuest 2), `isMainQuest` +0x22,
  `unlockForAllPlayers` +0x23, `rewards: BlobArray<QuestRewardResource>` +0x24.
- **`JournalEntryResource`** (72 B, one quest step): `entryId` +0, `name` +4, `text` +8, `mapMarkerReference` +0xc
  (LocaTagIds), `knowledgeRequirement: KnowledgeQueryData` +0x10 (visible), `completionRequirement` +0x1c (done),
  `progressStepsRequirement: BlobArray<KnowledgeQueryData>` +0x28 (sub-counters such as "gather 5 new crops"),
  `voiceover` +0x30, `itemIconId` +0x40, `recommendedLevel` +0x44.
- **`QuestRewardResource`** (24 B): `itemRef` +0, `itemId` +0x10, `experienceValueId: HashKey32` +0x14 (a
  BalancingRegistry experience value).
- `KnowledgeQueryData` (12 B): `knowledgeOrQueryId: HashKey32` +0, `compareValue: u32` +4, `compareOperator`
  (GreaterThan 0, LessThan 1, Equals 2) +8, `type: KnowledgeQueryDataType` +9 (**Extern** 0 = a named query of the
  query db, **SimpleBool** 1 / **SimpleCount** 2 / **SimpleFlag** 3 = one knowledge object directly),
  `isExplicitPlayerKnowledgeQuery` +0xa (the player's object even in shared progression).

Numbers (1287369): 188 quests (80 WorldQuest, 76 Auto, 32 PlayerQuest; 109 main), 406 steps, 240 with a map marker,
10 with progress steps; 180 quests have rewards (183 item, 171 XP); none sets `unlockForAllPlayers`. In 187 of 188
quests **every step has the same completion query** (the quest's): a step is "done" when the whole quest is, the
visible steps form a checklist that grows as their visibility knowledge is set.

Example (Alchemist, `NPC_Workshop_Alchemist01`), all ids live in the data:
```
Q 8755c335 "<npc>Alchemist</npc> Needs Shelter" Alchemist/Auto
   step "Build A Sheltered Bed For Balthazar"  visible 88b160c1 SimpleBool=1   done 4f5c5b09 Extern
        4f5c5b09 = query "NPC_Alchemist_Hint01_HAS_Shelter_Journal": OR(Npc action "NPC_Alchemist_sheltered_state")
   dialogue option "Shelter" of the Alchemist: knowledgeUnlockOperations [88b160c1 Bool=1]   (creatures.md §7)
Q 0c476979 "Cat Search" Alchemist/WorldQuest, 5 steps (craft bait → reach the tower [marker "Alchemist Tower"] →
   tame a cat → place it in your home → craft bowl and bed), one completion query 155d6e97
```

### 1.2 Other journal content
- `collections` (320): lore documents, tutorials (26 `isTutorial`), 14 lore categories, 564 entries; same entry
  layout (visibility by knowledge, no completion).
- `itemSetCategories` (20) / `itemSetRewards` (126): the collection sets (fossils, music sheets…): `{rewardKnowledge,
  experienceValueId, knowledgeQueryData}`; program `trigger_item_set_rewards` (#463, `0x1bcef0`) pays them like quest
  rewards.
- UI config: `FbUiQuestlogNpcViews` (the NPC portraits per quest source), `FbUiLocaMenuJournal` (journal texts).

### 1.3 Scene quest records
`SceneQuestEntity` (200 B: `transform`, `unlockOperations: StaticArray<KnowledgeTriggerOperation,8>`, `templateGuid`)
and `SceneQuestItem` (`transform`, `itemId`, `lootContainerId`, `sceneSpawnId`) **[refl]**. The server singleton
`QuestHelper` (GameServer+0x1660, players.md) holds them; program `quest_entity_check` (#1, `0x1031a0`, sys-read
BuildZoneSystem/G38Knowledge/ServerPlayers/EntityGrid, sys-write KnowledgeTriggerQueue+QuestHelper) walks up to 128
per step round-robin against the 16 player records and their build zones and fires their unlock operations **[dis]**:
the "place X in your base" objectives (Mortar, cat) **[hyp]** for the exact condition.

## 2. Knowledge

### 2.1 Objects **[refl] [data]**
`GameKnowledgeResource` (one resource `cb1eeb8b-f58a-4785-bf0f-6dd5437e9a5b`, 917 KB): `worldKnowledge:
WorldKnowledgeMap` +0, `playerKnowledge: PlayerKnowledgeMap` +0x18, `achievements` +0x30 (58), `skillPoints` +0x38
(78), `flagKnowledgePatch` +0x40 (27,120 old-id → new-id/flag patches for old saves).
- A map is a static hash map `{entries: BlobArray<u64 bucket>, keys: BlobArray<KnowledgeId>, values: BlobArray<…>}`;
  a bucket is `{u32 first index, u32 count}` at `(buckets-1) & id` (lookup `0x99fee0` world, `0x99fdb0` player
  **[dis]**). World: 8,192 buckets, 5,825 objects; player: 16,384 buckets, 10,744 objects (**[rt]** the same counts in the
  running server).
- `WorldKnowledgeObjectResource` (12 B): `id`, `sendToAnalytics`, `hasOptionalPlayerProgression`, `isPlayerKnowledge`,
  `type: KnowledgeType` (Bool 0, Counter 1, Flags 2) +7, `expectedFlags` +8. `PlayerKnowledgeObjectResource` (24 B) adds
  `isAdditionalPlayerKnowledgePerFlag` +0xc, `experienceGainId` +0x10 (XP on unlock), `unlockedSkillPoints` +0x14.
  World: 4,124 Flags, 1,698 Bool, 3 Counter; player: 6,918 Flags, 3,823 Bool, 3 Counter.
- **Generated knowledge** (the editor's converter, strings at `0x11e4c30`…): one object per dialogue line ("dialog
  directory"), per recipe, per item (pickup/use/inspect flags), per quest ("world knowledge for completed Quests":
  each quest's `entryId` is a **player** Bool, 188 of 188), and twins: 1,690 world objects have
  `hasOptionalPlayerProgression` and a player object with the same id ("Also generate player knowledge for personal
  quest progression").
- Ids: `KnowledgeId`/`HashKey32`. Item/recipe/dialogue ids are the item id, recipe id, dialogue line id; other
  objects have editor-made ids (no names shipped: `GameKnowledgeDbInfo` with names is not in the data).

### 2.2 Queries and triggers **[refl] [data]**
- `GameKnowledgeQueryResourceDb` (`aacd27f0-…`, 1.3 MB): 5,296 queries (4,240 AND, 1,055 OR, 1 XOR) **with debug
  names** (`NPC_ALCHEMIST_Hint02_AlchemistStaffSpells_conversation_topic`, `TX_Huntress_BossTrophy_Cyclops_unlocks_bronze`,
  …), `playerQueryStartIndex` 1,897, `npcQueryCount` 39, 145 `runtimeQueries` (`type` Inventory, InventoryCounter, Npc
  (IsSpawned, CraftingPropPlaced, IsSheltered, SufficientComfortLevel), FlameAltarCount/Level, MotherFlameLevel,
  PlayerLevel), 45 `queriedItems`. A query = `{name, queryId, queryIndex, type, isPlayerQuery, invertResult,
  actions[] {type, name, query: KnowledgeQueryData, invertResult}}`; queries nest (an action of type Query).
- `GameKnowledgeQueryTriggerResource` (`ead77791-…`): 64 world + 189 player + 2 event-based player triggers
  `{requirement, requiresEvent, requiredEvent, query, unlockKnowledge[]}`: when the query becomes true the knowledge
  in `unlockKnowledge` is set (quest chains: "step 2 visible when step 1's query holds").
- `KnowledgeUnlockOperation` (12 B): `{knowledgeId, type, value}`; `KnowledgeTriggerOperation` adds a
  `GameKnowledgeTriggerContext` (Invalid, Interact, Kill, Toggle, Range, MiningNode).

### 2.3 Where triggers live in the world **[data]**
- Template components: `KnowledgeTriggerOnKill` (61 templates), `InteractionKnowledgeLock` (67), `KnowledgeTriggerRange`
  (706), `KnowledgeTriggerToggle` (3), `TriggerCollectionCompletionKnowledge` (6), `RuntimeMapMarkerUnlockRequirements`
  (413), `LoreReading` (629).
- **Per placement**: the knowledge itself is mostly scene spawn data (`SceneEntityChunkSpawnData.components`):
  1,707 `SceneKnowledgeTrigger` (operations: 1,399 without context, 192 Range, 277 Interact, 7 Kill, 1 Toggle), 479
  `KnowledgeQuery`, 51 `RuntimeMapMarkerUnlockRequirements`. Example: the Alchemist's rescue spot in chunk 330 sets the
  Alchemist's unlock knowledge `6da00be7` (his `NpcResource.unlockRequirement`, creatures.md §7).
- Item sequences can trigger knowledge too (`ItemSequence.triggerKnowledge` / `knowledgeTrigger`), and so can action
  sequences (`actor::TriggerKnowledge` event, program `actor_trigger_knowledge` #272) and Impact programs
  (`impact_nodes::TriggerKnowledge`).

### 2.4 The registry at run time **[dis] [rt]**
ServerWorldSetup (`0x233ee0`) creates and registers (systems block offsets):

| Singleton | Created by | systems | Notes |
|---|---|---|---|
| `G38Knowledge` (0x20 B) | `0xa2a7f0` | `+0x1b8` | `+8` registry (`0x99ead0`), `+0x10` query state (`0xa2a910`; per player 0x78-byte records at `+0x160`, `+0x74` personal-progression flag, `+0x76` valid; single-player flag `+0x190`), `+0x18` per-player table (multiplayer) |
| `KnowledgeReplicationServer` | `0x99efa0` | `+0x1c8` | per-player replication state ("[Server] Creating player knowledge replication state for player index", `0x9a17e0`; destroyed `0x9a1860`) |
| `g38_knowledge::KnowledgeQueryTriggerRegistry` | `0xa2aee0` | `+0x1c0` | from `GameKnowledgeQueryTriggerResource` |
| `KnowledgeTriggerQueue` (0x8010 B) | `0x99f390` | `+0x1d0` | `{alloc, u32 count +8, 2048 x 16-byte entries +0xc}` |
| `g38_journal::JournalRegistry` | `0x9257d0` | `+0x1e8` | from `JournalRegistryResource` |

Registry (0xd8 B, `0x99ead0`): `+8` data (→ `+0x10` the GameKnowledgeResource), `+0x10` **world map** (dynamic open
hash map: bitset `+0x18`, capacity `+0x20`, keys `+0x38` (u32), values `+0x50` (u64), count `+0x64`; sized for
6,050 entries, capacity 16,384 in the lab), `+0x68` world change list, `+0x98` **player blocks** (16 × 0x90 B on a dedicated server, 1 when single
player `+0xd2`; block: map at `+0` (capacity `+0x10`, keys `+0x28`, values `+0x40`, count `+0x54`), change list
`+0x58`, version `+0x88`, valid `+0x8a`, record flag `+0x8b`, dirty `+0x8c`; sized for 11,000, capacity 32,768), `+0xc0` world version, `+0xd1` dirty. Lookup `KnowledgeIndex` `0x99fc80`
(sigdb `players/KnowledgeIndex`, the progression service already uses it). A state is a u64: low 32 bits the value
(Bool 1, Counter n, Flags bitmask), bits 32..39 a mask the game's code calls "isSaveGameStates" and docs here long called "new flags": set for what comes from a save (cleared per flag as it is set; the notifications read it, §"Notifications"); a missing entry is
`0xff00000000` (0, all new). **[rt]** every one of the 566 world entries of the lab's world is `0xff00000001`.

A character's record **[dis] [rt]**: the login activates the block (`0x99f3d0`: valid 1, version 0, `+0x8b` = 0, `+0x8c` = 1). When the client's
character arrives, about 3.5 s later, `0x7bf5be` calls `0x9a1110`: it fills the map directly (no change list, no version bump) and sets `+0x8b` and
`+0x8c` to 1. `update_knowledge_system_server` (`0x1bdc90`) calls `0x9a48e0` before the queue apply: for each block with the flag it clears the flag, empties
the change list, appends one entry with byte 12 = 2 and bumps the version, which sends the whole map to the client. So that entry, not the flag, is what
code after the update can see. Lab: `quests-test` "a character's record is the baseline", through the game's own `0x9a48e0`.

### 2.5 Granting: the queue and the update program **[dis]**
- **Push** (any thread; programs do it from job threads): `KnowledgeQueuePush` `0x9a3c40(queue, op16)` (sigdb
  `players/KnowledgeQueuePush`), span push `0x9a3b90`, predefined `0x9a3cd0(queue, player, PredefinedKnowledgeTypes)`
  (SkillpointsSpent → `0xd267ae0f`, FastTravelWorld `0xf5124d89`, …). Entry = `KnowledgeUnlockOperation` + a
  `KnowledgeTriggerEntityWrapper` (4 B) from `KnowledgePlayerContext` `0x99f300(entity)`: `{u8 1, u16 player}` for a
  player, a fixed default (no player) for 0; a mask form `{u8 0, u16 mask}` applies one operation to several players.
  Full queue: the push fails (the dialogue path logs "Failed to trigger knowledge from dialog line.").
- **Who pushes** (sys-write `KnowledgeTriggerQueue`): `quest_entity_check`, `update_world/player_query_triggers` (#71
  `0x1be4d0` / #72 `0x1be190`), loot pickup, inventory/equipment/crafting/cooking/fishing/building/mining programs,
  `actor_trigger_knowledge`, `unlock_knowledge_from_dialog_option_input`, `handle_knowledge_trigger_input`,
  `trigger_knowledge_toggle_server` #449, `trigger_knowledge_in_shape` #453 (`KnowledgeTriggerRange`),
  `trigger_knowledge_on_kill` #454, `trigger_collection_knowledge` #455, `trigger_quest_rewards`,
  `trigger_item_set_rewards`, `impact_simulation`.
- **Apply**: program `update_knowledge_system_server` (#747, `0x1bdc90`, flags 0x1814; sys-write G38Knowledge,
  KnowledgeReplicationServer, KnowledgeTriggerQueue, KnowledgeQueryTriggerRegistry; writes Experience; events-out
  KnowledgeAnalyticsEvent, XPGainedEvent) → `0x9a0830` → per entry `0x9a0670`:
  1. world: if the id is in the static **world** map (and for Flags the flag bit is in `expectedFlags`), merge
     (`0x99dc90`), store, change list, version, analytics;
  2. player(s): `0x9a4a80`: if the id is in the static **player** map and the player's block is valid, merge, store,
     change list, version; `experienceGainId` queues XP (→ XPGainedEvent `0x5d5171db`).
  3. then `0xa2ca60` runs the event-based query triggers, re-queues failed ones, and processes the triggers'
     unlocks; the replication is sent (`0x9a2670`), the query states updated (`0x9a4c20`, `0x9a2160`).
- **Merge** `0x99dc90(old, op)`: Bool → 1 (the operation's value is ignored), Counter → old + value (saturating),
  Flags → old | (1 << value) and the flag's "new" bit cleared. **So the queue never lowers a value: revoking needs a
  direct write** (the map entry, the change list, the version: the same three things the apply does; the client
  erases an entry it receives with value 0, §4).
- Unknown ids (not in the static maps) are silently dropped. **A mod's new knowledge needs entries in
  `GameKnowledgeResource`** (both maps if it should work in personal progression).
- The query evaluators: server `0xa2d5f0(out, knowledge, query, player, opts)` (used by `trigger_quest_rewards`);
  client [`0xf0a960`] `(out, knowledge, query, opts)` (used by `ui_dialog` for every line's requirement and "heard"
  check).

### 2.6 Personal progression **[refl] [dis]**
`ecs::PlayerQuestProgressionMode {isInPersonalProgressionMode, changeCounter}` on the player;
`handle_set_personal_progression_input_server` (#451, `0x1bc290`) copies the player input (`+0x19c`, version
`+0x1a0`) into it and tells the query state every step (`0xa2e650(G38Knowledge, player, mode)` → record `+0x74`). In
personal mode a player's queries read the player twins of world objects (`hasOptionalPlayerProgression`) instead of
the shared world objects **[hyp]** (from the data layout and the converter strings; the evaluator branch was not
followed).

## 3. Quest flow, end to end
1. **Start**: a dialogue option's `knowledgeUnlockOperations` (or a world/item/kill trigger) sets the first step's
   visibility knowledge. 29 of 163 SimpleBool visibility ids are set in the lab's world (§9) **[data] [rt]**.
2. **Journal**: client program `journal_ui` (#693, [`0x30e470`], flags 0x5; sys-read BalancingRegistry,
   G38Knowledge, JournalRegistry; sys-write G38UiPlayData; events-out KnowledgeGained) rebuilds its lists when the
   knowledge versions differ from its cached ones (`G38UiPlayData+0x36e9c4/+0x36e9c2`), like `unlocked_recipes_ui`
   (content.md §7) **[dis]**. Steps are listed when their visibility query is true for the local player.
   "[GameUi] Journal: Opened Document Browser : Questlog" [`0xb969a7`]; "Entered Journal from mapmarker reference".
3. **Progress**: game triggers (§2.3) and query triggers (§2.2) set more knowledge; NPC/inventory/flame runtime queries
   are re-evaluated by `update_*_knowledge_queries_server` (#748-750) and `update_player_runtime_knowledge_queries_server`.
4. **Completion and reward**: program `trigger_quest_rewards` (#462, `0x1bd9e0`; per player, up to 50 quests per step
   round-robin) **[dis]**: for each quest whose own player knowledge (`entryId`) is not set and whose **last step's
   completion query** is true for that player, `0x1bbbb0` pushes `entryId = Bool 1` for the player and, when the push
   succeeded, adds every reward's `experienceValueId` amount to the player's granted experience (attribute
   `0x1d7dd102`) with an XPGainedEvent. **Item rewards are not given here**: `CreateItemAction {itemId, type
   ClaimQuestItem}` in the player input is the claim **[refl]**; the handler and its checks were not traced **[hyp]**
   (inventory_actions).
5. **Tracked quest**: the character data has `activeQuestId` (save migration strings `0x7134f5`) **[dis]**: the
   journal's tracked quest is saved with the character.

## 4. Persistence and replication **[dis] [rt]**
- **World save** (server writer `0x7c3bbb`): the world map (non-zero states as `{ids[], states[]}`, version 10,
  `0x9a3000`). Player knowledge is not in the world file: for each of the 16 slots with a valid block, `0x9a2fb0`
  ("[server] Failed to save PlayerKnowledge for %k") writes it into that slot's outgoing client save (characters.md
  section 4). Load: "[save2] Failed to Parse Knowledge" (`0x7c06fd` →
  `0x9a1260`), the player records through save2 ("[save2] Patching player knowledge failed" `0x71c690`, world
  `0x71cba9`), with `flagKnowledgePatch` for old saves.
- **Sanitizing**: loading a player record drops every id that is not in the static player map (`0x9a1960`, the
  `0x99fdb0` check). Knowledge of a removed mod disappears at the next load; a mod's knowledge must be in the static
  maps **before** the save loads.
- **Player blocks** are valid only while the player is on the server (**[rt]** 16 blocks, all `valid 0`, no player).
- **Replication**: per-player state in `KnowledgeReplicationServer`; the server sends change lists ("[server] Failed to
  Send Player knowledge '%k(%k) Capacity exceeded'" `0x7b9fa0`; initial queries `0x7beaf5`) as
  `KnowledgeServerMessage {type PlayerKnowledge|WorldKnowledge, clearKnowledge, knowledgeIds[], unlockStates[]}` on
  channel 10; the client sends `KnowledgeClientMessage {isResendRequest}` (server handler `0x99dbc0`). The client's
  `update_knowledge_system_client` (#751, [`0x41e2b0`]) parses them ([`0x121f8a0`]) into its own registry: type 0 into
  its player block, type 1 into its world map, **by id without a static-map check**, and a value 0 **erases** the
  entry ([`0x121b540`]).
- Vanilla clients therefore store states for ids they do not know (harmless; nothing reads them).

## 5. Dialogue, NPC interaction and their knowledge
In `docs/re/creatures.md` §7 (layout, the select/unlock programs, what the server trusts, hooks). Summary: the client
builds and filters the options from its own template data; the server only receives `{option id, NPC}` and runs that
line's knowledge operations plus "line heard" (knowledge id = the line id), without checks.

## 6. Markers: map, compass, quest areas
- **Map markers** are entities (map.md): `MapMarker {markerId LocaTagReference, markerType, dungeonId,
  isInBigDungeon}` on 753 templates, 999 placements in the scene's global chunk **[data]**.
- **Quest links**: `JournalEntryResource.mapMarkerReference` = the marker's `markerId` tag id; 238 of 240 step
  references match a template's marker (the 2 others: "Mortar Location", "Dragon's Hideout") **[data]**. 52 of them are
  `mapmarker_investigation` (`fffa09d9…`) search-area labels (`…_searchArea_noUI` templates with
  `KnowledgeTriggerRange`), others are villages, camps, survivor camps. The journal opens from a marker ("Entered
  Journal from mapmarker reference") **[dis]**.
- **Visibility**: `RuntimeMapMarkerUnlockRequirements {hintStateQuery, visibleStateQuery, completedStateQuery}` (36 B,
  template or scene) and `MapMarkerUnlockRequirements` (full requirements + query ids); client programs
  `knowledge_query_mapmarker_visibility(_state)` (#459 [`0x41c150`], #460 [`0x41c3f0`]) compute
  `MapMarkerVisibilityState {visibility, lastChange, knowledgeChangeCounter}` from the client's knowledge;
  `replicate_map_markers_for_ui` (#667 [`0x3161b0`]) hands markers to the UI; `location` (#659) the area names
  **[dis]** (program table).
- **Waypoints**: `ecs::PlayerWaypoint {enabled, waypointPosition}` is a component of the player entity written by the
  **server** program `player_waypoint_selection` (#314, `0xd64f0`; input `WaypointAction {position, enable, isPing}`;
  events-out UiPingEvent) and read for every player by the client's `player_waypoints_ui` (#724) **[dis]**. A server
  mod can write a replicated waypoint. **Corrected 2026-11 (dev.28 RE, S 26430253 `f0d824be…`, C 26430625
  `5d9ef2f1…`) [lab] [seen]:** registry marks PlayerWaypoint replicated/not saved; session 4 showed a vanilla
  compass diamond 304–321 m away after exact server component readback. A distinct waypoint map-pin shape and
  restoration were not established. See [map.md](map.md) §7.
- New markers need a template with a `MapMarker` (its name and icon are template data): a Kiln client and a cloned
  template, or an existing marker template placed by world-edit (its vanilla name).

## 7. Programs (server RVA; [client RVA] for client programs)

| # | Program | Side | What it does (for this topic) |
|---|---|---|---|
| 1 | `quest_entity_check` `0x1031a0` | server | SceneQuestEntity records vs player bases → knowledge (§1.3) |
| 71/72 | `update_world/player_query_triggers` `0x1be4d0`/`0x1be190` | server | query triggers → unlocks |
| 73 | `update_terraforming_queries_server` | server | |
| 76 | `knowledge_trigger_copy_scene_knowledge` `0x1bc590` | server | SceneKnowledgeTrigger → KnowledgeTrigger at spawn |
| 272 | `actor_trigger_knowledge` `0x87b40` | server | sequence event TriggerKnowledge |
| 285/286 | `npc_unlock_state_server` `0x1fa330` / `_client` | both | NpcRegistry unlock states from knowledge (creatures.md §7) |
| 291 | `select_dialog_option_input` `0x1fab80` | server | input → ServerSelectDialogOptionEvent |
| 292 | `unlock_knowledge_from_dialog_option_input` `0x1fb380` | server | input → the line's knowledge ops |
| 449, 453-456 | `trigger_knowledge_toggle_server`, `_in_shape`, `_on_kill`, `trigger_collection_knowledge`, `knowledge_query_toggle` | server | world triggers |
| 451/452 | `handle_set_personal_progression_input_server` `0x1bc290`, `handle_knowledge_trigger_input` `0x1bc140` | server | client requests |
| 462/463 | `trigger_quest_rewards` `0x1bd9e0`, `trigger_item_set_rewards` `0x1bcef0` | server | XP rewards, "rewarded" knowledge |
| 747 | `update_knowledge_system_server` `0x1bdc90` | server | drains the queue, XP, triggers, replication |
| 748-750 | `update_player_runtime_knowledge_queries_server` `0x1be2e0`, `update_world_knowledge_queries_server` `0x1be470`, `update_player_knowledge_queries_server` `0x1be0f0` | server | query state |
| 751 | `update_knowledge_system_client` [`0x41e2b0`] | client | receives replication |
| 75, 457-461, 575 | item knowledge db, toggles, map marker visibility, UI world events, non-modal hints | client | knowledge-driven client state |
| 684, 698, 699 | `ui_dialog` [`0x460b40`], `play_dialog_voiceover` [`0x460360`], `stop_dialog_voiceover` [`0x460900`] | client | dialogue UI (creatures.md §7) |
| 690, 693, 694 | `fill_ui_npc_directory` [`0x45d160`], `journal_ui` [`0x30e470`], `unlocked_recipes_ui` | client | journal pages |

The descriptor's lists (offsets in the 0x188-byte descriptor): `+0x18` template read, `+0x28` template read optional,
`+0x38` read, `+0x48` read optional, `+0x58` write, `+0x68` write optional, `+0x98` singletons read, `+0xb0`
singletons write, `+0xd0`/`+0xe0`/`+0xf0` random-access template/read/write, `+0x160` events in, `+0x170` events out
**[dis]** (cross-checked on the programs above).

## 8. What a mod can do, and what it needs

| Change | Server only (vanilla clients) | Needs Kiln + data on the client | Authority |
|---|---|---|---|
| Grant a vanilla knowledge id (start/advance/complete a vanilla quest, unlock a vanilla NPC, mark a line heard) | works: queue push; the client's journal, NPC directory and markers follow through replication | – | server |
| Read a player's/world progress | registry maps (§2.4) | – | server |
| Revoke a knowledge id | direct map write + change list + version (no game API) **[hyp]** that the client erases it (it does for value 0, §4) | – | server |
| New knowledge ids | static map entries (resource rebuilt before the world loads) | ids are stored by vanilla clients but used by nothing | server |
| New journal quest | server: `JournalRegistry` entry only matters for `trigger_quest_rewards` (XP) | the journal is client data: `JournalRegistryResource` + loca + knowledge on the client | client display, server reward |
| Rewards | XP via the game's path or Kiln's progression; items via Kiln.Items | – | server |
| New dialogue lines/options, texts | the server runs knowledge of lines it has in its template data | options are built from the client's template data and loca | client UI, server effects (unchecked) |
| React to "player talks to NPC" | `ServerSelectDialogOptionEvent` / the input (creatures.md §7) | – | server |
| Objective markers | `PlayerWaypoint` vanilla compass **[seen, dev.28 session 4]**; new shipped-template map icon appears/removes **[seen, session 5]**; range/per-player relevance unproved | new marker names/icons/visibility | client display |
| Objectives (kill, collect, reach, craft, talk) | Kiln events + knowledge or Kiln state | – | server |

## 9. Lab run (a copied save, no player) **[rt]**
A probe (advanced mod): mid hooks at `0x99e450` (rcx = registry; called by
`update_knowledge_system_server` every step), at `0x9a0830` (rdx = KnowledgeTriggerQueue) and after the journal getter
in `trigger_quest_rewards` (`0x1bda0c`), all found by pattern; reads on the tick thread afterwards.
```
quests-probe: sites: registry 0x99e450, queue apply 0x9a0830, journal quests 0x1bda0c
quests-probe: hits: registry 2, queue 2, journal 0; registry 0xfc674a98, queue 0xf0703db0, journal quests 0x0
quests-probe: registry: single 0, dirty 1, version 1, world map count 566 cap 16384; data 0x897fff70 resource 0x64153aa0
quests-probe: GameKnowledgeResource: world buckets 8192 keys 5825 values 5825; player buckets 16384 keys 10744 values 10744; achievements 58 skillPoints 78 patch 27120
quests-probe: player blocks: 16 at 0xfc6bce30: [0: valid 0 count 0 cap 32768 ver 0] … [15: valid 0 count 0 cap 32768 ver 0]
quests-probe: KnowledgeTriggerQueue: count now 0, max seen at apply 0 (capacity 2048 x 16 B)
quests-probe: world knowledge dumped: 566 entries (566 non-zero)
```
- The journal getter site did not run: `trigger_quest_rewards` iterates players and has none.
- Offline cross-check of the dump : all 566 ids are static world objects
  (302 Flags, 264 Bool); 7 NPCs unlocked (Farmer, Alchemist, Carpenter, Farmer assistant, Hunter, Collector,
  Blacksmith); 15 of the Alchemist's 33 option lines are marked heard in the world map; 29 of 163 SimpleBool step
  visibility ids are set.
- The progression worker's lab (`labs/progression`, kiln.log 19:05) pushed player knowledge through the same queue
  ("request 5 give_points … ok (applied 1 … points 6 -> 7 of 44)").

## 10. Open questions
1. **Answered** (section 11): the evaluator's rule for world objects and player twins.
2. The item-reward claim (`CreateItemAction` type ClaimQuestItem): which program, which checks.
3. **Answered** (section 11): the registry and knowledge only; nothing reads `JournalRegistryInfo`.
4. `PlayerWaypoint` replication and vanilla compass drawing answered by dev.28 session 4; distinct map-pin shape,
   restoration and far/multiplayer marker relevance remain open ([map.md](map.md) §7).
5. The query state (`G38Knowledge+0x10`) sizes its per-query arrays from the query db at creation: new Extern queries
   need the query db rebuilt before the world starts, and probably the same `queryIndex` order on clients **[hyp]**.
   Kiln quests can avoid them (SimpleBool/Count/Flag steps, Kiln-side logic).
6. Client-side behaviour when the server sends a knowledge id the client's journal references but its static maps
   lack (stored by id, §4: expected to work; client checklist).

## 11. Native quests (server Steam 26430253, client 26430625, svn 1287369)

Ghidra decompiles (no analysis, one function at a time) and capstone scans over read-only copies of both exes, tools/kfc
over the client's data, a lab (no player). No client was run: everything about the client's
behaviour below is **[dis]** only. Dialogue, roster and recruiting: creatures.md 7.8. Usage: `docs/kiln/services/npcs.md`.

### When the registries read their resources, and whether a boot-time override reaches them
- **Every reader goes through the resource service.** The type hashes are immediates in the code, which finds the readers
  **[dis]**: `GameKnowledgeResource` `0xbc47511c` server `0x99ee60` [client `0x1219630`]; `JournalRegistryResource` `0x60b5ed8a`
  server `0x9257d0` [`0xe7e650`]; `NpcCollection` `0x43ae2af5` server `0x926110` [`0xe7ef90`]. Each calls the ByKey wrapper
  (server `0x829cd0`, client `0x1080b60`) and keeps a **handle**, not the data. The data comes from ResourceData later: the
  knowledge object's `+0x10` (`quests/data.resource`) was already read this way, `JournalRegistry+0x48` holds the journal's
  (client getter `0xe94b10`).
- **Who creates them.** The knowledge data object is made once at app level (server `0x53bd20`, client `0x852a46`) and
  handed to every world's `G38Knowledge` (`0x99ead0(alloc, data, single)`). The journal and the roster are per world: server setup
  `0x233ee0`; on the client two callers each, `0x3a60b0` and `0x49be10`, one per world setup **[hyp]** (not told apart here).
- **Nothing can read earlier than Kiln.** The service object is created by `ResourceInit` (`assets/ResourceInit`). A post-hook on it
  runs before any reader can exist. On the client too, the service comes up after Kiln's services: a real client's kiln.log
  has `assets: ready` at 20:10:57.935, then `assets: resource service … initialized` at 20:11:05.451 **[rt]**. So one
  override in that hook reaches hosted and joined worlds alike **[dis]**.
- **[rt]** server: the registry's pointer is the override, and the game's lookups use it:
  ```
  knowledge: native: 2 mod(s): 49 new knowledge object(s), 3 quest(s), 2 NPC(s) in the roster, 6 template(s), 0 text(s); GameKnowledgeResource 1509112 bytes served by kiln.assets
  knowledge: GameKnowledgeResource 000000007C530040 (registry data 00000000897FFF70 + 0x10) is the resource service's copy of cb1eeb8b (000000007C530040, 1509112 bytes)
  knowledge: catalog: 37 NPCs (30 with an unlock requirement; NpcRegistry: 0 unlocked, version 0), 191 quests (411 steps), 5296 named queries, 4494 names
  ```
  A new template served at boot is registered by the world's own init (`0x667c10`
  lists `ResourcesByType`): `Spawn.Lookup` finds it with no `RegisterTemplateInfo` call from Kiln, and the log keeps its
  `Initialized %,u template infos` count **[rt]**.
- **Consequence for the design:** the server's managed mods load at the first world step, after world setup, so the build has
  to be native. Managed code only gets the result.

### The personal-progression branch **[dis]**
`0xa284c0` (server; client `0xf055d0` has the same shape). Mode: `iVar10` from the options: 2 = personal, 1 = the player's flag
(record `+0x74` when valid `+0x76`), 0 = shared; with no player and no single-player flag, shared. For a Simple query with id `k`:
1. personal and a player given: the player's object;
2. else, unless `isExplicitPlayerKnowledgeQuery`: the world object;
3. else, when not personal and a player is given: the player's object;
4. none: error `0x1f`.
So a player-only object evaluates in shared mode as well, which is what Kiln's quest and line objects rely on. The client's wrapper `0xf0a990`
**swallows** `0x1f` (no error, value false) and returns `0x12` for id 0.

Compare (`0xa27b30`): SimpleBool is `value != 0` (compareValue ignored); SimpleCount compares with `compareOperator`, so `SimpleCount
Equals 0` means "not set" on any object; SimpleFlag is `flags & compareValue != 0`.

### Journal UI caches **[dis]**
`journal_ui` [`0x30e470`] rebuilds its lists when `G38UiPlayData+0x36e9c4` differs from the registry's version (`JournalRegistry+0x50`, getter
`0xe8e400`) or `+0x36e9c2` from the knowledge version (`0xf090a0`). Its lists grow through a callback (`0x2f8730`), so more quests only cost
memory. A boot override is in place before the first build, so no invalidation is needed; a mirror's knowledge change bumps the version
by itself. The registry is not re-read during a session: there is no hot change, and Kiln does not attempt one.

### `JournalRegistryInfo` / `isHidden` **[dis]**
The type hash `0x79d8d8da` is in the `.text` of neither exe. `0xe94b10` returns `reg+0x48`, and `journal_ui` reads it as `{quests +0, collections
+8}`: that is the `JournalRegistryResource` itself, not the Info type. So the quest list depends only on the registry and knowledge; `isHidden`
has no reader found. Open question 3 of section 10 is answered.

### What a quest entry needs **[data]**
All 188 quests have `referencedDocumentName`, priorities are 40..100, all 406 steps have `name` and `text`. Kiln fills all of these; a step
with only `text` uses that tag for both. Marker visibility [`0x41c3f0`]: completed, then visible, then hint; a zero id is skipped. Steps are listed by
`knowledgeRequirement`; `trigger_quest_rewards` still applies to Kiln's entries, which have no rewards in the resource (the engine pays).

### Lab
A lab, a clean copy of the lab save, `mods/tests/quest-native-test`. Log excerpts, in order:
```
(negative: the mod's quests/ renamed)
quest-native-test: PASS the game's maps are unchanged: 5825 + 10744
quest-native-test: Kiln refuses: Knowledge.Grant: 0x06931fa5 is not world knowledge in this game build
quest-native-test: unchecked grant through the game's apply: Grant 0x06931fa5: 0 -> 0 (…)
quest-native-test: PASS the game's own apply drops an unknown id: value stays 0
(run 1, with the data)
quest-native-test: PASS the rebuilt maps are live: 5839 + 10780 objects
quest-native-test: PASS the server accepts a grant of a Kiln id: Grant 0x06931fa5: 0 -> 1
quest-native-test: PASS a Kiln counter: Grant 0x325dfa73: 0 -> 3
quest-native-test: PASS a Kiln flags object, flag 1: Grant 0xd49a3989: 0 -> 2
quest-native-test: PASS a flag outside expectedFlags is refused
quest-native-test: PASS the game's evaluator shows the step: visible True, done False
quest-native-test: PASS mirror: done set, active cleared
(run 2, restart)
knowledge: first knowledge update of this world at step 272: world map 575 entries
quest-native-test: PASS run 2: the world save kept the Kiln id 0x06931fa5
quest-native-test: PASS run 2: the world quest's done knowledge survived (the game's save)
(run 3, restart without the mod)
knowledge: first knowledge update of this world at step 272: world map 567 entries
quest-native-test: PASS a restart without the mod: its world ids are gone from the map (the world map only has static objects)
```
Whole sequence on the final build (clean save, then restart, restart without the mod, restart with it): 62, 45, 7 and 5 PASS, 0 FAIL, no `[E` line.
The 567 is the save's 566 plus the unlock of the game NPC the test placed, one of the game's own ids. So the world's loader drops unknown ids as the
player's does (section 4 had that only for player records); the player path needs a client.

## 12. The game's UI as the default (client 26430625 RVAs in brackets, decompiled with Ghidra; game data read with tools/kfc)

### A step's counter **[dis] [data]**
`journal_ui` [`0x30e470`], for a step that is visible (`JournalEntryResource.progressStepsRequirement`, +0x28):
- **One query of type SimpleCount**: total = `compareValue + 1`; current = the counter's value [`0xf0b140`], clamped to total. One of the game's steps does this: `SimpleCount GreaterThan 24 0x87f058dc`, a count to 25 (quest `66631369`).
- **Otherwise**: total = the number of queries; current = how many hold. Nine of the game's steps do this, with 3 to 8 SimpleBool or SimpleFlag queries.

Kiln uses the first form on a Counter object: `SimpleCount GreaterThan n-1`.

### Earlier steps show as done **[dis]**
The same loop runs from the last step to the first. Once a later step is visible, every earlier one is forced visible and done, whatever its own completion query says. So parallel objectives cannot be
separate journal lines: an active later line marks the earlier ones done. A step with several objectives is one line, its counter their sum.

### Notifications **[dis] [refl]**
`0x2fc8e0`, called for each step's visibility and completion, evaluates the query ([`0xf0aa20`]'s result: 0x4 it holds, 0x1 the state's bits 32..39 are not 0 ([`0xf04d90`]), the mask of Q§2.4); one that holds without that mark, and hasn't notified in this client world (a per-query dedupe map), emits `ecs::KnowledgeGained {timeStamp, targetType, workshopId, npcId, recipeId, journalId, itemSetId, mapMarkerEntityId}` (hash `0xa138813f`, 40 B) once per query per mode. `KnowledgeEventType`: JournalEntry 1, JournalCompletion 2, Recipe 3, MapMarker 4, NpcUnlock 5, ItemSetCompletion 6, ItemSetHint 7. UI strings `notificationNewQuest`, `notificationQuestUpdated`, `knowledgeNotificationNewQuest`. So the game's notifications follow knowledge granted through its own apply, with nothing more from a mod.

**What the mask means:** the message's field is `isSaveGameStates` (`KnowledgeServerMessage {packageId, subPackageId, subPackageCount, isRecoveryResponse, isSavegameData, clearKnowledge, knowledgeIds, isSaveGameStates, unlockStates}`; [`0x121b540`] takes the per-entry array, else 0xff for the whole message when `isSavegameData`). A state from a save has it (the lab: every world entry `0xff00000001`), so loading a character's progress notifies for nothing. The game's apply clears it; a direct write keeps what the entry had, and a missing entry reads `0xff00000000`. So a direct write of a Bool is a value the client treats as old: `quests-test` "a direct write of a new id keeps the save's mark", "the game's own apply clears it". Kiln.Quests restores a join's progress this way (services/quests.md "The mirror").

### What the journal's texts are **[data]**
A step's `name` is the short line ("Reach The Elixir Well"), its `text` the paragraph. `referencedDocumentName` is no loca tag of the English table for the quests read; only 32 of 188 have it equal to `name`, and it has no visible use found. Kiln: `title` and `text`; a first step without a title takes the quest's `description` as its paragraph.

### Which markers the client lists **[dis]**
`replicate_map_markers_for_ui` [`0x3161b0`] skips an entity whose `MapMarkerVisibilityState.visibility` is Hidden, the marker type `77997dbc…`, and `6581fe05…` in one case. The investigation type `fffa09d9…`, which 63 of the journal's step markers use and Kiln's default base has (icon, `showRange`, `showAboveFogOfWar`), is listed. Not read here: whether the server replicates a spawned marker entity to a client far from it. The game's own markers are in the scene's global chunk, on both sides. That is why a step also writes the waypoint by default; the manual test decides.

Also [`0x41c3f0`]: a zero id is skipped, so a marker with no completed query goes back to hidden when its visible query stops holding. The game's search areas have none. Kiln's did, and would have left a finished step on the map.

### Base zones and the tick thread **[rt]**
In a lab, at `ServerStartedEvent`: `Build.Bases` is empty, and 3 s later it has the world's base (`NPC … at (3800.0, 833.0, 1434.0) (the voxel surface, no base in this world)`, then `is in or next to the base at (3800.0, 832.5, 1434.0)`). Kiln waits up to 8 s for the first base.

Real client, a lab: `Advance tobin_kindling.return: FAILED: This must run on the tick thread`, after `complete (1x)`. `Items.Take` completes with `RunContinuationsAsynchronously`, and service code has no synchronization context, so what followed the await ran on the thread pool; `Events.Post` threw before the mirror. Rewards were paid and the journal never heard. Fixed by `QuestTick`; `quest-engine-test` and `quest-native-test` now wait on another thread on purpose.
