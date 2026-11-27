# The `lootRules` registry

<!-- Generated from schema/registry/lootRules.json by tools/registrygen/registrygen.py. Do not edit. -->

World-wide random loot rules: the rarity weights a random roll uses where its table (randomLoot) has none of its own.

Schema version 0.1.0, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.LootRulesEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

Edits per call: 4,096. One `OnRegistry` callback (one phase of one entry point) or one `Registries.Change` takes at most this many edits of this registry; a call with more is refused whole, with the reason in kiln.log and registry.json, and nothing of it applies (the other calls do). The schema declares it (`editsPerCall`) to fit every field of every entry of the current game data.

## Ids

- Game entries: `game:loot`. Mod entries: `(none: one entry)`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- One entry, game:loot.

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

Groups of fields:

- `rarityWeights`: How likely each rarity is in a random roll, as weights: a rarity's chance is its weight divided by the sum of the weights of the rarities the item can have.

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `rarityWeights.common` | float 0..1000000000000 | now | number | 0.1.0 | mapped | serverOnly | The weight of common drops. |
| `rarityWeights.uncommon` | float 0..1000000000000 | now | number | 0.1.0 | mapped | serverOnly | The weight of uncommon drops. |
| `rarityWeights.rare` | float 0..1000000000000 | now | number | 0.1.0 | mapped | serverOnly | The weight of rare drops. |
| `rarityWeights.epic` | float 0..1000000000000 | now | number | 0.1.0 | mapped | serverOnly | The weight of epic drops. |
| `rarityWeights.legendary` | float 0..1000000000000 | now | number | 0.1.0 | mapped | serverOnly | The weight of legendary drops. |

## Entries

- Add (stage add, id `<your mod id>:<name>`): no.
- Remove: no.
- Raw game paths in edits (the Advanced tier): allowed.
