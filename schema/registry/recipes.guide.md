## Edit it from a data pack

Same edit shape as every registry ([items](items.md#edit-it-from-a-data-pack) shows `select` and `op`). Recipe specifics:

- A recipe's id is its debug name in lower case (`game:ammo_arrow_t3_bronze`). Select by id, glob or trait (`"has": ["cooking"]`),
  or by station: `"select": {"id": "*", "where": {"station": "game:prop_production_t1_forge"}}`.
- `inputs` and `outputs` are lists of entries: `"list": {"append": [{"item": "game:material_t1_twigs", "count": 1}]}` adds an input,
  `{"clear": true}` empties it. One element's field: `"field": "inputs[0].count"` (or `inputs[*].count` for every input).
  An input names an item or an input category, never both.
- `station` is read-only: a recipe stays where the game put it (a stations id, none for the player's own crafting).
- `unlock` is `game` (the game's knowledge requirement) or `always` (visible from the start); only a client's crafting menu reads it.
- Add and remove a recipe (GameStart only): `{"registry": "recipes", "entry": "add", "id": "my-mod:plank", "like": "game:ammo_arrow_t3_bronze"}`
  in `add`. A new recipe inherits its base's station, inputs, outputs, duration and unlock rule. Removal hides it from
  station lists and the crafting view ([what it keeps](README.md#entry-add-and-remove-gamestart-only)).

## Edit it from C#

```csharp
registry.Alter(view =>
{
    var forge = new RegistryId("game:prop_production_t1_forge");
    foreach (var recipe in view.Recipes.Select())
        if (recipe.Station == forge) recipe.Duration /= 2;          // Duration is a TimeSpan
    var arrow = view.Recipes["game:ammo_arrow_t3_bronze"];
    arrow.Inputs[0].Count = 2;                                      // an element of the input list, by its index
    var twig = arrow.Inputs.AppendNew();                            // a new input: then set its fields
    twig.Item = new RegistryId("game:material_t1_twigs");
    twig.Count = 1;
});
```

`view.Recipes` is a `RegistryEntries<RecipeEntry>`: `Select()` lists every recipe (or a glob), and `Station`, `Inputs`,
`Outputs`, `Duration` and `Unlock` are typed properties. A list of entries has `AppendNew()`, `RemoveWhere(...)` and `Clear()`;
what a read in the callback shows: [C# authoring](README.md#c-authoring-api-0217). Add a recipe at GameStart with
`view.Recipes.AddLike(id, like, recipe => ...)`.

## Views

`RegistryViews.Find("recipes")` is the stable view; its `station` field is a `Ref` to the `stations` view, so a mod can
walk from a recipe to its station and back. The advanced view over the game's recipe collection (`recipeRegistry`) is one resource
with long lists (about 1.2 s for a full `Snapshot`): read the fields you need ([views.md](views.md)).

## Worked example

Every forge recipe takes half as long, and the bronze arrow recipe also takes one twig:

```json
{
  "format": 1,
  "registry": {
    "alter": [
      { "registry": "recipes", "select": { "id": "*", "where": { "station": "game:prop_production_t1_forge" } }, "field": "duration", "op": { "mul": 0.5 } },
      { "registry": "recipes", "select": { "id": "game:ammo_arrow_t3_bronze" }, "field": "inputs", "list": { "append": [ { "item": "game:material_t1_twigs", "count": 1 } ] } }
    ]
  }
}
```

The first edit is [`mods/samples/SkillCosts`](../../../../mods/samples/SkillCosts/content/skill-costs.json)'s; the second
changes what the next craft takes.

## In multiplayer

The server applies recipes. A joined Kiln client gets the effective recipe layer (the Joined client column: `now` for inputs,
outputs and duration), so its crafting menu and prediction match; a vanilla client keeps the game's recipe on its screen and
the server charges the changed one. `unlock` is `localOnly`: the server never sends it, a client's own mod sets it and a
dedicated server refuses it. `station` is `serverOnly`. A pack of recipe edits is `clients: kiln` for matching menus
([choosing clients](README.md#registry-only-packs-choosing-clients)); one that uses `unlock` is `mod`, side `both`.

## Known limits

- `station` is read-only: Kiln reads the game's grouping at start, so a recipe cannot move to another station.
- Input categories (`inputs[].category`) are checked by the shape of their id only: Kiln has no `inputCategories` registry yet.
- A craft already running keeps its inputs and time; a removed recipe does not cancel it.
- Not yet seen in a real game: a joined client's crafting menu after a server edit, hidden recipes in a client's menu, and
  a station's recipe list after `entry: remove`.
