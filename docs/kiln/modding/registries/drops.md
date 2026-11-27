# The `drops` registry

<!-- Generated from schema/registry/drops.json by tools/registrygen/registrygen.py. Do not edit. -->

The game's drop tables (default inventories): what a container, an enemy or a plant gives, with counts and chances.

Schema version 0.1.0, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.DropTableEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

Edits per call: 4,096. One `OnRegistry` callback (one phase of one entry point) or one `Registry.Change` takes at most this many edits of this registry; a call with more is refused whole, with the reason in kiln.log and registry.json, and nothing of it applies (the other calls do). The default for a registry whose schema declares none (4,096).

## Ids

- Game entries: `game:<stable name>`. Mod entries: `<mod id>:<name>`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- Drop tables have no names in the game. A table that templates use is named after the first of them in name order, in lower case (game:lootcontainer_enemy_scavenger_melee01); a table no template uses is game:drops_<first 8 hex digits of its GUID>. When two tables would get the same name, the one that held it first keeps it and the other is game:<name>~<first 8 hex digits of its GUID>. A table's GUID also selects it (as data packs' drops section does today).

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

Groups of fields:

- `entries[].count`: How many of the item the entry gives: a number from min to max.

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `entries` | list of entries, at most 4096 | now | list | 0.1.0 | mapped | serverOnly | The table's stacks in walk order (root group first, then its groups). Index i in a field path (entries[3].chance) is that order. |
| `entries[].item` | ref items | now | read-only | 0.1.0 | mapped | serverOnly | The item. Given when an entry is appended; read-only afterwards. |
| `entries[].count.min` | int 0..1000000 | now | number | 0.1.0 | mapped | serverOnly | The fewest the entry gives. |
| `entries[].count.max` | int 0..1000000 | now | number | 0.1.0 | mapped | serverOnly | The most the entry gives. |
| `entries[].chance` | float 0..1 | now | number | 0.1.0 | mapped | serverOnly | The chance the entry drops. |
| `entries[].weight` | float 0..1000000 | now | number | 0.1.0 | mapped | serverOnly | The weight of the entry in a group that picks one. |

## Entries

- Add (stage add, id `<your mod id>:<name>`): no.
- Remove: no.
- Raw game paths in edits (the Advanced tier): allowed.
