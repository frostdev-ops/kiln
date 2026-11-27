// SPDX-License-Identifier: MIT
using System.Numerics;

namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private HarnessInput? _input;

    /// <summary>The fake kiln.ui.input of this harness: the player's presses and releases for listeners that own them
    /// (KeyInput.Listen with KeyListenFlags.Own), what reached the game, and the controllers PadInput reads. Since
    /// 0.2.0-dev.31.13, mod API 0.2.22.</summary>
    public HarnessInput Input => _input ??= new HarnessInput(this);
}

/// <summary>
/// The player's input for owning key listeners (<see cref="KilnHarness.Input"/>): KeyInput's and PadInput's own code runs on
/// the kit's kiln.ui.input, whose ownership rules are the native ones. Since 0.2.0-dev.31.13 (mod API 0.2.22).
/// </summary>
/// <remarks>
/// <para><b>Modelled.</b> <see cref="Down(uint, int, KeyMods, Vector2?)"/> is a new press: the listeners with <c>KeyListenFlags.Own</c> whose flags take
/// its kind (a key; <c>Mouse</c> the middle and side buttons 0x04..0x06; <c>Pointer</c> the left and right buttons 0x01,
/// 0x02; <c>Pad</c> a controller's 0x200..0x211) are offered it in the order they were made, and the first that keeps it owns
/// it. <see cref="Repeat"/> (a key's auto-repeat) and <see cref="Up"/> of an owned press reach its owner only and stay kept
/// from the game, unless <see cref="Leak"/> said the game saw the press go down; a new press of an owned code ends that
/// ownership (Lost). <see cref="FocusLost"/> ends the ownership of keys and mouse buttons (Focus), not a controller's; <see cref="WorldChange"/> ends the ones made
/// before it (World) and their releases reach nobody, as after the listener's Disown or Dispose. <see cref="Game"/> is what
/// reached the game, <see cref="Others"/> what Kiln's other key handlers (listeners without Own, key bindings, the pointer's
/// subscribers) were given; they take nothing here. <see cref="SetPad"/> and <see cref="Unplug"/> are what the game's last
/// poll of a controller read, for <c>PadInput.Read</c>.</para>
/// <para><b>Not modelled.</b> The game window, raw input records, the MK_ bits of mouse messages (<see cref="HiddenFromGame"/>
/// says whether a button is hidden as held), XInput itself, Kiln's menu key and Escape (which come before the owners in the
/// game), the answers of listeners without Own and of bindings, the cursor (<c>at</c> is the position the press reports),
/// and the threads: every call runs the listeners on the caller's thread.</para>
/// </remarks>
public sealed class HarnessInput
{
    private readonly KilnHarness _harness;

    internal HarnessInput(KilnHarness harness) => _harness = harness;

    private UiInputFakeState State => KitFakes.State<UiInputFakeState>(_harness);

    /// <summary>A new press of <paramref name="code"/> (a key's virtual key, 0x01/0x02 the left/right button, 0x04..0x06 the
    /// middle and side buttons, a "Pad:" chord's code on <paramref name="controller"/>) with <paramref name="mods"/> held;
    /// <paramref name="at"/>: the cursor for the left and right buttons. True when an owning listener took it: nothing else
    /// got it, nor the game.</summary>
    public bool Down(uint code, int controller = 0, KeyMods mods = KeyMods.None, Vector2? at = null)
    {
        Check(controller);
        return UiInputFake.Press(_harness, code, controller, mods, at);
    }

    /// <summary>A new press of <paramref name="chord"/>'s key with its modifiers (a "Pad:" chord on <paramref name="controller"/>).</summary>
    public bool Down(KeyChord chord, int controller = 0) => Down(chord.VirtualKey, controller, chord.Mods);

    /// <summary>The auto-repeat of a held key. True when it was kept from the game (an owned press's).</summary>
    public bool Repeat(uint code, KeyMods mods = KeyMods.None) => UiInputFake.Later(_harness, code, 0, true, mods, null);

    /// <summary>The release of <paramref name="code"/> (on <paramref name="controller"/>). True when it was kept from the game
    /// (the release of a press the game never saw: an owner's, or nobody's after a world change, a disown or an unload).</summary>
    public bool Up(uint code, int controller = 0, Vector2? at = null)
    {
        Check(controller);
        return UiInputFake.Later(_harness, code, controller, false, KeyMods.None, at);
    }

    /// <summary>The game window lost the focus: every owner of a key or mouse button hears the end of its presses
    /// (KeyOwnEnd.Focus); their releases, when they come back, reach the game. A controller's owned presses stay owned
    /// (XInput does not follow the focus): their releases are their owners'.</summary>
    public void FocusLost() => UiInputFake.FocusLost(_harness);

    /// <summary>The client world started or stopped: the owners of presses made before hear KeyOwnEnd.World; those presses'
    /// releases reach nobody, still not the game.</summary>
    public void WorldChange() => UiInputFake.WorldChange(_harness);

    /// <summary>The game saw the owned press of <paramref name="code"/> go down after all (in the game: Alt+F4, or a mouse
    /// record Kiln could not edit): its release reaches the game too (logged in <see cref="Game"/> as the down now).</summary>
    public void Leak(uint code, int controller = 0)
    {
        Check(controller);
        UiInputFake.Leak(_harness, code, controller);
    }

    /// <summary>The game is not told <paramref name="code"/> is held although it is: an owned (or nobody's) press it never
    /// saw go down. In the game a mouse button's MK_ bit leaves its mouse messages then.</summary>
    public bool HiddenFromGame(uint code, int controller = 0)
    {
        Check(controller);
        return UiInputFake.Hidden(_harness, code, controller);
    }

    /// <summary>What reached the game, in order: "down 2", "repeat 41", "up 20d@1" (hex codes, "@" a controller other than 0).</summary>
    public IReadOnlyList<string> Game
    {
        get { lock (State.Lock) return [.. State.Game]; }
    }

    /// <summary>What Kiln's other key handlers were given (listeners without KeyListenFlags.Own, key bindings, pointer
    /// subscribers), in <see cref="Game"/>'s format.</summary>
    public IReadOnlyList<string> Others
    {
        get { lock (State.Lock) return [.. State.Others]; }
    }

    /// <summary>Empties <see cref="Game"/> and <see cref="Others"/>.</summary>
    public void ClearLogs()
    {
        lock (State.Lock)
        {
            State.Game.Clear();
            State.Others.Clear();
        }
    }

    /// <summary>The game's next poll of <paramref name="controller"/> reads these: <paramref name="held"/> buttons, the sticks
    /// (-1..1, after the dead zone) and the triggers (0..1), as PadInput.Read returns them.</summary>
    public void SetPad(int controller, PadButtons held, Vector2 leftStick = default, Vector2 rightStick = default, float leftTrigger = 0,
        float rightTrigger = 0)
    {
        Check(controller);
        lock (State.Lock)
        {
            ref var p = ref State.Pads[controller];
            var polls = p.Polls;
            var packet = p.Packet;
            p = default;
            p.Connected = 1;
            p.Buttons = (uint)held;
            p.LeftX = leftStick.X;
            p.LeftY = leftStick.Y;
            p.RightX = rightStick.X;
            p.RightY = rightStick.Y;
            p.LeftTrigger = leftTrigger;
            p.RightTrigger = rightTrigger;
            p.RawLeftX = Raw(leftStick.X);
            p.RawLeftY = Raw(leftStick.Y);
            p.RawRightX = Raw(rightStick.X);
            p.RawRightY = Raw(rightStick.Y);
            p.RawLeftTrigger = (byte)Math.Clamp(MathF.Round(leftTrigger * 255), 0, 255);
            p.RawRightTrigger = (byte)Math.Clamp(MathF.Round(rightTrigger * 255), 0, 255);
            p.Packet = packet + 1;
            p.Polls = polls + 1;
            p.PollMs = (ulong)Environment.TickCount64;
        }
    }

    /// <summary>The game's next poll of <paramref name="controller"/> fails (unplugged): PadInput.Read says not connected.</summary>
    public void Unplug(int controller)
    {
        Check(controller);
        lock (State.Lock)
        {
            ref var p = ref State.Pads[controller];
            var polls = p.Polls;
            p = default;
            p.Polls = polls + 1;
            p.PollMs = (ulong)Environment.TickCount64;
        }
    }

    private static short Raw(float v) => (short)Math.Clamp(MathF.Round(v * 32767), -32768, 32767);

    private static void Check(int controller)
    {
        if (controller is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(controller), controller, "a controller is 0 to 3");
    }
}
