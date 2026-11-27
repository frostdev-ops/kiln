# The `drops` registry

<!-- Generated from schema/registry/drops.json by tools/registrygen/registrygen.py. Do not edit. -->

The game's drop tables (default inventories): what a container, an enemy or a plant gives, with counts and chances.

Schema version 0.1.0, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.DropTableEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

Edits per call: 4,096. One `OnRegistry` callback (one phase of one entry point) or one `Registries.Change` takes at most this many edits of this registry; a call with more is refused whole, with the reason in kiln.log and registry.json, and nothing of it applies (the other calls do). The default for a registry whose schema declares none (4,096).

## Ids

- Game entries: `game:<stable name>`. Mod entries: `<mod id>:<name>`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- Drop tables have no names in the game. A table that templates use is named after the first of them in name order, in lower case (game:lootcontainer_enemy_scavenger_melee01); a table no template uses is game:drops_<first 8 hex digits of its GUID>. When two tables would get the same name, the one that held it first keeps it and the other is game:<name>~<first 8 hex digits of its GUID>. A table's GUID also selects it (as data packs' drops section does today).

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

Groups of fields:

- `entries[].count`: How many of the item the entry gives: a number from min to max.

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `entries` | list of entries, at most 4096 | now | list | 0.1.0 | mapped | serverOnly | The table's stacks in walk order (root group first, then its groups). Index i in a field path (entries[3].chance) is that order. |
| `entries[].item` | ref items | now | read-only | 0.1.0 | mapped | serverOnly | The item. Given when an entry is appended; read-only afterwards. |
| `entries[].count.min` | int 0..1000000 | now | number | 0.1.0 | mapped | serverOnly | The fewest the entry gives. |
| `entries[].count.max` | int 0..1000000 | now | number | 0.1.0 | mapped | serverOnly | The most the entry gives. |
| `entries[].chance` | float 0..1 | now | number | 0.1.0 | mapped | serverOnly | The chance the entry drops. |
| `entries[].weight` | float 0..1000000 | now | number | 0.1.0 | mapped | serverOnly | The weight of the entry in a group that picks one. |

## Entries

- Add (stage add, id `<your mod id>:<name>`): no.
- Remove: no.
- Raw game paths in edits (the Advanced tier): allowed.

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
