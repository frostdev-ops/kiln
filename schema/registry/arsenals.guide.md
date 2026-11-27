## Edit it from a data pack

An arsenal is the list of attacks an enemy picks from. Enemies of one kind share one, so an edit reaches every enemy that uses
it. The id is the first creature that uses it (`game:enemy_vukah_melee`); `creatures.arsenal` tells which arsenal a creature has.

- `attacks` is in the game's order: `"field": "attacks[2].chance"`, or `attacks[*].chance` for every attack.
- Each attack has `chance` (0 to 1), `threat`, `minRange`, `maxRange` and `maxHeight` (metres; 0 for no limit of its own): all
  number fields (`"op": {"mul": 0.5}`).
- An arsenal **keeps its attacks**: only their values change. Appending or removing an attack is refused.

## Edit it from C#

```csharp
registry.Alter(view =>
{
    var arsenal = view.Arsenals["game:enemy_vukah_melee"];
    arsenal.Attacks[0].Chance = 0.5;
    foreach (var attack in arsenal.Attacks) attack.MaxRange = attack.MaxRange * 1.25;
});
```

`view.Arsenals` is a `RegistryEntries<ArsenalEntry>`; `Attacks` is a list of `ArsenalAttack` (`Index`, `Chance`, `Threat`, `MinRange`,
`MaxRange`, `MaxHeight`). The attack's order is the game's, so name an attack by its index only after reading the arsenal on this
build.

## Views

`RegistryViews.Find("arsenals")` is the stable view; `creatures.arsenal` is a `Ref` into it ([views.md](views.md)).

## Worked example

The Vukah warrior's arsenal picks each attack half as often:

```json
{
  "format": 1,
  "registry": {
    "alter": [
      { "registry": "arsenals", "select": { "id": "game:enemy_vukah_melee" }, "field": "attacks[*].chance", "op": { "mul": 0.5 } }
    ]
  }
}
```

This is [`mods/samples/RegistryEdits`](../../../../mods/samples/RegistryEdits/content/creatures.json)'s edit. An enemy reads its
arsenal at each decision, so enemies already in the world follow at their next one.

## In multiplayer

Arsenals are the server's (`serverOnly`): an enemy decides on the server and a vanilla client sees only the result. A pack with
only arsenal edits is `clients: none` ([choosing clients](README.md#registry-only-packs-choosing-clients)).

## Known limits

- No attack is added or removed, and an attack's animation or damage is not a field here (those are the abilities' timing and
  the creature's damage fields).
- A `chance` of 0 on every attack of an arsenal leaves the enemy nothing to pick: Kiln does not check the sum.
- Not yet seen in a real game: the effect of a chance or range edit on a real fight.
