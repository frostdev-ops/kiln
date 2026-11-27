# The `lootRules` registry

<!-- Generated from schema/registry/lootRules.json by tools/registrygen/registrygen.py. Do not edit. -->

World-wide random loot rules: the rarity weights a random roll uses where its table (randomLoot) has none of its own.

Schema version 0.1.0, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.LootRulesEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

Edits per call: 4,096. One `OnRegistry` callback (one phase of one entry point) or one `Registries.Change` takes at most this many edits of this registry; a call with more is refused whole, with the reason in kiln.log and registry.json, and nothing of it applies (the other calls do). The schema declares it (`editsPerCall`) to fit every field of every entry of the current game data.

## Ids

- Game entries: `game:loot`. Mod entries: `(none: one entry)`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- One entry, game:loot.

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

Groups of fields:

- `rarityWeights`: How likely each rarity is in a random roll, as weights: a rarity's chance is its weight divided by the sum of the weights of the rarities the item can have.

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `rarityWeights.common` | float 0..1000000000000 | now | number | 0.1.0 | mapped | serverOnly | The weight of common drops. |
| `rarityWeights.uncommon` | float 0..1000000000000 | now | number | 0.1.0 | mapped | serverOnly | The weight of uncommon drops. |
| `rarityWeights.rare` | float 0..1000000000000 | now | number | 0.1.0 | mapped | serverOnly | The weight of rare drops. |
| `rarityWeights.epic` | float 0..1000000000000 | now | number | 0.1.0 | mapped | serverOnly | The weight of epic drops. |
| `rarityWeights.legendary` | float 0..1000000000000 | now | number | 0.1.0 | mapped | serverOnly | The weight of legendary drops. |

## Entries

- Add (stage add, id `<your mod id>:<name>`): no.
- Remove: no.
- Raw game paths in edits (the Advanced tier): allowed.

## Edit it from a data pack

One entry, `game:loot`: the **world-wide** rarity weights a random roll uses where its table ([randomLoot](randomLoot.md)) has none
of its own. A rarity's chance is its weight divided by the sum of the weights of the rarities the item can have, so doubling one
weight raises its share without making rolls more frequent.

- `"field": "rarityWeights.legendary", "op": {"mul": 2}`; the five fields are `common`, `uncommon`, `rare`, `epic` and `legendary`.
- Raw game paths are allowed (the Advanced tier).
- The tables that set their own odds (elites, bosses, special chests) ignore these weights: edit their `rarity.*` in `randomLoot`.

## Edit it from C#

```csharp
[RegistryEntryPoints(RegistryEntryPoint.WorldLoad)]
public sealed class Leveler : Mod
{
    public override void OnLoad() => Config.Register<Settings>().Changed += _ =>
    {
        var r = Kiln.Registry.Registries.Reauthor();
        if (!r.Ok) Log.Warn($"not authored again: {r.Reason}");
    };
    public override void OnRegistry(RegistryContext context)
    {
        if (context.Phase != RegistryPhase.Alter) return;
        var w = Kiln.Registry.Registries.For(context).LootRules.RarityWeights;   // the values below this mod's own edit
        w.Legendary = w.Legendary * 4;                                          // never stacks: each pass reads below its own last edit
    }
}
```

`stage.LootRules` is a `LootRulesEntry`; `RarityWeights` has `Common` to `Legendary` (doubles). A value that follows a setting is
re-authored with `Registries.Reauthor()` ([README](README.md#explicit-changes-and-ready)): the pass reads
the game's weights again, so a settings change never doubles the last result.

## Views

`RegistryViews.Find("lootRules")` is the stable view with the one entry `game:loot` ([views.md](views.md)).

## Worked example

The world-wide legendary weight doubles:

```json
{
  "format": 1,
  "registry": {
    "alter": [
      { "registry": "lootRules", "select": { "id": "game:loot" }, "field": "rarityWeights.legendary", "op": { "mul": 2 } }
    ]
  }
}
```

This is the README's example ([loot odds and loot levels](README.md#loot-odds-and-loot-levels-since-020-dev32));
[`mods/samples/RegistryEdits`](../../../../mods/samples/RegistryEdits/RegistryEditsMod.cs) does it from a setting. The world reads
the weights at each roll, so an edit reaches the next roll; loot already rolled keeps what it got.

## In multiplayer

The server world rolls loot (`serverOnly`): no client holds a copy and a pack of loot-rule edits is `clients: none`
([choosing clients](README.md#registry-only-packs-choosing-clients)).

## Known limits

- One entry only; no entry is added or removed.
- Not yet seen in a real game: a real loot roll after a weight change on a hosted or co-op world, and the hot reload of the pass
  in the middle of a roll.
