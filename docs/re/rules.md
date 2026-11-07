# Player rules: the building fly mode, the build cursor, building costs, mining damage, the Shroud timer, gliding

Where the game decides what a player may do and what it costs, and where Kiln.Rules changes it
(docs/kiln/services/rules.md). Dedicated server Steam 26430253 (`f0d824be…`), client 26430625 (`5d9ef2f1…`), both svn
1287369. RVAs are the server's unless marked "client"; image base `0x140000000`.

Sources: the game's reflection (`tools/kfc` types), the program catalog of the server exe, Ghidra 12.1.2 headless
decompiles and capstone over both executables, the offline resolver (`tools/sigport/sigdb_check.py`) on both, and a
lab server with a spawned player character and no client (`mods/tests/rules-test`).

Tags: **[refl]** reflection, **[dec]** decompile, **[dis]** disassembly, **[cap]** sigdb typed-hole capture,
**[rt]** seen in the lab, **[hyp]** not proven.

## Short answers
- **The game's fly mode is the building spark.** Pressing the fly key in build mode (`PlayerInputType.BuildingModeFly`
  39) spawns `2_Player_Building_Spark`, an actor with locomotion state `BuildingFlying` (11), stores it in
  `PlayerState.buildingSparkEntityId` (+0x50), and from then on hands the player's input to the spark. The character
  stays behind, in the `Sitting` actor state (bit 9) unless in water. The camera follows the spark
  (`CameraId::PlayerCameraBuildingFlySparkMode`), and so does `PlayerFocus` (what the server streams around).
  **[refl] [dec]**
- **The claim gate is one decision in `player_control_action_sequence`,** on both sides (it runs in the server world
  and in a client's prediction world): the cursor is near a building zone (`IsNearBuildingZone`), the player is not
  dead or gliding, their user group has the building permission (bit 2 of `BasicPlayerInfos.permissions`), and they
  know the fly-mode unlock (a knowledge id). The same decision ends the flight: false (for example after leaving the
  base), the fly key, cancel, or a hit. **[dec]**
- **Any teleport of the player ends the flight** (the program reads an optional `Teleport` component), so the
  character can be brought to where the flight ended, not carried along during it. **[dec] [rt]**
- **Kiln changes the decision, not its inputs** (a mid hook where it is made, `rules/FlyGate`): a player with
  `FlyAnywhere` gets "allowed" unless dead or gliding. No zone, permission or knowledge is changed or granted.
  **[cap] [rt]**
- **The build cursor's refusals are one bit set** (`keen::ecs::CursorFlag`), computed by one function both cursor
  programs call (`rules/CursorFlags`). A building action runs only when the flags are 0. Kiln clears the bits a
  player's rules waive, after the game computed them. **[dec] [cap]**
- **What placing takes is a materials list** handed to `ApplyMaterials` (`rules/ApplyMaterials`): one block per cell a
  blueprint fills, the prop item for a prop. The same function gives materials back (dismantle, undo, terraforming
  loot); Kiln scales the list only for calls from the four building programs. **[dec]**
- **A dig's strength is one function per hit and material** (`rules/TerrainDamage`): `setup[hardness] x the server's
  miningDamageFactor x the class modifier`. Kiln multiplies its result for the player whose `TerraformImpact` runs on
  the thread. **[dis]**
- **"Needs a Flame Altar nearby" is the server's cursor zone test,** not the cursor flags: `server_cursor_no_prediction`
  asks the build-zone system once and the game copies the answer to the actor state and the replicated cursor, which
  the hammer's HUD, the client cursor and the fly-mode gate read. Kiln answers "yes" there for `BuildOutsideBases`
  (`rules/ZoneFlag`). The other in-world refusals (no-build zones, enemies nearby, the Shroud) are cursor flags only and
  were already cleared. **[dec] [dis] [rt]**
- **The Shroud timer is `FogResistance`** (whole seconds), drained by `fog_resistance_update` in the server world; the
  HUD shows the replicated value. Kiln picks the game's "no drain" or "recover" branch for a frozen player
  (`rules/ShroudDrain`). **[dis] [rt]**
- **Gliding is predicted:** the glide step, its gravity and its stamina run in the server world and in the client's
  prediction; Kiln's three glider hooks give the same result on both sides from the same rules and input. **[dis]**
- **Vitals and water need no hook:** Kiln.Abilities' attribute writes (health, stamina, mana) and Kiln.Items' refill
  policies (water) already go through the game's own paths.

## 1. The building fly mode

### 1.1 What it is **[refl] [dec]**
| Piece | Where | What |
|---|---|---|
| Input | `PlayerInputType.BuildingModeFly` (39), `input_mapping` action `BuildingModeFly` | a held key; `CancelAction` (13) also ends it |
| Spark | template `2_Player_Building_Spark` (`f3b1b68d-c96f-4f4a-a248-507524ee64ac`, GUID bytes at `.rdata 0xbe3b30`, client `0x15298e8`) | Actor, ActorInput, Locomotion, Movement, MovementCollider, NetworkLocomotion, `PlayerBuildingSpark` (1 byte marker), Vfx, sounds (`PlayerBuildingSparkSfx`) |
| Link | `PlayerState.buildingSparkEntityId` (+0x50, `PlayerState` 96 B) | 0 = not flying |
| Locomotion | `LocomotionState.BuildingFlying` (11) | the spark's initial `Locomotion` byte (`mov byte [rbp+0x59], 0xb` in the spawn) |
| Program | `player_control_building_fly_mode` (#254, `0xcd080`, flags 0x200) | per flying player: copies the player's `ActorInput` into the spark's (other-write), resets the player's own movement input, and writes `PlayerFocus.position` = the spark's position |
| UI | `FbUiCoverStyle.FlyModeEnter` / `FlyModeExit`, `FbUiLocaHudBuilding.buildFlyModeActivated` | events `0x95da80e9` (enter) and `0x21d0e859` (exit) sent by the two functions below |
| Rules elsewhere | `SequenceRequirements.NotInBuildingFlyMode`, `InteractionOffer.allowedAsBuildingSpark` | sequences that need the player out of it; interactions a spark may use |

- **Enter** (`0xae050`): spawns the spark through the queued create (`0x5fb340`) at the player's position plus an
  offset along the player's rotation, with `CurrentTransform`, `Actor` and `Locomotion` initial data; stores its id in
  `PlayerState+0x50` (marked written); sets the player's `Actor` state bit 9 unless it is `FloatingOnWater` or
  `UnderWater`; sends the enter cover event. **[dec]**
- **Exit** (`0xaebe0`): gives the spark a 0.25 s `LifeTime` (queued component add, hash `0xf22c253c`), clears the
  player's bit 9, sends the exit cover event. **[dec]**
- The cursor function treats a flying player as valid (`PlayerState` flying: no "invalid transform" bit, 2.1).

### 1.2 The gate **[dec] [dis]**
In `player_control_action_sequence` (#248, update `0xc8c40`, flags 0x200: the server world and a client's prediction
world; client `0x2dcca0`):
```
perm   = BasicPlayerInfos[player-1].permissions                      0x163040(out, player, BasicPlayerInfos)
allowed = ActorStateTest(actor, IsNearBuildingZone 54)               0x3b7a0, the actor slot at [rbp+0xa8]
          && !actor has Dead (7) or HangGliding (37)                 0x3ab40 with mask {7, 0x25}
          && (perm & 4)                                              the building permission
          && knows(0xbe3cfc's knowledge id)                          0x94c480(id, player knowledge, registry)
          -> bl                                                      0xc96e8 mov bl,1 / 0xc96ec xor bl,bl; merge 0xc96ee
if not flying:  fly key pressed -> if allowed: Enter (0xae050); consume the key
if flying:      spark gone -> PlayerState+0x50 = 0
                end = !allowed || [rbp+0xa0] != 0 || fly key || cancel key || a matching event without flag 0x4000
                end -> Exit (0xaebe0); consume both keys
```
- `IsNearBuildingZone` comes from the cursor: `server_cursor_no_prediction` sets `ServerCursor.serverFlags` bit 2 from
  `0x94be60(BuildZoneSystem, position)`, and `network_cursor` copies it into the actor state. So leaving the base ends
  the flight from inside the same decision. **[dec]**
- The client has the same code at `0x2dd725`..`0x2dd74e` (same frame offsets: entity id `[rbp+0x6dc]`, actor
  `[rbp+0xa8]`). **[dis]**

### 1.3 Kiln's change **[cap] [rt]**
`rules/FlyGate` = the merge point (`0xc96ee`, client `0x2dd74e`): a mid hook runs before `mov rcx,[rbp+0x2b0]`.
- `rdi` holds the ProgramContext; `JobStorage(*ctx) + storage.kind` gives the world kind (0 = the server world, a
  client's own worlds otherwise); `[rbp+0x6dc]` the player entity (`rules/fly.entity`, captured from the
  `CurrentEntityId` call before the gate); `[rbp+0xa8]` the actor (`rules/fly.actor`, captured from the
  `IsNearBuildingZone` test).
- When `bl` is 0 and the player has `FLY_ANYWHERE` (server: by entity; client: the local player's rules), and the
  game's own state test says neither Dead nor HangGliding, `bl` becomes 1. Enter and every later step then run the
  game's own code; the end conditions other than "not allowed" stay.
- Ending the rule makes `bl` 0 again, so outside a base the game itself ends the flight in the next step.

**Lab** (`mods/tests/rules-test`, a spawned `1_Player` 160 m outside the base, the input bit set in `PlayerInput`):
see docs/kiln/services/rules.md, "Lab results".

### 1.4 The character during a flight (Kiln.Rules, managed) **[rt]**
The game leaves the character sitting where the flight began, and **any teleport ends the flight**: the program reads
an optional `Teleport` component (`[rbp+0xa0]`, read_opt `Teleport`) and ends the mode while one is pending. Lab: a
Teleport added to the character 4 steps into a flight ended it in the next step. So Kiln does not move the character
during a flight. With `FlightCarriesBody`, when the spark goes (any reason) Kiln teleports the character to the
spark's last position, on the ground a voxel column read finds below it (the game's ground search only looks near
its target: a landing 88 m above the ground stayed in the air in the lab), and keeps it invulnerable until it has been
grounded for 12 checks (60 s at most). Moving it along during the flight would need a marked write of its `CurrentTransform` and
`LastTransform` from a Kiln program (the foundations' write path), not a teleport.

## 2. The build cursor **[dec] [cap]**

### 2.0 "Near a building zone": one server test **[dec] [dis] [cap]**
`server_cursor_no_prediction` (#116, `0x9cbd0`, client `0x297840`) asks `IsInBuildZone(BuildZoneSystem, cursor
position)` (`0x94be60`, client `0xea4c10`; its only caller) and stores the answer in `ServerCursor.serverFlags`
(+0x69) bit 2 (`ServerCursorFlag.IsNearBuildingZone`; bit 3 `CanUndoBuildingAction` is decided right after from it and
the player's undo record). `network_cursor` (#118, `0x9c810`) copies the flags into the replicated
`NetworkCursor.serverFlags` (+0x31) and bit 2 into the actor state (`IsNearBuildingZone`, 54, `Actor+0xbf8` bit 54),
both marked written. Everything else reads one of those two:
- the fly-mode gate (1.2), the hookshot targeting (`player_update_hookshot_targeting`, `0xd53c0`: an item's in-base
  targeting byte +0x127 instead of +0x125) and three more state-54 tests on the server (`0xcbee6`, `0xddbae`,
  `0x1bb4d2`, not followed);
- on the client, the HUD: `0x3c47b0` (a `player_ui` helper) turns `!NetworkCursor.serverFlags.IsNearBuildingZone`
  into the hammer's hint **"Needs a Flame Altar nearby."** (loca tag `0x904933e6`, `FbUiLocaHud.multiTool`
  (`FbUiMultiToolMenu` +0x2d0) `.needsFlameAltarHint` +0xa0), and `0x3c4160` (case 0x5c) gates a UI action on the same
  bit. The cursor's own "Needs building area" (`0x8a3deb3c`, `FbUiLocaHudBuildFeedback.cursorOutsideBuildZone`) is
  cursor flag 1.

So clearing cursor flag 1 alone (2.1) is not enough: the hammer still says it needs an altar. Kiln's change is at the
test: `rules/ZoneFlag` is a mid hook right after the `IsInBuildZone` call (`0x9cfaf`, client `0x297c1f`, the hosted
world); `al` is the answer and `r12` the ProgramContext (the program passes it to `CurrentEntityId` a few
instructions later). For a server-world player with `BUILD_OUTSIDE_BASES` a "no" becomes "yes", so the actor state, the
replicated cursor, the HUD and the fly-mode gate all see the player as inside a base. Side effect, by design: the game's
own fly mode then works for that player outside bases (the gate's zone test passes); `FlyAnywhere` stays the rule for a
player who may fly but not build. Server world only: a joined client gets the result through `NetworkCursor`.

### 2.1 The flags function
`rules/CursorFlags` (`0x9fb20`, client `0x29bfb0`) `void (u32* flags, CursorCtx* ctx, Query* q, i64* transform, u8, u8,
u32)`: `0x9e850` (the main checks), then bit 0 (invalid transform) from the actor state unless flying, bit 12
(missing materials) from `0x99750` (client `0x28ab60`), bit 8 (area occupied) from a collider query.
- Callers: `server_cursor_no_prediction` (`ServerCursor.primaryFlags` +0xC and `secondaryFlags` +0x10) and, on the
  client, the same function from `client_cursor` (`ClientCursor` +0x80 / +0x84) and the hosted world's server cursor.
- The cursor context's first qword is the ProgramContext (both programs, both executables), so the current entity
  comes from `CurrentEntityId(ctx)` and the world kind from its job's storage.
- A building program runs its action only when `0x393f0` gives 0: the primary or the secondary flags, by input.

| Bit | `CursorFlag` | Kiln rule that clears it |
|---|---|---|
| 1 | CursorOutsideBuildingZone | `BuildOutsideBases` |
| 2 | CursorInsideFogZone | `BuildInShroud` |
| 4, 5 | CursorInside(Lenient/Strict)BuildInhibitorZone | `IgnoreNoBuildZones` |
| 9 | EnemiesNearby | `IgnoreEnemiesNearby` |
| 12 | MissingMaterials | `BuildCost` 0 |
| 14 | NoPermission | `IgnoreBasePermissions` (denied unless the server's rules.json allows it) |
| others | invalid transform, too far, altar and water-source limits, occupied, normal, material, not unlocked, disabled, tame-animal limit, in water, save and factory budgets | never |

### 2.2 What else a placement checks **[dec] [hyp]**
`player_building_blueprints` (`0xc4410`): the cursor's error code must be 0 (2.1); with an undo storage it needs the
player's undo record (`0x230fb0` → `0x230260`); then the cost (3), `PlaceBlueprint` (`0x225c10`), `BuildFinish`. Whether
a player outside every base has an undo record was not followed: if not, a blueprint placed outside a base fails
there even with the cursor clear. Manual test.

The game saves voxels only inside bases (docs/re/build.md); `KeepBuildsOutsideBases` adds the 16 m chunks around a
placement outside bases to Kiln.Build's kept regions.

## 3. What placing costs **[dec]**
- `player_building_blueprints` dry-runs the placement (`0x228f30`) into a materials list: one block per blueprint cell
  that lands on air or a replaceable cell (docs/re/building.md 1.2).
- `rules/ApplyMaterials` (`0x22d480`, client `0x495f20`) `bool (CostCtx* c, {Entry* ptr; u64 count}* list, u8 take)`:
  `c+0` mode (0: the server's inventory transaction at `c+8`; 1, 2: the prediction's), entries of 0x18 bytes with the
  item at +0x10 and the count at +0x14. `take` 1 removes the list (`0x199c80`), 0 adds each entry (`InventoryAddItems`).
  The acting player is the transaction's operator (`items/tx.operator`, +0xe0).
- Call sites: blueprints `0xc4b4b`, place prop `0xc6be4` and `0xc6c41`, undo `0xc72f0`, dismantle `0xc585c`, `0xc5e25`,
  `0xc5f7e`; also impact natives (`0x17bb89`, `0x189f57`) and terraforming loot (`0x18f1d0`, give).
- Kiln's hook takes the caller from its return address: `RtlLookupFunctionEntry` and the chained unwind info give the
  primary function, compared with the update functions of `player_building_blueprints`, `_place_prop`, `_undo` and
  `_dismantle` read by name from the server world's program table (`world/programs.*`). Only those calls are scaled:
  `take` by `BuildCost`, give by `BuildRefund`. Counts round to the nearest whole item; an empty list succeeds.
- Terrain placed with the construction hammer is a blueprint shape (docs/re/building.md 1.2), so it is scaled with the
  blocks. Not scaled: crafting (`handle_crafting_events` → `CraftRecipe` `0x210f00`; kiln.gameplay hooks it),
  terraforming loot, the two impact natives (`0x17b8d0`, `0x189ce0`; what they buy was not followed). A client's
  prediction (mode 1, 2) is not changed: a client may show the items leave and come back.

## 4. Mining damage **[dis] [dec]**
`rules/TerrainDamage` (`0x22c970`, client `0x495410`) `{u8 error; u32; float damage}* (out, settings, setup, u8
hardness, u8 class)`: `setup[hardness]` (5 categories), times `miningDamageFactor` (`0x93df20`: the server settings'
float at +0x30), times the class modifier. Callers are the four voxel kernels (`0x227c6f`, `0x2285f5`, `0x22938b`,
`0x229493`), all inside the `terraforming` impact native `TerraformImpact` (`0x18e0b0`, client `0x3ecd30`) on the same
thread. Kiln notes the impact's player argument (arg 0, read as kiln.gameplay does) in a thread-local around
`TerraformImpact`, and multiplies a non-zero damage by the player's `DigDamage`. Zero damage and unbreakable materials
(no table entry) stay. Explosions pass no player and are never scaled.

## 5. The Shroud timer **[dis] [dec] [cap]**
- **Storage:** `keen::ecs::FogResistance` (hash `0x5f4ef514`, 32 B, an attribute component `{u32 group; u16 values
  offset (8); u8 flags (bit 0 valid); u8 count (6); s32 values[6]}`): current `0x6951df16` (whole seconds), max
  `0xb34ad82f`, base max `0x31a34ef4` (flame level x the server's `shroudTimeFactor`, `fog_power_max_scaling`
  `0x56aa0`), maxSubtract `0xb3a1581c`. `FogResistanceState {float accumulatedValue}` (`0xa926ed49`) holds the fraction.
- **Drain:** `fog_resistance_update` (#349, `0xc1c70`, client `0x2d5cd0`, server world) clears `InFog` (actor state
  13), skips the dead and the underwater, makes the fog type 1 in a safe zone (state 14), then dispatches on
  `EnvironmentSensor.fogType` (`bl`): 0/1 recover +25/s toward max; 2 (dangerous) -1/s, 3 (deadly) -100/s, then sets
  `InFog`; 2 and above run the death check.
- **Death:** when the timer is below 1 after the drain: deadly fog sets health to 0, dangerous fog takes `dt x
  HealthMax` (dead in about a second), and a `PlayerDeath` with cause Fog is recorded. A direct health write, not a
  hit, so Kiln.Combat's invulnerability does not stop it **[dis]**.
- **HUD:** `network_player_attributes` (#53, `0x8cca1..`) copies current, max and maxSubtract into
  `NetworkPlayerAttributes` +0xac/+0xae/+0xb0 (u16), and the client's `player_ui` copies those into `G38UiPlayData`
  +0x5c0.. (`0x2b8c79`). The countdown is the server's replicated value: a server-side freeze shows a steady timer for
  a host and for a joined client.
- **Kiln's change:** `rules/ShroudDrain`, a mid hook on the dispatch (`movzx ecx, bl`, `0xc1f35`, client `0x2d5f95`).
  `r15` is the ProgramContext, `r13w` / `r12w` the current / max value slots (from the two `AttrFind` calls at the top
  of the program), `[rsp+0x78]` the FogResistance component (`rules/fog.attr`), `[rsp+0x48]` the Actor
  (`rules/fog.actor`), `Actor+0xbf8` its state word (`rules/fog.state`). For a player in the Shroud (`bl >= 2`) with
  `SHROUD_FREEZE`: `bl = 4` (no drain branch; the death check cannot fire with the timer at 1 or more) and `InFog` set
  as the drain branch would; with the timer below the floor (or 0): `bl = 1`, the recovery branch (no death check).
  `shroud_floor` (a fraction of max) holds the timer at the floor the same way, a margin above one step's drain. The
  accumulator is never touched, so when the rule ends the game counts down from where the timer stands.
- Other levers that do not freeze it: `shroudTimeFactor` (server-wide max), max adders (skills, flame level), the
  debug program `debug_god_mode_fogresistance` (`0x54fd0`, zeroes `0x11c09660`, which the drain never reads).

## 6. Vitals and water (no new hooks)
- Health, stamina, mana: `Kiln.Abilities.Attributes` (the attribute system's own add, its formulas re-run, replicated).
  A floor is kept by adding the difference every 2 steps. Stamina is also spent by a client's prediction: a client may
  show a short dip **[hyp]**.
- Invulnerability: Kiln.Combat (`set_invulnerable`, held by Kiln.Rules as owner).
- Water: Kiln.Items' refill policies (docs/re/items.md section 12).

## 7. Gliding **[refl] [dis] [cap]**
- **State:** `DynamicLocomotion.state` (+0x59) `LocomotionState.HangGliding` (4); the actor state `HangGliding` (37)
  is the other enum. **[refl]**
- **Programs** (server / client, all without the host-only bit, so a client's prediction runs them too):
  `locomotion_execution` (`0x1dcf90` / `0x43d6d0`; case 4 calls the glide step `0x1d2320` / `0x432a80`, its only
  caller), `stamina_depletion_glider` (`0xdcd60` / `0x2f0dc0`), `gravity_apply_velocity_actor` (`0xa0de0` /
  `0x2a3510`), `locomotion_glider_upstream` (`0x1de910`, updrafts), `actor_teleport_glider` (`0xbbe60`: a pending
  Teleport aborts the glide and zeroes Velocity). Order in a step: the glide step, friction, gravity, updrafts, the
  collider move. **[dis]**
- **The glide step** `void (iterator*)`: velocity += `accelerationForward x F x (dt / (1/60))` (F = the transform's
  rotation applied to (0,0,1), ramped in over 0.5 s), then quadratic drag along F, up and sideways
  (`v *= 1 - min(1, |v| k dt)`). Glider values from `ItemInfo.equipment.gliderConfig` (+0x4c0): T1 accel 0.8 / drag
  0.39, 1.8, 0.7125; T4 1.0 / 0.155, 1.5, 3.0. Level speed is the glider's own thrust against drag (about 11 m/s for
  T1, 20 for T4); the sink comes from gravity (`Gravity`+0xc, or PlayerLocomotion +0x3c rising / +0x40 falling),
  about the root of g / k_vertical. No duration limit exists. **[dis] [data]**
- **Stamina:** for an actor HangGliding and not Exhausted, `StaminaDepletion += dt x cost x mod` (an item attribute and
  attribute `0xb22c38ff`); Exhausted (6) aborts the glide. Sprint's own drain needs the ground, water or the fly mode.
- **Ends:** a HitEvent on the entity with a hit reaction and flag bit 14 clear, or a death (the event lists at iterator
  +0x1d0 / +0x1d8), the glider failing its use check, no SlotSelection, a long collision, Exhausted, any Teleport. The
  abort (`0x36990`) sets `ActorInput+0x20` (the trigger, u16) to 24. **[dis]**
- **Input:** `PlayerInputType.Sprint` = 53, a held bit of `PlayerInput`'s digital input (+0x3b0, after the mask +0x3a8).
- **Iterator** of `locomotion_execution`: +0 ProgramContext, +0x20 CurrentTransform (rotation +0x18, `keen::Quaternion
  {x,y,z,w}`), +0x50 PlayerInput (null for anything but a player), +0x98 ActorInput, +0xf8 DynamicLocomotion, +0x158
  Velocity. `QuatRotate(float3* out, const Quaternion*, const float3*)` `0x3ba80` / `0x21e760`; `StepDelta(ctx, int64*
  out)` (`0x660d50` / `0x9c4da0`) gives the step's nanoseconds. **[cap]**
- **Each-programs have another context:** these programs (and `fog_resistance_update`) run as parallel each-programs:
  their context's first qword points at a per-chunk job copy inside the context (+0x10) whose storage pointer (+0x30)
  is null, so `world_kind_of` (JobStorage + `storage.kind`) cannot tell the world there **[rt]**. Kiln's glider and
  Shroud hooks therefore take the per-entity rules (which exist only where a server world runs) and the local player's
  rules for the local player (a joined client), without the world kind.

**Kiln's change** (three hooks, all keyed on the entity's rules; the same in the server world and a client's
prediction, so the server agrees with what the player sees):
- `rules/GlideStep` (inline wrap): before, the velocity is copied (v0) and the ActorInput trigger read; after, unless
  the game aborted the glide in this step: with sprint held and `GLIDE_BOOST`, the speed along F becomes
  `max(v.F, min(MaxSpeed, max(v.F, v0.F) + Thrust dt))` (the game's drag along F replaced by the thrust, so the push is
  the same with every glider) and `Lift dt` is added upward; without sprint, the speed along F falls at most `Coast dt`
  per step; with `glide_sink` 0 and the glider not pointing down (F.y > -0.2), a downward velocity is set to 0. The game
  has already marked Velocity written in this step.
- `rules/GlideGravity` (mid hook, `0xa0f13` / `0x2a3643`, before `StepDelta`): for a HangGliding actor, xmm6 (the
  gravity strength) x `glide_sink`^2 (the steady sink goes with the root of gravity under quadratic drag). rdi is the
  context; the Actor is at `[rbp + (int8)grav.actor]` (`0xaf` = -0x51).
- `rules/GlideStamina` (mid hook, `0xdcfda` / `0x2f103a`, before `addss xmm6,[rax]`): `GLIDE_UNLIMITED` sets this
  step's drain (xmm6) to 0.
Guards: `rules/GlideStep` works on a copy and writes back only a finite velocity no faster than 150 m/s (or the
game's own speed in that step when that was faster); a non-finite game velocity, heading or step time is left alone;
the gravity scale is applied only when the result is finite. `set` bounds the values (sink x10, boost 120 m/s,
accelerations 100 m/s²). A float the game converts into a 32.32 fixed-point position becomes INT64_MIN when non-finite
or huge, and the client's health-bar line of sight then runs for seconds per entity (docs/re/client.md, "The
camera, health-bar line of sight, and a frozen client").
Not done: a hit still ends a glide (the hit list could be hidden from the step, but its layout was not followed).

## 8. Signatures
`sigdb/symbols/server.d/rules.json`, unique on the server and the client; `client.d/rules.json` extends it with the
client's two guards (docs/re/client.md):

| Symbol | Server | Client | Feature |
|---|---|---|---|
| `rules/FlyGate` (offset 41 into the pattern) | `0xc96ee` | `0x2dd74e` | `rules.fly` |
| `rules/ActorStateTest` (captured) | `0x3b7a0` | `0x21e4f0` | `rules.fly` |
| `rules/CurrentEntityId` (captured) | `0x65b870` | `0x9bf9e0` | `rules.fly`, `rules.cursor` |
| `rules/fly.entity`, `rules/fly.actor` | `0x6dc`, `0xa8` | the same | `rules.fly` |
| `rules/ZoneFlag` (offset 12) | `0x9cfaf` | `0x297c1f` | `rules.zone` |
| `rules/CursorFlags` (`rules/cursor.actor` 0xf8 / client 0x108, a check) | `0x9fb20` | `0x29bfb0` | `rules.cursor` |
| `rules/ShroudDrain` (offset 101; `rules/fog.actor` 0x48, `rules/fog.attr` 0x78, `rules/fog.state` 0xbf8) | `0xc1f35` | `0x2d5f95` | `rules.shroud` |
| `rules/GlideStep` (+ `@rules/GlideStep.abort/.xform/.velocity`, `@rules/Loco.input`, `@rules/Input.mask`, `@rules/Input.trigger`; `QuatRotate`, `StepDelta` captured) | `0x1d2320` | `0x432a80` | `rules.glide` |
| `rules/GlideGravity` (offset 133; `rules/grav.actor` 0xaf) | `0xa0f13` | `0x2a3643` | `rules.glide` |
| `rules/GlideStamina` (offset 88) | `0xdcfda` | `0x2f103a` | `rules.glide` |
| `rules/ApplyMaterials` | `0x22d480` | `0x495f20` | `rules.costs` (+ `items/tx.operator`, `world/programs.*`) |
| `rules/TerrainDamage` | `0x22c970` | `0x495410` | `rules.dig` (+ `gameplay/TerraformImpact` and its argument constants) |
| `rules/client.HealthbarRay` (offset 73, scoped to `rules/client.Healthbar` `0x30bd70`; `rules/client.LineOfSight` `0xe88ee0` checked; `rules/client.hb.player_z` 0x278 fixed) | - | `0x30c4af` | `rules.healthbar_guard` (client.d only) |
| `rules/client.CameraInput` (offset 185; `rules/client.CameraTransform` `0xe98ea0` checked; `cam.obj` 0x5c8, `cpi.camera` 0x20, `cam.distance` 0x14a4, `cam.quat0/1` 0x14b0/0x14c0, `cam.offset` 0x148c, `cam.pivot` 0x1468 fixed) | - | `0x3af55f` | `rules.camera_check` (client.d only) |

## 9. Open
- The undo-record step of a blueprint placement outside every base (2.2): with `IsNearBuildingZone` forced (2.0) the
  server also decides `CanUndoBuildingAction` from the player's undo record, as inside a base.
- The program order is taken to be the table order (the network copy #53 before the drain #349) **[hyp]**.
- What a client shows: the character moved under the spark, the prediction of costs, a stamina dip.
- The end loop reads the program's `HitEvent`s (its only events-in): a hit on the player without flag 0x4000 ends the
  flight **[dec]**; which hits carry 0x4000 was not followed.
- The knowledge id at `.rdata 0xbe3cfc` (which unlock gives the fly mode) was not decoded.
