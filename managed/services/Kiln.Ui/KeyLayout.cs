// SPDX-License-Identifier: MIT
// Key names in the player's keyboard layout (0.2.0-dev.31.18, netjunk: a German keyboard, Kiln in English, the Keys page
// named his Ü key "LeftBracket" and ShroudForge's shortcut box wanted "SDL:47"). What Kiln stores does not change: a
// binding keeps its virtual key or its US place (keybinds.json, KeyChord.ToString), a ShroudForge shortcut its SDL code.
// Only the name Kiln shows for a letter or punctuation key follows the layout: the character that key makes there
// (MapVirtualKeyExW with the game window's layout, read again at every call, so a switch of layouts shows at once).
// Every other key keeps Kiln's name in Kiln's language ("F5", "Numpad7", "LeftCtrl"), not Windows' key name text, which
// is in the language of the layout. Under Wine and Proton MapVirtualKeyExW answers from the X keymap Wine reads; a
// key it cannot name keeps Kiln's name.
using System.Runtime.InteropServices;

namespace Kiln;

internal static class KeyLayout
{
    /// <summary>MapVirtualKeyExW (code, map type) in the game window's layout on Windows; null elsewhere (the tests set it to
    /// play a layout).</summary>
    internal static Func<uint, uint, uint>? Map = OperatingSystem.IsWindows() ? WindowsMap : null;

    /// <summary>Which layout the names come from now (a page that keeps names made earlier makes them again when it
    /// changes); 0 without a layout. The tests may set it.</summary>
    internal static Func<long> Stamp = OperatingSystem.IsWindows() ? () => SafeLayout() : () => 0;

    internal const uint VkToVscEx = 4, VscToVkEx = 3, VkToChar = 2;

    private static uint WindowsMap(uint code, uint type)
    {
        try { return MapVirtualKeyExW(code, type, Layout()); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return 0; }
    }

    private static long SafeLayout()
    {
        try { return Layout(); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return 0; }
    }

    // the game window's layout (a player may switch layouts per window), else this thread's
    private static nint Layout()
    {
        var w = GetForegroundWindow();
        if (w != 0)
        {
            var thread = GetWindowThreadProcessId(w, out var pid);
            if (pid == (uint)Environment.ProcessId) return GetKeyboardLayout(thread);
        }
        return GetKeyboardLayout(0);
    }

    /// <summary>A letter or punctuation key: the keys a layout moves or relabels.</summary>
    internal static bool Moves(uint vk) => vk is >= 'A' and <= 'Z' || KeyNames.Oem(vk);

    /// <summary>The character virtual key <paramref name="vk"/> makes in the layout, upper case (a dead key: its accent);
    /// null for a key that is not a letter or punctuation key, or makes nothing printable.</summary>
    internal static string? CharOfVk(uint vk)
    {
        if (Map is not { } map || !Moves(vk)) return null;
        var ch = (char)(map(vk, VkToChar) & 0xFFFF);
        if (ch == 0 || char.IsControl(ch) || char.IsWhiteSpace(ch) || char.IsSurrogate(ch)) return null;
        return ch.ToString().ToUpperInvariant();
    }

    /// <summary>The character of the key at set-1 make code <paramref name="scan"/> (no E0 keys: none of them is a letter or
    /// punctuation key), as <see cref="CharOfVk"/>.</summary>
    internal static string? CharOfScan(uint scan)
    {
        if (Map is not { } map || scan is 0 or >= 0x80) return null;
        var vk = map(scan, VscToVkEx);
        return vk == 0 ? null : CharOfVk(vk);
    }

    /// <summary>The name Kiln shows for a binding's key: the layout's character for a punctuation place (the key Kiln calls
    /// "LeftBracket" is "Ü" on a German keyboard) or another punctuation key; every other key by Kiln's name. A letter
    /// keeps its name: a letter binding is that letter in any layout.</summary>
    internal static string Name(uint vk)
    {
        foreach (var (scan, place) in KeyNames.Places)
            if (place == vk)
                return CharOfScan(scan) ?? KeyNames.Name(vk);
        if (KeyNames.Oem(vk) && CharOfVk(vk) is { } c) return c;
        return KeyNames.Name(vk);
    }

    /// <summary><paramref name="chord"/> as Kiln shows it ("Ctrl+Ü"); <see cref="KeyChord.ToString"/> stays the stored form.</summary>
    internal static string Show(KeyChord chord)
    {
        if (chord.IsNone || chord.IsPad) return chord.ToString();
        var s = "";
        if ((chord.Mods & KeyMods.Ctrl) != 0) s += "Ctrl+";
        if ((chord.Mods & KeyMods.Shift) != 0) s += "Shift+";
        if ((chord.Mods & KeyMods.Alt) != 0) s += "Alt+";
        return s + Name(chord.VirtualKey);
    }

    [DllImport("user32", EntryPoint = "MapVirtualKeyExW")]
    private static extern uint MapVirtualKeyExW(uint code, uint mapType, nint layout);

    [DllImport("user32")]
    private static extern nint GetKeyboardLayout(uint thread);

    [DllImport("user32")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
}
