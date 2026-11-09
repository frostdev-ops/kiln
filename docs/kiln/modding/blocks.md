# Building blocks

Say what you mean: a spell, a status, what happens when something is hit. Kiln chooses which game resources to start from, derives the ids, writes the
Impact programs and works out what players' games need. There are no GUIDs to write and no `base` to name.

```csharp
var chilled = Blocks.Define("chilled", new StatusBlock
{
    Name = "Chilled", Seconds = 8, TickEvery = 2, Look = StatusLook.Frost,
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
  "statuses": { "chilled": { "name": "Chilled", "seconds": 8, "tickEvery": 2, "look": "frost", "onTick": [ { "damage": { "ice": 3 }, "flinch": "none" } ], "modifies": { "move-speed": -0.5 } } },
  "effects": { "frost-burst": { "damage": { "ice": 40 }, "knockback": "small", "applies": "chilled" } },
  "spells": { "frostbolt": { "name": "Frostbolt", "cast": { "manaCost": 30 },
      "delivery": { "type": "projectile", "onImpact": [ { "area": { "radius": 3, "effects": [ "frost-burst" ] } } ] },
      "recipe": { "makes": 10, "needs": { "Material_T1_Twigs": 2 } } } } }
```
Start with `kiln new spell` (template `kiln-spell`), or read `mods/samples/FrostboltBlock`. Set `<KilnServices>Blocks</KilnServices>` in the project. Items, weapons, armor and
stations: [item-blocks.md](item-blocks.md). Props, forges and racks of your own: [world-blocks.md](world-blocks.md). Creatures, their bodies, animations,
AI and behaviour, and NPCs: [npc-blocks.md](npc-blocks.md). Quests and conversations: [quests.md](quests.md#quests-as-blocks).
Windows, HUD lines and keys: [ui-blocks.md](ui-blocks.md).

These pages describe the implemented API. A compile, lowered-resource readback or dedicated-server test does not
prove client drawing, animation, input or the player's interaction. Those checks remain on the feature's client checklist.

A field that takes a block takes it inline, as what `Blocks.Define` returned, or by name. A name is a block of this mod, or `"other-mod:name"`. Code and data files of one mod name each other's blocks freely: a data block that code names during `OnLoad` is compiled there and then, the rest after `OnLoad`, and the ids are the same either way. A chain that goes back and forth has to be defined in the order it is named.

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
| `Screen` | A full-screen effect for the target when it is a player, or for players near the place: [screen-effects.md](screen-effects.md) |
| `Damage`, `Knockback`, `Flinch` | A hit. `Damage.Ice(40) + Damage.Fire(10)` |
| `Heal`, `Restores` | `Restore.Mana(20) + Restore.Stamina(10)`. Less than 0 takes it away. A target without that attribute gets nothing: most creatures have no mana |
| `Removes` | Statuses taken off: blocks by name, or `GameBuffs` constants |
| `Applies` | A status put on: a block, or a `GameBuffs` constant. `new StatusApply { Status = chilled, Seconds = 4 }` to set the time |
| `Then` | More effects, after this one, at the same target or place |
| `Casts` | An ability the target casts, as from code: its costs and cooldown apply. `new EffectBlock { On = EffectOn.Caster, Casts = "flame-step" }` on a perk's hit casts it for the one who hit |

The parts run in that order. Where there is no target, such as the place a projectile stopped, only an area, a spawn, a VFX, a sound and what is `On` the caster can happen: the block says so when it compiles.

| Trigger | Target |
|---|---|
| `ProjectileBlock.OnImpact` | None: a place |
| `ProjectileBlock.OnHit` | What it struck |
| `SpellBlock.OnCast` | The caster, the moment the spell leaves the staff |
| `StatusBlock.OnApply`, `OnTick`, `OnExpire` | Who has the status |
| `WeaponBlock.OnHit`, `OnKill` | What was hit or killed |
| `ArmorBlock.OnHitTaken` | Who dealt the hit |
| `PerkBlock.OnHit`, `OnKill`, `OnHitTaken`; the same on a `TalentBlock` | As on a weapon |
| A delivery's `Effects` | Each one it reaches; `SelfBlock`: the caster |
| `AbilityBlock.OnCast`, `TalentBlock.OnStart`, `ItemBlock.OnEat`, `TriggerBlock.Do`, `OnEnd` | The caster, who has the talent, who ate, the player |

A hit an effect deals is skipped by these triggers, so an effect cannot trigger itself.

**Damage numbers.** On a spell, the first `Damage` is its main hit: `Damage.Ice(40)` is the number in the tooltip, and the staff's power adds to it in a cast, as the game does. `Flat = true` makes it exactly the number written. Everything else is exactly the number written. The target's armour and the attacker's stats apply after, as for every hit.

## Statuses
A buff or debuff in the game's buff bar. `Seconds`, `Kind`, `Look` (its icon and body effect: `StatusLook.Frost`, the frost slow's snail; `Poison`; `Fire`, the game's Burning with flames on the chest; without one a debuff has a timer's icon and a buff a blessing's, and neither a body effect), `Refreshes`, the three triggers above, `TickEvery`, `Screen` (a [screen effect](screen-effects.md) on its player while it lasts), and:

`Description` is yours or absent: a status never shows the text of the game status its look comes from.

A debuff also shows on the health bar over a creature that has it, in white like the game's own debuffs for enemies. The bar draws only a status with a colour, and the frost slow and the timer have none. `Tweak` sets another `color`: a number with alpha, blue, green and red from the high byte down, as the game's orange Burning, `0x766b98ff`. A buff keeps its look's colour, so like the game's buffs it doesn't show there.

`While` puts it on a player for as long as they are in a state, and takes it off after: `While = PlayerStateId.Gliding`, in JSON `"while": "Gliding"`. It needs no `Seconds`. Any of the game's states works: [player-states.md](player-states.md). Such a status shows no time in the buff bar, as the game's own untimed buffs do; one with `Seconds` shows what is left.

`Icon` is a game status's icon (a `GameBuffs` constant), or a PNG in the mod's folder (`"icons/chill.png"`, square, 256x256 as the game's; at most 1024x1024; then `clients: mod`). A PNG is served to each player's game under a GUID that follows from the block, and the status names that GUID. Until a player's game has the picture ready (a few frames after it loads the mod) its buff bar shows the look's icon, never a missing one.

`Modifies` changes stats for as long as it runs, and takes them back after. `Modifies = { [Stat.MoveSpeed] = -0.5, [Stat.Strength] = 5, [Stat.ResistIce] = 0.25 }`.

| Stats | Value |
|---|---|
| `Strength`, `Constitution`, `Dexterity`, `Endurance`, `Intelligence`, `Spirit`, `MaxHealth`, `MaxMana`, `MaxStamina`, `HealthRegen`, `ManaRegen`, `StaminaRegen`, `MaxOxygen`, `ShroudTime`, `PhysicalArmor`, `MagicalArmor`, `Block` | Whole points |
| `MoveSpeed`, `SprintSpeed`, `MeleeDamage`, `RangedDamage`, `MagicDamage`, `CritChance`, `CritDamage`, `Healing`, `Parry`, `StaminaUseClimb`, `StaminaUseGlide`, `StaminaUseSprint` | Shares: 0.1 is 10% more, -0.5 is half |
| `ResistPhysical`, `ResistMagical`, `ResistCut`, `ResistPierce`, `ResistBlunt`, `ResistFire`, `ResistIce`, `ResistLightning`, `ResistShroud`, `ResistPoison` | Shares of that damage taken away |

JSON keys are kebab case: `"move-speed"`. `new Stat("my-key", guid, Whole: true)` names any other attribute; in JSON, a GUID key is taken as a share.

## Spells
`Cast` (`ManaCost`, `StaminaCost`, `Charges`, `Animation`, `ReleaseAt`), `Delivery`, `OnCast`, `Level`, `MaxCharges`, `Recipe`, `Description` (yours or none: never the text of the game spell it starts from). A cast takes its costs and `Charges` from the stack: 1 by default, 0 for a spell that is never used up. The tooltip's rows follow from the spell: mana, stamina, charge time, the first status's time, the first area's radius.

## Deliveries
How a spell, a wand's attack or an ability gets there. A spell's staff and a wand place it at the hand, an ability at the caster, facing where they aim.

| Delivery | Reaches | JSON `type` |
|---|---|---|
| `ProjectileBlock` | `Speed`, `Gravity`, `Range`, `Look`, `OnImpact` where it stops, `OnHit` on what it strikes, `Homing`, `Pierce` (it flies through that many creatures, 0..8, `OnHit` on each once, and stops at the next or at anything that is not a creature) | `projectile` |
| `SelfBlock` | The caster | `self` |
| `AroundBlock` | Everyone within `Radius` of the caster | `around` |
| `ConeBlock` | Everyone within `Range`, inside `Angle` degrees of the aim | `cone` |
| `BeamBlock` | Everyone along a line, `Range` long and `Width` wide. With `Seconds` it keeps hitting every `TickEvery`, turning with the caster | `beam` |
| `ZoneBlock` | Everyone within `Radius` of a place `Ahead` of the caster, every `TickEvery`, for `Seconds` | `zone` |
| `ChainBlock` | The first within `Range` and `Angle`, then `Jumps` more, each within `JumpRange` of the last, never one twice | `chain` |
| `SummonBlock` | `Count` of a `GameTemplates` template in front of the caster, as its children; with `Seconds` they are taken away | `summon` |

All but the projectile find who they reach by a query, as an area does: through walls, with or without a player near. `Affects` picks enemies or friends. `Vfx` shows where it happens. `Homing` (`TurnSpeed`, `SeekRange`, `Delay`) is the game's own homing projectile: it turns toward the enemy an ability aimed at, or what it finds along its aim.

## Abilities
Cast on a key, a chat command, an item, or from code with `Abilities.Cast(caster, "<mod>:<name>")`:
```csharp
Blocks.Define("flame-step", new AbilityBlock
{
    Name = "Flame Step", Key = "G", Command = "step",
    Cast = new CastBlock { ManaCost = 20, StaminaCost = 10 }, Cooldown = 8,
    Delivery = new ConeBlock { Range = 8, Angle = 60, Effects = [new EffectBlock { Damage = Damage.Fire(30) }] },
});
```
An ability is a reusable action with costs and a cooldown. A spell is ammunition used by a staff; a perk follows an
item's enhancement unlock; a talent follows the player's learned level. Use an item's `OnHit` when no enhancement
unlock should be needed. All four can share the same effect and status blocks.

The ability above as data:
```jsonc
{ "format": 1, "abilities": { "flame-step": {
  "name": "Flame Step", "key": "G", "command": "step",
  "cast": { "manaCost": 20, "staminaCost": 10 }, "cooldown": 8,
  "delivery": { "type": "cone", "range": 8, "angle": 60, "effects": [{ "damage": { "fire": 30 } }] }
} } }
```

`Aim` is `Forward` or `NearestEnemy` (none in `Range`: no cast). `SpecialCost` costs the special-ability bar. Without `Cast.Animation` nothing plays and the delivery leaves at once, or after `ReleaseAt`; with one, the caster plays it and its event sends the delivery. `Item` makes an item that casts it when used: `Cast.Charges` is how many a cast takes, 0 keeps it, and a refused cast gives back what it took. A `Key` needs `clients: mod`; a chat command works on any client.

## Perks
An enhancement in an item's `Perks`. The game's rule applies: it runs while the item is worn or in hand, once a player has unlocked that enhancement at an upgrade. A weapon's `OnHit` is the same with no unlock.
```csharp
var edge = Blocks.Define("frost-edge-perk", new PerkBlock { Name = "Frost Edge", Value = "5%", OnHit = [new EffectBlock { Chance = 0.05, Applies = chilled }] });
Blocks.Define("glacier", new WeaponBlock { Name = "Glacier", Class = WeaponClass.Sword, Perks = [edge] });
```
`Modifies`, `OnHit`, `OnKill`, `OnHitTaken`, `Description` (yours, what several `Modifies` entries say, or none: never the text of the game perk it starts from). `Value` is the number in front of its name in the tooltip (`"5%"`, `"12"`, `"2.5"`, `"8s"`); left out, it is what one `Modifies` entry or one on-hit effect's `Chance` says. `Icon` takes a game perk's icon, or a PNG in the mod's folder by the status icon's route.

## Talents
A `TalentBlock` is a node: in a tree of its own, or in the game's tree with `Game`. `Cost` per level, `MaxLevel`, `Requires`, `Modifies` (per level), `OnHit`, `OnKill`, `OnHitTaken`, `Keeps` a status, `OnStart`, and `Unlocks` an ability only those with it can cast.
```csharp
Blocks.Define("ember-arts", new TalentTreeBlock
{
    Name = "Ember Arts", Key = "K",
    Nodes =
    {
        ["kindle"] = new TalentBlock { Name = "Kindle", MaxLevel = 3, Modifies = { [Stat.Intelligence] = 1 } },
        ["step"] = new TalentBlock { Name = "Flame Step", Requires = ["kindle"], Cost = 2, Unlocks = "flame-step" },
    },
});
Blocks.Define("ember-ward", new TalentBlock { Name = "Ember Ward", MaxLevel = 3, Modifies = { [Stat.Strength] = 1 }, Game = new GameTreePlace { From = ["0x8efcc472"], Offset = [170, -40] } });
```
A tree goes on Kiln's own screen ([skills.md](../services/skills.md), part B): `Points`, `Menu`, `Key`, `Respec`; nodes without `Column` and `Row` are laid out from what leads where. A node's tooltip is its name, level and cost, and its `Description`, or what its `Modifies` say. The node names are what saves keep. A talent with `Game` is a node of the game's own tree (part A): its screen draws it and its skill points pay for it. Past the game's room it is a shadow node. A dedicated server reads its tree before mods load, so a node defined in code or a data file is in from the next start. Both need `clients: mod`. Names in `Requires`, `Unlocks` and `Keeps` are block references, so code and JSON can
share them. Keep the tree, node and ability names stable: saves use them. `Skills.Unlock`, `Lock` and `Respec`
exercise the server rules; a client still has to confirm the screen, key binding and learned talent presentation.

## What players do
Any of the game's player states and actions ([player-states.md](player-states.md)) can drive effects:
```csharp
Blocks.Define("rest", new TriggerBlock { On = PlayerStateId.Sitting, Do = [new EffectBlock { Restores = Restore.Mana(5) }], Cooldown = 10 });
Blocks.Define("no-mushrooms-in-bed", new TriggerBlock { On = PlayerStateRef.Eating, Item = GameItems.Food_T1_raw_mushroom_Mushroom_brown, When = [PlayerStateId.Sleeping], Stop = true });
```
`On` is a state entered, an action started, `Eating` (a consumable started) or `Eaten` (it has really gone). In JSON a name: Kiln's (`"Gliding"`) or the game's (`"HangGliding"`). `Item`, `When`, `Unless`, `If` (code), `Cooldown`, `Do`, `OnEnd` when a state is left, and `Stop` to stop an action or a meal. A consumable says the same of itself: `OnEat` once it has gone, `CanEat` (`When`, `Unless`, `OnRefused`) before. Refused, the item stays and gives nothing; one the game uses up in its first moment (`Consumable_Scroll_Fog_Meteor_Shower`) cannot be refused, and a rule on it is an error. Triggers run on the server world. Nothing is watched until a mod has one.

## Bodies, loot and recipes
`BodyBlock` is the reusable body of a creature: a game premade such as `Bodies.Vukah`, or a body with its own model,
skeleton, slots and size. A body or animation that names files belongs in `blocks/*.json` so the cook can see it;
C# can name that block. See [npc-blocks.md](npc-blocks.md#bodies-and-animations-with-files-of-their-own).

`LootBlock` is an item's place in the game's loot: `Like`, `Tables` (each a `DropPlace`) and `Rolled`. A
`LootTableBlock` is a table of your own, with `Entries`, `Rolls` and `Pick`. A creature's `Drops` or a container's
`DefaultLoot` can name it, and its handle can `Roll`, `Give` or `Drop` it:
```csharp
var scraps = Blocks.Define("scrap-pile", new LootTableBlock
{
    Entries = [new LootEntry { Item = GameItems.Material_T1_Metal_Scraps, CountMin = 2, CountMax = 5 }],
});
Blocks.Define("scrapper", new CreatureBlock { Body = Bodies.Vukah, Drops = scraps });
```
Creature drops are additional to the game's own drops. The world's Loot Amount setting does not scale this table.
Loose pickups from `Drop` are not saved. A `RecipeBlock` can be defined on its own in `recipes`, as well as on an
item or station. See [item-blocks.md](item-blocks.md) for loot tables, recipe inputs and outputs.

## UI blocks
`WindowBlock`, `HudLineBlock` and `KeyBindingBlock` also have JSON forms: `windows`, `hudLines` and `keyBindings`.
They bind to this mod's config and the supported read-only `game.*` values; buttons and keys use `UiAction`.
```csharp
Blocks.Define("clock", new HudLineBlock { Text = "{game.clock}  {game.fps:0} fps", Anchor = HudAnchor.TopRight });
```
Every player needs the mod (`clients: mod`). A dedicated server validates and keeps these blocks but draws and
binds nothing. Host tests and server registration do not verify widget layout, gamepad/mouse input or visible HUD
text. See [ui-blocks.md](ui-blocks.md) for actions, conditions, config writes and the client checklist.

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

`VisualBlock.Vfx` attaches a VFX to an item's own visual template or a placed prop; it takes the same `VfxRef`.
A game VFX on an effect plays with its own default parameters unless the reference supplies `Parameters`.

**Limits.** The game has room for 473 new VFX across all mods and about 240 KB of mesh memory. Going over either aborts the game, so a mod that doesn't fit is refused as a whole, and each of its blocks says why when it loads. A server has no builder and counts VFX only. See [the builder's checks](../services/vfx-cook.md#the-limits-and-the-checks-before-them).

**If kiln.vfx is off or missing,** nothing crashes. A dedicated server carries references as always. A client with `kiln.vfx` drops an unknown VFX at its guard. A client without it keeps only the game's own VFX in the data it holds, and the rest show nothing.

## What players' games need
The build works it out from the blocks, the lowest level that works. `KilnClients` overrides it.

| The mod's blocks contain | `clients` |
|---|---|
| Effects only, triggers | `none` |
| New items, spells, statuses, perks, abilities | `kiln`: Kiln clients get the definitions from the server. Vanilla clients cannot join |
| A cue, a PNG icon, a model or a VFX of the mod's own, a key, talents | `mod`: every player needs the mod. Kiln clients without it are refused |
| A game VFX by name, on any of these | No more than the block needs |

An ability that only acts on its caster works with `none` too: say so with `KilnClients`. A compiled block type says its level with `[BlockClients]`, and a field that can name the mod's own files with `[ClientsModWhen]`. A code mod gets the level from a generator at compile time. The build also reads the mod's data files (`blocks/*.json`, and the `skills`, `quests`, `content` and `assets` folders) and takes the higher of the two. In a data file, talents, a talent tree, an NPC, a building material, a blueprint, a new VFX or material, an ability's key, or a field that names the mod's own file (a cue, a PNG icon, a model, an NPC's body or clips) asks `mod`; a new item, spell, status or perk, an ability that places something, or a game VFX by name asks `kiln`. Data files that ask no more than `none` still get `kiln` in a mod with `Blocks`: say `none` with `KilnClients`. `kiln lint` checks what a manifest says.

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
