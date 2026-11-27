# The `recipes` registry

<!-- Generated from schema/registry/recipes.json by tools/registrygen/registrygen.py. Do not edit. -->

Every crafting recipe: what it takes, what it makes and how long it takes.

Schema version 0.1.0, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.RecipeEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

Edits per call: 4,096. One `OnRegistry` callback (one phase of one entry point) or one `Registry.Change` takes at most this many edits of this registry; a call with more is refused whole, with the reason in kiln.log and registry.json, and nothing of it applies (the other calls do). The default for a registry whose schema declares none (4,096).

## Ids

- Game entries: `game:<stable name>`. Mod entries: `<mod id>:<name>`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- The stable name starts as the recipe's debug name in lower case. A recipe without a debug name is game:recipe_<its 32-bit recipe id as 8 hex digits> (the name Kiln's content selectors already give it). Recipes and items are separate registries: a recipe may have the same id as the item it makes. When two recipes would get the same name, the one that held it first keeps it and the other is game:<name>~<first 8 hex digits of its GUID>.

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `inputs` | list of entries, at most 32 | now | list | 0.1.0 | mapped | now | What one craft takes: an item or an input category, and a count. |
| `inputs[].item` | ref items, optional | now | set | 0.1.0 | mapped | now | The item; absent when the input is a category. |
| `inputs[].category` | ref inputCategories, optional | now | set | 0.1.0 | mapped | now | An input category (any item of it); absent when the input is an item. |
| `inputs[].count` | int 1..65535 | now | number | 0.1.0 | mapped | now | How many of the item or category one craft takes. |
| `outputs` | list of entries, at most 32 | now | list | 0.1.0 | mapped | now | What one craft makes. |
| `outputs[].item` | ref items | now | set | 0.1.0 | mapped | now | The item one craft makes. |
| `outputs[].count` | int 1..65535 | now | number | 0.1.0 | mapped | now | How many of the item one craft makes. |
| `duration` | time s 0..86400 | now | number | 0.1.0 | mapped | now | How long a station takes for one craft. |
| `unlock` | enum (game, always) | now | set | 0.1.0 | mapped | localOnly | When players see the recipe: game = the game's own knowledge requirement, always = from the start. Only the crafting menu reads it (a client without Kiln keeps the game's). Read by game clients only. |
| `station` | ref stations | restart | read-only | 0.1.0 | mapped | serverOnly | The station the recipe belongs to: a stations id, none for a recipe of the player's own crafting. Read-only for game recipes: the game sorts recipes into stations once at start. |

## Traits

Selectors ask for these with `has` / `hasNot`.

| Trait | Means |
|---|---|
| `cooking` | a cooking recipe |
| `upgrade` | an upgrade recipe |

## Entries

- Add (stage add, id `<your mod id>:<name>`): yes.
- Add requires GameStart and an explicit present Kiln `like` id; clone chains end at a game entry. Kiln retains the stable identity and pins the structural base install-wide. Use a new id for an incompatible base.
- Remove (hidden): GameStart only. Hides the recipe from station lists and the all-recipes/crafting view. Keeps the recipe record, id lookup and already-running crafts.
- Raw game paths in edits (the Advanced tier): allowed.

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
