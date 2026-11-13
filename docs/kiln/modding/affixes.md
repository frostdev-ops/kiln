# Affixes: rolled modifiers on one item

"+5 Strength", "+10% Move Speed", "+17% Resist Fire": a bonus that belongs to one sword, not to every sword of its kind.
A mod says which affixes there are and which items roll them. Kiln keeps each item's rolls with that item, applies them
to whoever wears or holds it, and sends them to the games of players near it. New in 0.2.0-dev.27; in `Kiln.Blocks`
(`<KilnServices>Blocks</KilnServices>`, or a reference to `Kiln.Blocks`). Sample: `mods/samples/AffixSword`.

Affix kinds and pools are [blocks](blocks.md): `Blocks.Define` in C#, or the `affixes` and `affixPools` sections of a
`blocks/*.json` file. Both are the same objects with the same ids.

```csharp
// one kind of modifier: its stat, its tiers and the range each tier rolls in
Blocks.Define("bear", new AffixBlock
{
    Name = "of the Bear", Stat = Stat.Strength,
    Tiers = [new AffixTier { Min = 2, Max = 4 }, new AffixTier { Min = 5, Max = 8, Weight = 0.5, MinItemLevel = 20 }],
});
Blocks.Define("fleet", new AffixBlock { Name = "Fleet", Stat = Stat.MoveSpeed, Stacks = false,
    Tiers = [new AffixTier { Min = 0.05, Max = 0.15, Step = 0.05 }] });

// which items roll which affixes, and how many
AffixPoolHandle blades = Blocks.Define("blades", new AffixPoolBlock
{
    Items = [GameItems.Weapon_T1_1H_SwordEpic_01],
    Affixes = [new AffixEntry { Affix = "bear" }, new AffixEntry { Affix = "fleet", Weight = 2 }],
});

AffixResult rolled = blades.Roll(sword);              // sword: an ItemInstanceRef (Items.Held, a give result, ...)
if (rolled.Ok) Log.Info(rolled.Affixes[0].Text);      // "+3 Strength (of the Bear)"
```
The same as data, in any file of the mod's `blocks/` folder (camel case, the same fields; the schema gives completion
and errors, `kiln lint` checks it):
```jsonc
// blocks/affixes.json
{ "format": 1,
  "affixes": { "bear":  { "name": "of the Bear", "stat": "strength", "tiers": [ { "min": 2, "max": 4 }, { "min": 5, "max": 8, "weight": 0.5, "minItemLevel": 20 } ] },
               "fleet": { "name": "Fleet", "stat": "move-speed", "stacks": false, "tiers": [ { "min": 0.05, "max": 0.15, "step": 0.05 } ] } },
  "affixPools": { "blades": { "items": [ "Weapon_T1_1H_SwordEpic_01" ], "affixes": [ { "affix": "bear" }, { "affix": "fleet", "weight": 2 } ] } } }
```
Code rolls a pool of a data file by name: `Affixes.FindPool("blades")?.Roll(sword)` (or `Blocks.Find("blades")`, which
gives the same `AffixPoolHandle`).

**`Affixes.Define`, `Affixes.DefinePool` and `Affixes.Load` are the same thing.** `Affixes.Define("bear", block)` is
`Blocks.Define("bear", block)`: one registration, the same handle, the same id. `Affixes.Load(json)` defines the affixes
and pools of a file in its own format (`"affixes"` and `"pools"`) from wherever the mod keeps it; a file in `blocks/`
needs no call.

## The shapes

| Block | Fields |
|---|---|
| `AffixBlock` | `Name` (what players read; kept with every roll), `Stat` (a [`Stat`](blocks.md#statuses) constant, or `new Stat("my-key", guid, Whole: true)` for another attribute of the game), `Tiers` (1 to 16, the weakest first), `Stacks` (default true), `Group` |
| `AffixTier` | `Min`, `Max`: whole points for a whole-number stat (`Strength`: 3), a share for the others (`MoveSpeed`: 0.05 is 5 %). Never 0, one sign (negative ranges make a curse). `Step`: the grid a value lies on (default 1, or 0.01 for a share). `Weight`: how likely this tier is, among the tiers the item's level allows. `MinItemLevel` (default 0; at least one tier with a weight above 0 must allow any level) |
| `AffixPoolBlock` | `Items`: the item types that roll from it (`GameItems` constants, or items of the mod by block name); left out: any item with an item entity of its own (gear, tools). `Affixes`: entries of `Affix` (a name of this mod, or `"other-mod:name"`) and `Weight`. `Min`, `Max`: how many different affixes one roll gives (0 to 8, default 1 and 1). A pool whose entries can never give `Min` (entries with a weight above 0, one per group) is refused with a `BlockError` on `min` |

A roll picks `Min` to `Max` different affixes by weight, never two of one `Group`, then for each a tier by weight among
those whose `MinItemLevel` the item's level reaches, then a value on the tier's grid. A seeded `Random` makes a roll
repeat. A mistake in a block is a `BlockError` that names the block and the field. When fewer than `Min` can roll on
an item now (an affix's mod is not loaded, an affix has no tier for the item's level, or affixes defined again share a
group), the roll is refused with a reason that says which, and the item keeps what it had.

## As blocks

| What | How |
|---|---|
| Routes | `Blocks.Define(name, AffixBlock)` and `Blocks.Define(name, AffixPoolBlock)` give an `AffixHandle` and an `AffixPoolHandle`, as `Affixes.Define` and `DefinePool` do; `Blocks.Define<AffixBlock>` gives the same object. A data file's `affixes` and `affixPools` sections make the same handles. `Blocks.Find`, `Affixes.Find` and `FindPool` return the one object, whichever route made it |
| Ids | `"mod:name"` (`AffixHandle.Id`): the string a rolled item keeps (`RolledAffix.Affix`), the same in every route and the same as before affixes were blocks. Nothing of the game is made, so there is no GUID and no `Was`: keep the names, or rolled items wait for an affix that is not there |
| Names | lower-case letters, digits, `-` and `_`, at most 64. A name is one block of the mod: an affix or a pool takes no name another kind of block of the mod has, and the other way round (a `BlockError`). The same kind again replaces the earlier one, by any route, as every block does: a data file's, compiled after `OnLoad`, replaces the code's of the same name. `kiln lint` flags a name defined twice in the data files (KL305), the analyzer two `Blocks.Define` calls with one name (KILN110) |
| References | a pool's entry names an affix of this mod, defined in code or in any data file of the mod, in either order (a data file's affix that code names is compiled there and then, as for every block), or `"other-mod:name"` of a mod loaded before |
| When | code's in `OnLoad`; a data file's when code names it, else a few steps after the mod loaded, with the mod's other data files. Not baked: an affix is Kiln's own, not the game's, so there is nothing to have ready at the game's start |
| Mistakes | a `BlockError` that names the block and the field (`block affix bear, tiers[0]: ...`). In a data file the same text is an error of the mod with the file's name (kiln.log, `kiln blocks list`), and the file's other blocks still load |
| Players' games | affixes and pools ask nothing of them: `clients: none` is enough (the rolls reach Kiln games as item data) |
| Unload, reload | the mod's affixes and pools go with it, and come back with its next `OnLoad` and data files; rolls stay on their items |

## One item's affixes (server, tick thread)

| Call | Does |
|---|---|
| `Affixes.Of(item)` | the item's affixes: `RolledAffix(Affix, Name, Stat, Tier, Value)`, with `Text` ("+5 Strength (of the Bear)") and `ValueText` |
| `Affixes.Roll(item, pool, random, replace)` / `pool.Roll(item)` | rolls the pool onto the item. Not `Ok` when the pool is not for this item type, the item already has affixes and `replace` is false, or fewer than the pool's `Min` can roll on it now |
| `Affixes.Add(item, affix, tier, value)` | adds one; the value is rolled in the tier's range when left out. Not `Ok` when the item has that affix, one of its group, or 8 already |
| `Affixes.Set(item, list)` | gives the item exactly these (for commands and tools); the affixes need not be defined now |
| `Affixes.Remove(item, affix)` / `Clear(item)` | takes one, or all, off: true when something went |
| `Affixes.Active(player)` | what Kiln holds on the player now: `ActiveAffix(Affix, Stat, Value, Item)` |
| `Affixes.StatOf(entity, stat)` → `Task<double?>` | the value the game's attribute holds now, read by the game's own attribute read two or three steps later: what every modifier adds up to. For a resistance: the share of that damage still taken |
| `Affixes.Find(id)` / `FindPool(id)` | a definition of a loaded mod, whichever route made it (the same handle as `Blocks.Find`), or null |

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
| `blocks.affixes.effects` | worn and held affixes change stats: also needs `abilities.programs`, `abilities.natives`, `items.instances`, `spawn.destroy` (a stat change ends when its entity is destroyed: without it none starts), and a server world whose Impact program registry has taken Kiln's four programs (pending until then) |

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
| the player leaves, or crashes | Kiln ends what it held; nothing is saved on the character, so nothing can stay behind. A wearer is a session (the player's session stamp, not the bare entity id): if another player gets the same entity id before Kiln's next pass, the newcomer gets changes of its own, and the one who left keeps none of theirs on the newcomer (a change of the one who left is not ended while the id is the newcomer's, as ending it would take its value from the newcomer; it ends once the id is free) |
| the server restarts, the player joins again | the rolls come back with the item (item data); the pass applies them as for any worn item |
| the affix's value changes (`Set`, an upgrade) | the old change ends and the new one starts, never both |
| the affix is defined again with another stat | rolls made before keep the stat they name (`RolledAffix.Stat`, which is what players' games show: "+3 Strength" stays Strength); new rolls take the new stat. A roll whose stat Kiln cannot name any more (a mod's own attribute the affix no longer uses) does nothing |
| the affix's mod unloads | its changes end at the next pass |
| a stat change cannot be ended (kiln.spawn refuses to destroy its entity) | it is kept and tried again after 2, 4, 8 ... passes (at most 64 apart), also after the player left. While a player has one, that player gets no new stat change at all (one wanted again exactly as it is is taken back), said once in kiln.log; while 64 are kept on the server, nobody gets a new one (one error line). So a bonus is never held twice and the cleanup stays bounded. `Affixes.Active` lists such a change while it acts |
| the game ends a stat change's entity, or gives its id to another entity | Kiln checks each change by its entity's spawn stamp and template: one that is gone, or whose id is something else's now, counts as ended by the game (started again if still wanted). Kiln never destroys an entity that is not the change it started |

**Stacking.** Two acting items with the same affix add up: +3 and +5 Strength are +8. An affix with `Stacks = false`
counts once, with the highest value among the acting items (once per stat, when rolls made before a redefinition name
another one). Different affixes on the same stat always add up.

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
  restarts (they are never saved, so a restart always clears them). The same holds for a change kiln.spawn keeps
  refusing to end: it is retried (less often each time), and a restart clears it; meanwhile its player gets no new
  bonus, and at 64 such changes on the server nobody does.
- A stat change whose entity has no spawn stamp is ended at once and never runs (Kiln could not tell it apart later);
  on 26430253 every one has one (they are kiln.spawn entities with a SpawnTime).
- An NPC or a creature that holds an item gets nothing: the pass looks at players.

## What was checked

Offline (`managed/tests/Kiln.Blocks.Tests/AffixTests.cs`, 22 tests on Kiln.Saves' real item-data store and a fake
game): every refusal of a definition by field, the data file, rolls in range and repeatable, groups, item level, the
rules and the argument errors, the stored JSON and its way back after a restart, and the pass: carried against worn,
stacking, a changed roll, an item gone, a wearer gone, a stat change the game ended, an inventory that cannot be read;
a stat change that cannot be ended (kept, retried, never doubled on a re-equip or a changed roll, retried after the
wearer left), nothing started without `spawn.destroy`, a roll that keeps its stat after its affix is defined again, a
pool that can never give its minimum and a roll that cannot reach it now; a wearer with a stuck change getting no new
one whatever it puts on, retries that back off (8 tries in 200 passes), 70 stuck changes stopping new ones for
everyone, a player who leaves and one who gets the same entity id before the next pass (neither's change reaches the
other), and a change whose id the game gave to an unrelated entity (never destroyed).

As blocks (`AffixBlockTests.cs`, 7 more tests on the same store and fake game; since then all 22 above define through
the block route): the id is `"mod:name"` in every route, as `Affixes.Define` gave before, and an item whose rolls were
stored before (the same bytes) acts for its affix defined by a data file, by `Blocks.Define` and by `Affixes.Define`, with
one stat change throughout; one object whichever route made it; code and data files naming each other's affixes in
either order, and another mod's; the same name twice replaces as in one route, another kind of block by the same name is
refused both ways; wrong blocks in code (`BlockError` by block and field) and in a file (an error naming file, block and
field, the file's other blocks loading, as a wrong loot table beside them costs); 100 seeded rolls of a data file's pool
equal those of the same pool from `Affixes.DefinePool`, and their bonuses act and end the same.

Lab `dev27-affix` (server 26430253, a copy of a real world, no player; `mods/tests/affix-test`, two boots, 89 checks
on the first and 9 on the second, all passing on 2026-11-12): the capability rows (`spawn.destroy` among them); a
pool that asks 3 of affixes that give at most 2 refused where it is defined; a roll that cannot reach its pool's
minimum refused with its reason, the item keeping its affixes; bear defined again as max health while a sword showing
"+2 Strength (of the Bear)" was in hand: strength stayed 0 -> 2, max health 0 -> 0; the 39 stats above by the attribute's value; a sword in the bag does nothing, in hand it acts, put away
it stops; a second item in the chest slot adds up (+3 and +5 = 8) and a non-stacking affix counts once (0.03 and 0.05 =
0.05); the worn item moved into a chest loses its share and keeps its rolls; 90 seconds on the change still holds and
the game ended none; the item destroyed in hand and the wearer destroyed leave nothing; 90 rolls of a pool of three in
range; after a restart the sword in the chest has its two rolls again, and moved into another stand-in's hand they act
for that one.

Lab `dev27-affixblocks` (the same server and world, 2026-11-13, after affixes became blocks, so every `Affixes.Define`
above went through the block route): 100 checks on the first boot and 10 on the second, all passing. The new ones: an
affix and a pool in the test mod's `blocks/affixes.json` defined with no call, with the ids `"mod:name"`, the same objects
from `Blocks.Find` and `Affixes.Find`, a pool from `Blocks.Define` naming the file's affix, a roll of the file's pool onto a
sword ("+4 Strength (of Might)") acting in hand (strength 0 -> 4) and ending when put away (4 -> 0), and after the restart
the file defining both again.

A lab cannot make a real player. **Not run with a player:** the equipment slots of a real character (the lab says which
inventory slot the chest slot is, through a test switch), death and respawn, a leave and a rejoin, the bars and the
walking speed on screen, two players. Guided checks (`mods/tests/affix-test/ClientChecks.cs`, area "Affixes"):

| Check | You do | The check asserts |
|---|---|---|
| `affixes.in-hand` | take the given sword in hand, put it away | three stat changes held while in hand, none after; the game's max health attribute +200 and back. You confirm: longer health and stamina bars, clearly faster, and back |
| `affixes.death-and-rejoin` | with that sword: die and return; put it into a chest and take it back; leave and join again | three stat changes with the sword in hand each time, never six. You confirm the bars after each |
