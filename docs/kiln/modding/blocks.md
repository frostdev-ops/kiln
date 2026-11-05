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
| `Vfx` | A VFX there: see [VFX](#vfx) |
| `Sound` | A sound there, for everyone near who has Kiln: a cue of this mod (needs `clients: mod`), `"other-mod:name"`, or a cue's GUID |
| `Damage`, `Knockback`, `Flinch` | A hit. `Damage.Ice(40) + Damage.Fire(10)` |
| `Heal`, `Restores` | `Restore.Mana(20) + Restore.Stamina(10)`. Less than 0 takes it away. A target without that attribute gets nothing: most creatures have no mana |
| `Removes` | Statuses taken off: blocks by name, or `GameBuffs` constants |
| `Applies` | A status put on: a block, or a `GameBuffs` constant. `new StatusApply { Status = chilled, Seconds = 4 }` to set the time |
| `Then` | More effects, after this one, at the same target or place |

The parts run in that order. Where there is no target, such as the place a projectile stopped, only an area, a spawn, a VFX, a sound and what is `On` the caster can happen: the block says so when it compiles.

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
An effect written from nothing, with no compiler and no game data: `VfxBlock` in C#, the same shape in a `vfx` section of `blocks/*.json`.

```csharp
var burst = Blocks.Define("frost_burst", new VfxBlock
{
    Particles =
    [
        new ParticleBlock
        {
            Burst = 24, Lifetime = (0.4f, 0.9f), Speed = (2f, 5f), Spread = Shape.Cone(35), Gravity = -4f,
            Size = Curve.Linear(0.15f, 0f), Color = Gradient.Of("#ffffff", "#7fd4ff"), Alpha = Curve.Linear(1f, 0f),
            Texture = "vfx/shard.png", Blend = Blend.Additive,
        },
    ],
    Light = new LightBlock { Color = "#7fd4ff", Radius = 6f, Brightness = 4f, Lifetime = 0.6f, FadeOut = 0.5f },
});
```
```jsonc
{ "format": 1,
  "vfx": { "frost_burst": {
      "particles": [ { "burst": 24, "lifetime": [0.4, 0.9], "speed": [2, 5], "spread": { "cone": 35 }, "gravity": -4,
                       "size": { "linear": [0.15, 0] }, "color": ["#ffffff", "#7fd4ff"], "alpha": { "linear": [1, 0] },
                       "texture": "vfx/shard.png", "blend": "additive" } ],
      "light": { "color": "#7fd4ff", "radius": 6, "brightness": 4, "lifetime": 0.6, "fadeOut": 0.5 } } } }
```
Every field is in [the builder's guide](../services/vfx-cook.md#the-definition). PNGs go in the mod's `vfx/` folder.

**A block in a data file is there from the first start. One from code is there from the next**, because the game lists its VFX once, before mod code runs. Until then what names it shows nothing, `handle.Status` says `Pending`, and the log and a toast say so. The definition is kept in `kiln/data/vfx/pending/<mod>/` and built at the next start. That file stays until deleted, even if the mod stops defining the block.

Attach one with a `Vfx` field. It takes what `Define` returned or a block's name, `"other-mod:name"`, or `"game:<name>"` for one of the game's (`VfxRef.Game(...)` in C#). A game name is a template that carries the VFX, a sequence that plays it, or a GUID. Kiln picks the route:

| Field | What it becomes | What shows |
|---|---|---|
| `CastBlock.Vfx` | The cast's VFX event | At the caster's hand, in place of the motion's own VFX and at its moment |
| `ProjectileBlock.Vfx` | The projectile template's VFX | While it flies, in place of the look's |
| `EffectBlock.Vfx`, `OnHitBlock.Vfx` | A hidden template, spawned where the effect happens. It lives as long as the VFX takes, at least 1 s | Where the effect happens. An `OnImpact` effect with a VFX replaces the look's burst |
| `AreaBlock.Vfx` | The same | Where the area is, once. Its size is the VFX's own: it does not follow `Radius` |
| `StatusBlock.Vfx` | The buff's `whileApplied` entry | On whoever has it, in place of the look's |
| An `OnApply` effect's `Vfx` | The buff's `onCreate` entry, when it has no `Chance` and is on the target | When the status starts. Any other runs as an effect |
| `Vfx.PlayAt(burst, position)` from code | A play. On a server, a relay on `kiln:vfx` | Kiln clients near it. A joiner does not see it |

All but the last are data: replication handles range and joiners. A server has no VFX and needs none. It carries the references, and each client draws its own copy.

To say more, write the reference out: `new VfxRef("frost_burst") { Slot = VfxSlot.Chest, Offset = new Vector3(0, 0.5f, 0), Seconds = 3 }`, or `{ "name": "frost_burst", "slot": "chest", "offset": [0, 0.5, 0], "seconds": 3 }`.

| Field | Means |
|---|---|
| `Slot` | Where on the body. A cast defaults to the right hand, everything else to the root. On an effect it picks where the VFX starts |
| `Offset` | Metres from the slot, in its axes |
| `Attach` | Follow the slot while it plays, the default. What an effect spawns stays where the effect happened |
| `Seconds` | On an effect, how long it plays. A VFX that runs until stopped needs this on an effect, or it is refused. A game VFX defaults to its template's life, else 5 s |
| `Parameters` | Named parameters of a game VFX: `["radius"] = 3f`, `{ "radius": 3 }` |

**Parameters** work only on an effect's VFX, and only on one of the game's. Everywhere else the game takes a block laid out by the VFX itself, which a server doesn't have, so a cast, a projectile or a status with parameters is refused by name. A `VfxBlock` has none yet. An effect with parameters is a relayed play, not a hidden template: Kiln clients near it see it, a joiner doesn't. A number is a float, `VfxValue.Int(7)` (`{ "int": 7 }`) a whole number, a `Vector3` (`[x, y, z]`) three floats, `"#rrggbb"` or `"#rrggbbaa"` a colour. The game ignores a name or type its VFX doesn't have.

A look can't carry a VFX yet: `VisualBlock.Vfx` is an error. A game VFX on an effect plays with its own default parameters, not its template's.

**Limits.** The game has room for 473 new VFX across all mods and about 240 KB of mesh memory. Going over either aborts the game, so a mod that doesn't fit is refused as a whole, and each of its blocks says why when it loads. A server has no builder and counts VFX only. See [the builder's checks](../services/vfx-cook.md#the-limits-and-the-checks-before-them).

**If kiln.vfx is off or missing,** nothing crashes. A dedicated server carries references as always. A client with `kiln.vfx` drops an unknown VFX at its guard. A client without it keeps only the game's own VFX in the data it holds, and the rest show nothing.

## What players' games need
The build works it out from the blocks, the lowest level that works. `KilnClients` overrides it.

| The mod's blocks contain | `clients` |
|---|---|
| Effects only | `none` |
| New items, spells, statuses | `kiln`: Kiln clients get the definitions from the server. Vanilla clients cannot join |
| A cue, a PNG icon, a model or a VFX of the mod's own | `mod`: every player needs the mod. Kiln clients without it are refused |
| A game VFX by name, on any of these | No more than the block needs |

A compiled block type says its level with `[BlockClients]`, and a field that can name the mod's own files with `[ClientsModWhen]`. A code mod gets the level from a generator at compile time. The build also reads `blocks/*.json` for new VFX. For the rest a mod with blocks only in data files gets `kiln`, and `kiln lint` checks.

## Going below a block
A block doesn't cover every game field. `Tweak` sets the rest on what it made, and wins over what the block sets ([typed-fields.md](typed-fields.md)):
```csharp
Blocks.Define("frostbolt", new SpellBlock { /* ... */ Tweak = { { GameFields.Items.MaxStackSize, 5 } } });
```
```jsonc
"frostbolt": { /* ... */ "tweak": { "maxStackSize": 5 } }
```
Spells and item blocks set their item's fields, statuses their buff's, projectiles their template's. A field of another section is an error.

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
| Load | `BlockError` with the block and the field: `block spell bad, cast.manaCost: -1 is less than 0`. A wrong block makes nothing, and the file's other blocks still load. A VFX's fields are checked on a server too; a client's builder also checks what needs its PNGs |
| Content ready | What the game could not take, by name: `block status x, onApply[0].applies: the game has no status "0xdeadbeef"` |

## Seeing what a block became
```
kiln blocks list [--mod ID] [--server SERVER]     the mods with blocks, their errors and warnings
kiln blocks show frostbolt [--programs]           the items, recipes, templates, buffs and rules it became, its programs as listings
```
From a test: `Blocks.Lowered(name)`, `Blocks.Report()`, `Blocks.Warnings()`, and `Blocks.DumpPrograms(folder)` for `tools/impact/impact.py dis` and `lint`.
