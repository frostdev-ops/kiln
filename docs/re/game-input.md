# Game input: the local player's resolved actions (kiln.client.game_input)

Client 26430625 (`enshrouded.exe` sha256 `5d9ef2f1…`), static only: python and capstone over a read-only copy of the
exe, `sigdb/types/client` for the reflected layouts, `sigdb/programs/client` for the program list. Nothing here ran in a
game. Used by kiln.client.game_input ([services/client-game-input.md](../kiln/services/client-game-input.md); sigdb
feature `client.game_input_sites`, `sigdb/symbols/client.d/client-game-input.json`; checked by
`tools/sigport/tests/test_client_game_input.py`). The frame around these sites (the client frame's order, the writer,
the version, who writes which field) is [client.md, "The local input path"](client.md#the-local-input-path-building-input);
this page adds what game input needs and checks the Combat And Attack Tweaks author's sites 19 and 20 (beta.6 notes,
"HOLD TO LIGHT ATTACK INPUT"). The dedicated server has none of this: it receives the input message.

Tags: **[dis]** disassembly, **[refl]** the game's reflection.

## Short answers
- **One sample per client world step.** The client frame `0x87a290` calls the local input writer `0x3af340`
  (`0x87a778`) and then the input message `0x3a8970` (`0x87a810`) only when the world's step counter changed, and skips
  the writer while `0x1037b60([r13+0x78])` says input is blocked; the message still goes out then. **[dis]**
- **The digital word is the game's resolved input.** `0x2477d0` (called from the writer's sample `0x3b1e20`) walks all
  56 `PlayerInputType` bits (`cmp rdi, 0x38`) and sets or clears each from the game's input action it maps to (a jump
  table at `0x248528`: bit 0 `MainhandAction` is action `0x0a` ... bit 55 `Sneak` is `0x7a`), after its own binding,
  controller and context handling (`0xe9f780`, `0xea2990`, the blocked set `0x473a40`); `bts`/`btr` on
  `[rsi + 0x3a8]`. **[dis]**
- **Kiln reads and writes at kiln.client's observation point `0x3af55f`** (already a link for building input and
  kiln.rules' camera check). Between it and the writer's `ret` (`0x3af66d`) the only stores into the record (`r14`) are
  the three `mask` stores at `+0x3a0`: a word written at `0x3af55f` is the word the message copies and the client
  programs read. **[dis]**
- **The stale press guard sits at `0x3a8a03`**, the message's first instruction after its "step already sent" early
  return, right before it copies the record: `r13` the record, `edi` the entity, `r14d` the step. **[dis]**
- **The author's two sites match** at their RVAs: site 19's pattern at `0x3af639` (their selected `0x3af649`), site 20's
  at `0x3a89f9` (selected `0x3a8a03`, the same instruction as Kiln's guard). Their register contracts agree with the
  disassembly. **[dis]**

## The local input writer, sample by sample (`0x3af340`)
| RVA | What |
|---|---|
| `0x3af35b` | `ebx = [rcx + 0x65c]`: the local player's entity (`client/input.entity`); `rbp = rcx` the client game's input object |
| `0x3af38b` | `GetComponent`-style lookup `0x9b2f20` with the type id at `0x152b58c` (`ClientPlayerInput`), `r14` = the record |
| `0x3af3d3` | the input sample `0x3b1e20`: the version `0x9bd990([rbp+8])` (the client world's step) and `0x2477d0(rcx = r14)`, which writes the whole digital word |
| `0x3af47c..0x3af55b` | the camera block (skipped to `0x3af55f` without a camera object) |
| **`0x3af55f`** | `mov rcx, [rbp + 0x3da8]`: the play state (`client/input.play`); kiln.client's observation, kiln.client.game_input's sample |
| `0x3af56d` | `mov ecx, [rcx + 0x14f68]`: the held item id (`client/play.held_item`) |
| `0x3af573` | `call 0xe94470`: the item registry's find (Kiln's `items/ItemRegistryFind`); `rdi` = the held `ItemInfo` from `0x3af586` |
| `0x3af5a1..0x3af641` | `mask` (+0x3a0, `ClientPlayerStateMask`, `keen::ecs::ClientPlayerFlags`): bit 0 `IsInMultiToolBuildingMenu` from `0x1221a90(hud)`, bit 1 `IsInBlueprintMenu` (HUD screen `0x1d`), bit 3 `IsInLootingMenu` (`0x15`), bit 2 `IsInLogicScrewdriverMode` (the held item's flag `+0x5d8 & 0x40` and `[rsp+0x30] & 4`); the HUD screen is the play state's `+0x1cd8` byte, the one kiln.ui reports |
| `0x3af649` | `mov r14, [rsp + 0xd0]`: the record's register restored (the author's site 19) |

So the writer runs while game screens are open (it records some of them in `mask`): the menu gate is Kiln's to apply to
synthetic input; the game's own bits are already context-filtered.

## The action bits (`keen::ecs::PlayerInputType`, 56 values) **[refl]**
What Kiln names (`client/pit.*`, fixed values checked against the reflection):

| Kiln action | Down (held) | Family (also cleared by Press/Release) | Notes |
|---|---|---|---|
| `PrimaryAttack` | 0 `MainhandAction`, 51 `RangedCombatShoot`, 52 `RangedCombatShoot_Tap` | + 1 `_Tap`, 2 `_Hold`, 3 `_Release` | the mainhand (melee, tools) and the ranged shot; the press pattern is the one the author's hold-to-repeat writes (beta.6 constant `0x18000000000001`, clear mask `0x1800000000000f`) |
| `SecondaryAttack` | 50 `RangedCombatArm` | - | the aim of bows and staffs |
| `Block` | 30 `Block` | - | |
| `Dodge` | 31 `Evade` | - | |
| `Jump` | 32 `Jump` | - | |
| `Interact` | 5 `ContextualAction` | + 6 `_Tap`, 7 `_Hold` | building's dismantle uses 5 and 7 |

`_Tap`, `_Hold` and `_Release` are separate input actions of the game (their own jump-table entries), not derived by the
server: a press Kiln writes for one sample carries no `_Hold`, so a held button turned into one-sample presses gives
light attacks instead of the heavy attack the game's own hold would give. Which server program reads which bit was not
traced (as in client.md). `ClientPlayerInput.toggleConfig` (+0x598, `configuredAsToggle`) and `doubleInputBindings`
(+0x5a0) say which actions the player set to toggle or double-bound; Kiln reads neither.

## The input message (`0x3a8970`)
| RVA | What |
|---|---|
| entry | `r8d` = the step to send (the client frame's `movd r8d, xmm6` at `0x87a7fe`, the low dword of `0x9d5440`'s {counter, time}: the world's step counter, the version the writer stamped); `r9` = the input object |
| `0x3a89a5` | `r12 = r9 + 0x658` (`client/input.slot`): `[r12]` compared with the stream id, `[r12 + 4]` the local entity (= `input.entity`), `[r12 + 0xc]` the last step sent |
| `0x3a89e3` | `GetComponent(out, [r9 + 8], type id at 0x152b58c, edi)`: `r13` = the same `ClientPlayerInput` the writer filled |
| `0x3a89f4` | `cmp r14d, [r12 + 0xc]; jne 0x3a8a03`: a step already sent returns early (`mov byte [rsi], 1`) |
| **`0x3a8a03`** | `lea rcx, [rbp - 0x48]` (4 bytes), then `movaps [rsp + 0xf10], xmm6` (8 bytes): neither rip-relative, no branch lands inside them (the only branch to the area is the `jne` to `0x3a8a03` itself) |
| `0x3a8a80..` | the 952-byte copy of `ClientPlayerInputData` out of `r13`, then the message |

**Why the guard.** While input is blocked the writer is skipped, so the record keeps the last word written, and the
message of the next step sends it again. For the game's own sample that is the game's choice; for a press Kiln wrote, it
would turn a one-sample tap into a held button. At `0x3a8a03` Kiln clears only the bits it set at its last sample,
when the record, the entity, the client session and the whole word are still as it left them and the message's step is
not that sample's (so the message of the sample itself still sends the press). The author's site 20 is the same rule
at the same instruction.

## Time and the bow's draw
- **The client world's time** `0x9bd9a0`: `step = [world + 0xe17e9c]`, `period = [world + 0xe17e80]` (keen::Time per step);
  step 0 gives 0, a period of `INT64_MAX` or `INT64_MIN` (keen's +-infinity) itself, else `step * period`. Programs use
  the same formula on their execution view (`0x9c4dc0`: view `+0x6c` and `+0x50`). **[dis]**
- **Elapsed** `0x25c1e0(now, start)`: `now - start` with the infinities (`+inf - +inf = 0`, `x - +inf = -inf`, ...). **[dis]**
- **The ranged weapon update** `0x478490` (called from `server_player_ranged_weapon`'s body, client copy `0x2ebc70`, after
  the parameter builder `0x476700`): the state machine `Holster 0, Aim 1, AimAndCharge 2, Fire 3, FireEaseOut 4`
  (`keen::ranged_weapon::State`); the fresh parameters are copied into its state only when the state changes from
  Holster or to a lower state (`0x4785db`), so a draw keeps the durations it started with; the charge test
  `0x478260` for AimAndCharge is `elapsed(now, start) >= chargeDuration` (`cmp rax, [rbx + 0x20]; setge`). **[dis]**
- **`keen::ecs::NetworkRangedWeapon`** (hash `0x959918c4`, 112 bytes) **[refl]**: `currentState` +0x00,
  `weaponParameters` +0x08 (`weaponType` +0x08: Bow 0, Staff 1, Wand 2; `chargeDuration` +0x10; `fireDuration` +0x18),
  `stateStartTime` +0x28, `weaponInstance` +0x30 (`itemId` +0x38).
- **Kiln's `BowFullyDrawn`**: the local player's `NetworkRangedWeapon` in the client world (the game's `GetComponent`
  `0x9b2ec0`) holds the held item (`itemId`) as a bow, `currentState` is AimAndCharge, and `elapsed(world time,
  stateStartTime) >= chargeDuration`, with the world time from the sample's step and the world's period. The charge
  duration is the one the game wrote at the draw's start, after skills and after kiln.combat's bow timing (when a
  speed is set). The same reads as the author's `BowRepeat.cs`. **[hyp]** on a joined client the replicated
  `stateStartTime` is the host's clock; that it lines up with the client world's step was not checked statically (the
  author relies on the same).

## The held item's kind
The held item id is the writer's own lookup key (`0x3af56d`). Kiln classifies it through kiln.items' catalog
(`KilnItemInfo.equip_slot`, `ItemInfo +0x104`, `keen::EquipmentSlotType`): MeleeWeapon (1) is Melee, Tool (2) is Tool,
RangeWeapon (5) is Bow when `NetworkRangedWeapon` holds that item as a bow, or, while it holds another item, when the
item's name has "Bow" in it (bows and staffs share RangeWeapon; wands are MidRangeWeapon 4, docs/re/abilities.md).

## Kiln's own input state, and why the two input paths do not fight
- **kiln.ui's raw input** (`native/services/ui/input.cpp`, `input_own.cpp`): window messages and raw input records,
  before the game sees them; it keeps keys from the game while Kiln captures input and owns presses for subscribers
  (docs/re/input-ownership.md). Game input sits after the game's own mapping: a key kiln.ui kept or owned never reaches
  the digital word, so a mod reading `GameInput` never sees it, and a synthetic `Press` never goes through kiln.ui.
- **The gates** are kiln.ui's state, read at each client world Pre: `screen()` (`ui_state` PLAY, `hud_screen` 1 InGame,
  the play state's `+0x1cd8` that the writer itself reads), `status()` (`menu_open`, `capturing`), plus the foreground
  window of the process. kiln.ui has no focus field; the OS foreground is the same fact.

## What was not checked
- The server programs that consume each bit (the light/heavy attack decision) were not traced; the press pattern is the
  author's, whose hold-to-repeat predates Kiln's API.
- `0x1037b60`'s blocked condition (`[rcx + 0x1e8]` or one slot's `+0x10`/`+0x14`) was not followed to its writers.
- Nothing here ran in a game.
