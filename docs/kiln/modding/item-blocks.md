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
`VisualBlock.Vfx` is a VFX on the thing itself: in the hand of whoever holds the item (in the right hand at the grip by default; `Offset` moves it up the blade), and on what stands there for a placed thing. In the hand it is a visual template of the item's own, a copy of the game's lit axe with the item's model. `OnHitBlock.Vfx` plays where a hit lands, and a wand attack's `CastBlock.Vfx` plays on the cast.
```csharp
Blocks.Define("glow-sword", new WeaponBlock { Name = "Glow Sword", Class = WeaponClass.Sword, Visual = new VisualBlock { Vfx = new VfxRef("frost_glow") { Offset = new Vector3(0, 0.8f, 0) } } });
```

## Blocks
| Block | Fields |
|---|---|
| `ItemBlock` | `Kind`, `Visual`, `Stack`, `Rarity`, `Level`, `Texts`, `Heals`, `OnUse`, `Feeds`, `Fuel`, `Comfort`, `Tool`, `Ammo`, `Holds`, `Flow`, `Recipe`, `Loot` |
| `WeaponBlock` | `Class`, `Visual`, `Stats`, `Perks`, `OnHit`, `OnKill`, `Recipe`, `Loot` |
| `WandBlock` | `Primary`, `Heavy`, `Special`, `Class`, `Visual`, `Stats`, `Perks`, `OnHit`, `OnKill`, `Recipe`, `Loot` |
| `ArmorBlock` | `Slot`, `Set`, `Visual`, `Look`, `Stats`, `Perks`, `OnHitTaken`, `Recipe`, `Loot` |
| `StationBlock` | `Kind`, `Visual`, `Recipe`, `Loot`, and more: [world-blocks.md](world-blocks.md) |

All take `Name`, `Description` and `Was` (the name before a rename: ids come from the oldest name, so saves keep it).

An item's kind follows from what you fill in: `Tool` makes a tool, `Ammo` an arrow, `Feeds` food, `Heals` or `OnUse` a potion, anything else a material. Say `Kind` to be explicit; a field the kind has no use for is an error. A water container is always named: `ItemKind.WateringCan` or `ItemKind.WaterPouch` (`"watering-can"`, `"water-pouch"`). The kinds are these premades only: an item that starts from a game item you choose is `Content.Items.Register`'s ([content.md](../services/content.md), "New items and recipes").

- **Potion.** `Heals` is the game's own drink and the tooltip's Health row. `OnUse` runs your effects on whoever uses it, from a use sequence of its own. Without `Heals`, its tooltip shows the Health, Mana and Stamina a use always gives (an effect with a `Chance` isn't counted).
- **Food.** `Feeds` names a status. It goes in the game's food slots for the status's `Seconds`, which is also the tooltip's Duration. A game food sets its own time in the item, and eating it uses that time rather than the status's, so Kiln writes the status's time into the item.
- **Arrow.** `Ammo.Damage` and `Ammo.Power` add to the bow's, as the game's arrows do.
- **Tool.** `Harvests` says what it is, `ToolClass` or the shorthands in `Harvest`. Each class takes the fields it has a number for, and refuses the others by name:

| Class (JSON) | What it is | Fields |
|---|---|---|
| `Axe` (`wood`), `Pickaxe` (`stone`) | The game's axe and pickaxe | `Power` (wood, stone damage; the rest scales along), `StaminaCost`, `BreaksInto` |
| `Rake` (`rake`) | Flattens the ground where it aims, in a base | `Power` (flattening strength: the game's rakes 500, 1000), `Range` (metres, the box the cursor shows too; 3), `StaminaCost`, `BreaksInto` |
| `Sieve` (`sieve`) | The game's mining sieve | `Power` (stone damage), `StaminaCost`, `BreaksInto` |
| `Glider` (`glider`), `GrapplingHook` (`grappling-hook`) | In their own slots | `StaminaCost`; a hook's `Range` (13.5) |
| `FishingRod` (`fishing-rod`) | The game's simple rod | `Fishing` (`Strength` 1, `Endurance` 30, the tooltip's numbers) |
| `Torch` (`torch`) | Held, it lights and burns down | `Durability` (seconds it burns while held, the tooltip's Duration: 600), `BreaksInto` |

`BreaksInto` is what it becomes when its durability runs out, as the game's torch becomes a burnt one: a game item or one of yours. Only a torch burns down over time; the game wears the other tools by its own rule (the world's durability setting), with no number of the item's own, so `Durability` on them is refused.
```csharp
Blocks.Define("garden-rake", new ItemBlock { Name = "Garden Rake", Tool = new ToolBlock { Harvests = ToolClass.Rake, Power = 750, Range = 5 } });
Blocks.Define("slow-torch", new ItemBlock { Name = "Slow Torch", Tool = new ToolBlock { Harvests = ToolClass.Torch, Durability = 1200, BreaksInto = "ash" } });
```
- **Water container.** `Holds` is its capacity in charges, as the game counts them: its watering can holds 100, its pouch 20 sips. `Flow` is how fast it fills and pours, a multiple of the game's own. See [Water containers](#water-containers).
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
| Stack sizes | The kind's own, unless you say. A tool or a water container has an item entity and does not stack. |
| A water container's water | Lives in the item's own entity, made from a template every can of the game shares (`Pide_Fuel_Watering_Can`). `Holds` clones that template with its `FuelSetup.maxFuelCharges` and points the new item at the clone, so the game's cans keep 100. |
| `clients` | `kiln` for all of this. A PNG icon or a model of your own needs `mod`, and a block that needs more than the manifest says is refused at load. |

Kiln checks the numbers these formulas use against the game's `keen::BalancingTable` when content is ready, and warns if an update changed them.

`Loot.Rolled` opts out of the crafted-item rule, for loot gear that scales with its area. It refuses a recipe, whose tooltip would then be wrong.

## Water containers
```csharp
Blocks.Define("big-can", new ItemBlock
{
    Name = "Big Watering Can",
    Kind = ItemKind.WateringCan,
    Holds = 1000,          // charges: the game's can holds 100
    Flow = 4,              // fills and pours 4x as fast as the game's can
    Recipe = new RecipeBlock { Station = "0x41bf48ce", Needs = { ["Material_T1_Metal_Scraps"] = 10, ["Material_T1_Charcoal"] = 6 } },
});
```
```jsonc
{ "format": 1, "items": { "big-pouch": { "name": "Big Water Pouch", "kind": "water-pouch", "holds": 60, "flow": 2 } } }
```
| Field | What | Range | Default |
|---|---|---|---|
| `Holds` | What a full one holds, in charges | 1..1023 | the kind's: can 100, pouch 20 |
| `Flow` | How fast it fills and pours, as a multiple of the game's own | 0.25..8 | 1 |

- **Why 1023.** The game keeps a container's water as charges × 64 (a can of 100 holds 6,400) and sends players that number in 16 bits, which stop at 65,535. At 1,023 charges a full can is 65,472 and players see exactly what the server has. At 1,024 the server holds 65,536 and players are sent 65,535: above 1,023 a can would show full until it is down to that. The block refuses anything outside 1..1023 with that reason.
- **One rate for both ways.** Each step of filling or pouring moves the item's `Volume` (32 for the can, 16 for the pouch: half a charge, a quarter), about 20 times a second while the button is held. The game has no rate for filling alone: at a lake, at a well or barrel, onto crops and onto soil, it is the same number. So `Flow = 4` also waters 4× as fast and empties the can 4× as fast. A step never overfills: the game moves at most what the source has and the destination has room for.
- **What it takes**, from the game's code and data (not yet timed in a game): the game's can at a lake goes from empty to full in about 10 s (100 charges at about 10 a second). `Holds = 1000, Flow = 4`: about 25 s; `Holds = 1000` alone: about 100 s. A well gives what it holds (75 to 500 charges), so a big can can empty one.
- `clients` is `kiln`: Kiln clients get the can and its template from the server. A new `Holds` (a reload of the mod) applies to cans made after it; cans already made keep what their entity was made with at least until the world next loads (not checked further).
- The Portable Water Pump fills the same way and has a `Volume` of its own (128), but is not a kind: change its `Volume` as data (`Content.ItemData.Get("Tool_T3_Channelled_Water_Hose_Tool")!.Config("Volume")`).
- Lab test: `mods/tests/blocks-water-test` (and `Observer/`). Measurements and the game's code: [docs/re/items.md](../../re/items.md), "Water containers".

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
`mods/tests/blocks-items-test` runs on a dedicated server with no player. A spawned player entity has no equipment slots set, so the test says which slot the chest is, through a test-only request of kiln.skills; it creates the switch that request needs, `<kiln>/data/skills/lab-ops`. Start it twice: the second run checks ids and stored items across the restart. It logs `blocks-items-test: PASS` and `FAIL` lines, and `blocks-items-test done: N PASS, M FAIL`. Started with `KILN_PLAYERSTATE_LAB=1` (test labs only), it also eats an Ember Stew as a player does, with the stew as the used item, and checks the status's time. `syncitems.py --port <labPort> --lab <lab folder>` checks what a Kiln client without the mod gets: definitions with their texts, clones, and the programs they name.

`mods/tests/blocks-water-test` checks water containers, with no player: the new cans' templates (1,000, 1,023, a pouch of 60 from `blocks/water.json`), the game's can and template untouched, `Flow` as the items' `Volume`, the recipe's station, what a can holds on the server and what players are sent (a can of 1,000 is 64,000 both ways), and holds 1024 refused. Use a lab made with `--save` from a world with a base: the cans go into one of its chests, and a second start checks that a full can of 1,000 is still full. `Observer/` (mod `blocks-water-observer`) logs what an `unload blocks-water-test` puts back: the recipe off its station, the items registered until the restart, the game's can and template as they were. Lab: fresh 30 PASS, restart 4 PASS.
