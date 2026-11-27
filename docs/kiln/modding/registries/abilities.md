# The `abilities` registry

<!-- Generated from schema/registry/abilities.json by tools/registrygen/registrygen.py. Do not edit. -->

Every action the game plays as a timed sequence: spell casts, weapon attacks, abilities (the double jump, the dodge, blink), item uses and creature attacks. Their timing: when a cast releases what it spawns, when a hit lands, when the cost is paid and when a buff is applied. Costs themselves are the item's (items.manaCost, items.staminaCost); the game has no cooldown for players beyond this timing.

Schema version 0.1.0, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.AbilityEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

Edits per call: 4,096. One `OnRegistry` callback (one phase of one entry point) or one `Registries.Change` takes at most this many edits of this registry; a call with more is refused whole, with the reason in kiln.log and registry.json, and nothing of it applies (the other calls do). The default for a registry whose schema declares none (4,096).

## Ids

- Game entries: `game:<stable name>`. Mod entries: `(none: a mod adds an ability as a kiln.gamedata sequence clone or an ability block)`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- The stable name starts as the name of the ability's first part in lower case (Attack_Player_Staff_FixedAnim_Fireball_fast is game:attack_player_staff_fixedanim_fireball_fast). When two abilities would get the same name, the one that held it first keeps it and the other is game:<name>~<first 8 hex digits of its GUID>.

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

Groups of fields:

- `timing`: When the ability does things, in seconds from its start. Each field is the first such moment of the ability; every event of the same kind at that moment moves with it, and every other event stays where it is (later moments of that kind, the ability's sounds, visual effects and animation events). The next time the ability plays takes the new time. An ability without such a moment refuses the edit; a time after the ability ends never comes.

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `timing.release` | time s 0..600 | now | number | 0.1.0 | mapped | now | When the ability releases what it spawns: a spell's projectile, a thrown item, a summon. |
| `timing.hit` | time s 0..600 | now | number | 0.1.0 | mapped | now | When the ability's first hit or area effect starts (a weapon swing's hit, a slam's area). |
| `timing.cost` | time s 0..600 | now | number | 0.1.0 | mapped | now | When the ability pays its cost (mana, stamina, a charge of the item). |
| `timing.buff` | time s 0..600 | now | number | 0.1.0 | mapped | now | When the ability applies its first buff. |

## Entries

- Add (stage add, id `<your mod id>:<name>`): no.
- Remove: no.
- Raw game paths in edits (the Advanced tier): refused.

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
