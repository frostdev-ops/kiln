# World blocks

Things a player builds into the world: props that stand there, stations that make things, furniture that does something, building materials and shapes. They're blocks like the rest ([blocks.md](blocks.md)): C# records, the same shape in `blocks/*.json`, no `base` to name, no GUIDs.

## Props and stations
Sample: `mods/samples/StationBlocks`. Every block below appears there in C#, and in `blocks/twins.json` as data.

```csharp
// works on its own, over time, from its own inventory, like the game's drying rack
Blocks.Define("herb-rack", new StationBlock
{
    Name = "Herb Rack",
    Processing = new ProcessingBlock { Slots = 12 },
    Recipe = new RecipeBlock { Station = StationKind.Workbench.Workshop, Needs = { [GameItems.Material_T1_Wood_Logs] = 6 } },
    Recipes =
    [
        new RecipeBlock { Seconds = 4, Needs = { [GameItems.Material_T1_Twigs] = 2 }, Output = GameItems.Material_T1_Wood_Logs, Makes = 3 },
    ],
});

// fuel is an input, as in the game's forge: Burns says which
var forge = Blocks.Define("cold-forge", new StationBlock
{
    Name = "Cold Forge",
    Kind = StationKind.Forge,
    Processing = new ProcessingBlock { Slots = 16, GoesOutInRain = false },
    Comfort = new ComfortBlock { Amount = 2, Category = ComfortCategory.Hearth },
    Recipes = [new RecipeBlock { Seconds = 3, Burns = { [GameItems.Material_T1_Charcoal] = 2 }, Needs = { [GameItems.Material_T1_Metal_Scraps] = 1 }, Output = GameItems.Material_T1_Metal_Sheets }],
});

// an item of your own says where it's made
Blocks.Define("frost-ingot", new ItemBlock
{
    Name = "Frost Ingot",
    Recipe = new RecipeBlock { Station = forge, Seconds = 2, Burns = { [GameItems.Material_T1_Charcoal] = 1 }, Needs = { [GameItems.Material_T1_Metal_Sheets] = 1 } },
});

Blocks.Define("old-table", new PropBlock
{
    Name = "Old Table",
    Visual = new VisualBlock { Like = GameItems.Prop_Decoration_Comfort1_T0_Table_Medium },
    Comfort = new ComfortBlock { Amount = 3, Category = ComfortCategory.Table },
});
```
```jsonc
{ "format": 1,
  "stations": {
    "cold-forge": { "name": "Cold Forge", "kind": "forge", "processing": { "slots": 16, "goesOutInRain": false },
      "comfort": { "amount": 2, "category": "hearth" },
      "recipes": [ { "seconds": 3, "burns": { "Material_T1_Charcoal": 2 }, "needs": { "Material_T1_Metal_Scraps": 1 }, "output": "Material_T1_Metal_Sheets" } ] } },
  "props": {
    "old-table": { "name": "Old Table", "visual": { "like": "Prop_Decoration_Comfort1_T0_Table_Medium" }, "comfort": { "amount": 3, "category": "table" } } } }
```
JSON sections: `props`, `stations`, and for parts `placements`, `comforts`, `processing`, `menus`. Schema: [schemas/kiln-blocks.md](schemas/kiln-blocks.md).

| Block | Fields |
|---|---|
| `PropBlock` | `Visual`, `Collider` or `Colliders`, `Placement`, `Comfort`, `Light`, `Provides`, `OnInteract` ([interactions](#furniture-and-interactions)), `Recipe`, `Loot`, `Tweak` (its item), `TweakPlaced` (its template) |
| `StationBlock` | all of a prop's, and `Kind`, `Processing`, `Recipes`, `OwnWorkshop`, `Menu` |
| `PlacementBlock` | `On` (`Surface.Floor`, `Wall`, `Ceiling`, or several: the first is tried first), `InBase`, `Size` (metres), `Snap`, `Water`, `Fog`, `Consumed` |
| `ComfortBlock` | `Amount`, `Category` (`ComfortCategory`), `Range` (metres, 5), `Warmth` (1) |
| `ProcessingBlock` | `Slots` (1..64), `GoesOutInRain`, `Running` (`Vfx`, `Sound`) |
| `MenuBlock` | `Like`: the station whose menu it opens |

All take `Name`, `Description` and `Was`. A field left out is what the block starts from.

- **What it starts from.** A station starts from its `Kind`: `Workbench`, `Carpenter` and `Kitchen` craft at once; `Forge`, `Oven`, `Alchemy`, `DryingRack`, `CharcoalKiln`, `Kiln`, `Smelter`, `Fireplace`, `Grinder`, `Tannery` and `TableSaw` work on their own. A prop starts from its look: `Visual.Like` names a game item that places something, which brings its model, collision and how it places. With only `Visual.Model`, a plain game prop for the surface in `Placement.On` brings the rest.
- **Its look.** `Visual.Like` or `Visual.Model` sets all three places a model shows: what stands, the ghost under the cursor, and the item's picture. `Model` is a glTF in the mod's folder (`"models/lantern.glb"`), `"item:<name>"`, or the GUID of a model your `assets/assets.json` serves ([models.md](models.md)). A glTF in a block of `blocks/*.json` is cooked by `kiln cook` with nothing else to write: modelcook finds it there, cooks it as a rigid model with its glTF materials, and adds it to `assets/assets.json`. Its GUID follows from `<mod>:model:<path without the extension>`, so the block and the cook agree. A glTF in a block written in code needs a model step in `assets/cook.json` (`{ "op": "model", "gltf": "../models/lantern.glb", "mode": "new", "materials": "gltf", "name": "models/lantern" }`). Until it is cooked, a client shows the starter's look and the block's warnings say so.
- **Its collision.** With a model of your own, what a player bumps into and what hits stop at is one box: the box around the glTF's meshes, or `Collider = new BoxCollider { Size = new Vector3(1, 0.5f, 1) }` (`Center` moves it; default: standing on the floor). It goes on what stands there, on the item's placement check, and on the box the cursor checks (unless `Placement.Size` says otherwise). On a ceiling the starter's collider is a capsule around the same box. A game item's look, a station and furniture keep the collision they start from, and refuse `Collider`.
- **Several boxes.** `Colliders` says a list in place of one box: a table's top and its legs, a doorway's two posts (at most 16, each as `Collider`; not with `Collider`). They are all solid and together one shape in the template: the game's collider holds a list of shapes, and Kiln makes the starter's list as long as yours (kiln.gamedata's `growArrays`, [services/gamedata.md](../services/gamedata.md)) before it sets each box by index. The physics shape has one hash for all of them, so a changed box is a new shape. The box a player's cursor checks for room, and the item's placement collider, stay one: the box around them all, unless `Placement.Size` says otherwise. A prop on a ceiling starts from one capsule, so several boxes need a floor or a wall. Only boxes are built: a sphere or a capsule among them is not.
```csharp
Blocks.Define("lantern", new PropBlock { Name = "Lantern", Visual = new VisualBlock { Model = "models/lantern.glb" }, Recipe = new RecipeBlock { Needs = { [GameItems.Material_T1_Twigs] = 1 } } });

// a table is its top and its legs: three boxes, one shape
Blocks.Define("trestle", new PropBlock
{
    Name = "Trestle Table", Visual = new VisualBlock { Model = "models/trestle.glb" },
    Colliders = [new BoxCollider { Size = new(2, 0.15f, 1), Center = new(0, 0.9f, 0) }, new BoxCollider { Size = new(0.15f, 0.85f, 0.8f), Center = new(-0.85f, 0.425f, 0) }, new BoxCollider { Size = new(0.15f, 0.85f, 0.8f), Center = new(0.85f, 0.425f, 0) }],
});
```
- **A workshop of its own.** A station that works on its own and has `Processing` or `Recipes` gets one: it runs those recipes and no others, and the game's stations of its kind don't run them. `OwnWorkshop = false` shares its kind's instead: it runs the game's recipes too, and its own also run at the game's stations. A bench shares by default: the server doesn't check a player's craft against the bench's workshop, so one of its own would only change what its menu lists.
- **Crafting props.** Some of the game's recipes ask for a prop near the station that makes them. `Provides = true` makes your prop one that a recipe of yours can ask for: `NeedsProps = [thatProp]` in a `RecipeBlock` (a station's list, an item's recipe, or a block of its own), or `"needsProps": ["rune-table"]` in JSON, by name (`"other-mod:name"` for another mod's; at most 8). The game keeps 13 free places for such props, for every mod together; yours takes the next one in every game that has the mod ([services/worldtables.md](../services/worldtables.md)), so a mod that provides one says `clients: mod`. The game's normal craft request succeeds only while a placed prop of yours stands near the station, and its own check refuses it otherwise. This is for manual stations such as workbenches. The automated factory path passes no nearby-prop context to that check, so factory block recipes refuse `NeedsProps` rather than register a recipe that never runs. The game's table is read once at its start: a prop of code is in it from the second start, and the block and the recipes that ask for it are made then (the log says so on the first); a prop in a data file is there from the first. The recipe's tooltip names the prop by its item's name.
```csharp
var runeTable = Blocks.Define("rune-table", new PropBlock { Name = "Rune Table", Visual = new VisualBlock { Like = GameItems.Prop_Decoration_Comfort1_T0_Table_Medium }, Provides = true });
Blocks.Define("rune-bench", new StationBlock
{
    Name = "Rune Workbench", Kind = StationKind.Workbench,
    Recipes = [new RecipeBlock { Needs = { [GameItems.Material_T1_Twigs] = 2 }, Output = GameItems.Material_T1_Wood_Logs, NeedsProps = [runeTable] }],
});
```
- **Its recipes.** `Recipes` lists what it makes, each with `Output`. An item says the same from its side with `Recipe = new RecipeBlock { Station = thisStation, ... }`. At a workshop of its own a recipe needs `Seconds`: there are no recipes of the game to take a time from. `Seconds` is at the game's normal speed: a world's production speed setting divides it as it does the game's own recipes, so a 4 s recipe takes 2 s in a world set to twice the speed. A cycle takes its inputs when it starts and gives its outputs when it ends; with nothing left it stops, and starts again by itself when given more.
- **Fuel.** `Burns` lists fuel the way `Needs` lists ingredients. It's an input, as charcoal is in the game's forge, so a station out of fuel stops like one out of anything else. A burn timer that runs on its own isn't a block yet.
- **The menu.** A station with a workshop of its own has a menu of its own: its recipes and no others, each once, under the tabs and headings its kind's menu (or `Menu.Like`'s) would list them under, and titled with the station's name. Its screen offers its first recipe until a player picks one, and its recipe picker ("Change recipe") lists the same. The game's stations of its kind don't list these recipes. A station that shares its kind's workshop opens its kind's menu, or `Menu.Like`'s, and its recipes are listed there among that station's own. A menu with tabs of its own is a later block that plugs into `Menu`.
- **Where its item is crafted and listed.** `Recipe` makes the block's item at a station (by hand without one). In that station's menu it is listed beside the game's recipe for the item it starts from, when the station makes that one too, else with the first group of placed things there: at the workbench, a station goes with the chests and a table with the game's tables.
- **Where it places.** As its starter: the item is the starter item's in everything the game's placement check reads (box, colliders, angles, base and no-build rules), apart from what `Placement` says.
- **Comfort.** `Comfort` gives the placed thing the game's comfort, also where its starter had none, and the item's tooltip gets the Comfort row. The game's rule applies: only the best of a category counts near a player.
- **Rain and roofs.** `GoesOutInRain = false` keeps it lit in any rain. A station that works on its own never checks a roof: the game's don't either, so their recipes refuse `Sheltered`. On a bench recipe, `Sheltered` is the game's own flag.
- **Light.** A VFX on it for as long as it stands, which is where the game's lamps have their light. `Visual.Vfx` says the same from the look; one of the two.

### From code
```csharp
var rack = Build.PlaceProp("my-mod.herb-rack.station", position);    // a clone by name or GUID, or the item that places it
Stations.Start(rack, recipe.Id);                                     // as a player's choice in its menu
Stations.Status(rack);                                               // State (Running, MissingMaterials, ...), the recipe, when the cycle started, what it needs
```
A station's template is `<mod>.<block>.station`, a prop's `<mod>.<block>.prop`. `Build.PlaceProp` sets what dismantling gives back to the block's item.

A prop collider is at most 32 m along each axis, including one derived from a model. Centers and dimensions must be finite. Changing its dimensions
or center changes the physics-shape hash too, so a reload uses the new collision instead of a shape cached from the previous definition.

### Saves and limits
- Ids come from the mod id and the block's name, the workshop's too: placed stations and what they hold come back after a restart, still running. Inside a base, that is: one placed outside a base by `Build.PlaceProp` didn't come back, with or without its item.
- The game reserves its workshop map once, with room for 128, and uses 46. Kiln keeps a quarter free so lookups stay short, which leaves 50 new workshops for all mods. A station past that is refused with its name.
- A station's item counts against the world's factory budget, as the game's stations do.

### Mistakes
A wrong block registers nothing, and the message names the block and the field:
```
blocks/forge.json: block station bad, processing: a workbench doesn't work on its own: a player crafts at it at once. Give it a kind that does (forge, oven, ...), or leave processing out
blocks/forge.json: block station rack, recipes[0].seconds: a station of its own has no recipes of the game to take a time from: say how many seconds
```

### What players' games need
`clients: kiln`: a new item, template and recipe. A model of your own, a PNG icon, a cue of your own or `Provides` needs `mod`.

Not proven with a client: a cooked prop model drawn, the cursor's ghost, and bumping into its box or boxes. The lab reads the boxes back from the template and the item (`mods/tests/blocks-more-test`, `mods/tests/furniture-test`). Also not proven with a client: that a recipe's tooltip lists a crafting prop under its item's name, and that a station's screen offers the recipe once the prop stands near it (the server's check is the lab's).

### Test
`mods/tests/stations-test` runs on a dedicated server with no player: the exact cycle, running out, burning fuel, a workshop of its own, placing clones, comfort through the game's own system, C# and JSON the same, the errors a mistake gets, and that every placed thing of the block samples loaded with it (stations, props, furniture) has its starter's item and template apart from what the block sets. Start it twice: the second run checks what the first one saved. It logs `stations-test: PASS` and `FAIL` lines and `stations-test done: N PASS, M FAIL`.

## Furniture and interactions
Sample: `mods/samples/FurnitureBlocks` (C#, and `blocks/twins.json` as data).

```csharp
// the game's own lock: no scrap in the bag, no opening, and the player's game shows it locked
Blocks.Define("lockbox", new ContainerBlock
{
    Name = "Lockbox", Slots = 8,
    OnInteract = new InteractBlock { Requires = new RequiresBlock { Item = GameItems.Material_T1_Metal_Scraps } },
});

// the game's sleep and rest; Kiln adds a spawn point and a night slept through
Blocks.Define("camp-bed", new BedBlock
{
    Name = "Camp Bed", Visual = new VisualBlock { Like = GameItems.Prop_Decoration_Comfort1_T0_Bed_Cot },
    SpawnPoint = true, SkipsNight = true,
});

// a container that holds something when it is placed, once: a loot table by name (or written in place)
Blocks.Define("scrap-pile", new LootTableBlock { Entries = [new LootEntry { Item = GameItems.Material_T1_Metal_Scraps, CountMin = 3 }, new LootEntry { Item = GameItems.Material_T1_Twigs, CountMin = 2 }] });
Blocks.Define("supply-cache", new ContainerBlock { Name = "Supply Cache", DefaultLoot = "scrap-pile" });

// a barrel has no lid to lift; a door that swings one way and is used from one side
Blocks.Define("barrel", new ContainerBlock { Name = "Barrel", Lid = new LidBlock { Moves = false } });
Blocks.Define("one-way-door", new DoorBlock { Name = "One Way Door", Opens = DoorSide.Front, Sides = DoorSide.Front });

// an interaction is a part: Kiln checks the player on the server, and declines the way the game does
var pray = Blocks.Define("pray", new InteractBlock { Verb = InteractVerb.Use, Requires = new RequiresBlock { Unless = ["InCombat"] }, Effects = [new EffectBlock { Heal = 25 }] });
Blocks.Define("prayer-bench", new SeatBlock { Name = "Prayer Bench", Visual = new VisualBlock { Like = GameItems.Prop_Decoration_Comfort1_T0_Bench }, OnInteract = pray });
```
```jsonc
{ "format": 1,
  "containers": {
    "lockbox": { "name": "Lockbox", "slots": 8, "onInteract": { "requires": { "item": "Material_T1_Metal_Scraps" } } },
    "supply-cache": { "name": "Supply Cache", "defaultLoot": "scrap-pile" },
    "barrel": { "name": "Barrel", "lid": { "moves": false } }
  },
  "beds": { "camp-bed": { "name": "Camp Bed", "visual": { "like": "Prop_Decoration_Comfort1_T0_Bed_Cot" }, "spawnPoint": true, "skipsNight": true } },
  "lootTables": { "scrap-pile": { "entries": [ { "item": "Material_T1_Metal_Scraps", "countMin": 3 }, { "item": "Material_T1_Twigs", "countMin": 2 } ] } },
  "doors": { "one-way-door": { "name": "One Way Door", "opens": "front", "sides": "front" } },
  "interactions": { "pray": { "verb": "use", "requires": { "unless": ["InCombat"] }, "effects": [ { "heal": 25 } ] } },
  "seats": { "prayer-bench": { "name": "Prayer Bench", "visual": { "like": "Prop_Decoration_Comfort1_T0_Bench" }, "onInteract": "pray" } } }
```
JSON sections: `containers`, `doors`, `beds`, `seats`, and `interactions` for parts (loot tables are the `lootTables` section, [item-blocks.md](item-blocks.md)).

| Block | Starts from | Fields of its own |
|---|---|---|
| `ContainerBlock` | the game's small chest: its lid, sounds, upgrades | `Slots` (1..120), `DefaultLoot`, `Lid` (`LidBlock`: `Moves`, `Graph`, `OpenState`, `ShutState`) |
| `DoorBlock` | the game's wooden door: opens both ways, paths and water follow it | `Opens`, `Sides` (`DoorSide.Both`, `Front`, `Back`) |
| `BedBlock` | the game's wooden bed: its sleep, comfort of the bed category | `Lies`, `SpawnPoint`, `SkipsNight` |
| `SeatBlock` | the game's wooden chair: its sitting, comfort of the chair category | `Sits` |
| `InteractBlock` | the part in `OnInteract` | `Verb` or `Prompt`, `Plays`, `Toggle` (`On`, `Off`, `OnPrompt`, `OffPrompt`, `StartsOn`), `Requires`, `Effects`, `OnRefused` |
| `RequiresBlock` | | `Item`, `Count`, `Uses`, `Time` (`DayPart.Day`, `Night`), `When`, `Unless`, `If` (code) |

All four take a prop's fields (`Visual`, `Placement`, `Comfort`, `Light`, `Recipe`, `Loot`, `Tweak`, `TweakPlaced`), and so does `OnInteract`, which every prop and station has. `Visual.Like` names another game item of the kind (a kingsize bed, a stool) and brings its model, collision and spot to sit or lie on; with `Visual.Model` the kind's starter keeps its collision and spot, so say `Sits` or `Lies` (`new SpotBlock { At = new(0, 0.45f, 0), Facing = 90 }`: metres in the thing's own axes, degrees about the vertical).

- **The prompt and what the player plays.** `Verb` is one of the game's words (`InteractVerb.Open`, `Sit`, `Sleep`, `Use`, `Read`, `Search`, ...), so every language has it; `Prompt` is a text of your own, the same in every language. `Plays` is one of the game's sequences (`InteractPlay.Sit`, `Sleep`, `Toggle`, `Chest`, ...) or a sequence's GUID.
- **A toggle.** `Toggle` gives anything two states that each use flips, as a door's; `StartsOn` places it on. The player's sequence flips it (the game's `Interaction_Toggle`). Its prompts are `On` and `Off` (game words) or `OnPrompt` and `OffPrompt` (your own); one left out is the starter's, a door's open and close.
- **What it takes.** `Item`, `Count`, `Uses` and `Time` are the game's own lock: it refuses in the player's game, which shows it locked; `Uses` takes the item, as a lockpick goes. `When`, `Unless` (player states, [player-states.md](player-states.md)) and `If` are checked by Kiln on the server, which declines the use the way the game does: the object never accepts and the player's sequence stops. `OnRefused` runs then; it isn't allowed with only the game's lock, which refuses where nothing of Kiln's runs.
- **Effects** run on the player once the game has accepted the use: a seat sat on, a bed lain in, a door used. Villagers that use furniture get nothing.
- **Beds.** The game's sleep gives the game's rest by the comfort around the player. The game's beds are no spawn point and skip no night: `SpawnPoint` makes the last such bed a player slept in the place they come back after dying (kept per player in `kiln/data/blocks/`, and dropped when the bed is gone), and `SkipsNight` brings the morning, as the admin menu's clock does, when every player in the world sleeps in a bed that says so. Both are Kiln's, on the server.
- **Hard-coded cases.** A prop whose look has no interaction of its own has nothing to start: say `Plays`, or start from a game item that has one. Kiln says so by name when the game's templates are there.
- **What a container holds when it is placed.** `DefaultLoot` is a loot table (a `lootTables` block by name, or one written in the block: [item-blocks.md](item-blocks.md)). Kiln rolls it on the server and gives the stacks into the container, once for each container: it looks through the world every second for containers of the block, and records each it fills, by template and spot (to 1/16 m), in the world's data (`WorldData`, a bundle of Kiln.Blocks'; it reaches the disk with the world save that holds the container). A restart, a mod reload, or taking the container away and placing it again on the same spot fills nothing again, and what a player took out stays out. A new spot is a new fill, so for a container players can make, each placement is a fill: give a loot container no `Recipe`, or place it from code (`Build.PlaceProp`). The game's own `DefaultInventory` isn't used: it puts its stacks in twice at placement and sets the inventory back to them at every load. What does not fit is lost; the world's Loot Amount setting does not scale the table. The log says `blocks: <mod>: container <block> ... default loot scrap x3, twigs x2`.
- **Its lid.** The game's lid is a toggle animation (`ToggleAnimation`: two states of the container's animation graph) that using the container flips. `Lid = new LidBlock { Moves = false }` takes it off: the container still opens its screen and nothing moves. `Graph`, `OpenState` and `ShutState` (GUIDs: `kiln catalog graphs` lists the game's graphs with their states) name another graph and its two states; a state left out is the game's (every game lid and door uses the same two). The graph has to be one the container's model is skinned for, which is what a game graph of another container is for: a model of your own has none yet, so a lid of its own waits for an animation of its own. The lid is a client sight.
- **A door's swing and sides.** The game's door has a front way and a back way to swing (`DirectionalInteraction`: the state it plays when used from the front, and from the back). `Opens = DoorSide.Front` makes both ways the front swing, as the game's doors that swing one way only have it; `Back` the back swing; `Both` (the wooden door's) swings away from whoever uses it. The angle is in the animation: the game's data has no angle to set. `Sides` is Kiln's: `Front` or `Back` declines a player on the other side of the door, the way Kiln's other checks decline (`OnRefused` runs), so a door that is a wall from one side. The front is the door's own +z side, turned with it (a door placed at yaw 0 has its front towards +z). The swing is a client sight.

### From code
```csharp
var box = Build.PlaceProp("my-mod.lockbox.prop", position);   // furniture's template is <mod>.<block>.prop
Barber.Wear(player, shortCrop);                                // hair or beard blocks on a character
```

### Saves
Placed furniture is a placed prop: in a base it comes back after a restart with what a container holds, outside one the world's save keeps none of it. A door comes back shut, as the game's own doors do. A container's default loot is recorded in the world's data once given, so it comes back with what is left in it; the record is kept with that world, not the mod, so a world played with the mod removed and back doesn't fill again.

Each rolled stack's progress is saved with the inventory, including a native inventory commit whose normal item callback has not run yet. The journal is staged again after the save's final simulation, before the game takes its snapshot. This needs kiln.items' result-peek function and kiln.saves' final Post staging; without either, default loot gives nothing and the log says why. Closing and reopening the same hosted world reads its saved progress again.
Pending stacks keep their item GUIDs, so an item's `Was` rename does not strand a partially filled container. Older name-only records are backfilled from the current item registry, or an unambiguous explicit `Was` alias. An unresolved or ambiguous name leaves its roll pending and is reported; Kiln does not guess another item.

### What players' games need
`clients: kiln`: new items and templates on the game's art. A model of your own, a PNG icon, or `Provides` needs `mod`. Default loot, `Sides` and the checks of `OnInteract` are the server's, and ask nothing of a player's game.

Not proven with a client: the lid and the door moving, in the swing `Opens` says and with `Lid.Graph` and its states; that a player's use of a door on its back side is declined without a stuck prompt; that a player opens the cache and sees what it was given. The lab proves the template's fields, and that the game's interaction code accepts a guest on the front side and Kiln declines one on the back.

### Mistakes
```
blocks/house.json: block door bad-count, onInteract.requires.count: 0 is outside 1..65535
blocks/house.json: block door refused-lock, onInteract.onRefused: only Kiln's checks (requires.when, unless, if) can refuse with effects: the game's own lock refuses in the player's game, where nothing runs
blocks/house.json: block container barrel, lid: it doesn't move (moves = false), so a graph and states have nothing to do: leave them out
blocks/house.json: block container cache, defaultLoot.entries: a loot table with no entries drops nothing: give it at least one
```

### Test
`mods/tests/furniture-test` runs on a dedicated server with no player: what the templates say, interactions through the game's own interaction code with a spawned character as the guest (accepted, declined by the game's lock, by the time of day and by Kiln's checks, effects once accepted, an item used up), C# and JSON the same, the errors a mistake gets, and what a restart keeps. It also runs default loot (filled once, not again after a restart, nor when placed again on the same spot, nor when a player took something out), a barrel's lid, a door's swing and sides (a guest on each side), a prop with three boxes (what its template holds and that it places), and a crafting prop (its place in the game's table, the station's mask with and without the prop near, the recipe running only with it near; the code's twin from the second start). Start it twice. It logs `furniture-test: PASS` and `FAIL` lines and `furniture-test done: N PASS, M FAIL`.

The save-boundary test stages the real WorldData bundle from the first of two item callbacks, while both container tasks are still pending, and checks the blob before requesting a game save and restarting. It calls the existing staging method through test-only reflection because `Saves.Request` queues its work for a later step; that call alone would not exercise this interval.
Another fixture queues a grant from the request-time save event: the initial stage sees an empty inventory, and the final Post stage must journal the subsequent simulation's commit before its callback. The committed manifest and a restart must preserve exactly one grant.

## Materials
Sample: `mods/samples/MaterialTweaks` (C#, and `blocks/twins.json` as data). The service under these blocks: [services/worldtables.md](../services/worldtables.md).

```csharp
// what it takes to dig a game material: the server's tables only, so players need nothing
Blocks.Define("tough-logs", new MaterialTweakBlock { Material = GameMaterials.WoodRough, Strength = new StrengthBlock { Hardness = Hardness.ModeratelyHard } });

// a strength is a part: write it once, use it on several materials
var vault = Blocks.Define("vault", new StrengthBlock { Hardness = Hardness.Unbreakable });
Blocks.Define("vault-stone", new MaterialTweakBlock { Material = GameMaterials.StoneFancy, Strength = vault });

// a new building material: an id in every world, and a block item the game's walls, floors and stairs list
Blocks.Define("moss-brick", new BuildingMaterialBlock
{
    Name = "Moss Brick",
    Strength = new StrengthBlock { Hardness = Hardness.Hard, Health = 1200, Breaks = Breaks.Stone },
    Recipe = new RecipeBlock { Makes = 20, Station = GameWorkshops.Workbench, Needs = { [GameItems.Build_TerrainMaterial_T1_Stone] = 10 } },
});
```
```jsonc
{ "format": 1,
  "strengths": { "vault": { "hardness": "unbreakable" } },
  "materialTweaks": {
    "tough-logs": { "material": "WoodRough", "strength": { "hardness": "moderatelyHard" } },
    "vault-stone": { "material": "StoneFancy", "strength": "vault" } },
  "buildingMaterials": {
    "moss-brick": { "name": "Moss Brick", "strength": { "hardness": "hard", "health": 1200, "breaks": "stone" } } } }
```

| Block | Fields |
|---|---|
| `MaterialTweakBlock` | `Material` (a `GameMaterials` name, `Cell<id>`, or the material's GUID), `Strength`, `Surface` |
| `BuildingMaterialBlock` | `Name`, `Description`, `Strength`, `Roof`, `Surface`, `Icon`, `Recipe`, `Was` |
| `StrengthBlock` | `Hardness`, `Health`, `Breaks` (`None`, `Wood`, `Stone`, `Metal`), `ItemsPerBlocks`, `Drops` (an item, or `StrengthBlock.OwnBlock`) |

A strength field left out stays what it was: the game material's own, or for a new material the game's plainest material of that `Breaks` (stone when unset).

- **How hard.** A tool or an explosion has a damage per `Hardness` category; a block costs `Health / (damage × the server's mining factor × the class modifier)`. An iron pickaxe does 100 to `Soft` and `SlightlyHard`, 95 to `ModeratelyHard`, 60 to `Hard`, 50 to `VeryHard`; a stone pickaxe 60, 50, 35, 20, 5. Rough Stone is `ModeratelyHard` 250, an Iron Block `VeryHard` 2000. A category a tool has 0 for is untouched by it: the weakest enemies' strikes have 0 against anything past `SlightlyHard`.
- **Unbreakable** takes no damage from anything, tools, explosions or enemies, and can't be dismantled. The game's TNT removes 504 of 729 cells of Rough Stone; with `Unbreakable`, none.
- **Bases aren't a shelter.** A player's own digging is held to their base permissions, but an explosion or an enemy with no player behind it digs a base as far as its numbers reach. Hardness is what protects walls.
- **When.** The game reads these tables once at start. A block in `blocks/*.json` is in them from the first start. One defined in C# is kept and is there from the next start: its handle's `Status` says `Pending`, then `Registered`. A C# block taken out of the code still applies for one more start.
- **How many.** A material is a byte in a cell, so the game has room for 49 new building materials, for every mod together. The build menu is tighter: a player's game lists at most 64 materials for the game's walls and has 61 of its own, so **3 new wall materials** fit across all mods (roofs: 50; placeable terrain: 8). A 65th stops the player's game, so Kiln refuses the material past the room by name, in sorted GUID order, before it takes an id; one a world already has walls of is kept as an absent mod's is, so they stay. Ids are refused past 49 the same way (KL330 says so before you ship; KL331 warns at 5 in one mod). An id may change between sessions: worlds follow the material's GUID, which comes from your mod id and the block's name. Rename with `Was`.
- **A mod taken away.** Each world remembers the materials it ran with. When their mod is absent, Kiln keeps them in the tables, so the walls stay, dig as before and draw the shader's fallback; enable the mod again and they're as they were. `WorldTables.Forget(guid)` lets one go. A world opened in a game without Kiln loses them: the game turns cells it doesn't know into air.
- **What digging gives back.** As the game's own blocks: the first of its recipe's `Needs`, at the recipe's ratio (Rough Stone is made 100 from 2 stone, and 50 dug give a stone back). `Drops` and `ItemsPerBlocks` say otherwise; `Drops = StrengthBlock.OwnBlock` (`"drops": "block"`) gives its own block item back, one per block. Without a recipe it gives what its starting material gives.
- **Its look.** `Surface`, below. Without one a new material looks like the game's Rough Stone.

### Surfaces
Sample: `mods/samples/SurfaceSample` (C#, and `blocks/twins.json` as data; `make_textures.py` made its PNGs). The service: [services/surfaces.md](../services/surfaces.md).

```csharp
Blocks.Define("moss-brick", new BuildingMaterialBlock
{
    Name = "Moss Brick",
    Surface = new SurfaceBlock
    {
        Side = new SurfaceLayer { Albedo = "textures/moss-brick.png", Normal = "textures/moss-brick_n.png", Roughness = "textures/moss-brick_r.png", Height = "textures/moss-brick_h.png" },
        Top = new SurfaceLayer { Albedo = "textures/moss-top.png", Tiling = 2 },
        Edge = SurfaceLayer.Of(GameMaterials.StoneAncientMoonstone),     // a game material's layer, as it is
        Bevel = 0.2f,
    },
    Strength = new StrengthBlock { Hardness = Hardness.Hard, Health = 1200, Breaks = Breaks.Stone },
});

// a game material's layer with a map of your own on top
Blocks.Define("rune-stone", new BuildingMaterialBlock
{
    Name = "Rune Stone",
    Surface = new SurfaceBlock { Side = SurfaceLayer.Of(GameMaterials.StoneRough) with { Emissive = new SurfaceEmissive { Map = "textures/runes.png", Strength = 3 } } },
});

// a retexture: the same material, new pixels on the sides
Blocks.Define("snowy-logs", new MaterialTweakBlock { Material = GameMaterials.WoodRough, Surface = new SurfaceBlock { Side = new SurfaceLayer { Albedo = "textures/snowy-logs.png" } } });
```
```jsonc
"buildingMaterials": {
  "moss-brick": { "name": "Moss Brick",
    "surface": { "side": { "albedo": "textures/moss-brick.png", "normal": "textures/moss-brick_n.png", "roughness": "textures/moss-brick_r.png", "height": "textures/moss-brick_h.png" },
                 "top": { "albedo": "textures/moss-top.png", "tiling": 2 }, "edge": { "game": "StoneAncientMoonstone" }, "bevel": 0.2 } } },
"materialTweaks": { "snowy-logs": { "material": "WoodRough", "surface": { "side": { "albedo": "textures/snowy-logs.png" } } } }
```

A surface can be written inline or defined once and named, like other parts: `var moss = Blocks.Define("moss", new SurfaceBlock { ... })` and `Surface = moss` (or `"moss"`), or a `surfaces` section in JSON and `"surface": "moss"`. A data file names surfaces of its own mod.

A surface has six layers: `Side`, `Top` and `Bottom` are the faces of a block, `Edge` and `EdgeTop` its worn edges, `Noise` a layer mixed in by noise to break up repeats. On a new material a layer left out repeats `Side` (`EdgeTop` repeats `Edge`); on a retexture it stays the game's. A layer can be another one's name: `Top = SurfacePart.Side`, `"top": "side"`.

| Layer field | Value | Default |
|---|---|---|
| `Albedo`, `Tint` | PNG, sRGB; `#rrggbb` multiplies it, or is the colour without one | required, unless the layer starts from a game material's |
| `Normal` | PNG, green up as in glTF and Blender; or with `Strength`, `YDown` for a DirectX map | flat |
| `Roughness`, `Metallic`, `Reflectance`, `Height`, `Ao` | PNG (its red channel) or a value 0 to 1 | 0.8, 0, 0.5, 0.5, 1 |
| `Emissive` | PNG, `#rrggbb`, or `Map`, `Color`, `Strength` (1), `Exposure` (0.75), `Gi` (how much it lights its surroundings, 4) | none |
| `Tiling` | repeats every 4 m | 1, as most of the game's |
| `Displacement` | `Scale`, `Offset` in metres: how far `Height` pushes the surface | 0.1, -0.05 with a height map |
| `Game`, `Layer` | start from a game material's layer: what you write replaces that map, the rest stays the game's. `SurfaceLayer.Of(material, part)` | the same part |

On the surface: `Bevel` (metres, 0.01), `EdgeCurve` (0 to 1: 1 a hard edge), `NoiseRange` (where the noise layer starts and covers all, 0.33 to 0.75), `EdgeOnlySide` (true), `Mapping` (`Default`, `Wood`; a roof is always a roof), `Decoration` (a game material whose edge decoration it shows, or `"none"`). A surface whose `Side` names a game material starts from all of that material's look, decoration included.

PNGs are in your mod's folder, a power of two on both sides, 4 to 4096; maps of one layer may differ in size. Each client cooks them at start, the first time, and keeps them in `kiln/data/surfaces/cache`. The game has room for about 2,450 new streamed textures for all mods together, materials' too; a surface past that is refused by name and its material looks like Rough Stone. A map shared by layers or materials counts once.

**Its block's icon.** A new material's block item shows a block of its surface, drawn by each client at start from the side and top albedo. `Icon` (`"icon"`) gives a PNG of your own, or `"item:<name>"` for a game item's.

A mistake names the block and the field, on every side: `block buildingMaterial moss-brick (mod x), surface.side.tint: "#12345" is not a colour: "#rrggbb"`. A wrong surface refuses its block. A PNG that is missing or the wrong size is a client's to find: the material then looks like Rough Stone, and kiln.log says why. `kiln lint` says so first ([KL333](lint.md#kl333-surface-texture), KL501, KL503), and also when a map's channels or colour space aren't what it's read as.

### Shapes
A shape of your own for every building material: it is listed beside the game's walls and floors and takes every material they take.
```csharp
string[] Row(string cells) => [cells];
Blocks.Define("arched-window", new BlueprintBlock
{
    Name = "Arched Window Wall",
    Cells = [Row("########"), Row("########"), Row("##....##"), Row("##....##"), Row("##....##"), Row("###..###"), Row("########"), Row("########")],
});
```
```jsonc
"blueprints": { "arched-window": { "name": "Arched Window Wall", "cells": [ ["########"], ["########"], ["##....##"], ["##....##"], ["##....##"], ["###..###"], ["########"], ["########"] ] } }
```
`Cells` are half a metre each, 32 at most each way: layers from the bottom up, in a layer rows from the back to the front, `#` a cell and `.` none. `Roof` lists the roof materials instead of the walls'. `Recipe`, `Was` and `Tweak` as every block.

**What a placement takes.** One block of the chosen material for every cell it fills, as with the game's own shapes: the arched window above takes 50, the game's 4 m wall 64 and its door wall 44. Cells that are already built are left as they are and cost nothing, so the number under the cursor drops where the shape overlaps a wall or a floor. `Cost` is obsolete: the game never read it.

**In a bag.** The game shows every shape item in an inventory as its Construction Hammer (name, text and the "Blueprints" line), whoever made it: its own shapes never sit in one. A shape a `Recipe` makes shows its own name in build mode.

A shape is an entry in a table every shape shares, merged at each game's start like the materials' tables, so every player needs the mod (`clients: mod`). Kiln makes its blueprint item, the table entry with its cells, and the model the client draws under the cursor (one of the item's own, so the ghost is the shape, not the wall it starts from). The game reads the table once at start: a shape from a data file is there from the first start; one defined in C# is kept and is there from the next start (`Status` says `Pending`, then `Registered`). The item's id comes from the mod and the block's name, so a placed shape's item stays the same across restarts.

### Terrain materials
Ground, rock and ore of your own. Sample: `mods/samples/TerrainSample` (C#, and `blocks/twins.json` as data; the admin commands `/glacier-vein` and `/snow-mound` put some on the ground 6 m east of you, or at `x y z` from a server console, and fail when no cell changed).

```csharp
// digs like copper ore, looks like celesthium ore, and gives celesthium: one per 12 cells dug
var ore = Blocks.Define("glacier-ore", new TerrainMaterialBlock
{
    Name = "Glacier Ore",
    Like = "Cell26",                 // copper ore: an id no item places answers to Cell<id>
    Look = "Cell115",                // celesthium ore
    Strength = new StrengthBlock { Health = 12, Drops = "Material_T7_Celesthium_Ore", ItemsPerBlocks = 12 },
});

// placed with the game's terrain shapes, slippery; digging gives it back
Blocks.Define("packed-snow", new TerrainMaterialBlock { Name = "Packed Snow", Like = "TerrainSnow", Look = "TerrainIce", Sliding = true, Placeable = true });

// a vein in the ground: cells of it, written by code
await Build.Paint(BuildShape.Sphere(at, 1.5), (byte)ore.CellId);
```
```jsonc
"terrainMaterials": {
  "glacier-ore": { "name": "Glacier Ore", "like": "Cell26", "look": "Cell115", "strength": { "health": 12, "drops": "Material_T7_Celesthium_Ore", "itemsPerBlocks": 12 } },
  "packed-snow": { "name": "Packed Snow", "like": "TerrainSnow", "look": "TerrainIce", "sliding": true, "placeable": true } }
```

| Field | What | Default |
|---|---|---|
| `Like` | the game terrain material it starts from: its strength, what digging it gives, its sounds, friction, footing for NPCs and buffs (tar's, mud's). A `GameMaterials` Terrain name or `Cell<1..127>` | `TerrainStone` |
| `Look` | the game terrain material whose look it takes: the same layers and particles | `Like` |
| `Strength` | as for building materials; a field left out is `Like`'s. A terrain cell is weak next to a block: dirt has 2..5 health, stone 5, ores 8..25 | `Like`'s |
| `Flatten`, `Sliding` | the game's flatten tool works on it; feet slide on it, as on ice | `Like`'s |
| `Placeable` | an item named `Name` that the game's terrain shapes place, and that digging gives back. Without it, it is only found and dug, as the game's ores | false |
| `Icon`, `Recipe`, `Description` | the item's, with `Placeable` | the game's stone's icon; not crafted |

All take `Was`. Ids: a terrain material is a byte in a cell, 1..127, and the game uses 116, so 11 fit for every mod together, apart from the building materials' 49, and 8 of them `Placeable` (the terrain shapes list at most 64 items, 56 of them the game's); past that one is refused by name (KL330 says so before you ship). Ids may change between sessions, as building materials' do: code reads `CellId` each session, and worlds follow the GUID. A world keeps its cells of a removed mod's terrain the same way: they stay, dig as before and come back with the mod. When the game reads its tables, how C# and data files arrive, and what players need are as for building materials: every player needs the mod (`clients: "mod"`, side `both`).

Where it appears is up to code: the world's own terrain is baked into the game's files, so new terrain doesn't grow there by itself yet. Its look is a game material's; textures of your own for terrain aren't a field yet.

### What players' games need
A `MaterialTweakBlock` changes only the server's dig, or only pictures: `clients: none`, and vanilla clients may join. A player with the mod sees a retexture; one without sees the game's own look. A `BuildingMaterialBlock` needs `clients: "mod"` and side `both`: cells reach players as ids, so every player needs the same materials. A client whose ids differ is refused at join, with the materials named; restarting with the same mods fixes it.

### Mistakes
```
worldtables: wt: blocks/walls.json: block materialTweak vault, strength.hardness: "steely" is not a hardness; there are soft, slightlyHard, moderatelyHard, hard, veryHard, unbreakable
worldtables: block materialTweak typo (mod wt), material: the game has no material "StoneRuogh"
worldtables: block buildingMaterial m49 (mod wt): refused: the game has room for 49 new building materials across all mods
worldtables: block buildingMaterial w03 (mod wt): refused: the game's build menu lists at most 64 wall materials and has 61 of its own: 3 new ones fit across all mods
worldtables: block terrainMaterial bad-like (mod wt), like: "WoodRough" is a building material: it takes a terrain material, a GameMaterials Terrain name or Cell<1..127>
```
A wrong block changes nothing; the file's other blocks load. On a game build whose tables Kiln can't read, none load and kiln.log says why; the game's own tables are untouched.

### Test
`mods/tests/worldtables-test/run.py` drives a lab server through one start per scenario (tweaks against the game's own explosions, a new material, restarts, a changed mod list, a removed mod, the join check with fake clients, the limit); `native/services/worldtables/tests/run.sh` checks ids and the table writers on the host. Surfaces: `tools/surfacecook/tests` against the game's own tables, and `mods/tests/surfaces-test/run.py` on a lab server ([services/surfaces.md](../services/surfaces.md#tests)). Terrain: `mods/tests/terrain-test/run.py` ([services/worldtables.md](../services/worldtables.md#tests)).
