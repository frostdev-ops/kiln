## Edit it from a data pack

A data pack is a `content/*.json` file of your mod: `"format": 1` and a `registry` section with the three stages `add`, `alter`
and `final` (what they mean: [README](README.md#stages-and-conflicts)). An edit names the registry, the entries (`select`),
a field and an operation:

- `select`: `{"id": "game:material_t1_wood_logs"}`, a glob (`"game:material_t1_*"`), or a trait (`"has": ["weapon"]`, see Traits).
  `where` keeps the entries whose fields meet a condition (`{"maxStack": ">=100"}`).
- `op`: a value (set), or `{"mul": 2, "max": 9999}` (relative operations stack with other mods' edits), or for `perks` a list
  operation: `"list": {"append": ["game:perk_damage_ice"]}`. One slot: `"field": "perks[0]"`.
- Add and remove an item (GameStart only): `{"registry": "items", "entry": "add", "id": "my-mod:crate", "like": "game:material_t1_wood_logs",
  "fields": {"maxStack": 50}}` in `add`, and `{"registry": "items", "entry": "remove", "select": {"id": "..."}}`.
  What removal hides and what it keeps: [entry add and remove](README.md#entry-add-and-remove-gamestart-only).

The gear numbers (`power`, `speed`, `damage.*`, `armor.*`) are what the tooltip shows at `levels.min`; Kiln solves the
game's own factors for them. A data pack's older `gear` section says the same things by other names
([content.md](../../services/content.md#gear-values-and-the-items-registry-020-dev32)).

## Edit it from C#

```csharp
[RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad)]
public sealed class Stacks : Mod
{
    public override void OnRegistry(RegistryContext context)
    {
        var registry = Kiln.Registry.Registries.For(context);
        registry.Alter(stage =>
        {
            stage.Items["game:material_t1_wood_logs"].MaxStack = 500;
            foreach (var weapon in stage.Items.Select("game:weapon_t1_*"))
                if (weapon.HasDamage) weapon.Power = weapon.Power * 2;
        });
        if (context.EntryPoint == RegistryEntryPoint.GameStart)
            registry.Add(stage => stage.Items.AddLike(new("my-mod:stout_logs"), new("game:material_t1_wood_logs"), item => item.MaxStack = 100));
    }
}
```

`stage.Items[id]` is an `ItemEntry`; `Select(pattern)` lists entries by glob. What a read inside the callback shows:
[C# authoring](README.md#c-authoring-api-0217). To change an item while a world runs, use `Registries.Change(stage => ...)`
(and `change.Revert()`); to follow a setting, declare the `WorldLoad` point and call
`Registries.Reauthor()` when it changes. [RegistryEdits sample](../../../../mods/samples/RegistryEdits/RegistryEditsMod.cs).

## Views

`RegistryViews.Find("items")` is the registry's stable view: every field above as it resolves on the running build
(`Mapped`, `Unmapped` or `Unproven`, with the reason), read-only. The advanced view over the game's item data
(`itemInfo`) lists members this registry does not map; where it does, the member reports `CoveredBy` (`"items.maxStack"`).
Write through the registry, read through either ([views.md](views.md)).

## Worked example

Every tier 1 material stacks twice as high (but never above 9,999), and one sword gets a socket and 10 more power. As a data pack (`content/stacks.json`):

```json
{
  "format": 1,
  "registry": {
    "alter": [
      { "registry": "items", "select": { "id": "game:material_t1_*" }, "field": "maxStack", "op": { "mul": 2, "max": 9999 } },
      { "registry": "items", "select": { "id": "game:weapon_t1_1h_sword_craft" }, "field": "socket", "op": true },
      { "registry": "items", "select": { "id": "game:weapon_t1_1h_sword_craft" }, "field": "power", "op": { "add": 10 } }
    ]
  }
}
```

`maxStack` applies at the next world load (`worldLoad`), `socket` and `power` at the next use (`now`; `socket` only for
copies made afterwards). In C# the data pack's `max` is `item.MaxStack = Math.Min(item.MaxStack * 2, 9999)`.

## In multiplayer

The server applies every edit. A joined Kiln client gets the item layer through the content sync (the Joined client column:
`now` for the gear numbers, perks, levels and costs; `worldLoad` for `maxStack`, which a client reads at its next world load;
`serverOnly` for `rolled`, `socket`, `hasDamage` and `hasArmor`), so its tooltips match. A vanilla client keeps the game's
numbers on its screen while the server's numbers decide what happens. For a pack with only item edits set `clients` to `kiln`
(matching tooltips) or `none` (vanilla players welcome; their tooltips differ). Adding or removing an item needs `kiln` at least,
and `mod` for assets of your own ([choosing clients](README.md#registry-only-packs-choosing-clients)).

## Known limits

- `fuel.burnTime` is refused on this build: no game code reads an item's burn time, so an edit would change nothing.
- `perks` and `socket` reach new copies only; a copy that exists keeps its rarity, perks and socket. A lower `maxStack`
  is guarded (stacks a save holds stay whole), a lower `levels.max` or a higher `levels.min` is refused for that entry
  ([edits that change existing saves](README.md#edits-that-change-existing-saves)).
- Not yet seen in a real game: a joined client's tooltips after a server edit, equipped copies after an item is removed,
  and an item added here on a vanilla client (it has no look for it).
