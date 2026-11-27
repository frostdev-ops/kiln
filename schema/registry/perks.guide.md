## Edit it from a data pack

The ids are the ones `items.perks` holds (`game:perk_damage_ice`). A perk's `value` is the number its tooltip shows (a share as
a fraction where the tooltip shows a percentage; below 0 for a perk that lowers something, such as a cost). The simple armour
perks also have `armor.*`: the share of the item's armour they add per kind.

- `"field": "value", "op": {"mul": 2}`; `"field": "armor.fire", "op": 0.25`; `"armor.enabled"` is `true` or `false`.
- A perk whose game value is a whole number takes whole numbers only: a fraction is refused (`not_exact: ...`), never rounded.
- `damage.*` (the simple damage perks' bonus) is **refused** on this build until a Kiln update traces when the game reads it.
- A perk's effect reads `value` when it starts: equipping the item, unlocking the perk. An effect already running keeps its value
  until it starts again.

## Edit it from C#

```csharp
registry.Alter(view =>
{
    view.Perks["game:perk_damage_ice"].Value = view.Perks["game:perk_damage_ice"].Value * 2;
    view.Perks["game:perk_max_adder_health_t7"].Value = 150;     // a whole-number value: a fraction would be refused
});
```

`view.Perks` is a `RegistryEntries<PerkEntry>`; `Value` is a double and `Armor` a group (`Enabled`, `Physical`, `Magical`, `Cut`,
`Pierce`, `Blunt`, `Fire`, `Ice`, `Shroud`, `Lightning`). To give an item a perk, edit the item's `perks` list ([items](items.md)).

## Views

`RegistryViews.Find("perks")` is the stable view; `items.perks` holds `Ref`s into it ([views.md](views.md)).

## Worked example

One perk grants 10 more, and every item's ice perk doubles:

```json
{
  "format": 1,
  "registry": {
    "alter": [
      { "registry": "perks", "select": { "id": "game:perk_max_adder_health_t7" }, "field": "value", "op": { "add": 10 } },
      { "registry": "perks", "select": { "id": "game:perk_damage_ice" }, "field": "value", "op": { "mul": 2 } }
    ]
  }
}
```

The first is [`mods/samples/RegistryEdits`](../../../../mods/samples/RegistryEdits/content/creatures.json)'s; the second is the
README's. The values apply at the next use and the tooltip follows.

## In multiplayer

`value` and `armor.*` are synced (Joined client `now`): a joined Kiln client gets the server's layer, so its tooltip and armour
match; a vanilla client keeps its own numbers while the server's decide. `damage.*` is `unknown` (refused). A pack of perk edits is
`clients: kiln` for matching tooltips ([choosing clients](README.md#registry-only-packs-choosing-clients)).

## Known limits

- `damage.*` is refused until the game's read time is traced; it ships as `unproven` on this build.
- A perk's value is read when its effect starts: effects running at the edit keep the old value.
- A perk is not added or removed here: the items' `perks` lists decide where it appears.
- Not yet seen in a real game: a perk edit in play, and a joined client's tooltip after a server edit.
