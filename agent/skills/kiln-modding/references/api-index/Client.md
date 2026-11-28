# Client (Kiln.Client)

## BuildingAction `0.2.13` — What one building input request does (kiln_client.h's `KILN_BUILDING_ACTION_*`).
enum: Select, Place, Remove, Undo, Dismantle

## BuildingInput `0.2.13` — The local player's ordinary building input: select a building item, place, remove, undo, dismantle, emitted through the game's own input path as if the player pressed it.
- `static Available { get; } -> bool` — True when building input works here: the capability row is available and this kiln.client has the members.
- `const Capability = "client.building_input" -> string` — The capability row: available only on a game client whose build has a verified input producer (ready for the first...
- `static Dismantle(EntityId prop, WorldPosition position, Quaternion rotation, Vector3? scale = null, uint expectedSession = 0) -> BuildingInputResult` — Dismantles the exact prop `prop` at the transform (the action is held for 1.2 s).
- `const MaxScale = 1024 -> float` — The largest scale component (each must be in (0, `BuildingInput.MaxScale`]).
- `static Place(WorldPosition position, Quaternion rotation, Vector3? scale = null, uint expectedSession = 0) -> BuildingInputResult` — The primary building action at the transform (the local player places what is selected).
- `const QueueTimeoutMilliseconds = 5000 -> int` — A queued request that the input path has not taken within this many milliseconds ends `BuildingInputState.TimedOut`.
- `static Remove(WorldPosition position, Quaternion rotation, Vector3? scale = null, uint expectedSession = 0) -> BuildingInputResult` — The secondary building action at the transform.
- `static Select(ItemId item, byte slot, ItemId material = default(ItemId), uint expectedSession = 0) -> BuildingInputResult` — Selects `item` (and `material`) in actionbar slot `slot` of the local player.
- `static Undo(uint expectedSession = 0) -> BuildingInputResult` — The game's own building undo.

## BuildingInputRefusal `0.2.13` — Why a building input submit was refused (`BuildingInputResult.Refusal`).
enum: None, Busy, Stale, NoLocalPlayer, ModUnloaded, Rejected

## BuildingInputRequest `0.2.13` — One submitted building input request (`BuildingInput`).
- `Action { get; } -> BuildingAction` — What the request does.
- `Cancel() -> bool` — Cancels the request when it is still the mod's latest.
- `ClientSession { get; } -> uint` — The client world session it was submitted for (`Client.ClientWorldSession`).
- `Id { get; } -> uint` — kiln.client's id of the request; never 0.
- `State { get; } -> BuildingInputState` — Where the request is now: a copy read from kiln.client (any thread, never waits).

## BuildingInputResult `0.2.13` — The answer of a `BuildingInput` submit.
- `BuildingInputResult(bool Ok, string? Reason, BuildingInputRefusal Refusal, BuildingInputRequest? Request)`
- `Ok { get; init; } -> bool` — True when the request was queued.
- `Reason { get; init; } -> string?` — Why not; null when queued.
- `Refusal { get; init; } -> BuildingInputRefusal` — Why not, as a value; `BuildingInputRefusal.None` when queued.
- `Request { get; init; } -> BuildingInputRequest?` — The queued request; null when refused.

## BuildingInputState `0.2.13` — Where a building input request is (kiln_client.h's `KILN_BUILDING_INPUT_*`).
enum: Unknown, Queued, Pressed, Dispatched, TimedOut, Cancelled

## Client — The game client: side, session state, the local player, and the client world's tick.
- `static Available { get; } -> bool` — True when the kiln.client service is there (any Kiln with hooks enabled).
- `static ClientWorldSession { get; } -> uint` — Client worlds since the game started (one per hosted or joined game).
- `static ClientWorldSteps { get; } -> ulong` — Client-world steps since the game started.
- `static HasLocalPlayer { get; } -> bool` — Whether `Client.LocalPlayer` can be known on this game build.
- `static InGame { get; } -> bool` — In game (hosting or joined).
- `static IsClient { get; } -> bool` — True in the game client (enshrouded.exe), false on a dedicated server.
- `static IsClientThread { get; } -> bool` — True on the thread that steps the client world (the game's main thread).
- `static IsDedicatedServer { get; } -> bool` — True on a dedicated server (enshrouded_server.exe).
- `static IsHosting { get; } -> bool` — This client hosts the world (single player or co-op host).
- `static IsRemote { get; } -> bool` — This client joined a world hosted elsewhere.
- `static LocalPlayer { get; } -> EntityId` — The local player's entity, `EntityId.None` in the menu, while loading, before the player spawned, or on a server.
- `static NextTick() -> Client.NextTickAwaitable` — `await Client.NextTick()` continues on the main thread at the next client-world step.
- `static Post(Action work) -> void` — Runs `work` on the game's main thread before the client world's next step.
- `static ServerWorldSession { get; } -> uint` — Server worlds since the game started (one per hosted game).
- `static State { get; } -> ClientState` — What runs right now.
- `static Tick -> TickHandler?` — One client-world step (about 60 a second while in game; nothing in the menu), on the game's main thread.

## Client.NextTickAwaitable — What `Client.NextTick` returns: await it to continue on the main thread at the next client-world step.
- `GetAwaiter() -> Client.NextTickAwaitable.Awaiter` — The awaiter that posts the continuation to the next client-world step.

## Client.NextTickAwaitable.Awaiter — The awaiter of `NextTickAwaitable`.
- `GetResult() -> void` — Nothing to return: awaiting only waits for the next step.
- `IsCompleted { get; } -> bool` — Always false: the continuation never runs inline.
- `OnCompleted(Action continuation) -> void` — Queues `continuation` to run on the main thread at the next client-world step.
- `UnsafeOnCompleted(Action continuation) -> void` — The same as `Awaiter.OnCompleted`: queues `continuation` for the next client-world step.

## ClientState — What runs in this process.
enum: Menu, DedicatedServer, Hosting, Remote

## Crosshair `0.2.22` — The game client's own crosshair hit, read-only: hit or miss, world position, surface normal, distance, the hit entity and the voxel material, with the client session...
- `static Available { get; } -> bool` — True when the crosshair can be read here: the capability row is on and kiln.client.crosshair is there.
- `const BusyRetries = 3 -> int` — How often `Crosshair.Read` asks again when a sample was being published.
- `const Capability = "client.crosshair" -> string` — The capability row: available on a game client whose build has the game's crosshair trace verified (hooked at the...
- `static FreshFor { get; } -> TimeSpan` — A sample older than this is stale (kiln_client_crosshair.h's `KILN_CROSSHAIR_FRESH_NS`: about 15 client steps).
- `static Read(out CrosshairReason reason) -> CrosshairHit?` — The newest sample, fresh or not; null when there is none to give (`reason` says why).
- `static TryGet(out CrosshairHit hit) -> bool` — A fresh sample of the live client session, hit or miss.

## CrosshairHit `0.2.22` — One copy of the game's own crosshair hit: what the stock cursor and HUD see under the screen's centre, from the local player's camera along its forward axis.
- `Age { get; init; } -> TimeSpan` — How old the sample was when it was read, rounded up to a whole tick (100 ns).
- `ClientSession { get; init; } -> uint` — The client world session of the sample (`Client.ClientWorldSession`).
- `ClientStep { get; init; } -> ulong` — The client world's step count at the sample (`Client.ClientWorldSteps`).
- `Distance { get; init; } -> float?` — Metres from the trace's start to the hit.
- `Entity { get; init; } -> EntityId` — The game's entity of the hit (a creature, a prop, a placed object), `EntityId.None` for terrain and without a hit.
- `Equals(CrosshairHit other) -> bool`
- `GameStatus { get; init; } -> uint` — The game's own result byte: 0 hit, 31 nothing within reach, 21 no usable camera direction.
- `HasEntity { get; } -> bool` — True when the hit is an entity (`CrosshairHit.Entity` is not `EntityId.None`).
- `IsFresh { get; } -> bool` — True when the sample was at most `Crosshair.FreshFor` old when it was read.
- `IsHit { get; init; } -> bool` — The game's trace hit something within its reach.
- `Normal { get; init; } -> Vector3?` — The surface normal at the hit, unit length.
- `static operator !=(CrosshairHit left, CrosshairHit right) -> bool`
- `static operator ==(CrosshairHit left, CrosshairHit right) -> bool`
- `Position { get; init; } -> WorldPosition?` — The hit point in world metres: the game's own point, moved 1 cm either against the surface normal (into the surface...
- `Sequence { get; init; } -> ulong` — Game traces copied since the game started; never 0 for a sample.
- `VoxelMaterial { get; init; } -> byte?` — The voxel material id the game reports for the hit surface (terrain and voxel building cells; some colliders carry...

## CrosshairReason `0.2.22` — Why `Crosshair.Read` has no fresh sample (kiln_client_crosshair.h's `KILN_CROSSHAIR_REASON_*`).
enum: None, NoSample, Busy, Stale, Faulted

## GameAction `0.2.28` — The game's own digital actions, after its rebinding and controller mapping, decoded from the local player's input record.
enum: PrimaryAttack, SecondaryAttack, Block, Dodge, Jump, Interact

## GameInput `0.2.28` — Reads and changes the local player's game actions where the game has resolved its own input (rebinding, controllers, menus and focus included).
- `static Available { get; } -> bool` — A client with a local player, and the game's input sites resolved here (capability `client.game_input`).
- `static IsHeld(GameAction action) -> bool` — Whether `action` was held at the last sample.
- `static OnSample(GameInputHandler handler) -> IDisposable` — Calls `handler` for each local input sample until the returned handle is disposed.
- `static Repeat(GameAction action, TimeSpan interval, Func<bool>? whileHeld = null) -> IDisposable` — Presses and releases `action` every `interval` (at least 50 ms) while `whileHeld` is true (default: while the action...
- `static WasPressed(GameAction action) -> bool` — Whether `action` went down at the last sample.

## GameInputHandler `0.2.28` — Called for each input sample of the local player (`GameInput.OnSample`), on the game's main thread inside its input writer.
- `Invoke(ref GameInputSample sample) -> void`

## GameInputSample `0.2.28` — One input sample of the local player, in the game's input writer, after the game resolved it.
- `BowFullyDrawn { get; } -> bool` — A bow draw has reached its effective charge duration (ActionSpeed included).
- `HeldKind { get; } -> ActionKind?` — The `ActionKind` of the held item (Melee, Bow, Tool), or null.
- `IsHeld(GameAction action) -> bool` — The action as the game sampled it (before handlers).
- `Player { get; } -> EntityId` — The local player.
- `Press(GameAction action) -> void` — Sets `action` for this sample.
- `Release(GameAction action) -> void` — Clears `action` for this sample.
