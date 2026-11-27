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
