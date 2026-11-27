# The `randomLoot` registry

<!-- Generated from schema/registry/randomLoot.json by tools/registrygen/registrygen.py. Do not edit. -->

The random loot tables: for each kind of enemy, generic container (barrels, crates, generic chests) and chest placed in the world, the level its random loot rolls at and its own rarity weights. Which items can drop is the loot registry's; the world-wide rarity weights are lootRules'. Where a table is used shows in its id: game:labels_* tables serve enemies and generic containers, game:world_* tables the chests placed in the world.

Schema version 0.1.0, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.RandomLootEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

Edits per call: 16,384. One `OnRegistry` callback (one phase of one entry point) or one `Registries.Change` takes at most this many edits of this registry; a call with more is refused whole, with the reason in kiln.log and registry.json, and nothing of it applies (the other calls do). The schema declares it (`editsPerCall`) to fit every field of every entry of the current game data.

## Ids

- Game entries: `game:labels_<8 hex digits> or game:world_<8 hex digits>_<8 hex digits>`. Mod entries: `(none: tables are not added)`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- Random loot tables have no names in the game. A table that enemies and generic containers use is game:labels_<its 32-bit id as 8 hex digits>; a table of the chests placed in the world is game:world_<the first 8 hex digits of its world's loot GUID>_<the scene node id of the first chest that uses it, as 8 hex digits> (the smallest node id among its chests). Both stay with their table when a game update inserts or moves tables; a table whose first chest changes gets a new id, and edits naming the old one are refused. A world table no chest uses has no id. Select by the prefixes (game:labels_*, game:world_*) and by the tables' values, or by an id read in the same run.

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

Groups of fields:

- `level`: The level the table's random loot rolls at. Without a mode the level of the place decides (the area level).
- `levelOffset`: Levels applied after level, before the random range.
- `levelOffsetRange`: A random number of extra levels each roll adds, from min to max (0 and 0: none).
- `rarity`: The table's own rarity weights. A table whose five modes are none rolls with the world-wide weights (lootRules); elites, bosses and special chests set all five.
- `rarity.common`: The table's own weight of common drops.
- `rarity.uncommon`: The table's own weight of uncommon drops.
- `rarity.rare`: The table's own weight of rare drops.
- `rarity.epic`: The table's own weight of epic drops.
- `rarity.legendary`: The table's own weight of legendary drops.

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `level.mode` | enum (none, set, add, multiply) | now | set | 0.1.0 | mapped | serverOnly | How value changes the level: none leaves the area level, set uses value as the level, add adds value to it, multiply multiplies it by value. |
| `level.value` | float -1000..1000 | now | number | 0.1.0 | mapped | serverOnly | The level, or what is added to or multiplied with it, as mode says. |
| `levelOffset.mode` | enum (none, set, add, multiply) | now | set | 0.1.0 | mapped | serverOnly | How value changes the level after level: none, set, add or multiply. |
| `levelOffset.value` | float -1000..1000 | now | number | 0.1.0 | mapped | serverOnly | The offset, as mode says. |
| `levelOffsetRange.min` | float -1000..1000 | now | number | 0.1.0 | mapped | serverOnly | The fewest extra levels a roll adds. |
| `levelOffsetRange.max` | float -1000..1000 | now | number | 0.1.0 | mapped | serverOnly | The most extra levels a roll adds. |
| `rarity.common.mode` | enum (none, set, add, multiply) | now | set | 0.1.0 | mapped | serverOnly | How the table's common weight combines with the world-wide weight (lootRules.rarityWeights.common): none keeps the world-wide weight, set replaces it, add adds to it, multiply multiplies it. |
| `rarity.common.weight` | float 0..1000000000000 | now | number | 0.1.0 | mapped | serverOnly | The table's common weight (used as its mode says). |
| `rarity.uncommon.mode` | enum (none, set, add, multiply) | now | set | 0.1.0 | mapped | serverOnly | How the table's uncommon weight combines with the world-wide weight (lootRules.rarityWeights.uncommon): none keeps the world-wide weight, set replaces it, add adds to it, multiply multiplies it. |
| `rarity.uncommon.weight` | float 0..1000000000000 | now | number | 0.1.0 | mapped | serverOnly | The table's uncommon weight (used as its mode says). |
| `rarity.rare.mode` | enum (none, set, add, multiply) | now | set | 0.1.0 | mapped | serverOnly | How the table's rare weight combines with the world-wide weight (lootRules.rarityWeights.rare): none keeps the world-wide weight, set replaces it, add adds to it, multiply multiplies it. |
| `rarity.rare.weight` | float 0..1000000000000 | now | number | 0.1.0 | mapped | serverOnly | The table's rare weight (used as its mode says). |
| `rarity.epic.mode` | enum (none, set, add, multiply) | now | set | 0.1.0 | mapped | serverOnly | How the table's epic weight combines with the world-wide weight (lootRules.rarityWeights.epic): none keeps the world-wide weight, set replaces it, add adds to it, multiply multiplies it. |
| `rarity.epic.weight` | float 0..1000000000000 | now | number | 0.1.0 | mapped | serverOnly | The table's epic weight (used as its mode says). |
| `rarity.legendary.mode` | enum (none, set, add, multiply) | now | set | 0.1.0 | mapped | serverOnly | How the table's legendary weight combines with the world-wide weight (lootRules.rarityWeights.legendary): none keeps the world-wide weight, set replaces it, add adds to it, multiply multiplies it. |
| `rarity.legendary.weight` | float 0..1000000000000 | now | number | 0.1.0 | mapped | serverOnly | The table's legendary weight (used as its mode says). |

## Entries

- Add (stage add, id `<your mod id>:<name>`): no.
- Remove: no.
- Raw game paths in edits (the Advanced tier): allowed.

## Edit it from a data pack

Each table is one place random loot rolls: `game:labels_*` tables serve enemies and generic containers (barrels, crates, generic
chests), `game:world_*` tables the chests placed in the world. A table has the **level** its loot rolls at and, optionally, its **own
rarity weights**.

- `level`, `levelOffset`, `levelOffsetRange`: `level.mode` is `none` (the area's level decides), `set`, `add` or `multiply`, and
  `level.value` is what that mode uses. `levelOffset` applies after `level`; `levelOffsetRange` adds a random number of levels
  from `min` to `max` to each roll.
- `rarity.<name>.mode` and `rarity.<name>.weight`: how the table's weight of a rarity combines with the world-wide one
  ([lootRules](lootRules.md)): `none` keeps the world-wide weight, `set` replaces it, `add` adds to it, `multiply` multiplies it. A
  table whose five modes are `none` rolls with the world-wide weights; elites, bosses and special chests set all five.
- Two levels more for every enemy and generic container: `"field": "levelOffset.mode", "op": "add"` then
  `"field": "levelOffset.value", "op": 2`.
- Select tables by the id prefixes and their values (`where`), not by a full id from another build: a table's id stays with it
  across game updates, but a table whose first chest changes gets a new id.
- An edit over 16,384 per call is refused (the cap of this registry; the others have 4,096).

## Edit it from C#

```csharp
registry.Alter(view =>
{
    foreach (var table in view.RandomLoot.Select("game:labels_*"))             // enemies and generic containers
        if (table.Rarity.Legendary.Mode == RandomLootMode.Set)                  // the elite, boss and chest tables with their own odds
            table.Rarity.Legendary.Weight = table.Rarity.Legendary.Weight * 2;
    foreach (var table in view.RandomLoot.Select("game:labels_*"))
    {
        table.LevelOffset.Mode = RandomLootMode.Add;
        table.LevelOffset.Value = 2;
    }
});
```

`view.RandomLoot` is a `RegistryEntries<RandomLootEntry>` (about 800 tables). A pass that edits every table (about 12,752 edits)
fits one call. To follow a setting or the players' level while a world runs, use `Registry.Reauthor(RegistryEntryPoint.WorldLoad)`:
the pass runs on a worker thread and lands in one step, in a few milliseconds
([README](README.md#explicit-changes-and-ready)).

## Views

`RegistryViews.Find("randomLoot")` is the stable view (797 tables on the current server build: 99 `labels_` and 698 `world_`);
which items drop is the [loot](loot.md) registry's ([views.md](views.md)).

## Worked example

Every enemy and generic-container table rolls two levels higher, and the world-wide legendary weight doubles:

```json
{
  "format": 1,
  "registry": {
    "alter": [
      { "registry": "lootRules", "select": { "id": "game:loot" }, "field": "rarityWeights.legendary", "op": { "mul": 2 } },
      { "registry": "randomLoot", "select": { "id": "game:labels_*" }, "field": "levelOffset.mode", "op": "add" },
      { "registry": "randomLoot", "select": { "id": "game:labels_*" }, "field": "levelOffset.value", "op": 2 }
    ]
  }
}
```

The server world reads both registries at each roll, so an edit reaches the next roll.

## In multiplayer

The server world rolls loot (`serverOnly`); no client holds a copy and a pack of random-loot edits is `clients: none`
([choosing clients](README.md#registry-only-packs-choosing-clients)).

## Known limits

- Tables are not added or removed; an id naming a table another build lacks is refused ("removed by game build ...").
- A re-authored pass runs off the world tick: a tick-thread API called in it throws, and Kiln then runs that mod's passes on the tick
  ([README](README.md#explicit-changes-and-ready)).
- Not yet seen in a real game: a hot reload in the middle of a roll, drop tables the game loads while a pass is prepared, and
  real rolls on a co-op or hosted world.
