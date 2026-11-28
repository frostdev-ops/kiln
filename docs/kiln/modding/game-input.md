# Game input: hold-to-repeat and other action tricks (GameInput)

`GameInput` (Kiln.Client, since Kiln 0.2.0-dev.31.30, mod API 0.2.28) reads the local player's game actions where the
game client has already resolved its own input, and lets a mod press or release an action for one input sample. The
player's own key bindings and controller work unchanged, the game's menus and chat are respected, and Kiln's own UI and
an unfocused window stop it. Reference and rules: [../services/client-game-input.md](../services/client-game-input.md).

> **Not checked in a real game yet.** It is a supported API on game client 26430625, covered by host tests, C# tests and
> a read-only test of the game's code; a real game has not confirmed it. Tell us what you see.

## Hold to repeat light attacks, in about ten lines

```csharp
using Kiln;

public sealed class HoldToRepeat : Mod
{
    private IDisposable? _repeat;
    private uint _world;

    public override void OnLoad() => Client.Tick += (in TickInfo t) =>
    {
        if (!GameInput.Available || Client.ClientWorldSession == _world) return;
        _world = Client.ClientWorldSession;   // a Repeat ends with its world: one per world
        _repeat = GameInput.Repeat(GameAction.PrimaryAttack, TimeSpan.FromMilliseconds(100));
    };
}
```

While the player holds the light attack with any input it is bound to, the game sees a short press every 100 ms instead
of one long hold: light attacks instead of the heavy attack. Letting go stops it. A `Repeat` belongs to the world of its
first sample and ends when that world does (and when the mod unloads, or when you dispose it), which is why the example
starts one at the first client tick of each world.

Only for melee weapons, with your mod's own switch:

```csharp
_repeat = GameInput.Repeat(GameAction.PrimaryAttack, TimeSpan.FromMilliseconds(intervalMs),
    whileHeld: () => config.HoldToAttack && GameInput.IsHeld(GameAction.PrimaryAttack) && _heldMelee);

_watch = GameInput.OnSample((ref GameInputSample s) => _heldMelee = s.HeldKind == ActionKind.Melee);
```

## A bow that releases itself when fully drawn

```csharp
_bow = GameInput.OnSample((ref GameInputSample s) =>
{
    if (s.HeldKind == ActionKind.Bow && s.BowFullyDrawn && s.IsHeld(GameAction.PrimaryAttack))
        s.Release(GameAction.PrimaryAttack);   // the shot leaves; still holding draws the next one
});
```

`BowFullyDrawn` is the game's own test: its draw has lasted the charge duration it took when the draw started, which
already includes the player's skills and any `ActionSpeed` multiplier (Kiln.Combat).

## Reading only

```csharp
if (GameInput.WasPressed(GameAction.Dodge)) Log.Info("dodge went down at the last sample");
bool aiming = GameInput.IsHeld(GameAction.SecondaryAttack);
```

Reads work from any thread and never throw: false where game input is unavailable, and false when the last sample is
older than 250 ms (a loading screen, the main menu).

## Good to know

- Handlers and `whileHeld` run on the game's main thread about 60 times a second, inside the game's input step. Keep
  them short: no waiting, no file or network work.
- `Press` and `Release` work only inside the handler that got the sample. Handlers run in the order they subscribed;
  the later `Press` or `Release` of the same action wins.
- Nothing runs while a game menu, the inventory, the map, the chat, Kiln's menu or a Kiln window has the input, or while
  the game window is not in front. Reading still works there (the game's answer is "not held").
- Multiplayer: the presses are the local player's own input and go to the host or server like the player's own; the
  server still decides. Each player's setting is their own; nothing needs syncing.
- Test it: `GameInput` is client-only and has no Kiln.TestKit fake ([unit-tests.md](unit-tests.md)); keep the logic in
  a plain method you can test without it.
