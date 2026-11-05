# Player/world interaction on the dedicated server (svn 1285601): evidence for kiln.gameplay

Build: dedicated server Steam 26404334 / svn 1285601, exe sha256 `c5c9fe50…`, image base `0x140000000`; addresses are
RVAs. Cross-checked against build 1273043. Sources: EMF 0.3.3 docs (MIT;
`engine_parts/inventory_crafting.md`, `world_building_daytime.md`, `re_events.md`, `re_villager_jobs.md`,
`re_villages.md`, `re_teleport.md`, generated event layouts in `sdk/include/emf/generated/components.hpp`), program
descriptors read from the exe (`.rdata`, 0x188-byte `ProgramDesc`s: name, update, access lists), capstone
disassembly, Ghidra 12.1.2 headless decompiles (no auto-analysis), the offline resolver (`tools/sigport/sigdb_check.py`
on both builds), and the lab `gameplay` (a copied save, no player). Tags: **[desc]** program
descriptor, **[dis]** disassembly, **[dec]** decompile, **[cap]** typed-hole capture in
`sigdb/symbols/server.d/gameplay.json`, **[rt]** runtime in the lab (kiln.log quoted).

## 1. The key finding: pending ECS events are changeable in Pre

- `EmitEvent` `0x5fcfc0` (old `0x5f6bb0`; EMF sig `48 83 EC 28 4C 8B D9 41 81 F8 00 01`) `void* (Buffer* b, u32 hash, u32
  size, u16 programId)`: `Buffer = {u8* base; u64 cap; u32 used}`; appends `{u32 hash; u16 program; u8 size; u8 0;
  data[align8(size)]}` and returns `data` (zeroed); refuses `size >= 0x100`. **[dis]** Programs call it with
  `JobEventBuffer(J)` `0x661310` (`mov rax,[rcx+0x1b8]`) and `JobProgramId(J)` `0x65f5f0` (`movzx eax, word [rcx+0xb8]`),
  `J` = the events-out iterator slot. **[dis] [cap]**
- `DeliverEvents` `0x6c2300` (old `0x6ba490`) is called by **SimStep** (`0x6c8f10`, call at `0x6c8f6c`, before any
  program runs) with the step's storage: for every job buffer in `storage+0x7b9ef0` (array, stride 0x18, count
  `storage+0x7b9ef8`) it walks the records, looks the hash up in the registry's event map (`registry+0xe16dc0` bits,
  `+0xe16dc8` cap, `+0xe16de0` keys, `+0xe16df8` u16 values, `+0xe16e0c` count; registry = `*storage`), appends the data
  to that type's queues (`storage+0x7b9e58` and `+0x7b9e80`, 0x48 bytes per type) with `0x5feb60`, then sets the buffer's
  `used` (+0x10) to 0. **[dis] [cap]**
- `0x5feb60` copies **the queue's element size** (`[queue+0x14]`) from the record's data, not the record's size byte:
  a record can be retyped to a smaller event type in place. **[dis]**
- So everything programs sent in step N sits in the job buffers between SimStep N and SimStep N+1, i.e. during Kiln's
  Pre of N+1, on the tick thread with no job running, and the game's readers only get it after Kiln looked.
  kiln.gameplay reads them there (observe), and neutralizes canceled ones (cancel):
  `pending events readable in Pre: 2 record(s) in step 275` **[rt]**.
- Queue layout (for the delivery check): `+0x14` element size, `+0x18` count, `+0x30` page base, `+0x40` first page
  (1-based, 0x800-byte pages, `+2` next page, events from `+8`, 0x7F8 usable). The queues still hold step N+1's events
  in Post N+1: `delivery check (Post): DeclinedInteraction queue holds 1 event(s) of 12 bytes, 1 of them the 1 Kiln
  declined` **[rt]**.
- Without players the world sends almost nothing: `pending events over 1500 walks: 7 type(s): 7c31fc55 x9, 82e404b2 x4,
  a4766fb7 x3, 730ab56d x3, d079d101 x2, 13f49577 x2, 7fe02e20 x1` (all but two from the test's injections). **[rt]**

## 2. Programs involved (descriptors, svn 1285601) **[desc]**

| Program | Update | Flags | What matters |
|---|---|---|---|
| `player_crafting` | `0xd2250` | 0x210 | `PlayerInput.playerCraftingAction` (+0xbc; version +0xf8, type +0xfc) vs `ServerConsumedPlayerInput+0x44`; type 0 sends `CraftRecipeEvent` {operator = player, station +0xbc, recipe +0xc0, amount u16 +0xc8, selections +0xd4..} |
| `handle_crafting_events` | `0x8ad90` | 0x200 | events-in `CraftRecipeEvent`; per entity with `Crafting`: events whose station == current entity → `RecipeFind` `0x940a30`; the two altar workshops (`b326e042`, `6f7d5d2f`) need `BasicPlayerInfos+0x9D & 0x10` (CanExtendBase); `CraftRecipe` `0x210f00` `amount` times |
| `factory_station` | (via `0xe43c2`, `0xe4545`, `0xedcc0`) | | automated stations: `CraftRecipe` with operator == station every step (MissingMaterials when empty) **[rt]** |
| `interaction_attempt` | `0xa4e80` | 0x4 | events-in `AcceptOfferedInteractionEvent`; host's `InteractionOfferRuntime` must be active (+0x2d), offer == +0x28 and free (+0x24/+0x18) → `InteractionAcceptedEvent`, else `DeclinedInteraction` {guest, host, offer} **[dec]** |
| `player_control_not_predicted` | `0xcfa60` | 0x200 | events-in `DeclinedInteraction`: guest == current entity and `Actor+0x88 == offer`, `Actor+0x78 == host` → ends the interaction sequence (`0x36990`) **[dec]** |
| senders of AcceptOffered | `player_control_action_sequence` `0xc8c40`, `path_walking` `0x1ff610` (villagers) | | |
| `loot_pickup_item` / `immediate_loot_action` | `0x1a9200` / `0x1a51f0` | 0x200 / 0x4 | events-in `LootInteractionEvent` (sent by `actor_game_events`, `player_control_action_sequence`) |
| `player_building_blueprints` / `_place_prop` / `_dismantle` / `_undo` | `0xc4410` / `0xc6280` / `0xc4e20` / `0xc6dd0` | 0x200/0x210 | no events-in: they read the actor's action-sequence events (`0x34520`, PlaceVoxelObjectEvent `0xde627227` …) of the current animation window, i.e. created and consumed inside one step; the building core below |
| `server_cursor_no_prediction` | `0x9cbd0` | 0x10 | reads BuildZoneSystem, BasicPlayerInfos (permissions): writes `ServerCursor` whose `+0xc`/`+0x10` error code the building programs require to be 0 (`0x393f0`) |
| `impact_simulation` | `0x197cb0` | 0x1200 | runs impact functions incl. `terraforming` |
| `random_spawner` | `0x103bb0` | 0x10 | `with PlayerInRange`: only near players; spawns through `QueueSpawn` (call `0x106282`) |
| `teleport` | `0x916a0` | 0x10 | applies `Teleport` (docs/re/world.md) |

## 3. Crafting

- `CraftRecipeEvent` (0x38): `+8` operator, `+0xc` station, `+0x10` recipe, `+0x14` u16 amount, `+0x18` category
  selections (player_crafting fill **[dec]**, EMF layout).
- Cancel in Pre: station := 0. handle_crafting_events compares the station with its current entity (`if (*piVar17 ==
  *piVar7)`) and skips the event silently: nothing consumed, no log line. **[dec] [rt]** `PASS chest unchanged by the
  canceled craft: input 4 -> 4, output 1 -> 1`.
- `CraftRecipe` `0x210f00` `int (CraftContext*, RecipeInfo*)`: ctx `+0` operator, `+4` station, `+8` station workshop;
  returns 0 crafted, 2 no room, 4 missing materials/requirements, 5 missing props (EMF; 4 seen for empty factories
  **[rt]**). Hooked for observation only.
- `RecipeFind` `0x940a30` `RecipeInfo* (u32 id, RecipeRegistry*)`, map at registry `+0x48` (count `+0x9c`, cap `+0x58`,
  bits `+0x50`, keys `+0x70`, values `+0x88`) **[dis] [cap]**; workshop map `+0xF8` (keen map, values 0x1008 B, u16
  count at +0x1000). RecipeInfo `+0x10` id, `+0x24` workshop, `+0x28`/`+0x30` inputs/outputs BlobArrays (48 / 24 B
  items, item `+0x10`, count `+0x14`), `+0x38` duration, `+0x48` props mask: consistent at runtime (`station 1318
  workshop 4da88f73: 166 recipe(s)`, the 166 workbench recipes EMF counted).
- Lab run (Kiln's program sends the event, operator = chest 1126, station = workbench 1318):
  ```
  event CraftEvent operator 1126 station 1318 recipe 2cd4c47e x1 injected True canceled False
  event ItemCraftedEvent operator 1126 station 1318 recipe 2cd4c47e workshop 4da88f73 result Crafted (0) outputs [55bf8eb8 x1]
  event ItemPickupEvent receiver 1126 source 0 item 55bf8eb8 x1 slot 1448:0 hidden False
  PASS chest after the craft: output 0x55bf8eb8 0 -> 1, input 0x07e67b5b back to 0
  ```

## 4. Interaction and loot

- `AcceptOfferedInteractionEvent` (0x18): `+8` guest, `+0xc` host, `+0x10` offer; `InteractionAcceptedEvent` same
  layout; `DeclinedInteraction` (0xc, derives from the old `Event`, no timestamp): guest, host, offer. **[dec]**
- Cancel in Pre: the record is retyped to `DeclinedInteraction` {guest, host, offer} (hash `0x13f49577`, data rewritten).
  That is what interaction_attempt sends when it refuses, and the readers (`player_control_not_predicted`) end the
  guest's interaction sequence on it. Verified delivered: `delivery check (Post): DeclinedInteraction queue holds 1
  event(s) of 12 bytes, 1 of them the 1 Kiln declined` **[rt]**.
- `LootInteractionEvent` (0x80): `+8` guest, `+0xc` host, `+0x10` offer, `+0x14` lootAll, `+0x78` bypassInteraction
  (EMF layout; the size byte checked per record). Cancel: same retype. Loot-all with bypass from container 1127 into 1126
  moved all 8 stacks and sent 8 `PickupEvent`s (receiver 1126, source 1127) **[rt]**; the canceled one moved nothing.
- `PickupEvent` (0x30): `+8` float3 position, `+0x14` item, `+0x18` count, `+0x1c` pickedUpBy, `+0x20` pickedUpFrom,
  `+0x24` slot {u32 inventory entity, u8 index}, `+0x2c` hideInUi. **[rt]** (`slot 1448:0`, item ids match the recipe).
- Offers: without players `interaction_offer` (`with PlayerInRange`) keeps offers inactive, so the game declined the
  test's villager → chest attempt (offer 0): `InteractionResultEvent guest 1318 host 1126 offer 0 accepted False`.
  An accepted attempt needs a client.

## 5. Building and terraforming (hooks; no player in the lab, so installed-and-silent only)

| Function | 1285601 | 1273043 | Signature / refusal |
|---|---|---|---|
| `PlaceBlueprint` | `0x225c10` | `0x220030` | `bool (BuildingOp*, const float* xf /*pos m +0, rot +0xc*/, u32 item)`; return → `BuildFinish` r9b **[dis]** |
| `PlaceProp` | `0x2253d0` | `0x21f7f0` | `bool (op, PropDesc* /*+0 template GUID, +0x10 float3 pos*/, u32 item)` **[dec] [dis]** |
| `DismantleBlock` / `DismantleBlock2` | `0x226440` / `0x226fd0` | (call-site patterns do not match) | `bool (op, const float* xf, u32 item)` from player_building_dismantle **[dis]** |
| `PickUpProp` | `0x227f50` | | `bool (op, {u32 entity; u8 flag}*, u32 item)` **[dis]** |
| `BuildFinish` | `0x22a5f0` | `0x224d7c`-area | `(op, InventoryTx*, u32 material, bool success)`: success → commit + undo entry; failure → rollback; always `UiBuildingEvent` {op+0x14c player, material, op+0x150 secondary, op+0x151 replay, success} **[dec] [cap op.player]** |
| `TerraformImpact` | `0x18e0b0` | `0x1888f0` | impact function `Result* (Result* out, args**, ImpactContext* ctx)`; arg i = `ctx[2] + *(u16*)(ctx[0] + 0xe + (handle_i & 0xffff)*0x18)`; arg0 player u32, arg1 WorldTransform (i64 32.32), arg2 u8 TerraformingType; its own early exit for a missing argument writes `{u8 0x18 @0, u32 0 @4, u8 0 @8}` **[dec] [cap]** |

- The positions passed to the building core are world-space float metres (`(float)fixed * 2^-32` in the callers).
- A zone refusal returns `false` without running the core: the caller passes it to BuildFinish (rollback, nothing
  consumed, `UiBuildingEvent.isSuccessful = 0`). Terraforming returns the 0x18 result before any voxel/inventory work.
- Where the server's own permission lives: `BasicPlayerInfos[e-1]+0x9D` holds the user group's permission bits (bit 0
  KickBan = the "admin" bit `IsAdmin` `0x165720` reads, `0x10` ExtendBase checked by handle_crafting_events). The
  build-zone / permission test for building is inside `server_cursor_no_prediction` (writes the ServerCursor error
  code); not isolated further (it computes a code per cursor position, so per-zone refusals belong at the core calls).

## 6. Natural spawns

- `QueueSpawn` `0x5fb1c0` (old `0x5f4da0`) `u32* (cmd?, u32* out_id, ?, u32 parent?, const Guid* tpl, const
  WorldTransform* xf)`: builds a SpawnRequest with a CurrentTransform component (`0x27ac564`, 0x38 B) and calls the
  queued create `0x5fb3d0` (which writes `*out_id`, 0 on failure: "[ecs] Can not create entity with template guid 0").
  Callers: random_spawner (`0x106282`, return address `0x106287` captured as `gameplay/RandomSpawnerSpawnRet`),
  admin_player, animal_reproduction, animal_upgrade_interaction, spawn_random_loot_on_destroy,
  find_npc_to_npc_interactions. **[dis] [cap]**
- random_spawner stores `*out` and branches on 0 (`cmp dword [rax+8],0; jne`), so a refused spawn (`*out = 0`) takes the
  game's own "no entity" path. **[dis]**
- Spawn-rate knob: `GameSettings.randomSpawnerAmount` (+0x54, live) is already in kiln.settings (docs/re/settings.md).
- No player → random_spawner (`with PlayerInRange`) never ran in the lab: `natural spawn events seen: 0`.

## 7. Teleports

- Every game teleport (fast travel, portal, respawn, admin, kiln.world) adds a `Teleport` component through a command
  buffer at the end of a step and program `teleport` applies it the next step (docs/re/world.md). In Pre of that next
  step the component exists with `applied` (+0x48) = 0; target WorldTransform at +0, target entity +0x38, ground search
  +0x49.
- Cancel: `applied := 1`; the game's teleport program then removes the component without moving (`cmp byte
  [rbx+0x48],0` → kind-4 removal). Retarget: overwrite the target position. **[rt]**
  ```
  event TeleportEvent 1160 from (3801.9, 834.0, 1424.6) to (3798.9, 834.0, 1424.6) ... canceled True
  PASS the canceled teleport did not move it ((3801.9, 834.0, 1424.6))
  teleport of 1160 redirected to (3798.9, 834.0, 1426.6)
  PASS redirected teleport landed at (3798.9, 834.0, 1426.6) (asked (3798.9, 834.0, 1424.6) ...)
  ```
  (kiln.world reports its own canceled request as "not moved ... the Teleport component was removed" after 10 s.)
- TeleportCount (u8 at +0) changes after the move: `event TeleportedEvent 1160 from (3798.9, 834.0, 1424.6) to
  (3801.9, 834.0, 1424.6) count 1`.

## 8. Not done / open

- Item drop: see section 10 (Enshrouded has no world drop; discards are inventory actions).
- Pickups by walking over items (`loot_pickup_in_range`, no event) can only be observed (PickupEvent), not canceled.
- Undo (`player_building_undo` → `0x228920`) is not hooked: an undo inside a zone is not refused.
- Whether clients predict building and correct themselves when the server refuses (client checklist).
- The DeclinedInteraction a player gets from a Kiln cancel ends the sequence when Actor+0x88/+0x78 match the offer;
  confirmed only by code reading.
- `d079d101`: an unidentified event type seen twice in the idle world.

## 9. Every entity created / destroyed (kiln.gameplay ENTITY_JOINED / ENTITY_LEFT)

- Two immediate engine functions do all creation and destruction (docs/re/spawn.md for their signatures):
  `createEntity` `0x623ce0` (1273043 `0x61d350`; callers: SpawnWrapper `0x620cf3/0x620d39`, the save loader
  `0x628e66`, `0x635f37/0x635f89`, `0x66e94e`, `0x677ba8`, and the command executor `0x6bf473` that runs the queued
  creates of game programs, e.g. `0x5fb340` used by 40 programs) and `destroyEntity` `0x63e8a0` (1273043 `0x637730`;
  12 callers incl. the game's removal loops `0x60af80`/`0x60b2c0`, the command executor `0x6bf546`, `0x6c8622`,
  `0x6c917f`). **[dis]** Resolved by kiln.spawn's own sigdb entries (`spawn/CreateEntity`, `spawn/DestroyEntity`);
  feature `gameplay.entities` in gameplay.json lists them with the entity-map constants.
- A creature's child entities (weakspot head, hookshot pull, VFX link) are created **inside** createEntity of the
  parent (same thread, same call: the hook sees them with the parent as record `+0x14`), and destroyEntity of the
  parent removes them inside the same call. **[rt]** `join of the spawned Vukah 2542 ... kiln True`, `its child
  entities joined too (3): 2544 VFX_SubEntity_Toggle_Signal_Link_Enemy_Vukah_Slinger Other kiln True, 2545
  Enemy_Weakspot_Vukah_Melee_Head Creature kiln True, 2543 Hookshot_Pull_Entity_To_Player Other kiln True`,
  `its children left (3/3 ...)`.
- Three more callers of the destroy core `0x657090(storage, record*, mode)` bypass destroyEntity: `0x613c90`
  (from `0x69de50`), `0x63f710` (from `0x63de10`), `0x6c4790` (from `0x6c4c70`, from the world shutdown path
  `0x69e229`). Not hooked; assumed world teardown / bulk removal (not verified).
- Kiln reads, in the hooks: the entity record (map `storage+0x148`, hash `0x650c50`, record `+0x10` id, `+0x14` parent,
  `+0x28` TemplateInfo* → GUID), the position (GetEntityTransform) and the template kind from kiln.spawn
  (thread-safe). Only `storage == server world + world.storage` is reported.
- Idle copied world (no player): 0 creations/destructions in 250 steps after the Run state. Game-made queued paths
  seen in the lab through the inventory code: see the events-test lines in docs/kiln/services/gameplay.md.

## 10. Discards: Enshrouded's "toss" (kiln.gameplay DISCARD → ItemTossEvent)

- There is no world drop for players: the reflection enums are `InventoryActionType {One, Half, All, CustomAmount,
  Destroy}` and `ItemActionType {Inspect, Consume, Trash, UnlockPerk, Tint, TintRemove}` (exe strings); `DropItem` is
  only a `PathGridInteractionObjectType`. Getting rid of a stack = the transfer's Destroy or the item menu's Trash.
- `inventory_actions` (`0x1a5af0`, 1273043 `0x1a03f0`; reads PlayerInput, writes ServerConsumedPlayerInput) runs each
  versioned action whose version differs from the stored one **[dis] [cap]**:
  - InventoryTransferAction: PlayerInput `+0x08` version vs ServerConsumedPlayerInput `+0x20`
    (`mov ecx,[rbx+8]; cmp [rax+0x20],ecx; je` at `0x1a6264`), handler `0x19e860` (stores the version first);
    fields `+0x0C` sourceEntityId, `+0x10` targetEntityId, `+0x14` sourceSlotId {u32 inventory entity; u8 index at
    `+0x18`}, `+0x1C` targetSlotId, `+0x24` u8 InventoryActionType, `+0x25` flags. Destroy (4) destroys the source
    slot (`mov r8,[rsi+0x14]` → `0x1b27a0`) when it is set.
  - ItemAction: slotId `+0x1E4` {u32 entity; u8 index `+0x1E8`}, secondarySlotId `+0x1EC`, version `+0x1F4`, u8 type
    `+0x1F8`, consumed `+0x80` (`0x1a610a`); dispatch 0 Inspect `0x1a0950`, 2 Trash `0x1a14a0`, 3 UnlockPerk
    `0x1a0b80`, 4 Tint `0x1a1230`, 5 TintRemove inline; 1 Consume is handled elsewhere.
  Same offsets on 1273043 (sigdb captures).
- No program writes PlayerInput (it is the client's replicated input, applied outside the program schedule), so the new
  version is visible in Pre before inventory_actions runs; writing the version into ServerConsumedPlayerInput makes
  the game skip the action. Not run with a client yet: whether the input reaches the server world before Kiln's Pre in
  the same frame, and what the client shows when the server never acts on its action (client checklist).
- Stack contents for the event: the slot's inventory entity has an 8-slot `Inventory` {u32 item; u32 count; u32 item
  entity} (docs/re/items.md section 7).
