# The `loot` registry

<!-- Generated from schema/registry/loot.json by tools/registrygen/registrygen.py. Do not edit. -->

How an item drops as random loot: its stack size and its rarities. One entry per item that can drop.

Schema version 0.1.0, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.ItemLootEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

Edits per call: 4,096. One `OnRegistry` callback (one phase of one entry point) or one `Registry.Change` takes at most this many edits of this registry; a call with more is refused whole, with the reason in kiln.log and registry.json, and nothing of it applies (the other calls do). The default for a registry whose schema declares none (4,096).

## Ids

- Game entries: `game:<the item's stable name>`. Mod entries: `<mod id>:<the item's name>`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- A loot entry has the id of its item (items registry). An entry whose item has no name is game:item_<its 32-bit item id as 8 hex digits>.

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

Groups of fields:

- `stack`: The stack size of a drop.

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `stack.min` | int 0..65535 | now | number | 0.1.0 | mapped | serverOnly | The smallest stack a drop has. |
| `stack.max` | int 0..65535 | now | number | 0.1.0 | mapped | serverOnly | The largest stack a drop has. |
| `stack.scalable` | bool | now | set | 0.1.0 | mapped | serverOnly | The stack grows with the world's loot amount, up to maxScaled. |
| `stack.maxScaled` | int 0..65535 | now | number | 0.1.0 | mapped | serverOnly | The largest stack a drop has with the world's loot amount applied (when scalable). |
| `rarities` | flags (common, uncommon, rare, epic, legendary) | now | set | 0.1.0 | mapped | serverOnly | The rarities a drop may roll. |
| `fixedRarity` | enum (none, common, uncommon, rare, epic, legendary) | now | set | 0.1.0 | mapped | serverOnly | A rarity every drop gets (none: rolled from rarities). |

## Entries

- Add (stage add, id `<your mod id>:<name>`): no.
- Remove (disabled): GameStart only. Excludes the entry from random-loot candidates, retaining its record and item. Recipes, direct drops and already-owned copies are unchanged.
- Raw game paths in edits (the Advanced tier): allowed.

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
registry.Alter(view =>
{
    foreach (var entry in view.Loot.Select("game:material_t1_*")) entry.Stack.Max += 1;
    view.Loot["game:material_t1_wood_logs"].Rarities = RegistryRarities.Epic | RegistryRarities.Legendary;
});
registry.Alter(view => view.Loot.Remove("game:material_t1_twigs"));     // GameStart only
```

`view.Loot` is a `RegistryEntries<ItemLootEntry>`. `Stack` is a group (`Min`, `Max`, `Scalable`, `MaxScaled`), `Rarities` a flags
enum (`RegistryRarities`), `FixedRarity` an enum (`ItemLootRarity`). Removal goes through `Remove(pattern)` in the collection.
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
