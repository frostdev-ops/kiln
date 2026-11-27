# The mod API: gear, rarity and upgrades

Part of the mod API guides ([api.md](api.md) is the index). Here: one item's rarity, enhancements, level, power and armour,
paid rarity upgrades and upgrade stations, rarity rolls, and a type's gear values (`Content.Gear`, the `gear` and
`gearRules` sections). The dev.30 equipment wave. Signatures are the common ones; the XML docs and the generated API
reference ([docs-map.md](docs-map.md)) have the rest.

## Gear: rarity, enhancements, level (dev.30+, mod API 0.2.12)
The game keeps an item's rarity as its number of **enhancement slots** (Common one .. Legendary five); each unlocked slot is
one level. Kiln.Items reads and writes them on the same item entity, so its `InstanceData`, affixes and gem stay on it.
**"Make this sword Rare" is `Items.SetRarity` on the item, never `Fields["rarity"]` on a clone.** The type's numbers
(damage, armour, level range, the rarity new copies get) are `Content.Gear` and the `gear` pack section, below.
Services page `docs/kiln/services/items.md` "Gear: rarity, enhancements, level"; samples `GearValues`, `RarityAnvil`, `SlotMarks`.
```csharp
var item = Items.Held(player);                      // an ItemInstanceRef; also Items.Equipped(...), InventorySlot.Ref
GearState? g = Items.GearOf(item);                  // null: gone, or the id names another item now
//   g.Rarity, Enhancements, Unlocked, Level, MaxLevel, MaxRarity, Durability, DurabilityMax, HasSocket, Perks[0..4]
GearResult up = await Items.SetRarity(item, ItemRarity.Rare);   // one more slot, locked: unlocked at the workbench
if (!up.Ok) Log.Info($"not changed: {up.Reason}");              // NoSlot, Stale, OutOfRange, ...
await Items.SetEnhancements(item, 2);                           // unlock two; the level moves with them
await Items.SetLevel(item, 12, new GearWriteOptions { Durability = DurabilityRule.Repair });
Items.SetPower(item, 1.25);                         // this item deals a quarter more, wherever it goes (also SetArmor)
await Items.SetDurabilityMax(item, 900);            // null gives the game's own max back
GearValues? v = Items.ValuesOf(item);               // Power, Armor, KeptBaseLevel, KeptDurabilityMax, Kept
await Items.Give(chest, id, 1, new Gear(Level: 20, Rarity: ItemRarity.Epic) { Unlocked = 5, GemSlot = true });
Events.On<GearRarityChangedEvent>(e => Log.Info($"{e.Item}: {e.From} -> {e.To} ({e.Source} by {e.Origin})"));
```
- Server, tick thread. The ref must carry kiln.items' stamp (`Items.Held`, `Items.Equipped`, `InventorySlot.Ref`, a give or
  move result's `Ref`): any other ref is an `ArgumentException`. `ItemRarity` is Kiln.Items'; blocks use `Rarity`.
- Writes run in the next sim step through the game's tracked writes: the task completes one step later. A higher rarity adds
  locked slots, a lower one takes slots (and levels) away. `SetLevel` sets the level the game shows; its base (level minus
  unlocked) must be in the type's range unless `GearWriteOptions.AnyLevel`, which Kiln then keeps and writes again after
  each load. `GearWriteOptions`: `Durability` (`KeepRatio` default, `Keep`, `Repair`), `AllowEmptySlot`, `RefuseEquipped`,
  `DryRun`.
- `GearResult` (`IKilnResult`): `Status` (`GearStatus.Done`, `Stale`, `NotGear`, `NoSlot`, `OutOfRange`, `Equipped`,
  `Unavailable`, `Dropped`, `Refused`), `Before`, `After`, `Changed`, `Kept`. A refusal changes nothing; bad arguments throw; an
  off capability throws `KilnUnavailableException`. `SetPower`/`SetArmor` (scale 0.01 to 100; 1 removes it) answer at once with
  `GearValuesResult`. Kiln keeps these values, an `AnyLevel` level and a `SetDurabilityMax` with the item (item data of owner
  `kiln-gear`, `Kept`) through moves, saves and restarts.
- Events (tick thread): `GearChangedEvent`, then `GearRarityChangedEvent` (`From`, `To`) and `GearEnhancedEvent` (`From`,
  `To` unlocked). All carry `Before`, `After`, `Source` (`GearChangeSource.Code`, `Upgrade`, `Game`: a player's unlock at the
  workbench is the game's) and `Origin` (the mod id, "kiln" or "game"). Game changes are polled while the event has
  subscribers (one player's items a step), so they come late; chests are not read.
- Capabilities: `items.gear.read`, `.write`, `.durability`, `.power`, `.armor`; `Items.GearReadAvailable`,
  `GearWriteAvailable`. A player without Kiln sees the game's own state (slots, colour, level) and the type's numbers in the
  tooltip; a Kiln client shows power and armour scaled (`ui.tooltip.values`). A client showing a new rarity without
  re-joining is not yet confirmed in a real game.

### Upgrading rarity and rolling it (dev.30+)
`Items.UpgradeRarity` raises one item by one step of a rule and pays for it, all or nothing: the next enhancement slot
(locked, as above), the cost out of the player's inventories (or a chest), a server-rolled chance. The rule is a C# record
or a block.
```csharp
var rule = new RarityUpgradeRule { Items = ["Weapon_*", "Armor_*"], Steps =        // by the rarity each step reaches
{
    [ItemRarity.Rare] = new UpgradeStep { Cost = { ["Material_T1_Metal_Sheets"] = 5 }, Chance = 0.9 },
    [ItemRarity.Epic] = new UpgradeStep { Cost = { ["my-mod:ember-shard"] = 3 }, Chance = 0.75, OnFail = UpgradeFail.KeepItemLoseCost },
} };
UpgradeResult r = await Items.UpgradeRarity(player, Items.Held(player), rule);
if (!r.Ok) Chat.Tell(player, $"Not upgraded: {r.Reason}");        // Failed, CantPay, NoSlot, AtMax, NotAllowed, Canceled, ...
await Items.UpgradeRarity(player, item, "standard-upgrades", new UpgradeOptions { PayFrom = chest });   // a block of this mod
// the same rule as a block (JSON "rarityUpgrades"), and the creation-time roll (JSON "rarityRolls")
Blocks.Define("standard-upgrades", new RarityUpgradeBlock { Items = ["Weapon_*"], Max = Rarity.Legendary,
    Steps = { [Rarity.Uncommon] = new UpgradeStep { Cost = { ["Material_T1_Metal_Scraps"] = 10 } } } });
Blocks.Define("ember-rolls", new RarityRollBlock { Items = ["my-mod:*"], Sources = ItemSource.Craft | ItemSource.Loot,
    Weights = new Dictionary<Rarity, double> { [Rarity.Common] = 50, [Rarity.Rare] = 15, [Rarity.Legendary] = 1 } });
```
- Costs are plain items (never gear or a gem), 1 to 100000 each, at most 8 per step; `LevelScale` grows a cost with the item's
  level; `UpgradeStep.Unlock` unlocks the new slot at once. `OnFail`: `UpgradeFail.KeepItemLoseCost` (default),
  `KeepItemKeepCost`, `Downgrade`. A rule has `Max`, `Durability` (`DurabilityRule`), `Socket` (`SocketRule.Keep` or
  `GameChance`), `AllowEmptySlot`. A malformed rule throws; a refusal is a result and pays nothing.
- `UpgradeResult` (`IKilnResult`): `Status` (`UpgradeStatus.Done`, `Failed`, `CantPay`, `NoSlot`, `AtMax`, `NotAllowed`,
  `NotGear`, `Stale`, `Canceled`, `Refused`, ...), `From`, `To`, `Paid`, `Chance`, `Rolled`, `Before`, `After`.
  `UpgradeOptions`: `DryRun`, `Free`, `Guaranteed`, `PayFrom`. Events: `GearUpgradingEvent` (cancelable; set `Cost` and
  `Chance`), `GearUpgradedEvent`. Capability `items.gear.upgrade`; `Items.UpgradeAvailable`.
- **A station** that runs a rule when a player uses it: `InteractBlock.Upgrade = new UpgradeAction { Rule = "standard-upgrades",
  Target = UpgradeTarget.Choose, Range = 6 }` on a `PropBlock` (`Held` is the default; `Choose` opens a window on Kiln
  players' games and gives others the held item), or from code `UpgradeStation.Offer(entity, action, out var why)` (an E prompt on any
  existing entity, no new template, works for players without Kiln) and `await UpgradeStation.Use(player, station, action)`.
  Capability `blocks.upgrade_station`. A prop of your own needs `clients: kiln`.
- **`RarityRollBlock`** sets the rarity the server rolls when the game makes an item (`ItemSource.Craft`, `Loot`, `Give`):
  one item type with five perk slots covers common to legendary. A later definition wins an item type that two rules name.
  Capability `items.gear.rolls`. Pages `docs/kiln/modding/item-blocks.md` "Rarity at creation", "Rarity upgrades";
  `docs/kiln/modding/world-blocks.md` "Upgrade stations".

## Gear values of a type: `Content.Gear` and the `gear` section (dev.30+)
A typed face over the item fields of gear, in the numbers the game's own tooltip shows ("power 30", "physical armour 45")
instead of solving `damageSetup.dmgMod` or `armorSetup.distribution.physical` by hand. Kiln solves them per item with the
game's formulas and writes ordinary item fields: layers of your mod (reverted with it, conflicts listed, synced to Kiln
clients). Capability `content.gear`. Page `docs/kiln/services/content.md` "Gear values"; sample `GearValues` (data only).
```csharp
Content.OnReady(_ =>
{
    var sword = Content.Gear("Weapon_T1_1H_Sword_Craft")!;   // null: no such item
    sword.Power = 30;                                         // the tooltip's damage at sword.Level
    sword.SetDamage(GearDamageKind.Fire, 0.25);
    sword.SetDamage(GearDamageKind.Cut, 0.75);
    var chest = Content.Gear("Armor_T2_Warrior_Tank_Chest")!;
    chest.SetArmor(GearArmorKind.Physical, chest.Armor(GearArmorKind.Physical) + 10);
    chest.Rarity = ItemRarity.Epic;                           // the type's rarity: copies made from now on
});
```
```jsonc
{ "format": 1,
  "gear": {
    "Weapon_T1_1H_Sword_Craft": { "power": 30, "damage": { "cut": 0.75, "fire": 0.25 }, "levels": [5, 5] },
    "Armor_T2_*":               { "@where": { "rarity": "Rare" }, "armor": { "physical": { "mul": 1.2 } } },
    "Ring_T3_Ring_of_Light":    { "socket": true, "rolled": false } },
  "gearRules": { "rarityDurability": [0.9, 0.95, 1, 1.1, 1.25], "socketChance": { "epic": 0.75 } } }
```
- `Content.Gear(string | ItemId | ContentEntry)` → `GearStats`: `Power`, `PowerAt(level)`, `Speed`, `Damage(kind)` /
  `SetDamage`, `Armor(kind)` / `SetArmor`, `Block`, `Rarity`, `Rolled`, `Socket`, `MaxStack`, `Level`, `MaxLevel`,
  `SetLevels(lowest, highest?)`, `Reset()`. Writes are `ContentEntry` writes of your mod; a value out of range throws
  `ArgumentOutOfRangeException`, `Power` on an item with no damage of its own throws `InvalidOperationException`. Tick thread.
- **`gear` keys** (selectors, `@where` and ops as in the `items` section; it applies after `items`): `power` and `armor`
  (per kind) are the tooltip's numbers at the item's lowest level, `speed`, `damage` (shares per kind: `cut`, `pierce`,
  `blunt`, `fire`, `ice`, `shroud`, `lightning`, `poison`, `healing`), `block`, `staminaCost`, `rarity` (`common` ..
  `legendary`), `levels` (`5` or `[1, 5]`), `rolled` (the game rolls each copy's rarity), `perks`, `socket`, `maxStack`,
  `recipe`. Numbers take ops relative to the game's value (`{"mul": 1.2}`, `add`, `set`, `min`, `max`). `levels` alone keeps
  the game's curve: give `power` and `armor` in the same rule to keep the numbers.
- A copy has rarity + 1 enhancement slots filled from the type's perk slots: raise `rarity` together with `perks` where the
  type has fewer slots. Copies already made keep theirs; narrowing a released item's level range lowers the level of the
  copies players have. Each problem is one line of the pack report; `kiln lint` checks names, ranges and op keys.
- The server's values act for everyone; Kiln clients get them through content sync (the tooltip shows the new numbers
  without the mod); a player without Kiln plays with them and sees the game's numbers.
- **`gearRules`** are the game's balancing values (durability factor per rarity, socket chance per rarity, durability start
  and growth, the workbench's unlock cost). The game reads them once at start, so they are resource patches: the mod needs
  `"boot": true` and the capability `mods.boot.resource_patches` ([boot.md](boot.md)). One wrong value leaves the file's
  `gearRules` out, with the reason in kiln.log. Two mods setting one member conflict.
- Gear in the UI: lines and scaled numbers in the game's item view (`Ui.Tooltip`), marks and clicks on its slots
  (`Ui.Inventory`, `SlotMark`), panels beside backpack and chest screens (`InventoryScreens`):
  [api-ui.md](api-ui.md#the-games-inventory-slots-item-detail-and-outlines-dev30).

## Gear rules on the registry (dev.32+)
The 19 `gearRules` keys are fields of the `balance` registry of the same name (`rarityDurability.<rarity>`,
`socketChance.<rarity>`, `durability.start` / `.growth`, `unlockCost.seed`, `.creep`, `.weapons`, `.armor`, `.rings`,
`.advanced`, `.advancedFrom`). **Nothing changes for your mod:** the same `content/*.json` file, the same `"boot": true`, the same
values. Since dev.32 Kiln writes a `gearRules` file's values through its registry mapping instead of the resource patch, where that
gives the same bytes.
- **The same bytes, or the old way.** At every start Kiln builds the balancing table both ways and serves the registry's copy only
  when the two are byte for byte equal. A file stays a resource patch (the reason is in `kiln/state/registry.json`, `frontEnds`,
  `state: "resourcePatch"`) when a value is not a balance field Kiln proved on this game build, when an op's `min` is above its
  `max`, or when the copies differ.
- **Composition as before.** The file stays an older path below every registry edit: a `balance.durability.start` edit in a
  registry pass wins over the file, a relative one composes on the file's value. Two mods setting one member still conflict.
  `registry.json` lists every field the file sets as a row with the file as writer (`frontEnd: true`).
- **The `gear` section stays a data pack** (`Content.Gear`, the `gear` keys): one registry edit per gear key does not always mean
  what the rule means (a relative power with `levels` in one rule, a speed a float cannot hold, the registry's save checks that
  refuse a narrower `levels` or lower `maxStack`, the rule's place among other packs). Do not port it.
- `lootRarityRates` is still refused: use the `lootRules` registry for rarity weights ([api-registry.md](api-registry.md)).
- A switch for support and labs only: an empty file `<game>/kiln/data/content/registry/front-ends.off` keeps every `gearRules`
  file a resource patch at the next start. Not a setting for players.
- A server and a player on dev.31 still join each other: the join compares the boot content of the mod, and the registry's own
  part counts registry edits only. Not seen in a real game: gear rules through the registry on a client, tooltip numbers, durability,
  socket and unlock-cost behaviour in play. Pages `docs/kiln/modding/gear-rules.md` (a walkthrough), `docs/kiln/services/content.md` "Gear rules on the registry".

