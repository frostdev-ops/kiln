# Affixes: rolled modifiers on one item

"+5 Strength", "+10% Move Speed", "+17% Resist Fire": a bonus that belongs to one sword, not to every sword of its kind.
A mod says which affixes there are and which items roll them. Kiln keeps each item's rolls with that item, applies them
to whoever wears or holds it, and sends them to the games of players near it. New in 0.2.0-dev.27; in `Kiln.Blocks`
(`<KilnServices>Blocks</KilnServices>`, or a reference to `Kiln.Blocks`). Sample: `mods/samples/AffixSword`.

```csharp
// one kind of modifier: its stat, its tiers and the range each tier rolls in
Affixes.Define("bear", new AffixBlock
{
    Name = "of the Bear", Stat = Stat.Strength,
    Tiers = [new AffixTier { Min = 2, Max = 4 }, new AffixTier { Min = 5, Max = 8, Weight = 0.5, MinItemLevel = 20 }],
});
Affixes.Define("fleet", new AffixBlock { Name = "Fleet", Stat = Stat.MoveSpeed, Stacks = false,
    Tiers = [new AffixTier { Min = 0.05, Max = 0.15, Step = 0.05 }] });

// which items roll which affixes, and how many
var blades = Affixes.DefinePool("blades", new AffixPoolBlock
{
    Items = [GameItems.Weapon_T1_1H_SwordEpic_01],
    Affixes = [new AffixEntry { Affix = "bear" }, new AffixEntry { Affix = "fleet", Weight = 2 }],
});

AffixResult rolled = blades.Roll(sword);              // sword: an ItemInstanceRef (Items.Held, a give result, ...)
if (rolled.Ok) Log.Info(rolled.Affixes[0].Text);      // "+3 Strength (of the Bear)"
```
The same as data, with `Affixes.Load(File.ReadAllText(path))` in `OnLoad` (camel case, the same fields):
```jsonc
{ "format": 1,
  "affixes": { "bear":  { "name": "of the Bear", "stat": "strength", "tiers": [ { "min": 2, "max": 4 }, { "min": 5, "max": 8, "weight": 0.5, "minItemLevel": 20 } ] },
               "fleet": { "name": "Fleet", "stat": "move-speed", "stacks": false, "tiers": [ { "min": 0.05, "max": 0.15, "step": 0.05 } ] } },
  "pools": { "blades": { "items": [ "Weapon_T1_1H_SwordEpic_01" ], "affixes": [ { "affix": "bear" }, { "affix": "fleet", "weight": 2 } ] } } }
```

## The shapes

| Block | Fields |
|---|---|
| `AffixBlock` | `Name` (what players read; kept with every roll), `Stat` (a [`Stat`](blocks.md#statuses) constant, or `new Stat("my-key", guid, Whole: true)` for another attribute of the game), `Tiers` (1 to 16, the weakest first), `Stacks` (default true), `Group` |
| `AffixTier` | `Min`, `Max`: whole points for a whole-number stat (`Strength`: 3), a share for the others (`MoveSpeed`: 0.05 is 5 %). Never 0, one sign (negative ranges make a curse). `Step`: the grid a value lies on (default 1, or 0.01 for a share). `Weight`: how likely this tier is, among the tiers the item's level allows. `MinItemLevel` (default 0; at least one tier must allow any level) |
| `AffixPoolBlock` | `Items`: the item types that roll from it (`GameItems` constants, or items of the mod by block name); left out: any item with an item entity of its own (gear, tools). `Affixes`: entries of `Affix` (a name of this mod, or `"other-mod:name"`) and `Weight`. `Min`, `Max`: how many different affixes one roll gives (0 to 8, default 1 and 1) |

A roll picks `Min` to `Max` different affixes by weight, never two of one `Group`, then for each a tier by weight among
those whose `MinItemLevel` the item's level reaches, then a value on the tier's grid. A seeded `Random` makes a roll
repeat. A mistake in a block is a `BlockError` that names the block and the field.

`Affixes.Define` and `DefinePool` belong to the calling mod (call them in `OnLoad`); a pool's affixes must be defined
before it. They are not sections of `blocks/*.json` yet and `Blocks.Define` does not take them: see
[what is not there yet](#what-is-not-there-yet).

## One item's affixes (server, tick thread)

| Call | Does |
|---|---|
| `Affixes.Of(item)` | the item's affixes: `RolledAffix(Affix, Name, Stat, Tier, Value)`, with `Text` ("+5 Strength (of the Bear)") and `ValueText` |
| `Affixes.Roll(item, pool, random, replace)` / `pool.Roll(item)` | rolls the pool onto the item. Not `Ok` when the pool is not for this item type, or the item already has affixes and `replace` is false |
| `Affixes.Add(item, affix, tier, value)` | adds one; the value is rolled in the tier's range when left out. Not `Ok` when the item has that affix, one of its group, or 8 already |
| `Affixes.Set(item, list)` | gives the item exactly these (for commands and tools); the affixes need not be defined now |
| `Affixes.Remove(item, affix)` / `Clear(item)` | takes one, or all, off: true when something went |
| `Affixes.Active(player)` | what Kiln holds on the player now: `ActiveAffix(Affix, Stat, Value, Item)` |
| `Affixes.StatOf(entity, stat)` → `Task<double?>` | the value the game's attribute holds now, read by the game's own attribute read two or three steps later: what every modifier adds up to. For a resistance: the share of that damage still taken |
| `Affixes.Find(id)` / `FindPool(id)` | a definition of a loaded mod, or null |

Results follow [the contract](contract.md): a refusal by the rules is `AffixResult.Ok == false` with a `Reason` (nothing
changed); a none or stamp-less ref, a stale ref on a write, an unknown affix, a tier it does not have, a value that is 0
or not whole for a whole-number stat throw `ArgumentException`; a capability that is off throws
`KilnUnavailableException`. `Of`, `Remove` and `Clear` answer "none" for a stale ref.

Events (server, tick thread): `AffixesChangedEvent` (`Item`, `Before`, `Now`) for every successful roll, add, set and
removal; `AffixesAppliedEvent` (`Wearer`, `Before`, `Now`) when what Kiln holds on a player changed. Under them,
Kiln.Saves' `InstanceDataChangedEvent` fires as for any item data.

| Capability | Means |
|---|---|
| `blocks.affixes` | the rolls work: needs `saves.instance_data` (pending while a hosted world's data is not open) |
| `blocks.affixes.effects` | worn and held affixes change stats: also needs `abilities.programs`, `abilities.natives`, `items.instances`, and a server world whose Impact program registry has taken Kiln's four programs (pending until then) |

## Where the rolls are kept

An item's rolls are [item data](inventory.md#data-on-one-item-instancedata) of Kiln's own (owner `kiln-affixes`, key
`affixes`: `Affixes.DataMod`, `Affixes.DataKey`), as `{ "Rolls": [ { "Id", "Name", "Stat", "Tier", "Value" } ] }`. So
they stay with that one item through moves, saves, `kill -9` and restarts by the rules of
[services/saves.md](../services/saves.md), with those rules' limits: two identical swords with different rolls that were
swapped where Kiln did not watch are set aside, not guessed (`/instances` lists them; `/instances claim` puts a
record on an item). A mod that is not loaded leaves its affixes on their items; they do nothing until it is back.

## How they act

About twice a second on a dedicated server (every 10 steps; at once after an affix call) Kiln asks, for every player in
game: which items with affixes does the player carry, and which of them act right now: the item **in hand** and the
items in the **equipment slots** (armour, rings, ranged weapon, shield, arrows, spell, bait, hook shot, glider,
backpack; not the vanity slots). For each affix of an acting item Kiln holds one stat change on the player. It ends the
ones that are no longer wanted and starts the missing ones. That one rule is all there is:

| What happens | What follows |
|---|---|
| the item is taken in hand, or put on | its affixes act within half a second |
| it is put away, taken off, moved to a chest, given to another player, trashed, used in a recipe | they stop within half a second; for the other player they start once it is worn |
| the player dies | whatever is still worn or in hand after the respawn acts; what went to the tombstone does not. If the game ended a stat change with the death, Kiln finds it gone at the next pass and starts it again (once) |
| the player leaves, or crashes | Kiln ends what it held; nothing is saved on the character, so nothing can stay behind |
| the server restarts, the player joins again | the rolls come back with the item (item data); the pass applies them as for any worn item |
| the affix's value changes (`Set`, an upgrade) | the old change ends and the new one starts, never both |
| the affix's mod unloads | its changes end at the next pass |

**Stacking.** Two acting items with the same affix add up: +3 and +5 Strength are +8. An affix with `Stacks = false`
counts once, with the highest value among the acting items. Different affixes on the same stat always add up.

**What a stat change is.** One instance of a small Impact program on the game's generic impact entity, with the
attribute, the operation and the value as its config values: `applyAttributeValue` on the player in its code, the
matching `revertApplyAttributeValue` in its shutdown code, which the game runs when Kiln destroys the entity. It is the
pair the game's own statuses and equipment use, so the game's formulas, bars and replication follow as for a buff's
modifier, and vanilla clients need nothing. The entities are never saved.

## Which stats

Every stat of [`Stat`](blocks.md#statuses) works. On a lab server (26430253, a player-template entity as the wearer,
`mods/tests/affix-test`) each one was put on a sword in hand and read back through the game's attribute read before,
while, and after:

| Stats | Affix value | The game's attribute: before, with the sword in hand, after |
|---|---|---|
| `Strength`, `Constitution`, `Dexterity`, `Endurance`, `Intelligence`, `Spirit`, `MaxHealth`, `MaxMana`, `MaxStamina`, `HealthRegen`, `MaxOxygen`, `ShroudTime`, `PhysicalArmor`, `MagicalArmor`, `Block` | +7 | 0, 7, 0 (these attributes hold the added points; the game's formulas make the final number from them) |
| `ManaRegen`, `StaminaRegen` | +7 | 5, 12, 5 and 50, 57, 50 |
| `MoveSpeed`, `MeleeDamage`, `RangedDamage`, `MagicDamage`, `StaminaUseClimb`, `StaminaUseGlide`, `StaminaUseSprint` | +0.15 | 1, 1.15, 1 |
| `SprintSpeed` | +0.15 | 8, 8.15, 8 (an added speed, not a share of 8: see below) |
| `CritChance`, `CritDamage` | +0.15 | 0.05, 0.2, 0.05 and 1.5, 1.65, 1.5 |
| `Healing`, `Parry` | +0.15 | 0, 0.15, 0 |
| `ResistPhysical`, `ResistMagical`, `ResistCut`, `ResistPierce`, `ResistBlunt`, `ResistFire`, `ResistIce`, `ResistLightning`, `ResistShroud`, `ResistPoison` | +0.15 | 1, 0.85, 1 (the share of that damage still taken) |

That is the attribute. **What the player then feels is the game's own formula** from that attribute (how much damage a
point of Strength is, how fast 1.15 walks), and was not measured in a lab: no lab has a player. Two things to know:
`SprintSpeed` holds a speed (8), so +0.15 adds 0.15 to it and is hardly felt; and the `StaminaUse*` stats are costs, so a
positive value makes the action dearer and a negative one cheaper.

## What is not there yet

| Missing | What it would take |
|---|---|
| **Flat damage on a hit** ("+12 fire damage") | It is no attribute of the character: a hit's damage is the weapon's power times the hit's per-type factors (`DamageSetup`). Route: wrap the game's `applyDamage` (`Abilities.OnDamage`, synchronous, on the simulation's thread) and add a second typed hit, or raise the factor, when the attacker's held item carries such an affix. About 2 days with the lab proof (the hit's numbers through `ImpactDamageEvent`); the factor route needs the damage formula checked first (`docs/re/combat.md`). A game update that changes `applyDamage`'s arguments breaks it, as it would break Kiln.Combat. Today: `Stat.MeleeDamage`, `RangedDamage` and `MagicDamage` (a share of all damage of that kind) |
| **A share of a pool** ("+5% of max stamina") | `MaxStamina`, `MaxHealth` and `MaxMana` take points. A share needs the base value read at the moment it is applied and again when it changes; half a day, and a rule for what "base" means when two such affixes meet |
| **Effects that are not numbers** (a chance to burn on hit, a light, a sound) | Those are effects, not stat changes: an `AffixBlock.OnHit` would run an [effect block](blocks.md#effects) through the wielder's program, as a weapon block's `OnHit` does for an item type. The program exists per item type today; per item it needs the same wrap as flat damage. About 3 days |
| **Affixes in `blocks/*.json` and `Blocks.Define`** | The records are blocks in shape, but they are defined through `Affixes.Define` and `Affixes.Load`. Making them sections of a block file means entries in the block schema (`schema/kiln-blocks.schema.json`), the SDK's data reader (`Kiln.Sdk/DataClients.cs`), `kiln lint` and the block analyzer; a day, mostly in files other packages own |
| **Automatic rolls** (every dropped or crafted sword rolls) | A pool rolls when code says so. The game has no "item created" event; Kiln sees an item when it is read. Route: roll on first sight in a player's inventory (the pass reads it anyway), with a rule for items that should stay plain. Half a day, and a decision about which items count |
| **The game's own tooltip** | See below |

## What players see

The rolls travel as item data with `Sync = InstanceSync.Nearby` (16 m): the game of the player who holds the item gets
them, and so does every Kiln game within 16 m of a chest or another player that holds it
([how](../services/saves.md#on-players-games-dev27)). A mod's client side reads them:

```csharp
// kiln.mod.json "side": "both". In the player's game:
public override void OnLoad() { if (Client.IsClient) Affixes.SyncedItems(); }   // opens the channel they arrive on

[Subscribe]
private void OnSynced(InstanceDataSyncedEvent e)
{
    if (e.Mod != Affixes.DataMod || e.Key != Affixes.DataKey || e.Change != InstanceSyncChange.Set) return;
    foreach (var a in Affixes.Synced(e.Item.Entity) ?? []) Hud.Toast(a.Text);     // "+3 Strength (of the Bear)"
}
```
`Affixes.SyncedItems()` lists every item with affixes the game knows (`Item`, `ItemType`, `Holder`, `FlatSlot`, `Own`,
`Affixes`); `Affixes.Synced(item)` reads one. Both work on any thread, so a window's draw code may call them. The text
needs no definitions in the game: a roll carries its affix's name and stat.

**The game's own item tooltip cannot show them yet.** A mod cannot draw into the detail view of an item: the client's
hook points are mapped (the design in `.out/foundations/instance-data-ui.md` section 3: the detail view's bar at client
`0xc1401a`, the slot bar at `0xc05895`, both from static analysis only) but not built, and the layout below the bar,
where a line of text would go, is not mapped at all. What it would take: one day of reverse engineering of the detail
view's layout with one client session to confirm it, sigdb patterns for the client, a native mid hook in kiln.ui and a
`Ui` call that adds lines to the open detail view: about a week, and every game update can move the hook. Until then a
mod shows affixes in a window, HUD line or notice of its own; the stat changes themselves need no client code at all and
show in the game's own bars and numbers.

## Limits

- 8 affixes on one item; 16 tiers per affix; 256 entries per pool.
- The pass runs every 10 steps and at once after an affix call: a change of what is worn acts within half a second on a
  dedicated server, not in the same step.
- If Kiln's managed side stops while the server keeps running, the stat changes it held stay until the server
  restarts (they are never saved, so a restart always clears them).
- An NPC or a creature that holds an item gets nothing: the pass looks at players.

## What was checked

Offline (`managed/tests/Kiln.Blocks.Tests/AffixTests.cs`, 13 tests on Kiln.Saves' real item-data store and a fake
game): every refusal of a definition by field, the data file, rolls in range and repeatable, groups, item level, the
rules and the argument errors, the stored JSON and its way back after a restart, and the pass: carried against worn,
stacking, a changed roll, an item gone, a wearer gone, a stat change the game ended, an inventory that cannot be read.

Lab `dev27-affix` (server 26430253, a copy of a real world, no player; `mods/tests/affix-test`, two boots, 85 checks
on the first and 8 on the second, all passing on 2026-11-12): the capability rows; the 39 stats above by the attribute's value; a sword in the bag does nothing, in hand it acts, put away
it stops; a second item in the chest slot adds up (+3 and +5 = 8) and a non-stacking affix counts once (0.03 and 0.05 =
0.05); the worn item moved into a chest loses its share and keeps its rolls; 90 seconds on the change still holds and
the game ended none; the item destroyed in hand and the wearer destroyed leave nothing; 90 rolls of a pool of three in
range; after a restart the sword in the chest has its two rolls again, and moved into another stand-in's hand they act
for that one.

A lab cannot make a real player. **Not run with a player:** the equipment slots of a real character (the lab says which
inventory slot the chest slot is, through a test switch), death and respawn, a leave and a rejoin, the bars and the
walking speed on screen, two players. Guided checks (`mods/tests/affix-test/ClientChecks.cs`, area "Affixes"):

| Check | You do | The check asserts |
|---|---|---|
| `affixes.in-hand` | take the given sword in hand, put it away | three stat changes held while in hand, none after; the game's max health attribute +200 and back. You confirm: longer health and stamina bars, clearly faster, and back |
| `affixes.death-and-rejoin` | with that sword: die and return; put it into a chest and take it back; leave and join again | three stat changes with the sword in hand each time, never six. You confirm the bars after each |
