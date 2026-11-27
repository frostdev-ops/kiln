# The `skills` registry

<!-- Generated from schema/registry/skills.json by tools/registrygen/registrygen.py. Do not edit. -->

The nodes of the game's skill tree: what each costs in skill points, how many levels it has, its kind and class, and the ability it unlocks.

Schema version 0.1.0, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.SkillNodeEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

Edits per call: 4,096. One `OnRegistry` callback (one phase of one entry point) or one `Registry.Change` takes at most this many edits of this registry; a call with more is refused whole, with the reason in kiln.log and registry.json, and nothing of it applies (the other calls do). The default for a registry whose schema declares none (4,096).

## Ids

- Game entries: `game:skill_<node id>`. Mod entries: `(none: a mod adds nodes through kiln.skills)`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- A node has no debug name: it is game:skill_<its 32-bit node id as 8 hex digits> (game:skill_4475e8dd), the name Kiln's skills data-pack section and catalogs already give it. Nodes a mod adds through Kiln's skill tree service are not entries: their definition holds their cost.

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `cost` | int 0..65535 | now | number | 0.1.0 | mapped | now | The skill points one unlock costs (each level of a node with levels costs it again). The game counts it at every unlock, at every join and at a respec: a character whose unlocked nodes now cost more than their skill points gets the game's free reset of the whole tree at their next join. |
| `maxLevel` | int 0..7 | now | read-only | 0.1.0 | mapped | serverOnly | How many levels the node has: 0 is one unlock, 1 to 7 are levels. Read-only: a character's saved level above it would reset the whole tree at the next join, and a node without levels has no level slot. |
| `kind` | enum (root, attribute, active, passive) | now | read-only | 0.1.0 | mapped | serverOnly | The node's kind: a root of the tree, an attribute, an active skill (it unlocks an ability) or a passive skill. |
| `class` | enum (warrior, mage, ranger, neutral) | now | read-only | 0.1.0 | mapped | serverOnly | The class the node belongs to (its colour on the skill screen). |
| `unlocks` | ref items, optional | now | read-only | 0.1.0 | mapped | serverOnly | The ability item the node unlocks; absent for every node that unlocks none. |

## Traits

Selectors ask for these with `has` / `hasNot`.

| Trait | Means |
|---|---|
| `active` | unlocks an ability (kind active) |
| `leveled` | has levels (maxLevel above 0) |

## Entries

- Add (stage add, id `<your mod id>:<name>`): no.
- Remove: no.
- Raw game paths in edits (the Advanced tier): refused.
