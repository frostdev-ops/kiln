## Edit it from a data pack

One entry, `game:difficulty`. For each setting a world can choose, the entry has a `min` and a `max`: the bounds the game keeps a
world's own (Custom) settings inside when it reads them **at start** (`restart`). The presets (Default, Relaxed, Hard, Survival)
keep their own values. Setting names are the server's `enshrouded_server.json` `"gameSettings"` keys.

- `"field": "enemyDamageFactor.max", "op": 10`; `"op": {"min": 100, "raiseOnly": true}` raises a bound to 100 unless another mod
  already set it higher.
- A time setting (`fromHungerToStarving`, `dayTimeDuration`, `nightTimeDuration`) is in seconds.
- To **use** a wider bound, set the value in the world's settings (the server file or Kiln's settings service): this registry
  only moves what the game accepts.

## Edit it from C#

```csharp
[RegistryEntryPoints(RegistryEntryPoint.GameStart)]
public sealed class WiderBounds : Mod
{
    public override void OnRegistry(RegistryContext context)
    {
        Kiln.Registry.Registries.For(context).Alter(stage =>
        {
            var bounds = stage.Difficulty.EnemyDamageFactor;
            bounds.Max = Math.Max(bounds.Max, 10);            // never lower another mod's higher bound
        });
    }
}
```

`stage.Difficulty` is a `DifficultyEntry`: one group per setting (`PlayerHealthFactor`, `EnemyDamageFactor`, `DayTimeDuration`, ...)
with `Min` and `Max`. [RegistryEdits sample](../../../../mods/samples/RegistryEdits/RegistryEditsMod.cs).

## Views

`RegistryViews.Find("difficulty")` is the stable view. Kiln's settings service reads these bounds too: where a mod set one,
`Settings.Set` accepts a value inside it ([services/settings.md](../../services/settings.md#the-registrys-bounds-since-dev32)).

## Worked example

The enemy settings may go up to 100 (10,000 %), as XHL's Enemy Difficulty Limits does; a bound another mod set higher stays:

```json
{
  "format": 1,
  "registry": {
    "alter": [
      { "registry": "difficulty", "select": { "id": "game:difficulty" }, "field": "enemyDamageFactor.max", "op": { "min": 100, "raiseOnly": true } },
      { "registry": "difficulty", "select": { "id": "game:difficulty" }, "field": "enemyHealthFactor.max", "op": { "min": 100, "raiseOnly": true } }
    ]
  }
}
```

This is two of the edits of [`mods/samples/WorldRules`](../../../../mods/samples/WorldRules/content/world-rules.json). The values apply at
the next start.

## In multiplayer

Every field is `restart`: nothing syncs, and **every player needs the mod** (`side: both`, `clients: mod`); the join compares the
resolved boot values ([join compatibility](../../network.md#registry-boot-values-and-order)). The settings screen's slider steps
are a game client's own and not covered here (resource patches still edit them).

## Known limits

- A world's own setting outside the new bounds is clamped into them when the game reads the settings at start; a **narrower**
  bound than before warns ([edits that change existing saves](README.md#edits-that-change-existing-saves)).
- Not covered: what a mod does beyond a number (Enemy Difficulty Limits' wider perception stays its own hook).
- Not yet seen in a real game: a client's settings screen after the bounds move.
