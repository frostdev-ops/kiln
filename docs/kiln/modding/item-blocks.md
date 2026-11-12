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
JSON sections: `items`, `weapons`, `wands`, `armor`, `stations`, `lootTables`, `recipes`, and for parts `looks`, `weaponStats`, `armorStats`, `weaponClasses`, `armorSlots`, `armorSets`, `onHits`, `wornLooks`. Schema: [schemas/kiln-blocks.md](schemas/kiln-blocks.md).

## Parts
A part is a block of its own. Write it inline, define it once and share the handle, or name it in JSON. A premade such as `WeaponClass.Sword` is a plain instance of the same record.

| Part | Fields | Premades |
|---|---|---|
| `VisualBlock` | `Like` (an item's whole look), `Model`, `Icon`, `Dropped`, `Tint`, `Vfx`, `Animated` (a held model with moving parts) | |
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

**A held model with moving parts.** `VisualBlock.Animated` (`AnimatedModel`) is a skinned model of the mod's own with the graph that plays its clip, for the hand: a coal that turns in its cage, a bell that rocks. `Model` and `Graph` are each a GUID or the name of the `model` or `graph` step of `assets/cook.json` that makes it (the GUID the cook derives from the mod and that name; skinned models and their skeleton, clips and graph: [models.md](models.md), "Skinned models", and [animations.md](animations.md)).
```csharp
Visual = new VisualBlock { Model = emberModel, Animated = new AnimatedModel { Model = "cinderbrand_anim", Graph = "cinderbrand" },
                           Vfx = new VfxRef("cinder_embers") { Offset = new Vector3(0, 0.52f, 0) } }
```
```jsonc
"visual": { "model": "<guid>", "animated": { "model": "cinderbrand_anim", "graph": "cinderbrand" }, "vfx": { "name": "cinder_embers", "offset": [0, 0.52, 0] } }
```
- Kiln makes a template of the item's own, `<mod>.<item>.moving`, from one of the game's staffs (a skinned model with a graph already), with the model, the graph and the look's `Vfx` on the model's root at its `Offset` (no `Vfx`: none, not the staff's), and the item holds it. The look's rigid `Model` stays what the icon, the dropped model and the collection show; the rigid `.held` template above is not made.
- The hand is the class's own: when content is ready Kiln reads the slot of the visual template of the item's starter (a sword's right hand, a hammer's or a bow's left, a shield's arm) and puts the template there. A starter held in no hand is reported by block, and the model then stays in the staff's right hand.
- **Daggers** are a pair: a parent (a copy of the game's dual daggers) with a moving child in each hand, `<mod>.<item>.moving.r` and `.moving.l`. `LeftModel` and `LeftGraph` are the left one's (left out: the left hand holds `Model` too); each child takes the look's `Vfx`. Which child is which hand is read from the starter's pair when content is ready. `LeftModel` on any other class is refused.
- It is for what a player holds (a weapon, a wand, a tool), and asks `clients: mod`. A prop, a station or a piece of furniture refuses it (`visual.animated`). Refused by field: a model without a graph or a graph without a model, a name that is no GUID and no cook step's name, a left half without the other part, an animated look on something not held.
- Every machine has the template under the same GUID, cooked or not, and the item always holds it: a server spawns the held entity from it. Where the moving model or its graph is not served on that machine (not cooked there), the template stands in with the look's rigid model (with none, the `Like` item's or the class's own) and no graph of its own, so that player sees the rigid weapon in the same hand with the same VFX: see [models.md](models.md), "On a dedicated server".
- Not seen in a game yet: that the template moves in the hand, in the class's hand, with its VFX where the offset says, and for daggers one in each hand.

## Blocks
| Block | Fields |
|---|---|
| `ItemBlock` | `Kind`, `Visual`, `Stack`, `Rarity`, `Level`, `Texts`, `Heals`, `OnUse`, `Feeds`, `Fuel`, `Comfort`, `Tool`, `Ammo`, `Holds`, `Flow`, `Recipe`, `Loot` |
| `WeaponBlock` | `Class`, `Visual`, `Stats`, `Perks`, `OnHit`, `OnKill`, `Recipe`, `Loot`, `Moves` ([Moves](#moves)) |
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
- **Loot.** `Like` rolls it in chests and world pickups where that item is. `Tables` puts it into the game's own drop tables, as `DropPlace`s, by `DropsLike` or a table GUID. A table of your own, which a creature drops, is a `LootTableBlock`: see [Loot tables](#loot-tables).

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

## Loot tables
A loot table is a list of what can drop. A creature names it in `Drops`, and code can roll it.

```csharp
var cache = Blocks.Define("ember-cache", new LootTableBlock
{
    Entries =
    [
        new LootEntry { Item = shard, CountMin = 2, CountMax = 4 },                                           // always, 2 to 4
        new LootEntry { Item = GameItems.Material_T1_Metal_Scraps, CountMin = 1, CountMax = 3, Chance = 0.5 }, // half the time
    ],
});
Blocks.Define("ember-scavenger", new CreatureBlock { Body = Bodies.Vukah, Drops = cache });   // or Drops = "ember-cache"
```
```jsonc
{ "format": 1,
  "lootTables": { "ember-cache": { "entries": [
      { "item": "ember-shard", "countMin": 2, "countMax": 4 },
      { "item": "Material_T1_Metal_Scraps", "countMin": 1, "countMax": 3, "chance": 0.5 } ] } },
  "creatures": { "ember-scavenger": { "body": "vukah", "drops": "ember-cache" } } }
```
`Drops` takes a table by name (`"other-mod:name"` for another mod's), a handle, or a table written in place.

| Field | Meaning |
|---|---|
| `Entries[].Item` | A `GameItems` constant, an item of this mod by its block name, or `"other-mod:name"`. |
| `Entries[].CountMin`, `CountMax` | How many, 1 to 65535. Leave out `CountMax` for exactly `CountMin`. |
| `Entries[].Chance` | 0 to 1, how often the entry gives anything. Default 1. |
| `Entries[].Weight` | With `Pick = LootPick.One`: how likely this entry is the one picked. Default 1. |
| `Pick` | `Each` (default): every entry tries its own chance. `One`: one entry is picked by weight, then its chance decides. |
| `Rolls` | How many times the table rolls, 1 to 64. What comes up is added together per item. |

- **Kiln rolls it, not the game.** The game's drop tables are fixed resources: a mod can add a stack to one or change its counts, and cannot make a new one. The game also rolls a creature's loot from its death sequence, which only runs near a player. A loot table is Kiln's instead. It is rolled on the server where a creature of the block dies, with no player needed, and the stacks are dropped as loose pickups around the spot. The game's own loot for the creature is not touched: one that dies near a player should drop that as well (not yet checked with a player).
- **For code**, the handle rolls it: `cache.Roll(random)` gives the stacks and touches nothing; `await cache.Drop(position)` drops them as pickups; `await cache.Give(chest)` puts them into an inventory through `Items.Give`. A seeded `Random` makes a roll repeat.
- **The world's Loot Amount** setting does not scale it.
- **Pickups** are the game's generic pickup (`Spawn.Item`), and the game does not save them. The lab reads them back on the server; how they look and fall in a player's game is not checked yet.
- **The game's own tables.** To put an item into a table the game already has, use `Loot.Tables` on the item (`DropPlace`: a table GUID or glob, or `DropsLike`, every table that drops that item). Up to 0.2.0-dev.19 `DropPlace` was called `LootTableBlock`: C# that still writes `new LootTableBlock { DropsLike = ... }` there stops at build with error CS0619 saying so. The JSON (`loot.tables`) did not change.
- **Mistakes.** An empty table, `Pick = One` with every weight 0 (or weights too large to add up), a chance outside 0 to 1, a `countMax` under `countMin`, a block that is no item or no table: refused at load, by block and field. An item name that is nobody's is said once content is ready: `block loottable x, entries[1].item: the game has no item "No_Such_Item"` (`drops.entries[1].item` in a table written in a creature). The rest of the table still rolls.
- **`clients`.** A table asks nothing of players' games. The items in it ask what they ask.

## Recipes of their own
A `RecipeBlock` on an item says how that item is made. A `RecipeBlock` of its own, in `recipes`, is one more recipe for any item: a game item at the game's workbench, or an item of yours there. It needs `Output`.

```csharp
Blocks.Define("ember-charcoal", new RecipeBlock
{
    Output = GameItems.Material_T1_Charcoal, Makes = 2, Station = StationKind.Workbench.Workshop,
    Needs = { [shard] = 2, [GameItems.Material_T1_Wood_Logs] = 1 },
});
```
```jsonc
{ "format": 1,
  "recipes": { "ember-charcoal": { "output": "Material_T1_Charcoal", "makes": 2, "station": "0x4da88f73",
      "needs": { "ember-shard": 2, "Material_T1_Wood_Logs": 1 } } } }
```
The fields are the recipe's as everywhere: `Station`, `Makes`, `Needs`, `Burns`, `Seconds`, `Sheltered`, `Comfort`. `Station` is `"hand"`, a game workshop's id (the workbench is `0x4da88f73`, `StationKind.Workbench.Workshop`), or a station block of this mod, by name or handle. A workshop of a station of your own needs `Seconds`, as for any recipe. The recipe is `<mod id>.<name>` in the game, and its id comes from the mod id and the name, so it stays.

- A game recipe for the same item stays. This is another one, registered beside it (read back from the game's recipe registry; the workbench screen in a player's game is not checked yet).
- `clients` is `kiln`: Kiln clients get the recipe from the server.
- Mistakes are refused at load by field: no `Output`, nothing in `Needs`, `Makes` outside 1 to 65535, a `Station` that is no station, an `Output` that is not an item.

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

## Moves
What a weapon's attack slots play: its own motions, the moment its blow lands, its own chain. A slot left out keeps its class's, and a weapon with no `Moves` is made exactly as before.

Sample: `mods/samples/WeaponMoves`, a sword with a chain of two swings, a heavy and a block on clips of its own (authored, retargeted onto the player, the second swing mirrored from the first), and a held model with moving parts.

```csharp
Blocks.Define("cinderbrand", new WeaponBlock
{
    Name = "Cinderbrand", Class = WeaponClass.Sword,
    Moves = new WeaponMoves
    {
        Primary = [ new MoveBlock { Animation = "cinder-slash-1", Like = "Attack_Player_1H_Single_LightChain1", HitAt = 0.28 },
                    new MoveBlock { Animation = "cinder-slash-2", Like = "Attack_Player_1H_Single_LightChain2", HitAt = 0.31 } ],
        Heavy = new MoveBlock { Animation = "cinder-cleave", HitAt = 0.9, Length = 2.2 },
        Block = new MoveBlock { Animation = "cinder-guard" },
    },
});
```
```jsonc
"weapons": { "cinderbrand": { "name": "Cinderbrand", "class": "sword",
  "moves": {
    "primary": [ { "animation": "cinder-slash-1", "like": "Attack_Player_1H_Single_LightChain1", "hitAt": 0.28 },
                 { "animation": "cinder-slash-2", "like": "Attack_Player_1H_Single_LightChain2", "hitAt": 0.31 } ],
    "heavy":   { "animation": "cinder-cleave", "hitAt": 0.9, "length": 2.2 },
    "block":   { "animation": "cinder-guard" } } } }
```

**A move** (`MoveBlock`) has four fields, all optional; a move with none of them changes nothing and is refused.
- `Animation`: the state its motion plays. See [What `animation` takes](#what-animation-takes).
- `Like`: the game sequence it starts from, a sub-sequence name or a sequence GUID (the catalog's `sequences`). Left out: what the class has in that slot; for the n-th link of a chain, the class's own chain followed n-1 times (a sword's light chain has two links, so a third link needs a `Like`).
- `HitAt`: seconds into the move its blow lands.
- `Length`: seconds the whole move lasts.

`Primary` and `Heavy` take one move or a list, a chain: `Primary = [first, second]`, in JSON a list. One move and a list of one are the same. The other slots take one move.

### Slots
A slot is an entry of the item's `sequences`, and the same slot sits at another index on every starter, so Kiln never names one by index: each is a selector, and when content is ready Kiln finds it on the made item. Every entry the selector matches is swapped (the daggers list the jump attack twice). An own class (`WeaponClass` with `like`) has its premade's slots.

| Slot (JSON) | Selector: `triggeredBy`, `requirements`, `specialInteractionType` | Classes |
|---|---|---|
| `Primary` (`primary`) | `Primary` | sword, axe, mace, greatsword, greataxe, hammer, daggers |
| `Heavy` (`heavy`) | `Heavy`, `IsGrounded` | the same |
| `HeavyAir` (`heavyAir`) | `Heavy`, `IsAirborne` | the same. Left out with `Primary` given: it plays `Primary`'s first link, as the game's weapons do |
| `DodgeAttack` (`dodgeAttack`) | `Primary`, `IsEvading` | the same |
| `JumpAttack` (`jumpAttack`) | `Primary`, `IsAirborne_Not_Falling` | the same |
| `PlungeAttack` (`plungeAttack`) | `Heavy`, `IsAboveTarget` + `IsAirborne_Falling` | the same |
| `Block` (`block`) | `Block` | the same, and shield |
| `Parry` (`parry`) | `Parry` | the same, and shield |
| `Special` (`special`) | `SpecialAbility` (greataxe and hammer: `SpecialAbility`, `IsGrounded`) | the melee classes, and bow |
| `Execution` (`execution`) | `Interact`, `Enemy_Execution` | the melee classes |
| `SneakAttack` (`sneakAttack`) | `Interact`, `Sneak_Attack` | the melee classes |

A bow's and a staff's primary slot holds only the motion of having no ammo: their attacks are the arrow's and the spell's, so they have no `Primary` (a spell's cast takes `animation` and `hitAt`, below). A move for a slot the class does not have is refused at load, naming the weapon, the slot and the slots it has. A slot the item no longer has (a game update) is reported when content is ready, by block and slot, and the other slots still apply.

### What Kiln does for a move
1. **Clone.** A move with `Like` is a clone of that sequence, made at load; one without is a clone of the slot's own sequence, made when content is ready. Name `<mod>.<weapon>.<slot>`, in a chain `<mod>.<weapon>.<slot>.<n>` (n from 1); GUID `derive(mod, "sequence", "<key>.<slot>[.<n>]")` with `key` = `Was` or the block's name. A sequence with two sub-sequences (the greataxe special has one for the ground and one for the air) is cloned whole, and the move applies to each sub-sequence.
2. **State.** The move's states go into the clone's state events in order (below).
3. **Retime.** When content is ready, from the clone's own events (no game timing is written into a mod or into Kiln): one map over the sequence's clock, with `h0` its first damage event (`SpawnImpact`) and `len0` its length: up to `h0`, `f(t) = t × hitAt / h0`; after it a shift, `f(t) = hitAt + (t − h0)`, or with `Length`, `f(t) = hitAt + (t − h0) × (length − hitAt) / (len0 − h0)`. `Length` alone keeps the blow where it is and scales what follows; a move with no damage event (a block) and a `Length` scales as a whole. Through the map go every event's `time`, the end of every window (`time + duration`; a damage window keeps its own duration), the end of the spans an event carries (`lerpDuration`, `hitStopDuration`), every `SetActionOportunity.followUpTime`, and the length. The map keeps the events' order, and the rules are written so that no event overtakes another on the way (later ones from the last to the first, then earlier ones from the first to the last): kiln.gamedata keeps events sorted and `[Type#k]` counts in the current order ([gamedata.md](../services/gamedata.md), "Event order").
4. **Chain.** In each clone of a `Primary` chain, every `SetActionOportunity` for `Primary` with a follow-up names the next link (the last the first), and every one for `Heavy` names the first `Heavy` clone when the weapon has a `Heavy` move. In `Heavy` clones, `Heavy` follow-ups name the next heavy link (a single heavy names itself where the game's heavy names itself, and keeps the game's follow-up otherwise), and `Primary` follow-ups the first primary link. An opening with no follow-up (an evade) stays.
5. **Slots.** The item's matching `sequences[i].sequence` name the clones.

Refused at load: a slot the class lacks, a move with no field, `hitAt` or `length` that is not a number of seconds above 0, `hitAt` at or past `length`, `hitAt` on a block or a parry (they land no blow). Reported when content is ready, by block and slot, the rest still applying: a `hitAt` on a motion with no damage event, or whose blow is at 0 s; a motion with fewer state events than the move names; a slot the item no longer has; a `Like` the game does not have.

What `kiln blocks` shows under the weapon: `moves` (the slots' selectors, the clones with their `like`, the states as written and resolved, the timing asked for, where each clone's follow-ups go), and once content was ready `movesReady` (the indices found, what each clone started from, the rules).

**Events that move, per kind of sequence** (the starters' sequences of each slot). Every event's `time` moves with the map; listed are the kinds of events there, and what else of theirs moves:

| Kind | Events | Also moved |
|---|---|---|
| light swing (`primary`, `heavyAir`) | SetStateEvent, SetAnimationEvent, SetAnimationBoolInputEvent, Enable/DisableAbilityEvent (the hand-back), SetActionOportunity, SpawnImpact, LerpMovementFactorEvent, RotationLerpEvent, RootMotionWarpEvent, PreserveVelocity, AvoidHolesEvent, ReplaceHitReactionEvent, ActivateCombatStanceEvent, ClearActionOportunity, Sfx/Vfx/Foley/EnvironmentEffect notifiers, TriggerNoiseEvent | the ends of the input, warp, rotation, velocity and hit-reaction windows; `followUpTime`; not the SpawnImpact's duration |
| heavy (`heavy`) | as a light swing, with CameraShakeEvent, ClientHitStopListenerEvent, RootMotionScaleModifierEvent, LerpRotationFactorEvent | as above, and `hitStopDuration` |
| dodge attack | as a heavy, with PayUsageCost, NotifierEvent | as above |
| jump attack | JumpEvent, ImpulseEvent, ApplyImpulseEvent, KeepAliveEvent, ResetFallDamageEvent, PayUsageCost, and a swing's | as above |
| plunge attack | three SetAnimationEvents (start, fall, landing), LoopEvent (the fall, held until landing), KeepAliveEvent, Set/ResetGravityEvent, JumpEvent, ScopeEvent, and a swing's | the loop's and the scope's ends |
| special | the most: LoopEvent, SpawnEntityEvent, ApplyBuffEvent, CleanseBuffEvent, ChangeColliderSetup, AdaptiveTriggerEffectEvent, and a heavy's; the greataxe's has two sub-sequences, each with three SetAnimationEvents | each sub-sequence by its own blow |
| execution, sneak attack | SyncAttackEvent, LockCameraIdEvent, ChangeColliderSetup, ImpulseEvent, and a swing's (two SpawnImpacts) | the camera lock's and the collider's ends |
| block | SetAnimationEvent, LoopEvent (held while the button is), LerpMovementFactorEvent, LerpRotationFactorEvent, SetAbortByMovement, SetActionOportunity (`Parried` names the parry), and the daggers' a SpawnImpact | `length` only: no `hitAt` |
| parry | SetAnimationEvent, Set/ClearStateEvent, SetActionOportunity, RotationLerpEvent | `length` only: no `hitAt` |
| staff cast | SetAnimationOverlayEvent (the raise), LoopEvent, KeepAliveEvent, SetAnimationEvent (the cast), SpawnEntityEvent (the shot), PayUsageCost, notifiers | the loops' ends |
| wand attack | SetAnimationOverlayEvent (one or two; the special has a SetAnimationEvent too), SpawnEntityEvents (shots that fire and shots the premade silences), PayUsageCost, notifiers | |

### What `animation` takes
- **A GUID**, or **`0x` and 8 hex digits** (a state's id, as the catalog's `graphs` section and `player-anim` write it): a state the game has, the advanced form. It is not checked against the graph. An id becomes 16 bytes whose FNV-1a is that id: the engine only hashes the reference.
- **A state of this mod by name**: GUID `derive(mod, "state", name)`, the state `player-anim` defines in `assets/player-states.json`.
- **A game state by name**, `"State"` or `"Machine/State"`: the id the game's graph has for it.

Names are looked up when content is ready, through player-anim's lookup. **In this build the lookup is not there yet**: a name is reported when content is ready ("player states are not available in this build") by block and slot, and the motion keeps its own state. A mod state that is refused, pending or unavailable is reported with player-anim's reason. A name no machine and no mod has, a name several machines have (the message lists them, write `Machine/State`), a state no id enters (such as `Idle_Block`), and a state with several ids (a jump-attack state has four, one per weapon family: write the one you mean as its `0x` id) are reported the same way.

One name goes to the motion's first state event; a list names each in order, a `null` keeping that one: a plunge is `["start", "fall", "landing"]`, and `"start"` alone changes only its start. Which event: a mod's or a game's state by name says its kind (a full-body state is entered by `SetAnimationEvent.animationName`, an overlay by `SetAnimationOverlayEvent.overlayName`); a GUID or an id says none, and the motion's full-body state is used when it has one, else its overlay (a staff's FixedAnim cast has both, the cast being the full-body one; the wand chains and the bow have only overlays). One move takes one kind. A motion with neither event names no state, and a move's animation on it is reported.

### Block and parry
A block is entered by the sequence: `Ability_Block`'s `SetAnimationEvent` names `Block_FullBody` (`0xeb2a5b2e`) in `SM_Main`, a layer blend that lays the guard pose over `SM_Block_Locomotion`. That machine's `Idle_Block` and `Move_Block` have no id: the legs while blocking are chosen by a float input (the movement), never by a sequence, so no move can name them. So `"block": { "animation": … }` changes the guard: a state of the mod made like `Block_FullBody` keeps the block's locomotion underneath and plays the mod's clip as the guard. It cannot change the legs' idle or walk while blocking, and the block's hold is the sequence's loop (`length` times it). The parry is the same with `Parry_FullBody` (`0xfeec6023`), named by `Ability_Player_Parry_AllWeapons`. The daggers have a block sequence of their own, so a daggers' block move starts from theirs.

### Wands and staff casts
A wand attack's `Moves` takes a premade as before, or one changed: `WandMoves.Heavy with { Animation = …, HitAt = 1.1 }`, in JSON `"moves": { "like": "heavy", "animation": …, "hitAt": 1.1 }` (no `like`: the attack's own premade). A spell's `CastBlock.Animation` the same: `CastAnimation.Lob with { Animation = …, HitAt = 1.2 }`, `"animation": { "like": "lob", "animation": …, "hitAt": 1.2 }`. `HitAt` is the release, the same thing as `releaseAt`: give one, or the same number (two numbers are refused). With `releaseAt` alone the shots (and on a staff the cost, the VFX and the sound) move to it as before; with `hitAt` the whole motion is retimed when content is ready, its first firing shot (a wand premade's shots that fire, a staff cast's shot) landing on it. A `like` other than the premades is refused: a wand motion's shots are a mix of the one that fires, the ones the premade silences and the per-element ones, and only the premades say which is which. Supporting any wand sequence would mean reading each shot's template when content is ready and telling a projectile from the rest by its components: about a day, with a lab check per element, and a game update that adds a shot would need the same check again. Where they work: a spell's `CastBlock.Animation` and a wand attack's `Moves`. An ability's cast (`AbilityBlock.Cast`) refuses them, by block and field (`cast.animation.animation`, `cast.animation.hitAt`): not yet on an ability's cast; its premade alone (`"lob"`) is still its motion.

### Limits
- Checked in a lab (`mods/tests/weapon-moves-test`, a dedicated server, fresh and after a restart): on the game's data the clones exist under their names and GUIDs, start from their `like`, name the state given by GUID or `0x` id, sit in the slots their selectors name (and `heavyAir` plays the first primary link) while every other slot and the game's own weapons keep their sequences; every event, window end, `followUpTime` and the length follow the map, in order; the chain's and the heavy's follow-ups name the clones; a spawned player character pressing the attack button gets link 1, link 2, link 1 from the game itself, and holding it gets the heavy; the damage window of a retimed clone opens within one step of `hitAt`. Not checked in a game: that a player sees the motions (guided checks below), that a mod state is entered, that sub-sequence rules reach the second sub-sequence of a clone (`<clone>#1`), and a move on an overlay: kiln.content refuses every `SetAnimationOverlayEvent.overlayName` in this build ("is not a loaded keen::ObjectReference<keen::animationgraph::OverlayId>"), so a wand's or a staff overlay cast's `animation` is reported and the motion keeps its own overlay. The offline tests (`managed/tests/Kiln.Blocks.Tests`: `WeaponMovesTests`, `MoveTimingTests`, `CastMovesTests`) check what the compile and the content-ready planner make, over made-up slots and events, and replay kiln.gamedata's re-sort after every rule.
- A plunge's and a special's fall loop is retimed as any window: the loop holds until landing, so the map moves its start and end, not how long a fall lasts.
- The jump attack's follow-up to the plunge, and every other slot's follow-ups, stay the game's: only the primary and heavy chains are linked to each other.
- A move whose only change could not be made (a name that cannot be looked up) still plays its clone, which is the class's sequence unchanged.

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

`mods/tests/blocks-loot-test` checks loot tables, creature drops and recipes of their own, with no player: a creature block spawned and killed with `Combat.SetHealth(0)` drops its table's stacks as pickups (`blocks: blocks-loot-test:ember-scavenger died at (...): drops blocks-loot-test.ember-shard x3, Material_T1_Metal_Scraps x2`), a table written in a creature and a C# twin of the data file do the same, a creature with no `Drops` drops nothing, a table rolls (chance, `Pick`, `Rolls`, a seed) and its stacks are dropped and given into an inventory, a recipe of its own is read back from the game's recipe registry at the workbench (`0x4da88f73`) with its inputs and output, and wrong blocks are refused by block and field. Lab: 36 PASS, 0 FAIL on a fresh world and on a restart. Not proven with a player: that a creature killed near one drops the game's own loot as well, and what the pickups look like in a client. `mods/samples/LootBlocks` is the sample, in C# and as `blocks/twins.json`.

`mods/tests/weapon-moves-test` checks [Moves](#moves) on a dedicated server, with a spawned player character and training dolls round it (the test drives the character the way `mods/tests/creative-test` does: ready for gameplay, standing on the ground). Its weapons are in `blocks/weapons.json` (a sword with a two-link chain, a heavy and a block, a greataxe with one heavy and nothing else, a sword with no moves, a wand with a move on its primary attack), and `chain-sword-cs` is the chain sword in C#: both lower to the same. Until player-anim lands, its moves name game states of other weapon families, by GUID and by `0x` id. What it expects it works out from the game's original values and the formula above, not from what the moves code wrote: the clones' names and GUIDs, the `like` each starts from and the state it plays, the slots (by their selectors, every other slot the starter's), the retime of every event, window end and follow-up time, the chain links, the game's own weapons and sequences unchanged. Then the moves are played: the attack button written into the character's input (`PlayerInput.fromClient.digitalInput`) makes the game pick the held weapon's slot and the chain's follow-ups itself (link 1, link 2, link 1); holding it starts the heavy. `Abilities.PlaySequence` plays the chain too, but starts no damage window on a player character, and `PlayerStates.Diagnostics.LabUseItem` starts link 1 with the sword as the used item, but no blow either: only the input lands blows. The step the damage window opens is measured against `hitAt` (within one step), and the hurt event (`EntityHurtEvent.Step`) must come inside the blow's window; the game's own light chain and the greataxe's heavy are the controls. Start it twice: the second run checks the ids and the slots across the restart. Start it with `KILN_PLAYERSTATE_LAB=1` for the `LabUseItem` line. It logs `weapon-moves-test: PASS` and `FAIL` lines, `weapon-moves-test done: N PASS, M FAIL`, and `KNOWN` lines for defects outside the test (the overlay move above); a file named `fail-once` in its data folder turns one expectation of each group wrong. Its four `[ClientCheck]`s (`blocks.moves-chain`, `blocks.moves-hit-timing`, `blocks.moves-heavy-block`, `blocks.moves-vanilla-unchanged`) are what only a player can judge; the lab only lists them (`client checks: Register found 4; ...: listed`). Lab: fresh 179 PASS, restart 199 PASS, 0 FAIL, 2 KNOWN.
