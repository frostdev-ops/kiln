## Edit it from a data pack

`buffs` is [kiln.gamedata](../../services/gamedata.md)'s buff data under Kiln names. A game buff has no debug name: its id is `game:buff_<its 32-bit id as 8 hex digits>`
(`game:buff_5f425faf` is the Poisoned debuff, the id kiln.gamedata shows). Fields: `duration` (seconds), `stacking` (what applying it again does), and the
read-only `kind` and `saved`.

- `"field": "duration", "op": 20`; `"field": "stacking", "op": "refresh"`.
- A buff **applied with a time of its own** (an item, an ability or a program that gives one) keeps it: `duration` is the time
  of an application that gives none. A food buff's time is also scaled by the world's food buff duration setting.
- No raw paths and no entry add or remove: a mod's own buffs are kiln.gamedata clones or blocks. The old `buffs` data-pack
  section (the game's own paths) keeps working; a registry edit is the later layer and wins (reported).

## Edit it from C#

```csharp
[RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad | RegistryEntryPoint.HotReload)]
public sealed class Poison : Mod
{
    public override void OnRegistry(RegistryContext context)
    {
        if (!Kiln.Registry.Registries.Available) return;                      // kiln.registry: the host's registry transport
        Kiln.Registry.Registries.For(context).Alter(stage =>
        {
            var poisoned = stage.Buffs["game:buff_5f425faf"];
            poisoned.Duration = TimeSpan.FromSeconds(20);
            Log.Info($"Poisoned: {poisoned.Duration.TotalSeconds:0} s, {poisoned.Kind}, applied again: {poisoned.Stacking}");
        });
    }
}
```

`stage.Buffs` is a `RegistryEntries<BuffEntry>` (`Duration` a `TimeSpan`, `Stacking` a `BuffStacking`, `Kind` and `Saved`
read-only). The complete mod: [SpellRegistry sample](../../../../mods/samples/SpellRegistry/SpellRegistryMod.cs).

## Views

`RegistryViews.Find("buffs")` is the stable view; `abilities.timing.buff` moves *when* an ability applies a buff, this
registry says how long it lasts ([abilities](abilities.md), [views.md](views.md)).

## Worked example

Poisoned lasts 20 seconds and a second application restarts it:

```json
{
  "format": 1,
  "registry": {
    "alter": [
      { "registry": "buffs", "select": { "id": "game:buff_5f425faf" }, "field": "duration", "op": 20 },
      { "registry": "buffs", "select": { "id": "game:buff_5f425faf" }, "field": "stacking", "op": "refresh" }
    ]
  }
}
```

The next new application takes the time; a buff already running keeps its end.

## In multiplayer

Buff fields are the server's alone (`serverOnly`): the server applies the buff and a client shows the time the server gave it. A
pack of buff edits is `clients: none` ([choosing clients](README.md#registry-only-packs-choosing-clients)).

## Known limits

- `replaceAtEnd` is applied by the game as `replace` on this build. A buff in the same place as another (a food slot) always
  replaces it, whatever `stacking` says.
- A running buff keeps its end; refresh restarts it with the length it started with.
- Not yet seen in a real game: a buff's timing on a joined client.
