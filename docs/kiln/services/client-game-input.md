# kiln.client.game_input / `GameInput`: the local player's game actions

Read and change the local player's game actions at the point where the game client has already resolved its own input:
after its key bindings, controller mapping and menu handling, before the simulation and the input message use it. So a
mod gets rebinding, controllers and the game's own menu rules for free, and nothing polls the keyboard (no
`GetAsyncKeyState`). A mod can press or release an action for one input sample, or let Kiln repeat a press at an
interval: hold-to-repeat attacks, a bow that releases itself when fully drawn, in a few lines.

**Since Kiln 0.2.0-dev.31.30 (mod API 0.2.28).** Asked for by the Combat And Attack Tweaks author (its "Hold to Light
Attack" sites 19 and 20 and its user32 polling become Kiln's; [porting note](../modding/catt-porting-input.md)). Bound on
game client 26430625 (sigdb features `client.input_observation` and `client.game_input_sites`). Client only. **Not checked in a
real game yet:** a read-only sigdb test on the exe, host tests of the rules (ASan, UBSan, TSan), the capability row's host
tests and C# tests over a stand-in table cover it. Tell us what you see.

Kiln provides the mechanism; which actions to press, when and how often is the mod's choice. Players see nothing new
without a mod that uses it.

| Layer | Where |
|---|---|
| C table | `native/core/abi/services/kiln_client_game_input.h` (`kiln.client.game_input` v1, in-core) |
| Native | `native/core/src/services/client_game_input.cpp` (rules), `client_game_input_service.cpp` (hooks, gates, table), `client_input.cpp` / `client.cpp` (the shared observation link) |
| Managed | `managed/services/Kiln.Client/GameInput.cs`: `GameInput`, `GameAction`, `GameInputSample`, `GameInputHandler` |
| sigdb | `sigdb/symbols/client.d/client-input.json` (`client.input_observation`), `client-game-input.json` (`client.game_input_sites`) |
| Game side | [docs/re/game-input.md](../../re/game-input.md) |
| Tests | `native/core/tests/test-client-game-input.sh` (asan, tsan), `test-caps.sh`; `tools/sigport/tests/test_client_game_input.py`; `managed/tests/Kiln.Api.Tests/GameInputTests.cs` |

## C#

```csharp
using Kiln;

if (!GameInput.Available) { Log.Info(Capabilities.Why("client.game_input")); return; }

// Hold left mouse with a melee weapon: light attacks every 120 ms instead of the heavy attack.
var repeat = GameInput.Repeat(GameAction.PrimaryAttack, TimeSpan.FromMilliseconds(120),
    () => GameInput.IsHeld(GameAction.PrimaryAttack) && holdToRepeat);

// A bow that lets go by itself once fully drawn (the player keeps holding to draw the next one).
var autoRelease = GameInput.OnSample((ref GameInputSample s) =>
{
    if (s.HeldKind == ActionKind.Bow && s.BowFullyDrawn && s.IsHeld(GameAction.PrimaryAttack))
        s.Release(GameAction.PrimaryAttack);
});
```

| Member | Does |
|---|---|
| `GameInput.Available` | a game client with a local player, the game's input sites resolved here (row `client.game_input`) |
| `GameInput.IsHeld(action)` | held at the last sample, as the game sampled it; false when there is no fresh sample (250 ms) of the live client world. Any thread, never throws |
| `GameInput.WasPressed(action)` | held at the last sample and not at the one before it in the same world. Any thread, never throws |
| `GameInput.OnSample(handler)` | calls `handler(ref sample)` at each sample while the gates are open, on the game's main thread, until the returned handle is disposed or the mod unloads; five throws in a row remove it |
| `GameInput.Repeat(action, interval, whileHeld = null)` | presses `action` for one sample, releases it until `interval` (at least 50 ms) after that press, presses again, while `whileHeld` is true (default: the action itself is held) and the gates are open; ends when disposed, when the world ends (it belongs to the world of its first sample) or when the mod unloads |
| `GameInputSample.Player` | the local player |
| `GameInputSample.HeldKind` | `ActionKind?` of the held item: Melee, Bow, Tool, or null (`ActionKind` is in Kiln.Api: no Kiln.Combat reference needed) |
| `GameInputSample.BowFullyDrawn` | a bow draw has reached its effective charge duration (`ActionSpeed` included) |
| `GameInputSample.IsHeld(action)` | the action as the game sampled it, before any handler |
| `GameInputSample.Press(action)` / `Release(action)` | down / up for this sample; the later call wins, also across handlers (they run in the order they subscribed); only inside the sample's own handler call |

`OnSample` and `Repeat` throw `InvalidOperationException` on a dedicated server, where the row is off, outside mod code,
when the mod is unloading, or with 64 subscriptions (both kinds together) already; `Repeat` throws
`ArgumentOutOfRangeException` for a value that is not a `GameAction`.

## The actions

Each action is a set of the game's own `PlayerInputType` bits. "Down" bits mean its input is held; the "family" adds the
bits the game derives from that input. `IsHeld`: any down bit. `Press`: clear the family, set the down bits. `Release`:
clear the family.

| `GameAction` | Down | Family adds | Default input (as the game maps it) |
|---|---|---|---|
| `PrimaryAttack` | `MainhandAction`, `RangedCombatShoot`, `RangedCombatShoot_Tap` | `MainhandAction_Tap`, `_Hold`, `_Release` | left mouse: light attack, tool swing, bow shot |
| `SecondaryAttack` | `RangedCombatArm` | - | right mouse: aim with a bow or staff |
| `Block` | `Block` | - | block |
| `Dodge` | `Evade` | - | dodge |
| `Jump` | `Jump` | - | jump |
| `Interact` | `ContextualAction` | `ContextualAction_Tap`, `_Hold` | interact (E) |

A press written for one sample has no `_Hold`: the game's heavy attack comes from holding, so one-sample presses are
light attacks. The list may grow (append only).

## When handlers run: the gates

Reading is never gated: the game's sample is already menu-aware. Handlers and repeats run, and Kiln writes, only on the
local gameplay path, from Kiln's existing input state read at each client world step:

| Gate | Closed when |
|---|---|
| world | no world in play (main menu, loading) |
| game screens | any game screen other than the in-game HUD: the pause menu, settings, the tabbed menu, the map, inventory, chests, crafting, dialogs, the text chat (kiln.ui's `hud_screen` is not 1) |
| Kiln's UI | Kiln's menu shows, or a Kiln window captures input (kiln.ui's `menu_open`, `capturing`) |
| focus | the game window is not the foreground window |

Without kiln.ui's screen state (kiln-ui.dll missing or its feature `ui.screens` off on the build) handlers never run
(kiln.log says so once). The gates are read one step before the sample.

## Stale presses

When the game skips its input writer for a step (blocked input), its input message still sends the record as last
written. Kiln clears the bits it pressed itself before that message copies the record, when the record, the local
player, the client session and the whole digital word are still as Kiln left them and the message is for a later step.
Always on; a mod never handles it.

## Multiplayer

The sample is the local player's own input, sent to the host or the dedicated server exactly as the game sends it; the
host or server still decides what happens (attack speeds included: [combat.md](combat.md)). A guest and a host use it
the same way. Nothing is synced: a mod's hold-to-repeat setting is each player's own.

## Threads and hooks

- `IsHeld`, `WasPressed`, `Available`, `OnSample`, `Repeat` and disposing a handle: any thread. Disposing waits for a
  handler call in flight on another thread; from inside the handler it returns at once.
- Handlers and `whileHeld` run on the game's main thread inside its input writer, about 60 times a second: decide at
  once, no waiting, no blocking calls.
- Nothing is hooked until the first use of `GameInput` in the process. Then two mid links go in: kiln.client's
  observation of the local input writer (shared with building input) and the guard in the input message. A failed or
  refused install is in the row's text.

## C (native plugins)

`kiln.client.game_input` v1 (`kiln_client_game_input.h`): `last(sample, reason)`, `subscribe(owner, fn, user, out)`,
`repeat(owner, action, interval_ms, cond, user, out)`, `unsubscribe(handle)`. A subscriber edits `sample->press` and
`sample->release` (a bit left in both is released); a repeat's `cond` NULL means "while the action is held"; a native
repeat starts again in a new world instead of ending (Kiln.Client ends its own). The sample also has the game's raw
`digital` word, the step, the gate and the held item id.

## Capability rows

| Row | When available |
|---|---|
| `client.input_observation` | sigdb feature: the local input writer's observation point and the `ClientPlayerInput` layout on this build |
| `client.game_input_sites` | sigdb feature: the held item at that point, the input message's copy, the world clock, the action bits and the `NetworkRangedWeapon` fields |
| `client.game_input` | a game client with kiln.client.game_input and both features: "ready ... hooked at the first use", then "hooked". Unavailable: `wrong-side`, `policy` (safe idle), `service-missing`, `symbol-missing` (the reason names what is missing), `faulted` (the first use's install failed) |
