## Edit it from a data pack

Stations take **no edit**: every field is read-only. The registry exists so a recipe can name its station and a mod can select
recipes by it. A station's id is the id of the item that builds it (`game:prop_production_t1_forge`; a villager's station: the
villager's item), or `game:station_<8 hex>` for the few without one.

What a data pack does with it is select recipes: `"select": {"id": "*", "where": {"station": "game:prop_production_t1_forge"}}`
over the [recipes](recipes.md) registry. Traits: `npc` (a villager's station).

## Edit it from C#

```csharp
registry.Alter(view =>
{
    var forge = new RegistryId("game:prop_production_t1_forge");
    foreach (var recipe in view.Recipes.Select())
        if (recipe.Station == forge) recipe.Duration /= 2;           // every forge recipe takes half as long
});

// reading the stations themselves, outside a pass (on the tick thread, in your mod)
Kiln.Registry.Registry.Read(view =>
{
    foreach (var station in view.Stations.Select())
        Log.Info($"{station.Id.Value}: built by {station.Item}, villager's: {station.Npc}");
});
```

`view.Stations` is a `RegistryEntries<StationEntry>` (`Item`, `Npc`: read-only properties, no setters).

## Views

`RegistryViews.Find("stations")` is the stable view (51 stations on the current server build, 7 without an item); every field
reads `Readable` and is marked `ReadOnly` ([views.md](views.md)).

## Worked example

Every forge recipe takes half as long. The station is only the selector; the edit is the recipes':

```json
{
  "format": 1,
  "registry": {
    "alter": [
      { "registry": "recipes", "select": { "id": "*", "where": { "station": "game:prop_production_t1_forge" } }, "field": "duration", "op": { "mul": 0.5 } }
    ]
  }
}
```

## In multiplayer

The registry is read-only and `serverOnly`: nothing syncs. What the example edits (recipe durations) syncs as the
[recipes](recipes.md#in-multiplayer) registry says.

## Known limits

- Read-only: a recipe cannot move to another station and a station cannot be added or removed.
- One station has many placed forms (the game has 17 for the workbench), so what a placed chest or station does for its base is
  not a station's data: which containers count as a base's crafting stock and which take area deposits are the containers'
  templates, and no registry maps them yet ([README](README.md#stations-recipes-and-the-bases-storage)).
