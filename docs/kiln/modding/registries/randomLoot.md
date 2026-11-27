# The `randomLoot` registry

<!-- Generated from schema/registry/randomLoot.json by tools/registrygen/registrygen.py. Do not edit. -->

The random loot tables: for each kind of enemy, generic container (barrels, crates, generic chests) and chest placed in the world, the level its random loot rolls at and its own rarity weights. Which items can drop is the loot registry's; the world-wide rarity weights are lootRules'. Where a table is used shows in its id: game:labels_* tables serve enemies and generic containers, game:world_* tables the chests placed in the world.

Schema version 0.1.0, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.RandomLootEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

## Ids

- Game entries: `game:labels_<8 hex digits> or game:world_<8 hex digits>_<8 hex digits>`. Mod entries: `(none: tables are not added)`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- Random loot tables have no names in the game. A table that enemies and generic containers use is game:labels_<its 32-bit id as 8 hex digits>; a table of the chests placed in the world is game:world_<the first 8 hex digits of its world's loot GUID>_<the scene node id of the first chest that uses it, as 8 hex digits> (the smallest node id among its chests). Both stay with their table when a game update inserts or moves tables; a table whose first chest changes gets a new id, and edits naming the old one are refused. A world table no chest uses has no id. Select by the prefixes (game:labels_*, game:world_*) and by the tables' values, or by an id read in the same run.

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

Groups of fields:

- `level`: The level the table's random loot rolls at. Without a mode the level of the place decides (the area level).
- `levelOffset`: Levels applied after level, before the random range.
- `levelOffsetRange`: A random number of extra levels each roll adds, from min to max (0 and 0: none).
- `rarity`: The table's own rarity weights. A table whose five modes are none rolls with the world-wide weights (lootRules); elites, bosses and special chests set all five.
- `rarity.common`: The table's own weight of common drops.
- `rarity.uncommon`: The table's own weight of uncommon drops.
- `rarity.rare`: The table's own weight of rare drops.
- `rarity.epic`: The table's own weight of epic drops.
- `rarity.legendary`: The table's own weight of legendary drops.

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `level.mode` | enum (none, set, add, multiply) | now | set | 0.1.0 | mapped | serverOnly | How value changes the level: none leaves the area level, set uses value as the level, add adds value to it, multiply multiplies it by value. |
| `level.value` | float -1000..1000 | now | number | 0.1.0 | mapped | serverOnly | The level, or what is added to or multiplied with it, as mode says. |
| `levelOffset.mode` | enum (none, set, add, multiply) | now | set | 0.1.0 | mapped | serverOnly | How value changes the level after level: none, set, add or multiply. |
| `levelOffset.value` | float -1000..1000 | now | number | 0.1.0 | mapped | serverOnly | The offset, as mode says. |
| `levelOffsetRange.min` | float -1000..1000 | now | number | 0.1.0 | mapped | serverOnly | The fewest extra levels a roll adds. |
| `levelOffsetRange.max` | float -1000..1000 | now | number | 0.1.0 | mapped | serverOnly | The most extra levels a roll adds. |
| `rarity.common.mode` | enum (none, set, add, multiply) | now | set | 0.1.0 | mapped | serverOnly | How the table's common weight combines with the world-wide weight (lootRules.rarityWeights.common): none keeps the world-wide weight, set replaces it, add adds to it, multiply multiplies it. |
| `rarity.common.weight` | float 0..1000000000000 | now | number | 0.1.0 | mapped | serverOnly | The table's common weight (used as its mode says). |
| `rarity.uncommon.mode` | enum (none, set, add, multiply) | now | set | 0.1.0 | mapped | serverOnly | How the table's uncommon weight combines with the world-wide weight (lootRules.rarityWeights.uncommon): none keeps the world-wide weight, set replaces it, add adds to it, multiply multiplies it. |
| `rarity.uncommon.weight` | float 0..1000000000000 | now | number | 0.1.0 | mapped | serverOnly | The table's uncommon weight (used as its mode says). |
| `rarity.rare.mode` | enum (none, set, add, multiply) | now | set | 0.1.0 | mapped | serverOnly | How the table's rare weight combines with the world-wide weight (lootRules.rarityWeights.rare): none keeps the world-wide weight, set replaces it, add adds to it, multiply multiplies it. |
| `rarity.rare.weight` | float 0..1000000000000 | now | number | 0.1.0 | mapped | serverOnly | The table's rare weight (used as its mode says). |
| `rarity.epic.mode` | enum (none, set, add, multiply) | now | set | 0.1.0 | mapped | serverOnly | How the table's epic weight combines with the world-wide weight (lootRules.rarityWeights.epic): none keeps the world-wide weight, set replaces it, add adds to it, multiply multiplies it. |
| `rarity.epic.weight` | float 0..1000000000000 | now | number | 0.1.0 | mapped | serverOnly | The table's epic weight (used as its mode says). |
| `rarity.legendary.mode` | enum (none, set, add, multiply) | now | set | 0.1.0 | mapped | serverOnly | How the table's legendary weight combines with the world-wide weight (lootRules.rarityWeights.legendary): none keeps the world-wide weight, set replaces it, add adds to it, multiply multiplies it. |
| `rarity.legendary.weight` | float 0..1000000000000 | now | number | 0.1.0 | mapped | serverOnly | The table's legendary weight (used as its mode says). |

## Entries

- Add (stage add, id `<your mod id>:<name>`): no.
- Remove: no.
- Raw game paths in edits (the Advanced tier): allowed.
