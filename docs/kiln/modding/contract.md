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

`IKilnResult` (`bool Ok`, `string? Reason`) is the common shape of such results. From dev.27 the result records of the
services that took up the contract implement it (`SpawnResult`, `BuildResult`, `PasteResult`, `PlaceOnceResult`,
`TalentResult`, `CommandResult`, `QuestResult`, `ConversationResult`, `KnowledgeResult`, ...); through the interface
`Reason` is null when `Ok`. Services not adopted yet may still throw their own exception types for an unavailable
capability.

## 2. Identity: EntityRef, ItemInstanceRef, PlayerRef, ItemId

An `EntityId` is a number, and the same number can come to name another entity: a player slot (1..1023) goes to the
next player who joins, and every id is handed out again when a world is set up (a restart, a hosted game that loads
another world), so an id remembered from before a restart names whatever the save loaded there. Inside one running
world the game hands out every other id from a counter that only grows (kiln.items' evidence:
docs/kiln/services/items.md, "The stamp, and why it is enough"). A mod that keeps an id across steps (a dummy it
spawned, a chest it watches, an item) holds a number, not an entity. The ref types carry enough to notice that.

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
- Where serials come from: three sources, asked in a fixed order (Players, Items, Spawn); the first that knows the
  entity answers, and serials of different sources never compare equal. **Items** (Kiln.Items, capability
  `items.instances`): an item entity's stamp, a sequence number kiln.items gives every item entity it sees. **Spawn**
  (Kiln.Spawn): any other entity kiln.spawn can inspect: the game's `SpawnTime` when the entity has one (everything
  spawned at run time, by the game or by Kiln), else the kiln.spawn tracker serial of a record re-found after a
  restart, else 0. **Players** (Kiln.Players): player slots (ids 1 to 1023), stamped per session, so the next player
  in the same slot is another entity.
- **`ItemInstanceRef(EntityRef Entity, ItemId Item)`**: one particular item in the world: its entity and its type.
  It is not an item type (that is `ItemId`) and not Kiln.Blocks' `ItemRef` (which names an item for a recipe).
  `IsAlive` says nothing more than its entity's `IsAlive` does.
  Where the item is comes from Kiln.Items (`item.Location()`, `Items.Held`, `Items.Equipped`); its data calls (`Get`,
  `Set`, `Has`, ...) are extension methods in Kiln.Saves.
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
- **`ItemId`**: an item type (FNV-1a-32 of the item's GUID). It moved from Kiln.Items to Kiln.Api in dev.26.
  Kiln.Items forwards the type, so a mod built against the old Kiln.Items still loads and sees the same type.

Capabilities: `identity.entities` (entity serials), `identity.players` (players).

On a server, Kiln.Items and Kiln.Spawn supply entity serials (`identity.entities`). Their sources start at the first
service tick, so in `OnLoad` `EntityRef.Of` for a non-none id and `IsAlive` on a non-none ref still throw
`KilnUnavailableException("identity.entities")`, and they also throw while the server world is still loading its save
(before the game's Run state: kiln.spawn's source says "the server world is not running yet"). From the first tick in
which the world runs they answer (lab: step 272-274 after a start, the first tick callback a mod gets with
`Spawn.WorldRunning` true). Without Kiln.Spawn (or on a game build where kiln.spawn is off) every entity that is not an
item entity throws; without kiln.items v4 (`items.instances` off) item entities are answered by kiln.spawn's source with
weaker serials. A ref has no serial (`HasSerial` false: `IsAlive` only says that the id exists) for an entity the save
loaded that has no `SpawnTime`: in the lab world (ids 1 to 2681 after a start) 232 of 1,518 entities, all of them
`Scatter_*` (83), `Prop_*` (78), `DefaultInventory` (48, the linked 8-slot inventories of chests and stations),
stations and factories (`T1_*`, `Craftingstation`, 12), NPCs (6) and a few more props; 1,219 had a `SpawnTime` (every
entity streamed in or spawned at run time, by the game or by Kiln) and 67 item entities had kiln.items' stamp. A record
kiln.spawn re-finds after a restart gives its tracker serial instead (none in that world).

Kiln.Players supplies players (`identity.players`) and the stamps of player slots, also from the first service tick:
`PlayerRef.Of`, `Resolve()` and `IsOnline` work from the first tick callback, not in `OnLoad`. A player who is joining
or leaving gives an offline ref. On a server where kiln.players is unavailable, `PlayerRef.Of` for a non-none id and
`PlayerRef.Resolve()` on a non-none ref throw `KilnUnavailableException("identity.players")`, and a player slot's
`EntityRef` throws `identity.entities`. `None.IsAlive` and `PlayerRef.None.IsOnline` return false in every case.
Services adopt these types as their packages land; until then they still use `EntityId` and their own player types.

## 3. Change events

Every write Kiln makes for a mod posts a bus event (`ContentChangedEvent`, `CreatureChangedEvent`, `SettingChangedEvent`,
`EntitySpawnedEvent`, ...), on the tick thread, right after the write took, and only when it changed something: a
data-pack document whose every section was refused, a reset of a layer the mod did not have or of a creature that is
gone posts nothing. A write made while an event of the same kind is being dispatched (a handler of it, or a catch-all
`Events.On<KilnEvent>` handler, writes again) is not posted inside that dispatch: its event waits, under the same rule.
So a handler that writes in answer to the event sees its own write once a step at most, never recursively, and a mod
written before the event existed keeps working (no `Events.MaxDepth` exception, no faulted handler). What a waiting
event is promised:

- **When.** At the start of the next step, before anything else of that step runs (Kiln's own service handlers, queued
  events, every mod's tick), one per changed value (the newest write wins). A write made during that delivery waits
  for the step after. The order in which services or mods subscribed does not change this.
- **Nothing is dropped for want of room.** The waiting set holds one event per value written during a dispatch in one
  step, and is emptied at the next.
- **Identity, not only an id.** An event about an entity (`CreatureChangedEvent`, `CombatChangedEvent`) keeps the
  entity's spawn serial from the moment of the write and goes out only while that entity is still there: if it is gone,
  or its id now names another entity, the event is dropped instead of naming the wrong one (the entity's end is its own
  news). The same holds for an impact instance (`ImpactVariableChangedEvent`). Without an entity source to ask (no
  Kiln.Spawn on that side) Kiln cannot tell, and the event goes out as written.
- **At a stop.** When the server stops or a client's hosted world closes, the events that wait go out before the mods
  unload, as the step they were waiting for. A write made by that last round's handlers is not announced (no step
  follows); Kiln logs how many.

The events added in dev.27 post through `managed/Kiln.Api/Services/ChangeNotices.cs` (tests:
`managed/tests/Kiln.Contract.Tests/ChangeEventTests.cs`, `managed/tests/Kiln.Host.Tests/ChangeNoticeStopTests.cs`); the
older ones (`SettingChangedEvent`, `EntitySpawnedEvent`, `EntityDestroyedEvent`, ...) keep the inline delivery they
always had, so nothing changes for a mod that handles them.

## 4. Additive in source

A mod built against an earlier release keeps loading (the binary gate, `tools/check.sh`), and its source keeps
compiling. A new overload in a method group that shipped can break the second without the first noticing:
`var destroy = Spawn.Destroy;` has no delegate type once `Destroy` has two overloads, and `Chat.Tell(default, "hi")`
turns ambiguous. So a new shape that takes an identity type gets a name of its own (`Spawn.DestroyIfAlive(EntityRef)`,
`Chat.TellIfOnline(PlayerRef, text)`, `Permissions.PersonHas(PlayerRef, node)`, `PermissionSubject.ForPerson(PlayerRef)`)
instead of an overload. `managed/tests/Kiln.Contract.Tests/OldCallShapesTests.cs` compiles call shapes of dev.26 against
the services as they are and refuses a new overload in a method group of the services' `PublicAPI.Shipped.txt`.
