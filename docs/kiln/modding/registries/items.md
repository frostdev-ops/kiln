# The `items` registry

<!-- Generated from schema/registry/items.json by tools/registrygen/registrygen.py. Do not edit. -->

Every item the game or a mod defines: stack sizes, rarity, levels, the gear numbers a tooltip shows (power, speed, damage, armour), costs, perks and sockets.

Schema version 0.1.1, experimental (KILNX031: may change in any release until it is marked stable). C# entry type `Kiln.Registry.ItemEntry`. What registries are, the stages, the conflict rules and how ids are made: [README](README.md).

Edits per call: 4,096. One `OnRegistry` callback (one phase of one entry point) or one `Registry.Change` takes at most this many edits of this registry; a call with more is refused whole, with the reason in kiln.log and registry.json, and nothing of it applies (the other calls do). The default for a registry whose schema declares none (4,096).

## Ids

- Game entries: `game:<stable name>`. Mod entries: `<mod id>:<name>`. All ids are lower case. The mod ids `game` and `kiln` are reserved: no mod's entries use them.
- The stable name starts as the item's debug name in lower case (Material_T1_Wood_Logs is game:material_t1_wood_logs). An item without a debug name is game:item_<its 32-bit item id as 8 hex digits>. When two items would get the same name, the one that held it first keeps it and the other is game:<name>~<first 8 hex digits of its GUID>.

## Fields

Applies: `now` = takes effect at the next use; `worldLoad` = takes effect when a world loads; `restart` = takes effect after a game restart. Ops: `number` = a value or the relative operations (mul, add, min, max, raiseOnly, lowerOnly); `set` = a value; `list` = clear, append, remove, replace; `indexed` = a list of fixed length, edited one element at a time (`xpPerLevel[2]`) with a value or a number operation. A field of a list's elements is named with [] in this table and with the element's index in an edit (`entries[3].chance`, or [*] for every element). Mapping: whether this Kiln maps the field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).

Groups of fields:

- `levels`: The item's level range. New copies are made inside it; a save load clamps existing copies into it.
- `damage`: The kinds of damage a hit deals. The hit kinds (cut to poison) are shares that should add up to 1; wood, stone, metal and explosion are whole numbers.
- `armor`: The armour of each kind the tooltip shows at levels.min. Kiln solves the game's share for it with the balance table's constants.
- `fuel`: What the item gives as fuel. No game code reads the burn time on this game build (only whether an item is a fuel), so edits of it stay refused.

| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |
|---|---|---|---|---|---|---|---|
| `maxStack` | int 1..65535 | worldLoad | number | 0.1.0 | mapped | worldLoad | The largest stack. In place before the world's save is read: a lower value cuts stacks in that save. |
| `rarity` | enum (common, uncommon, rare, epic, legendary) | now | set | 0.1.0 | mapped | now | The rarity a new copy gets when it does not roll one. Copies that exist keep theirs. |
| `rolled` | bool | now | set | 0.1.0 | mapped | serverOnly | New copies roll their rarity (and perks) instead of taking rarity. Copies that exist keep theirs. |
| `levels.min` | int 1..105 | now | number | 0.1.0 | mapped | now | The lowest level (also the level power and armour are shown at). |
| `levels.max` | int 1..105 | now | number | 0.1.0 | mapped | now | The highest level. |
| `socket` | bool | now | set | 0.1.0 | mapped | serverOnly | New copies get a gem socket. |
| `perks` | list of ref perks, at most 5 | now | list | 0.1.0 | mapped | now | The perk of each rarity slot, common first. A copy fills rarity + 1 slots. New copies only. |
| `power` | int 0..100000 | now | number | 0.1.0 | mapped | now | The damage the tooltip shows at levels.min. Kiln solves the game's damage factor for it with the balance table's constants. Formula: `gear.power`. |
| `speed` | float 0.01..100 | now | number | 0.1.0 | mapped | now | The speed factor of the weapon's damage (multiplies power). |
| `hasDamage` | bool | now | read-only | 0.1.0 | mapped | serverOnly | The item deals damage (power, speed and damage apply). |
| `damage.cut` | float 0..10 | now | number | 0.1.0 | mapped | now | The share of a hit dealt as cut damage. |
| `damage.pierce` | float 0..10 | now | number | 0.1.0 | mapped | now | The share of a hit dealt as pierce damage. |
| `damage.blunt` | float 0..10 | now | number | 0.1.0 | mapped | now | The share of a hit dealt as blunt damage. |
| `damage.fire` | float 0..10 | now | number | 0.1.0 | mapped | now | The share of a hit dealt as fire damage. |
| `damage.ice` | float 0..10 | now | number | 0.1.0 | mapped | now | The share of a hit dealt as ice damage. |
| `damage.shroud` | float 0..10 | now | number | 0.1.0 | mapped | now | The share of a hit dealt as shroud damage. |
| `damage.lightning` | float 0..10 | now | number | 0.1.0 | mapped | now | The share of a hit dealt as lightning damage. |
| `damage.poison` | float 0..10 | now | number | 0.1.0 | mapped | now | The share of a hit dealt as poison damage. |
| `damage.healing` | float 0..10 | now | number | 0.1.0 | mapped | now | The share of a hit that heals instead of hurting; not counted in the hit shares. |
| `damage.wood` | int 0..1000000 | now | number | 0.1.0 | mapped | now | The damage a hit deals to wood, a whole number. |
| `damage.stone` | int 0..1000000 | now | number | 0.1.0 | mapped | now | The damage a hit deals to stone, a whole number. |
| `damage.metal` | int 0..1000000 | now | number | 0.1.0 | mapped | now | The damage a hit deals to metal, a whole number. |
| `damage.explosion` | int 0..1000000 | now | number | 0.1.0 | mapped | now | The explosion damage a hit deals, a whole number. |
| `hasArmor` | bool | now | read-only | 0.1.0 | mapped | serverOnly | The item gives armour (armor applies). |
| `armor.physical` | int 0..100000 | now | number | 0.1.0 | mapped | now | The physical armour the tooltip shows at levels.min. Formula: `gear.armor`. |
| `armor.magical` | int 0..100000 | now | number | 0.1.0 | mapped | now | The magical armour the tooltip shows at levels.min. Formula: `gear.armor`. |
| `armor.cut` | int 0..100000 | now | number | 0.1.0 | mapped | now | The cut armour the tooltip shows at levels.min. Formula: `gear.armor`. |
| `armor.pierce` | int 0..100000 | now | number | 0.1.0 | mapped | now | The pierce armour the tooltip shows at levels.min. Formula: `gear.armor`. |
| `armor.blunt` | int 0..100000 | now | number | 0.1.0 | mapped | now | The blunt armour the tooltip shows at levels.min. Formula: `gear.armor`. |
| `armor.fire` | int 0..100000 | now | number | 0.1.0 | mapped | now | The fire armour the tooltip shows at levels.min. Formula: `gear.armor`. |
| `armor.ice` | int 0..100000 | now | number | 0.1.0 | mapped | now | The ice armour the tooltip shows at levels.min. Formula: `gear.armor`. |
| `armor.shroud` | int 0..100000 | now | number | 0.1.0 | mapped | now | The shroud armour the tooltip shows at levels.min. Formula: `gear.armor`. |
| `armor.lightning` | int 0..100000 | now | number | 0.1.0 | mapped | now | The lightning armour the tooltip shows at levels.min. Formula: `gear.armor`. |
| `block` | float 0..1 | now | number | 0.1.0 | mapped | now | How well the item blocks (0 to 1). The game reads it for each new block or attack of the item, so an edit applies at the next one. |
| `staminaCost` | float 0..100000 | now | number | 0.1.0 | mapped | now | The stamina a use costs, at the next payment. |
| `manaCost` | float 0..100000 | now | number | 0.1.0 | mapped | now | The mana a use costs, at the next cast. |
| `fuel.burnTime` | time s 0..86400 | worldLoad | number | 0.1.0 | unproven: no game code reads an item's burn time on this build, only whether the item is a fuel, so an edit of it would change nothing | unknown | How long one item burns. |
| `comfort` | int 0..1000 | now | number | 0.1.0 | mapped | now | The comfort the item gives while a player plays it (the game's instruments). The game reads it at every pass, so an edit applies at once. |

## Traits

Selectors ask for these with `has` / `hasNot`.

| Trait | Means |
|---|---|
| `weapon` | deals damage (hasDamage) |
| `armor` | gives armour (hasArmor) |
| `stackable` | maxStack above 1 |

## Entries

- Add (stage add, id `<your mod id>:<name>`): yes.
- Add requires GameStart and an explicit present Kiln `like` id; clone chains end at a game entry. Kiln retains the stable identity and pins the structural base install-wide. Use a new id for an incompatible base.
- Remove (hidden): GameStart only. Retains records and usable saved copies in inventory, equipment and containers; hides random loot and dependent recipes, and disables direct-drop slots without resizing. Whole removal is refused for unproven drop/recipe dependencies, missing group metadata or a group with no surviving spawnable stack. Removal wins later registry chance/weight conflicts.
- Raw game paths in edits (the Advanced tier): allowed.

## Edit it from a data pack

A data pack is a `content/*.json` file of your mod: `"format": 1` and a `registry` section with the three stages `add`, `alter`
and `final` (what they mean: [README](README.md#stages-and-conflicts)). An edit names the registry, the entries (`select`),
a field and an operation:

- `select`: `{"id": "game:material_t1_wood_logs"}`, a glob (`"game:material_t1_*"`), or a trait (`"has": ["weapon"]`, see Traits).
  `where` keeps the entries whose fields meet a condition (`{"maxStack": ">=100"}`).
- `op`: a value (set), or `{"mul": 2, "max": 9999}` (relative operations stack with other mods' edits), or for `perks` a list
  operation: `"list": {"append": ["game:perk_damage_ice"]}`. One slot: `"field": "perks[0]"`.
- Add and remove an item (GameStart only): `{"registry": "items", "entry": "add", "id": "my-mod:crate", "like": "game:material_t1_wood_logs",
  "fields": {"maxStack": 50}}` in `add`, and `{"registry": "items", "entry": "remove", "select": {"id": "..."}}`.
  What removal hides and what it keeps: [entry add and remove](README.md#entry-add-and-remove-gamestart-only).

The gear numbers (`power`, `speed`, `damage.*`, `armor.*`) are what the tooltip shows at `levels.min`; Kiln solves the
game's own factors for them. A data pack's older `gear` section says the same things by other names
([content.md](../../services/content.md#gear-values-and-the-items-registry-020-dev32)).

## Edit it from C#

```csharp
[RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad)]
public sealed class Stacks : Mod
{
    public override void OnRegistry(RegistryContext context)
    {
        var registry = Kiln.Registry.Registry.For(context);
        registry.Alter(view =>
        {
            view.Items["game:material_t1_wood_logs"].MaxStack = 500;
            foreach (var weapon in view.Items.Select("game:weapon_t1_*"))
                if (weapon.HasDamage) weapon.Power = weapon.Power * 2;
        });
        if (context.EntryPoint == RegistryEntryPoint.GameStart)
            registry.Add(view => view.Items.AddLike(new("my-mod:stout_logs"), new("game:material_t1_wood_logs"), item => item.MaxStack = 100));
    }
}
```

`view.Items[id]` is an `ItemEntry`; `Select(pattern)` lists entries by glob. What a read inside the callback shows:
[C# authoring](README.md#c-authoring-api-0217). To change an item while a world runs, use `Registry.Change(view => ...)`
(and `change.Revert()`); to follow a setting, declare the `WorldLoad` point and call
`Registry.Reauthor(RegistryEntryPoint.WorldLoad)` when it changes. [RegistryEdits sample](../../../../mods/samples/RegistryEdits/RegistryEditsMod.cs).

## Views

`RegistryViews.Find("items")` is the registry's stable view: every field above as it resolves on the running build
(`Mapped`, `Unmapped` or `Unproven`, with the reason), read-only. The advanced view over the game's item data
(`itemInfo`) lists members this registry does not map; where it does, the member reports `CoveredBy` (`"items.maxStack"`).
Write through the registry, read through either ([views.md](views.md)).

## Worked example

Every tier 1 material stacks twice as high (but never above 9,999), and one sword gets a socket and 10 more power. As a data pack (`content/stacks.json`):

```json
{
  "format": 1,
  "registry": {
    "alter": [
      { "registry": "items", "select": { "id": "game:material_t1_*" }, "field": "maxStack", "op": { "mul": 2, "max": 9999 } },
      { "registry": "items", "select": { "id": "game:weapon_t1_1h_sword_craft" }, "field": "socket", "op": true },
      { "registry": "items", "select": { "id": "game:weapon_t1_1h_sword_craft" }, "field": "power", "op": { "add": 10 } }
    ]
  }
}
```

`maxStack` applies at the next world load (`worldLoad`), `socket` and `power` at the next use (`now`; `socket` only for
copies made afterwards). In C# the data pack's `max` is `item.MaxStack = Math.Min(item.MaxStack * 2, 9999)`.

## In multiplayer

The server applies every edit. A joined Kiln client gets the item layer through the content sync (the Joined client column:
`now` for the gear numbers, perks, levels and costs; `worldLoad` for `maxStack`, which a client reads at its next world load;
`serverOnly` for `rolled`, `socket`, `hasDamage` and `hasArmor`), so its tooltips match. A vanilla client keeps the game's
numbers on its screen while the server's numbers decide what happens. For a pack with only item edits set `clients` to `kiln`
(matching tooltips) or `none` (vanilla players welcome; their tooltips differ). Adding or removing an item needs `kiln` at least,
and `mod` for assets of your own ([choosing clients](README.md#registry-only-packs-choosing-clients)).

## Known limits

- `fuel.burnTime` is refused on this build: no game code reads an item's burn time, so an edit would change nothing.
- `perks` and `socket` reach new copies only; a copy that exists keeps its rarity, perks and socket. A lower `maxStack`
  is guarded (stacks a save holds stay whole), a lower `levels.max` or a higher `levels.min` is refused for that entry
  ([edits that change existing saves](README.md#edits-that-change-existing-saves)).
- Not yet seen in a real game: a joined client's tooltips after a server edit, equipped copies after an item is removed,
  and an item added here on a vanilla client (it has no look for it).
