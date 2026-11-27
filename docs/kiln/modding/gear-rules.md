# Gear rules: change what gear is worth

Gear values and gear rules need no code. A data-only mod changes a weapon's damage, an armour piece's armour, how long gear lasts,
how often a new weapon gets a gem socket and what unlocking an enhancement costs. Since 0.2.0-dev.30. Since 0.2.0-dev.32 the
game's balancing values go through Kiln's [balance registry](registries/balance.md) where that gives the game the same bytes.

Two parts, one file:

| Part | What it changes | When it is read | Needs |
|---|---|---|---|
| `gear` | single item types, in the numbers the tooltip shows (power, armour, speed, rarity, levels, perks, socket, stack, recipe) | any time: a data pack layer | `content.gear` |
| `gearRules` | the game's balancing table for gear: durability per rarity and level, socket chance per rarity, the enhancement unlock cost | **once, before the game starts** | `"boot": true` in `kiln.mod.json`, `mods.boot.resource_patches` |

## Make it: epic gear that lasts longer and sockets more often

A mod of one manifest and one data file. It is [`mods/samples/GearValues`](../../../mods/samples/GearValues) in small.

1. `kiln.mod.json`. `boot` is what lets `gearRules` run; `clients: mod` puts the mod on every player's game:

   ```json
   {
     "id": "sturdy-gear",
     "name": "Sturdy gear",
     "version": "1.0.0",
     "apiVersion": "0.2",
     "side": "both",
     "clients": "mod",
     "boot": true,
     "description": "Epic and legendary gear lasts longer, and a new epic weapon gets a gem socket three times in four."
   }
   ```

2. `content/gear.json`:

   ```json
   {
     "format": 1,
     "gear": {
       "Weapon_T1_1H_Sword_Craft": { "power": 30, "damage": { "cut": 0.75, "fire": 0.25 } },
       "Armor_T2_*": { "@where": { "rarity": "Rare" }, "armor": { "physical": { "mul": 1.2 } } }
     },
     "gearRules": {
       "rarityDurability": [0.9, 0.95, 1, 1.1, 1.25],
       "socketChance": { "epic": 0.75 }
     }
   }
   ```

   The sword's tooltip damage is 30, three quarters cut and a quarter fire. Every tier 2 armour piece the game made Rare gets a
   fifth more physical armour than the game gives it, at each piece's own level. Epic and legendary copies get 1.1 and 1.25 times
   the max durability (the game: 1.05 and 1.1), and a new epic weapon gets a gem socket three times in four (the game: one in two).

3. `kiln lint mods/sturdy-gear` checks names, kinds, ranges and operation keys before you start the game.
4. Put the folder in the mod profile of the server and of every player who should see the new numbers.
5. Start the game. The mod worked when kiln.log says, for the rules:
   `registry: sturdy-gear's gearRules@gear.json is written by the registry's boot back-end (n field(s), the same bytes as its resource patch)`
   (or `... stays a resource patch: <reason>`), and `kiln/state/boot.json` has `gearRules@gear.json`. A mistake in `gearRules` (a value
   out of range, an unknown key) leaves the whole file's rules out, with the reason in kiln.log; the `gear` part still applies.

## The `gear` section

Keys per item type, in the numbers the game's tooltip shows; Kiln solves the game's formulas per item and writes ordinary item
fields. Every key is a field of the [items registry](registries/items.md):

| Gear key | Means | Items field |
|---|---|---|
| `power` | the damage the tooltip shows at the item's lowest level | `power` |
| `speed` | the damage factor of the weapon's speed class | `speed` |
| `damage.<kind>` | shares per kind (`cut`, `pierce`, `blunt`, `fire`, `ice`, `shroud`, `lightning`, `poison`, `healing`; the whole numbers `wood`, `stone`, `metal`, `explosion`) | `damage.<kind>` |
| `armor.<kind>` | the armour per kind at the lowest level | `armor.<kind>` |
| `block`, `staminaCost` | how well the item blocks; what a use costs | the same names |
| `rarity`, `rolled`, `levels`, `perks`, `socket`, `maxStack` | the type's rarity, whether copies roll it, the level range, the perk slots, a socket, the stack | `rarity`, `rolled`, `levels.min`/`levels.max`, `perks`, `socket`, `maxStack` |
| `recipe` | the ingredients and time of every recipe that makes the item | `recipes.inputs`, `recipes.duration` |

Each number takes a value or a relative operation on the game's value of that item (`{"mul": 1.2}`, `{"add": 5}`, `min`, `max`,
`raiseOnly`, `lowerOnly`). The full reference, the formulas and every validation message: [content.md](../services/content.md#gear-values).
From C#: `Content.Gear("Weapon_T1_1H_Sword_Craft")` and its `GearStats` (`Power`, `SetDamage`, `SetArmor`, `Rarity`, ...), the same values
as writes of your mod ([content.md](../services/content.md#in-c)).

## The `gearRules` section

| Key | Means | Registry field |
|---|---|---|
| `rarityDurability` | the factor of each rarity on a copy's max durability (0.01 to 100): a list common to legendary (`null` keeps the game's), or `{"epic": 1.1}` | `balance.rarityDurability.<rarity>` |
| `socketChance` | the chance of each rarity that a new weapon gets a gem socket (0 to 1) | `balance.socketChance.<rarity>` |
| `durability.start`, `.growth` | max durability = (start + growth x (level - 1)) x the rarity's factor x the item's own factor | `balance.durability.start`, `.growth` |
| `unlockCost.seed`, `.creep` | what unlocking an enhancement at the workbench costs, and how it grows | `balance.unlockCost.seed`, `.creep` |
| `unlockCost.weapons`, `.armor`, `.rings` | the cost's factor per kind of gear | `balance.unlockCost.weapons`, `.armor`, `.rings` |
| `unlockCost.advanced`, `.advancedFrom` | the extra cost of a legendary item's later unlocks, and from which unlock (0 to 5) | `balance.unlockCost.advanced`, `.advancedFrom` |

`lootRarityRates` is refused: Kiln has not mapped the resource that holds the loot rarity rates. For the loot's rarity use
[`lootRules`](registries/lootRules.md).

### What the registry does with a rules file (front-end)

Each key is a field of the balance registry (19 fields). At every start Kiln builds the balancing table twice, with the file's
resource patch and with the registry's write, and uses the registry's copy only when the two are byte for byte the same. A file
stays a resource patch, with the reason in `registry.json` `frontEnds`, when a value it sets is a balance field Kiln has not proven
on this game build (after a game update, until a Kiln update proves the mapping), when an operation's `min` is above its `max`, or
when the two copies differ. Nothing changes for the mod: the same file, the same `"boot": true`, the same values in the game. The
file stays below every registry edit (a registry edit of the same field wins; a relative one composes on the file's value). Details,
the join and the support switch (`front-ends.off`): [registries README](registries/README.md#older-paths-as-front-ends-020-dev32).

To set the same fields from code, edit [`balance`](registries/balance.md) at GameStart (`stage.Balance.SocketChance.Epic = 0.75`).

## In multiplayer

- `gear` values act on the server for everyone. A Kiln player gets them through the content sync even without the mod, so the
  tooltip shows them; a player without Kiln plays with them and sees the game's numbers in the tooltip.
- `gearRules` act **in each process that has the mod**: the server's values act on the server, and a player's game shows (and
  predicts) them only when it has the mod too. Put the mod on both sides (`"clients": "mod"`). The join compares the file by its
  boot content (`kiln.boot`); the front-end changes nothing there, so a server and a player of 0.2.0-dev.31 still compare equal.

## Known limits

- Not seen in a real game: the tooltip's numbers on a client (check `equip-p6.gear-tooltip`), durability and sockets on copies a
  client makes, and the join with a dev.31 peer or `front-ends.off` set.
- Not covered: the loot rarity rates, an item's own durability factor (its config is not mapped), the values of one copy (a copy keeps
  what it was made with), and a rarity above legendary.
- Copies already made keep their rarity, perks and socket; `gearRules` read a copy's max durability when it loads.
- A rarity's perk slots: a copy has rarity + 1 enhancement slots; a slot the type leaves empty makes the copy one rarity lower, so
  raise `rarity` together with `perks`.
- Two mods setting the same balancing member conflict as resource patches do: the later mod in the load order is refused
  ([resource-patches.md](resource-patches.md#two-mods-on-one-resource)).
