# TestKit (Kiln.TestKit): part 2 of 3

## TestKit.FakeWorld `0.2.2` — The fake world of a harness (`KilnHarness.Entities`): entities with components laid out by Kiln.Game's types, under the real Kiln.World.
- `All { get; } -> IReadOnlyList<EntityId>` — Every entity made here (and every fake player's entity that was given a component), by id.
- `BusyReads { get; set; } -> int` `0.2.16` — The next `value` frame-quiet reads from tasks (`ReadMode.FrameQuiet`) answer `ReadStatus.Busy`, as when a server...
- `BusyWrites { get; set; } -> int` `0.2.16` — The next `value` compare-exchanges from tasks (`WorldConcurrent.TryCompareExchange`) meet the engine's gate taken...
- `Create() -> EntityId` — Makes an entity with no component and no position.
- `Create(WorldPosition position) -> EntityId` — Makes an entity that stands at `position`: its `CurrentTransform` (identity rotation, scale 1), which...
- `Destroy(EntityId entity) -> bool` — Takes an entity and its components out of the world.
- `Exists(EntityId entity) -> bool` — Whether the entity is in the world: made here, or a fake player's.
- `GameWrites<T>(string program, TestKit.FakeGameWriter<T>? each = null) -> IDisposable` — A program of the game that writes `T`: from now on `World.WriteInfo` and every tracked write of the type name it...
- `Get<T>(EntityId entity) -> T` — The entity's component.
- `GetBytes(EntityId entity, string component) -> byte[]?` — A copy of the component's bytes; null when the entity has none.
- `Remove<T>(EntityId entity) -> bool` — Takes component `T` off an entity.
- `Set<T>(EntityId entity, T value) -> TestKit.FakeWorld` — Puts component `T` on an entity, or replaces its value, as if the game had: no write tracking, no event, at once.
- `SetBytes(EntityId entity, string component, ReadOnlySpan<byte> bytes) -> TestKit.FakeWorld` — Puts a component on an entity by its game name, as bytes: for a component no generated type names.
- `SetPosition(EntityId entity, WorldPosition position) -> void` — Moves an entity (or gives it its first position): the position in its `CurrentTransform`.
- `Track(string component, bool save, bool replication) -> void` — Says what the game's write tracking does with a component type, which the kit cannot know: a tracked write of it...
- `TryGet<T>(EntityId entity, out T value) -> bool` — The entity's component; false when it has none (or one of another size).
- `Writes { get; } -> IReadOnlyList<TestKit.FakeWorldWrite>` — Every tracked write a mod asked for that reached the world, in the order they ran, with what became of each (a write...

## TestKit.FakeWorldPosition `0.2.17` — The local player's position, in metres (the publication carries it in 32.32 fixed point).
- `FakeWorldPosition()`
- `FakeWorldPosition(double X, double Y, double Z)`
- `X { get; init; } -> double` — The X coordinate, in metres.
- `Y { get; init; } -> double` — The Y coordinate, in metres.
- `Z { get; init; } -> double` — The Z coordinate, in metres.

## TestKit.FakeWorldSample `0.2.17` — What a test publishes through `HarnessUiSnapshot.Publish`: each group it gives is in the publication when something demanded it.
- `HostHours { get; init; } -> double?` — The hour of the world this game hosts (`UiWorldFields.HostTime`).
- `Inventory { get; init; } -> IReadOnlyList<TestKit.FakeWorldSlot>?` — The own inventory's nonempty slots (`UiWorldFields.LocalInventory`), in any order: the publication sorts them by...
- `LocalPlayer { get; init; } -> uint` — The local player's entity (each inventory slot's holder).
- `Position { get; init; } -> TestKit.FakeWorldPosition?` — The position (`UiWorldFields.LocalPosition`).
- `Unavailable { get; init; } -> IReadOnlyDictionary<UiWorldFields, string>?` — Groups that are missing for a reason of their own, by one `UiWorldFields` bit each.
- `Vitals { get; init; } -> TestKit.FakeVitals?` — Health, stamina and mana (`UiWorldFields.LocalVitals`).

## TestKit.FakeWorldSlot `0.2.17` — One nonempty slot of the local player's own inventory.
- `FakeWorldSlot()`
- `FakeWorldSlot(uint Item, uint Count, uint InventoryPage = 0, uint SlotIndex = 0, uint Pide = 0)`
- `Count { get; init; } -> uint` — How many of the item the slot holds.
- `Deconstruct(out uint Item, out uint Count, out uint InventoryPage, out uint SlotIndex, out uint Pide) -> void`
- `Equals(TestKit.FakeWorldSlot other) -> bool`
- `InventoryPage { get; init; } -> uint` — The inventory page the slot is on (part of the slot's binding key).
- `Item { get; init; } -> uint` — The item in the slot, by the game's item id.
- `static operator !=(TestKit.FakeWorldSlot left, TestKit.FakeWorldSlot right) -> bool`
- `static operator ==(TestKit.FakeWorldSlot left, TestKit.FakeWorldSlot right) -> bool`
- `Pide { get; init; } -> uint` — The id of the item's own per-item data in the game (gear: level, rarity, gems); 0 for an item without any.
- `SlotIndex { get; init; } -> uint` — The slot's index on its page.

## TestKit.FakeWorldWrite `0.2.2` — One tracked write a mod asked for, as the fake world ran it (`FakeWorld.Writes`).
- `FakeWorldWrite(ulong Step, string? ModId, EntityId Entity, string Component, int Offset, WorldWriteStatus Status, byte[] Before, byte[] After)`
- `After { get; init; } -> byte[]` — The same bytes afterwards.
- `Before { get; init; } -> byte[]` — The bytes it named, as they were; empty when nothing was read.
- `Component { get; init; } -> string` — The component's short name.
- `Entity { get; init; } -> EntityId` — The entity.
- `ModId { get; init; } -> string?` — The mod that asked; null for a call outside any mod.
- `Offset { get; init; } -> int` — The first byte the write named.
- `Status { get; init; } -> WorldWriteStatus` — What became of it.
- `Step { get; init; } -> ulong` — The harness step at whose start the write ran: the step after the call.

## TestKit.HarnessAmmoList `0.2.15` — The fake kiln.ui.ammo of a harness (`KilnHarness.AmmoList`): Ui.AmmoList's own code runs on it.
- `Hold(AmmoListKind kind, ItemId weapon, params AmmoListRow[] gameRows) -> void` — The player holds `weapon` (a bow for `AmmoListKind.Arrow`, a staff for `AmmoListKind.Spell`): the game builds its...
- `KnownItems(AmmoListKind kind, params ItemId[] items) -> void` — The client's items of this kind (the arrows, the spells): a snapshot naming any other item is refused, as the game...
- `ModeOf(AmmoListKind kind) -> AmmoListMode?` — How the mods' rows go in (null: the game's list as it is).
- `NewSession() -> void` — A new client session (the player left and joined again): the snapshots and queued picks are cleared, as native does...
- `Pick(AmmoListKind kind, ItemId item) -> bool` — The player picks the row of `item`.
- `Player { get; set; } -> EntityId` — The local player the lists are built for (the context's `Player`).
- `Provided(AmmoListKind kind) -> IReadOnlyList<AmmoListRow>` — The mods' rows Kiln.Ui published for the list (composed, in order); empty while the game's list shows as it is.
- `Publishes { get; } -> int` — Snapshots published so far (a clear included).
- `Select(AmmoListKind kind, ItemId item) -> void` — The player moves the list's selection onto the row of `item`.
- `Selected(AmmoListKind kind) -> ItemId?` — The item the selection rests on: it stays on its item when the rows change, and goes to the first row when its item...
- `Session { get; } -> uint` — The client session the lists belong to (1 at the start).
- `SetGameRows(AmmoListKind kind, params AmmoListRow[] gameRows) -> void` — The backpack changed: the game's own rows of the held weapon's list are now `gameRows`.
- `Shown(AmmoListKind kind) -> IReadOnlyList<AmmoListRow>` — The list as the HUD shows it now: the game's rows, then (Append) or instead (Replace) the mods' rows.
- `Unhold(AmmoListKind kind) -> void` — The weapon is put away: the list is not built (no context; its snapshot stays until Kiln.Ui publishes another).

## TestKit.HarnessAssets `0.2.3` — The kit's kiln.assets of a harness (`KilnHarness.Assets`): Kiln.Assets' own code runs on it.
- `AddGameBlob(byte[] bytes) -> string` — Adds `bytes` to the game's content index (in order; a hash already there is not listed twice by the catalog).
- `CatalogGeneration { get; } -> ulong` — The catalog's generation: it grows with every change of the registrations.
- `IndexReady { get; set; } -> bool` — Whether the game's content index can be read.
- `Registrations { get; } -> IReadOnlyList<TestKit.FakeBlobRegistration>` — The live registrations, in the order that decides which one is served (the later one).

## TestKit.HarnessBootPhase `0.2.3` — The boot phase of a harness (`KilnHarness.BootPhase`): a unit test of a mod's `OnBoot`.
- `Run(string folder, bool commit = true) -> TestKit.BootRun` — Runs the OnBoot of the mod in `folder` (a built mod folder with a kiln.mod.json that has `"boot"`) and, with...
- `Runs { get; } -> IReadOnlyList<TestKit.BootRun>` — Every run of this harness, in order.
- `SetRecord(string key, string valueJson, Guid resourceId = default(Guid), uint typeHash = 0, int part = 0) -> void` — A record `boot.GameData.Read()` answers: its value as camelCase JSON.

## TestKit.HarnessCamera `0.2.17` — The fake kiln.camera of a harness (`KilnHarness.Camera`): Kiln.Camera's own code runs on it.
- `Calls { get; } -> IReadOnlyList<TestKit.FakeCameraCall>` — Every camera call the fake answered through its callback, in order.
- `End(ulong lease, uint endReason) -> bool` — Ends an applied lease by itself, as the service would for `endReason` (`CameraEndReason`'s numbers: 2 expired, 3...
- `Expire(ulong lease) -> bool` — Lets a lease run out, as its duration would: its change event (ended, expired) goes out at the next step.
- `KilnClient(EntityId player, ulong parts = 30) -> void` — Makes `player`'s client a Kiln client that offers `parts` (`CameraCapability`'s local part bits: 2 pose, 4 lens, 8...
- `Leases { get; } -> IReadOnlyList<ulong>` — The numbers of the leases that hold a camera now.
- `RefuseNext(uint reason) -> void` — The next apply or update that the lease rules would accept is refused with `reason` instead (`CameraReason`'s...
- `StopWorld() -> int` — The world stops: at the next step every applied lease ends `WorldStopped` and a waiting apply or update is answered...

## TestKit.HarnessCapabilities `0.2.1` — The capability rows mods see offline (`KilnHarness.Capabilities`): every Kiln service the kit has no fake for is unavailable with reason `offline`, so a mod's own...
- `All { get; } -> IReadOnlyList<TestKit.CapabilityRow>` — Every row, sorted by name.
- `ClearOverride(string name) -> void` — Drops the test's override of `name`.
- `Is(string name) -> bool` — Whether `name` is available (what `Game.HasFeature` answers).
- `Override(string name, bool available, string? reasonText = null) -> void` — Switches a row on or off for this harness.
- `Why(string name) -> string` — One sentence saying whether and why not, as a mod could log it.

## TestKit.HarnessChat `0.2.2` — The fake kiln.chat of a harness (`KilnHarness.Chat`): Kiln.Chat's own code (`Chat`, `Chat.Received`, `ChatMessageEvent`, commands typed in chat) runs on it.
- `Sent { get; } -> IReadOnlyList<TestKit.FakeChatLine>` — Every server line mods and Kiln sent, in order: one per `Broadcast`, `Tell` or `Say` line that reached a player...
- `Typed { get; } -> IReadOnlyList<TestKit.FakeChatMessage>` — Every line the players typed, as delivered, in order (consumed ones included).

## TestKit.HarnessClientSkills `0.2.22` — The client's skill tree of a harness (`KilnHarness.ClientSkills`): what `ClientSkills` reads on a joined client.
- `AddNode(uint id, SkillNodeType type = SkillNodeType.PassiveSkill, int cost = 1, int maxLevel = 0, float uiX = 0, float uiY = 0, int classAlignment = 3, bool shadow = false) -> TestKit.FakeClientSkillNode` — Adds a node to the client's tree (the next tree index), locked.
- `Clear() -> void` — Empties the tree (a world change).
- `Link(uint from, uint to, bool bidirectional = false) -> uint` — Links two nodes of the tree; the link's id is returned.
- `Loaded { get; set; } -> bool` — A copy exists (the client has a local player and its tree).
- `Lock(uint id) -> void` — The player does not have the node.
- `Player { get; set; } -> EntityId` — The local player (entity 1 unless a test says).
- `Reads { get; } -> int` — How many reads mods made of the client's skill-tree view.
- `Total { get; set; } -> int` — The player's skill points in all (level and skill-point knowledge, as the server counts them).
- `Unlock(uint id, int level = 1) -> void` — The player has the node at `level` (1 for a plain node).

## TestKit.HarnessClientStations `0.2.22` — The client's station cache of a harness (`KilnHarness.ClientStations`): what `ClientStations` reads on a joined client.
- `Add(EntityId station, uint workshop, WorldPosition position, BaseId baseId = default(BaseId)) -> TestKit.FakeClientStation` — Puts a station into the client's cache (or returns the one there): no recipe, state NoRecipe.
- `Clear() -> void` — Empties the client's cache (a world change).
- `Clock { get; set; } -> TimeSpan` — The client's game clock (what the station's cycle start is measured on).
- `Get(EntityId station) -> TestKit.FakeClientStation?` — The station in the client's cache, or null.
- `Reads { get; } -> int` — How many reads mods made of the client's station view.
- `Remove(EntityId station) -> bool` — Takes a station out of the client's cache (the server stopped sending it, its base went).
- `SetRecipe(uint recipe, TimeSpan duration, IReadOnlyList<ItemId> outputs, IReadOnlyList<ItemId>? inputs = null) -> void` — A recipe as the client's data has it: its unscaled duration, its outputs and its input items (a category input's...
- `SpeedFactor { get; set; } -> float` — The client's production speed factor (the game's FactorySpeed: a craft's time is multiplied by it).

## TestKit.HarnessCombat `0.2.2` — The fake kiln.combat of a harness (`KilnHarness.Combat`): Kiln.Combat's own code (`Combat.GetHealth`, `SetHealth`, `Heal`, `Damage`, `Kill`, god mode...
- `Health(EntityId entity) -> TestKit.FakeHealth?` — The entity's health; null when it has none.
- `Hit(EntityId target, int amount, EntityId attacker = default(EntityId), EntityId source = default(EntityId)) -> ulong` — The game deals a hit: `amount` damage (negative: a healing hit) on `target`.
- `Hits { get; } -> IReadOnlyList<TestKit.FakeHit>` — Every hit, mods' and the game's, in the order they were dealt.
- `Respawn(EntityId entity, int health) -> void` — A dead player (or creature) is alive again with `health`: `PlayerRespawnEvent` at the next step.
- `SetHealth(EntityId entity, int current, int max) -> void` — Gives an entity health, as the game's Health component holds it (no event).

## TestKit.HarnessComponentChanges `0.2.4` — The fake kiln.component_changes of a harness (`KilnHarness.ComponentChanges`): Kiln.World's own consumer of `ComponentChanges.Watch` runs on it.
- `PresentPairs { get; } -> int` — The present (entity, component) pairs the watches cover.
- `QueuedFrames { get; } -> int` — The frames of changes that wait for the consumer (at most two).
- `QueueOverflows { get; } -> ulong` — How often the queue was full and the watches were suspended.
- `SetAttribute(EntityId entity, ObservedAttribute attribute, int current, int maximum) -> void` — The typed current and maximum value of an attribute as the game's attribute code would read them (the kit does not...
- `Watches { get; } -> int` — How many watches exist.

## TestKit.HarnessCrafting `0.2.15` — Player-source registrations and their filtered counts in the kit.
- `BuildStock(EntityId player) -> IReadOnlyDictionary<ItemId, long>` `0.2.21` — The counts a block placement may use: the links registered with `CraftSourceOptions.Building` only, likewise.
- `Dyes(EntityId player) -> IReadOnlyDictionary<ItemId, long>` — The same registered counts restricted to ColorPalette items (dyes), with current filters, player-session generation...
- `FillStock(EntityId player) -> IReadOnlyDictionary<ItemId, long>` `0.2.21` — The counts a production-station fill may take: the links registered with `CraftSourceOptions.Fills` only, with the...
- `LockStock(EntityId player) -> IReadOnlyDictionary<ItemId, long>` `0.2.21` — The counts a lock may use: the links registered with `CraftSourceOptions.Locks` only, with the same filters...
- `RespecStock(EntityId player) -> IReadOnlyDictionary<ItemId, long>` `0.2.25` — The counts the skill respec (a node's unlearn, the Flame Altar's reset) may use: the links registered with...
- `Sources { get; } -> IReadOnlyList<TestKit.FakeCraftSourceRegistration>` — Live registrations, in registration order.
- `Stock(EntityId player) -> IReadOnlyDictionary<ItemId, long>` — Registered source counts only (not the bag), with the same per-player allow/deny union and current inventory...

## TestKit.HarnessCreatureControl `0.2.3` — The fake kiln.creature_control of a harness (`KilnHarness.CreatureControl`): Kiln.CreatureControl's own code (`CreatureControl.Acquire`, `WaitActive`, `Inspect`...
- `Calls { get; } -> IReadOnlyList<TestKit.FakeControlCall>` — Every call mods made, in order.
- `IsControlled(EntityId body) -> bool` — True while the broker holds `body` (its AI-off layer and the SteeringAgent removal, in the game).
- `SetBusy(EntityId body, bool busy = true) -> void` — Makes `body`'s current action keep playing (true) or end (false): a lease on it stays Pending while it plays, and...

## TestKit.HarnessCrosshair `0.2.22` — The fake kiln.client.crosshair of a harness (`KilnHarness.Crosshair`): Kiln.Client's `Crosshair.Read` and `Crosshair.TryGet` run on it unchanged, from any thread.
- `HarnessCrosshair(TestKit.KilnHarness harness)`
- `Age(TimeSpan age) -> void` — The newest sample is `age` old at the next reads (past `Crosshair.FreshFor`: stale).
- `Aim(WorldPosition position, Vector3? normal = null, float? distance = null, EntityId entity = default(EntityId), byte? voxelMaterial = null) -> void` — The game's trace hits `position`: a fresh sample with the next sequence.
- `AimAtNothing() -> void` — The game's trace hits nothing within its reach (its status 31): a fresh miss with the next sequence.
- `Clear() -> void` — No sample (as at the start of a client session), and no fault.
- `Fault() -> void` — Copying the game's result faulted: reads answer faulted, no sample, until `HarnessCrosshair.Clear`.
- `Reads { get; } -> int` — How many times mods read the crosshair (every get the fake answered).

## TestKit.HarnessDecisions `0.2.4` — The fake kiln.decisions of a harness (`KilnHarness.Decisions`): Kiln's own dispatcher of decisions runs on it.
- `Active { get; } -> int` — How many decisions are open (held, not ended).
- `Outcomes { get; } -> IReadOnlyList<DecisionResult>` — How every decision ended so far, in order: what the service recorded, whether or not a mod listened for...
- `RestartWorld() -> void` — The server world stops and the next one starts, as when a hosted world is switched: every open decision ends...

## TestKit.HarnessGameTexts `0.2.22` — The game's text tables and the items' text tags of a harness (`KilnHarness.GameTexts`).
- `Add(string language, uint locaId, string text) -> TestKit.HarnessGameTexts` — Adds (or replaces) a text of `language` (`"de-de"`; `"De_De"` works too), with the game's markup if you like.
- `AddItem(ItemId item, ItemTextTags tags) -> TestKit.HarnessGameTexts` — Makes `item` a game item with these text tags (`Items.TextTags`); its texts are the tables' texts of the tags...
- `Languages { get; } -> IReadOnlyList<string>` — The languages added so far, in order.
- `Reads { get; } -> int` — How many times Kiln read a table of this harness (a table is read once, then kept).

## TestKit.HarnessImpactNatives `0.2.4` — The fake kiln.abilities natives of a harness (`KilnHarness.ImpactNatives`): Kiln.Abilities' own code (`Abilities.Natives.RegisterSync`, `Attach`, `DeclareTyped`, an...
- `AddProgram(uint program, params TestKit.FakeEcall[] calls) -> void` — Declares a game program in the world's ImpactRegistry with the natives it calls and where (the ECALL's call-site...
- `Call(TestKit.FakeImpactCall call) -> TestKit.FakeImpactResult` — Makes one ECALL of `FakeImpactCall.Native`, as a program's step would, and waits for it.
- `Calls { get; } -> IReadOnlyList<TestKit.FakeImpactCallRecord>` — Every call the test made, in order.
- `OtherRead { get; } -> ISet<string>` — The types of impact_simulation's other_read list: what NativeContext.TryRead may read of any entity (with...
- `OtherWrite { get; } -> ISet<string>` — The types of impact_simulation's other_write list: what NativeContext.TryWrite may write (and TryRead read).
- `Serial { get; set; } -> bool` — The simulated pin-and-world-thread condition for ECS access (default true).
- `SetGameNative(string native, TestKit.FakeGameNativeHandler? handler = null) -> void` — Makes `native` one of the game's natives (its node name, `"applyDamage"`, or "0x" and its hash) and says what it...

## TestKit.HarnessInput `0.2.22` — The player's input for owning key listeners (`KilnHarness.Input`): KeyInput's and PadInput's own code runs on the kit's kiln.ui.input, whose ownership rules are the...
- `ClearLogs() -> void` — Empties `HarnessInput.Game` and `HarnessInput.Others`.
- `Down(KeyChord chord, int controller = 0) -> bool` — A new press of `chord`'s key with its modifiers (a "Pad:" chord on `controller`).
- `Down(uint code, int controller = 0, KeyMods mods = KeyMods.None, Vector2? at = null) -> bool` — A new press of `code` (a key's virtual key, 0x01/0x02 the left/right button, 0x04..0x06 the middle and side buttons...
- `FocusLost() -> void` — The game window lost the focus: every owner of a key or mouse button hears the end of its presses (KeyOwnEnd.Focus)...
- `Game { get; } -> IReadOnlyList<string>` — What reached the game, in order: "down 2", "repeat 41", "up 20d@1" (hex codes, "@" a controller other than 0).
- `HiddenFromGame(uint code, int controller = 0) -> bool` — The game is not told `code` is held although it is: an owned (or nobody's) press it never saw go down.
- `Leak(uint code, int controller = 0) -> void` — The game saw the owned press of `code` go down after all (in the game: Alt+F4, or a mouse record Kiln could not...
- `Others { get; } -> IReadOnlyList<string>` — What Kiln's other key handlers were given (listeners without KeyListenFlags.Own, key bindings, pointer subscribers)...
- `Repeat(uint code, KeyMods mods = KeyMods.None) -> bool` — The auto-repeat of a held key.
- `SetPad(int controller, PadButtons held, Vector2 leftStick = default(Vector2), Vector2 rightStick = default(Vector2), float leftTrigger = 0, float rightTrigger = 0) -> void` — The game's next poll of `controller` reads these: `held` buttons, the sticks (-1..1, after the dead zone) and the...
- `Unplug(int controller) -> void` — The game's next poll of `controller` fails (unplugged): PadInput.Read says not connected.
- `Up(uint code, int controller = 0, Vector2? at = null) -> bool` — The release of `code` (on `controller`).
- `WorldChange() -> void` — The client world started or stopped: the owners of presses made before hear KeyOwnEnd.World; those presses' releases...

## TestKit.HarnessItems `0.2.2` — The fake kiln.items of a harness (`KilnHarness.Items`): Kiln.Items' own code (`Items` Give, Take, TakeSlot, Read, FindInventories, the catalog, `ItemsGivenEvent` and...
- `AddContainer(EntityId entity, int inventories = 1) -> TestKit.FakeInventory` — Gives an entity of the fake world an inventory: a chest, a station.
- `AddItem(string name, int maxStack, ItemCategory category = ItemCategory.Materials, bool gear = false, ItemRarity rarity = ItemRarity.Common, int levelMin = 0, int levelMax = 0, Guid id = default(Guid)) -> ItemId` — Says the game has an item, so a mod can give, take, look it up and read it.
- `Calls { get; } -> IReadOnlyList<TestKit.FakeItemsCall>` — Every give and take a mod asked for, in the order they ran, with what became of each.
- `ContainerName(EntityId entity) -> string?` `0.2.13` — The name a container carries in the fake: what `Items.SetContainerName` stored (a mod's rename runs at the start of...
- `IdOf(string name) -> ItemId` — The id of an item the test added.
- `Of(EntityId entity) -> TestKit.FakeInventory?` — The inventory of an entity (a container, or an in-game fake player's); null when it has none.
- `PlayerInventories { get; set; } -> int` — How many 8-slot inventories a fake player's inventory gets (default 5, 40 slots): the kit does not model the game's...
- `SetEquipmentSlotType(ItemId item, EquipmentSlot slot) -> void` `0.2.15` — Sets the equipment type a fake item fits (both ring slots share one type).
- `SetGear(ItemInstanceRef item, int? enhancements = null, int? unlocked = null, int? level = null, int? durability = null) -> void` `0.2.12` — Changes a piece of gear as if the game had: a player's unlock at the workbench (`unlocked`, with `level` one...
- `SetPerkSlots(ItemId item, int slots) -> void` `0.2.12` — Says how many of a gear item's five perk slots its type fills (its last filled one + 1; default 5): gear made...
- `SetUpgradeRolls(params double[] rolls) -> void` `0.2.12` — Names the rolls of the upgrades that follow (`Items.UpgradeRarity`), in order, each 0 (inclusive) to 1 (exclusive)...

## TestKit.HarnessModValues `0.2.17` — Kiln.ModValues offline: which characters are in game, the world id, and the records as JSON.
- `static Character(int n) -> CharacterKey` — A character id for tests: account 0x1111..., id n.
- `Join(CharacterKey character) -> void` — The character is in game: its values are initialized now (on the tick thread, as Kiln does at the character's first...
- `Leave(CharacterKey character) -> void` — The character left: its values stay (readable), writes are refused (CharacterUnavailable).
- `Record(CharacterKey character, string mod, string partition = "world") -> string?` — The records of a character and owning mod as stored ("world", "carried-authority", "carried"), or null.
- `World { get; set; } -> Guid` — The world's id (revisions and compare-and-set are per world).

## TestKit.HarnessNpcBodies `0.2.17` — What the game's start built of NPC blocks with a body of their own (`KilnHarness.NpcBodies`).
- `Clear() -> void` — No rows: a start that built no bodies.
- `Failed(string mod, string npc, string reason, ulong generation = 0) -> void` — The start's boot build refused `npc` of `mod` for `reason`: Failed.
- `Ready(string mod, string npc, Guid template = default(Guid), Guid model = default(Guid), Guid graph = default(Guid), string? closure = null, ulong generation = 1) -> void` — The start built `npc` of `mod`: Ready.
- `Removed(string mod, string npc, ulong generation = 0) -> void` — `npc` of `mod` was Ready at the previous start and is gone now: Removed.

## TestKit.HarnessNpcSpeech `0.2.23` — What Kiln clients' speech bubbles would be told (`KilnHarness.NpcSpeech`): the effective override of each NPC (after every mod's priorities) and its running Say, with...
- `Calls { get; } -> int` — How many set and say calls reached the fake (a test that expects nothing new compares it).
- `FarewellsOf(EntityId npc, string language = "en-us") -> IReadOnlyList<string>` `0.2.27` — The farewells (`Npcs.Farewells`) a Kiln client's dialogue menu shows for the NPC, in `language`; empty when it keeps...
- `LinesOf(EntityId npc, string language = "en-us") -> IReadOnlyList<string>` — The lines a `SpeechMode.Replace` shows, in `language` (a game language key such as "en-us" or "de-de"); empty otherwise.
- `MenuLinesOf(EntityId npc, string language = "en-us") -> IReadOnlyList<string>` `0.2.27` — The menu's own lines (`Npcs.MenuLine`) a Kiln client's dialogue menu shows for the NPC when it opens, in `language`...
- `ModeOf(EntityId npc) -> SpeechMode` — What the NPC's greeting bubble does: `SpeechMode.Game` when Kiln leaves it to the game.
- `PickOf(EntityId npc, NpcLineKind kind = NpcLineKind.Greeting) -> string?` `0.2.27` — How the NPC's line of `kind` picks among its lines: "Random", "InOrder", "Seeded(seed)" or "By" (the host's...
- `PickTopic(EntityId npc, string topicId, EntityId player) -> bool` `0.2.27` — A pick of the topic `topicId` (the id a mod gave it) by `player`, as the host runs it: the topic's OnPick, now, on...
- `RepliesTo(EntityId player, string language = "en-us") -> IReadOnlyList<(EntityId Npc, string Text)>` `0.2.27` — What `Npcs.MenuSay` (and a topic's reply) told `player`'s menu, oldest first: the NPC and the text in `language`.
- `Saying(EntityId npc, string language = "en-us") -> string?` — The text of the Say showing over the NPC now, in `language`; null when none.
- `TopicsOf(EntityId npc, string language = "en-us") -> IReadOnlyList<string>` `0.2.27` — The labels of the Kiln topics a Kiln client's talk page of the NPC shows after the NPC's own (`Npcs.Topics`, every...

## TestKit.HarnessOutlines `0.2.13` — The fake kiln.render's outlines of a harness (`KilnHarness.Outlines`): Ui.World.Outline's own code runs on it.
- `All { get; } -> IReadOnlyList<TestKit.FakeOutline>` — Every request this harness got, oldest first, ended ones too.
- `Despawn(EntityId entity) -> int` — `entity` leaves the client world: every live request on it ends now (`OutlineReason.Despawned`).
- `EndWorld() -> void` — The client world ends (the player leaves the game): every live request ends now (`OutlineReason.WorldEnded`).
- `GameHighlight(EntityId entity, bool on = true) -> void` — The game highlights `entity` itself (the building cursor rests on it), or stops; applies at the next step.
- `HideModel(EntityId entity, bool hidden = true) -> void` — `entity` has no model instance (hidden, not streamed in, a voxel model), or has one again; applies at the next step.
- `Live { get; } -> IReadOnlyList<TestKit.FakeOutline>` — The requests that have not ended, oldest first.
- `Of(WorldOutline outline) -> TestKit.FakeOutline?` — The request behind `outline` (null: not this harness's, or it ended before the kit saw it).
- `SetState(WorldOutline outline, OutlineState state, OutlineReason reason = OutlineReason.None) -> void` — Pins the state of `outline`'s request (the steps no longer decide it): what `WorldOutline.State` and...
- `Setting { get; set; } -> TestKit.FakeOutlineSetting` — The player's kiln.json `"ui": {"outlines": ...}`; applies at the next step.
- `Showing(EntityId entity) -> TestKit.FakeOutline?` — The request whose colour `entity`'s model shows now, or null.

## TestKit.HarnessPets `0.2.27` — Kiln.Pets offline: the owners in game, the pets' bodies and the hostile pairs a mod declared.
- `Bodies { get; } -> IReadOnlyList<EntityRef>` — The pets' live bodies (the kit's entities: no game entity stands behind them).
- `static Character(int n) -> CharacterKey` — A character id for tests: account 0x2222..., id n.
- `Down(PetId pet) -> void` — The pet's body takes a lethal hit: the roster sees it at the next step and the pet goes down as its definition says.
- `HostilePairs { get; } -> IReadOnlyList<(CharacterKey A, CharacterKey B)>` — The character pairs a mod declared hostile (`Pets.SetHostile`), each pair once.
- `Join(CharacterKey character, WorldPosition at = default(WorldPosition), int level = 10) -> PlayerRef` — The character is in game at `at`: the player its requests name (`Pets.Summon(player, ...)`).
- `Leave(CharacterKey character) -> void` — The character left: its pets' bodies are retired at the next step (records and wishes kept).
- `Of(CharacterKey character) -> IReadOnlyList<PetSnapshot>` — The character's pets, as `Pets.Of` answers on the tick thread.

## TestKit.HarnessPlayers `0.2.1` — The players of a harness's server (`KilnHarness.Players`).
- `All { get; } -> IReadOnlyList<TestKit.FakePlayer>` — The players the test joined that have not left (in game, or joining until the next step).
- `Join(string name, ulong steamId = 0, PlayerPermissions rights = PlayerPermissions.None, bool isAdmin = false, WorldPosition? position = null, uint userGroupId = 0) -> TestKit.FakePlayer` — A player connects and logs in: in game, with an entity (slot + 1), at the start of the next step.
- `Slots { get; } -> int` — Player slots of the server (16, as a dedicated server of this game has).

## TestKit.HarnessPlayerStorage `0.2.16` — The fake kiln.items.storage of a harness (`KilnHarness.PlayerStorage`): Kiln.Items' player storage runs its own code on it.
- `Apply(TestKit.FakePlayer player) -> void` — The join made the player's own pages: a restored player's storages are built now.
- `Capture(TestKit.FakePlayer player, ulong witness) -> ulong` — The game captured `player`'s character in a world save (with this witness): Kiln froze the storages into the save's...
- `CapturesDone() -> ulong` — The save's captures are done: the batch completes and the persistence owner's stage runs.
- `Die(TestKit.FakePlayer player, EntityId tombstone, TestKit.HarnessTombstoneMode mode = TestKit.HarnessTombstoneMode.AddBackpackMaterials) -> void` — The player died; the game made `tombstone` (none: no tombstone) under `mode`.
- `Join(TestKit.FakePlayer player, ulong witness = 1) -> void` — The game read `player`'s character at the join, with this inventory witness.
- `JoinAndRestore(TestKit.FakePlayer player, params TestKit.HarnessStorageCopy[] storages) -> void` — `HarnessPlayerStorage.Join`, `HarnessPlayerStorage.Restore` with `storages` (none: a new character), and...
- `Leave(TestKit.FakePlayer player) -> void` — The player left: the storages' inventories go.
- `Parked(TestKit.FakePlayer player, string fullKey) -> IReadOnlyList<TestKit.HarnessStorageRecord>` — The player's parked records of a storage, as Kiln holds them now.
- `Quarantined(TestKit.FakePlayer player, string fullKey) -> IReadOnlyList<TestKit.HarnessStorageRecord>` — The player's quarantined records of a storage (Kiln could not use them; kept).
- `Restore(TestKit.FakePlayer player, PlayerStorageSource source = PlayerStorageSource.New, bool paired = false, long revision = 0, params TestKit.HarnessStorageCopy[] storages) -> void` — Restores the copy Kiln.Saves chose for `player`'s join (once per join): an empty one with `PlayerStorageSource.New`.

## TestKit.HarnessPrograms `0.2.4` — The fake world's programs (`KilnHarness.Programs`).
- `Add(string name, TestKit.FakeProgramLists lists, Action<TestKit.FakeProgramSlice>? update = null) -> void` — Declares one of the game's programs in the fake server world, after the ones declared before (its index).
- `Add<T>(Action<TestKit.FakeProgramSlice>? update = null) -> void` — Declares the program of Kiln.Game's typed view `T` (`Ecs.Programs.FuelSetup`): its name and lists are the view's...
- `Marks { get; } -> IReadOnlyList<TestKit.FakeProgramMark>` — The writes wrap stages marked, in order.
- `Names { get; } -> IReadOnlyList<string>` — The names declared, in table order.
- `Updates(string program) -> int` — How many slices of `program` the game's update (the declared one) ran on.

## TestKit.HarnessQueries `0.2.3` — The fake kiln.queries of a harness (`KilnHarness.Queries`): Kiln.Queries' own code runs on it.
- `MaxRaysPerStep { get; set; } -> int` — How many rays the fake admits per step before it refuses with `BudgetExceeded` (the service's default: 64).
- `OpenPaths { get; } -> int` — How many path requests hold a slot now (not ended, or ended and not yet delivered).
- `PathRequests { get; } -> IReadOnlyList<TestKit.FakePathRequest>` — Every path request that was admitted, in order.
- `Paths { get; set; } -> Func<TestKit.FakePathRequest, TestKit.FakePathAnswer>?` — What the path search answers a request.
- `PathSteps { get; set; } -> int` — How many steps after its admission a path request answers (default 3; the game takes a few).
- `Rays { get; } -> IReadOnlyList<TestKit.FakeVoxelRay>` — Every ray that was admitted, in order.
- `Voxels { get; set; } -> Func<TestKit.FakeVoxelRay, TestKit.FakeVoxelAnswer>?` — What a voxel ray answers.
- `WithoutSpawnTime(EntityId entity) -> void` — Makes `entity` an anchor without a SpawnTime marker (as a saved entity or an item is in the game): a path request...

## TestKit.HarnessRepl `0.2.17` — The fake kiln.repl of a harness (`KilnHarness.Repl`): Kiln.Replication's own code runs on it.
- `AddComponent(TestKit.FakeReplComponent component) -> void` — Adds a component to the world's registry (what Replication.Component reads from the first step, and what a message's...
- `Connect(uint machine, uint peer) -> void` — A player connects on machine slot `machine` (1 to 16) with peer handle `peer` (kiln.net's peer id).
- `ConnectFake(uint machine, uint? labPeer = null) -> uint` — A lab's loopback fake on machine slot `machine`: its peer is `0x80000000 | machine`, or `labPeer` when a kiln.net...
- `ConnectLocal(uint peer) -> void` — A hosted world's own client on machine 0 (rows and events say Local).
- `Count(uint peer, byte channel = 4, long unchanged = 0, long tooBig = 0, long refused = 0, long acks = 0, long resets = 0) -> void` — Counts what the game's writer and session did for the peer and channel besides messages (only while some owner holds...
- `Counting { get; } -> bool` — Some owner holds the counters now (they count).
- `Destroy(uint peer, EntityId entity, uint tick, uint seq = 0, byte channel = 4) -> void` — A destroy list to `peer` names `entity` as destroyed: a watch gets a Destroyed event, then ENDED "destroyed" after...
- `Drop(uint peer, byte channel = 4, int records = 1) -> void` — kiln.repl's ring for the peer and channel was full: `records` watch records were dropped (the row's Dropped), and...
- `Leave(uint peer) -> void` — The peer leaves (its machine slot changes): its rows start over, and every watch for that one peer gets ENDED "peer...
- `Receive(TestKit.FakeReplMessage message) -> void` — This client receives a create or update message; a receive watch of the entity gets it at the start of the next step.
- `ReceiveDestroy(EntityId entity, uint tick) -> void` — This client is told the entity was destroyed: Destroyed, then ENDED "destroyed".
- `ReuseId(EntityId entity) -> void` — The entity's id now belongs to an entity of another template: the next message about it ends its watches with ENDED...
- `Send(uint peer, TestKit.FakeReplMessage message) -> void` — The server sends `peer` a create or update message about an entity in this frame.
- `StatsCalls { get; } -> IReadOnlyList<(string Owner, bool On)>` — Every stats_enable call that changed or kept a hold, in order: the owner's name (`mod:<id>`-style, as the kit names...
- `StopWorld() -> void` — The server world stops: every watch still on gets ENDED "world stopped" at the start of the next step; the peers and...
- `WatchSlots { get; } -> int` — kiln.repl's watch slots in use: live watches, and ended ones not removed yet (a slot is free at the step after its...

## TestKit.HarnessReplNet `0.2.17` — The fixture and recordings behind Kiln.Replication's filters, touches, item remap and game message hooks.
- `ConfigureItemRemap(IReadOnlyList<TestKit.FakeReplItemSite> sites, IReadOnlyCollection<ItemId> gameItems, IReadOnlyDictionary<ItemId, ItemId> lookAlikes) -> void` — Replaces the item remap fixture.
- `Connect(uint machine, uint peer, bool vanilla = true) -> NetPeer` — Connects a peer to `KilnHarness.Repl` and gives it an identity for replication callbacks and injection.
- `DriveMessage(uint peer, GameChannel channel, MessageDirection direction, ReadOnlySpan<byte> bytes) -> byte[]?` — Runs the registered handlers on a scripted game message and returns the resulting bytes, or null if dropped.
- `Injections { get; } -> IReadOnlyList<(uint Peer, GameChannel Channel, byte[] Bytes)>` — Inject attempts in call order, with copies of the requested bytes, including refused requests.
- `Touches { get; } -> IReadOnlyList<EntityId>` — Touch requests in call order, including requests for missing entities or entities without a carrier.
- `ValidateSend { get; set; } -> Func<GameChannel, ReadOnlyMemory<byte>, bool>?` — Supplies the test's verdict for injected bytes and send rewrites.

## TestKit.HarnessRoutines `0.2.17` — The fake kiln.routines of a harness (`KilnHarness.Routines`): Kiln.Blocks' own code (`Routines.Assign`, `Remove`, `Resume`, `Inspect`...
- `Decide(uint npcId) -> string?` — The villager `npcId` reaches a decision.
- `Finish(uint npcId, bool ok, string? reason = null) -> void` — The game ends the villager's current task state: done (`ok`) or failed (no seat, no path; `reason`, default...
- `Queued { get; } -> int` — How many changes wait for the next step.
- `SetClock(int hour, int minute = 0) -> void` — Sets the game time the villagers' decisions read.
- `SetSleepAllowed(uint npcId, bool allowed) -> void` — What the game's own sleep check answers for the villager: whether its sleep window lets it go to bed now.

## TestKit.HarnessSide `0.2.10` — Which game process a `KilnHarness` stands for, for its mods' Kiln tasks (#82).
enum: DedicatedServer, HostingClient, JoinedClient

## TestKit.HarnessSpawn `0.2.2` — The fake kiln.spawn of a harness (`KilnHarness.Spawned`): Kiln.Spawn's own code (`Spawn`, `EntitySpawnedEvent`, `EntityDestroyedEvent`, `EntityRef` stamps) runs on...
- `AddTemplate(string name, TemplateKind kind = TemplateKind.Creature, string displayName = "", Guid id = default(Guid)) -> Guid` — Says the game has a template loaded, so a mod can spawn it by name, display name or GUID (and `Spawn.Lookup`...
- `All { get; } -> IReadOnlyList<TestKit.FakeSpawned>` — The entities Kiln.Spawn made that are still in the world, in the order they were spawned.
- `Calls { get; } -> IReadOnlyList<TestKit.FakeSpawnCall>` — Every spawn, item drop, destroy and cleanup a mod asked for, in order, refused ones included.
- `Of(string template) -> IReadOnlyList<TestKit.FakeSpawned>` — The live spawned entities of a template (by name, ignoring case).
- `const PickupTemplate = "Base_LootPickup_Generic_Gravity" -> string` — The name of the game's generic item pickup, the template of every item drop.

## TestKit.HarnessState `0.2.1` — Where a harness's server is in its life.
enum: Created, Starting, Running, Stopped, Disposed, Abandoned

## TestKit.HarnessStoppedException `0.2.1` — The harness's tick thread was given up on (a test blocked it and the watchdog stopped the harness): no call works any more.

## TestKit.HarnessStorageCopy `0.2.16` — One storage of a restored copy in a test (`HarnessPlayerStorage.Restore`).
- `HarnessStorageCopy(string FullKey, int Slots, IReadOnlyList<TestKit.HarnessStorageRecord> Records, bool Parked = false)`
- `FullKey { get; init; } -> string` — "\<mod id>:\<name>".
- `Parked { get; init; } -> bool` — Saved as parked (no mod had it registered then) rather than live.
- `Records { get; init; } -> IReadOnlyList<TestKit.HarnessStorageRecord>` — Its stacks.
- `Slots { get; init; } -> int` — The slots it had when it was saved.
