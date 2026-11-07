# Kiln.Rules: what players may do, and what it costs them

Per-player rules for things the game decides itself: where a player may fly and build, what building costs, how fast
tools dig, and what never runs out (health, stamina, mana, water). Mods apply **rule sets** to a scope (one player,
everyone, an area) for a while or until they stop them; Kiln combines every rule set that applies to a player and
applies the result where the game makes each decision. `/creative` is one preset of these rules; it and its command
belong to this service, so they work wherever a world runs (a dedicated server, or a game hosting or playing offline).

| Layer | Where |
|---|---|
| C table | `native/core/abi/services/kiln_rules.h` (`kiln.rules` v1) |
| Native | `native/services/rules/` → `kiln/services/kiln-rules.dll` (hooks: flight gate, building zone, build cursor, costs, mining, Shroud timer, glider) |
| Symbols | `sigdb/symbols/server.d/rules.json` (features `rules.fly`, `rules.zone`, `rules.cursor`, `rules.costs`, `rules.storage`, `rules.dig`, `rules.shroud`, `rules.glide`, `rules.glide_stick`, `rules.glide_view`); `client.d/rules.json` extends it with the client guards |
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

// a Shroud flask: the Shroud timer holds for 3 minutes
Rules.Apply(player, new PlayerRules { FreezeShroudTimer = true }, TimeSpan.FromMinutes(3), "shroud flask");

// a "jetpack glider" while it is equipped: no stamina, slow sink, sprint pushes and climbs
RuleSet jet = Rules.Apply(player, new PlayerRules
{
    UnlimitedGliding = true, GlideSink = 0.5,
    GlideBoost = new GlideBoostRule { Thrust = 12, MaxSpeed = 30, Lift = 9 },
}, label: "jetpack glider");

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
| `BuildOutsideBases` | switch | the player is "near a building zone" everywhere: the hammer's "Needs a Flame Altar nearby" goes, the cursor's "outside a building zone" is lifted (this also opens the game's own fly mode, whose gate is the same zone test) | server + client |
| `IgnoreNoBuildZones` | switch | the game's no-build zones (points of interest, dungeons, altars) lifted | server + client |
| `IgnoreEnemiesNearby` | switch | "enemies nearby" lifted | server + client |
| `BuildInShroud` | switch | "inside the Shroud" lifted | server + client |
| `IgnoreBasePermissions` | switch | "no permission" (other players' bases) lifted; only when the server's rules.json allows it | server + client |
| `KeepBuildsOutsideBases` | switch | placements outside bases kept in the save (Kiln.Build's regions); default: with `BuildOutsideBases` | server |
| `BuildCost` | multiplier | what placing blocks, terrain and props takes (0 = free, which also lifts "missing materials"); 0 also makes upgrading a storage chest in place free | server (+ client for 0) |
| `BuildRefund` | multiplier | what dismantling and undo give back | server |
| `FreeCrafting` | switch | crafting takes nothing and needs nothing: the recipe's payment answers "paid" inside the game's own transaction, the output goes in as usual (crafting from your own inventory; stations and factories pay as usual). In the game's crafting menu the craft buttons stay enabled without the inputs (client hook) | server (+ client for the buttons) |
| `KeepConsumables` | switch | fired arrows, thrown items (bombs) and staff ammunition are not used up: the game's two consume removals remove nothing. Food, drinks, potions and bandages are still used up (their removal site is not hooked yet) | server |
| `DigDamage` | multiplier | the damage the player's tools deal to terrain and blocks; 1000 = one hit | server |
| `Invulnerable` | switch | no damage from hits | server |
| `NoFallDamage` | switch | no damage and no hard-landing stagger from falls, from any height (Kiln.Combat's fall event, canceled); `Invulnerable` already covers falls | server |
| `MinHealth`, `MinStamina`, `MinMana` | fraction 0..1 | kept at least this full (1 = locked full) | server |
| `FreezeShroudTimer` | switch | the Shroud timer does not go down in the Shroud, and the Shroud cannot kill; when the rule ends it counts down from where it stands | server |
| `MinShroudTime` | fraction 0..1 | the Shroud timer is kept at least this full (below it, it recovers at the game's 25 s per second) | server |
| `UnlimitedGliding` | switch | gliding costs no stamina, so the glider is never forced down for lack of it | server + client |
| `GlideSink` | multiplier | how fast the glider sinks (0 = holds altitude unless it points down) | server + client |
| `GlideBoost` | `GlideBoostRule` | gliding like a plane: S pitches up, W pitches down (the mouse stays free), climbing trades speed for height and diving height for speed, holding sprint pushes along the path: `Thrust` (m/s², 15), `MaxSpeed` (m/s, 35), `Lift` (m/s² up, 0), `Coast` (m/s² of slow-down after letting go, 8), `Climb` (the highest climb rate, m/s, 15; 0 never climbs) | server + client |
| `Water` | `WaterRule` | the player's water containers refilled: `WaterRule.KeepFull(items)`, `WaterRule.Rate(chargesPerSecond, items)`; no items = every container | server |

**How rule sets combine** (several mods, scopes and times at once):
- switches: `true` from any rule set grants, an explicit `false` from any rule set denies, and a denial wins (an arena
  that bans flight beats creative mode);
- multipliers multiply (two half-cost rules make a quarter);
- floors take the highest;
- `GlideBoost` takes the most generous of each value (the server's `maxGlideSpeed` holds `MaxSpeed` and `Climb`);
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
- `Rules.Status()`: which hooks are installed on this build and side (`Flight`, `BuildingZone`, `Placement`, `Costs`,
  `Mining`, `ShroudTimer`, `Gliding`), with counters (`FlyAllowed`, `ZoneForced`, `CursorCleared`, `CostsScaled`,
  `RefundsScaled`, `DigsScaled`, `ShroudHeld`, `GlideChanged`, `Faults`) and `Detail`.
- `Rules.ClientOf(player)`: `Host`, `Kiln` (a Kiln game with the rules channel: flight and placement work),
  `KilnWithoutRules`, `Vanilla`, `Pending`, `Unknown`. Server-side rules apply whatever the client; flight and placement
  need `Kiln` or `Host`.
- `Rules.Preset(name)`, `Rules.Presets`, `Rules.DefinePreset(name, rules)` (as long as the mod is loaded).
- `Rules.Simulate(character)`: a spawned player character with no client (a lab test) gets rules like a player
  (`ClientOf` says `Simulated`).
- **Creative mode** is this service's too: `Creative.Set(player, on)` → `CreativeResult(Ok, On, Message)`,
  `Creative.IsOn`, `Creative.Players`, `Creative.Status()`, and the `/creative` command (docs/kiln/commands.md). It is
  the `creative` preset as one Kiln-owned rule set per player; it ends with off, a leave, or the world closing.
- `PlayerRulesChangedEvent` (tick thread): `Before`, `After`. `PlayerRules.FromJson` / `ToJson`, `With`, `Problems()`.

### As data
A preset is a `PlayerRules` in JSON (camelCase, comments allowed). Kiln writes its own `creative` preset to
`<configDir>/rules/creative.json` the first time it runs; edit it to change what `/creative` gives on that server. A file
an earlier Kiln wrote and which still matches its emitted bytes exactly is replaced with this version's preset (so new
rules such as the Shroud timer and gliding reach existing worlds); an edited file is left alone. `Rules.Preset` does this check itself before it reads
the file, so the first `/creative` after an update already gets the new preset (it reads the preset before Kiln.Rules
has started and written its files).
```jsonc
// <configDir>/rules/easy-farming.json
{ "water": { "chargesPerSecond": 2, "items": ["Tool_T1_WateringCan"] }, "digDamage": 3, "minStamina": 0.5 }
```
A mod or the server then applies it with `Rules.Apply(scope, Rules.Preset("easy-farming")!)`. Item blocks or quests
could name a preset too (an item that applies one while held, a quest reward of a timed rule set); not built yet.

### The server's limits: `<configDir>/rules.json`
`allowFlyAnywhere`, `allowBuildOutsideBases`, `allowIgnoreNoBuildZones`, `allowIgnoreEnemiesNearby`,
`allowBuildInShroud`, `allowIgnoreBasePermissions` (**false** by default: other players' bases stay theirs),
`allowInvulnerable`, `allowWater`, `minBuildCost`, `maxDigDamage`, `maxHealthFloor`, `maxStaminaFloor`, `maxManaFloor`,
`allowFreezeShroudTimer`, `maxShroudFloor`, `allowUnlimitedGliding`, `allowGlideBoost`, `maxGlideSpeed` (60 m/s),
`allowNoFallDamage`, `allowFreeCrafting`, `allowKeepConsumables`.
Edits apply within a second.

## Creative menu
Creative mode's tools beyond the rules, in one Kiln screen: **F7** in the game (Kiln menu > Keys changes it), or
**Creative** in the pause menu (shown to admins and to players in creative mode).
- **Items**: the server's item catalogue (every item of the world, mods' items included, without the game's debug, test
  and retired entries), a search, a category, "without equipment" (weapons, armour, rings and gems left out). Pick an
  item: **Take 1**, **Take** a stack, or **Take** a count up to one stack (9,999 at most); gear gets a level and a rarity. Items go where the game puts
  picked-up items, with its "+N" popup. Needs creative mode.
- **World**: the time of day (jump forward to an hour) and the day and night cycle: **Normal**, **Stopped** (the sun
  stands still; sleeping doesn't move it), **Fast** (a whole day and night in about 90 s). Admins only; for the whole
  world, until it is set back or the server restarts.
- **Rules**: what creative mode gives you now, and which hooks work on this build.
- **Only what I have found** (journey): the list shows only items you have picked up, crafted or used (the game's
  item knowledge, read on the server), so you find or craft the first one as the game intends and then take as many as
  you like.
- Creative mode on or off for yourself (admins), and a "Creative" line on the HUD while it is on.

- **Item options** for gear: **Upgrades** (0-5: upgrades unlocked as at the workbench, each one level higher; 5 = all
  the item has, which its rarity decides) and, for weapons, **Gem slot** (an empty gem slot when the game's roll gave
  none). With "Only what I have found" the level is capped at your character's level (server side).
- **World > Map reveal**: the game's range, 200 m, 400 m or the whole map. Client only, for this session while creative
  mode is on (back to the game's when you leave the world or creative mode ends). What is uncovered stays uncovered
  for the character: the game saves it. "Whole map" sweeps the map over about a second, not in one frame.

`/creative` requests `FreeCrafting` and `KeepConsumables` (`PlayerRules.CreativeExtras`) wherever the server's
`creative.json` says nothing about them. Both are native hooks inside the game's own transactions (kiln.rules,
`creative.cpp`; docs/re/creative-mode-146.md "Part 3 research"): no managed event mints or refunds items. On a build where
a hook is missing the rule stays off and `Denied` lists `freeCraftingUnavailable` / `keepConsumablesUnavailable`, as
before. `allowFreeCrafting` and `allowKeepConsumables` in rules.json still deny them first. `Rules.Status()` says which
hooks are installed (`FreeCrafting`, `KeptConsumables`, `MapReveal`, `CraftButtons`) and counts (`CraftsFree`,
`ConsumablesKept`).

How the hooks decide (all fail soft: a symbol that does not resolve, or a return address that does not follow a call to
the hooked function, turns that one hook off):
- **Free crafting**: the recipe function pays through the inventory payment (the one the storage upgrade uses) or, for
  recipes with flag 0x40, through a second payer. The payment answers 0 ("paid") when it is called from the recipe
  function (its return address) and the transaction's operator is the inventory owner and has FREE_CRAFTING.
- **Kept consumables**: the spawn path's `consume` (bows, throwables) and PayUsageCost's `consume` (staffs) each call
  `InventoryRemoveItems(…, actor, item, slot, 1)` and commit only on "nothing missing". For those two calls and an actor
  with KEEP_CONSUMABLES the removal answers "nothing missing" without queueing a removal.
- **Craft buttons** (client): the crafting menu enables its craft actions from the game's craftable-amount lookup; with
  the local player's FREE_CRAFTING (sent by the server with the other client rules) it answers at least the amount asked.
- **Map reveal** (client): the fog of war's discover call gets a wider range (its range is a diameter: 200 m = 400), or
  a grid of 512 m circles over the map, four per frame.

Every action is a request the server checks; the menu shows what the server answers. Taking items needs creative mode
for that player (only an admin turns it on: `/creative on <player>`); the time and the cycle need the creative
permission (`kiln.command.creative`: admins, a hosted world's host). A game hosting its own world (co-op or offline)
asks its own server half directly; a player on a dedicated server needs Kiln in their game (channel `kiln:creative`,
not required: games without it join as before). Requests use the authenticated sender's player. The inbox is
bounded, drains at most 16 requests per tick, and limits each player's pending requests and request rate. Give
admission is shared by the channel, chat and admin ops: one pending transaction per player, at least 250 ms between
accepted submissions. Invalid quantities, hidden/unknown items, levels and rarities are refused rather than clamped.
An accepted inventory transaction may finish after creative mode is switched off; later requests are refused.
The catalogue is paged in the menu so all 4,000+ entries remain reachable without rendering thousands of rows.
Journey returns unavailable for a character that is not joined; it never falls back to showing all items.
The same tools from code and commands:
```csharp
CreativeResult r = await CreativeTools.Give(player, item.Id, count: 50);             // refused unless creative mode is on
await CreativeTools.Give(player, sword.Id, 1, level: 30, rarity: ItemRarity.Epic);
await CreativeTools.Give(player, sword.Id, 1, 30, ItemRarity.Epic, upgrades: 5, gemSlot: true);  // needs Items.GearOptionsAvailable
CreativeTools.SetTime(player, 7.5);                  // checks the player's permission
CreativeTools.SetCycle(player, TimeCycle.Stopped);
CreativeTools.SetCycle(TimeCycle.Fast, "my mod");    // no check: the console, commands with their own permission
IReadOnlyList<ItemInfo> list = CreativeTools.Catalog();
IReadOnlyList<ItemId>? found = CreativeTools.Found(player);   // journey mode; null when knowledge can't be read
```
`/creative give <player> <item> [count] [level]`, `/creative time <time>`, `/creative cycle normal|stopped|fast`
(docs/kiln/commands.md). The cycle is kiln.time's held clock speed (`Time.SetCycle`, `Time.SetSpeed`;
docs/kiln/services/time.md).

Lab `wv-creative3-a` (server 26430253, `mods/tests/creative-test`, a spawned player character, no client), 2026-11-07:
40 PASS, 0 FAIL. `free crafting: the recipe's payments (rva 0x199c80 returns to 0x19a28c; second payer 0x19a9b0)`,
`kept consumables: the consume removals (rva 0x1b0d90; calls return to 0x42665 and 0x41255)`; `free craft at 1318:
output 0x55bf8eb8 +1, inputs kept (0x07e67b5b x4, 0x18feb214 x7, 0x98946174 x10)`, `free craft with no inputs at all:
output +1`, `kiln.rules counted the free crafts (2)`; with creative off `the game's own craft takes the inputs: output
+1, inputs spent True` and `without the rule and without inputs nothing is made (output +0)`. Gear options:
`plain Epic: perks 0/4 unlocked, level 30`, `all upgrades: perks 4/4, level 34 (asked 30 + 4)`, `the weapon has a gem
slot (WeaponGemSlot on item entity 2554)`, `two upgrades: perks 2/4, level 32`, `a gem slot on armour is refused`.
Kept consumables need a player firing a bow (client checklist); the map reveal and the craft buttons are client only.
The storage upgrade's hook installs (`storage upgrade: free with BuildCost 0`); the
upgrade itself and journey mode need a joined player. The catalogue lists 4,180 of
4,213 items; taking is refused without creative mode, gives a stack (`0 -> 7`) and gear at the asked level and rarity
(`level 55, Epic`) with it; an unknown item is refused; Stopped holds the clock (`15:35 -> 15:35` over 100 steps, scale
0, counter unchanged while the game's requests are refused), Fast runs it at x46.7 (`15:36 -> 16:27`), Normal gives it
back; `/creative time 7:30` and `/creative cycle` from the console; NoFallDamage: `a 40 m fall canceled (would have
taken 200 health)`, the same drop without the rule `200 -> 0`. The menu itself needs the game (manual test).

## Server and client
| Part | Where it runs |
|---|---|
| Combining, scopes, vitals, water, invulnerability, moving the character, keeping builds, `/creative` | the server (a dedicated server, or the game hosting a world) |
| Building costs and refunds, mining damage, the Shroud timer | the server's hooks (the Shroud countdown on the HUD is the server's replicated value) |
| "Near a building zone" (the hammer's altar hint, the cursor, the fly gate) | the server's hook; the player's game gets the result through the game's own replicated cursor |
| The fly-mode gate | the server's hook **and** the player's game (its prediction runs the same decision) |
| Gliding (stamina, sink, boost) | the server's hooks **and** the player's game: the glide is predicted, and the same rules on both sides give the same motion |
| The build cursor | the server's hook **and** the player's game (its cursor tells the player what is allowed and starts the action) |

The server sends each player's game its rules over `kiln:rules` (not required: a client without it joins as before).
A game hosting a world (co-op or offline) is both sides in one process: the host's own rules go straight to its
prediction and cursor hooks, with no network message. Nothing on either side needs Kiln.Admin (a server-only mod).
A vanilla game gets only the server-side half: its own prediction refuses the flight and its cursor refuses the
placement, so it should not be given those rules (`/creative` refuses it). The glider's rules are held back on the
server until the player's game has the matching rules channel (version 3 since the climb rate, released in 0.2.0-dev.15): a boost only the server applied
would be corrected on the player's screen every step.

## Gliding
The game's glider is its own thrust against air drag, with gravity for the sink (docs/re/rules.md 7). Kiln wraps the
glide step and changes nothing the game would not predict the same way (the math: `native/services/rules/glide_math.h`,
tested on the build machine by `native/services/rules/test-glide-math.sh`).

`GlideBoost` flies like a plane. Each step the glider follows a path with the glider's own heading (the game turns it
as it always does) and a pitch from the movement keys, like a plane's stick; the mouse is free (looking around never
climbs or dives):
- Holding **S** (back) pitches up and climbs, at 60 degrees at most; holding **W** (forward) pitches down into a dive,
  at 60 degrees at most; neither returns to level flight, which holds the altitude with `Lift` and `GlideSink` both 0.
  An analog stick pitches in proportion after a 0.2 dead zone; non-finite input counts as centred and finite input
  outside -1..1 is clamped.
- The pitch moves at 0.8 rad/s (about 46 degrees a second) at most, both ways: 60 degrees takes about 1.3 s, and
  letting go of S levels out just as smoothly. It starts from the pitch the glider is actually flying (its velocity),
  so the server and the player's prediction compute the same ramp from the same velocity, rules, replicated input
  (`PlayerInput.fromClient.movementStick`, sigdb feature `rules.glide_stick`) and simulation step time (capped at 0.25 s).
  The climb and 60-degree caps and the return to the game's glide take priority over that pitch rate. An updraft can
  nudge the pitch up; after it ends, the stick brings the path back toward its target.
- On a game build where the movement stick is not found, the camera's pitch is used as before (a view within about
  5 degrees of level is level flight), and the log says so once at startup.
- Along the path the speed changes by gravity's share (-9.81 m/s² x sin pitch): climbing costs speed and diving gives it.
- Holding sprint adds `Thrust` up to `MaxSpeed` (a dive's speed is not taken away). With stick control, `Lift` offsets
  the target's vertical velocity by `Lift x dt`, within the pitch rate and caps; it does not accumulate into a climb.
  The camera fallback keeps its direct upward push.
- Without sprint, `Coast` slows what is over the path's own glide speed: the game's glide when level or climbing, more
  the steeper the dive (a dive settles at a speed for its angle, at most 2 x `MaxSpeed`). `Coast` 0 is the game's drag.
- The climb rate never passes `Climb` (0: holding S only slows the glider). The final velocity, including `Lift`,
  respects the boost speed budget and the 120 m/s path limit.
- Once the path is no faster than the game's own glide (after letting go, or a climb that ran out of speed), the
  game's glide stands again: releasing everything settles into a normal glide, and a plain glide does not climb.
`GlideSink` scales gravity for a gliding character (0: no gravity, and a glider that is not pointing down holds its
altitude; with a sink above 0 a boosted glider also sinks by that gravity). `UnlimitedGliding` zeroes the glider's
stamina drain. Hits, landing, water and letting go still end a glide, as in the game; Kiln's landing teleport belongs to
the fly mode only, so a glide that ends on the ground never snaps the character anywhere. Without `GlideBoost` the glide
is the game's (with `GlideSink` and `UnlimitedGliding`).

Channel versions must match exactly: a v2 peer still joins, but receives no v3 rules messages and gets no server-side
glide changes. This prevents prediction from using different gliding rules.

## The flight
The game's fly mode puts the player's input into a flying spark and leaves the character sitting where it was
(docs/re/rules.md 1). Any teleport of the player ends the mode, so the character can't be carried along during the
flight. With `FlightCarriesBody` Kiln brings it to where the flight ends instead: when the flight ends for any reason
(the fly key, a hit, or the rule ending outside a base, which makes the game itself end it), the character is put on
the ground below the spark's last position and kept invulnerable until it has stood on the ground for half a second,
or 60 s at most. The character never leaves the ground during a flight, so ending creative mode in mid-air never drops
the player.

## Lab results (a lab with a copied world, no player)
`mods/tests/rules-test`: 53 PASS, 0 FAIL (0.2.0-dev.15 hooks; earlier runs 44 and 38). A spawned `1_Player` 160 m outside the base, driven through the game's own
input path (`Rules.Simulate`, the client's "ready" flag and the terrain check forced):
```
kiln-rules.dll: hooks: flight on, placement on, costs on, mining on
kiln-rules.dll: building programs: blueprints +0xc4410, place_prop +0xc6280, undo +0xc6dd0, dismantle +0xc4e20
kiln-rules.dll: hooks: flight on, building zone on, placement on, costs on, mining on, Shroud timer on, gliding on
kiln.rules: …\config\rules\creative.json: an earlier Kiln's unedited "creative" preset; updated to this version's
PASS outside a base the game says: not near a building zone
PASS BuildOutsideBases: the game's own state says near a building zone (what the hammer's hint and the fly gate read)
PASS the rule ended: not near a building zone again
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
INFO teleported to (3164.3, 805.4, 2547.2) (a Springlands Shroud valley): in the Shroud True, timer 419 of 420
PASS without a rule the timer goes down: 419 -> 413 in 150 steps
PASS FreezeShroudTimer: the timer holds 413 -> 413 in 150 steps, still in the Shroud (steps held 152)
PASS the rule ended: it counts down again from where it stood: 413 -> 407
PASS MinShroudTime 1: back up to the max at the game's own rate: 407 -> 420 of 420
INFO counters: fly allowed 87, zone forced 4, Shroud held 305, glide changed 0, faults 0
```
`/creative` from the console with a simulated joined player (`KILN_PLAYERS_SIMULATE`): `/creative on Alder` refused,
"Alder's game has no Kiln"; as a player who is not an admin, "You may not use /creative."; `/creative status` lists the
hooks. The Large Watering Can's lab check (`/watercan fill`, `refill on|off` against a drain, a full can kept across a
restart) uses the same water rule.

Not runnable without a client: the cursor (no building tool in hand: `cursor cleared 0`), placement costs (no building
action), mining (a terraform impact spawned without a tool deals no damage the hook could scale), gliding (the lab
character has no glider equipped; the three glider hooks install and resolve on both executables), the client's half.

## Manual test (needs the game)
No server needed: Kiln in the game, a throwaway world hosted from the game (co-op, or offline). On a dedicated server
the same works for an admin who joins with Kiln.
1. `/creative` in chat (a world hosted offline has no chat: write the line `/creative` into
   `kiln/control/console/creative.cmd` in the game folder; it runs as your character, and
   `creative.result.json` next to it shows the reply) → "Creative mode is on: fly anywhere …". kiln.log:
   `kiln.rules: applied … 'creative'` and `kiln.rules: local player's rules: flags 0x1df`.
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
11. With creative on, outside every base, take the construction hammer: no "Needs a Flame Altar nearby"; the build menu
    works and blocks place. kiln.log: `kiln-rules.dll] hooks: … building zone on`.
12. Shroud: with creative on, walk into the Shroud and stay 2 minutes: the timer on the HUD does not move (note any
    flicker). `/creative off` there: it counts down again from the same number. Back on before it runs out.
13. Gliding: with creative on, jump off a height and glide a long way: no height lost while looking level, stamina does
    not drop. Hold Shift looking level: smooth acceleration up to about 35 m/s, the altitude holds. Hold Shift looking
    up: the glider climbs (at most 15 m/s up). Let go after a boost and look up: it climbs while it slows, then glides on
    normally. Look down: it dives and speeds up; level out and it slows back to a normal glide within a few seconds.
    Land: no snap back. `/creative off`: the game's glide (sinks, uses stamina). kiln.log: `local player's rules: flags
    0x1df, … glide sink x0, boost 15 m/s² to 35 m/s, lift 0, climb 15 m/s`.

## Client guards (the game client only)
Not rules: two guards against a broken game camera, in kiln-rules because it already hooks the client's programs
(docs/re/client.md, "The camera, health-bar line of sight, and a frozen client"; `sigdb/symbols/client.d/rules.json`).
- `rules.healthbar_guard`: the health bars' line of sight from the camera to each enemy within 150 m is made zero-length
  when it is longer than 1 km or not finite. A camera 2^31 m below the world once made every such ray walk ~1.5
  billion voxel cells (18 s per frame). kiln.log, once per session: `client guard: the health bars' line of sight ran
  from the camera (…) …; made it zero-length so the game keeps running` (W), then `client guard: N more health-bar
  ray(s) …` every 30 s while it lasts. A flight far from the character (inside the world) only logs one I line.
- `rules.camera_check`: read only. A camera outside the world (or not finite) logs three W lines once per session:
  `client camera check: ClientPlayerInput's camera is at …` with the player, the recent frame times and the camera
  object's fields. Far from the player for 2 s (inside the world): the same at I.
- Off on a dedicated server; a game update that moves either place turns that guard off (`client guards: … OFF`).

## Limits
- Kiln never moves a character to a position outside the world (finite, within the 10240 x 2560 x 10240 m world plus a
  512 m margin): a flight that ended outside it leaves the character where it was (logged). The glider hooks never
  write a non-finite velocity, nor one faster than 150 m/s (or the game's own speed in that step, when that was
  faster), and their values are bounded natively as well (boost speed 120 m/s, accelerations 100 m/s², sink x10).
- Crafting costs, pouring costs (beyond refilling) and health/stamina/mana regeneration rates are not rules yet (hook
  points: docs/re/rules.md).
- A hit still ends a glide (the game's own check on the glide's hit events), as does landing or water.
- A client predicts its own costs and stamina: it may show a short dip that the server's rules undo.
- Building outside a base also depends on the game's undo record for that player (docs/re/rules.md 2.2).
- The foundations work (program wraps, typed hooks, the shared marked-write path) will replace the four native hooks
  with typed hooks and wraps; the `PlayerRules` API stays.

Host regression checks (no game required): `bash native/services/rules/test-glide-math.sh` and
`bash native/services/rules/test-rules.sh`; managed rules tests: `dotnet test managed/tests/Kiln.Rules.Tests`.
The native guard checks cover coherent rule publication, old ABI inputs, Shroud floors on slow steps, invalid ray
endpoints, and bit-for-bit preservation of normal rays. With executable copies,
`python native/services/rules/rules_sigdb_test.py --server <server.exe> --client <client.exe>` checks signatures,
mid-hook instruction boundaries and failure cases for the client guards (requires Capstone).
