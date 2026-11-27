# The `world` registry

<!-- Generated from schema/registry/world.json by tools/registrygen/registrygen.py. Do not edit. -->

The world's build rules from its scene: the boxes where nobody may build (around points of interest and dungeons), and the playable areas of the big dungeons. The game takes them when it loads the world.

Schema version 0.1.0, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.WorldEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

Edits per call: 4,096. One `OnRegistry` callback (one phase of one entry point) or one `Registry.Change` takes at most this many edits of this registry; a call with more is refused whole, with the reason in kiln.log and registry.json, and nothing of it applies (the other calls do). The default for a registry whose schema declares none (4,096).

## Ids

- Game entries: `game:world`. Mod entries: `(none: one entry)`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- One entry, game:world: the world's scene.

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

Groups of fields:

- `noBuildZones[].min`: The zone's lowest corner
- `noBuildZones[].max`: The zone's highest corner
- `playableAreas[].min`: The area's lowest corner
- `playableAreas[].max`: The area's highest corner

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `noBuildZones` | list of entries, at most 4096 | restart | list | 0.1.0 | mapped | restart | The world's no-build zones: boxes in which nothing may be built. Clear the list to build anywhere the other rules allow (a base's build zone, the Shroud, enemies nearby). The game's no-build zones of furniture and creatures are not in this list. |
| `noBuildZones[].min.x` | float m -100000..100000 | restart | number | 0.1.0 | mapped | restart | The zone's lowest corner: the position along x, in metres from the world's origin. |
| `noBuildZones[].min.y` | float m -100000..100000 | restart | number | 0.1.0 | mapped | restart | The zone's lowest corner: the position up (height), in metres from the world's origin. |
| `noBuildZones[].min.z` | float m -100000..100000 | restart | number | 0.1.0 | mapped | restart | The zone's lowest corner: the position along z, in metres from the world's origin. |
| `noBuildZones[].max.x` | float m -100000..100000 | restart | number | 0.1.0 | mapped | restart | The zone's highest corner: the position along x, in metres from the world's origin. |
| `noBuildZones[].max.y` | float m -100000..100000 | restart | number | 0.1.0 | mapped | restart | The zone's highest corner: the position up (height), in metres from the world's origin. |
| `noBuildZones[].max.z` | float m -100000..100000 | restart | number | 0.1.0 | mapped | restart | The zone's highest corner: the position along z, in metres from the world's origin. |
| `playableAreas` | list of entries, at most 4096 | restart | list | 0.1.0 | mapped | restart | The playable areas of the world's big dungeons: boxes the game keeps players inside while they are in that dungeon (it puts a player who leaves one back). Clear the list to lift those bounds. An area appended here is a plain box: it is not marked as a big dungeon and names no map marker. |
| `playableAreas[].min.x` | float m -100000..100000 | restart | number | 0.1.0 | mapped | restart | The area's lowest corner: the position along x, in metres from the world's origin. |
| `playableAreas[].min.y` | float m -100000..100000 | restart | number | 0.1.0 | mapped | restart | The area's lowest corner: the position up (height), in metres from the world's origin. |
| `playableAreas[].min.z` | float m -100000..100000 | restart | number | 0.1.0 | mapped | restart | The area's lowest corner: the position along z, in metres from the world's origin. |
| `playableAreas[].max.x` | float m -100000..100000 | restart | number | 0.1.0 | mapped | restart | The area's highest corner: the position along x, in metres from the world's origin. |
| `playableAreas[].max.y` | float m -100000..100000 | restart | number | 0.1.0 | mapped | restart | The area's highest corner: the position up (height), in metres from the world's origin. |
| `playableAreas[].max.z` | float m -100000..100000 | restart | number | 0.1.0 | mapped | restart | The area's highest corner: the position along z, in metres from the world's origin. |

## Entries

- Add (stage add, id `<your mod id>:<name>`): no.
- Remove: no.
- Raw game paths in edits (the Advanced tier): allowed.
