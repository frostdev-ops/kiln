# Kiln building blocks (blocks/*.json), format 1

<!-- Generated from schema/kiln-blocks.schema.json by kiln_manage::lint::docs (cargo test -p kiln-manage --test lint with KILN_LINT_WRITE=1). Do not edit. -->

A mod's blocks: spells, weapons, wands, armor, items, stations, statuses, effects and their parts, the same shape as the C# records (Kiln.Blocks). Read by managed/services/Kiln.Blocks/BlockJson.cs (System.Text.Json: comments and trailing commas allowed, unknown keys refused). A field that takes a block takes it inline, or by name: a block of this mod, or "other-mod:name".

- Files: `**/blocks/*.json`
- JSON: comments allowed, trailing commas allowed
- Schema: [`schema/kiln-blocks.schema.json`](../../../../schema/kiln-blocks.schema.json); rules: [lint.md](../lint.md)

## Top level

| Key | Type | Required | Notes |
|---|---|---|---|
| `$schema` | string |  |  |
| `armor` | object |  | Things worn: made exactly as their recipe tooltip shows. |
| `armorSets` | object |  | Parts: a family of looks, one per slot. |
| `armorSlots` | object |  | Parts: your own slot, starting from a premade. |
| `armorStats` | object |  | Parts: a piece of armor's numbers. |
| `effects` | object |  | What happens: the same effect plugs in everywhere. |
| `format` | `1` | yes |  |
| `items` | object |  | Anything else that goes in a bag: a material, a potion, food, an arrow, a tool. Its kind follows from what you fill in, or say it. |
| `looks` | object |  | Parts: what an item looks like. Use one by name wherever a field takes a look. |
| `materials` | `kiln-vfx.schema.json materialSection` |  | Surfaces for models, on the game's own shaders. No base to name, no game data. Built by the game client at start. |
| `onHits` | object |  | Parts: what an item does to what its wearer hits. |
| `spells` | object |  | Staff spells. A charge is one item in the stack. |
| `stations` | object |  | Crafting stations a player builds. |
| `statuses` | object |  | Buffs and debuffs, made of effects. |
| `vfx` | `kiln-vfx.schema.json section` |  | Effects written from nothing: particles and a light. No compiler, no game data. Built by the game client at start. |
| `wands` | object |  | One-handed casters whose attacks are spells of their own: no spell item, no charges. |
| `weaponClasses` | object |  | Parts: your own class of weapon, starting from a premade. |
| `weaponStats` | object |  | Parts: a weapon's numbers. |
| `weapons` | object |  | Weapons that are made exactly as their recipe tooltip shows. |

## ammo

What an arrow adds to the bow that shoots it.

| Key | Type | Required | Notes |
|---|---|---|---|
| `damage` | [damage](#damage) |  | The kinds it deals: the shares count, as for a weapon. |
| `delivery` | [projectile](#projectile) |  | A flight of its own. Default: an arrow's. |
| `power` | integer |  | The power it adds to the bow's. Default: damage's total, or an arrow's own. min 0 |

## applies

A status put on the target.

Type: [ref](#ref) or object

## area

Everything around the place gets these effects; who is in it is found when it happens.

| Key | Type | Required | Notes |
|---|---|---|---|
| `affects` | `"enemies"` \| `"friends"` |  | Default enemies. |
| `effects` | [effects](#effects) | yes |  |
| `radius` | number | yes | Metres. |
| `vfx` | [vfx](#vfx) |  | A VFX where the area is, once. Its size is the VFX's own. |

## armor

| Key | Type | Required | Notes |
|---|---|---|---|
| `description` | string |  |  |
| `loot` | [loot](#loot) |  |  |
| `name` | string | yes | min length 1 |
| `onHitTaken` | [hits](#hits) |  | What every hit the wearer takes does to whoever dealt it, while it is worn, with no unlock. |
| `perks` | [perks](#perks) |  |  |
| `recipe` | [recipe](#recipe) |  |  |
| `set` | `"warrior"` \| `"mage"` \| `"ranger"` or [ref](#ref) or [armorSet](#armorset) |  | A family of looks: this takes the look the set has for its slot. visual wins over it. |
| `slot` | `"head"` \| `"chest"` \| `"arms"` \| `"legs"` \| `"feet"` \| `"ring"` or [ref](#ref) or [armorSlot](#armorslot) | yes | Where it is worn. |
| `stats` | [armorStatsRef](#armorstatsref) |  |  |
| `tweak` | [tweak](#tweak) |  |  |
| `visual` | [visualRef](#visualref) |  |  |
| `was` | [name](#name) |  |  |

## armorSet

A family of looks, one per slot.

| Key | Type | Required | Notes |
|---|---|---|---|
| `arms` | [visualRef](#visualref) |  |  |
| `chest` | [visualRef](#visualref) |  |  |
| `feet` | [visualRef](#visualref) |  |  |
| `head` | [visualRef](#visualref) |  |  |
| `legs` | [visualRef](#visualref) |  |  |

## armorSlot

Your own slot: it brings its look and stats to the pieces worn there.

| Key | Type | Required | Notes |
|---|---|---|---|
| `like` | `"head"` \| `"chest"` \| `"arms"` \| `"legs"` \| `"feet"` \| `"ring"` | yes | The premade it starts from. |
| `stats` | [armorStatsRef](#armorstatsref) |  |  |
| `visual` | [visualRef](#visualref) |  |  |

## armorStats

What a piece of armor gives. A number left out: what its slot has at that level.

| Key | Type | Required | Notes |
|---|---|---|---|
| `bonus` | object |  | What wearing it adds; the tooltip follows. A share (melee-damage) is 0.05 for 5%. |
| `level` | [level](#level) |  |  |
| `magical` | integer |  | The tooltip's magical armour. min 0 |
| `physical` | integer |  | The tooltip's physical armour. min 0 |
| `rarity` | [rarity](#rarity) |  |  |
| `resist` | object |  | Armour against one kind, on top of the two above. |

## cast

How a spell is cast: animation, timing, costs.

| Key | Type | Required | Notes |
|---|---|---|---|
| `animation` | `"fast-bolt"` \| `"lob"` |  |  |
| `manaCost` | integer | yes | min 0 |
| `releaseAt` | number |  | Seconds into the animation the spell leaves the staff. |
| `vfx` | [vfx](#vfx) |  |  |

## damage

Damage per kind. On a spell the first one is its main hit: the tooltip's number, and the staff's power adds to it unless flat. Elsewhere exactly this number. Armour and the attacker's stats apply after it.

| Key | Type | Required | Notes |
|---|---|---|---|
| `blunt` | number |  | min 0 |
| `cut` | number |  | min 0 |
| `explosion` | integer |  | min 0 |
| `fire` | number |  | min 0 |
| `flat` | boolean |  | On a spell: exactly this number, with no share of the staff's power. |
| `ice` | number |  | min 0 |
| `lightning` | number |  | min 0 |
| `metal` | integer |  | min 0 |
| `pierce` | number |  | min 0 |
| `poison` | number |  | min 0 |
| `shroud` | number |  | min 0 |
| `stone` | integer |  | min 0 |
| `wood` | integer |  | min 0 |

## effect

What happens. Parts run in this order: chance decides, area, spawns, vfx, sound, damage (with knockback and flinch), heal and restores, removes, applies, then.

| Key | Type | Required | Notes |
|---|---|---|---|
| `applies` | [applies](#applies) |  |  |
| `area` | [area](#area) |  |  |
| `chance` | number |  | The whole effect happens this often. min 0 max 1 |
| `damage` | [damage](#damage) |  |  |
| `flinch` | [flinch](#flinch) |  |  |
| `heal` | integer |  | Health given to the target. min 1 |
| `knockback` | [knockback](#knockback) |  |  |
| `on` | `"target"` \| `"caster"` |  | Who it is for: what the trigger gives it (default), or the caster, whoever wields the weapon or cast the spell. |
| `removes` | array of [statusRef](#statusref) |  | Statuses taken off the target: blocks by name, or the game's by their 0x id. |
| `restores` | object |  | Given back to the target. Less than 0 takes it away. A target without that attribute gets nothing. |
| `sound` | string |  | A sound at the target or the place: a cue of this mod (needs clients: mod), "other-mod:name", or a cue's GUID. min length 1 |
| `spawns` | string |  | Spawned at the target or the place: a game template's name, or a template's GUID. min length 1 |
| `then` | [effects](#effects) |  | More effects, on the same target. |
| `vfx` | [vfx](#vfx) |  |  |

## flinch

How the target flinches.

Type: `"none"` \| `"veryLight"` \| `"light"` \| `"medium"` \| `"heavy"`

## hits

A list of effects, a block of onHits or an effect by name, or an onHit inline.

Type: [ref](#ref) or [effects](#effects) or [onHit](#onhit)

## item

Its kind follows from what you fill in: tool, ammo, feeds, heals or onUse, else a material.

| Key | Type | Required | Notes |
|---|---|---|---|
| `ammo` | [ammo](#ammo) |  |  |
| `comfort` | integer |  | The comfort a placed one gives. min 0 |
| `description` | string |  |  |
| `feeds` | [statusRef](#statusref) |  | The status eating it gives, in the game's food slots. |
| `fuel` | number |  | Seconds it burns in a fire. |
| `heals` | integer |  | Health using it gives at once; the tooltip's number. |
| `kind` | `"material"` \| `"consumable"` \| `"food"` \| `"ammo"` \| `"tool"` |  |  |
| `level` | [level](#level) |  |  |
| `loot` | [loot](#loot) |  |  |
| `name` | string | yes | min length 1 |
| `onUse` | [effects](#effects) |  | What using it does, to whoever uses it. |
| `rarity` | [rarity](#rarity) |  |  |
| `recipe` | [recipe](#recipe) |  |  |
| `stack` | integer |  | How many fit in a stack. Default: the kind's own. A tool does not stack. min 1 max 65535 |
| `texts` | object |  | Texts per language (en, de, pt-BR, ...). |
| `tool` | [tool](#tool) |  |  |
| `tweak` | [tweak](#tweak) |  |  |
| `visual` | [visualRef](#visualref) |  |  |
| `was` | [name](#name) |  |  |

## itemRef

An item: a game item (Material_T1_Twigs), an item of this mod by its block name, or "other-mod:name". min length 1

Type: string

## knockback

How far the hit pushes the target.

Type: `"none"` \| `"small"` \| `"normal"` \| `"big"` \| `"veryBig"` \| `"verySmall"` \| `"pullSmall"` \| `"pullNormal"`

## level

The tooltip's level; a made item has exactly this. min 1 max 55

Type: integer

## loot

Where it can be found, besides being made.

| Key | Type | Required | Notes |
|---|---|---|---|
| `like` | [itemRef](#itemref) |  | It is rolled in chests and world pickups where this item is, with its chances. |
| `rolled` | boolean |  | Gear only: a roll gives it a level and rarity of its own. The opposite of the crafted-item rule, so it refuses a recipe (KL406). |
| `stackMax` | integer |  | min 1 |
| `stackMin` | integer |  | min 1 |
| `tables` | array of [lootTable](#loottable) |  | Drop tables: what enemies and fixed chests hold. |

## lootTable

A place in the drop tables: table or dropsLike, one of them.

| Key | Type | Required | Notes |
|---|---|---|---|
| `chance` | number |  | min 0 max 1 |
| `countMax` | integer |  | min 1 |
| `countMin` | integer |  | min 1 |
| `dropsLike` | [itemRef](#itemref) |  | Every table that drops this item, next to it. |
| `table` | string |  | A table's GUID or a glob over GUIDs. |
| `weight` | number |  | Where the table picks one of several. min 0 |

## name

The block's name. Ids come from your mod id and this: keep it. A rename says "was". Pattern `^[A-Za-z0-9_.-]+$`.

Type: string

## onHit

What an item does to what its wearer hits.

| Key | Type | Required | Notes |
|---|---|---|---|
| `effects` | [effects](#effects) | yes |  |
| `vfx` |  |  | Not available yet: setting it is an error. A VFX goes on what happens, an on-hit or a status. |

## perks

Enhancement slots, from the first: a game perk's name or GUID, item:<name> (that item's perk in the same slot), "none", or null to keep the slot's. The game's rule applies: a perk runs once its slot is unlocked. max items 5

Type: array of string \| null

## projectile

It flies. What it strikes gets onHit, and where it stops onImpact happens.

| Key | Type | Required | Notes |
|---|---|---|---|
| `gravity` | number |  | 0 flies straight, 1 falls like a thrown stone (default 0.1). |
| `look` | `"ice-bolt"` \| `"fireball"` |  |  |
| `onHit` | [effects](#effects) |  | On the creature it strikes, if it struck one. |
| `onImpact` | [effects](#effects) |  | At the place it stops, which has no target of its own: use an area. |
| `range` | number |  | Metres before it fizzles (default 150). |
| `speed` | number |  | Metres per second (default 30). |
| `tweak` | [tweak](#tweak) |  |  |
| `type` | `"projectile"` | yes |  |
| `vfx` | [vfx](#vfx) |  |  |

## rarity

What a made item is: its colour, and how many enhancements it can have (one more than the rank).

Type: `"common"` \| `"uncommon"` \| `"rare"` \| `"epic"` \| `"legendary"`

## recipe

How something is crafted.

| Key | Type | Required | Notes |
|---|---|---|---|
| `makes` | integer |  | How many one craft gives. For a spell: charges. min 1 max 65535 |
| `needs` | object | yes | Item -> count: a game item, an item of this mod by its block name, or "other-mod:name". |
| `seconds` | number |  |  |
| `station` | string |  | "hand" (the default): the player's own menu; a workshop's 0x id; or a station block by name. |

## ref

A block of this mod by name, or "other-mod:name". min length 1

Type: string

## spell

| Key | Type | Required | Notes |
|---|---|---|---|
| `cast` | [cast](#cast) | yes |  |
| `delivery` | [projectile](#projectile) | yes |  |
| `description` | string |  |  |
| `level` | integer |  | min 1 max 55 |
| `maxCharges` | integer |  | Charges in one stack (default 500). min 1 max 65535 |
| `name` | string | yes | min length 1 |
| `onCast` | [effects](#effects) |  | On the caster, the moment it leaves the staff. |
| `recipe` | [recipe](#recipe) |  |  |
| `tweak` | [tweak](#tweak) |  |  |
| `was` | [name](#name) |  |  |

## station

A crafting station a player builds: it opens a workshop's menu, which lists that workshop's recipes and those naming this station.

| Key | Type | Required | Notes |
|---|---|---|---|
| `description` | string |  |  |
| `kind` | `"workbench"` \| `"carpenter"` \| `"kitchen"` \| `"forge"` \| `"oven"` \| `"alchemy"` |  | Which menu it opens, and the build it starts from. Default workbench. |
| `loot` | [loot](#loot) |  |  |
| `name` | string | yes | min length 1 |
| `recipe` | [recipe](#recipe) |  |  |
| `tweak` | [tweak](#tweak) |  |  |
| `visual` | [visualRef](#visualref) |  |  |
| `was` | [name](#name) |  |  |

## status

A buff or debuff, shown in the game's buff bar.

| Key | Type | Required | Notes |
|---|---|---|---|
| `description` | string |  |  |
| `icon` | string |  | The icon, when it is not the look's: a game status by its id, whose icon it takes. Pattern `^(0x[0-9a-fA-F]{8}\|buff_[0-9a-fA-F]{8})$`. |
| `kind` | `"debuff"` \| `"buff"` |  |  |
| `look` | `"frost"` \| `"poison"` |  | Its icon and body effect. |
| `modifies` | object |  | Stats changed for as long as it runs. Whole-number stats take whole numbers; the rest are shares, 0.1 being 10%. A GUID names any other attribute (as a share). |
| `name` | string | yes | min length 1 |
| `onApply` | [effects](#effects) |  |  |
| `onExpire` | [effects](#effects) |  |  |
| `onTick` | [effects](#effects) |  |  |
| `refreshes` | boolean |  | Applied again while it runs: the timer starts over (true, the default), or the new one replaces the old. |
| `seconds` | number | yes | How long it lasts unless whoever applies it says otherwise. |
| `tickEvery` | number |  | Seconds between onTick (default 1). |
| `tweak` | [tweak](#tweak) |  |  |
| `vfx` | [vfx](#vfx) |  |  |
| `was` | [name](#name) |  | The name this block had before a rename. Ids come from the oldest name, so saves keep it. |

## tool

What a tool gets out of the world.

| Key | Type | Required | Notes |
|---|---|---|---|
| `harvests` | `"wood"` \| `"stone"` | yes | wood: an axe. stone: a pickaxe, for stone and metal, and it digs. |
| `power` | integer |  | How hard it bites into what it is for: an axe's wood damage, a pickaxe's stone damage. The rest of what it can do scales along. |
| `staminaCost` | integer |  | Stamina each swing costs; the tooltip follows. min 0 |

## tweak

Game fields this block does not cover, by the path a data pack writes, set on what it makes. They win over what the block sets.

Type: object

## vfx

A VFX and where it shows: a VFX block of this mod by name, "other-mod:name", or "game:<name>" for one of the game's (a template that carries it, a sequence that plays it, or a GUID). A new VFX needs "clients": "mod".

Type: [ref](#ref) or object

## visual

What an item looks like. Empty: its starter's look.

| Key | Type | Required | Notes |
|---|---|---|---|
| `dropped` | [itemRef](#itemref) |  | An item whose model this shows lying on the ground. |
| `icon` | string |  | The icon alone: item:<name>, or a PNG in the mod's folder (then clients "mod"). |
| `like` | [itemRef](#itemref) |  | An item whose whole look this takes: held or worn model, dropped model, collection model and icon. |
| `model` | string |  | The model alone: item:<name>, or the GUID of a model your assets/assets.json serves (then clients "mod"). |
| `tint` | string |  | The colour of the parts the game lets you tint. Pattern `^#[0-9a-fA-F]{6}$`. |
| `vfx` |  |  | Not available yet: setting it is an error. A VFX goes on what happens, an on-hit or a status. |

## visualRef

A look: inline, or a block of looks by name.

Type: [ref](#ref) or [visual](#visual)

## wand

| Key | Type | Required | Notes |
|---|---|---|---|
| `class` | [weaponClassRef](#weaponclassref) |  |  |
| `description` | string |  |  |
| `heavy` | [wandAttack](#wandattack) |  | Default: the primary again. Its damage is exactly what it says. |
| `loot` | [loot](#loot) |  |  |
| `name` | string | yes | min length 1 |
| `onHit` | [hits](#hits) |  |  |
| `onKill` | [hits](#hits) |  |  |
| `perks` | [perks](#perks) |  |  |
| `primary` | [wandAttack](#wandattack) | yes | Its main damage is the wand's power: the tooltip's number, and what perks and stats scale. |
| `recipe` | [recipe](#recipe) |  |  |
| `special` | [wandAttack](#wandattack) |  | Once a player has the game's skill for it. Default: the primary again. Its damage is exactly what it says. |
| `stats` | [weaponStatsRef](#weaponstatsref) |  |  |
| `tweak` | [tweak](#tweak) |  |  |
| `visual` | [visualRef](#visualref) |  |  |
| `was` | [name](#name) |  |  |

## wandAttack

One of a wand's attacks: how it is cast and what it sends.

| Key | Type | Required | Notes |
|---|---|---|---|
| `cast` | [cast](#cast) | yes | The mana it costs and when it leaves the wand. animation is a staff's: a wand's motion is moves. |
| `delivery` | [projectile](#projectile) | yes |  |
| `moves` | `"bolt"` \| `"heavy"` \| `"special"` |  | The motion. Default: the attack slot's own. |
| `onCast` | [effects](#effects) |  | On the caster, the moment it leaves the staff. |

## weapon

| Key | Type | Required | Notes |
|---|---|---|---|
| `class` | [weaponClassRef](#weaponclassref) | yes |  |
| `description` | string |  |  |
| `loot` | [loot](#loot) |  |  |
| `name` | string | yes | min length 1 |
| `onHit` | [hits](#hits) |  | What every hit the wielder lands does to the target, while it is in hand, with no unlock. |
| `onKill` | [hits](#hits) |  | What a hit that kills does to what it killed, while it is in hand. |
| `perks` | [perks](#perks) |  |  |
| `recipe` | [recipe](#recipe) |  |  |
| `stats` | [weaponStatsRef](#weaponstatsref) |  |  |
| `tweak` | [tweak](#tweak) |  |  |
| `visual` | [visualRef](#visualref) |  |  |
| `was` | [name](#name) |  |  |

## weaponClass

Your own class of weapon. It brings its look, stats and perks to the weapons of the class.

| Key | Type | Required | Notes |
|---|---|---|---|
| `like` | `"sword"` \| `"axe"` \| `"mace"` \| `"greatsword"` \| `"greataxe"` \| `"hammer"` \| `"daggers"` \| `"bow"` \| `"staff"` \| `"shield"` \| `"wand"` | yes | The premade it starts from. |
| `perks` | [perks](#perks) |  |  |
| `stats` | [weaponStatsRef](#weaponstatsref) |  |  |
| `visual` | [visualRef](#visualref) |  |  |

## weaponClassRef

How it is held and swung: a premade, a block of weaponClasses by name, or your own.

Type: `"sword"` \| `"axe"` \| `"mace"` \| `"greatsword"` \| `"greataxe"` \| `"hammer"` \| `"daggers"` \| `"bow"` \| `"staff"` \| `"shield"` \| `"wand"` or [ref](#ref) or [weaponClass](#weaponclass)

## weaponStats

A weapon's numbers: the tooltip's, made exactly so.

| Key | Type | Required | Notes |
|---|---|---|---|
| `block` | number |  | A shield's block strength. min 0 max 1 |
| `damage` | [damage](#damage) |  | The kinds it deals: the shares of the kinds that hurt count; wood, stone and metal are exact. |
| `level` | [level](#level) |  |  |
| `power` | integer |  | The tooltip's damage at that level. Default: what the class has, or damage's total. min 0 |
| `rarity` | [rarity](#rarity) |  |  |
| `staminaCost` | integer |  | Stamina each use costs; the tooltip follows. min 0 |

