// SPDX-License-Identifier: MIT
// Key bindings (Forge: KeyMapping + the Controls screen). Remappable in the Kiln menu and in <configDir>/keybinds.json (kiln/config, or the profile's configDir).
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>Modifier keys of a binding.</summary>
[Flags]
public enum KeyMods
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
}

/// <summary>A key with modifiers, written like "F8", "Ctrl+K", "Shift+Alt+F2", "Insert", "Numpad5", "Backquote", "Key223", or a mouse
/// input (since 0.2.0-dev.27): "Mouse4", "Mouse5", "MouseMiddle" (the side and middle buttons), "WheelUp", "WheelDown",
/// "WheelLeft", "WheelRight" (one press per notch of the wheel), with modifiers too: "Ctrl+WheelUp", "Shift+Mouse4". The
/// left and right buttons cannot be bound: Kiln's screens need them. <see cref="VirtualKey"/> is the Windows virtual-key
/// code; the mouse buttons have theirs (0x04 middle, 0x05 Mouse4, 0x06 Mouse5), and the wheel, which has none, uses 0x100
/// (up), 0x101 (down), 0x102 (left), 0x103 (right), above every key. keybinds.json stores the names, not the codes. A
/// Kiln older than 0.2.0-dev.27 does not know the mouse names (<see cref="TryParse"/> says false there).
/// <para>Since 0.2.0-dev.28 every key has a name that parses back: the OEM keys have names ("Oem8", "IntlBackslash"), a key
/// with no name is "Key" and its decimal code ("Key223"), and a modifier key by itself is the last word ("Ctrl",
/// "Shift+Alt"). The punctuation names ("Backquote", "Minus", "Equals", "LeftBracket", "RightBracket", "Backslash",
/// "Semicolon", "Quote", "Comma", "Period", "Slash", "IntlBackslash", "IntlRo", "NumpadComma") name the key at that place
/// of a US keyboard on every layout: "Backquote" is the key left of 1 also where a UK, German or French layout gives it
/// another virtual key. <see cref="VirtualKey"/> holds the US layout's code of that place.</para></summary>
public readonly record struct KeyChord(uint VirtualKey, KeyMods Mods)
{
    public static readonly KeyChord None = default;
    public bool IsNone => VirtualKey == 0;

    // Names of the left and right mouse buttons: known, so that the refusal can say why.
    private static readonly HashSet<string> Clicks = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mouse1", "Mouse2", "LeftClick", "RightClick", "MouseLeft", "MouseRight", "LeftMouse", "RightMouse", "LButton", "RButton",
        "Key1", "Key2",
    };

    /// <summary>Parses "Ctrl+Shift+F8"; false for an unknown key name. Every name <see cref="ToString"/> writes parses
    /// back, "Key223" (a key with no name, by its decimal code) included.</summary>
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
        var mods = KeyMods.None;
        uint vk = 0;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var open = text.TrimEnd().EndsWith('+');  // "Ctrl+": a key is missing
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var (mod, key) = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => (KeyMods.Ctrl, 0x11u),
                "shift" => (KeyMods.Shift, 0x10u),
                "alt" => (KeyMods.Alt, 0x12u),
                _ => (KeyMods.None, 0u),
            };
            if (mod != KeyMods.None)
            {
                // a modifier key by itself is the last word ("Ctrl", "Shift+Ctrl"), as ToString writes it
                if (i == parts.Length - 1 && vk == 0 && !open) vk = key;
                else mods |= mod;
                continue;
            }
            if (vk != 0 || !KeyNames.TryCode(part, out vk)) return false;
        }
        if (vk == 0) return false;
        chord = new KeyChord(vk, mods);
        return true;
    }

    public static KeyChord Parse(string text) =>
        TryParse(text, out var c) ? c : throw new FormatException(ParseError(text));

    /// <summary>Why <paramref name="text"/> is not a key (the message <see cref="Parse"/> throws).</summary>
    internal static string ParseError(string? text) =>
        text is not null && text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(Clicks.Contains)
            ? $"'{text}': the left and right mouse buttons cannot be bound (Kiln's screens need them); Mouse4, Mouse5, " +
              "MouseMiddle, WheelUp and WheelDown can, with Ctrl, Shift or Alt"
            : $"unknown key '{text}' (examples: F8, Ctrl+K, Shift+Insert, Numpad5, Ctrl+WheelUp, Shift+Mouse4; the left and " +
              "right mouse buttons cannot be bound)";

    public override string ToString()
    {
        if (IsNone) return "None";
        var s = "";
        if ((Mods & KeyMods.Ctrl) != 0) s += "Ctrl+";
        if ((Mods & KeyMods.Shift) != 0) s += "Shift+";
        if ((Mods & KeyMods.Alt) != 0) s += "Alt+";
        return s + KeyNames.Name(VirtualKey);
    }
}

/// <summary>The names of keys and which key a binding means: the same table as kiln.ui's
/// (native/services/ui/key_names.h, held together by Kiln.Ui.Tests' KeyNameTests). A punctuation binding is the key at its
/// place on a US keyboard, found by its scan code on any layout; every other binding is its virtual key.</summary>
internal static class KeyNames
{
    /// <summary>key_names.h kNames: codes the ranges in <see cref="Name"/> do not cover.</summary>
    internal static readonly (uint Vk, string Name)[] Table =
    [
        (0x03, "Break"), (0x04, "MouseMiddle"), (0x05, "Mouse4"), (0x06, "Mouse5"), (0x08, "Backspace"), (0x09, "Tab"),
        (0x0C, "Clear"), (0x0D, "Enter"), (0x10, "Shift"), (0x11, "Ctrl"), (0x12, "Alt"), (0x13, "Pause"), (0x14, "CapsLock"),
        (0x15, "Kana"), (0x16, "ImeOn"), (0x17, "Junja"), (0x18, "Final"), (0x19, "Kanji"), (0x1A, "ImeOff"),
        (0x1B, "Escape"), (0x1C, "Convert"), (0x1D, "NonConvert"), (0x1E, "Accept"), (0x1F, "ModeChange"), (0x20, "Space"),
        (0x21, "PageUp"), (0x22, "PageDown"), (0x23, "End"), (0x24, "Home"), (0x25, "Left"), (0x26, "Up"), (0x27, "Right"),
        (0x28, "Down"), (0x2C, "PrintScreen"), (0x2D, "Insert"), (0x2E, "Delete"), (0x5B, "LeftWin"), (0x5C, "RightWin"),
        (0x5D, "ContextMenu"), (0x6A, "NumpadMultiply"), (0x6B, "NumpadAdd"), (0x6C, "NumpadSeparator"),
        (0x6D, "NumpadSubtract"), (0x6E, "NumpadDecimal"), (0x6F, "NumpadDivide"), (0x90, "NumLock"), (0x91, "ScrollLock"),
        (0x92, "OemNecEqual"), (0x93, "OemFjMasshou"), (0x94, "OemFjTouroku"), (0x95, "OemFjLoya"), (0x96, "OemFjRoya"),
        (0xA0, "LeftShift"), (0xA1, "RightShift"), (0xA2, "LeftCtrl"), (0xA3, "RightCtrl"), (0xA4, "LeftAlt"),
        (0xA5, "RightAlt"), (0xA6, "BrowserBack"), (0xA7, "BrowserForward"), (0xA8, "BrowserRefresh"), (0xA9, "BrowserStop"),
        (0xAA, "BrowserSearch"), (0xAB, "BrowserFavorites"), (0xAC, "BrowserHome"), (0xAD, "VolumeMute"),
        (0xAE, "VolumeDown"), (0xAF, "VolumeUp"), (0xB0, "MediaNext"), (0xB1, "MediaPrevious"), (0xB2, "MediaStop"),
        (0xB3, "MediaPlayPause"), (0xB4, "LaunchMail"), (0xB5, "LaunchMedia"), (0xB6, "LaunchApp1"), (0xB7, "LaunchApp2"),
        (0xBA, "Semicolon"), (0xBB, "Equals"), (0xBC, "Comma"), (0xBD, "Minus"), (0xBE, "Period"), (0xBF, "Slash"),
        (0xC0, "Backquote"), (0xC1, "IntlRo"), (0xC2, "NumpadComma"), (0xDB, "LeftBracket"), (0xDC, "Backslash"),
        (0xDD, "RightBracket"), (0xDE, "Quote"), (0xDF, "Oem8"), (0xE1, "OemAx"), (0xE2, "IntlBackslash"),
        (0xE3, "IcoHelp"), (0xE4, "Ico00"), (0xE6, "IcoClear"), (0xE9, "OemReset"), (0xEA, "OemJump"), (0xEB, "OemPa1"),
        (0xEC, "OemPa2"), (0xED, "OemPa3"), (0xEE, "OemWsCtrl"), (0xEF, "OemCuSel"), (0xF0, "OemAttn"),
        (0xF1, "OemFinish"), (0xF2, "OemCopy"), (0xF3, "OemAuto"), (0xF4, "OemEnlw"), (0xF5, "OemBackTab"),
        (0xFE, "OemClear"), (0x100, "WheelUp"), (0x101, "WheelDown"), (0x102, "WheelLeft"), (0x103, "WheelRight"),
    ];

    /// <summary>key_names.h kPlaces: the punctuation keys by scan code, and the US layout's code there.</summary>
    internal static readonly (uint Scan, uint Vk)[] Places =
    [
        (0x29, 0xC0), (0x0C, 0xBD), (0x0D, 0xBB), (0x1A, 0xDB), (0x1B, 0xDD), (0x2B, 0xDC), (0x27, 0xBA),
        (0x28, 0xDE), (0x33, 0xBC), (0x34, 0xBE), (0x35, 0xBF), (0x56, 0xE2), (0x73, 0xC1), (0x7E, 0xC2),
    ];

    private static readonly Dictionary<uint, string> ByCode = Table.ToDictionary(x => x.Vk, x => x.Name);
    private static readonly Dictionary<string, uint> ByName = BuildByName();

    private static Dictionary<string, uint> BuildByName()
    {
        var d = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var (vk, name) in Table) d[name] = vk;
        for (uint i = 1; i <= 24; i++) d[$"F{i}"] = 0x70 + i - 1;
        for (var c = 'A'; c <= 'Z'; c++) d[c.ToString()] = c;
        for (var c = '0'; c <= '9'; c++) d[c.ToString()] = c;
        for (uint i = 0; i <= 9; i++) d[$"Numpad{i}"] = 0x60 + i;
        // other spellings that parse (never written)
        d["Ins"] = 0x2D;
        d["Del"] = 0x2E;
        d["Return"] = 0x0D;
        d["Esc"] = 0x1B;
        d["Grave"] = 0xC0;
        d["Mouse3"] = MouseKeys.Middle;
        return d;
    }

    /// <summary>The name of <paramref name="vk"/>: the table, F1..F24, 0..9, A..Z, Numpad0..9, else "Key" and the decimal
    /// code (key_names.h name()).</summary>
    public static string Name(uint vk)
    {
        if (ByCode.TryGetValue(vk, out var n)) return n;
        if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x70 + 1}";
        if (vk is >= '0' and <= '9' or >= 'A' and <= 'Z') return ((char)vk).ToString();
        if (vk is >= 0x60 and <= 0x69) return $"Numpad{vk - 0x60}";
        return $"Key{vk}";
    }

    /// <summary>The code of a key name, "Key&lt;3..255&gt;" included (1 and 2 are the left and right buttons: never).</summary>
    public static bool TryCode(string name, out uint vk)
    {
        if (ByName.TryGetValue(name, out vk)) return true;
        if (name.Length > 3 && name.StartsWith("Key", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(name.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var n) &&
            n is >= 3 and <= 0xFF)
        {
            vk = n;
            return true;
        }
        vk = 0;
        return false;
    }

    /// <summary>A punctuation (OEM) virtual key (key_names.h oem()).</summary>
    public static bool Oem(uint vk) =>
        vk is >= 0xBA and <= 0xC2 or >= 0xDB and <= 0xE4 or 0xE6 or >= 0xE9 and <= 0xF5 or >= 0x92 and <= 0x96 or 0xFE;

    /// <summary>A binding on this code means a place (a punctuation name), not a virtual key.</summary>
    public static bool IsPlace(uint vk)
    {
        foreach (var p in Places)
            if (p.Vk == vk) return true;
        return false;
    }

    /// <summary>The binding code of a press: a punctuation key at a known place is that place's code, any other press its
    /// virtual key (also a press without a scan code). The Keys page stores this.</summary>
    public static uint PressCode(uint vk, uint scan)
    {
        if (scan == 0 || !Oem(vk)) return vk;
        foreach (var p in Places)
            if (p.Scan == scan) return p.Vk;
        return vk;
    }

    /// <summary>Does a binding on <paramref name="bound"/> take this press: its place's key, or (for a code that names no
    /// place, such as "Key223" a dev.27 Keys page saved) the virtual key itself.</summary>
    public static bool Matches(uint bound, uint vk, uint scan) => bound == PressCode(vk, scan) || (!IsPlace(bound) && bound == vk);
}

/// <summary>The codes of the mouse inputs a <see cref="KeyChord"/> can hold (kiln_ui.h: KILN_UI_VK_WHEEL_*); their names are
/// in <see cref="KeyNames"/>.</summary>
internal static class MouseKeys
{
    public const uint Middle = 0x04, Mouse4 = 0x05, Mouse5 = 0x06;
    public const uint WheelUp = 0x100, WheelDown = 0x101, WheelLeft = 0x102, WheelRight = 0x103;

    public static bool IsWheel(uint vk) => vk is >= WheelUp and <= WheelRight;

    /// <summary>The middle or a side button (only the mouse gives Kiln.Ui these codes).</summary>
    public static bool IsButton(uint vk) => vk is Middle or Mouse4 or Mouse5;
}

/// <summary>One registered key binding (Forge: a KeyMapping).</summary>
public sealed class KeyBinding : IDisposable
{
    internal KeyBinding(string id, KeyChord defaultKey, Action? onPress, string description, string category, ModContext? owner)
    {
        Id = id;
        Default = defaultKey;
        Key = defaultKey;
        OnPress = onPress;
        Description = description;
        Category = category;
        Owner = owner;
    }

    public string Id { get; }
    /// <summary>What the key does, as the Keys page shows it. Kiln's own bindings set it again when the language of
    /// Kiln's texts changes (internal set, any thread).</summary>
    public string Description { get; internal set; }

    /// <summary>Groups bindings in the Kiln menu (default: the mod's name).</summary>
    public string Category { get; internal set; }

    public KeyChord Default { get; }

    /// <summary>The key in use (the player's choice from keybinds.json, else <see cref="Default"/>).</summary>
    public KeyChord Key { get; internal set; }

    /// <summary>Runs on key press (the game's main thread). Use <c>Client.Post</c> / <c>Context.Scheduler.Post</c>
    /// to act on the client / server world.</summary>
    public Action? OnPress { get; set; }

    /// <summary>Runs on key release. A wheel notch has no release: it never runs for a binding on the wheel.</summary>
    public Action? OnRelease { get; set; }

    /// <summary>Keep the key from the game when it matches (default true).</summary>
    public bool Consume { get; set; } = true;

    /// <summary>Also fire while the Kiln menu is open (default false). Bindings always fire while a mod's own window
    /// has the input, unless a text field has the keyboard. A binding on the mouse (a button or the wheel) never fires
    /// while a Kiln screen has the input: the wheel scrolls the screen, the buttons are the screen's.</summary>
    public bool WhileCapturing { get; set; }

    /// <summary>A key that types something (a letter, digit, punctuation or Space, without Ctrl or Alt) fires only during play or
    /// while a Kiln screen is open, because the game's own text fields (chat, a rename, the main menu's name and password
    /// boxes) don't tell Kiln they have the keyboard. True: fire on every game screen too. Function keys and chords with
    /// Ctrl or Alt always do. The wheel without Ctrl or Alt counts as such a key (the game's screens scroll with it); the
    /// mouse buttons always fire.</summary>
    public bool OnGameScreens { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Held right now (Forge: isDown()). Never true for a binding on the wheel (a notch is a press without a
    /// hold).</summary>
    public bool IsDown { get; internal set; }

    /// <summary>Presses since the last call (Forge: consumeClick()); counts even without <see cref="OnPress"/>.</summary>
    public int TakePresses() => Interlocked.Exchange(ref _presses, 0);

    internal int _presses;
    internal ModContext? Owner { get; }
    internal bool Disposed;

    public void Dispose() => KeyBindings.Remove(this);

    public override string ToString() => $"{Id} = {Key}";
}

/// <summary>Key bindings: register, remap, persist (<c>&lt;configDir&gt;/keybinds.json</c>: kiln/config or the profile's configDir).</summary>
[ClientOnly]
public static class KeyBindings
{
    private static readonly object Lock = new();
    private static KeyBinding[] _all = [];
    private static Dictionary<string, string> _saved = new(StringComparer.Ordinal);
    private static bool _loaded;
    internal const string MenuId = "kiln.menu";

    /// <summary>Registers <paramref name="id"/> ("mymod.toggle") with <paramref name="defaultKey"/> ("F8", "Ctrl+K").
    /// The player's choice from keybinds.json wins over the default. The binding belongs to the calling mod.</summary>
    public static KeyBinding Register(string id, string defaultKey, Action? onPress, string? description = null,
        string? category = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var chord = KeyChord.Parse(defaultKey);
        UiEngine.EnsureStarted();
        Load();
        var owner = UiModCalls.Caller;
        UiModCalls.Watch(owner);
        var b = new KeyBinding(id, chord, onPress, description ?? id, category ?? owner?.Info.Name ?? "Kiln", owner);
        lock (Lock)
        {
            if (_all.Any(x => x.Id == id)) throw new InvalidOperationException($"key binding {id} is already registered");
            if (_saved.TryGetValue(id, out var s) && KeyChord.TryParse(s, out var c)) b.Key = c;
            _all = [.. _all, b];
        }
        return b;
    }

    public static IReadOnlyList<KeyBinding> All
    {
        get
        {
            lock (Lock) return _all;
        }
    }

    public static KeyBinding? Find(string id) => All.FirstOrDefault(b => b.Id == id);

    /// <summary>Sets the key of <paramref name="id"/> and saves keybinds.json. "None" unbinds. False for an unknown
    /// key, and for the wheel on the Kiln menu's key (it would close the menu while you scroll it); the mouse buttons
    /// can open the menu.</summary>
    public static bool Rebind(string id, string key) => KeyChord.TryParse(key, out var c) && Rebind(id, c);

    public static bool Rebind(string id, KeyChord key)
    {
        if (id == MenuId && MouseKeys.IsWheel(key.VirtualKey)) return false;
        Load();
        var b = Find(id);
        lock (Lock) _saved[id] = key.ToString();
        if (b != null) b.Key = key;
        if (id == MenuId) UiEngine.ApplyMenuKey(key);
        Save();
        return b != null || id == MenuId;
    }

    public static void Reset(string id)
    {
        var b = Find(id);
        if (b != null) Rebind(id, b.Default);
        else if (id == MenuId) Rebind(id, MenuDefault);
    }

    internal static readonly KeyChord MenuDefault = new(0x2D, KeyMods.None);  // Insert

    internal static KeyChord MenuKey
    {
        get
        {
            Load();
            lock (Lock)
                return _saved.TryGetValue(MenuId, out var s) && KeyChord.TryParse(s, out var c) && !c.IsNone &&
                       !MouseKeys.IsWheel(c.VirtualKey) ? c : MenuDefault;
        }
    }

    internal static void Remove(KeyBinding b)
    {
        lock (Lock)
        {
            b.Disposed = true;
            _all = _all.Where(x => x != b).ToArray();
        }
    }

    internal static void RemoveOwnedBy(ModContext mod)
    {
        lock (Lock) _all = _all.Where(x => !ReferenceEquals(x.Owner, mod)).ToArray();
    }

    public static string? FilePath
    {
        get
        {
            try
            {
                // the profile's configDir (KILN_PATH_CONFIG) when one is active, else kiln/config
                return Path.Combine(KilnPaths.Config, "keybinds.json");
            }
            catch
            {
                return null;
            }
        }
    }

    internal static void Load()
    {
        lock (Lock)
        {
            if (_loaded) return;
            _loaded = true;
            var path = FilePath;
            if (path is null || !File.Exists(path)) return;
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
                if (root?["bindings"] is JsonObject o)
                    foreach (var (k, v) in o)
                        if (v?.GetValueKind() == JsonValueKind.String) _saved[k] = v.GetValue<string>();
            }
            catch (Exception e)
            {
                UiEngine.Log(LogLevel.Warn, $"{path}: {e.Message} (using defaults)");
            }
        }
    }

    internal static void Save()
    {
        var path = FilePath;
        if (path is null) return;
        try
        {
            Dictionary<string, string> copy;
            lock (Lock) copy = new Dictionary<string, string>(_saved);
            var o = new JsonObject
            {
                ["comment"] = "Kiln key bindings: id -> key (F8, Ctrl+K, Shift+Mouse4, Ctrl+WheelUp, None). Edited by the Kiln menu (Keys).",
                ["bindings"] = new JsonObject(copy.OrderBy(kv => kv.Key).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch (Exception e)
        {
            UiEngine.Log(LogLevel.Warn, $"saving {path}: {e.Message}");
        }
    }
}
