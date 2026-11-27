# The `balance` registry

<!-- Generated from schema/registry/balance.json by tools/registrygen/registrygen.py. Do not edit. -->

The game's balance table: experience and levels, the flame's altars and water sources, base fog resistance, durability, sockets and perk unlock costs. The game reads it once at start.

Schema version 0.1.0, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.BalanceEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

Edits per call: 4,096. One `OnRegistry` callback (one phase of one entry point) or one `Registry.Change` takes at most this many edits of this registry; a call with more is refused whole, with the reason in kiln.log and registry.json, and nothing of it applies (the other calls do). The default for a registry whose schema declares none (4,096).

## Ids

- Game entries: `game:balance`. Mod entries: `(none: one entry)`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- One entry, game:balance.

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

Groups of fields:

- `xpCurve`: The experience a level from 6 needs: base = min(start, end) + |end - start| * level / levelMax; need = round(base * seed * 1.5 + fromKnowledge).
- `fogResistance`: A player's shroud time.
- `durability`: An item's maximum durability: (start + growth * (level - 1)) * rarityDurability[rarity]. Each copy computes it when it is loaded.
- `rarityDurability`: The durability factor of each rarity.
- `socketChance`: The chance a new weapon of each rarity gets a gem socket (0 to 1).
- `unlockCost`: What unlocking a perk costs.
- `gear`: The constants of the power and armour formulas (items.power, items.armor). Read-only in this version: Kiln's solver follows them.

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `xpPerLevel` | list of int 0..100000000, exactly 5 | restart | indexed | 0.1.0 | mapped | restart | The experience levels 1 to 5 need (later levels follow xpCurve). Exactly 5 values, edited one at a time (xpPerLevel[i]). |
| `xpCurve.start` | int 0..100000000 | restart | number | 0.1.0 | mapped | restart | One end of the curve's base range (base = min(start, end) + /end - start/ * level / levelMax). |
| `xpCurve.end` | int 0..100000000 | restart | number | 0.1.0 | mapped | restart | The other end of the curve's base range. |
| `xpCurve.levelMax` | int 1..100000 | restart | number | 0.1.0 | mapped | restart | The level at which the base has moved the whole way from one end of the range to the other. |
| `xpCurve.seed` | int 0..100000000 | restart | number | 0.1.0 | mapped | restart | The factor the base is multiplied by (with 1.5) to give the experience a level needs. |
| `xpCurve.fromKnowledge` | int 0..100000000 | restart | number | 0.1.0 | mapped | restart | Experience added to every level's need after the factor. |
| `levelCap` | int 1..1000 | restart | number | 0.1.0 | mapped | restart | The highest player level. |
| `skillPointsPerLevel` | int 0..1000 | restart | number | 0.1.0 | mapped | restart | Skill points a level gives. |
| `flameLevelMax` | int 0..1000 | restart | read-only | 0.1.0 | mapped | restart | The highest flame level. Read-only in this version: the per-flame-level lists are exactly flameLevelMax + 1 long. |
| `altarsPerFlameLevel` | list of int 0..16, as long as flameLevelMax + 1 | restart | indexed | 0.1.0 | mapped | restart | Flame altars allowed at each flame level, one value per level from 0 to flameLevelMax (the game's code stops at 16). Edited one level at a time (altarsPerFlameLevel[i]). |
| `waterSourcesPerFlameLevel` | list of int 0..255, as long as flameLevelMax + 1 | restart | indexed | 0.1.0 | mapped | restart | Water sources allowed at each flame level, one value per level from 0 to flameLevelMax. Edited one level at a time. |
| `buildzoneSizesPerAltarLevel` | list of entries, at most 1001 | restart | list | 0.1.0 | mapped | restart | The size of an altar's build zone at each altar level, in metres. |
| `buildzoneSizesPerAltarLevel[].x` | float m 0..100000 | restart | number | 0.1.0 | mapped | restart | The build zone's size along x, in metres. |
| `buildzoneSizesPerAltarLevel[].y` | float m 0..100000 | restart | number | 0.1.0 | mapped | restart | The build zone's size along y (height), in metres. |
| `buildzoneSizesPerAltarLevel[].z` | float m 0..100000 | restart | number | 0.1.0 | mapped | restart | The build zone's size along z, in metres. |
| `fogResistance.base` | int 0..100000 | restart | number | 0.1.0 | mapped | restart | A player's shroud time before any flame level. |
| `fogResistance.perFlameLevel` | int 0..100000 | restart | number | 0.1.0 | mapped | restart | The shroud time each flame level adds. |
| `durability.start` | float 0..1000000 | restart | number | 0.1.0 | mapped | restart | The maximum durability of a level 1 item, before the rarity factor. |
| `durability.growth` | float 0..1000000 | restart | number | 0.1.0 | mapped | restart | The maximum durability each item level above 1 adds, before the rarity factor. |
| `rarityDurability.common` | float 0..100 | restart | number | 0.1.0 | mapped | restart | The durability factor of common items. |
| `rarityDurability.uncommon` | float 0..100 | restart | number | 0.1.0 | mapped | restart | The durability factor of uncommon items. |
| `rarityDurability.rare` | float 0..100 | restart | number | 0.1.0 | mapped | restart | The durability factor of rare items. |
| `rarityDurability.epic` | float 0..100 | restart | number | 0.1.0 | mapped | restart | The durability factor of epic items. |
| `rarityDurability.legendary` | float 0..100 | restart | number | 0.1.0 | mapped | restart | The durability factor of legendary items. |
| `socketChance.common` | float 0..1 | restart | number | 0.1.0 | mapped | restart | The chance a new common weapon gets a gem socket. |
| `socketChance.uncommon` | float 0..1 | restart | number | 0.1.0 | mapped | restart | The chance a new uncommon weapon gets a gem socket. |
| `socketChance.rare` | float 0..1 | restart | number | 0.1.0 | mapped | restart | The chance a new rare weapon gets a gem socket. |
| `socketChance.epic` | float 0..1 | restart | number | 0.1.0 | mapped | restart | The chance a new epic weapon gets a gem socket. |
| `socketChance.legendary` | float 0..1 | restart | number | 0.1.0 | mapped | restart | The chance a new legendary weapon gets a gem socket. |
| `unlockCost.seed` | int 0..100000 | restart | number | 0.1.0 | mapped | restart | The base cost of a perk unlock, before the creep and the kind's factor. |
| `unlockCost.creep` | float 0..100 | restart | number | 0.1.0 | mapped | restart | How much the cost grows with each unlock step, as a factor of the base. |
| `unlockCost.weapons` | float 0..100 | restart | number | 0.1.0 | mapped | restart | The cost factor of weapon and shield perks. |
| `unlockCost.armor` | float 0..100 | restart | number | 0.1.0 | mapped | restart | The cost factor of armour perks. |
| `unlockCost.rings` | float 0..100 | restart | number | 0.1.0 | mapped | restart | The cost factor of ring perks. |
| `unlockCost.advanced` | float 0..100000 | restart | number | 0.1.0 | mapped | restart | The numerator of an advanced perk's cost. |
| `unlockCost.advancedFrom` | int 0..5 | restart | number | 0.1.0 | mapped | restart | The perk level (0 to 5) from which the advanced cost applies. |
| `gear.seed` | float 0..100000 | restart | read-only | 0.1.0 | mapped | serverOnly | The base of the damage power formula: base power = int(seed * creep * level + seed). |
| `gear.creep` | float 0..100 | restart | read-only | 0.1.0 | mapped | serverOnly | The share of seed the base power grows by with each level. |
| `gear.armorOffset` | float 0..100000 | restart | read-only | 0.1.0 | mapped | serverOnly | The armour formula's offset: armour = round((armorGradient * level + armorOffset) * share). |
| `gear.armorGradient` | float 0..100000 | restart | read-only | 0.1.0 | mapped | serverOnly | The armour each level adds in the armour formula, before the item's share. |

## Entries

- Add (stage add, id `<your mod id>:<name>`): no.
- Remove: no.
- Raw game paths in edits (the Advanced tier): allowed.
