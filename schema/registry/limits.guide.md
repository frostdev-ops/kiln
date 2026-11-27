## Edit it from a data pack

One entry, `game:limits`: numbers the game keeps **in its code** (not in its data) which Kiln answers through hooks of its own,
installed only for a value that is not the game's. Both fields apply at the next start (`restart`), for the whole process.

- `"field": "maxAltars", "op": 20`: the most Flame Altars at any Flame level. The game's own is 16, and its altar check answers the
  smaller of `balance.altarsPerFlameLevel` at the world's flame level and this, so raise both to allow more (`balance.altarsPerFlameLevel[n]`).
  Past 16 the game's bases still count 16: altars past the 16th give build zones without the base features.
- `"field": "giftingRange", "op": 500`: how far apart two players may be to give each other an item, in metres (the game's own is 50).

## Edit it from C#

```csharp
[RegistryEntryPoints(RegistryEntryPoint.GameStart)]
public sealed class MoreAltars : Mod
{
    public override void OnRegistry(RegistryContext context)
    {
        Kiln.Registry.Registries.For(context).Alter(stage =>
        {
            stage.Limits.MaxAltars = 20;
            stage.Limits.GiftingRange = 500;
            for (var level = 0; level < stage.Balance.AltarsPerFlameLevel.Count; level++)
                stage.Balance.AltarsPerFlameLevel[level] = Math.Max(stage.Balance.AltarsPerFlameLevel[level], 20);
        });
    }
}
```

`stage.Limits` is a `LimitsEntry` (`MaxAltars`, `GiftingRange`).

## Views

`RegistryViews.Find("limits")` is the stable view. Its one entry has no game resource: its values are Kiln's hooks of the game's
code, and its mapping reads them from the game's code, never from data ([views.md](views.md)).

## Worked example

Twenty altars at every flame level, and gifts across 500 m (XHL's Unlimited Gifting Range and More Flame Altars, as numbers):

```json
{
  "format": 1,
  "registry": {
    "alter": [
      { "registry": "limits", "select": { "id": "game:limits" }, "field": "maxAltars", "op": 20 },
      { "registry": "limits", "select": { "id": "game:limits" }, "field": "giftingRange", "op": 500 },
      { "registry": "balance", "select": { "id": "game:balance" }, "field": "altarsPerFlameLevel[*]", "op": { "min": 20 } }
    ]
  }
}
```

The first two are `mods/tests/registry-world-test`'s edits. A mod's own hook of the same game check (More Flame Altars'
`AltarLimitHook`) is never refused: both hooks stay in one chain, the first that answers decides, and Kiln reports a conflict in
`registry.json` and the launcher's Conflicts view naming which one answers. Use one of the two.

## In multiplayer

Both fields are `restart`: **every player needs the mod** (`side: both`, `clients: mod`) and the join compares the resolved boot
values. The host checks a gift; a player's game uses `giftingRange` to offer it. The hook answers on whatever thread the game
asks.

## Known limits

- Lowering `maxAltars` below what saved bases hold is applied with a warning: what the game does with the extra altars is not
  traced ([edits that change existing saves](README.md#edits-that-change-existing-saves)).
- Past 16 altars the game's bases still count 16 (see above).
- Not yet seen in a real game: altars above 16 on a client, and gifting beyond 50 m.
