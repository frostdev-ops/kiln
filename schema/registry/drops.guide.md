## Edit it from a data pack

A table's id is named after the first template that uses it (`game:lootcontainer_enemy_scavenger_melee01`), or its GUID's first
digits (`game:drops_<8 hex>`). Its `entries` are listed in walk order (the root group first, then its groups), and an edit names
one by its index, or every one with `*`:

- `"field": "entries[3].chance"`, `"entries[*].count.max"`; each is a number operation. `entries[].item` is read-only.
- Append a stack: `"field": "entries", "list": {"append": [{"item": "game:material_t1_wood_logs", "count": {"min": 1, "max": 3}, "chance": 0.25}]}`
  (an element's struct is a nested object, as its C# type is; the older dotted leaves, `"count.min": 1`, still read, and naming
  one leaf both ways is refused; `kiln lint` checks every element); `{"clear": true}` empties the table; `{"remove": [...]}` takes
  out the entries a condition picks, named by their values.
- A table's GUID also selects it (the older `drops` data-pack section does the same).

An item removed through the [items](items.md) or [loot](loot.md) registries has its slots here set to chance 0 and weight 0, never
resized: removal wins over a later chance edit of the same slot.

## Edit it from C#

```csharp
registry.Alter(stage =>
{
    var table = stage.Drops["game:lootcontainer_enemy_skeleton_elite"];
    foreach (var entry in table.Entries) entry.Chance = entry.Chance * 0.5;
    table.Entries[0].Count.Max = 5;                      // one stack's most
    var bonus = table.Entries.AppendNew();               // a new stack: what you set on it goes into the append
    bonus.Item = new RegistryId("game:material_t1_wood_logs");
    bonus.Count.Min = 1; bonus.Count.Max = 3; bonus.Chance = 0.25;
});
```

`stage.Drops` is a `RegistryEntries<DropTableEntry>`; `Entries` is a list of `DropEntry` (`Index`, `Item`, `Count.Min`,
`Count.Max`, `Chance`, `Weight`). A table the game has not loaded yet reads the value it takes when it loads (its base and the
writers' layers), and an edit of it waits ([README](README.md#explicit-changes-and-ready)). A stack's item is given when the stack
is appended (`Entries.AppendNew()` in C#, as above, or a data pack's `append`) and read-only after: setting `Item` on a stack that
exists throws `ArgumentException`, since the item is the stack's identity.

## Views

`RegistryViews.Find("drops")` is the stable view (420 tables on the current server build). The tables' stacks are
`entries[0].item`, `entries[0].count.min`, and so on, readable with `Get<T>` ([views.md](views.md)).

## Worked example

Elite skeletons drop half as often:

```json
{
  "format": 1,
  "registry": {
    "alter": [
      { "registry": "drops", "select": { "id": "game:lootcontainer_enemy_skeleton_elite" }, "field": "entries[*].chance", "op": { "mul": 0.5 } }
    ]
  }
}
```

This is the edit `mods/tests/registry-stage-test` runs in a lab. It applies at the next kill.

## In multiplayer

Drops are the server's (`serverOnly`); a vanilla client sees no difference and a pack of drop edits is `clients: none`
([choosing clients](README.md#registry-only-packs-choosing-clients)).

## Known limits

- A table cannot be added or removed, and an element appended keeps the physical slot order after the existing ones.
- The registry shows at most 64 entries per list in `registry.json` (the rest are counted in `resultEntriesLeftOut`).
- Not yet seen in a real game: a drop table edit on a joined server's real kill.
