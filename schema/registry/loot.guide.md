## Edit it from a data pack

One entry per item that can drop as random loot; its id is the item's id (`game:material_t1_wood_logs`). Edits use the same
shape as every registry ([items](items.md#edit-it-from-a-data-pack)):

- `stack.min`, `stack.max`, `stack.maxScaled`: numbers (`"op": {"add": 1}`); `stack.scalable`: `true` or `false`.
- `rarities`: the rarities a drop may roll, as a list of names (`"op": ["rare", "epic", "legendary"]`);
  `fixedRarity`: one name, `none` to roll from `rarities`.
- Take an item out of the random-loot candidates (GameStart only): `{"registry": "loot", "entry": "remove", "select": {"id": "game:material_t1_wood_logs"}}`.
  The item, its recipes and its direct drops stay ([what removal keeps](README.md#entry-add-and-remove-gamestart-only)).
  A loot entry cannot be added: an item that has none cannot drop as random loot through this registry.

## Edit it from C#

```csharp
registry.Alter(stage =>
{
    foreach (var entry in stage.Loot.Select("game:material_t1_*")) entry.Stack.Max += 1;
    stage.Loot["game:material_t1_wood_logs"].Rarities = RegistryRarities.Epic | RegistryRarities.Legendary;
});
registry.Alter(stage => stage.Loot.Remove("game:material_t1_twigs"));     // GameStart only
```

`stage.Loot` is a `RegistryEntries<ItemLootEntry>`. `Stack` is a group (`Min`, `Max`, `Scalable`, `MaxScaled`), `Rarities` a flags
enum (`RegistryRarities`), `Rarity` the fixed one (a `RegistryRarity?`, null to roll from `Rarities`; 0.2.0-dev.31's `FixedRarity`
still works, obsolete). Removal goes through `Remove(pattern)` in the collection.
To roll rarities by weight instead, see [randomLoot](randomLoot.md) and [lootRules](lootRules.md).

## Views

`RegistryViews.Find("loot")` is the stable view; the ids are the items' ids, so `RegistryViews.Find("items")` answers the same
id ([views.md](views.md)).

## Worked example

Tier 1 materials drop in one bigger stack, and epic and legendary are the only rarities wood logs roll:

```json
{
  "format": 1,
  "registry": {
    "alter": [
      { "registry": "loot", "select": { "id": "game:material_t1_*" }, "field": "stack.max", "op": { "add": 1 } },
      { "registry": "loot", "select": { "id": "game:material_t1_wood_logs" }, "field": "rarities", "op": ["epic", "legendary"] }
    ]
  }
}
```

The first edit is the one `mods/tests/registry-stage-test` runs in a lab. Both apply at the next roll.

## In multiplayer

Loot is rolled on the server only (`serverOnly`): no client needs a copy and a vanilla client sees nothing different. A
pack of loot edits is `clients: none` ([choosing clients](README.md#registry-only-packs-choosing-clients)). Loot already rolled
keeps what it got.

## Known limits

- Entries cannot be added, and removal only filters random-loot candidates: direct drops (a drop table's own entries) are the
  [drops](drops.md) registry's.
- Not yet seen in a real game: a stack or rarity change in a real roll on a joined server.
