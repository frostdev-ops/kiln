# The `limits` registry

<!-- Generated from schema/registry/limits.json by tools/registrygen/registrygen.py. Do not edit. -->

Limits the game's code holds as fixed numbers, not data: Kiln answers the game's own check with the registry's value through a hook of its own, installed only when the value is not the game's. The game reads each at every check; Kiln takes the value at start.

Schema version 0.1.0, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.LimitsEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

Edits per call: 4,096. One `OnRegistry` callback (one phase of one entry point) or one `Registries.Change` takes at most this many edits of this registry; a call with more is refused whole, with the reason in kiln.log and registry.json, and nothing of it applies (the other calls do). The default for a registry whose schema declares none (4,096).

## Ids

- Game entries: `game:limits`. Mod entries: `(none: one entry)`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- One entry, game:limits.

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `maxAltars` | int 1..255 | restart | number | 0.1.0 | mapped | restart | The most Flame Altars the game allows at any Flame level: its altar check answers the smaller of balance.altarsPerFlameLevel at the world's Flame level and this. The game's own is 16, so raise both to allow more. The two are apart on purpose: the table is the progression (how many at each level), this is one ceiling over every level, and raising it past 16 is the choice to take what that costs, so a table edit (a relative one, add 5, too) never crosses 16 by itself; kiln.log says when the table asks for more than this allows. Past 16 the game's bases still count 16: altars past the 16th give build zones without the base features (comfort, base chests, survivor beds, animals, the journal), the network message of a world's zones counts them in one byte, and a player's game draws at most 32 zone borders at once. |
| `giftingRange` | float m 0..1000000 | restart | number | 0.1.0 | mapped | restart | How far apart two players may be for one to give the other an item, in metres. The game's own is 50 m. The host checks the gift; a player's game uses it to offer the gift. |

## Entries

- Add (stage add, id `<your mod id>:<name>`): no.
- Remove: no.
- Raw game paths in edits (the Advanced tier): refused.

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
