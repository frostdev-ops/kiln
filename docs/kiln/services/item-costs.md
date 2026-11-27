<!-- SPDX-License-Identifier: MIT -->
# kiln.item_costs: exact costs from a selected slot (since 0.2.0-dev.32)

A cost that names **one slot** and what it holds now, paid exactly once, with a **receipt**. Taming steps and pet revives
pay through it (dossier chapter 19e, section 5.2); any server mod may. Native table: `kiln.item_costs` v1
(`native/core/abi/services/kiln_item_costs.h`), published by kiln-items.dll. Managed: `ItemCosts`, `ItemSelection`,
`CostReceipt` in Kiln.Items.

| | |
|---|---|
| Side | server (the authoritative world: a dedicated server, the server half of a hosted game) |
| Threads | `ItemCosts.*` on the tick thread; the native `submit`, `peek`, `acknowledge` on the tick thread, `status` on any |
| Capabilities | `items.costs.exact` (stack takes and refunds), `items.costs.wear` (gear wear and restores) |
| Runs in | kiln.items' serial program `kiln_items_gear` (the game's inventory transaction and the tracked Durability write), the sim step after the call; the receipt arrives in the step after that |
| Events | `ItemCostSettledEvent` for every committed or uncertain receipt; `ItemsTakenEvent` (takes) and `ItemsGivenEvent` (stack refunds) as Kiln.Items' other transactions |

## What a cost is

| Kind | Call | What happens | Refuses when |
|---|---|---|---|
| Stack take | `ItemCosts.Take(selection, count, requestId)` | `count` items leave the selected plain stack, from that slot only, all or nothing (the game's exact-slot take; a take that would touch another slot is rolled back) | the slot is not the owner's now, or holds another item, another count, or gear (`StaleSelection`, `NotAStack`) |
| Gear wear | `ItemCosts.Wear(selection, units, requestId, allowBreak)` | `units` durability off the stamped item in the slot, through the game's own durability debit (`WearDelta`, with no skills: no skill-saving roll, the authored number is exact). Other loaders' hooks on it stay in the chain: what they change is reported (`ModifiedByOtherMod`), never undone or debited again | the item moved, is another instance (stamp), has no durability, is broken (`Broken`), or would go below 1 without `allowBreak` (`WouldBreak`) |
| Stack refund | `ItemCosts.Refund(receipt, requestId)` of a take | the receipt's items given back to the same owner, its slot first; stacks are fungible values, so this is a true refund | nothing fits (`NoRoom`); part fits: `Partial` |
| Gear restore | `ItemCosts.Refund(receipt, requestId)` of a wear | the durability the game applied given back to the same stamped item, never above its max | the item is gone or not the owner's any more, or at its max (`Full`) |

A selection is a snapshot of one slot: `ItemSelection.Of(owner, slot)` from `Items.Read`, or `ItemCosts.Choose(owner,
item, count)` (the first plain stack in link order that can pay). Nothing searches when the cost runs: a changed slot
refuses and **nothing** is taken. A client never names a slot id of its own: the server builds the selection from its own
read (a picker shows the server's rows and sends back which one).

`ItemCosts.Check(selection, amount, allowBreak)` answers whether the selection could pay now, without moving anything
(the cost is checked again when it runs).

## One request id, one debit

Every call carries the caller's request id (a `Guid`, never empty):

- the same id with the same cost while it runs: the same task (nothing is queued again);
- after it settled: the same receipt (Kiln.Items keeps the last 4,096; the native table keeps every request id it let go,
  the last 65,536, and refuses them `RequestClosed`);
- another cost under the same id: `RequestConflict`, nothing submitted.

So a network retry sends the **same** id and can never pay twice; a new intended payment takes a new id.

## Receipts

`CostReceipt(Item, Requested, Taken, Committed)` plus `RequestId`, `NativeTicket`, `Kind`, `Selection`, `State`,
`Disposition`, `Refusal`, `Reason`, `DurabilityBefore`/`After`, `RefundOf` and `Step`.

| State | Means |
|---|---|
| `Committed` | it happened: `Taken` is what moved (items, or the durability the game applied) |
| `Refused` | nothing changed: `Refusal` and `Reason` say why |
| `Uncertain` | a fault inside the game's change: `Taken` is what was read afterwards, and whether the cost was paid is not known. Never submit it again as if nothing happened, and `Refund` never refunds it |

`Disposition`: `Exact`; `WearWaivedByWorld` (a world with durability off: the use costs 0, as the game's own debits
there); `ModifiedByOtherMod` (another hook changed the wear, negative for a repair; the use happened once);
`Partial` (less moved than asked).

Not promised: atomicity with anything else. A committed receipt says the inventory changed in the running world; a pet
record, a sidecar and the character's own save are other stores, saved on their own, and a crash between them can lose
either side. Kiln exposes the receipt, never an unverified success.

## Errors

Bad arguments throw `ArgumentException` (no owner or slot, an empty request id, a count outside 1..the selection's count,
a stack selection for a wear or a gear one for a take, wear outside 0..32767); a capability that is off throws
`KilnUnavailableException` naming `items.costs.exact` or `items.costs.wear`; everything the game or the world refuses is a
receipt with `Committed` false. Not on the tick thread: `InvalidOperationException`.

## What is not done

- The game's break notice is not sent for a Kiln wear that breaks a tool (`allowBreak`): `kiln_items_gear` has no
  NotifyEvent output. The tool still breaks (durability 0, the game marks it broken as for its own debits).
- A wear is the game's helper called from Kiln's own program, not from the game's tool action: the game's per-action
  rules (the hit cap, the projectile spawn) do not apply to it, and kiln.toolwear's rules never change it.
- A refund is a give: the game's "+N item" popup is not shown and no PickupEvent is sent (a refund must not count as a
  pickup for quests).

## Native table

```c
KilnResult submit(KilnOwner, const KilnSelectedCost*, uint64_t* ticket);   /* tick thread */
KilnResult peek(KilnOwner, uint64_t ticket, KilnCostResult* out);          /* QUEUED until it ran */
KilnResult acknowledge(KilnOwner, uint64_t ticket);                        /* a QUEUED ticket refuses */
KilnResult status(KilnItemCostsStatus* out);                               /* any thread */
```

`KilnSelectedCost` (80 bytes) names the world (`world_epoch`, `KilnItemCostsStatus.world_epoch`), the holder and, for a
player holder, its session (`(handle << 32) ^ joined_step` from kiln.players; a chest passes 0), the slot, the item, the
expected count, the item entity and stamp (gear), the amount, `allow_break` and the request id. `KilnCostResult` (264
bytes) is the receipt. A receipt is kept until its owner acknowledges it, or 10 minutes after it settled; 1,024 held at
most (`KILN_ITEM_COSTS_BUSY`). A world that closes refuses what did not run (`WORLD_ENDED`); a program that did not run
for 10 s refuses it (`NOT_RUN`).

Host test: `native/services/items/tests/costs_test.cpp` (the shape checks, the wear arithmetic, the receipt table).
Managed: `managed/tests/Kiln.Items.Tests/ItemCostsTests.cs`. Lab: `mods/tests/item-costs-test`.
