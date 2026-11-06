# kiln.content: registries and data packs

Read and change the game's content at run time, the way Forge mods use registries and data packs: items (max stack,
rarity, damage, armor, fuel, …), recipes (ingredients, results, duration, unlock), random loot per item (stack sizes)
and drop tables (enemy drops, fixed chests). Every change is a layer owned by a mod over the game's original value:
it comes off when the mod unloads or reloads, and it is applied again at the next world load. Mods ship data packs
(`content/*.json`) or change things from code. Evidence and layouts: `docs/re/content.md`.

| Layer | Where |
|---|---|
| Symbols | `sigdb/symbols/server.d/content.json` (features `content.reflection`, `content.items`, `content.recipes`, `content.loot`, `content.drops`, `content.stack_guard`, `content.new`); client overlay `client.d/content.json` (`content.loca`, `content.ui_texture`, `content.recipe_ui`, `content.recipe_ui_refresh`) |
| C table | `native/core/abi/services/kiln_content.h` (`kiln.content` v4: v2 `export_changes`, v3 `define` / `definitions`, v4 `set_ref` / `undefine_owner` / `info`) |
| Native | `native/services/content/` → `kiln/services/kiln-content.dll` |
| Managed | `managed/services/Kiln.Content/` → `kiln/host/Kiln.Content.dll` (namespace `Kiln`, class `Content`) |
| Test | `mods/tests/content-test/` (PASS / FAIL lines) + `Observer/` (logs value changes); `mods/tests/newcontent-test/` (new items / recipes); `mods/tests/kitbash-test/` (references, texts, distribution; `syncdefs.py`: definitions in the content sync); `mods/tests/placeholder-test/` (+ `Items/`: missing content over four restarts); `mods/tests/craftmatch-test/` (a made item is what its recipe tooltip promises) |
| Sample | `mods/samples/BigStacks/` (bigger stacks, a C# port of EMF's big_stacks idea); `mods/samples/NewItems/` (new items and recipes, side both) |

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
  (default-inventory tables: enemy loot, fixed chests; key = table GUID or a glob), `options`.
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
  made from code win over every pack (in call order). Two owners giving one field different values is a conflict:
  logged (`conflict: items Material_T1_Twigs.maxStackSize: pack … sets 600, bigstacks … sets 1000 (the later one wins: …)`)
  and listed in `Content.PackReport()`. A clone's definition and its own mod's later changes to it are not: the definition is
  the clone's starting value (a block writes `none` until a game VFX's name is known, then a rule writes that VFX). Problems (unknown field, value out of range, selector matching nothing) are
  logged per file and never stop the other entries.

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
  layer); items: `MaxStack`; recipes: `Inputs`, `Outputs`, `SetInputs`, `SetOutputs`, `CraftingDuration`; drops:
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
tools/lab/lab.sh start content        # fresh: 34 PASS (registries, pack, code changes, 1500 logs in one slot, drops, dump)
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
  `BaseOn` the recipe has no level / comfort / prop / water requirement), `Menu` (`"menu"` in a pack: the `"0x…"`
  workshop whose crafting menu lists it, when that is not its own; a station opens the menu of its
  `CraftingInteraction.workshopId` and runs the recipes of its `Crafting.workshopId`. At a `"workshop:<guid>"`, a
  player's game gives that workshop a menu of its own instead, listing only its recipes, under the tabs and subcategories
  of this menu where they would go), `StationItem` (`"stationItem"`, with a `"workshop:<guid>"` only: the item that
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
`outputs`, `duration`, `unlock`). Rules after the list (in the same pack, or later packs) can change the new entries
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
- **Definitions made later** cannot be there earlier: data packs' `add` lists and `OnReady` handlers run when kiln.content's registries are ready
  (hosted: long before the client world; a first joined world: ~300 ms after its first step; later worlds keep them from the first). In that
  window such an item answers "[Unknown item]", then its definition takes over.
- **Perks and buffs** keep their later registration (hosted: the client world's step 11 mod 25; joined: registries ready, ~300 ms after the first
  step). Every game lookup of both tests for a miss (docs/re/content.md section 9), so a Kiln perk on an item is skipped until then. Skill trees are
  served as the game's resource to each new world before it steps (`skills: the new world's skill tree loaded`).
- Switches, in `<kiln>/data/content/`: `no-early-registration`, `no-unknown-items` turn the parts off; `early-test` runs the same path on a server
  world and answers unknown ids there too (lab check only).

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
  subcategories → recipe ids). The service adds each new recipe to the subcategory that holds its base recipe; else
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
  (`keen::Model` references: `keen::RenderModel`; templates: `keen::ecs::TemplateResource`), or, for a template, when it
  is a template clone defined in this game (a world registers its clones at its load, after its items and before anything
  can place one, so a placeable item names its own template from the start). `item:<name>.<path>`
  must name a field of the same type. Anything else is skipped with a warning (the base's value stays; the item still
  registers): `NewContent.Definitions()` / `definitions()` list them as `warnings`, kiln.log has one line each (a
  placeholder's, whose references name its missing mod's resources, are one Info line per item instead). A
  reference that was not loaded is tried again every ~2 s (kiln.assets may register it later), and at once when a
  template clone goes into a world (a joined client gets its items before the templates in the same content state).
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
  every table seen so far when definitions change. A plain group gets one more child group holding the new stacks
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
  reference(s) to its mod's resources stay unresolved while the mod is not loaded (the base's values stay):
  equipment.placedEntity`. The same unresolved reference on an item of a loaded mod is a warning.
- **The mod comes back**: at the next start its definition is there before the placeholders are made (none is
  made). Loaded while the world runs (`load <mod>`), its definition takes the GUID over: `state` `restored`, kiln.log
  `Kiln_PH_Sword ... is back (mod placeholder-items): it takes its id 0x3f56132f back from the placeholder`.
- Kiln clients joined to the server get the placeholders' definitions (see below), so they show "[Missing: ...]"
  instead of "[Invalid Item Stack]".
- Lab (`mods/tests/placeholder-test` + `Items/`, four boots, SIGINT and kill -9): see "Lab test" below.

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

## Not yet
- A changed reference of a **game** item is not sent to clients for list fields (`model`'s per-body / pickup model
  lists): clients get the GUID fields. New items (definitions) are complete on both sides.
- Recipes in the trader's barter screen and random loot rolled for a player are client checks.
- Global loot multiplier: the world setting "Loot Amount" (`resourceDropStackAmountFactor`) is kiln.settings';
  per-table and per-item changes are here.
