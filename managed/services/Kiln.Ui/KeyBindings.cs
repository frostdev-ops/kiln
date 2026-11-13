// SPDX-License-Identifier: MIT
// Key bindings (Forge: KeyMapping + the Controls screen). Remappable in the Kiln menu and in <configDir>/keybinds.json (kiln/config, or the profile's configDir).
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

/// <summary>A key with modifiers, written like "F8", "Ctrl+K", "Shift+Alt+F2", "Insert", "Numpad5", "Backquote", or a mouse
/// input (since 0.2.0-dev.27): "Mouse4", "Mouse5", "MouseMiddle" (the side and middle buttons), "WheelUp", "WheelDown",
/// "WheelLeft", "WheelRight" (one press per notch of the wheel), with modifiers too: "Ctrl+WheelUp", "Shift+Mouse4". The
/// left and right buttons cannot be bound: Kiln's screens need them. <see cref="VirtualKey"/> is the Windows virtual-key
/// code; the mouse buttons have theirs (0x04 middle, 0x05 Mouse4, 0x06 Mouse5), and the wheel, which has none, uses 0x100
/// (up), 0x101 (down), 0x102 (left), 0x103 (right), above every key. keybinds.json stores the names, not the codes. A
/// Kiln older than 0.2.0-dev.27 does not know the mouse names (<see cref="TryParse"/> says false there).</summary>
public readonly record struct KeyChord(uint VirtualKey, KeyMods Mods)
{
    public static readonly KeyChord None = default;
    public bool IsNone => VirtualKey == 0;

    private static readonly Dictionary<string, uint> Names = BuildNames();

    // Names of the left and right mouse buttons: known, so that the refusal can say why.
    private static readonly HashSet<string> Clicks = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mouse1", "Mouse2", "LeftClick", "RightClick", "MouseLeft", "MouseRight", "LeftMouse", "RightMouse", "LButton", "RButton",
    };

    private static Dictionary<string, uint> BuildNames()
    {
        var d = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["Insert"] = 0x2D, ["Ins"] = 0x2D, ["Delete"] = 0x2E, ["Del"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23,
            ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Left"] = 0x25, ["Right"] = 0x27, ["Up"] = 0x26, ["Down"] = 0x28,
            ["Enter"] = 0x0D, ["Return"] = 0x0D, ["Escape"] = 0x1B, ["Esc"] = 0x1B, ["Tab"] = 0x09, ["Backspace"] = 0x08,
            ["Space"] = 0x20, ["Pause"] = 0x13, ["ScrollLock"] = 0x91, ["CapsLock"] = 0x14, ["NumLock"] = 0x90,
            ["Backquote"] = 0xC0, ["Grave"] = 0xC0, ["Minus"] = 0xBD, ["Equals"] = 0xBB, ["LeftBracket"] = 0xDB,
            ["RightBracket"] = 0xDD, ["Backslash"] = 0xDC, ["Semicolon"] = 0xBA, ["Quote"] = 0xDE, ["Comma"] = 0xBC,
            ["Period"] = 0xBE, ["Slash"] = 0xBF, ["NumpadMultiply"] = 0x6A, ["NumpadAdd"] = 0x6B,
            ["NumpadSubtract"] = 0x6D, ["NumpadDecimal"] = 0x6E, ["NumpadDivide"] = 0x6F,
        };
        for (uint i = 1; i <= 24; i++) d[$"F{i}"] = 0x70 + i - 1;
        for (var c = 'A'; c <= 'Z'; c++) d[c.ToString()] = c;
        for (var c = '0'; c <= '9'; c++) d[c.ToString()] = c;
        for (uint i = 0; i <= 9; i++) d[$"Numpad{i}"] = 0x60 + i;
        foreach (var (name, vk) in MouseKeys.Names) d[name] = vk;
        d["Mouse3"] = MouseKeys.Middle;
        return d;
    }

    /// <summary>Parses "Ctrl+Shift+F8"; false for an unknown key name.</summary>
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
        var mods = KeyMods.None;
        uint vk = 0;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= KeyMods.Ctrl; continue;
                case "shift": mods |= KeyMods.Shift; continue;
                case "alt": mods |= KeyMods.Alt; continue;
            }
            if (vk != 0 || !Names.TryGetValue(part, out vk)) return false;
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
        return s + (MouseKeys.Name(VirtualKey) ?? UiNative.KeyName(VirtualKey));
    }
}

/// <summary>The mouse inputs a <see cref="KeyChord"/> can name, and their codes (kiln_ui.h: KILN_UI_VK_WHEEL_*). Named here,
/// not by kiln.ui, so that keybinds.json gets the same names whatever native table answers.</summary>
internal static class MouseKeys
{
    public const uint Middle = 0x04, Mouse4 = 0x05, Mouse5 = 0x06;
    public const uint WheelUp = 0x100, WheelDown = 0x101, WheelLeft = 0x102, WheelRight = 0x103;

    public static readonly (string Name, uint Vk)[] Names =
    [
        ("MouseMiddle", Middle), ("Mouse4", Mouse4), ("Mouse5", Mouse5),
        ("WheelUp", WheelUp), ("WheelDown", WheelDown), ("WheelLeft", WheelLeft), ("WheelRight", WheelRight),
    ];

    public static string? Name(uint vk)
    {
        foreach (var (name, code) in Names)
            if (code == vk) return name;
        return null;
    }

    public static bool IsWheel(uint vk) => vk is >= WheelUp and <= WheelRight;
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
    public string Description { get; }

    /// <summary>Groups bindings in the Kiln menu (default: the mod's name).</summary>
    public string Category { get; }

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
    /// has the input, unless a text field has the keyboard. A binding on the wheel never fires while a Kiln screen has the
    /// input: the wheel scrolls the screen.</summary>
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
