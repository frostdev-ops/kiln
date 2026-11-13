# Creatures and NPCs: definition, AI, attacks, looks, spawning (server svn 1285601, client 26404966)

Sources:
- **Game data:** read offline from the client's `enshrouded.kfc` with `tools/kfc` (our MIT reader; game build
  `1285601|release_1.0`; reflection from the client exe 26404966, 12,658 types).
- **Code:** the dedicated server exe svn 1285601 (sha256 `c5c9fe50…`, RVAs below; capstone, the program-descriptor
  table, EMF's patterns re-found).
- **Runtime:** the lab `creatures` (a copied save, no player), with probe mods (now `mods/tests/creatures-test`).
  - The offline numbers can be reproduced with `tools/kfc/kfc.py decode <guid> <type>` and a loop over
    `list --type keen::ecs::TemplateResource`.
- **Background:** EMF 0.3.3 docs (MIT: `engine_parts/ai.md`, `re_companions.md`, `re_companion_orders.md`,
  `re_templates.md`, `components.txt`; builds 1269579/1273043). Nothing from kfc-parser.
- **Impact VM, sequences, buffs and player abilities:** `docs/re/abilities.md` (VM format, natives, config binding order
  §1, live semantics §7, server/client split §6), cited as **[abil]**.

Tags:
- **[data]** = an offline decode of the game's resources.
- **[dis]** = read in the server code at the address given.
- **[rt]** = seen in the running lab (quoted).
- **[emf]** = EMF's finding on an older build, not re-checked here.
- **[hyp]** = hypothesis.

Everything not tagged **[hyp]** is proven by one of the first three.

## Short answers

| Question | Answer | Tag |
|---|---|---|
| What is a creature? | An `ecs::TemplateResource`: a name and a flat list of ~110 component values (no inheritance at run time). Variants such as `_BossHealthBar`, `_hookPtE` and `_SummonStone` are whole template copies. 538 templates carry the enemy AI, 92 wildlife, 57 animal, 58 villager/NPC (12,269 templates in all). | data |
| Where do stats come from? | Attribute components (`Health`, `Level`, `DamageSusceptibility`, `DamageMod`, `Movement`, …) whose formulas live in `AttributeContainerResource`. `enemy_scaling` turns `Level` (+ player count) into max health: level 1 → 119, 10 → 417, 12 → 511 for the Vukah, live on the server without a player. | data, rt |
| AI? | Program `enemy01`: a state machine (30 states × 33 conditions, table in `.data`). Its tuning is the template's `Enemy01BehaviorState` (2,520 B: aggro, combat, roaming, flee, guard, patrol, sleep, …). **Which attack and when** comes from data, the creature's **arsenal** (201 arsenals, 619 attacks, 399 behaviour rules in one resource). Villagers: `npc_active_behavior`. Server-only. | data, dis, rt |
| Attacks? | An arsenal attack names an `ActorSequenceResource`. Its `actor::SpawnImpact` events spawn an ImpactProgram with config values (damage 17.5, hit reaction, push-back, damage types), the same machinery as player abilities. | data, abil |
| Bosses, phases, shields, adds? | Arsenal behaviours: `IsInPhaseConstrain` (293 uses), `SetPhaseAction` (124), `EnemyHealthPercentageConstrain`, `SetImmuneAction`, `SendToggleEventAction`, `SpawnedEntitiesAliveConstraint`. Phase GUIDs live in `EnemyPhases`/`DynamicEnemyPhases`. Weak spots are child entities with their own `DamageSusceptibility` that pass hits to the parent. | data |
| Looks? | Client-only template data: `ModelResource` → RenderModel (skinned, names its skeleton), `AnimationGraphResourceComponent2` (same skeleton), VFX. The server has **no** model component. Scale is a per-entity `ScaleComponent` plus the transform scale (both sides). Monsters have no tint component; humanoid NPCs have `CharacterSetup` colour presets. | data, rt, emf |
| Can a mod change things live? | Yes, on the server:<br>• template data is shared, read-only resource memory; a write through `VirtualProtect` reaches every entity of the template at once;<br>• the per-entity template slot can point **one** entity at a private copy, and `enemy01` reads the copy for that entity only;<br>• dynamic components (faction, level, scale, resistances, loot labels, speed) are per entity and writable. | rt |
| Why don't enemies act in the lab? | Ordinary spawned AI waits for gameplay readiness/streaming. The old forced-ready lab did not walk, but dev.28 loaded nonplayer path queries succeeded (§13). Direct ActorInput control has a separate blocker: steering_locomotion overwrites movement while SteeringAgent remains (§15). | dis, rt, static, lab |
| New creatures? | `createEntity` locks the template **resource** by GUID, then looks up a `TemplateInfo`; both are needed, on the server and on the client. A clone therefore needs a new resource (kiln.assets can serve new resources of any type) plus a TemplateInfo; clients need the same definition. | dis |
| Vanilla clients? | They see server-side changes (stats, level, faction, AI, attacks, loot, spawns) through the normal replicated components (NetworkHealth, NetworkLevel, NetworkEnemy, NetworkActor, transforms). New templates, model changes and anything client-only need Kiln on the client. | rt for the server half, hyp for the client half |

## 1. The creature definition (data)

### 1.1 Templates
`keen::ecs::TemplateResource` (24 B, type hash 0x39768775; 12,269 resources) **[data]**:
- `+0x0` `name: string`;
- `+0x8` `family: TemplateFamily`;
- `+0x9` `predictEntity`, `+0xa` `farCulling`, `+0xb` `omitTransformReplication`;
- `+0xc` `proxyModelId`;
- `+0x10` `components: BlobArray<BlobVariant<ecs::Component>>`.

A template is a flat component list. `tools/kfc decode <guid> keen::ecs::TemplateResource` gives it as JSON.
`Base_*` templates exist (87 with enemy AI) but are complete templates themselves; nothing inherits at run time.

| Family (by component) | Templates | Examples |
|---|---|---|
| `Enemy01BehaviorState` (enemy AI; all 538 also have `EnemyArsenalReference`, `NetworkEnemy`, `Faction`, `Level`, `Health`) | 538 (341 Melee, 106 Ranger, 91 Mage by `enemyClass`) | `Enemy_Vukah_Melee`, `Enemy_Fogger_*`, `Enemy_Boss_*`, `Player_Companion_*` (summons are enemy-AI actors with faction PlayerAlly) |
| `Wildlife` | 92 | `Animal_Wildlife_*`, fish |
| `Animal` (farm/tame) | 57 | `Animal_Baby_*`, pets |
| `ActiveNpcState` (villagers, workshop NPCs) | 58 | `NPC_Workshop_Blacksmith01`, `Townsfolk_*` |
| `Boss` | 54 | `Enemy_Boss_Fog_Dragon`, `Enemy_Boss_Vorgoth_Phase1_Humanoid`, `*_BossHealthBar` variants |
| `EnemyPhases` | 114 | bosses and elites |
| `RandomSpawner` (spawn points) | 606 | `Spawnpoints_Enemy_Scattering_Wildbeast_Wolf_Above_1-3_Enemies` |
| `TriggerSpawner` / `TriggerBoxMultiSpawner` / `RayCastSpawner` / `WhiteboxRandomSpawner` | 33 / 14 / 20 / 7 | boss arenas, waves |
| `Enemy_Weakspot_*` (by name) | 66 | `Enemy_Weakspot_Vukah_Melee_Head` |

- Factions of the 538: Fogger 180, Scavenger 88, Skeleton 70, WildBeast 63, Ancients 37, PlayerAlly 36, Vukah 24, Drak 12,
  others small. `keen::FactionType` has 16 values: Environment 0, HitAll, Fogger, Vukah, Scavenger, Wildlife, WildBeast,
  Player 7, AutomatedPlayer, HitOthers, Skeleton, PlayerAlly 11, FarmAnimal, Cyclops, Drak, Ancients 15.
- 206 distinct models, 106 animation graphs and 190 arsenals serve the 538 enemy templates. Variants share the model
  and graph (one Fogger model serves 51 templates; the Vukah graph serves 7).
- The full survey: a local `templates.jsonl` (component list per template) and
  `templates_full.jsonl` (full decodes of creature/NPC/spawner templates).

### 1.2 Example: `Enemy_Vukah_Melee` (`d5d05217-52dc-467a-9192-26b4f661d8d9`), 111 components [data]
| Area | Component → value |
|---|---|
| identity | `Faction` Vukah, `EntityTagContainer` (1 tag), `DoNotSave` |
| AI | `Enemy01BehaviorState` (combat/aggro/… setups, below), `EnemyArsenalReference` → arsenal `e30312e7-9749-4329-8bfd-469a295747b5`, `Aggro {applyEnemyValue, sightCheckSlot Head}`, `FleeSensor`, `FriendSensor` (32 m), `MovementSensor`, `PathWalker` + `PathFinder`, `SteeringAgent`, `EnemyMarkerCheck` (50 m) |
| stats | `Level [1, 105]`, `Health` (0s: derived), `Stamina [30,…]`, `Strength/Dexterity/Intelligence [100]`, `DamageMod [0, 0.75, 1, 0]`, `DamageSusceptibility` (19 floats: …Pierce 0.8, Explosion 1.2, …), `Movement` (18 speeds: walk 2.4, run 5, …), `EnemyScaling {updateInterval 5 s, playerCount healthScaling 0.48, damageScaling 0.05, levelScaling health ×1.2}`, `CombatFlags`, `ExecutionTarget`, `PacifiedState {canBePacified}` |
| loot | `RandomLoot {defaultLabels fa76a666-…}`, `RandomLootSlotCount {1..4}`, `RandomLootContainer`, `ExperienceSource {xpMod 1.0}` |
| look (client) | `ModelResource` → RenderModel `7d9ae3f5-…` (`char_enemy_vukah`, 3 PBR materials, 5 LODs, skeleton `aa1770ef-…`, 86/97 joints), `AnimationGraphResourceComponent2` → `6d44ec65-…` (same skeleton), `ScaleComponent 1.0`, `Fade`, `HitReactionBehavior` (per-reaction sequences), `UiOffsets` (health bar 2.3 m) |
| children | `SubEntities` (2 entries: a VFX link on HandL and the head weak spot) + `HookShotAnchorSubEntity` |

### 1.3 Template vs per-entity data, server vs client
The engine splits components:
- **template** components: one shared copy per template;
- **dynamic** components: per entity, in archetype chunks;
- flags `server_only` / `client_only` / `is_dynamic` / `do_not_save` / `savegame_relevant` (EMF `components.txt`, from the
  game's registration).

**On the dedicated server, for a spawned Vukah [rt]** (`World.ComponentSize` > 0 = dynamic, `GetTemplateComponent` ≠ 0 = template):
```
per-entity (dynamic) components: Faction=1, Level=16, NetworkLevel=2, Health=40, NetworkHealth=8, ScaleComponent=4,
  DynamicEnemy01BehaviorState=1256, NetworkEnemy=8, DynamicAggro=2848, DynamicEnemyScaling=40, DamageSusceptibility=84,
  DamageMod=24, Movement=80, CombatFlags=12, PacifiedState=16, SelectedTargets=2184, RandomLoot=36, RandomLootSlotCount=2,
  DynamicSubEntities=64, DynamicEnemyAiming=40, DynamicHitReactionBehavior=356, Actor=3832, CurrentTransform=56,
  ExperienceSource=8, Strength=20, Stamina=40, DynamicPathWalker=456, DoNotSave=1, ClientNoBuildZone=8, UiOffsets=80,
  DynamicLocomotion=456, ExecutionTarget=16, DynamicFleeSensor=64, FriendSensor=36
template-only components: EnemyArsenalReference, Enemy01BehaviorState, Aggro, EnemyScaling, SubEntities, EnemyAiming,
  HitReactionBehavior, EntityTagContainer, AnimationGraphResourceComponent2, ColliderResourceComponent, PathWalker, Fade,
  Locomotion, MovementColliderConfig, FleeSensor
absent: ModelResource, ModelDynamic, OverrideLevel, EnemySettings, EnemySensor
```

What this means:
- **ModelResource is in the template resource but not in the server world** (EMF flags it `client_only`): the server
  knows nothing about a creature's model.
- Faction, level, scale, resistances, damage mod, speeds, loot labels and XP factor are **per entity** and can be changed
  for one creature without touching its template.

EMF flags for the relevant components:

| Flag | Components |
|---|---|
| `server_only` | EnemyArsenalReference, EnemyScaling, EnemyPhases, EnemyAiming, Locomotion, RandomLoot, CombatFlags, SelectedTargets, SubEntities, OverrideLevel, EnemySettings, Equipment, ExperienceSource, BossHealthBar, RandomSpawner |
| `client_only` | ModelResource, ModelDynamic, VisualEquipment, VfxComponentResource, RenderEquipment, HighlightColor |
| both sides, dynamic | Faction, ScaleComponent, CurrentTransform, NetworkEnemy, NetworkLevel, NetworkHealth, PacifiedState, CharacterSetup, Inventory |

### 1.4 Template data at run time
- **Same data on the server as in the client's .kfc [rt].** `GetTemplateComponent(world, 0x52487b01, entity)` (server
  `0x64fae0`) returned data whose fields equal the offline decode:
  ```
  template values: combatSetup.preferedDistance 5 (kfc 5), aggroSetup.viewRange 18 (18), viewConeHalfAngle 88 (88), aggroRange 10 (10),
  alarmedAggroThreshold 500 (500), guardSetup.startDistance 10 (10), targetDistance 3 (3), canGuardPlayer 1 (1), homeZone.radius 10 (10),
  enabledFeatures 0x613953f, enemyClass 0 (0 Melee), threatFactor 1 (1), cooldownGroups[0].cooldown 4s (4)
  EnemyArsenalReference (template) = e30312e7-9749-4329-8bfd-469a295747b5 (kfc e30312e7-9749-4329-8bfd-469a295747b5)
  EnemyScaling (template): updateInterval 5s playerCount healthScaling 0.48 (0.48) levelScaling.health 1.2 (1.2)
  ```
  So the reflection layouts of the client exe fit the server's memory (`Enemy01BehaviorState` is 2,520 B; EMF's 2,504 B
  on 1273043 is outdated).
- **Read-only resource memory, shared [rt]:**
  ```
  template memory: protect 0x2, type 0x20000, region 0x61F63000 + 407.9 MB, allocation 0x5CD50000
  Vukah B = 2543: Enemy01BehaviorState template at 0x61F633B8 (same as A: True)
  template write (aggroSetup.viewRange 18 -> 42.5 through VirtualProtect): A sees 42.5, B sees 42.5
  a Vukah spawned after the write (2544) sees 42.5
  restored viewRange 18; template memory protect after the write: 0x2
  ```
  This is the same kind of allocation as the item/recipe data kiln.content writes (docs/re/content.md §3).
- **Per-entity template slot [dis] [rt].** `GetTemplateComponent` `0x64fae0` → `0x64fa00`:
  - type index t from the template map (`registry+0x30`, `0x64dc40`);
  - entity record from the entity map (`storage+0x148`, `0x650c50`);
  - pointer = `*(record+0x20 chunk + archetype(record+0x18)[0x584 + 2t] + record+0x30 index × 8)`.
  That 8-byte slot is per entity. Pointing it at a private copy changes the template data for that entity only:
  ```
  A 2542: template ptr 0x61F633B8; slot 0xB66BE4D0 holds 0x61F633B8 (match True)
  A now reads template 0x7D267060 (copy 0x7D267060) viewRange 99; B reads 0x61F633B8 viewRange 18
  10 s after the override: A template 0x7D267060 (copy True) ... enemy01 behaviour update for A saw the copy 250x, the original 0x
  after 6 spawns, 2 destroys and a teleport: A template 0x7D267060 (copy True), slot now 0xB66CE4E0 ...
  ```
  The game moved A to another chunk (the teleport's archetype change) and carried the pointer along. The program reads it
  from its iterator: the behaviour context's `+0x50` was the copy. In each step, `+0x48` held A's dynamic state, so the
  test matched A by that pointer.
- Nothing of this is saved: templates are reloaded from the game files at every start (EMF; the save keeps template
  GUID + transform + a few components, docs/re/spawn.md).

### 1.5 Stats and scaling
- **Attributes [data].** `AttributeContainerResource` `8f1fd455-…` defines 40+ attribute structures with debug names and
  formulas (`AttributeStructure.calculation: BlobArray<AttributeCommand>`). Examples:
  - `Health {Health, Health_Min, Health_Max_Base, Health_Max_Adder, …}`
  - `Level {Level, Level_Max}`
  - `DamageSusceptibility` (19 slots: Physical, Cut, Pierce, Blunt, Wood, Stone, Metal, Poison, Explosion, SkillShot,
    Magical, …)
  - `Movement` (18: Walk, Run, Sprint, Combat, Patrol, Flee, PushbackMod, RotationMod, …)
  - `DamageMod`, `Armor`, `CritChance`, `DamageModAgainst_Faction_*`, `Threat_Pool*`, `AllyAggroMultiplier`.

  A creature's attribute component holds the slot values (`dataStorage`); writes go through attrAdd/attrRecalc
  (docs/re/combat.md).
- **Level → health [rt].** Program `enemy_scaling` (#37, `0x12fa80`; reads EnemyScaling, Level, DynamicAggro; writes
  DynamicEnemyScaling, Health, Stamina, DamageMod; sys-read BalancingRegistry, ServerPlayers) runs on the server with no
  player:
  ```
  wrote A: Faction 3 -> 11, Level 1 -> 10, DynamicEnemyScaling.nextUpdateTime -> 0; health now 119/119
  A 8 s after the writes: ... Level 10/105 NetworkLevel 10 | ... lastLevel 10 ... | faction 11 | ... health 417/417
  D (OverrideLevel 10 via kiln.spawn) ... 6 s later: ... Level 10/105 NetworkLevel 10 ... health 417/417
  2561 (Level dataStorage 12 at spawn): ... Level 12/105 NetworkLevel 12 ... health 511/511
  ```
  The same 119 → 417 as EMF's in-game companion test. Health formulas (`htkStartPve/htkEndPve`, `BalancingTable`) are
  not decoded. Player-count scaling is `EnemyScaling.playerCountScaling` (health +48 %, damage +5 % per player for the
  Vukah).
- Per-attack damage is not a creature stat: it is the attack's SpawnImpact config (17.5 for the Vukah swing, §3.2),
  scaled by the creature's `DamageMod`/level in `applyDamage` (docs/re/combat.md, abilities-research).

## 2. Behaviour (AI)

### 2.1 Programs and functions (server 1285601)
The program table was parsed from the 0x188-byte descriptors that `RegisterAllPrograms` registers (706 of 757 getters
matched by the scan) **[dis]**. The function addresses come from EMF's patterns (1269579), each unique on 1285601 **[dis]**.

| What | RVA | Notes |
|---|---|---|
| `enemy01` (#226) update | `0x12db50` | iterator 0x648; per-entity `0x12ce30` → behaviour update `0x135470` (ctx `+0x48` DynamicEnemy01BehaviorState*, `+0x50` the template Enemy01BehaviorState*). Access: tpl Enemy01BehaviorState, EnemyArsenalReference, PathWalker; sys-read g38_enemy::EnemyArsenalRegistry, g38_enemy::EnemyRegistry, AttributeRegistry, Daytime, PathGridSystem, VoxelGrid, … |
| HandleCondition | `0x12c4b0` | `next = table[state*0x21 + cond]` with the table at `0x14C5B70` (`lea rbx,[rip+0x13996a6]` at `0x12c4c3`); feature gates `[dyn+0x4bc]`/`[dyn+0x4c0]`/`[tpl+0x99c]` (EnemyFeature bits, e.g. 0x400 ReturnHome, 0x800 FleeHome) |
| SetState | `0x12af10` | `dl` = new state (seen: 4 Spawning, 2 Idle, 15 Roaming) |
| StateDispatch | `0x13a7f0` | per-state enter/execute/exit |
| `enemy_combat_targets` (#109) | `0x13e580` | aggro slots → `SelectedTargets[0]` (EMF ai.md §5; descriptor and pattern agree) |
| `enemy_sensor` #432 `0xf4040`, `friend_sensor` #431, `flee_sensor` #433, `danger_sensor` #434 | | perception |
| `enemy_threat_events` #221 `0x1310b0`, `call_factions_for_help` #85, `threat_timestep` #151 | | threat/help |
| `enemy_launch_projectiles` #223 `0x12edf0`, `enemy_attack_attachment` #81 | | ranged |
| `network_enemy` #228 `0x1316a0` | | DynamicEnemy01BehaviorState → `NetworkEnemy {state, awarenessState, awareness}` |
| `actor_spawn_entity` #259 `0x454f0` | | runs SpawnImpact/SpawnEntity events of sequences **[abil]** |
| `actor_is_ready_for_gameplay` #29 `0x7d1a0` | | sets/clears Actor `+0xBF8` bit 50 (`shr rcx,0x32; and cl,1`, `xor [rax+0xbf8]`); PlayerInput `+0x5b8` for players; streaming checks `0x176670`/`0x1765d0` for others |
| villager `npc_active_behavior` #234 | `0x1f86b0` | worker `0x1f7850`, SetState `0x1f6da0`, FinishState `0x1f7750`, RunStateMachine `0x1fe460`, StateDispatch `0x1ef600`, DecideTask `0x1fb8c0`, SetDecision `0x1f65b0`, NpcCommandEvent handler `0x1f3320` (12-byte pattern, unique but short) |
| `wildlife` #232 `0x21f880`, `animal_behavior` #237 `0x4c500`, `guard_interactions` #405 `0xc2970`, `path_walking` #245, `pathfinding` #246 | | |
| `random_spawner` #654 `0x103bb0`, `trigger_spawner` #652, `spawn_sub_entities` #532, `conditional_despawn` #658, `limited_entity_spawn` #538, `unique_npc_init` #288, `boss_display` #464 | | spawning (§5) |

### 2.2 The enemy state machine
- `enemy01::State` (30): Invalid 0, Start, Idle 2, Dead 3, Spawning 4, Investigate, Combat 6, ReturnHome, FleeHome,
  WaitAtHome, ReactToNoise 10, Patrol, TurnAfterPatrol, WaitAfterPatrol, ControlledByAttack 14, Roaming 15, Flee 16,
  WaitAfterFlee, Falling, Hit 19, Guard 20, Interact, Landing, BlockProjectile, MoveToMarker 24, Sleep, Circling, AvoidWater,
  Revive, Stunned 29.
- `StateCondition` (33): Death 0, TargetSelected, TargetLost, IsFinished 3, TargetUnreachable, TargetOutsideLeash,
  NoiseHeard, ProjectileHeard, HasReachedNo/Low/HighAwarenessState 8-10, IsReadyToPatrol, IsReadyToInteract, …,
  WantsToFlee 17, HasBeenHit, …, NeedsToGuard 23, …, IsExhausted 30, IsHelpingEnemy, NeedsToInvestigate 32.
- The transition table lives in `.data` and is filled at first use **[rt]**:
  ```
  Idle(2)   = 3,6,0,0,0,0,10,0,0,0,0,11,21,0,14,15,26,16,0,5,5,18,19,20,22,0,24,25,27,0,29,5,5
  Combat(6) = 3,0,7,7,0,8,0,23,0,0,0,0,0,0,14,0,0,16,0,0,0,18,19,0,22,0,0,0,27,0,29,0,0
  Spawning(4) = 3,0,0,2,0,…
  ```
  E.g. Idle + TargetSelected → Combat; Combat + TargetLost/IsFinished → ReturnHome; Combat + TargetOutsideLeash →
  FleeHome; any + IsExhausted → Stunned. The table is global for all enemy types: per-creature behaviour comes from the
  template's feature mask and setups, not from the table.
- **Feature switches:** `Enemy01BehaviorState.enabledFeatures` (template, `+0x99c`) and `DynamicEnemy01BehaviorState`
  `enabledFeatures`/`disabledFeatures` (`+0x4bc`/`+0x4c0`, per entity) gate states.
  - `EnemyFeature` (27 values): UseTaunt, UseThreatSystem, UseBlocking, Move, Prowl, Rotate, RotateWhileStanding,
    Patrol, Roaming, ShowSpawnSequence, ReturnHome, FleeHome, Combat, Interact, TriggerAlarm, CallForHelp, Run, IdleTurn,
    BlockProjectiles, SearchForCover, MoveToMarker, Sleep, RunAround, Circling, StunnedState, Awareness, Investigate.
  - Arsenal actions `Enable/DisableFeatureAction` toggle them at run time.
  - The Vukah's mask: UseTaunt, UseThreatSystem, UseBlocking, Move, Prowl, Rotate, Roaming, ReturnHome, Combat,
    CallForHelp, Run, IdleTurn, MoveToMarker, Awareness, Investigate.
- **Tuning (template `Enemy01BehaviorState`, 2,520 B; reflection offsets) [data].**

  | Setup | Offset | Holds |
  |---|---|---|
  | `combatSetup` | `+0x0` | prefered distances, max target distances, `cooldownGroups[8] {id, cooldown}`, walk/run distances, attack durations, counter-attack |
  | `homeZoneSetup` | `+0x188` | radius |
  | `aggroSetup` | `+0x18c` | viewRange 18, nightViewRangeFactor, viewConeHalfAngle 88, aggroFromAttack 200, aggroInRange, aggroLossOutOfSight, alarmedAggroThreshold 500, aggroRange 10, pools, preferCloserTargets, … |
  | other setups | | combatCommitment, targetSelection (232 B), callForHelp, roaming (radii 10-18 m), flee (12-20 m, 10 s), blocking, dodging, parry, guard (`+0x598`), patrol, taunt, sleep (day/night chance), turn, circling, awareness, investigate, notifier, stunned, recoverHealth |
  | scalars | | `enemyClass` `+0x998`, `threatFactor` `+0x9a0`, `pushBackScale` `+0x9b4`, `disabledHitReactions` `+0x9b8` |
- Per entity: `DynamicEnemy01BehaviorState` (1,256 B): `state +0`, `homeZonePosition +0x10`, `movementTarget +0x20`,
  `combatData +0x40`, `awarenessData +0x238`, `guardEntityId +0x454`, `enabled +0x498`, feature overrides
  `+0x4bc/+0x4c0`, …; `DynamicAggro` (2,848 B, 6 target slots); `SelectedTargets` (2,184 B).

### 2.3 Why spawned enemies stand still on the dedicated server [rt]
```
10 s without probe entities: enemy01 update calls 0, enemy01 per-entity 0, behavior update 0, ... npc worker 1500
10 s with 3 probe Vukahs: enemy01 update calls 250, enemy01 per-entity 750, behavior update 750 ... SetState 3, enemy_scaling 250
  conditions: 3:3 8:3 | SetState dl: 4:3
A after 10 s: state 4 enabled 1 ... NetworkEnemy state 4 ... actor bits 0x100000000
```
- The AI program runs every step for spawned enemies even with no player in the world.
- The copied world itself had **0 enemies** (`world has 0 NetworkEnemy entities, 8 NpcCommon entities`): natural
  enemies are only spawned near players.
- A spawned enemy enters `Spawning` (4) and stays there, because bit 50 (IsReadyForGameplay) of `Actor+0xBF8` is never set.
- With bit 50 forced every step (probe 2), the forced Vukah left Spawning by itself:
  ```
  t=2s A(forced) 2574: ... state 15 ... actor 0x4000100000000 ...      t=2s B 2575: ... state 4 ... actor 0x100000000
  A behavior updates 735, HandleCondition 874: 3:4 8:689 15:181; SetState (all enemies) 10: 2:4 4:2 15:4
  ```
  It alternated Roaming(15) ↔ Idle(2) on its own (IsReadyForRoaming 181×), but `movementTarget valid 0` and it did not
  move in that dormant-AI lab. Corrected 2026-11 (dev.28 RE): this is **not** a universal absence of terrain/path
  data without players. Explicit loading and a mod-owned query later returned paths with a nonplayer owner and no
  Player entity or client; see §13. The original readiness/dormancy observation remains valid. [lab]
- Villagers are paused the same way: `state 1 ... closestPlayerDistance 1.8446743E+19` (the worker pauses at ≥ 150 m,
  EMF).
- Consequences:
  - ordinary AI movement/combat and presentation still need a joined-player acceptance check; this does not
    prevent player-free, explicitly loaded geometry/path queries (§13);
  - Kiln can still test decision logic, stats, loot rolls and spawns player-free;
  - forcing readiness is a lab tool only, never for production.

### 2.4 The arsenal: data-driven attack and behaviour selection [data] [rt]
- **Resource [data].** `keen::enemy::EnemyArsenalRegistryResource` `cd861d95-a79c-4c11-8b48-8fae34a4156c` (216,904 B;
  `DevEnemyArsenalRegistryResource`, same GUID, 220,360 B):
  - `arsenals: BlobArray<EnemyArsenalResource {guid, attacks: BlobArray<AttackDesscriptionResource 156 B>, behaviors: BlobArray<BehaviorDesscriptionResource 88 B>}>`;
  - 201 arsenals, 619 attacks, 399 behaviours, 385 distinct attack sequences.
- **Runtime [rt].** `g38_enemy::EnemyArsenalRegistry` (singleton, FNV `0x23fe551f`) `+0x78` → the resource data in
  read-only memory:
  ```
  +0x78 -> 0x717AB6E0 (protect 0x2, region 159.6 MB) first u32 8 next u32 201 | u32s 8 c9 bc353fa3 43c4a6ad ...
  ```
  That is `BlobArray {offset 8, count 201}`, then arsenal 0 `bc353fa3-a6ad-43c4-…`, exactly the offline decode. Editing
  attack numbers in place needs the same VirtualProtect write path as templates and items.
- **Attack** = `AttackDesscriptionResource`:
  - `guid`, `description` (`AttackDesscription` 96 B): `actionSequence` (an `actor::ActorSequenceResource`),
    `attackStyle`, `selectionChance`, `flags`, `threat`, `minRange`/`maxRange`/`maxVerticalRange`, `cooldownGroupID`,
    `speed`, `acceleration`, `executionAttackSequence`, `combatTargetChangeMode`, …;
  - `selectionConstrain` / `executionConstrain` / `abortConstraints` (BlobArray<BlobVariant<AbilityConstrain>>);
  - `commands`; `data` (TeleportAttackData 19 / DashAttackData 17 / JumpAttackData 14).

  Example from arsenal 0: attack `6f6144ed…` → sequence `a837f190…`, threat 9, constraints ClearPath + Distance 0-8 m.
  Cooldowns come from the template's `combatSetup.cooldownGroups` (Vukah group `b666434a…`: 4 s).
- **Behaviour rule** = `BehaviorDesscriptionResource`:
  - `mode` (OnUpdate 281, OnEnterState 53, OnEndAttack 29, OnEnter 19, OnExitState 9, OnExit 8);
  - `settings {checkIntervalMin/Max, cooldownMin/Max, chance}`;
  - `executionConstrain`, `abortConstraints`, `actions: BlobArray<BlobVariant<Action>>`.
  - Example: in Combat within 2 m, 60 % chance, within 90° of the target and hit in the last 1 s →
    `SetCombatStateAction Block 1-1.5 s`.
- **Constraints used:**
  - Distance 489, IsInPhase 293, AngleToTarget 252, ClearPath 163, State 153, ActorStates 140, IsVariableSet 92,
    CombatState 80, StateDuration 57, IsPlayerTarget 45, IsAttackSelected 43, IsGroupMember 39, TargetActorStates 33,
    EnemyHealthPercentage 27, IsGuardingPlayer 26, RandomChance 17, …;
  - SpawnedEntitiesAlive 10, Cooldown 8, HasBeenHitInCurrentPhase 7, FindBestFriend 7, IsLastPhase 3, NeedsToFlee 3.
- **Actions used:**
  - SetPhase 124, SendToggleEvent 85, DisableFeature 67, SetCombatState 54, EnableFeature 42, SetMovementSpeed 38,
    StartFlying 31, PlayActionSequence 30, ClearVariable 21, SetWalkAnimation 21, SelectAttack 21, GuardGroupLeader 20,
    SetIdleAnimation 19, SelectFriend 15, TriggerStateCondition 14, MoveToTarget 14;
  - SetImmune 6, RecoverHealth 1, RunAway, SetFleeRadius, ….
- So "act" for a creature splits into three parts:
  - the global FSM (code + table);
  - per-template tuning (`Enemy01BehaviorState`);
  - per-arsenal rules and attacks (resource data).

  The last two are plain data.

### 2.5 Perception, aggro, targets (EMF ai.md §5, current addresses above) [emf] [dis]
- `enemy_combat_targets` gathers candidates and processes HitEvent / EnemyAttackStarted / EnemyIsHelping.
- It decays `AggroTarget.aggro` and updates the 6 `DynamicAggro` slots (Unknown/Aborted/Noticed/Alarmed).
- The winner goes to `SelectedTargets.targets[0]`; target switches are rate-limited (2-2.5 s).
- Ranges and gains come from `aggroSetup`; targeting rules from `targetSelectionSetup` and the singleton resource
  `keen::ecs::TargetSelectionSettings` (1 resource).
- Faction friend/enemy rules are hard-coded leaf functions (EMF: `isEnemyFaction`, "player side" = Player /
  AutomatedPlayer / PlayerAlly).
- Engine-supported commands (proven in game by EMF's companion mod, events from a mod program):
  - `AddThreatTargetEvent {attacker, target, threat, pool}` = "attack that";
  - `StartGuardingEvent` / `StopGuardingEvent` = follow / stay;
  - `RestoreEnemyHealthEvent`, `SetEnemyVariableEvent`, `RegisterControlledCombatTargetEvent` (not tested).
  - Villagers: `NpcCommandEvent {type Reset|GoToPosition, npcId, targetTransform}`.

### 2.6 Villagers, animals, companions
- **Villagers** (`npc_active_behavior`): the enter/update/exit FSM EMF documented (17 states, decision weights,
  target search). Template `ActiveNpcState` (456 B): `decisionSetup` weights, idle/roam/sleep/search setups, sequences.
- **Companions** = enemy-AI actors guarding the player:
  - `guardSetup.canGuardPlayer` is set on 79 templates **[data]**;
  - 43 enemy-AI templates have faction Player/PlayerAlly (necromancy skeletons, wisps, totems, `Enemy_Companion_*`)
    **[data]**;
  - EMF made a spawned Vukah a working companion by writing `Faction = 11` and sending `StartGuardingEvent` **[emf]**.
    The Faction write works here too: `faction 3 -> 11` read back, also set at spawn time **[rt]**.
- **Animals** (`animal_behavior`, `wildlife`) use other components (`Animal`, `Wildlife`, `NpcCommon`), not enemy01.

## 3. Abilities and mechanics

### 3.1 Shared machinery (owned by abilities-research)
- The chain: arsenal attack → `ActorSequenceResource` (sub-sequences of timed events: `SetAnimationEvent`,
  `Enable/DisableAbilityEvent`, `VfxNotifierEvent`, `SfxNotifierEvent`, `TriggerNoiseEvent`, `CameraShakeEvent`,
  `actor::SpawnImpact`, `SpawnEntityEvent`) **[data]**.
- `actor_spawn_entity` (#259) runs SpawnImpact on the server. It spawns an impact entity with `ecs::Impact {program}`, a
  collider and `SequenceEventImpactConfig` **[abil]**. `impact_initialize` (`0x197250`) binds the event's `impactValues`
  by configId, the same path for players and enemies **[abil]** (docs/re/abilities.md §1). A changed value applies to the
next attack instance. In its lab, impact programs of spawned projectile templates resolve and run with no player, but
their collision streams see no events and deal no damage: the same no-player limit as AI movement (§2.3).

### 3.2 Example: the Vukah swing [data]
- Sequence `a837f190-…` has 3 SpawnImpacts.
- First one at 0.97 s, 0.1 s long, HandL, box collider, `copyCombatAttributes`/`copyFaction`, impact program
  `0c3a7176-9336-47a3-ae98-d9433ac5ffbb`.
- `damageDistribution {cut 0.2, blunt 0.2, fog 0.1}`.
- `impactValues.simple`: `FloatImpactConfig` configId `0xa1946b32` = **17.5**, `HitReactionTypeConfig Light`,
  `PushBackForceConfig Big`, `BoolImpactConfig true`, `MaterialIdImpactConfig`, `AttackCategoryMaskConfig [Melee]`.

### 3.3 Other mechanics
- **Projectiles [data]:** `EnemyAiming {hitChance 0.4, minDistanceToMissTarget 36}` (268 templates);
  `enemy_launch_projectiles`; projectile templates (`EnemyProjectile`, `BaseEnemyProjectile`, `Projectile_Enemy_*`).
- **Bosses and phases [data]:**
  - `EnemyPhases {phases: ObjectReference<EnemyPhase>[12]}` (114 templates), `DynamicEnemyPhases {lastPhase,
    currentPhase, currentPhaseStartTime}`;
  - behaviours `SetPhaseAction` / `IsInPhaseConstrain` / `EnemyHealthPercentageConstrain` / `IsLastPhaseConstraint`
    (phase changes at health %);
  - `LinkToggleStateToEnemyPhase` children (phase VFX, arena parts);
  - `Boss` (54), `BossHealthBar {displayName LocaTag, maxPlayerDistance}` (28), `BossMusicTag` (51).
  - Vorgoth is two templates (`Enemy_Boss_Vorgoth_Phase1_Humanoid` → `_Phase2_Monstrosity`) plus organ "respawner"
    TriggerSpawners per wave (§5).
- **Shields, immunity, enrage:**
  - `SetImmuneAction` (6) and `CombatFlags.immune` (per entity; kiln.combat notes applyHitToEntity does not read
    `immune`, so the impact side must);
  - `SetMovementSpeedAction`, `EnableFeatureAction`;
  - `RecoverHealthAction` + `RecoverHealthSetup`;
  - "enrage" is a phase whose actions change speed and attacks.
- **Summons and adds:**
  - `SendToggleEventAction` (85) toggles child spawners (`TriggerBoxMultiSpawner` "SpawnInBox" templates of the lizard
    bosses, 14);
  - `SpawnedEntitiesAliveConstraint` (10); `limited_entity_spawn` (MaxSkeletonSpellCount, MaxTotemCount);
  - SpawnEntity events in sequences; `IncreaseParentEnemySpawnCount`.
- **Weak spots [data]:**
  - 66 `Enemy_Weakspot_*` templates spawned as `SubEntities` children;
  - each has its own collider, `CombatFlags {propagateHits true, handleHits false}`, `CopyParentDamageSusceptibility`
    and its own `DamageSusceptibility` (the Vukah head: SkillShot ×2.0).
  - Hits travel to the parent through the ImpactHierarchy (docs/re/combat.md: the conclusion rewrites the target per
    hierarchy parent).
- **Debuffs on players:** buffs applied by impact programs (`applyBuff` native, BuffType; abilities-research).

## 4. Appearance

| Part | Where | Side | Change takes |
|---|---|---|---|
| Mesh | template `ModelResource.model` → `keen::RenderModel` (10,470 resources) | client only (absent on the server **[rt]**) | client-side template edit, or another template |
| Skeleton | `RenderModel.hierarchy` → `ModelHierarchyResource` (257) | client | must match the animation graph |
| Animations | `AnimationGraphResourceComponent2.animationGraph` → `AnimationGraphResource2_0` (279; same `hierarchy`, `slotBoneIndexMapping[36]`), sequences reference graph states | template, both sides (no flags in EMF) | graph + sequences move with the skeleton |
| Materials / colours | `RenderModel.materials[]` → `RenderMaterialResource` (3,458; PBR parameters with up to 3 albedo tint layers: `PbrMaterialLayer.albedoTint`) | client | asset-layer resource override of the material (or a texture retexture) |
| Humanoid colours | `CharacterSetup {gender, presetId, hair, beard, hair/beard/skin/eyebrow/eye ColorPresetId}` (villagers; dynamic, saved, both sides) and `RenderEquipment.colors[24] ItemRenderColors` (client) | both / client | data only (preset ids) |
| Scale | `ScaleComponent.uniformScale` (dynamic, both sides; 68 enemy templates carry it, 20 with a value ≠ 1, e.g. `Enemy_Wildbeast_Bat_Frost_Hover` 1.5, `Enemy_Plant_Spitter_Fog_Stare` 0.4) and `CurrentTransform.scale` | both | per entity at spawn **[rt]**: `2559: ... scale <2, 2, 2> ... ScaleComponent 2` |
| Gear / attachments | `SubEntities[]` children (templates attached to slots: `slot HandL`, `attach`, `copyFaction`, `despawnOnDeath`), `SlotAttachment`; NPC gear from `Inventory` + `RenderEquipment`/`EquipmentCheck` | server spawns the children; their models are client data | per-template child list |
| VFX / sound | `VfxComponentResource` (client only), sequence `VfxNotifierEvent`/`SfxNotifierEvent`, `HitReactionBehavior` sequences | client | client data |
| Highlight | `HighlightColor {colorIndex}` (client only, palette) | client | client |

- **There is no tint component on monsters [data].** The only `tint` fields are in materials, item tinting,
  `EntitySpawnData.tintColor` (spawn-definition data, probably props) and `RenderInstanceData.tintColor` (renderer).
- **"Swap to another existing look" is a template swap in practice [data].** Model, skeleton, animation graph,
  colliders, sub-entities, hit-reaction sequences and the arsenal's attack sequences all assume the same skeleton. The
  Vukah graph `6d44ec65` serves exactly the 7 templates that use its model. Giving a Vukah a wolf's look means using the
  wolf's model **and** graph **and** attack sequences, i.e. the wolf template with the Vukah's numbers. A different look
  on the same skeleton (a variant mesh or material) is the cheap case.
- **`ModelDynamic` (client-only per-entity model override; used by pickups) [rt].** Passing it as a spawn component
  created the entity, but the server world has no such component on it:
  ```
  SpawnWrapper G (ModelDynamic newModel = Fogger model): id 2560
    2560: exists True ... ModelDynamic none
  ```
  Whether the server forwards client-only spawn components to clients is **[hyp]**. The string `Spawn Component %s is
  neither in server nor client components` hints that spawn components may be either. Client checklist.

## 5. Spawning, despawning, persistence
- **Natural spawns [data] [dis]:** scene chunks (`SceneEntityChunkResource`, 1,609: template refs + spawn data) place
  spawner entities. `random_spawner` (#654, `0x103bb0`, "with PlayerInRange") spawns through `QueueSpawn`
  (docs/re/gameplay.md §6; Kiln's `NaturalSpawnEvent` is cancelable there).
- `RandomSpawner` (168 B, template, server only) [data]:
  - `spawnEntries[] {entityTemplates[], spawnGroupId, spawnConditions IsDay|IsNight, weight}`, `spawnGroups[]`;
  - min/max range, timeouts, amount, delay, `maxTotalSpawnCount`, `maxEntitiesAlive`;
  - `spawnMode` (Random 385 / Grouped 221), `spawnTarget`, `spawnAsUnitGroup`, `despawnEntities`;
  - `conditionalDespawnMinRange`, `defaultDespawnMinRange` (wolf spawner: 50 / 180 m);
  - `useUp/DownScaling`, `minSpawnerAmount` (the world setting "Few/Many/Extreme" through
    `BalancingTable.randomSpawnerBalancing`), `useShuffleBag`;
  - `enemySignalSettings` (features on signal);
  - most referenced: `Enemy_Fog_Bug_Critter_hook` (26 spawners), `Enemy_Fogger_Soldier01` (11).
- **Other spawners:** `TriggerSpawner {entityTemplate, logicSetup, spawnTimeout, allowSpawningInsidePlayerBase}`,
  `TriggerBoxMultiSpawner`, `RayCastSpawner`, `WhiteboxRandomSpawner`, `FakePlayerSpawner` (AutomatedPlayer).
- **Level at spawn:** `OverrideLevel {newLevel}` (server only; kiln.spawn's `Level` option) and `EnemySettings`
  (spawner-side: idle animation, feature masks, marker tag, spawn sequence, leash length).
- **Spawn components override template defaults and may add components [rt].** Faction 11 at spawn: `2558: ... faction
  11`; Level 12 at spawn: health 511. kiln.spawn already adds DoNotSave the same way (docs/re/spawn.md).
- **Despawn:** `ConditionalDespawn`, spawner `despawnEntities`, `despawn_link`, LifeTime. EMF/world.md: enemies teleported
  far from players are despawned. Probe Vukahs 60 m from the base lived for minutes with no player (not culled).
- **Persistence:** 839 of 1,007 creature-like templates carry DoNotSave (docs/re/spawn.md). Villagers have `ForceSave`
  and are unique per template (`unique_npc_init`/`unique_npc_spawns`, EMF).

## 6. Loot
- Per creature (both per entity, server only):
  - `RandomLoot {disableRandomLoot, defaultLabels → DefaultLootLabelCollection, forwardSpecialItemLabels}`;
  - `RandomLootSlotCount {slotsMin, slotsMax}` (Vukah 1..4).
- `DefaultLootLabelCollectionResource` (99 `DefaultLootSettingsResource` entries: level/rarity modifiers, shufflebag
  weights, loot tags with weights; id = LootContainerId) **[data]**. The roll uses `LootableItemsResource` (kiln.content
  `loot`) and the world's loot factor.
- Fixed drops: `LootContainer_Enemy_*` templates with `DefaultInventoryResource` tables (kiln.content `drops`).
- kiln.combat's `EntityDropsEvent` sees the container (docs/re/combat.md).
- Not exercised in the lab: a killed creature gets no death sequence and no loot without a player.
  ```
  kill A: hit 2542: 417 -> 0 (killing blow) after 0 step(s)
  A dead: state 4 ... actor bits 0x100000080 ... health 0/417 dead
  ```

## 7. Friendly NPCs, vendors, dialogue
Server Steam 26430253 (`f0d824be…`, RVAs) and client 26430625
(`5d9ef2f1…`, [RVAs]), svn 1287369; data from the client's KFC (1287369). Quests, the journal and the knowledge system
behind every unlock are in `docs/re/quests.md` (cited as **Q§n**).

### 7.1 Roster and identity **[refl] [data] [dis]**
- `NpcCollection` (one resource `377f61dc-…`): 35 `NpcResource` (136 B): `id: NpcId` +0, `name` +4 (LocaTagId),
  `isCraftingAssistant` +8, `npcRoamingRange` +0xc, `unlockRequirement: KnowledgeQueryData` +0x10, `workshopId` +0x1c,
  `workshopLabel` +0x20, `itemId` +0x24 / `itemRef` +0x28 (the **summon tool**, §7.6), `characterPresetReference` +0x38,
  `characterPresetId` +0x48, `uiRendering` +0x4c (model, animation, graph for the NPC directory portrait), `debugName`
  +0x80. 18 crafters/assistants/traders (`NPC_Workshop_*`), 10 villagers (`Townsfolk_*`), 6 `test_*`, 1 deprecated.
- **`NpcId` = FNV-1a-32 of the NPC's template GUID**: Alchemist `7456862b-…` → `0xf1d74b27`, Blacksmith → `0x47a2094a`,
  Robert Gibson → `0xabed4b43` (= their `NpcResource.id` and `ActiveNpcState.npcId`) **[data]**; `unique_npc_init`
  and `unique_npc_spawns` compute exactly this hash of the entity's template GUID **[dis]**.
- `NpcRegistry` (server singleton, from the collection) answers id → resource and the unlock state;
  `npc_unlock_state_server` (#285, `0x1fa330`, once per step) recomputes the unlock states from knowledge
  (`0xa2ef90`) and writes them into the registry when they change (`0x96c6a0`); the client has the same program (#286)
  **[dis]**. Unlocking an NPC = setting its `unlockRequirement` knowledge (the Alchemist's `6da00be7` comes from a
  `SceneKnowledgeTrigger` at his rescue spot, chunk 330) **[data]**. The journal's NPC directory is the client's
  `fill_ui_npc_directory` (#690 [`0x45d160`]: CharacterPresetRegistry, G38Knowledge, NpcRegistry, ItemRegistry). What it shows, drawn by the
  Survivors UI (from [`0xc46200`]): the row's title is the **summon tool's item name** (row [`0xc046c0`], `ItemInfo.name` +0x48 of `itemId`), the page's title
  the resource's `name` ([`0xc46efe`]), the page's text the **tool's item description** (`ItemInfo.description` +0x68, [`0xc46f1f`]). None of it depends on holding the tool. So
  the game's 35 tools carry their NPC's name and a line about them: `NPC_Townsfolk_Male_Villager_08_fisher_Trader` is "Captain Melville Fontane", "Captain Melville Fontane is a fisher and entrepreneur who offers fishing gear.", lore "Villager" **[dis] [data]**.
- Templates (58 with `ActiveNpcState`, 71 with `NpcDialog`): `ActiveNpcState` (456 B: `npcId`, `npcType`, sit/sleep/
  inspect sequences, decision weights), `ActiveNpcStateSaved` (saved: spawn position, roaming range override,
  happiness), `NpcState` (greeting sequences, turn setup, idle, `hapinessMask`, `minDistanceToPlayer`), `NpcCommon`
  (runtime: `bedId` +0x48, targets), `NpcActionSequences`, `CharacterSetup` + `NewCharacterSetup`, `Crafting {workshop,
  workshopId}` (vendors are NPC workshops), `CraftingInteraction`, `Inventory` + `RenderEquipment`, `LocaTagComponent`,
  `MapMarker` (18), `Shelter`/`Comfort`, `InteractionOfferRuntime` (`defaultVerbId`), `Faction Player`, `ForceSave`.

### 7.2 Dialogue data **[refl] [data]**
`ecs::NpcDialog` (template-only, 120 B): `isFlame` +0 (the Flame altar's dialogue), `dialog:
ObjectReference<NpcDialogDefinition>` +4 (editor side), **`dialogResource: NpcDialogResource` +0x14 (inline; what the
game reads)**, `interactionVerbLoca` +0x68.
- `NpcDialogResource` (84 B): `headline` +0 (the NPC's name), `crafting` +4, `itemUpgrade` +8, `itemRecycling` +0xc,
  `sendBackToFlame` +0x10 (menu entries, LocaTagIds), `defaultSequence` +0x14, `missingRequirementSequence` +0x24,
  `initLines` +0x34 (68-byte lines), `transitionLines` +0x3c (64 B), `farewellLines` +0x44 (64 B), `dialogOptions`
  +0x4c (68 B).
- A line (`NpcDialogOptionResourceBase`, 64 B): `optionId: DialogOptionId` +0 (a HashKey32), `overrideSequence` +4,
  `sequence` +8 (the NPC's animation for it), `knowledgeUnlockOperations: BlobArray<KnowledgeUnlockOperation>` +0x18,
  `knowledgeRequirement: KnowledgeQueryData` +0x20, `bodyTexts: BlobArray<LocaTagId>` +0x2c (pages / variants),
  `voiceovers` +0x34, `uiHintType` +0x3c (None, Hint, Attention, MainQuest: the marker over the NPC's head),
  `dialogStartAction`/`dialogEndAction` +0x3d/+0x3e (None, PlayOutro); options add `optionText` +0x40, init lines
  `isWakeup` +0x40.
- Example, the Alchemist (`NPC_Workshop_Alchemist01`): 58 init lines (greetings, most with a requirement: "My friend,
  if I had a sheltered bed…"), 1 transition line (3 bodies), 15 farewells, 33 options (MainQuest/Attention/Hint
  markers); 29 options unlock knowledge (all Bool = 1), 32 have an Extern (named query) requirement such as
  `NPC_ALCHEMIST_Hint02_AlchemistStaffSpells_conversation_topic`. The option "Shelter" sets `88b160c1`, the visibility
  knowledge of the quest "Alchemist Needs Shelter" (Q§1.1): **quests start from dialogue options**.
- **Line ids are knowledge**: every init line and option id of the Alchemist is a world+player knowledge object
  (91 of 91; farewell/transition lines are not): the converter's "dialog knowledge" ("has this line been heard"),
  queried by `DialogKnowledgeQueryAction` and by the client to place the "new" markers **[data]**.

### 7.3 Dialogue at run time **[dis]**
1. **Client builds the menu.** `ui_dialog` (#684 [`0x460b40`]; template NpcDialog, DynamicNpcDialog, G38Knowledge;
   events-out SelectDialogOptionEvent) keeps a per-NPC record (0x14b80 B) for NPCs in range and evaluates every line's
   `knowledgeRequirement` and its "heard" knowledge with the client's evaluator [`0xf0a960`] against the client's
   replicated knowledge. The menu itself is immediate-mode UI: "doVerticalTopicListDialogMenu" / "[GameUi] NPC Dialog
   Menu opened" [`0xb8ffc0`], "doNpcUIWindow" / "[GameUi] NPC Layer" [`0xc48d70`], "doNpcInteractionWindow"
   [`0xc49958`]; HUD screen 26 NpcDialog (27 NpcCrafting) (game-ui.md §2). **Texts, options and their filtering are
   all client data.**
2. **The choice goes to the server as player input** (ClientPlayerInput is written by `player_ui` #662 [`0x2b8430`]):
   `PlayerInput+0x1a4 SelectDialogOptionAction {dialogOptionId: LocaTagId, targetNpc: EntityId, versionData}` and
   `+0x1b0 StopDialogOptionAction` (same layout).
3. **Server, `select_dialog_option_input`** (#291, `0x1fab80`): for each player whose action version differs from the
   consumed copy (`ServerConsumedPlayerInput+0x6c`, stop `+0x68`) emits `ServerSelectDialogOptionEvent` (`0x62409977`,
   24 B: `npcEntity` +8, `playerEntity` +0xc, `selectedOptionId` +0x10, `type` +0x14 Start 0 / Stop 1). Nothing is
   checked.
4. **Server, `npc_behavior`** (#294, `0x1f97e0`, events-in that event; handler `0x1f2c10`): stores the line in
   `DynamicNpcState` (`lastSelectedDialogId` +0, `lastInteractingEntityId` +4), turns to the player and plays a
   sequence: id **1** → `missingRequirementSequence`; a line with `overrideSequence` → its own; else `defaultSequence`.
   Remote players see the NPC animate through the replicated actor sequence (§8).
5. **Server, `unlock_knowledge_from_dialog_option_input`** (#292, `0x1fb380`; template-read NpcDialog of the target
   NPC, sys-write KnowledgeTriggerQueue; consumed copy `+0x70` "consumedSelectDialogOptionActionForKnowledge"): ids 0
   and 1 are skipped; `FindDialogLine` `0x1f0ef0(rng, &NpcDialogResource, &id)` looks the id up in `initLines`, then
   `transitionLines` (matching the line id **or any of its body text ids**), then `dialogOptions`, then `farewellLines`
   (id 0 = a random farewell); the line's `knowledgeUnlockOperations` plus `{id, Bool, 1}` ("heard") are pushed for that
   player (up to 9; full queue → "Failed to trigger knowledge from dialog line."). **The option's
   `knowledgeRequirement` is not evaluated and neither distance nor the NPC's state is checked.**
6. **Voiceover and body text** are client-side: `play_dialog_voiceover` (#698 [`0x460360`]) writes the local
   `NpcDialogClientSync {selectedOptionId, selectedOptionVoiceoverId}`; `stop_dialog_voiceover` (#699).
7. Special ids: 0 = farewell (random line on the server), 1 = "missing requirement" (the client sends it for an option
   whose requirement fails: the NPC plays its refusal) **[dis]** for the server half, **[hyp]** that the client sends
   them so. That the client sends the chosen **init line's id when the dialogue opens** (so the greeting plays for
   everyone) is **[hyp]** (strong: init lines are searched first and their ids are knowledge).

What the server trusts: any `{line id, NPC}` pair from any client runs that NPC's line knowledge, and
`TriggerKnowledgeAction` (`PlayerInput+0x18c`, `handle_knowledge_trigger_input` #452 `0x1bc140`) pushes any non-zero
knowledge operation the client sends for itself (Q§2.5). A modified client can therefore start, advance or unlock
anything a dialogue line or knowledge object can; a Kiln server can veto both inputs (detour or a program ahead of
them).

### 7.4 What a new dialogue needs
| Change | Server | Client |
|---|---|---|
| Other texts for existing lines | nothing | loca overrides (kiln.content's loca hooks answer any tag id) |
| A new option / init line on an existing NPC | the line in the server's `NpcDialogResource` (template data) so its knowledge runs; its id a knowledge object if "heard" should persist | the same line in the client's template data (the menu is built from it) + loca for `optionText`/`bodyTexts`; vanilla clients never show it |
| A new NPC with its own dialogue | template clone with its `NpcDialog` (§7.6) | the clone on every client (`clients: "kiln"`) |
| A Kiln-driven conversation (branching, C# conditions, rewards) | Kiln reacts to `ServerSelectDialogOptionEvent` / the input; effects through Kiln services | Kiln.Ui window (§7.5) |
Template data is shared read-only memory edited in place (§1.4, gamedata.md §1): a longer `dialogOptions` array means
a rebuilt `NpcDialog` blob pointed to by the template's component span, or a cloned template (gamedata.md §5) **[hyp]**
for the in-place repointing (not tried).

### 7.5 Hook points for a custom (Kiln.Ui) dialogue
- **Server, no hook needed:** a kiln.world program with `events_in = ServerSelectDialogOptionEvent` sees every
  start/stop with NPC, player and line id; `unlock_knowledge_from_dialog_option_input` can be bypassed for Kiln NPCs by
  giving them lines without knowledge operations. The interaction offer (`interaction_query_new` #403 `0xa77d0` reads
  other entities' NpcDialog template data and their InteractionKnowledgeLock / KnowledgeQuery components) is what
  makes an NPC talkable **[dis]** (program table; the function itself was not read).
- **Client:** Kiln's ui service already reports the HUD screen (26 NpcDialog) and runs on the UI thread; the target
  NPC of the open dialogue is in the dialogue UI state (not mapped: the per-NPC records of `ui_dialog` and the menu
  builder [`0xb8ffc0`] are the candidates) **[hyp]**. Simplest working design: a Kiln NPC whose template dialogue has
  one init line (its greeting) and no options; on HUD 26 for that NPC (or on the server's Start event mirrored to the
  client over kiln.net) Kiln opens its own window and closes the game's (toggleHudScreen to 1, game-ui.md §2).
- **Vanilla clients:** no custom window; fallbacks are chat lines (kiln.chat) and the NPC's vanilla lines.

### 7.6 Recruitable NPCs: rescue, summon, beds, uniqueness **[dis] [data]**
- **Rescue = knowledge**: the NPC's `unlockRequirement` (SimpleBool) is set by a scene trigger or a dialogue line; the
  NpcRegistry follows (§7.1). The lab's world has 7 NPCs unlocked (Q§9) **[rt]**.
- **Summon tool**: `NpcResource.itemId` is a `BuildTools` item with flag `IsNpc` (35 of 35 resolve: "NPC_Crafter_Alchemist01"
  `0xf72a4c04`, permission CanEditBase, held visual `Visuals_Tool_NPC_Summon`). Its item sequences: `Building_Npc_Place`
  (Primary; spawns the VFX `VFX_NPC_Place` and a null-template SpawnEntityEvent at the cursor) and `Building_Npc_Assign`
  (Primary, requires `HasNpcTarget`; contains an `actor::AssignNpc {npcId 0}` event) **[data]**. The NPC for a held
  tool is looked up in the NpcRegistry by item (`0x92ff80`, called from the server cursor and `player_assign_npc`)
  **[dis]**; how the null-template spawn becomes the NPC (or moves the existing one) was not traced **[hyp]**.
- **Beds**: `player_assign_npc` (#319, `0xc4170`) walks the actor's running sequence events, and for `AssignNpc`
  (`0xc8407ec1`) with a selected bed: clears the bed's previous NPC, sets `NpcCommon.bedId` (+0x48) = the bed and
  `NpcBed.associatedNpc` = the NpcId. `npc_bed_init` #293, `npc_bed` #295, `base_register_npc_bed_server` #308 keep the
  base's bed list (PlayerBasesSystemServer); `ClientNpcBed` mirrors it (`npc_active_target_to_client` #297).
- **Uniqueness**: `unique_npc_init` (#288, `0x1fade0`) and `unique_npc_spawns` (#289, `0x1fb0f0`; every entity with
  ActiveNpcStateSaved) keep **one entity per NpcId** in the PlayerBases registry (`0x209840` get, `0x20a560` set): a
  second entity of the same template is destroyed (`0x5fbe20`); an NPC that the NpcRegistry knows but reports locked is
  destroyed too, an NPC the registry does not know only gets the uniqueness check.
- **Happiness**: `NpcState.hapinessMask`, `npc::HappinessRequirement` (Sheltered, Bed, Food), `npc::PlacedNpcInfo
  {baseIndex, entityId, happiness}`, `npc_shelter` #64; runtime queries of type Npc (IsSpawned, CraftingPropPlaced,
  IsSheltered, SufficientComfortLevel) feed quests ("Build A Sheltered Bed For Balthazar") (Q§2.2).
- **A new recruitable NPC needs**, on the server and on every client (`clients: "kiln"`): a template clone of a
  villager/crafter with a new GUID (its NpcId follows; `ActiveNpcState.npcId` set to it **[hyp]**; see 7.8),
  an `NpcCollection` entry (name, unlock knowledge, workshop, summon item, preset, portrait), a summon tool item (clone
  of an NPC item, kiln.content), new knowledge objects for the unlock and the dialogue lines (Q§2.5: the static maps),
  loca, and for vendors a workshop + crafting menu entry (content.md §7, game-ui.md §5). The collection and the
  knowledge resource are read when the world starts: boot-time resource overrides (kiln.assets) or registry inserts
  **[hyp]** (neither tried).

### 7.8 What the build relies on (no client run)
Evidence as in `docs/re/quests.md` section 11; usage `docs/kiln/services/npcs.md`.

**A longer `dialogOptions` array: repoint inside a copy.** No clone needed and no write to game memory: every offset in a
`TemplateResource` is self-relative, so an override is the game's bytes plus the new arrays behind them, with the BlobArray fields of the
copied `NpcDialogResource` pointing there. Moved lines keep their inner arrays by rebasing `+0x18`, `+0x2c`, `+0x34`. The world's
template-info init and `createEntity` get the override from the resource service, as for any template.
- **[rt]** the engine's per-entity template slot reads it: `PASS entity 1159's template slot reads the overridden template: headline 3825829699 = 0xe4098743`
  (Balthazar, loaded from the save); kiln.gamedata reads the appended option too. Offline, his 33 options and 58 init lines stay intact and no other
  component moves.
- What the client does with lines, `ui_dialog` [`0x460b40`] **[dis]**:
  - The per-NPC record's lists hold 32 lines inline (`+0x28`, `+0x14d2`, `+0x297c`, `+0x3e38`) and have a grow callback, so more lines are allowed.
  - A line with more than 32 texts or 32 voice-overs **stops the game** (`0x20 < n`, then a fatal path). `voiceovers` runs parallel to `bodyTexts`.
  - A line with no text is skipped.
  - An option is listed when its requirement holds, and that query has no zero-id guard: id 0 gives error `0x12` and hides it. All of the game's options
    have a requirement. Kiln gives unconditional options `SimpleCount Equals 0` on a world counter nothing sets.
  - Init lines with no operations, no requirement and no `isWakeup` are the plain greetings. Other init lines compete by `uiHintType`; only the highest
    priority stays while unheard.
  - An evaluation error on any line, other than the unknown-id `0x1f` that the wrapper swallows, keeps the whole NPC's menu from being rebuilt in that pass.

**The summon path.** `Building_Npc_Place`'s null-template SpawnEntityEvent spawns the tool's `equipment.placedEntity`:
- **[data]** for all 28 roster tools, FNV-1a(`placedEntity`) = the NPC's id, and all have `placementConsume`. That the null template resolves through this field is
  **[hyp]**: the field is what prop items place (build.md), and the code path between them was not followed.
- **[dis]** actor spawn-event branch near `0x447c0..0x448b0`: FNV(template GUID) is looked up in the NpcRegistry (`0x93eeb0`) and the PlayerBases NPC map
  (corrected 2026-11, dev.28 RE [static]: the previously named `0x425b0` is interior code; chained unwind roots the
  function at `0x42390`, not a qualified raw-call API)
  (`0x209840`). **Both found: no spawn**; it emits `NpcCommandEvent` (`0x4b001be5`, 72 B) with type Reset, that id and the spawn transform: the existing NPC moves
  to the cursor. Otherwise it creates the entity (`0x5fb340`).

**NpcCommandEvent** (`npc_active_behavior`'s handler `0x1f3320`) **[dis]**:
- Each NPC finds events whose `npcId` (+0xc) equals its own value.
- **Reset (0)**: SetState Start, a flag and the orientation stored in a component not identified here (`+0x49`, `+0x4c`), `ActiveNpcStateSaved.spawnPosition` = the position (**[rt]** below), teleport through `0x1ed350`.
- **GoToPosition (1)**: SetState Roam (7), decision `+2` WantsToRoam, `NpcCommon.currentTargetPosition` (+0x64) = the position.
- **While the NPC's state is Start (1), every type but Reset is ignored**: the handler returns. **[rt]** consistent: with no player NPCs stay in Start, and a `GoTo` changed no state in the lab. The walk itself has not been seen.

**`ActiveNpcState.npcId` for a clone.** Set to FNV(clone GUID). `unique_npc_init` / `unique_npc_spawns` compute the id from the template GUID themselves, so uniqueness and
the unlock rule never read the field **[dis]**. The command handler compares with a per-entity value, and with the field patched a Reset reaches the clone **[rt]**: `PASS Teleport
(the game's Reset): moved to (3896.0, 834.0, 1492.0) (0.0 m from the target)`, `ActiveNpcStateSaved.spawnPosition (3896.0, 834.0, 1492.0)`. Whether that per-entity value is exactly this field, so
that an unpatched clone would answer its base's commands, was not tried.

**Rebuilt `NpcCollection`, not a registry insert.** The registry keeps a handle and builds its maps (id, and item at `+0xb0`, `0x92ff80`) at load, so a roster rebuilt before that needs nothing else
**[rt]**: `PASS the roster has the clone`, `PASS NpcRegistry follows: unlocked`. An insert would have to update those maps too.
- **Limit [dis]:** `0xa2ef90` keeps the unlock states in one u64 (`1 << (i & 0x3f)`, stride 0x88), so **64 roster NPCs at most**; the 65th would alias the first. The build refuses more.
- **The rules on a clone [rt]** (`unique_npc_spawns`, entity gone about 1.3 s after the spawn):
  ```
  PASS the game destroyed the locked roster NPC 2556 (uniqueness / unlock rule)
  PASS NpcRegistry follows: unlocked
  PASS the unlocked clone lives: 2558
  PASS one per world: the second clone 2570 was destroyed, 2558 stays
  PASS a clone that is not in the roster lives without any unlock: 2572
  PASS its uniqueness holds too
  PASS a second giver 2584 was destroyed by the game (one entity per NpcId)
  ```
  The same holds for game NPCs: a locked villager placed by a mod is destroyed, so placing one needs its unlock (a lab saw the same with Hugo Vanderhall). Saved
  clones return after a restart (`native: NPC quest-native-test:lab_clone is in the world: 1167`). Without the mod they are gone and the server starts normally (`Initialized 12,269 template infos`,
  no entity of the template).

**Not run in that earlier lab:** dialogue selection on a new line (needs a client), the NPC directory [`0x45d160`] with a new entry (a hash map at `G38UiPlayData+0x463e90` whose capacity was not read), the summon tool, beds, and the GoTo walker. Dev.28 later exercised native summon-tool input and tracked bed assignment (§14); their native UI and walking remain unchecked. A player is not a universal path-query prerequisite (§13).

### 7.7 Vanilla client vs Kiln client
- Vanilla clients work with: server-side knowledge grants for vanilla ids (their journal, NPC directory, dialogue
  filters and markers follow the replicated knowledge), NPCs spawned from vanilla templates, Kiln reacting to their
  dialogue choices, chat text.
- Kiln (+ the mod's data) on the client is needed for: new or changed lines and texts, new NPC templates, new journal
  entries, new marker names, a Kiln.Ui dialogue window.
- Server-authoritative: every knowledge change and its consequences (rewards, NPC unlocks, uniqueness, beds,
  sequences); the client is authoritative only for what it displays and which line it says it picked.

## 8. Networking: what a vanilla client gets
- **Server-authoritative and replicated through existing components:**
  - health (`NetworkHealth`, docs/re/combat.md), level (`NetworkLevel` followed the write: `NetworkLevel 10`), AI state
    (`NetworkEnemy` followed: `NetworkEnemy state 4`);
  - position/rotation/scale (transform), action sequences (`NetworkActor.sequenceRuntimeId`, replicated by id; the client
    plays its own copy of the sequence), faction (dynamic on both sides).

  Server-only data never leaves the server: `Enemy01BehaviorState`, arsenal, `EnemyScaling`, `RandomLoot`, `SubEntities`,
  `EnemyPhases`. So changing it needs nothing on the client, and its effects (moves, hits, drops) arrive as normal state.
  **[rt]** for the server side; vanilla client rendering: client checklist.
- **Template identity:** the client's `createEntity` has the same lock-by-GUID code (client string reference
  `0x987f1e` = server `0x623e8e`) **[dis]**. Reflection has `ecs::EntityTemplateMessage {templateGuid, templateData:
  BlobArray<u64>}` and `ecs::TemplateMessage {templates[] {guid, name, family, componentsMask}}` **[data]**. So clients
  most likely create replicated entities by template GUID from their own resources **[hyp]**. A template unknown to the
  client fails there ("Could not lock template resource"). New templates therefore need the definition on every client
  (Kiln + synced definitions), like new items.
- **Client-only template data** (model, VFX, visual equipment, highlight): a vanilla client always uses its own copy, so
  look changes need Kiln on the client. Data edits on the client: template memory is shared by the server and client
  worlds of one process (EMF, confirmed in game), so a hosted game needs one edit.
- **Scale:** dynamic on both sides; whether `ScaleComponent`/transform scale replicates to a vanilla client and is drawn
  is **[hyp]** (client checklist).

## 9. Lab runs

The probes:
1. Component map, template data vs kfc, template write, AI program counters, transition table, dynamic writes,
   spawn-component variants, kill.
2. Forced IsReadyForGameplay.
3. The g38_enemy singletons.
4. Per-entity template override.

All entities were spawned 60 m from the base and destroyed; template bytes were restored; the server ran on.

## 10. Open questions
- Replication path for entity creation and which components replicate by default (EntityTemplateMessage,
  TimelineReplicationTemplate). Does `ScaleComponent` reach clients? Can the server send a client-only component
  (ModelDynamic, HighlightColor) per entity?
- Arsenal lookup inside enemy01: by GUID through `EnemyArsenalRegistry` maps (`+0x10/+0x30/+0x48` look like hash maps),
  cached per entity or per update? This decides whether a per-entity `EnemyArsenalReference` slot override (like §1.4)
  switches attacks live.
- `BlobVariant` / `TemplateResource` component lookup without an entity (to patch a template before any entity exists),
  and whether the template data pointers point into the TemplateResource blob (same region as items: likely).
- How `register template info` (`0x6671d0`) and the template-info init (`0x667c10`) pick up a TemplateResource that
  kiln.assets serves as **new** at boot (would make clones nearly free), and TemplateInfo array growth after init.
- Health formula (attribute commands, `htkStart/End`, `BalancingTable`) for exact level → stat curves.
- Actor `+0xBF8` bits 23, 24 and 32 (set while spawning), and which checks `0x176670`/`0x1765d0` perform.
- Villager recruiting (`player_assign_npc`, NpcResource `itemId`), vendor stock (workshop recipes = trade lists?), and
  whether new NPC workshops need `WorkshopRegistryResource` and UI config entries (cf. the crafting-menu config,
  docs/re/content.md §7).
- Animation compatibility rules between sequences and graphs (state id hashes), for mixing attacks across creatures:
  abilities-research and models.

## 11. Findings while building kiln.creatures

Everything below is captured by `sigdb/symbols/server.d/creatures.json` (it resolves unchanged on the server 1285601,
the old server 1273043 and the client exe 26404966) and checked by `mods/tests/creatures-test` (76 PASS).

- **SetState has one caller [dis].** `0x12af10` is called only from HandleCondition (`0x12c70a`); HandleCondition
  itself has 44 callers (state handlers in `0x118000..0x137000`). So a mid hook on both entries sees every state change
  with its condition. The behaviour context (`rcx` of both) is the struct the per-entity function `0x12ce30` builds at
  `[rbp-0x41]`: `+0x00` the engine ProgramContext (CurrentEntityId works on it: the hook gets the entity id), `+0x08`
  the write records, `+0x28` the template slots, `+0x48` DynamicEnemy01BehaviorState*, `+0x50` the template
  Enemy01BehaviorState*, `+0x58` the arsenal record, `+0x60` the program time. **[rt]** `Spawning->Idle (IsFinished),
  Idle->Roaming (IsReadyForRoaming), Roaming->Idle (IsFinished)` for a Vukah held ready.
- **The arsenal is resolved by GUID in every update [dis] [rt].** The per-entity function calls
  `ArsenalFind(EnemyArsenalRegistry, &EnemyArsenalReference.arsenal)` (`0x992d50`, a GUID hash map at singleton
  `+0x10` bits / `+0x18` cap / `+0x30` keys / `+0x5c` count) right before the behaviour update and passes the result in
  the context. A private `EnemyArsenalReference` copy therefore swaps one creature's arsenal live: `enemy01 looks up
  A's new arsenal 50x in 2 s (and B's own 50x)`.
- **Template slot layout [dis] [cap].** `GetTemplateComponent` `0x64fae0`: type index from the template component map
  (`registry+0x30`, `0x64dc40`), then `0x64fa00`: entity map `storage+0x148` (`0x650c50`, cap `+0x10`, values `+0x40`),
  record `+0x18` archetype (template-component bitset at `+0`, column offsets `+0x584 + 2·type`), `+0x20` chunk,
  `+0x30` index; slot = `chunk + column + index·8`. Chunk moves (teleport adds/removes the `Teleport` component) carry
  the slot value: the copy was read `125x` across them, while the DynamicEnemy01BehaviorState pointer changed twice.
- **Readiness bit [cap].** `actor_is_ready_for_gameplay` `0x7d1a0`: `mov rcx,[rbx+0xbf8]; shr rcx,0x32; and cl,1`
  (bit 50); the game calls MarkWritten on Actor before it toggles the bit, so the state replicates.
- **Command events and their readers [dis].** The descriptor's events lists (`+0x160` in, `+0x170` out) name:
  `AddThreatTargetEvent` → `enemy_threat_events` (#221, SYSTEM), `StartGuardingEvent` / `StopGuardingEvent` /
  `SetEnemyVariableEvent` → `enemy01` (#226; players send StartGuarding through `player_control_action_sequence`),
  `RestoreEnemyHealthEvent` → `enemy01_pre_update` (#224), `RegisterControlledCombatTargetEvent` →
  `control_combat_target` (#225), `NpcCommandEvent` → `npc_active_behavior` (#234; `npcId` is an `NpcId`, not an
  entity). Layouts (reflection): AddThreatTarget `{Time, attacker, target, f32 threat, HashKey32 pool}` 24 B,
  Start/StopGuarding `{Time, guarding, guarded}` 16 B, RestoreEnemyHealth `{Time, enemy, Time duration}` 24 B.
  **[rt]** (readiness forced, no player) `StartGuardingEvent` → `guardEntityId` = the guarded entity within 2 s,
  `StopGuardingEvent` clears it; `AddThreatTargetEvent` → state Combat and `SelectedTargets[0]` = the target (a
  PlayerAlly Vukah; it targeted back).
- **Attributes [data] [rt].** DamageSusceptibility's Physical/Cut/Pierce/Blunt calcs are only clamps of their own value
  (0..100): direct slot writes stay. DamageMod = `Base · Multiplier + Adder`; enemy_scaling's update keeps a written
  Multiplier (`DamageFactor 1.5 holds after an enemy_scaling update`). Level's calc clamps to Level_Max. Health at
  level 20: `1031/1031`.
- **Spawn components [rt].** Through kiln.spawn's components: `Faction 11, ScaleComponent 2 and Level 12 ...: faction
  PlayerAlly, scale 2, level 12, health 511`.
- **MarkWritten write records [rt].** In a raw program the write record's `[5]` (the tracker MarkWritten takes) is NULL
  for components the engine does not track for that entity; a non-raw write needs one MarkWritten per component. A raw
  program's update runs once per archetype group (several per step): `2 iterated` per call for Vukahs.
- **Catalog [data] [rt].** Shipped survey vs the engine's template infos: 538 enemy-AI + 58 villager templates with
  `DynamicEnemy01BehaviorState` / `DynamicActiveNpcState` in their dynamic bitset, all in the 689-entry catalog
  (+82 wildlife, 11 animals). A 269-entry display-name list: 251 are catalog creatures, the other 18 are attachments and
  attack entities (`Enemy_*_Weapon_*`, `Attack_*`, `VFX_SubEntity_*`, one pickup).

Answered from §10: arsenal lookup (by GUID, every update), TemplateResource lookup without an entity (kiln.content
gamedata, `docs/kiln/services/content.md`), template clones (kiln.content `templates.add`, gamedata worker).

## 12. Guard and follow (server Steam 26430253 / svn 1287369)

A real client saw `Idle -> Guard (NeedsToGuard)`, `Guard -> Idle (IsFinished)` 0.5 s later, again and again, and
no following. Why, and what the game's own followers need. RVAs below are equal on svn 1285601 and 1287369 (the
functions did not move); decompiles from the Ghidra project of 1285601, checked against the current exe with capstone.

- **The Guard state (20) is a path follow [dis].** StateDispatch `0x13a7f0` case 20 → `0x1371e0(ctx, phase)`:
  - enter: `currentGuardTargetDistance (+0x468) = guardSetup.targetDistance (tpl +0x59c) × random` (2.5-3.6 m for the
    Vukah);
  - execute: if `guardEntityId (+0x454)` resolves (`0x38730`: the target's CurrentTransform), `movementTarget (+0x20)`
    = the target's position, `movementTargetIsValid (+0x1c)` = 1, `DynamicPathWalker.moveTypes |=
    movementSetup.extendedMoveTypes (+0x594)`, `keepLastResults (+0xf0)` = 1 (group members get offsets around the
    leader, `useMovementTarget`/`useTargetOffsetsWhileTargetIsMoving`/`predictTargetPosition` +0x5a7..+0x5a9);
    then the move request `0x1169e0(ctx, distance, 1)`;
  - the move request writes the path walker (`targetPosition +0xb0`, Guard only: `maxTargetNodeDistance +0x100` =
    the guard distance, `maxArriveTargetDistance +0xdc` = `guardSetup.maxArriveDistance +0x5a0`; run/walk by distance
    unless `disableRunning +0x5aa`) and returns from the walker's state: TargetReached (8) → 0, Idle/WaitingForQuery
    (0, 1)/Unknown → 4, Stuck (10) → 2, TargetNotReachable (9) → 3, else 1 (walking);
  - **the give-up rule**: Guard raises IsFinished (→ Idle) when the request returns 0 (arrived), or after **0.5 s**
    (`cmp rbx, 0x1dcd6500` at `0x137652`) when it returns 2 or 3 (Stuck / TargetNotReachable).
- **NeedsToGuard [dis].** `0x118e10`, every 0.5-2 s (`nextCheckGuardTime +0x480`): if the guarded position is at least
  `guardSetup.startDistance` (`tpl +0x598`, 10 m for the Vukah) away → HandleCondition(23). HandleCondition maps a
  ReturnHome (7) to Idle while `guardEntityId != 0` (a guard does not walk home).
- **Who sets guardEntityId [dis].** StartGuardingEvent `{Time, guarding +8, guarded +0xc}` (enemy01; the player's
  "guard me" interaction of tamed creatures sends the same event from `player_control_action_sequence`, `0xca653`,
  after `guard_interactions` (#405) offered it: the only reader of `guardSetup.canGuardPlayer +0x5a6` is `0xc2b49`
  there). Companions spawned by players (`Player_Companion_*`, `guardSetup.guardParent` +0x5a4) set it to their ECS
  parent in the Spawning state (`0x13a420`, `0x13a78e`). StopGuardingEvent (`0x119050`) clears it when both ids match;
  an arsenal action (`0x11eed0`, `0x11efa2`) can also clear it.
- **So following is the game's path walker [rt].** Lab, no player (a probe mod): a forced-ready Vukah A
  guarding Vukah B 12-25 m away:
  ```
  g0 A (3865.8,833.0,1433.8) st 20 mvValid 1 mv (3877.8,833.0,1433.8) ... guard 2543 gdist 2.5 |
     pw state 9 res 2 tgt (3877.8,833.0,1433.8) ... arrive 2.0 node 2.5 moves 0x1d
  state 2542: Guard -> Idle (IsFinished)                  (0.5 s later; then NeedsToGuard again)
  ```
  The walker answered TargetNotReachable / `lastPathfindingResult` NoPathFound in that lab. Corrected 2026-11
  (dev.28 RE): it does **not** prove “no path grid without a player”; explicitly loaded, mod-owned queries succeeded
  with no Player entity (§13). Ordinary AI dormancy/streaming remains a separate observation. A real client showed
  the same failure pattern after a teleport to a spot with no ground (`raw target: no ground found`, then emergency
  teleport), where the walker likewise had no reachable target. [lab] [seen]
  A spawned player-template entity (`1_Player`) does not stream terrain: Vukahs next to it stay in Spawning.
- **What Kiln adds (Kiln.Creatures `Guarding.cs`, lab-verified in `creatures-test`):** the order is kept (resent
  when guardEntityId is dropped), the guard's home zone follows the target (roaming and leash centre on it), a
  catch-up teleport (kiln.world, `FindGround`) 2.5 m from the target when the guard is more than 30 m away or stood
  still for 4 s while the target is out of guard range (the walker gave up), and defend/assist through
  AddThreatTargetEvent on kiln.combat's damage events:
  ```
  catch-up #1 (stood still 4.1 s, 18.0 m away (state Idle)): moved 2607 to (3881.3, 833.5, 1463.8) ... 6 steps
  catch-up #2 (44.5 m away (catch-up distance 30)): moved 2607 to (3923.3, 833.5, 1463.8) ... 7 steps
  guard 2607 (defends 2608): threat on 2609        (probe: A Investigate -> Combat (TargetSelected), target 0 -> C)
  ```
- **Near a player [rt, real client].** The same Vukah order with a vanilla client joined: `Guard ... 10.4 m ->
  5.7 m -> 3.3 m, path walker Walking (PathFound)`, then over 30 m with a JumpDown after the player until the walker
  answered NoPathFound / MaxIterationsReached. So the game's Guard follow works where a path grid exists; that client's
  loop came from the player's position (no reachable ground). An ally Vukah also keeps leaving the player through
  `NeedsToInvestigate` (its template has Awareness + Investigate; the game's own necromancy companions have no
  Awareness): with Investigate off it stayed 2-3 m from the player. Kiln's `GuardOptions.Focus` turns both off.


## 13. Mod-owned path queries (dev.28; server 26430253 / client 26430625)

S `f0d824be…`, C `5d9ef2f1…`. Source: dev.28 dossier, chapter 02, queries REPORT/VERIFY. All five contracts and
layouts below independently confirmed; Reset count-clearing and map-growth qualifications are incorporated.
See [build.md](build.md) for voxel-ray/domain checks: a clear voxel ray is not body-clearance or connectivity proof.

### Calls (Windows x64) [static] [lab]

| Function | S / C RVA | Contract |
|---|---|---|
| PathQueryDefaults | `0x61e720` / `0x982890` | `void* f(void* q)`; 0x110 B, owner +0 preserved; returns q |
| CreatePathQuery | `0x5fb550` / `0x95fa00` | `u32* f(CmdBuf*, u32* outId, const void* q)`; returns outId, zero id on reservation failure; no fourth arg |
| GetPathQueryResult | `0x6533c0` / `0x9b7530` | `bool f(ProgramContext*, void* out /*0x28 B*/, u32 id, bool acceptDirty)` |
| DestroyPathQuery | `0x5fbe60` / `0x960310` | `bool f(CmdBuf*, u32 ownerForLog, u32 id)`; true = queued, not destroyed yet |
| ResetPathQueryResults | `0x5fbfc0` / `0x960470` | same bool signature; makes Inactive, **not** cancel/recompute |

Zero an aligned 0x110-byte query; call Defaults **once before** filling coordinates. Its internal base initializer
S `0x5f4f50` / C `0x959400` is a layout guard, not a required extra call. Create queues kind 5 / 0x128 B, id `+0xc`,
query `+0x10`; atomically increments `*(u32*)(*(cb+0x38)+4)`. The flush requires the owner still exists, but no
creature PathFinder component. Destroy queues kind 8 / 0x10 B; Reset kind 7 / 0x10 B. Owner on destroy/reset is
**diagnostic only**, so cleanup can use it after the entity disappears. [static] [lab]

| Query offset | Meaning |
|---|---|
| `+0` u32 | owner entity |
| `+0x10/+0x1c` float3 | absolute start/target metres |
| `+0x36` u8 | target type: Default 0, PreferClosestTargetNode 1, UseClosestTargetNodeAsFallback 2 [refl] |
| `+0x48` u32 | max iterations; zero means 500 [static] |
| `+0x54` u16; `+0x56/+0x57` u8 | move mask; jump-up/down heights |
| `+0x6c..+0xbb` | 20 internal cost floats; preserve defaults, not the reflected 17-category PathNodeCostFactors |
| `+0xca` u8 | default 1; time-slicing name [inferred], complete toggle semantics untested |
| `+0xf0/+0xf4`; `+0xf8/+0xfc` u32 | start radii (defaults 2/2), target radii (4/2), half-metres |
| `+0x100/+0x104`; `+0x108/+0x10c` u32 | min horizontal/vertical space; extra target-space requirements (defaults zero) |

Offsets from game callers/defaults/normalizer [static], walking subset [lab]. Preserve unexposed defaults; the engine
normalizes half-metre units, so do not do it twice. Request mask is `1 << PathMoveType`, while each point stores a
**value**: Move 0, Fly 1, JumpUp 2, JumpDown 3, JumpHorizontal 4, OpenDoor 5, Swim 6, Dive 7, MoveOnWaterGround 8,
FlyThroughWater 9. Only Move was exercised. [refl] [lab]

### Context, result and lifetime [static] [refl] [lab]

Use a real scheduler ProgramContext: `*(ctx+0)=job`, job `+8` command buffer, `+0x28` PathGridSystem, realm byte
`+0xbb`. The realm getter S `0x661300` / C `0x9c5350` and its ray-options caller guard that last field; it is not
an arbitrary request realm. System: id map `+0x138`, **bucket capacity** u64 `+0x148`, records pointer `+0x178`,
occupied count u32 `+0x18c`. Record stride 0x1a0: state `+0x120`, points/count `+0x128/+0x130`, auxiliary/count
`+0x150/+0x158`, update counter `+0x178`, result byte `+0x191`.

Get returns true for state 3, or state 4 **with points** and acceptDirty. Output: points/count `+0/+8`,
auxiliary/count `+0x10/+0x18`, counter u32 `+0x20`, result byte `+0x24`. Point stride 16: float XYZ `+0/+4/+8`,
move byte `+0xc`. Arrays are **borrowed**: copy inside this callback before destroy/reset or a later update.
Auxiliary element format, dirty-result branch and non-Move traversal are not lab-proved. False Get leaves the
output unchanged, not a measured zero-count result.

PathQueryState: Requested 0, PrepareAdditionalSpaceSearch 1, AdditionalSpaceSearch 2, Processed 3, Dirty 4,
Inactive 5. PathfindingResult: None 0, PathFound 1, NoPathFound 2, NoFreePath 3, NoOccupiedPath 4, NoStartNodeFound 5,
NoTargetNodeFound 6, MaxIterationsReached 7, OpenListOutOfMemory 8, NodesOutOfMemory 9. **True Get is not PathFound.**
Reset flush clears stored point count and sets state 5 without erasing; unreadable polling for 241 steps was observed,
but clearing count is static evidence, not the zero-initialized false-result log.

The map constructor S `0x628680` requests 1024 entries; allocator `0x6173b0` creates 2048 buckets / load limit 1024.
Insertion `0x675b90` attempts 4× bucket growth through `0x6b64a0`: **neither is a hard query cap or residency radius**.
Core grid `system+0x130`, unit bytes `+0xd1..+0xd6`, dimensions `+0x110/+0x114`, origin float3 `+0x120`; lab snapshot
16,8 / 2,1,2,2 / 2048,512 / (-1024,-1024,-1024). Search driver S `0x60f430` can yield across steps; busy polling
cannot advance it. Constructors/search-driver addresses are analysis anchors, not direct public calls. [static] [lab]

[lab] Explicit loading, nonplayer `Base_LootPickup_Generic_Gravity` owner, PlayerInput=null, no Player entity or
network client: 3 m route **12 points / 4 steps**, fallback 12 / 6, final map count 0. This corrects the universal
no-player grid claim, not ordinary AI dormancy. A 213 m request returned readable None/empty in one step;
200 m is conservative proposed service policy, not a measured exact engine cutoff. Idle/pressure eviction is unmeasured.
No cheaper general region/flood-fill reachability call was established; a clear ray or shared cell residency does not
prove connectivity. Returned paths were queried, not followed by an external mod-owned walker.

[seen] Hosted session 2 exercised all five client addresses: short path 12 points / 3 steps, fallback 12 / 5.
Destroy was queued for each id; final **shared** map count 7 proves neither a leak nor complete per-id removal.
Reset timeout was expected; the later unconditional deadline was a probe false failure **before** reload. Module
state prevented a second-world test, and the server-world pointer was reused across generations. Hosted wall writes
were base-refused, not failed collision tests. No follower, per-id hosted cleanup or reload-safe query service was proved.

The game consumers remain S `path_walking=0x1ff610`, `pathfinding=0x203ab0` [static]. PathWalker is 92 B
(usePathFinding `+0x20`, radius/height `+0x38/+0x3c`, tolerances `+0x40/+0x44`); DynamicPathFinder 0x110 B
(target `+8`, queryId `+0x38`, moves `+0xe8`), DynamicPathWalker 0x1c8 B (state `+0`, pathIndex `+0x10`, move
`+0x14`, last result `+0x24`, target `+0xb0`, disablePathfinding `+0xf1`, moves `+0x1a4`). [refl]
Returned points do not move an entity and no external-array ownership field was established. CreatureCommands.MoveTo
sets Home; NpcControl.MoveTo writes normalized per-tick XZ input, not a route. Existing custom-brain perception is
faction/distance only, not voxel LOS. [code checked] Full enum/layout catalogs and signature captures stay in chapter 02.

## 14. Survivor recruitment and beds (dev.28; server 26430253 / client 26430625)

S `f0d824be…`, C `5d9ef2f1…`. Source: dev.28 dossier chapter 18, survivors VERIFY (11 confirmed, 3 corrected).
These are native mechanisms and bounded lab evidence, not a shipped survivor service or native-UI acceptance.

### Unlock and tool delivery [static] [refl] [lab]

Unlock knowledge, roster confirmation and inventory tool delivery are separate operations. `npc_unlock_state_server`
S `0x1fa330` / hosted C `0x45fb10` evaluates world knowledge; presentation twin C `0x45fa90`. Granting rescue Bool
`0x1c68d6ae` unlocked Blacksmith assistant NpcId `0x672da6c9`, and it survived uniqueness/unlock programs. Its summon
ItemId was `0x171d1f69`. No new physical rescue scene or two-player personal-progression result is claimed.

`inventory_actions` S `0x1a5af0` / C `0x4047f0` consumes CreateItemAction (12 B): version `+0`, ItemId `+4`,
type byte `+8` (Npc 0, ClaimQuestItem 1). In PlayerInput these are **`+0x208/+0x20c/+0x210`**; consumed version is
ServerConsumedPlayerInput `+0x34`. Injecting the actual Npc action on a spawned player-template entity granted a second
tool in the lab; it was not a real player's UI interaction. The internal six-argument claim helper S `0x1b12d0`
requires an inventory transaction and incompletely typed descriptors: do not fabricate a raw call.

`u32 NpcFromTool(NpcRegistry*, u32 itemId)` S `0x92ff80` / C `0xe88db0`; `u32 FindNpc(PlayerBasesSystemServer*,
const u32* npcId)` S `0x209840` / C `0x470fe0`; both return 0 on miss. The latter signature is captured from the
assignment caller because the neighboring body is identical. Use live registries at a safe world phase, not stale
pointers. NpcId is stable FNV(template GUID), **not entity id**. Reset uses NpcCommandEvent (72 B): Time `+0`,
command `+8` (Reset 0 / GoTo 1), NpcId `+0xc`, WorldTransform `+0x10`; never raw-call the interior `0x425b0` (§7.8).

### Bed relationship and maintenance [static] [refl] [lab]

| Program | S / C RVA | Contract |
|---|---|---|
| player_assign_npc | `0xc4170` / `0x2d81d0` | `void(ProgramContext*)`; held tool → NpcId → unique entity; selected bed; tracked writes to both endpoints |
| npc_bed_init | `0x1f9610` / `0x45ed70` | real INIT context; restore runtime relationship from saved stable NpcId |
| npc_bed | `0x1f92a0` / `0x45ea00` | real program context; range/consistency maintenance |

NpcBed is 4 B, associatedNpc (NpcId) `+0`. NpcCommon is 0x698 B, bedId (EntityId) `+0x48`, target position `+0x64`.
Native reassignment clears the target bed's displaced NPC, writes both new endpoints, and leaves the NPC's old bed
for maintenance. In range, maintenance fills common.bedId when zero or frees this bed when it names another.
**Correction:** NpcId lookup miss keeps a stale associatedNpc (init S `0x1f96e8`, maintenance `0x1f9373`); init clears
it at `0x1f972f` only when a returned entity id fails ECS record lookup. Do not equate lookup miss with cleanup.

Bed radius is template ActiveNpcState.searchTargetSetup `+0xb8`, bedRadius absolute `+0xbc`. With nonzero saved
home, maintenance compares squared bed-to-home distance to radius²; out-of-range queues common.bedId=0 and clears
the bed. Out-of-range behavior is [static], not lab-tested. ActiveNpcStateSaved is 48 B: spawnPosition float3 `+0`,
overrideRoamingRange `+0xc`, happiness `+0xe`, roamingRange `+0x10`, teleportPositionAfterFinishing `+0x18`.
Double-bed StoreAssociatedNpcIds is 12 B: initialized `+0`, two stable NpcIds `+4/+8`; not itself an NpcBed. [refl]

[lab] A tracked **two-sided writer**, not direct invocation of player_assign_npc, survived assign/reassign/unassign.
Restart changed NPC/bed entity ids 2023/2024 → 1026/1027 and rebuilt common.bedId. Removing the altar did not break
the link; destroying the bed cleared it. No sleeping/walking, station animation or native assignment UI was observed.
Stations are AI-discovered targets, not an established persistent player-assigned job link.

Runtime metadata [lab]: NpcBed and StoreAssociatedNpcIds saved, not directly replicated; NpcCommon neither;
ClientNpcBed replicated, not saved. Relationship entity ids are runtime-only; resolve stable NpcId again after load.
Base count/size contracts and TriggerShape replication are in [build.md](build.md).

### The roster's 64-bit cache (chapter 17.3) [static] [refl] [data] [lab]

Caps VERIFY confirmed the common evaluator/setter/query contracts. Vanilla NpcCollection has 35 entries, stride 0x88:
NpcId `+0`, unlockRequirement `+0x10`, workshop `+0x1c`, ItemId `+0x24`. It is knowledge, not a saved raw unlock mask,
that drives recomputation independently on server/client.

| Contract | S / C RVA |
|---|---|
| `ResultU64* Eval(out /*16 B*/, knowledgeContext, u16* seenVersion, const SpanNpc* {ptr,u64 count})` | `0xa2ef90` / `0xf0c510` |
| `void SetUnlocks(NpcRegistry*, u64 mask, u16 version)` | `0x96c6a0` / `0xec4800` |
| `ResultBool* IsUnlocked(out, NpcRegistry*, u32 npcId)` | `0x94bf00` / `0xea4cb0` |
| `const NpcResource* Find(NpcRegistry*, u32 npcId)` | `0x93eeb0` / `0xe97c70` |

Result status byte `+0`, payload `+8`; registry collection `+0x20`, id map `+0x38`, knowledge version `+0x90`,
result wrapper `+0x98`, mask `+0xa0`. Eval returns status 6 when unchanged, 0 on success. Query returns 1 for invalid
cache, `0x1f` for unknown id, 0 on success. Producer `bts rbx,rsi` S `0xa2f0f0` / C `0xf0c670` and consumer
`shr rax,cl` both wrap at **64**. Thus simply removing the roster guard aliases index 64 to 0; u32 NPC ids do not
solve it. This is a **derived cache** bound, not proof that save/wire fields need widening. No >64 outcome was tested.

[lab] 5,425 evaluator calls, setter and read-only query forwarding confirmed 16-byte out/return ABI on the existing
35-entry roster, not client or expanded-roster behavior. Use the world's scheduler registry/context; no arbitrary
thread API is proved. Client has two relocated-identical unlock programs: `@quests/NpcUnlock.calls` is ambiguous
there. Common Eval/IsUnlocked/SetUnlocks signatures are unique on S/C and old server `d7baa768…`; use those rather
than treating one copied caller as unique. Full widening design and consumer audit remain in dossier chapter 17.3.

## 15. Direct control, pet survival and native taming (dev.28; server 26430253 / client 26430625)

S `f0d824be…`, C `5d9ef2f1…`. Dossier chapters 19a/19d/19e, riding/pets REPORT deltas and VERIFY. Pets VERIFY:
15 confirmed/two corrected (offer override fields and non-diagnostic constant pet ID). Later safety restart proof is
bounded below. All scheduler/EnemyContext pointers are invocation-local, not tick-call APIs.

### Direct input has a steering owner [static] [refl] [lab]

`void steering_locomotion(ProgramContext*)` S `0x112f00` / C `0x3538a0` writes ActorInput movement `+0x198` from
SteeringAgent.velocity `+0x1c`, including idle zero. AI-off alone does not remove that writer. Tick/program/tracked
input writes and steering-velocity writes produced no movement in the early probes; with SteeringAgent removed,
AI-off sabertooth moved 12.4 m/40 steps, turned toward movement; vultures/dragon climbed in 3D. Tested sabertooth
Sneak/Walk/Run/Sprint speeds .5/2.3/7/10 m/s are **body-specific**, not engine constants.

ActorInput 456 B: movement float3 `+0x198`, look `+0x1b8`, speed byte `+0x1c4`. SteeringAgent 80 B: queryId u32 `+0`,
active `+4`, desiredVelocity float3 `+8`, velocity float3 `+0x1c`, maxSpeed `+0x28`. DynamicLocomotion 456 B:
inputVelocity `+0`, rootMotionMode `+0x58`, state `+0x59`. Locomotion velocity program S `0x1e0db0` / C `0x4414f0`
normalizes full XYZ input for Flying 3/BuildingFlying 11; walking uses horizontal movement. Read/modify through declared
access and queued structural changes, not NativeOfferWorld.Change (that native whitelist accepts only three offer types).

[lab] Riding run 7 restored saved SteeringAgent bytes and AI roamed 10.7 m; a zero queryId was recreated within 20 steps.
The dossier's **restoration safety rule** resets queryId `+0`, desiredVelocity `+8` and velocity `+0x1c` before adding
saved configuration back, avoiding expired query handles/stale movement; absence-at-baseline stays absent. The full
broker/lease contract is a **design**, not an engine API installed by this wave. Do not run direct path/input control
and the existing EngineGuard keeper concurrently. AI-off also does **not abort an active action sequence**; quiescence
or actor retirement is distinct from disabling the next AI decision. Riding/carry/flight details are in [mounts.md](mounts.md).

### Guard, death and revival are different mechanisms [static] [refl] [lab]

Enemy StateDispatch S `0x13a7f0` / C `0x37b100` is `void(EnemyContext*,u8 state,u8 phase)`; enter/execute/exit 0/1/2.
Guard handler S `0x1371e0` / C `0x377af0` is `void(ctx*,u8 phase)`. Context pointers dynamic/template/arsenal
`+0x48/+0x50/+0x58`; tracked records `+8`, template table `+0x28`. DynamicEnemy01BehaviorState 0x4e8 B has guard
entity `+0x454`, distance `+0x468`, next check Time `+0x480`; template 0x9d8 B GuardSetup `+0x598`, DeathSetup `+0x854`,
revive sequence `+0x858`. Start/StopGuardingEvent 16 B has time `+0`, guarding/guarded ids `+8/+0xc`.

Guard target/faction do not confer persistent ownership. OwnerRelationship **exists**, 4 B session owner EntityId
`+0`, not CharacterKey. [lab] Guard bound a synthetic player-template target and keeper catch-up teleported the wolf;
no path following or terrain-safe placement was shown. Existing keeper already retains order, focus, defend/assist
and catch-up; it is not a missing native primitive.

Enemy states Dead 3/Guard 20/Revive 28; **enemy01** StateCondition.Revive 29. Revive case enters the configured sequence
and waits for Actor flags; no owner, cost/channel or health restoration was established there. A healed Skeleton Hound
remained AI Dead after 30 steps despite HP 20, zero Revive transitions. Player Revive 32 B (six float storage values `+8`)
and NetworkReviveData 12 B (state/duration/left `+0/+4/+8`) are not a general pet revive API.

Health>0 clears raw Actor Dead but may still emit **pending EntityDiedEvent**; after-the-mark healing is too late
for a no-death guarantee. See [combat.md](combat.md), pre-death MID S `0x8971a` / C `0x277a1a`, next entity
S `0x89b30` / C `0x277e30`. Oxygen update S `0x59210` / C `0x23c340` writes Health directly, without ApplyHit;
fall infliction also bypasses ApplyHit. Hurt-only clamps miss direct Health 0, drowning and destruction. Injected
50 m/100% fall was canceled/clamped to HP 1; not an actual falling trajectory or universal hazard proof.

Later native safety prototype [lab]: active Vukah swing, pre-death skip retained raw dead/pending clear beyond 8 steps,
outgoing/root and queued post-retirement hits denied, then existing Spawn.Destroy removed actor in same safe Pre;
no later death event/victim loss. ApplyHit S `0x190fb0` / C `0x3efd10` has native propagate-byte early-return seam;
DestroyEntity S `0x63e8a0` / C `0x9a2a10` takes Storage*, entity, mode 1 (real destroy), skipReplication 0 through existing service;
only its low-byte result is used here. It returns a bool in AL (0/1, upper RAX unspecified) on both exes; see
[network.md](network.md) [static]. **AI-off alone was not this protection.**
Dossier's Downed retires combat body/keeps a virtual recovery
anchor; Revive creates a new protected generation. Those lifecycle choices are not claims of a native corpse-revive
mechanic or completed service. Full root-provenance/retirement design stays in chapter 19d.

[lab] Original CharacterData restart tests proved Template/FeedSteps/boot counter, **not logical-ID retention**:
initializer supplied the same constant GUID. Later safety first/restart runs each 23 checks passed with a random ID
and separately persisted Guid.Empty-default witness. This closes synthetic-record identity only; authenticated
character relog/crash durability and cross-store cost atomicity remain unproved.

### Wildlife taming [static] [refl], threshold injection [lab]

| Program `void(ProgramContext*)` | S / C RVA | Role |
|---|---|---|
| wildlife | `0x21f880` / `0x488400` | separate wildlife FSM; can stop when IsTame and activeWhileTamed=false |
| wildlife_farm_animal_transformer | `0x21fe70` / `0x4889f0` | Tameable bit 3 + progress>=required → FarmAnimal 12 and queued IsTame |
| wildlife_enemy_transformer | `0x21fa80` / `0x488600` | hit/no-hit behavior switch, including enemy enabled `+0x498` |
| interaction_taming | `0xa8320` / `0x2aaeb0` | no IsTame, state FoodBaitEat 15, bait!=0, Actor not Dead → offer overrides |

Wildlife 736 B: feature mask `+0x80`, TamingSetup 72 B `+0x1c0`, sequence/verb GUIDs `+0x2c0/+0x2d0`.
TamingSetup: activeWhileTamed `+0`, favoriteFood reference `+4`, required steps byte `+0x14`, sensor range `+0x18`,
eat/alert Times `+0x20/+0x28/+0x30`, approach/flee tuning `+0x38/+0x3c/+0x40`.
DynamicWildlife 176 B: state/last `+0/+1`, eat timer `+0x60`, bait EntityId `+0x88`, sensed/fully-eaten flags
`+0x8c/+0x8d`, progress byte `+0x8e`. NetworkWildlife 24 B: alert `+0`, Time `+8`, progress/eating `+0x10/+0x11`.
IsTame/FoodBait are 1-byte markers; wildlife Dead 12/Pursue 14/Eat 15 are **not enemy state numbers**.

Corrected by VERIFY: taming program hashes template sequence/verb into InteractionOfferRuntime **overrideSequence
`+0xc` / overrideVerbLocaId `+0x14`**, not defaults `+8/+0x10`. It writes no food debit/progress/player ownership.
AnimalSaved 56 B stores needs/times/home, no CharacterKey. Native farm registration S `0x4ecc0` is an inspection
lead, not a qualified call. [lab] Injected dog progress 2 led to IsTame/faction 12, while network progress remained 0:
no natural feeding or client progress proof. Existing dog offer was not replaceable through Offers.TryAdd.

A separate weakened Heavy Wolf fixture consumed three exact-slot custom tokens and saved a synthetic pet record
(12 checks); full-health refusal consumed nothing. Token was not verified native bait; tool wear, natural tamer
attribution, real-player authority and crash-safe cost+ownership commit remain unproved. Byte counters are
representation widths, not validated 255-step gameplay. Full custom taming/protocol designs stay in chapter 19e.
