# Action speed: faster or slower attacks, bow draws and tool swings

Since Kiln 0.2.0-dev.31.30 (mod API 0.2.28). Kiln provides the framework for attack and action speed mods:
`Kiln.ActionSpeed` (assembly Kiln.Combat) sets a speed multiplier per player for melee attacks, bow shots and tool
swings. Kiln finds the game's code on both executables, tells the server's run from a client's prediction, sends the
values to joined clients and keeps unknown actions at the game's timing. Your mod only says what it wants. Service
details: [../services/combat.md](../services/combat.md#action-speed).

## A whole mod in ten lines
```csharp
using Kiln;

public sealed class FastHands : Mod
{
    public override void OnLoad()
    {
        if (!ActionSpeed.Available) { Log.Warn($"no action speed here: {ActionSpeed.Status.Reason}"); return; }
        if (Client.IsRemote) return;                    // the host or the server decides; Kiln sends it to the guests
        ActionSpeed.SetDefault(ActionKind.Melee, 2.0);  // every player, also players who join later
        ActionSpeed.SetDefault(ActionKind.Tool, 1.5);
    }
}
```
That is all: no config sync, no hooks, no clean-up. When the mod unloads, its values go with it.

## What you can set
| Call | What |
|---|---|
| `ActionSpeed.SetDefault(kind, m)` / `ClearDefault(kind)` | your mod's value for every player |
| `ActionSpeed.Set(player, kind, m)` / `Clear(player, kind)` / `Clear(player)` | your mod's value for one player; it overrides your default there |
| `ActionSpeed.Get(player, kind)` | the effective value (every mod's value multiplied), on any side |
| `ActionSpeed.Changed` | an effective value changed: `ActionSpeedChanged(Player, Kind, Multiplier)` |
| `ActionSpeed.Available`, `ActionSpeed.Status` | what works in this game, and why a part does not |

- **Kinds:** `ActionKind.Melee` (light and heavy attacks, combos, recovery), `ActionKind.Bow` (bow draw and fire
  durations and the shot; staffs and wands keep their timing), `ActionKind.Tool` (axe, pickaxe and the like).
- **Range:** a value is 0.5 to 10 (`ActionSpeed.Min`, `ActionSpeed.Max`). 1.0 is the game's timing. Anything else
  (and NaN or infinity) throws `ArgumentOutOfRangeException`.
- **Several mods:** each mod keeps its own values; Kiln multiplies them and clamps the product to [0.5, 10]. Two mods
  at 2x and 3x give 6x.
- **Who may set them:** the server or the host, on the tick thread (your `OnLoad`, ticks and handlers). On a joined
  client the setters throw `InvalidOperationException`; read `Get` there instead.
- **Players only:** a creature or NPC entity throws `ArgumentException`. When a player leaves, every mod's values for
  that player go.

## What happens in the game
For a player's melee attack, tool swing or bow shot, Kiln scales the action's clock (when its hits, combos and recovery
happen) and the animation's speed together, so the swing you see and the hit you get stay in step. For a bow, the
draw and fire durations are divided too. A draw already in progress keeps its duration; the next draw uses the new value.

Melee contact sampling (`ActionSpeed.MeleeSamplingAbove`, 2.0) is not in this release: `Status.MeleeSampling` is false.
At high melee rates a hit lands where the game's own frames find it, so a very fast swing can pass through a target
between two server ticks. Kiln's sampler follows in a later release, after an in-game check. Until then your mod may
keep its own contact sampling and read the rate with `ActionSpeed.Get(player, ActionKind.Melee)`.

**Untested above about 3-5x on busy servers.** `Max` is 10, but values above about 3-5x on busy dedicated servers are
untested. In a lab with 4 bots, a mod's 10x melee with its own sampling gave memory errors and frame spikes the 1x run
did not (bots never acknowledge replication, so part of it may be the bots). A lab with Combat And Attack Tweaks'
beta.6 sampler measured 0.94 sword hits per swing at 1x, 0.19-0.64 at 3x and 0.22 at 10x. Axes stayed at 0.94-0.98.

## Multiplayer
Kiln sends the values to every joined client that runs Kiln, by itself. A client applies them to its own player's
swing, so it looks and feels right there. Other players look as the server sends them. A client without Kiln, or on an
older Kiln, sees the server's results at the server's timing. The server stays in charge either way.

## Detecting it
A mod that also loads on an older Kiln asks for `"kiln": ">=0.2.0-dev.31.30"` in its manifest. Without that, Kiln
refuses the mod at load on an older Kiln and names the missing member. In a game where a part does not work (another
game build, another tool that hooks the same places), `ActionSpeed.Available` or the matching `Status` field is false
and `Status.Reason` says why. Capabilities: `combat.action_speed`, `combat.bow_timing`, `combat.melee_sampling`,
`combat.action_speed.prediction`.
