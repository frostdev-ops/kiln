# Building blocks

Say what you mean: a spell, a status, what happens when something is hit. Kiln chooses which game resources to start from, derives the ids, writes the
Impact programs and works out what players' games need. There are no GUIDs to write and no `base` to name.

```csharp
var chilled = Blocks.Define("chilled", new StatusBlock
{
    Name = "Chilled", Seconds = 8, TickEvery = 2,
    OnTick = [new EffectBlock { Damage = Damage.Ice(3), Flinch = Flinch.None }],
    Modifies = { [Stat.MoveSpeed] = -0.5 },
});

var burst = Blocks.Define("frost-burst", new EffectBlock { Damage = Damage.Ice(40), Knockback = Knockback.Small, Applies = chilled });

Blocks.Define("frostbolt", new SpellBlock
{
    Name = "Frostbolt",
    Cast = new CastBlock { ManaCost = 30 },
    Delivery = new ProjectileBlock { OnImpact = [new EffectBlock { Area = new AreaBlock { Radius = 3, Effects = [burst] } }] },
    Recipe = new RecipeBlock { Makes = 10, Needs = { [GameItems.Material_T1_Twigs] = 2 } },
});
```
The same as data, in `blocks/*.json` (the schema gives the editor completion and errors):
```jsonc
{ "format": 1,
  "statuses": { "chilled": { "name": "Chilled", "seconds": 8, "tickEvery": 2, "onTick": [ { "damage": { "ice": 3 }, "flinch": "none" } ], "modifies": { "move-speed": -0.5 } } },
  "effects": { "frost-burst": { "damage": { "ice": 40 }, "knockback": "small", "applies": "chilled" } },
  "spells": { "frostbolt": { "name": "Frostbolt", "cast": { "manaCost": 30 },
      "delivery": { "type": "projectile", "onImpact": [ { "area": { "radius": 3, "effects": [ "frost-burst" ] } } ] },
      "recipe": { "makes": 10, "needs": { "Material_T1_Twigs": 2 } } } } }
```
Start with `kiln new spell` (template `kiln-spell`), or read `mods/samples/FrostboltBlock`. Set `<KilnServices>Blocks</KilnServices>` in the project. Items, weapons, armor and
stations: [item-blocks.md](item-blocks.md).

A field that takes a block takes it inline, as what `Blocks.Define` returned, or by name. A name is a block of this mod, or `"other-mod:name"`.

## Effects
An effect is what happens. It has no resource of its own: it becomes statements in the program of whatever triggers it, so the same effect works on a spell, a weapon or a status.

| Part | What it does |
|---|---|
| `Chance` | 0..1: the whole effect happens this often |
| `On` | Who it is for: `EffectOn.Target` (what the trigger gives it), or `EffectOn.Caster` (who cast the spell, who wields the weapon) |
| `Area` | Everyone in `Radius` metres who is an enemy (or a friend, with `Affects`) gets `Effects`. Hits through walls, like the game's sphere |
| `Spawns` | Spawns a `GameTemplates` constant or a template's GUID there, as a child of what spawned it, at its level and faction |
| `Sound` | A sound there, for everyone near who has Kiln: a cue of this mod (needs `clients: mod`), `"other-mod:name"`, or a cue's GUID |
| `Damage`, `Knockback`, `Flinch` | A hit. `Damage.Ice(40) + Damage.Fire(10)` |
| `Heal`, `Restores` | `Restore.Mana(20) + Restore.Stamina(10)`. Less than 0 takes it away. A target without that attribute gets nothing: most creatures have no mana |
| `Removes` | Statuses taken off: blocks by name, or `GameBuffs` constants |
| `Applies` | A status put on: a block, or a `GameBuffs` constant. `new StatusApply { Status = chilled, Seconds = 4 }` to set the time |
| `Then` | More effects, after this one, at the same target or place |

The parts run in that order. Where there is no target, such as the place a projectile stopped, only an area, a spawn, a sound and what is `On` the caster can happen: the block says so when it compiles.

| Trigger | Target |
|---|---|
| `ProjectileBlock.OnImpact` | None: a place |
| `ProjectileBlock.OnHit` | What it struck |
| `SpellBlock.OnCast` | The caster, the moment the spell leaves the staff |
| `StatusBlock.OnApply`, `OnTick`, `OnExpire` | Who has the status |
| `WeaponBlock.OnHit`, `OnKill` | What was hit or killed |
| `ArmorBlock.OnHitTaken` | Who dealt the hit |

A hit an effect deals is skipped by these triggers, so an effect cannot trigger itself.

**Damage numbers.** On a spell, the first `Damage` is its main hit: `Damage.Ice(40)` is the number in the tooltip, and the staff's power adds to it in a cast, as the game does. `Flat = true` makes it exactly the number written. Everything else is exactly the number written. The target's armour and the attacker's stats apply after, as for every hit.

## Statuses
A buff or debuff in the game's buff bar. `Seconds`, `Kind`, `Look` (its icon and body effect), `Refreshes`, the three triggers above, `TickEvery`, and:

`Modifies` changes stats for as long as it runs, and takes them back after. `Modifies = { [Stat.MoveSpeed] = -0.5, [Stat.Strength] = 5, [Stat.ResistIce] = 0.25 }`.

| Stats | Value |
|---|---|
| `Strength`, `Constitution`, `Dexterity`, `Endurance`, `Intelligence`, `Spirit`, `MaxHealth`, `MaxMana`, `MaxStamina`, `HealthRegen`, `ManaRegen`, `StaminaRegen`, `MaxOxygen`, `ShroudTime`, `PhysicalArmor`, `MagicalArmor`, `Block` | Whole points |
| `MoveSpeed`, `SprintSpeed`, `MeleeDamage`, `RangedDamage`, `MagicDamage`, `CritChance`, `CritDamage`, `Healing`, `Parry`, `StaminaUseClimb`, `StaminaUseGlide`, `StaminaUseSprint` | Shares: 0.1 is 10% more, -0.5 is half |
| `ResistPhysical`, `ResistMagical`, `ResistCut`, `ResistPierce`, `ResistBlunt`, `ResistFire`, `ResistIce`, `ResistLightning`, `ResistShroud`, `ResistPoison` | Shares of that damage taken away |

JSON keys are kebab case: `"move-speed"`. `new Stat("my-key", guid, Whole: true)` names any other attribute; in JSON, a GUID key is taken as a share.

## Spells
`Cast` (`ManaCost`, `Animation`, `ReleaseAt`), `Delivery`, `OnCast`, `Level`, `MaxCharges`, `Recipe`. A cast takes the mana and one charge from the stack. The tooltip's rows follow from the spell: mana, charge time, the first status's time, the first area's radius.

`ProjectileBlock`: `Speed`, `Gravity`, `Range`, `Look` (hand, projectile and impact visuals), `OnImpact`, `OnHit`. It needs one of the two.

## Game things, by name
`GameBuffs` constants are ids, `GameTemplates` constants are names. Only the running game knows what they stand for, so a program naming one is finished when content is ready. A name that is nothing is a load error by block and field, and `kiln lint` says so first if a catalog is there.

## VFX
`Vfx` fields are there on effects, statuses, casts and projectiles, with `Slot`, `Offset` and `Attach`. Blocks cannot show a VFX yet: setting one is an error.

## What players' games need
The build works it out from the blocks, the lowest level that works. `KilnClients` overrides it.

| The mod's blocks contain | `clients` |
|---|---|
| Effects only | `none` |
| New items, spells, statuses | `kiln`: Kiln clients get the definitions from the server. Vanilla clients cannot join |
| A cue, a PNG icon or a model of the mod's own | `mod`: every player needs the mod. Kiln clients without it are refused |

A compiled block type says its level with `[BlockClients]`, and a field that can name the mod's own files with `[ClientsModWhen]`. A code mod gets the level from a generator at compile time. A mod with blocks only in `blocks/*.json` gets `kiln`, and `kiln lint` checks the rest.

## Ids and renames
Ids come from the mod id and the block's name: `UUIDv5(<mod>:<kind>:<name>)`. Never write a GUID, and keep the names, because saves hold the ids.

To rename a block, keep the oldest name in `Was`: `Blocks.Define("frostlance", new SpellBlock { Was = "frostbolt", ... })`. Nothing in a save notices.

A rename without `Was` makes a new item, and what the world holds of the old one stays as `[Missing: mod:item]`, so nothing is lost. Kiln logs a warning naming the old block, and `kiln blocks list` shows it.

## When something is wrong
| When | What |
|---|---|
| Compile | `required` members and typed fields: `Applies` takes a status, not an effect |
| Build | [KILN110](analyzers.md#kiln110) a bad or duplicate name, [KILN111](analyzers.md#kiln111) a name that is no block, [KILN112](analyzers.md#kiln112) `clients` too low |
| Editor and `kiln lint` | The schema, KL201 and KL202 for `clients`, KL314 for names, `E_UNCONNECTED` for hand-written Impact programs |
| Load | `BlockError` with the block and the field: `block spell bad, cast.manaCost: -1 is less than 0`. A wrong block makes nothing, and the file's other blocks still load |
| Content ready | What the game could not take, by name: `block status x, onApply[0].applies: the game has no status "0xdeadbeef"` |

## Seeing what a block became
```
kiln blocks list [--mod ID] [--server SERVER]     the mods with blocks, their errors and warnings
kiln blocks show frostbolt [--programs]           the items, recipes, templates, buffs and rules it became, its programs as listings
```
From a test: `Blocks.Lowered(name)`, `Blocks.Report()`, `Blocks.Warnings()`, and `Blocks.DumpPrograms(folder)` for `tools/impact/impact.py dis` and `lint`.
