## Edit it from a data pack

`abilities` is [kiln.gamedata](../../services/gamedata.md)'s sequence data under Kiln names: every action the game plays as a
timed sequence. An id is the name of the ability's first part in lower case (`game:attack_player_staff_fixedanim_fireball_fast`
is Fireball I's quick cast). The fields are four moments, in seconds from the ability's start:

| Field | The moment |
|---|---|
| `timing.release` | the ability releases what it spawns (a projectile, a thrown item, a summon) |
| `timing.hit` | its first hit or area effect starts |
| `timing.cost` | it pays its cost (mana, stamina, a charge) |
| `timing.buff` | it applies its first buff |

- `"field": "timing.release", "op": 0.45`, or `{"mul": 0.8}`. Each field is the **first** such moment: every event of the same kind
  at that moment moves with it, and every other event stays (later moments of that kind, the sounds, the visual effects and the
  animation events).
- An ability that has no such moment refuses the edit; a time after the ability ends never comes.
- A spell's costs are its item's (`items.manaCost`, `items.staminaCost`); the game has no cooldown for players beyond this timing.
- No raw paths and no entry add or remove: a mod's own abilities are kiln.gamedata clones or ability blocks. The old `sequences`
  data-pack section (the game's own paths) keeps working; a registry edit is the later layer and wins (reported).

## Edit it from C#

```csharp
Kiln.Registry.Registry.For(context).Alter(view =>
{
    var cast = view.Abilities["game:attack_player_staff_fixedanim_fireball_fast"];
    cast.Timing.Release = TimeSpan.FromSeconds(0.45);      // the projectile leaves earlier...
    cast.Timing.Cost = TimeSpan.FromSeconds(0.45);         // ...and the mana is paid at the same moment
});
```

`view.Abilities` is a `RegistryEntries<AbilityEntry>` with one group, `Timing` (`Release`, `Hit`, `Cost`, `Buff`; `TimeSpan`s). The
complete mod: [SpellRegistry sample](../../../../mods/samples/SpellRegistry/SpellRegistryMod.cs).

## Views

`RegistryViews.Find("abilities")` is the stable view; the buff an ability applies is the [buffs](buffs.md) registry's
([views.md](views.md)).

## Worked example

Fireball I releases its projectile at 0.45 s instead of 0.65 s and pays its mana at the same moment:

```json
{
  "format": 1,
  "registry": {
    "alter": [
      { "registry": "abilities", "select": { "id": "game:attack_player_staff_fixedanim_fireball_fast" }, "field": "timing.release", "op": 0.45 },
      { "registry": "abilities", "select": { "id": "game:attack_player_staff_fixedanim_fireball_fast" }, "field": "timing.cost", "op": 0.45 }
    ]
  }
}
```

This is [`mods/samples/SpellRegistry`](../../../../mods/samples/SpellRegistry/content/spell-registry.json)'s pack
([SpellTweaks](../../../../mods/samples/SpellTweaks) moves each event by its kiln.gamedata path instead).

## In multiplayer

A `timing.*` edit makes the server spawn, hit, pay and apply buffs at the new time. The Joined client column says `now`: a Kiln
client gets the new time through the content sync (kiln.gamedata's sequence values) and plays the ability at it. A vanilla client
keeps playing the ability's animation and effects at the old times. One exception: a time moved past a later event of the same
kind in that ability is not sent, so Kiln clients keep the old time there too (the server still acts at the new one); a move that
keeps the order (earlier, or later but not past such an event) is sent. A pack of timing edits is `clients: kiln`
([choosing clients](README.md#registry-only-packs-choosing-clients)).

## Known limits

- Only the first moment of each kind is a field; later events of a kind move through the older `sequences` paths.
- The ability's sounds, visual effects and animation events are not fields: they stay where the game put them.
- Not yet seen in a real game: an ability's timing on a joined client, and a moved moment against the animation.
