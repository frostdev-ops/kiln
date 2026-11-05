# Every player state and action

What the game has, read from its own reflection with `tools/typegen/playerstates.py`. Client 26430625 / server 26430253: the two agree. How to use them: [../services/player-state.md](../services/player-state.md). Evidence: `docs/re/player-state.md`.

**Everything in the state table is exposed**: `player.Is(PlayerStateId.X)`, and `PlayerStateChangedEvent` when it changes. `PlayerStates.All` gives the same list from the running game, so it also has what a newer game adds. A value is `source << 8 | raw`.

A few states never turn on for a player, because the game only uses them for creatures: `Unhappy` (wildlife), `HasAggro`, `EnemyPrepareAttack`. The `Ability` source says what the running sequence allows. `ClientUi` and `Input` come from the client, so a modified client can send anything there.

A bit is on while the state lasts. A value source has one value at a time; entering one leaves the other. Values named `Invalid` or `None` mean "no state": they raise no event of their own.

## Shortcuts
| Say | The game's | `Player` member |
|---|---|---|
| `Gliding` | `HangGliding` | `IsGliding` |
| `Airborne` | worked out: locomotion Falling, HangGliding or Flying | `IsAirborne` |
| `Swimming` / `Diving` | `LocomotionSwimming` / `LocomotionDiving` | `IsSwimming` / `IsDiving` |
| `Underwater` | `UnderWater` | `IsUnderwater` |
| `Climbing`, `ClimbingWall`, `ClimbingLadder` | `Climbing`, the locomotion values | `IsClimbing` |
| `Sprinting` | `IsSprinting` | `IsSprinting` |
| `Sneaking` | `InSneakMode` | `IsCrouching` |
| `Grappling` | `AttachedToHook` | `IsGrappling` |
| `Attacking`, `Casting`, `UsingItem` | worked out: the running action's trigger and item | `IsAttacking`, `IsCasting`, `IsUsingItem` |
| `InShroud` | `InFog` | `IsInShroud` |
| `InSafeZone` | `InSaveZone` | |
| `InBase` | worked out: inside a flame altar's build zone | `IsInBase` |
| `Rested`, `Fed`, `Hungry`, `HasDebuff` | worked out: the game's buff slots. `Hungry` is the game's own hunger marker in a food slot, which is why a food slot being used does not mean fed | |

## Every state
Generated: `python3 tools/typegen/playerstates.py --exe <enshrouded.exe> --table`.

| State | Game name | Raw | Note |
|---|---|---|---|
| **Actor** | `keen::actor::StateFlag` in `Actor.currentState` | bit | |
| `Grounded` | Grounded | 0 | On the ground. Program actor_grounded |
| `Jumping` | Jumping | 1 | Rising from a jump. Program actor_jump, sequences |
| `Blocking` | Blocking | 2 | Holding a block. Sequences; enemies: blocking_state_enemy |
| `Dodging` | Dodging | 3 | In a dodge. Sequence Ability_Movement_Dodge and its variants |
| `ParryOportunity` | ParryOportunity | 4 | A parry window of the attacker's sequence (sequence data, the game's spelling) |
| `BlockOpportunity` | BlockOpportunity | 5 | A block window (sequence data) |
| `Exhausted` | Exhausted | 6 | Out of stamina. Set in player_control_action_sequence |
| `Dead` | Dead | 7 | Program death; cleared when health is back |
| `Sheltered` | Sheltered | 8 | Under a roof. Program actor_shelter_state |
| `Sitting` | Sitting | 9 | Sitting on a seat. player_control_action_sequence, sequences |
| `PlayingInstrument` | PlayingInstrument | 10 | Sequences of the instruments |
| `Sleeping` | Sleeping | 11 | Sequence Interaction_Sleep |
| `Spawning` | Spawning | 12 | Not ready yet after a spawn or respawn |
| `InFog` | InFog | 13 | Standing in the Shroud. Program fog_resistance_update |
| `InSaveZone` | InSaveZone | 14 | Near the flame safe zone. Program player_update_insave_zone (host only) |
| `Mining` | Mining | 15 | Sequences of mining tools |
| `Warm` | Warm | 16 | Near warmth. Program warmth |
| `Wet` | Wet | 17 | Programs actor_wet_soaked_* |
| `Soaked` | Soaked | 18 | Programs actor_wet_soaked_* |
| `Cold` | Cold | 19 | Program environmental_hazards |
| `Freezing` | Freezing | 20 | Program environmental_hazards |
| `InSneakMode` | InSneakMode | 21 | Crouching. actor_water_locomotion and sequences |
| `Unhappy` | Unhappy | 22 | Wildlife only (program wildlife); never set on players |
| `HasAggro` | HasAggro | 23 | Enemies only (post_enemy_combat_targets) |
| `InCombat` | InCombat | 24 | Programs actor_in_combat, set_cluster_combat_state |
| `InRangeCombat` | InRangeCombat | 25 | Bow or staff out. Program server_player_ranged_weapon |
| `InRangeCombatPreparing` | InRangeCombatPreparing | 26 | Program server_player_ranged_weapon |
| `InRangeCombatShooting` | InRangeCombatShooting | 27 | Program server_player_ranged_weapon |
| `InRangeCombatKeepTarget` | InRangeCombatKeepTarget | 28 |  |
| `HeavyAttack` | HeavyAttack | 29 | Sequences of heavy attacks |
| `InSnappingLerp` | InSnappingLerp | 30 |  |
| `Cooking` | Cooking | 31 | At a cooking fire. Program player_cooking |
| `Falling` | Falling | 32 | Sequences; the locomotion state is the live one |
| `HeadDiving` | HeadDiving | 33 | Sequence Ability_Movement_Jump_HeadDive |
| `Climbing` | Climbing | 34 | On a wall or ladder |
| `ClimbingTransition` | ClimbingTransition | 35 |  |
| `Attached` | Attached | 36 |  |
| `HangGliding` | HangGliding | 37 | Gliding. Sequence Movement_Tool_HangGlider_Start and its variants |
| `AttachedToHook` | AttachedToHook | 38 | On a grappling hook line. Sequence Movement_Tool_HookPull_Self |
| `Flying` | Flying | 39 |  |
| `InTurnSequence` | InTurnSequence | 40 |  |
| `InStopSequence` | InStopSequence | 41 |  |
| `InStartSequence` | InStartSequence | 42 |  |
| `FloatingOnWater` | FloatingOnWater | 43 | Swimming at the surface. Program actor_water_locomotion |
| `UnderWater` | UnderWater | 44 | Diving. Program actor_water_locomotion |
| `Suffocating` | Suffocating | 45 | Out of oxygen. Program oxygen_update |
| `CanPayUsageCosts` | CanPayUsageCosts | 46 |  |
| `Invisible` | Invisible | 47 | Program actor_simulation_start, sequences |
| `EnemyPrepareAttack` | EnemyPrepareAttack | 48 |  |
| `IsAnythingAbove` | IsAnythingAbove | 49 | Program check_line_above_sensor |
| `IsReadyForGameplay` | IsReadyForGameplay | 50 | Program actor_is_ready_for_gameplay |
| `IsActionOpportunityTriggered` | IsActionOpportunityTriggered | 51 |  |
| `WantsToMove` | WantsToMove | 52 |  |
| `Strafing` | Strafing | 53 | Program strafing_state_player |
| `IsNearBuildingZone` | IsNearBuildingZone | 54 | The cursor's server flag: near a build zone. Program network_cursor |
| `HandsFree` | HandsFree | 55 |  |
| `IsWorking` | IsWorking | 56 |  |
| `Petting` | Petting | 57 |  |
| `IsSequenceTriggerInputActive` | IsSequenceTriggerInputActive | 58 | The input that started the sequence is still held. Program base_player_action |
| `IsChargingFishingRod` | IsChargingFishingRod | 59 | Program server_player_fishing |
| `IsFishing` | IsFishing | 60 | Program server_player_fishing |
| `IsHoldingFish` | IsHoldingFish | 61 | Program server_player_fishing |
| `FreezeLeaningDirection` | FreezeLeaningDirection | 62 |  |
| **Locomotion** | `keen::ecs::LocomotionState` in `DynamicLocomotion.state` | value | |
| `LocomotionWalking` | Walking | 0 |  |
| `LocomotionSliding` | Sliding | 1 |  |
| `LocomotionFalling` | Falling | 2 | Derived by the game: not Grounded |
| `LocomotionFlying` | Flying | 3 |  |
| `LocomotionHangGliding` | HangGliding | 4 | Derived by the game from HangGliding |
| `LocomotionHookPulling` | HookPulling | 5 |  |
| `LocomotionHookSwinging` | HookSwinging | 6 |  |
| `LocomotionClimbingWall` | ClimbingWall | 7 |  |
| `LocomotionClimbingLadder` | ClimbingLadder | 8 |  |
| `LocomotionSwimming` | Swimming | 9 | Derived by the game from FloatingOnWater, and canSwim |
| `LocomotionDiving` | Diving | 10 | Derived by the game from UnderWater, and canDive |
| `LocomotionBuildingFlying` | BuildingFlying | 11 |  |
| **Player** | `keen::ecs::PlayerFlags` in `PlayerState.playerStateMask` | bit | |
| `IsSpawned` | IsSpawned | 0 |  |
| `InFullScreenUi` | InFullScreenUi | 1 | A full screen menu is open (replicated from the client) |
| `UiCoverRequest` | UiCoverRequest | 2 |  |
| `CanHeadDiveHere` | CanHeadDiveHere | 3 |  |
| `CanTriggerHeadDive` | CanTriggerHeadDive | 4 |  |
| `CanTriggerHookShot` | CanTriggerHookShot | 5 |  |
| `CanTriggerGlider` | CanTriggerGlider | 6 |  |
| `CanAreaLootHere` | CanAreaLootHere | 7 |  |
| `IsSkyDiving` | IsSkyDiving | 8 | Falling fast toward a head dive |
| `InBlueprintSnappingMode` | InBlueprintSnappingMode | 9 |  |
| `InVoxelGridSnappingMode` | InVoxelGridSnappingMode | 10 |  |
| `WantsToSprint` | WantsToSprint | 11 |  |
| `WantsToWalk` | WantsToWalk | 12 |  |
| `IsSprinting` | IsSprinting | 13 | Program player_sprint |
| `InTargetLockCombatMode` | InTargetLockCombatMode | 14 |  |
| `QueueFireRangedShot` | QueueFireRangedShot | 15 |  |
| `HasRangedWeaponFeedbackBeenSent` | HasRangedWeaponFeedbackBeenSent | 16 |  |
| `IsOutsideSafeZone` | IsOutsideSafeZone | 17 | The death-respawn safe zone timer runs |
| `IsInBigDungeon` | IsInBigDungeon | 18 |  |
| `IsInDeepSubmergeMaterial` | IsInDeepSubmergeMaterial | 19 |  |
| `HideHelmet` | HideHelmet | 20 |  |
| `HideUpperbodyArmour` | HideUpperbodyArmour | 21 |  |
| `HideLowerbodyArmour` | HideLowerbodyArmour | 22 |  |
| `HideFeetArmour` | HideFeetArmour | 23 |  |
| `HideGloves` | HideGloves | 24 |  |
| **Motion** | `keen::ecs::LocomotionFlags` in `DynamicLocomotion.flags` | bit | |
| `MotionLveRunning` | LveRunning | 0 |  |
| `MotionSprinting` | Sprinting | 1 |  |
| `MotionWadingInWater` | WadingInWater | 2 |  |
| `MotionOffBalance` | OffBalance | 3 |  |
| `MotionMovingTowardsHole` | MovingTowardsHole | 4 |  |
| `MotionAirwolfActive` | AirwolfActive | 5 |  |
| `MotionIsUpdraftDepleted` | IsUpdraftDepleted | 6 |  |
| `MotionWantsToMove` | WantsToMove | 7 |  |
| `MotionOnVoxelStairs` | OnVoxelStairs | 8 |  |
| **ClientUi** | `keen::ecs::ClientPlayerFlags` in `PlayerInput.fromClient.mask` | bit | |
| `ClientUiIsInMultiToolBuildingMenu` | IsInMultiToolBuildingMenu | 0 |  |
| `ClientUiIsInBlueprintMenu` | IsInBlueprintMenu | 1 |  |
| `ClientUiIsInLogicScrewdriverMode` | IsInLogicScrewdriverMode | 2 |  |
| `ClientUiIsInLootingMenu` | IsInLootingMenu | 3 |  |
| `ClientUiIsInFreeCam` | IsInFreeCam | 4 |  |
| `ClientUiIsInFirstPerson` | IsInFirstPerson | 5 |  |
| `ClientUiIsInBuildingDetailTranslationMode` | IsInBuildingDetailTranslationMode | 6 |  |
| `ClientUiIsInBuildingDetailRotationMode` | IsInBuildingDetailRotationMode | 7 |  |
| `ClientUiIsLockCombatTargetBindingEnabled` | IsLockCombatTargetBindingEnabled | 8 |  |
| **Ability** | `keen::actor::AbilityFlag` in `Actor.currentAbilityMask` | bit | |
| `AbilityCanTriggerPrimary` | CanTriggerPrimary | 0 |  |
| `AbilityCanTriggerHeavy` | CanTriggerHeavy | 1 |  |
| `AbilityCanTriggerSecondary` | CanTriggerSecondary | 2 |  |
| `AbilityCanTriggerSpecialAbility` | CanTriggerSpecialAbility | 3 |  |
| `AbilityCanRangedCombatShoot` | CanRangedCombatShoot | 4 |  |
| `AbilityCanUseTool` | CanUseTool | 5 |  |
| `AbilityCanEvade` | CanEvade | 6 |  |
| `AbilityCanBlock` | CanBlock | 7 |  |
| `AbilityCanUseLongerStunParry` | CanUseLongerStunParry | 8 |  |
| `AbilityCanJump` | CanJump | 9 |  |
| `AbilityCanTriggerGlider` | CanTriggerGlider | 10 |  |
| `AbilityCanInteract` | CanInteract | 11 |  |
| `AbilityCanSprint` | CanSprint | 12 |  |
| `AbilityCanSneak` | CanSneak | 13 |  |
| `AbilityCanStandUp` | CanStandUp | 14 |  |
| `AbilityCanSit` | CanSit | 15 |  |
| `AbilityCanClimb` | CanClimb | 16 |  |
| `AbilityCanTriggerStunBreak` | CanTriggerStunBreak | 17 |  |
| `AbilityCanExternallyAbortSequence` | CanExternallyAbortSequence | 18 |  |
| `AbilityCanAbortSequenceByDeath` | CanAbortSequenceByDeath | 19 |  |
| `AbilityCanShowHitReaction` | CanShowHitReaction | 20 |  |
| `AbilityCanChangeCombatTargetOrHardLockCandidate` | CanChangeCombatTargetOrHardLockCandidate | 21 |  |
| `AbilityCanChangeVisceralTarget` | CanChangeVisceralTarget | 22 |  |
| `AbilityCanChangeInteractionTarget` | CanChangeInteractionTarget | 23 |  |
| `AbilityCanChangeBuildingCursor` | CanChangeBuildingCursor | 24 |  |
| `AbilityCanRecharge` | CanRecharge | 25 |  |
| `AbilityCanMove` | CanMove | 26 |  |
| `AbilityCanRotate` | CanRotate | 27 |  |
| `AbilityCanCancel` | CanCancel | 28 |  |
| **Input** | `keen::ecs::PlayerInputType` in `PlayerInput.fromClient.digitalInput` | bit | |
| `InputMainhandAction` | MainhandAction | 0 |  |
| `InputMainhandAction_Tap` | MainhandAction_Tap | 1 |  |
| `InputMainhandAction_Hold` | MainhandAction_Hold | 2 |  |
| `InputMainhandAction_Release` | MainhandAction_Release | 3 |  |
| `InputSecondaryBuildingAction` | SecondaryBuildingAction | 4 |  |
| `InputContextualAction` | ContextualAction | 5 |  |
| `InputContextualAction_Tap` | ContextualAction_Tap | 6 |  |
| `InputContextualAction_Hold` | ContextualAction_Hold | 7 |  |
| `InputAreaLoot` | AreaLoot | 8 |  |
| `InputSpecialAbility` | SpecialAbility | 9 |  |
| `InputSpecialAbility_Tap` | SpecialAbility_Tap | 10 |  |
| `InputSpecialAbility_Hold` | SpecialAbility_Hold | 11 |  |
| `InputDropItem` | DropItem | 12 |  |
| `InputCancelAction` | CancelAction | 13 |  |
| `InputQuickUse0` | QuickUse0 | 14 |  |
| `InputQuickUse1` | QuickUse1 | 15 |  |
| `InputQuickUse2` | QuickUse2 | 16 |  |
| `InputQuickUse3` | QuickUse3 | 17 |  |
| `InputQuickUse4` | QuickUse4 | 18 |  |
| `InputQuickUse5` | QuickUse5 | 19 |  |
| `InputQuickUse6` | QuickUse6 | 20 |  |
| `InputQuickUse7` | QuickUse7 | 21 |  |
| `InputQuickUse0_Release` | QuickUse0_Release | 22 |  |
| `InputQuickUse1_Release` | QuickUse1_Release | 23 |  |
| `InputQuickUse2_Release` | QuickUse2_Release | 24 |  |
| `InputQuickUse3_Release` | QuickUse3_Release | 25 |  |
| `InputQuickUse4_Release` | QuickUse4_Release | 26 |  |
| `InputQuickUse5_Release` | QuickUse5_Release | 27 |  |
| `InputQuickUse6_Release` | QuickUse6_Release | 28 |  |
| `InputQuickUse7_Release` | QuickUse7_Release | 29 |  |
| `InputBlock` | Block | 30 |  |
| `InputEvade` | Evade | 31 |  |
| `InputJump` | Jump | 32 |  |
| `InputHeadDive` | HeadDive | 33 |  |
| `InputSwimStartDiving` | SwimStartDiving | 34 |  |
| `InputDiveAscend` | DiveAscend | 35 |  |
| `InputDiveDescend` | DiveDescend | 36 |  |
| `InputGlider` | Glider | 37 |  |
| `InputRespawn` | Respawn | 38 |  |
| `InputBuildingModeFly` | BuildingModeFly | 39 |  |
| `InputBuildingUndo` | BuildingUndo | 40 |  |
| `InputBuildingBlueprintSnapping` | BuildingBlueprintSnapping | 41 |  |
| `InputBuildingVoxelGridSnapping` | BuildingVoxelGridSnapping | 42 |  |
| `InputLockCombatTarget` | LockCombatTarget | 43 |  |
| `InputCombatTargetSelect1dLeft` | CombatTargetSelect1dLeft | 44 |  |
| `InputCombatTargetSelect1dRight` | CombatTargetSelect1dRight | 45 |  |
| `InputCombatTargetSelect2dLeft` | CombatTargetSelect2dLeft | 46 |  |
| `InputCombatTargetSelect2dRight` | CombatTargetSelect2dRight | 47 |  |
| `InputCombatTargetSelect2dUp` | CombatTargetSelect2dUp | 48 |  |
| `InputCombatTargetSelect2dDown` | CombatTargetSelect2dDown | 49 |  |
| `InputRangedCombatArm` | RangedCombatArm | 50 |  |
| `InputRangedCombatShoot` | RangedCombatShoot | 51 |  |
| `InputRangedCombatShoot_Tap` | RangedCombatShoot_Tap | 52 |  |
| `InputSprint` | Sprint | 53 |  |
| `InputWalk` | Walk | 54 |  |
| `InputSneak` | Sneak | 55 |  |
| **Action** | `keen::actor::ActionSequenceTrigger` in `Actor.currentAction.triggerContext.triggerType` | value | |
| `ActionInvalid` | Invalid | 0 |  |
| `ActionPrimary` | Primary | 1 |  |
| `ActionPrimary_Tap` | Primary_Tap | 2 |  |
| `ActionPrimary_Hold` | Primary_Hold | 3 |  |
| `ActionHeavy` | Heavy | 4 |  |
| `ActionSecondary` | Secondary | 5 |  |
| `ActionTool` | Tool | 6 |  |
| `ActionEvade` | Evade | 7 |  |
| `ActionBlock` | Block | 8 |  |
| `ActionParry` | Parry | 9 |  |
| `ActionJump` | Jump | 10 |  |
| `ActionGlider` | Glider | 11 |  |
| `ActionInteract` | Interact | 12 |  |
| `ActionInteractHold` | InteractHold | 13 |  |
| `ActionDismantle` | Dismantle | 14 |  |
| `ActionSpecialAbility` | SpecialAbility | 15 |  |
| `ActionDropItem` | DropItem | 16 |  |
| `ActionSneak` | Sneak | 17 |  |
| `ActionSit` | Sit | 18 |  |
| `ActionClimb` | Climb | 19 |  |
| `ActionHitReaction` | HitReaction | 20 |  |
| `ActionRespawn` | Respawn | 21 |  |
| `ActionExternalTrigger` | ExternalTrigger | 22 |  |
| `ActionParried` | Parried | 23 |  |
| `ActionExternalAbortAndOverride` | ExternalAbortAndOverride | 24 |  |
| `ActionDeath` | Death | 25 |  |
| `ActionParriedOverride` | ParriedOverride | 26 |  |
| **Jump** | `keen::actor::JumpType` in `DynamicLocomotion.jumpType` | value | |
| `JumpInvalid` | Invalid | 0 |  |
| `JumpDefault` | Default | 1 |  |
| `JumpDoubleDefault` | DoubleDefault | 2 |  |
| `JumpDoubleSprint` | DoubleSprint | 3 |  |
| `JumpDoubleMountain` | DoubleMountain | 4 |  |
| `JumpSprint` | Sprint | 5 |  |
| `JumpDefaultSubmerged` | DefaultSubmerged | 6 |  |
| `JumpStruggleSubmerged` | StruggleSubmerged | 7 |  |
| `JumpGlider` | Glider | 8 |  |
| `JumpHookSwingEnter` | HookSwingEnter | 9 |  |
| `JumpHookSwingDefaultExit` | HookSwingDefaultExit | 10 |  |
| `JumpHookSwingSprintExit` | HookSwingSprintExit | 11 |  |
| `JumpLadderClimbing` | LadderClimbing | 12 |  |
| `JumpFreeClimbing` | FreeClimbing | 13 |  |
| `JumpMountain` | Mountain | 14 |  |
| `JumpSlide` | Slide | 15 |  |
| `JumpAttack` | Attack | 16 |  |
| `JumpAirwolf` | Airwolf | 17 |  |
| `JumpNpcMovement` | NpcMovement | 18 |  |
| `JumpJumpPad` | JumpPad | 19 |  |
| **Speed** | `keen::ecs::LocomotionSpeedSelector` in `ActorInput.lastLocomotionSpeed` | value | |
| `SpeedZero` | Zero | 0 |  |
| `SpeedSneak` | Sneak | 1 |  |
| `SpeedWalk` | Walk | 2 |  |
| `SpeedRun` | Run | 3 |  |
| `SpeedSprint` | Sprint | 4 |  |
| `SpeedSwim` | Swim | 5 |  |
| `SpeedDive` | Dive | 6 |  |
| `SpeedSwimSlow` | SwimSlow | 7 |  |
| `SpeedDiveSlow` | DiveSlow | 8 |  |
| `SpeedSwimSprint` | SwimSprint | 9 |  |
| `SpeedDiveSprint` | DiveSprint | 10 |  |
| `SpeedCombat` | Combat | 11 |  |
| `SpeedPatrol` | Patrol | 12 |  |
| `SpeedFlee` | Flee | 13 |  |
| `SpeedCustom` | Custom | 14 |  |
| **Collider** | `keen::ecs::ColliderMode` in `Actor.currentColliderMode` | value | |
| `ColliderDefault` | Default | 0 |  |
| `ColliderSitting` | Sitting | 1 |  |
| `ColliderLying` | Lying | 2 |  |
| `ColliderEvading` | Evading | 3 |  |
| `ColliderClimbingWall` | ClimbingWall | 4 |  |
| `ColliderDeath` | Death | 5 |  |
| `ColliderInvisible` | Invisible | 6 |  |
| `ColliderSwimming` | Swimming | 7 |  |
| `ColliderDivingAlive` | DivingAlive | 8 |  |
| `ColliderDivingDeath` | DivingDeath | 9 |  |
| `ColliderFlying` | Flying | 10 |  |
| **Climb** | `keen::ecs::ClimbState` in `Climb.climbState` | value | |
| `ClimbInvalid` | Invalid | 0 |  |
| `ClimbEnter` | Enter | 1 |  |
| `ClimbClimb` | Climb | 2 |  |
| `ClimbExit` | Exit | 3 |  |
| **ClimbType** | `keen::ecs::ClimbType` in `Climb.climbType` | value | |
| `ClimbTypeInvalid` | Invalid | 0 |  |
| `ClimbTypeWall` | Wall | 1 |  |
| `ClimbTypeLadder` | Ladder | 2 |  |
| **Hook** | `keen::ecs::HookShotState` in `HookShot.hookShotState` | value | |
| `HookInvalid` | Invalid | 0 |  |
| `HookEnter` | Enter | 1 |  |
| `HookUpdate` | Update | 2 |  |
| `HookExit` | Exit | 3 |  |
| **HookType** | `keen::ecs::HookShotType` in `HookShot.hookShotType` | value | |
| `HookTypeNone` | None | 0 |  |
| `HookTypePullToggleEntity` | PullToggleEntity | 1 |  |
| `HookTypePullTarget` | PullTarget | 2 |  |
| `HookTypePullSelf` | PullSelf | 3 |  |
| `HookTypeSwingSelf` | SwingSelf | 4 |  |
| `HookTypeAuto` | Auto | 5 |  |
| **Ranged** | `keen::ranged_weapon::State` in `NetworkRangedWeapon.currentState` | value | |
| `RangedHolster` | Holster | 0 |  |
| `RangedAim` | Aim | 1 |  |
| `RangedAimAndCharge` | AimAndCharge | 2 |  |
| `RangedFire` | Fire | 3 |  |
| `RangedFireEaseOut` | FireEaseOut | 4 |  |
| **Fishing** | `keen::fishing::State` in `NetworkFishing.state` | value | |
| `FishingInvalid` | Invalid | 0 |  |
| `FishingIdle` | Idle | 1 |  |
| `FishingFishingMinigame` | FishingMinigame | 2 |  |
| `FishingAim` | Aim | 3 |  |
| `FishingAimAndCharge` | AimAndCharge | 4 |  |
| `FishingWaitForCastAvailable` | WaitForCastAvailable | 5 |  |
| `FishingAbortPreHooked` | AbortPreHooked | 6 |  |
| `FishingPreGame` | PreGame | 7 |  |
| `FishingPrepareCastFloat` | PrepareCastFloat | 8 |  |
| `FishingCastFloat` | CastFloat | 9 |  |
| `FishingWaitForFloatToLand` | WaitForFloatToLand | 10 |  |
| `FishingWaitForValidFloatLocation` | WaitForValidFloatLocation | 11 |  |
| `FishingPreGame_EnsureFloatInWater` | PreGame_EnsureFloatInWater | 12 |  |
| `FishingReadSpawnTable` | ReadSpawnTable | 13 |  |
| `FishingSpawnFish` | SpawnFish | 14 |  |
| `FishingWaitFishSpawned` | WaitFishSpawned | 15 |  |
| `FishingWaitForFish` | WaitForFish | 16 |  |
| `FishingWaitForFishAttracted` | WaitForFishAttracted | 17 |  |
| `FishingWaitForFishHooked` | WaitForFishHooked | 18 |  |
| `FishingFishHooked` | FishHooked | 19 |  |
| `FishingConsumeBait` | ConsumeBait | 20 |  |
| `FishingWaitHookInput` | WaitHookInput | 21 |  |
| `FishingAdvancedMinigame` | AdvancedMinigame | 22 |  |
| `FishingWaitPullCloser` | WaitPullCloser | 23 |  |
| `FishingPreparePullCloser` | PreparePullCloser | 24 |  |
| `FishingPullCloser` | PullCloser | 25 |  |
| `FishingInitMinigame` | InitMinigame | 26 |  |
| `FishingInitFightStage` | InitFightStage | 27 |  |
| `FishingDecideOutbreak` | DecideOutbreak | 28 |  |
| `FishingOutbreak` | Outbreak | 29 |  |
| `FishingFinalHook` | FinalHook | 30 |  |
| `FishingStartReelIn` | StartReelIn | 31 |  |
| `FishingReelIn` | ReelIn | 32 |  |
| `FishingFinishReelIn` | FinishReelIn | 33 |  |
| `FishingFloatReturned` | FloatReturned | 34 |  |
| `FishingVictory` | Victory | 35 |  |
| `FishingWaitForPlayerInteraction` | WaitForPlayerInteraction | 36 |  |
| `FishingWaitForLootFish` | WaitForLootFish | 37 |  |
| `FishingWaitForDiscardFish` | WaitForDiscardFish | 38 |  |
| `FishingEmergencyExit` | EmergencyExit | 39 |  |
| **CookingFlag** | `keen::ecs::CookingFlags` in `NetworkCookingData.mask` | bit | |
| `CookingFlagReceivedSpawnCookEvent` | ReceivedSpawnCookEvent | 0 |  |
| `CookingFlagReceivedStartCookEvent` | ReceivedStartCookEvent | 1 |  |
| `CookingFlagIsCooking` | IsCooking | 2 |  |
| `CookingFlagCanCook` | CanCook | 3 |  |
| **Revive** | `keen::ecs::ReviveState` in `NetworkPlayerAttributes.revivieData.state` | value | |
| `ReviveInvalid` | Invalid | 0 |  |
| `ReviveReviving` | Reviving | 1 |  |
| `ReviveGettingRevived` | GettingRevived | 2 |  |
| **Fog** | `keen::FogType` in `EnvironmentSensor.fogType` | value | |
| `FogNone` | None | 0 |  |
| `FogNoneTemporary` | NoneTemporary | 1 |  |
| `FogDangerous` | Dangerous | 2 |  |
| `FogDeath` | Death | 3 |  |
| **Temperature** | `keen::TemperatureVoxelType` in `EnvironmentSensor.temperature` | value | |
| `TemperatureNormal` | Normal | 0 |  |
| `TemperatureFreezing` | Freezing | 1 |  |
| **Derived** | worked out by Kiln | | |
| `Airborne` | | 0 | Not on the ground, and not climbing, swimming or hanging from a hook |
| `Attacking` | | 1 | Running an action that swings or fires a weapon |
| `Casting` | | 2 | Running an action that uses a spell, or a wand's own attack |
| `UsingItem` | | 3 | Running an action with a used item that is not a weapon |
| `InBase` | | 4 | Inside a flame altar's build zone (kiln.build's base list) |
| `Rested` | | 5 | The Rested buff is on |
| `Fed` | | 6 | At least one food buff is on |
| `HasDebuff` | | 7 | At least one debuff is on |
| `Hungry` | | 8 | The game's hunger marker is in a food slot: nothing eaten (program thirst_and_hunger) |

## Every client action
Commands a client sends with a version number, by the game's field names. All raise `PlayerClientActionEvent`; `PlayerStates.ClientActions` lists them from the running game. Generated with `--actions`. "Can cancel": the game's consumed version for it is known, and writing it makes the game skip the action. Proven in a lab for `changeEquipmentAction`; the others work the same way, but a game program could also read the version elsewhere.

| Action | Bytes | Can cancel |
|---|---|---|
| `inventoryTransferAction` | 32 | yes |
| `actionbarSelectionAction` | 8 | yes |
| `sortInventoryAction` | 8 | yes |
| `selectSongAction` | 8 | yes |
| `loreVoiceoverAction` | 32 | yes |
| `inventoryStockCycleAction` | 8 | yes |
| `buildingStockCycleAction` | 20 | yes |
| `playerCraftingAction` | 68 | yes |
| `areaDepositPermissionAction` | 12 | yes |
| `upgradeStorageAction` | 60 | yes |
| `changeCustomizationAction` | 44 | yes |
| `teleportAction` | 12 | yes |
| `unlockSkillAction` | 12 | yes |
| `triggerKnowledgeAction` | 16 | yes |
| `setPersonalProgressionAction` | 8 | yes |
| `selectDialogOptionAction` | 12 | yes |
| `stopDialogOptionAction` | 12 | yes |
| `npcInputAction` | 12 | yes |
| `changeEquipmentAction` | 16 | yes |
| `swapEquipmentSetAction` | 12 | yes |
| `itemAction` | 24 | yes |
| `createBuildingItemAction` | 12 | yes |
| `createItemAction` | 12 | yes |
| `flameAltarAction` | 12 | yes |
| `logicEntityConfigAction` | 28 | yes |
| `waypointAction` | 32 | yes |
| `weaponGemAction` | 28 | yes |
| `parryAction` | 20 | yes |
| `accountAction` | 24 | yes |
| `redeemItemAction` | 16 | yes |
| `triggerEmoteAction` | 8 | yes |
| `adminInventoryCreateAction` | 20 | yes |
| `adminSpawnCommand` | 80 | yes |
| `adminKillFaction` | 12 | yes |
| `adminTeleport` | 40 | yes |
| `adminApplyInventory` | 24 | yes |
| `adminSetDaytime` | 24 | yes |
| `adminDestroyItemAction` | 12 | yes |
| `adminSetFlameLevel` | 8 | yes |
| `adminSetCharacterLevel` | 12 | yes |
| `adminPerkAction` | 16 | yes |
| `adminDebugCameraDisplayAction` | 8 | yes |
| `adminChangeGameSettingsAction` | 152 | yes |
| `adminDebugEnemyAction` | 56 | yes |
| `adminRepairAllItemsAction` | 4 | yes |
| `adminRefillAllFuelAction` | 4 | yes |

## What a state list leaves out, and why
| The game has | Exposed? | Why, and what to use |
|---|---|---|
| Health, stamina, mana, body heat, special ability, Shroud timer, oxygen, level, comfort | yes, as values | `player.Snapshot()`, from `NetworkPlayerAttributes` |
| The rest of `NetworkPlayerAttributes` (armor, attributes, damage by kind, crit, resistances) | no constant | A character sheet, not a state. `World.TryRead` or `Kiln.Game` has the struct |
| Buffs: `BuffSlotType` Food, Buff, Debuff, Rested, TerrainDebuff | yes | `player.Buffs` with `Slot`; states `Fed`, `Hungry`, `Rested`, `HasDebuff`. A joined client has no `BuffContainer` |
| Equipment and the action bar | yes | `player.Equipment`, `PlayerEquipmentChangedEvent`, `PlayerHotbarChangedEvent` |
| Which entity an interaction is with | yes | `e.Action.Host` while an `ActionInteract` sequence runs; `InteractEvent` in Kiln.Gameplay |
| `actor::ActionSequenceFlags` (`IsActive`, `IsInScope`, `IsInScopeLoop`, `RetriggerAnimation`, `AbortViaMovement`, `SelfDestruct`) | no | The sequence player's bookkeeping. `PlayerAction.Played` and `Length` say how far it is |
| `actor::SequenceFlags`, `SequenceRequirements` | no | Data of a sequence or an item (`DoesRequireStandingUp`, `IsAirborne_Falling`), not of a player |
| `ServerCursorFlag`, `CursorFlag` | no | The building cursor's answer for the block it points at. `IsNearBuildingZone` is the one that matters. One line in the source table if a mod needs it |
| `ClientPlayerSettings`, `ClientPlayerInputToggleConfig` | no | The player's options, not what they are doing |
| `fishing::MinigameState`, `Transition`, `Direction` | no | Inside the fishing minigame. `Fishing*` states show where it is |
| `ranged_weapon::Input`, `StateMachine` details | no | Inside the bow and staff code. `Ranged*` and `InRangeCombat*` show where it is |
| `SongState`, `SongSelection` | no | `PlayingInstrument` says that; the song is in `World.ReadBytes` |
| `CombatStance` end time, `PlayerLowHealth`, `SafePlayerPosition`, `TeleportCount` | no | Timers and bookkeeping. `InCombat`, `Health`, `TeleportEvent` |
| Hit reactions | as actions | `ActionHitReaction`; damage and knockback: `EntityHurtEvent`, `EntityKnockbackEvent` |
| `ConsumeItemEvent`, `ItemActionType.Consume` | no | Nothing in the game sends or handles them. Eating goes through a sequence: `PlayerConsumeEvent` |
