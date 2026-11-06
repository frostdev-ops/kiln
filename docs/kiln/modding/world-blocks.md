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
| `PropBlock` | `Visual`, `Placement`, `Comfort`, `Light`, `OnInteract` ([interactions](#furniture-and-interactions)), `Recipe`, `Loot`, `Tweak` (its item), `TweakPlaced` (its template) |
| `StationBlock` | all of a prop's, and `Kind`, `Processing`, `Recipes`, `OwnWorkshop`, `Menu` |
| `PlacementBlock` | `On` (`Surface.Floor`, `Wall`, `Ceiling`, or several: the first is tried first), `InBase`, `Size` (metres), `Snap`, `Water`, `Fog`, `Consumed` |
| `ComfortBlock` | `Amount`, `Category` (`ComfortCategory`), `Range` (metres, 5), `Warmth` (1) |
| `ProcessingBlock` | `Slots` (1..64), `GoesOutInRain`, `Running` (`Vfx`, `Sound`) |
| `MenuBlock` | `Like`: the station whose menu it opens |

All take `Name`, `Description` and `Was`. A field left out is what the block starts from.

- **What it starts from.** A station starts from its `Kind`: `Workbench`, `Carpenter` and `Kitchen` craft at once; `Forge`, `Oven`, `Alchemy`, `DryingRack`, `CharcoalKiln`, `Kiln`, `Smelter`, `Fireplace`, `Grinder`, `Tannery` and `TableSaw` work on their own. A prop starts from its look: `Visual.Like` names a game item that places something, which brings its model, collision and how it places. With only `Visual.Model`, a plain game prop for the surface in `Placement.On` brings the rest.
- **Its look.** `Visual.Like` or `Visual.Model` (`"item:<name>"`, or the GUID of a model your `assets/assets.json` serves, cooked by `tools/modelcook`: [models.md](models.md)) sets all three places a model shows: what stands, the ghost under the cursor, and the item's picture.
- **A workshop of its own.** A station that works on its own and has `Processing` or `Recipes` gets one: it runs those recipes and no others, and the game's stations of its kind don't run them. `OwnWorkshop = false` shares its kind's instead: it runs the game's recipes too, and its own also run at the game's stations. A bench shares by default: the server doesn't check a player's craft against the bench's workshop, so one of its own would only change what its menu lists.
- **Its recipes.** `Recipes` lists what it makes, each with `Output`. An item says the same from its side with `Recipe = new RecipeBlock { Station = thisStation, ... }`. At a workshop of its own a recipe needs `Seconds`: there are no recipes of the game to take a time from. A cycle takes its inputs when it starts and gives its outputs when it ends; with nothing left it stops, and starts again by itself when given more.
- **Fuel.** `Burns` lists fuel the way `Needs` lists ingredients. It's an input, as charcoal is in the game's forge, so a station out of fuel stops like one out of anything else. A burn timer that runs on its own isn't a block yet.
- **The menu.** A station opens an existing station's menu: its kind's, or `Menu.Like`'s. Its recipes are listed there among that station's own. With a workshop of its own it doesn't run that station's recipes, which still show in the menu and say materials are missing. A menu with tabs of its own is a later block that plugs into `Menu`.
- **Comfort.** `Comfort` gives the placed thing the game's comfort, also where its starter had none, and the item's tooltip gets the Comfort row. The game's rule applies: only the best of a category counts near a player.
- **Rain and roofs.** `GoesOutInRain = false` keeps it lit in any rain. A station that works on its own never checks a roof: the game's don't either, so their recipes refuse `Sheltered`. On a bench recipe, `Sheltered` is the game's own flag.
- **Light.** A VFX on it for as long as it stands, which is where the game's lamps have their light.

### From code
```csharp
var rack = Build.PlaceProp("my-mod.herb-rack.station", position);    // a clone by name or GUID, or the item that places it
Stations.Start(rack, recipe.Id);                                     // as a player's choice in its menu
Stations.Status(rack);                                               // State (Running, MissingMaterials, ...), the recipe, when the cycle started, what it needs
```
A station's template is `<mod>.<block>.station`, a prop's `<mod>.<block>.prop`. `Build.PlaceProp` sets what dismantling gives back to the block's item.

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
`clients: kiln`: a new item, template and recipe. A model of your own, a PNG icon or a cue of your own needs `mod`.

### Test
`mods/tests/stations-test` runs on a dedicated server with no player: the exact cycle, running out, burning fuel, a workshop of its own, placing clones, comfort through the game's own system, C# and JSON the same, and the errors a mistake gets. Start it twice: the second run checks what the first one saved. It logs `stations-test: PASS` and `FAIL` lines and `stations-test done: N PASS, M FAIL`.

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

// an interaction is a part: Kiln checks the player on the server, and declines the way the game does
var pray = Blocks.Define("pray", new InteractBlock { Verb = InteractVerb.Use, Requires = new RequiresBlock { Unless = ["InCombat"] }, Effects = [new EffectBlock { Heal = 25 }] });
Blocks.Define("prayer-bench", new SeatBlock { Name = "Prayer Bench", Visual = new VisualBlock { Like = GameItems.Prop_Decoration_Comfort1_T0_Bench }, OnInteract = pray });
```
```jsonc
{ "format": 1,
  "containers": { "lockbox": { "name": "Lockbox", "slots": 8, "onInteract": { "requires": { "item": "Material_T1_Metal_Scraps" } } } },
  "beds": { "camp-bed": { "name": "Camp Bed", "visual": { "like": "Prop_Decoration_Comfort1_T0_Bed_Cot" }, "spawnPoint": true, "skipsNight": true } },
  "interactions": { "pray": { "verb": "use", "requires": { "unless": ["InCombat"] }, "effects": [ { "heal": 25 } ] } },
  "seats": { "prayer-bench": { "name": "Prayer Bench", "visual": { "like": "Prop_Decoration_Comfort1_T0_Bench" }, "onInteract": "pray" } } }
```
JSON sections: `containers`, `doors`, `beds`, `seats`, and `interactions` for parts.

| Block | Starts from | Fields of its own |
|---|---|---|
| `ContainerBlock` | the game's small chest: its lid, sounds, upgrades | `Slots` (1..120) |
| `DoorBlock` | the game's wooden door: opens both ways, paths and water follow it | |
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

### From code
```csharp
var box = Build.PlaceProp("my-mod.lockbox.prop", position);   // furniture's template is <mod>.<block>.prop
Barber.Wear(player, shortCrop);                                // hair or beard blocks on a character
```

### Saves
Placed furniture is a placed prop: in a base it comes back after a restart with what a container holds, outside one the world's save keeps none of it. A door comes back shut, as the game's own doors do.

### What players' games need
`clients: kiln`: new items and templates on the game's art. A model of your own or a PNG icon needs `mod`.

### Mistakes
```
blocks/house.json: block door bad-count, onInteract.requires.count: 0 is outside 1..65535
blocks/house.json: block door refused-lock, onInteract.onRefused: only Kiln's checks (requires.when, unless, if) can refuse with effects: the game's own lock refuses in the player's game, where nothing runs
```

### Test
`mods/tests/furniture-test` runs on a dedicated server with no player: what the templates say, interactions through the game's own interaction code with a spawned character as the guest (accepted, declined by the game's lock, by the time of day and by Kiln's checks, effects once accepted, an item used up), C# and JSON the same, the errors a mistake gets, and what a restart keeps. Start it twice. It logs `furniture-test: PASS` and `FAIL` lines and `furniture-test done: N PASS, M FAIL`.

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
- **Ids.** A material is a byte in a cell, so the game has room for 49 new building materials, for every mod together. They're given in sorted GUID order and refused past that by name (KL330 says so before you ship; KL331 warns at 5 in one mod). An id may change between sessions: worlds follow the material's GUID, which comes from your mod id and the block's name. Rename with `Was`.
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
`Cells` are half a metre each, 32 at most each way: layers from the bottom up, in a layer rows from the back to the front, `#` a cell and `.` none. `Roof` lists the roof materials instead of the walls'. `Cost` is the blocks one placement takes (default: 20 for every 64 cells, as the game's walls). `Recipe`, `Was` and `Tweak` as every block.

A shape is an entry in a table every shape shares, merged at each game's start like the materials' tables, so every player needs the mod (`clients: mod`). Kiln makes its blueprint item, the table entry with its cells, and the model the client draws under the cursor (one of the item's own, so the ghost is the shape, not the wall it starts from). The game reads the table once at start: a shape from a data file is there from the first start; one defined in C# is kept and is there from the next start (`Status` says `Pending`, then `Registered`). The item's id comes from the mod and the block's name, so a placed shape's item stays the same across restarts.

### Terrain materials
Ground, rock and ore of your own. Sample: `mods/samples/TerrainSample` (C#, and `blocks/twins.json` as data; the admin commands `/glacier-vein` and `/snow-mound` put some in front of you).

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

All take `Was`. Ids: a terrain material is a byte in a cell, 1..127, and the game uses 116, so 11 fit for every mod together, apart from the building materials' 49; past that one is refused by name (KL330 says so before you ship). Ids may change between sessions, as building materials' do: code reads `CellId` each session, and worlds follow the GUID. A world keeps its cells of a removed mod's terrain the same way: they stay, dig as before and come back with the mod. When the game reads its tables, how C# and data files arrive, and what players need are as for building materials: every player needs the mod (`clients: "mod"`, side `both`).

Where it appears is up to code: the world's own terrain is baked into the game's files, so new terrain doesn't grow there by itself yet. Its look is a game material's; textures of your own for terrain aren't a field yet.

### What players' games need
A `MaterialTweakBlock` changes only the server's dig, or only pictures: `clients: none`, and vanilla clients may join. A player with the mod sees a retexture; one without sees the game's own look. A `BuildingMaterialBlock` needs `clients: "mod"` and side `both`: cells reach players as ids, so every player needs the same materials. A client whose ids differ is refused at join, with the materials named; restarting with the same mods fixes it.

### Mistakes
```
worldtables: wt: blocks/walls.json: block materialTweak vault, strength.hardness: "steely" is not a hardness; there are soft, slightlyHard, moderatelyHard, hard, veryHard, unbreakable
worldtables: block materialTweak typo (mod wt), material: the game has no material "StoneRuogh"
worldtables: block buildingMaterial m49 (mod wt): refused: the game has room for 49 new building materials across all mods
worldtables: block terrainMaterial bad-like (mod wt), like: "WoodRough" is a building material: it takes a terrain material, a GameMaterials Terrain name or Cell<1..127>
```
A wrong block changes nothing; the file's other blocks load. On a game build whose tables Kiln can't read, none load and kiln.log says why; the game's own tables are untouched.

### Test
`mods/tests/worldtables-test/run.py` drives a lab server through one start per scenario (tweaks against the game's own explosions, a new material, restarts, a changed mod list, a removed mod, the join check with fake clients, the limit); `native/services/worldtables/tests/run.sh` checks ids and the table writers on the host. Surfaces: `tools/surfacecook/tests` against the game's own tables, and `mods/tests/surfaces-test/run.py` on a lab server ([services/surfaces.md](../services/surfaces.md#tests)). Terrain: `mods/tests/terrain-test/run.py` ([services/worldtables.md](../services/worldtables.md#tests)).
