# The `stations` registry

<!-- Generated from schema/registry/stations.json by tools/registrygen/registrygen.py. Do not edit. -->

The game's crafting stations: the workbench, the production stations and the crafters' stations of the villagers. A recipe names its station (recipes.station), so a station's recipes are the recipes that name it.

Schema version 0.1.0, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.StationEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

Edits per call: 4,096. One `OnRegistry` callback (one phase of one entry point) or one `Registry.Change` takes at most this many edits of this registry; a call with more is refused whole, with the reason in kiln.log and registry.json, and nothing of it applies (the other calls do). The default for a registry whose schema declares none (4,096).

## Ids

- Game entries: `game:<the station's item>`. Mod entries: `(none yet)`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- A station has the id of the item that builds or offers it (the workbench is game:craftingstation_workbench, the forge game:prop_production_t1_forge, the blacksmith game:npc_crafter_blacksmith01). A station without an item is game:station_<its 32-bit station id as 8 hex digits>. When two stations would get the same id, the one that held it first keeps it and the other is <id>~<first 8 hex digits of its GUID>.

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `item` | ref items, optional | restart | read-only | 0.1.0 | mapped | serverOnly | The item that builds the station (a villager's station: the villager's item); absent for the stations of the flame altar and a few others without one. |
| `npc` | bool | restart | read-only | 0.1.0 | mapped | serverOnly | The station is a villager's (a crafter or a trader), not one the player builds. |

## Traits

Selectors ask for these with `has` / `hasNot`.

| Trait | Means |
|---|---|
| `npc` | a villager's station (npc) |

## Entries

- Add (stage add, id `<your mod id>:<name>`): no.
- Remove: no.
- Raw game paths in edits (the Advanced tier): refused.
