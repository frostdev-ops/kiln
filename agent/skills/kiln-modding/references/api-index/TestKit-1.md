# TestKit (Kiln.TestKit): part 1 of 3

## TestKit.BootRun `0.2.3` — One OnBoot run of the kit (`HarnessBootPhase.Run`).
- `BootRun(string ModId, BootModState State, string? Reason, IReadOnlyList<TestKit.BootRunDefinition> Staged, IReadOnlyList<string> Definitions, IReadOnlyList<BootIssue> Issues)`
- `Definitions { get; init; } -> IReadOnlyList<string>` — What lowered into the generation, as `kind/key` (a data file's block of the same name is left out).
- `Issues { get; init; } -> IReadOnlyList<BootIssue>` — Its issues: CallbackFailed, BootCapOff, ShadowedByFile, ...
- `ModId { get; init; } -> string` — The mod.
- `Ok { get; } -> bool` — True when the mod's boot content is in (Committed or Staged).
- `Reason { get; init; } -> string?` — Why it failed (its OnBoot threw, a definition does not lower, mods.boot is off); null when it did not.
- `Staged { get; init; } -> IReadOnlyList<TestKit.BootRunDefinition>` — What its OnBoot staged, in order: kind, key and the canonical JSON.
- `State { get; init; } -> BootModState` — Its outcome: `Committed` (or `Staged` without commit), else `ExcludedCallback`.

## TestKit.BootRunDefinition `0.2.3` — One definition an OnBoot staged.
- `BootRunDefinition(string Kind, string Key, string Json)`
- `Json { get; init; } -> string` — The canonical JSON Kiln lowers.
- `Key { get; init; } -> string` — Its name.
- `Kind { get; init; } -> string` — The plural kind, as in blocks/*.json (`statuses`, `items`, ...).

## TestKit.CapabilityRow `0.2.1` — One capability row as a test sees it.
- `CapabilityRow()`
- `CapabilityRow(string Name, bool Available, string ReasonText)`
- `Available { get; init; } -> bool` — Whether it is available.
- `Name { get; init; } -> string` — The row's name: `kiln.<service>` for a Kiln service, a game feature's name, or a mod's own.
- `ReasonText { get; init; } -> string` — Why not (or, when available, where it comes from).

## TestKit.Coverage `0.2.1` — What the kit makes real, what it fakes and what is unavailable offline: the data the docs' table is made from.
- `static All { get; } -> IReadOnlyList<TestKit.CoverageRow>` — Every row: the explicit ones in order, then one per Kiln service no row names, by name.

## TestKit.CoverageKind `0.2.1` — How the kit stands behind an area of Kiln's API.
enum: Real, RecordingFake, Unavailable

## TestKit.CoverageRow `0.2.1` — One row of `Coverage.All`.
- `CoverageRow(string Area, TestKit.CoverageKind Kind, IReadOnlyList<string> Capabilities, string Note)`
- `Area { get; init; } -> string` — What a mod author would look for ("Events", "kiln.combat").
- `Capabilities { get; init; } -> IReadOnlyList<string>` — The capability names of the area (empty when it has none: Kiln.Api's own parts).
- `Kind { get; init; } -> TestKit.CoverageKind` — Real, a recording fake, or unavailable offline.
- `Note { get; init; } -> string` — One line: what holds and what does not.

## TestKit.FakeBlobRegistration `0.2.3` — A content blob a mod registered with the kit's kiln.assets (`Assets.AddBlob`, `OverrideBlob`).
- `FakeBlobRegistration()`
- `FakeBlobRegistration(string Hash, ulong Owner, bool Override)`
- `Hash { get; init; } -> string` — Its content hash as `ContentHash.ToString()` writes it (`size-h0-h1-h2`, hex).
- `Override { get; init; } -> bool` — True for an override of a hash, false for an added blob.
- `Owner { get; init; } -> ulong` — The native owner it was registered under (the mod's).

## TestKit.FakeCameraCall `0.2.17` — A camera call that reached the fake kiln.camera (one the service would have answered through its callback; a call it turns away at once is not recorded).
- `FakeCameraCall()`
- `FakeCameraCall(ulong Step, TestKit.FakeCameraCallKind Kind, ulong Lease, uint Endpoint, EntityId Player, uint Parts, uint DurationMs, int Priority, uint Refusal)`
- `DurationMs { get; init; } -> uint` — The request's duration; 0 for an end.
- `Endpoint { get; init; } -> uint` — 1 a server mode lock, 2 the local view, 3 a remote view (`CameraEndpoint`'s numbers).
- `Kind { get; init; } -> TestKit.FakeCameraCallKind` — Which call.
- `Lease { get; init; } -> ulong` — The lease's number: the new one for an apply.
- `Parts { get; init; } -> uint` — The request's parts as the service's bits: 1 mode, 2 path, 4 profile, 8 shake, 16 lens.
- `Player { get; init; } -> EntityId` — The player whose camera it is; none for the local view.
- `Priority { get; init; } -> int` — The request's priority; 0 for an end.
- `Refusal { get; init; } -> uint` — 0 when the fake accepted it, else the reason's number (`CameraReason`).
- `Step { get; init; } -> ulong` — The step it was made in.

## TestKit.FakeCameraCallKind `0.2.17` — What a camera call asked of the fake kiln.camera.
enum: Apply, Update, End

## TestKit.FakeChatLine `0.2.2` — A line in a fake player's chat (`FakePlayer.Chat`), or a server line a mod sent (`HarnessChat.Sent`).
- `ModId { get; } -> string?` — The mod that sent a server line; null for a player's typed line and for Kiln's own lines.
- `Speaker { get; } -> string?` — Who speaks it: a player's name for a typed line or `Chat.Say`; null for a notice (`Chat.Tell`, `Chat.Broadcast`...
- `Step { get; } -> ulong` — The harness step during which it was sent.
- `Text { get; } -> string` — The line as the game's chat shows it: a notice's text alone (Kiln's server lines start with `Chat.ServerPrefix`), a...
- `To { get; } -> TestKit.FakePlayer?` — The one player the line was for; null when it went to every player.

## TestKit.FakeChatMessage `0.2.2` — A line a fake player typed (`FakePlayer.Say`), as the chat's receive loop handed it over.
- `Consumed { get; } -> bool` — A handler or a command took it (`ChatMessage.Cancel`, a command line): nobody was shown it.
- `Player { get; } -> TestKit.FakePlayer?` — The sender; null when the line came from a connection with no player in game.
- `Step { get; } -> ulong` — The step whose start delivered it (the step after it was typed).
- `Text { get; } -> string` — The text as typed.

## TestKit.FakeClientSkillNode `0.2.22` — One node of the client's tree in a harness (`HarnessClientSkills.AddNode`).
- `ClassAlignment { get; set; } -> int` — Its class (0 Warrior, 1 Mage, 2 Ranger, 3 Neutral).
- `Cost { get; set; } -> int` — Its cost per level.
- `Id { get; } -> uint` — The node's id.
- `Level { get; set; } -> int` — The player's level: 0 locked.
- `MaxLevel { get; set; } -> int` — 0: one unlock.
- `Shadow { get; set; } -> bool` — A shadow node.
- `Type { get; set; } -> SkillNodeType` — Its type.
- `UiX { get; set; } -> float` — Its x position on the skill screen.
- `UiY { get; set; } -> float` — Its y position on the skill screen.

## TestKit.FakeClientStation `0.2.22` — One station of the client's cache in a harness (`HarnessClientStations.Add`).
- `Base { get; set; } -> BaseId` — The base the client grouped it under.
- `Choices { get; } -> List<CategoryChoice>` — The selected recipe's category choices (at most 4 are read).
- `CraftsLoaded { get; set; } -> int` — The complete crafts its inputs still hold after the cycle in progress.
- `Inventory { get; set; } -> List<StationStack>?` — The client world's copy of the station's inventory; null: the client has none (outputs unknown).
- `Needs { get; set; } -> StationNeeds` — What it lacks.
- `NeedsEnergy { get; set; } -> bool` — It runs on rotational energy.
- `Position { get; set; } -> WorldPosition` — Where it stands (read in 0.25 m steps).
- `Recipe { get; set; } -> uint` — The selected recipe; 0: none.
- `Run(uint recipe, int craftsLoaded, TimeSpan started) -> TestKit.FakeClientStation` — Runs `recipe`: state Running, its cycle started at `started`, with `craftsLoaded` more crafts loaded.
- `Started { get; set; } -> TimeSpan` — The cycle's start on the client's clock (`HarnessClientStations.Clock`).
- `State { get; set; } -> StationState` — Its state as the server sent it.
- `Station { get; } -> EntityId` — The station.
- `UseAllOptions { get; set; } -> bool` — "Use all ingredient options".
- `Water { get; set; } -> int` — Its water in Fuel units (64 per charge).
- `WaterMax { get; set; } -> int` — Its water capacity in Fuel units.
- `Workshop { get; set; } -> uint` — Its workshop.

## TestKit.FakeControlCall `0.2.3` — One call a mod made to the fake kiln.creature_control.
- `FakeControlCall(ulong Step, string Function, ulong Owner, EntityId Entity, ulong Lease, int Result, string Detail)`
- `Detail { get; init; } -> string` — The lease state or reason it answered, in words.
- `Entity { get; init; } -> EntityId` — The body's entity (0 when none was named).
- `Function { get; init; } -> string` — The table function (`bind_body`, `acquire_control`, `release_control`, `release_body`, `next_request`, `quarantine`...
- `Lease { get; init; } -> ulong` — The lease (0 when none).
- `Owner { get; init; } -> ulong` — The caller's native owner.
- `Result { get; init; } -> int` — The KilnResult it answered.
- `Step { get; init; } -> ulong` — The harness step.

## TestKit.FakeCraftSourceRegistration `0.2.15` — A mod's live source registration in the kit.
- `FakeCraftSourceRegistration(ulong Owner, EntityId Player, EntityId Container, CraftSourceOrder Order, CraftSourceFilter Filter, IReadOnlyList<ItemId> Items)`
- `Building { get; init; } -> bool` `0.2.21` — Registered with `CraftSourceOptions.Building`: block placements may count and pay from it.
- `Container { get; init; } -> EntityId` — The storage container.
- `Fills { get; init; } -> bool` `0.2.21` — Registered with `CraftSourceOptions.Fills`: production-station fills may take from it.
- `Filter { get; init; } -> CraftSourceFilter` — The item-set interpretation.
- `Items { get; init; } -> IReadOnlyList<ItemId>` — A copy of the filter items.
- `Locks { get; init; } -> bool` `0.2.21` — Registered with `CraftSourceOptions.Locks`: locks may count and pay from it.
- `Order { get; init; } -> CraftSourceOrder` — Its position relative to station outputs.
- `Owner { get; init; } -> ulong` — The registering native owner.
- `Player { get; init; } -> EntityId` — The only player it supplies.
- `Respec { get; init; } -> bool` `0.2.25` — Registered with `CraftSourceOptions.Respec`: the skill respec may count and pay its respec item from it.

## TestKit.FakeCrosshair `0.2.22` — Values the kit's crosshair fake gives every sample.
- `const Session = 1 -> uint` — The client session of every sample (`CrosshairHit.ClientSession`).

## TestKit.FakeEcall `0.2.4` — One ECALL of a game program: the native it calls and the call-site id.
- `FakeEcall()`
- `FakeEcall(string Native, uint Site)`
- `Native { get; init; } -> string` — The native's node name (`"applyDamage"`) or "0x" and its hash.
- `Site { get; init; } -> uint` — The ECALL's call-site id.

## TestKit.FakeGameNativeCall `0.2.4` — The call a game native answers in the fake (valid only while its handler runs).
- `ArgCount { get; } -> int` — The values the ECALL pops.
- `Bytes(int index) -> Span<byte>` — Argument `index`'s variable (empty for a literal or one not connected).
- `Get<T>(int index) -> T` — Argument `index`'s value.
- `Program { get; } -> uint` — The calling program.
- `Set<T>(int index, T value) -> bool` — Writes argument `index`'s variable (false for a literal or a smaller variable).
- `Site { get; } -> uint` — The call site.

## TestKit.FakeGameNativeHandler `0.2.4` — What a game native does when it runs in `HarnessImpactNatives.Call` (`HarnessImpactNatives.SetGameNative`): it may read and change its arguments' variables, and...
- `Invoke(TestKit.FakeGameNativeCall call) -> byte`

## `TestKit.FakeGameWriter<T>` `0.2.2` — What a program of the game does to a component every step (`FakeWorld.GameWrites`).
- `Invoke(EntityId entity, ref T value) -> void`

## TestKit.FakeHealth `0.2.2` — An entity's health in the fake kiln.combat (`HarnessCombat.Health`).
- `Current { get; } -> int` — Health points now.
- `Dead { get; } -> bool` — Dead: its health fell below 1 (and it was not healed or respawned since).
- `Max { get; } -> int` — The maximum.

## TestKit.FakeHit `0.2.2` — One hit, from a mod (`Combat.Damage`, `Kill`) or from the game (`HarnessCombat.Hit`), as it went (`HarnessCombat.Hits`).
- `Amount { get; } -> int` — The damage (negative: a healing hit), as the handlers left it.
- `Attacker { get; } -> EntityId` — The attacker (none when unknown).
- `FromGame { get; } -> bool` — A hit of the game (the test's `HarnessCombat.Hit`); false for a mod's.
- `Health { get; } -> (int Before, int After)` — The target's health before and after, once applied (-1 before).
- `Id { get; } -> ulong` — kiln.combat's id of the hit.
- `KillingBlow { get; } -> bool` — It took the target from above 0 to 0.
- `ModId { get; } -> string?` — The mod that asked; null for the game's hits (and a call outside any mod).
- `State { get; } -> TestKit.FakeHitState` — Where it is.
- `Step { get; } -> ulong` — The step during which it was dealt or asked for.
- `Target { get; } -> EntityId` — The target.

## TestKit.FakeHitState `0.2.2` — Where a hit is (`FakeHit.State`), as kiln.combat reports it.
enum: Queued, Held, Applied, Dropped

## TestKit.FakeImpactArg `0.2.4` — One value an ECALL pops (`FakeImpactCall`): a variable of the instance of an impact type, a raw literal, or nothing connected.
- `static Bool(bool value) -> TestKit.FakeImpactArg` — A bool variable (one byte).
- `static Entity(EntityId value) -> TestKit.FakeImpactArg` — An ecs.EntityId variable.
- `static Float(float value) -> TestKit.FakeImpactArg` — A float32 variable.
- `static Int(int value) -> TestKit.FakeImpactArg` — An sint32 variable.
- `static Literal(uint value) -> TestKit.FakeImpactArg` — A raw value the program pushes (a literal, not a variable).
- `static NotConnected { get; } -> TestKit.FakeImpactArg` — An argument the program leaves not connected (0).
- `static Time(TimeSpan value) -> TestKit.FakeImpactArg` — A Time variable (int64 nanoseconds).
- `static UInt(uint value) -> TestKit.FakeImpactArg` — A uint32 variable.
- `static Variable(string type, ReadOnlySpan<byte> bytes) -> TestKit.FakeImpactArg` — A variable of impact type `type` holding `bytes`.
- `static Variable<T>(string type, T value) -> TestKit.FakeImpactArg` — A variable of impact type `type` (`"sint32"`, `"impact.DamageSetup"`, ...) holding `value`'s bytes.
- `static Vector(Vector3 value) -> TestKit.FakeImpactArg` — A float3 variable.

## TestKit.FakeImpactCall `0.2.4` — One ECALL for `HarnessImpactNatives.Call`.
- `FakeImpactCall(string native, params TestKit.FakeImpactArg[] args)`
- `Args { get; } -> IReadOnlyList<TestKit.FakeImpactArg>` — The values it pops.
- `ImpactId { get; init; } -> uint` — The instance's id.
- `Native { get; } -> string` — The native.
- `Program { get; init; } -> uint` — The calling program's id (FNV-1a-32 of its GUID).
- `Self { get; init; } -> EntityId` — The instance's own entity.
- `Site { get; init; } -> uint` — The ECALL's call-site id.

## TestKit.FakeImpactCallRecord `0.2.4` — One ECALL the test made (`HarnessImpactNatives.Call`).
- `FakeImpactCallRecord(ulong Step, string Native, uint Hash, uint Program, uint Site, byte Error, byte Value, int Handlers)`
- `Error { get; init; } -> byte` — The ECALL's error (0 ok).
- `Handlers { get; init; } -> int` — The handlers that ran (a sync native's, the attachments').
- `Hash { get; init; } -> uint` — The native's hash.
- `Native { get; init; } -> string` — The native's name (a Kiln native's full name, a game native's node name) or its hash.
- `Program { get; init; } -> uint` — The calling program's id.
- `Site { get; init; } -> uint` — The call site.
- `Step { get; init; } -> ulong` — The harness step.
- `Value { get; init; } -> byte` — What it pushed.

## TestKit.FakeImpactResult `0.2.4` — What an ECALL answered (`HarnessImpactNatives.Call`).
- `Arg<T>(int index) -> T` — Argument `index`'s value after the call.
- `Bytes(int index) -> ReadOnlySpan<byte>` — Argument `index`'s variable after the call (empty for a literal or one not connected).
- `Error { get; } -> byte` — The ECALL's error: 0 ok; 0x11 the handler threw; 0x18 a required argument not connected; 0x1F a native the registry...
- `Handlers { get; } -> int` — The handlers that ran (a Kiln native's; the attachments that entered).
- `OriginalCalled { get; } -> bool` — The game's native ran.
- `Value { get; } -> byte` — What the ECALL pushed (when `FakeImpactResult.Error` is 0).

## TestKit.FakeInventory `0.2.2` — The inventory of a fake player (`FakePlayer.Inventory`) or of a container (`HarnessItems.AddContainer`), as the fake kiln.items keeps it.
- `Clear() -> void` — Empties every slot, as if the game had (gear's item entities go too).
- `Count(ItemId item) -> int` — How many of `item` the inventory holds.
- `Count(string name) -> int` — How many of the item named `name` (as `HarnessItems.AddItem` named it) it holds.
- `Kind { get; set; } -> InventoryOwnerKind` `0.2.17` — What kiln.items.inventory_kinds answers for this container (`Items.FindInventoryOwners`)...
- `Owner { get; } -> EntityId` — The entity whose inventory it is.
- `Put(ItemId item, int count = 1) -> int` — Puts `count` x `item` in, as if the game had (stacks first, then empty slots; no transaction, no event).
- `Slots { get; } -> IReadOnlyList<TestKit.FakeSlot>` — Every slot, empty ones included, in the order `Items.Read` lists them.

## TestKit.FakeInventorySlot `0.2.12` — A slot as the game would draw it: the item, the stack, the item's entity (the server's id of it: what `SlotClick.ItemEntity` carries), the inventory that holds it...
- `FakeInventorySlot()`
- `FakeInventorySlot(uint Item, uint Count = 1, uint ItemEntity = 0, uint Holder = 0, uint InventoryPage = 0, byte SlotIndex = 0)`
- `Count { get; init; } -> uint` — The stack size (default 1).
- `Deconstruct(out uint Item, out uint Count, out uint ItemEntity, out uint Holder, out uint InventoryPage, out byte SlotIndex) -> void`
- `Equals(TestKit.FakeInventorySlot other) -> bool`
- `Holder { get; init; } -> uint` — The entity of the inventory that holds the item (0: none).
- `InventoryPage { get; init; } -> uint` — The inventory page the slot is on (an entity id; 0: none).
- `Item { get; init; } -> uint` — The item's id.
- `ItemEntity { get; init; } -> uint` — The server's entity id of the item: what `SlotClick.ItemEntity` carries (0: none).
- `Level { get; init; } -> int` `0.2.14` — The item's level as drawn.
- `static operator !=(TestKit.FakeInventorySlot left, TestKit.FakeInventorySlot right) -> bool`
- `static operator ==(TestKit.FakeInventorySlot left, TestKit.FakeInventorySlot right) -> bool`
- `Rarity { get; init; } -> ItemRarity?` `0.2.14` — The item's rarity as drawn (null: none).
- `SlotIndex { get; init; } -> byte` — The slot's index on its page.

## TestKit.FakeItemsCall `0.2.2` — One give or take a mod asked for (`HarnessItems.Calls`), as it ran.
- `Done { get; } -> int` — How many were given or taken (a dry run: would be).
- `DryRun { get; } -> bool` — A dry run: nothing changed.
- `Entity { get; } -> EntityId` — Whose inventory.
- `IsTake { get; } -> bool` — A take; false for a give.
- `Item { get; } -> ItemId` — The item.
- `ModId { get; } -> string?` — The mod that asked; null for a call outside any mod.
- `Requested { get; } -> int` — How many were asked for.
- `Status { get; } -> ItemsStatus` — The result's status.
- `Step { get; } -> ulong` — The step whose start ran the transaction (the step after the request).

## TestKit.FakeOutline `0.2.13` — An outline request as the kit's kiln.render holds it (`HarnessOutlines`).
- `FakeOutline(EntityId Entity, Color Color, bool Pulse, int Priority, TimeSpan? Duration, OutlineState State, OutlineReason Reason, int Group, bool ColorShared, string Owner)`
- `Color { get; init; } -> Color` — The colour asked for.
- `ColorShared { get; init; } -> bool` — A third colour: it shows with the nearest other colour's group.
- `Duration { get; init; } -> TimeSpan?` — How long it lasts (null: until removed).
- `Entity { get; init; } -> EntityId` — The outlined entity.
- `Group { get; init; } -> int` — The game's highlight group its colour uses (4 or 5) while shown, 0 otherwise.
- `Owner { get; init; } -> string` — Who asked: the mod's id, or `kiln`.
- `Priority { get; init; } -> int` — The request's priority.
- `Pulse { get; init; } -> bool` — The colour breathes.
- `Reason { get; init; } -> OutlineReason` — Why it waits or ended.
- `State { get; init; } -> OutlineState` — Its state now.
- `ThroughWalls { get; init; } -> bool` `0.2.14` — Asked with `OutlineOptions.ThroughWalls` (Kiln's silhouette through walls; its drawing is not modelled).

## TestKit.FakeOutlineSetting `0.2.13` — The player's kiln.json `"ui": {"outlines": ...}` in the kit (`HarnessOutlines.Setting`).
enum: GameFirst, ModsFirst, Off

## TestKit.FakePathAnswer `0.2.3` — What the fake's path search answers: `FakePathAnswer.Route` (found when its last point is at the request's target, partial otherwise), or one of the game's results...
- `static IterationLimit { get; } -> TestKit.FakePathAnswer` — The search hit its iteration limit.
- `static NoPath { get; } -> TestKit.FakePathAnswer` — The game found no path.
- `static NoStartNode { get; } -> TestKit.FakePathAnswer` — The start has no node.
- `static NoTargetNode { get; } -> TestKit.FakePathAnswer` — The target has no node.
- `static NotLoaded { get; } -> TestKit.FakePathAnswer` — The game gave no answer (the area is not loaded).
- `static Route(params WorldPosition[] points) -> TestKit.FakePathAnswer` — A walking route through these points.
- `static RouteWithMove(byte move, params WorldPosition[] points) -> TestKit.FakePathAnswer` — A route with a step that is not walking (`move`: the game's move kind, 2 = jump up): the service reports it as...
- `static TimedOut { get; } -> TestKit.FakePathAnswer` — The request ran out of time.

## TestKit.FakePathRequest `0.2.3` — A path request a mod made (`Queries.RequestPath`, `FindPath`, `CanReach`).
- `FakePathRequest(EntityId Anchor, WorldPosition From, WorldPosition To, double HorizontalSpace, double VerticalSpace, uint MaxIterations, bool ClosestFallback)`
- `Anchor { get; init; } -> EntityId` — The entity the game's path query was made for.
- `ClosestFallback { get; init; } -> bool` — Whether the closest node may be used when the target has none.
- `From { get; init; } -> WorldPosition` — Where the route starts.
- `HorizontalSpace { get; init; } -> double` — The free space the route needs sideways, in metres.
- `MaxIterations { get; init; } -> uint` — The search's iteration limit.
- `To { get; init; } -> WorldPosition` — Where the route should end.
- `VerticalSpace { get; init; } -> double` — The free space the route needs upwards, in metres.

## TestKit.FakePhysicsCookHost `0.2.17` — A stand-in for `kiln cook`'s physics step in unit tests: the two cook features (`modelcook.cloth`, `modelcook.chains2`, on since 0.2.0-dev.31...
- `FakePhysicsCookHost(params string[] experimental)` — A host for a recipe whose `"experimental"` key lists `experimental`.
- `static ContentHashOf(byte[] bytes) -> string` — A new content blob's hash as kiln.assets derives it: the size and the first 12 bytes of SHA-256 as three...
- `Cook(string model, IEnumerable<string> needs, IReadOnlyDictionary<string, byte[]> outputs, string? refusal = null) -> TestKit.PhysicsCookResult` — One model's cook: refused (nothing committed, nothing registered) when a feature it `needs` is off or when `refusal`...
- `static Features { get; } -> IReadOnlyList<string>` — The cook features of physics, as the cook names them.
- `Files { get; } -> IReadOnlyDictionary<string, byte[]>` — The files committed so far, by path in the mod.
- `Held { get; init; } -> IReadOnlyCollection<string>` — The features this host holds (off unless the recipe's `"experimental"` key names them), as a release that holds a...
- `IsOn(string feature) -> bool` — Whether `feature` is on for this host's recipe: on unless held, and a held one when the recipe names it.
- `const PendingReason = "game test pending: 16e-cc16e" -> string` — Why a held feature is off by default (the same text kiln.caps shows for a held row).
- `Registrations { get; } -> IReadOnlyList<string>` — What kiln.assets registered from them, in order: `blob <size>-<h0>-<h1>-<h2>` for each `.blob`...

## TestKit.FakePlayer `0.2.1` — A player the test joined (`HarnessPlayers.Join`).
- `Chat { get; } -> IReadOnlyList<TestKit.FakeChatLine>` `0.2.2` — What this player's chat showed, in order: server lines mods sent them (`Chat.Tell`, `Chat.Broadcast`, command...
- `Entity { get; } -> EntityId` — The player's entity: slot + 1, as on this game build.
- `InGame { get; } -> bool` — In game: from the step after the join until the step after the leave.
- `Inventory { get; } -> TestKit.FakeInventory` `0.2.2` — This player's inventory in the fake kiln.items (`HarnessItems`): made at its first use while the player is in game...
- `IsAdmin { get; } -> bool` — The game's admin bit.
- `Leave() -> bool` — The player disconnects.
- `Name { get; } -> string` — The platform name.
- `Position { get; set; } -> WorldPosition?` — Where the player stands.
- `Rights { get; } -> PlayerPermissions` — The user group's rights.
- `Say(string text) -> void` `0.2.2` — The player types `text` in chat (a command line starts with `Commands.Prefix`).
- `Slot { get; } -> int` — The game's player slot (0..15), the lowest one free at the join.
- `SteamId { get; } -> ulong` — SteamID64 (the one given to `HarnessPlayers.Join`, or a made-up one).
- `UserGroupId { get; } -> uint` — The game's raw user-group id (0: unknown).

## TestKit.FakeProgramLists `0.2.4` — A fake program's descriptor lists: the game's component names (`"Fuel"`), singletons with their namespace.
- `AnyOf { get; init; } -> IReadOnlyList<string>` — any_of.
- `EventsIn { get; init; } -> IReadOnlyList<string>` — events_in.
- `EventsOut { get; init; } -> IReadOnlyList<string>` — events_out.
- `Flags { get; init; } -> uint` — The descriptor's flags (0x1: a client program, which has no update in a server world).
- `Interval { get; init; } -> uint` — The descriptor's interval.
- `OtherRead { get; init; } -> IReadOnlyList<string>` — other_read.
- `OtherTemplate { get; init; } -> IReadOnlyList<string>` — other_tpl.
- `OtherWrite { get; init; } -> IReadOnlyList<string>` — other_write.
- `Read { get; init; } -> IReadOnlyList<string>` — read.
- `ReadOptional { get; init; } -> IReadOnlyList<string>` — read_opt.
- `SingletonsRead { get; init; } -> IReadOnlyList<string>` — sys_read (singletons; the fake has none: their slots are empty).
- `SingletonsWrite { get; init; } -> IReadOnlyList<string>` — sys_write.
- `Template { get; init; } -> IReadOnlyList<string>` — tpl.
- `TemplateOptional { get; init; } -> IReadOnlyList<string>` — tpl_opt.
- `With { get; init; } -> IReadOnlyList<string>` — with.
- `Without { get; init; } -> IReadOnlyList<string>` — without.
- `Write { get; init; } -> IReadOnlyList<string>` — write.
- `WriteOptional { get; init; } -> IReadOnlyList<string>` — write_opt.

## TestKit.FakeProgramMark `0.2.4` — A write a wrap's stage marked for the save and replication (the game's MarkWritten).
- `FakeProgramMark()`
- `FakeProgramMark(string Program, EntityId Entity, string Component, ulong Step)`
- `Component { get; init; } -> string` — The component's game name.
- `Entity { get; init; } -> EntityId` — The entity.
- `Program { get; init; } -> string` — The wrapped program.
- `Step { get; init; } -> ulong` — The world's step.

## TestKit.FakeProgramSlice `0.2.4` — What a fake program's update sees: one slice of its program (one archetype's matching entities).
- `FakeProgramSlice(string Program, IReadOnlyList<EntityId> Entities, ulong Step)`
- `Entities { get; init; } -> IReadOnlyList<EntityId>` — The slice's entities, in row order.
- `Program { get; init; } -> string` — The program.
- `Step { get; init; } -> ulong` — The world's step.

## TestKit.FakeReplComponent `0.2.17` — A component of the fake world's registry (`HarnessRepl.AddComponent`): what `Replication.Component` reads, and how a message's component is named and flagged.
- `FakeReplComponent(string Name, ushort Index, ushort Size, bool Replicated = true, bool Versioned = false, bool CustomCodec = false, bool ClientChannel = true, bool PredictionChannel = false)`
- `ClientChannel { get; init; } -> bool` — It may travel on the client channel.
- `CustomCodec { get; init; } -> bool` — The game packs it with a codec of its own (ReplSent.PackedByGame).
- `Index { get; init; } -> ushort` — Its index in the registry.
- `Name { get; init; } -> string` — The short name (hashed as kiln.world's component_hash).
- `PredictionChannel { get; init; } -> bool` — It may travel on the prediction channel.
- `Replicated { get; init; } -> bool` — A tracked write queues the entity for the client channel.
- `Size { get; init; } -> ushort` — Bytes of one component.
- `Versioned { get; init; } -> bool` — A version byte travels with it.

## TestKit.FakeReplItemSite `0.2.17` — A field or strided array of little-endian item IDs in a scripted replication component.
- `FakeReplItemSite()`
- `FakeReplItemSite(string component, ushort offset, ushort count = 1, ushort stride = 0)`
- `Component { get; } -> string` — The component registered with `HarnessRepl.AddComponent`.
- `Count { get; } -> ushort` — How many 32-bit IDs this site covers.
- `Offset { get; } -> ushort` — The first item's byte offset.
- `Stride { get; } -> ushort` — Byte spacing between IDs when the count is greater than one.

## TestKit.FakeReplKind `0.2.17` — What a replication message did to an entity, as kiln.repl numbers it.
enum: Create, Update

## TestKit.FakeReplMessage `0.2.17` — One create or update message about an entity (`HarnessRepl.Send`, `HarnessRepl.Receive`).
- `FakeReplMessage(EntityId Entity, TestKit.FakeReplKind Kind, uint Tick)`
- `BaselineTick { get; init; } -> uint` — An update's baseline tick (0: the template).
- `Channel { get; init; } -> byte` — The channel: 4 (client, the default) or 5 (prediction).
- `Components { get; init; } -> IReadOnlyList<(string Component, byte[] Bytes)>` — The full values the message carried, by registry component name (`HarnessRepl.AddComponent`).
- `Entity { get; init; } -> EntityId` — The entity.
- `Gap { get; init; } -> bool` — Records were dropped before this one (also set by `HarnessRepl.Drop` for the next record).
- `Kind { get; init; } -> TestKit.FakeReplKind` — Create or update.
- `PayloadBytes { get; init; } -> uint` — The payload bytes as sent (also what the counters add).
- `Seq { get; init; } -> uint` — The message's number on its peer's channel.
- `Tick { get; init; } -> uint` — The server tick of the message.

## TestKit.FakeSlot `0.2.2` — One slot of a fake inventory (`FakeInventory.Slots`).
- `Count { get; } -> int` — How many.
- `IsEmpty { get; } -> bool` — The slot is empty.
- `Item { get; } -> ItemId` — The item; none when the slot is empty.
- `ItemEntity { get; } -> EntityId` — Gear: its item entity in the fake world; none for a stack.
- `Level { get; } -> int` — Gear: its level; 0 for a stack.
- `Rarity { get; } -> ItemRarity?` — Gear: its rarity; null for a stack and an empty slot.
- `Ref { get; } -> ItemInstanceRef` `0.2.12` — Gear: the item as one particular item, with the stamp the kit's kiln.items gave its item entity (what...
- `Slot { get; } -> SlotId` — The slot (its 8-slot inventory's entity and index), as `Items.Read` reports it.

## TestKit.FakeSlotClickResult `0.2.12` — What a `HarnessUiInventory.Click` did: how many handlers got it, and whether the game would not have seen the press (a handler asked to take it).
- `FakeSlotClickResult()`
- `FakeSlotClickResult(int Delivered, bool Taken)`
- `Delivered { get; init; } -> int` — How many handlers got the click.
- `Taken { get; init; } -> bool` — True when a handler asked to take the press, so the game would not have seen it.

## TestKit.FakeSpawnCall `0.2.2` — One call a mod made to kiln.spawn that changes the world (`HarnessSpawn.Calls`), with its answer.
- `Code { get; } -> int` — The native answer: 0 ok, 1 invalid argument, 2 not found (an unknown template, no such entity), 3 unavailable.
- `Entity { get; } -> EntityId` — The new or destroyed entity; none for a refused spawn and a cleanup.
- `Kind { get; } -> TestKit.FakeSpawnKind` — What was asked.
- `ModId { get; } -> string?` — The mod that asked; null for a call outside any mod.
- `Position { get; } -> WorldPosition?` — Where a spawn was asked for; null for a destroy or a cleanup.
- `Step { get; } -> ulong` — The harness step during which the call ran.
- `Subject { get; } -> string` — The template's name or GUID text as the mod gave it, `item 0x... xN`, `entity N`, or the cleanup's filter.
- `Tag { get; } -> string` — The spawn's tag (an item drop's is "item" by default), the destroyed entity's, or the cleanup's filter.

## TestKit.FakeSpawned `0.2.2` — An entity Kiln.Spawn put into the fake world (`HarnessSpawn.All`).
- `Amount { get; } -> int` — An item drop's amount; 0 for a template spawn.
- `Entity { get; } -> EntityId` — The entity.
- `Flags { get; } -> SpawnFlags` — The spawn's flags.
- `ItemId { get; } -> uint` — An item drop's item; 0 for a template spawn.
- `Kind { get; } -> TemplateKind` — The template's kind.
- `Level { get; } -> int` — The level asked for (0: the template's own).
- `Origin { get; } -> string` — The mod that spawned it ("" outside any mod).
- `Position { get; } -> WorldPosition?` — Where it stands now (its `CurrentTransform` in the fake world); null when it is gone.
- `Serial { get; } -> ulong` — Kiln's tracked-record serial; 0 for a spawn with `SpawnFlags.NoTrack`.
- `SpawnPosition { get; } -> WorldPosition` — Where it was spawned.
- `Step { get; } -> ulong` — The harness step during which it was spawned.
- `Tag { get; } -> string` — The spawn's tag ("" when none; "item" for an item drop without one).
- `Template { get; } -> string` — The template's name (`HarnessSpawn.PickupTemplate` for an item drop).
- `TemplateId { get; } -> Guid` — The template's GUID.
- `YawDegrees { get; } -> float` — The rotation about the up axis it was spawned with.

## TestKit.FakeSpawnKind `0.2.2` — What a spawn call was (`FakeSpawnCall.Kind`).
enum: Entity, Item, Destroy, Cleanup

## TestKit.FakeTerrainWrite `0.2.3` — One write an overlay made (a batch, or a single block when a base was in the way).
- `FakeTerrainWrite()`
- `FakeTerrainWrite(string Mod, string Label, CellBox Box, long CellsChanged, bool Transient, ulong Step)`
- `Box { get; init; } -> CellBox` — The cells the write covered.
- `CellsChanged { get; init; } -> long` — How many cells the write changed.
- `Label { get; init; } -> string` — "terrain \<plan> v\<version>".
- `Mod { get; init; } -> string` — The mod that applied the patch ("kiln" from code that is not a mod's).
- `Step { get; init; } -> ulong` — The harness step it was made in.
- `Transient { get; init; } -> bool` — The write is transient: `HarnessTerrain.Restart` puts the cells it changed back.

## TestKit.FakeTimeCall `0.2.2` — A change to the clock a mod made (`HarnessTime.Calls`).
- `ModId { get; } -> string?` — The mod that asked; null for a call outside any mod.
- `Step { get; } -> ulong` — The step during which it ran.
- `Value { get; } -> double` — The time of day set (0..24), or the speed held (1 when released).
- `What { get; } -> string` — `set` (forward), `set game` (the game's setTimeOfDay), `speed` or `speed released`.

## TestKit.FakeUiBar `0.2.3` — A drawn value: the fraction, the numbers and the label (null: the game's own).
- `FakeUiBar()`
- `FakeUiBar(float Fraction, uint Current, uint Max, string? Label = null)`
- `Current { get; init; } -> uint` — The current number.
- `Deconstruct(out float Fraction, out uint Current, out uint Max, out string? Label) -> void`
- `Equals(TestKit.FakeUiBar other) -> bool`
- `Fraction { get; init; } -> float` — How full the bar is, as a fraction.
- `Label { get; init; } -> string?` — The label text; null leaves the game's own.
- `Max { get; init; } -> uint` — The maximum number.
- `static operator !=(TestKit.FakeUiBar left, TestKit.FakeUiBar right) -> bool`
- `static operator ==(TestKit.FakeUiBar left, TestKit.FakeUiBar right) -> bool`

## TestKit.FakeUiDecision `0.2.3` — What one provider did with a value: `Result` is its native result (0 ok), `Edit` the fields it committed (KilnUiEditFlags), `Invalid` when the fake refused the edit...
- `FakeUiDecision()`
- `FakeUiDecision(uint Kind, int Result, uint Edit, bool Invalid)`
- `Edit { get; init; } -> uint` — The fields the provider committed, as the native edit flags (0 when it changed nothing or its edit was refused).
- `Invalid { get; init; } -> bool` — True when the fake refused the provider's edit as the game would, counting it as a fault.
- `Kind { get; init; } -> uint` — The kind of value the provider was asked about (the numbering of `UiValueKind`: detail, slot, held, health, stamina...
- `Result { get; init; } -> int` — The provider's native result; 0 is ok.

## TestKit.FakeUiItem `0.2.3` — An item as the game's UI would show it to a provider (the fields of `UiItemView` a test sets).
- `FakeUiItem()`
- `FakeUiItem(uint Item, uint Fuel = 0, uint FuelMax = 0, uint Count = 1, uint InventoryPage = 0, byte SlotIndex = 0, uint ClientEntity = 0, uint Flags = 0, uint UnitsPerCharge = 1)`
- `ClientEntity { get; init; } -> uint` — The stack's client-world entity; valid with flag 1.
- `Count { get; init; } -> uint` — The stack size (default 1).
- `Deconstruct(out uint Item, out uint Fuel, out uint FuelMax, out uint Count, out uint InventoryPage, out byte SlotIndex, out uint ClientEntity, out uint Flags, out uint UnitsPerCharge) -> void`
- `Equals(TestKit.FakeUiItem other) -> bool`
- `Flags { get; init; } -> uint` — Which identity fields are valid: 1 client entity, 4 inventory slot, 8 detail copy, 16 synthetic.
- `Fuel { get; init; } -> uint` — The item's fuel, in raw fuel units.
- `FuelMax { get; init; } -> uint` — The item's largest fuel, in raw fuel units.
- `InventoryPage { get; init; } -> uint` — The inventory page of the slot (an entity id); valid with flag 4.
- `Item { get; init; } -> uint` — The item's id.
- `static operator !=(TestKit.FakeUiItem left, TestKit.FakeUiItem right) -> bool`
- `static operator ==(TestKit.FakeUiItem left, TestKit.FakeUiItem right) -> bool`
- `SlotIndex { get; init; } -> byte` — The slot's index on its page; valid with flag 4.
- `UnitsPerCharge { get; init; } -> uint` — The detail view's divisor for fuel (at least 1; default 1).

## TestKit.FakeVitals `0.2.17` — The local player's health, stamina and mana and their maxima, as the game has them.
- `FakeVitals()`
- `FakeVitals(uint Health, uint HealthMax, uint Stamina = 0, uint StaminaMax = 0, uint Mana = 0, uint ManaMax = 0)`
- `Deconstruct(out uint Health, out uint HealthMax, out uint Stamina, out uint StaminaMax, out uint Mana, out uint ManaMax) -> void`
- `Equals(TestKit.FakeVitals other) -> bool`
- `Health { get; init; } -> uint` — Current health.
- `HealthMax { get; init; } -> uint` — Maximum health.
- `Mana { get; init; } -> uint` — Current mana.
- `ManaMax { get; init; } -> uint` — Maximum mana.
- `static operator !=(TestKit.FakeVitals left, TestKit.FakeVitals right) -> bool`
- `static operator ==(TestKit.FakeVitals left, TestKit.FakeVitals right) -> bool`
- `Stamina { get; init; } -> uint` — Current stamina.
- `StaminaMax { get; init; } -> uint` — Maximum stamina.

## TestKit.FakeVoxelAnswer `0.2.3` — What the fake answers a voxel ray: `FakeVoxelAnswer.Clear`, `FakeVoxelAnswer.Hit`, or `FakeVoxelAnswer.Refused` with one of the service's reasons (`NotLoaded`...
- `static Clear { get; } -> TestKit.FakeVoxelAnswer` — Loaded, and nothing on the segment.
- `Equals(TestKit.FakeVoxelAnswer other) -> bool`
- `static Hit(WorldPosition at, uint material = 1, ushort voxel = 65281) -> TestKit.FakeVoxelAnswer` — The first voxel surface is at `at`.
- `static operator !=(TestKit.FakeVoxelAnswer left, TestKit.FakeVoxelAnswer right) -> bool`
- `static operator ==(TestKit.FakeVoxelAnswer left, TestKit.FakeVoxelAnswer right) -> bool`
- `static Refused(string reason) -> TestKit.FakeVoxelAnswer` — Nothing is known, for `reason`: `NotLoaded`, `OutOfGrid`.

## TestKit.FakeVoxelRay `0.2.3` — A voxel ray a mod asked for (`Queries.RaycastVoxels`, `GroundBelow`, `VoxelLineOfSight`, a brain's sight).
- `FakeVoxelRay()`
- `FakeVoxelRay(WorldPosition From, WorldPosition To, byte Realm)`
- `From { get; init; } -> WorldPosition` — Where the ray starts.
- `Realm { get; init; } -> byte` — The realm the ray was cast in (0: the overworld).
- `To { get; init; } -> WorldPosition` — Where the ray ends.
