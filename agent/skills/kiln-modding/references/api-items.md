# The mod API: items, item data and gear

Part of the mod API guides ([api.md](api.md) is the index). Here: items, inventory transfers and equip, data kept on items
and worlds, affixes, recipes (menu tab, tracking, legacy ids) and crafting from linked containers. Rarity, levels and
upgrades: [api-gear.md](api-gear.md). Signatures are the common ones; the XML docs and the generated API reference
([docs-map.md](docs-map.md)) have the rest.

## Items
- Items: `Items.Lookup(name)`, `Items.Search`, `await Items.Give(target, id, n, gear, options)`,
  `Items.Read(inventory)`, `await Items.Take(...)`, `Items.FindInventories(kinds)`. dev.27+ equip, in
  [Inventory transfers](#inventory-transfers-dev20). A mod item with a model of its own lies on the ground as that model
  (`VisualBlock.Dropped` names another); before dev.27 it took its base item's ground model. dev.29+ equipment sets,
  dev.28+ tool wear: [gameplay.md](gameplay.md). dev.29.15+: `Items.DisplayName(id)` is the name in the player's
  language, also on a joined client.

## Inventory transfers (dev.20+)
`await Items.Move(from, slot, to, count: 0)`, `MoveAll(from, to)`, `QuickStack(from, to)` keep the same gear entity and its
level, upgrades, gem slot and durability. A take followed by a give makes new gear. `MoveResult` reports refusals and a
bounded `Changes` ledger. Check `Items.MovesAvailable`; run on the tick thread and await completion there.
dev.31.30+ (mod API 0.2.28): `await Items.MoveChecked(from, expected, to, count, destination)` takes slots as `Items.Read`
listed them and moves only while they still hold that (gear: the same item entity and stamp); with a destination, the whole
count lands in exactly that slot or nothing moves. `SlotChanged` / `NoRoom` move nothing, a request sent twice moves once.
Check `Items.CheckedMovesAvailable` first. Guide: `docs/kiln/modding/inventory.md` (Checked moves).

`MoveOptions.DryRun` rolls back and emits no transfer event; `Equipped` and `ActionBar` opt into those slots; `Silent` skips
transfer rules/events. `Items.ContainerName(chest)` reads a player's name; check `ContainerNamesAvailable`. `await Items.SetContainerName(chest, "Ores")` (since 0.2.0-dev.30.3, mod API 0.2.13, capability `items.rename`) renames through the game's own rename on the server (replicates, saves; "" clears; at most 48 characters / 64 bytes, no control characters; throws `ArgumentException` for a bad name or a non-container, `InvalidOperationException` on a joined client); check `ContainerRenameAvailable`.
`Items.Transfers.Keep(slot)`, `KeepItem(item, owner)`, `Deny(container, TransferKinds.QuickStack)` return disposable rules.
`InventoryTransferEvent` is a report after quick stack/move all, not cancelable. To cancel an incoming player request,
first require `e.Name == "inventoryTransferAction"` and `e.CanCancel` on `PlayerClientActionEvent`; then parse `e.Payload`
with `Items.Transfers.ParseAction` and cancel the held action. The parser does not identify an action from its bytes.
Joined-client events cannot cancel the server's action, and a Quick Stack Station has no such request. Rules and
subscriptions end with the original mod lifetime.
dev.21+: a native `transfer_listen` handler (kiln.items) also runs on a dry-run move (not a `Silent` one) with
`KILN_TRANSFER_DRY_RUN` in `ev->flags`. Keep and cancel as usual; count, log and change nothing outside the transfer.
Wiki: [inventory](https://frostdev.io/kiln/wiki/inventory).

dev.27+ **equip:** `await Items.Equip(player, fromSlot, EquipmentSlot.Ring1)` puts an item from the backpack on (no slot:
the item's own), `await Items.Unequip(player, slot)` takes one off into a free backpack slot. The game's own equip runs
on the server with all its checks; `EquipResult` says what moved where or why not. `EquipOptions.DryRun` asks without
changing anything; `EquipChangedEvent` follows each change. Capability `items.equip`. Sample `Loadouts`.

dev.30.6+ (mod API 0.2.15) **give straight into equipment:** `await Items.GiveEquip(player, item, count, EquipmentSlot.Ring1,
new GiveEquipOptions { Replaced = Replaced.Take })` creates one stack in an empty or
occupied equipment slot and equips it in one game transaction (server, tick thread; no free backpack slot is needed for an
empty slot or `Replaced.Take`). `Replaced.Backpack` (default) keeps the old stack in a free backpack slot (`NoRoom`, nothing
changed, when none is free); `Replaced.Take` **destroys** the old stack and its item entity. `count` is 1 to 65535 and one stack
(1 for gear); the item's equipment slot type must fit; a backpack item cannot be given into the bag slot yet. The
`EquipResult` has `From` None and reports the old stack in `ReplacedItem` and `ReplacedCount` (also on `DryRun`; 0 for
`Equip` and `Unequip`). `Items.GiveEquipAvailable`, capability `items.give_equip`. Page `docs/kiln/services/items.md`
"Give straight into equipment"; test kit `kiln.Items.SetEquipmentSlotType`.

## Persistent data
`WorldData.Get<T>(key)` (live object: change it in place; saved with the world), `PlayerData.Get<T>(steamId, key)`,
`EntityData.Get<T>(entity, key)` / `Set`. Tick thread. JSON via System.Text.Json. Keys are per mod. Kept across hot
reloads. `WorldSavingEvent` runs before the game's snapshot. Backups: `Saves.Request()`, `Saves.Backup(...)`.
In a hosted world, `OnLoad` sees empty world data until `WorldLoadedEvent`: read per-world data from that event.
**dev.20+:** `EntityData.IsPersistent(gearEntity)` is false. Gear has no stable world-position identity: its data follows a
move within the session, not a restart. Kiln skips these attachments on save and refuses old positional gear records.
World entities such as saved chests retain their normal persistence. dev.24 and older supply no persistent per-item data.

**Data on one item (dev.26+; a player's inventory not seen by a player):** Kiln.Saves.
```csharp
_kills = InstanceData.Key<Kills>("kills");          // OnLoad; options: new InstanceKeyOptions { Persist = Persist.Session }
var sword = Items.Held(player);                     // ItemInstanceRef; also Items.Equipped(player, EquipmentSlot.ArmourHead),
sword.Get(_kills).Count++;                          //   InventorySlot.Ref, give/take/move results' Ref
```
- `Get` (the live object, saved with the world), `TryGet`, `Has`, `Set`, `Remove`; `InstanceData.All(key)`,
  `InstanceData.Parked(key)` (waiting or set aside, with the reason). Tick thread.
- Events: `InstanceDataChangedEvent` (a `Set`/`Remove`), `InstanceDataRestoredEvent` (`Exact`, `Moved`),
  `InstanceDataParkedEvent`, `InstanceDataLostEvent` (`Destroyed`, `Unmatched`, `Ambiguous`, `Capped`). Kiln.Items:
  `ItemMovedEvent` (`Transaction`, `Transfer`, `Observed`), `item.Location()` (`ItemLocation`). Admins: `/instances`.
- When Kiln is not sure which of two identical items a value belongs to, it sets the value aside, never on another item.
  Identical items swapped where Kiln did not see it (another world, an offline edit) trade their data.
- `Persist.Carried` throws `KilnUnavailableException("saves.instance_data.carried")`: not in this release. Plain stacks
  have no item entity. 256 records per holder per mod, 64 KB per value, waiting records dropped after 3 sessions.
- dev.27+: `InstanceData.Claim(parked, item)` puts a set-aside value on an item you name, `InstanceData.Discard(parked)`
  drops it (also from a handler of the item data events; admins: `/instances claim|discard`).
- dev.27+ **values in players' games:** `new InstanceKeyOptions { Sync = InstanceSync.Holder }` (or `Nearby`, with
  `NearbyRadius`); declare the same key on the mod's client side (`"side": "both"`) and read it there with
  `InstanceData.Synced(item, key)` or `InstanceDataSyncedEvent` (capability `saves.instance_data.sync`). A value never
  goes to a player the key does not name; a player without Kiln dev.27 gets nothing.
- Chests proven on a test server; backpack, action bar and worn items wait for the guided client pass. Page:
  `docs/kiln/modding/inventory.md` "Data on one item"; sample `mods/samples/ItemData`.

**Affixes (dev.27+; Kiln.Blocks):** bonuses rolled onto one item. Define kinds and pools as blocks (JSON sections
`affixes` and `affixPools` in `blocks/*.json`, or in `OnLoad`):
```csharp
Blocks.Define("bear", new AffixBlock { Name = "of the Bear", Stat = Stat.Strength,
    Tiers = [new AffixTier { Min = 2, Max = 4 }] });
var blades = Blocks.Define("blades", new AffixPoolBlock { Items = ["Weapon_T1_1H_SwordEpic_01"],
    Affixes = [new AffixEntry { Affix = "bear" }] });
var r = blades.Roll(Items.Held(player));             // later, on one item (ItemInstanceRef); AffixResult: Ok, Reason, Affixes
```
- `Affixes.Roll(item, pool)`, `Add`, `Set`, `Remove`, `Clear`, `Of(item)`, `Active(wearer)`, `Find`, `FindPool`; on a
  client `Affixes.Synced(item)`. Ids are `"mod:name"`. `AffixesChangedEvent`, `AffixesAppliedEvent`. Capability
  `blocks.affixes`. All 39 game stats.
- They act while the item is in hand or worn, never in the backpack. At most 8 rolls per item act; a value is at most
  1,000,000 either way. A pool that can never reach its `Min` is refused at definition. An affix or pool cannot share its
  name with another kind of block in its mod (`BlockError`).
- Not yet: flat damage on a hit, a share of a pool, lines in the game's own tooltip, rolls by themselves on a drop or a
  craft. Page `docs/kiln/modding/affixes.md`; sample `AffixSword`.

## Recipes: menu tab, tracking, ids of another loader (30.5+, mod API 0.2.14)
Page `docs/kiln/modding/item-blocks.md` "Recipes of their own" and the three sections after it.
```csharp
Blocks.Define("make-staff", new RecipeBlock
{
    Output = "staff", Station = "0xbddd74e6",                    // the Carpenter
    Needs = { [GameItems.Material_T1_Wood_Logs] = 1 },
    MenuGroup = CraftingGroups.Carpenter.Essentials,             // a tab of the game's own menu (Kiln.Content)
    TrackCrafted = true,                                         // the game's "crafted once" mark for the recipe
});
Blocks.Define("staff-found", new KnowledgeBlock { Trigger = KnowledgeTrigger.Pickup, Of = "staff" });
Blocks.Define("staff-made", new KnowledgeBlock { Trigger = KnowledgeTrigger.Craft, Of = "make-staff" });
// an item or recipe another loader made keeps that loader's GUID (or only its game id), so players' stacks stay yours:
new ItemBlock { Identity = BlockIdentity.Legacy(new Guid("...")) }   // or BlockIdentity.Legacy(3205570361)
```
- **`MenuGroup`**: a recipe is listed beside recipes of the same kind unless it names a tab. The `CraftingGroups` constants
  (`CraftingGroups.Carpenter.Essentials`, `.Hand.Weapons`, ...) are every tab of every workshop menu; `new
  CraftingGroupRef(workshop, tab, subcategory)` names another list; JSON `"menuGroup": "0xbddd74e6/8c91ca52-..."`. The tab's
  workshop must be the recipe's station (`"hand"` is workshop `0x00000000`); a station of your own has no game tabs and
  refuses it. If a game update moved the tab the recipe falls back to the default place and says so in its warnings.
- **`KnowledgeBlock`** puts one of your items or recipes into the game's knowledge table, so "found" and "crafted once" are
  tracked as for the game's own: `Trigger` (`Pickup`, `Craft`), `Of` (an item or recipe block of this mod), or `Identity`,
  `Scope` (`TrackingScope.Both`, `World`, `Player`; a recipe is always `Player`). A tracked recipe says `TrackCrafted = true`
  (`"trackCrafted": true`). The table is built at start: a block takes effect from the next start (the first, when built with
  Kiln.Sdk, which bakes `quests/generated/<mod>.json`). Players need the mod (`clients: mod`). Reading and changing what
  players know: `Knowledge` (Kiln.Quests), `docs/kiln/services/knowledge.md`.
- **`BlockIdentity.Legacy(Guid)` / `Legacy(uint id)`** (`"identity": { "legacy": "<guid>" }` or `{ "legacyId": 3205570361 }`) on
  `ItemBlock.Identity` and `RecipeBlock.Identity`; the block's game name stays `<mod id>.<name>`. Refused by field: an empty
  guid, id 0, an id of another block or mod. Never change it after release: saves keep the id.

## Crafting from linked containers (30.6+, mod API 0.2.15)
`Crafting.AddSource(player, container, options)` (Kiln.Blocks, server, tick thread) links a storage container to one player so
crafts at stations, at NPCs and in the inventory pay from it, **in any base or in none**; dye sources use the same links
(`crafting.dye_sources`: the tinting screen counts linked dyes, the server pays one from the bag first, then a link). Page
`docs/kiln/services/stations.md` "Player crafting sources".
```csharp
CraftSourceHandle source = Crafting.AddSource(player, chest, new CraftSourceOptions
{
    Order = CraftSourceOrder.BeforeStationOutputs,       // or AfterStationOutputs
    Filter = CraftSourceFilter.Allow,                    // Deny (default): Items null or empty allows every item
    Items = new[] { wood, stone },                       // ItemIds, copied now: dispose and add again to change
});
source.Dispose();                                        // any thread; unload removes all of the mod's links
```
- The bag and the base's own stock pay first; equal-order links pay in registration order. A container counts once per item.
  Refused (`ArgumentException`): a non-container, a player's bag, an NPC inventory, a production station (those lend their
  outputs through `Stations.SetOutputSource`). At 4096 links `InvalidOperationException`.
- **Access is your policy plus the game's:** you choose which container to link, even one in another player's base; at each
  payment the server checks the link belongs to this player, that the player has the game's inventory access right and a
  login that matches. A client's count is only UI help. Kiln clients show linked stock in every crafting menu (craft count
  and, 30.8+, ingredient rows); 30.8+ a production station's ingredient rows show the game's own counts (no links, no
  opted-in outputs: their transfer buttons move only the bag and the Magic chests). 30.8+: linked dyes sort with the
  player's own in the tinting palette.
- Links last for the session only: save them in your own data and add them again, and dispose them when a player leaves.
  Capabilities `crafting.sources`, `crafting.source_ui`, `crafting.dye_sources`. Not seen in a real game yet.
- **Locks too (since 0.2.0-dev.31.12, mod API 0.2.21):** `new CraftSourceOptions { Locks = true }` lets the same link supply a lock's
  required item (the game's `InteractionLock`: lockpicks, keys; a furniture block's `requires.item`). The link's filter
  applies. The bag counts and pays first, then the opted-in links in link order; a lock with `uses` (Consume) takes the
  item in the game's own all-or-nothing interaction transaction, one without only needs it held. Off by default: a link
  without `Locks` never serves a lock. Needs `Crafting.LockSourcesCapability` (`items.lock_sources`) on the server or
  host; the server's own lock check decides every press, so the lock opens for a vanilla client too, but only a Kiln
  client's locked prompt and predicted press count the link (a vanilla client still shows it locked). Without the capability the link still
  serves crafting and kiln.log says why once. Every refusal logs one `locks:` line (no snapshot, not opted in, short
  count, a stack the server no longer sees, a foreign patch). TestKit: `h.Crafting.Sources[i].Locks`,
  `h.Crafting.LockStock(player)`; no lock check or interaction payment is simulated. Not seen in a real game yet.
- **The skill respec too (since 0.2.0-dev.31.18, GitHub #150):** `new CraftSourceOptions { Respec = true }` lets the
  same link pay the skill tree's unlearn and the Flame Altar's skill reset, which cost the game's respec item (Runes).
  The bag and the Magic chests of the player's base pay first (the game's own order), then the opted-in links in link
  order, in the game's all-or-nothing respec payment; nothing from the links when the bag and the chests cover it. The
  free reset the game makes at join stays free. Needs `Crafting.RespecSourcesCapability` (`crafting.respec_sources`) on
  the server or host; the server pays every player's respec, but only a Kiln client's skill tree and altar offer the
  unlearn and the reset when the bag and the chests alone are short (a vanilla client greys them out and sends nothing).
  Without the capability the link still serves crafting. kiln.log: `respec:` lines per decision and stand-down.
  TestKit: `h.Crafting.Sources[i].Respec`, `h.Crafting.RespecStock(player)`; no respec payment is simulated. Not seen
  in a real game yet.
