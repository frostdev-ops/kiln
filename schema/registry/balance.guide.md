## Edit it from a data pack

The registry has one entry, `game:balance`, and every field applies at the next **game start** (`restart`): the game reads the
table once. Edits use the same shape as every registry ([items](items.md#edit-it-from-a-data-pack)):

- A number field: `"field": "levelCap", "op": 60`, or `{"mul": 1.5}`, `{"add": 5}`.
- A list of fixed length is edited one element at a time: `"field": "xpPerLevel[2]"` (exactly 5 elements),
  `"altarsPerFlameLevel[4]"` (one value per flame level, 0 to `flameLevelMax`). `[*]` takes every element.
- `buildzoneSizesPerAltarLevel` is a list of entries: edit one with `"buildzoneSizesPerAltarLevel[3].x"`. It is edited in place:
  an edit that changes its length is refused at start.
- `flameLevelMax` and `gear.*` are read-only (Kiln's gear formulas follow them).
- An older data pack's `gearRules` (durability, rarity durability, socket chance, unlock cost) is a **front-end** of this registry
  since 0.2.0-dev.32: Kiln writes its values through this registry's mapping when that gives the game the same bytes
  ([older paths as front-ends](README.md#older-paths-as-front-ends-020-dev32);
  [gear rules guide](../gear-rules.md)).

## Edit it from C#

```csharp
[RegistryEntryPoints(RegistryEntryPoint.GameStart)]
public sealed class Levels : Mod
{
    public override void OnRegistry(RegistryContext context)
    {
        var registry = Kiln.Registry.Registry.For(context);
        registry.Alter(view =>
        {
            var balance = view.Balance;                    // the one entry, no id needed
            balance.LevelCap = 60;
            balance.XpPerLevel[2] = 3000;                  // a fixed list: set an element
            balance.SocketChance.Epic = 0.75;
            balance.Durability.Growth = balance.Durability.Growth * 2;
        });
    }
}
```

`view.Balance` is a `BalanceEntry` (`LevelCap`, `SkillPointsPerLevel`, `XpPerLevel`, `XpCurve`, `AltarsPerFlameLevel`,
`BuildzoneSizesPerAltarLevel`, `Durability`, `RarityDurability`, `SocketChance`, `UnlockCost`, `FogResistance`, `Gear`). Restart
fields are written at **GameStart** (and, in a mod that declares `ModLoad`, `WorldLoad` or `HotReload`, such an edit is refused
there with `RestartRequired`).

## Views

`RegistryViews.Find("balance")` is the stable view with the one entry `game:balance`; it reads the values the stage composed.

## Worked example

Level cap 60, more experience for level 3, and epic weapons socketed three times in four:

```json
{
  "format": 1,
  "registry": {
    "alter": [
      { "registry": "balance", "select": { "id": "game:balance" }, "field": "levelCap", "op": 60 },
      { "registry": "balance", "select": { "id": "game:balance" }, "field": "xpPerLevel[2]", "op": { "add": 100 } },
      { "registry": "balance", "select": { "id": "game:balance" }, "field": "socketChance.epic", "op": 0.75 }
    ]
  }
}
```

The first two are what `mods/tests/registry-stage-test` runs in a lab. The values apply at the next start; the mod is
`"side": "both"`, `"clients": "mod"`.

## In multiplayer

Every field here is `restart` for a joined client: ContentSync does not carry it. **Both sides need the same boot values at
process start**: the server and every client run the mod (`side: both`, `clients: mod`), and the join compares the resolved
values (`kiln.registry.boot`: [join compatibility](../../network.md#registry-boot-values-and-order)). A rejoin alone does not
change a value. `gear.*` is server-only (read-only).

## Known limits

- Saved characters keep their level, spent points and experience: lowering `levelCap` or `skillPointsPerLevel`, or changing the
  experience curve, is applied with a warning ([edits that change existing saves](README.md#edits-that-change-existing-saves));
  a shorter `buildzoneSizesPerAltarLevel` is refused.
- The game's code allows at most 16 altars whatever `altarsPerFlameLevel` says; [limits](limits.md) (`maxAltars`) raises that.
- Not yet seen in a real game: the level cap and experience values in a client's UI after a join, and the base limits past 16.
