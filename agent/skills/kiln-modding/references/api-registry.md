# The mod API: registries (Kiln.Registry; the stage since dev.31, 12 more registries, views and Reauthor in dev.32)

Part of the mod API guides ([api.md](api.md) is the index). Here: what a registry is, the 17 registries and what each one holds,
the three ways to edit one (a data pack's `registry` section, `Mod.OnRegistry`, `Registries.Change`), when an edit takes effect,
re-authoring a pass when settings change (`Registries.Reauthor`), the per-call edit cap, the read-only views over everything else,
and what a joined player sees. **Experimental (`KILNX031`)**: the shape may change in any release until a registry is marked
stable; at API 1.0.0 the stable ones are frozen (additive only). The API has no generated index file
([api-index/INDEX.md](api-index/INDEX.md) leaves experimental API out): the entry types are generated from the schemas, so read
`docs/kiln/modding/registries/<registry>.md` (one page per registry: every field, its type, range, when it applies and what a
joined client sees) and `RegistrySchema` in code.

- Assembly: `Registry` in `KilnServices`, plus `<NoWarn>$(NoWarn);KILNX031</NoWarn>`. `Mod.OnRegistry`, `RegistryContext` and
  `[RegistryEntryPoints]` are Kiln.Api (always there).
- Pages: `docs/kiln/modding/registries/README.md`, `views.md`, `docs/kiln/services/registry.md`, wiki
  https://frostdev.io/kiln/wiki/registries, `new-registries`, `loot-rules`. Samples: `RegistryEdits` (C# and a data pack),
  `SpellRegistry` (timing in data, a buff duration in C#).
- Status: the dev.31 registries (`items`, `recipes`, `loot`, `drops`, `balance`) ran on dedicated servers and in offline tests;
  the 12 dev.32 registries were proven by a lab re-proof of 262 of 277 fields on a dedicated server (the other 15, `fuel.burnTime`
  and 14 `perks.damage.*`, are refused with a reason). **Nothing of the registries was seen in a real game client** (creature, arsenal and
  perk edits in play, the joined client's skill screen, buff and ability timing, altars above 16, gifting beyond 50 m, cleared
  no-build zones, real loot rolls). Say so.

## What a registry is

Game data under **Kiln-owned names**: `items.maxStack`, `drops.entries[3].chance`, `creatures.health.perPlayer`. A mod edits a
field by its Kiln name and an entry by its Kiln id (`game:material_t1_wood_logs`, never a GUID, address or reflection path).
Kiln maps each field onto the game for every game build it supports; a game update breaks Kiln's mapping (a Kiln update fixes
it), not your mod. A field Kiln has not proven on the running build is refused per entry with a reason, never guessed. A
registry holds **data only**; services and events (Kiln.Items, Kiln.World, ...) stay as they are. Ids: `game:<stable name>` for
the game's entries, `<your mod id>:<name>` for yours (`game` and `kiln` are reserved).

| Registry | Entries (id) | Holds | Add / remove |
|---|---|---|---|
| `items` | every item (`game:<debug name>`) | stacks, rarity, levels, power, speed, damage, armour, costs, perks, socket | add (`AddLike`), hide |
| `recipes` | every recipe | inputs, outputs, duration, `station` (read only) | add (`AddLike`), hide |
| `loot` | random loot per item | stack sizes, rarities | hide from candidates |
| `drops` | the game's drop tables | each entry's item, count, chance, weight | no |
| `balance` | one, `game:balance` | experience, levels, flame altars, durability, sockets, perk costs, the 19 `gearRules` keys | no |
| `creatures` (dev.32) | every creature (`game:<name>`) | level, faction, scale, xp, health and damage scaling, 14 resistances, speeds, loot slots, enemy sight, a wild animal's `tamingSteps`; `kind`, `arsenal`, `tameable`, `favoriteBait` read only | no |
| `arsenals` (dev.32) | enemies' arsenals (`game:<first creature>`) | each attack's `chance`, `threat`, `minRange`, `maxRange`, `maxHeight` | no (values only) |
| `perks` (dev.32) | every perk | `value`, `armor.*`, `damage.*` (the damage ones are refused: not traced yet) | no |
| `skills` (dev.32) | skill tree nodes (`game:skill_<node id>`) | `cost`; `maxLevel`, `kind`, `class`, `unlocks` read only | no (mods add nodes through `skills/*.json`) |
| `stations` (dev.32) | crafting stations (`game:<station's item>`) | read only; a recipe names its station | no |
| `buffs` (dev.32) | every buff (`game:buff_<8 hex>`) | `duration`, `stacking`; `kind`, `saved` read only | no (a clone or a status block) |
| `abilities` (dev.32) | every timed action | `timing.release`, `.hit`, `.cost`, `.buff` | no (an ability block) |
| `world` (dev.32) | one, `game:world` | `noBuildZones`, `playableAreas` (lists of boxes) | no |
| `difficulty` (dev.32) | one, `game:difficulty` | the lowest and highest value of each difficulty setting | no |
| `limits` (dev.32) | one, `game:limits` | `maxAltars`, `giftingRange` (numbers the game's code holds) | no |
| `lootRules` (dev.32) | one, `game:loot` | `rarityWeights.common` to `.legendary`: the world-wide rarity weights | no |
| `randomLoot` (dev.32) | every random loot table (`game:labels_<8 hex>`, `game:world_<8 hex>_<8 hex>`) | `level`, `levelOffset`, `levelOffsetRange`, `rarity.<rarity>.mode` and `.weight` | no |

`buffs` and `abilities` are kiln.gamedata's data under Kiln names: a data pack's `buffs` / `sequences` sections still work
(game paths); a registry edit is the later layer and wins. A spell's costs are its item's (`items.manaCost`, `staminaCost`).
Stations' recipes are the recipes that name them: select with `"where": {"station": "game:prop_production_t1_forge"}`.

## Edit from a data pack

A pack's `registry` section (`content/*.json`), by Kiln id; no code. `add`, `alter` and `final` are the three passes of one
start, run across all mods in load order.

```json
{ "format": 1,
  "registry": { "alter": [
    { "registry": "lootRules", "select": { "id": "game:loot" }, "field": "rarityWeights.legendary", "op": { "mul": 2 } },
    { "registry": "randomLoot", "select": { "id": "game:labels_*" }, "field": "levelOffset.mode", "op": "add" },
    { "registry": "randomLoot", "select": { "id": "game:labels_*" }, "field": "levelOffset.value", "op": 2 },
    { "registry": "creatures", "select": { "id": "game:enemy_vukah_*", "has": ["enemy"] }, "field": "sight.range", "op": { "mul": 1.5 } },
    { "registry": "arsenals", "select": "game:enemy_vukah_melee", "field": "attacks[*].chance", "op": { "mul": 0.5 } },
    { "registry": "skills", "select": "game:skill_4475e8dd", "field": "cost", "op": 0 },
    { "registry": "limits", "select": "game:limits", "field": "giftingRange", "op": 200 } ] } }
```

A plain `op` sets the value; an object is `mul`, `add`, `min`, `max`, `raiseOnly`, `lowerOnly` or `set`. Lists take
`clear`, `append`, `remove`, `replace`; a fixed-length list is edited per element (`xpPerLevel[2]`, `attacks[*]` for all).
Not every creature has every field (most have no size of their own; only enemies have sight and an arsenal): select with
`"has": ["enemy"]` or by id, or the edit is refused for those entries (a relative op over one refuses the whole edit).

## Edit from C#

```csharp
using Kiln;
using Kiln.Registry;

[RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad)]
public sealed class Tweaks : Mod
{
    public override void OnRegistry(RegistryContext context)
    {
        if (!Capabilities.Is("kiln.registry")) return;                            // the transport; fields have their own rows
        var registry = Kiln.Registry.Registries.For(context);
        registry.Alter(stage =>
        {
            stage.Items["game:material_t1_wood_logs"].MaxStack = 500;
            var vukah = stage.Creatures["game:enemy_vukah_melee"];
            vukah.Sight.Range = vukah.Sight.Range * 1.5;
            stage.Arsenals["game:enemy_vukah_melee"].Attacks[0].Chance = 0.5;
            stage.Skills["game:skill_4475e8dd"].Cost = 0;
            stage.LootRules.RarityWeights.Legendary = stage.LootRules.RarityWeights.Legendary * 2;
            foreach (var table in stage.RandomLoot.Select("game:labels_*"))        // enemies and generic containers
                if (table.Rarity.Legendary.Mode == RandomLootMode.Set)             // the elite, boss and chest tables with their own odds
                    table.Rarity.Legendary.Weight = table.Rarity.Legendary.Weight * 2;
        });
        if (context.EntryPoint == RegistryEntryPoint.GameStart)                    // world rules are restart fields: GameStart only
            registry.Alter(stage => stage.World.NoBuildZones.Clear());
    }
}
```

An assignment is an absolute edit in the **alter** pass; `registry.Add(...)` and `registry.Final(...)` are the other passes.
Entry points (`[RegistryEntryPoints]` is constant metadata Kiln reads without running the mod; no attribute means `GameStart`):

| Point | Runs | Inputs |
|---|---|---|
| `GameStart` | at resources-ready, before the game uses its data, once per Add / Alter / Final pass | global settings only; no world; no other mod's runtime exports |
| `ModLoad` | after `OnLoad`, once the base registry exists | global settings; the layers follow the process across worlds |
| `WorldLoad` | for each world, a new one included | the world's settings once bound; the layers end with that world |
| `HotReload` | for the replacement instance after a hot reload | replaces `ModLoad` for that pass |
| `Manual` | `Registries.Change` only | an active world |

- **Reads leave out your own layers.** A runtime pass reads the values from before the pass, so `item.MaxStack = item.MaxStack * 2`
  restates your intent at each point instead of doubling again. Keep copied values, not the context, an entry or a list: they
  stop working when the callback returns (`RegistryContextException`: `Closed`, `WrongThread`, `WorldChanged`, `Unloaded`).
- **Declaring points without an override is KILN122; Manual as an automatic flag is KILN123.**
- **Entry operations are GameStart only:** `registry.Add(stage => stage.Items.AddLike(new("my-mod:stout_logs"), new("game:material_t1_wood_logs"), item => item.MaxStack = 100))`
  adds an item or recipe; `stage.Loot.Remove(pattern)` and item/recipe removal **hide, never delete** (owned copies keep working).
  A later point answers `RestartRequired`.
- **Explicit, live changes** on the tick thread of a loaded world: `var change = Registries.Change(stage => ...); if (!change.Result.Ok) ...;
  change.Revert()`. `Ok` only when applied with nothing refused or skipped. A change is not disposable: leaving a scope never
  undoes it. `Registries.Read(stage => ...)` reads live values in an expiring read-only frame. `Registries.Ready` (subscribe in
  `OnLoad`) says the registry is ready, not that every edit applied: read the results.
- **Refusals are per entry** at ModLoad, WorldLoad and HotReload (an exclusive claim, an entry missing in this world, a config
  the entry lacks, a value its storage cannot hold exactly: `not_exact: ...`). The result is `Applied` with `Refused > 0` and the
  first reason. `Registries.Change` is all or nothing.
- **Several mods, one field:** the later mod in load order wins and relative edits compose (x4 then x2 is x8); `exclusive`
  refuses another mod's edit of that field; `yield` applies only where nobody set it. `kiln/state/registry.json` has the writer
  chain per field; the launcher's Conflicts page reads it.
- **A field by name, without its typed property:** `stage.Entries("creatures")["game:enemy_vukah_melee"].Set("health.perPlayer", 1.2)`
  (`RegistryStage.Entries(name)`) inside `OnRegistry` or `Registries.Change`. The generic `RegistryEntry` makes the same edits and checks the value against the
  schema's type first.

## When an edit takes effect, and who sees it

Each field says so in its page, on its C# property and in `RegistrySchema.Find(...).Applies`:

| Applies | Means | Typical fields |
|---|---|---|
| `now` | the next use (craft, roll, hit, payment); a creature edit reaches creatures that spawn after it, sight and health scaling at once | gear numbers, loot, drops, `lootRules`, `randomLoot`, `skills.cost`, `buffs`, `abilities`, `arsenals` |
| `worldLoad` | when a world loads, before its save is read | `items.maxStack`, `perks.damage.*` |
| `restart` | after a game restart: the game reads it once | `balance`, `world`, `difficulty`, `limits` |

A later point never writes a `restart` field: it answers `RestartRequired`. Ask per field: `Capabilities.Is("registry.<registry>.<field>")`.
**Joined clients** (`RegistrySchema.Find(...).JoinedClient`, a column on each page): `serverOnly` fields (all of `creatures`,
`arsenals`, `buffs`, `randomLoot`, `lootRules`) need nothing on the client; `now` fields (items, recipes, `perks.value`,
`skills.cost`, `abilities.timing`) travel by ContentSync to Kiln clients (a vanilla client keeps its own numbers and
prediction); `restart` fields (`balance`, `world`, `difficulty`, `limits`) are not transferred, **both sides need the same boot
values** and the join compares them. So a data pack with only `restart` fields needs `"clients": "mod"`, `"side": "both"`;
`creatures`, `arsenals`, `loot`, `drops` only: `"clients": "none"`. `kiln lint` says `KL213` for a registry-only pack whose
`clients` differs. A server moved past a time (`abilities.timing`) past a later event of the same ability is not sent to Kiln
clients: the server still acts at the new time.

## Re-author a pass when settings change: `Registries.Reauthor` (dev.32)

Edits derived from a config file or the game's state (a player's level) can follow a change without a restart:

```csharp
[RegistryEntryPoints(RegistryEntryPoint.WorldLoad)]
public sealed class Leveler : Mod
{
    public override void OnLoad() => Config.Register<Settings>().Changed += _ =>
    {
        if (!Kiln.Registry.Registries.Available) return;                            // the host's registry back-end
        var r = Kiln.Registry.Registries.Reauthor();
        if (!r.Ok) Log.Warn($"not authored again: {r.Reason}");
    };
    public override void OnRegistry(RegistryContext context)
    {
        if (context.Phase != RegistryPhase.Alter || !Capabilities.Is("kiln.registry")) return;
        var w = Kiln.Registry.Registries.For(context).LootRules.RarityWeights;   // the values below this mod: the game's, or other mods' edits
        w.Legendary = w.Legendary * 4;                                          // never stacks: each pass reads below its own last edit
    }
}
```

- `Registries.Reauthor()` runs your declared WorldLoad pass again from the next host tick, in the same world (WorldLoad is the
  one point re-authored on request, so the call names none). Its new layers replace the layers that point made, **in one turn**:
  the game never sees a moment without your values. Its `RegistryReauthorResult` is **not Ok**, with the `Reason` (also in
  kiln.log), for a mod without a declared WorldLoad point, a call **from inside an `OnRegistry` callback** (it would run again on
  every tick), or no live back-end. With no world loaded the next world load authors the point anyway.
- **The pass runs on a worker thread of Kiln's, off the world tick** (`context.OffTick` is true there; `context.Pass` says
  which pass it is). Read your settings and the game state **before** asking,
  in the handler that saw them change, and keep them in fields the pass reads. A tick-thread API called inside a re-authored pass
  throws `InvalidOperationException` ("... runs off it"); Kiln then runs that mod's passes on the tick from then on (slower,
  logged once). `[AnyThread]` APIs are fine (`Players.All`, `Progression.Get`: the last snapshot).
- Requests while one runs **coalesce** into one more pass: no debounce needed, but never ask on every tick or for a change your
  edits do not depend on. If the registry moves before the turn lands (another mod's change, a drop table the game loads
  meanwhile) Kiln drops it and runs it again; after three in a row the pass runs whole on the tick. A mod's unload waits for a
  pass running on the worker, and a new world's pass waits for one still running there (the two never run side by side).
- Re-authored layers are the newest: they win over other mods' WorldLoad edits of the same fields until those passes run
  again. Keep re-authored edits to fields other mods leave alone, or derive them from the game's values.
- A large pass fits: a Loot Rarity Leveler sized one (every field of the ~800 random loot tables, 12,752 edits) lands in a few
  milliseconds on the tick. Not seen in a real game: a hot reload mid-pass, drop tables mid-preparation, co-op or hosted worlds.

## The edit cap per call (dev.32)

One call (one `OnRegistry` callback, that is one phase of one entry point; one `Registries.Change`; one batch a native plugin
sends) takes at most the registry's cap of edits **of that registry**: `RegistrySchema.EditsPerCall["randomLoot"]` is
16,384, every other registry 4,096. Each registry counts on its own. A call over the cap is **refused whole**: nothing of it
applies, `registry.json` has a row for each registry it names (the over-cap one with its count and the cap), kiln.log says so,
and Kiln **never throws** for it in your code (before dev.32 an edit past 4,096 threw). Your other calls still apply. Split big
edits over the phases (`Alter`, `Final`) or several `Registries.Change` calls. Count edits, not entries: an edit is one field of
one entry.

## Views: read anything under Kiln names (dev.32)

`RegistryViews` is a read-only view over every registry and over every other resource type of the running build
(quests, NPC dialogs, materials, UI data: the long tail no registry covers). Stable views are the registries; **advanced** views
are named by Kiln from the game's reflection, verified per game build, read only.

```csharp
if (Capabilities.Is("kiln.registry.view") && RegistryViews.Find("quest") is { } quests)
{
    foreach (var id in quests.Select("game:quest_*"))
        if (quests[id].TryGet<int>("xpReward", out var xp, out var why)) Log.Info($"{id.Value}: {xp} xp");
        else Log.Info(why);                                             // this entry has no value of it, in Kiln words
}
var maxStack = RegistryViews.Find("items")!["game:material_t1_wood_logs"].Get<int>("maxStack");
```

`RegistryViews.All` and `Find(name)`; `RegistryView.Tier`, `Fields`, `Field(path)` (`CoveredBy` names the registry field over the
same member), `Select(selector)`; `view[id].Get<T>`, `TryGet`, `Snapshot()`, `view.TryRead`. `Get` throws
`KilnUnavailableException` when the field does not read for this entry and `KeyNotFoundException` for no such entry or field;
`TryGet` returns false with the reason. Any thread; nothing is cached. **The first read is slow** (a stable view prepares the
registry's values: 4 to 11 s; an advanced view's first `Select` or read lists its entries: about 0.2 s; a `Snapshot` of a long
resource about 1.2 s): make it off the game's thread (`Task.Run`), never in a tick. Views never write: write through a registry
field where one covers the member, or the old path (a data pack section, a resource patch).

## Checks

- Capability rows: `kiln.registry` (the transport: `Registries.For`), `kiln.registry.live` (`Registries.Change`, `Reauthor`),
  `kiln.registry.view` (`RegistryViews`), and `registry.<name>` and `registry.<name>.<field>` (the field's case and dots, no `[]`).
  The analyzer (KILN121) wants a `Capabilities.Is("row")` before each of those calls, as the snippets above do.
- `kiln/state/registry.json`: the live value and its writer chain per field, `overridden`, `composed`, `exclusive`, `yielded`,
  refused edits with a reason, `frontEnds`, `replay`. Written off the world tick after the start.
- Tests: `KilnHarness.Registry` in Kiln.TestKit is a recording fake (`Seed`, `Author`): it proves your callback, not the game
  ([testing.md](testing.md)). Try an edit on a test server and read `registry.json`.
- Not covered by any registry yet (use a boot patch): the settings screen's slider steps, what the mods behind a number do beyond
  it (Enemy Difficulty Limits' wider perception, More Flame Altars' merged bases), what a placed chest does for its base.
