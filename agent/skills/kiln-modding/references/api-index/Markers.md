# Markers (Kiln.Markers)

## MarkerAnchor `0.2.17` — A lease on one incarnation of an entity, for `MarkerSpec.Follow` (from `Markers.CaptureAnchorAsync` on a client, `Markers.CaptureServerAnchor` on the server).
- `Dispose() -> void` — Releases the caller's lease.
- `Entity { get; } -> EntityId` — The entity at capture (diagnostic only: the lease follows that incarnation, not the id).
- `WorldEpoch { get; } -> ulong` — kiln.markers' epoch of that world (diagnostic).
- `WorldKind { get; } -> uint` — The world kind it belongs to: 0 the server world, 2 the client's world (diagnostic).

## MarkerAnchorResult `0.2.17` — What became of a capture, by Kiln's error rule: a bad argument throws `ArgumentException`, a capability that is off throws `KilnUnavailableException` naming it, and a...
- `MarkerAnchorResult(bool Ok, string? Reason, MarkerAnchor? Anchor)`
- `Anchor { get; init; } -> MarkerAnchor?` — The caller's lease when `MarkerAnchorResult.Ok`; dispose it when it is no longer needed.
- `Ok { get; init; } -> bool` — True when `Anchor` holds a new lease.
- `Reason { get; init; } -> string?` — Why not, starting with the refusal's name: `NotFound` (no such entity, or it has no position in that world yet: not...

## MarkerCatalogChangedEvent `0.2.17` — The icon catalog became ready (or could not be read).
- `Count { get; } -> int` — Its icons.
- `Generation { get; } -> ulong` — The catalog's generation (1 for the first one; it does not change for the life of the process).
- `Ready { get; } -> bool` — The catalog can be read now.
- `Reason { get; } -> string?` — Why native map icons stay off with a ready catalog, or why it could not be read; null otherwise.
- `Role { get; } -> uint` — Whose catalog: 0 the server's, 1 this client's.

## MarkerChangedEvent `0.2.17` — One accepted change of a marker's logical value (not of its visibility).
- `Affected { get; } -> int` — Records changed: 1, or the count a `MarkerChangeKind.Cleared` stands for.
- `Audience { get; } -> MarkerSource` — Whose record: a local one, a server layer's, or a received one.
- `Cause { get; } -> string` — Why: `Api` (a call), `OwnerRetired` (its mod unloaded), `WorldStopped`, `PeerLeft`, `Snapshot` or `LinkStale`.
- `Id { get; } -> string` — The marker's key; empty for `MarkerChangeKind.Cleared`.
- `Kind { get; } -> MarkerChangeKind` — What happened.
- `Owner { get; } -> string` — The publisher's namespace (a mod's id).
- `PeerGeneration { get; } -> ulong` — The link generation of a received record; 0 otherwise.
- `PublisherGeneration { get; } -> ulong` — The publisher's binding generation (a new one each time a publisher binds).
- `Recipient { get; } -> EntityId` — The player a per-player server change is for; `EntityId.None` otherwise.
- `Sequence { get; } -> ulong` — The order of accepted changes (rising).
- `WorldEpoch { get; } -> ulong` — kiln.markers' epoch of the world the record belongs to.

## MarkerChangeKind `0.2.17` — What a `MarkerChangedEvent` reports.
enum: Added, Updated, Removed, Cleared

## MarkerIcon `0.2.17` — A marker type of the game's icon catalog (`Icons`), by its id.
- `MarkerIcon()`
- `MarkerIcon(uint Id)`
- `Id { get; init; } -> uint` — The game's marker type id; 0 is `MarkerIcon.None`.
- `static None { get; } -> MarkerIcon` — No icon: for markers without the `MarkerSurface.Map` surface (a map marker needs one).

## MarkerIconInfo `0.2.17` — One icon of the catalog.
- `MarkerIconInfo(MarkerIcon Icon, string Key, string Alias, string TextureName, Guid Texture, uint Flags, MarkerSurface Surfaces)`
- `Alias { get; init; } -> string` — Its friendly name: the texture name without a leading `mapmarker_`, lower-cased (`well_plant`).
- `Flags { get; init; } -> uint` — The type's metadata: 1 has an image, 2 shows a label, 4 shows a range, 8 above the fog of war, 16 a custom-pin type...
- `Icon { get; init; } -> MarkerIcon` — The type, for `MarkerSpec.Icon`.
- `Key { get; init; } -> string` — Its canonical name, `type:` and eight lower-case hex digits (`type:ffe5e444`).
- `Surfaces { get; init; } -> MarkerSurface` — `MarkerSurface.Map` when it may be used on the map; none for the six fast-travel types, which are listed but refused...
- `Texture { get; init; } -> Guid` — The icon image's GUID.
- `TextureName { get; init; } -> string` — The icon texture's name in the game.

## MarkerIconResult `0.2.17` — The answer of `Icons.Resolve`, by Kiln's error rule.
- `MarkerIconResult(bool Ok, string? Reason, MarkerIconInfo? Icon)`
- `Icon { get; init; } -> MarkerIconInfo?` — The icon; null unless `MarkerIconResult.Ok`.
- `Ok { get; init; } -> bool` — True when the name named exactly one icon.
- `Reason { get; init; } -> string?` — Why not: `NotFound`, `Ambiguous` (an alias of several types: use the key) or `CatalogNotReady`; null when...

## MarkerMapLabel `0.2.17` — What the native map shows for a marker's text.
enum: None, NativeTooltip

## MarkerResult `0.2.17` — What became of a marker request, by Kiln's error rule: a bad argument throws `ArgumentException`, a capability that is off throws `KilnUnavailableException` naming...
- `MarkerResult(bool Ok, string? Reason, bool Committed, int Queued, int Refused, int Affected)`
- `Affected { get; init; } -> int` — Records changed: 0 for a value identical to the stored one or an empty clear.
- `Committed { get; init; } -> bool` — The store holds the request (also when it was identical to what it held).
- `Ok { get; init; } -> bool` — True when the request was stored and, for a server send, every connected recipient took it.
- `Queued { get; init; } -> int` — Server sends: connected recipients whose update was admitted to their link (0 for local markers).
- `Reason { get; init; } -> string?` — Why not, starting with the refusal's name (`NotFound`, `Full`, `NotReady`, `CatalogNotReady`, `RequiresClient`...
- `Refused { get; init; } -> int` — Server sends: connected recipients that cannot take it (a vanilla client, a pending handshake).

## Markers `0.2.17` — Markers by key on the game's map, Kiln's drawn compass strip and world labels.
- `static CaptureAnchorAsync(EntityId entity, CancellationToken cancel = default(CancellationToken)) -> Task<MarkerAnchorResult>` — Captures a lease on the current incarnation of a client-world entity, for a marker that follows it...
- `static CaptureServerAnchor(EntityId entity) -> MarkerAnchorResult` — Captures a lease on the current incarnation of a server-world entity, now (the tick thread, a dedicated server or...
- `static CatalogChanged -> Action<MarkerCatalogChangedEvent>?` — The icon catalog became ready, or could not be read (`MarkerCatalogChangedEvent.Ready` false with a reason).
- `static Changed -> Action<MarkerChangedEvent>?` — Every accepted change of every publisher's markers, in accepted order, at the next step of their world (a client's...
- `static Clear() -> MarkerResult` — Removes every local marker of the calling mod; `Markers.Changed` reports one `MarkerChangeKind.Cleared` with their...
- `static ClearSent(EntityId player = default(EntityId)) -> MarkerResult` — Removes the calling mod's sent markers: of one player's layer (the broadcast shows again for them), or with...
- `static Remove(string id) -> MarkerResult` — Removes the calling mod's local marker of `id`; `Markers.Changed` reports it at the next client step.
- `static Send(EntityId player, MarkerSpec marker) -> MarkerResult` — Sends the calling mod's marker of `MarkerSpec.Id` to every player (`player EntityId.None`: the broadcast, also for...
- `static Set(MarkerSpec marker) -> MarkerResult` — Sets the calling mod's local marker of `MarkerSpec.Id` (adds it or replaces its value).
- `static SetNameplate(string id, EntityId entity, string text, float height = 2) -> Task<MarkerResult>` — A text label that follows an entity in the world (a nameplate): captures the entity, then sets the calling mod's...
- `static SuppressSent(EntityId player, string id) -> MarkerResult` — Hides the calling mod's marker `id` from one player without removing it for anyone else: the player's layer holds a...
- `static Unsend(EntityId player, string id) -> MarkerResult` — Removes the calling mod's sent marker `id` from one layer: `player EntityId.None` removes the broadcast one...
- `static VisibilityChanged -> Action<MarkerVisibilityChangedEvent>?` — A marker's admission on this client changed (`MarkerVisibilityChangedEvent`), raised at the client world's step on...

## Markers.Icons `0.2.17` — The game's map marker types with an icon image, by canonical key and alias.
- `static All { get; } -> IReadOnlyList<MarkerIconInfo>` — Every icon, sorted by id; empty while warming up.
- `static IsReady { get; } -> bool` — Whether the catalog has warmed up.
- `static Resolve(string name) -> MarkerIconResult` — The icon a name names: its canonical key (`type:ffe5e444`) or an exact alias (`well_plant`).

## MarkerSource `0.2.17` — Whose record a marker is.
enum: Local, ServerBroadcast, ServerTarget, Received

## MarkerSpec `0.2.17` — One marker, by key.
- `MarkerSpec(string Id, WorldPosition Position, string Text, MarkerIcon Icon, MarkerSurface Surfaces = MarkerSurface.Map | MarkerSurface.Compass, MarkerAnchor? Follow = null, float Height = 0, float MaxDistance = 0, int Priority = 0, uint Rgba = 4294967295, MarkerMapLabel MapLabel = MarkerMapLabel.None)`
- `Follow { get; init; } -> MarkerAnchor?` — An entity to follow instead of a fixed position (a lease from `CaptureAnchorAsync`); null for a fixed marker.
- `static FromJson(Json.JsonElement json) -> MarkerSpec` — Reads a marker from a JSON element (one object of a larger document); the rules of `MarkerSpec.FromJson`.
- `static FromJson(string json) -> MarkerSpec` — Reads a marker from JSON, for example...
- `Height { get; init; } -> float` — Metres above the point for the world label, in [-128, 128].
- `Icon { get; init; } -> MarkerIcon` — The map icon, from `Icons`: required with `MarkerSurface.Map`, and `MarkerIcon.None` without it.
- `Id { get; init; } -> string` — The key: 1 to 64 characters of `A-Z a-z 0-9 _ . : -`.
- `MapLabel { get; init; } -> MarkerMapLabel` — What the map shows for the text: `MarkerMapLabel.NativeTooltip` makes it the title of the game's own map tooltip.
- `MaxDistance { get; init; } -> float` — Metres from the camera beyond which it is not shown, in [0, 100000]; 0 = unlimited.
- `Position { get; init; } -> WorldPosition` — Where it is, in metres: x and z in [-1000, 12000], y in [-1000, 5000].
- `Priority { get; init; } -> int` — Higher first when a surface has no room, in [-32768, 32767].
- `Rgba { get; init; } -> uint` — Colour of the compass glyph and the world label, 0xRRGGBBAA (map icons keep the game's colours).
- `Surfaces { get; init; } -> MarkerSurface` — Where it is shown (default map and compass).
- `Text { get; init; } -> string` — The text drawn on the compass and world surfaces, and with `MarkerMapLabel.NativeTooltip` in the map's tooltip: at...
- `ToJson() -> string` — This marker as one JSON object in the form `MarkerSpec.FromJson` reads: every field written, the icon as its...

## MarkerSurface `0.2.17` — Where a marker is shown.
enum: Map, Compass, World

## MarkerVisibilityChangedEvent `0.2.17` — A marker's admission changed on this client: which of its requested surfaces it has now and why not the others.
- `After { get; } -> MarkerSurface` — The surfaces it has now.
- `Audience { get; } -> MarkerSource` — A local marker or a received one.
- `Before { get; } -> MarkerSurface` — The surfaces it had before this event (none for `MarkerVisibilityChangedEvent.Initial`).
- `Compass { get; } -> MarkerVisibilityReason` — The compass surface's reason.
- `Id { get; } -> string` — The marker's key.
- `Initial { get; } -> bool` — The first evaluation of this marker.
- `Map { get; } -> MarkerVisibilityReason` — The map surface's reason (`MarkerVisibilityReason.Ready` exactly when `MarkerVisibilityChangedEvent.After` has it).
- `Owner { get; } -> string` — The publisher's namespace (a mod's id).
- `PeerGeneration { get; } -> ulong` — The link generation of a received marker; 0 for a local one.
- `PublisherGeneration { get; } -> ulong` — The publisher's binding generation.
- `Recipient { get; } -> EntityId` — Always `EntityId.None` on a client (kept for the shape of the server events).
- `Requested { get; } -> MarkerSurface` — The surfaces the marker asks for.
- `Sequence { get; } -> ulong` — The sequence of the logical change that set the marker's current value.
- `World { get; } -> MarkerVisibilityReason` — The world-label surface's reason.
- `WorldEpoch { get; } -> ulong` — kiln.markers' epoch of the client world.

## MarkerVisibilityReason `0.2.17` — Why a requested surface of a marker is or is not admitted (`MarkerVisibilityChangedEvent`).
enum: Ready, MapCapacity, SurfaceBudget, CameraUnavailable, BehindCamera, OutOfRange, FollowMissing, AnchorEnded, WorldInactive, CapabilityOff, SourceStale, InvalidPosition, Removed, NotRequested
