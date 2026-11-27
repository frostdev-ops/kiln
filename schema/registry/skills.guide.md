## Edit it from a data pack

A node's id is `game:skill_<its node id>` (`game:skill_4475e8dd` is Airborne Architect, the glider build node). Only `cost` is
editable: the other fields tell the node's kind and class and what it unlocks, and select nodes by trait (`active`: it unlocks
an ability; `leveled`: it has levels).

- `"field": "cost", "op": 0` or `{"add": -1, "min": 0}`. A node with levels charges its cost again for each level.
- Raw game paths are refused here.
- Nodes a mod **adds** through kiln.skills (`skills/*.json`, `GameTree.Define`) are not entries of this registry: their
  definition holds their cost ([skills service](../../services/skills.md)).

## Edit it from C#

```csharp
registry.Alter(stage =>
{
    stage.Skills["game:skill_4475e8dd"].Cost = 0;                       // Airborne Architect is free
    foreach (var node in stage.Skills.Select())
        if (node.Kind == SkillNodeKind.Active) node.Cost = Math.Max(0, node.Cost - 1);
});
```

`stage.Skills` is a `RegistryEntries<SkillNodeEntry>`; `Cost` is the only writable property (`MaxLevel`, `Kind`, `Class` and
`Unlocks` read the node). [RegistryEdits sample](../../../../mods/samples/RegistryEdits/RegistryEditsMod.cs).

## Views

`RegistryViews.Find("skills")` is the stable view (243 nodes on the current server build); `unlocks` is a `Ref` to the `items`
view (the ability item the node unlocks) ([views.md](views.md)).

## Worked example

Airborne Architect costs nothing, and every node that unlocks an ability costs one point less, never less than nothing:

```json
{
  "format": 1,
  "registry": {
    "alter": [
      { "registry": "skills", "select": { "id": "game:skill_4475e8dd" }, "field": "cost", "op": 0 },
      { "registry": "skills", "select": { "id": "*", "has": ["active"] }, "field": "cost", "op": { "add": -1, "min": 0 } }
    ]
  }
}
```

This is [`mods/samples/SkillCosts`](../../../../mods/samples/SkillCosts/content/skill-costs.json).

## In multiplayer

`cost` is `now`: the server's edit is kiln.gamedata's skills slot of that node, which the content sync carries. A Kiln client
writes it into its own skill tree resource, so its skill screen and its point count follow at the next draw; a vanilla client keeps
the game's costs on its screen while the server's unlock check decides. A pack of skill-cost edits is `clients: kiln`
([choosing clients](README.md#registry-only-packs-choosing-clients)). The other fields are `serverOnly` and read-only.

## Known limits

- A character in a save whose unlocked nodes now cost more than their skill points gets the game's **free reset** of the whole
  tree at their next join (the points come back; the nodes have to be unlocked again). Raising a cost warns
  ([edits that change existing saves](README.md#edits-that-change-existing-saves)); lowering is safe.
- `maxLevel` is read-only: a saved level above it would reset the tree at the next join.
- Not yet seen in a real game: a joined client's skill screen after a server edit.
