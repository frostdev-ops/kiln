# Api (Kiln.Api)

## AdvancedApiAttribute — An advanced API: the mod's manifest needs `advanced: true` (analyzer rule).

## AnyThreadAttribute — Safe to call from any thread.

## BootContext `0.2.3` — What `Mod.OnBoot` gets: the mod's boot-author frame.
- `GameData { get; } -> IReadOnlyGameData` — The offline view of the game's data.
- `Generation { get; } -> ulong` — The process's boot generation.
- `ModId { get; } -> string` — The author's mod id.
- `Roles { get; } -> BootRoles` — Every role the mod may take in this process.

## BootDataResult `0.2.3` — What `IReadOnlyGameData.Read` found.
- `BootDataResult(bool Ok, string? Reason, BootDataValue? Data)`
- `Data { get; init; } -> BootDataValue?` — The record.
- `Ok { get; init; } -> bool` — True when `BootDataResult.Data` holds the record.
- `Reason { get; init; } -> string?` — Why not, in plain words; null when `BootDataResult.Ok`.

## BootDataValue `0.2.3` — One record of the game's data as the offline view read it.
- `BootDataValue(Guid ResourceId, uint TypeHash, int Part, Json.JsonElement Value)`
- `Part { get; init; } -> int` — The resource part.
- `ResourceId { get; init; } -> Guid` — The resource it came from.
- `TypeHash { get; init; } -> uint` — The resource type's hash.
- `Value { get; init; } -> Json.JsonElement` — The whole record as camelCase JSON (detached: valid after the frame).

## BootIssue `0.2.3` — One issue of the boot generation.
- `BootIssue(string ModId, string Kind, string Key, string Phase, string Code, string Reason)`
- `Code { get; init; } -> string` — What happened: DuplicateDefinition, CallbackFailed, Dependency, PrepareRefused, ShadowedByFile, ...
- `Key { get; init; } -> string` — The definition's key, or empty.
- `Kind { get; init; } -> string` — The definition's kind (plural, as in blocks/*.json), or empty.
- `ModId { get; init; } -> string` — The mod it is about (empty: none).
- `Phase { get; init; } -> string` — Where it happened: stage, callback, prepare, commit.
- `Reason { get; init; } -> string` — In plain words.

## BootModReport `0.2.3` — A mod's boot state, its committed definitions (`kind/key`) and its issues.
- `BootModReport(string ModId, ulong Generation, BootModState State, IReadOnlyList<string> Definitions, IReadOnlyList<BootIssue> Issues)`
- `Definitions { get; init; } -> IReadOnlyList<string>` — What it staged and the generation holds, as `kind/key`.
- `Generation { get; init; } -> ulong` — The process's boot generation (0: none).
- `Issues { get; init; } -> IReadOnlyList<BootIssue>` — Its issues.
- `ModId { get; init; } -> string` — The mod.
- `State { get; init; } -> BootModState` — Its part in it.

## BootModState `0.2.3` — A mod's part in this process's boot generation (`Mods.BootState`).
enum: NotParticipating, Staged, Committed, ExcludedCallback, ExcludedDependency, RejectedPrepare, GenerationAborted, MissedPhase, ActivationFault

## BootRoles `0.2.3` — Where a boot author runs, as the boot graph resolved it (a game client authors for every role it may take).
enum: DedicatedServer, HostedServer, Client

## BootThreadOnlyAttribute `0.2.3` — Call this only inside `Mod.OnBoot`, on the boot thread, before the game's entry point (a boot-author frame); no tick, world or player exists there.

## CancelableEvent — An event whose default outcome a handler can stop by setting `CancelableEvent.Canceled`.
- `Cancel() -> void` — Cancels the event: sets `CancelableEvent.Canceled`.
- `Canceled { get; set; } -> bool` — Set by a handler to stop the default outcome.
- `CanceledBy { get; } -> string?` — Who canceled it: the id of the mod whose handler set `CancelableEvent.Canceled` ("kiln" for Kiln's own handlers)...

## CancelableResultEvent — A `CancelableEvent` that also carries an `EventResult`.
- `Allow() -> void` — Sets `CancelableResultEvent.Result` to `EventResult.Allow`.
- `Decide(bool vanilla) -> bool` — The decision, with `vanilla` for `EventResult.Default`.
- `Deny() -> void` — Sets `CancelableResultEvent.Result` to `EventResult.Deny`.
- `Result { get; set; } -> EventResult` — The decision so far; `EventResult.Default` until a handler sets one.

## Capabilities — What works in this process, on this game build and side, and why something does not: one row per capability name, filled by the Kiln core (every sigdb feature, every...
- `static All { get; } -> IReadOnlyList<Capability>` — Every row, sorted by name (ordinal).
- `static Get(string name) -> Capability` — The row of `name`.
- `static Is(string name) -> bool` — Whether `name` works here: its row is `CapabilityState.Available`.
- `static IsHeld(string name) -> bool` `0.2.3` — Whether `name` is held back until its game test (`Capability.IsHeld`).
- `static Why(string name) -> string` — One sentence about `name` a mod can log: the name, the state, the reason in words, the reason code and who said so...

## Capability — One row of the capability registry: a name, whether it works, and why not.
- `Capability()`
- `Capability(string Name, CapabilityState State, CapabilityReason Reason, string ReasonText, string Source)`
- `IsAvailable { get; } -> bool` — `Capability.State` is `CapabilityState.Available`.
- `IsHeld { get; } -> bool` `0.2.3` — The capability is built but held back until it has been tested in a real game: it is `CapabilityState.Unavailable`...
- `Name { get; init; } -> string` — The capability's name: a sigdb feature (`items.fuel`), `kiln.<service>` for a service table being here...
- `Reason { get; init; } -> CapabilityReason` — Why it is off or pending; `CapabilityReason.None` when it works.
- `ReasonText { get; init; } -> string` — The reason in words, for a log line; empty when there is nothing to say (an available row may carry a note, such as...
- `SessionDependent { get; init; } -> bool` — The row depends on the session: it was reported `CapabilityState.Pending` at least once.
- `Source { get; init; } -> string` — Who made the report that is shown: `sigdb` (a feature row), `core` (a service row the core derived), else the...
- `State { get; init; } -> CapabilityState` — Whether it works.

## CapabilityChangedEvent — A row of the capability registry changed: posted on the bus (tick thread, at the start of the next step) for each row that is new, changed or removed.
- `Current { get; } -> Capability` — The row now.
- `Name { get; } -> string` — The capability's name.
- `Previous { get; } -> Capability` — The row before the change.

## CapabilityReason — Why a capability is off or pending (`Capability.Reason`).
enum: None, SymbolMissing, ServiceMissing, ServiceOlder, WrongSide, NeedsKilnClient, Policy, Faulted, Offline, Session, Unknown

## CapabilityState — Whether a capability works (`Capability.State`).
enum: Unavailable, Available, Pending

## CapacityChangedEvent `0.2.4` — A budget changed: its capacity, readiness or one of its counts.
- `Current { get; } -> CapacityInfo` — The budget now.
- `Previous { get; } -> CapacityInfo` — The budget before.
- `Quota { get; } -> string` — The quota's name: the capability row of the query that reads it (for example `gamedata.buff_budget`).

## CapacityInfo `0.2.4` — One budget: how much of a quota there is and who uses it.
- `CapacityInfo(uint Capacity, uint GameUsed, uint KilnUsed, uint Reserved, CapacityReadiness Readiness, bool Expanded, CapacityScope Scope, bool ExternalUsageUnknown, CapacityWorldToken? World, ulong Revision)`
- `Available { get; } -> uint?` — The room that is left, or null when the budget is not ready or its counts do not add up.
- `Capacity { get; init; } -> uint` — The quota.
- `Expanded { get; init; } -> bool` — The larger quota is really installed (not merely asked for in the configuration).
- `ExternalUsageUnknown { get; init; } -> bool` — Another loader's use could not be measured.
- `GameUsed { get; init; } -> uint` — What the game itself uses and Kiln cannot have.
- `KilnUsed { get; init; } -> uint` — What is in active use through Kiln: mods, Kiln's own services, other loaders' mods that register through Kiln.
- `Readiness { get; init; } -> CapacityReadiness` — Whether the counts are valid.
- `Ready { get; } -> bool` — True when the counts are valid.
- `Reserved { get; init; } -> uint` — Occupied for another reason: records kept for a mod that is gone, placeholders, room Kiln's services reserved and do...
- `Revision { get; init; } -> ulong` — The answering service's own counter: it rises whenever what it publishes changes, so two reads with the same...
- `Scope { get; init; } -> CapacityScope` — One budget for the process, or one per world.
- `World { get; init; } -> CapacityWorldToken?` — The world of a per-world budget; null for a process budget and when there is no world.

## CapacityQueryResult `0.2.4` — The answer of a budget query.
- `CapacityQueryResult(bool Ok, string? Reason, CapacityInfo Budget)`
- `Budget { get; init; } -> CapacityInfo` — The budget; its counts are 0 when it is not ready.
- `Ok { get; init; } -> bool` — True when `Budget` is ready.
- `Reason { get; init; } -> string?` — Null when `Ok`; else the name of `CapacityInfo.Readiness` (`NotReady`, `NoWorld`, `WorldEnded`...

## CapacityReadiness `0.2.4` — Whether a budget can be read now, and if not, why.
enum: NotReady, Ready, NoWorld, WorldEnded, UnknownExternalUsage, AccountingMismatch, NotFound

## CapacityScope `0.2.4` — Whether a budget is one for the process or one per world.
enum: Process, World

## CapacityWorldKind `0.2.4` — The world a per-world budget belongs to.
enum: Server, Client

## CapacityWorldToken `0.2.4` — Names one world of this process while it lives: its kind and the session's counter of that kind.
- `CapacityWorldToken()`
- `CapacityWorldToken(CapacityWorldKind Kind, uint Generation)`
- `Generation { get; init; } -> uint` — The session's counter of that kind; never 0 for a real world.
- `Kind { get; init; } -> CapacityWorldKind` — Which world.

## ClientMainThreadAttribute `0.2.3` — Runs on, or must be called from, the game client's main thread: the thread that runs the game's own UI frame and steps the client world.

## ClientOnlyAttribute — Works on a game client only.

## CommentAttribute — Text written above a config entry or section (several lines: use \n).
- `CommentAttribute(string text)`
- `Text { get; } -> string` — The text; several lines separated by `\n`.

## Config — The registry of every loaded mod's configs (Forge's `ModLoadingContext.registerConfig` + `ConfigTracker`).
- `static All { get; } -> IReadOnlyList<ConfigFile>` — Every registered config, by id.
- `static Directory { get; } -> string` — The folder of the config files: the profile's configDir (KILN_PATH_CONFIG), else `kiln/config`.
- `static Find(string id) -> ConfigFile?` — A config by id ("mymod", "mymod-server"), or null.
- `static Register<T>(ConfigKind kind = ConfigKind.Common, string? name = null) -> ModConfig<T>` — Registers the calling mod's config of type `T` and loads it: the file...
- `static RegisterWorld<T>(string? name = null) -> WorldConfig<T>` — Registers the calling mod's per-world config of type `T`: one file for each world, in the world's sidecar folder...
- `static Schema() -> Json.Nodes.JsonArray` — Schema and values of every config, as one JSON array.

## ConfigChangedEvent — A config's values changed while the server runs (not raised for the first load).
- `Changed { get; } -> IReadOnlyList<string>` — Key paths ("pvp.enabled") whose running value changed.
- `Config { get; } -> ConfigFile` — The config that changed.
- `Has(string path) -> bool` — True when the key path (compared without regard to case) is in `ConfigChangedEvent.Changed`.
- `ModId { get; } -> string` — The id of the mod that registered the config ("kiln" for Kiln's own).
- `PendingRestart { get; } -> IReadOnlyList<string>` — Key paths marked `RequiresRestartAttribute` whose file value changed: the running value stays until the mod reloads.
- `Source { get; } -> ConfigChangeSource` — What changed the config.

## ConfigChangeSource — What changed a config.
enum: File, Api, Server, World

## ConfigEntry — One entry of a config, as of now.
- `ConfigEntry(string Path, string Type, string? Comment, object? Default, object? Value, object? FileValue, double? Min, double? Max, IReadOnlyList<string> Options, bool RequiresRestart, bool Synced)`
- `Comment { get; init; } -> string?` — The text of the entry's `CommentAttribute`; null when it has none.
- `Default { get; init; } -> object?` — The default value.
- `FileValue { get; init; } -> object?` — The value in the file; it differs from `Value` while a restart is pending.
- `FromServer { get; init; } -> bool` — The running value came from the server (client side, while connected; see `ConfigFile.IsFromServer`).
- `Max { get; init; } -> double?` — The highest allowed value; null when there is none.
- `Min { get; init; } -> double?` — The lowest allowed value of a number or duration (its `RangeAttribute`); null when there is none.
- `Options { get; init; } -> IReadOnlyList<string>` — The names an enum entry (or a list of enums) accepts; empty for any other entry.
- `Path { get; init; } -> string` — The dotted key path ("pvp.enabled").
- `PendingRestart { get; } -> bool` — True when the file's value differs from the running one, so the change waits for a reload or restart.
- `RequiresRestart { get; init; } -> bool` — The entry has `RequiresRestartAttribute`.
- `SoftMax { get; init; } -> bool` `0.2.26` — The high end of the entry's `RangeAttribute` is soft (`RangeAttribute.SoftMax`): `ConfigEntry.Max` is where a slider...
- `SoftMin { get; init; } -> bool` `0.2.26` — The low end of the entry's `RangeAttribute` is soft (`RangeAttribute.SoftMin`): `ConfigEntry.Min` is where a slider...
- `Step { get; init; } -> double?` `0.2.17` — The slider step of the entry's `RangeAttribute` (`[Range(min, max, step)]`): editors move a slider from...
- `Synced { get; init; } -> bool` — The entry is marked `SyncedAttribute` (or sits in a section that is).
- `Type { get; init; } -> string` — "bool", "int", "float", "string", "enum", "duration" or "list\<...>".
- `Value { get; init; } -> object?` — The running value.

## ConfigFile — A registered config file (see `Config`).
- `Changed -> Action<ConfigChangedEvent>?` — Raised on the tick thread after values changed (file edit or `ConfigFile.Set`), in the context of the mod that added...
- `Entries { get; } -> IReadOnlyList<ConfigEntry>` — Every entry with its running and file value.
- `FilePath { get; } -> string` — Full path of the JSONC file.
- `Get(string path) -> object?` — The running value at `path`. unknown path
- `Id { get; } -> string` — The file name without ".json": "\<modid>", "\<modid>-server", "\<modid>-\<name>".
- `IsFromServer(string path) -> bool` — Whether `path`'s running value is the server's (see `ConfigFile.ServerValues`).
- `Kind { get; } -> ConfigKind` — The kind of config: where it applies and which file it is.
- `LastError { get; } -> string?` — Why the last read of the file was refused (bad JSON), or null.
- `LastWriteError { get; } -> string?` `0.2.3` — Why the last write of the file failed ("cannot write", the file and the reason: a read-only folder, a full disk), or...
- `LoadedAt { get; } -> DateTime?` — When the file was last read (UTC); null before.
- `ModId { get; } -> string` — The mod that registered it ("kiln" for Kiln's own).
- `PendingRestart { get; } -> IReadOnlyList<string>` — Key paths waiting for a restart (file value differs from the running value).
- `PerWorld { get; } -> bool` — The config belongs to a world (`Config.RegisterWorld`): its file lies in the world's sidecar folder beside...
- `Reload() -> void` — Reads the file now (Kiln also does this by itself when the file changes).
- `Save() -> void` — Writes the running values into the file (after the mod changed its `Value` in code).
- `Schema() -> Json.Nodes.JsonObject` — The schema and values as JSON (for the dashboard and an in-game editor).
- `SchemaJson(bool indented = true) -> string` — `ConfigFile.Schema` as JSON text, indented unless `indented` is false.
- `SchemaSnapshot() -> Json.Nodes.JsonObject` — The offline schema snapshot (schema/config-schema.schema.json): `ConfigFile.Schema` without values, for the launcher...
- `ServerValues { get; } -> IReadOnlyList<string>` — Client side: the key paths whose running value is the server's ([Synced] entries of a server or common config, while...
- `Set(string path, object? value) -> void` — Like `ConfigFile.TrySet`, but throws `ArgumentException` when the value is refused.
- `SnapshotPath { get; } -> string` — `<configDir>/.schema/<id>.json` (contracts.md 5.5).
- `SyncedValuesJson() -> string` — The values marked `SyncedAttribute`, as JSON.
- `TrySet(string path, object? value, out string error) -> bool` — Changes one entry: validates it (out of range is refused, not clamped), applies it (unless it requires a restart...
- `Type { get; } -> Type` — The config class.
- `ValuesJson(bool syncedOnly = false) -> string` — The running values as a JSON object (only `SyncedAttribute` entries with `syncedOnly`): what a server would send its...
- `Warnings { get; } -> IReadOnlyList<string>` — Corrections made at the last read (out-of-range values clamped, bad values replaced).
- `WorldId { get; } -> string?` — Per-world configs: the id of the world whose file is loaded, or null (no world open yet).

## ConfigIgnoreAttribute — Not part of the config file.

## ConfigKeyAttribute — The JSON key of an entry (default: the member name in camelCase).
- `ConfigKeyAttribute(string key)`
- `Key { get; } -> string` — The JSON key.

## ConfigKind — Where a config applies (Forge's `ModConfig.Type`).
enum: Common, Server, Client

## DecisionCompletedEvent `0.2.4` — Posted once for every decision when it has ended, in the Pre of a later step than the one that held the action: whatever the handlers of the cancelable event said...
- `DecisionCompletedEvent(DecisionResult result)`
- `Involves(EntityId entity) -> bool`
- `Result { get; } -> DecisionResult` — How the decision ended.

## DecisionId `0.2.4` — Names one decision (a held action a mod may allow or refuse) for as long as the process runs: the server world's epoch and the request's number in it.
- `DecisionId()`
- `DecisionId(ulong WorldEpoch, ulong Request)`
- `Request { get; init; } -> ulong` — The request's number, never 0, never reused in this process.
- `WorldEpoch { get; init; } -> ulong` — Counts the server worlds of this process, from 1.

## DecisionResult `0.2.4` — The end of one decision.
- `DecisionResult(DecisionId Id, DecisionTerminal Status, EntityId Subject, EntityId Actor, int? RequestedHealth, int? HealthBefore, int? AppliedHealth, string? Reason)`
- `Actor { get; init; } -> EntityId` — The entity that acted (the player picking up), or `EntityId.None`.
- `AppliedHealth { get; init; } -> int?` — The health read back afterwards.
- `HealthBefore { get; init; } -> int?` — The health read right before the answer was carried out.
- `Id { get; init; } -> DecisionId` — The decision.
- `Ok { get; } -> bool`
- `Reason { get; init; } -> string?` — Why, when not `DecisionResult.Ok`.
- `RequestedHealth { get; init; } -> int?` — The health a canceling handler asked for.
- `Status { get; init; } -> DecisionTerminal` — How it ended.
- `Subject { get; init; } -> EntityId` — The entity the action was about (the dying entity, the item on the ground, the player).

## Decisions `0.2.4` — Decisions: things the game is about to do that a mod may refuse (a death, a walk-over pickup, an equipment change).
- `static ReevaluatePickup(EntityId drop, EntityId receiver) -> PickupReevaluationResult` — Asks again about walk-over pickups that were refused (or allowed) for their current exposure: each exposure of...

## DecisionTerminal `0.2.4` — How a decision ended.
enum: Allowed, Prevented, AlreadyAlive, StaleEntity, NoPositiveRange, WriteRefused, TimeoutReleased, CapabilityReleased, SourceChanged, QueueDropped, WorldEnded

## DefinitionRefusalCode `0.2.4` — Why a definition was refused, as a value a mod can branch on.
enum: None, CapacityExceeded, UnknownBase, IdCollision, NameTaken, RegistryClosed, NoWorld, NotReady, StorageFailure, NotFound, OwnerInactive, RegistrationFailedRetained, Superseded, Removed, GameRefused

## DefinitionRegistrationChangedEvent `0.2.4` — A registration changed: a definition was registered, stored for the next start, refused after it had a receipt, or a stored one was settled at this start.
- `Current { get; } -> DefinitionRegistrationResult` — The registration now (its id, definition, kind, state, room, reason and revision).
- `ModId { get; } -> string` — The mod that owns the definition.
- `PreviousSlotsConsumed { get; } -> uint` — The room it occupied before.
- `PreviousState { get; } -> DefinitionRegistrationState?` — Its state before; null when it had no receipt.
- `World { get; } -> CapacityWorldToken?` — The world it belongs to, for a per-world registry; else null.

## DefinitionRegistrationKind `0.2.4` — The kinds of definition that register with a result.
enum: Vfx, Buff, BuildingMaterialPool

## DefinitionRegistrationOptions `0.2.4` — Options of a result-bearing definition call.
- `DefinitionRegistrationOptions(DefinitionTiming Timing = DefinitionTiming.RequireRegistered)`
- `Timing { get; init; } -> DefinitionTiming` — What the call may settle for.

## DefinitionRegistrationResult `0.2.4` — The answer of a result-bearing definition call, and of a receipt query.
- `DefinitionRegistrationResult(bool Ok, string? Reason, DefinitionRefusalCode Code, Guid RegistrationId, Guid Definition, DefinitionRegistrationKind Kind, DefinitionRegistrationState State, uint SlotsConsumed, ulong SnapshotRevision, IReadOnlyList<string> Warnings)`
- `Code { get; init; } -> DefinitionRefusalCode` — Why, as a value; `DefinitionRefusalCode.None` when `Ok`.
- `Definition { get; init; } -> Guid` — The definition's GUID.
- `Kind { get; init; } -> DefinitionRegistrationKind` — What was defined.
- `Ok { get; init; } -> bool` — True for `DefinitionRegistrationState.Registered` and `DefinitionRegistrationState.AcceptedNextStart`.
- `Reason { get; init; } -> string?` — Null when `Ok`; else why, in words.
- `RegistrationId { get; init; } -> Guid` — The receipt: stable for the same mod, kind, definition and content.
- `SlotsConsumed { get; init; } -> uint` — The room this definition occupies now (not an increase: a repeated call returns the same number); 0 when stored for...
- `SnapshotRevision { get; init; } -> ulong` — The service revision the answer was committed in (`CapacityInfo.Revision` of the same service).
- `State { get; init; } -> DefinitionRegistrationState` — What became of it.
- `Warnings { get; init; } -> IReadOnlyList<string>` — Things the author should know that did not stop it.

## DefinitionRegistrationState `0.2.4` — What became of a definition.
enum: Refused, Registered, AcceptedNextStart, Retained

## DefinitionTiming `0.2.4` — What a definition call may settle for.
enum: RequireRegistered, AllowNextStart

## EcsProgramBinding `0.2.4` — The outcome of `EcsPrograms.TryBind`.
- `EcsProgramBinding(bool Ok, string? Reason, WorldKind World, IDisposable? Handle)`
- `Handle { get; init; } -> IDisposable?` — The binding: dispose it to unbind (the mod's unload does too).
- `Ok { get; init; } -> bool` — True when the callback is bound and the world that runs now took the program, or no world of that kind runs yet (the...
- `Reason { get; init; } -> string?` — Why not, in words; null when `Ok`.
- `World { get; init; } -> WorldKind` — The world it was bound for.

## EcsProgramCallback — A worker-thread ECS update.
- `Invoke(EcsProgramContext context) -> void`

## EcsProgramContext — Access declared by this program.
- `EmitEvent(string name, ReadOnlySpan<byte> data) -> bool` — Sends an exact event payload through a declared output stream.
- `Entity { get; } -> EntityId` — The entity the callback runs for; none for a system program.
- `QueueAdd(EntityId entity, string component, ReadOnlySpan<byte> data) -> bool` — Adds component bytes at the end of this step.
- `QueueRemove(EntityId entity, string component) -> bool` — Removes a component at the end of this step.
- `ReadEvents(string name) -> byte[][]` — A copy of each event in this declared input stream, in queue order.
- `TryGet<T>(out T value, bool template = false) -> bool` `0.2.2` — `EcsProgramContext.TryRead` for a generated component type (`Kiln.Ecs.Health`): the declared name is the type's.
- `TryGetOther<T>(EntityId entity, out T value) -> bool` `0.2.2` — `EcsProgramContext.TryReadOther` for a generated component type.
- `TryGetWritable<T>(out T value) -> bool` `0.2.10` — `EcsProgramContext.TryReadWritable` for a generated component type (`Kiln.Ecs.SlotAttachment`): the declared name is...
- `TryRead<T>(string component, out T value, bool template = false) -> bool` — Reads a declared read component of the current entity.
- `TryReadOther<T>(EntityId entity, string component, out T value) -> bool` — Reads another entity through an otherRead declaration.
- `TryReadWritable<T>(string component, out T value) -> bool` `0.2.10` — Reads a declared write component of the current entity: its value now, before (or after) this call's...
- `TrySet<T>(T value) -> bool` `0.2.2` — `EcsProgramContext.TryWrite` for a generated component type.
- `TrySetOther<T>(EntityId entity, T value) -> bool` `0.2.2` — `EcsProgramContext.TryWriteOther` for a generated component type.
- `TryWrite<T>(string component, T value) -> bool` — Writes a declared write component of the current entity.
- `TryWriteOther<T>(EntityId entity, string component, T value) -> bool` — Writes another entity through an otherWrite declaration, marking replication/save dirtiness with the game's job tracker.
- `World { get; } -> WorldKind` `0.2.4` — The world this call runs in: `WorldKind.Server`, or for a client binding `WorldKind.Client` / `WorldKind.Prediction`.

## EcsProgramRefusedEvent `0.2.4` — A client world refused one of the calling mod's bound programs (a name that world's maps lack, a full program table, a fault of its callback there, its managed...
- `Key { get; } -> string` — Its key in the mod's kiln.programs.json.
- `ModId { get; } -> string` — The mod whose program it is.
- `Reason { get; } -> string` — Why, in kiln.world's words ("events_in: unknown event 'StopGuardingEvent'").
- `World { get; } -> WorldKind` — The world that refused it (`WorldKind.Client`, or `WorldKind.Prediction` for a prediction program, which lives in...

## EcsPrograms — Callbacks bound to scheduler declarations in the mod's kiln.programs.json.
- `static Available { get; } -> bool` — Whether boot declarations and callback binding work on this build.
- `static Bind(string key, EcsProgramCallback callback) -> IDisposable` — Binds one declaration belonging to the calling mod.
- `const ClientWorldCapability = "world.programs.client" -> string` `0.2.4` — The capability of `EcsPrograms.TryBind` for a client's worlds.
- `static SupportsOtherWrites { get; } -> bool` — Whether tracked other-entity writes work on this build.
- `static TryBind(string key, EcsProgramCallback callback, WorldKind world) -> EcsProgramBinding` `0.2.4` — Binds the calling mod's declaration `key` of its kiln.programs.json to `callback` for the world of `world`.

## EntityId — A server-world entity id.
- `EntityId()`
- `EntityId(uint Value)`
- `IsNone { get; } -> bool` — True for `EntityId.None`.
- `IsPlayer { get; } -> bool` — True for ids 1 to 1023, the engine's range for player entities.
- `static readonly None -> EntityId` — No entity (0).
- `Value { get; init; } -> uint` — The id; 0 is none.

## EntityRef — An entity as it was when it was captured: its id and the spawn serial it had then.
- `EntityRef()`
- `EntityRef(EntityId Id, ulong Serial)`
- `HasSerial { get; } -> bool` — True when the ref carries a spawn serial.
- `Id { get; init; } -> EntityId` — The entity id.
- `static implicit operator EntityId(EntityRef r) -> EntityId` — The entity id, for every API that takes an `EntityId`.
- `IsAlive { get; } -> bool` — True while the id still belongs to the spawn this ref captured.
- `IsNone { get; } -> bool` — True when this refers to no entity.
- `static readonly None -> EntityRef` — No entity.
- `static Of(EntityId entity) -> EntityRef` — Captures the entity's current serial.
- `Serial { get; init; } -> ulong` — An opaque spawn stamp from the entity source; 0 when the entity has none (then `EntityRef.IsAlive` can only check...

## EventEntityKey `0.2.4` — Which life of an entity id an event is about.
- `EventEntityKey()`
- `EventEntityKey(uint Entity, bool HasSpawnTime, ulong SpawnTime, ulong LifeSerial, ulong PlayerHandle, ulong JoinedStep)`
- `Entity { get; init; } -> uint` — The entity id.
- `HasSpawnTime { get; init; } -> bool` — False for a player that is told apart by its session alone; then `SpawnTime` is 0 and means nothing.
- `JoinedStep { get; init; } -> ulong` — The step at which Kiln saw the player join; 0 for anything else.
- `LifeSerial { get; init; } -> ulong` — Kiln's serial of this life, unique in this process.
- `PlayerHandle { get; init; } -> ulong` — A player's connection handle; 0 for anything else.
- `SpawnTime { get; init; } -> ulong` — The entity's spawn time as the game stores it.

## EventFilter `0.2.2` — What a subscription receives (`Events.On`).
- `Entities { get; init; } -> IReadOnlySet<EntityId>?` — Only events about one of these entities (the event type must be an `IEntityEvent`).
- `static ForEntities(IEnumerable<EntityId> entities) -> EventFilter` — Events about any of `entities`.
- `static ForEntity(EntityId entity) -> EventFilter` — Events about `entity` only: every event that names it (`IEntityEvent.Involves`).
- `Passes(KilnEvent ev) -> bool` — Whether `ev` passes this filter.

## EventHandlerInfo — A row of `Diagnostics.Handlers`.
- `EventHandlerInfo(string Event, string Owner, string Handler, EventPriority Priority, bool ReceiveCanceled, EventHandlerStats Stats)`
- `Event { get; init; } -> string` — The event type's full name.
- `Handler { get; init; } -> string` — "Type.Method" of the handler.
- `Owner { get; init; } -> string` — The id of the mod that subscribed ("kiln" for Kiln's own code).
- `Priority { get; init; } -> EventPriority` — Where the handler runs.
- `ReceiveCanceled { get; init; } -> bool` — Whether it also runs for canceled events.
- `Stats { get; init; } -> EventHandlerStats` — The time it has used.

## EventHandlerStats — Time spent in one handler since it subscribed (or since `Diagnostics.ResetStats`).
- `EventHandlerStats()`
- `EventHandlerStats(long Calls, double TotalMs, double MaxMs, int Faults)`
- `AverageMs { get; } -> double` — `EventHandlerStats.TotalMs` divided by `EventHandlerStats.Calls`; 0 before the first call.
- `Calls { get; init; } -> long` — How many times the handler ran.
- `Faults { get; init; } -> int` — The exceptions it threw.
- `MaxMs { get; init; } -> double` — The longest single call, in milliseconds.
- `TotalMs { get; init; } -> double` — The time spent in it, in milliseconds.

## EventPriority — Order in which handlers run.
enum: Highest, High, Normal, Low, Lowest, Monitor

## EventResult — Outcome of an event that asks for a decision (Forge's `Event.Result`).
enum: Default, Allow, Deny

## Events — Kiln's event bus, shared by all mods and services (Forge's `EVENT_BUS`).
- `static Enqueue(KilnEvent ev) -> void` — Posts `ev` on the tick thread at the start of the next step.
- `static HasSubscribers(Type type) -> bool` — Whether anything listens for events of `type`.
- `static HasSubscribers<T>() -> bool` — Whether anything listens for `T` (or a base class of it).
- `const MaxDepth = 32 -> int` — How deep posts inside handlers may nest before `Events.Post` refuses (a loop between handlers).
- `const MaxFaults = 5 -> int` — A handler is removed after this many exceptions.
- `static On<T>(Action<T> handler, EventFilter filter, EventPriority priority = EventPriority.Normal, bool receiveCanceled = false) -> EventSubscription` `0.2.2` — Subscribes `handler` to the events of `T` that pass `filter`: with `EventFilter.Entities`, only events about one of...
- `static On<T>(Action<T> handler, EventPriority priority = EventPriority.Normal, bool receiveCanceled = false) -> EventSubscription` — Subscribes `handler` to `T` and every event derived from it.
- `static Post<T>(T ev) -> T` — Runs every handler for `ev` in priority order (then subscription order) and returns it, so the poster can read...
- `static Register(object target) -> IReadOnlyList<EventSubscription>` — Subscribes every `SubscribeAttribute` method of `target` (instance and static methods of its type; pass a `Type` for...
- `static Subscriptions { get; } -> IReadOnlyList<EventSubscription>` — All active subscriptions (diagnostics).
- `static Unregister(object target) -> int` — Removes every subscription `Events.Register` made for `target`.

## Events.Diagnostics — Handler timings and counters.
- `static HandlerCalls { get; } -> long` — Handler calls, since the start.
- `static Handlers { get; } -> IReadOnlyList<EventHandlerInfo>` — Every active handler with its timing, slowest total first.
- `static Posts { get; } -> long` — Posts that had at least one handler, since the start.
- `static Queued { get; } -> int` — Events waiting in `Events.Enqueue`'s queue.
- `static ResetStats() -> void` — Zeroes every handler's timing (not the fault counts).

## EventSubscription — One handler on the bus.
- `Active { get; } -> bool` — False once the subscription was removed: disposed, its mod unloaded, or too many faults.
- `Dispose() -> void` — Unsubscribes the handler.
- `Filter { get; } -> EventFilter?` `0.2.2` — What this handler receives (`Events.On`); null: every event of its type.
- `Name { get; } -> string` — "Type.Method" of the handler (diagnostics).
- `Owner { get; } -> string` — Id of the mod that subscribed ("kiln" for Kiln's own code).
- `Priority { get; } -> EventPriority` — Where the handler runs among the handlers of the event.
- `ReceiveCanceled { get; } -> bool` — Whether it also runs for events an earlier handler canceled.
- `Stats { get; } -> EventHandlerStats` — The time this handler has used.
- `Type { get; } -> Type` — The event type the handler subscribed to; a handler of a base class also receives derived events.

## Game — Process-wide facts about the running server and Kiln.
- `static AssertTickThread() -> void` — Throws unless this is the tick thread.
- `static HasFeature(string name) -> bool` — Whether a Kiln capability (e.g. `"tick"`, `"log.mirror"`, `"items.fuel"`) works here: `Capabilities.Is`, which also...
- `static IsTickThread { get; } -> bool` — True on the server-world tick thread, where game state may be touched.
- `static KilnVersion { get; } -> string` — The version of the Kiln that is running.
- `static Language { get; } -> string` — The language mod texts are shown in, as a lower-case tag: `"en-us"`, `"de-de"`, `"pt-br"` (the game's own language...

## GameEvents — The game's ECS events of one step, and posting events into its queue (kiln.types).
- `static Each(GameType type, Action<ReflectedValue> handler) -> int` `0.2.13` — `GameEvents.Each` for a type resolved once (`GameTypes.Find`, `GameTypes.Events`): no name lookup at all.
- `static Each(string type, Action<ReflectedValue> handler) -> int` — Every event of `type` the server world produced in the step that just ended (call it in `Context.Tick.Post`).
- `static Post(GameType type, IReadOnlyDictionary<string, object?> fields) -> bool` `0.2.13` — `GameEvents.Post` for a type resolved once.
- `static Post(string type, IReadOnlyDictionary<string, object?> fields) -> bool` — Posts an event with the named fields set (checked; unnamed fields, including timeStamp, are 0).

## GameField — A field of a reflected struct.
- `GameField()`
- `GameField(string Name, GameType Type, int Offset)`
- `Name { get; init; } -> string` — The field's name.
- `Offset { get; init; } -> int` — Where the field starts, in bytes, in the struct that declares it.
- `Type { get; init; } -> GameType` — The field's type.

## GameLogEvents — Lines of the game's own log.
- `Line -> Action<LogLevel, string>?` — One game log line (without trailing newline), on the tick thread, a step or so after it was written.

## GamePixelFormat `0.2.13` — The game's pixel formats (`keen::PixelFormat`), with the game's numbers: the value a texture resource stores is the enum value.
enum: None, R4G4UnormPack8, R4G4B4A4UnormPack16, B4G4R4A4UnormPack16, R5G6B5UnormPack16, B5G6R5UnormPack16, R5G5B5A1UnormPack16, B5G5R5A1UnormPack16, A1R5G5B5UnormPack16, R8Unorm, R8Snorm, R8Uscaled, R8Sscaled, R8Uint, R8Sint, R8Srgb, R8G8Unorm, R8G8Snorm, R8G8Uscaled, R8G8Sscaled, R8G8Uint, R8G8Sint, R8G8Srgb, R8G8B8Unorm, R8G8B8Snorm, R8G8B8Uscaled, R8G8B8Sscaled, R8G8B8Uint, R8G8B8Sint, R8G8B8Srgb, B8G8R8Unorm, B8G8R8Snorm, B8G8R8Uscaled, B8G8R8Sscaled, B8G8R8Uint, B8G8R8Sint, B8G8R8Srgb, R8G8B8A8Unorm, R8G8B8A8Snorm, R8G8B8A8Uscaled, R8G8B8A8Sscaled, R8G8B8A8Uint, R8G8B8A8Sint, R8G8B8A8Srgb, B8G8R8A8Unorm, B8G8R8A8Snorm, B8G8R8A8Uscaled, B8G8R8A8Sscaled, B8G8R8A8Uint, B8G8R8A8Sint, B8G8R8A8Srgb, A8B8G8R8UnormPack32, A8B8G8R8SnormPack32, A8B8G8R8UscaledPack32, A8B8G8R8SscaledPack32, A8B8G8R8UintPack32, A8B8G8R8SintPack32, A8B8G8R8SrgbPack32, A2R10G10B10UnormPack32, A2R10G10B10SnormPack32, A2R10G10B10UscaledPack32, A2R10G10B10SscaledPack32, A2R10G10B10UintPack32, A2R10G10B10SintPack32, A2B10G10R10UnormPack32, A2B10G10R10SnormPack32, A2B10G10R10UscaledPack32, A2B10G10R10SscaledPack32, A2B10G10R10UintPack32, A2B10G10R10SintPack32, R16Unorm, R16Snorm, R16Uscaled, R16Sscaled, R16Uint, R16Sint, R16Sfloat, R16G16Unorm, R16G16Snorm, R16G16Uscaled, R16G16Sscaled, R16G16Uint, R16G16Sint, R16G16Sfloat, R16G16B16Unorm, R16G16B16Snorm, R16G16B16Uscaled, R16G16B16Sscaled, R16G16B16Uint, R16G16B16Sint, R16G16B16Sfloat, R16G16B16A16Unorm, R16G16B16A16Snorm, R16G16B16A16Uscaled, R16G16B16A16Sscaled, R16G16B16A16Uint, R16G16B16A16Sint, R16G16B16A16Sfloat, R32Uint, R32Sint, R32Sfloat, R32G32Uint, R32G32Sint, R32G32Sfloat, R32G32B32Uint, R32G32B32Sint, R32G32B32Sfloat, R32G32B32A32Uint, R32G32B32A32Sint, R32G32B32A32Sfloat, R64Uint, R64Sint, R64Sfloat, R64G64Uint, R64G64Sint, R64G64Sfloat, R64G64B64Uint, R64G64B64Sint, R64G64B64Sfloat, R64G64B64A64Uint, R64G64B64A64Sint, R64G64B64A64Sfloat, B10G11R11UfloatPack32, E5B9G9R9UfloatPack32, D16Unorm, X8D24UnormPack32, D32Sfloat, S8Uint, D16UnormS8Uint, D24UnormS8Uint, D32SfloatS8Uint, BC1RgbUnormBlock, BC1RgbSrgbBlock, BC1RgbaUnormBlock, BC1RgbaSrgbBlock, BC2UnormBlock, BC2SrgbBlock, BC3UnormBlock, BC3SrgbBlock, BC4UnormBlock, BC4SnormBlock, BC5UnormBlock, BC5SnormBlock, BC6HUfloatBlock, BC6HSfloatBlock, BC7UnormBlock, BC7SrgbBlock

## GameSystem — One of the game's ECS systems, with its profiler numbers.
- `GameSystem(int Index, string Name, ulong Rva, bool Enabled, ulong Calls, double Milliseconds, string World)`
- `Calls { get; init; } -> ulong` — How many times it ran since the profiler started.
- `Enabled { get; init; } -> bool` — False while the system is switched off (the game skips it).
- `Index { get; init; } -> int` — The system's position in the list, which is sorted by name.
- `Milliseconds { get; init; } -> double` — CPU time since the profiler started, summed over every thread.
- `Name { get; init; } -> string` — The system's name (`stamina_depletion_sprint`).
- `Rva { get; init; } -> ulong` — Where the system's update function is, relative to the image base.
- `World { get; init; } -> string` — Where it ran while profiling: `"server"`, `"client"` or `"both"`; empty when it has no calls.

## GameSystems — The game's ECS systems (the code it runs every step: stamina drain, durability loss, fall damage, spawners...) and their profiling data (kiln.types).
- `static All { get; } -> IReadOnlyList<GameSystem>` — Every system, sorted by name.
- `static Available { get; } -> bool` — Whether switching and profiling work on this build.
- `static Find(string name) -> GameSystem?` — The system with this name; null when there is none, or kiln.types is not running.
- `static Hook(string name, Action? before = null, Action? after = null) -> IDisposable` — Runs callbacks immediately before and after a game's system update, once per slice.
- `static Profile(bool on) -> void` — On: starts the profiler from zero.
- `static ProfileSeconds { get; } -> double` — How long the profiler has run (or ran), seconds.
- `static Profiling { get; } -> bool` — True while the profiler runs.
- `static Search(string words) -> IEnumerable<GameSystem>` — Systems whose name holds every word: `Search("fall damage")` finds fall_damage_infliction.
- `static SetEnabled(string name, bool enabled) -> void` — Claims or releases this caller's disabling switch.
- `static Stats() -> IReadOnlyList<GameSystem>` — The systems that ran while profiling, costliest first.
- `static TurnAllOn() -> int` — Releases this caller's switches.

## GameTexture `0.2.13` — Game texture pixels in code: decodes the bytes of a game texture (any `GamePixelFormat` kiln mode's `image.decode_texture` reads) to RGBA8 pixels, and encodes RGBA8...
- `static ByteSize(GamePixelFormat format, int width, int height) -> int` — The bytes of one image of `width` x `height` in `format` (block formats: whole 4x4 blocks).
- `static Decode(ReadOnlySpan<byte> data, GamePixelFormat format, int width, int height) -> byte[]` — Decodes one image of `width` x `height` pixels in `format` to RGBA8 (`width * height * 4` bytes).
- `static DecodeMip(ReadOnlySpan<byte> mipChain, GamePixelFormat format, int width, int height, int mip) -> byte[]` — Decodes level `mip` of a mip chain: the levels one after another, largest first, as a game texture's data holds them.
- `static Encode(ReadOnlySpan<byte> rgba, int width, int height, GamePixelFormat format) -> byte[]` — Encodes RGBA8 pixels (`width * height * 4` bytes) to `format`.
- `static MipOffset(GamePixelFormat format, int width, int height, int mip) -> int` — Where level `mip` of a mip chain starts: the sum of `GameTexture.ByteSize` of the larger levels.

## GameType — One of the game's reflected types (about 11,000 on the server, 12,600 in the game).
- `Alignment { get; } -> int` — The type's alignment in bytes.
- `AllFields { get; } -> IEnumerable<GameField>` — Every field, base classes first.
- `ArrayLength { get; } -> int` — A static array's element count (0 for other kinds).
- `Base { get; } -> GameType?` — A struct's base class, or null.
- `Defaults { get; } -> byte[]?` — The game's default value (`GameType.Size` bytes), or null when the type has none.
- `EnumValues { get; } -> IReadOnlyList<(string Name, long Value)>` — An enum's or bitmask's values (name, value).
- `Field(string name) -> GameField?` — A field by name, base classes included (a derived class's field hides a base class's of the same name).
- `FieldCount { get; } -> int` — Own fields (struct) or values (enum, bitmask).
- `Fields { get; } -> IReadOnlyList<GameField>` — Own fields, in the game's order.
- `Flags { get; } -> byte` — The game's flags byte for the type.
- `Hash { get; } -> uint` — FNV-1a-32 of `GameType.QualifiedName`: the key of the game's component and event lookups.
- `ImpactName { get; } -> string?` `0.2.3` — The name Impact scripts use for this type: `"ecs.Health"` for `keen::ecs::Health`, `"float32"` for `keen::float32`...
- `Index { get; } -> int` — The type's position in the game's type table.
- `Inner { get; } -> GameType?` — A struct's base class; an array's, optional's or typedef's element or target; else null.
- `Kind { get; } -> GameTypeKind` — The game's primitive kind of the type.
- `Name { get; } -> string` — "Health".
- `QualifiedName { get; } -> string` — "keen::ecs::Health".
- `Resolved { get; } -> GameType` — The type a typedef names (followed to the end), else this type.
- `Size { get; } -> int` — The type's size in bytes.

## GameTypeKind — The game's primitive kinds (kiln_types.h KILN_TYPE_*).
enum: None, Bool, U8, S8, U16, S16, U32, S32, U64, S64, F32, F64, Enum, Bitmask8, Bitmask16, Bitmask32, Bitmask64, Typedef, Struct, StaticArray, DsArray, DsString, DsOptional, DsVariant, BlobArray, BlobString, BlobOptional, BlobVariant, ObjectReference, Guid

## GameTypes — The game's reflected types: every name, size, field, base class, enum value and default value of the running build, read from the game itself (kiln.types).
- `static All { get; } -> IEnumerable<GameType>` — Every type, in the game's order (read-only, built once per type table).
- `static Available { get; } -> bool` — Whether the type table was found on this build.
- `static ByHash(uint hash) -> GameType?` — The type whose `GameType.Hash` is `hash`; null when there is none or kiln.types is not running.
- `static ByImpactName(string impactName) -> GameType?` `0.2.3` — The one type whose `GameType.ImpactName` is `impactName` exactly (ordinal, case-sensitive; a short or qualified name...
- `static Components { get; } -> IReadOnlyList<GameType>` — The game's ECS component types (read-only, built once per type table).
- `static Count { get; } -> int` — The number of types in the table; 0 when kiln.types is not running.
- `static Events { get; } -> IReadOnlyList<GameType>` — The game's ECS event types (read-only, built once per type table).
- `static Find(string name) -> GameType?` — By name: "keen::ecs::Health" exactly, or a short name ("Health", "ItemInfo") that is unique.
- `static FindAllByImpactName(string impactName) -> IReadOnlyList<GameType>` `0.2.3` — Every type whose `GameType.ImpactName` is `impactName` exactly (ordinal), in the game's order; empty when none.
- `static Search(string words) -> IEnumerable<GameType>` — Types whose qualified name holds every word (any order, any case): `Search("player input")`.
- `static Status { get; } -> string` — What kiln.types found, in words ("" when everything works).
- `static TypeOf(object? value) -> GameType?` — The reflected type of a value (EML's `types.of`): a `ReflectedValue` (a component or an event copy), a...

## GameValueCodec — Reads and writes one scalar value with the game's layout and Kiln's checks.
- `static Fits(long v, GameTypeKind kind, int size) -> bool` — Whether `v` fits a field of that kind and size.
- `static Format(object value, GameTypeKind kind, IReadOnlyList<(string Name, long Value)>? enumValues = null) -> string` — A value as text: numbers, bools, bitmasks in hex, enums as `Name (n)`.
- `static IsScalar(GameTypeKind k) -> bool` — True for kinds stored as plain numbers (bool, integers, floats, enums, bitmasks).
- `static Read(ReadOnlySpan<byte> data, GameTypeKind kind, int size) -> object` — The value at `data`: bool, long (integers, enums, bitmasks), ulong (u64) or double (floats).
- `static Read(ReadOnlySpan<byte> data, GameTypeKind kind, int size, IReadOnlyList<(string Name, long Value)>? enumValues) -> object` — Reads a scalar, preserving declared enum values when their stored bits represent a negative value.
- `static Write(Span<byte> data, GameTypeKind kind, int size, object? value, IReadOnlyList<(string Name, long Value)>? enumValues = null, string field = "value") -> void` — Checks `value` against the field and writes it.

## IEntityEvent `0.2.2` — An event about entities (a hit's target and attacker, a death's victim).
- `Involves(EntityId entity) -> bool` — Whether the event is about `entity`: any entity it names as taking part (for a hit: the target, the part hit, the...

## `IGameAttribute<TSelf, TValue>` `0.2.2` — A generated ECS component that is a game attribute (its base chain runs through `keen::ecs::AttributeComponent`: `Health`, `Stamina`, `Fuel`, ...).
- `ValuesOf(ref TSelf self) -> Span<TValue>` — The value slots of `self`, over its own memory.

## `IGameComponent<TSelf>` `0.2.2` — A generated struct that is an ECS component of the game (the dump's component list).

## `IGameEvent<TSelf>` `0.2.2` — A generated struct that is an ECS event of the game (the dump's event list).

## `IGameStruct<TSelf>` `0.2.2` — A struct generated from the game's reflection table (every nested type of `Kiln.Ecs` that is a struct).
- `Hash { get; } -> uint` — The engine's type hash: FNV-1a-32 of `IGameStruct.QualifiedName`.
- `Layout { get; } -> ulong` — The layout hash: FNV-1a-64 over the type's canonical layout text, which covers every type inside it...
- `Name { get; } -> string` — The game's short name (for an ECS component, the name kiln.world uses).
- `QualifiedName { get; } -> string` — The game's qualified name, such as `keen::ecs::Health`.
- `Size { get; } -> int` — The size in bytes, the engine's and the C# struct's.

## IKilnResult — The shape of a result by Kiln's error rule: `IKilnResult.Ok` false with a `IKilnResult.Reason` when the game refused.
- `Ok { get; } -> bool` — True when the action happened.
- `Reason { get; } -> string?` — Why it did not happen; null when `IKilnResult.Ok`.

## ImcMessage — One inter-mod message.
- `Method { get; } -> string` — What the sender asks for; the receiver decides what the names mean.
- `Payload { get; } -> object?` — The data that goes with it; null when there is none.
- `PayloadAs<T>() -> T?` — The payload as `T`, or default when it is something else.
- `Sender { get; } -> string` — The sending mod's id (`kiln` when Kiln or a service sent it).

## IReadOnlyGameData `0.2.3` — The game's data before the game is entered: a bounded offline view, never the live registries.
- `Read(string key) -> BootDataResult` — One record: `item:<debugName>`, `template:<debugName>`, `skill:0x<8 hex digits>`...

## IResultEvent — An event with an `EventResult`; see `ResultEvent`.
- `Result { get; set; } -> EventResult` — The decision so far; `EventResult.Default` until a handler sets one.

## ItemId — An item type: FNV-1a-32 of the item's 16 GUID bytes, as the game computes it (and the dashboard catalog uses).
- `ItemId()`
- `ItemId(uint Value)`
- `static FromGuidBytes(ReadOnlySpan<byte> guid) -> ItemId` — The id the game gives the item with these GUID bytes (as stored in the game's data).
- `IsNone { get; } -> bool` — True for `ItemId.None`.
- `static readonly None -> ItemId` — No item (0).
- `Value { get; init; } -> uint` — The id; 0 is none.

## ItemInstanceRef — One particular item in the world (not an item type, which is `ItemId`, and not `Kiln.Blocks`' `ItemRef`, which names an item for a recipe): the item's entity and its...
- `ItemInstanceRef()`
- `ItemInstanceRef(EntityRef Entity, ItemId Item)`
- `Entity { get; init; } -> EntityRef` — The item's entity.
- `IsAlive { get; } -> bool` — Exactly the entity's `EntityRef.IsAlive`, nothing more.
- `IsNone { get; } -> bool` — True when this refers to no item entity.
- `Item { get; init; } -> ItemId` — The item's type.
- `static readonly None -> ItemInstanceRef` — No item.

## KilnEvent — Base class of every event on Kiln's event bus (`Events`).

## KilnLanguage `0.2.11` — The language Kiln's own UI shows (the Kiln menu and its pages, Kiln's windows and toasts, the Keys page, the lines kiln-ui.dll adds to the game's menus) and when it...
- `static Changed -> Action?` — Kiln's texts changed: `KilnLanguage.Current` is another language (the player's setting, or the game's language while...
- `static Current { get; } -> string` — The language Kiln's UI shows now, as a lower-case tag (`"en-us"`, `"zh-cn"`, `"de"`): `ui.language`'s tag, or the...
- `static OverlayInEnglish { get; } -> bool` — True while Kiln's overlay shows its texts in English although `KilnLanguage.Current` is another language: the...
- `static Setting { get; } -> string` — kiln.json `ui.language` as set: `"auto"` or the tag the player chose.

## KilnLog — kiln.log lines written as Kiln itself (no mod name), for Kiln's own tool assemblies.
- `static Info(string text) -> void` — Writes an `LogLevel.Info` line to `kiln.log` as Kiln itself.
- `static Warn(string text) -> void` — Writes a `LogLevel.Warn` line to `kiln.log` as Kiln itself.

## KilnTask `0.2.10` — A task a mod started with `KilnTasks`: its state, its fault, a way to cancel it and to await it.
- `AsTask() -> Task` — A `Task` that ends as this task does (the same one on every call), for composition.
- `Cancel() -> void` — Asks the task to stop: a task that has not started never starts (`KilnTaskState.Cancelled`), a running one sees...
- `Fault { get; } -> Exception?` — The exception that ended the task when `KilnTask.State` is `KilnTaskState.Faulted`; null in every other state.
- `GetAwaiter() -> KilnTask.Awaiter` — The awaiter `await task` uses.
- `Name { get; } -> string` — The name the task was started with.
- `State { get; } -> KilnTaskState` — Where the task stands now.
- `Wait() -> void` — Blocks until the task has ended, then throws its fault (or its `OperationCanceledException` when it was cancelled).

## KilnTask.Awaiter `0.2.10` — Awaits a `KilnTask`.
- `GetResult() -> void` — Throws the task's fault or cancellation.
- `IsCompleted { get; } -> bool` — True once the task has ended.
- `OnCompleted(Action continuation) -> void` — Runs `continuation` once when the task has ended.
- `UnsafeOnCompleted(Action continuation) -> void` — As `Awaiter.OnCompleted`, without flowing the execution context.

## `KilnTask<T>` `0.2.10` — A `KilnTask` whose work returns a value: `T value = await task;`.
- `AsTask() -> Task<T>` — A `Task` that ends as this task does (the same one on every call), for composition.
- `GetAwaiter() -> KilnTask<T>.Awaiter` — The awaiter `await task` uses.
- `Result { get; } -> T` — The work's result once the task has `KilnTaskState.Completed`.

## `KilnTask<T>.Awaiter` `0.2.10` — Awaits a `KilnTask` for its result, as `Awaiter` does.
- `GetResult() -> T` — The result, or the task's fault or cancellation.
- `IsCompleted { get; } -> bool` — True once the task has ended.
- `OnCompleted(Action continuation) -> void` — Runs `continuation` once when the task has ended.
- `UnsafeOnCompleted(Action continuation) -> void` — As `Awaiter.OnCompleted`, without flowing the execution context.

## KilnTaskContext `0.2.10` — What a task's work gets from Kiln: its cancellation, and the ways back to a world's tick.
- `Cancellation { get; } -> CancellationToken` — Signalled when the task is cancelled: `KilnTask.Cancel`, or Kiln stopping it (its mod unloads, a world it hops to...
- `NextGateWindow() -> KilnTaskContext.GateWindow` — `await ctx.NextGateWindow()` continues the work on a worker after a later Post edge of the task's session of its...
- `NextGateWindow(WorldKind world) -> KilnTaskContext.GateWindow` — `KilnTaskContext.NextGateWindow` after a later step of the named world: `NextGateWindow(WorldKind.Client)` resumes...
- `OnTick<T>(WorldKind world, Func<T> apply) -> Task<T>` — `KilnTaskContext.OnTick` on the named world's tick: `await ctx.OnTick(WorldKind.Client, () => _view.Selected)`.
- `OnTick<T>(Func<T> apply) -> Task<T>` — Runs `apply` once on the tick of the task's world and gives its result back...
- `PostToTick(WorldKind world, Action apply) -> void` — `KilnTaskContext.PostToTick` to the named world's tick: `ctx.PostToTick(WorldKind.Client, () => _view.Show(marks))`.
- `PostToTick(Action apply) -> void` — Hands `apply` to the tick of the task's world without waiting for it: it runs once, before a coming step's Pre handlers.
- `TickWorld { get; } -> WorldKind` — The world the parameterless `KilnTaskContext.ToTick`, `KilnTaskContext.OnTick`, `KilnTaskContext.PostToTick` and...
- `ToTick() -> KilnTaskContext.TickHop` — `await ctx.ToTick()` continues the work on the tick of the task's world (`KilnTaskContext.TickWorld`), before that...
- `ToTick(WorldKind world) -> KilnTaskContext.TickHop` — `await ctx.ToTick(WorldKind.Client)`: `KilnTaskContext.ToTick` to the named world (the server world's tick, or the...

## KilnTaskContext.GateWindow `0.2.10` — The awaitable of `KilnTaskContext.NextGateWindow`.
- `GetAwaiter() -> KilnTaskContext.GateWindow.Awaiter` — The awaiter `await` uses.

## KilnTaskContext.GateWindow.Awaiter `0.2.10` — Awaits the next gate window.
- `GetResult() -> void` — Throws the task's `OperationCanceledException` when the task was cancelled or the session ended first.
- `IsCompleted { get; } -> bool` — Always false: the work always waits for a later Post edge.
- `OnCompleted(Action continuation) -> void` — Queues `continuation` for a worker after the next Post edge.
- `UnsafeOnCompleted(Action continuation) -> void` — As `Awaiter.OnCompleted`.

## KilnTaskContext.TickHop `0.2.10` — The awaitable of `KilnTaskContext.ToTick`.
- `GetAwaiter() -> KilnTaskContext.TickHop.Awaiter` — The awaiter `await` uses.

## KilnTaskContext.TickHop.Awaiter `0.2.10` — Awaits a hop to the tick.
- `GetResult() -> void` — Throws the task's `OperationCanceledException` when the hop could not run.
- `IsCompleted { get; } -> bool` — True only on this task's tick (no hop needed), while the task is not cancelled.
- `OnCompleted(Action continuation) -> void` — Queues `continuation` for the tick, whatever context is current.
- `UnsafeOnCompleted(Action continuation) -> void` — As `Awaiter.OnCompleted`.

## KilnTaskOptions `0.2.10` — How `KilnTasks.Run` runs a task.
- `KilnTaskOptions()`
- `KilnTaskOptions(bool LongRunning = false, KilnTaskPriority Priority = KilnTaskPriority.Normal)`
- `Deconstruct(out bool LongRunning, out KilnTaskPriority Priority) -> void`
- `Equals(KilnTaskOptions other) -> bool`
- `LongRunning { get; init; } -> bool` — The task gets a dedicated worker instead of one of the shared ones: for work that runs for seconds or loops until...
- `static operator !=(KilnTaskOptions left, KilnTaskOptions right) -> bool`
- `static operator ==(KilnTaskOptions left, KilnTaskOptions right) -> bool`
- `Priority { get; init; } -> KilnTaskPriority` — The task's place in the mod's own queue.

## KilnTaskPriority `0.2.10` — A task's place in its mod's own queue.
enum: Normal, Low, High

## KilnTaskQueueFullException `0.2.10` — The mod already holds as many tasks as Kiln allows one mod (1024 that have not ended).

## KilnTasks `0.2.10` — Runs a mod's heavy work off the game's tick, on Kiln's own worker threads: scans, path finding, planning, file and network IO, big calculations.
- `Run(string name, Action<KilnTaskContext> work, KilnTaskOptions? options = null) -> KilnTask` — Starts `work` on a Kiln worker and returns at once.
- `Run(string name, Func<KilnTaskContext, Task> work, KilnTaskOptions? options = null) -> KilnTask` — Starts async `work` on a Kiln worker and returns at once.
- `Run<T>(string name, Func<KilnTaskContext, Task<T>> work, KilnTaskOptions? options = null) -> KilnTask<T>` — Starts async `work` with a result on a Kiln worker and returns at once...

## KilnTaskState `0.2.10` — Where a Kiln task stands (`KilnTask.State`).
enum: None, Queued, Running, Completed, Faulted, Cancelled

## KilnUnavailableException — A capability is off in this process: its service is not loaded, this game build lacks it, or it does not exist on this side.
- `KilnUnavailableException(string capability, string? reason = null, Exception? innerException = null)` — A new exception for `capability`.
- `Capability { get; } -> string` — The capability that is off.
- `Reason { get; } -> string?` — Why it is off; null when unknown.

## Lifecycle — Current lifecycle state, for mods that load late (hot reload) and missed the events.
- `static IsServerRunning { get; } -> bool` — True while the world is live (`ServerPhase.Running`).
- `static ModsLoaded { get; } -> bool` — True once `ModsLoadedEvent` fired (all mods found at start-up are loaded).
- `static Phase { get; } -> ServerPhase` — Where the server is in its life now.
- `static StartedAtStep { get; } -> ulong` — Server-world step at which `ServerStartedEvent` fired (0 before).

## Logger — Writes to `kiln/logs/kiln.log`, tagged with the mod's id.
- `Debug(string message) -> void` — Writes a `LogLevel.Debug` line.
- `Error(Exception e, string message) -> void` — Writes an `LogLevel.Error` line: the message, then the exception (its type, message and stack).
- `Error(string message) -> void` — Writes an `LogLevel.Error` line.
- `Info(string message) -> void` — Writes an `LogLevel.Info` line.
- `Warn(string message) -> void` — Writes a `LogLevel.Warn` line.
- `Write(LogLevel level, string message) -> void` — Writes a line at `level`.

## LogLevel — How severe a log line is: from `LogLevel.Error` (1, the most severe) to `LogLevel.Trace` (5, the finest detail).
enum: Error, Warn, Info, Debug, Trace

## LoopWait — A wait of the engine's event loop, about to happen (`Pacing.OnLoopWait`).
- `LoopWait()`
- `LoopWait(int Milliseconds, long LeftNanoseconds, LoopWaitKind Kind = LoopWaitKind.Handles)`
- `Kind { get; init; } -> LoopWaitKind` — Which wait.
- `LeftNanoseconds { get; init; } -> long` — The time left before the loop's deadline.
- `Milliseconds { get; init; } -> int` — What the game will wait: the time left, truncated to whole milliseconds.

## LoopWaitHandler — Runs on the thread that waits, just before the event loop waits.
- `Invoke(in LoopWait wait) -> void`

## LoopWaitKind — Which wait of the event loop.
enum: Handles, Messages

## Mod — Base class of a Kiln mod.
- `Context { get; } -> ModContext` — The mod's view of Kiln.
- `Log { get; } -> Logger` — Shortcut for `Context.Log`.
- `virtual OnBoot(BootContext boot) -> void` `0.2.3` — The mod's boot phase (its kiln.mod.json says `"boot": true` or `"required"`): called once per process, on the boot...
- `virtual OnLoad() -> void` — Called once after the mod is loaded.
- `virtual OnUnload() -> void` — Called once before the mod is unloaded (server shutdown, reload, unload of a dependency, or disable).
- `Text(string key, params object?[] args) -> string` — A text of the mod, in the game's language (`Game.Language`): `Text("shop.buy")`, `Text("shop.price", 12)` for `{0}`...
- `TextIn(string language, string key, params object?[] args) -> string` — Like `Mod.Text` in another language (e.g. one a player chose in your own settings).
- `WorldConfig<T>(string? name = null) -> WorldConfig<T>` — The mod's settings for the world that is open: `WorldConfig<MySettings>().Value.Difficulty`.

## `ModConfig<T>` — A mod's typed config.
- `Value { get; } -> T` — The running values.

## ModContext — Everything a mod gets from Kiln.
- `DataDirectory { get; } -> string` — Per-mod persistent directory (`kiln/data/<id>`), created on first use.
- `GameLog { get; } -> GameLogEvents` — Lines of the game's own log, delivered on the tick thread.
- `Info { get; } -> ModInfo` — The mod's identity and metadata (its `kiln.mod.json`).
- `Locale { get; } -> ModLocale` — The mod's strings from `locales/<language>.json` (see `Mod.Text`).
- `Log { get; } -> Logger` — The mod's logger: lines go to `kiln.log` tagged with the mod's id.
- `Messages { get; } -> ModMessages` — Inter-mod messages sent to this mod with `Mods.Send` (Forge's InterModComms).
- `Scheduler { get; } -> Scheduler` — Run work on the tick thread from anywhere; await the next tick.
- `Tasks { get; } -> KilnTasks` `0.2.10` — Run heavy work off the tick, on Kiln's own worker threads, attributed to this mod (`KilnTasks`).
- `Tick { get; } -> TickEvents` — Server-world tick events (24.5 Hz on a dedicated server).

## ModDependency — A dependency declared in `kiln.mod.json`.
- `ModDependency(string Id, string VersionRange, bool Optional)`
- `Id { get; init; } -> string` — The mod id depended on (`kiln` means Kiln itself).
- `Optional { get; init; } -> bool` — Load after it when present; load anyway when absent.
- `VersionRange { get; init; } -> string` — Accepted versions, e.g. `^1.2`, `>=1.0 <2`, `[1.0,2.0)`; `*` = any.

## ModDisabledEvent — The fault policy disabled a mod (too many exceptions within the window; kiln.json `mods.fault_limit`, `mods.fault_window_s`).
- `CrashReport { get; } -> string?` — Path of the crash report, when one was written.
- `Faults { get; } -> int` — The exceptions the mod threw since it was loaded.
- `Id { get; } -> string` — The disabled mod's id.
- `Reason { get; } -> string` — The last exception's message.

## ModEntry — One mod Kiln knows about (loaded or not).
- `Dependencies { get; } -> IReadOnlyList<ModDependency>` — The dependencies the mod declared, in manifest order.
- `Directory { get; } -> string` — The mod's folder.
- `Error { get; } -> string?` — Why the mod is not loaded (or was disabled); null otherwise.
- `Id { get; } -> string` — The mod's id.
- `Info { get; } -> ModInfo?` — The full manifest metadata, when the manifest could be read.
- `IsLoaded { get; } -> bool` — True when `ModEntry.State` is `ModState.Loaded`.
- `LoadIndex { get; } -> int` — Position in the load order (0 = first), -1 when not loaded.
- `Name { get; } -> string` — The mod's display name.
- `Side { get; } -> ModSide` — Where the mod runs.
- `State { get; } -> ModState` — What became of the mod.
- `Stats { get; } -> ModStats` — Live stats (tick time, faults) of the running instance; zero when not loaded.
- `Version { get; } -> string` — The mod's version.

## ModInfo — Identity and metadata of a loaded mod, from its `kiln.mod.json`.
- `ModInfo(string Id, string Name, string Version, string Directory)`
- `Advanced { get; init; } -> bool` — The mod opted into the advanced tier (raw hooks, symbols, coremod-style patches).
- `ApiVersion { get; init; } -> string` — The Kiln API range the mod declared (`apiVersion`).
- `Authors { get; init; } -> IReadOnlyList<string>` — The mod's authors from `kiln.mod.json`.
- `Dependencies { get; init; } -> IReadOnlyList<ModDependency>` — Declared dependencies (required and optional), in manifest order.
- `Description { get; init; } -> string` — The mod's description from `kiln.mod.json`; empty when it has none.
- `Directory { get; init; } -> string` — The mod's folder (`kiln/mods/<folder>`).
- `Exports { get; init; } -> IReadOnlyList<string>` — Assemblies (file names) that dependent mods compile against and share at run time.
- `Features { get; init; } -> IReadOnlyList<string>` — Kiln features the mod requires (`Game.HasFeature`).
- `Id { get; init; } -> string` — Unique id (`[a-z0-9][a-z0-9._-]*`, at most 64 characters).
- `License { get; init; } -> string?` — The license the manifest names; null when it names none.
- `Name { get; init; } -> string` — Display name.
- `Side { get; init; } -> ModSide` — Where the mod runs: `server` (default), `client` or `both`.
- `SteamMessages { get; init; } -> bool` `0.2.13` — The mod talks to Steam users directly: its kiln.mod.json says `"steamMessages": true`, so it may open its own Steam...
- `Url { get; init; } -> string?` — The web address the manifest names; null when it names none.
- `Version { get; init; } -> string` — The mod's version (semantic versioning).

## ModLocale — A mod's strings in the languages it ships: one JSON file per language in `locales/` beside `kiln.mod.json` (`en.json`, `de.json`, `pt-BR.json`).
- `Directory { get; } -> string` — The `locales` folder (it need not exist).
- `const Fallback = "en" -> string` — The language every other falls back to.
- `Get(string key, params object?[] args) -> string` — The text of `key` in the language of the game (`Game.Language`), with its placeholders filled.
- `GetIn(string language, string key, params object?[] args) -> string` — Like `ModLocale.Get` for another language, e.g. one a player chose in your own settings.
- `Has(string key) -> bool` — Some file in the game language's lookup order has `key`.
- `Keys { get; } -> IReadOnlyList<string>` — Every key any file has, sorted.
- `Languages { get; } -> IReadOnlyList<string>` — The languages that have a file, as lower-case tags ("de", "pt-br"), sorted.
- `static LookupOrder(string language) -> IReadOnlyList<string>` — The language tags tried for `language`, in order: the language itself, its closest relatives, its main variety, English.
- `const MaxFileBytes = 1048576 -> int` — A locale file larger than this is not read (logged): texts, not data.
- `Missing(string language) -> IReadOnlyList<string>` — The keys the English file has that `language` has no text for in its own files: its file and its main variety's...
- `static Normalize(string? language) -> string` — "pt_BR", "PT-br" and " pt-br " are all "pt-br".
- `Reload() -> void` — Reads the files again.
- `TryGet(string key, out string text) -> bool` — The text of `key` in the language of the game, placeholders as written.

## ModMessages — A mod's inbox for `Mods.Send`.
- `Dropped { get; } -> long` — Messages dropped because the inbox was full.
- `On(string method, Action<ImcMessage> handler) -> IDisposable` — Handles messages for `method`; dispose the result to stop.
- `Queued { get; } -> int` — Messages waiting for a handler.
- `Take(string method) -> IReadOnlyList<ImcMessage>` — Takes (removes and returns) queued messages for `method` that no handler took, for mods that prefer to poll (Forge's...

## ModOperationResult — Result of a mod operation requested through `Mods`.
- `ModOperationResult(bool Ok, string Message, IReadOnlyList<string> Affected)`
- `Affected { get; init; } -> IReadOnlyList<string>` — Every mod id the operation unloaded or loaded (a reload cascades to dependents).
- `Message { get; init; } -> string` — What happened, for logs and chat.
- `Ok { get; init; } -> bool` — Whether the operation fully succeeded.

## ModReloadedEvent — A mod was hot-reloaded (control command `reload`, `Mods.ReloadAsync`).
- `Cascade { get; } -> IReadOnlyList<string>` — Dependents that were unloaded and loaded again with it, in load order.
- `Id { get; } -> string` — The reloaded mod's id.
- `Ok { get; } -> bool` — False when the mod or one of its dependents failed to load again.
- `Version { get; } -> string` — The version now on disk (it may have changed).

## Mods — The mod list, per-mod state and inter-mod messages.
- `static All { get; } -> IReadOnlyList<ModEntry>` — Every mod Kiln found, in load order (loaded first), then the ones that did not load.
- `const ApiVersion = "0.2.27" -> string` — The Kiln mod API version this Kiln implements; a manifest's `apiVersion` is checked against it.
- `static BootState(string modId) -> BootModReport` `0.2.3` — A mod's part in this process's boot generation (Mod.OnBoot, chapter 08): its state, its committed definitions...
- `static Current { get; } -> ModEntry?` — The mod whose code is running on this thread (null outside mod code).
- `static Get(string id) -> ModEntry?` — The mod with `id`, loaded or not; null when Kiln never saw it.
- `static IsLoaded(string id) -> bool` — True when the mod with this id is loaded.
- `static Load(string folder, Action<ModOperationResult> done) -> void` — Callback forms for code without a synchronization context (services, chat commands): the operation runs at the start...
- `static LoadAsync(string folder) -> Task<ModOperationResult>` — Loads the mod folder `kiln/mods/<folder>` at the start of the next step.
- `static Reload(string id, Action<ModOperationResult> done) -> void` — Callback forms for code without a synchronization context (services, chat commands): the operation runs at the start...
- `static ReloadAsync(string id) -> Task<ModOperationResult>` — Reloads a mod from disk at the start of the next step, together with every mod that depends on it (dependents unload...
- `static RuntimeApiVersion { get; } -> string` — The Kiln mod API version of the Kiln that is running (for example `0.2.0`); a manifest's `apiVersion` is checked...
- `static Send(string targetId, string method, object? payload = null) -> bool` — Sends an inter-mod message (Forge's `InterModComms.sendTo`).
- `static Side { get; } -> ModSide` — The side this process runs as (from the core: dedicated server or client).
- `static Unload(string id, Action<ModOperationResult> done) -> void` — Callback forms for code without a synchronization context (services, chat commands): the operation runs at the start...
- `static UnloadAsync(string id) -> Task<ModOperationResult>` — Unloads a mod (and its dependents) at the start of the next step.

## ModSide — Where a mod runs.
enum: Server, Client, Both

## ModsLoadedEvent — All mods found at start-up are loaded (Forge's `FMLLoadCompleteEvent`).
- `Loaded { get; } -> IReadOnlyList<ModEntry>` — Loaded mods in load order.
- `NotLoaded { get; } -> IReadOnlyList<ModEntry>` — Mods that failed or were skipped, with `ModEntry.Error`.

## ModState — What happened to a mod Kiln found in `kiln/mods`.
enum: Pending, Loaded, Disabled, Failed, Skipped, Unloaded

## ModStats — Time and faults of one mod since it was loaded.
- `ModStats()`
- `ModStats(long Steps, double TotalMs, double PreMsPerStep, double PostMsPerStep, double MaxMs, int Faults, int RecentFaults, DateTime? LastFault, string? LastFaultMessage)`
- `Faults { get; init; } -> int` — The exceptions it has thrown since it was loaded.
- `LastFault { get; init; } -> DateTime?` — When the last fault happened (UTC); null when there was none.
- `LastFaultMessage { get; init; } -> string?` — The exception type, its message and where it was thrown, for the last fault; null when there was none.
- `MaxMs { get; init; } -> double` — The longest single run of its handlers, in milliseconds.
- `PostMsPerStep { get; init; } -> double` — The average time its Post handlers took per step, in milliseconds.
- `PreMsPerStep { get; init; } -> double` — The average time its Pre handlers took per step, in milliseconds.
- `RecentFaults { get; init; } -> int` — The faults inside the fault policy's time window (`mods.fault_window_s`); the mod is disabled when this reaches...
- `Steps { get; init; } -> long` — The steps the mod's Pre handlers have run in since it was loaded.
- `TotalMs { get; init; } -> double` — The time spent in its handlers, Pre and Post together, in milliseconds.

## OverrideInfo — One claim in force (`Overrides.Active`).
- `OverrideInfo(string Mod, string Target, string Detail)`
- `Detail { get; init; } -> string` — For a resource field: its path and value.
- `Mod { get; init; } -> string` — The mod that holds it ("kiln" for Kiln's own tools).
- `Target { get; init; } -> string` — What it holds: the name given to `Overrides.Claim`, or a resource.

## OverrideResult — The answer to a claim.
- `OverrideResult(OverrideStatus Status, string Message, string? HeldBy = null)`
- `HeldBy { get; init; } -> string?` — For `OverrideStatus.Conflict`: the id of the mod that holds the thing.
- `Message { get; init; } -> string` — A sentence for the log or the player.
- `Ok { get; } -> bool` — True when `OverrideResult.Status` is `OverrideStatus.Applied`.
- `Status { get; init; } -> OverrideStatus` — What happened.

## Overrides — Changes a mod makes to shared things, each with a way back.
- `static Active { get; } -> IReadOnlyList<OverrideInfo>` — Every claim in force, by every mod, oldest first.
- `static Claim(string target, Action apply, Action restore) -> OverrideResult` — Claims `target` (any name that is the same for everyone who wants that thing, such as `"mymod:max-players"` or...
- `static HolderOf(string target) -> string?` — The id of the mod that holds `target` (a `Overrides.Claim` name), or null.
- `static Release(string target) -> bool` — Gives back this mod's claim on `target` (runs its restore).
- `static RestoreAll() -> int` — Gives back every claim this mod holds, newest first: resource fields get what the game had, and each...

## OverrideStatus — What happened to a claim.
enum: Applied, Conflict, NotFound, Unavailable

## Pacing — Where the engine waits and how many worker threads it starts.
- `static Available { get; } -> bool` — At least one wait or cap can be used on this build: Kiln found the place in the game's code.
- `static CapTaskWorkers(TaskWorkerCaps caps) -> bool` — Caps the worker threads each task group starts with, for the calling mod.
- `static EffectiveTaskWorkerCaps { get; } -> TaskWorkerCaps` — The cap each task group starts with, as the mods asked for them (0: left to the game).
- `static HandlerCount(PacingSites site) -> int` — Handlers registered now, per site (diagnostics).
- `static OnLoopWait(LoopWaitHandler handler) -> PacingHandler` — Runs `handler` before every wait of the engine's event loop on its handles, on the thread that waits: the main loop...
- `static OnServerWait(ServerWaitHandler handler) -> PacingHandler` — Runs `handler` where the server's game loop has picked its wait between two passes (`Pacing.ServerIdleWaitNs`...
- `const ServerIdleWaitNs = 40000000 -> long` — The server loop's wait with nobody online (nanoseconds).
- `const ServerMaxWaitNs = 1000000000 -> long` — The longest wait a handler can set for the server loop: what handlers leave is clamped into `Pacing.ServerMinWaitNs`...
- `const ServerMinWaitNs = 100000 -> long` — The shortest wait a handler can set for the server loop: the game's own shortest (`Pacing.ServerSlowWaitNs`).
- `const ServerOnlineWaitNs = 1000000 -> long` — The server loop's wait with players online.
- `const ServerSlowWaitNs = 100000 -> long` — The server loop's wait after a pass longer than 14 ms.
- `static Sites { get; } -> PacingSites` — Which waits this build lets mods pace (what the game's code lets Kiln hook).
- `static TaskWorkersStarted { get; } -> bool` — The task system has started its worker pools: caps come too late.

## PacingHandler — A pacing handler a mod registered.
- `Dispose() -> void` — Removes the handler.
- `Faulted { get; } -> bool` — The handler threw too often and was removed (see `Pacing`).
- `Found { get; } -> bool` — This build has the wait the handler is for (it will be called).

## PacingSites — Which of the game's waits this build lets mods pace.
enum: None, LoopWait, MessageWait, ServerWait, TaskWorkers

## PickupReevaluationResult `0.2.4` — What `Decisions.ReevaluatePickup` did.
- `PickupReevaluationResult(bool Ok, string? Reason, int ExposuresReset)`
- `ExposuresReset { get; init; } -> int` — How many exposures were reopened.
- `Ok { get; init; } -> bool` — True when at least one exposure was reopened.
- `Reason { get; init; } -> string?` — `NoActiveExposure` when the pair had none (no pickup was held for it, or the receiver left the item's range, or its...

## PlayerRef — A person on this server: the Steam id, the character they play (may be empty) and their session entity (none while offline).
- `PlayerRef()`
- `PlayerRef(ulong SteamId, Guid CharacterId, EntityRef Entity)`
- `CharacterId { get; init; } -> Guid` — The game's CharacterId; `Guid.Empty` when unknown.
- `Entity { get; init; } -> EntityRef` — The session entity while in game; `EntityRef.None` while offline.
- `HasCharacter { get; } -> bool` — True when the character is known.
- `IsNone { get; } -> bool` — True when every part is empty.
- `IsOnline { get; } -> bool` — True only when the held session entity has a serial and is still alive (`EntityRef.IsAlive`).
- `IsSamePerson(PlayerRef other) -> bool` — Compares Steam ids when both are non-zero; otherwise compares character ids when both are non-empty; otherwise false.
- `static readonly None -> PlayerRef` — No player.
- `static Of(EntityId player) -> PlayerRef` — The player whose session entity this is; `PlayerRef.None` when it is not a player in game.
- `Resolve() -> PlayerRef` — The same person, with their current session entity (`EntityRef.None` when offline).
- `SteamId { get; init; } -> ulong` — SteamID64; 0 when unknown.

## `Point<TIn, TOut>` — A point a mod defined: it asks it with `Point.Decide` whenever its rule applies.
- `AllowRefuse { get; } -> bool` — Whether a link may refuse: a `PointVerdict.Refuse` then ends the chain with `PointOutcome.Refused`.
- `Decide(TIn input, TOut answer) -> PointDecision<TOut>` — Asks the point: `answer` is the owner's own answer; each link follows in order and sees the answer as the links...
- `Links { get; } -> IReadOnlyList<PointLink>` — The links that run, in order.
- `Mode { get; } -> PointMode` — How links act on the answer.
- `Name { get; } -> string` — "\<owner id>:\<name>".
- `Owner { get; } -> string` — The id of the mod that defined the point.

## `PointDecision<TOut>` — The answer of `Point.Decide`.
- `PointDecision()`
- `PointDecision(PointOutcome Outcome, TOut Value, string? DecidedBy)`
- `DecidedBy { get; init; } -> string?` — The id of the mod whose link last changed the answer, or refused; null when no link did.
- `IsRefused { get; } -> bool` — True when `PointDecision.Outcome` is `PointOutcome.Refused`.
- `Outcome { get; init; } -> PointOutcome` — What the links did to the owner's answer.
- `Value { get; init; } -> TOut` — The answer after the links; when a link refused, the answer at that moment.
- `ValueOr(TOut refused) -> TOut` — The answer, or `refused` when a link refused.

## PointLink — One link on a point, for diagnostics and for switching it off.
- `Dispose() -> void` — Detaches the link.
- `Enabled { get; } -> bool` — False when the link was switched off, threw, or does not match the point's types.
- `Observer { get; } -> bool` — True for a link attached with Observe: it never changes the answer.
- `OffReason { get; } -> string?` — Why the link does not run, or null.
- `Order { get; } -> PointOrder` — Where the link runs among the point's links.
- `Owner { get; } -> string` — The mod that attached the link.
- `Point { get; } -> string` — The point's full name.

## PointMode — How links act on a point's answer, as the owning mod chooses.
enum: Observe, Advise, Override

## PointOrder — Where a link runs among the other links of a point: lower `PointOrder.Priority` first, then before or after the links of the named mods.
- `After { get; init; } -> IReadOnlyList<string>` — Mods whose links run before this one.
- `Before { get; init; } -> IReadOnlyList<string>` — Mods whose links run after this one.
- `static Default { get; } -> PointOrder` — The default order: priority 0, no before or after constraints.
- `Priority { get; init; } -> int` — Lower runs first.

## PointOutcome — What asking a point came to.
enum: Kept, Changed, Refused

## PointRegistry — The points and links of one set of mods.
- `AllLinks { get; } -> IReadOnlyList<PointLink>` — Every link, including those that are off.
- `Define<TIn, TOut>(string owner, string name, PointMode mode, bool allowRefuse = false) -> Point<TIn, TOut>` — Defines the point `<owner>:<name>`.
- `Defined { get; } -> IReadOnlyList<(string Name, string Owner, PointMode Mode)>` — Every defined point's name, owner and mode.
- `Extend<TIn, TOut>(string owner, string point, Func<TIn, TOut, (PointVerdict, TOut)> link, PointOrder? order = null) -> PointLink` — Attaches a link that may change the answer of `point` (a full name).
- `Log { get; set; } -> Action<string>?` — Where notes go (a link turned off, a cycle in the order).
- `Observe<TIn, TOut>(string owner, string point, Action<TIn, TOut> observer, PointOrder? order = null) -> PointLink` — Attaches a link that sees each answer and never changes it.
- `RemoveOwner(string owner) -> void` — Removes every point and link of `owner` (a mod that unloads), and foreign links to its points so their delegates and...
- `SetEnabled(string point, string owner, bool enabled) -> void` — Switches the links of `owner` on `point` off or back on (a player's choice; Enshwick keeps it in mods.toml).

## Points — Extension points between mods (see `PointRegistry`): the calling mod owns what it defines and attaches, and its points and links go when it unloads.
- `static Define<TIn, TOut>(string name, PointMode mode, bool allowRefuse = false) -> Point<TIn, TOut>` — Defines the point "\<this mod's id>:`name`".
- `static Extend<TIn, TOut>(string point, Func<TIn, TOut, (PointVerdict, TOut)> link, PointOrder? order = null) -> PointLink` — Attaches this mod's link to another mod's point, "\<owner id>:\<name>".
- `static Observe<TIn, TOut>(string point, Action<TIn, TOut> observer, PointOrder? order = null) -> PointLink` — Attaches this mod's observer to a point.
- `static Shared { get; } -> PointRegistry` — The registry every mod shares.

## PointVerdict — What a link does with the answer it is shown.
enum: Pass, Set, Refuse

## RangeAttribute — Allowed range of a number (config entry, command argument).
- `RangeAttribute(double min, double max)`
- `RangeAttribute(double min, double max, double step)` `0.2.17` — A range whose config slider moves in steps of `step` from `min` (`min`, `min + step`, `min + 2 * step`, ... up to...
- `Max { get; } -> double` — The highest allowed value (for a `TimeSpan`, in seconds).
- `Min { get; } -> double` — The lowest allowed value (for a `TimeSpan`, in seconds).
- `SoftMax { get; set; } -> bool` `0.2.26` — The high end is soft (mod API 0.2.26): the config slider stops at `RangeAttribute.Max`, but a value above it is kept...
- `SoftMin { get; set; } -> bool` `0.2.26` — The low end is soft (mod API 0.2.26): the config slider stops at `RangeAttribute.Min`, but a value below it, typed...
- `Step { get; } -> double?` `0.2.17` — The slider step given to the three-argument constructor, as given; null for `[Range(min, max)]`.

## Reflected — Components by name with the game's own field layout, and checked writes (kiln.world + kiln.types).
- `static All(string component) -> IReadOnlyList<EntityId>` — Every entity that has the component (loaded entities only).
- `static Component(EntityId entity, string component) -> ReflectedValue?` — The entity's component, or null when it has none.

## ReflectedResource — One of the game's resources as a reflected struct: read fields by path, change them, put them back.
- `Bytes { get; } -> byte[]` — A copy of the bytes the game gets for the resource now.
- `Count(string path) -> int` — The number of elements of the list or array at `path`, or the length in bytes of a text.
- `Describe(int maxArray = 8) -> string` — Every field with its value, one per line (`damageSetup.dmgMod = 1.5`), lists cut after `maxArray` elements.
- `Exists { get; } -> bool` — Whether the game (or a mod) still serves this resource.
- `Get<T>(string path) -> T` — The value at `path` converted to `T`.
- `Guid { get; } -> Guid` — The resource's GUID.
- `HeldBy(string path) -> string?` — The id of the mod that changed the field at `path` through `Overrides`, or null.
- `IsSet(string path) -> bool` — Whether the optional part at `path` is present, or the variant holds a value.
- `Original(string path) -> object?` — The value at `path` before any change made through `Overrides` (what the game has, or had when the first change was...
- `Part { get; } -> uint` — The resource's part index (0 for nearly every type).
- `Restore(string path) -> bool` — Gives the field back what it had before this mod changed it.
- `RestoreAll() -> int` — Undoes every change this mod made to this resource.
- `Set(string path, object? value) -> OverrideResult` — Changes one field (copy-on-write: the game gets the resource with your value in it).
- `this[string path] { get; set; } -> object?` — The value at `path`, as `ReflectedResource` describes.
- `Type { get; } -> GameType` — The resource's reflected type.
- `VariantType(string path) -> GameType?` — The type a variant at `path` holds (for example one component of a template), or null when empty.

## ReflectedResources — Reflected resources: `ReflectedResources.Get` a resource by GUID and type, list a type's resources, and find resources by the value of one of their fields.
- `static All(GameType type) -> IEnumerable<ReflectedResource>` — `ReflectedResources.All` with the type as a `GameType`.
- `static All(string type) -> IEnumerable<ReflectedResource>` — Every resource of a type, the game's and the mods' new ones, in the order kiln.assets lists them.
- `static Available { get; } -> bool` — Whether resources can be read: kiln.assets (with its resource hooks on, row `assets.resources`) and kiln.types are...
- `static FindByField(GameType type, string path, object? value) -> IReadOnlyList<ReflectedResource>` — `ReflectedResources.FindByField` with the type as a `GameType`.
- `static FindByField(string type, string path, object? value) -> IReadOnlyList<ReflectedResource>` — The resources of a type whose field `path` equals `value`.
- `static Get(Guid guid, GameType type, uint part = 0) -> ReflectedResource?` — `ReflectedResources.Get` with the type as a `GameType`.
- `static Get(Guid guid, string type, uint part = 0) -> ReflectedResource?` — The resource with this GUID and type (part 0 unless the type has parts), or null when the game has none.

## ReflectedValue — A reflected struct (a component, an event copy): read and write fields by path, checked against the game's types.
- `Bytes { get; } -> byte[]` — A copy of the current bytes.
- `Describe(int maxArray = 16) -> string` — Every scalar field with its value, one per line (`dataStorage[0] = 20`).
- `Entity { get; } -> EntityId` — The entity of a component view (0 for copies).
- `Get<T>(string path) -> T` — The scalar field at `path`, converted to `T` (invariant culture).
- `this[string path] { get; set; } -> object?` — A scalar field by path: bool, long, ulong or double; a nested struct as a `ReflectedValue` copy.
- `Type { get; } -> GameType` — The type of the value.

## RegistryThreadOnlyAttribute `0.2.17` — Use only on the owning registry callback's thread.

## RenderThreadAttribute — Call this on the game's render thread only (client).

## RequiresCapabilityAttribute — Marks a member that needs a capability.
- `RequiresCapabilityAttribute(string name)`
- `Name { get; } -> string` — The capability's name, as `Capabilities.Is` takes it.

## RequiresRestartAttribute — A change of this entry only applies after the mod reloads (or the server restarts): a live edit is written to the file and reported as pending, and the running value...

## ResultEvent — An event that asks handlers for a decision: Allow, Deny or Default (Forge's `@HasResult`).
- `Allow() -> void` — Sets `ResultEvent.Result` to `EventResult.Allow`.
- `Decide(bool vanilla) -> bool` — The decision, with `vanilla` for `EventResult.Default`.
- `Deny() -> void` — Sets `ResultEvent.Result` to `EventResult.Deny`.
- `Result { get; set; } -> EventResult` — The decision so far; `EventResult.Default` until a handler sets one.

## Scheduler — Gets work onto the tick thread.
- `NextTick() -> Scheduler.NextTickAwaitable` — `await Scheduler.NextTick()` continues on the tick thread at the next step.
- `Post(Action action) -> void` — Runs `action` on the tick thread before the next step's Pre handlers.
- `SynchronizationContext { get; } -> SynchronizationContext` — The context Kiln installs while the mod's code runs on the tick thread: `await` without `ConfigureAwait(false)`...

## Scheduler.NextTickAwaitable — What `Scheduler.NextTick` returns: `await` it to continue on the tick thread at the next step.
- `NextTickAwaitable()`
- `NextTickAwaitable(Scheduler scheduler)`
- `GetAwaiter() -> Scheduler.NextTickAwaitable.Awaiter` — The awaiter the `await` uses.

## Scheduler.NextTickAwaitable.Awaiter — The awaiter of `NextTickAwaitable`.
- `Awaiter()`
- `Awaiter(Scheduler scheduler)`
- `GetResult() -> void` — Returns nothing: the await has no value.
- `IsCompleted { get; } -> bool` — Always false: the continuation is posted to the tick thread (`Scheduler.Post`).
- `OnCompleted(Action continuation) -> void` — Posts `continuation` to the tick thread.
- `UnsafeOnCompleted(Action continuation) -> void` — Posts `continuation` to the tick thread.

## ServerOnlyAttribute — Works on the server only (a dedicated server, or the host of a local game).

## ServerPhase — Where the server is in its life, as Kiln sees it.
enum: Booting, Starting, Running, Stopping, Idle

## ServerStartedEvent — The world is live (Forge's `ServerStartedEvent`): the game switched from `LoadEcsScene` to `Run` and the server world steps.
- `Source { get; } -> string` — How Kiln knew: `game-log` (the Run switch was seen), `first-step` (no log mirror), `timeout` (the switch line never...
- `Step { get; } -> ulong` — The server-world step it fired on.

## ServerStartingEvent — The server is starting: mods are loaded and the server world steps (Forge's `ServerStartingEvent`: register commands and world hooks here).
- `Step { get; } -> ulong` — The server-world step it fired on.

## ServerStoppingEvent — The server is shutting down (Forge's `ServerStoppingEvent`).
- `Reason { get; } -> string` — What triggered it: `SIGINT` (console Ctrl-C), `SIGTERM`, `SIGHUP`, `SIGQUIT`, `game-log` (the game's own shutdown...
- `Step { get; } -> ulong` — The server-world step it fired on.

## ServerWaitHandler — Runs on the thread that waits (the server loop, once a pass): `waitNanoseconds` is what the loop picked; set it to change it.
- `Invoke(ref long waitNanoseconds) -> void`

## SubscribeAttribute — Marks a method of a `Mod` (or of an object passed to `Events.Register`) as an event handler.
- `SubscribeAttribute(EventPriority priority = EventPriority.Normal)` — Marks the method as a handler that runs at `priority`.
- `Priority { get; } -> EventPriority` — Where the handler runs among the handlers of the event.
- `ReceiveCanceled { get; init; } -> bool` — Also run for events an earlier handler canceled.

## SyncedAttribute — Marks an entry (or a whole section) of a server or common config that clients must know (Forge: a server config synced at login).

## TaskWorkerCaps — The most worker threads the task system starts each group with; 0 leaves a group as the game sizes it.
- `TaskWorkerCaps()`
- `TaskWorkerCaps(int Default, int Background)`
- `static AtMost(int workers) -> TaskWorkerCaps` — The same cap for both groups.
- `Background { get; init; } -> int` — The background group: streaming, saves and other slow work.
- `Default { get; init; } -> int` — The default group, which also sizes the ECS task queue.
- `IsEmpty { get; } -> bool` — Both groups are left as the game sizes them.

## TickEvents — Server-world tick events of one mod.
- `Post -> TickHandler?` — After the sim step, once the world's jobs have finished.
- `Pre -> TickHandler?` — Before the world's sim step.

## TickHandler — A handler of `TickEvents.Pre` or `TickEvents.Post`; it gets the step's `TickInfo`.
- `Invoke(in TickInfo tick) -> void`

## TickInfo — One server-world sim step.
- `Clock { get; } -> uint` — The game's 60 Hz tick clock.
- `ClockDelta { get; } -> uint` — Game clock advance since the previous step (60 Hz units; 2-3 on a dedicated server).
- `ClockDeltaTime { get; } -> TimeSpan` — Game time elapsed since the previous step.
- `ClockPeriod { get; } -> long` — The game's raw tick period value.
- `Step { get; } -> ulong` — Steps since the server started, from 1.
- `WallDelta { get; } -> TimeSpan` — Wall time elapsed since the previous step.
- `WallDeltaMs { get; } -> double` — Wall-clock milliseconds since the previous step started.

## TickThreadOnlyAttribute — Call this on the tick thread only.

## WorkerThreadAttribute — Runs on, or may be called from, a worker thread; never touch game state there.

## `WorldConfig<T>` — A mod's typed settings for one world (see `Config.RegisterWorld`).
- `Value { get; } -> T` — The running values: the open world's, or the defaults while no world is open.

## WorldKind `0.2.4` — The kinds of world a process runs (kiln_api.h's KILN_WORLD_* values).
enum: Server, Prediction, Client, Menu `0.2.12`

## WorldPosition — A world position in metres (x east, y up, z north).
- `WorldPosition()`
- `WorldPosition(double X, double Y, double Z)`
- `DistanceTo(WorldPosition o) -> double` — The straight-line distance to `o`, in metres.
- `static From(Vector3 v) -> WorldPosition` — A position from a `Vector3` (metres).
- `ToVector3() -> Vector3` — This position as a `Vector3` (single precision).
- `X { get; init; } -> double` — Metres east.
- `Y { get; init; } -> double` — Metres up.
- `Z { get; init; } -> double` — Metres north.
