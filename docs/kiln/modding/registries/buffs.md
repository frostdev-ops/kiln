# The `buffs` registry

<!-- Generated from schema/registry/buffs.json by tools/registrygen/registrygen.py. Do not edit. -->

Every buff and debuff the game defines (food buffs, Well Rested, Poisoned, Burning, the slows and the shields of spells and potions): how long one lasts and what applying it again does.

Schema version 0.1.0, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.BuffEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

Edits per call: 4,096. One `OnRegistry` callback (one phase of one entry point) or one `Registries.Change` takes at most this many edits of this registry; a call with more is refused whole, with the reason in kiln.log and registry.json, and nothing of it applies (the other calls do). The default for a registry whose schema declares none (4,096).

## Ids

- Game entries: `game:buff_<its 32-bit buff id as 8 hex digits>`. Mod entries: `(none: a mod adds a buff as a kiln.gamedata clone or a status block)`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- A game buff has no debug name: its stable name is buff_ and its 32-bit buff id in lower-case hex (the id kiln.gamedata shows, and the one saves and the network carry), so the buff kiln.gamedata calls buff_cdbe9c80 is game:buff_cdbe9c80.

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `duration` | time s 0..86400 | now | number | 0.1.0 | mapped | serverOnly | How long the buff lasts when it is applied without a time of its own (an item, an ability or a program that gives one overrides it; a food buff's time is also scaled by the world's food buff duration setting). The next new application takes it; a buff already running keeps its end, and applying it again while it runs (refresh) restarts it with the length it started with. |
| `stacking` | enum (keep, refresh, replace, replaceAtEnd) | now | set | 0.1.0 | mapped | serverOnly | What applying the buff again does while it runs: keep (the running one stays and the new one is refused), refresh (the running one starts its time again), replace (the running one ends and a new one starts, its effects with it). replaceAtEnd: on this game build the game applies it as replace. A buff in the same place as another buff (a food slot) always replaces it. |
| `kind` | enum (food, buff, debuff, rested, terrain) | now | read-only | 0.1.0 | mapped | serverOnly | Where the buff sits on a character: a food slot, the buffs, the debuffs, the rested slot or the terrain debuff slot. |
| `saved` | bool | now | read-only | 0.1.0 | mapped | serverOnly | The buff stays on a character through a save and a load. |

## Entries

- Add (stage add, id `<your mod id>:<name>`): no.
- Remove: no.
- Raw game paths in edits (the Advanced tier): refused.

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
        Kiln.Registry.Registry.For(context).Alter(view =>
        {
            var poisoned = view.Buffs["game:buff_5f425faf"];
            poisoned.Duration = TimeSpan.FromSeconds(20);
            Log.Info($"Poisoned: {poisoned.Duration.TotalSeconds:0} s, {poisoned.Kind}, applied again: {poisoned.Stacking}");
        });
    }
}
```

`view.Buffs` is a `RegistryEntries<BuffEntry>` (`Duration` a `TimeSpan`, `Stacking` a `BuffStacking`, `Kind` and `Saved`
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
