# Blocks (Kiln.Blocks): world, part 1 of 2

## BaseAltar `0.2.6` — One flame altar of a base: its entity (this session) and its own id (the GUID the game keeps in the save with the altar; it never changes).
- `BaseAltar()`
- `BaseAltar(EntityId Entity, BaseId Id)`
- `Entity { get; init; } -> EntityId` — The altar's entity this session.
- `Id { get; init; } -> BaseId` — The altar's own id: the GUID the game keeps in the save.

## BaseId `0.2.6` — A base's id: the GUID the game gives a flame altar (its `BaseIdComponent`, kept in the save).
- `BaseId()`
- `BaseId(Guid Value)`
- `IsNone { get; } -> bool` — True for `BaseId.None`.
- `static None { get; } -> BaseId` — No base.
- `Value { get; init; } -> Guid` — The GUID.

## BaseIdChange `0.2.6` — A base whose id changed when the game rebuilt its bases: an overlapping altar came or went (`BasesChangedEvent.Changed`).
- `BaseIdChange()`
- `BaseIdChange(BaseId Old, BaseId New)`
- `New { get; init; } -> BaseId` — The base's id after the rebuild.
- `Old { get; init; } -> BaseId` — The base's id before the rebuild.

## BaseInfo `0.2.6` — One of the game's bases as its crafting code sees it: the union of overlapping flame-altar zones, with the crafting-stock containers the game lists for it (the Magic...
- `BaseInfo(BaseId Id, int Index, IReadOnlyList<BaseAltar> Altars, WorldPosition Min, WorldPosition Max, int StorageCount, int WaterCount, int ZoneCount, int StationCount)`
- `Altars { get; init; } -> IReadOnlyList<BaseAltar>` — The flame altars whose zones make the base (more than one when their zones overlap), each with its own id: keep...
- `Id { get; init; } -> BaseId` — The base's id (see `BaseId`).
- `InBox(WorldPosition p) -> bool` — Whether `p` lies in the base's box (the game also checks the altar zones inside it: `Bases.At` answers exactly).
- `Index { get; init; } -> int` — The game's record index this session (0..15): it changes when the bases are rebuilt.
- `Max { get; init; } -> WorldPosition` — The opposite corner.
- `Min { get; init; } -> WorldPosition` — The corner of the base's box with the smallest coordinates.
- `Name { get; } -> string?` — The name a player gave the base's (first) flame altar; null when none.
- `StationCount { get; init; } -> int` — Production stations standing in the base (as of the last scan).
- `StorageCount { get; init; } -> int` — Crafting-stock containers the game lists for the base now.
- `WaterCount { get; init; } -> int` — Water sources holding water.
- `ZoneCount { get; init; } -> int` — Altar zone boxes merged into the base.

## Bases `0.2.6` — The game's bases as its crafting code sees them (since 0.2.0-dev.29.14): each is the union of overlapping flame-altar zones, with the crafting-stock containers the...
- `static All { get; } -> IReadOnlyList<BaseInfo>` — Every base now, in the game's record order.
- `static At(WorldPosition position) -> BaseInfo?` — The base a position lies in (the game's own lookup); null when none.
- `static Available { get; } -> bool` — True when kiln.stations reads here (a server world, stations.read on).
- `const Capability = "stations.read" -> string` — The capability row of the reads.
- `static Chests(BaseId id) -> IReadOnlyList<EntityId>` `0.2.17` — The base's chests (since 0.2.0-dev.31, GitHub issue #138): its ordinary storage containers and its Magic chests...
- `static FindRoomFor(BaseId id, ItemId item) -> EntityId?` — A crafting-stock container of the base that takes deposits (`Bases.Storage` with deposit) for `item`: one that holds...
- `static Get(BaseId id) -> BaseInfo?` — The base with this id; null when none has it now.
- `static Of(EntityId entity) -> BaseInfo?` — The base the entity stands in (the game's own lookup by position); null when none.
- `static Stock(BaseId id) -> IReadOnlyDictionary<ItemId, long>` — What the base's crafting-stock containers hold, per item; empty when no base has this id now (as `Bases.Storage`).
- `static Stock(BaseId id, ItemId item) -> long` — How many of `item` the base's crafting-stock containers hold.
- `static StockWithStations(BaseId id) -> IReadOnlyDictionary<ItemId, long>` `0.2.7` — `Bases.Stock` plus the finished items still in the base's production stations: each station's `StationInfo.Outputs`...
- `static Storage(BaseId id, bool deposit = false) -> IReadOnlyList<EntityId>` — The base's crafting-stock containers, in the game's order (the order its fill takes from them): the Magic storage...
- `static TryGet(BaseId id, out BaseInfo? info) -> bool` — The base with this id, when one has it now.
- `static WaterSources(BaseId id) -> IReadOnlyList<EntityId>` — The base's water sources that hold water (wells, water barrels), in the game's order; empty when no base has this id...

## BasesChangedEvent `0.2.6` — The game rebuilt its bases (a flame altar placed, removed or upgraded): base indices may have changed, and a base's id changes when an overlapping altar comes or goes.
- `Added { get; } -> IReadOnlyList<BaseId>` — Bases that share no altar with a base before.
- `Changed { get; } -> IReadOnlyList<BaseIdChange>` — Bases whose id changed: old and new share at least one altar (two bases that merged both map to the new one; a base...
- `Count { get; } -> int` — The number of bases now.
- `Kept { get; } -> IReadOnlyList<BaseId>` — Bases whose id is the same before and after (their altars may have changed).
- `Removed { get; } -> IReadOnlyList<BaseId>` — Bases before that share no altar with a base now.

## BedBlock — A bed: the game's sleep and rest (a player who sleeps in it gets the game's rested status, by the comfort around them), and comfort of the bed category.
- `Lies { get; init; } -> SpotBlock?` — Where the player lies.
- `SkipsNight { get; init; } -> bool` — When every player in the world sleeps at night, in beds that say this, the morning comes.
- `SpawnPoint { get; init; } -> bool` — A player who dies comes back at the last of these beds they slept in, instead of at the flame altar.

## BlueprintBlock — A new shape on the game's building materials: in the build menu beside the walls and floors, and uses their materials unless MaterialPool selects an opt-in pool.
- `Cells { get; init; } -> IReadOnlyList<IReadOnlyList<string>>` — The shape, half a metre a cell: layers from the bottom up; in a layer, rows from the back (+z) to the front; in a...
- `Cost { get; init; } -> int?` — Not used.
- `Description { get; init; } -> string?` — The shape's description.
- `MaterialPool { get; init; } -> PartRef<BuildingMaterialPoolBlock>?` `0.2.4` — An opt-in custom pool.
- `Name { get; init; } -> string` — The shape's name, as players read it in the build menu.
- `Recipe { get; init; } -> RecipeBlock?` — How the shape is made.
- `Roof { get; init; } -> bool` — A roof shape: it cycles through the roof materials, not the walls'.
- `Tweak { get; init; } -> FieldValues` — Game fields this block does not cover, set on the blueprint item: `Tweak = { { GameFields.Items.MaxStackSize, 5 } }`.
- `Was { get; init; } -> string?` — The name this block had before a rename.

## BoxCollider — A collision box, in metres, around the model's origin as the model is drawn.
- `Center { get; init; } -> Vector3?` — Where its centre is.
- `Size { get; init; } -> Vector3` — Width, height and depth.

## Breaks — Which of a tool's class modifiers applies when it digs a material.
enum: None, Wood, Stone, Metal

## BuildingMaterialBlock — A new building material: an id and a GUID in the game's tables, its strength, and a block item the game's shapes (walls, floors, stairs, columns) can list.
- `Description { get; init; } -> string?` — The block item's description.
- `Icon { get; init; } -> string?` — The block item's picture: a PNG in the mod's folder, or `item:<name>`.
- `Name { get; init; } -> string` — The block item's name, as players read it.
- `Recipe { get; init; } -> RecipeBlock?` — How the block item is made.
- `Roof { get; init; } -> bool` — It's a roof material: the roof pieces list it, not the walls.
- `Strength { get; init; } -> PartRef<StrengthBlock>?` — What it takes to dig it.
- `Surface { get; init; } -> PartRef<SurfaceBlock>?` — How it looks.
- `Was { get; init; } -> string?` — The block's old name, after a rename: the material's id comes from it, so its walls stay in every world.

## BuildingMaterialPoolBlock `0.2.4` — A boot-time pool of 1..64 building-material items for opt-in new blueprint shapes.
- `Materials { get; init; } -> IReadOnlyList<BuildingMaterialPoolEntry>` — One to 64 distinct live building-material block items, in selector order.
- `Name { get; init; } -> string` — The declaration's readable name.
- `Roof { get; init; } -> bool` — Uses the roof equipment slot; every member and bound shape must agree.
- `Was { get; init; } -> string?` — The old key after a rename.

## BuildingMaterialPoolEntry `0.2.4` — One selector entry.
- `BuildingMaterialPoolEntry(PartRef<BuildingMaterialBlock>? Material = null, string? GameMaterial = null)`
- `GameMaterial { get; init; } -> string?` — A building material of the game, by its catalog name.
- `Material { get; init; } -> PartRef<BuildingMaterialBlock>?` — A building material of this mod by its name, or `"other-mod:name"`.

## CategoryChoice `0.2.6` — For a recipe input "any item of category X", the option chosen (the game's `RecipeInputCategorySelection`).
- `CategoryChoice()`
- `CategoryChoice(uint Category, int Index)`
- `Category { get; init; } -> uint` — The ingredient category's id.
- `Index { get; init; } -> int` — The option's index in the category (`IngredientCategory.Options`).

## ClientStationInfo `0.2.22` — One production station as the game client's own station cache has it (`ClientStations`).
- `ClientStationInfo(EntityId Station, uint Workshop, uint SelectedRecipe, BaseId Base, WorldPosition Position, StationState State, StationNeeds Needs, IReadOnlyList<CategoryChoice> SelectedChoices, bool UseAllOptions, int CraftsLoaded, int Water, int WaterMax, bool NeedsEnergy, TimeSpan? CraftDuration, TimeSpan? CraftRemaining, TimeSpan? Remaining, float? Progress, IReadOnlyList<StationStack>? Outputs, ulong Step)`
- `Base { get; init; } -> BaseId` — The base the client grouped it under.
- `CraftDuration { get; init; } -> TimeSpan?` — One craft of the selected recipe on this client (the recipe's time and the client's copy of the production speed...
- `CraftRemaining { get; init; } -> TimeSpan?` — What is left of the cycle in progress, at least zero; null unless `ClientStationInfo.State` is Running and the time...
- `CraftsLoaded { get; init; } -> int` — The complete crafts its inputs still hold after the cycle in progress (the server's count).
- `DisplayName { get; } -> string?` — The workshop's name in the player's language ("Charcoal Kiln"), from the game's data (`Stations.WorkshopName`); null...
- `IsRunning { get; } -> bool` — A cycle is running (`ClientStationInfo.State` is Running).
- `Needs { get; init; } -> StationNeeds` — What it lacks (the game's missing requirements).
- `NeedsEnergy { get; init; } -> bool` — It runs on rotational energy (it needs a power source in range).
- `Outputs { get; init; } -> IReadOnlyList<StationStack>?` — Finished stacks of the selected recipe's outputs (an item that is no input of it) in the client world's copy of the...
- `Position { get; init; } -> WorldPosition` — Where it stands, in 0.25 m steps (as sent).
- `Progress { get; init; } -> float?` — 0..1 of the cycle in progress; null as `CraftRemaining`.
- `RecipeName { get; } -> string?` — The selected recipe's name (`Stations.RecipeName`); null without a recipe.
- `Remaining { get; init; } -> TimeSpan?` — Until every loaded craft is done: the time the game's HUD shows ("finishes in"), at least zero; null as...
- `SelectedChoices { get; init; } -> IReadOnlyList<CategoryChoice>` — The category choices of the selected recipe ("any wood": which wood).
- `SelectedRecipe { get; init; } -> uint` — The recipe it repeats; 0 when none is chosen.
- `State { get; init; } -> StationState` — Its state as the server last sent it.
- `Station { get; init; } -> EntityId` — The station (the server's entity id; the client uses the same ids).
- `Step { get; init; } -> ulong` — The client world's step of the copy.
- `UseAllOptions { get; init; } -> bool` — "Use all ingredient options" is on.
- `Water { get; init; } -> int` — Its water, in the game's Fuel units (64 per charge); 0 without water.
- `WaterMax { get; init; } -> int` — Its water capacity, in Fuel units.
- `Workshop { get; init; } -> uint` — Its workshop (`Crafting.workshopId`): which recipes it runs.

## ClientStations `0.2.22` — The production stations (kilns, smelters, looms, seed beds: every station with the game's `FactoryStation`) as the game client itself knows them, read-only: for a...
- `static All() -> IReadOnlyList<ClientStationInfo>` — Every station in the client's cache, in the cache's order (grouped by base).
- `static Available { get; } -> bool` — The view works here now: a game client whose build qualifies the station cache, nothing faulted.
- `const Capability = "stations.client_view" -> string` — The capability row: the client's station cache can be read here (sigdb feature, since 0.2.0-dev.31.13, mod API 0.2.22).
- `static Near(WorldPosition center, double radius = 16) -> IReadOnlyList<ClientStationInfo>` — The stations of the cache within `radius` metres of `center` (your player's or the camera's position), nearest first.
- `static Open { get; } -> ClientStationInfo?` — The station whose screen the local player has open (the game's production-station screen, the entity of...
- `static Read(EntityId station) -> ClientStationInfo?` — One station of the cache; null when the client's cache does not have it (not in a known base, not sent, not copied yet).
- `static Status { get; } -> ClientStationsStatus?` — What works and the copy's age; null when kiln.stations.client is not here.

## ClientStationsStatus `0.2.22` — What `ClientStations` works with now.
- `ClientStationsStatus()`
- `ClientStationsStatus(bool On, bool World, bool Copying, int Rows, ulong Step, TimeSpan? Age, string Detail)`
- `Age { get; init; } -> TimeSpan?` — Since the copy was taken; null when there is none.
- `Copying { get; init; } -> bool` — A read in the last 5 seconds keeps the copying on.
- `Detail { get; init; } -> string` — Why it is off, in words; while it is on and has no copy yet, why none was taken (the cache not in the client world...
- `On { get; init; } -> bool` — The client's station cache can be read on this build and nothing faulted.
- `Rows { get; init; } -> int` — Stations in the copy.
- `Step { get; init; } -> ulong` — The client world's step of the copy (0: none yet).
- `World { get; init; } -> bool` — A client world runs now (in game, not in the menu).

## CollectWhat `0.2.6` — What `Stations.Collect` moves.
enum: Outputs, Leftovers, All

## ComfortBlock — What the game's comfort counts, for whoever rests within range.
- `Amount { get; init; } -> int` — How much comfort it counts, a whole number.
- `Category { get; init; } -> ComfortCategory` — Only the best of a category counts near a player, as the game's rule goes.
- `Range { get; init; } -> double` — Metres.
- `Warmth { get; init; } -> int` — Resistance to the cold near it.

## ComfortCategory — The game's comfort categories.
enum: Hearth, Bed, Chair, Table, Bench, Throne, Bathtub, Sink, Toilet, Shower, Carpet, Illumination, Flame, Trophy, Stool, Kitchen, Wellness

## ContainerBlock — Something that holds items, as the game's chests do.
- `DefaultLoot { get; init; } -> PartRef<LootTableBlock>?` — What it holds when it is placed: a `LootTableBlock` of this mod by name (`"other-mod:name"` for another's), or one...
- `Lid { get; init; } -> LidBlock?` — How its lid moves.
- `Slots { get; init; } -> int?` — Inventory slots, 1..120.

## Crafting `0.2.15` — Player crafting sources for station, NPC and inventory crafting.
- `static AddSource(EntityId player, EntityId container, CraftSourceOptions? options = null) -> CraftSourceHandle` — Registers a container for this player, in any base or none.
- `const BuildSourcesCapability = "crafting.build_sources" -> string` `0.2.21` — Block placements count and pay from links registered with `CraftSourceOptions.Building`: on a server or host the...
- `const LockSourcesCapability = "items.lock_sources" -> string` `0.2.21` — Locks count and pay their required item from links registered with `CraftSourceOptions.Locks`: on a server or host...
- `const RespecSourcesCapability = "crafting.respec_sources" -> string` `0.2.25` — The skill respec (a node's unlearn, the Flame Altar's reset) counts and pays its respec item from links registered...
- `const SourcesCapability = "crafting.sources" -> string` — Server payment through the recipe transaction, with player-bound linked storage.
- `const SourceUiCapability = "crafting.source_ui" -> string` — A Kiln client's crafting screens count the server's linked-storage snapshot.

## CraftSourceFilter `0.2.15` — How `CraftSourceOptions.Items` is interpreted.
enum: Deny, Allow

## CraftSourceHandle `0.2.15` — A session-only player crafting source.
- `Container { get; } -> EntityId` — The registered storage container, independent of its base.
- `Dispose() -> void` — Removes this registration, on any thread.
- `IsDisposed { get; } -> bool` — Whether this handle has been disposed.
- `Player { get; } -> EntityId` — The only player this registration can supply.

## CraftSourceOptions `0.2.15` — Options copied when a linked crafting source is registered.
- `Building { get; init; } -> bool` `0.2.21` — Building too (default false): the build cursor's material check and the Construction Hammer's blueprint placements...
- `Fills { get; init; } -> bool` `0.2.21` — Production-station fills too (default false): the station screen's "+1" and "+10" (the game's "fill from chests"...
- `Filter { get; init; } -> CraftSourceFilter` — Allow-list or deny-list interpretation of `CraftSourceOptions.Items`; default deny.
- `Items { get; init; } -> IReadOnlyCollection<ItemId>?` — The item set, copied eagerly on registration; null means an empty set.
- `Locks { get; init; } -> bool` `0.2.21` — Locks too (default false): a lock that requires an item (the game's InteractionLock: a lockpick, a key) also counts...
- `Order { get; init; } -> CraftSourceOrder` — After the bag/base stock, before or after opted-in station outputs.
- `Respec { get; init; } -> bool` `0.2.25` — The skill respec too (default false): a skill node's unlearn and the Flame Altar's skill reset pay their respec item...

## CraftSourceOrder `0.2.15` — Where linked storage pays, after the bag and the crafting base's stock.
enum: BeforeStationOutputs, AfterStationOutputs

## DayPart — Day (the game's day phase, from 06:00 by default) or night.
enum: Day, Night

## DoorBlock — A door: it opens and closes for whoever uses it, and paths and water follow it, as the game's doors do.
- `Opens { get; init; } -> DoorSide?` — Which way it swings.
- `Sides { get; init; } -> DoorSide?` — Which side a player can use it from.

## DoorSide — A door's side: its front, its back, or both.
enum: Both, Front, Back

## FilledContainers — The containers of a world that were given their default loot: its template and spot.
- `Filled { get; set; } -> Dictionary<string, long>` — "template/x/y/z" (the position in 1/16 m cells) -> the step counter's time, for the record only.
- `Pending { get; set; } -> Dictionary<string, FilledContainers.FillProgress>` — A roll in progress.

## FilledContainers.FillProgress — A fill in progress for one container: the stacks a loot table rolled and how far giving them has got.
- `ItemGuids { get; set; } -> Dictionary<string, Guid>` — Canonical item identities captured before a grant or save.
- `Lost { get; set; } -> int` — How many items did not fit in the container.
- `Next { get; set; } -> int` — The index in `FillProgress.Stacks` of the next stack to give.
- `Stacks { get; set; } -> List<LootStack>` — The stacks the loot table rolled for this container.

## FillInput `0.2.6` — One input of a fill plan: what one craft needs, what the station holds, what the base's stock can give (the Magic chests after the reserves, and the named player's bag).
- `FillInput()`
- `FillInput(ItemId Item, int PerCraft, int Have, long Stock)`
- `Chests { get; init; } -> long` `0.2.9` — What the named ordinary chests hold of it, taken after `FillInput.Stock` and before `FillInput.Outputs`.
- `Have { get; init; } -> int` — How many the station holds.
- `Item { get; init; } -> ItemId` — The item.
- `Linked { get; init; } -> long` `0.2.21` — What the named player's linked sources registered with `CraftSourceOptions.Fills` hold of it (with...
- `Outputs { get; init; } -> long` `0.2.9` — What opted-in stations of the base lend of it (their finished items), taken after `FillInput.Stock` and...
- `PerCraft { get; init; } -> int` — How many one craft needs.
- `Stock { get; init; } -> long` — How many the base's stock can give: the Magic chests after the reserves, and the named player's bag.

## FillLimit `0.2.6` — What kept a fill below what was asked.
enum: None, Stock, Reserve, Room, Water

## FillMode `0.2.6` — How `FillOptions.Crafts` counts.
enum: UpTo, Add

## FillOptions `0.2.6` — Options of `Stations.Fill`.
- `Chests { get; init; } -> IReadOnlyList<EntityId>?` `0.2.9` — Ordinary chests in the station's base to take from after the Magic chests and before opted-in station outputs.
- `Choices { get; init; } -> IReadOnlyList<CategoryChoice>?` — The category choices for `FillOptions.Recipe`; null: the station's own.
- `Crafts { get; init; } -> int` — Crafts to fill for, 1..1000 (see `FillOptions.Mode`).
- `DryRun { get; init; } -> bool` — Run the game's function and roll back: the result says what would move.
- `Exact { get; init; } -> bool` — All of it or nothing; default false: as many crafts as the base's stock and the station's room cover.
- `Keep { get; init; } -> IReadOnlyDictionary<ItemId, long>?` — Reserves: at least this many of each item stay in the base's stock containers (up to 64 items).
- `Mode { get; init; } -> FillMode` — `FillMode.UpTo` (default) or `FillMode.Add`.
- `Player { get; init; } -> EntityId` — Also take from this player's inventories first, as the game's own button does.
- `Recipe { get; init; } -> uint?` — The recipe to fill for; null: the station's selected recipe.
- `UseLinkedSources { get; init; } -> bool` `0.2.21` — Also take from `FillOptions.Player`'s linked crafting sources registered with `CraftSourceOptions.Fills`, in any...
- `UseStationOutputs { get; init; } -> bool` `0.2.9` — Also take, last, from the finished items of the base's stations that are opted in as crafting stock...
- `Water { get; init; } -> bool` — Pour the recipe's water from the base's water sources (default true).

## FillPlan `0.2.6` — What a fill would do now (`Stations.Plan`).
- `FillPlan(StationActionStatus Status, EntityId Station, uint Recipe, int CraftsLoaded, int CraftsAffordable, int CraftsPlanned, IReadOnlyList<FillInput> Inputs, string Message)`
- `CraftsAffordable { get; init; } -> int` — More crafts the base's stock covers (after the reserves; with the opted-in stations' outputs, since 0.2.0-dev.29.17).
- `CraftsLoaded { get; init; } -> int` — Complete crafts the station holds now.
- `CraftsPlanned { get; init; } -> int` — More crafts a fill with these options would ask the game for.
- `Inputs { get; init; } -> IReadOnlyList<FillInput>` — The resolved inputs (category choices applied).
- `LimitedBy { get; init; } -> FillLimit` — What keeps the fill below what was asked.
- `Message { get; init; } -> string` — Why, when nothing (or less than asked) would go in.
- `Ok { get; } -> bool` — True when something went in: `FillPlan.Status` is Done or Partial.
- `Reason { get; } -> string?` — Why nothing (or less than asked) would go in: the message, else the status; null when `FillPlan.Ok`.
- `Recipe { get; init; } -> uint` — The recipe.
- `ShortItem { get; init; } -> ItemId?` — The item short (`FillLimit.Stock`, `FillLimit.Reserve`); null otherwise.
- `Station { get; init; } -> EntityId` — The station.
- `Status { get; init; } -> StationActionStatus` — Done when `FillPlan.CraftsPlanned` crafts would go in; Partial when fewer than asked; otherwise why none would.
- `UseAllOptions { get; init; } -> bool` — The station has "use all ingredient options" on: its cycles may consume other options of a category than the one a...
- `WaterHave { get; init; } -> int` — Water in the station now (units).
- `WaterPerCraft { get; init; } -> int` — Water one craft needs (Fuel units, 64 a charge); 0 when the recipe needs none or the options pour none.
- `WaterStock { get; init; } -> int` — Water in the base's water sources (units).

## Hardness — How hard a material is: which column of a tool's or an explosion's damage applies to it.
enum: Soft, SlightlyHard, ModeratelyHard, Hard, VeryHard, Unbreakable

## IngredientCategory `0.2.6` — An ingredient category of the game's recipes ("any wood"): its options in the game's order.
- `IngredientCategory(uint Id, IReadOnlyList<IngredientOption> Options)`
- `Id { get; init; } -> uint` — The category's id, as `CategoryChoice.Category` names it.
- `Options { get; init; } -> IReadOnlyList<IngredientOption>` — Its options, in the game's order.

## IngredientOption `0.2.6` — One option of an ingredient category: an item, and how many of it count as one (the game's scaling: a craft needs `ceil(count x Scaling)` of it).
- `IngredientOption()`
- `IngredientOption(ItemId Item, float Scaling)`
- `Item { get; init; } -> ItemId` — The item.
- `Scaling { get; init; } -> float` — How many of it count as one: a craft needs `ceil(count x Scaling)` of it.

## InteractBlock — What happens when a player uses a placed thing.
- `Effects { get; init; } -> IReadOnlyList<EffectRef>?` — What happens to the player once the game has accepted the use.
- `OnRefused { get; init; } -> IReadOnlyList<EffectRef>?` — What happens to the player when `InteractBlock.Requires` refuses them.
- `Plays { get; init; } -> InteractPlay?` — What the player plays when they use it: one of the game's (`InteractPlay.Sit`, `InteractPlay.Sleep`, ...), or a...
- `Prompt { get; init; } -> string?` — A prompt of your own instead: the same text in every language.
- `Requires { get; init; } -> RequiresBlock?` — What it takes to use it.
- `Toggle { get; init; } -> ToggleBlock?` — Two states, and each use flips it: the game's toggle, as a door's.
- `Upgrade { get; init; } -> UpgradeAction?` `0.2.12` — The use raises an item's rarity by one step of a rarity upgrade rule (`RarityUpgradeBlock`), on the server once the...
- `Verb { get; init; } -> InteractVerb?` — The prompt's word: one of the game's (`InteractVerb.Open`, `InteractVerb.Sit`, ...), so every language has it.

## InteractPlay — What a player plays when they use something: one of the game's sequences, or any sequence by GUID (`new InteractPlay("mine", guid)`; in JSON the GUID).
- `InteractPlay(string Key, Guid Sequence)`
- `static readonly Chest -> InteractPlay` — Opens a chest and its inventory screen: `Interaction_InventoryUi_StorageChest`.
- `static readonly Drink -> InteractPlay` — `Interaction_Drink_from_Container`, a well's.
- `Id { get; } -> uint` — The game's id of the sequence: FNV-1a of its GUID.
- `static readonly Inventory -> InteractPlay` — Opens the inventory screen: `Interaction_InventoryUi`.
- `Key { get; init; } -> string` — The name JSON uses for it, such as `"sit"`.
- `static readonly Read -> InteractPlay` — `Interaction_Read_Lore`.
- `Sequence { get; init; } -> Guid` — The GUID of the sequence that plays.
- `static readonly Sit -> InteractPlay` — Sits down on the spot the seat says: `Interaction_Sit`.
- `static readonly Sleep -> InteractPlay` — Lies down and sleeps, the game's rest: `Interaction_Sleep`.
- `static readonly Talk -> InteractPlay` — `Interaction_Talk`.
- `static readonly Toggle -> InteractPlay` — A hand on it: `Interaction_Toggle`, a door's.

## InteractVerb — A prompt's word: the game's own, so every language has it.
- `InteractVerb(string Key, uint Id, string? Reference = null)`
- `static readonly Activate -> InteractVerb` — The game's `activate` prompt.
- `static readonly Close -> InteractVerb` — The game's `close` prompt.
- `static readonly Collect -> InteractVerb` — The game's `collect` prompt.
- `static readonly Craft -> InteractVerb` — The game's `craft` prompt.
- `static readonly Deactivate -> InteractVerb` — The game's `deactivate` prompt.
- `static readonly Drink -> InteractVerb` — The game's `drink` prompt.
- `static readonly Flip -> InteractVerb` — The game's `flip` prompt.
- `static readonly Harvest -> InteractVerb` — The game's `harvest` prompt.
- `Id { get; init; } -> uint` — The game's loca tag.
- `static readonly Inspect -> InteractVerb` — The game's `inspect` prompt.
- `Key { get; init; } -> string` — The name JSON uses for it, such as `"open"`.
- `static readonly Open -> InteractVerb` — The game's `open` prompt.
- `static readonly Read -> InteractVerb` — The game's `read` prompt.
- `Reference { get; init; } -> string?` — The game's GUID of the word, or null where the game keeps none.
- `static readonly Search -> InteractVerb` — The game's `search` prompt.
- `static readonly Sit -> InteractVerb` — The game's `sit` prompt.
- `static readonly Sleep -> InteractVerb` — The game's `sleep` prompt.
- `static readonly Talk -> InteractVerb` — The game's `talk` prompt.
- `static readonly Use -> InteractVerb` — The game's `use` prompt.

## LidBlock — A container's lid: the game's toggle animation, which the container's use flips.
- `Graph { get; init; } -> Guid?` — The animation graph that moves it, by GUID: a game graph (`kiln catalog graphs` lists them, with their states) or...
- `Moves { get; init; } -> bool` — False: nothing moves (a barrel, a pile of sacks, a model of yours with no lid): the toggle animation is taken off.
- `static readonly Open -> Guid` — The state the game's lids and doors play while open.
- `OpenState { get; init; } -> Guid?` — The graph's state while the lid is open, by GUID.
- `static readonly Shut -> Guid` — The state the game's lids and doors play while shut.
- `ShutState { get; init; } -> Guid?` — The graph's state while the lid is shut, by GUID.

## MaterialTableChangedEvent `0.2.4` — A committed material-table hash change, or a deferred pool definition.
- `Deferred { get; } -> bool` — A next-start definition changed; no live table capacity was consumed.
- `Groups { get; } -> IReadOnlyList<Guid>` — Affected pool GUIDs, copied at observation.
- `Materials { get; } -> IReadOnlyList<Guid>` — Affected material GUIDs, copied at observation.
- `NewHash { get; } -> string` — The committed table hash; unchanged for a deferred definition.
- `OldHash { get; } -> string` — The previously observed table hash.

## MaterialTweakBlock — Changes a game material in place: `Material = GameMaterials.StoneRough, Strength = new() { Hardness = Hardness.Unbreakable }`.
- `Material { get; init; } -> string` — The game material: a `GameMaterials` name, `Cell<id>`, or the material's GUID.
- `Strength { get; init; } -> PartRef<StrengthBlock>?` — What it takes to dig it.
- `Surface { get; init; } -> PartRef<SurfaceBlock>?` — New pictures for it: a layer written here replaces the game's, the rest stays.

## MenuBlock — The crafting menu a station opens.
- `Like { get; init; } -> StationKind?` — The station whose menu it opens.
- `Tabs { get; init; } -> IReadOnlyList<StationMenuTabBlock>?` `0.2.17` — The menu in tabs of its own: each tab a name, the icon of a tab of the menu it is `MenuBlock.Like`, and sets of...

## MenuDefinitionResult `0.2.17` — What `StationMenus.Define` answered.
- `Generation { get; init; } -> ulong` — The generation of this game's station menus after the call.
- `Ok { get; init; } -> bool` — The layout is stored and shows on the station's menu.
- `Reason { get; init; } -> string` — Why not, a word first: `PendingContent` (a recipe it lists is not in the game yet: try again after the next content...
- `Registration { get; init; } -> StationMenuRegistration?` — The layout, to take it off again.

## PlacementBlock — Where and how a player can place something.
- `Consumed { get; init; } -> bool?` — Placing it takes the item from the stack.
- `Fog { get; init; } -> bool?` — It can be placed below the Shroud's line.
- `InBase { get; init; } -> bool?` — Only inside a base, as the game's furniture.
- `On { get; init; } -> Surface?` — The surfaces it can be placed on: floor, wall, ceiling, or several.
- `Size { get; init; } -> Vector3?` — Metres, width, height and depth: the box that has to be free to place it, standing on its floor.
- `Snap { get; init; } -> bool?` — The cursor starts out snapping to the voxel grid.
- `Water { get; init; } -> bool?` — It can stand in water.

## ProcessingBlock — How a station works on its own.
- `GoesOutInRain { get; init; } -> bool?` — Rain puts it out.
- `Running { get; init; } -> RunningBlock?` — What shows while it runs.
- `Slots { get; init; } -> int?` — Inventory slots, inputs and outputs in one.

## PropBlock — Something placed that stands there: furniture, a banner, a lamp.
- `PropBlock(PropBlock original)`
- `Collider { get; init; } -> BoxCollider?` — What a player bumps into and what hits stop at: one box.
- `Colliders { get; init; } -> IReadOnlyList<BoxCollider>?` — Several boxes in place of one: a table's top and its legs, a doorway's two posts.
- `Comfort { get; init; } -> PartRef<ComfortBlock>?` — The comfort it gives to whoever rests within range, as a block or the name of one defined once.
- `Description { get; init; } -> string?` — The description of the item that places it.
- `Light { get; init; } -> VfxRef?` — A VFX on it for as long as it stands, which is where the game's lamps have their light.
- `Loot { get; init; } -> LootBlock?` — Where else the item that places it is found: chests, world pickups and drop tables.
- `Name { get; init; } -> string` — The name of the item that places it, as players read it.
- `OnInteract { get; init; } -> PartRef<InteractBlock>?` — What happens when a player uses it: the prompt, what they play, a toggle, what it takes, effects.
- `Placement { get; init; } -> PartRef<PlacementBlock>?` — Where and how a player can place it, as a block or the name of one defined once.
- `Provides { get; init; } -> bool` — A recipe elsewhere can ask for it nearby: `NeedsProps = ["cold-forge"]` in a `RecipeBlock`.
- `Recipe { get; init; } -> RecipeBlock?` — How the item that places it is made.
- `Tweak { get; init; } -> FieldValues` — Game fields this block does not cover, set on the item it makes: `Tweak = { { GameFields.Items.MaxStackSize, 5 } }`.
- `TweakPlaced { get; init; } -> FieldValues` — The same for the template it places: `TweakPlaced = { { GameFields.Templates.Shelter.CheckRange, 20 } }`.
- `Visual { get; init; } -> PartRef<VisualBlock>?` — What it looks like, as a block or the name of one defined once.
- `Was { get; init; } -> string?` — The name this block had before a rename.
