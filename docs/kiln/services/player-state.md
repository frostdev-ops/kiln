# Player state and actions

What a player is doing right now, and an event for anything that changes. Part of Kiln.Players.

| Layer | File |
|---|---|
| Managed | `managed/services/Kiln.Players/PlayerStates.cs`, `PlayerStateEvents.cs`, `PlayerStateIds.g.cs` (generated) |
| Native | `native/services/players/playerstate.cpp`, `kiln_playerstate.h`, service `kiln.playerstate` in `kiln-players.dll` |
| Symbols | `sigdb/symbols/server.d/players.json` (features `players.reflection`, `players.action_cancel`, `players.sequence_names`) |
| Test mod | `mods/tests/playerstate-test` |
| Evidence | `docs/re/player-state.md` |
| Every state | [../modding/player-states.md](../modding/player-states.md) |
| Generator | `tools/typegen/playerstates.py` |

## Reading
```csharp
foreach (var p in Players.All)
{
    if (p.IsGliding) Log.Info($"{p.Name} is gliding");
    if (p.Is(PlayerStateId.Soaked)) Log.Info($"{p.Name} is soaked");
    Log.Info($"{p.Name}: {p.Health}/{p.HealthMax}, holding {p.HeldItem}");
}
```

| Member | What |
|---|---|
| `player.Is(state)` | true or false. False too when it cannot be read |
| `player.Check(state)` | true, false, or null when it cannot be read here |
| `player.IsGliding`, `IsAirborne`, `IsGrounded`, `IsJumping`, `IsFalling`, `IsSwimming`, `IsDiving`, `IsUnderwater`, `IsClimbing`, `IsSprinting`, `IsCrouching`, `IsGrappling`, `IsBlocking`, `IsDodging`, `IsAttacking`, `IsCasting`, `IsUsingItem`, `IsSitting`, `IsSleeping`, `IsDead`, `IsInBase`, `IsInShroud`, `IsInCombat` | shortcuts for `Is` |
| `player.Snapshot()` | one read of everything: `Is`, `Check`, `Active`, `Action`, `RawValue(source)`, `Health`, `HealthMax`, `Stamina`, `Mana`, `BodyHeat`, `SpecialAbility`, `ShroudResistance` and `ShroudTimeLeft` (the timer the game shows), `Oxygen` and their maximums, `Level`, `Comfort`, `JumpCount`, `WaterDepth`, `CanSwim`, `CanDive` |
| `player.Health`, `HealthMax`, `Mana`, `ManaMax`, `Stamina`, `StaminaMax`, `ShroudTimeLeft` | null when unknown. Each is a full read: take a `Snapshot()` for several |
| `player.Equipment` | `[PlayerEquipSlot.Glider]`, `Held`, `HotbarIndex` |
| `player.HeldItem`, `player.Buffs`, `player.CurrentAction` | |
| `PlayerStates.All` | every state the running game has, from its own reflection |
| `PlayerStates.NameOf(state)`, `InfoOf`, `Find(gameName)`, `Raw(source, n)`, `SourceOf` | the table |
| `PlayerStates.Read(entity)`, `Is`, `Check`, `BuffsOf`, `EquipmentOf`, `BuffsGivenBy(item)` | by entity id |
| `PlayerStates.ClientActions` | the commands a client can send, by the game's field names |
| `PlayerStates.Available`, `Status` | what works on this build |

**Threads.** Live on the world's own thread: the tick thread on a server or host, the main thread on a joined client. That is where `OnTick` and event handlers run. From any other thread, `Read`, `Is` and `Check` give the copy from the last tick, and null the first time: an off-thread read starts the scan for a minute. `Equipment`, `Buffs` and item names are for the world's thread.

**Unknown is never a guess.** If a field or enum did not resolve on a build, `Check` is null and `Is` is false. kiln.log says what is missing: `playerstate: source 2: keen::ecs::DynamicLocomotion.state is not in the game's reflection on this build: its states read unknown`.

## Events
```csharp
public sealed class MyMod : Mod
{
    [Subscribe]
    private void OnEat(PlayerConsumeEvent e)
    {
        if (e.Item == GameItems.Food_T1_raw_mushroom_Mushroom_brown) DoX(e.Player);
        else DoY(e.Player);
    }

    [Subscribe]
    private void OnState(PlayerStateChangedEvent e)
    {
        if (e.State == PlayerStateId.Swimming && e.Entered) Log.Info($"{e.Player.Name} is in the water");
    }

    [Subscribe]
    private void OnAction(PlayerActionEvent e)
    {
        if (e.Trigger == PlayerStateId.ActionGlider && NoGlidingHere(e.Player)) e.Cancel();
    }
}
```

| Event | When | Cancel |
|---|---|---|
| `PlayerStateChangedEvent` | any state in the table entered or left: `State`, `Entered`, `Left`, `Name`, `Source`, `Snapshot` | no |
| `PlayerActionEvent` | anything the game runs as an action sequence starts: `Trigger`, `Input`, `Item`, `SequenceName`, `Action` | **yes**: see below |
| `PlayerConsumeEvent` : `PlayerActionEvent` | eating, drinking, a potion, a scroll: `Kind`, `Buffs`, `IsFood`, `IsDrink`. Before the item is used up | **yes**: item stays, no buff |
| `PlayerConsumedEvent` | the stack really went down: `Item`, `Kind`, `Buffs`, `Left` | no |
| `PlayerActionEndedEvent` | an action finished or was replaced: `Duration`, `Canceled` | no |
| `PlayerClientActionEvent` | a client sent a versioned command, before the game's program reads it: `Name`, `Payload`, `Version` | **yes** where `CanCancel`: the game's program skips it |
| `PlayerJumpEvent` | `Type`, `Count`, `IsDoubleJump` | no |
| `PlayerGlideEvent` | `Started`, `Ended`, `Glider`, `Duration` | no: cancel `ActionGlider` |
| `PlayerEquipmentChangedEvent` | `Slot`, `Before`, `Now`, `Equipped`. Within 4 steps | no |
| `PlayerHotbarChangedEvent` | `Before`, `Now`, `HeldBefore`, `Held`. Within 4 steps | no |

All are posted in Pre of the step after the change, on the tick thread. A `PlayerActionEvent` handler also receives `PlayerConsumeEvent`s.

Use what exists for the rest: fall damage `EntityFallEvent`, death `EntityDeathEvent`, respawn `PlayerRespawnEvent` (Kiln.Combat), parry and hit results `EntityHurtEvent`, all actors' sequences `SequenceStartedEvent` (Kiln.Abilities).

| You want | Write |
|---|---|
| Glide, swim, dive, climb, grapple, dodge, sit, sleep, sprint, crouch | `PlayerStateChangedEvent` with `Gliding`, `Swimming`, `Diving`, `Climbing`, `Grappling`, `Dodging`, `Sitting`, `Sleeping`, `Sprinting`, `Sneaking` |
| Shroud, shelter, base, safe zone | `InShroud`, `Sheltered`, `InBase`, `InSafeZone` |
| Wet, cold, exhausted, out of breath | `Wet`, `Soaked`, `Cold`, `Freezing`, `Exhausted`, `Suffocating` |
| Weapon swing, spell, block, parry, dodge | `PlayerActionEvent` with `ActionPrimary`, `ActionHeavy`, `ActionSpecialAbility`, `ActionBlock`, `ActionParry`, `ActionEvade`; or the states `Attacking`, `Casting`, `Blocking` |
| Item use | `PlayerActionEvent`, `e.Item`; or the state `UsingItem` |
| An input | `InputJump`, `InputEvade`, `InputQuickUse0`... (replicated from the client) |

### What Cancel does
A `PlayerClientActionEvent` cancel writes the action's version into `ServerConsumedPlayerInput`, what the game's program does after running it. The program then skips it. The client is not told: what it shows depends on the action.

A `PlayerActionEvent` cancel calls the game's own reset of a running sequence, what it does when a sequence's keep-alive fails. It stops everything the sequence has not done yet. Three limits:
- **Events in its first step have run.** A sequence starts inside a step and Kiln sees it in the next Pre. Of the consumables only `UseItem_Scroll_Meteor` consumes at time 0, so that item is gone either way. Costs at time 0 (the glider's stamina) are paid too.
- **It stops the sequence, not the input.** A held button can start the action again next step: cancel that one too.
- **The player's own client has started the action.** It predicted the animation, and sees the server stop it. What that looks like is a manual test.

Only the server world can cancel: dedicated server and host. `e.CanCancel` is false elsewhere.

### What it costs
One scan per tick while a handler subscribes to any of these events, and none otherwise (the check is a few memory reads). It reads only what those handlers need. No hook, no job-thread work.

| Subscribed to | Components read per player per tick | One step's scan and comparison, 16 players, median over three runs |
|---|---|---|
| nothing | none | no scan at all |
| `PlayerActionEvent`, `PlayerConsumeEvent`, `PlayerGlideEvent` and the like | `Actor`, about 0.2 us | 13 to 15 us |
| also `PlayerJumpEvent` | also `DynamicLocomotion`, `PlayerState`, about 0.3 us | 13 to 20 us |
| also `PlayerStateChangedEvent` or `PlayerClientActionEvent` | also `PlayerInput`, `ActorInput`, about 0.5 us; every 6th tick everything else, about 1 us | 19 to 22 us (p99 about 50 us) |

So about 1.4 us per player per step at most (lab under Wine, 16 spawned characters, no state changing), of a step that takes 16 to 40 ms. Each change adds one event object and its handlers.

## What has been run
With no client, on spawned `1_Player` characters (`mods/tests/playerstate-test`): every read; the state table against the running game; every event; `Cancel` on an emote, on eating, on a glide from input and on an equip action; 5 mushrooms to 4 through the game's own consume code with a real used item, and drink and potion likewise; a glide, a jump and a dodge from input bits through the game's own control programs; fail-safe with the signatures broken on purpose. Item use from a key press, and everything on a real client, is a manual test: nothing here has seen a real player yet.

## Sides
| Where | `Is`, `Snapshot` | Events | Players |
|---|---|---|---|
| Dedicated server | server world, authoritative | all, for every player | `Players.All` |
| Host (single player, co-op host) | server world, authoritative | all, for every player | `Players.All` |
| Joined client | the local player from the prediction world (`Origin == ClientPrediction`); other players from the replicated `NetworkActor` (`Replicated`): actor flags and the running action only | the local player's, on the main thread; `CanCancel` false | `PlayerStates.Read(Client.LocalPlayer)`. Handlers get a `Player` with only `Entity` set |

`PlayerStateSource.ClientUi` and `Input` come from the client's replicated input, on any side: a modified client can send anything there. Everything else on a server is the server's own simulation.

Health and the other vitals are `NetworkPlayerAttributes`, updated once per step, so they are one step old.

## A new game build
- Offsets and names come from the running game, so moved fields keep working.
- A state a new build adds fires `PlayerStateChangedEvent` with a raw id: `(PlayerStateId)0x0140` and `e.Name` the game's name. `PlayerStates.All` lists it with `NamedByKiln == false`.
- A state a new build drops reads as unknown.
- Regenerate the constants: `python3 tools/typegen/playerstates.py --exe <enshrouded.exe> managed/services/Kiln.Players/PlayerStateIds.g.cs`. `--check` lists what changed first.
