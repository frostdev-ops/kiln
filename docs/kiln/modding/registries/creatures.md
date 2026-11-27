# The `creatures` registry

<!-- Generated from schema/registry/creatures.json by tools/registrygen/registrygen.py. Do not edit. -->

Every creature the game defines: enemies, wildlife, farm animals and villagers: their level, faction, size, health and damage scaling, resistances, speeds, random loot, the enemy's sight, and a wild animal's taming (tameable, steps, favourite bait). An edit is global: every creature of the entry, now or later, as the field says.

Schema version 0.1.1, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.CreatureEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

Edits per call: 4,096. One `OnRegistry` callback (one phase of one entry point) or one `Registry.Change` takes at most this many edits of this registry; a call with more is refused whole, with the reason in kiln.log and registry.json, and nothing of it applies (the other calls do). The default for a registry whose schema declares none (4,096).

## Ids

- Game entries: `game:<the creature's stable name>`. Mod entries: `<mod id>:<name>`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- A creature has the id of its template: the template's debug name in lower case (Enemy_Vukah_Melee is game:enemy_vukah_melee). A template without a name is game:template_<first 8 hex digits of its GUID>. When two templates would get the same name, the one that held it first keeps it and the other is game:<name>~<first 8 hex digits of its GUID>. The entries are the templates with an enemy's, a wild animal's, a farm animal's or a villager's behaviour; the same id names the template in every registry.

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

Groups of fields:

- `health`: How the creature's health grows. Health itself comes from its level through the game's formulas.
- `damage`: How hard the creature hits.
- `resist`: How much of each kind of damage the creature takes: 1 = normal, 0 = immune, 2 = double. Creatures that spawn after the edit get it; creatures already in the world keep the value they spawned with.
- `speed`: How fast the creature moves, in metres per second. Creatures that spawn after the edit get it; creatures already in the world keep the value they spawned with.
- `loot`: The random loot a killed creature leaves. Creatures that spawn after the edit get it; creatures already in the world keep the value they spawned with.
- `loot.slots`: How many random loot rolls a kill gives: a number from min to max.
- `sight`: What an enemy sees. Read at once by every enemy of the entry, also those already in the world.

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `kind` | enum (enemy, wildlife, animal, villager) | now | read-only | 0.1.0 | mapped | serverOnly | What the creature is, by the behaviour it has: an enemy, a wild animal, a farm animal or pet, a villager. |
| `level` | int 1..10000 | now | number | 0.1.0 | mapped | serverOnly | The level a creature spawns at when its spawner sets none (most spawners set their own). Creatures that spawn after the edit get it; creatures already in the world keep the value they spawned with. |
| `levelMax` | int 1..10000 | now | number | 0.1.0 | mapped | serverOnly | The highest level the creature can have; a level above it is cut to it. Creatures that spawn after the edit get it; creatures already in the world keep the value they spawned with. |
| `faction` | enum (environment, hitAll, fogger, vukah, scavenger, wildlife, wildBeast, player, automatedPlayer, hitOthers, skeleton, playerAlly, farmAnimal, cyclops, drak, ancients) | now | set | 0.1.0 | mapped | serverOnly | The side the creature fights for (playerAlly: on the players' side). Creatures that spawn after the edit get it; creatures already in the world keep the value they spawned with. |
| `scale` | float 0.01..100 | now | number | 0.1.0 | mapped | serverOnly | The creature's size (1 = as made). Only a creature that has a size value of its own can take one. Creatures that spawn after the edit get it; creatures already in the world keep the value they spawned with. |
| `xp` | float 0..1000 | now | number | 0.1.0 | mapped | serverOnly | The factor on the experience a kill gives (1 = as the game gives). Creatures that spawn after the edit get it; creatures already in the world keep the value they spawned with. |
| `health.levelFactor` | float 0..1000 | now | number | 0.1.0 | mapped | serverOnly | The factor of the health a level adds (the game's level scaling). Read at the creature's next health update. |
| `health.perPlayer` | float 0..100 | now | number | 0.1.0 | mapped | serverOnly | The health added per player nearby beyond the first, as a share (0.48 = 48 %). Read at the creature's next health update. |
| `damage.multiplier` | float 0..1000 | now | number | 0.1.0 | mapped | serverOnly | The factor on all the damage the creature deals (1 = as made). Creatures that spawn after the edit get it; creatures already in the world keep the value they spawned with. |
| `damage.perPlayer` | float 0..100 | now | number | 0.1.0 | mapped | serverOnly | The damage added per player nearby beyond the first, as a share (0.05 = 5 %). Read at the creature's next health update. |
| `resist.physical` | float 0..100 | now | number | 0.1.0 | mapped | serverOnly | The share of physical damage as a whole the creature takes. |
| `resist.cut` | float 0..100 | now | number | 0.1.0 | mapped | serverOnly | The share of cutting damage the creature takes. |
| `resist.pierce` | float 0..100 | now | number | 0.1.0 | mapped | serverOnly | The share of piercing damage the creature takes. |
| `resist.blunt` | float 0..100 | now | number | 0.1.0 | mapped | serverOnly | The share of blunt damage the creature takes. |
| `resist.wood` | float 0..100 | now | number | 0.1.0 | mapped | serverOnly | The share of wood damage (tools) the creature takes. |
| `resist.stone` | float 0..100 | now | number | 0.1.0 | mapped | serverOnly | The share of stone damage (tools) the creature takes. |
| `resist.metal` | float 0..100 | now | number | 0.1.0 | mapped | serverOnly | The share of metal damage (tools) the creature takes. |
| `resist.poison` | float 0..100 | now | number | 0.1.0 | mapped | serverOnly | The share of poison damage the creature takes. |
| `resist.explosion` | float 0..100 | now | number | 0.1.0 | mapped | serverOnly | The share of explosion damage the creature takes. |
| `resist.skillShot` | float 0..100 | now | number | 0.1.0 | mapped | serverOnly | The share of skill-shot hits (weak spots) the creature takes. |
| `resist.fire` | float 0..100 | now | number | 0.1.0 | mapped | serverOnly | The share of fire damage the creature takes. |
| `resist.shroud` | float 0..100 | now | number | 0.1.0 | mapped | serverOnly | The share of shroud damage the creature takes. |
| `resist.ice` | float 0..100 | now | number | 0.1.0 | mapped | serverOnly | The share of ice damage the creature takes. |
| `resist.lightning` | float 0..100 | now | number | 0.1.0 | mapped | serverOnly | The share of lightning damage the creature takes. |
| `speed.sneak` | float m/s 0..1000 | now | number | 0.1.0 | mapped | serverOnly | Its speed when sneaking. |
| `speed.walk` | float m/s 0..1000 | now | number | 0.1.0 | mapped | serverOnly | Its speed when walking. |
| `speed.run` | float m/s 0..1000 | now | number | 0.1.0 | mapped | serverOnly | Its speed when running. |
| `speed.sprint` | float m/s 0..1000 | now | number | 0.1.0 | mapped | serverOnly | Its speed when sprinting. |
| `speed.combat` | float m/s 0..1000 | now | number | 0.1.0 | mapped | serverOnly | Its speed when moving in combat. |
| `speed.patrol` | float m/s 0..1000 | now | number | 0.1.0 | mapped | serverOnly | Its speed when patrolling. |
| `speed.flee` | float m/s 0..1000 | now | number | 0.1.0 | mapped | serverOnly | Its speed when fleeing. |
| `loot.disabled` | bool | now | set | 0.1.0 | mapped | serverOnly | The creature leaves no random loot. |
| `loot.slots.min` | int 0..255 | now | number | 0.1.0 | mapped | serverOnly | The fewest random loot rolls. |
| `loot.slots.max` | int 0..255 | now | number | 0.1.0 | mapped | serverOnly | The most random loot rolls. |
| `arsenal` | ref arsenals, optional | now | read-only | 0.1.0 | mapped | serverOnly | The enemy's arsenal: the attacks it chooses from (arsenals registry). Creatures of the same kind share one. |
| `sight.range` | float m 0..10000 | now | number | 0.1.0 | mapped | serverOnly | How far the enemy sees a target by day, in metres (night shortens it by the game's night factor). |
| `sight.engageRange` | float m 0..10000 | now | number | 0.1.0 | mapped | serverOnly | Within this distance, in metres, the enemy engages a target it sees at once. |
| `tameable` | bool | now | read-only | 0.1.1 | mapped | serverOnly | Whether the game lets the creature be tamed its own way: a wild animal whose template allows taming (a dog, a cat, a goat, a bunny, a catdeer, a turkey, a yak, a capybara, a sanddigger). Null for a creature that is no wild animal. Read only in this release. |
| `tamingSteps` | int 1..255 | now | number | 0.1.1 | mapped | serverOnly | How many of the game's taming steps (a petting while the animal eats its favourite bait) tame a tameable wild animal into a farm animal. Read at once by every animal of the entry, also those already in the world (the game keeps it in the template only); an animal whose progress already reaches the new number is tamed at its next update. Null for a creature that is no wild animal. |
| `favoriteBait` | ref templates, optional | now | read-only | 0.1.1 | mapped | serverOnly | The bait a tameable wild animal eats (the template its bait item places, game:foodbait_t2_dog for a dog); none for an animal with no favourite bait. Read only in this release. |

## Traits

Selectors ask for these with `has` / `hasNot`.

| Trait | Means |
|---|---|
| `enemy` | an enemy (kind enemy) |
| `wildlife` | a wild animal (kind wildlife) |
| `animal` | a farm animal or pet (kind animal) |
| `villager` | a villager or workshop NPC (kind villager) |

## Entries

- Add (stage add, id `<your mod id>:<name>`): no.
- Remove: no.
- Raw game paths in edits (the Advanced tier): allowed.
