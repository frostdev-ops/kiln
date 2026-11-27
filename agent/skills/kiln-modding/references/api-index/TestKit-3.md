# TestKit (Kiln.TestKit): part 3 of 3

## TestKit.HarnessStorageRecord `0.2.16` — One record of a player storage in a test: a stack in a slot.
- `HarnessStorageRecord()`
- `HarnessStorageRecord(int Slot, ItemId Item, int Count, int Level = 0)`
- `Count { get; init; } -> int` — How many.
- `Deconstruct(out int Slot, out ItemId Item, out int Count, out int Level) -> void`
- `Equals(TestKit.HarnessStorageRecord other) -> bool`
- `Item { get; init; } -> ItemId` — The item.
- `Level { get; init; } -> int` — Gear's level (0 for a plain stack).
- `static operator !=(TestKit.HarnessStorageRecord left, TestKit.HarnessStorageRecord right) -> bool`
- `static operator ==(TestKit.HarnessStorageRecord left, TestKit.HarnessStorageRecord right) -> bool`
- `Slot { get; init; } -> int` — The slot in the storage (0 to slots - 1).

## TestKit.HarnessTaming `0.2.26` — Kiln.Taming offline: the players in game, the creatures they tame and what their inventories hold.
- `Count(PlayerRef player, ItemId item) -> int` — How many of `item` the player holds now.
- `Give(PlayerRef player, ItemId item, int count) -> ItemSelection` — Puts `count` of `item` (a plain stack) in a slot of the player's own and returns its selection, as `Taming.Choices`...
- `Hurt(EntityRef creature, double healthFraction) -> void` — The creature's health is now `healthFraction` of its maximum (a weaken step reads it).
- `Join(CharacterKey character, WorldPosition at = default(WorldPosition)) -> PlayerRef` — The player is in game at `at` (a character joined to `HarnessPets` too is the same player there).
- `Leave(PlayerRef player) -> void` — The player left: their attempts fail OwnerLeft at the next step.
- `NativeStep(EntityRef creature) -> void` — The game counted one native step on the creature (a petting while it ate its bait).
- `Place(string creature, WorldPosition at, double healthFraction = 1, bool nativeTameable = false) -> EntityRef` — A creature of `creature` (a creatures registry id or a creature block's) at `at`; `nativeTameable`: a tameable wild...
- `Remove(EntityRef creature) -> void` — The creature is gone (its attempt fails BodyGone).

## TestKit.HarnessTasks `0.2.10` — The Kiln tasks of a harness's mods (`KilnHarness.Tasks`).
- `Live { get; } -> int` — The tasks that have not ended yet (and ended ones with a hop still to run).
- `Settle(int maxSteps = 1000, TimeSpan? timeout = null) -> int` — Runs the mods' tasks to their end: waits while their work runs on the workers, and steps the server whenever all of...

## TestKit.HarnessTerrain `0.2.3` — The fake world of a harness behind Kiln.Terrain (`KilnHarness.Terrain`): `Terrain.Inspect`, `Apply`, `Forget` and `Applied` run their own code on it.
- `AddBase(CellBox box) -> void` — A player base: overlays leave blocks that touch `box` alone unless bases are allowed.
- `Editing { get; set; } -> Action<CellBox>?` — Runs inside every write of an overlay where kiln.build posts its `BuildEditingEvent`: after the overlay read the...
- `Get(CellPos at) -> Voxel` — The cell at `at`.
- `GroundY { get; set; } -> int` — Cells below this height are solid ground, cells from it up are air, where nothing was set.
- `Loaded { get; set; } -> bool` — Whether the voxel tiles are there.
- `LoseLedger(string mod, bool wholeWorldData = false) -> bool` — Loses `mod`'s terrain ledger in this world, as when the world's Kiln data or the ledger key is lost; the cells and...
- `Restart() -> void` — The server saves the world and starts again (`HarnessTerrain.SaveWorld`, then the next start).
- `Revision { get; } -> uint` — The world file's revision; `HarnessTerrain.SaveWorld` adds one.
- `SaveWorld() -> void` — The game writes the world file: the ledgers as they are now are in that save (a record made before it and lost after...
- `Set(CellPos at, Voxel cell) -> void` — Sets one cell, as a player's edit would (no ledger knows of it).
- `Witnessed(string mod) -> IReadOnlyList<string>` — The plans Kiln's witness has for `mod` in this world, sorted.
- `WorldId { get; set; } -> string` — The world's id: a ledger belongs to one world.
- `Writes { get; } -> IReadOnlyList<TestKit.FakeTerrainWrite>` — Every write overlays made, in order.

## TestKit.HarnessTime `0.2.2` — The fake kiln.time of a harness (`KilnHarness.Time`): Kiln.Time's own code (`Time.OfDay`, `State`, `IsDay`, `Set`, `SetSpeed`, `SetCycle`, `TimeSetEvent`...
- `Calls { get; } -> IReadOnlyList<TestKit.FakeTimeCall>` — Every change to the clock a mod made, in order.
- `DayLength { get; set; } -> TimeSpan` — How long the day phase lasts in real time (the server's GameSettings dayTimeDuration, 2 to 60 minutes; default 30).
- `Hours { get; } -> double` — The time of day now, 0..24.
- `IsDay { get; } -> bool` — Whether it is day (06:00 to 18:00).
- `NightLength { get; set; } -> TimeSpan` — How long the night phase lasts in real time (nightTimeDuration, 2 to 60 minutes; default 12).
- `Set(double hours) -> void` — Sets the time of day as the game's own admin menu does (setTimeOfDay: the clock restarts in its first cycle), with...
- `Speed { get; } -> double` — The speed the clock runs at (1: the game's; what a mod's `Time.SetSpeed` holds).

## TestKit.HarnessTombstoneMode `0.2.16` — The game's tombstone setting at a death (`HarnessPlayerStorage.Die`).
enum: AddBackpackMaterials, Everything, NoTombstone

## TestKit.HarnessUiChest `0.2.17` — Models registrations and callbacks, not the game's row renderer.
- `HarnessUiChest(TestKit.KilnHarness harness)`
- `Buttons { get; } -> IReadOnlyList<string>` — Captions shown for the open storage chest, in registration order.
- `Press(int index) -> bool` — Activates one visible button by its zero-based registration order.

## TestKit.HarnessUiInventory `0.2.12` — The fake kiln.ui.inventory of a harness (`KilnHarness.UiInventory`): Ui.Inventory's own code runs on it.
- `Click(int index, SlotButton button = SlotButton.Left, SlotModifiers modifiers = SlotModifiers.None) -> TestKit.FakeSlotClickResult` — The player presses `button` with `modifiers` on drawn slot `index`.
- `ClickSubscriptions { get; } -> int` — OnClick subscriptions held now.
- `CloseScreen() -> void` — Every menu closed: the play view (no slots drawn).
- `Draw(HudScreen screen, params TestKit.FakeInventorySlot[] slots) -> void` — The game draws `slots` on `screen` (the latest UI frame from now on; the hovered slot is cleared).
- `Drawn { get; } -> IReadOnlyList<TestKit.FakeInventorySlot>` — The slots of the latest `HarnessUiInventory.Draw` (empty before the first).
- `Hover(int? index) -> void` — The cursor rests on drawn slot `index` (null: on none): what Ui.Inventory.Hovered reads.
- `MarkOf(int index) -> SlotMark?` — The mark drawn slot `index` would show (null: none): an item mark of any mod first, then the MarkWhen rules in...
- `OpenBackpack(params TestKit.FakeInventorySlot[] slots) -> void` — The player opens the backpack with these slots drawn (the backpack screen, HUD Backpack).
- `OpenChest(EntityId chest, params TestKit.FakeInventorySlot[] slots) -> void` — The player opens the storage chest `chest` (its entity: what InventoryScreen.Container and GameScreen.Target carry)...
- `PanelsShown { get; } -> IReadOnlyList<string>` — The titles of the Ui.Inventory.AddPanel panels that show on the open screen now (their Screens and When, in the...
- `PressPad(string chord) -> TestKit.FakeSlotClickResult` `0.2.14` — The player presses a controller chord (`"Pad:LS"`, `"Pad:LB+A"`) while the game's selection rests on a drawn slot...
- `Screen { get; } -> InventoryScreen?` — The backpack or chest screen open now (what Ui.Inventory.Screen says in a game), or null.
- `Select(int? index) -> void` `0.2.14` — The game's own selection (a controller's: what Ui.Inventory.Selected reads, since 0.2.0-dev.30.5) rests on drawn...

## TestKit.HarnessUiItemDetail `0.2.14` — Explicit game-panel captures for Ui.Values.ItemDetail.
- `Capture(ItemId item, EntityId entity, int level, ItemRarity? rarity, params UiItemDetailLine[] lines) -> void` — Records a completed panel (owned copy).
- `Clear() -> void` — Forgets every recorded panel, as on a client world change.

## TestKit.HarnessUiNativeTints `0.2.17` — Evaluates Ui.Inventory.TintWhen without drawing.
- `HarnessUiNativeTints(TestKit.KilnHarness harness)`
- `TintOf(TestKit.FakeInventorySlot slot, Color background) -> Color` — Runs the rules over this copied slot (including plain stacks or empty slots).

## TestKit.HarnessUiPlacement `0.2.20` — The overlay's frames for a mod's `UiScreen` and `UiWindow` placement (`KilnHarness.UiPlacement`): Kiln.Ui's own code decides where each goes.
- `ContentSize { get; set; } -> (float Width, float Height)` — The size, in pixels at UI scale 1, that a window sized by its contents takes (Width or Height 0, or AutoResize).
- `FilePath { get; } -> string?` — ui-placement.json in the harness's config folder (it exists once something was remembered).
- `Frame(UiScreen screen) -> UiPlacement?` — One overlay frame of `screen`: where it is now (fractions of the viewport, sizes at UI scale 1), or null when it is...
- `Frame(UiWindow window) -> UiPlacement?` — One overlay frame of `window`: where it is now, or null when it is hidden or removed.
- `FrameWindows() -> IReadOnlyDictionary<UiWindow, UiPlacement>` `0.2.23` — One overlay frame of every open `UiWindow` (of every mod in the harness), docked ones together: where each one is...
- `PlayerDrags(UiWindow window, float x, float y, bool shift = false) -> void` `0.2.23` — The player holds `window` and drags it in the next `HarnessUiPlacement.FrameWindows`, its centre to (`x`, `y`)...
- `PlayerPlaces(UiScreen screen, float x, float y, float width, float height) -> void` — The player drags and resizes `screen` in its next frame: its centre to (`x`, `y`), fractions of the viewport, at...
- `PlayerPlaces(UiWindow window, float x, float y, float width, float height) -> void` — The player drags and resizes `window` in its next frame (as for a screen).
- `Remembered(UiScreen screen) -> UiPlacement?` — What Kiln remembers for `screen` (null: nothing, or it does not remember).
- `Remembered(UiWindow window) -> UiPlacement?` — What Kiln remembers for `window` (null: nothing, or it does not remember).
- `Restart() -> void` — A game start for placements: what Kiln remembers is read from ui-placement.json again (write it first to script a...
- `Scale { get; set; } -> float` — The UI scale (1 unless the test says): sizes at UI scale 1 are multiplied by it.
- `ViewportHeight { get; set; } -> float` — The display's height in pixels (1080 unless the test says).
- `ViewportWidth { get; set; } -> float` — The display's width in pixels (1920 unless the test says).

## TestKit.HarnessUiSnapshot `0.2.17` — The fake kiln.ui.snapshot of a harness (`KilnHarness.UiSnapshot`): Kiln.Blocks' BlockUi.ReadWorld, BlockUi.DemandWorld and the block runtime's `world.*` bindings run...
- `Demanded { get; } -> UiWorldFields` — The groups the live demands ask for together (what the next publication carries).
- `Demands { get; } -> int` — Demands held now (all mods, Kiln.Blocks' windows and HUD lines included).
- `EndWorld() -> void` — A world or session change (the menu, a loading screen, a rejoin): the publication goes and every token is revoked at...
- `Publication { get; } -> ulong` — The newest publication's id; 0 when there is none.
- `Publish(TestKit.FakeWorldSample sample) -> ulong` — Makes `sample` the newest publication and returns its id (ids grow by one per publication).
- `Tokens { get; } -> int` — Tokens not released yet (a read releases its own).

## TestKit.HarnessUiValues `0.2.3` — The fake kiln.ui.values of a harness (`KilnHarness.UiValues`): Ui.Values' own code runs on it.
- `Decisions { get; } -> IReadOnlyList<TestKit.FakeUiDecision>` — Every provider call of `HarnessUiValues.Draw` so far, in order.
- `DetailLines(TestKit.FakeUiItem item) -> IReadOnlyList<string>` — Draws `item`'s detail view: the tooltip-lines providers run in order and the lines they added come back (at most 16...
- `Draw(uint kind, TestKit.FakeUiItem item, TestKit.FakeUiBar game) -> TestKit.FakeUiBar` — Draws a value of `kind` (`UiValueKind`'s number: 0 detail, 1 slot, 2 held, 3 health, 4 stamina, 5 mana) for `item`...
- `Registrations { get; } -> int` — Registrations held now (values and tooltip lines).

## TestKit.HarnessVoxelMaterials `0.2.22` — The voxel materials of a harness (`KilnHarness.VoxelMaterials`): rows as the game's terraforming registry, world table and items would give them.
- `Add(int cellId, string name, ItemId lootItem = default(ItemId), int? exchangeRate = null, IReadOnlyList<ItemId>? placingItems = null, string? hardness = null, int healthPoints = 0, Guid guid = default(Guid)) -> TestKit.HarnessVoxelMaterials` — Adds (or replaces) the material of `cellId` (1..127 terrain, 128..255 building).
- `Reads { get; } -> int` — How many times Kiln read this harness's materials (once, then kept).

## TestKit.HarnessWorld `0.2.1` — The world of a harness's server (`KilnHarness.World`): what the game's world file and its save cycle are to Kiln.Saves.
- `Directory { get; } -> string` — The world's sidecar folder, `kiln/data/worlds/<id>`: mod data and per-world configs.
- `Id { get; } -> Guid` — The world's id (what `Saves.WorldId` and `WorldData.WorldId` say).
- `Loaded { get; } -> bool` — Whether the world has loaded (the first step after the server started).
- `Revision { get; } -> uint` — The revision of the newest written save (0: never saved).
- `Save(int maxSteps = 100) -> uint` — Saves the world now, as an admin's "save now": asks the game for a save and steps the server until the world file is...

## TestKit.HarnessWorldTables `0.2.4` — Scripted process boot data, not a game or renderer.
- `Dependency(string mod, string dependency) -> void` — Declares that `mod` depends on `dependency`, as if its manifest listed it.
- `GameMaterial(string name, uint item, bool roof = false, bool terrain = false) -> void` — Scripts a game (vanilla) material that a pool entry may name as its `gameMaterial`.
- `Material(string mod, string key, uint item, bool roof = false, bool live = true, bool terrain = false) -> void` — Configures a live or retained building/terrain material and its block item.
- `PendingCount { get; } -> int` — How many accepted pool and brush definitions are pending: `HarnessWorldTables.PendingToolDefinitionsJson` copies them.
- `PendingToolDefinitionsJson(string mod) -> string` `0.2.14` — A copy of the accepted pending policy-pool and computed brush descriptors, in world-table JSON form.
- `Readiness { get; set; } -> CapacityReadiness` — The readiness the fake's boot tables report.
- `ReservePoolId(uint id) -> void` — Models an occupied foreign pool ID so collision refusal can be tested without searching for an FNV collision.
- `ServePool(string mod, string key, BuildingMaterialPoolBlock block) -> void` — Configures one actual served boot row.
- `StorageFails { get; set; } -> bool` — When true, the fake refuses new pool and brush definitions with a storage failure, as if it could not persist them.
- `TerrainBrushCells(string mod, string key) -> byte[]?` `0.2.14` — A copy of a pending sphere descriptor's raw fill bytes (x fastest, then y, then z), sampled at half-metre voxel...
- `TerrainItem(string name, uint item, byte cell) -> void` `0.2.14` — Scripts a placeable terrain item's item-to-cell mapping.

## TestKit.KilnHarness `0.2.1` — A Kiln server with no game, for mod tests under `dotnet test`: a temp server folder with its `kiln/` tree, mods loaded the way the game loads them (Kiln.Host's own...
- `KilnHarness(TestKit.KilnHarnessOptions? options = null)` — Creates the temp server folder, points Kiln at it and starts the tick thread.
- `AmmoList { get; } -> TestKit.HarnessAmmoList` `0.2.15` — The fake kiln.ui.ammo of this harness: the HUD's ammunition and spell lists Ui.AmmoList adds rows to (the game's...
- `As(TestKit.ModHandle mod, Action action) -> void` — Runs `action` on the tick thread as code of `mod`: its `HostAccess.Current` and synchronization context are set as...
- `As<T>(TestKit.ModHandle mod, Func<T> func) -> T` — Runs `action` on the tick thread as code of `mod`: its `HostAccess.Current` and synchronization context are set as...
- `Assets { get; } -> TestKit.HarnessAssets` `0.2.3` — The kit's kiln.assets of this harness: the game's content index the blob catalog reads, and the content blobs mods...
- `Boot() -> void` — Loads the waiting mods and starts the server (the first step) when that has not happened yet.
- `BootPhase { get; } -> TestKit.HarnessBootPhase` `0.2.3` — The boot phase of this harness (Mod.OnBoot): run a boot mod's OnBoot as a start does, read what it staged and why it...
- `Camera { get; } -> TestKit.HarnessCamera` `0.2.17` — The fake kiln.camera of this harness: the camera requests mods made, and leases a test lets run out.
- `Capabilities { get; } -> TestKit.HarnessCapabilities` — Capability rows as mods see them offline, and the test's overrides.
- `Chat { get; } -> TestKit.HarnessChat` `0.2.2` — The server's text chat as the kit's fake of kiln.chat keeps it: what the players typed and every server line mods sent.
- `ClientSkills { get; } -> TestKit.HarnessClientSkills` `0.2.22` — The fake kiln.skills.client of this harness: the local player's skill tree as the game client holds it, as...
- `ClientStations { get; } -> TestKit.HarnessClientStations` `0.2.22` — The fake kiln.stations.client of this harness: the stations the game client's own station cache holds, as...
- `Combat { get; } -> TestKit.HarnessCombat` `0.2.2` — Health, hits and deaths in this server's fake world as the kit's fake of kiln.combat keeps them: the test gives...
- `ComponentChanges { get; } -> TestKit.HarnessComponentChanges` `0.2.4` — The kit's fake kiln.component_changes of this harness: what is watched, and the typed values its attribute events carry.
- `ConfigDirectory { get; } -> string` — `kiln/config`: mod configs (`Config.Directory`).
- `ControlDirectory { get; } -> string` — `kiln/control`: the console folder is `control/console` (Kiln.Chat polls it).
- `Crafting { get; } -> TestKit.HarnessCrafting` `0.2.15` — The linked crafting-source recording fake.
- `CreatureControl { get; } -> TestKit.HarnessCreatureControl` `0.2.3` — The kit's fake kiln.creature_control of this harness: what a test scripts of the bodies a mod leases, and what the...
- `Crosshair { get; } -> TestKit.HarnessCrosshair` `0.2.22` — The fake kiln.client.crosshair of this harness: what the game's crosshair hits, as the test aims it.
- `CurrentStep { get; } -> ulong` — The last step that ran (0 before the first).
- `DataDirectory { get; } -> string` — `kiln/data`: each mod's `ModContext.DataDirectory` is `data/<id>`.
- `Decisions { get; } -> TestKit.HarnessDecisions` `0.2.4` — The kit's fake kiln.decisions of this harness: how the decisions mods were asked about ended.
- `Dispose() -> void` — Stops the server (`ServerStoppingEvent`, every mod's `OnUnload` in reverse load order), logs any mod whose load...
- `DumpLogs() -> string` — The captured lines as text, one per line (for an assertion message).
- `EnterWorld() -> void` `0.2.10` — The player hosts or joins again, for the tasks (#82): new sessions of the side's worlds (the server world first on a...
- `Entities { get; } -> TestKit.FakeWorld` `0.2.2` — The fake world's entities and components: what Kiln.World (`World`), Kiln.Game's typed reads and tracked writes run...
- `FindLogs(string text, string? modId = null) -> IReadOnlyList<TestKit.LogLine>` — The lines whose text contains `text` (ordinal), optionally only those of one mod.
- `GameLog(string text, LogLevel level = LogLevel.Info) -> void` — A line of the game's own log, delivered at the next step: to mods' `ModContext.GameLog` handlers, and to Kiln's...
- `GameTexts { get; } -> TestKit.HarnessGameTexts` `0.2.22` — The game's texts per language and the items' text tags this harness's game has (the kit's content lookups, since...
- `HasLog(string text, string? modId = null) -> bool` — Whether a line contains `text`, optionally only one of mod `modId`.
- `ImpactNatives { get; } -> TestKit.HarnessImpactNatives` `0.2.4` — The kit's fake kiln.abilities natives of this harness: the game's programs and natives a test declares, and the...
- `Input { get; } -> TestKit.HarnessInput` `0.2.22` — The fake kiln.ui.input of this harness: the player's presses and releases for listeners that own them...
- `Items { get; } -> TestKit.HarnessItems` `0.2.2` — The items and inventories of this server as the kit's fake of kiln.items keeps them: the items the game has (the...
- `Kill() -> void` — Ends the server the way `kill -9` does, as far as the world goes: the game makes no shutdown save, a save that is...
- `KilnDirectory { get; } -> string` — `<server>/kiln`: what `KilnPaths.Root` points at while the harness lives.
- `Language { get; set; } -> string` — The game's language as mods see it (`Game.Language`, and so `ModLocale.Get`): `"en-us"`, as a dedicated server...
- `LeaveWorld() -> void` `0.2.10` — The player leaves the world, for the tasks (#82): its worlds' STOPPING, the client world first.
- `static ListTests(Assembly assembly) -> IReadOnlyList<string>` — The `[KilnTest]` names of `assembly` in the order Kiln.Testing's runner runs them: by `Order`, then the type's full...
- `Load<TMod>(TestKit.ModManifest? manifest = null) -> TestKit.ModHandle` — Loads a second, isolated copy of the assembly that holds `TMod`, as the game does: its files go into...
- `LoadFolder(string path) -> TestKit.ModHandle` — Loads a built mod folder (its `kiln.mod.json`, assemblies, `locales/` and data), copied into `kiln/mods/<id>` as...
- `LogFile { get; } -> string` `0.2.11` — The path of `kiln/logs/kiln.log`, the file the harness writes every captured line to.
- `Logs { get; } -> IReadOnlyList<TestKit.LogLine>` — Every line Kiln wrote while this harness lived, in order.
- `LogsDirectory { get; } -> string` — `kiln/logs`: `kiln.log` (every captured line), `mods.txt`, `tests/`.
- `LogsOf(TestKit.ModHandle mod) -> IReadOnlyList<TestKit.LogLine>` — The lines mod `modId` wrote (its `Logger`, and Kiln's lines in its name such as an event handler that threw).
- `LogsOf(string modId) -> IReadOnlyList<TestKit.LogLine>` — The lines mod `modId` wrote (its `Logger`, and Kiln's lines in its name such as an event handler that threw).
- `MenuFrame() -> int` `0.2.12` — One UI frame of the game's main menu, for the tasks (#83): on the game's main thread, while no world runs (after...
- `Mod(string id) -> TestKit.ModHandle` — The handle of a mod this harness was given (by id).
- `ModsDirectory { get; } -> string` — `kiln/mods`: where the harness puts each mod's folder.
- `ModValues { get; } -> TestKit.HarnessModValues` `0.2.17` — The fake of Kiln.ModValues' world store and live characters (created and installed on first use).
- `NpcBodies { get; } -> TestKit.HarnessNpcBodies` `0.2.17` — The boot build's NPC body rows this harness's game reports: script what the start built, then test...
- `NpcSpeech { get; } -> TestKit.HarnessNpcSpeech` `0.2.23` — The kit's fake kiln.npcspeech of this harness: what a Kiln client's speech bubble of each NPC would show after the...
- `OnMainThread(Action work) -> void` `0.2.10` — Runs `work` on the game client's main thread, where the client world steps (#82): a task started there belongs to...
- `Outlines { get; } -> TestKit.HarnessOutlines` `0.2.13` — The fake kiln.render's outlines of this harness (Ui.World.Outline): what mods outlined, and the game's side of it...
- `Pets { get; } -> TestKit.HarnessPets` `0.2.26` — The fake of Kiln.Pets' world (created and installed on first use, gone with the harness).
- `Players { get; } -> TestKit.HarnessPlayers` — The players on this server: `HarnessPlayers.Join` one, `FakePlayer.Leave`.
- `PlayerStorage { get; } -> TestKit.HarnessPlayerStorage` `0.2.16` — The fake kiln.items.storage of this harness: the game's side of player storage (joins, restores, saves, leaves, deaths).
- `Programs { get; } -> TestKit.HarnessPrograms` `0.2.4` — The game's ECS programs in the harness's fake server world (kiln.world v4): the ones a test declares, which...
- `Queries { get; } -> TestKit.HarnessQueries` `0.2.3` — The fake kiln.queries of this harness: what a voxel ray answers, and the rays mods asked for.
- `ReadLogFile() -> string` `0.2.11` — The text of `KilnHarness.LogFile` as written so far ("" when it was not created).
- `Reload(TestKit.ModHandle mod, bool checkCollected = true) -> TestKit.ModHandle` — Reloads `mod` from its folder with its dependents (they unload first and load again after it), as the control...
- `Repl { get; } -> TestKit.HarnessRepl` `0.2.17` — The fake kiln.repl of this harness (the network inspector, Kiln.Replication): the peers on the server's machine...
- `ReplNet { get; } -> TestKit.HarnessReplNet` `0.2.17` — Script and inspect the fake kiln.repl.net service used by Kiln.Replication.
- `ReportWorldRunning() -> void` — The game says its world runs (the line Kiln's lifecycle waits for): with `KilnHarnessOptions.WaitForWorldRunning`...
- `Routines { get; } -> TestKit.HarnessRoutines` `0.2.17` — The kit's fake kiln.routines of this harness: the game time and the villagers' decisions a test plays, for the...
- `Run(Action action) -> void` — Runs `action` on the tick thread, as Kiln (no mod).
- `Run<T>(Func<T> func) -> T` — Runs `func` on the tick thread, as Kiln (no mod), and returns its result.
- `RunCommand(CommandSource source, string line) -> CommandResult` — Runs a command line as `source` (see `KilnHarness.RunCommand`).
- `RunCommand(TestKit.FakePlayer player, string line) -> CommandResult` — Runs a command line as `player` typed it in chat (`CommandSource.ForPlayer`), through the public `Commands.Execute`...
- `RunCommand(string line) -> CommandResult` — Runs a command line as the server console (`CommandSource.Console`), through the public `Commands.Execute` on the...
- `RunTest(TestKit.ModHandle mod, string name, TestKit.KilnTestOptions? options = null) -> TestKit.KilnTestResult` — Runs one `[KilnTest]` of `mod` through Kiln.Testing's own runner (as the control command `test <mod> =name` does)...
- `RunTests(TestKit.ModHandle mod, TestKit.KilnTestOptions? options = null) -> IReadOnlyList<TestKit.KilnTestResult>` — Runs every `[KilnTest]` of `mod`, one `KilnHarness.RunTest` each, in the runner's order.
- `ServerDirectory { get; } -> string` — The temp server folder (the game's install folder in a real server).
- `Spawned { get; } -> TestKit.HarnessSpawn` `0.2.2` — What Kiln.Spawn (`Spawn`) did in this server's fake world: the templates the game has, the entities mods spawned...
- `State { get; } -> TestKit.HarnessState` — Where the server is.
- `StateDirectory { get; } -> string` — `kiln/state`: `mods.json` for the launcher.
- `Step(int steps = 1) -> void` — Runs `steps` server steps, each in the order of Kiln.Host's Pre: the first one loads the mods waiting in `kiln/mods`...
- `StepUntil(Func<bool> condition, int maxSteps = 1000, TimeSpan pace = default(TimeSpan)) -> int` — Steps until `condition` (checked on the tick thread before each step) is true; returns the steps it took (0 when it...
- `Stop(string reason = "SIGINT") -> void` — Stops the server at once, as a stop signal does: `ServerStoppingEvent` with `reason`, then every mod's `OnUnload` in...
- `Taming { get; } -> TestKit.HarnessTaming` `0.2.26` — The fake of Kiln.Taming's world and item costs (created and installed on first use, gone with the harness).
- `Tasks { get; } -> TestKit.HarnessTasks` `0.2.10` — Kiln tasks (`ModContext.Tasks`) of the mods under this harness: they run on the kit's task pool, whose workers are...
- `Terrain { get; } -> TestKit.HarnessTerrain` `0.2.3` — The fake world behind Kiln.Terrain in this harness: its cells, bases and what overlays wrote.
- `Time { get; } -> TestKit.HarnessTime` `0.2.2` — The server's day/night clock as the kit's fake of kiln.time runs it on the harness's virtual time.
- `UiChest { get; } -> TestKit.HarnessUiChest` `0.2.17` — Offline native chest-button registrations and activations.
- `UiInventory { get; } -> TestKit.HarnessUiInventory` `0.2.12` — The fake kiln.ui.inventory of this harness: draw a screen's slots, hover and click one, and ask what mark a drawn...
- `UiItemDetail { get; } -> TestKit.HarnessUiItemDetail` `0.2.14` — Recordings of the game's item-detail text and values (no game renderer is simulated).
- `UiNativeTints { get; } -> TestKit.HarnessUiNativeTints` `0.2.17` — Offline native-background tint rules; no game drawing.
- `UiPlacement { get; } -> TestKit.HarnessUiPlacement` `0.2.20` — Where a mod's screens and windows sit: play an overlay frame for one (`HarnessUiPlacement.Frame`), move it as the...
- `UiSnapshot { get; } -> TestKit.HarnessUiSnapshot` `0.2.17` — The fake kiln.ui.snapshot of this harness (since 0.2.0-dev.31): publish the live game values that BlockUi.ReadWorld...
- `UiValues { get; } -> TestKit.HarnessUiValues` `0.2.3` — The fake kiln.ui.values of this harness: draw a value or an item detail view through the registered Ui.Values providers.
- `Unload(TestKit.ModHandle mod, bool checkCollected = true) -> void` — Unloads `mod`, and first every mod that depends on it (reverse order), as the control command `unload` does.
- `VoxelMaterials { get; } -> TestKit.HarnessVoxelMaterials` `0.2.22` — The game's voxel materials of this harness's game (the kit's content lookups, since 0.2.0-dev.31.13): what...
- `World { get; } -> TestKit.HarnessWorld` — The world this server runs: its id, its saves, its sidecar folder.
- `WorldTables { get; } -> TestKit.HarnessWorldTables` `0.2.4` — Scripts boot pool snapshots and registration refusals.
- `WritePermissions(string json) -> void` — Writes `kiln/config/permissions.json` (JSONC, the format Kiln writes: groups, userGroups, players by SteamID64) and...
- `WriteServerConfig(string json) -> void` — Writes the server's `enshrouded_server.json` in `KilnHarness.ServerDirectory` (Kiln.Permissions reads its...

## TestKit.KilnHarnessOptions `0.2.1` — Settings of one `KilnHarness`.
- `CallTimeout { get; init; } -> TimeSpan` — How long an ordinary harness call (including Step, Unload and Reload) may block its tick thread before the watchdog...
- `CrashReports { get; init; } -> bool` — Crash reports (a mod disabled by the fault policy) are written to `kiln/crash-reports` in the temp folder.
- `FaultLimit { get; init; } -> int` — Faults within `KilnHarnessOptions.FaultWindowSeconds` that disable a mod (kiln.json `mods.fault_limit`).
- `FaultWindowSeconds { get; init; } -> double` — The fault window in seconds of the harness's virtual time (steps x `KilnHarnessOptions.WallDeltaMs`); 0: ever.
- `KeepFolder { get; init; } -> bool` — Keep the temp server folder after `KilnHarness.Dispose` (also: environment `KILN_TESTKIT_KEEP=1`).
- `LogSink { get; init; } -> Action<TestKit.LogLine>?` — Gets every captured log line as it is written (any thread), e.g. to show it in a test's output.
- `ServerDirectory { get; init; } -> string?` — Run in an isolated test folder instead of a new temp one, e.g. the folder of an earlier harness that kept it...
- `Side { get; init; } -> TestKit.HarnessSide` `0.2.10` — Which game process the harness stands for, for the mods' Kiln tasks: where their hops can go (the server world, the...
- `StopTimeout { get; init; } -> TimeSpan` — How long Dispose may spend stopping the server before the watchdog stops the blocked callback and abandons the harness.
- `TempDirectory { get; init; } -> string?` — Where the temp server folder is made; default the system's temp folder.
- `TestTimeout { get; init; } -> TimeSpan` — The timeout of one mod's `[KilnTest]` run (kiln.json `mods.test_timeout_s`).
- `ThrowOnModFault { get; init; } -> bool` — A harness call during which a mod faulted throws `ModFaultException` (default).
- `WaitForOtherHarness { get; init; } -> TimeSpan` — How long a new harness waits for the one still alive in this process (harnesses run one at a time: Kiln's state is...
- `WaitForWorldRunning { get; init; } -> bool` — False (default): like a server without the game-log mirror, `ServerStartedEvent` fires at the first step (source...
- `WallDeltaMs { get; init; } -> double` — Wall-clock milliseconds one step stands for (`TickInfo.WallDeltaMs`); 40.8 is a dedicated server's 24.5 steps a second.
- `WorldId { get; init; } -> Guid?` — The id of the world the server runs (`KilnHarness.World`); default the world the server folder ran last, or a new one.

## TestKit.KilnTestOptions `0.2.1` — How `KilnHarness.RunTest` judges and guards one `[KilnTest]`.
- `AllowUnavailable { get; init; } -> IReadOnlyCollection<string>` — Unavailable names (`kiln.spawn`, a game feature) a test may ask for and still count: for tests whose subject is the...
- `static Default { get; } -> TestKit.KilnTestOptions` — The defaults.
- `SetupTimeout { get; init; } -> TimeSpan` — How long `KilnTestSession`'s setup (and the server start) may block the tick thread (default 60 s).
- `WatchdogGrace { get; init; } -> TimeSpan` — How much longer than the test's timeout one harness step may take before the watchdog stops the tick thread (default...

## TestKit.KilnTestResult `0.2.1` — One `[KilnTest]` as the harness ran it.
- `KilnTestResult(string Name, Testing.TestOutcome Outcome, TimeSpan Duration, string? Message, string? Stack, IReadOnlyList<string> Unavailable)`
- `Duration { get; init; } -> TimeSpan` — How long it ran.
- `Log { get; init; } -> IReadOnlyList<TestKit.LogLine>` — Every log line written while it ran.
- `Message { get; init; } -> string?` — Why it failed, was skipped or timed out; for an offline skip also what the body did.
- `Name { get; init; } -> string` — The test's name (`attr.Name ?? Class.Method`).
- `Notes { get; init; } -> IReadOnlyList<string>` — What the kit did around the test (a fresh harness after a stopped test, faults of other code).
- `Outcome { get; init; } -> Testing.TestOutcome` — The kit's verdict: Kiln.Testing's, except that a test that asked for something the kit does not fake is...
- `Runner { get; init; } -> Testing.TestResult?` — What Kiln.Testing's runner reported for the test; null when the watchdog stopped it, or the runner never reported.
- `Stack { get; init; } -> string?` — Where it failed (the assertion's frames), or for a stopped test the tick thread's stack where it blocked.
- `Unavailable { get; init; } -> IReadOnlyList<string>` — The unavailable names (services, features) asked while it ran, by the test's mod or a mod it depends on, without the...

## TestKit.KilnTestSession `0.2.1` — Runs a mod's `[KilnTest]` methods one by one on one harness and one load, as a server runs them, for a test framework's adapter (`Kiln.TestKit.Xunit.KilnTestSuite` is...
- `KilnTestSession(Func<TestKit.KilnHarness, TestKit.ModHandle> setup, Func<TestKit.KilnHarnessOptions>? harnessOptions = null, TestKit.KilnTestOptions? options = null)` — A session that runs a mod's `[KilnTest]` methods on harnesses it makes, one after another.
- `Dispose() -> void` — Disposes the current harness.
- `DisposeError { get; } -> string?` — What the last dispose of a harness reported, if it did not stop cleanly.
- `Harness { get; } -> TestKit.KilnHarness?` — The harness the next test runs on, if one is made (null before the first `KilnTestSession.Run`).
- `HarnessCount { get; } -> int` — How many harnesses the session made (more than one after a stopped test).
- `Run(string name) -> TestKit.KilnTestResult` — Runs the test named `name`.

## TestKit.LogLine `0.2.1` — One line Kiln wrote to its log while a harness ran: a mod's `Logger`, Kiln's own code, or the host (the loader and the lifecycle).
- `LogLine(long Sequence, ulong Step, LogLevel Level, string Source, string Text)`
- `Level { get; init; } -> LogLevel` — The level.
- `ModId { get; } -> string?` — The mod that wrote it, or null for Kiln and the host.
- `Sequence { get; init; } -> long` — Order of writing, from 1.
- `Source { get; init; } -> string` — `mod:<id>`, `kiln` (Kiln.Api and the services) or `host` (Kiln.Host).
- `Step { get; init; } -> ulong` — The harness's step when it was written (0 before the first).
- `Text { get; init; } -> string` — The text.

## TestKit.ModFaultException `0.2.1` — A mod faulted (an exception Kiln's fault policy counted) during a harness call.
- `Faults { get; } -> IReadOnlyList<string>` — Each fault as Kiln logged it (mod, where, the exception with its stack).

## TestKit.ModHandle `0.2.1` — A test's handle on one mod of a `KilnHarness`: what a test keeps in a local.
- `Context { get; } -> ModContext` — The running instance's context (loaded or disabled).
- `Error { get; } -> string?` — Why the mod is not loaded or was disabled; null otherwise.
- `Folder { get; } -> string` — The mod's folder, `kiln/mods/<folder>` in the harness's temp server folder.
- `Id { get; } -> string` — The mod id.
- `Info { get; } -> ModInfo` — The manifest's metadata, once the loader has read it.
- `Instance { get; } -> Mod` — The running instance (loaded or disabled).
- `IsLoaded { get; } -> bool` — `ModHandle.State` is Loaded.
- `State { get; } -> ModState` — What the mod list says: `ModState.Pending` while it waits for the server's first step, Loaded, Disabled (fault...

## TestKit.ModLeakException `0.2.1` — After an unload, a mod's load context was still alive: something outside the mod holds its code or objects, and in the game every reload of it would leak.
- `ModIds { get; } -> IReadOnlyList<string>` — The mods whose load context is still alive.

## TestKit.ModLoadException `0.2.1` — A mod did not load: the loader refused it (manifest, resolver, a missing dependency or feature) or its `OnLoad` threw.
- `ModId { get; } -> string` — The mod that did not load (several, comma-separated, when more failed at the first step).
- `Reason { get; } -> string` — The loader's reason, followed by what the capability rows say about any feature it named.
- `State { get; } -> ModState` — Failed or Skipped, as the mod list says.

## TestKit.ModManifest `0.2.1` — A `kiln.mod.json` built in code, for `KilnHarness.Load`.
- `ModManifest(string id)` — A manifest built in code for a mod loaded by the harness.
- `Advanced { get; set; } -> bool` — The `advanced` tier flag.
- `ApiVersion { get; set; } -> string` — The Kiln API the mod was built for; default this Kiln's (`Mods.RuntimeApiVersion`).
- `DependsOn(string id, string versionRange = "*", bool optional = false) -> TestKit.ModManifest` — Adds a dependency (`"kiln"` is Kiln itself).
- `Exports(params string[] files) -> TestKit.ModManifest` — Adds exported assemblies (`exports`, file names such as `MyApi.dll`).
- `Id { get; } -> string` — The mod id.
- `LocalesDirectory { get; set; } -> string?` — A folder whose files become the mod's `locales/` (not written to the manifest).
- `Name { get; set; } -> string?` — Display name; default the id.
- `Requires(params string[] features) -> TestKit.ModManifest` — Adds required features (`features`): the resolver refuses the mod while one is unavailable.
- `Set(string key, Json.Nodes.JsonNode? value) -> TestKit.ModManifest` — Any other manifest key (`tests`, `loadAfter`, `incompatible`...), as JSON.
- `Side { get; set; } -> ModSide` — Server (default), client or both.
- `SteamMessages { get; set; } -> bool` `0.2.13` — The `steamMessages` opt-in: the mod opens its own Steam peer message leases (Kiln.Network's `SteamMessages`).
- `ToJson(string entryAssembly, string? entryType) -> string` — The manifest as the mod's `kiln.mod.json` would hold it.
- `Version { get; set; } -> string` — The mod's version (semantic versioning); default 1.0.0.
- `Writes(params string[] components) -> TestKit.ModManifest` `0.2.2` — The component types the mod writes through the game's write tracking (`"writes"`: short names such as `Fuel`): what...

## TestKit.PhysicsCookResult `0.2.17` — What `FakePhysicsCookHost.Cook` did: `PhysicsCookResult.Ok` with the files it committed, or the reason it refused.
- `PhysicsCookResult(bool Ok, string? Reason, IReadOnlyList<string> Files)`
- `Files { get; init; } -> IReadOnlyList<string>` — The files committed by this cook, in the order given.
- `Ok { get; init; } -> bool` — The files were committed and registered.
- `Reason { get; init; } -> string?` — Why not (the cook's words: the field, the feature and its reason); null when `PhysicsCookResult.Ok`.

## TestKit.Xunit.KilnSuiteLifetime `0.2.1` — The class fixture of every `KilnTestSuite`: when xunit has run the suite's tests it disposes this, and the suite's harness goes with it.
- `Dispose() -> void` — Disposes the suite's harness.

## TestKit.Xunit.KilnTestOrderer `0.2.1` — Orders a `KilnTestSuite`'s tests as Kiln.Testing's runner does, whatever subset xunit was asked to run; other tests of the class come after, in xunit's default order.
- `OrderTestCases<TTestCase>(IReadOnlyCollection<TTestCase> testCases) -> IReadOnlyCollection<TTestCase>` — The suite's `[KilnTest]` cases in the in-game runner's order, then the class's other cases.

## `TestKit.Xunit.KilnTestSuite<TMod>` `0.2.1` — Every `[KilnTest]` method of `TMod`'s assembly as an xunit test, run under a `KilnHarness` through Kiln.Testing's own runner: the same test body runs offline under...
- `virtual AllowUnavailable { get; } -> IReadOnlyCollection<string>` — Unavailable names the tests may ask for and still count (tests of the unavailable path).
- `virtual HarnessOptions() -> TestKit.KilnHarnessOptions` — The options of each harness the suite makes.
- `KilnTest(string name) -> void` — One `[KilnTest]`, by name.
- `static KilnTests { get; } -> IEnumerable<Xunit.TheoryDataRow<string>>` — The `[KilnTest]` names of `TMod`'s assembly, for xunit's discovery.
- `virtual LoadMod(TestKit.KilnHarness kiln) -> TestKit.ModHandle` — Loads the mod whose tests run (default `kiln.Load<TMod>()`, its own manifest file); override for a `ModManifest`...
- `virtual Setup(TestKit.KilnHarness kiln) -> void` — Before the mod loads, on each new harness: other mods it needs (a test mod's mod under test), fake players, config...
- `virtual WatchdogGrace { get; } -> TimeSpan` — How much longer than its timeout a test may block the tick thread before the watchdog stops it.
