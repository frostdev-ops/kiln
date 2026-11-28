# The `arsenals` registry

<!-- Generated from schema/registry/arsenals.json by tools/registrygen/registrygen.py. Do not edit. -->

The enemies' arsenals: the attacks an enemy chooses from, with their chances, threat and ranges. Enemies of one kind share an arsenal, so an edit reaches every enemy that uses it, also those already in the world (an enemy reads its arsenal at each decision).

Schema version 0.1.0, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.ArsenalEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

Edits per call: 4,096. One `OnRegistry` callback (one phase of one entry point) or one `Registries.Change` takes at most this many edits of this registry; a call with more is refused whole, with the reason in kiln.log and registry.json, and nothing of it applies (the other calls do). The default for a registry whose schema declares none (4,096).

## Ids

- Game entries: `game:<stable name>`. Mod entries: `<mod id>:<name>`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- Arsenals have no names in the game. An arsenal that creatures use is named after the first of their templates in name order, in lower case (game:enemy_vukah_melee); an arsenal no creature uses is game:arsenal_<first 8 hex digits of its GUID>. When two arsenals would get the same name, the one that held it first keeps it and the other is game:<name>~<first 8 hex digits of its GUID>.

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `attacks` | list of entries, at most 256 | now | list | 0.1.0 | mapped | serverOnly | The arsenal's attacks in the game's order. Index i in a field path (attacks[2].chance) is that order. An arsenal keeps its attacks: only their values change (no attack is added or removed). |
| `attacks[].chance` | float 0..1 | now | number | 0.1.0 | mapped | serverOnly | The chance the enemy picks this attack when it may (0 to 1). |
| `attacks[].threat` | float 0..1000 | now | number | 0.1.0 | mapped | serverOnly | How threatening the attack is to the enemy's choice of target and timing. |
| `attacks[].minRange` | float m 0..10000 | now | number | 0.1.0 | mapped | serverOnly | The nearest a target may be for this attack, in metres. |
| `attacks[].maxRange` | float m 0..10000 | now | number | 0.1.0 | mapped | serverOnly | The farthest a target may be for this attack, in metres (0: no limit of its own). |
| `attacks[].maxHeight` | float m 0..10000 | now | number | 0.1.0 | mapped | serverOnly | The most a target may be above or below the enemy for this attack, in metres (0: no limit of its own). |

## Entries

- Add (stage add, id `<your mod id>:<name>`): no.
- Remove: no.
- Raw game paths in edits (the Advanced tier): allowed.

## Edit it from a data pack

An arsenal is the list of attacks an enemy picks from. Enemies of one kind share one, so an edit reaches every enemy that uses
it. The id is the first creature that uses it (`game:enemy_vukah_melee`); `creatures.arsenal` tells which arsenal a creature has.

- `attacks` is in the game's order: `"field": "attacks[2].chance"`, or `attacks[*].chance` for every attack.
- Each attack has `chance` (0 to 1), `threat`, `minRange`, `maxRange` and `maxHeight` (metres; 0 for no limit of its own): all
  number fields (`"op": {"mul": 0.5}`).
- An arsenal **keeps its attacks**: only their values change. Appending or removing an attack is refused.

## Edit it from C#

```csharp
registry.Alter(stage =>
{
    var arsenal = stage.Arsenals["game:enemy_vukah_melee"];
    arsenal.Attacks[0].Chance = 0.5;
    foreach (var attack in arsenal.Attacks) attack.MaxRange = attack.MaxRange * 1.25;
});
```

`stage.Arsenals` is a `RegistryEntries<ArsenalEntry>`; `Attacks` is a list of `RegistryArsenalAttack` (`Index`, `Chance`, `Threat`, `MinRange`,
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
