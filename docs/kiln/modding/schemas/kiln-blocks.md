# Kiln building blocks (blocks/*.json), format 1

<!-- Generated from schema/kiln-blocks.schema.json by kiln_manage::lint::docs (cargo test -p kiln-manage --test lint with KILN_LINT_WRITE=1). Do not edit. -->

A mod's blocks: spells, weapons, wands, armor, items, stations, statuses, effects, perks, abilities, talents, triggers and their parts, the same shape as the C# records (Kiln.Blocks). Read by managed/services/Kiln.Blocks/BlockJson.cs (System.Text.Json: comments and trailing commas allowed, unknown keys refused). A field that takes a block takes it inline, or by name: a block of this mod, or "other-mod:name".

- Files: `**/blocks/*.json`
- JSON: comments allowed, trailing commas allowed
- Schema: [`schema/kiln-blocks.schema.json`](../../../../schema/kiln-blocks.schema.json); rules: [lint.md](../lint.md)

## Top level

| Key | Type | Required | Notes |
|---|---|---|---|
| `$schema` | string |  |  |
| `abilities` | object |  | What a character casts that is no staff's: on a key, a chat command, an item, or from code. |
| `ais` | object |  | AI tunings. |
| `animations` | object |  | Animations: clips of a glTF, cooked when the mod builds. |
| `armor` | object |  | Things worn: made exactly as their recipe tooltip shows. |
| `armorSets` | object |  | Parts: a family of looks, one per slot: looks or worn looks. |
| `armorSlots` | object |  | Parts: your own slot, starting from a premade. |
| `armorStats` | object |  | Parts: a piece of armor's numbers. |
| `attacks` | object |  | Attacks, for behaviours to name. |
| `beards` | object |  | Beards: worn looks on the jaw. A look with files of the mod's own needs "clients": "mod". |
| `beds` | object |  | Beds: the game's sleep and rest, comfort of the bed category; with Kiln a spawn point and a night slept through. |
| `behaviours` | object |  | Behaviours: attacks, rules, a brain. |
| `blueprints` | object |  | New shapes for the game's building materials, half a metre a cell: a table each game merges at its start, so needs "clients": "mod". |
| `bodies` | object |  | Bodies, for creatures and NPCs to name. |
| `buildingMaterials` | object |  | New building materials: an id and a GUID in the game's tables, and a block item. 49 fit across all mods. Needs "clients": "mod". |
| `comforts` | object |  | Parts: what the game's comfort counts. |
| `containers` | object |  | Things that hold items, as the game's chests do: a lid, sounds and upgrades from the game's small chest. |
| `cosmetics` | object |  | Pieces for the vanity slots: shown over what is worn there, with no stats. A look with files of the mod's own needs "clients": "mod". |
| `creatures` | object |  | Creatures: a body, how it moves, sees and fights. |
| `doors` | object |  | Doors: they open and close for whoever uses them, and paths and water follow them. |
| `effects` | object |  | What happens: the same effect plugs in everywhere. |
| `format` | `1` | yes |  |
| `hair` | object |  | Hair styles: worn looks on the head. A look with files of the mod's own needs "clients": "mod". |
| `hudLines` | object |  | Lines of text on the game's screen with values in them, a condition and a refresh rate. Needs "clients": "mod". |
| `interactions` | object |  | Parts: what happens when a player uses a placed thing. Use one by name in onInteract. |
| `items` | object |  | Anything else that goes in a bag: a material, a potion, food, an arrow, a tool. Its kind follows from what you fill in, or say it. |
| `keyBindings` | object |  | Keys that run an action (open a window, toggle a config value, run a command, cast an ability), rebindable in the Kiln menu. Needs "clients": "mod". |
| `looks` | object |  | Parts: what an item looks like. Use one by name wherever a field takes a look. |
| `materialTweaks` | object |  | Changes to game materials: what it takes to dig them. Read at boot by kiln.worldtables; a strength change asks nothing of players' games. |
| `materials` | `kiln-vfx.schema.json materialSection` |  | Surfaces for models, on the game's own shaders. No base to name, no game data. Built by the game client at start. |
| `menus` | object |  | Parts: the crafting menu a station opens. |
| `npcs` | object |  | Friendly NPCs: talk, wares, a place in the roster. |
| `onHits` | object |  | Parts: what an item does to what its wearer hits. |
| `perks` | object |  | Enhancements for an item's perks: the game's rule applies, so one runs once a player has unlocked its slot. |
| `placements` | object |  | Parts: where and how a player can place something. |
| `processing` | object |  | Parts: how a station works on its own. |
| `props` | object |  | Things placed that stand there: furniture, banners, lamps. |
| `rules` | object |  | Rules, for behaviours to name. |
| `screenEffects` | object |  | Full-screen effects: colour grading, a tint, a vignette, fades and flashes. Drawn by each Kiln client; players' games need nothing for them. |
| `seats` | object |  | Seats: the game's sitting, comfort of the chair category. |
| `sounds` | object |  | Sounds of the mod's own: clips and how they play. kiln cook writes their steps into assets/cook.json (tools/soundcook blocks) and cooks them. An effect, a station or code plays one by its name. Players need the mod (clients "mod"). |
| `spells` | object |  | Staff spells. A charge is one item in the stack. |
| `stations` | object |  | Stations a player builds: a workbench, or a forge or rack that works on its own. |
| `statuses` | object |  | Buffs and debuffs, made of effects. |
| `strengths` | object |  | Parts: what it takes to dig a material. Use one by name wherever a field takes a strength. |
| `surfaces` | object |  | Parts: how building materials look, defined once for materials of this mod to name. |
| `talentTrees` | object |  | Whole trees of their own, on Kiln's screen. Needs "clients": "mod". |
| `talents` | object |  | Nodes of a skill tree: in the game's own tree with game, else parts for a talent tree. Needs "clients": "mod". |
| `terrainMaterials` | object |  | New terrain materials: ground, rock or ore, an id and a GUID in the game's tables. 11 fit across all mods, apart from the building materials. Needs "clients": "mod". |
| `triggers` | object |  | What a player does or is, any of the game's states and actions, driving effects. |
| `vfx` | `kiln-vfx.schema.json section` |  | Effects written from nothing: particles and a light. No compiler, no game data. Built by the game client at start. |
| `wands` | object |  | One-handed casters whose attacks are spells of their own: no spell item, no charges. |
| `weaponClasses` | object |  | Parts: your own class of weapon, starting from a premade. |
| `weaponStats` | object |  | Parts: a weapon's numbers. |
| `weapons` | object |  | Weapons that are made exactly as their recipe tooltip shows. |
| `windows` | object |  | Windows and screens of rows: text, values, buttons, toggles and sliders on the mod's config. Drawn by each player's game from the mod's own file, so needs "clients": "mod"; on a dedicated server checked and kept, nothing drawn. |
| `wornLooks` | object |  | Parts: what something looks like on the body. Use one by name wherever a field takes a worn look. The cook finds the files of every look here by itself. |

## ability

Cast on a key, a chat command, an item, or from code. It pays its costs, keeps its cooldown, then does what its delivery says. Its id is <mod>:<name>.

| Key | Type | Required | Notes |
|---|---|---|---|
| `aim` | `"self"` \| `"forward"` \| `"nearestEnemy"` |  | Where it aims (default forward). nearestEnemy: no enemy in range, no cast. |
| `cast` | [cast](#cast) |  |  |
| `command` | string |  | A chat command that casts it, without the prefix. min length 1 |
| `cooldown` | number |  | Seconds, per caster. min 0 |
| `delivery` | [delivery](#delivery) |  |  |
| `description` | string |  |  |
| `item` | [item](#item) |  | An item that casts it when used. cast.charges is how many a cast takes of it. |
| `key` | string |  | The default key ("G", "Shift+F"). Binding it needs the mod on the player's game: "clients": "mod". min length 1 |
| `name` | string | yes | min length 1 |
| `onCast` | [effects](#effects) |  | On the caster, the moment it leaves. |
| `playersOnly` | boolean |  | Keys and commands for players only (default). |
| `range` | number |  | Metres, for aim (default 20). |
| `specialCost` | integer |  | Special-ability charge. min 0 |
| `was` | [name](#name) |  |  |

## action

A rule's action: setPhase, attack, custom, { "effect": ..., "on": "target" \| "self" }, flee, backOff, play, immune, pause, enableFeature, disableFeature, heal, summon, cast, { "apply": ..., "on": ... }, or one of the game's { "type": ... }.

Type: object

## ai

How it notices and chases: a game creature's tuning to start from, and what to change.

| Key | Type | Required | Notes |
|---|---|---|---|
| `aggro` | number |  | Metres. min 0 max 1000 |
| `callsForHelp` | boolean |  |  |
| `disable` | string |  | The game's AI features to turn off, comma separated. |
| `enable` | string |  | The game's AI features to turn on, comma separated (EnemyFeatures names). |
| `faction` | string |  |  |
| `fights` | boolean |  |  |
| `flee` | object |  | How it runs from a fight. |
| `hearing` | number |  | Share of the game's noise ranges. min 0 max 10 |
| `hears` | boolean |  | False: it does not react to noise. |
| `helpRadius` | number |  | Metres. min 0 max 200 |
| `leash` | number |  | Its home zone's radius, metres. min 0 max 1000 |
| `like` | `"vukah"` \| `"scavenger"` \| `"heavy"` \| `"still"` |  |  |
| `moves` | boolean |  |  |
| `nightSight` | number |  | Share of its sight at night. min 0 max 4 |
| `pickEvery` | number |  | Seconds. min 0 max 60 |
| `pickTarget` | string |  | A picker registered with Blocks.Target. |
| `relations` | object |  | Faction name -> how this creature treats it. |
| `returnsHome` | boolean |  | False: it never walks home after a fight. |
| `roam` | object |  | How it wanders (turns the game's Roaming on). |
| `senseEvery` | number |  | Seconds. min 0.05 max 60 |
| `senseFilter` | string |  | A sense filter registered with Blocks.SenseFilter. |
| `sight` | number |  | Metres. min 0 max 1000 |
| `sightAngle` | number |  | Half its view, degrees. min 0 max 180 |
| `template` | string |  |  |
| `threat` | number |  | How much it draws enemies' attention, 1 as the game has it. min 0 max 100 |
| `tune` | object |  | Any field of the game's AI by path. |

## ammo

What an arrow adds to the bow that shoots it.

| Key | Type | Required | Notes |
|---|---|---|---|
| `damage` | [damage](#damage) |  | The kinds it deals: the shares count, as for a weapon. |
| `delivery` | [projectile](#projectile) |  | A flight of its own. Default: an arrow's. |
| `power` | integer |  | The power it adds to the bow's. Default: damage's total, or an arrow's own. min 0 |

## animBlend

Animations mixed by an input.

| Key | Type | Required | Notes |
|---|---|---|---|
| `circular` | boolean |  |  |
| `clips` | object | yes |  |
| `input` | string |  | moveDirection, hitDirection, lookYaw, ..., or a name of your own. |

## animClip

What a state plays: a glTF animation's name, or { "clip": name, ... }, or { "blend": ... }.

Type: string or object

## animLayer

Played over the states: always, or on an overlay.

| Key | Type | Required | Notes |
|---|---|---|---|
| `blend` | [animBlend](#animblend) |  |  |
| `bones` | array of string |  |  |
| `clip` | string |  |  |
| `fadeIn` | number |  |  |
| `fadeOut` | number |  |  |
| `name` | string |  |  |
| `overlay` | string |  |  |
| `reference` | string |  |  |
| `speed` | number |  |  |
| `weight` | number |  |  |

## animation

How a body moves: a glTF of the mod with its clips (cooked when the mod builds), the engine's states and your own. Or a premade: "like".

| Key | Type | Required | Notes |
|---|---|---|---|
| `clips` | string |  | The glTF file, its path in the mod's folder. Pattern `\.([gG][lL][bB]\|[gG][lL][tT][fF])$`. |
| `dead` | [animClip](#animclip) |  |  |
| `fall` | [animClip](#animclip) |  |  |
| `hit` | [animClip](#animclip) |  |  |
| `idle` | [animClip](#animclip) |  |  |
| `idleCombat` | [animClip](#animclip) |  |  |
| `idleSneak` | [animClip](#animclip) |  |  |
| `layers` | array of [animLayer](#animlayer) |  |  |
| `like` | `"vukah"` \| `"scavenger"` \| `"fisher"` \| `"villager"` |  |  |
| `retarget` | object |  | The clips are authored on a rig of their own: play them on "skeleton" (a body's) by pairing joints. Each paired joint turns as its source turns; translations carry over on the hips, scaled by the rigs' heights. |
| `run` | [animClip](#animclip) |  |  |
| `skeleton` | string |  |  |
| `sneak` | [animClip](#animclip) |  |  |
| `sprint` | [animClip](#animclip) |  |  |
| `stagger` | [animClip](#animclip) |  | What a hit reaction, a stun or a parry plays over the whole body. Default: the hit clip once, else Idle. |
| `states` | object |  | States of your own, by name. |
| `stun` | [animClip](#animclip) |  |  |
| `template` | string |  | A game creature whose graph this is. |
| `walk` | [animClip](#animclip) |  |  |

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
| `look` | [wornLookRef](#wornlookref) |  | What it looks like on the body: a model of the mod's own. It wins over visual for what is worn; visual still gives the dropped model and the tint. |
| `loot` | [loot](#loot) |  |  |
| `name` | string | yes | min length 1 |
| `onHitTaken` | [hits](#hits) |  | What every hit the wearer takes does to whoever dealt it, while it is worn, with no unlock. |
| `perks` | [perks](#perks) |  |  |
| `recipe` | [recipe](#recipe) |  |  |
| `set` | `"warrior"` \| `"mage"` \| `"ranger"` or [ref](#ref) or [armorSet](#armorset) |  | A family of looks: this takes the look the set has for its slot. visual wins over it. |
| `slot` | `"head"` \| `"chest"` \| `"arms"` \| `"legs"` \| `"feet"` \| `"ring"` or [ref](#ref) or [armorSlot](#armorslot) | yes | Where it is worn. |
| `source` | string |  | Where to get it, in the tooltip. Not given: nothing. |
| `stats` | [armorStatsRef](#armorstatsref) |  |  |
| `tweak` | [tweak](#tweak) |  |  |
| `visual` | [visualRef](#visualref) |  |  |
| `was` | [name](#name) |  |  |

## armorLook

A set's look for a slot: a look, a worn look, or either by name.

Type: [ref](#ref) or [visual](#visual) or [wornLook](#wornlook)

## armorSet

A family of looks, one per slot.

| Key | Type | Required | Notes |
|---|---|---|---|
| `arms` | [armorLook](#armorlook) |  |  |
| `chest` | [armorLook](#armorlook) |  |  |
| `feet` | [armorLook](#armorlook) |  |  |
| `head` | [armorLook](#armorlook) |  |  |
| `legs` | [armorLook](#armorlook) |  |  |

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

## beard

A beard: a worn look on the jaw. A morph target named Female is its shape on a female head.

| Key | Type | Required | Notes |
|---|---|---|---|
| `description` | string |  |  |
| `look` | [wornLookRef](#wornlookref) | yes |  |
| `name` | string | yes | min length 1 |
| `tweak` | [tweak](#tweak) |  |  |
| `was` | [name](#name) |  |  |

## bed

A bed: the game's sleep and rest. Starts from the game's wooden bed; visual.like names another.

| Key | Type | Required | Notes |
|---|---|---|---|
| `comfort` | [ref](#ref) or [comfort](#comfort) |  |  |
| `description` | string |  |  |
| `lies` | [spot](#spot) |  | Where the player lies. Default: the starter's spot. |
| `light` | [vfx](#vfx) |  | A VFX on it for as long as it stands: where the game's lamps have their light. |
| `loot` | [loot](#loot) |  |  |
| `name` | string | yes | min length 1 |
| `onInteract` | [interactionRef](#interactionref) |  | What happens when a player uses it. |
| `placement` | [ref](#ref) or [placement](#placement) |  |  |
| `recipe` | [recipe](#recipe) |  | How the item that places it is made. |
| `skipsNight` | boolean |  | When every player in the world sleeps at night in such beds, the morning comes. Kiln's, on the server. |
| `spawnPoint` | boolean |  | A player who dies comes back at the last of these beds they slept in. Kiln's, on the server. |
| `tweak` | [tweak](#tweak) |  |  |
| `tweakPlaced` | [tweak](#tweak) |  | The same, set on the template it places (templates section paths). |
| `visual` | [visualRef](#visualref) |  | Its look: like (a game item that places something: model, collision and how it places), or model (item:<name>, or the GUID of a model your assets/assets.json serves). |
| `was` | [name](#name) |  |  |

## behaviour

What a creature does: attacks and rules on top of the game's combat AI, or a brain.

| Key | Type | Required | Notes |
|---|---|---|---|
| `attacks` | array of [ref](#ref) or [creatureAttack](#creatureattack) |  | max items 32 |
| `brain` | string |  | A brain registered with Blocks.Brain. |
| `like` | `"vukah-melee"` \| `"scavenger-melee"` \| `"none"` |  |  |
| `phases` | array of string |  |  |
| `pickAttack` | string |  |  |
| `pickEvery` | number |  | Seconds. min 0 max 60 |
| `rules` | array of [ref](#ref) or [rule](#rule) |  | max items 32 |
| `template` | string |  |  |

## blueprint

A new shape on the game's building materials: beside the walls and floors in the build menu, taking every material they take.

| Key | Type | Required | Notes |
|---|---|---|---|
| `cells` | array of array of string | yes | Layers from the bottom up; in a layer, rows from the back to the front; in a row, # a cell and . or a space none. Half a metre a cell, 32 at most each way. min items 1 max items 32 |
| `cost` | integer |  | Obsolete and not used: a placement takes one block of the chosen material per cell it fills, as the game's own shapes do. min 1 max 9999 |
| `description` | string |  |  |
| `name` | string | yes | min length 1 |
| `recipe` | [recipe](#recipe) |  |  |
| `roof` | boolean |  | A roof shape: it cycles through the roof materials. |
| `tweak` | [tweak](#tweak) |  |  |
| `was` | [name](#name) |  |  |

## body

What a creature is: a skinned glTF of the mod, or a game creature's body ("like").

| Key | Type | Required | Notes |
|---|---|---|---|
| `colliders` | array of [collider](#collider) |  | max items 2 |
| `like` | `"vukah"` \| `"scavenger"` \| `"fisher"` \| `"villager"` |  |  |
| `model` | string |  | Pattern `\.([gG][lL][bB]\|[gG][lL][tT][fF])$`. |
| `scale` | number |  | 1 as modelled. min 0 max 20 |
| `size` | object |  |  |
| `skeleton` | string |  |  |
| `slots` | object |  | The game's attachment slots on joints: { "HandR": "hand_r" }. |
| `template` | string |  |  |
| `tool` | string |  |  |
| `workshop` | string |  | Pattern `^0x[0-9a-fA-F]+$`. |

## bodyPart

A body part, or what is worn in a slot. Hidden, it is not drawn.

Type: `"head"` \| `"hair"` \| `"eyeBrow"` \| `"beard"` \| `"neck"` \| `"collar"` \| `"chest"` \| `"waist"` \| `"crotch"` \| `"upperArms"` \| `"lowerArms"` \| `"hands"` \| `"upperLegs"` \| `"lowerLegs"` \| `"feet"` \| `"armorHead"` \| `"armorUpperBody"` \| `"armorArms"` \| `"armorLowerBody"` \| `"armorFeet"` \| `"ringLeft"` \| `"ringRight"` \| `"shield"` \| `"mainHand"`

## buildingMaterial

A new building material.

| Key | Type | Required | Notes |
|---|---|---|---|
| `comment` | string |  |  |
| `description` | string |  |  |
| `icon` | string |  | The block item's picture: a PNG in the mod's folder, or item:<name>. Without one, each client draws a block of its surface. min length 1 |
| `name` | string | yes | The block item's name, as players read it. min length 1 |
| `recipe` | [recipe](#recipe) |  |  |
| `roof` | boolean |  | A roof material: the roof pieces list it, not the walls. |
| `strength` | [strengthRef](#strengthref) |  |  |
| `surface` | [buildingSurfaceRef](#buildingsurfaceref) |  | How it looks. Without one, like the game's Rough Stone. |
| `was` | string |  | The block's old name, after a rename: the material's id comes from it, so its walls stay in every world. |

## buildingSurface

How a building material looks. On a new material a layer left out repeats side (edgeTop repeats edge); on a retexture it stays the game's.

| Key | Type | Required | Notes |
|---|---|---|---|
| `bevel` | number |  | Metres the edges are rounded by. Default 0.01. min 0 max 1 |
| `bottom` | [surfaceLayer](#surfacelayer) |  |  |
| `comment` | string |  |  |
| `decoration` | string |  | A game material whose edge decoration it shows, or "none". |
| `edge` | [surfaceLayer](#surfacelayer) |  |  |
| `edgeCurve` | number |  | How the edge layer blends in, as the game keeps it: 1 a hard edge, 0.5 softer. max 1 |
| `edgeOnlySide` | boolean |  | The edge layer only on the sides of a block. Default true. |
| `edgeTop` | [surfaceLayer](#surfacelayer) |  |  |
| `mapping` | `"default"` \| `"wood"` |  | How the maps lie on a block. A roof material always maps as a roof. |
| `noise` | [surfaceLayer](#surfacelayer) |  |  |
| `noiseRange` | array of number |  | [start, full]: where the noise layer starts to show and where it covers all. Default [0.33, 0.75]. min items 2 max items 2 |
| `side` | [surfaceLayer](#surfacelayer) |  |  |
| `top` | [surfaceLayer](#surfacelayer) |  |  |

## buildingSurfaceRef

A building surface, inline or by the name of one in this mod's surfaces sections.

Type: string or [buildingSurface](#buildingsurface)

## cast

How a spell or an ability is cast: animation, timing, costs.

| Key | Type | Required | Notes |
|---|---|---|---|
| `animation` | `"fast-bolt"` \| `"lob"` |  | The motion. A spell's default is fast-bolt; an ability's, none. |
| `charges` | integer |  | How many a cast takes of a spell's stack or an ability's item. 0: none, used for ever. Default 1. min 0 |
| `manaCost` | integer |  | min 0 |
| `releaseAt` | number |  | Seconds into the animation the spell leaves the staff. |
| `staminaCost` | integer |  | min 0 |
| `vfx` | [vfx](#vfx) |  |  |

## collider

| Key | Type | Required | Notes |
|---|---|---|---|
| `height` | number |  | Metres, a capsule's caps included. min 0 |
| `radius` | number |  | Metres. min 0 |
| `shape` | `"capsule"` \| `"sphere"` |  |  |

## comfort

What the game's comfort counts, for whoever rests within range. Only the best of a category counts.

| Key | Type | Required | Notes |
|---|---|---|---|
| `amount` | integer | yes | min 0 |
| `category` | `"hearth"` \| `"bed"` \| `"chair"` \| `"table"` \| `"bench"` \| `"throne"` \| `"bathtub"` \| `"sink"` \| `"toilet"` \| `"shower"` \| `"carpet"` \| `"illumination"` \| `"flame"` \| `"trophy"` \| `"stool"` \| `"kitchen"` \| `"wellness"` |  | Default hearth. |
| `range` | number |  | Metres. Default 5. |
| `warmth` | integer |  | Resistance to the cold near it. Default 1. min 0 |

## condition

A rule's condition: healthBelow, healthAbove, targetWithin, targetBeyond, hasBuff, time, night, day, inPhase, custom, not, inCombat, state, hasTarget, targetIsPlayer, targetHealthBelow, hitWithin, chance, targetAtHome, enemiesNear, alliesNear, summonsBelow, or one of the game's { "type": ... }.

Type: object

## container

Something that holds items. Starts from the game's small chest; visual.like names another.

| Key | Type | Required | Notes |
|---|---|---|---|
| `comfort` | [ref](#ref) or [comfort](#comfort) |  |  |
| `description` | string |  |  |
| `light` | [vfx](#vfx) |  | A VFX on it for as long as it stands: where the game's lamps have their light. |
| `loot` | [loot](#loot) |  |  |
| `name` | string | yes | min length 1 |
| `onInteract` | [interactionRef](#interactionref) |  | What happens when a player uses it. |
| `placement` | [ref](#ref) or [placement](#placement) |  |  |
| `recipe` | [recipe](#recipe) |  | How the item that places it is made. |
| `slots` | integer |  | Inventory slots. Default: the starter's. min 1 max 120 |
| `tweak` | [tweak](#tweak) |  |  |
| `tweakPlaced` | [tweak](#tweak) |  | The same, set on the template it places (templates section paths). |
| `visual` | [visualRef](#visualref) |  | Its look: like (a game item that places something: model, collision and how it places), or model (item:<name>, or the GUID of a model your assets/assets.json serves). |
| `was` | [name](#name) |  |  |

## cosmetic

A piece for a vanity slot: it shows over what is worn there, with no stats of its own and none taken away.

| Key | Type | Required | Notes |
|---|---|---|---|
| `description` | string |  |  |
| `dyes` | boolean |  | Takes the game's dyes, where its material has a tint mask. Default false. |
| `look` | [wornLookRef](#wornlookref) | yes |  |
| `loot` | [loot](#loot) |  |  |
| `name` | string | yes | min length 1 |
| `recipe` | [recipe](#recipe) |  |  |
| `slot` | `"head"` \| `"upperBody"` \| `"arms"` \| `"lowerBody"` \| `"feet"` | yes | Which vanity slot it goes in. |
| `tweak` | [tweak](#tweak) |  |  |
| `was` | [name](#name) |  |  |

## creature

A creature: its body, animation, AI and behaviour.

| Key | Type | Required | Notes |
|---|---|---|---|
| `ai` | [ref](#ref) or [ai](#ai) |  |  |
| `animation` | [ref](#ref) or [animation](#animation) |  |  |
| `behaviour` | [ref](#ref) or [behaviour](#behaviour) |  |  |
| `body` | [ref](#ref) or [body](#body) | yes |  |
| `name` | string |  |  |
| `spawns` | array of [creatureSpawn](#creaturespawn) |  |  |
| `stats` | [creatureStats](#creaturestats) |  |  |
| `was` | string |  |  |

## creatureAttack

An attack: the state it plays, its reach, what its blow does.

| Key | Type | Required | Notes |
|---|---|---|---|
| `angle` | number |  | Degrees in front. min 0 max 360 |
| `chance` | number |  | 0..1. min 0 max 1 |
| `cooldown` | number |  | Seconds. min 0 |
| `delivery` | [delivery](#delivery) |  |  |
| `hitAt` | number |  | Seconds into it. min 0 |
| `length` | number |  | Seconds. min 0 max 30 |
| `minRange` | number |  | Metres. min 0 |
| `name` | string |  | Pattern `^[A-Za-z0-9._-]+$`. |
| `onHit` | array of [effectRef](#effectref) |  |  |
| `plays` | string |  |  |
| `range` | number |  | Metres. min 0 max 200 |
| `sequence` | [guid](#guid) |  |  |
| `style` | array of string |  |  |

## creatureSpawn

| Key | Type | Required | Notes |
|---|---|---|---|
| `chance` | number |  | 0..1. min 0 max 1 |
| `dayOnly` | boolean |  |  |
| `nightOnly` | boolean |  |  |
| `replaces` | string | yes |  |

## creatureStats

| Key | Type | Required | Notes |
|---|---|---|---|
| `damage` | number |  | 1 as the game has it. min 0 max 100 |
| `experience` | number |  | 1 as the game has it. min 0 max 100 |
| `level` | integer |  | min 1 max 60 |
| `takes` | object |  |  |

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

## delivery

How it gets there: a flight, or one of the deliveries placed where the caster is.

Type: [projectile](#projectile) or object or object or object or object or object or object or object

## door

A door. Starts from the game's wooden door; visual.like names another.

| Key | Type | Required | Notes |
|---|---|---|---|
| `comfort` | [ref](#ref) or [comfort](#comfort) |  |  |
| `description` | string |  |  |
| `light` | [vfx](#vfx) |  | A VFX on it for as long as it stands: where the game's lamps have their light. |
| `loot` | [loot](#loot) |  |  |
| `name` | string | yes | min length 1 |
| `onInteract` | [interactionRef](#interactionref) |  | What happens when a player uses it. |
| `placement` | [ref](#ref) or [placement](#placement) |  |  |
| `recipe` | [recipe](#recipe) |  | How the item that places it is made. |
| `tweak` | [tweak](#tweak) |  |  |
| `tweakPlaced` | [tweak](#tweak) |  | The same, set on the template it places (templates section paths). |
| `visual` | [visualRef](#visualref) |  | Its look: like (a game item that places something: model, collision and how it places), or model (item:<name>, or the GUID of a model your assets/assets.json serves). |
| `was` | [name](#name) |  |  |

## effect

What happens. Parts run in this order: chance decides, area, spawns, vfx, sound, damage (with knockback and flinch), heal and restores, removes, applies, then.

| Key | Type | Required | Notes |
|---|---|---|---|
| `applies` | [applies](#applies) |  |  |
| `area` | [area](#area) |  |  |
| `casts` | [ref](#ref) |  | An ability the target casts, as from code: its costs and cooldown apply. With on: caster, whoever wields, wears or cast. |
| `chance` | number |  | The whole effect happens this often. min 0 max 1 |
| `damage` | [damage](#damage) |  |  |
| `flinch` | [flinch](#flinch) |  |  |
| `heal` | integer |  | Health given to the target. min 1 |
| `knockback` | [knockback](#knockback) |  |  |
| `on` | `"target"` \| `"caster"` |  | Who it is for: what the trigger gives it (default), or the caster, whoever wields the weapon or cast the spell. |
| `removes` | array of [statusRef](#statusref) |  | Statuses taken off the target: blocks by name, or the game's by their 0x id. |
| `restores` | object |  | Given back to the target. Less than 0 takes it away. A target without that attribute gets nothing. |
| `screen` | [screenRef](#screenref) |  | A full-screen effect for the target, when it is a player, or for the players near the place. It needs seconds. |
| `sound` | string |  | A sound at the target or the place: a cue of this mod (needs clients: mod), "other-mod:name", or a cue's GUID. min length 1 |
| `spawns` | string |  | Spawned at the target or the place: a game template's name, or a template's GUID. min length 1 |
| `then` | [effects](#effects) |  | More effects, on the same target. |
| `vfx` | [vfx](#vfx) |  |  |

## fits

What a look makes the rest of the character do: "hood" gives hair its hood cut. A name, or a list of names.

Type: [fitName](#fitname) or array of [fitName](#fitname)

## flinch

How the target flinches.

Type: `"none"` \| `"veryLight"` \| `"light"` \| `"medium"` \| `"heavy"`

## hair

A hair style: a worn look on the head. A morph target named Female is its shape on a female head; Hat and Hood the cut under a hat or a hood.

| Key | Type | Required | Notes |
|---|---|---|---|
| `description` | string |  |  |
| `look` | [wornLookRef](#wornlookref) | yes |  |
| `name` | string | yes | min length 1 |
| `tweak` | [tweak](#tweak) |  |  |
| `was` | [name](#name) |  |  |

## hits

A list of effects, a block of onHits or an effect by name, or an onHit inline.

Type: [ref](#ref) or [effects](#effects) or [onHit](#onhit)

## hudLine

A line of text on the game's screen. It shows while its condition holds and updates at its own pace; hidden, it costs nothing. Drawn by each player's game, so the mod needs "clients": "mod".

| Key | Type | Required | Notes |
|---|---|---|---|
| `anchor` | `"topLeft"` \| `"topCenter"` \| `"topRight"` \| `"middleLeft"` \| `"center"` \| `"middleRight"` \| `"bottomLeft"` \| `"bottomCenter"` \| `"bottomRight"` |  | Default topLeft. |
| `color` | [uiColor](#uicolor) |  | Default white. |
| `comment` | string |  |  |
| `refresh` | number |  | Seconds between updates. Default 1. min 0.1 max 3600 |
| `text` | [uiText](#uitext) | yes | The text, with {game.fps:0} and {config.clock.scale:0.00} for values; {{ and }} are braces. |
| `when` | [uiWhen](#uiwhen) |  |  |

## interaction

What happens when a player uses a placed thing. A field left out is what its starter does. verb, plays, toggle and the game's lock in requires are the game's own; effects and requires' when, unless are Kiln's, on the server.

| Key | Type | Required | Notes |
|---|---|---|---|
| `effects` | [effects](#effects) |  | What happens to the player once the game has accepted the use. |
| `onRefused` | [effects](#effects) |  | What happens to the player when requires' when or unless refuses them. |
| `plays` | `"sit"` \| `"sleep"` \| `"toggle"` \| `"chest"` \| `"inventory"` \| `"read"` \| `"talk"` \| `"drink"` or [guid](#guid) |  | What the player plays: one of the game's interaction sequences, or a sequence's GUID. |
| `prompt` | string |  | A prompt of your own instead of verb: the same text in every language. min length 1 |
| `requires` | [requires](#requires) |  |  |
| `toggle` | object |  | Two states, and each use flips it, as a door's: the guest's sequence flips it. A prompt left out is its starter's. |
| `verb` | `"open"` \| `"close"` \| `"use"` \| `"activate"` \| `"deactivate"` \| `"flip"` \| `"read"` \| `"sit"` \| `"sleep"` \| `"search"` \| `"inspect"` \| `"talk"` \| `"craft"` \| `"collect"` \| `"harvest"` \| `"drink"` |  | The prompt's word: one of the game's, so every language has it. |

## interactionRef

An interaction: inline, or a block of interactions by name.

Type: [ref](#ref) or [interaction](#interaction)

## item

Its kind follows from what you fill in: tool, ammo, feeds, heals or onUse, else a material. A water container (watering-can, water-pouch) is named by kind.

| Key | Type | Required | Notes |
|---|---|---|---|
| `ammo` | [ammo](#ammo) |  |  |
| `canEat` | object |  | When it can be used. Refused, the item stays and gives nothing. |
| `comfort` | integer |  | The comfort a placed one gives. min 0 |
| `description` | string |  |  |
| `feeds` | [statusRef](#statusref) |  | The status eating it gives, in the game's food slots. |
| `flow` | number |  | How fast a water container fills and pours (at a lake, a well or a barrel, onto crops and soil), as a multiple of the game's own: what one step moves, 32 for the can, 16 for the pouch, about 20 steps a second. The game has no rate for filling alone. Default: 1. min 0.25 max 8 |
| `fuel` | number |  | Seconds it burns in a fire. |
| `heals` | integer |  | Health using it gives at once; the tooltip's number. |
| `holds` | integer |  | A water container's capacity, in charges: the game's watering can holds 100, its pouch 20 sips. At most 1023: players' games are sent the water as charges x 64 in 16 bits. Default: the kind's own. min 1 max 1023 |
| `kind` | `"material"` \| `"consumable"` \| `"food"` \| `"ammo"` \| `"tool"` \| `"watering-can"` \| `"water-pouch"` |  | watering-can and water-pouch: the game's water containers; holds and flow are theirs. |
| `level` | [level](#level) |  |  |
| `loot` | [loot](#loot) |  |  |
| `name` | string | yes | min length 1 |
| `onEat` | [effects](#effects) |  | On whoever used it, once the item has really gone. |
| `onUse` | [effects](#effects) |  | What using it does, to whoever uses it. |
| `rarity` | [rarity](#rarity) |  |  |
| `recipe` | [recipe](#recipe) |  |  |
| `source` | string |  | Where to get it, in the tooltip. Not given: nothing. |
| `stack` | integer |  | How many fit in a stack. Default: the kind's own. A tool does not stack. min 1 max 65535 |
| `texts` | object |  | Texts per language (en, de, pt-BR, ...). |
| `tool` | [tool](#tool) |  |  |
| `tweak` | [tweak](#tweak) |  |  |
| `visual` | [visualRef](#visualref) |  |  |
| `was` | [name](#name) |  |  |

## itemRef

An item: a game item (Material_T1_Twigs), an item of this mod by its block name, or "other-mod:name". min length 1

Type: string

## keyBinding

A key that runs an action. The player can rebind it in the Kiln menu's Keys tab; the choice is kept in keybinds.json under <mod>:<name>. Bound on each player's game, so the mod needs "clients": "mod".

| Key | Type | Required | Notes |
|---|---|---|---|
| `category` | string |  | The group in the Keys tab. Default: the mod's name. min length 1 |
| `comment` | string |  |  |
| `description` | [uiText](#uitext) |  | What the Keys tab says. Default: the block's name. |
| `does` | [uiAction](#uiaction) | yes |  |
| `key` | string | yes | The default key: F8, Ctrl+K, Shift+Alt+F2, Numpad5, or None to bind it only once the player chooses. min length 1 |
| `onGameScreens` | boolean |  | A key that types something (a letter, a digit) fires in play only; true fires on every game screen too, chat included. |
| `was` | [name](#name) |  |  |
| `whileCapturing` | boolean |  | Also fire while the Kiln menu is open. |

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

## materialTweak

Changes a game material in place: what it takes to dig it, its pictures, or both.

| Key | Type | Required | Notes |
|---|---|---|---|
| `comment` | string |  |  |
| `material` | string | yes | The game material: a GameMaterials name, Cell<id>, or the material's GUID. |
| `strength` | [strengthRef](#strengthref) |  |  |
| `surface` | [buildingSurfaceRef](#buildingsurfaceref) |  | New pictures: a layer written here replaces the game's, the rest stays. Asks nothing of other players. |

## menu

The crafting menu a station opens: an existing station's, which lists the station's recipes among its own.

| Key | Type | Required | Notes |
|---|---|---|---|
| `like` | `"workbench"` \| `"carpenter"` \| `"kitchen"` \| `"forge"` \| `"oven"` \| `"alchemy"` \| `"drying-rack"` \| `"charcoal-kiln"` \| `"kiln"` \| `"smelter"` \| `"fireplace"` \| `"grinder"` \| `"tannery"` \| `"table-saw"` |  | Default: the station's kind. |

## name

The block's name. Ids come from your mod id and this: keep it. A rename says "was". Pattern `^[A-Za-z0-9_.-]+$`.

Type: string

## npc

A friendly NPC on a game NPC's body: talk, wares, a place in the roster.

| Key | Type | Required | Notes |
|---|---|---|---|
| `ai` | [ref](#ref) or [ai](#ai) |  |  |
| `animation` | [ref](#ref) or [animation](#animation) |  |  |
| `behaviour` | [ref](#ref) or [behaviour](#behaviour) |  |  |
| `body` | [ref](#ref) or [body](#body) | yes |  |
| `name` | string | yes | min length 1 |
| `names` | object |  |  |
| `place` | object |  |  |
| `roster` | object |  |  |
| `talk` | string |  | A dialogue of the mod's quests/*.json. |
| `vendor` | object |  |  |
| `was` | string |  |  |

## onHit

What an item does to what its wearer hits.

| Key | Type | Required | Notes |
|---|---|---|---|
| `effects` | [effects](#effects) | yes |  |
| `vfx` | [vfx](#vfx) |  | A VFX where each hit lands. |

## perk

An enhancement on an item, in its perks: it runs while the item is worn or in hand, once a player has unlocked that enhancement. A weapon's onHit is the same with no unlock.

| Key | Type | Required | Notes |
|---|---|---|---|
| `description` | string |  |  |
| `icon` | string |  | A game perk, whose icon it takes, or a PNG in the mod's folder (square, 256x256 as the game's; then clients "mod"). min length 1 |
| `modifies` | [modifies](#modifies) |  |  |
| `name` | string | yes | min length 1 |
| `onHit` | [hits](#hits) |  | What every hit they land does to what was hit. |
| `onHitTaken` | [hits](#hits) |  | What every hit they take does to whoever dealt it. |
| `onKill` | [hits](#hits) |  | What a hit that kills does to what it killed. |
| `tweak` | [tweak](#tweak) |  |  |
| `value` | string |  | The number the tooltip shows in front of the name: "5%", "12", "2.5", "8s". Default: what one modifies entry or one on-hit effect's chance says. Pattern `^\s*-?[0-9]+(\.[0-9]+)?\s*(%\|s)?\s*$`. |
| `was` | [name](#name) |  |  |

## perks

Enhancement slots, from the first: a game perk's name or GUID, item:<name> (that item's perk in the same slot), "none", or null to keep the slot's. The game's rule applies: a perk runs once its slot is unlocked. max items 5

Type: array of string \| null

## placed

What every delivery that is not a flight has: it happens where it is placed, facing where the caster aims.

| Key | Type | Required | Notes |
|---|---|---|---|
| `affects` | `"enemies"` \| `"friends"` |  | Default enemies. |
| `look` | `"ice-bolt"` \| `"fireball"` |  | On a spell, what shows in the hand. |
| `tweak` | [tweak](#tweak) |  |  |
| `vfx` | [vfx](#vfx) |  | A VFX where it happens, facing where it goes. |

## placement

Where and how a player can place it. A field left out: what its starter does.

| Key | Type | Required | Notes |
|---|---|---|---|
| `consumed` | boolean |  | Placing it takes the item from the stack. |
| `fog` | boolean |  | It can be placed below the Shroud's line. |
| `inBase` | boolean |  | Only inside a base. |
| `on` | [surface](#surface) |  |  |
| `size` | array of number |  | Metres, width, height, depth: the box that has to be free, standing on its floor. min items 3 max items 3 |
| `snap` | boolean |  | The cursor starts out snapping to the voxel grid. |
| `water` | boolean |  | It can stand in water. |

## processing

How a station works on its own. A field left out: what its kind does.

| Key | Type | Required | Notes |
|---|---|---|---|
| `goesOutInRain` | boolean |  | false: it never goes out. |
| `running` | object |  | What shows while it runs. |
| `slots` | integer |  | Inventory slots, inputs and outputs in one. min 1 max 64 |

## projectile

It flies. What it strikes gets onHit, and where it stops onImpact happens.

| Key | Type | Required | Notes |
|---|---|---|---|
| `gravity` | number |  | 0 flies straight, 1 falls like a thrown stone (default 0.1). |
| `homing` | object |  | It turns toward what it was aimed at, as the game's homing projectiles do. |
| `look` | `"ice-bolt"` \| `"fireball"` |  |  |
| `onHit` | [effects](#effects) |  | On the creature it strikes, if it struck one. |
| `onImpact` | [effects](#effects) |  | At the place it stops, which has no target of its own: use an area. |
| `pierce` | integer |  | How many creatures it flies through, each struck once (onHit on each), before the next one stops it. Needs onHit. Anything that is not a creature stops it at once. min 0 max 8 |
| `range` | number |  | Metres before it fizzles (default 150). |
| `speed` | number |  | Metres per second (default 30). |
| `tweak` | [tweak](#tweak) |  |  |
| `type` | `"projectile"` | yes |  |
| `vfx` | [vfx](#vfx) |  |  |

## prop

Something placed that stands there: furniture, a banner, a lamp.

| Key | Type | Required | Notes |
|---|---|---|---|
| `collider` | object |  | What a player bumps into and hits stop at: one box, in metres. Default with a glTF model: the box around the model. Only with a model of yours. |
| `comfort` | [ref](#ref) or [comfort](#comfort) |  |  |
| `description` | string |  |  |
| `light` | [vfx](#vfx) |  | A VFX on it for as long as it stands: where the game's lamps have their light. |
| `loot` | [loot](#loot) |  |  |
| `name` | string | yes | min length 1 |
| `onInteract` | [interactionRef](#interactionref) |  | What happens when a player uses it. |
| `placement` | [ref](#ref) or [placement](#placement) |  |  |
| `recipe` | [recipe](#recipe) |  | How the item that places it is made. |
| `tweak` | [tweak](#tweak) |  |  |
| `tweakPlaced` | [tweak](#tweak) |  | The same, set on the template it places (templates section paths). |
| `visual` | [visualRef](#visualref) | yes | Its look: like (a game item that places something: model, collision and how it places), or model (a glTF in the mod's folder, which kiln cook makes into the game's model; item:<name>; or the GUID of a model your assets/assets.json serves). |
| `was` | [name](#name) |  |  |

## rarity

What a made item is: its colour, and how many enhancements it can have (one more than the rank).

Type: `"common"` \| `"uncommon"` \| `"rare"` \| `"epic"` \| `"legendary"`

## recipe

How something is crafted.

| Key | Type | Required | Notes |
|---|---|---|---|
| `burns` | object |  | Fuel each cycle burns, item -> count. An input, as in the game's data: with none left the station stops. |
| `comfort` | integer |  | The comfort level it asks of whoever makes it. min 0 |
| `makes` | integer |  | How many one craft gives. For a spell: charges. min 1 max 65535 |
| `needs` | object |  | Item -> count: a game item, an item of this mod by its block name, or "other-mod:name". |
| `output` | [itemRef](#itemref) |  | What it makes, in a station's list of recipes. Elsewhere a recipe makes the block it is on. |
| `seconds` | number |  | Seconds at a station that works on its own. Needed at one with a workshop of its own. |
| `sheltered` | boolean |  | A player crafts it only under a roof. A station that works on its own never checks a roof, so its recipes refuse it. |
| `station` | string |  | "hand" (the default): the player's own menu; a workshop's 0x id; or a station block by name. In a station's list of recipes: that station, so leave it out. |

## ref

A block of this mod by name, or "other-mod:name". min length 1

Type: string

## requires

What it takes to use it. item, count, uses and time are the game's own lock, shown in the player's game; when and unless are checked by Kiln on the server.

| Key | Type | Required | Notes |
|---|---|---|---|
| `count` | integer |  | How many of item. Default 1. min 1 max 65535 |
| `item` | [itemRef](#itemref) |  | An item in the player's bag. |
| `time` | `"day"` \| `"night"` |  | Only by day, or only by night. |
| `unless` | [states](#states) |  | Not while the player is in any of these. |
| `uses` | boolean |  | The use takes item from the bag, as a lockpick goes. |
| `when` | [states](#states) |  | Only while the player is in one of these. |

## rule

On a trigger, every so often, when its conditions hold, do something.

| Key | Type | Required | Notes |
|---|---|---|---|
| `chance` | number |  | 0..1. min 0 max 1 |
| `cooldown` | number |  | Seconds. min 0 |
| `do` | array of [action](#action) | yes | min items 1 |
| `every` | number |  | Seconds. min 0 max 3600 |
| `name` | string |  |  |
| `on` | `"onUpdate"` \| `"onEnter"` \| `"onExit"` \| `"onStartAttack"` \| `"onEndAttack"` \| `"onEnterState"` \| `"onExitState"` \| `"OnUpdate"` \| `"OnEnter"` \| `"OnExit"` \| `"OnStartAttack"` \| `"OnEndAttack"` \| `"OnEnterState"` \| `"OnExitState"` |  |  |
| `when` | array of [condition](#condition) |  |  |

## screenColor

"#rrggbb", or { "color": "#rrggbb", "amount": 0.5 }.

Type: string or object

## screenEffect

A full-screen effect. Every field left out changes nothing. Graded in linear light: exposure and temperature, contrast, saturation, gamma, tint, vignette, fill; then mixed in by strength and the fades. Drawn over the frame the game presents, so over the game's UI too.

| Key | Type | Required | Notes |
|---|---|---|---|
| `contrast` | number |  | Around mid grey. Default 1. min 0.05 max 4 |
| `exposure` | number |  | Stops: 1 is twice as bright. Default 0. min -8 max 8 |
| `fadeIn` | number |  | Seconds to reach full. Default 0. min 0 max 60 |
| `fadeOut` | number |  | Seconds to fade once it ends or is stopped. Default 0. min 0 max 60 |
| `fill` | [screenColor](#screencolor) |  | The whole picture goes to this colour by its amount: a fade to black, or a flash with a fade out. |
| `gamma` | number |  | Above 1 lifts the mid tones. Default 1. min 0.1 max 5 |
| `order` | integer |  | Several playing at once run lower first. |
| `range` | number |  | Metres: when an effect plays it at a place, the players this near see it. Default 30. |
| `saturation` | number |  | 0 is grey. Default 1. min 0 max 4 |
| `seconds` | number |  | Seconds at full. Left out: until stopped; an effect's screen effect needs it. 0 with a fade out is a flash. min 0 max 3600 |
| `strength` | number |  | How much of the whole effect shows. Default 1. min 0 max 1 |
| `temperature` | number |  | -1 cold to 1 warm, keeping brightness. Default 0. min -1 max 1 |
| `tint` | [screenColor](#screencolor) |  | A colour the picture is multiplied by. |
| `vignette` | number or object |  | Darker or coloured edges: how much (0.6), or { amount, color, radius, softness }. |
| `was` | [name](#name) |  | The name this block had before a rename. |

## screenRef

A screen effect: its name, or one written here.

Type: [ref](#ref) or [screenEffect](#screeneffect)

## seat

A seat: the game's sitting. Starts from the game's wooden chair; visual.like names another.

| Key | Type | Required | Notes |
|---|---|---|---|
| `comfort` | [ref](#ref) or [comfort](#comfort) |  |  |
| `description` | string |  |  |
| `light` | [vfx](#vfx) |  | A VFX on it for as long as it stands: where the game's lamps have their light. |
| `loot` | [loot](#loot) |  |  |
| `name` | string | yes | min length 1 |
| `onInteract` | [interactionRef](#interactionref) |  | What happens when a player uses it. |
| `placement` | [ref](#ref) or [placement](#placement) |  |  |
| `recipe` | [recipe](#recipe) |  | How the item that places it is made. |
| `sits` | [spot](#spot) |  | Where the player sits. Default: the starter's spot. |
| `tweak` | [tweak](#tweak) |  |  |
| `tweakPlaced` | [tweak](#tweak) |  | The same, set on the template it places (templates section paths). |
| `visual` | [visualRef](#visualref) |  | Its look: like (a game item that places something: model, collision and how it places), or model (item:<name>, or the GUID of a model your assets/assets.json serves). |
| `was` | [name](#name) |  |  |

## sound

A sound of the mod's own: one or more clips (each play picks one), how loud and how far it carries.

| Key | Type | Required | Notes |
|---|---|---|---|
| `clips` | array of string | yes | Audio files in the mod's folder: WAV, FLAC, Ogg Vorbis, MP3. Mono for a sound with a range. min items 1 |
| `comment` | string |  |  |
| `loop` | boolean |  | It plays until stopped: a station's running sound, an ambience. |
| `normalize` | number |  | The sources' active RMS in dBFS, as the cook sets it (the game's effects: about -23). max 0 |
| `pitchRandomness` | number |  | How much each play's pitch varies. min 0 max 1 |
| `range` | array of number |  | [min, max] metres: full volume within min, silent beyond max. Without: heard alike everywhere (UI, music). min items 2 max items 2 |
| `scene` | `"Sfx"` \| `"Music"` \| `"Ui"` \| `"Voice"` |  | The player's volume slider it follows. |
| `volume` | number |  | The cue's volume. Default 1. min 0 max 1 |

## spell

| Key | Type | Required | Notes |
|---|---|---|---|
| `cast` | [cast](#cast) | yes |  |
| `delivery` | [delivery](#delivery) | yes |  |
| `description` | string |  |  |
| `level` | integer |  | min 1 max 55 |
| `maxCharges` | integer |  | Charges in one stack (default 500). min 1 max 65535 |
| `name` | string | yes | min length 1 |
| `onCast` | [effects](#effects) |  | On the caster, the moment it leaves the staff. |
| `recipe` | [recipe](#recipe) |  |  |
| `tweak` | [tweak](#tweak) |  |  |
| `was` | [name](#name) |  |  |

## spot

Where the player sits or lies: metres from the thing's origin in its own axes, and which way they face, in degrees.

| Key | Type | Required | Notes |
|---|---|---|---|
| `at` | array of number |  | min items 3 max items 3 |
| `facing` | number |  |  |

## state

One of the game's player states or actions, by Kiln's name or the game's own (Gliding, HangGliding, ActionPrimary, Sitting, ...): docs/kiln/modding/player-states.md. A trigger also takes eating and eaten. min length 1

Type: string

## states

min items 1

Type: array of [state](#state)

## station

A station a player builds: a prop that makes things. With processing it works on its own, over time, from its own inventory, like a forge; without, a player crafts at it at once, like a workbench.

| Key | Type | Required | Notes |
|---|---|---|---|
| `comfort` | [ref](#ref) or [comfort](#comfort) |  |  |
| `description` | string |  |  |
| `kind` | `"workbench"` \| `"carpenter"` \| `"kitchen"` \| `"forge"` \| `"oven"` \| `"alchemy"` \| `"drying-rack"` \| `"charcoal-kiln"` \| `"kiln"` \| `"smelter"` \| `"fireplace"` \| `"grinder"` \| `"tannery"` \| `"table-saw"` |  | The game's station it starts from: model, collision, sound and the menu it opens. Default: drying-rack with processing, else workbench. |
| `light` | [vfx](#vfx) |  | A VFX on it for as long as it stands: where the game's lamps have their light. |
| `loot` | [loot](#loot) |  |  |
| `menu` | [ref](#ref) or [menu](#menu) |  |  |
| `name` | string | yes | min length 1 |
| `onInteract` | [interactionRef](#interactionref) |  | What happens when a player uses it. |
| `ownWorkshop` | boolean |  | A workshop of its own, which decides which recipes it runs. Default: yes for a kind that works on its own, with processing or recipes. |
| `placement` | [ref](#ref) or [placement](#placement) |  |  |
| `processing` | [ref](#ref) or [processing](#processing) |  |  |
| `recipe` | [recipe](#recipe) |  | How the item that places it is made. |
| `recipes` | array of [recipe](#recipe) |  | What it makes, each with output. |
| `tweak` | [tweak](#tweak) |  |  |
| `tweakPlaced` | [tweak](#tweak) |  | The same, set on the template it places (templates section paths). |
| `visual` | [visualRef](#visualref) |  | Its look: like (a game item that places something: model, collision and how it places), or model (item:<name>, or the GUID of a model your assets/assets.json serves). |
| `was` | [name](#name) |  |  |

## status

A buff or debuff, shown in the game's buff bar. It lasts its seconds, or while a player is in a state.

| Key | Type | Required | Notes |
|---|---|---|---|
| `description` | string |  |  |
| `icon` | string |  | The icon, when it is not the look's: a game status by its id, whose icon it takes, or a PNG in the mod's folder (square, 256x256 as the game's; then clients "mod"). |
| `kind` | `"debuff"` \| `"buff"` |  |  |
| `look` | `"frost"` \| `"poison"` \| `"fire"` |  | Its icon and body effect. Default: a timer's icon for a debuff, a blessing's for a buff, and no body effect. |
| `modifies` | object |  | Stats changed for as long as it runs. Whole-number stats take whole numbers; the rest are shares, 0.1 being 10%. A GUID names any other attribute (as a share). |
| `name` | string | yes | min length 1 |
| `onApply` | [effects](#effects) |  |  |
| `onExpire` | [effects](#effects) |  |  |
| `onTick` | [effects](#effects) |  |  |
| `refreshes` | boolean |  | Applied again while it runs: the timer starts over (true, the default), or the new one replaces the old. |
| `screen` | [screenRef](#screenref) |  | A full-screen effect on the screen of the player who has it, while they have it. |
| `seconds` | number |  | How long it lasts unless whoever applies it says otherwise. Not needed with while. |
| `tickEvery` | number |  | Seconds between onTick (default 1). |
| `tweak` | [tweak](#tweak) |  |  |
| `vfx` | [vfx](#vfx) |  |  |
| `was` | [name](#name) |  | The name this block had before a rename. Ids come from the oldest name, so saves keep it. |
| `while` | [state](#state) |  | A player has it for as long as they are in this state. |

## strength

What it takes to dig a material. Cost per block = health / (the tool's damage for this hardness x the server's mining factor x the class modifier). A field left out stays what it was.

| Key | Type | Required | Notes |
|---|---|---|---|
| `breaks` | `"none"` \| `"wood"` \| `"stone"` \| `"metal"` |  | The tool's class modifier that applies. none: 1.0, whatever the tool. |
| `drops` | string |  | The loot item: a game item's name or a GUID, or on a new material "block": its own block item. A new material that doesn't say gives back its recipe's first input at the recipe's ratio, as the game's blocks do. |
| `hardness` | `"soft"` \| `"slightlyHard"` \| `"moderatelyHard"` \| `"hard"` \| `"veryHard"` \| `"unbreakable"` |  | The damage column a tool or an explosion uses. Unbreakable takes no damage from anything, and can't be dismantled. |
| `health` | integer |  | Health points of a block. Rough Stone is 250, an Iron Block 2000. min 1 max 1000000 |
| `itemsPerBlocks` | integer |  | How many blocks dug give one loot item. min 1 max 100000 |

## strengthRef

A strength, inline or by name.

Type: string or [strength](#strength)

## surface

What it sits on: floor, wall, ceiling, or several ("wall, ceiling"): the first is tried first. Pattern `^(floor\|wall\|ceiling)(, ?(floor\|wall\|ceiling))*$`.

Type: string

## surfaceColor

#rrggbb, sRGB. Pattern `^#[0-9a-fA-F]{6}$`.

Type: string

## surfaceLayer

One layer of a surface: maps of your own, a game material's layer ("game", "layer") with maps of yours on top, or the name of another layer of this surface.

Type: [surfacePart](#surfacepart) or object

## surfacePng

A PNG in the mod's folder, a power of two on both sides, 4 to 4096. Pattern `^[^/\\].*\.[pP][nN][gG]$`. A file that must exist.

Type: string

## surfaceValue

A PNG (its red channel) or a value 0 to 1.

Type: [surfacePng](#surfacepng) or number

## talent

A node: in the game's own tree with game, or in a talent tree. It works for as long as a character has it.

| Key | Type | Required | Notes |
|---|---|---|---|
| `characterLevel` | integer |  | In a tree: the character level needed. min 0 |
| `column` | integer |  | min 0 max 31 |
| `cost` | integer |  | Points per level (default 1). min 0 |
| `description` | string |  | Default: what modifies says. |
| `game` | object |  | A place in the game's own tree: its screen draws it, its skill points pay for it. |
| `icon` | string |  | A PNG in the mod's folder. Pattern `\.[pP][nN][gG]$`. |
| `keeps` | [statusRef](#statusref) |  | A status they have for as long as they have the talent. |
| `maxLevel` | integer |  | Levels (default 1). The game's tree keeps at most 7. min 1 max 99 |
| `modifies` | [modifies](#modifies) |  | Stats changed, per level. |
| `name` | string | yes | min length 1 |
| `onHit` | [hits](#hits) |  |  |
| `onHitTaken` | [hits](#hits) |  |  |
| `onKill` | [hits](#hits) |  |  |
| `onStart` | [effects](#effects) |  | On them when it starts to work: at the unlock and at each join. |
| `pointsSpent` | integer |  | In a tree: points spent in it first. min 0 |
| `requireAll` | boolean |  | In a tree: all of requires, not any one. |
| `requires` | array of string |  | In a tree: its node names. In the game's tree: talents of this mod by name, or game nodes by 0x id. |
| `row` | integer |  | min 0 max 31 |
| `texts` | [texts](#texts) |  |  |
| `unlocks` | [ref](#ref) |  | An ability only those with this talent can cast. |
| `was` | [name](#name) |  |  |

## talentTree

A whole tree on Kiln's screen in the game's look. Kiln keeps each character's unlocks with the character.

| Key | Type | Required | Notes |
|---|---|---|---|
| `description` | string |  |  |
| `key` | string |  | min length 1 |
| `menu` | string |  | Its pause-menu entry. Default: name. min length 1 |
| `name` | string | yes | min length 1 |
| `nodes` | object | yes | Node name -> a talent, inline or by name. Keep the names: saves hold them. |
| `points` | object |  | Where its points come from (default 1, and 1 more per character level). |
| `respec` | boolean |  | Everything can be given back for free (default). |
| `was` | [name](#name) |  |  |

## terrainMaterial

A new terrain material: it starts from a game terrain material and looks like one.

| Key | Type | Required | Notes |
|---|---|---|---|
| `comment` | string |  |  |
| `description` | string |  |  |
| `flatten` | boolean |  | The game's flatten tool works on it. Default: like's. |
| `icon` | string |  | The item's picture: a PNG in the mod's folder, or item:<name>. Default: the game's stone's. min length 1 |
| `like` | string |  | The game terrain material it starts from: a GameMaterials Terrain name or Cell<1..127>. Its strength, loot, sounds, friction and buffs come along. Default TerrainStone. min length 1 |
| `look` | string |  | The game terrain material whose look it takes. Default: like's. min length 1 |
| `name` | string | yes | What players read: its item's name when it's placeable. min length 1 |
| `placeable` | boolean |  | It has an item the game's terrain shapes place, and digging gives it back. Default false: only found and dug, as the game's ores. |
| `recipe` | [recipe](#recipe) |  | How its item is made; needs placeable. |
| `sliding` | boolean |  | Feet slide on it, as on ice. Default: like's. |
| `strength` | [strengthRef](#strengthref) |  |  |
| `was` | string |  | The block's old name, after a rename: the material's id comes from it, so its cells stay in every world. |

## texts

Texts per language (en, de, pt-BR, ...).

Type: object

## tool

What a tool does: what it is for, and how well. Each class takes the fields it has a number for.

| Key | Type | Required | Notes |
|---|---|---|---|
| `breaksInto` | [itemRef](#itemref) |  | What it becomes when its durability runs out: a game item or an item of this mod. A torch, or a tool with enhancement slots. |
| `durability` | number |  | Seconds a torch burns while held, the tooltip's Duration (600). Only a torch. |
| `fishing` | object |  | A fishing rod's pull and stamina. |
| `harvests` | `"wood"` \| `"stone"` \| `"rake"` \| `"sieve"` \| `"glider"` \| `"grappling-hook"` \| `"fishing-rod"` \| `"torch"` | yes | wood: an axe. stone: a pickaxe, for stone and metal, and it digs. rake: flattens the ground. sieve: the mining sieve. glider, grappling-hook, fishing-rod, torch: the game's. |
| `power` | integer |  | How hard it bites: an axe's wood damage, a pickaxe's or a sieve's stone damage (the rest scales along), a rake's flattening strength (the game's: 500, 1000). |
| `range` | number |  | Metres: how far a rake flattens around where it aims (3), how far a grappling hook reaches (13.5). max 32 |
| `staminaCost` | integer |  | Stamina each use costs; the tooltip follows. Not for a fishing rod or a torch. min 0 |

## trigger

A state entered, an action started, or a consumable eaten, driving effects on the player. Runs on the server world.

| Key | Type | Required | Notes |
|---|---|---|---|
| `cooldown` | number |  | Seconds, per player. min 0 |
| `do` | [effects](#effects) |  |  |
| `item` | [itemRef](#itemref) |  | Only with this item: what an action uses, what is eaten, or for a state what is in hand or worn. |
| `on` | [state](#state) | yes |  |
| `onEnd` | [effects](#effects) |  | When they leave the state. A state only. |
| `stop` | boolean |  | Stops the action, or the eating. |
| `unless` | [states](#states) |  | Not while the player is in any of these. |
| `was` | [name](#name) |  |  |
| `when` | [states](#states) |  | Only while the player is in one of these. |

## tweak

Game fields this block does not cover, by the path a data pack writes, set on what it makes. They win over what the block sets.

Type: object

## uiAction

What a button or a key does: exactly one of command, toggle, set, castAbility, applyEffect, openWindow, closeWindow, toast. Runs on the player's game, never on a dedicated server.

| Key | Type | Required | Notes |
|---|---|---|---|
| `applyEffect` | [effectRef](#effectref) |  | Applies an effect to the player, through a hidden ability named ui-apply.<name>. The server needs the mod. |
| `castAbility` | [ref](#ref) |  | Asks the server to cast an ability of this mod (or "other-mod:name"), as its key would. The server needs the mod. |
| `closeWindow` | [ref](#ref) |  | Closes a window block. |
| `command` | string |  | A command line of this game's command registry, as the console runs it: as the host's own character when this game hosts the world, else as the console. It does not reach a server this game joined. min length 1 max length 256 |
| `openWindow` | [ref](#ref) |  | Opens a window block of this mod (or "other-mod:name"). |
| `set` | [uiSet](#uiset) |  |  |
| `toast` | [uiText](#uitext) |  | A short message in the corner of the screen. |
| `toggle` | [uiBind](#uibind) |  | Flips a true/false config entry. |

## uiBind

A value: game.<name> (fps, frameMs, state, inGame, screen, language, clock) or config.<path> of this mod's config (config.<config id>/<path> when two configs have the path). Pattern `^(game\|config)\.[A-Za-z0-9_.\-/]+$`.

Type: string

## uiColor

#rrggbb, #rrggbbaa, or white, black, gray, red, green, yellow, orange, cyan.

Type: string

## uiMenu

An entry in the game's pause menu and/or main menu that opens the window.

| Key | Type | Required | Notes |
|---|---|---|---|
| `in` | `"pause"` \| `"main"` \| `"both"` |  | Default pause. |
| `order` | integer |  | Lower first. Kiln's own Mods entry has 1000. |
| `text` | [uiText](#uitext) | yes |  |

## uiRow

One line of a window. "type" says which: text, value, toggle, slider, choice, separator, button.

Type: [uiRowText](#uirowtext) or [uiRowValue](#uirowvalue) or [uiRowToggle](#uirowtoggle) or [uiRowSlider](#uirowslider) or [uiRowChoice](#uirowchoice) or [uiRowSeparator](#uirowseparator) or [uiRowButton](#uirowbutton)

## uiRowButton

A button that runs an action.

| Key | Type | Required | Notes |
|---|---|---|---|
| `does` | [uiAction](#uiaction) | yes |  |
| `label` | [uiText](#uitext) | yes |  |
| `sameLine` | boolean |  | On the same line as the row before it. |
| `tooltip` | [uiText](#uitext) |  | Shown while the pointer rests on the row's widget. May show values like a text row does. |
| `type` | `"button"` | yes |  |
| `when` | [uiWhen](#uiwhen) |  |  |

## uiRowChoice

A drop-down on an enum config entry, with its options.

| Key | Type | Required | Notes |
|---|---|---|---|
| `bind` | [uiBind](#uibind) | yes |  |
| `label` | [uiText](#uitext) | yes |  |
| `sameLine` | boolean |  | On the same line as the row before it. |
| `tooltip` | [uiText](#uitext) |  | Shown while the pointer rests on the row's widget. May show values like a text row does. |
| `type` | `"choice"` | yes |  |
| `when` | [uiWhen](#uiwhen) |  |  |

## uiRowSeparator

A line, with a heading when it has text.

| Key | Type | Required | Notes |
|---|---|---|---|
| `sameLine` | boolean |  | On the same line as the row before it. |
| `text` | [uiText](#uitext) |  |  |
| `tooltip` | [uiText](#uitext) |  | Shown while the pointer rests on the row's widget. May show values like a text row does. |
| `type` | `"separator"` | yes |  |
| `when` | [uiWhen](#uiwhen) |  |  |

## uiRowSlider

A slider on a whole-number or decimal config entry. A drag writes the config once it rests for a moment.

| Key | Type | Required | Notes |
|---|---|---|---|
| `bind` | [uiBind](#uibind) | yes |  |
| `label` | [uiText](#uitext) | yes |  |
| `max` | number |  |  |
| `min` | number |  | Default: the entry's own range, which it must have when min and max are left out. |
| `sameLine` | boolean |  | On the same line as the row before it. |
| `tooltip` | [uiText](#uitext) |  | Shown while the pointer rests on the row's widget. May show values like a text row does. |
| `type` | `"slider"` | yes |  |
| `when` | [uiWhen](#uiwhen) |  |  |

## uiRowText

A line of text. {game.fps:0} and {config.clock.scale:0.00} in it show a value (refreshed at the window's pace); {{ and }} are braces.

| Key | Type | Required | Notes |
|---|---|---|---|
| `color` | [uiColor](#uicolor) |  |  |
| `sameLine` | boolean |  | On the same line as the row before it. |
| `text` | [uiText](#uitext) | yes |  |
| `tooltip` | [uiText](#uitext) |  | Shown while the pointer rests on the row's widget. May show values like a text row does. |
| `type` | `"text"` | yes |  |
| `when` | [uiWhen](#uiwhen) |  |  |
| `wrap` | boolean |  | Break long text at the window's edge. The overlay's wrapped text has no colour: not with color. |

## uiRowToggle

A checkbox on a true/false config entry. A click writes the config.

| Key | Type | Required | Notes |
|---|---|---|---|
| `bind` | [uiBind](#uibind) | yes |  |
| `label` | [uiText](#uitext) | yes |  |
| `sameLine` | boolean |  | On the same line as the row before it. |
| `tooltip` | [uiText](#uitext) |  | Shown while the pointer rests on the row's widget. May show values like a text row does. |
| `type` | `"toggle"` | yes |  |
| `when` | [uiWhen](#uiwhen) |  |  |

## uiRowValue

A label and a value that cannot be edited here: Label: value.

| Key | Type | Required | Notes |
|---|---|---|---|
| `bind` | [uiBind](#uibind) | yes |  |
| `format` | string |  | How a number shows ("0", "0.00", "#,##0") or a true/false ("yes\|no"). min length 1 |
| `label` | [uiText](#uitext) | yes |  |
| `sameLine` | boolean |  | On the same line as the row before it. |
| `tooltip` | [uiText](#uitext) |  | Shown while the pointer rests on the row's widget. May show values like a text row does. |
| `type` | `"value"` | yes |  |
| `when` | [uiWhen](#uiwhen) |  |  |

## uiSet

A config entry and the value to give it: true or false, a number in its range, or an option of an enum.

| Key | Type | Required | Notes |
|---|---|---|---|
| `bind` | [uiBind](#uibind) | yes |  |
| `to` | boolean \| number \| string | yes |  |

## uiText

A text: plain, or one per language ({ "en": "Save", "de": "Speichern" }, "*" for every other language). The game's language picks it: the exact key ("pt-br"), the primary language ("pt"), then "en", then "*".

Type: string or object

## uiWhen

Shown only while this is true: a name (game.inGame, config.clock.show: true, not 0, not empty), !name, or name == value with == != < <= > >=, joined with &&. Examples: "game.inGame", "!game.inGame", "game.screen == InGame", "game.fps < 30 && config.hud.on". min length 1

Type: string

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
| `vfx` | [vfx](#vfx) |  | A VFX on the thing itself while it is there: in the hand while held (right hand, at the grip, by default), or on what stands there for a placed thing. |

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
| `source` | string |  | Where to get it, in the tooltip. Not given: nothing. |
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
| `delivery` | [delivery](#delivery) | yes |  |
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
| `source` | string |  | Where to get it, in the tooltip. Not given: nothing. |
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

## window

A window or a screen of rows: text, values, buttons, toggles, sliders, choices and separators. Drawn by each player's game from the mod's own file, so the mod needs "clients": "mod"; on a dedicated server it is checked and kept, and nothing is drawn. What it shows refreshes every "refresh" seconds, not every frame; a closed window costs nothing.

| Key | Type | Required | Notes |
|---|---|---|---|
| `alpha` | number |  | A window: how opaque the background is. min 0 max 1 |
| `autoResize` | boolean |  | A window: it grows and shrinks with its rows. |
| `comment` | string |  |  |
| `dim` | boolean |  | A screen: darken the game behind it (default true). |
| `height` | number |  | Pixels at UI scale 1. 0: a window fits its rows, a screen is 520 high. min 0 max 4096 |
| `interactive` | boolean |  | A window: false gives a panel the player only looks at (default true). |
| `kind` | `"window"` \| `"screen"` |  | window (default): an overlay window the player can drag. screen: a full screen in the game's look, dimming the game, closed by Escape. |
| `menu` | [uiMenu](#uimenu) |  |  |
| `openAtStart` | boolean |  | Open as soon as the game has it. Default: closed until a key, a button or a menu entry opens it. |
| `position` | array of number |  | A window: where its centre is, [x, y] as 0..1 of the screen. min items 2 max items 2 |
| `refresh` | number |  | Seconds between updates of the values and conditions it shows. Default 0.25. min 0.1 max 3600 |
| `rows` | array of [uiRow](#uirow) | yes | min items 1 max items 200 |
| `title` | [uiText](#uitext) | yes |  |
| `titleBar` | boolean |  | Default true. |
| `width` | number |  | Pixels at UI scale 1. 0: a window fits its rows, a screen is 720 wide. min 0 max 4096 |

## wornLook

What something looks like on the body: a model of your own or a game item's, what it hides and how the rest of the character fits around it.

| Key | Type | Required | Notes |
|---|---|---|---|
| `attach` | string |  | A rigid model: a mesh with no skin, on this slot or j<index>. Its origin is the joint. |
| `female` | [wornModel](#wornmodel) |  | A second file for a female body, instead of a morph target. |
| `fits` | [fits](#fits) |  |  |
| `hides` | array of [bodyPart](#bodypart) |  | What is not drawn while this is worn. Nothing else is hidden. |
| `icon` | string |  | A PNG in the mod's folder, or item:<name>. Default: the game draws the model. |
| `like` | [itemRef](#itemref) |  | A game item, or an item of this mod, whose whole look this takes. What else the look says wins. |
| `lods` | array of string |  | Coarser files of model, from the next level on. |
| `materials` | object |  | A glTF material's name, or "*" for every one, to a material block's name or a game material's GUID. One it leaves out becomes a block of its own. |
| `model` | [wornModel](#wornmodel) |  | Skinned on the player's skeleton, or a plain mesh with attach. A morph target named Female is the shape on a female body. |
| `pixelSizes` | array of number |  | When each level shows: one per level, 0 first, rising. |
| `variants` | object |  | Whole models for what the character wears or is: "shield", "female+tight". The most specific that applies is drawn, and model otherwise. |

## wornLookRef

A worn look: inline, or a block of wornLooks by name.

Type: [ref](#ref) or [wornLook](#wornlook)

## wornModel

A .glb or .gltf in the mod's folder, item:<name> for a game item's model, or the GUID of a model the mod's assets.json serves.

Type: string

