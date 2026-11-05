# Player state and actions: what the game keeps, and where

Server Steam build 26430253 / client 26430625 (svn 1287369). RVAs are server RVAs unless marked. Sources: the game's reflection read offline with `tools/kfc`, the 652 server and 861 client ECS program descriptors, Ghidra headless decompiles, decoded game data (2,157 sequences, 4,268 items), and a lab with spawned `1_Player` characters and no client. Usage: `docs/kiln/services/player-state.md`. Every state: `docs/kiln/modding/player-states.md`.

Tags: **[refl]** game reflection, **[dis]** decompile or disassembly, **[data]** game resources, **[rt]** seen in the lab, **[hyp]** not proven.

## Short answers
- **The server simulates the player.** `player_control`, `player_control_action_sequence`, `locomotion_state`, `locomotion_execution`, `actor_*` all run in the server world for real players, from replicated `PlayerInput`. So actor state, locomotion and sequences on a dedicated server are the server's own, not a copy of the client's. The client predicts the same programs in its prediction world, except those with flag `0x10` (host only). **[dis]**
- **Nearly every state is a bit or a value of a reflected enum.** Nothing needs a hook to read: `Actor.currentState` (63 bits), `DynamicLocomotion.state` (12 values) and 19 more fields. Table below. **[refl]**
- **Almost every action is an action sequence.** Attacks, casts, jumps, dodges, gliding, grappling, eating, drinking, interacting, emotes, hit reactions, death and respawn all run through `Actor.currentAction`. One poll of `Actor.sequenceRuntimeId` sees them all. **[dis] [data]**
- **Eating is `actor_apply_buff` handling an `ApplyBuffEvent` with `consume`.** No event, function or program is specific to eating. `ItemActionType.Consume` has no handler at all, and `ConsumeItemEvent` is reflected but nothing sends or reads it. **[dis]**
- **A running sequence can be stopped from Pre** with the game's own reset, what it does to a sequence whose keep-alive failed. That is the generic cancel. **[dis] [rt]**

## Where each state lives **[refl]**

| Source | Field | Enum | Kind | Written by |
|---|---|---|---|---|
| Actor flags | `Actor.currentState` +0xBF8, effective with `currentAction.playbackContext.setStateMask` / `unsetStateMask` | `keen::actor::StateFlag` | 63 bits | many programs and every sequence's `SetStateEvent`, table below |
| Locomotion | `DynamicLocomotion.state` +0x59 (`previousState` +0x5A) | `keen::ecs::LocomotionState` | 12 values | `locomotion_state` `0x1e0240`, from the flags |
| Player flags | `PlayerState.playerStateMask` +0x34 | `keen::ecs::PlayerFlags` | 25 bits | `player_control`, `player_control_action_sequence`, `player_sprint`, `player_ui_control` |
| Motion flags | `DynamicLocomotion.flags` +0xF8 | `keen::ecs::LocomotionFlags` | 9 bits | locomotion programs |
| Client UI flags | `PlayerInput.fromClient.mask` | `keen::ecs::ClientPlayerFlags` | 9 bits | the client: replicated input |
| Abilities | `Actor.currentAbilityMask` +0xC00 | `keen::actor::AbilityFlag` | 29 bits | `EnableAbilityEvent` / `DisableAbilityEvent` of sequences |
| Inputs held | `PlayerInput.fromClient.digitalInput` | `keen::ecs::PlayerInputType` | 56 bits | the client: replicated input |
| Action | `Actor.currentAction.triggerContext.triggerType` | `keen::actor::ActionSequenceTrigger` | value | sequences |
| Jump | `DynamicLocomotion.jumpType` +0x154 (`jumpCount` +0x155, `lastJumpTime` +0xB8) | `keen::actor::JumpType` | value | `actor_jump` |
| Speed | `ActorInput.lastLocomotionSpeed` +0x1C5 | `keen::ecs::LocomotionSpeedSelector` | value | `actor_simulation_end` copies `locomotionSpeed` |
| Collider | `Actor.currentColliderMode` +0xEB0 | `keen::ecs::ColliderMode` | value | `actor_collider_from_state` |
| Climb | `Climb.climbState`, `climbType` | `ClimbState`, `ClimbType` | values | `player_locomotion_climb_*` |
| Hook | `HookShot.hookShotState`, `hookShotType` | `HookShotState`, `HookShotType` | values | `hook_shot_*` |
| Ranged | `NetworkRangedWeapon.currentState` | `keen::ranged_weapon::State` | value | `server_player_ranged_weapon` |
| Fishing | `NetworkFishing.state` | `keen::fishing::State` | 29 values | `server_player_fishing` |
| Cooking | `NetworkCookingData.mask` | `keen::ecs::CookingFlags` | 4 bits | `player_cooking` |
| Revive | `NetworkPlayerAttributes.revivieData.state` (the game's spelling) | `keen::ecs::ReviveState` | value | `interaction_player_revive` |
| Environment | `EnvironmentSensor.fogType`, `temperature` | `keen::FogType`, `keen::TemperatureVoxelType` | values | sensors |

Server and client reflection agree on every one of these types, sizes and offsets. **[refl]**

**The effective actor state.** The game's own test (`0x3b7a0`, client `0x21e4f0`): `v = currentState; if (playback.state & 1) v = (v | setStateMask) & ~unsetStateMask`. A sequence's `SetStateEvent` goes into `setStateMask`, so a read of `currentState` alone misses gliding, sitting, dodging. **[dis]**

**The locomotion state is derived from the flags** (`0x1c5180`), in this order: Flying (39), UnderWater (44) and not Attached and `canDive`, FloatingOnWater (43) and not Attached and `canSwim`, HangGliding (37), not Grounded means Falling, then the walking and sliding cases. **[dis]**

### Who writes the actor flags
Survey of every program that writes `Actor` (decompiles), and of `SetStateEvent` / `ClearStateEvent` in the 2,157 sequences. **[dis] [data]**

| Flag | Code | Sequences that set it |
|---|---|---|
| Grounded | `actor_grounded` | 3 |
| Jumping | `actor_jump` | 26 |
| Blocking | `blocking_state_enemy` (enemies) | 4 |
| Dodging | | 11 (`Ability_Movement_Dodge` ...) |
| ParryOportunity, BlockOpportunity | | 166, 69 |
| Exhausted | helper `0x3ce55` in `player_control_action_sequence` | |
| Dead | `death` | 5 |
| Sheltered | `actor_shelter_state`, `npc_shelter` | |
| Sitting | `player_control_action_sequence` | 19 |
| PlayingInstrument | | 15 |
| Sleeping | `animal_behavior` | 1 (`Interaction_Sleep`) |
| Spawning | | 67 |
| InFog | `fog_resistance_update` | |
| InSaveZone | `player_update_insave_zone` | |
| Warm | `warmth` | |
| Wet, Soaked | `actor_wet_soaked_cleanup` | |
| Cold, Freezing | `environmental_hazards` | |
| InSneakMode | `actor_water_locomotion`, `actor_handle_sequence_playback_end`, cleared by `actor_simulation_end` | 11 |
| Unhappy | `wildlife` (not players) | |
| HasAggro | `post_enemy_combat_targets` (enemies) | |
| InCombat | `actor_in_combat`, `set_cluster_combat_state` | |
| InRangeCombat, ...Preparing, ...Shooting | `server_player_ranged_weapon` | 27 |
| HeavyAttack | | 6 |
| Cooking | `player_cooking` | |
| HangGliding | | 3 (`Movement_Tool_HangGlider_Start` ...) |
| AttachedToHook | `hook_pull_impulse` | 3 |
| FloatingOnWater, UnderWater | `actor_water_locomotion` | |
| Suffocating | `oxygen_update` | |
| Invisible | `actor_simulation_start`, others | 117 |
| IsReadyForGameplay | `actor_is_ready_for_gameplay` | |
| Strafing | `strafing_state_player`, `server_player_ranged_weapon` | 14 |
| IsNearBuildingZone | `network_cursor` (bit 2 of `ServerCursor.serverFlags`) | |
| IsSequenceTriggerInputActive | `base_player_action` | |
| IsChargingFishingRod, IsFishing, IsHoldingFish | `server_player_fishing` | |
| Others | not traced | |

## How an action starts and ends **[dis]**
Program order inside a step, from `RegisterAllPrograms` `0x245960`:
```
37 player_control_not_predicted   emotes
38 player_control
179 player_control_action_sequence  player input to a trigger, TriggerSequence 0x369d0 and its siblings
239 actor_simulation_start         takes ActorInput.triggerContext, starts the sequence in Actor.currentAction (0x36e80)
240 actor_clear_input              clears ActorInput.triggerContext
241-262 actor_basics .. actor_apply_buff, actor_spawn_entity, actor_pay_usage_cost ..  walk this step's window of events
262 actor_simulation_end
331 actor_to_network               Actor to NetworkActor
```
So a trigger is written and consumed inside one step; Kiln's Pre never sees a pending one. Kiln's Pre sees the started sequence in `Actor.currentAction` after its first window of events ran. An event at a time past the first step has not run yet.

`Actor.sequenceRuntimeId` +0xC08 `{sequenceId, triggerTime +8, actorEntityId +0x10, callIndex +0x14}` identifies one start. `Actor.currentAction.triggerContext` keeps what started it: `triggerType` +0x18, `inputType` +0x1A, `costItem` +0x1C, `usedItem` +0x30, `usedAmmunition` +0x44, `jumpType` +0x58, `interactionHostId` +0x78.

**Stopping one.** `0x34040` (client `0x2170a0`) `void (Actor*)`: clears `currentAction` (0xBF8 bytes), resets the ability mask to `0x23c4000`, keeps the SelfDestruct bit, clears `sequenceRuntimeId` and `currentServerAction`. `0x36e80` has the same code inline for a failed keep-alive. Called in Pre, with no job running, the sequence is gone at the next step and `actor_to_network` sends the empty runtime id to clients. **[dis] [rt]**
A sibling at `0x33f30` does the same without clearing `currentServerAction`; the signature takes the longer one.

**What a sequence's name comes from.** `0x932930(ActionSequenceRegistry*, sequenceId, subIndex)` returns the `ActorSubSequenceResource` (72 B), `name` a BlobString at +0x14. Server and client have the same data. **[dis] [rt]**

## Eating, drinking and other consumables **[dis] [data]**
There is no "eat" in the code. `actor_apply_buff` (`0x75bc0`, flags `0x210`: host only), for each `ApplyBuffEvent` (hash `0xf02be4c8`) in the step's window:
```
item = ItemRegistryFind(Actor.currentAction.triggerContext.usedItem.itemId)     (+0x38)
buff = event.buffTypeOverride, else item.equipment.appliedBuff (+0x414)
if no buff: nothing happens
if event.consume (+0x70):
    InventoryTx init; removeItems(tx, self, item, usedItem.slotId, 1)           0x1b0d90
    ok: applyBuffCore(self, self, ..)                                           0x15e130
        error: rollback 0x1b8310, else commit 0x1af860
else:
    applyBuffCore
```
The consumables, over 222 items **[data]**:

| Sub-sequence | Items | Consuming event | At |
|---|---|---|---|
| `UseItem_Food_Buff` | 109 | ApplyBuffEvent | 0.40 s |
| `UseItem_Food_Liquid_Buff` | 32 | ApplyBuffEvent | 0.05 s |
| `UseItem_Drop_Fish_Into_World` | 15 | SpawnEntityEvent | 0.15 s |
| `UseItem_Potion_Health` | 12 | SpawnImpact | 0.30 s |
| `UseItem_Potion_Mana` | 10 | SpawnEntityEvent | 0.30 s |
| `UseItem_Food_Bad` | 9 | ApplyBuffEvent | 0.40 s |
| `UseItem_Food_Heal_Buff` | 7 | SpawnImpact | 0.60 s |
| `UseItem_Bandage_buff` | 4 | ApplyBuffEvent | 0.50 s |
| Antidotes, lotions, flasks, scrolls | 14 | various | 0 to 0.48 s |

`UseItem_Scroll_Meteor`'s consume is at time 0: it has run before Kiln sees the sequence, so a cancel of that one scroll comes too late for the item. Everything else has at least 0.05 s, three steps at 60 Hz, two at the 24 Hz of an idle server.

Food and drink are told apart only by the sequence: the game treats a drink as food (same buff slot, `BuffSlotType.Food`). Items trigger through `ItemInfo.sequences[].triggeredBy = Primary`. **[data]**

`ItemActionType.Consume` (1) in `inventory_actions` (`0x1a5af0`): the dispatch handles 0, 2, 3, 4, 5; a 1 jumps past the code that marks the action consumed. `keen::ecs::ConsumeItemEvent` has no reference to its hash in either exe. **[dis]**

## Vitals and equipment **[refl] [dis]**
`NetworkPlayerAttributes` (256 B, `network_player_attributes`, flags 0) holds, one step old, as `u16`: `health`, `healthMax`, `stamina`, `staminaMax`, `mana`, `manaMax`, `bodyHeat`, `bodyHeatMax`, `specialAbility`, `specialAbilityMax`, `fogResistance`, `fogResistanceMax`, `oxygen`, `oxygenMax`, `level`, `comfortLevel`, and much more. One component read gives all of it. Who else it replicates to, besides its owner, was not traced **[hyp: owner only]**.

`BuffContainer` (472 B, server only): `foodBuffs[4]`, `genericBuffs[16]`, `genericDebuffs[16]`, `restedBuff`, `terrainDebuff`, each `{buffEntityId, typeId, cleansed}`. The buff entity has `BuffNetworkData {lifeTime, endOfLife, buffTypeId, level}`. A food slot does not mean food: `thirst_and_hunger` (`0x5ddd0`) keeps its own marker `0x89cb0b34` in a slot while a character has eaten nothing, and a spawned character starts with it. **[dis] [rt]**

`SlotSelection` (216 B): `equipment` = 20 `InventorySlotId {u32 inventory entity, u8 index}`, `actionbarSlotSelection.index` the selected bar slot, 0..15: slot `index & 7` of `actionbarEntityIds[index >> 3]`. `actionItem` is the running action's used item, copied each step by `player_control_action_sequence`, and wins over the selection (`0x1444a0` with `EquipmentSlot.MainHand`). A slot's item is in that inventory entity's `Inventory` component, 12 bytes per slot. `HandleEquipmentSlotChangedEvent` exists but is only sent from `player_control_action_sequence`; polling the items is simpler and covers every path.

## Bases and the Shroud **[dis] [rt]**
Being in a base is not a flag: `IsNearBuildingZone` is about the cursor (`ServerCursor.serverFlags` bit 2). Kiln tests the player's position against `BuildZoneSystem` list 0 through kiln.build (`docs/re/build.md` 7).

The Shroud is `InFog` (`fog_resistance_update` `0xc1c70`), with `EnvironmentSensor.fogType` (None, NoneTemporary, Dangerous, Death) saying which. `fogResistance` is the timer in seconds: `Dangerous` takes 1 a second, `Death` 100 a second, and it recovers 25 a second outside. A level 1 character has 420. **[dis] [rt]**

## What a client sends **[refl] [dis]**
31 versioned actions in `PlayerInput.fromClient` and 14 in `fromAdminClient`: every field whose struct has a `versionData`. A program compares it with the field of the same name in `ServerConsumedPlayerInput` (`consumedChangeEquipmentAction` ...), runs the action once when they differ, and stores it. No program writes `PlayerInput`, so a version is visible in Pre before its program runs, and writing it to the consumed field makes the program skip it. **[dis]** Lab: a fake `changeEquipmentAction` equips a glider through `equipment_actions` on a character with no client; the same action with its version marked consumed does nothing. **[rt]** Three admin ones are named differently (`adminSpawnCommand` and `consumedAdminSpawnAction`, `adminKillFaction` and `consumedAdminKillFactionAction`, `adminDebugEnemyAction` and `consumedDebugEnemyAction`).

## A character with no client **[dis] [rt]**
`actor_is_ready_for_gameplay` sets `IsReadyForGameplay` only when `PlayerInput.fromClientIsReadyForGameplay` (+0x5B8) is set and the terrain at the character is loaded (`0x176670`). Force those two on a spawned `1_Player` and the game's own control programs act on the input bits in `fromClient.digitalInput`, so the input-to-action path runs in a lab:
```
real input InputGlider: actions [Movement_Tool_HangGlider_Start ActionGlider item Tool_Glider_T1], states [+Gliding -LocomotionFalling +LocomotionHangGliding +ActionGlider ...]
real input InputJump: jumps [JumpDefault], states [-Grounded +Jumping -LocomotionWalking +LocomotionFalling +JumpDefault +Airborne +Falling ...]
real input InputEvade: actions [Ability_Movement_Dodge ActionEvade item (none)], states [+Dodging ...]
```
The glider comes from the equipment slot, as the code says (`0x1444a0` with `EquipmentSlot.Glider`). Item use from the main action and quick use keys did not start on this character: a gate in `0xb1410` stays closed, and which one was not traced. Consumption after the trigger was run instead by writing the used item into the trigger context as the game does: 5 mushrooms to 4 through `actor_apply_buff`, the buff in the food slot.

## Signatures
`sigdb/symbols/server.d/players.json`, unique on server 26430253 and 26404334 and client 26430625:

| Symbol | Server | Client | Feature |
|---|---|---|---|
| `players/TypeByHash` | `0x7e15d0` | `0x8de860` | `players.reflection` |
| `players/ActionReset` | `0x34040` | `0x2170a0` | `players.action_cancel` |
| `players/SubSequence` | `0x932930` | `0xe8b7f0` | `players.sequence_names` |

Used from other files when present: `GetComponent`, `GetEntityTransform`, `items/ItemRegistryFind`, `gamedata/GetBuffType`. Field offsets and names come from reflection at run time, so a moved field needs no signature change. A symbol that does not resolve turns that one feature off, with a line in kiln.log.

## Open questions
- Which gate keeps item use from starting on a character with no client (`0xb1410`). A real client passes it.
- What a client shows when the server cancels a sequence it predicted (manual test).
- Whether `Actor` and `DynamicLocomotion` in a joined client's prediction world always hold the local player, and how far they run ahead of the server (manual test).
- Which writer sets `Blocking` for players outside the four sequences (none found in the code).
