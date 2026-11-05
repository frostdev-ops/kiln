# Character progression on the dedicated server: level, experience, skill tree, skill points (svn 1287369)

Service: `kiln.progression` (in `native/services/players`, `sigdb/symbols/server.d/progression.json`, usage in
`docs/kiln/services/progression.md`). Build: dedicated server Steam 26430253 / svn 1287369 (`f0d824be…`, image base
0x140000000, RVAs below); every pattern also resolves on the previous server build 26404334 (`c5c9fe50…`, functions
+0x10 from 0x99e500 on) and on the client 26430625 (`5d9ef2f1…`) (`sigdb_check.py`, all four features on). Sources: the
game's reflection (`tools/kfc types`), decoded resources (`tools/kfc decode`), the program table parsed from
`RegisterAllPrograms` 0x245960 with the real descriptor layout (`native/services/items/items.cpp` `ProgramDesc`), Ghidra
12.1.2 headless (no auto-analysis, functions created on demand) and capstone over a read-only copy, and the lab
`progression` (port 26260, a copied world, no player; `mods/tests/progression-test`). Tags: **[refl]**
reflection, **[data]** decoded resources, **[dis]** disassembly / decompile, **[rt]** lab runtime (kiln.log quoted),
**[hyp]** hypothesis.

## Short answers
- **Where it lives.** The player entity's components: `Level` and `Experience` (UInt32Attributes), `UnlockedSkillNodes`
  (2,316 B) and `PlayerSkillsUi` (136 B, derived) **[refl]**. Skill points are **not stored**: total =
  `skillPointsPerLevel` (2) × (level − 1) + the amounts of the player's skill-point knowledge; available = total − the
  costs of the unlocked nodes **[dis] [data]**. The world save holds none of it: characters are client-side
  (`characterN.ksd`, block `player_character`: level, experience, node list), the world's `known_users` block has no
  level **[data]**.
- **How the game grants experience.** Every source (`combat_experience_source` #417, `trigger_quest_rewards` #462,
  `trigger_item_set_rewards` #463, `impact_simulation` #510) adds to the Experience attribute's **granted** value,
  re-runs the attribute formulas and sends `XPGainedEvent` (an output for the client's `player_ui`; nobody on the server
  reads it). `player_level` #677 (host only, write Experience, Level, PlayerTracking; with PlayerState) moves granted into
  experience and raises the level **one per step** while experience ≥ needed, with `LevelUpEvent` **[dis]**.
- **The game's admin path.** `PlayerInput.fromAdminClient.adminSetCharacterLevel {versionData, targetEntityId, level u8}`
  (admin_player #97, gated by `IsAdmin`): level := n, granted := 0, experience := 0, needed := XpForLevel(n + 1)
  **[dis]**. Injecting into PlayerInput is not used (the server's consumed version replicates back to the client in
  `NetworkConsumedPlayerInput`); Kiln writes the same values in its own program (needed = XpForLevel(n), as
  `player_level` sets it for a level reached by playing).
- **Skill nodes.** `player_skilltree` #201 handles the client's `UnlockSkillAction {skillId, unlearnSkill,
  resetAllSkills, versionData}`; unlock checks the links (`SkillReachable`) and the points; unlearn keeps the rest
  connected and pays a cost item (`0x41a792dc` × cost); reset-all is free when the game itself resets an over-budget tree
  at login **[dis]**.
- **Bonus points are knowledge.** `GameKnowledgeResource.skillPoints` lists 78 player knowledge flags: 60 × +1
  (`additional_skillpoint_01..12`, `plant_1..3`, three unnamed) and 18 × +3 (Elixir wells `well_02..18`, `WellCompleted`,
  `Knowledge_no_skillpoints_yet`) = 114 points **[data]** (names from the vendored editor's `conhecimento.json`). The game
  sets player knowledge through `KnowledgeTriggerQueue` **[dis]**.

## 1. Components **[refl]**
| Type (hash) | Size | Fields |
|---|---|---|
| `ecs::Experience` 0xa446f23f | 20 | UInt32Attribute `{rootId, storageOffset u16, flags u8 (bit 0 initialised), storageCount u8}` + `dataStorage[3]` |
| `ecs::Level` 0xe550da37 | 16 | UInt32Attribute + `dataStorage[2]` (template `1_Player`: `[1, 100]`) |
| `ecs::UnlockedSkillNodes` 0xa8d1e07c | 2316 | `initialized` +0, `knownSkillNodes[256]` +4, `activeSkillImpacts[256]` +0x404, `unlockLevel[256]` +0x804 (level in the 3 high bits), `version` +0x904, `changeCount` u8 +0x908 |
| `ecs::PlayerSkillsUi` 0x075026be | 136 | `skillUnlockMasks` 4 × u64 +0 (bit = node index), `unlockLevel[100]` +0x20 (by `levelIndex`), `changeCount` +0x84 |
| `ecs::NetworkLevel` | 2 | `level` u16 (written by `network_level_attribute` #526) |
| `XPGainedEvent` 0x5d5171db | 24 | `timeStamp`, `playerEntityId` +8, `amount` +0xc, `source` u8 +0x10 (`ExperienceSourceType`: None, Exploration, Lore, Quest, Combat, Mining, ItemSetCompleted) |
| `LevelUpEvent` 0x4183785e | 56 | `timeStamp`, `position` +8, `playerEntityId` +0x20, `duration` +0x28 (from PlayerTracking), `level` +0x30 |
| `SkillTreeNode` | 140 | `id` +0, `uiPosition` +4, `activeSkillUnlock` +0x28, `incomingLinks` +0x74, `outgoingLinks` +0x7c, `costs` u16 +0x84, `type` +0x86 (Root, Attribute, ActiveSkill, PassiveSkill), `classAlignment` +0x87 (Warrior, Mage, Ranger, Neutral), `maxLevel` +0x88, `levelIndex` +0x89 |

Attribute ids (`AttrFind` 0x991810 keys; the Experience root's id is the experience id) **[dis] [rt]**:
`0x724bf568` level (Level slot 0), `0x8dcddaf4` experience (slot 0), `0x1d7dd102` granted (slot 1), `0x04723f62` needed
(slot 2). **[rt]** "Experience root 0x8dcddaf4, slots level -1 experience 0 granted 1 needed 2", "Level root 0x724bf568,
slots level 0". The Level formula clamps the level to its second value: "Level after writing 30: 30,100; after writing
200: 100,100".

`network_player_attributes` #528 reads Experience and Level every step into the replicated `NetworkPlayerAttributes`;
`attribute_save` #523 reads `UnlockedSkillNodes` **[refl]** (program lists).

## 2. Levels and experience **[dis]**
`player_level` 0xd3bf0, per player (Ghidra, slot names from the descriptor's write order Experience, Level, PlayerTracking):
```
level = Level[0x724bf568]; xp = Exp[0x8dcddaf4]; granted = Exp[0x1d7dd102]; need = Exp[0x04723f62]
if level < LevelCap(balancing):                       // 0x940570: BalancingTable.playerLevelCap (+0x10)
    if granted: Exp.xp += granted; recompute(Exp) ; Exp.granted = 0; recompute(Exp)     // 0xab970
    if need == 0 and level == 1: Exp.need = XpForLevel(1); recomputeNeed(Exp)            // 0x52700
    elif need == 0 or need <= xp:
        Level += 1; Exp.xp = xp - need; Exp.need = XpForLevel(Level)
        emit LevelUpEvent(time, CurrentTransform, entity, PlayerTracking[0], Level); PlayerTracking[0] = 0
else: Exp.granted = 0                                  // experience at the cap is discarded
```
Every write is preceded by MarkWritten on the write record and followed by the formula re-run.

`XpForLevel(level, balancing)` 0x93e900 reads `BalancingTable` 82706b40-… (`BalancingRegistry+0x20`): levels 1–5
`experienceNeedPerLevel` [1000, 2023, 3046, 4069, 5092]; from 6: `base = min(s, e) + |e − s| × level / playerLevelMax`
(s = killToLevelUPStart 50, e = killToLevelUPEnd 200, playerLevelMax 100), `v = base × xpSeedEnemy (50)`, `v × 1.5 +
xpNeededFromKnowledge (2562)`, rounded half away from zero. **[rt]** the game's function equals this for 1..50:
"experience per level 1:1000 2:2023 3:3046 4:4069 5:5092 6:6987 10:7437 20:8562 30:9687 40:10812 50:11937"; cap
"level on (cap 50 …)".

XP grant (`0x1bbbb0`, the reward helper of trigger_quest_rewards / item sets): `Exp.granted += amount`, the formula
re-run inline, `XPGainedEvent {player, amount, source}`. The quest rewards check `BasicPlayerInfos[e-1] +0x9D & 0x20`
(CanReceiveEXP) first (`0x163040` returns the byte), and `IsAdmin` checks bit 0 (CanKickBan): the +0x9D byte holds the
user group's permission bits (answers docs/re/players.md open question 2 in part) **[dis]**.

admin_player (0xbc0b0) set-level block (PlayerInput +0x4a4 version vs ServerConsumedPlayerInput +0xc0, target +0x4a8,
level +0x4ac): writes through other-write accessors of the target: level, granted 0, experience 0, needed
`XpForLevel(level + 1)` **[dis]**. Set-flame-level (+0x49c/+0x4a0) writes the Level of `ServerProgressSystem.progressEntity`.

## 3. Skill tree and points **[dis] [data]**
- `SkillPointsTotal` 0xaa810 `(out{u8 err; u32 value@8}, G38Knowledge, BalancingRegistry, level, entity)` = 
  `KnowledgePoints(entity) + SkillPointsPerLevel (0x944aa0: BalancingTable.skillPointsPerLevel +0xb4 = 2) × (level −
  1)`; error 0x12 for level 0, the knowledge error when the player's knowledge is not loaded.
- `KnowledgePoints` 0x99e500 via the wrapper 0xa2a530 `(out, G38Knowledge, &entity)`: `sys = *(G38Knowledge + 8)`;
  block = `*(sys+0x98) + (sys[0xd2] ? 0 : entity − 1) × 0x90`, valid `+0x8a`; sums `amount` of every
  `GameKnowledgeResource.skillPoints` entry (`*(*(sys+8)+0x10) + 0x38`, 8 B `{id, amount}`) whose value in the block's
  hash map (`KnowledgeIndex` 0x99fc80; capacity `+0x10`, values `+0x40`, 8 B each) is not 0.
- `SkillPointsSpent` 0x9222f0 `(ui masks, ui + 0x20, tree)`: for each set bit, `costs`, or `(unlockLevel[levelIndex]
  >> 5) × costs` for nodes with levels.
- `SkillNodeById` 0x944b70 `(id, tree)` (map at tree+0x50), `SkillReachable` 0x91f990 `(id, ui masks, tree)`: a root
  is always reachable; a locked node needs an incoming link from an unlocked node or a root, or an outgoing link to an
  unlocked node; an unlocked node with levels is "reachable" while below its max level. Root nodes are implicit (never
  stored in `knownSkillNodes`).
- `SkillNodeSet` 0x91fe90 `(id, UnlockedSkillNodes*, unlock, tree)`: unlock adds the id (first free slot) or one level;
  unlearn removes a plain node or one level (the id stays at level 0).
- `SkillUiRebuild` 0x92d670 `(ui, UnlockedSkillNodes, tree)` derives the masks and copies `changeCount`;
  `SkillTreeConnected` 0x96f8b0 `(ui, tree)` compares the masks with the ones reachable from the roots (0x92e000).
- `player_skilltree` 0xd4780: rebuilds PlayerSkillsUi when `UnlockedSkillNodes.changeCount` ≠ the copy, validates the
  tree once per spawn (0xe00b0: links, levels, spent ≤ total, else **reset all, free**, 0xb9a00 with pay 0), and runs
  the version-checked UnlockSkillAction: unlock 0xbbc60 (link check, cost, SkillNodeSet, knowledge context 0 via
  0x9a3cd0), unlearn 0xbbab0 (SkillNodeSet off, rebuild, connectivity, pay 0xb9230 = remove `0x41a792dc` × cost; undo on
  failure), reset 0xb9a00 (pay the whole spent sum unless free).
- `handle_skill_impacts` #205 then spawns / removes the nodes' impact programs from PlayerSkillsUi every step;
  `server_progress_scaling` #505 derives the attribute points.
- Tree resource 108ac23f-…: **[rt]** "tree 243 nodes: Root 12, PassiveSkill 123, ActiveSkill 43, Attribute 65, 91 with
  levels, 5 unlock an ability"; the node list read at `*(tree+0x20)` agrees node by node with `SkillNodeById` and with
  kiln.gamedata's reflection reader ("the tree agrees with kiln.gamedata's reflection reader (243 nodes: ids, costs, max
  levels)"); the double jump `0xffd5f940` is an ActiveSkill of cost 2 that unlocks `Ability_DoubleJump`.
- On an empty tree **[rt]** (the game's functions on a scratch UnlockedSkillNodes): "21 of 231 nodes are next to a
  root", "unlock 0xc01ee111: spent = its cost 2, 1 node, connected", "unlock 0xbc1ca1a4 through it: spent 4, connected",
  "locking the first node disconnects the second", "node 0xa598792e with 3 levels: level 1..3, spent 1 per level", "one
  level past the last: refused (the link check says so too)", "respec: nothing unlocked, nothing spent".

## 4. Knowledge **[dis] [data]**
- `KnowledgeTriggerQueue` (singleton, `systems+0x1d0`, registered at 0x23570b): `u32 count` +8 (atomic), entries of 16 B
  from +0xc, at most 0x800. Push `0x9a3c40(queue, op)` copies `{u32 knowledgeId; u8 type (Bool 0, Counter 1, Flags 2);
  u32 value @+8; u32 context @+12}`; the context of a player is `0x99f300(entity)` = `(entity << 16) | 1`.
- Producers: quest / item-set rewards (`{id, Bool, 1}` + the player's context), the client's `TriggerKnowledgeAction`
  (`handle_knowledge_trigger_input` 0x1bc140: any id ≠ 0, the player's context), loot, crafting, … 30 programs declare it.
  The consumer was not traced (it is not one of the program updates read here) **[hyp: the knowledge system's update]**.
- Kiln's give-points pushes `{id, Bool, 1}` for unowned +1 entries, inside its own program (declared `sys_write
  KnowledgeTriggerQueue`), like the game's producers. **[rt]** "skill-point knowledge on (78 entries, 60 of them +1, 114
  points in all)".

## 5. Kiln's program (lab) **[rt]**
```
program kiln_players_progression registered as #824 (T0 R0 W3 S5 OT0 O0 EI0 EO1, iterator 200 bytes, flags 0x10)
progression: program kiln_players_progression accepted (write slots 1 7 13, singletons 19 20 21 22 23, XPGainedEvent slot 24)
progression: world 00000000B2410080: level on (cap 50; experience per level 1..5: 1000 2023 3046 4069 5092, at the cap 11937),
  skills on (243 nodes, 2 points per level), skill-point knowledge on (78 entries, 60 of them +1, 114 points in all)
progression-test: offline done: 45 PASS, 0 FAIL
reload-test progression-test: 3 reloads, 0 failures, avg 15.66 ms, max 38.27 ms, 0 leaked context(s)
```
The write path, the client's view and the character save were checked in a client (section 6).

## 6. In a client **[rt]**
Vanilla client 26430625 (Kiln not loaded: play-once vanilla) joined lab `progression` as Admin with a LOCAL character
copy (level 16, 4814/8112, 37 of 37 points spent, 21 nodes, 7 points from knowledge). Driven through Kiln.Admin's file
bridge; seen in the game's own UI:
- `give_xp 500`: the character screen's XP bar moved from 59 % to 65 % at once (5314/8112). `give_xp 300` with source
  Combat: the HUD showed "Enemy slain XP +300" — **server-side game events reach the client** (`XPGainedEvent`, the
  source picks the label).
- `give_xp 3000` across the level: "LEVEL UP! 17" banner and "+2" skill points on the HUD, "XP +3300"; Kiln's result
  "level 16 -> 17, experience 5614 -> 502", needed 8225 (the game's player_level did the level-up and its LevelUpEvent).
- Set level 19: "Level 19" with an empty bar, 6 points on the HUD; skill tree "In use 37, Available 6".
- `give_skill_points 1`: Available 7 (knowledge 7 -> 8); the World tab got a notification marker.
- Unlock 0xa66775b7 (a linked active skill, cost 2): lit in the tree at once, In use 39 / Available 5; lock: back to
  37 / 7; a second lock "not unlocked". Respec: In use 0 / Available 44, Spirit 5 -> 2 and Intelligence 8 -> 2 (the
  attribute nodes' effects went with them); the 21 nodes put back with forced unlocks: 37 / 7, Spirit 5, Int 8, the same
  ids and levels.
- Over budget (a test run set level 10 by mistake: 36 spent of 26): the tree shows "Available 0" (clamped).
- Leave and rejoin: an experience value Kiln wrote without a level-up (4900) and the knowledge point were still there:
  **the writes go into the client's character save**; the nodes came back unchanged.
- The scripted run (`progression-test.live`): 59 PASS, 0 FAIL; `PlayerLevelChangedEvent` and `PlayerSkillsChangedEvent`
  carried the request ids.
- Chat commands from the console folder (`/level <name>`, `/skillpoints <name>`, `/xp <name> 100`, `/skill <name> lock
  …`) worked; async replies come after the console's result file is written, so the commands now also answer at once.
- `IsAdmin` was 1 for the Admin group ("admin 1, perms 0x3f"): consistent with +0x9D holding the group's bits.

## 7. Open questions
1. The consumer of `KnowledgeTriggerQueue` (works: the point arrived and was saved), and what sets the in-world
   `additional_skillpoint_*` flags: a flag granted early is not granted again by the world.
2. Whether the client predicts skill unlocks (refuses locally with 0 points) — it showed every server change at once.
3. `AdminSetCharacterLevel` sets needed = XpForLevel(level + 1); natural leveling uses XpForLevel(level). Kiln follows
   natural leveling; the game's admin command is probably off by one.
4. The label the client shows for `ExperienceSource.None` and `Quest` grants (Combat shows "Enemy slain").
