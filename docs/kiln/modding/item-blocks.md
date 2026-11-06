# Item blocks

Blocks say what you mean: a sword, a potion, an arrow. Kiln picks the game item a block starts from, derives the ids, and solves the game's own formulas so that the tooltip shows what you wrote. There is no `base` to name and no GUID to write.

Sample: `mods/samples/ItemBlocks`. Every block below appears there in C#, and in `blocks/twins.json` as data.

```csharp
var shard = Blocks.Define("ember-shard", new ItemBlock { Name = "Ember Shard", Stack = 100, Fuel = 120 });

Blocks.Define("ember-sword", new WeaponBlock
{
    Name = "Ember Sword",
    Class = WeaponClass.Sword,
    Stats = new WeaponStats { Level = 5, Power = 30, Rarity = Rarity.Rare },   // the tooltip's numbers, made exactly so
    OnHit = [sear],                                                            // an EffectBlock: works in hand, with no unlock
    Recipe = new RecipeBlock { Needs = { [shard] = 4 } },
});
```
```jsonc
{ "format": 1,
  "items": { "ember-shard": { "name": "Ember Shard", "stack": 100, "fuel": 120 } },
  "weapons": { "ember-sword": { "name": "Ember Sword", "class": "sword",
      "stats": { "level": 5, "power": 30, "rarity": "rare" }, "onHit": [ "sear" ], "recipe": { "needs": { "ember-shard": 4 } } } } }
```
JSON sections: `items`, `weapons`, `wands`, `armor`, `stations`, and for parts `looks`, `weaponStats`, `armorStats`, `weaponClasses`, `armorSlots`, `armorSets`, `onHits`, `wornLooks`. Schema: [schemas/kiln-blocks.md](schemas/kiln-blocks.md).

## Parts
A part is a block of its own. Write it inline, define it once and share the handle, or name it in JSON. A premade such as `WeaponClass.Sword` is a plain instance of the same record.

| Part | Fields | Premades |
|---|---|---|
| `VisualBlock` | `Like` (an item's whole look), `Model`, `Icon`, `Dropped`, `Tint`, `Vfx` | |
| `WeaponStats` | `Level`, `Power`, `Rarity`, `Damage`, `Block`, `StaminaCost` | |
| `ArmorStats` | `Level`, `Rarity`, `Physical`, `Magical`, `Resist`, `Bonus` | |
| `WeaponClass` | `Visual`, `Stats`, `Perks`; in JSON `like` | `Sword`, `Axe`, `Mace`, `Greatsword`, `Greataxe`, `Hammer`, `Daggers`, `Bow`, `Staff`, `Shield`, `Wand` |
| `ArmorSlot` | `Visual`, `Stats`; in JSON `like` | `Head`, `Chest`, `Arms`, `Legs`, `Feet`, `Ring` |
| `ArmorSet` | a look per slot: a `VisualBlock` or a `WornLook` | `Warrior`, `Mage`, `Ranger` |
| `WornLook` | a model on the body: `Model`, `Attach`, `Female`, `Variants`, `Fits`, `Hides`, ... ([cosmetics.md](cosmetics.md)) | |
| `OnHitBlock` | `Effects`, `Vfx` | |

```csharp
var blade = Blocks.Define("ember-blade", WeaponClass.Sword with { Visual = emberLook, Stats = veteran });
Blocks.Define("ember-edge", new WeaponBlock { Name = "Ember Edge", Class = blade, OnHit = searing });
```
A look cannot carry a VFX yet: `VisualBlock.Vfx` is refused with a message. `OnHitBlock.Vfx` plays where a hit lands, and a wand attack's `CastBlock.Vfx` plays on the cast.

## Blocks
| Block | Fields |
|---|---|
| `ItemBlock` | `Kind`, `Visual`, `Stack`, `Rarity`, `Level`, `Texts`, `Heals`, `OnUse`, `Feeds`, `Fuel`, `Comfort`, `Tool`, `Ammo`, `Recipe`, `Loot` |
| `WeaponBlock` | `Class`, `Visual`, `Stats`, `Perks`, `OnHit`, `OnKill`, `Recipe`, `Loot` |
| `WandBlock` | `Primary`, `Heavy`, `Special`, `Class`, `Visual`, `Stats`, `Perks`, `OnHit`, `OnKill`, `Recipe`, `Loot` |
| `ArmorBlock` | `Slot`, `Set`, `Visual`, `Look`, `Stats`, `Perks`, `OnHitTaken`, `Recipe`, `Loot` |
| `StationBlock` | `Kind`, `Visual`, `Recipe`, `Loot`, and more: [world-blocks.md](world-blocks.md) |

All take `Name`, `Description` and `Was` (the name before a rename: ids come from the oldest name, so saves keep it).

An item's kind follows from what you fill in: `Tool` makes a tool, `Ammo` an arrow, `Feeds` food, `Heals` or `OnUse` a potion, anything else a material. Say `Kind` to be explicit; a field the kind has no use for is an error.

- **Potion.** `Heals` is the game's own drink and the tooltip's Health row. `OnUse` runs your effects on whoever uses it, from a use sequence of its own.
- **Food.** `Feeds` names a status. It goes in the game's food slots, and the tooltip's Duration is the status's.
- **Arrow.** `Ammo.Damage` and `Ammo.Power` add to the bow's, as the game's arrows do.
- **Tool.** `Harvests = Harvest.Wood` is an axe, `Harvest.Stone` a pickaxe. `Power` is its wood or stone damage; the rest of what it can do scales along.
- **Wand.** Each attack is a `CastBlock` and a delivery, with a sequence of its own and no ammo item. `CastBlock.ManaCost` is paid by that attack alone. The primary's main damage is the wand's power and the tooltip's number. Heavy and special deal exactly what they say. An attack left out is the primary again; its motion comes from `Moves`, and the hand effect from the starter wand.
- **Staff spell.** A `SpellBlock`: an ammo item, one charge per item in the stack.
- **Armor.** `Physical` and `Magical` are the tooltip's. `Bonus` is what wearing it adds, for example `[EquipBonus.Health] = 60`. Its look on the body is a game item's (`Visual = new VisualBlock { Like = ... }`) or a model of your own: `Look = new WornLook { Model = "models/mail.glb", Hides = [BodyPart.Chest, BodyPart.Waist] }`, skinned or on one joint, with a `Female` morph or file, as a cosmetic's ([cosmetics.md](cosmetics.md)). `Look` wins over `Visual` for what is worn, and `Visual` still gives the dropped model and the tint. With neither, the piece takes its set's look for its slot, which can be a worn look too. A ring has no look on the body.
- **Station.** A placeable that opens an existing workshop's menu. A recipe with `Station = bench` is listed there. Forges and racks of your own, with workshops and recipes of their own, and props: [world-blocks.md](world-blocks.md).
- **Loot.** `Like` rolls it in chests and world pickups where that item is. `Tables` puts it into drop tables, by `DropsLike` or a table GUID.

## What is derived

| What | How |
|---|---|
| Ids | From the mod id and the block's name. Stable across restarts; stored items keep them. |
| Texts | `Description` and `Source` (where to get it, in the tooltip) are yours or absent: an item does not show the texts of the hidden starter it is made from. In JSON `description`, `source`. Other languages: `Texts`, on `ItemBlock`. |
| The crafted-item rule | The recipe tooltip reads `rarity` and `itemLevelRange.minLevel`, while a craft uses `generateRarity` and a level roll. Gear gets `generateRarity` off and one level, so a craft is what the tooltip shows. |
| Enhancement slots | An item's rarity is its perk count less one, so the slot its rarity needs must be filled. Classes bring the perks. A tool has 3 slots: above Rare, name the perk or Kiln refuses it. |
| Damage | `power = int(int(10 × 0.15 × level + 10) × dmgMod × speedMod)`, solved for `Power`. |
| Armour | `armour[kind] = round((15 × level + 23) × distribution[kind])`, solved for the number written. |
| Tooltip rows | The list is written from what the block says, so a starter's rows don't show. |
| Stack sizes | The kind's own, unless you say. A tool has an item entity and does not stack. |
| `clients` | `kiln` for all of this. A PNG icon or a model of your own needs `mod`, and a block that needs more than the manifest says is refused at load. |

Kiln checks the numbers these formulas use against the game's `keen::BalancingTable` when content is ready, and warns if an update changed them.

`Loot.Rolled` opts out of the crafted-item rule, for loot gear that scales with its area. It refuses a recipe, whose tooltip would then be wrong.

## Effects on items
`OnHit`, `OnKill` and `OnHitTaken` take the same `EffectBlock` a spell or status does. They run while the item is in an equipment slot, with no unlock:
- `OnHit`: on what the wielder hit. A hit dealt by an effect doesn't count, so an effect can't trigger itself.
- `OnKill`: on what the wielder killed.
- `OnHitTaken`: on whoever hit the wearer.

`Perks` follow the game's rule instead: a perk runs once a player has unlocked its slot.

## Mistakes
A wrong block registers nothing, and the message names the block and the field. The file's other blocks still load.
```
blocks/gear.json: block weapon rusty, stats.level: 99 is outside the game's item levels 1..55
blocks/gear.json: block item bad, visual: no visual "gone-look"; there are: ember-look
```
`kiln lint` says most of this first: KL102, KL104 and KL105 from the schema, KL201 for `clients`, KL314 for a name that is nothing or the wrong kind, KL406 for a rolled item with a recipe.

## How many fit

One server holds about 730 new templates (a wand attack or a spell uses two, a station one), about 3,900 new items, about 1,500 new recipes, 512 recipes at one workshop and 1,024 new statuses, across all its mods. Past that, the block's item, recipe or template is refused with a line in `kiln.log` that names it: nothing crashes, and the rest of the mod still loads. Numbers come from the game's own tables, so a game update can change them.

Each block costs about the same to load however many there are: a lab server with 1,250 blocks was ready in about 6 seconds, with 5,000 in under half a minute. `kiln.log` says where the time went at each start (`load timing, ...`).

## Test
`mods/tests/blocks-items-test` runs on a dedicated server with no player. A spawned player entity has no equipment slots set, so the test says which slot the chest is, through a test-only request of kiln.skills; it creates the switch that request needs, `<kiln>/data/skills/lab-ops`. Start it twice: the second run checks ids and stored items across the restart. It logs `blocks-items-test: PASS` and `FAIL` lines, and `blocks-items-test done: N PASS, M FAIL`.
