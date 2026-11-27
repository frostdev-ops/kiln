# kiln.content: registries and data packs

Read and change the game's content at run time, the way Forge mods use registries and data packs: items (max stack,
rarity, damage, armor, fuel, …), recipes (ingredients, results, duration, unlock), random loot per item (stack sizes)
and drop tables (enemy drops, fixed chests). Every change is a layer owned by a mod over the game's original value:
it comes off when the mod unloads or reloads, and it is applied again at the next world load. Mods ship data packs
(`content/*.json`) or change things from code. Evidence and layouts: `docs/re/content.md`.

| Layer | Where |
|---|---|
| Symbols | `sigdb/symbols/server.d/content.json` (features `content.reflection`, `content.items`, `content.recipes`, `content.loot`, `content.drops`, `content.stack_guard`, `content.new`); client overlay `client.d/content.json` (`content.loca`, `content.ui_texture`, `content.recipe_ui`, `content.recipe_ui_refresh`) |
| C table | `native/core/abi/services/kiln_content.h` (`kiln.content` v4: v2 `export_changes`, v3 `define` / `definitions`, v4 `set_ref` / `undefine_owner` / `info`; appended to v4: `icon_alias`, `txn_begin` / `txn_end` (0.2.0-dev.28), `text` / `text_info` (0.2.0-dev.29.18, [the game's own texts](#the-games-own-texts))) |
| Native | `native/services/content/` → `kiln/services/kiln-content.dll` |
| Managed | `managed/services/Kiln.Content/` → `kiln/host/Kiln.Content.dll` (namespace `Kiln`, class `Content`) |
| Test | `mods/tests/content-test/` (PASS / FAIL lines) + `Observer/` (logs value changes); `mods/tests/newcontent-test/` (new items / recipes); `mods/tests/kitbash-test/` (references, texts, distribution; `syncdefs.py`: definitions in the content sync); `mods/tests/placeholder-test/` (+ `Items/`: missing content over four restarts); `mods/tests/craftmatch-test/` (a made item is what its recipe tooltip promises); `native/services/content/tests/run.sh` (`text_test.cpp`: the game's own texts over a fake lookup; near memory: the allocation policy on simulated address spaces and, under Wine, on a real fragmented one) |
| Sample | `mods/samples/BigStacks/` (bigger stacks, a C# port of EMF's big_stacks idea); `mods/samples/NewItems/` (new items and recipes, side both); `mods/samples/GearValues/` (gear values in the tooltip's numbers, data only) |

## Data packs
A mod folder may hold `content/*.json`. kiln.content applies every loaded mod's files when the world's registries
have loaded (about 0.3 s after the server world's first step, ~11 s before the save is read and players can join),
in mod load order and file-name order inside a mod. A pack follows its mod: unload the mod and its pack comes off
(within ~1 s), load it and the pack applies again; editing a file re-applies all packs (checked every ~2 s).

```json
{
  "format": 1,
  "items": {
    "Material_T1_Wood_Logs": { "maxStackSize": 1000 },
    "*": { "@where": { "maxStackSize": ">1", "category": "Materials" },
           "maxStackSize": { "mul": 4, "max": 9999, "raiseOnly": true } },
    "Weapon_T1_1H_SwordEpic_01": { "damageSetup.dmgMod": 1.25, "rarity": "Epic" }
  },
  "recipes": {
    "Tool_basic_FishingRod_T0_PK": { "inputs": [ { "item": "Material_T1_Twigs", "count": 1 },
                                                 { "item": "Material_T1_String", "count": 1 } ] },
    "Ammo_Arrow_T1_Flint": { "outputs": { "mul": 2 } },
    "*": { "@where": { "output": "Material_T1_Charcoal" }, "craftingDuration": 30 }
  },
  "loot":  { "Material_T1_Wood_Logs": { "stackSizeMin": 5, "stackSizeMax": 20 } },
  "drops": { "*": { "count": { "mul": 2 }, "chance": { "mul": 1.5, "max": 1 } },
             "a20e8eae-7833-462e-9ea9-59d1e17a1b98": { "items": { "Material_T1_Metal_Scraps": { "countMax": 10 } } } },
  "options": { "stackGuard": true }
}
```

- **Sections**: `items`, `recipes`, `loot` (random loot of containers and world pickups, per item), `drops`
  (default-inventory tables: enemy loot, fixed chests; key = table GUID or a glob), `gear` and `gearRules` ([gear
  values](#gear-values): typed item values in the tooltip's numbers), `options`.
- **Selectors** (keys): a debug name (`Material_T1_Wood_Logs`), an id (`0x2b0cf1d5`, decimal), or a
  case-insensitive glob (`Material_*`, `*`). Recipe names are the recipes' debug names (`Content.Dump()` lists them).
- **`@where`**: conditions on the game's original values: a number / bool / enum name for equality, `">1"`, `">="`,
  `"<"`, `"<="`, `"!="`, `"!Name"`, a list = any of; `name` (glob); recipes also `output` / `input` (an item name or
  glob that must appear in the list).
- **Fields**: any path of the registry's schema (from the game's reflection; `Content.Dump()` writes all of them):
  `maxStackSize`, `rarity`, `damageSetup.dmgMod`, `fuelSetup.burnTime`, `comfortSetup.comfortAmount`, `perkIds[2]`,
  `craftingDuration`, `knowledgeRequirement.knowledgeOrQueryId`, `stackSizeMax`, `countMin`, `spawnProbability`, ….
  Identity fields (ids, GUIDs, ItemInfo `flags`, RecipeInfo workshop / props mask) are read-only. Item references
  (models, templates, perks, ...) take `"item:<name>"` / GUID values: see "Kitbash" (game items need `"@refs": true`).
- **Values**: numbers (rounded and range-checked for integer fields: `maxStackSize` is 16-bit, at most 65535), `true`
  / `false`, enum names (`"Epic"`), flag-name lists, times in seconds; or an op relative to the game's original value:
  `{"mul": 4, "add": 0, "min": 1, "max": 9999, "raiseOnly": true, "lowerOnly": false}` or `{"set": v}`.
- **Recipe lists**: `inputs` / `outputs` = the whole new list (1–32 entries, `{"item", "count"}` or
  `{"category": "0x…", "count"}` for "any item of an input category"), or `{"mul": x, "min": 1}` to scale the original
  amounts.
- **Drop tables**: `count` scales countMin and countMax, `chance` spawnProbability (kept within 0..1), `items` changes
  stacks of one item (`countMin`, `countMax`, `spawnProbability`, `weight`). The game loads tables when it first
  fills a default inventory; rules apply at that moment, before the roll.
- **Order and conflicts**: within a pack, later entries win; across packs, the later mod in load order wins; changes
  made from code win over every pack (in call order) and over registry edits. An op of a pack or of code is always computed
  on the game's original value (`raiseOnly`: never below the game's value), never on what another pack, a registry edit or
  other code made of it ([registries README](../modding/registries/README.md#stages-and-conflicts)). Two owners giving one field different values is a conflict:
  logged (`conflict: items Material_T1_Twigs.maxStackSize: pack … sets 600, bigstacks … sets 1000 (the later one wins: …)`)
  and listed in `Content.PackReport()`. A clone's definition and its own mod's later changes to it are not: the definition is
  the clone's starting value (a block writes `none` until a game VFX's name is known, then a rule writes that VFX). Problems (unknown field, value out of range, selector matching nothing) are
  logged per file and never stop the other entries.

### Reloads and edits (0.2.0-dev.31.13)
A pack follows the mod's **load**, not only its id. `reload <id>` (kiln/control, `kiln reload`, the SDK's `KilnReload`,
`Mods.ReloadAsync`) takes the mod's layer off and applies its current `content/*.json` again, exactly as `unload <id>`
then `load <folder>` do; so does an unload and a load that fall between two of Kiln.Content's looks (every 25 steps).
Before 0.2.0-dev.31.13 only a change in the list of loaded mod ids re-applied the packs. A reload keeps that list as it
was when the mod stays in its place (the last loaded mod, for example one hot-loaded with `load`), so its old layer
stayed until the next restart or file edit.

The mod's packs apply in the place the mod had in the data-pack order at the start (0.2.0-dev.31.18): Kiln.Host loads a
reloaded mod after the mods that stay, but Kiln.Content lists the mods to kiln.content with each mod in the place it first
had, so a field two mods' packs set goes to the same mod as at the start (before, the reloaded mod's packs applied after
every other mod's until the next start). An `unload` and a later `load` put the mod back in that place; a mod first loaded
after the start comes after every mod before it. The registry stage composes the `registry` sections in the start's order
too.

- One line names the mod and the reason, then the usual summary:
  `mod skilltest was reloaded (1.0.0 -> 1.0.1): re-applying data packs (its old layer comes off, its current files apply)`.
  Other reasons: `was reloaded with <mod>` (a dependent reloaded with the mod it depends on), `was unloaded and loaded
  again`. The list of loaded mods changing still logs `the set of loaded mods changed: re-applying data packs`.
- Every data layer that comes from the packs follows: items, recipes, loot, drops, gear, the gamedata sections
  (`kiln.gamedata`, including their `add` clones), the sections other services register (`creatures`, `abilities`,
  `Content.RegisterSection`), new items and recipes from `add` lists, and the `registry` section.
- The content generation grows by one, so the [content sync](../sync.md) sends joined Kiln clients the new state, and
  `Content.OnReady` handlers and `ContentReadyEvent` run again (`Reload` true), as after any pack reload.
- A reload of a mod without data packs re-applies nothing.
- An edit of a mod's `kiln.mod.json` (its `id`, `version` or `side`) counts as a pack file change: the ~2 s scan
  re-applies, without a reload.

### When a reload fails partway (0.2.0-dev.31.13)
A mod's packs apply whole from one version, never half:

- **The mod does not load again** (its `OnLoad` throws, its manifest is invalid, a dependency is missing): it is not
  loaded, so its layer comes off, as after `unload`. The reload's result and `kiln/logs/mods.txt` say why; the line
  `data packs of <mod> skipped: Kiln.Host did not load the mod` follows.
- **A file of the new version cannot be read or is no pack** (cut off while a deploy writes it, not JSON, a `format`
  other than 1): the mod keeps the **old layer**, whole: the packs it had at its last application in which every file
  of it read, from the files' text as it was then. The new files of that mod do not apply, not even the good ones. The
  log says `data packs of <mod>: <file> (<why>) cannot be applied: the packs it had before stay applied, whole (from
  generation N), until every file of it reads as a pack`, and `Content.PackReport()` lists the kept files as
  `<file> (kept from generation N)` and the new ones as `<file> (not applied)`. The next reload or file edit tries the
  files again.
- With no old layer to keep, the files that read apply and the broken one is reported, as before. There is none when
  this world has not yet applied the mod's packs with every file reading: the world's first load, the mod's first
  application after its packs came off (an `unload` seen as a change of the list, its files gone), or every application
  since then had a broken file. A world reset forgets every old layer.
- **A pack throws while it applies** (an unexpected value type in a section's code): the mod's application ends at that
  pack, and everything the mod's packs did in it comes off at once, in the mod's place in the load order
  (since 0.2.0-dev.31.17; before, only its field and drop layers did, after every other mod had applied):
  - its field and drop layers;
  - the new items and recipes of its `add` lists. One it changed is the definition it had before again, as a new
    version with the clone, recipe and random-loot entry the game had (the game's maps point at them again), so what
    other mods' packs set on its fields applies as before. One it took from another mod's pack is that pack's again.
    One it added comes off as a removed pack's does: its id stays registered until the next start, so nothing that holds
    it breaks, and a recipe leaves its station;
  - kiln.gamedata's `add` clones (templates, sequences, perks, buffs), the same way, and the configs its item paths added
    to an item (`impactValues.<config>` the item did not have);
  - what it applied in a section of another service (`creatures`, `abilities`): the service takes the mod's part off
    (`pack:<mod>`, every source), also a section it had from the application before. A section that waited for its
    service is forgotten;
  - the stack guard, which is the whole game's: on or off as it was when the mod's packs began, if one of them switched
    it (`"options": {"stackGuard": false}`, or a raised max stack turned it on).

  Then its packs from the last application in which every file read apply again in that place, whole, with their
  `registry` sections (ordinary problems in them, such as an unknown field, are reported as before and do not stop them).
  Without such packs, or when one of them throws too, none of the mod's packs apply and no `registry` section of it
  reaches the registry stage. Mods later in the load order apply over the result, so their layers are what they would be
  without the pack that threw, and no other mod's definition, clone, config or section is touched. The report lists the
  mod's files after the one that threw as `<file> (not applied: an earlier pack of the mod threw)` and the mod as
  `<mod> (set apart)`; the log says `data packs of <mod>: a pack threw while it applied: what it did came off (<n>
  definition(s), clone(s) and section(s) besides its layers); the packs it had before apply again, whole` (or `none of
  its packs apply`). The report's `setApart` list has one row per mod set apart in that application (`mod`, `file`: the
  pack that threw, `reason`: what it threw, `previous`: whether its packs from before apply again), and the reply of a
  `reload <id>` names them (0.2.0-dev.31.18; [architecture.md](../architecture.md#what-reload-id-covers)). What a service does with its own state beyond its section (a creature it already spawned from it)
  is the service's.
- Inside one application the layers change under one store lock: no reader that takes it (a drop table being acquired,
  the content sync's export) sees a mix of the old and the new layer.

## Gear values
A typed face over the item fields of gear, in the numbers the game's own tooltip shows: a server owner writes "power 30" or
"physical armour 45" instead of solving `damageSetup.dmgMod` or `armorSetup.distribution.physical` by hand. The `gear`
section of a data pack and `Content.Gear(item)` in C# set the same values; Kiln solves them per item with the game's
formulas, and writes them as ordinary item fields: layers of the pack's (or the calling mod's) owner, reverted with it,
listed as conflicts, synced to Kiln clients like every items rule. Sample: `mods/samples/GearValues/` (data only).
Capability `content.gear` (Kiln.Content's row; off only without kiln.content).

```json
{ "format": 1,
  "gear": {
    "Weapon_T1_1H_Sword_Craft": { "power": 30, "damage": { "cut": 0.75, "fire": 0.25 }, "levels": [5, 5] },
    "Armor_T2_*":               { "@where": { "rarity": "Rare" }, "armor": { "physical": { "mul": 1.2 } } },
    "Ring_T3_Ring_of_Light":    { "socket": true, "rolled": false } },
  "gearRules": { "rarityDurability": [0.9, 0.95, 1, 1.1, 1.25], "socketChance": { "epic": 0.75 } } }
```

| Key | Means | Writes |
|---|---|---|
| `power` | the damage the tooltip shows at the item's lowest level (`levels` of the same rule, else the game's), 0..100000 | `damageSetup.dmgMod`, solved with the item's speed: tooltip = int(int(10 × 0.15 × level + 10) × dmgMod × speedMod) |
| `speed` | the damage factor of the weapon's speed class (it multiplies the damage; the attack speed itself is the animation), more than 0, at most 100 | `damageSetup.speedMod`. Alone it changes the tooltip's damage: give `power` too to keep it |
| `damage` | shares per kind: `cut`, `pierce`, `blunt`, `fire`, `ice`, `shroud` (the game's fog), `lightning`, `poison`, `healing` (0..10; 0.5 = half); `wood`, `stone`, `metal`, `explosion`: the whole numbers the game keeps for what a tool harvests and breaks. Kinds not named keep their value | `damageSetup.distribution.*`. Shares of the hit kinds that do not add up to 1 are a warning (Kiln's weapon blocks write shares that add up to 1) |
| `armor` | the armour the tooltip shows per kind at the item's lowest level: `physical`, `magical`, `cut`, `pierce`, `blunt`, `fire`, `ice`, `shroud`, `lightning`, 0..100000 | `armorSetup.distribution.*`: tooltip = round((15 × level + 23) × share) |
| `block` | how well the item blocks, 0..1 | `blockSetup.quality` |
| `staminaCost` | the item's StaminaCost value, for items that have one (as weapon blocks write it) | `impactValues.StaminaCost` |
| `rarity` | `common` … `legendary` (any case, or 0..4): what the recipe tooltip shows and a copy is made with when the game does not roll it. The game has no rarity above legendary | `rarity` |
| `levels` | a level (both ends) or `[lowest, highest]`, 1..55 | `itemLevelRange.minLevel` / `maxLevel` |
| `rolled` | the game rolls the rarity of each new copy (as for loot weapons) or not | `generateRarity` |
| `perks` | the five perk slots, as the items section's `perks` (a perk GUID or name, `item:<name>`, `none`, null = keep); a game item needs `"@refs": true` (or `"options": {"refs": true}`), as there | `perkReferences[i]`, `perkIds[i]` |
| `socket` | new copies get a gem socket (gear other than weapons needs Kiln's sockets: [gems-sockets.md](gems-sockets.md)) | `gemSlotOverride` |
| `maxStack` | 1..65535 | `maxStackSize` |
| `recipe` | `{"inputs": [...] or {"mul": x}, "duration": seconds}` for every recipe that makes the item (other recipe fields: the recipes section). No recipe making it is a warning | the recipes' `inputs`, `craftingDuration` |

- **Numbers or ops.** Every number takes an op relative to the game's value of that item, in the same units: `"power":
  {"mul": 1.2}` is a fifth more than the game's tooltip damage, `"armor": {"magical": {"add": 5}}` five more armour; also
  `set`, `min`, `max`, `raiseOnly`, `lowerOnly`. Values are solved for each item a selector matches, at its own level.
- **Selectors, `@where`, order** as in the items section. The `gear` section applies after `items` in the same document,
  so a typed key wins over a raw path that names the same field; other keys of a gear rule are item field paths and pass
  through as in `items`.
- **Levels.** `power` and `armor` are the numbers at the item's lowest level (the recipe tooltip's). A rolled loot
  weapon's copies at higher levels get more, by the game's curve. `levels` alone keeps `dmgMod` and the armour shares,
  so the tooltip's numbers follow the game's curve to the new level: give `power` and `armor` in the same rule to keep them.
  The game brings an existing copy's level into the item's range when the save loads ("Rarity, level and perks of new
  gear" below): narrowing a released item's range lowers the level of copies players have.
- **Rarity.** A copy has rarity + 1 enhancement slots, filled from the type's perk slots: a slot left empty makes the copy
  one rarity lower, so raise `rarity` together with `perks` where the type has fewer slots. Copies already made keep theirs.
- **Validation.** Each problem is one line of the pack's report and kiln.log, naming the item and the key
  (`gear Weapon_X.power: this item deals no damage of its own (damageSetup.isSet is off)`, `gear Y.levels: [0, 60] is not
  within the game's item levels 1..55, lowest first`, `gear Z.damage.frost: not a kind of damage; there are cut, pierce,
  ...`); the rest of the rule and the file still apply. `kiln lint` checks names, kinds, ranges and op keys before you start.
- **Who sees it.** The server's values act for everyone. Kiln clients get them through the content sync (the tooltip shows
  the new numbers without the mod); a player without Kiln plays with them and sees the game's numbers in the tooltip.
- **Formulas.** The tooltip's damage and armour follow from the game's balancing table (`genericWeaponSeed` 10,
  `powerCreepWeapon` 0.15, armour offset 23 and gradient 15 on the builds Kiln measures). Kiln.Blocks checks them against
  the running game when a mod uses item blocks, and logs a warning if a game update changed them (gear values and item
  blocks are then off by that much until Kiln follows).
  Native: `native/services/content/gear.cpp`, managed: `Kiln.Content/GearMath.cs`, the same numbers in both.

### In C#
```csharp
Content.OnReady(_ =>
{
    var sword = Content.Gear("Weapon_T1_1H_Sword_Craft")!;   // null: no such item
    sword.Power = 30;                                         // the tooltip's damage at sword.Level
    sword.SetDamage(GearDamageKind.Fire, 0.25);
    sword.SetDamage(GearDamageKind.Cut, 0.75);
    var chest = Content.Gear("Armor_T2_Warrior_Tank_Chest")!;
    chest.SetArmor(GearArmorKind.Physical, chest.Armor(GearArmorKind.Physical) + 10);
    chest.Rarity = ItemRarity.Epic;                           // the type's: copies made from now on
});
```
- `Content.Gear(string | ItemId | ContentEntry)` → `GearStats`: `Power`, `PowerAt(level)`, `Speed`, `Damage(kind)` /
  `SetDamage`, `Armor(kind)` / `SetArmor`, `Block`, `Rarity`, `Rolled`, `Socket`, `MaxStack`, `Level`, `MaxLevel`,
  `SetLevels(lowest, highest?)`, `HasDamage`, `HasArmor`, `Reset()` (takes this mod's layers off these fields). Perks:
  `Content.Items.Get(item).SetPerks(...)`. Reads give the values now (every layer applied).
- Writes are `ContentEntry` writes of the calling mod (reverted with it, `ContentChangedEvent`, conflicts with packs and
  other mods). A value out of range throws `ArgumentOutOfRangeException`; `Power` or `SetDamage` on an item without damage
  of its own, `SetArmor` on one without armour, throw `InvalidOperationException`. Tick thread.
- Test: `managed/tests/Kiln.Blocks.Tests/GearValuesTests.cs` (offline), `native/services/content/tests/gear_test.sh`
  (the pack section's expansion), `mods/tests/content-test` (lab: its pack's `gear` rules and `Content.Gear`).

### Gear rules (boot values)
`gearRules` changes the game's balancing values for gear (`keen::BalancingTable`). The game reads them once, when it
starts, so they are [resource patches](../modding/resource-patches.md) made before that: the mod needs `"boot": true`
in its kiln.mod.json, and the capability `mods.boot.resource_patches`. Kiln reads every loaded mod's `content/*.json` at
the boot (files by name) and turns each file's `gearRules` into one patch of the table (`gearRules@<file>` in
`state/boot.json` and kiln.log).

| Key | Means | Member (the game's values) |
|---|---|---|
| `rarityDurability` | the factor of each rarity on a copy's max durability, 0.01..100 | `rarityDurabilityMod` (0.9, 0.95, 1, 1.05, 1.1) |
| `socketChance` | the chance of each rarity that a new weapon gets a gem socket, 0..1 | `gemSlotProbability` (0, 0.05, 0.25, 0.5, 1) |
| `durability.start`, `.growth` | max durability = (start + growth × (level − 1)) × the rarity's factor × the item's own factor | `durabilityStart` (145), `durabilityGrowth` (2) |
| `unlockCost.seed`, `.creep` | what unlocking an enhancement at the workbench costs, and how it grows with the level | `perkUpgradeCostSeed` (8), `perkCostCreep` (0.15) |
| `unlockCost.weapons`, `.armor`, `.rings` | the cost's factor per kind of gear | `perkCostFactorWeaponsShield`, `perkCostFactorArmor`, `perkCostFactorRings` |
| `unlockCost.advanced`, `.advancedFrom` | the extra cost of a legendary item's later unlocks, and from which unlock (0..5) | `advancedPerkCostNominator` (8), `advancedPerkCostStartLevel` (4) |

- A rarity's five values: a list, common to legendary (`null` keeps the game's), or `{"epic": 0.75}`. Numbers or ops
  (`{"mul": 2}`) as in resource patches.
- **Whole or not at all**: one wrong value (out of range, an unknown key) leaves the file's `gearRules` out, with the reason
  in kiln.log (`mod my-mod: content/gear.json: its gearRules are not applied: gearRules.socketChance[4]: 1.5 is outside 0..1`);
  a mod with `"boot": "required"` stops the start instead. Without `"boot"` in its kiln.mod.json, kiln.log says the rules
  are not applied. At a world's load kiln.content skips `gearRules` (they are not item rules).
- `lootRarityRates` (the game's loot rarity rates) is refused: Kiln has not mapped the resource that holds them yet.
- **Server values act; a Kiln client with the mod shows them.** Each process applies the mod it has: a player without the
  mod (or without Kiln) predicts with the game's numbers (a durability bar, a socket on a weapon the server made). Put
  such a mod on both sides (`"clients": "mod"`).
- Two mods setting the same member are a conflict of resource patches: the later mod in the load order is refused
  ([resource-patches.md](../modding/resource-patches.md#two-mods-on-one-resource)).

## C#
Reference the assembly like any Kiln service:
```xml
<ProjectReference Include="…/managed/services/Kiln.Content/Kiln.Content.csproj" Private="false" ExcludeAssets="runtime" />
```
```csharp
using Kiln;

public override void OnLoad()
{
    // Runs after the data packs at every world load and pack reload (at the next tick if the world already loaded).
    Content.OnReady(_ =>
    {
        var wood = Content.Items.Get("Material_T1_Wood_Logs")!;
        wood.MaxStack = 2000;                                     // layer of this mod; reverted on unload / reload
        var rod = Content.Recipes.Get("Tool_basic_FishingRod_T0_PK")!;
        rod.SetInputs([RecipeStack.Of("Material_T1_Twigs", 1), RecipeStack.Of("Material_T1_String", 1)]);
        rod.CraftingDuration = TimeSpan.FromSeconds(5);
        Content.Loot.Get("Material_T1_Wood_Logs")!.Set("stackSizeMax", 20);
        var r = Content.Apply("""{ "items": { "Material_*": { "maxStackSize": { "mul": 2 } } } }""");
        foreach (var c in r.Conflicts) Log.Warn(c.ToString());
    });
}
```
- `Content.Available`, `Ready`, `Status()` (counts, packs, errors, conflicts, changed fields, stack guard, generation).
- Registries `Content.Items`, `Recipes`, `Loot`, `Drops` (`IReadOnlyList<ContentEntry>`): `Get(name | "0x…" | GUID)`,
  `Get(uint id)`, `Get(ItemId)`, `Fields` (`ContentField`: path, game type, offset, size, value type, writable, enum
  values), `Field(path)`.
- `ContentEntry`: `Id`, `Name`, `Guid`, `Modified`; `Get(path)` / `Original(path)` → `ContentValue` (type, integer,
  number, GUID, enum / flag names); `Set(path, long|double|bool|TimeSpan|string)`; `Reset(path)` (drops this mod's
  layer); items: `MaxStack`; recipes: `Inputs`, `Outputs`, `SetInputs`, `SetOutputs`, `CraftingDuration`, `CraftingWater`
  (since 0.2.0-dev.30.3: `waterCharges`, see "A recipe's water in code"); drops:
  `Stacks`, `SetStacks` (same stacks, counts / chances / weights), field paths `"[3].countMax"`.
- `Content.Apply(json)` / `ApplyFile(path)` (the pack format, as this mod's code layers) → `ContentReport` (applied,
  errors, warnings, conflicts); `Revert()`; `ReloadPacks()`; `PackReport()` (JSON); `Dump(path?)` (every registry with
  its schema, non-zero fields, recipe lists, drop stacks and original values of changed fields; one entry per line,
  `kiln/data/content/dump.json`; ~0.5 s); `StackGuard` (get / set).
- `ContentReadyEvent` on the event bus (`Generation`, `Reload`): fires on the tick thread after each application.
  Handlers changing content should use `Content.OnReady`, which runs them as their mod (so the changes are attributed
  to it and reverted with it).
- Rules: tick thread only (mod code is). Changes are owned by the calling mod: a mod's layers come off in its
  `Retiring` (unload, reload, fault-disable); kiln.content also notices owners that went away within ~1 s. Writing an
  `ItemId`/GUID, a value out of the field's range, or an unknown path throws `ArgumentException` /
  `KeyNotFoundException`.

Without any mod referencing Kiln.Content, data packs still apply (native), in mod-folder order, with folders holding a
`.disabled` file skipped; the pack-follows-mod behaviour (load order from Kiln.Host's mod list, unload reverts) needs
Kiln.Content running.

**A pack needs no code.** A folder with `kiln.mod.json` (no `entryAssembly`) and `content/*.json` is a mod of data only
([manifest.md](../modding/manifest.md#mods-of-data-only)): Kiln.Host loads it (`mod my-pack 1.0.0 loaded in 0.4 ms (data only: no
entryAssembly)`), so its packs apply in its place in the load order and come off when it unloads. Before dev.22 Kiln.Host refused
such a manifest and the packs were skipped. When Kiln.Host does not load a folder that has packs (an invalid manifest, a side that does not
match, a missing dependency, a failed `OnLoad`), kiln.log warns: `data packs of my-pack skipped: Kiln.Host did not load the mod (its
"mod ... not loaded" / "skipped" line above and kiln/logs/mods.txt say why)`.

## C (native plugins and services)
```c
const KilnContentApi* c = api->service((KilnStr){"kiln.content", 12}, 1);   /* in on_ready */
KilnContentEntry e = { sizeof e };
if (c->find(KILN_CONTENT_ITEMS, (KilnStr){"Material_T1_Wood_Logs", 21}, &e) == KILN_OK) {
    KilnContentValue v = { KILN_CV_INT, 0, 2000 };
    c->set(owner, KILN_CONTENT_ITEMS, e.id, (KilnStr){"maxStackSize", 12}, &v);
}
c->subscribe_ready(owner, on_content_ready, NULL, &handle);   /* tick thread, after every application */
```
`status`, `count` / `entry_at` / `find`, `field_count` / `field_at` / `field_find` / `enum_name`, `get` / `set` /
`reset`, `stacks` / `set_stacks`, `apply_json`, `revert_owner`, `dump`, `reload_packs`, `report`, `subscribe_ready` /
`unsubscribe_ready`, `stack_guard`, `label_owner`, `pack_mods`. Every field: the header.

**Content transactions** (appended to the table in 0.2.0-dev.28; check `header.size`): `txn_begin(owner, &txn)` sets
every layer of one owner aside; what the owner then applies (`apply_json`, `set`, `reset`) can be kept with
`txn_end(txn, 0, ...)` or taken back whole with `txn_end(txn, 1, ...)`. A rollback puts the owner's layers back exactly
as they were (layers written since come off, layers taken off come back in their order, with the sequence and program
ids that follow a reference), writes them, and reads every touched field back from memory. When one does not read
back, every layer of the owner is taken off (as `revert_owner`, fail closed) and the result is `KILN_ERR_REVOKED`. The
report is `{"outcome": "committed" | "restored" | "revoked" | "cancelled", "fields": n, "error"?}`; `cancelled`
(`KILN_ERR_NOT_FOUND`) means the owner's layers were taken off while it was open (revert, the owner going, the world's
reset), so nothing is put back. One open transaction per owner, at most 64 open, tick thread. Field rules only: a
document's `add` lists, drop rules, options and other services' sections are not covered. Kiln.Blocks' routed weapons
use it (item-blocks.md, "Follow-ups"); mods do not call it. Host test: `native/services/content/tests/txn_test.sh`.

## How it runs
- At boot: symbols, the DefaultInventoryAcquire hook (drop tables). In Pre, every 4 steps until ready: the world's
  `g38_items::ItemRegistry` is done loading (`+0x320` = 0, count stable), the recipe resource is in; then layouts from
  the game's reflection, a snapshot of the four registries, the data packs, the ready callbacks.
- The store keeps each touched field's original bytes and one layer per owner; the newest layer is written, the
  original comes back when no layer is left. Writes go out in batches; the data sits in read-only resource memory and
  is unprotected page cluster by page cluster for the copy.
- **Stack guard**: the game cuts a stack down to its item's max stack size whenever a transaction touches it
  ("[inventory] Deleted N items from stack"). Taking a raise back (unload, reload, config change) would then delete
  items, so kiln.content switches the guard on as soon as any max stack is raised (one code byte in
  validateItemStack) and keeps it on until the server restarts. After a restart **without** the mod, stacks above the
  vanilla size are cut at their next transaction (as in vanilla; EMF warns the same for big_stacks); with the mod they
  are safe because packs and `OnReady` changes apply before the save is read.
- Nothing is written to the save; the game reloads its data from its files on every start.

## Multiplayer: what clients see (the client has its own registries)

Kiln clients get the server's values when they join and while connected (docs/kiln/sync.md); the table below is for vanilla clients.
| change | works with an unmodded client? |
|---|---|
| random loot, drop tables | yes: rolled on the server |
| max stack size | the server's stacks are right (and saved); the client's UI and prediction use the vanilla size: splits / merges may look odd until the server's state arrives |
| recipe ingredients / results | the server crafts by its recipe; the client's crafting UI shows (and checks materials against) the vanilla recipe: install the same mod on clients |
| crafting duration, item stats | the server's values apply; the client shows vanilla numbers |
| unlock conditions | client-only (the server does not check knowledge) |
| new items / recipes | no: the client needs the same definitions (install the mod on both sides); an unmodded client shows "[Invalid Item Stack]" |
The pack format has no server-only parts: the same `content/*.json` is meant to be applied by the client side of Kiln
(the client exe has the same registries and every content signature resolves on it).

## For Kiln.Admin
| op | Kiln |
|---|---|
| `content_status {}` → `{ready, items, recipes, loot, drops, packs, errors, conflicts, changed, stackGuard}` | `Content.Status()` |
| `content_get {kind, entry, field}` → `{value, original}` | `Content.Registry(kind).Get(entry)?.Get(field)` / `.Original(field)` |
| `content_set {kind, entry, field, value}` | `entry.Set(field, value)` (owned by Kiln.Admin; reverted with it) |
| `content_recipe {entry}` → `{inputs, outputs, duration}` | `entry.Inputs`, `entry.Outputs`, `entry.CraftingDuration` |
| `content_report {}` / `content_dump {}` | `Content.PackReport()` / `Content.Dump()` |
| `content_reload {}` | `Content.ReloadPacks()` |

## Test (lab, no player)
```sh
export KILN_OUT=<build dir>
dotnet build mods/tests/content-test/ContentTest.csproj -c Release -o $KILN_OUT/mods/content-test --artifacts-path $KILN_OUT/dotnet
dotnet build mods/tests/content-test/Observer/ContentObserver.csproj -c Release -o $KILN_OUT/mods/content-observer --artifacts-path $KILN_OUT/dotnet
tools/deploy-server.sh ~/srv/enshrouded/labs/content --policy any --samples
tools/lab/lab.sh start content        # fresh: 34 PASS (registries, pack, code changes, 1500 logs in one slot, drops, dump), and since 0.2.0-dev.29.19 six gear checks (written, not run in a lab yet)
echo "reload content-test" > ~/srv/enshrouded/labs/content/kiln/control/r.cmd   # 5 PASS: code layers came off, pack stayed
echo "unload content-test" > …/u.cmd; echo "load content-test" > …/l.cmd        # OBSERVE lines: everything back / again
tools/lab/lab.sh stop content; tools/lab/lab.sh start content   # 2 PASS: pack at world load, code re-applied
```
Delete `kiln/data/content-test/` for a fresh run. BigStacks: build it into `$KILN_OUT/mods/bigstacks`; it logs
"raised the max stack size of 2252 of 2252 stackable items (x4, at most 9999)". With BigStacks and content-test
together, content-test's pack values lose to BigStacks' code layer (reported as conflicts), so run them apart.

## Client checklist (needs a joined player)
1. BigStacks on the server only: pick up / craft more than 250 wood logs: one stack in the backpack; split, merge and
   drag a stack of 1000 between backpack and a chest; the counts shown match the server's.
2. The same with BigStacks on the client too (once the client side of Kiln applies packs): no visual split / merge
   glitches.
3. Crafting UI with a changed recipe (server only): what the station lists for `Tool_basic_FishingRod_T0_PK`; craft it
   with the new (server) ingredients; confirm the server takes the new amounts and gives the new result.
4. A timed station (charcoal kiln at 30 s instead of 120 s): the station finishes after 30 s; the progress bar.
5. Random loot (`loot` section) and drops (`drops` section): open a fresh chest / kill an enemy and see the new
   amounts; enemy loot spot counts doubled with `"*": {"count": {"mul": 2}}`.
6. Unload BigStacks with a stack of 1000 in a player's backpack: the stack stays 1000 (stack guard) and the client
   copes; restart without BigStacks: the stack is cut to 250 on its next move (vanilla behaviour).
7. An item stat change (`damageSetup.dmgMod`): the damage a player deals changes; the tooltip shows vanilla.
8. Gear made as promised (NewItems): the hand menu's Glacier tooltip and a newly crafted Glacier in the bag show the same: LEVEL
   1/5, EPIC, Damage 17, enhancements at levels 2-5. Tower Sword: LEVEL 1/2, COMMON. Frost Edge: LEVEL 1/4, RARE. Frost Mail: LEVEL 5/8, RARE.
9. v4, hosting with NewItems (a save past the intro): Glacier in hand shows Weapon_T4_1H_SwordEpic_02's
   model and the glacier.png icon; a dropped Glacier shows the T4 model on the ground; Frost Mail worn shows the
   Armor_T2_Warrior_Tank_Chest model; tooltips "Glacier" / "Frost Mail"; game language German: "Gletscher" /
   "Frostkettenhemd"; the hand menu lists Frost Mail (4 Frost Shards only) and crafting it works.
10. v4 loot and vendors: on a server with a definition with `"loot"` (kitbash-test's Kiln_Kitbash_Token), open fresh
   chests where `like` drops: the new item appears now and then; kill a scavenger (the table of kitbash-test's
   `drops`): the loot spot holds Kitbash Token / Coin; the fisher trader's screen lists Kiln_Kitbash_Trade (3 Coins ->
   1 Token).
11. v4 sync: join a dedicated server running placeholder-items (`"clients": "kiln"`) without having it: Placeholder
    Test Sword / Ore / Gem show their names (not "[Invalid Item Stack]"), the hand menu lists Kiln_PH_Ore_Recipe and
    crafting it works; leave: the client log says the definitions were set aside.

## New items and recipes (Forge's DeferredRegister)
A mod adds items and recipes the game does not have: each definition has a **fixed GUID** and is a clone of a base
entry (the base item's model, icon, behaviour and stats; the base recipe's station and requirements) with its own
debug name, texts and field values. Ids are FNV-1a over the GUID's 16 bytes, the game's own ItemId / RecipeId rule,
so the server and every client that registers the same definition agree on them without any sync, and saves (which
store item ids) find the item again after a restart. Evidence: `docs/re/content.md` section 7.

A clone copies the item, not what its item entity (`pidEntity`) is made from. Some of an item's numbers live there:
a watering can's capacity is its template's `FuelSetup.maxFuelCharges`, which every can shares, so a clone of the can
holds 100 like the game's. For water containers use the item block (`ItemKind.WateringCan`, `Holds`, `Flow`:
[modding/item-blocks.md](../modding/item-blocks.md#water-containers)); it clones the template too.

```csharp
using Kiln;

public override void OnLoad()   // register early: the registries take them when the world loads, before the save
{
    Content.Items.Register(new ItemDefinition
    {
        Guid = Guid.Parse("54b131f3-525f-451c-9976-a4bbcd8393c6"),   // fixed forever (saves keep the id)
        Name = "Kiln_Sample_FrostEdge",                              // debug name: packs, recipes, admin tools
        BaseOn = "Weapon_T1_1H_SwordEpic_01",                        // the Bronze Sword: model, icon, behaviour
        DisplayName = "Frost Edge",                                  // what players see (every language)
        Description = "A bronze blade quenched in frost.",
        Fields = new Dictionary<string, object> { ["damageSetup.dmgMod"] = 1.5, ["rarity"] = "Rare" },
    });
    Content.Recipes.Register(new RecipeDefinition
    {
        Guid = Guid.Parse("51805e95-e1ea-44ef-85ea-cfbfc7882644"),
        Name = "Kiln_Sample_FrostEdge_Recipe",
        Station = "hand",                                            // or BaseOn = "<recipe>", or "0x<workshop id>"
        Inputs = [("Kiln_Sample_FrostShard", 3), ("Material_T1_Twigs", 2)],
        Outputs = [("Kiln_Sample_FrostEdge", 1)],
        Duration = null,                                             // the template's
        Unlock = RecipeUnlock.Always,                                // no knowledge needed (see below)
    });
}
```
- `Content.Items.Register(ItemDefinition)` / `Content.Recipes.Register(RecipeDefinition)` (extension methods; also
  `NewContent.Register(...)`, `NewContent.RegisterJson(kind, json)`) → `ContentDefinition` (`Kind`, `Guid`, `Id`,
  `Name`, `State`: `defined` / `unchanged` / `redefined` / `adopted`). An invalid definition throws
  `ArgumentException` (zero GUID, bad debug name, a name another definition uses, a recipe without base or station,
  empty lists, counts outside 1..65535); without support on the build `NotSupportedException`.
- `NewContent.Available`, `NewContent.IdOf(Guid)`, `ItemDefinition.Id`, `NewContent.Definitions()` (every definition
  with `State`: `pending` (no world loaded yet), `registered`, `orphaned`, `error` + `Problem`, and `Worlds`:
  `server`, `client`), `NewContent.DefinitionsJson()`.
- Once registered, a new item / recipe is an ordinary registry entry: `Content.Items.Get("Kiln_Sample_FrostEdge")`
  (by name, `0x` id or GUID), `Get/Set/Reset` of any field (layers of other mods work on it), recipe `Inputs` /
  `Outputs` / `SetInputs`, `Dump()`. kiln.items gives and takes it (`Items.Give(chest, new ItemId(id), …)`, gear gets
  its item entity, level and rarity like the base) and lists it in `Items.All`.
- Item definitions: `BaseOn` (debug name, `0x` id or GUID; required), `DisplayName`, `Description`, `Lore` (texts:
  null keeps the base's), `Fields` (paths of `Content.Items.Fields`: numbers, bools, enum names, string lists for flag
  sets; identity fields `itemId`, `objectId`, `flags` cannot be set). Gear: see "Rarity, level and perks of new gear".
- Recipe definitions: `BaseOn` (a recipe to copy: station, level, comfort, props, cooking, …) and/or `Station` (a
  recipe whose station to use, `"0x<workshop id>"`, `"hand"`: the player's own crafting menu, or `"workshop:<guid>"`: a
  workshop of the mod's own, id FNV-1a of the GUID, given a recipe list of its own when nothing has one yet; without
  `BaseOn` the recipe has no level / comfort / prop / water requirement; `Water`, a pack's `"water"`, gives it its water), `Menu` (`"menu"` in a pack: the `"0x…"`
  workshop whose crafting menu lists it, when that is not its own; a station opens the menu of its
  `CraftingInteraction.workshopId` and runs the recipes of its `Crafting.workshopId`. At a `"workshop:<guid>"`, a
  player's game gives that workshop a menu of its own instead, listing only its recipes, under the tabs and subcategories
  of this menu where they would go), `MenuGroup` (`"menuGroup"`, since 0.2.0-dev.30.5, mod API 0.2.14: a tab of the game's crafting menu that
  lists it, a `CraftingGroups` constant, `"0x<workshop>/<tab GUID>[/<subcategory GUID>]"`; its workshop must be the menu the recipe shows in;
  a menu without that tab lists it where the rules below put it, with a warning on the definition), `TrackCrafted` (`"trackCrafted"`, since
  0.2.0-dev.30.5: keep the game's "crafted once" completion query, on the recipe's own id, instead of clearing it; the player knowledge object of
  that id comes from a Kiln.Blocks `KnowledgeBlock`), `StationItem` (`"stationItem"`, with a `"workshop:<guid>"` only: the item that
  places its station; a player's game names the workshop after it, as the title over the station's recipe list),
  `Inputs` (1..32: item names, including new items, or
  `RecipeIngredient.OfCategory(id, n)`), `Outputs` (1..32 items), `Duration`, `DisplayName` (a recipe name; most
  recipes show their result's), `Fields` (other `RecipeInfo` paths), `Unlock`:
  - `Always` (default): no knowledge requirement. The game's crafting UI lists such a recipe once the player knows one
    of its ingredients (its rule for recipes without a requirement). A new item counts as known (it has no item
    knowledge record, and the game treats an item without one as known: docs/re/content.md 8.4), so recipes made only
    of new items are listed too (NewItems' Frost Mail: 4 Frost Shards). The "crafted once" query is cleared too.
  - `LikeBase`: the base recipe's requirement (EMF saw a copied requirement keep a recipe hidden: some unlocks are
    tied to the original recipe).
  The server never checks knowledge (EMF), so unlocking is only about what the client lists.
- **A recipe's water in code** (`RecipeDefinition.Water`, `ContentEntry.CraftingWater`; since 0.2.0-dev.30.3, mod API
  0.2.13; thanks netjunk). Typed forms of `Fields["waterCharges"]` / `Set("waterCharges", n)`, the game's own recipe
  water: **charges per craft** (one charge is 64 of the game's water units), what the recipe screen shows as its
  "Water" row ("Water 92 / 2": 92 charges at hand, 2 a craft). The game takes it when a craft starts, after the
  ingredients:
  - at one of the **six stations with a tank**, from the station's own tank, which the player fills with a watering can
    or the station's Fill: the Seedbed (150 charges), the Oven, the Cooking Station, the Laboratory and the Coffee Brewer
    (100 each) and the Beehive (50). A recipe that asks more than its station's tank holds never starts;
  - in a **crafting menu** (`"hand"`, the Workbench, the Kitchen, an NPC's crafting), from **the player and the base**:
    the crafter's water containers, then the base's wells and barrels;
  - a factory station **without** a tank never starts a recipe that asks water.

  `Water` is a whole number 0..1023 (`ArgumentException` otherwise); null keeps the template's (the `BaseOn` recipe's,
  none with only a `Station`), 0 clears a base's. It travels as the definition's `"water"`, as a data pack's recipe gives it
  (applied after a station-only recipe's requirements are cleared); `Fields["waterCharges"]` keeps working as before;
  give one or the other (both: `ArgumentException`, since `"fields"` are applied after `"water"` and would win). `CraftingWater` reads and sets a game recipe's (0..1023,
  `ArgumentOutOfRangeException` otherwise; `Reset("waterCharges")` puts the game's back). Not the game's old "Water"
  canteen item (`Food_T1_raw_hydration_Water_Pouch_Filled_DEPRECATED`): as an ingredient a craft takes that item, never
  tank water.
  ```csharp
  Content.Recipes.Register(new RecipeDefinition
  {
      Guid = Guid.Parse("1f0c8a52-3b7e-4d21-9a6c-2e5d8b4f7a10"), Name = "MyMod_Herb_Tea", Station = "0x6b8247d6",
      Inputs = [("Food_T7_raw_herb_Thyme", 2)], Outputs = [("Food_T7_dish_hydration_ThymeTea", 1)],
      Water = 5,                                                   // 5 charges from the station's tank each craft
  });
  Content.OnReady(_ => Content.Recipes.Get("Food_T3_Meat_Chicken_Soup")!.CraftingWater = 3);
  ```

### Rarity, level and perks of new gear
**A new item is what its recipe tooltip shows.** The game does not guarantee that by itself: the tooltip reads `rarity` and
`itemLevelRange.minLevel`, while a craft (and `Items.Give` without `Gear`) passes no level or rarity, so the game takes the
rarity from `generateRarity` (on: the lowest allowed, Common) and rolls the level in the range. Loot weapons, the usual base of a
new sword, have `generateRarity` on and levels 1..55, because their loot roll passes both. A clone would inherit that: Epic in
the menu, Common at a random level in the bag. So a new item with an item entity (`HasPIDE`) takes the form of the game's
crafted gear: **`generateRarity` off and one level** (evidence: docs/re/items.md section 10).

| Field | What it does on a new item | If you do not set it |
|---|---|---|
| `rarity` | the rarity, in the tooltip and on the item | the base's (a loot weapon's tier: often Legendary) |
| `itemLevelRange.minLevel` | the level, in the tooltip and on the item | the base's lowest (a loot weapon: 1) |
| `perkReferences[i]` (`perks`) | rarity + 1 perks, slots 0..rarity: fill them. An item's rarity is its perk count - 1, so with slot [rarity] empty an Epic comes out Rare (listed in `warnings`) | the base's |
| `rolledLoot` / `RolledLoot` | **the opt-in for a rolled loot item**: keeps the base's `generateRarity` and level range | false |
| `generateRarity`, `itemLevelRange.maxLevel` | each opts in to that one behaviour | off, `minLevel` |

- So you need no field for a consistent item; `rarity` and `itemLevelRange.minLevel` choose what it is. A tooltip's `LEVEL 1/5` is
  level 1 with rarity + 1 = 4 enhancements (Epic), at levels 2 to 5. A player can unlock those for a cost; each adds a level.
- **`Gear(level)` is clamped to the range**, so it cannot change a fixed item's level, as with every crafted item of the game.
  `Gear(rarity)` is always honoured. Random loot and drop tables pass a level and clamp it the same way, so a loot-only sword
  that should scale with its area needs `RolledLoot`. To offer several tiers, make one item per tier, as the game does.
- **`RolledLoot` with a recipe** is honoured but its tooltip then differs from what is made: listed in the definition's `warnings`
  (`NewContent.Definitions()`, kiln.log) and by `kiln lint` (KL406). Without `Gear`, such an item is the lowest allowed
  rarity at a random level.
- **Items already made**: rarity and perks stay as they were made (they are on the item entity), but when the save loads the game
  brings the level into the item's current range, keeping unlocked levels on top. So a Common Glacier saved at level 49 reads
  Common, level 1 once the item has one level (lab: `mods/tests/craftmatch-test`, phase 2). **Narrowing a released item's range
  lowers the level of the copies players already have.** Only new crafts get the new rarity.
- A recipe that makes a **game** loot weapon has the game's behaviour (Common, a rolled level; KL406 says so); kiln.content does not
  change game items on its own. A rule can: `"Weapon_X": {"generateRarity": false, "itemLevelRange.maxLevel": 1}`.
- Client and server build the same clone from the same definition (the mod on both sides, or `clients: "kiln"`: the sync sends the
  definition, `rolledLoot` included), so the client's tooltip reads the values the server makes the item from.

Lab test (no player; the game's recipe function through `Items.GiveRecipeResult`, then each item entity read back):
```sh
export KILN_OUT=<build dir>
dotnet build mods/tests/craftmatch-test/CraftMatchTest.csproj -c Release -o $KILN_OUT/mods/craftmatch-test --artifacts-path $KILN_OUT/dotnet
tools/lab/lab.sh new craft-match 26430 --save <savegame dir>
tools/deploy-server.sh ~/srv/enshrouded/labs/craft-match --policy any --samples
tools/lab/lab.sh start craft-match    # fresh: 65 PASS with NewItems + ModelSample deployed; MADE and TOOLTIP lines per item
tools/lab/lab.sh stop craft-match; tools/lab/lab.sh start craft-match   # phase 2: 6 PASS: instances saved under the old rules
```
Delete `kiln/data/craftmatch-test/` for a fresh run.

### Data packs: "add"
The same definitions in `content/*.json`, as `"add"` lists in the `items` / `recipes` sections (fields as in the C#
form: `guid`, `name`, `base`, `displayName`, `description`, `lore`, `fields`, `rolledLoot`; recipes `station`, `inputs`,
`outputs`, `duration`, `water`, `unlock`). Rules after the list (in the same pack, or later packs) can change the new entries
like any game entry:
```json
{ "format": 1,
  "items": {
    "add": [ { "guid": "04e82028-a29d-4948-925d-72741b3c206d", "name": "Kiln_Sample_FrostLantern",
               "base": "Material_T1_Resin", "displayName": "Frost Lantern", "fields": { "maxStackSize": 40 } } ],
    "Kiln_Sample_FrostLantern": { "maxStackSize": 77 } },
  "recipes": {
    "add": [ { "guid": "84d38550-b3df-426d-a42d-5b1259156447", "name": "Kiln_Sample_FrostLantern_Recipe",
               "station": "hand", "inputs": [ { "item": "Kiln_Sample_FrostShard", "count": 1 } ],
               "outputs": [ { "item": "Kiln_Sample_FrostLantern", "count": 1 } ] } ] } }
```

**A recipe's water** (`"water"`, 0.2.0-dev.30.3): the game's own recipe water, `RecipeInfo.waterCharges`, in charges a craft
(1 charge = 64 water units; the recipe screen shows it as its "Water" row, e.g. "Water 92 / 2"). The game takes it when a
craft starts: a station with a tank (Seedbed 150 charges, Oven, Cooking Station, Laboratory, Coffee Brewer 100, Beehive 50)
from its own tank, filled with a watering can or the station's Fill; a crafting menu (`"hand"`, the Workbench, the
Kitchen, the NPCs) from the crafter's water containers and the base's wells and barrels. A whole number 0..1023; without
it a recipe has its base's water, or none with only a `station`; `0` clears a base's. A change to a game recipe sets
`"waterCharges"`. kiln lint KL408 warns about water at a factory station without a tank (it never starts the recipe) or
above its tank. **Not the "Water" item**: `Food_T1_raw_hydration_Water_Pouch_Filled_DEPRECATED` is the game's old canteen;
as an input a craft takes that item, never tank water (KL408 says so). Evidence: docs/re/stations.md section 3.6.
```json
{ "format": 1, "recipes": { "add": [ {
    "guid": "1f0c8a52-3b7e-4d21-9a6c-2e5d8b4f7a10", "name": "MyMod_Herb_Tea", "station": "0x6b8247d6", "water": 5,
    "inputs": [ { "item": "Food_T7_raw_herb_Thyme", "count": 2 } ], "outputs": [ { "item": "Food_T7_dish_hydration_ThymeTea", "count": 1 } ] } ],
  "Food_T3_Meat_Chicken_Soup": { "waterCharges": 3 } } }
```
A pack's definitions belong to the pack (they follow its mod's load state like the pack's rules).

### C
```c
char rep[512];
const char* def = "{\"guid\":\"…\",\"name\":\"My_Item\",\"base\":\"Material_T1_Resin\",\"displayName\":\"My Item\"}";
if (c->header.version >= 3 &&
    c->define(owner, KILN_CONTENT_ITEMS, (KilnStr){def, strlen(def)}, rep, sizeof rep, NULL) == KILN_OK) { … }
c->definitions(buf, cap, &len);   /* JSON array: kind, guid, id, name, base, owner, source, state, worlds, … */
```
`define` / `definitions` work from any thread.

### Boot generations (since 0.2.0-dev.28)
With a boot mod on a dedicated server ([../modding/onboot.md](../modding/onboot.md)) kiln.content runs two of kiln.boot's
collectors; both prepare before the game's entry point:
- **`content`** takes the documents Kiln.Blocks lowered from the boot mods' staged definitions: `define` (a new item
  or recipe, the JSON of `NewContent`) and `clone` (a template, sequence, perk or buff clone). Each is defined under
  the mod's process owner (`boot:<mod>`) with the GUIDs a runtime `Define` gives, and reaches the game as any
  definition does (below). A definition it refuses leaves the whole mod out; if the
  generation is not published, every definition of it is taken out again. kiln.content reads no definition files of
  its own at boot, so this collector has nothing to prepare without such documents; data packs and pack documents are
  applied at world load as before.
- **`patches`**, the last collector, checks the boot mods' resource patches (from `blocks/*.json` and from `OnBoot`)
  and writes them when the game's resource service comes up ([../modding/resource-patches.md](../modding/resource-patches.md)).

### Lifecycle
- **Registration time**: a definition made before the world loads waits; when a world's item and recipe registries
  have loaded (0.3 s after the server world's first step, ~11 s before the save is read) the service inserts every
  definition into them, before the data packs, so `OnReady` handlers, packs and the save see them. Lab, restart of a
  copied world: "added to the server world's registry" at 02:57:25.6, the save was read ~12 s after start, and the
  given sword and ingots were still there ("Removed invalid Item" never appeared). Defined after the world loaded (hot
  load, `OnReady`), it is in at once on the active world's thread, else at the next tick.
- **Every world**: the service keeps the definitions for the life of the process and inserts them into each world that
  starts: on a client the hosted server world **and** the client's own world (each world has its own registries; the
  item data is shared), again in every later hosted or joined session. See "Client worlds" for when.
- **Deterministic ids**: `id = FNV-1a(GUID)`. Two definitions with the same id, or a definition whose id or debug name
  is a game item's, are refused for that world (logged, `State` `error`).
- **Mod unload** (Forge's missing mappings): the items stay registered until the process ends, so stacks of them in
  inventories and chests stay valid and are never cut; the recipes leave their station's list (no longer listed or
  craftable; their ids still resolve). Lab: `unload newcontent-test` → "item Kiln_Test_Emberblade stays registered
  until the server restarts (saved stacks are kept)", "recipe … taken off its station". **Reload**: the same GUIDs
  registered again are adopted (`State` `adopted`) and the recipes listed again. A pack's definitions follow the pack.
- **Redefinition** (same GUID, other values, e.g. a reload with a changed mod): a new clone is built and the
  registries are re-pointed at it; the old memory stays valid. Field layers other mods had on the old clone are lost
  until their next application.
- **Restart without the mod**: the item is unknown again and the game drops its stacks while it loads the save
  (vanilla behaviour; lab: a sword and 7 ingots given before the restart were gone right after the world loaded, no
  game log line). That is also why the definitions go in before the save is read. Never change a released item's
  GUID.

### Client worlds: before the first step
A client world's frame steps the prediction world first, and the prediction world reads the client world's registries, so
registering in the client world's Pre is too late: `apply_armor` faulted on a held item with perks (docs/re/content.md section 9).
- **When**: the game's RegisterSingleton names a new world's item registry; when ItemRegistryUpdate finishes loading it (the loading
  screen), items and recipes go in, on the thread that loads it. Every client world this way, hosted or joined, first or later; a world
  at a reused address starts clean. A world without its item registry loaded at its first step retries from `KILN_WORLD_STARTED` and its Pre.
- **kiln.log**, per world:
  `new content: client world 0x...: definitions registered BEFORE its first step, when its item registry finished loading (N ms after
  the world was set up): 7 item(s), 7 recipe(s)`, then `new content: prediction world 0x... first step (session 3): the client world's
  registry got the definitions N ms before it: ...` and the same for the client world. A server world logs `definitions registered N ms
  after its first step` (it loads its registries while it steps, ~11 s before the save). A `NOT in the client world's registry yet` or
  `AFTER its first step` line on a client means the early hook did not run.
- **Unknown ids**: a miss in a client world's registry (a mod this game lacks, or a server's definitions that have not arrived) answers a
  copy of the internal item `Kiln_Unknown_Item` carrying that id: "[Unknown item]", a Bronze Sword's record without perks, its icon, no stack
  limit. The game cannot fault on it, the server still owns the stack, and a definition that arrives later wins because the registry is asked
  first. kiln.log: `the game asked the client world's registry for item 0x...`, once per id. Server worlds keep the game's behaviour.
  The game's own items outside its registry are not unknown ids: its data has keen::ItemInfo resources the registry doesn't list (55 on
  client 26430625, test and deprecated items; the catalog marks them `"registered": false`), and the game asks for some of them
  (`Armor_Tint_TEST`, 0x4ef84410). Those get NULL, as without Kiln. Kiln lists the game's ItemInfo resources once (kiln.assets, without
  the ones a mod serves) and checks it when it turns the answers on: `new content: 55 of the game's 4268 keen::ItemInfo resources are not
  in this registry; they stay NULL as in the game (self-check with 0x...: ok)`.
  `Kiln_Unknown_Item` is in no definition list and not in the content sync; it does appear in `Content.Items` of a joined client.
- **The lookup hook, on first need** (dev.29, GitHub issue 39): those answers need a hook on the game's ItemRegistryFind, one of its
  hottest functions (478 call sites, job threads) and one that other loaders' mods hook too (Quick Deposit). Kiln defines
  `Kiln_Unknown_Item` at boot but hooks the lookup only when a client world's item registry finishes loading (the loading screen, where the
  install's short stop of the other threads is not seen), and only if this game joined someone else's server (no server world in the
  process) or mods here define items. A single-player or hosted world without item mods never has the hook; its unknown ids stay NULL, as
  without Kiln. Once in, the hook stays until the game closes. The install follows the gameplay hooks' rule (no live world of any kind):
  - **No world runs** (a join's loading screen: no server world, the client world still loading, the prediction world only steps inside
    the client world's frame): the plain installer, as dev.28 installed this hook at boot. The checked one refuses on PCs where Kiln cannot
    open every thread, and the join is where the placeholder is needed.
  - **A world runs** (a host: the server world's job threads call the lookup; or a join whose client world already steps because the
    early registration is off): the checked installer. kiln.hooks' `mid2_checked` places the detour only when every other thread could be
    stopped and none is inside the bytes it replaces; the raw link then joins that target without writing (the pattern of
    `quests/roster.cpp`). Refused: no hook and no placeholder in this world, and the next world's registry load tries again.

  kiln.log, once per client world: `new content: client world: the game's item lookup is hooked now (this game joined a server, which may
  send items of mods this game does not have: they show as [Unknown item]; no world runs yet)`, `... is hooked now (mods here define 3
  item(s): one this world's registry does not have yet shows as [Unknown item]; checked install: a world runs)`, `... is not hooked (this
  game hosts its world and no mod defines items, so no unknown item id can reach it)`, `... is hooked (since an earlier world of this
  game)`, or (warning) `the [Unknown item] placeholder is off for this world: the game's item lookup could not be hooked because the
  install could not be checked while a world runs (...); the next world's registry load tries again`. An item a mod defines while a
  client world without the hook runs: `new content: client world: an item was defined while this world runs and the game's item lookup
  is not hooked: its [Unknown item] placeholder starts with the next world`. The boot line says `unknown item ids answered by a
  placeholder when needed`.
- **Definitions made later** cannot be there earlier: data packs' `add` lists and `OnReady` handlers run when kiln.content's registries are ready
  (hosted: long before the client world; a first joined world: ~300 ms after its first step; later worlds keep them from the first). In that
  window such an item answers "[Unknown item]", then its definition takes over.
- **Perks and buffs** keep their later registration (hosted: the client world's step 11 mod 25; joined: registries ready, ~300 ms after the first
  step). Every game lookup of both tests for a miss (docs/re/content.md section 9), so a Kiln perk on an item is skipped until then. Skill trees are
  served as the game's resource to each new world before it steps (`skills: the new world's skill tree loaded`).
- Switches, in `<kiln>/data/content/`: `no-early-registration`, `no-unknown-items` turn the parts off (`no-unknown-items`: no placeholder
  and never a hook on the lookup); `early-test` runs the same path on a server world, hooks the lookup at boot as before dev.29 and
  answers unknown ids there too (lab check only).

### Client side (the mod is `side: both`)
The client needs the definitions to show and craft new content: install the same mod on the server and the clients.
With `content.loca`, `content.ui_texture`, `content.recipe_ui`, `content.recipe_ui_refresh` and `content.recipe_menu`
(client build 26404966, `client.d/content.json`):
- **Names, descriptions, lore**: the item's name / description / lore tags are new tags derived from its GUID; the
  service answers them in both of the game's loca lookups (the UI first fetches the tag's record, then its text; all
  languages show the one text).
- **Icon**: the UI's icon texture cache is keyed by the item's own GUID; lookups of a new item's GUID use its base
  item's, or the `icon` item's (without this the game stops with "corrupted game ui texture"). A PNG icon
  (`"icon": "<file>.png"`) is registered with kiln.assets; while kiln.assets reports it SERVED the key passes
  unchanged (seen: "the UI texture cache refilled; 2 icon(s) now served", no crash). The 3D model
  comes from the base's references or `model` (kitbash).
- **Names per language**: the hooks read the LocaPool's language (`content.loca_language`, log "loca on (per
  language)").
- **Crafting UI**: the client's unlocked-recipe lists (`unlocked_recipes_ui`) iterate the recipe resource's array, not
  the registry maps; the service's getter hook returns the array plus the new recipes, and resets the UI's cached
  knowledge version so the lists are rebuilt after recipes are added or removed.
- **Crafting menu**: the menu shows only recipes named in its UI configuration (per workshop: categories →
  subcategories → recipe ids). A recipe whose definition names a `menuGroup` goes into that tab's list (the tab's
  `groupId` and the list's `recipeSetId`, read with the game's reflection; since 0.2.0-dev.30.5). Otherwise the service adds each new recipe to the subcategory that holds its base recipe; else
  the one that lists the game's recipe for the item its result was cloned from; else, for a result placed with the
  build cursor (a prop, a station, furniture), the first subcategory in the menu's order that lists placed things of
  the same item category; else the one with the most recipes making items of the same item category; else the
  workshop's last one (the copied list replaces the subcategory's; the original comes back when the recipe goes).
  Building tools are blocks and placed things alike, so by category alone a new station at the workbench went to
  "Blocks - Stone"; now it goes with the workbench's chests, and a new table beside the game's tables. In a client: the three NewItems
  recipes appeared under the hand menu's "Supplies — Utility".
- **Verified in a client** (build 26404966): hosting a world with
  NewItems, the Frost Shard and Frost Edge show their names, descriptions, the base items' icons and models, Rare and
  the damage; the hand menu lists the three recipes; crafting Frost Shard (2 twigs) and Frost Edge (3 shards + 2
  twigs) took the inputs and gave the items; the items stayed in the character across game restarts. Joined to the
  lab dedicated server (REMOTE): the lab gave the starter kit, the items brought from the hosted world kept their
  ids, the recipes were listed and crafting a Frost Shard on the dedicated server took 2 twigs and gave the shard.
- Debugging on a client: with `<kiln>/data/content/debug-ui` the service logs every ~10 s how many loca lookups it
  answered and the crafting UI's hand list; with `loca-trace` as well, every distinct tag id the game looks up.
- An **unmodded client** on a modded server shows "[Invalid Item Stack]" for the new items and cannot see the recipes
  (EMF §3), and can fault on an equipped one with perks (read in the code, not run: docs/re/content.md section 9); crafting a new recipe
  needs the client's own registry entry.

### Lab test
```sh
export KILN_OUT=<build dir>
dotnet build mods/tests/newcontent-test/NewContentTest.csproj -c Release -o $KILN_OUT/mods/newcontent-test --artifacts-path $KILN_OUT/dotnet
tools/lab/lab.sh new newcontent 25910 --save <savegame dir>
tools/deploy-server.sh ~/srv/enshrouded/labs/newcontent --policy any --samples
tools/lab/lab.sh start newcontent      # fresh: 24 PASS (register, refusals, registries, pack "add", catalog, give, save)
echo "reload newcontent-test" > ~/srv/enshrouded/labs/newcontent/kiln/control/r.cmd   # adopted, listed again
tools/lab/lab.sh stop newcontent; tools/lab/lab.sh start newcontent   # 24 PASS: the sword and ingots survived; cleaned up
# optional, after a fresh run: restart as if the mod were gone
touch ~/srv/enshrouded/labs/newcontent/kiln/data/newcontent-test/without-definitions   # then stop + start
#   PASS without the definitions: the placeholders kept the saved stacks (the game alone drops them)
```

### Definitions export (for the launcher)
Kiln.Content writes every definition (`NewContent.DefinitionsJson()`: items and recipes of every mod and data pack,
`@definitions` synced from a server, placeholders) to `kiln/state/content-definitions.json` whenever the list changes
(`DefinitionsExport.cs`: checked every 25 ticks from the content service tick, atomic tmp + rename,
`{"format": 1, "written": "<RFC 3339>", "definitions": [ … ]}`). The Kiln Launcher's save editor names and draws mod items
with it (docs/kiln/launcher/gameart.md). Lab (Kiln.Content with the export): 28 definitions written;
newcontent-test 24 PASS / 0 FAIL, kitbash-test 24 PASS / 0 FAIL.

## Kitbash: new items made of loaded resources (v4)
A new item is a clone of its `base` (behaviour, flags, category, stats). Its references can point at **anything the
game already loaded** of the right type: another weapon's model, another armour set's worn model, another item's
icon, perks, weapon category, impact entity, program. Kiln never creates a resource here (new models and textures
are kiln.assets': a GUID that kiln.assets serves counts as loaded too). Evidence: docs/re/content.md
section 8.

```json
{ "guid": "7d3e9b52-1c84-4f0a-a6d7-5e2b8c9f1a03", "name": "Kiln_Sample_Glacier", "base": "Weapon_T1_1H_SwordEpic_01",
  "model": "item:Weapon_T4_1H_SwordEpic_02",                 // another sword's model and model setup
  "icon": "icons/glacier.png",                               // or "item:<name>" / an item GUID
  "perks": ["item:Weapon_T5_1H_SwordEpic_01", null, "none"], // slot 0 from a T5 sword, slot 1 kept, slot 2 cleared
  "weaponType": "item:Weapon_T1_1H_Axe_01",                  // the weapon category (skills, bonuses)
  "refs": { "equipment.impactEntity": "item:Weapon_T1_1H_Axe_01" },
  "fields": { "damageSetup.dmgMod": 1.6, "rarity": "Epic" } }
```
| key | what it sets | values |
|---|---|---|
| `model` | `item:<name>`: the whole visual setup of that item: `equipment.visualEntity` (weapons: the template the server spawns in the hand), `equipment.visualModel` and the per-body list `equipment.visualModels` (armour: the worn model), `equipment.hideMask` / `blendShapeMask`, `equipment.equipmentEntityModelSetup.*`, `iconModel` and `overridePickupModels` (the item lying on the ground), `collectionInfo.*` (the collection shelf). A model GUID: `equipmentEntityModelSetup.model` (weapons) or `visualModel` (armour, `isSet` false) | `item:<name>`, GUID |
| `icon` | `item:<name>`: that item's icon (a prebaked UI texture the UI looks up under the item's GUID: aliased) and its `iconImage`, `iconScene`, `iconRender*`. A PNG file: registered with kiln.assets under the new item's GUID (Kiln clients draw it once kiln.assets serves it; until then, and on a server, the base's icon) | `item:<name>`, item GUID, `<file>.png` (relative: the mod's folder) |
| `perks` | `perkReferences[i]` and `perkIds[i]` (the id the game data pairs with that perk GUID; all 6124 perk slots of the game data have id = FNV-1a(GUID)) | list of up to 5: perk GUID, `item:<name>` (that item's perk in the same slot), `item:<name>.perkReferences[k]`, `none`, null (keep); or `"item:<name>"`: all five |
| `weaponType` | `weaponCategoryReference` | `item:<name>`, GUID |
| `visual`, `impact`, `program`, `animation`, `collectionModel`, `iconModel` | `equipment.visualEntity`, `equipment.impactEntity`, `equipment.program`, `equipment.equipmentEntityModelSetup.animation`, `collectionInfo.overrideModel`, `iconModel` | `item:<name>`, GUID, `none` |
| `refs` | any reference field by path (`Content.Items.Fields` where `IsReference`; not `objectId`, not the text tags) | `{ "<path>": "item:<name>" \| "item:<name>.<path>" \| GUID \| "none" }` |

- **Validation**: a GUID is accepted for a field when a loaded item uses it in a field of the same reflected
  reference type (27 reference types, pools built from every ItemInfo of the world), when the engine's template list
  holds it (template references, through kiln.spawn), or when kiln.assets serves a resource of that type under it
  (`keen::Model` references: `keen::RenderModel`; templates: `keen::ecs::TemplateResource`; an item's `program`
  (`equipment.program`, an `ImpactProgramReference`): `keen::ImpactProgram`, since the gem wave: before it a served program was
  refused there, the base's stayed), or, for a template, when it
  is a template clone defined in this game (a world registers its clones at its load, after its items and before anything
  can place one, so a placeable item names its own template from the start). `item:<name>.<path>`
  must name a field of the same type. Anything else is skipped with a warning (the base's value stays; the item still
  registers): `NewContent.Definitions()` / `definitions()` list them as `warnings`, kiln.log has one line each (a
  placeholder's, whose references name its missing mod's resources, are one Info line per item instead). A
  reference that was not loaded is tried again every ~2 s (kiln.assets may register it later), and at once when a
  template clone goes into a world (a joined client gets its items before the templates in the same content state).
  Each world tries again for the clones in its own registry, on its own thread; in a game that hosts its world, a clone
  the server world has (usually the one clone both worlds share) is written in the server world's Pre only, never from
  the client world's step, as long as that server world may still read it: a client world set up after it stopped, or
  a newer server world, takes the clone over (without the world-stop hook only a newer server world).
- **Game items**: the same keys work as layers on registered items: data-pack rules (`"Weapon_X": {"@refs": true,
  "visual": "item:Weapon_Y"}`, or the document option `"options": {"refs": true}`), C# `entry.SetRef("visual",
  "item:Weapon_Y", allowGameItem: true)` / `SetPerks([...])` / `SetRefJson(key, json)`, C `set_ref(owner, kind, id,
  key, value_json, KILN_CONTENT_REF_ALLOW_GAME, ...)`. New items need no opt-in; a game item without it is refused
  ("a reference of a game item; changing it needs the opt-in"). Layers revert like any field (unload, `Reset`,
  `Revert`); `Get(path)` / `GetRef(path)` read the GUID, `Original(path)` the game's. Icons are definition-only.
- **When a change shows** (docs/re/content.md 8.3): worn models, perk ids and the weapon category are read live
  (next frame / next hit); the held weapon (`visualEntity`) and `impactEntity` / `program` are per equipment slot:
  unequip and equip again; perk slots and `pidEntity` apply to item entities made afterwards; a dropped item keeps the
  model it was dropped with. New items are built with their references before anything uses them.
- **Schema**: reference fields are `IsReference` (C: `KilnContentField.reserved[0] & KILN_CONTENT_FIELD_REF`), still
  `Writable` false (they are not values). `Content.Dump()` now writes them too (GUID text), so a modder can look up
  which item has which model.
- **What the client shows** is the client's own clone (each process resolves its references against its own data):
  install the mod on clients (`"clients": "mod"`) or let the server send the definition (`"clients": "kiln"`).

## Texts per language (v4)
```json
"texts": { "en": { "name": "Glacier", "description": "A blade of old ice." },
           "de": { "name": "Gletscher", "description": "Eine Klinge aus altem Eis." },
           "pt-BR": { "name": "Geleira" } }
```
- Keys: `name`, `caption` (the line under the name), `description`, `lore`, `acquisitionHint`, `primaryAction`,
  `secondaryAction`, `tertiaryAction` (the action hints). Each set text gets its own loca tag (derived from the
  item GUID) in the clone; texts not given stay the base item's, except `acquisitionHint`: where the base is found is not where a new item is, so without a text of its own it has none. `displayName` / `description` / `lore` / `source` (`acquisitionHint`) are the
  language `"*"` (every language). `"description": ""` is no description, rather than the base's.
- The game's language (`keen::LanguageId`: 1 en-us, 2 en-gb, 3 de-de, 5 fr-fr, 16 pt-br, 20 zh-cn, ... 34 uk-ua, from
  the game's reflection; kiln.log "game languages") is the LocaPool's current language (client `+0xd8`,
  `content/loca.language`, feature `content.loca_language`). Per tag and language the hooks answer: the exact key
  (`pt-br`), the primary language (`pt`), `en`, `*`. 0 (`none`, Keen's source strings) uses `en`.
- C#: `ItemDefinition.Texts = new Dictionary<string, ItemTexts> { ["de"] = new() { Name = "...", Description = "..." } }`.
  `NewContent.Definitions()` / `definitions()` list the language keys of each item (`languages`).
- A language change in the game's settings takes effect at the next lookup (the hooks read the language every time).

## The game's own texts
Since 0.2.0-dev.29.18 (mod API 0.2.10). On a game client, `Content.Text` gives the game's own text for a loca tag, in the
language the player's game shows now: a tooltip row's label ("Mana Cost" / "Manakosten"), an item's name, any text the
game has. It is what the game's UI gets: the texts of Kiln's new content (above) answer in the same language too.

```csharp
if (Content.TextAvailable)                                   // false on a dedicated server; Capabilities.Why("content.text")
{
    string? label = Content.Text(0xbe5b2600);                // a loca tag id: "Mana Cost" (English), "Manakosten" (German)
    string? name  = Content.Text("c0ffee00-0000-4000-8000-000000000001");  // a LocaTag GUID: hashed as the game does
    string? lang  = Content.TextLanguage;                    // "en-us", "de-de", ...
}
```

| Member | Thread | What |
|---|---|---|
| `string? Content.Text(uint locaId)` | the game client's main thread | the text of a loca tag id; null when the game has none for it or when this is not available here |
| `string? Content.Text(Guid locaTag)`, `Content.Text(string locaTag)` | same | a LocaTag GUID (the id is FNV-1a-32 over its 16 bytes, `Texts.TagOf`), or a string: a GUID, `0x1234abcd` or a decimal id; null for anything else |
| `bool Content.TextAvailable` | any | the row `content.text` is available: a game client whose game has looked up a text |
| `string? Content.TextLanguage` | any | the language the game shows its texts in now, as the game's key (`en-us`, `de-de`, `pt-br`); null while not available |
| `const string Content.TextCapability` | | `"content.text"` |

- **Client only.** A dedicated server has no texts and no text lookup (`LocaPool_lookup` is client code,
  docs/re/content.md section 7): there `Text` returns null and the row says `wrong-side`. `Texts.Get` is the
  server-side alternative: English only, from a Kiln catalog.
- **The row** `content.text` (kiln.content reports it before the mods load): pending until the game looks up its first
  text (its title screen does, right after boot), then available for the process. Unavailable: `wrong-side` on a server,
  `symbol-missing` on a game build without the lookup, `faulted` when the hook could not go in, `service-older` with a
  kiln.content from before 0.2.0-dev.29.18. `Text` never throws for any of these: it returns null.
- **Thread: the game client's main thread only**: the thread that runs the game's UI frame and steps the client world
  (a `Ui.Screens` UI tick, `Client.Tick`, `Client.Post`, a Kiln menu entry's callback). The game changes its text
  language on that thread and reloads the language's table there; another thread could read the table while it is
  replaced. On another thread `Text` throws `InvalidOperationException`: the overlay's render thread (an ImGui window's
  draw callback) and the server tick of a hosted game are other threads. A window drawn in the overlay looks its texts
  up in a UI tick and keeps them.
- **Copied, not cached.** The text is a .NET string (a copy, never game memory), with the game's markup (`<b>`, ...;
  `Texts.GetPlain` shows how to strip it). Kiln caches nothing: every call reads the game's language and asks the
  game, so a language change in the game's settings applies at the next call. Right after a change the game may answer
  from English until its new table has loaded, as its own UI does. Calls are cheap (a binary search and a copy), but
  for a window drawn every frame keep the texts and look them up again when `Content.TextLanguage` changes.
- C: `text(loca_id, buffer, cap, out_len)` and `text_info(KilnContentTextInfo*)` in `kiln_content.h` (check
  `header.size`); `text` answers `KILN_ERR_WRONG_THREAD` off the main thread and `KILN_ERR_UNAVAILABLE` where the row is not
  available.

### Tooltip rows with their labels
`Content.TooltipValues(ItemId)` gives the rows of an item's tooltip (its `uiValues`: the numbers a spell or a tool
shows) as `TooltipValue(LocaId, Label, Raw, Value, Display, Format, IsSigned, IsScaled)`; `GameDataEntry.TooltipValues(list
= "uiValues")` does the same for a perk, a buff or a skill node (`Content.Perks.Get(...)`), or an item's `uiBuffValues`.

| `Format` | `Raw` | `Value` (what a player reads) | `Display` |
|---|---|---|---|
| `Normal` | 53 | 53 | `53` |
| `Percentage` | 5 | 5 (percent) | `5%` (`+5%` when `IsSigned`) |
| `Duration` | 2500 (ms) | 2.5 (seconds) | `2.5s` |
| `DurationMilliseconds` | 250 | 250 (ms) | `250ms` (not seen in a game tooltip yet) |
| `Float` | 260 | 2.6 | `2.6` |
| `Numberless` | | 0 | `` (the label alone) |
| `Unknown` (a format a later game adds) | raw | raw | raw |

- **Per item type, not per item.** The rows are the game's data for every item of that type; a stack's level or rarity,
  the player's attributes and scaling are not applied. `IsScaled` says the game scales that number when it shows it
  (by the weapon's power or an attribute: `scaleData`); `Raw` and `Value` are the base. Data-pack changes are in (the
  rows follow their configs, see kiln.gamedata "Tooltips").
- **Damage and its element are not rows**: the game computes them from `damageSetup` (read it with
  `Content.ItemData.Get(...).Get("damageSetup.dmgMod")` and the element fields).
- `Display` is Kiln's rendering in the invariant culture; the game's own (decimal separator, spacing) may differ: format
  `Value` yourself to match it.
- **Thread**: kiln.content's world thread, as every `GameDataEntry` read (`[TickThreadOnly]`). A game client joined to a
  server runs its tick on the main thread, so the labels come with the rows. In a hosted game the tick is the server's
  thread: the rows come with `Label` null; look the labels up on the main thread with `Content.Text(row.LocaId)`. Not
  cached either: keep the result.

```csharp
// A spellbook's tooltip: Fire Bolt I's rows with their labels, in the player's language.
// A client joined to a server: in a tick (main thread). Hosting: rows on the tick, labels via Client.Post.
var item = Content.Items.Get("Ammo_T0_Spell_Fire_Bolt_lvl3")?.Id ?? 0;   // Fire Bolt I, or any ItemId from a bag
var rows = Content.TooltipValues(new ItemId(item));
Client.Post(() =>
{
    foreach (var r in rows)
        Log.Info($"{r.Label ?? Content.Text(r.LocaId) ?? $"0x{r.LocaId:x8}"}: {r.Display}");  // "Charge Time: 2.5s"
});
```

### Real-client checklist (not run in a real game yet)
1. Start the game in English with a client-side test mod; in the main menu `Content.TextAvailable` is true and
   `Content.TextLanguage` is `en-us`; `kiln/state/capabilities.json` says `content.text` is available.
2. In a world, `Content.TooltipValues` of Fire Bolt I (`Ammo_T0_Spell_Fire_Bolt_lvl3`) and Fireball I (`Ammo_T1_Spell_Fire_Ball_lvl10`) gives the rows the game's own tooltip shows, with
   the same labels and numbers (mana cost, charge time, ...).
3. Switch the game's language to German in the settings without restarting: the next `Content.Text` of the same ids
   gives the German labels ("Manakosten"); `Content.TextLanguage` is `de-de`; the game's own tooltip agrees.
4. A Kiln item (NewItems' Frost Edge): `Content.Text` of its name tag gives the name the backpack shows, in both
   languages.
5. `Content.Text` from the overlay's draw callback throws the main-thread `InvalidOperationException` (and nothing
   crashes); from a `Ui.Screens` UI tick it answers.

## Game texts in any language (since 0.2.0-dev.31.13)
Mod API 0.2.22. Asked for by XHL (EmberCodex, Easy HUD, Planting Assistant, Creative Mode Fix). `Content.Text` above asks the
game's own lookup in the player's language, on the main thread. These read the language tables the game ships, on any
thread, in any of its languages: a codex page in the language a player picks, every text of a language for a search, an
item's description next to its name.

| Member | What |
|---|---|
| `IReadOnlyList<string> Content.TextLanguages` | the languages the game ships, as it keys them: 15 on client 26430625 (`en-us`, `de-de`, `fr-fr`, `it-it`, `es-es`, `ru-ru`, `pl-pl`, `ja-jp`, `ko-kr`, `pt-br`, `th-th`, `tr-tr`, `zh-cn`, `zh-tw`, `uk-ua`) |
| `GameTextTable? Content.TryGetTexts(string? language)` | one language's texts when they are read, without waiting; null while they are read (the first call starts that) |
| `Task<GameTextTable> Content.LoadTextsAsync(string? language, CancellationToken = default)` | reads them on a worker and completes with the table |
| `string? Content.TextIn(uint locaId, string? language)`, `TextIn(Guid locaTag, string? language)` | one text with the game's markup, without waiting |
| `GameTextTable` | `IReadOnlyDictionary<uint, string>` (tag id to text, with markup): `Language`, `Source` (`Game`, `KilnCatalog`, `None`), `Available`, `Reason`, `Get(uint)`, `Get(Guid)`, `GetPlain(uint)` |
| `Items.Description(item)`, `Items.DescriptionIn(item, language)`, `Items.DisplayNameIn(item, language)`, `Items.TextTags(item)` | an item type's texts ([items.md](items.md#c)); `ItemTextTags(Name, Caption, Description, Lore)` are its tag ids |
| `const string Content.GameTextsCapability` | `"content.game_texts"` |

- **Languages.** `language` is a key the game ships (`"de-de"`); `"de"` (the first shipped `de-*`), `"De_De"` and `"DE-de"`
  work too. Null is the player's game language (`GameLanguage.Current`; `"en-us"` on a server). A language the game does
  not ship gives an unavailable table whose `Reason` lists the ones it has.
- **Where from.** `keen::LocaTagCollectionResource.languages` names one content blob per language; each is a table of
  `keen::LocaTagResource {id, text}` (types from kiln.types, bytes through kiln.assets' `blob_read_resident`, so registered
  blob overrides apply). Layouts checked on client 26430625: the collection's `languages` at `+0x20`, 40-byte language
  rows (`language` `+0`, `data` `+8`), 24-byte text rows (`id` `+0`, `text` `+4`). A row or string outside its blob makes
  the whole table unavailable, never a table with wrong texts. Trailing NULs are dropped; the markup stays (`GetPlain`
  strips it).
- **Not cached by the game, kept by Kiln.** A table is read once (about a quarter of a second for a client language) and
  kept; Kiln keeps the four most recently used languages. A failed read (the game has not opened its content index yet) is
  tried again after 5 s, then twice as long each time, at most 5 minutes; `LoadTextsAsync` asks again at once.
- **Kiln's new content.** A kiln.content item's texts are Kiln's, not in the game's tables: `Content.Text` (the game's
  lookup) answers them, `TextIn` does not; `Items.Description` and `Items.DisplayNameIn(item, language)` take its definition's
  texts (that language, its primary language, `"*"`, English; else its base item's).
- **Dedicated server.** Its data has no texts (no `LocaTagCollectionResource`, none of the client's blobs; server
  26430253, docs/re/world-status.md section 12). Every language other than English is unavailable with the reason
  `a dedicated server's game data has no texts (the game ships them with its client only); ...`. English comes from a Kiln
  catalog's `loca` section (`Source` `KilnCatalog`, the same table as `Texts.Get`) when the server has one
  ([catalogs.md](../modding/catalogs.md)), and `TextLanguages` is `["en-us"]` once it is loaded. `Items.Description` is null
  there; `Items.DisplayNameIn(item, "en-us")` is the English name Kiln ships.
- **The row** `content.game_texts`: available on a game client with kiln.assets' resources (`assets.resources`) and kiln.types;
  `wrong-side` on a dedicated server. The members never throw for a missing table: they say why in `Reason`.

```csharp
// EmberCodex-style: a codex page in the language the player picked in the codex's own settings ("fr-fr"; null: the game's).
static async Task<CodexPage> Page(ItemId item, string? language)
{
    var texts = await Content.LoadTextsAsync(language);              // read once per language, then from memory
    if (!texts.Available)                                            // a server, a language the game does not ship
        return new CodexPage(Items.DisplayName(item) ?? item.ToString(), "", texts.Reason, "");
    if (Items.TextTags(item) is not { } tags)                        // a new item (kiln.content): its definition's texts
        return new CodexPage(Items.DisplayNameIn(item, language) ?? item.ToString(), "", Items.DescriptionIn(item, language) ?? "", "");
    return new CodexPage(
        texts.GetPlain(tags.Name) ?? Items.DisplayNameIn(item, language) ?? item.ToString(),
        texts.GetPlain(tags.Caption) ?? "",
        texts.Get(tags.Description) ?? "",                           // with the game's markup, for a rich-text widget
        texts.GetPlain(tags.Lore) ?? "");
}
record CodexPage(string Name, string Caption, string Description, string Lore);

// The codex's language picker: every language the game ships.
IReadOnlyList<string> languages = Content.TextLanguages;            // ["en-us", "de-de", ...]

// Search every text of one language (Creative Mode Fix-style search over names and descriptions).
var german = await Content.LoadTextsAsync("de-de");
var hits = german.Where(t => t.Value.Contains("Kupfer", StringComparison.OrdinalIgnoreCase)).Select(t => t.Key).ToList();

// A label drawn every frame (Easy HUD-style): never waits; null until the table is read, then the text.
string label = Content.TextIn(0xbe5b2600, null) ?? "…";
```

## Voxel materials by cell (since 0.2.0-dev.31.13)
Mod API 0.2.22. Asked for by XHL (Vein Mining, Blueprint, Easy HUD). A voxel cell's low byte is its material: 1..127
terrain (dirt, stone, ores), 128..255 building materials. `Content.TryGetVoxelMaterials` / `LoadVoxelMaterialsAsync` give
every material the game configures, keyed by that byte and independent of its items: what it is, what digging it gives and
at what rate, which items place it, and a label in a chosen language. The byte comes from `Queries.RaycastVoxels`,
`Build.Read`, a `BuildEvent`, or a client's `ClientCursor.hoveredVoxelMaterialId`.

| Member | What |
|---|---|
| `VoxelMaterialCatalog? Content.TryGetVoxelMaterials(string? language = null)` | the catalog when it is read, without waiting; null while it (or that language's texts) is read |
| `Task<VoxelMaterialCatalog> Content.LoadVoxelMaterialsAsync(string? language = null, CancellationToken = default)` | reads them on a worker and completes with the catalog |
| `VoxelMaterialCatalog` | `IReadOnlyList<VoxelMaterial>` in cell order: `Get(cellId)`, `PlacedBy(item)`, `WithLootItem(item)`, `Language`, `Available`, `Reason` |
| `VoxelMaterial` | `CellId`, `Kind` (`Terrain`, `Building`), `Guid`, `Name`, `LootItem`, `ExchangeRate`, `PlacingItems`, `Hardness`, `HealthPoints`, `Label`, `LabelSource` (`None`, `PlacingItem`, `LootItem`) |
| `const string Content.VoxelMaterialsCapability` | `"content.voxel_materials"` |

- **Where from** (types from kiln.types, bytes through kiln.assets: what the game gets, so kiln.worldtables' merged rows are
  in). `keen::TerraformingEfficiencyRegistryResource` (both sides): 116 `terrainConfigs` rows (`terrainMaterial` u8,
  `terrainItem` the loot, `hardness`, `terrainPerLootItemExchangeRate`, `healthPoints`) and 85 `buildingConfigs` rows
  (cell `128 + buildingMaterial`, `materialItem`, `blocksPerLootItemExchangeRate`) on 26430625 / 26430253; the first row of
  an id wins. `keen::VoxelWorldResource.materialGuids[256]` gives `Guid`. Every `keen::ItemInfo`'s
  `equipment.voxelData {isBuildingVoxel, placeVoxelMaterial, placeVoxelMaterialId}` gives `PlacingItems`: a building item
  places 128..255, a terrain item 1..127, and a mapping that breaks that or names another GUID than the world's table is
  left out (unknown, never guessed). Item ids are FNV-1a-32 of the item GUIDs, as the game's.
- **Names and labels.** The game has no text of its own for a voxel material (its `SolidVoxelMaterial` resources do not
  ship). `Name` is Kiln's stable game-material name, the one `WorldTables.GameMaterialId` and world-block files accept, made
  from the debug name of the item that places it (`Build_TerrainMaterial_T1_Stone` → `TerrainStone`,
  `Block_T1_Wood_Rough_CRAFTING` → `WoodRough`); a name two cells would share is nobody's; a material no item places
  (ores, rubble) is `Cell<id>`. `Label` is an item's name in the catalog's language (`Items.DisplayNameIn(item, language)`)
  and `LabelSource` says which: the item that places it, else the loot item. Several materials share a loot item (terrain
  1, 15, 37 and 73 all give the grassland dirt item), so a `LootItem` label is a grouping: keep the cell id to tell them apart.
- `ExchangeRate` is how many cells of the material one `LootItem` is (copper ore, 26: 8); null when the data gives none.
  Unbreakable world materials (5, 16, 34) have no loot item.
- **Read once.** The materials are read once per process (the registry, the world table, every item: a fraction of a
  second, on a worker); a failed read is tried again after 5 s, doubling to 5 minutes. A catalog is immutable; the same one
  comes back while nothing changed, so asking every frame is cheap.
- **Server.** The data is on both sides. Labels need the game's texts (a client); on a dedicated server English labels are
  the names Kiln ships, other languages have none (`LabelSource` `None`).
- **The row** `content.voxel_materials`: available where kiln.assets reads resources (`assets.resources`) and kiln.types is
  here. A catalog that cannot be read says why in `Reason`.
- What the held tool's material pool holds (amounts in the backpack and magic chest) stays `ToolMaterials.TrySnapshot`
  ([tools.md](tools.md)); this catalog describes every material, whatever is held.

```csharp
// Vein Mining-style: which material is this cell, and what does mining it give?
VoxelMaterialCatalog? materials;                                    // keep it: read once

public override void OnLoad() => _ = LoadMaterials();

async Task LoadMaterials()
{
    var c = await Content.LoadVoxelMaterialsAsync();                 // labels in the player's language ("en-us" on a server)
    if (!c.Available) { Log.Warn($"no voxel materials: {c.Reason}"); return; }
    materials = c;
}

// cell: the hit voxel's material byte (Queries.RaycastVoxels, Build.Read, ClientCursor.hoveredVoxelMaterialId)
string Describe(int cell)
{
    if (materials is not { } c || c.Get(cell) is not { } m) return $"cell {cell}: not a game material";
    var name = m.LabelSource == VoxelMaterialLabelSource.LootItem ? $"{m.Label} ({m.Name})" : m.Label ?? m.Name;
    var gives = m.LootItem.Value == 0 ? "nothing (unbreakable)"
        : $"1 {Items.DisplayNameIn(m.LootItem, c.Language) ?? m.LootItem.ToString()} per {m.ExchangeRate} cells";
    return $"{name}: {m.Hardness}, {m.HealthPoints} HP, gives {gives}";
}

// A vein is one cell id: terrain 37 and 1 both give dirt, but only 37 is the hard ground the player struck.
bool SameVein(int struck, int neighbour) => struck == neighbour && materials?.Get(struck) is { LootItem.Value: not 0 };

// Blueprint-style: the material a held building block places, and the item and rate a removed cell gives back.
if (materials?.PlacedBy(heldItem) is { } placed) Log.Info($"{placed.Name}: 1 {Items.DisplayNameIn(placed.LootItem, null)} per {placed.ExchangeRate} cells");
```

## Distribution: random loot, drop tables, vendors, knowledge (v4)
```json
{ "guid": "...", "name": "Kiln_Kitbash_Token", "base": "Material_T1_Metal_Scraps",
  "loot":  { "like": "Material_T1_Metal_Scraps", "stackSizeMin": 2, "stackSizeMax": 4 },
  "drops": [ { "table": "a20e8eae-7833-462e-9ea9-59d1e17a1b98", "countMin": 3, "countMax": 3, "chance": 1 },
             { "table": "like:Material_T1_Metal_Scraps", "countMin": 1, "countMax": 2, "chance": 0.25, "weight": 1 } ] }
```
- **Random loot** (`loot`: `true` = like the base, or `{like, stackSizeMin, stackSizeMax, stackSizeMaxScaled,
  fields}`): the item gets a copy of `like`'s `LootableItemInfo` (rarities, label groups, shufflebag type: it is rolled
  where `like` is rolled, with the same chances), in the loot map (`LootFind`) and in the candidate list every roll
  builds (containers, world pickups, random drop-table stacks: the getter `content/LootItems`, hooked; feature
  `content.loot_new`). `like` must be an item with a random-loot entry. It is a `Content.Loot` entry afterwards (rules
  in the `loot` section work on it). Server world only (the client never rolls). An orphaned definition (its mod
  went away) leaves the rolls; placeholders never drop.
- **Drop tables** (`drops`, a list): `table` = a table GUID or a glob over GUIDs (the stack goes into its root
  group), or `like:<item>` (into every group of every table that drops that item, next to it). `countMin`,
  `countMax` (1..100000), `chance` (spawnProbability 0..1), `weight` (in a selector group). Tables load on demand: the
  stacks are added the first time the game uses a table, before that roll (the DefaultInventoryAcquire hook), and to
  every table seen so far when definitions change. Another thread that uses the same table meanwhile waits until they
  are in. A plain group gets one more child group holding the new stacks
  (each rolls its own chance; the group's own stacks stay where they are, so drop rules keep working on them); a
  selector group (one entry picked by weight) gets its stack list copied with the new stacks appended (at most 64
  entries, the game's limit; drop rules are applied to the copy again). The world's Loot Amount scales the counts as
  for any stack. `Content.Drops` shows them like the table's own stacks.
- **Vendors**: there is no shop system; NPC traders are workshops with barter recipes (EMF re_villages: the fisher
  trader `0xc4acc104`, the Chinese-new-year trader `0x8554479a`). A trade offer is a new recipe with
  `"station": "0xc4acc104"`; the server's craft handler checks no workshop (docs/re/content.md 8.4), so the server side
  works like any recipe; whether the trader's screen lists it is a client check.
- **Knowledge**: nothing to do. A new item has no `ItemKnowledge` record, and an item without one counts as known
  (docs/re/content.md 8.4; the earlier note here was wrong): requirement-less recipes whose ingredients are only new
  items are listed (`Unlock = Always`), and names / descriptions show at once. There is no item codex; collection
  shelves sort by `collectionInfo`, which the clone has (and `model` brings).
- C#: `ItemDefinition.Loot = new ItemLoot { Like = "...", StackMin = 2, StackMax = 4 }`,
  `Drops = [new ItemDrop("<table GUID>|like:<item>") { CountMin = 3, CountMax = 3, Chance = 1, Weight = 1 }]`.
- `info()` / `NewContent.InfoJson()` `loot`: `{on, rolls, extended, items, getter: {count, newAtTheEnd, lootFind}}`
  (rolls since boot that read the candidate list, how many of them got the new items, a self-check of the getter).

## Missing content: placeholders (Forge's missing registry entries)
Every new item a world has seen (registered in the server world) is recorded in that world's **WorldData**
(Kiln.Saves, Kiln's own bundle `kiln.content`, key `missing-content`): GUID, id, owning mod, debug name, the display
name and the definition as kiln.content normalized it. It is saved with every world save, so the record matches the
world file on disk (also after `kill -9`, Kiln.Saves' generations).

- **At world load**, right after the registries are ready (the native ready callback, before mods' `OnReady` and
  ~10 s before the game reads the save), every recorded item that no loaded mod or pack defines gets a placeholder:
  same GUID and id, the recorded base, field values and references (so max stacks, gear entities and looks stay
  valid), display name **`[Missing: <mod>:<item>]`** and a description that names the mod. It is inert: no recipes,
  no loot, drops or knowledge. Saved stacks of it survive.
- kiln.log: `missing content: 3 item(s) of mods that are not loaded are kept as placeholders (their saved stacks
  stay): placeholder-items:Kiln_PH_Sword (0x3f56132f), ...`. `Content.Status().Placeholders` (Kiln.Admin
  `content_status`, see the requested change), `NewContent.Placeholders()`, `info()` `placeholders`, and
  `definitions()` (`"placeholder": true`, `mod`). A placeholder's references to its mod's own templates and models stay
  unresolved while the mod is away: one Info line per item, `new content: placeholder item-blocks.ember-bench: 1
  reference(s) to its mod's resources stay unresolved while the mod is not loaded (the base's values stay; a program:
  none): equipment.placedEntity`. The same unresolved reference on an item of a loaded mod is a warning.
- **A program it cannot resolve is none** (since 0.2.0-dev.29.11): an `equipment.program` (any `ImpactProgramReference`
  or `ObjectReference<keen::ImpactProgram>`) that nothing serves now is not kept as the base's program, so a missing
  mod's gem with a program of its own (a `Gems.Register` trigger gem, a gem with a cooked program) does nothing when
  socketed instead of acting as its base gem; the game runs nothing for a gem without a program (it logs "Gem has no
  valid Impcat Program assigned" in its own log every step while such a gem sits in an impact slot). kiln.log: `new
  content: placeholder Gem_mymod_frostbite: 1 program reference(s) nothing serves now: it runs no program (not its
  base's)`. A program that still resolves (a game program, a trigger program another loaded mod's gem serves) is kept.
  Kiln's own gem trigger programs resolve too: Kiln.Abilities serves the one a placeholder names when no registered gem
  does (kiln.log `gems: placeholders of a removed mod's gems name the trigger program on_kill (...): Kiln serves it, so
  they run it and do nothing`), and the reference retry (~2 s) points the placeholder at it ([gems-new.md](gems-new.md),
  "Unload").
- **The mod comes back**: at the next start its definition is there before the placeholders are made (none is
  made). Loaded while the world runs (`load <mod>`), its definition takes the GUID over: `state` `restored`, kiln.log
  `Kiln_PH_Sword ... is back (mod placeholder-items): it takes its id 0x3f56132f back from the placeholder`.
- Kiln clients joined to the server get the placeholders' definitions (see below), so they show "[Missing: ...]"
  instead of "[Invalid Item Stack]".
- Lab (`mods/tests/placeholder-test` + `Items/`, four boots, SIGINT and kill -9): see "Lab test" below.

## Multiplayer registry and skills edits

For the shipped `registry` section, item/recipe live layers are included in the effective ContentSync export just like
older content layers. A connected Kiln client receives a new snapshot after WorldLoad/HotReload changes; removing a
server layer sends a snapshot without it, and the client first removes the previous server-sync owner layer. A value
read only at world load is not retroactively consumed by systems that already read it. Per-field visibility and
`clients` choices are in [the registry guide](../modding/registries/README.md#what-a-joined-client-sees).

**The game's skill nodes' costs** are a registry field since 0.2.0-dev.32 (`skills.cost`): the registry's edit is a layer of
kiln.gamedata's `skills` slot of that node (the same slot as a pack's `"skills": {"0x4475e8dd": {"costs": 0}}`, which lies
below it), and the content sync sends it with kiln.gamedata's other sections. The game's skill tree never copies a node: its
node map points into the tree resource, so the server's next unlock, join check and respec count the new cost at once, and a
Kiln client that received the slot writes it into its own resource, which its screen and its own point count read. A vanilla
client keeps its own costs on its screen; the server's unlock check decides.

**Nodes a mod adds are not.** A pack's `skills/*.json` / `gameTree` definitions (Kiln nodes, their cost included) and the
expanded tree kiln.skills builds from them are computed independently on each side, not transferred by ContentSync. If a
server changes a Kiln node's defined cost from 3 to 0 while a player is joined, the player's existing tree can still show
3; server acceptance and the displayed cost can disagree for that connection. Rejoin with the same updated skill pack on
the client (restart too if its boot data was already committed). Rejoining without the matching client files is not a sync
mechanism. Use `clients: "mod"`, with the matching pack on both sides; `clients: "kiln"` alone does not supply a new tree.
Shadow-node unlock-state messages are separate from rebuilding that tree. See [skills](skills.md).

## Kiln clients get definitions (`"clients": "kiln"`, A5)
A mod whose manifest says `"clients": "kiln"` does not have to be installed on players' games: the server's content
state (docs/kiln/sync.md) carries `"@definitions": [{kind, mod, definition}]` with every registered definition of
such mods (code and data packs) and every placeholder. A Kiln client defines them as the "server sync" owner before
it applies the values, skipping GUIDs its own mods define, and sets them aside when it leaves (`undefine_owner`:
items stay registered until the game closes, recipes leave their station). Texts and kitbash references travel with
the definition; PNG icon paths are not sent (the client shows the base's or the `item:` icon; a PNG needs the mod or
a modpack). References of game items changed by layers are sent as GUIDs with `"options": {"refs": true}`.
`"clients": "mod"` (default for `side` client / both) and `"none"` (default for server mods) send nothing.

## Lab tests (v4)
```sh
export KILN_OUT=<build dir>
dotnet build mods/tests/kitbash-test/KitbashTest.csproj -c Release -o $KILN_OUT/mods/kitbash-test --artifacts-path $KILN_OUT/dotnet
dotnet build mods/tests/placeholder-test/PlaceholderTest.csproj -c Release -o $KILN_OUT/mods/placeholder-test --artifacts-path $KILN_OUT/dotnet
dotnet build mods/tests/placeholder-test/Items/PlaceholderItems.csproj -c Release -o $KILN_OUT/mods/placeholder-items --artifacts-path $KILN_OUT/dotnet
tools/lab/lab.sh new content-data 26050 --save <savegame dir>
tools/deploy-server.sh ~/srv/enshrouded/labs/content-data --policy any --samples
echo '{"labPort": 26051}' > ~/srv/enshrouded/labs/content-data/kiln/config/network.json
tools/lab/lab.sh start content-data   # kitbash-test: 19 PASS (+ distribution); placeholder-test phase 1: 5 PASS
mods/tests/kitbash-test/syncdefs.py --port 26051      # 7 PASS: @definitions of "clients": "kiln" mods, refs as GUIDs
# placeholder-test: phase 2 / 3 without the items mod (phase 3 after kill -9 of the phase-2 server), phase 4 with it
touch ~/srv/enshrouded/labs/content-data/kiln/mods/placeholder-items/.disabled
tools/lab/lab.sh stop content-data; tools/lab/lab.sh start content-data   # phase 2: 10 PASS (stacks of 420 / gear kept)
kill -9 $(tools/lab/lab.sh pid content-data); tools/lab/lab.sh start content-data   # phase 3: 10 PASS
rm ~/srv/enshrouded/labs/content-data/kiln/mods/placeholder-items/.disabled
tools/lab/lab.sh stop content-data; tools/lab/lab.sh start content-data   # phase 4: 9 PASS, the container as it was
```
Hot return: with placeholders active, `echo "load placeholder-items" > kiln/control/l.cmd` logs "... is back (mod
placeholder-items): it takes its id ... back from the placeholder" (`state` `restored`).

## Data packs and profiles (A6)
`discover_packs` asks the in-core service `kiln.profile` (`native/core/abi/services/kiln_profile.h`) for the
profile's managed mods (their folders in profile order, then `kiln/mods` when the profile keeps that drop-in folder);
a profile with no mods or an invalid one gives no packs; without the service (the legacy layout, or a core that does
not have it yet) every folder of `kiln/mods` in name order, as before. Relative PNG icon paths of a pack resolve to
its mod's folder.

## Near memory (new items and recipes)
A new item or recipe is a copy of its base, and the copy keeps the 32-bit self-relative offsets of the game's data it points into, so it must lie
within 2 GB of that data. kiln.content therefore sets address space aside at boot, before the game fills the space near its resources, and hands
out memory from it a chunk (256 KB) at a time; only when none is in reach does it look around the target. kiln.log, at boot:
```
[service:kiln-content.dll] near memory: 128 MB set aside at 0000000070000000
[service:kiln-content.dll] near memory: 7 arena(s) of 128 MB set aside above 4 GB, from 0000000140050000 (the system's next allocation would go to 0000000000800000): 0000000140050000, 0000000220050000, ...
```
The first line is the low arena (the game's data lands in the low 4 GB under Wine), the second a ladder of arenas above 4 GB, spaced so that every
address up to its end has one in reach (on a native Windows server the data is above 4 GB). The world's first definitions then log
`near memory: chunk 1 at 0000000200000000, 256 KB, for target 0000000152F5D230`. When nothing is in reach, one line says why, once per
neighbourhood (the definitions that go without name themselves: `not registered in the server world: no memory near the base item`):
```
near memory: nothing free within 2 GB of 0000000152F5D230 for 2112 bytes (0 chunks): window 00000000D3F5D230 to 00000001D1F5D230; walked 32512 regions above
to the window's edge and 32512 regions below to the window's edge in 313 ms; free runs seen: 0, none inside the window that can hold a block; the whole
window is reserved or committed; 8 arena(s), the nearest at 0000000200000000 is 2.45 GB above the target: out of reach
```
It names the target, the window, how many regions each side saw and why each side stopped (to the window's edge; the limit of 262,144 regions; the top of
the address space), the largest free run that lies in the window and whether it is too small, and the nearest arena (in reach, out of reach, or full).
"The whole window is reserved or committed" means the game left no room; "the window was not seen in full" means the walk was cut short and
the line is the evidence to send. Internals and measurements: `docs/re/content.md` section 10.

## Station menus (kiln.stationmenus, since 0.2.0-dev.31)
kiln.content's DLL also serves the table `kiln.stationmenus` v1 (`native/core/abi/services/kiln_station_menus.h`): the
crafting-menu layouts of mods' own workshops ("menus with tabs of their own",
[../modding/world-blocks.md](../modding/world-blocks.md#menus-with-tabs-of-their-own-since-020-dev31)). One normalized
record per layout (format 1, canonical JSON: origin station and its workshop, the definition, the base menu, tabs, sets
and recipe ids), one parser and serializer for `define`, `export_layouts` and `remote_replace`. Layers per (owner,
definition): the newest accepted wins its workshop, removing it shows the one before or the game's layout; a joined
client's server collection (`remote_replace`, `remote_clear`) is above every local layer. 256 layers, 1 MiB per record, 64
MiB in all. On a game client the existing crafting-menu hook (`content.recipe_menu`) builds the menu of a workshop with a
layout (all arrays in near memory first, both collection headers switched last; at most 16 builds per workshop and 64 MiB
in all, then `RestartRequired`); `set_pending` keeps own workshops empty while a joined client waits for the server's
menus; `drain_applied` hands what the hook built to Kiln.Content, which posts `StationMenuAppliedEvent` on the tick. The
managed side is `StationMenuStore` / `StationMenuSync` in Kiln.Content; the channel is [../sync.md](../sync.md#station-menus).
Capability rows: `stationmenus.store`, `stationmenus.render`, `stationmenus.sync.v1` ([caps.md](caps.md)).

## Not yet
- A changed reference of a **game** item is not sent to clients for list fields (`model`'s per-body / pickup model
  lists): clients get the GUID fields. New items (definitions) are complete on both sides.
- Recipes in the trader's barter screen and random loot rolled for a player are client checks.
- Global loot multiplier: the world setting "Loot Amount" (`resourceDropStackAmountFactor`) is kiln.settings';
  per-table and per-item changes are here.

## The contract (dev.27)

Kiln.Content follows [the API contract](../modding/contract.md).

- **Capabilities.** `kiln.content` and `kiln.gamedata` (the tables); the server-side sigdb features (`content.items`,
  `content.recipes`, `content.loot`, `content.loot_new`, `content.drops`, `content.new`, `content.reflection`,
  `content.stack_guard`; `gamedata.template_clones`, `gamedata.buff_lookup`), lowered by Kiln.Content when their
  service is not loaded; `content.references` (managed, static: this kiln.content sets references, table v4) and
  `content.ready` (managed, session: the registries are loaded and the packs applied, `Content.Ready`; refreshed
  every 25 steps). Both are reported at Kiln.Content's start, the first service tick, before a mod calls anything.
- **Errors.** A capability that is off throws `KilnUnavailableException` naming it, where these members threw
  `NotSupportedException` before (it still is one). Each member carries `[RequiresCapability]` with the row it
  needs: `kiln.content`, `kiln.gamedata`, `content.new` (`NewContent`), `content.references` (`SetRef`, `SetPerks`),
  `gamedata.template_clones` (`GameDataRegistry.Define`). Unknown fields and refused values keep
  `KeyNotFoundException` / `ArgumentException`.
- **Events.** `ContentChangedEvent` (new) is posted for every change made from code: `ContentEntry.Set`, `SetRef`,
  `SetInputs` / `SetOutputs`, `SetStacks` (`Set`), `ContentEntry.Reset` (`Reset`), `Content.Apply` (`Applied`) and
  `Content.Revert` (`Reverted`), with the entry, the path and the mod; only when the call changed something (a
  document whose every section was refused, a `Reset` of a layer the mod did not have and a `Revert` with nothing to
  put back post nothing). A report longer than the 2 MiB buffer is cut: `Applied` still counts what was applied (read
  from the report's start) and `Applied` is posted; the report's errors then say it could not be read whole, as
  before. A write made by a handler while the event is being dispatched (a catch-all
  `Events.On<KilnEvent>` that writes again) is posted at the start of the next step, once per field: such a handler
  cannot loop ([contract](../modding/contract.md#3-change-events)). Data packs that load without a call keep
  reporting through `ContentReadyEvent`. Not covered yet: `GameDataEntry.Set` and `NewContent.Register` (kiln.gamedata
  has `GameDataEvent` for its own animation events only).
- **Threads.** `[TickThreadOnly]` on every call that reads or writes a registry (also `RecipeStack.ToString`, which
  looks its item's name up); `Content.Ready` and `Status()` read kiln.content's own bookkeeping on any thread.
- Tested offline: rows, exceptions, attributes, the service's start (`ServiceStartTests`), and the writes with
  subscribers on a fake kiln.content table (`ChangeEventTests`: a handler that writes again, refused writes, a
  document that changed nothing). Against a real kiln.content no lab run in dev.27 has watched the event.
