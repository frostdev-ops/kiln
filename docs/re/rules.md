# Player rules: the building fly mode, the build cursor, building costs, mining damage

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

## 5. Vitals and water (no new hooks)
- Health, stamina, mana: `Kiln.Abilities.Attributes` (the attribute system's own add, its formulas re-run, replicated).
  A floor is kept by adding the difference every 2 steps. Stamina is also spent by a client's prediction: a client may
  show a short dip **[hyp]**.
- Invulnerability: Kiln.Combat (`set_invulnerable`, held by Kiln.Rules as owner).
- Water: Kiln.Items' refill policies (docs/re/items.md section 12).

## 6. Signatures
`sigdb/symbols/server.d/rules.json`, unique on the server and the client (the client uses the file unchanged):

| Symbol | Server | Client | Feature |
|---|---|---|---|
| `rules/FlyGate` (offset 41 into the pattern) | `0xc96ee` | `0x2dd74e` | `rules.fly` |
| `rules/ActorStateTest` (captured) | `0x3b7a0` | `0x21e4f0` | `rules.fly` |
| `rules/CurrentEntityId` (captured) | `0x65b870` | `0x9bf9e0` | `rules.fly`, `rules.cursor` |
| `rules/fly.entity`, `rules/fly.actor` | `0x6dc`, `0xa8` | the same | `rules.fly` |
| `rules/CursorFlags` (`rules/cursor.actor` 0xf8 / client 0x108, a check) | `0x9fb20` | `0x29bfb0` | `rules.cursor` |
| `rules/ApplyMaterials` | `0x22d480` | `0x495f20` | `rules.costs` (+ `items/tx.operator`, `world/programs.*`) |
| `rules/TerrainDamage` | `0x22c970` | `0x495410` | `rules.dig` (+ `gameplay/TerraformImpact` and its argument constants) |

## 7. Open
- The undo-record step of a blueprint placement outside every base (2.2).
- What a client shows: the character moved under the spark, the prediction of costs, a stamina dip.
- The end loop reads the program's `HitEvent`s (its only events-in): a hit on the player without flag 0x4000 ends the
  flight **[dec]**; which hits carry 0x4000 was not followed.
- The knowledge id at `.rdata 0xbe3cfc` (which unlock gives the fly mode) was not decoded.
