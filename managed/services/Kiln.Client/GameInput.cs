// SPDX-License-Identifier: MIT
// PREVIEW SDK SURFACE (dev31/sdk-preview): the public API of Kiln.GameInput as settled in the 0.2.0-dev.31.30 spec, for mods
// to compile against before the release. The bodies are placeholders: in the preview pack nothing is available. The
// implementation ships in Kiln 0.2.0-dev.31.30 (mod API 0.2.28).
using System;

namespace Kiln;

/// <summary>The game's own digital actions, after its rebinding and controller mapping, decoded from the local player's
/// input record. Append only.</summary>
public enum GameAction
{
    /// <summary>Light attack / fire (left mouse by default).</summary>
    PrimaryAttack,
    /// <summary>Heavy attack / aim (right mouse by default, as the game maps it).</summary>
    SecondaryAttack,
    /// <summary>Block.</summary>
    Block,
    /// <summary>Dodge.</summary>
    Dodge,
    /// <summary>Jump.</summary>
    Jump,
    /// <summary>Interact.</summary>
    Interact,
}

/// <summary>Called for each input sample of the local player (<see cref="GameInput.OnSample"/>).</summary>
public delegate void GameInputHandler(ref GameInputSample sample);

/// <summary>One input sample of the local player, in the game's input writer, after the game resolved it.</summary>
public ref struct GameInputSample
{
    /// <summary>The local player.</summary>
    public EntityId Player { get; }

    /// <summary>The <see cref="ActionKind"/> of the held item (Melee, Bow, Tool), or null.</summary>
    public ActionKind? HeldKind { get; }

    /// <summary>A bow draw has reached its effective charge duration (ActionSpeed included).</summary>
    public bool BowFullyDrawn { get; }

    /// <summary>The action as the game sampled it (before handlers).</summary>
    public bool IsHeld(GameAction action) => false;

    /// <summary>Sets <paramref name="action"/> for this sample.</summary>
    public void Press(GameAction action) { }

    /// <summary>Clears <paramref name="action"/> for this sample.</summary>
    public void Release(GameAction action) { }
}

/// <summary>Reads and changes the local player's game actions where the game has resolved its own input (rebinding,
/// controllers, menus and focus included). Client only. Handlers run only on the local gameplay path: never while the
/// game's menus, inventory, map or chat or Kiln's UI have input, and never unfocused. Kiln always clears synthetic presses
/// that the outbound input message would resend after a skipped writer step.</summary>
public static class GameInput
{
    /// <summary>A client with a local player, and the game's input sites resolved here (capability <c>client.game_input</c>).</summary>
    public static bool Available => false;

    /// <summary>Whether <paramref name="action"/> was held at the last sample.</summary>
    public static bool IsHeld(GameAction action) => false;

    /// <summary>Whether <paramref name="action"/> went down at the last sample.</summary>
    public static bool WasPressed(GameAction action) => false;

    /// <summary>Calls <paramref name="handler"/> for each local input sample until the returned handle is disposed.</summary>
    /// <exception cref="InvalidOperationException">On a dedicated server, or where GameInput is unavailable.</exception>
    public static IDisposable OnSample(GameInputHandler handler) => throw Unavailable();

    /// <summary>Presses and releases <paramref name="action"/> every <paramref name="interval"/> (at least 50 ms) while
    /// <paramref name="whileHeld"/> is true (default: while the action itself is held). Stops when the handle is disposed,
    /// the world ends or the mod unloads.</summary>
    /// <exception cref="InvalidOperationException">On a dedicated server, or where GameInput is unavailable.</exception>
    public static IDisposable Repeat(GameAction action, TimeSpan interval, Func<bool>? whileHeld = null) => throw Unavailable();

    private static InvalidOperationException Unavailable() =>
        new("preview SDK: GameInput ships in Kiln 0.2.0-dev.31.30 (mod API 0.2.28)");
}
