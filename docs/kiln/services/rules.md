# Kiln.Rules: what players may do, and what it costs them

Per-player rules for things the game decides itself: where a player may fly and build, what building costs, how fast
tools dig, and what never runs out (health, stamina, mana, water). Mods apply **rule sets** to a scope (one player,
everyone, an area) for a while or until they stop them; Kiln combines every rule set that applies to a player and
applies the result where the game makes each decision. `/creative` (Kiln.Admin) is one preset of these rules.

| Layer | Where |
|---|---|
| C table | `native/core/abi/services/kiln_rules.h` (`kiln.rules` v1) |
| Native | `native/services/rules/` → `kiln/services/kiln-rules.dll` (hooks: flight gate, build cursor, costs, mining) |
| Symbols | `sigdb/symbols/server.d/rules.json` (features `rules.fly`, `rules.cursor`, `rules.costs`, `rules.dig`; the client uses the same file) |
| Managed | `managed/services/Kiln.Rules/` → `kiln/host/Kiln.Rules.dll` (`Rules`, `PlayerRules`, `RuleScope`, `RuleSet`) |
| Uses | Kiln.Items (water refill policies), Kiln.Abilities (vitals), Kiln.Combat (invulnerability), Kiln.World (moving the character), Kiln.Build (keeping builds), Kiln.Network (the client's half) |
| Tests | `managed/tests/Kiln.Rules.Tests` (how rules combine), `mods/tests/rules-test` (lab) |
| Evidence | `docs/re/rules.md` |

## Using it
Reference `Kiln.Rules` (`<KilnServices>Rules</KilnServices>` in a mod project). Tick thread (mod code always is).

```csharp
using Kiln;

// one player, until disposed (or the mod unloads, or the player leaves)
RuleSet fly = Rules.Apply(player, new PlayerRules { FlyAnywhere = true });
fly.Dispose();

// everyone, for a weekend: half-cost building
Rules.Apply(RuleScope.Everyone, new PlayerRules { BuildCost = 0.5, BuildRefund = 0.5 }, TimeSpan.FromDays(2), "half-cost weekend");

// an area: no stamina drain in your base
Rules.Apply(RuleScope.Cylinder(baseCenter, 40, baseCenter.Y - 20, baseCenter.Y + 60), new PlayerRules { MinStamina = 1 });

// a flight potion: 60 seconds of flight for whoever drinks it
[Subscribe] void OnDrink(PlayerConsumedEvent e) { if (e.Item.Id == FlightPotion) Rules.Apply(e.Player.Entity, new PlayerRules { FlyAnywhere = true }, TimeSpan.FromSeconds(60)); }

// what applies to a player now
EffectiveRules now = Rules.Of(player);   // FlyAnywhere, BuildCost, ..., From (the rule sets), Denied (by the server)

// a preset by name: <configDir>/rules/<name>.json, a mod's DefinePreset, or Kiln's "creative"
Rules.Apply(player, Rules.Preset("creative")!);
```

### `PlayerRules`
Every member is optional (null: no opinion).

| Member | Type | What | Side |
|---|---|---|---|
| `FlyAnywhere` | switch | the game's building fly mode outside bases, without the fly-mode unlock or the building permission | server + client |
| `FlightCarriesBody` | switch | the character is brought to the ground below where the flight ends (default: with `FlyAnywhere`) | server |
| `BuildOutsideBases` | switch | the cursor's "outside a building zone" lifted | server + client |
| `IgnoreNoBuildZones` | switch | the game's no-build zones (points of interest, dungeons, altars) lifted | server + client |
| `IgnoreEnemiesNearby` | switch | "enemies nearby" lifted | server + client |
| `BuildInShroud` | switch | "inside the Shroud" lifted | server + client |
| `IgnoreBasePermissions` | switch | "no permission" (other players' bases) lifted; only when the server's rules.json allows it | server + client |
| `KeepBuildsOutsideBases` | switch | placements outside bases kept in the save (Kiln.Build's regions); default: with `BuildOutsideBases` | server |
| `BuildCost` | multiplier | what placing blocks, terrain and props takes (0 = free, which also lifts "missing materials") | server (+ client for 0) |
| `BuildRefund` | multiplier | what dismantling and undo give back | server |
| `DigDamage` | multiplier | the damage the player's tools deal to terrain and blocks; 1000 = one hit | server |
| `Invulnerable` | switch | no damage from hits | server |
| `MinHealth`, `MinStamina`, `MinMana` | fraction 0..1 | kept at least this full (1 = locked full) | server |
| `Water` | `WaterRule` | the player's water containers refilled: `WaterRule.KeepFull(items)`, `WaterRule.Rate(chargesPerSecond, items)`; no items = every container | server |

**How rule sets combine** (several mods, scopes and times at once):
- switches: `true` from any rule set grants, an explicit `false` from any rule set denies, and a denial wins (an arena
  that bans flight beats creative mode);
- multipliers multiply (two half-cost rules make a quarter);
- floors take the highest;
- water takes the fastest rate and the items of every rule set (one rule set without items means every container);
- then the server's limits (`<configDir>/rules.json`) hold: a switch it denies is off for everyone (`EffectiveRules.Denied`
  names it), multipliers and floors are clamped.

**Ownership.** A rule set belongs to the mod that applied it and ends when that mod unloads. A rule set for one player
(`RuleScope.ForPlayer`, `.Only(player)`) also ends when that player leaves: a player's entity id is reused by the next
player who joins. `Update(rules)` changes it in place; `Dispose()` ends it; `Expires` ends it by itself. Rules are
session state: nothing is saved, so a restart starts from the game's rules.

**Scopes.** `RuleScope.ForPlayer(e)`, `RuleScope.Everyone`, `RuleScope.Sphere(center, r)`, `RuleScope.Box(min, max)`,
`RuleScope.Cylinder(center, r, minY, maxY)`, and `.Only(player)` on an area. Areas are checked against player
positions four times a second.

### Other API
- `Rules.Status()`: which hooks are installed on this build and side (`Flight`, `Placement`, `Costs`, `Mining`), with
  counters (`FlyAllowed`, `CursorCleared`, `CostsScaled`, `RefundsScaled`, `DigsScaled`, `Faults`) and `Detail`.
- `Rules.ClientOf(player)`: `Host`, `Kiln` (a Kiln game with the rules channel: flight and placement work),
  `KilnWithoutRules`, `Vanilla`, `Pending`, `Unknown`. Server-side rules apply whatever the client; flight and placement
  need `Kiln` or `Host`.
- `Rules.Preset(name)`, `Rules.Presets`, `Rules.DefinePreset(name, rules)` (as long as the mod is loaded).
- `Rules.Simulate(character)`: a spawned player character with no client (a lab test) gets rules like a player.
- `PlayerRulesChangedEvent` (tick thread): `Before`, `After`. `PlayerRules.FromJson` / `ToJson`, `With`, `Problems()`.

### As data
A preset is a `PlayerRules` in JSON (camelCase, comments allowed). Kiln writes its own `creative` preset to
`<configDir>/rules/creative.json` the first time it runs; edit it to change what `/creative` gives on that server.
```jsonc
// <configDir>/rules/easy-farming.json
{ "water": { "chargesPerSecond": 2, "items": ["Tool_T1_WateringCan"] }, "digDamage": 3, "minStamina": 0.5 }
```
A mod or the server then applies it with `Rules.Apply(scope, Rules.Preset("easy-farming")!)`. Item blocks or quests
could name a preset too (an item that applies one while held, a quest reward of a timed rule set); not built yet.

### The server's limits: `<configDir>/rules.json`
`allowFlyAnywhere`, `allowBuildOutsideBases`, `allowIgnoreNoBuildZones`, `allowIgnoreEnemiesNearby`,
`allowBuildInShroud`, `allowIgnoreBasePermissions` (**false** by default: other players' bases stay theirs),
`allowInvulnerable`, `allowWater`, `minBuildCost`, `maxDigDamage`, `maxHealthFloor`, `maxStaminaFloor`, `maxManaFloor`.
Edits apply within a second.

## Server and client
| Part | Where it runs |
|---|---|
| Combining, scopes, vitals, water, invulnerability, moving the character, keeping builds | the server (a dedicated server, or the game hosting a world) |
| Building costs and refunds, mining damage | the server's hooks |
| The fly-mode gate | the server's hook **and** the player's game (its prediction runs the same decision) |
| The build cursor | the server's hook **and** the player's game (its cursor tells the player what is allowed and starts the action) |

The server sends each player's game its rules over `kiln:rules` (not required: a client without it joins as before).
A vanilla game gets only the server-side half: its own prediction refuses the flight and its cursor refuses the
placement, so it should not be given those rules (`/creative` refuses it).

## The flight
The game's fly mode puts the player's input into a flying spark and leaves the character sitting where it was
(docs/re/rules.md 1). Any teleport of the player ends the mode, so the character can't be carried along during the
flight. With `FlightCarriesBody` Kiln brings it to where the flight ends instead: when the flight ends for any reason
(the fly key, a hit, or the rule ending outside a base, which makes the game itself end it), the character is put on
the ground below the spark's last position and kept invulnerable until it has stood on the ground for half a second,
or 60 s at most. The character never leaves the ground during a flight, so ending creative mode in mid-air never drops
the player.

## Lab results (a lab with a copied world, no player)
`mods/tests/rules-test`: 38 PASS, 0 FAIL. A spawned `1_Player` 160 m outside the base, driven through the game's own
input path (`Rules.Simulate`, the client's "ready" flag and the terrain check forced):
```
kiln-rules.dll: hooks: flight on, placement on, costs on, mining on
kiln-rules.dll: building programs: blueprints +0xc4410, place_prop +0xc6280, undo +0xc6dd0, dismantle +0xc4e20
PASS without a rule the game refuses its fly mode outside a base (spark 0)
PASS with FlyAnywhere the game starts it outside a base: spark 2553 (2_Player_Building_Spark), gate decisions turned 11
PASS the flight holds while the rule does
PASS during the flight the character stays where it was, as in the game
PASS the rule ended: the game ended the flight by itself (outside a base)
kiln.rules: player 2543: the flight ended at (3980.3, 732.9, 1434.5); brought there from (3960.3, 731.4, 1434.3)
PASS invulnerable while landing / the landing grace ends once it stands on the ground
PASS no damage from the flight or the landing: health 200 -> 200
PASS the fly key ends the flight as in the game
PASS Stamina floor 1: spent to 240, back to 480 of 480 within 4 steps (mana the same; without the rule it stays spent)
PASS invulnerable: a 50 hit (Dropped) leaves health at 200 of 200
PASS water rule: drained to 1000 (as a pour), full again; the rule ended: a drain stays
PASS two rule sets: costs multiply, a denial wins: building x0.25
PASS an area around the character applies / an area elsewhere does not / a rule set with a duration ends by itself
PASS the native table is empty again; no hook faulted
```
`/creative` from the console with a simulated joined player (`KILN_PLAYERS_SIMULATE`): `/creative on Alder` refused,
"Alder's game has no Kiln"; as a player who is not an admin, "You may not use /creative."; `/creative status` lists the
hooks. The Large Watering Can's lab check (`/watercan fill`, `refill on|off` against a drain, a full can kept across a
restart) uses the same water rule.

Not runnable without a client: the cursor (no building tool in hand: `cursor cleared 0`), placement costs (no building
action), mining (a terraform impact spawned without a tool deals no damage the hook could scale), the client's half.

## Manual test (needs the game)
Kiln in the game and on the server (or a hosted world), an admin character on a throwaway world.
1. `/creative` → chat: "Creative mode is on: fly anywhere …". kiln.log (server): `kiln.rules: applied … 'creative'`;
   client: `kiln.rules: local player's rules: flags 0x1f`.
2. Outside any base, take the construction hammer and press the fly key: the spark mode starts (the game refuses it
   without creative). Fly 200 m away and up 50 m. The character stays at the start point, sitting, as in the game.
3. Press the fly key again: the character appears on the ground below the spark, no damage. Note anything odd about the
   camera or a loading screen.
4. Fly again, then `/creative off` in mid-air: the flight ends, the character is set down on the ground below; no
   damage; health, stamina and costs back to normal.
5. With creative on, outside any base: place walls, floors, terrain and a chest. The cursor shows them valid, nothing
   leaves the inventory (note any short flicker of counts), dismantling gives nothing back.
6. Build next to a point of interest or a dungeon entrance (a no-build zone) and near enemies: allowed. In another
   player's base: refused (rules.json default).
7. Restart the server: the blocks placed outside bases in step 5 are still there.
8. Pickaxe on stone, ore and a stone block: each breaks in one hit. Explosives are unchanged.
9. Sprint, cast spells, take a hit: stamina and mana stay full, no damage. Water a crop: the can stays full.
10. `/creative off`: costs, stamina, mana, damage and the can are back to the game's. A player without Kiln: `/creative on
    <them>` is refused.
## Limits
- Crafting costs, pouring costs (beyond refilling), health/stamina/mana regeneration rates and the Shroud timer are
  not rules yet (hook points: docs/re/rules.md).
- A client predicts its own costs and stamina: it may show a short dip that the server's rules undo.
- Building outside a base also depends on the game's undo record for that player (docs/re/rules.md 2.2).
- The foundations work (program wraps, typed hooks, the shared marked-write path) will replace the four native hooks
  with typed hooks and wraps; the `PlayerRules` API stays.
