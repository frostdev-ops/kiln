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

## Persistent player storage (since 0.2.0-dev.30.7)

For a quiver, spellbook, or extra bag, use `Items.RegisterPlayerStorage`. Kiln owns the inventory pages and persists
their game item records separately, paired with the character save; the player's normal backpack and crafting do not
include them. `PlayerStorage.Store` and `PlayerStorage.Retrieve` use the moves below. Start with
[player-storage.md](player-storage.md); save/trust/removal limits are in the
[service contract](../services/player-storage.md).

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
  `slot` is a slot of `from` as `Items.Read` lists it. `count` 0 is the whole stack, and gear is always 1. Kiln picks
  the destination unless you give `toSlot`: partial stacks of the item first, then the first empty slot.
- **Where a move into a player lands** (issue #109). A player's inventory links its equipment pages first, and before this
  fix a move without `toSlot` could put a stack into an empty equipment slot, where the player never sees it. Now, for a
  player or an NPC (`InventorySetup` flags), the search only uses the **backpack**: the Generic pages of the player's own
  inventory and the pages of the worn backpack (behind its Virtual link), in the order `Items.Read` lists them. It never
  uses equipment, currency or customization pages, nor the action bar unless you pass `MoveOptions.ActionBar`. A backpack
  that is not worn adds no pages. When nothing fits, the result is `NoRoom` ("no room in entity N's backpack") and nothing
  moves.
- **`toSlot` into a player** must be a slot the item may enter: a backpack slot (an action-bar slot only with
  `MoveOptions.ActionBar`), a currency-page slot for a currency item, or a worn equipment slot (`SlotSelection`) whose
  equipment type is the item's, to top up the worn stack (arrows onto the worn arrows, bait onto the worn bait). Anything
  else is `Refused` with the reason in `Message`, and nothing moves: an **empty** equipment slot (a move is no equip: use
  `Items.Equip`, the game's own path, which also marks the slot worn and moves a replaced backpack's contents), the
  backpack slot, a material into a ring slot, an equipment-page slot no equipment slot shows, a slot that is not the
  player's. A `toSlot` the item may enter but that is full or holds another item gives way to the backpack search.
- **Checked after the move.** A stack move is rolled back and `Refused` when any of it arrived outside the slots above, or
  left the slot without arriving anywhere, so `Done` always means the items are where the player can take them.
- Chests and other containers keep the old rule: every page, and a `toSlot` that is not theirs or does not fit gives way to
  the search.
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

### Checked moves

`Items.Move` names its source by slot number: when it runs, it moves whatever is in that slot then. A move a player asked
for on an old view (a drag and drop in a mod's own inventory screen, a request that arrives twice) can then move another
stack, or another piece of gear. `Items.MoveChecked` (since 0.2.0-dev.32) takes the slots as a read listed them and moves
only while they still hold that:

```csharp
if (!Items.Available || !Items.CheckedMovesAvailable) return;    // kiln.items, and checked moves on this Kiln
var seen = Items.Read(player)!.Slots.First(s => s.Item == flint);   // what the player dragged
var spot = Items.Read(chest)!.Slots.First(s => s.IsEmpty);          // where they dropped it
MoveResult r = await Items.MoveChecked(player, seen, chest, 3, spot);   // exactly 3, exactly into that slot
if (r.Status == ItemsStatus.SlotChanged) { /* the view was old: send the player a fresh one */ }
```

- The source must still hold the item and the count it held, and gear the same item entity with the same stamp
  (`seen.Ref`). The destination, when you give one, must still hold what it held (an empty slot: still empty), and the
  whole count must fit there: nothing goes to another slot and nothing moves in part. Without a destination Kiln chooses,
  as for `Items.Move`, and a part may move (`Partial`).
- kiln.items checks this inside the move's own transaction, in the step the move runs, so nothing can change between the
  check and the move.
- `SlotChanged`: a slot changed (read again). `NoRoom`: the destination cannot take the whole count. `Refused`: as for
  `Items.Move`. Nothing moved then, in either inventory.
- The same request twice moves once: the second finds its source changed. A plain stack has no identity, so a stack of the
  same item and count put in its place passes.
- No options: equipped and held items and the action bar are refused, as by `Items.Move` without options.
- `Items.CheckedMovesAvailable` is false on an older Kiln (then `MoveChecked` is `Unavailable` and says to update Kiln) and
  while moves are off. Gear also needs capability `items.instances`; without it a checked move of gear is `Unavailable`.
- The test kit does not model moves: offline, `CheckedMovesAvailable` is false and `MoveChecked` is `Unavailable`.

## Using a consumable for a player (0.2.0-dev.30.3)

To make a player eat, drink or use a potion from code, put the item in one of their own slots and call
`Abilities.UseItem(player, slot)` (Kiln.Abilities, server, tick thread). The game then does what it does for a hotbar
use: the item's own sequence plays, one item is taken from that slot at the sequence's consume event, and the buff is
applied with the item's own values and time, so the player sees the same effect lines and timer as when eating by hand.
A remote chest list moves the stack in first:

```csharp
var moved = await Items.Move(chest, chestSlot, player, count: 1);
if (moved.Succeeded && moved.Changes.Count > 0)
    Log.Info($"eat: {Abilities.UseItem(player, moved.Changes[0].To)}");
```

The answer is an `ItemUseResult`: `Started`, or why nothing happened (`NotConsumable`, `NotOwnSlot`, `EmptySlot`,
`SlotChanged`, `NotAPlayer`, `Dead`, `NotReady`, `Busy` while another action or use plays, `ServerOnly` on a
joined client). The item is not gone at the call: `PlayerConsumedEvent` says when it went, and a `PlayerConsumeEvent`
handler or a block's `CanEat` can still refuse it, as for a hand use. Use it rather than `ApplyBuff` for food: a food's
buff type holds no numbers (148 foods share 16 buff types). Details, the checks and what a real game still has to
confirm: [kiln.abilities, Using an item](../services/abilities.md#using-an-item-since-020-dev303-mod-api-0213).

## Container names

```csharp
if (Items.ContainerName(chest) == "Ores") await Items.QuickStack(player, chest);
```

`ContainerName(entity)` is the text a player typed into the Rename button, or null. Only a renamed object has the game's
name component, so a plain chest has none. shroudlink finds the names by scanning the heap for their record layout. Kiln reads
the game's string table, the `CustomPlayerStringsSystem` singleton, and checks each record the same way and by its hash (the
id a chest carries is the FNV-1a 64 of the name). `Items.ContainerNamesAvailable` says whether the build has it; the
call throws `NotSupportedException` if not. It reads game memory: tick thread.

A name replicates and saves the way the game's own rename does.

### Renaming (since 0.2.0-dev.30.3)

```csharp
foreach (var chest in Items.FindInventories(InventoryKinds.Containers))
    if (Items.ContainerName(chest) is null) await Items.SetContainerName(chest, "Unsorted");
```

`Items.SetContainerName(entity, name)` does what a player's Rename button does, on the server and through the game's own
rename: the name replicates to every player, is in the next save, and shows in the game's Rename screen and in
`ContainerName` (GitHub issue #112, thanks Fuzen777). Mod API 0.2.13; capability `items.rename`;
`Items.ContainerRenameAvailable` is true where the server world runs (a dedicated server, or the game client hosting).

- **The name** is checked like the game's Rename field: at most 48 characters and 64 bytes of UTF-8
  (`Items.ContainerNameMaxChars`, `Items.ContainerNameMaxBytes`), no control character but tab, any script and emoji,
  kept as given (no trimming), no profanity filter. `""` clears the name (the server's rename does; the game's own field
  never sends an empty name).
- **Only a container**: an entity with an inventory of its own that is neither a player's nor an NPC's. A Flame Altar or
  another renameable object without an inventory is refused.
- **Refusals throw at the call**: `ArgumentException` (no entity, it does not exist, not a container, a name the field
  would not take; the message says which), `InvalidOperationException` on a game client joined to another machine's
  world, `KilnUnavailableException("items.rename")` on an older Kiln or an unknown game build.
- **The task** completes on the tick thread in the next step: `true` when the game took the name (also when the chest
  already had it), `false` when the rename could not run there (the chest went away, the game's table of 1024 different
  names was full, the world closed); the reason is in kiln.log. A mod that unloads drops its callbacks; the rename still
  runs.
- Names are set where the world runs: on a joined game client, let the server side of your mod do the rename.

Details and the native side: [services/items.md](../services/items.md#renaming-a-container-v9-since-020-dev303).

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
including destination slots. A faulting handler is skipped and logged. A mod's dry-run move (`KILN_ITEMS_MOVE_DRY_RUN`) also runs
the handlers (not when it is also `KILN_ITEMS_MOVE_SILENT`), with `KILN_TRANSFER_DRY_RUN` set in `ev->flags`. Everything is rolled back afterwards: keep and cancel as
usual (so the dry run predicts the real move), but do not count it, log it or change anything outside the transfer.

`transfer_end` and `cancel_owner` invalidate callbacks even in a registry snapshot already being delivered. Outside a
transfer callback they wait for callbacks already running, so their code and userdata can then be released. Inside a
transfer callback cancellation does not wait: already-running callbacks may finish. Keep their code and userdata
alive until those callbacks return, or drain again from outside all transfer callbacks before freeing or unloading.

## Next to shroudlink

kiln-shroudlink hosts shroudlink's own server core, which hooks the same two functions (`quick_stack`, `move_all`). Both can be
installed, in either order. kiln.items hooks through [kiln.hooks](../services/hooks.md), whose one detour per function copies a
jump already at the function's entry into its trampoline; shroudlink's detour library does the same on top of ours (it matches its pattern against the exe file when the
code in memory is already hooked). Each runs once per call. Neither is ever removed: a hook taken down under another
one would take the other down with it, so kiln.items leaves its hooks in for the life of the process and ends rules, handlers
and reports with a flag. Removing shroudlink's mods never touches Kiln's hooks.

Lab switch: `KILN_ITEMS_HOOKS=early` installs Kiln's hooks at load, before kiln-shroudlink starts, to try the other order.

## Data on one item (`InstanceData`)

A mod can keep a value on one particular item (this sword, not "a sword", and not its slot), and the value stays with
that item when it moves, when the world saves, and after a restart. The API is in Kiln.Saves
([services/saves.md](../services/saves.md), "Instance data"); the items come from Kiln.Items' item instances
([services/items.md](../services/items.md), "Item instances (v4)"). Sample: `mods/samples/ItemData` (`/notch` on the
weapon in your hand).

```csharp
public sealed class Notches { public int Count { get; set; } }

private InstanceKey<Notches> _notches = null!;
public override void OnLoad() => _notches = InstanceData.Key<Notches>("notches");   // one typed key per kind of value

// an ItemInstanceRef: Items.Held(player), Items.Equipped(player, slot), Items.Read(holder).Slots[i].Ref, a give, take or move result
void Notch(ItemInstanceRef sword) => sword.Get(_notches).Count++;   // the live object; saved with the world
```
`Get`, `TryGet`, `Has`, `Set`, `Remove` on the item; `InstanceData.All(key)` lists the items that carry it in this
session, `InstanceData.Parked(key)` the records waiting for their item or set aside, with the reason. Three events
report what happens to stored data: `InstanceDataRestoredEvent` (back on its item after a load or a join: `Exact` or
`Moved`), `InstanceDataParkedEvent` (waiting: the holder is not loaded, or the player left) and `InstanceDataLostEvent`
(`Destroyed`, `Unmatched`, `Ambiguous`). Admins list everything with `/instances [player|entity]`. A record set aside
(or still waiting) can be put on an item by the mod that owns it, `InstanceData.Claim(parked, item)`, or dropped,
`InstanceData.Discard(parked)` (dev.27); an admin does it with `/instances claim <record> <player|holder> <flat slot>`
and `/instances discard <record>`.

### Showing a value in the player's game (dev.27)
(dev.28: to draw such a value in the game's own item detail view, slot bar or held indicator, or to list it in Kiln's
Item details window, see `Ui.Values` in [../ui.md](../ui.md#values-in-the-games-own-ui-dev28). A synced value is keyed by
the server's entity; `UiItemView.ServerEntity` is not proven in dev.28, so a provider that needs one item's instance
passes, and a provider by item type works.)

A value stays on the server unless its key says who may see it. `Sync = InstanceSync.Holder` sends it to the player who
holds the item, `InstanceSync.Nearby` also to every player within `NearbyRadius` metres of the item's holder (another
player, a chest). The mod then runs on both sides (`"side": "both"`): the same `InstanceData.Key` line declares the key
on the server and in the game, and the game reads the server's copies.

```csharp
_notches = InstanceData.Key<Notches>("notches", new InstanceKeyOptions { Sync = InstanceSync.Holder });

[Subscribe]   // in the player's game: a copy arrived, changed, moved or went
private void OnSynced(InstanceDataSyncedEvent e)
{
    if (e.Mod == Context.Info.Id && e.Change == InstanceSyncChange.Set && e.Item.TryGet(_notches, out var n))
        Hud.Toast($"{n.Count} notch(es) on the item in slot {e.Item.FlatSlot}");
}
```
`InstanceData.Synced()` lists every item this game has copies for, `Synced(entity)` and `Synced(entity, key)` read one
(any thread, so a UI frame may call them). A copy says which item it is by the server's ids: the item entity, the holder,
the flat slot, the item type, and `Own` (this game's player holds it). Kiln keeps the copies current through `Set`,
edits of the live object, `Remove`, moves, gifts, chests, losses and claims, and sends a joining player the whole set.
A player without Kiln, with an older Kiln, or without a mod that reads synced values gets nothing and notices
nothing; a value never goes to a player its key does not name. An update lost on the network is made good (the game
acknowledges what it takes, and starts over with the whole set after a loss). Limits (64 KB per value, 96 KB per
update with the rest following a quarter of a second later, 4,096 items per game), the wire format and what is proven are in
[services/saves.md](../services/saves.md#on-players-games-dev27). The sample `mods/samples/ItemData` shows its notch
count this way.

**Showing it on the item's slot (0.2.0-dev.30):** `Ui.Inventory.Mark(item, mark)` draws a mark over the slot the
game draws for that item (backpack, chest, hotbar), and `Ui.Inventory.OnClick` gives your mod a click on a slot with the
item's entity id (the server's id of it), for example to mark it on the server: see [../ui.md](../ui.md#the-games-inventory-slots-020-dev30).
Lines in the game's own item tooltip come with `Ui.Tooltip` (the equipment work); until then a mod shows a synced value in
Kiln's Item details window, a screen of its own, a HUD line or a notice.

### Which API
| The thing | API |
|---|---|
| an item with an item entity of its own: gear, tools, a watering can, wherever it is (a chest, a backpack, the action bar, worn, a tombstone) | `InstanceData` |
| a plain stack (wood, arrows): it has no item entity, so there is nothing to keep data on | none |
| a placed object (a chest itself, a station, a prop) or a creature | `EntityData` |
| a player | `PlayerData`, or `CharacterData` per character |

`EntityData.IsPersistent(itemEntity)` stays false: an item entity has no position, and `EntityData`'s
template-and-position identity once bound a tag to the wrong sword after a restart (dev.20). Use `InstanceData` for
items.

`Sync` says which players get a value on their game (above); `Persist` says where a value is kept. `Persist.World` (the default) keeps the value on this world's save, the item in a
player's inventory included. `Persist.Session` keeps it until the server stops. **`Persist.Carried` is not in this
release:** data that travels with a character between worlds is not available yet, and declaring such a key throws
`KilnUnavailableException("saves.instance_data.carried")` saying so.

### How a value finds its item again
The game keeps no id for an item: every load and every join makes new item entities from compact save records. Kiln
writes, at each save, where each item with data is (its holder and slot), the fields of its save record (item id,
level, perks, gem, dye) and the holder's whole list of items. After a load or a join, per holder:
- the holder is exactly as it was saved and as the loader made it: each record goes to the item in its own slot, two
  identical items included;
- otherwise a record goes to an item only if that item is the one item of the holder with those fields, wanted by no
  other record (`Exact` in its slot, `Moved` elsewhere);
- when that is not certain (two identical items in a holder that changed, a holder found twice within its radius, a
  record without the holder's list), every record concerned is **set aside**: reported (`Ambiguous`), listed by
  `Parked` and `/instances`, kept, and never attached by itself, not in a later session either (a mod claims or
  discards it: `InstanceData.Claim` / `Discard`);
- a record whose item is not there waits, and is dropped after 3 sessions (`Unmatched`). A player's records wait for
  the character's next join.

**The one case nobody can detect:** two items with equal save records swapped while no Kiln was watching (in another
world without Kiln, or edited offline), with nothing else in the holder changed; or an item taken away while no Kiln
was watching and a look-alike put in its place. Both look exactly as if nothing had happened.

**So two identical items trade their data when they were swapped where Kiln did not see.** Two items with equal item
id, level, perks, gem and dye, and the same durability and fuel, whose slots were swapped in another world, or anywhere
Kiln did not see, come back by slot on the newest exact load, and the two items trade their data. Nothing reports it.

Caps: 256 records per holder per mod (on an item, waiting and set aside together; the oldest waiting or set-aside go
first) and 64 KB per value as JSON (`Set` throws `ArgumentException` above it). A destroyed item (trash, crafting, taken
away) drops its data at once with `Destroyed`.

**When a player leaves** (the order in the game, server 26430253): from the menu, the game saves first (Kiln writes the
character's records in that save), kiln.players reports the leave, and only then are the player's item entities
destroyed, so the records wait with the character for the next join. After a crash or a lost connection there is no
leave save: the client keeps the character file of the last world save it received, and Kiln compares the inventory
that comes back with every state the client may have kept. A server shutdown takes everyone at once.

### What is proven where
**Items in chests and other world holders are proven on a test server; items in a player's inventory wait for the
guided client pass.** On a lab server with a copy of a real world and no player (dev.26, `mods/tests/instances-test`):
two identical swords and their look-alikes, each with its own value, moved within a chest, between chests, swapped,
moved by the game's move all and by a move no hook sees, kept their own values after a stop, after `kill -9` between
saves and right after one, and after a restored backup; 1,000 tagged identical swords in 42 chests came back on their own
items after a restart; a mod that was not loaded for one boot found its values on its swords after they had moved;
whenever a holder had changed in a way Kiln could not follow, the records concerned were set aside. No item got another
item's value in any run. A lab cannot make a real player: a simulated player has no inventory, so the lab shows only that
a character's records wait across its leave, a rejoin in another slot and a restart, are listed, and attach to nothing.
Everything about items in a backpack, on the action bar or worn is in the checks below, which have not been run yet.

### Affixes
An item's perks are not stored on the item: an instance holds only how many perks it has and how many are unlocked,
and which perks those are comes from its item type, so two instances of one item can never have different perks. Rolled
affixes are therefore item data plus an engine that applies them, and since dev.27 Kiln has both:
[affixes.md](affixes.md) (`Affixes.Define`, pools, `pool.Roll(item)`; the rolls are item data of Kiln's own, they act
while the item is worn or in hand, and they go to the games of players near the item). A fixed set of perks can still
be its own item variant.

Enshwick (MIT) built the same idea first: at its
[alpha.13 commit](https://github.com/enshwick/enshwick/blob/945a1b8f0c9d1971679d9f3bc3dcecc127a5bcad/crates/internal/loader/src/item_data.rs)
item data is keyed by the item entity in a session and by its place (container and slot, or character and slot) on a
save, with ambiguous anchors kept aside; its affixes are rolled modifier bundles stored as that data and applied by its
own stat engine ([source](https://github.com/enshwick/enshwick/blob/945a1b8f0c9d1971679d9f3bc3dcecc127a5bcad/crates/internal/loader/src/stats/affixes.rs)).

### Client checklist for item data (guided checks, `mods/tests/instances-test/ClientChecks.cs`)
Join a server that runs `instances-test`, open the check list (Ctrl+Shift+F5) and run them (area "Item data"). The
`.before` / `.after` pairs span a leave, a crash or a restart. None of them has run yet.

| Check | You do | The server asserts |
|---|---|---|
| `instances.held-moves` | drag a new sword to the action bar, take it in hand, into a chest, back | `Items.Held` named it; every drag an `ItemMovedEvent` (`Observed`), one to the chest; the value stayed |
| `instances.twins-swap` | swap two identical swords by hand, both into a chest and back | each keeps its own value through every move |
| `instances.equip` | put on a piece of armour, take it off | `Items.Equipped` named it; equip and unequip as `Observed` moves; the value stayed |
| `instances.held-equipped` | hold a weapon, wear armour | lists `Items.Held` and every `Items.Equipped` slot: you compare |
| `instances.trash` | trash a marked sword | `Destroyed`, nothing parked |
| `instances.craft` | craft with a marked gear item as an ingredient (skip if no recipe takes one) | `Destroyed`, nothing parked |
| `instances.tombstone` | with the tombstone mode that drops everything, die and loot the sword back | moves to the tombstone and back as `Observed`; the value stayed |
| `instances.gift` (two players) | gift a marked sword to the other player | an `Observed` move to the other player; the value went with it |
| `instances.leave.before` / `.after` | leave through the menu, rejoin | each value in its slot; the leave save came before `Players.Left`, then each item parked (`PlayerLeft`) |
| `instances.move-leave.before` / `.after` | move a sword and leave within a second, rejoin | the value in the new slot |
| `instances.crash-swap.before` / `.after` | swap two identical marked swords by hand, close the game without the menu, rejoin | no leave save; each sword has its own value as the client's file has it, or both are set aside; never the other's |
| `instances.shutdown.before` / `.after` | stay in game while the server stops; rejoin after the start | each value in its slot |
| `instances.restart.before` / `.after` | leave, the server restarts, rejoin | each value in its slot |
| `instances.elsewhere.before` / `.after` | play the character in a world without Kiln, move one sword, swap the identical two, come back | the moved one found (`Moved`); the identical two set aside; never another's value |
| `instances.rejoin-ids` | run it right after a rejoin | nothing: logs each item entity id beside its flat slot (does a player's inventory load in slot order, as a chest's does?) |

**The sync (dev.27), `mods/tests/instance-sync-test`** (side `both`: the same mod in your game and on the server, or in
a world you host). Its client side shows every change of a copy as a notice and reports it back, so the check asserts
in code what your game holds. None of them has run yet.

| Check | You do | The check asserts |
|---|---|---|
| `instances.synced` | nothing: a sword is put into your backpack | your game has the channel; it reported `held = 7`, then `held = 8` after an edit without `Set`; the key without `Sync` never arrived; no start over in the next 12 seconds (the server hears your game's acknowledgements); hosted: `InstanceData.Synced` reads 8. You confirm the two notices |
| `instances.synced-moves` | drag the sword to another slot, into a chest, back out | your game reported the move with the slot the server reads, then the copy gone, then the value again |
| `instances.synced-rejoin` (joined) | leave through the menu, join again, run it | your game reported the value for the new item entity without anything being set |
| `instances.synced-nearby` (two players) | stand near the other player, then walk away | the other game reported `seen = 21` as another holder's item and never got `held`; gone once out of range |

## What was checked, and how

Lab `dev20-wp13` (a copy of the user's world, no player, game build 26430253, Wine):

| What | How |
|---|---|
| Whole-stack and part-stack moves, refusals, dry run | `items-test`: "move_stack: 9 wood logs moved", "move_items: 3 of 9 wood logs moved back", ledger lines |
| Gear keeps its state | `items-test`: the item entity is the same, `Level`, `PerkContainerNew`, `Durability`, `WeaponGemSlot` byte for byte, `ItemState.containedInSlotId` and `OwnerRelationship` updated |
| Quick stack and move all by the game's functions, with the ledger | `items-test`: the ledger puts both containers back slot for slot |
| Container names | The lab's self-test names a placed chest through the game's own rename path (`set_string` + `AddComponent`); `items-test` reads it back, checks the component against the FNV-1a 64 of the text, and after a save and restart reads it from the save |
| Container renames | `items-test` (since 0.2.0-dev.30.3, to run in the release's lab gate): renames a second container through `SetContainerName`, reads it back and checks the component against the text's id, shares the self-test chest's name and clears it again (the other keeps its name), and the refusals at the call. Offline: `native/services/items/tests/rename_test.cpp` (the text rules, the rename handler's steps against a fake string table), `managed/tests/Kiln.Items.Tests/ContainerRenameTests.cs` (the test kit) |
| A moved piece of gear survives a save | `items-test` phase 2 after a restart |
| Item attachment identity is safe | `items-test` tags two same-template swords before a restart: both are explicitly session-only, neither receives a positional tag afterwards; a saved chest restores its own attachment as a positive control. This is not persistent per-item data |
| Rules, reports | `items-test`: keep slot, keep item, deny, silent, the event's counts |
| Native handler in the game's own move all | the lab self-test: keep, a move of its own first, cancel; the ledger restores |
| Chaining with shroudlink, both orders | a shroudlink mod built from the vendored SDK, in `shroudlink_mods/`, keeps wood out of every quick stack: it runs once per call with Kiln's hook on top of it (default), and with Kiln's hook first (`KILN_ITEMS_HOOKS=early`) |
| Logic without the game | `native/services/items/tests/run.sh` (Wine): destination search, ledger, rules, name records, the hook against a fake inventory world, the hook chain in both orders, a faulting handler |

Unit tests are not game evidence. Only a joined player can show the rest:

## Equipment sets: for this session only
`Items.CaptureSet`, `PlanSet` and `EquipSet` change a player's 20 equipment slots in one transaction (every slot or none;
[services/items.md](../services/items.md#equipment-sets-v6-since-020-dev29)). A set is scoped to this process, this
server world and this session of the player: keep it in memory, forget a player's sets when they leave, and do not save
it. After a restart, in another world or after a rejoin it comes back `StaleSnapshot` and nothing changes; Kiln never
re-resolves an old set onto items that look the same. The sample `mods/samples/equipment-sets` keeps a dictionary per
player:

```csharp
private readonly Dictionary<EntityId, Dictionary<string, EquipmentSet>> _sets = [];
public override void OnLoad() => Events.On<PlayerLeftEvent>(e => _sets.Remove(e.Player.Entity));
public override void OnUnload() => _sets.Clear();   // nothing for the next world or start
```

For a loadout that survives a restart, keep item types (as the Loadouts sample does with `Items.Equip`) or data on the
items themselves (`InstanceData`), and build a fresh set from what the player has when they ask.

## Client checklist (needs a player)

1. Hold R with a chest in reach: stacks the chest already has go in. With a keep rule on a slot of your backpack, that slot stays.
2. With a deny rule on a chest, R next to it moves nothing (and the game shows what it shows for "nothing to deposit").
3. A Quick Stack Station with three chests in range: one `InventoryTransferEvent` per chest.
4. Tap R: move all; a keep rule holds its stack back. The action bar stays unless the game's own move all takes it.
5. `Items.Move` from a player's backpack to a chest, with the player's window open: the slot empties on the player's screen and the gear is in the chest with its level and upgrades.
6. A move of equipped or held gear is refused (`Refused`) by default. With `MoveOptions.Equipped`, check the game's
   unequip behaviour and the resulting equipment and inventory on the player.
7. `Items.Move` into a player's backpack never lands on the action bar. With no `toSlot` and empty equipment slots (no
   rings worn), the stack shows in the backpack, never in a ring or other equipment slot (issue #109); with the backpack's
   own pages full and a backpack worn, in the worn backpack's pages; with no backpack worn and the backpack full, the
   result is `NoRoom` and the chest keeps the stack. A ring moved with `toSlot` an empty ring slot is `Refused` ("use
   Items.Equip") and stays in the chest; arrows with `toSlot` the worn arrow slot top up the worn arrows.
8. Rename a chest "Ores" in the game; `Items.ContainerName` returns it within a step of the rename reaching the server.
9. A player and kiln-shroudlink's own addon (`dinput8.dll`, MODS tab, slot locks) together: R still honours the addon's locks and Kiln's rules.

Container renames (`Items.SetContainerName`, since 0.2.0-dev.30.3), on a dedicated server with two players joined:

10. A mod renames a chest both players can see: each player sees the new name where the game shows it (looking at the
    chest, its window title, the Rename screen's text), without reopening the game. A player who joins afterwards sees it too.
11. Save, stop the server, start it again: the chest still has the name, for the players and for `Items.ContainerName`.
12. Rename the same chest with the game's own Rename button: the player's name replaces Kiln's, and `Items.ContainerName`
    returns it; rename it again from the mod: the mod's name shows.
13. A 48-character name and one with accents, CJK and an emoji show whole in the game; a mod's `""` clears the name in
    the players' view (the Rename screen opens empty).
14. Two chests renamed to the same text both show it; clearing one keeps the other's name.
15. In a hosted game (single player or co-op host), the same as 10 and 11 from the host's own mod.
