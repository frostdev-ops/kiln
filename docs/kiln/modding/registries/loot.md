# The `loot` registry

<!-- Generated from schema/registry/loot.json by tools/registrygen/registrygen.py. Do not edit. -->

How an item drops as random loot: its stack size and its rarities. One entry per item that can drop.

Schema version 0.1.0, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.ItemLootEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

Edits per call: 4,096. One `OnRegistry` callback (one phase of one entry point) or one `Registries.Change` takes at most this many edits of this registry; a call with more is refused whole, with the reason in kiln.log and registry.json, and nothing of it applies (the other calls do). The default for a registry whose schema declares none (4,096).

## Ids

- Game entries: `game:<the item's stable name>`. Mod entries: `<mod id>:<the item's name>`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- A loot entry has the id of its item (items registry). An entry whose item has no name is game:item_<its 32-bit item id as 8 hex digits>.

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

Groups of fields:

- `stack`: The stack size of a drop.

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `stack.min` | int 0..65535 | now | number | 0.1.0 | mapped | serverOnly | The smallest stack a drop has. |
| `stack.max` | int 0..65535 | now | number | 0.1.0 | mapped | serverOnly | The largest stack a drop has. |
| `stack.scalable` | bool | now | set | 0.1.0 | mapped | serverOnly | The stack grows with the world's loot amount, up to maxScaled. |
| `stack.maxScaled` | int 0..65535 | now | number | 0.1.0 | mapped | serverOnly | The largest stack a drop has with the world's loot amount applied (when scalable). |
| `rarities` | flags (common, uncommon, rare, epic, legendary) | now | set | 0.1.0 | mapped | serverOnly | The rarities a drop may roll. |
| `fixedRarity` | enum (none, common, uncommon, rare, epic, legendary) | now | set | 0.1.0 | mapped | serverOnly | A rarity every drop gets (none: rolled from rarities). In C# ItemLootEntry.Rarity, null for none (0.2.0-dev.31's FixedRarity, an ItemLootRarity, still works). |

## Entries

- Add (stage add, id `<your mod id>:<name>`): no.
- Remove (disabled): GameStart only. Excludes the entry from random-loot candidates, retaining its record and item. Recipes, direct drops and already-owned copies are unchanged.
- Raw game paths in edits (the Advanced tier): allowed.
