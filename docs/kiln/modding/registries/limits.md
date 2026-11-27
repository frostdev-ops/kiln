# The `limits` registry

<!-- Generated from schema/registry/limits.json by tools/registrygen/registrygen.py. Do not edit. -->

Limits the game's code holds as fixed numbers, not data: Kiln answers the game's own check with the registry's value through a hook of its own, installed only when the value is not the game's. The game reads each at every check; Kiln takes the value at start.

Schema version 0.1.0, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.LimitsEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

## Ids

- Game entries: `game:limits`. Mod entries: `(none: one entry)`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- One entry, game:limits.

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `maxAltars` | int 1..255 | restart | number | 0.1.0 | mapped | restart | The most Flame Altars the game allows at any Flame level: its altar check answers the smaller of balance.altarsPerFlameLevel at the world's Flame level and this. The game's own is 16, so raise both to allow more. Past 16 the game's bases still count 16: altars past the 16th give build zones without the base features (comfort, base chests, survivor beds, animals, the journal), the network message of a world's zones counts them in one byte, and a player's game draws at most 32 zone borders at once. |
| `giftingRange` | float m 0..1000000 | restart | number | 0.1.0 | mapped | restart | How far apart two players may be for one to give the other an item, in metres. The game's own is 50 m. The host checks the gift; a player's game uses it to offer the gift. |

## Entries

- Add (stage add, id `<your mod id>:<name>`): no.
- Remove: no.
- Raw game paths in edits (the Advanced tier): refused.
