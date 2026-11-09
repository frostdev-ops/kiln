# Inventories: moving items, container names, transfer rules

What `Kiln.Items` (kiln.items v3) gives a mod on top of give, take and read ([services/items.md](../services/items.md)):

- **Move** a stack, a quick stack or a move all between two inventories, **keeping the item's state**.
- Read the **name** a player gave a chest.
- **Rules and events** for the game's quick stack and move all (the R key, a Quick Stack Station): keep a slot, keep an
  item, deny a container, and report what happened.

Ported from [shroudlink](https://github.com/bjck/shroudlink) (MIT, bjck, v0.8.0): `move_stack`, `move_items`,
`chest_name`, and the `keep` and `on_transfer` inventory events. The licence text ships in `licenses/shroudlink-LICENSE.txt`.
Kiln reaches the same game functions by its own signatures (`sigdb/symbols/server.d/items.json`) and runs the moves inside
the game's own transaction, which shroudlink's host does not. Everything is server side. Call it on the tick thread.

## Moving items

```csharp
var sword = Items.Read(player)!.Slots.First(s => s.IsGear && !s.IsEquipped);
MoveResult r = await Items.Move(player, sword.Slot, chest);     // a whole stack
if (r.Succeeded) Log.Info($"entity {r.Changes[0].ItemEntity} is now in {r.Changes[0].To}");   // the same item entity

await Items.Move(chest, slot, player, count: 3);                // 3 of a stack, one item at a time
await Items.Move(chest, slot, player, toSlot: SlotId.Of(page, 2));
MoveResult all = await Items.MoveAll(chest, other);             // the game's own move all
MoveResult qs = await Items.QuickStack(player, chest);          // the game's own quick stack
foreach (var c in qs.Changes) Log.Info($"{c.Amount} x {c.Item} {c.From} -> {c.To}");
```

**State is kept.** The game's slot transfer hands the item entity (its level, upgrades, gem slot, durability) from one slot
to the next inside its transaction. A take followed by a give destroys the gear and makes a new one. After a move, the
item entity is the same, `ItemState.containedInSlotId` and `OwnerRelationship` name the new slot and inventory, and
players and the save follow, as after a drag and drop.

- `Move(from, slot, to, count = 0, options, toSlot)`: `from` and `to` are inventory owners (a player, a chest, a villager);
  `slot` is a slot of `from` as `Items.Read` lists it. `count` 0 is the whole stack, and gear is always 1. The game picks
  the destination unless you give `toSlot`: partial stacks of the item first, then the first empty slot.
- `QuickStack(from, to)` moves the stacks of `from` that `to` already holds a stack of. `MoveAll(from, to)` moves every stack
  that fits. Both run the game's own function, so everything it does for a player, it does here.
- **The ledger.** `MoveResult.Changes` says who gave what to whom (`From`, `To`, `Item`, `Amount`, and the `ItemEntity` of
  gear), in the order things left the source. `Done` and `Stacks` count all of it, `Changes` lists the first 16. It is worked out
  from the inventories before and after, so it is right for the game's own functions too.
- **Options** (`MoveOptions`): `DryRun` runs and rolls back (the result says what would move), `Equipped` lets a stack
  move take a slot a player has equipped or holds (default: refused), `ActionBar` lets it take from and fill a player's
  action bar and lets a move all take the bar along (default: the bar stays), `Silent` hides a quick stack or move all from
  transfer rules and events (for a handler that runs its own).
- **Status** is `Done`, `Partial` (only `Done` of `Requested` fit), `NoRoom` (nothing moved), `NotEnough` (`count` is more
  than the stack), `NoInventory`, `SlotChanged` (not a slot of `from`, or empty), `Refused` (equipped; the game refused;
  `Canceled` says a rule or handler did), `Unavailable`, `Dropped`. Game-side outcomes never throw.
- It runs in the next sim step, in kiln.items' program, like give and take; the task completes on the tick thread one step
  later. A mod that unloads drops its callbacks. `Items.MovesAvailable` says whether this game build has it.

## Container names

```csharp
if (Items.ContainerName(chest) == "Ores") await Items.QuickStack(player, chest);
```

`ContainerName(entity)` is the text a player typed into the Rename button, or null. Only a renamed object has the game's
name component, so a plain chest has none. shroudlink finds the names by scanning the heap for their record layout. Kiln reads
the game's string table, the `CustomPlayerStringsSystem` singleton, and checks each record the same way and by its hash (the
id a chest carries is the FNV-1a 64 of the name). `Items.ContainerNamesAvailable` says whether the build has it; the
call throws `NotSupportedException` if not. It reads game memory: tick thread.

A name replicates and saves the way the game's own rename does. Kiln does not rename.

## Transfer rules and events

The game moves a whole inventory with two functions: quick stack, and move all. A player's R key and a Quick Stack Station
reach them (the station once per chest). So does `Items.QuickStack` and `Items.MoveAll`. kiln.items hooks both, but only once
you ask:

```csharp
using var lock1 = Items.Transfers.Keep(slot);               // this stack stays in the source (a locked slot)
using var lock2 = Items.Transfers.KeepItem(torch, player);  // stacks of this item stay in this player's inventory
using var closed = Items.Transfers.Deny(vault, TransferKinds.QuickStack);   // no quick stack into or out of the vault

[Subscribe]
void OnTransfer(InventoryTransferEvent e) =>
    Log.Info($"{e.Kind} {e.From} -> {e.To}: {e.Moved} item(s), {e.Kept} kept{(e.Canceled ? ", canceled" : "")}");
```

- **Rules are data.** The game runs a transfer on a worker thread inside the sim step, where managed code cannot run, so
  a rule is read there without calling you. `Keep` and `KeepItem` hide the stacks from the game's transfer while it runs (their
  count reads 0, then is put back); `Deny` skips the transfer, and the caller is told it moved nothing. A rule lives until you
  dispose it or your mod unloads. At most 256 are active.
- **The event** is posted on the tick thread in the Pre after the step the transfer ran in, while a handler subscribes. It is
  not cancelable: it already ran. `Moved` is counted from the inventories, `Kept` counts the stacks your rules hid, `ByMod`
  marks `Items.QuickStack` and `Items.MoveAll`, `Player` is whichever end is a player. Dry-run moves emit no event;
  their result still says what would move, with the transfer rules evaluated.
- **To decide per transfer on the tick thread**, cancel the client's action before the game runs it: the client's
  `inventoryTransferAction` arrives as `PlayerClientActionEvent` (Kiln.Players), which holds it one step and can `Cancel()`
  it. `Items.Transfers.ParseAction(e.Payload)` reads it: `RefillStacks` is a quick stack, `MoveAllStacks` a move all, neither
  is one stack from `SourceSlot` to `TargetSlot`. That works on a dedicated server, not for a Quick Stack Station.
- **Cost.** Nothing is hooked until the first rule or subscriber. After that, with nothing registered, a call reads one flag
  and passes through. The hooks are never removed (below). `Items.Transfers.Status()` counts the calls it saw.

### Native handlers (shroudlink's `keep` and `on_transfer`)

A native plugin can decide per transfer, synchronously, as shroudlink's mods do: `transfer_listen` (kiln_items.h). The handler
runs inside the game's call, before its transfer, on the game's worker thread, and can read inventories, keep slots, move
stacks itself and cancel:

```c
static void on_transfer(const KilnItemsTransferEvent* ev, const KilnItemsTransferHost* host, void* user) {
    KilnItemsSlot slots[256];
    uint32_t n = host->read(ev->ctx, ev->from, slots, 256);
    for (uint32_t i = 0; i < n; ++i)
        if (slots[i].item == TORCH) host->keep(ev->ctx, slots[i].slot);        /* never quick stack torches away */
    host->move(ev->ctx, sorted_slot, ev->to, 0);                              /* a stack of its own first, state kept */
}
it->transfer_listen(owner, on_transfer, NULL, &handle);                     /* it = the kiln.items table, v3 */
```

Keep it short; it must not block, and must not call kiln.items' tick-thread functions. `host->move` never takes a player's
equipped or held items and never fills a player's action bar. `host->keep` ignores slots outside the source inventory,
including destination slots. A faulting handler is skipped and logged.

`transfer_end` and `cancel_owner` invalidate callbacks even in a registry snapshot already being delivered. Outside a
transfer callback they wait for callbacks already running, so their code and userdata can then be released. Inside a
transfer callback cancellation does not wait: already-running callbacks may finish. Keep their code and userdata
alive until those callbacks return, or drain again from outside all transfer callbacks before freeing or unloading.

## Next to shroudlink

kiln-shroudlink hosts shroudlink's own server core, which hooks the same two functions (`quick_stack`, `move_all`). Both can be
installed, in either order. kiln.items hooks with safetyhook, which copies a jump already at the function's entry into its
trampoline; shroudlink's detour library does the same on top of ours (it matches its pattern against the exe file when the
code in memory is already hooked). Each runs once per call. Neither is ever removed: a hook taken down under another
one would take the other down with it, so kiln.items leaves its hooks in for the life of the process and ends rules, handlers
and reports with a flag. Removing shroudlink's mods never touches Kiln's hooks.

Lab switch: `KILN_ITEMS_HOOKS=early` installs Kiln's hooks at load, before kiln-shroudlink starts, to try the other order.

## Per-item instance data and affixes

dev.20 does not add these. What the research found, so the next step starts there:

- **No free-form payload was found in the inspected item components.** A piece of gear is a "pide" entity with fixed components: `Level`,
  `PerkContainerNew`, `ItemState`, `Durability`, `OwnerRelationship`, `WeaponGemSlot`, `Fuel`. A plain stack is `{item, count}`
  in an `Inventory` component. There is no spare field to put a mod's value in.
- **Enshwick (MIT) keeps it beside the game.** At the pinned
  [alpha.13 commit](https://github.com/enshwick/enshwick/blob/945a1b8f0c9d1971679d9f3bc3dcecc127a5bcad/crates/internal/loader/src/item_data.rs),
  its `item_data_*` is a table keyed by the item entity within a session and by
  its *place* (the placed container and the slot, or the character and the slot) on a save. Its affixes are rolled
  modifier bundles stored as that item data and applied by Enshwick's own stat engine
  ([source](https://github.com/enshwick/enshwick/blob/945a1b8f0c9d1971679d9f3bc3dcecc127a5bcad/crates/internal/loader/src/stats/affixes.rs)).
  Ambiguous or missing anchors are kept aside and cannot attach to a later item.
- **`EntityData` is session-only on item entities.** A value keyed to the item entity follows `Items.Move` within a
  session. It cannot use Kiln.Saves' template-and-world-position identity: swords have no `CurrentTransform`, and two
  identical swords have the same default position. The restart test exposed tags merging and binding to the wrong
  sword. Kiln now reports `EntityData.IsPersistent(itemEntity) == false`, omits these attachments from new saves, and
  refuses to bind old positional item records. Saved world entities such as chests keep their normal persistence.
- **Deferred stretch:** a persistent item-data API needs an inventory/character-and-slot anchor, item checks and
  ambiguity handling. Affixes also need a modifier engine. Neither is supplied by this work package.

## What was checked, and how

Lab `dev20-wp13` (a copy of the user's world, no player, game build 26430253, Wine):

| What | How |
|---|---|
| Whole-stack and part-stack moves, refusals, dry run | `items-test`: "move_stack: 9 wood logs moved", "move_items: 3 of 9 wood logs moved back", ledger lines |
| Gear keeps its state | `items-test`: the item entity is the same, `Level`, `PerkContainerNew`, `Durability`, `WeaponGemSlot` byte for byte, `ItemState.containedInSlotId` and `OwnerRelationship` updated |
| Quick stack and move all by the game's functions, with the ledger | `items-test`: the ledger puts both containers back slot for slot |
| Container names | The lab's self-test names a placed chest through the game's own rename path (`set_string` + `AddComponent`); `items-test` reads it back, checks the component against the FNV-1a 64 of the text, and after a save and restart reads it from the save |
| A moved piece of gear survives a save | `items-test` phase 2 after a restart |
| Item attachment identity is safe | `items-test` tags two same-template swords before a restart: both are explicitly session-only, neither receives a positional tag afterwards; a saved chest restores its own attachment as a positive control. This is not persistent per-item data |
| Rules, reports | `items-test`: keep slot, keep item, deny, silent, the event's counts |
| Native handler in the game's own move all | the lab self-test: keep, a move of its own first, cancel; the ledger restores |
| Chaining with shroudlink, both orders | a shroudlink mod built from the vendored SDK, in `shroudlink_mods/`, keeps wood out of every quick stack: it runs once per call with Kiln's hook on top of it (default), and with Kiln's hook first (`KILN_ITEMS_HOOKS=early`) |
| Logic without the game | `native/services/items/tests/run.sh` (Wine): destination search, ledger, rules, name records, the hook against a fake inventory world, the hook chain in both orders, a faulting handler |

Unit tests are not game evidence. Only a joined player can show the rest:

## Client checklist (needs a player)

1. Hold R with a chest in reach: stacks the chest already has go in. With a keep rule on a slot of your backpack, that slot stays.
2. With a deny rule on a chest, R next to it moves nothing (and the game shows what it shows for "nothing to deposit").
3. A Quick Stack Station with three chests in range: one `InventoryTransferEvent` per chest.
4. Tap R: move all; a keep rule holds its stack back. The action bar stays unless the game's own move all takes it.
5. `Items.Move` from a player's backpack to a chest, with the player's window open: the slot empties on the player's screen and the gear is in the chest with its level and upgrades.
6. A move of equipped or held gear is refused (`Refused`) by default. With `MoveOptions.Equipped`, check the game's
   unequip behaviour and the resulting equipment and inventory on the player.
7. `Items.Move` into a player's backpack never lands on the action bar.
8. Rename a chest "Ores" in the game; `Items.ContainerName` returns it within a step of the rename reaching the server.
9. A player and kiln-shroudlink's own addon (`dinput8.dll`, MODS tab, slot locks) together: R still honours the addon's locks and Kiln's rules.
