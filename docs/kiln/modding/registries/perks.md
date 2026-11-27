# The `perks` registry

<!-- Generated from schema/registry/perks.json by tools/registrygen/registrygen.py. Do not edit. -->

The game's perks: what a perk on an item grants. Its value (the number its tooltip shows), the armour and the damage bonus of the simple perks.

Schema version 0.1.0, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.PerkEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

Edits per call: 4,096. One `OnRegistry` callback (one phase of one entry point) or one `Registry.Change` takes at most this many edits of this registry; a call with more is refused whole, with the reason in kiln.log and registry.json, and nothing of it applies (the other calls do). The default for a registry whose schema declares none (4,096).

## Ids

- Game entries: `game:<stable name>`. Mod entries: `<mod id>:<name>`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- The stable name starts as the perk's debug name in lower case (Perk_Damage_Ice is game:perk_damage_ice), the same ids items.perks holds. A perk without a debug name is game:perk_<first 8 hex digits of its GUID>. When two perks would get the same name, the one that held it first keeps it and the other is game:<name>~<first 8 hex digits of its GUID>.

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

Groups of fields:

- `armor`: The armour the perk adds, as a share of the item's armour per kind. Read whenever the wearer's armour is computed.
- `damage`: The damage bonus the perk adds, as a share per kind of damage. When the game reads it is not traced yet: refused until a Kiln update proves it.

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `value` | float -100000..100000 | now | number | 0.1.0 | mapped | now | The amount the perk grants, the number its tooltip shows (a share as a fraction where the tooltip shows a percentage; below 0 for a perk that lowers something, such as a cost). A perk's effect reads it when it starts: equipping the item, unlocking the perk. A perk whose game value is a whole number takes whole numbers only (a fraction is refused, never rounded). The simple armour and damage perks have none. |
| `armor.enabled` | bool | now | set | 0.1.0 | mapped | now | The perk adds armour (only the simple armour perks do in the game). |
| `armor.physical` | float 0..10 | now | number | 0.1.0 | mapped | now | The share of physical armour the perk adds. |
| `armor.magical` | float 0..10 | now | number | 0.1.0 | mapped | now | The share of magical armour the perk adds. |
| `armor.cut` | float 0..10 | now | number | 0.1.0 | mapped | now | The share of cut armour the perk adds. |
| `armor.pierce` | float 0..10 | now | number | 0.1.0 | mapped | now | The share of pierce armour the perk adds. |
| `armor.blunt` | float 0..10 | now | number | 0.1.0 | mapped | now | The share of blunt armour the perk adds. |
| `armor.fire` | float 0..10 | now | number | 0.1.0 | mapped | now | The share of fire armour the perk adds. |
| `armor.ice` | float 0..10 | now | number | 0.1.0 | mapped | now | The share of ice armour the perk adds. |
| `armor.shroud` | float 0..10 | now | number | 0.1.0 | mapped | now | The share of shroud armour the perk adds. |
| `armor.lightning` | float 0..10 | now | number | 0.1.0 | mapped | now | The share of lightning armour the perk adds. |
| `damage.enabled` | bool | worldLoad | set | 0.1.0 | unproven: when the game reads a perk's damage bonus is not traced yet | unknown | The perk adds damage (only the simple damage perks do in the game). |
| `damage.cut` | float 0..10 | worldLoad | number | 0.1.0 | unproven: when the game reads a perk's damage bonus is not traced yet | unknown | The share of cutting damage the perk adds. |
| `damage.pierce` | float 0..10 | worldLoad | number | 0.1.0 | unproven: when the game reads a perk's damage bonus is not traced yet | unknown | The share of piercing damage the perk adds. |
| `damage.blunt` | float 0..10 | worldLoad | number | 0.1.0 | unproven: when the game reads a perk's damage bonus is not traced yet | unknown | The share of blunt damage the perk adds. |
| `damage.fire` | float 0..10 | worldLoad | number | 0.1.0 | unproven: when the game reads a perk's damage bonus is not traced yet | unknown | The share of fire damage the perk adds. |
| `damage.ice` | float 0..10 | worldLoad | number | 0.1.0 | unproven: when the game reads a perk's damage bonus is not traced yet | unknown | The share of ice damage the perk adds. |
| `damage.shroud` | float 0..10 | worldLoad | number | 0.1.0 | unproven: when the game reads a perk's damage bonus is not traced yet | unknown | The share of shroud damage the perk adds. |
| `damage.lightning` | float 0..10 | worldLoad | number | 0.1.0 | unproven: when the game reads a perk's damage bonus is not traced yet | unknown | The share of lightning damage the perk adds. |
| `damage.poison` | float 0..10 | worldLoad | number | 0.1.0 | unproven: when the game reads a perk's damage bonus is not traced yet | unknown | The share of poison damage the perk adds. |
| `damage.healing` | float 0..10 | worldLoad | number | 0.1.0 | unproven: when the game reads a perk's damage bonus is not traced yet | unknown | The share of healing damage the perk adds. |
| `damage.wood` | int 0..1000000 | worldLoad | number | 0.1.0 | unproven: when the game reads a perk's damage bonus is not traced yet | unknown | The share of wood damage the perk adds. |
| `damage.stone` | int 0..1000000 | worldLoad | number | 0.1.0 | unproven: when the game reads a perk's damage bonus is not traced yet | unknown | The share of stone damage the perk adds. |
| `damage.metal` | int 0..1000000 | worldLoad | number | 0.1.0 | unproven: when the game reads a perk's damage bonus is not traced yet | unknown | The share of metal damage the perk adds. |
| `damage.explosion` | int 0..1000000 | worldLoad | number | 0.1.0 | unproven: when the game reads a perk's damage bonus is not traced yet | unknown | The share of explosion damage the perk adds. |

## Entries

- Add (stage add, id `<your mod id>:<name>`): no.
- Remove: no.
- Raw game paths in edits (the Advanced tier): allowed.

## Edit it from a data pack

The ids are the ones `items.perks` holds (`game:perk_damage_ice`). A perk's `value` is the number its tooltip shows (a share as
a fraction where the tooltip shows a percentage; below 0 for a perk that lowers something, such as a cost). The simple armour
perks also have `armor.*`: the share of the item's armour they add per kind.

- `"field": "value", "op": {"mul": 2}`; `"field": "armor.fire", "op": 0.25`; `"armor.enabled"` is `true` or `false`.
- A perk whose game value is a whole number takes whole numbers only: a fraction is refused (`not_exact: ...`), never rounded.
- `damage.*` (the simple damage perks' bonus) is **refused** on this build until a Kiln update traces when the game reads it.
- A perk's effect reads `value` when it starts: equipping the item, unlocking the perk. An effect already running keeps its value
  until it starts again.

## Edit it from C#

```csharp
registry.Alter(view =>
{
    view.Perks["game:perk_damage_ice"].Value = view.Perks["game:perk_damage_ice"].Value * 2;
    view.Perks["game:perk_max_adder_health_t7"].Value = 150;     // a whole-number value: a fraction would be refused
});
```

`view.Perks` is a `RegistryEntries<PerkEntry>`; `Value` is a double and `Armor` a group (`Enabled`, `Physical`, `Magical`, `Cut`,
`Pierce`, `Blunt`, `Fire`, `Ice`, `Shroud`, `Lightning`). To give an item a perk, edit the item's `perks` list ([items](items.md)).

## Views

`RegistryViews.Find("perks")` is the stable view; `items.perks` holds `Ref`s into it ([views.md](views.md)).

## Worked example

One perk grants 10 more, and every item's ice perk doubles:

```json
{
  "format": 1,
  "registry": {
    "alter": [
      { "registry": "perks", "select": { "id": "game:perk_max_adder_health_t7" }, "field": "value", "op": { "add": 10 } },
      { "registry": "perks", "select": { "id": "game:perk_damage_ice" }, "field": "value", "op": { "mul": 2 } }
    ]
  }
}
```

The first is [`mods/samples/RegistryEdits`](../../../../mods/samples/RegistryEdits/content/creatures.json)'s; the second is the
README's. The values apply at the next use and the tooltip follows.

## In multiplayer

`value` and `armor.*` are synced (Joined client `now`): a joined Kiln client gets the server's layer, so its tooltip and armour
match; a vanilla client keeps its own numbers while the server's decide. `damage.*` is `unknown` (refused). A pack of perk edits is
`clients: kiln` for matching tooltips ([choosing clients](README.md#registry-only-packs-choosing-clients)).

## Known limits

- `damage.*` is refused until the game's read time is traced; it ships as `unproven` on this build.
- A perk's value is read when its effect starts: effects running at the edit keep the old value.
- A perk is not added or removed here: the items' `perks` lists decide where it appears.
- Not yet seen in a real game: a perk edit in play, and a joined client's tooltip after a server edit.
