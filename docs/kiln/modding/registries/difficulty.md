# The `difficulty` registry

<!-- Generated from schema/registry/difficulty.json by tools/registrygen/registrygen.py. Do not edit. -->

The bounds of the world's difficulty settings: for each setting a world can choose, the lowest and the highest value the game accepts. The game keeps a world's own (Custom) settings inside them when it reads the settings at start; the presets (Default, Relaxed, Hard, Survival) keep their own values. Setting names are the server's enshrouded_server.json "gameSettings" keys, as Kiln's settings service names them.

Schema version 0.1.0, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.DifficultyEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

Edits per call: 4,096. One `OnRegistry` callback (one phase of one entry point) or one `Registries.Change` takes at most this many edits of this registry; a call with more is refused whole, with the reason in kiln.log and registry.json, and nothing of it applies (the other calls do). The default for a registry whose schema declares none (4,096).

## Ids

- Game entries: `game:difficulty`. Mod entries: `(none: one entry)`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- One entry, game:difficulty.

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

Groups of fields:

- `playerHealthFactor`: The setting for players' health (playerHealthFactor).
- `playerManaFactor`: The setting for players' mana (playerManaFactor).
- `playerStaminaFactor`: The setting for players' stamina (playerStaminaFactor).
- `playerBodyHeatFactor`: The setting for players' body heat (playerBodyHeatFactor).
- `playerDivingTimeFactor`: The setting for players' diving time (playerDivingTimeFactor).
- `foodBuffDurationFactor`: The setting for how long food buffs last (foodBuffDurationFactor).
- `fromHungerToStarving`: The setting for the time from hungry to starving (fromHungerToStarving).
- `shroudTimeFactor`: The setting for players' time in the Shroud (shroudTimeFactor).
- `miningDamageFactor`: The setting for mining damage (miningDamageFactor).
- `plantGrowthSpeedFactor`: The setting for how fast plants grow (plantGrowthSpeedFactor).
- `resourceDropStackAmountFactor`: The setting for how much resources drop (resourceDropStackAmountFactor).
- `factoryProductionSpeedFactor`: The setting for how fast workshops make things (factoryProductionSpeedFactor).
- `perkUpgradeRecyclingFactor`: The setting for what salvaging an upgraded perk gives back (perkUpgradeRecyclingFactor).
- `perkCostFactor`: The setting for what upgrading a weapon costs (perkCostFactor).
- `experienceCombatFactor`: The setting for experience from combat (experienceCombatFactor).
- `experienceMiningFactor`: The setting for experience from mining (experienceMiningFactor).
- `experienceExplorationQuestsFactor`: The setting for experience from exploring and quests (experienceExplorationQuestsFactor).
- `enemyDamageFactor`: The setting for enemies' damage (enemyDamageFactor).
- `enemyHealthFactor`: The setting for enemies' health (enemyHealthFactor).
- `enemyStaminaFactor`: The setting for enemies' stamina (enemyStaminaFactor).
- `enemyPerceptionRangeFactor`: The setting for how far enemies see and hear (enemyPerceptionRangeFactor).
- `bossDamageFactor`: The setting for bosses' damage (bossDamageFactor).
- `bossHealthFactor`: The setting for bosses' health (bossHealthFactor).
- `threatBonus`: The setting for how often enemies attack (threatBonus).
- `dayTimeDuration`: The setting for how long a day lasts (dayTimeDuration).
- `nightTimeDuration`: The setting for how long a night lasts (nightTimeDuration).
- `flameLevelFogRemoval`: The setting for the Flame level from which the Shroud lifts where players build (flameLevelFogRemoval).

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `playerHealthFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for players' health. |
| `playerHealthFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for players' health. |
| `playerManaFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for players' mana. |
| `playerManaFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for players' mana. |
| `playerStaminaFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for players' stamina. |
| `playerStaminaFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for players' stamina. |
| `playerBodyHeatFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for players' body heat. |
| `playerBodyHeatFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for players' body heat. |
| `playerDivingTimeFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for players' diving time. |
| `playerDivingTimeFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for players' diving time. |
| `foodBuffDurationFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for how long food buffs last. |
| `foodBuffDurationFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for how long food buffs last. |
| `fromHungerToStarving.min` | time s 0..604800 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for the time from hungry to starving. |
| `fromHungerToStarving.max` | time s 0..604800 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for the time from hungry to starving. |
| `shroudTimeFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for players' time in the Shroud. |
| `shroudTimeFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for players' time in the Shroud. |
| `miningDamageFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for mining damage. |
| `miningDamageFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for mining damage. |
| `plantGrowthSpeedFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for how fast plants grow. |
| `plantGrowthSpeedFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for how fast plants grow. |
| `resourceDropStackAmountFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for how much resources drop. |
| `resourceDropStackAmountFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for how much resources drop. |
| `factoryProductionSpeedFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for how fast workshops make things. |
| `factoryProductionSpeedFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for how fast workshops make things. |
| `perkUpgradeRecyclingFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for what salvaging an upgraded perk gives back. |
| `perkUpgradeRecyclingFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for what salvaging an upgraded perk gives back. |
| `perkCostFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for what upgrading a weapon costs. |
| `perkCostFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for what upgrading a weapon costs. |
| `experienceCombatFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for experience from combat. |
| `experienceCombatFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for experience from combat. |
| `experienceMiningFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for experience from mining. |
| `experienceMiningFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for experience from mining. |
| `experienceExplorationQuestsFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for experience from exploring and quests. |
| `experienceExplorationQuestsFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for experience from exploring and quests. |
| `enemyDamageFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for enemies' damage. |
| `enemyDamageFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for enemies' damage. |
| `enemyHealthFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for enemies' health. |
| `enemyHealthFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for enemies' health. |
| `enemyStaminaFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for enemies' stamina. |
| `enemyStaminaFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for enemies' stamina. |
| `enemyPerceptionRangeFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for how far enemies see and hear. |
| `enemyPerceptionRangeFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for how far enemies see and hear. |
| `bossDamageFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for bosses' damage. |
| `bossDamageFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for bosses' damage. |
| `bossHealthFactor.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for bosses' health. |
| `bossHealthFactor.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for bosses' health. |
| `threatBonus.min` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for how often enemies attack. |
| `threatBonus.max` | float 0..10000 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for how often enemies attack. |
| `dayTimeDuration.min` | time s 0..604800 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for how long a day lasts. |
| `dayTimeDuration.max` | time s 0..604800 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for how long a day lasts. |
| `nightTimeDuration.min` | time s 0..604800 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for how long a night lasts. |
| `nightTimeDuration.max` | time s 0..604800 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for how long a night lasts. |
| `flameLevelFogRemoval.min` | int 0..255 | restart | number | 0.1.0 | mapped | restart | The lowest value of the setting for the Flame level from which the Shroud lifts where players build. |
| `flameLevelFogRemoval.max` | int 0..255 | restart | number | 0.1.0 | mapped | restart | The highest value of the setting for the Flame level from which the Shroud lifts where players build. |

## Entries

- Add (stage add, id `<your mod id>:<name>`): no.
- Remove: no.
- Raw game paths in edits (the Advanced tier): allowed.
