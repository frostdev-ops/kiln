// SPDX-License-Identifier: MIT
// ShroudForge's shortcut strings for people (0.2.0-dev.31.18, netjunk): a ShroudForge mod's keybind setting holds
// "SDL:<scancode>" for a key and "Mouse:<button>" for a mouse button, with Ctrl, Alt, Shift and Meta in that order in
// front, and at most one keyboard and one mouse chord joined by "|" (the fork's modules/hotkeys/parse_hotkey.rs). The
// scancode is SDL's for the key's place on the keyboard, the same on every layout: the Ü key of a German keyboard is
// SDL:47, where a US keyboard has [.
//
// What ShroudForge sees: in a hosted start Kiln hands the game window's own raw records to ShroudForge's runtime
// (kiln-shroudforge key_feed.cpp), which turns a record's make code and E0 flag into a scancode through SDL's
// windows_scancode_table (the fork's host_keyboard.c); Kiln's interpreter does the same with its port of that table
// (kiln-sfi input/feed.rs SET1). kiln.ui hands Kiln.Ui the same make code and E0 flag (KilnUiKey.scan: the make code,
// 0x100 for E0), so a key pressed in the capture box becomes exactly the scancode ShroudForge later sees for that key.
using System.Globalization;
using System.Text;

namespace Kiln;

internal static class SfShortcuts
{
    /// <summary>SDL3's windows_scancode_table (src/events/scancodes_windows.h, as the ShroudForge fork builds it): the index
    /// is a set-1 make code, with 0x80 for an E0-prefixed one; the indexes it leaves unknown are not here. Kiln.Ui.Tests
    /// holds it against kiln-sfi's port (input/feed.rs SET1), which leaves out the consumer keys (SDL codes from 258).</summary>
    internal static readonly (byte Index, ushort Sdl)[] Set1 =
    [
        (0x01, 41), (0x02, 30), (0x03, 31), (0x04, 32), (0x05, 33), (0x06, 34), (0x07, 35), (0x08, 36), (0x09, 37), (0x0A, 38),
        (0x0B, 39), (0x0C, 45), (0x0D, 46), (0x0E, 42), (0x0F, 43), (0x10, 20), (0x11, 26), (0x12, 8), (0x13, 21), (0x14, 23),
        (0x15, 28), (0x16, 24), (0x17, 12), (0x18, 18), (0x19, 19), (0x1A, 47), (0x1B, 48), (0x1C, 40), (0x1D, 224), (0x1E, 4),
        (0x1F, 22), (0x20, 7), (0x21, 9), (0x22, 10), (0x23, 11), (0x24, 13), (0x25, 14), (0x26, 15), (0x27, 51), (0x28, 52),
        (0x29, 53), (0x2A, 225), (0x2B, 49), (0x2C, 29), (0x2D, 27), (0x2E, 6), (0x2F, 25), (0x30, 5), (0x31, 17), (0x32, 16),
        (0x33, 54), (0x34, 55), (0x35, 56), (0x36, 229), (0x37, 85), (0x38, 226), (0x39, 44), (0x3A, 57), (0x3B, 58), (0x3C, 59),
        (0x3D, 60), (0x3E, 61), (0x3F, 62), (0x40, 63), (0x41, 64), (0x42, 65), (0x43, 66), (0x44, 67), (0x45, 83), (0x46, 71),
        (0x47, 95), (0x48, 96), (0x49, 97), (0x4A, 86), (0x4B, 92), (0x4C, 93), (0x4D, 94), (0x4E, 87), (0x4F, 89), (0x50, 90),
        (0x51, 91), (0x52, 98), (0x53, 99), (0x56, 100), (0x57, 68), (0x58, 69), (0x59, 103), (0x5C, 140), (0x64, 104), (0x65, 105),
        (0x66, 106), (0x67, 107), (0x68, 108), (0x69, 109), (0x6A, 110), (0x6B, 111), (0x6C, 112), (0x6D, 113), (0x6E, 114), (0x70, 136),
        (0x71, 145), (0x72, 144), (0x73, 135), (0x76, 115), (0x77, 147), (0x78, 146), (0x79, 138), (0x7B, 139), (0x7D, 137), (0x7E, 133),
        (0x87, 121), (0x88, 122), (0x8A, 125), (0x90, 268), (0x97, 123), (0x98, 124), (0x99, 267), (0x9C, 88), (0x9D, 228), (0xA0, 127),
        (0xA2, 271), (0xA4, 269), (0xAC, 270), (0xAE, 129), (0xB0, 128), (0xB2, 281), (0xB5, 84), (0xB6, 229), (0xB7, 70), (0xB8, 230),
        (0xBB, 117), (0xBE, 273), (0xBF, 274), (0xC0, 275), (0xC5, 83), (0xC6, 72), (0xC7, 74), (0xC8, 82), (0xC9, 75), (0xCB, 80),
        (0xCD, 79), (0xCF, 77), (0xD0, 81), (0xD1, 78), (0xD2, 73), (0xD3, 76), (0xD7, 277), (0xD8, 278), (0xDB, 227), (0xDC, 231),
        (0xDD, 101), (0xDE, 102), (0xDF, 258), (0xE3, 259), (0xE5, 280), (0xE6, 286), (0xE7, 285), (0xE8, 284), (0xE9, 283), (0xEA, 282),
        (0xED, 272),
    ];

    private const int SdlPause = 72, SdlPrintScreen = 70, SdlNumLock = 83;
    private static readonly ushort[] BySet1 = BuildBySet1();
    // the first index of each SDL code (host_keyboard.c's label lookup does the same)
    private static readonly Dictionary<int, int> IndexOf = BuildIndexOf();
    private static readonly Dictionary<uint, int> UsByVk = BuildUs();
    private static readonly Dictionary<int, uint> VkBySdl = BuildVkBySdl();

    private static ushort[] BuildBySet1()
    {
        var t = new ushort[256];
        foreach (var (i, s) in Set1) t[i] = s;
        return t;
    }

    private static Dictionary<int, int> BuildIndexOf()
    {
        var d = new Dictionary<int, int>();
        foreach (var (i, s) in Set1) d.TryAdd(s, i);
        return d;
    }

    /// <summary>The SDL code of each key Kiln names (<see cref="KeyNames"/>), on a US keyboard.</summary>
    private static Dictionary<uint, int> BuildUs()
    {
        var d = new Dictionary<uint, int>();
        for (uint c = 'A'; c <= 'Z'; c++) d[c] = 4 + (int)(c - 'A');
        for (uint c = '1'; c <= '9'; c++) d[c] = 30 + (int)(c - '1');
        d['0'] = 39;
        for (uint i = 0; i < 12; i++) d[0x70 + i] = 58 + (int)i;   // F1..F12
        for (uint i = 0; i < 11; i++) d[0x7C + i] = 104 + (int)i;  // F13..F23
        d[0x87] = 115;                                             // F24
        for (uint i = 1; i <= 9; i++) d[0x60 + i] = 88 + (int)i;   // Numpad1..9
        d[0x60] = 98;                                              // Numpad0
        // the punctuation keys by their place (KeyNames.Places), as Kiln's own bindings name them
        foreach (var (scan, vk) in KeyNames.Places) d[vk] = BySet1[scan];
        (uint Vk, int Sdl)[] named =
        [
            (0x08, 42), (0x09, 43), (0x0D, 40), (0x10, 225), (0x11, 224), (0x12, 226), (0x13, SdlPause), (0x14, 57), (0x1B, 41),
            (0x20, 44), (0x21, 75), (0x22, 78), (0x23, 77), (0x24, 74), (0x25, 80), (0x26, 82), (0x27, 79), (0x28, 81),
            (0x2C, SdlPrintScreen), (0x2D, 73), (0x2E, 76), (0x5B, 227), (0x5C, 231), (0x5D, 101), (0x6A, 85), (0x6B, 87),
            (0x6D, 86), (0x6E, 99), (0x6F, 84), (0x90, SdlNumLock), (0x91, 71), (0xA0, 225), (0xA1, 229), (0xA2, 224),
            (0xA3, 228), (0xA4, 226), (0xA5, 230),
        ];
        foreach (var (vk, sdl) in named) d[vk] = sdl;
        return d;
    }

    private static Dictionary<int, uint> BuildVkBySdl()
    {
        var d = new Dictionary<int, uint>();
        // the side-specific modifiers name their keys (LeftCtrl, not Ctrl)
        foreach (var (vk, sdl) in UsByVk.OrderBy(x => x.Key is 0x10 or 0x11 or 0x12 ? 1 : 0).ThenBy(x => x.Key)) d.TryAdd(sdl, vk);
        return d;
    }

    // ---------------------------------------------------------------------------------------- codes ----

    /// <summary>The SDL code of a kiln.ui scan code (the make code, 0x100 for E0); 0 for none.</summary>
    public static int SdlOfScan(uint scan) => scan is 0 or > 0x1FF ? 0 : BySet1[(scan & 0x7F) | ((scan & 0x100) != 0 ? 0x80u : 0u)];

    /// <summary>The SDL code ShroudForge gives the key of a press kiln.ui handed over (<paramref name="vk"/>,
    /// <paramref name="scan"/>); 0 for none. Pause, Print Screen and Num Lock by their virtual key, as ShroudForge's own
    /// conversion does (kiln.ui's Pause comes as its E1 record, whose make code is Ctrl's); a press without a scan code (a
    /// key sent by virtual key) by the layout's place of that key.</summary>
    public static int SdlOfPress(uint vk, uint scan)
    {
        switch (vk)
        {
            case 0x13: return SdlPause;
            case 0x2C: return SdlPrintScreen;
            case 0x90: return SdlNumLock;
        }
        if (SdlOfScan(scan) is var s and > 0) return s;
        // a press by virtual key alone: that key's place in this layout (VK_OEM_1 is Ü on a German keyboard), else its
        // US place
        return LayoutKey(vk) && LayoutSdl(vk) is var l and > 0 ? l : SdlOfVk(vk);
    }

    /// <summary>The SDL code of a key Kiln names by its virtual key: a punctuation name by its place, a letter, digit or
    /// other layout key where the keyboard layout has it, every other key by its fixed place; 0 for none.</summary>
    public static int SdlOfVk(uint vk)
    {
        if (!KeyNames.IsPlace(vk) && LayoutKey(vk) && LayoutSdl(vk) is var s and > 0) return s;
        return UsByVk.TryGetValue(vk, out var us) ? us : 0;
    }

    // the SDL code of the key the layout gives virtual key vk (MAPVK_VK_TO_VSC_EX, as ShroudForge's own conversion of a
    // virtual key does); 0 without a layout or a place
    private static int LayoutSdl(uint vk)
    {
        if (KeyLayout.Map is not { } map) return 0;
        var sc = map(vk, KeyLayout.VkToVscEx);
        var high = sc >> 8;
        return (sc & 0xFF) != 0 && high is 0 or 0xE0 ? SdlOfScan((sc & 0xFF) | (high != 0 ? 0x100u : 0u)) : 0;
    }

    // letters, digits and the OEM keys: the keys a layout moves around
    private static bool LayoutKey(uint vk) => vk is >= 'A' and <= 'Z' or >= '0' and <= '9' || KeyNames.Oem(vk);

    // ---------------------------------------------------------------------------------------- strings ----

    /// <summary>ShroudForge's modifier bit of each modifier key, for a chord on it (observe_keyboard primary_modifier).</summary>
    private static int Primary(int sdl) => sdl switch { 224 or 228 => 1, 226 or 230 => 2, 225 or 229 => 4, 227 or 231 => 8, _ => 0 };

    /// <summary>ShroudForge's modifier bits (Ctrl 1, Alt 2, Shift 4) of Kiln's (Ctrl 1, Shift 2, Alt 4).</summary>
    private static int Bits(KeyMods m) => ((m & KeyMods.Ctrl) != 0 ? 1 : 0) | ((m & KeyMods.Alt) != 0 ? 2 : 0) | ((m & KeyMods.Shift) != 0 ? 4 : 0);

    private static string Prefix(int bits)
    {
        var s = new StringBuilder();
        foreach (var (bit, name) in new[] { (1, "Ctrl"), (2, "Alt"), (4, "Shift"), (8, "Meta") })
            if ((bits & bit) != 0) s.Append(name).Append('+');
        return s.ToString();
    }

    /// <summary>A keyboard chord in ShroudForge's form ("Ctrl+SDL:10"); a modifier key does not name its own modifier.</summary>
    public static string KeyChord(int sdl, int bits) => Prefix(bits & ~Primary(sdl)) + "SDL:" + sdl.ToString(CultureInfo.InvariantCulture);

    /// <summary>A mouse chord in ShroudForge's form ("Shift+Mouse:4").</summary>
    public static string MouseChord(int button, int bits) => Prefix(bits) + "Mouse:" + button.ToString(CultureInfo.InvariantCulture);

    /// <summary>Why a press cannot be a ShroudForge shortcut.</summary>
    public enum Refusal
    {
        /// <summary>It can.</summary>
        None,
        /// <summary>A notch of the mouse wheel: ShroudForge has no wheel shortcuts.</summary>
        Wheel,
        /// <summary>A game controller's button: ShroudForge's shortcuts read the keyboard and the mouse only.</summary>
        Pad,
        /// <summary>A key with no SDL code Kiln knows.</summary>
        Unknown,
    }

    /// <summary>The shortcut a press in the capture box makes: a key with Ctrl, Shift or Alt held, or the middle or a side
    /// mouse button (kiln.ui never hands over the left and right ones); or why it cannot be one.</summary>
    public static (string? Binding, Refusal Why) FromPress(uint vk, uint scan, KeyMods mods)
    {
        if (PadKeys.IsPad(vk)) return (null, Refusal.Pad);
        if (MouseKeys.IsWheel(vk)) return (null, Refusal.Wheel);
        var bits = Bits(mods);
        switch (vk)
        {
            case MouseKeys.Middle: return (MouseChord(2, bits), Refusal.None);
            case MouseKeys.Mouse4: return (MouseChord(4, bits), Refusal.None);
            case MouseKeys.Mouse5: return (MouseChord(5, bits), Refusal.None);
        }
        var sdl = SdlOfPress(vk, scan);
        return sdl > 0 ? (KeyChord(sdl, bits), Refusal.None) : (null, Refusal.Unknown);
    }

    /// <summary>What the player typed as ShroudForge's own form: "Ctrl+G", "f5", "Shift + Mouse4", "Ü" (a key by its
    /// character on this keyboard), ShroudForge's "Ctrl+SDL:10" or "Mouse:4" (also with the modifiers in another order), a
    /// keyboard and a mouse chord joined by "|". Empty stays empty (no shortcut). Null: not a shortcut Kiln can read (the
    /// wheel, a controller button, an unknown name).</summary>
    public static string? Normalize(string? text)
    {
        if (text is null) return null;
        text = text.Trim();
        if (text.Length == 0) return "";
        if (text.Length > 200) return null;
        string? keyboard = null, mouse = null;
        foreach (var alternative in text.Split('|'))
        {
            var parts = alternative.Split('+', StringSplitOptions.TrimEntries);
            if (parts.Any(p => p.Length == 0)) return null;
            var bits = 0;
            foreach (var p in parts[..^1])
            {
                var bit = p.ToLowerInvariant() switch
                {
                    "ctrl" or "control" => 1, "alt" => 2, "shift" => 4, "meta" or "win" or "windows" or "super" or "cmd" => 8, _ => 0,
                };
                if (bit == 0 || (bits & bit) != 0) return null;
                bits |= bit;
            }
            var (sdl, button) = Trigger(parts[^1]);
            if (sdl > 0)
            {
                if (keyboard is not null) return null;
                keyboard = KeyChord(sdl, bits);
            }
            else if (button > 0)
            {
                if (mouse is not null) return null;
                mouse = MouseChord(button, bits);
            }
            else return null;
        }
        var result = keyboard is not null && mouse is not null ? keyboard + "|" + mouse : keyboard ?? mouse!;
        return ShroudForgePackages.ValidShortcut(result) ? result : null;
    }

    // the key or button of one chord: (SDL code, 0) or (0, mouse button) or (0, 0)
    private static (int Sdl, int Button) Trigger(string word)
    {
        var colon = word.IndexOf(':');
        if (colon > 0)
        {
            var kind = word[..colon];
            if (!int.TryParse(word.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return (0, 0);
            if (kind.Equals("SDL", StringComparison.OrdinalIgnoreCase)) return n is >= 1 and <= 511 ? (n, 0) : (0, 0);
            if (kind.Equals("Mouse", StringComparison.OrdinalIgnoreCase)) return n is >= 1 and <= 32 ? (0, n) : (0, 0);
            return (0, 0);
        }
        // (not "Mouse1" or "Mouse2": Kiln's Mouse2 is the right button, ShroudForge's Mouse:2 the middle one)
        if (word.Equals("MouseLeft", StringComparison.OrdinalIgnoreCase)) return (0, 1);
        if (word.Equals("MouseRight", StringComparison.OrdinalIgnoreCase)) return (0, 3);
        if (KeyNames.TryCode(word, out var vk))
            return vk switch
            {
                MouseKeys.Middle => (0, 2),
                MouseKeys.Mouse4 => (0, 4),
                MouseKeys.Mouse5 => (0, 5),
                _ when MouseKeys.IsWheel(vk) => (0, 0),
                _ => (SdlOfVk(vk), 0),
            };
        if (word.Equals("NumpadEnter", StringComparison.OrdinalIgnoreCase)) return (88, 0);
        // a key typed as the character it makes on this keyboard ("Ü", "ß", "^")
        foreach (var (index, sdl) in Set1)
            if (index < 0x80 && KeyLayout.CharOfScan((uint)index) is { } c && string.Equals(c, word, StringComparison.CurrentCultureIgnoreCase)) return (sdl, 0);
        return (0, 0);
    }

    // ---------------------------------------------------------------------------------------- names ----

    /// <summary>The name of a key by its SDL code: the character it makes on this keyboard for a letter or punctuation key ("Ü" on a German one), else Kiln's name of the key ("F5", "Numpad7", "LeftBracket" without a layout), else
    /// ShroudForge's own "SDL:n".</summary>
    public static string KeyName(int sdl)
    {
        if (IndexOf.TryGetValue(sdl, out var index) && KeyLayout.CharOfScan((uint)index) is { } c) return c;
        if (VkBySdl.TryGetValue(sdl, out var vk)) return KeyNames.Name(vk);
        if (sdl == 88) return "NumpadEnter";
        return "SDL:" + sdl.ToString(CultureInfo.InvariantCulture);
    }

    private static string MouseName(int button) => button switch
    {
        1 => "MouseLeft", 2 => "MouseMiddle", 3 => "MouseRight", _ => "Mouse" + button.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>A ShroudForge shortcut for people: "Ctrl + G", "Ü", "F5 / Mouse4"; the text itself when it is not one
    /// ShroudForge reads; "" for none.</summary>
    public static string Label(string binding)
    {
        if (binding.Length == 0) return "";
        if (!ShroudForgePackages.ValidShortcut(binding)) return binding;
        var chords = new List<string>();
        foreach (var alternative in binding.Split('|'))
        {
            var parts = alternative.Split('+');
            var trigger = parts[^1].Split(':');
            var n = int.Parse(trigger[1], CultureInfo.InvariantCulture);
            var words = parts[..^1].Select(m => m == "Meta" ? "Win" : m).ToList();
            words.Add(trigger[0] == "SDL" ? KeyName(n) : MouseName(n));
            chords.Add(string.Join(" + ", words));
        }
        return string.Join(" / ", chords);
    }
}
