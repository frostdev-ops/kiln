# The `abilities` registry

<!-- Generated from schema/registry/abilities.json by tools/registrygen/registrygen.py. Do not edit. -->

Every action the game plays as a timed sequence: spell casts, weapon attacks, abilities (the double jump, the dodge, blink), item uses and creature attacks. Their timing: when a cast releases what it spawns, when a hit lands, when the cost is paid and when a buff is applied. Costs themselves are the item's (items.manaCost, items.staminaCost); the game has no cooldown for players beyond this timing.

Schema version 0.1.0, experimental (KILNX032: may change in any release until it is marked stable). C# entry type `Kiln.Registry.AbilityEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

## Ids

- Game entries: `game:<stable name>`. Mod entries: `(none: a mod adds an ability as a kiln.gamedata sequence clone or an ability block)`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- The stable name starts as the name of the ability's first part in lower case (Attack_Player_Staff_FixedAnim_Fireball_fast is game:attack_player_staff_fixedanim_fireball_fast). When two abilities would get the same name, the one that held it first keeps it and the other is game:<name>~<first 8 hex digits of its GUID>.

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

Groups of fields:

- `timing`: When the ability does things, in seconds from its start. Each field is the first such moment of the ability; every event of the same kind at that moment moves with it, and the others stay where they are. The next time the ability plays takes the new time. An ability without such a moment refuses the edit; a time after the ability ends never comes.

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `timing.release` | time s 0..600 | now | number | 0.1.0 | mapped | now | When the ability releases what it spawns: a spell's projectile, a thrown item, a summon. |
| `timing.hit` | time s 0..600 | now | number | 0.1.0 | mapped | now | When the ability's first hit or area effect starts (a weapon swing's hit, a slam's area). |
| `timing.cost` | time s 0..600 | now | number | 0.1.0 | mapped | now | When the ability pays its cost (mana, stamina, a charge of the item). |
| `timing.buff` | time s 0..600 | now | number | 0.1.0 | mapped | now | When the ability applies its first buff. |

## Entries

- Add (stage add, id `<your mod id>:<name>`): no.
- Remove: no.
- Raw game paths in edits (the Advanced tier): refused.
