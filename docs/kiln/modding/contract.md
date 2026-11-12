# The API contract: errors, identity, threads

Three rules that every Kiln API follows, or is moving to. The value types live in `Kiln.Api`
(`managed/Kiln.Api/Results.cs`, `Identity.cs`, `ThreadingAttributes.cs`) and can be used without referencing a service.
Capturing entity serials and resolving players require initialized supplying services; merely having the types does
not make those operations available. Services auto-started by the first service tick are available from the first
tick callback, not in `OnLoad`. Tests: `managed/tests/Kiln.Api.Tests/IdentityTests.cs`, `ContractTests.cs`,
`ItemIdForwardTests.cs`.

## 1. The error rule

A call can fail in three ways, and each way has one shape.

| What happened | What you get |
|---|---|
| The game refused (player left, slot full, already set) | a result with `Ok = false` and a `Reason` |
| Your argument is wrong (unknown id, out of range, null) | `ArgumentException` (or a subclass) is thrown |
| The capability is off (service not loaded, this game build lacks it, wrong side) | `KilnUnavailableException` is thrown |

A refusal by the game is normal play, so it is a value you check, not an exception:

```csharp
var r = await Knowledge.Grant(player, name);
if (!r.Ok) Log.Info($"not granted: {r.Reason}");   // e.g. the player left before the step
```

A bad argument is a bug in the mod, so it throws:

```csharp
SlotId.Of(inventory, 9);   // ArgumentOutOfRangeException: an inventory has slots 0-7
```

A capability that is off throws `KilnUnavailableException`. It names the capability (`Capability`) and says why
(`Reason`, may be null). It derives from `NotSupportedException`, so code that already catches
`NotSupportedException` keeps working:

```csharp
try
{
    var me = EntityRef.Of(entity);
}
catch (KilnUnavailableException e) when (e.Capability == "identity.entities")
{
    Log.Warn(e.Message);   // "identity.entities is not available: …"
}
```

`Knowledge` already behaves this way: game reasons come back as `KnowledgeResult.Ok = false`, unknown ids and bad
arguments throw (`managed/services/Kiln.Quests/Knowledge.cs`, the remarks on `Knowledge`, lines 194-197).

`IKilnResult` (`bool Ok`, `string? Reason`) is the common shape of such results. The services' result records
implement it from dev.26 on; in this release none does yet, and other services may still throw their own exception
types for an unavailable capability.

## 2. Identity: EntityRef, ItemInstanceRef, PlayerRef, ItemId

An `EntityId` is a number, and the game hands the same number to a new entity after the old one is destroyed. A mod
that keeps an id across steps (a dummy it spawned, a chest it watches) can end up talking to a different entity. The
ref types carry enough to notice that.

- **`EntityRef(EntityId Id, ulong Serial)`**: an id plus the spawn serial it had when it was captured.
  `EntityRef.Of(id)` captures it (tick thread). `default` / `EntityRef.None` is no entity. A ref converts
  implicitly to `EntityId`, so you pass it to every API that takes an id; there is no conversion the other way, as
  that would hide the capture.
- **`IsAlive`** (tick thread) is false for `None`, false when the id no longer exists, and false when the id now has
  another serial: the id was reused. With no entity source, a non-none ref throws
  `KilnUnavailableException("identity.entities")`, just as `EntityRef.Of` does; `None.IsAlive` still returns false.
  `ItemInstanceRef.IsAlive` and `PlayerRef.IsOnline` (when its entity has a serial) follow that rule too.
  When the ref has no serial
  (`HasSerial` false, `Serial` 0), `IsAlive` true means no more than "an entity with this id exists": a reused id
  still counts as alive. `HasSerial` is the way to tell which answer you have.
- Where serials come from is up to the service that fills the seam. kiln.spawn has a tracker serial only for entities
  Kiln spawned with tracking; the game's `SpawnTime` component exists on entities spawned at run time, not on
  entities loaded from the save. An entity with neither has serial 0.
- **`ItemInstanceRef(EntityRef Entity, ItemId Item)`**: one particular item in the world: its entity and its type.
  It is not an item type (that is `ItemId`) and not Kiln.Blocks' `ItemRef` (which names an item for a recipe).
  `IsAlive` says nothing more than its entity's `IsAlive` does.
  Where the item is and its data come as extension methods in Kiln.Items.
- Test **`EntityRef.IsNone`** and **`ItemInstanceRef.IsNone`**, not `== None`: a none entity id can still carry a
  serial or item type, and record equality compares those fields too. For example, `new EntityRef(EntityId.None, 9)`
  and `new ItemInstanceRef(EntityRef.None, new ItemId(7))` both have `IsNone == true` but are not equal to `None`.
- **`PlayerRef(ulong SteamId, Guid CharacterId, EntityRef Entity)`**: a person: the Steam id, the character
  (`Guid.Empty` when unknown, see `HasCharacter`) and the session entity (none while offline). `PlayerRef.Of(entity)`
  finds the player of a session entity (`None` when it is not one); `Resolve()` gives the same person with their
  current session entity. `IsOnline` is true only when the held session entity has a serial and is alive; without a
  serial or for an earlier session it says false. Call `Resolve()` to find the current session. With no player service,
  `Of` and `Resolve` throw
  `KilnUnavailableException("identity.players")`. `==` also compares the session entity; use `IsSamePerson(other)`
  across sessions (compare Steam ids when both are non-zero, otherwise character ids when both are non-empty,
  otherwise false). This partial-id comparison is not transitive, so it is not a dictionary-key comparer;
  use a known `SteamId` or `CharacterId` as the key for persistent per-player records.
- **`ItemId`**: an item type (FNV-1a-32 of the item's GUID). It moved from Kiln.Items to Kiln.Api in dev.25.
  Kiln.Items forwards the type, so a mod built against the old Kiln.Items still loads and sees the same type.

Capabilities: `identity.entities` (entity serials), `identity.players` (players).

When no service supplies entity serials, which is the case until the release notes say otherwise, `EntityRef.Of`
for a non-none id and `IsAlive` on a non-none entity or item ref throw `KilnUnavailableException("identity.entities")`.
When no service supplies players, which is likewise the case until the release notes say otherwise, `PlayerRef.Of`
for a non-none id and `PlayerRef.Resolve()` on a non-none player ref throw `KilnUnavailableException("identity.players")`.
`None.IsAlive` and `PlayerRef.None.IsOnline` still return false; `PlayerRef.IsOnline` also returns false without a
session serial, and otherwise uses the entity source (throwing `identity.entities` when that source is missing).
The supplying services must be initialized and their capabilities enabled before these operations can work; for
auto-started services that means from the first tick callback, not in `OnLoad`. Services adopt these types as their
packages land; until then they still use `EntityId` and their own player types.

## 3. Side and thread attributes

Kiln marks its APIs with where and on which thread they may be called. The attributes are in `Kiln.Api` and a mod
may use them on its own code too.

| Attribute | Means |
|---|---|
| `[TickThreadOnly]` | Call it on the server-world tick thread only; elsewhere it throws `InvalidOperationException`. Use `Scheduler.Post` to get there. |
| `[ServerOnly]` | It works on the server (a dedicated server, or the host of a local game). |
| `[ClientOnly]` | It works on a game client only. |
| `[AnyThread]` | Safe from any thread. |
| `[RenderThread]` | Call it on the game's render thread only (client). |
| `[WorkerThread]` | It runs on, or may be called from, a worker thread; do not touch game state there. |

Only `[TickThreadOnly]` has an analyzer rule today: [KILN101](analyzers.md#kiln101) warns when such a member is
called from code that runs on another thread. The other five are markers for now; their analyzer rules are planned
for dev.26.
