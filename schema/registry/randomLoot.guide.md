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
registry.Alter(stage =>
{
    foreach (var table in stage.RandomLoot.Select("game:labels_*"))             // enemies and generic containers
        if (table.Rarity.Legendary.Mode == RandomLootMode.Set)                  // the elite, boss and chest tables with their own odds
            table.Rarity.Legendary.Weight = table.Rarity.Legendary.Weight * 2;
    foreach (var table in stage.RandomLoot.Select("game:labels_*"))
    {
        table.LevelOffset.Mode = RandomLootMode.Add;
        table.LevelOffset.Value = 2;
    }
});
```

`stage.RandomLoot` is a `RegistryEntries<RandomLootEntry>` (about 800 tables). A pass that edits every table (about 12,752 edits)
fits one call. To follow a setting or the players' level while a world runs, use `Registries.Reauthor()`:
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
