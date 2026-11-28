# Combat And Attack Tweaks on the new API: Hold To Light Attack (sites 19 and 20, user32)

For the Combat And Attack Tweaks author: which of the beta.6 native sites `Kiln.GameInput` replaces (Kiln 0.2.0-dev.31.30,
mod API 0.2.28), and what the mod's code becomes. Sites 01 to 18 (speed, bow timing, melee sampling) are
`Kiln.ActionSpeed`'s (Kiln.Combat); this note covers the "HOLD TO LIGHT ATTACK INPUT" part and the keyboard polling.
Reference: [../services/client-game-input.md](../services/client-game-input.md); the game side, with your sites
re-checked on client 26430625: [../../re/game-input.md](../../re/game-input.md).

## Site by site

| Beta.6 | What it did | On the new API |
|---|---|---|
| 19 `InputWriterCode` (`0x3af639`, hook `0x3af649`, `RewriteAttack`) | rewrote the 64-bit digital input at `ClientPlayerInput +0x3a8` in the local input writer: melee press/release samples while left mouse is held, the bow's shot released once its draw timer expired; device, menu, focus and session checks | `GameInput.Repeat(GameAction.PrimaryAttack, interval, whileHeld)` for the melee repeat, `GameInput.OnSample` + `GameInputSample.Release(GameAction.PrimaryAttack)` when `BowFullyDrawn` for the bow. Kiln writes at its own link in the same writer (`0x3af55f`, after the digital word and the camera; nothing writes the word between it and your `0x3af649`) and owns the menu, Kiln-UI and focus gates |
| 20 `InputMessageCode` (`0x3a89f9`, hook `0x3a8a03`, `ClearStaleAttack`) | cleared the last synthetic attack bits in the outbound input message when the writer was skipped (same record, actor, session and bits, step changed) | nothing to write: Kiln's guard is always on, at the same instruction, with the same rule (the whole digital word must still be the one Kiln wrote), for every bit any mod pressed |
| user32 polling (`GetAsyncKeyState` and the like, "left mouse held") | read the mouse directly | `GameInput.IsHeld(GameAction.PrimaryAttack)` or `GameInputSample.IsHeld(...)`: the game's own resolved action, so rebinding and controllers work and a key Kiln's UI keeps never counts |
| equipment check (`RDI` = the held `ItemInfo`) | told melee from bow | `GameInputSample.HeldKind` (`ActionKind.Melee`, `Bow`, `Tool` or null), from Kiln's item catalog and the game's ranged weapon state |
| `BowRepeat.cs`: `GetComponent` of `NetworkRangedWeapon` (`0x959918C4`), phase +0x00, type +0x08, charge +0x10, start +0x28, weapon +0x38, world period +0xE17E80 / step +0xE17E9C | the native draw deadline | `GameInputSample.BowFullyDrawn`: the same fields and the game's own test (AimAndCharge and elapsed >= chargeDuration), the client world's time as the game's getter computes it |

What stays yours: the switches (melee on/off, bows on/off), the 60-250 ms interval and its INI override, and the config.
Kiln ships no gameplay change on its own.

## The mod's code

```csharp
using Kiln;

// Melee: hold the light attack to repeat it (one Repeat per world: it ends with its world).
_meleeRepeat = GameInput.Repeat(GameAction.PrimaryAttack, TimeSpan.FromMilliseconds(cfg.RepeatIntervalMs),
    () => cfg.HoldMelee && _heldKind == ActionKind.Melee && GameInput.IsHeld(GameAction.PrimaryAttack));

// What is in hand, and the bow: let go of a fully drawn shot while the player keeps holding.
_sample = GameInput.OnSample((ref GameInputSample s) =>
{
    _heldKind = s.HeldKind;
    if (cfg.HoldBows && s.HeldKind == ActionKind.Bow && s.BowFullyDrawn && s.IsHeld(GameAction.PrimaryAttack))
        s.Release(GameAction.PrimaryAttack);
});
```

- Detect it with `GameInput.Available` (false on a dedicated server, on an older Kiln the mod is refused at load with
  the missing member named when built against MDK 0.2.0-dev.31.30).
- Remove `HoldAttackHooks.cs`'s two mid hooks, its user32 imports and its `BowRepeat.cs` component reads; the Hold To
  Light Attack switches stay local (not `[Synced]`), as today.
- `Repeat`'s interval is raised to at least 50 ms (your floor of 60 ms is above it).
- The press Kiln writes is your beta.6 pattern: `MainhandAction`, `RangedCombatShoot`, `RangedCombatShoot_Tap` down, and
  the whole primary family (`_Tap`, `_Hold`, `_Release`, bits 1-3) cleared, so a held button never becomes the heavy
  attack while the repeat runs.

## Differences to know

- **Where it writes:** `0x3af55f` instead of `0x3af649`. Between them the writer stores only `mask` (+0x3a0), from the
  HUD screen and the held item; the digital word the message and the simulation see is the same.
- **Gates:** handlers run only with the in-game HUD up (any other game screen, the chat included, closes them), without
  Kiln's menu or a Kiln window capturing input, and with the game window in front; read one client step before the
  sample. Your device and session checks are not needed.
- **Order:** with several mods, handlers run in the order they subscribed and the later `Press`/`Release` wins.
- **Not checked in a real game yet** (like your beta.6 input path); Kiln's evidence is static, host-tested and C#-tested.
