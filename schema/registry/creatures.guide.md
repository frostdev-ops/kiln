## Edit it from a data pack

A creature's id is its template's: `game:enemy_vukah_melee`. **An edit is global to the creature's kind**, never one creature
in the world (kiln.creatures keeps its per-creature setters for that). Select by id, glob or trait (`"has": ["enemy"]`;
`wildlife`, `animal` and `villager` too); creatures without a value of a field are named below.

- Number fields take values and relative operations (`"op": {"mul": 1.5}`); `faction`, `loot.disabled` are `set`
  (`"op": "scavenger"`, `"op": true`).
- A field a creature does not have (most have no size of their own; only enemies have sight and an arsenal) reads `null` there:
  an absolute value is refused for that creature alone, and a relative operation refuses the edit. Select the creatures that have
  the field (`"has": ["enemy"]`) or name them.
- `kind`, `arsenal`, `tameable` and `favoriteBait` are read-only. The wild animals' taming: `tamingSteps`
  ([taming guide](../taming.md)).

## Edit it from C#

```csharp
registry.Alter(stage =>
{
    var vukah = stage.Creatures["game:enemy_vukah_melee"];
    vukah.Sight.Range = vukah.Sight.Range * 1.5;
    vukah.Resist.Fire = 2.0;                               // takes double fire damage; 0 would make it immune
    foreach (var wolf in stage.Creatures.Select("game:enemy_wildbeast_wolf_*"))
        if (wolf.Kind == CreatureKind.Enemy) wolf.Level = 12;
    var dog = stage.Creatures["game:animal_wildlife_dog_05_white_ag2"];
    if (dog.Tameable) dog.TamingSteps = 3;
});
```

`stage.Creatures` is a `RegistryEntries<CreatureEntry>`. A typed property reads 0 (or false) for a creature that has no value of
that field: tell such creatures by `Kind` or id, never by a 0, which for `Resist.*` would mean immune
([RegistryEdits sample](../../../../mods/samples/RegistryEdits/RegistryEditsMod.cs)).

## Views

`RegistryViews.Find("creatures")` is the stable view (689 creatures on the current server build). Its `arsenal` field is a `Ref`
to the `arsenals` view, `favoriteBait` a `Ref` to the game's templates. The advanced view `template` lists every template
(12,269) and reports `CoveredBy` where this registry maps the member ([views.md](views.md)).

## Worked example

Every Vukah enemy sees half again as far, early-game wolves spawn at level 12 (when their spawner sets no level of its own), and
the white wild dog takes 3 pettings to tame:

```json
{
  "format": 1,
  "registry": {
    "alter": [
      { "registry": "creatures", "select": { "id": "game:enemy_vukah_*", "has": ["enemy"] }, "field": "sight.range", "op": { "mul": 1.5 } },
      { "registry": "creatures", "select": { "id": "game:enemy_wildbeast_wolf_earlygame" }, "field": "level", "op": 12 },
      { "registry": "creatures", "select": { "id": "game:animal_wildlife_dog_05_white_ag2" }, "field": "tamingSteps", "op": 3 }
    ]
  }
}
```

This is [`mods/samples/RegistryEdits`](../../../../mods/samples/RegistryEdits/content/creatures.json)'s pack. `level`,
`levelMax`, `faction`, `scale`, `xp`, `damage.multiplier`, `resist.*`, `speed.*` and `loot.*` reach creatures that **spawn after**
the edit; `health.*`, `damage.perPlayer`, `sight.*` and `tamingSteps` are read at once by every creature of the entry.

## In multiplayer

The server owns creatures: every field is `serverOnly`, so no client holds a copy and a vanilla client plays against the changed
creatures unchanged. A pack with only creature edits is `clients: none`
([choosing clients](README.md#registry-only-packs-choosing-clients)).

## Known limits

- An edit cannot reach one creature, and cannot add or remove a kind.
- A creature already in the world keeps the values it spawned with (see the field's note).
- `levelMax` lower than before may load a saved creature at a lower level (a warning in `registry.json`).
- Not yet seen in a real game: creature, faction and speed edits in play on a joined server, and the taming steps a real
  animal takes after `tamingSteps`.
