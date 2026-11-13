# Kiln event bus (`Kiln.Events`, like Forge's `EVENT_BUS`)

`managed/Kiln.Api/Events.cs`. One bus for every mod and service. Tests: `managed/tests/Kiln.Api.Tests/EventsTests.cs`,
lab: `mods/tests/api-test`.

## Using it
```csharp
public sealed class MyMod : Mod
{
    [Subscribe]                                         // registered by Kiln before OnLoad, removed on unload
    private void OnJoin(PlayerJoinedEvent e) => Log.Info($"{e.Player.Name} joined");

    [Subscribe(EventPriority.High, ReceiveCanceled = true)]
    private void OnChat(ChatMessageEvent e) { if (e.Text.Contains("badword")) e.Cancel(); }

    public override void OnLoad()
    {
        var sub = Events.On<CommandExecutedEvent>(e => Log.Info($"{e.Source} ran {e.Line}"));  // sub.Dispose() to stop
        var ev = Events.Post(new MyEvent());           // your own event class: tick thread, returns it after dispatch
    }
}
```
- `Events.On<T>(handler, priority, receiveCanceled)`, `Events.Register(obj)` (its `[Subscribe]` methods; a `Type`: static
  ones), `Events.Unregister(obj)`, `Events.Post(ev)` (tick thread), `Events.Enqueue(ev)` (any thread, posted at the next
  step), `Events.HasSubscribers<T>()` (lock-free: two memory reads), `Events.Subscriptions`,
  `Events.Diagnostics.{Handlers, Posts, HandlerCalls, Queued, ResetStats}`.
- Order: `Highest, High, Normal, Low, Lowest, Monitor`, then subscription order. A handler of a base class receives
  derived events (a `KilnEvent` handler receives all).
- `CancelableEvent`: `Cancel()` / `Canceled`; later handlers are skipped unless `receiveCanceled`; those may un-cancel.
  `CanceledBy` = mod id of the handler that canceled.
- `ResultEvent` / `CancelableResultEvent` (Forge `@HasResult`): `Result` = `Default | Allow | Deny`, `Allow()`,
  `Deny()`, `Decide(vanilla)`.
- Each handler runs as its mod (`await`, scheduler and service calls are attributed to it). A throwing handler is
  logged, the others still run; after 5 faults it is removed (`Events.MaxFaults`); `HandlerFaulted` tells the loader.
- Dispatch works on a snapshot: posting inside a handler works (up to 32 levels, `MaxDepth`); a handler subscribed
  during dispatch sees the next post; a handler disposed during dispatch does not run any more.
- Unload: every subscription of a mod ends when it unloads (also the bus's caches of the mod's own event types, so the
  load context can be collected; verified by `ModLeakTests` and the lab's `gc` control command).
- Producing an event lazily: an event class's static constructor runs when the first handler subscribes to that class
  (`PlayerJoinedEvent` starts its pump that way). Build events only when `HasSubscribers<T>()`.
- Per-handler timing: `EventSubscription.Stats` (calls, total/avg/max ms, faults).
- **Filters (dev.27):** `Events.On<T>(handler, EventFilter.ForEntity(doll))` (or `ForEntities(...)`, or
  `new EventFilter { Entities = ... }`) runs the handler only for events about those entities. The event type says which
  entities it is about by implementing `IEntityEvent.Involves(entity)`; an entity filter on a type that does not throws
  `ArgumentException`. Kiln.Combat's events do (a hit: target, part hit, attacker, source; a death: victim, killer,
  source; ...). A service that holds work for its handlers asks the bus for the union of their filters: kiln.combat
  holds only the hits that name a watched entity, every other hit applies at once (one handler without a filter, also a
  `KilnEvent` catch-all, holds every hit again) ([services/combat.md](services/combat.md)). `EventSubscription.Filter`
  says what a subscription has. `[Subscribe]` methods take no filter: subscribe with `Events.On` for one. Filters by
  template or component are not there: a handler checks those itself. Tests: `Kiln.Api.Tests/EventFilterTests.cs`; lab
  `mods/tests/event-filter-test` (BetterDummies' case).

## The service tick (internal)
`ServiceTick` (Events*.cs) gives Kiln's services one tick without a mod of their own: event pumps, the config and
permission file watchers, the console folder, `Enqueue`'s queue. Kiln.Host does not drive it yet, so it rides on the
Pre tick of the first loaded mod (moving on when that mod unloads); its time shows in that mod's tick statistics.
Requested host change: call `ServiceTick.Run(tick)` at the start of `HostRuntime.Pre` and set
`ServiceTick.HostDriven = true` (see the api worker's report).

## Events Kiln posts (all on the tick thread)

| Event | Assembly | When | Cancel | Handlers may change |
|---|---|---|---|---|
| `ChatMessageEvent` | Kiln.Chat | a player's line (or an injected one), Pre of the step after it arrived, after the `Chat.Received` handlers, before anyone sees it | drop the line: nobody sees it, no command runs | `Text`: the line is dropped and said again by the sender (`Chat.Say`, needs the sender's entity); a rewritten command line runs as the command |
| `CommandExecutingEvent` | Kiln.Chat | a found command the source may use, before parsing | the command does not run; the source gets `CancelMessage` | `CancelMessage` |
| `CommandExecutedEvent` | Kiln.Chat | after a command ran, failed to parse, failed or was canceled | - | - (`Result`, `ElapsedMs`) |
| `PlayerJoinedEvent` / `PlayerLeftEvent` | Kiln.Players | start of the step after kiln.players saw it (same moment as `Players.Joined/Left`); players already in game when you subscribe raise nothing | - | - |
| `TimeSetEvent` | Kiln.Time | right after `Time.Set` (any mod; `By` = mod id); not for in-game changes | - | - |
| `TimeSpeedChangedEvent` | Kiln.Time | right after `Time.SetSpeed` / `SetCycle` changed the held clock speed (`Before`, `After`, `By`); a change made inside its own dispatch waits for the next step | - | - |
| `ImpactVariableChangedEvent` | Kiln.Abilities | right after `ImpactInstance.Write` (or `Set`) changed a live instance's variable; a change made inside its own dispatch waits for the next step and is dropped if the instance has ended by then | - | - |
| `DayStartedEvent` / `NightStartedEvent` | Kiln.Time | the day/night phase flipped (also by a set or sleeping); sampled every 12 steps while someone listens | - | - |
| `SettingChangedEvent` | Kiln.Settings | after `Settings.Set/Reset/ResetAll` changed a value (`By` = mod), or within ~1 s for changes outside Kiln (`By` = "game", polled on the settings revision while someone listens) | - | - |
| `EntitySpawnedEvent` | Kiln.Spawn | right after `Spawn.Entity/Group/Item` created an entity (not the game's own spawns) | - | - |
| `EntityDestroyedEvent` | Kiln.Spawn | after `Spawn.Destroy` (`Reason` "destroy") or `Spawn.CleanupTracked` ("cleanup") | - | - |
| `ItemsGivenEvent` / `ItemsTakenEvent` (base `ItemsTransactionEvent`) | Kiln.Items | when a Kiln.Items give/take completes with something moved (not dry runs), before the caller's task continues | - | - |
| `WaterRefilledEvent` | Kiln.Items | Pre, at most once per step and refill policy (`Items.Fuel.Refill`), with every container it gave water back since the last one | - | - |
| `InventoryTransferEvent` | Kiln.Items | Pre of the step after the game's quick stack or move all ran (a player's R key, a Quick Stack Station, `Items.QuickStack` / `MoveAll`), except dry-run moves, while a handler subscribes: `Kind`, `From`, `To`, `Player`, `ByMod`, `Canceled`, `Moved`, `Kept`. Nothing is hooked until someone subscribes or adds an `Items.Transfers` rule | - (it ran; use `Items.Transfers` rules, [modding/inventory.md](modding/inventory.md)) | - |
| `PlayerProgressionChangedEvent` | Kiln.Players | when a Kiln progression request completed that changed a player's experience or skill points (`What`, `Before`, `After`, `Request`, `Origin`), before the caller's task continues; only what the request changed, and only while the player it was made for is still there | - | - |
| `PlayerKickedEvent` | Kiln.Players | after the login failure of `Players.Kick`, `Network.Kick`, a ban or a login refusal went out (or the connection left on the refusal first) | - | - |
| `PlayerBannedEvent` / `PlayerUnbannedEvent` | Kiln.Players | right after `Bans.Add` / `Bans.Remove` (also `/ban`, `/unban`); not when a ban runs out | - | - |
| `PlayerLoginEvent` | Kiln.Players (posted by Kiln.Network) | once per connection, when it first names its player (Kiln.Network's server tick, about once a second); not for a banned player | refuse the connection: Kiln's REJECT with `Reason` (a Kiln client shows it), then the game's login failure | `Reason` |
| `BuffRemovedEvent` | Kiln.Abilities | when an `Abilities.RemoveBuff` took a buff off (the game's removeBuff said so), before the caller's task continues; not for buffs that run out or that the game's programs remove | - | - |
| `PlayerRulesChangedEvent` | Kiln.Rules | when a player's combined rules change (a rule set applied, updated or ended; an area entered or left; a duration ran out) | - | - |
| `ConfigChangedEvent` | Kiln.Api | a config's values changed while running (file edit or `ConfigFile.Set`), after the new values are in place | - | - |
| `PermissionsChangedEvent` | Kiln.Permissions | permissions.json reloaded/edited or the server's user groups changed | - | - |

### World progress (Kiln.Quests)
Read-only reports of what the game already did; details: [services/world-progress.md](services/world-progress.md#events).

| Event | When | Cancel |
|---|---|---|
| `FlameLevelChangedEvent` | the world's flame level changed (sampled every 30 steps) | - |
| `SpireUnlockedEvent` | an Ancient Spire's unlock was set (world, or a character's twin), or a character reached its top (`TopReached`) | - |
| `ShrineFoundEvent` | a character found a Flame Shrine or Flame Sanctum | - |
| `ShroudRootDestroyedEvent` | a character destroyed a Shroud Root (an Elixir Well's: `Root.ElixirWell`) | - |
| `BossKilledEvent` | a boss (a creature with a boss health bar) died; `Recorded` when its kill record changed | - |
| `ChestLootedEvent` | a loaded chest was emptied after it was opened (sampled every 30 steps) | - |
| `QuestStatusChangedEvent` | a player's quest (vanilla or Kiln) changed status or step | - |

### Player state and actions (Kiln.Players)
One scan per tick, only while a handler subscribes to one of these. Details and what Cancel does: [services/player-state.md](services/player-state.md). Every state and action the game has: [modding/player-states.md](modding/player-states.md).

| Event | When | Cancel |
|---|---|---|
| `PlayerStateChangedEvent` | any state entered or left: `State`, `Entered`, `Name`. Glide, swim, climb, grapple, dodge, sit, sleep, sprint, sneak, Shroud, base and about 340 more, and states of a newer game by raw id | - |
| `PlayerActionEvent` | anything the game runs as an action sequence: attack, cast, dodge, block, jump, glide, item use, interact, emote, hit reaction, death. `Trigger`, `Input`, `Item`, `SequenceName`, `Action` | stops the sequence before its next event |
| `PlayerConsumeEvent` : `PlayerActionEvent` | eating, drinking, a potion, a scroll, before the item goes: `Item`, `Kind`, `Buffs` | item stays, no buff |
| `PlayerConsumedEvent` | the stack really went down | - |
| `PlayerActionEndedEvent` | an action finished or was replaced: `Duration`, `Canceled` | - |
| `PlayerClientActionEvent` | a client sent a versioned command, by the game's field name: `changeEquipmentAction`, `inventoryTransferAction`, `teleportAction` and 43 more. `Name`, `Payload`. Held one step before the game runs it | where `CanCancel`: it never runs |
| `PlayerJumpEvent` | a jump: `Type`, `Count`, `IsDoubleJump` | - |
| `PlayerGlideEvent` | a glide started or ended: `Started`, `Glider`, `Duration` | - |
| `PlayerEquipmentChangedEvent` | a slot's item changed, within 4 steps: `Slot`, `Before`, `Now` | - |
| `PlayerHotbarChangedEvent` | the bar selection or the item in hand changed, within 4 steps | - |

Fall damage, death and respawn stay in Kiln.Combat: `EntityFallEvent`, `EntityDeathEvent`, `PlayerRespawnEvent`.

Services add their own event classes in their assemblies (combat, gameplay, saves, content, loader): see their
docs. Added in docs/kiln/services/combat.md and gameplay.md: `EntityKnockbackEvent`, `EntityFallEvent`,
`EntityHealEvent.Cause` (Hit, Kiln, Regeneration, Attribute) in Kiln.Combat; `EntityJoinLevelEvent`,
`EntityLeaveLevelEvent` (every entity, filtered by `Gameplay.WatchEntities`) and `ItemTossEvent` in Kiln.Gameplay.

### Render thread (Kiln.Ui, client)
One event is posted **outside the tick thread**: `RenderFrameEvent`, once per presented frame on the game's render
thread, while a handler subscribes to that class (a `KilnEvent` catch-all does not get it). Handlers draw through it
and get a time budget per frame; a slow or throwing one is turned off. Details: [render.md](render.md).
