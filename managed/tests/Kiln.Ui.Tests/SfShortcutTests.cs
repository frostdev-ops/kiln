// SPDX-License-Identifier: MIT
// ShroudForge's shortcut strings from what the player presses or types (SfShortcuts.cs; netjunk, 2026-11-26: Altar
// Cleanup's key took only a raw "SDL:47"). The SDL codes are the ones ShroudForge itself gives a key: its hosted runtime
// turns the game window's raw records into codes through SDL's windows_scancode_table, Kiln's interpreter through its
// port (kiln-sfi input/feed.rs SET1), and kiln.ui hands Kiln.Ui the same make code and E0 flag. No game.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kiln.FakeUi;
using Xunit;

namespace Kiln.SfShortcutTests;

[Collection(UiSerial.Name)]
public sealed class SfShortcutTests : IDisposable
{
    private readonly Func<uint, uint, uint>? _layout = KeyLayout.Map;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kiln-sfkeys-" + Guid.NewGuid().ToString("N"));

    public SfShortcutTests() => KeyLayout.Map = null;  // a US keyboard unless a test plays another layout

    public void Dispose()
    {
        KeyLayout.Map = _layout;
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    // A German (QWERTZ) layout as MapVirtualKeyExW answers it, for the keys these tests use: Ü at the place of a US [, Z
    // and Y swapped, ß at the US minus, ^ (a dead key) at the US backquote.
    private static uint German(uint code, uint type)
    {
        (uint Scan, uint Vk, char Ch)[] keys =
        [
            (0x1A, 0xBA, 'Ü'), (0x27, 0xC0, 'Ö'), (0x28, 0xDE, 'Ä'), (0x0C, 0xDB, 'ß'), (0x29, 0xDC, '^'),
            (0x15, 'Z', 'Z'), (0x2C, 'Y', 'Y'), (0x22, 'G', 'G'), (0x1E, 'A', 'A'),
        ];
        foreach (var k in keys)
        {
            if (type == 3 && code == k.Scan) return k.Vk;
            if (type == 4 && code == k.Vk) return k.Scan;
            if (type == 2 && code == k.Vk) return k.Ch == '^' ? 0x8000_0000u | '^' : k.Ch;
        }
        return 0;
    }

    // ---- kiln-sfi's port of SDL's table ----

    private static string FeedRs([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "crates", "sfi", "kiln-sfi", "src", "input", "feed.rs"));

    [Fact]
    public void The_table_is_the_one_kiln_sfi_ports_plus_the_consumer_keys_it_leaves_out()
    {
        var text = File.ReadAllText(FeedRs());
        var start = text.IndexOf("const SET1: &[(u8, u16)] = &[", StringComparison.Ordinal);
        Assert.True(start >= 0, "feed.rs has no SET1");
        var block = text[start..text.IndexOf("];", start, StringComparison.Ordinal)];
        var rust = Regex.Matches(block, @"\((0x[0-9A-Fa-f]+),\s*(\d+)\)")
            .ToDictionary(m => Convert.ToInt32(m.Groups[1].Value, 16), m => int.Parse(m.Groups[2].Value));
        Assert.True(rust.Count > 130, $"read {rust.Count} pairs from feed.rs");
        var ours = SfShortcuts.Set1.ToDictionary(x => (int)x.Index, x => (int)x.Sdl);
        foreach (var (index, sdl) in rust) Assert.True(ours.TryGetValue(index, out var o) && o == sdl, $"index 0x{index:X2}: kiln-sfi {sdl}, Kiln.Ui {o}");
        foreach (var (index, sdl) in ours)
            if (!rust.ContainsKey(index)) Assert.True(sdl >= 258, $"index 0x{index:X2} -> {sdl} is only in Kiln.Ui's table");
    }

    // ---- a press in the capture box ----

    [Theory]
    // netjunk's key: Ü on a German keyboard (VK_OEM_1 there), [ on a US one: the same place, SDL:47 both
    [InlineData(0xBAu, 0x1Au, KeyMods.None, "SDL:47")]
    [InlineData(0xDBu, 0x1Au, KeyMods.None, "SDL:47")]
    [InlineData(0xDBu, 0x0Cu, KeyMods.None, "SDL:45")]  // ß (German), - (US)
    [InlineData(0xC0u, 0x27u, KeyMods.None, "SDL:51")]  // Ö
    [InlineData(0xDEu, 0x28u, KeyMods.None, "SDL:52")]  // Ä
    [InlineData(0xDCu, 0x29u, KeyMods.None, "SDL:53")]  // ^, a dead key on a German keyboard: its place all the same
    [InlineData(0xE2u, 0x56u, KeyMods.None, "SDL:100")] // < > left of Y/Z on an ISO keyboard
    [InlineData(0x5Au, 0x15u, KeyMods.None, "SDL:28")]  // Z of a German keyboard: the place of a US Y
    [InlineData(0x47u, 0x22u, KeyMods.Ctrl, "Ctrl+SDL:10")]
    [InlineData(0x78u, 0x43u, KeyMods.Ctrl | KeyMods.Shift | KeyMods.Alt, "Ctrl+Alt+Shift+SDL:66")]  // ShroudForge's order
    [InlineData(0x27u, 0x14Du, KeyMods.None, "SDL:79")]  // Right arrow (E0 4D)
    [InlineData(0x24u, 0x47u, KeyMods.None, "SDL:95")]   // keypad 7 with Num Lock off (VK_HOME): the keypad key
    [InlineData(0x24u, 0x147u, KeyMods.None, "SDL:74")]  // Home (E0 47)
    [InlineData(0x0Du, 0x1Cu, KeyMods.None, "SDL:40")]   // Enter
    [InlineData(0x0Du, 0x11Cu, KeyMods.None, "SDL:88")]  // keypad Enter (E0 1C)
    [InlineData(0x13u, 0x1Du, KeyMods.None, "SDL:72")]   // Pause: its raw E1 record's make code is Ctrl's
    [InlineData(0x13u, 0x45u, KeyMods.None, "SDL:72")]   // Pause by its window message
    [InlineData(0x90u, 0x45u, KeyMods.None, "SDL:83")]   // Num Lock, raw (no E0)
    [InlineData(0x90u, 0x145u, KeyMods.None, "SDL:83")]  // Num Lock, window message (E0)
    [InlineData(0x2Cu, 0x137u, KeyMods.None, "SDL:70")]  // Print Screen
    [InlineData(0xA3u, 0x11Du, KeyMods.Ctrl, "SDL:228")] // Right Ctrl alone: no Ctrl+ of its own
    [InlineData(0x5Bu, 0x15Bu, KeyMods.None, "SDL:227")] // the left Windows key
    [InlineData(0x70u, 0u, KeyMods.None, "SDL:58")]      // F1 sent without a scan code: by its virtual key
    [InlineData(0x04u, 0u, KeyMods.None, "Mouse:2")]
    [InlineData(0x05u, 0u, KeyMods.Shift, "Shift+Mouse:4")]
    [InlineData(0x06u, 0u, KeyMods.Ctrl | KeyMods.Alt, "Ctrl+Alt+Mouse:5")]
    public void A_press_is_the_code_ShroudForge_gives_that_key(uint vk, uint scan, KeyMods mods, string binding)
    {
        Assert.Equal((binding, SfShortcuts.Refusal.None), SfShortcuts.FromPress(vk, scan, mods));
        Assert.True(ShroudForgePackages.ValidShortcut(binding), binding);
    }

    [Theory]
    [InlineData(0x100u, "Wheel")]
    [InlineData(0x103u, "Wheel")]
    [InlineData(0x20Cu, "Pad")]   // A
    [InlineData(0x210u, "Pad")]   // LT
    [InlineData(0xFFu, "Unknown")]
    public void The_wheel_a_controller_and_an_unknown_key_are_refused_with_the_reason(uint vk, string why) =>
        Assert.Equal((null, Enum.Parse<SfShortcuts.Refusal>(why)), SfShortcuts.FromPress(vk, 0, KeyMods.None));

    [Fact]
    public void A_key_sent_by_virtual_key_alone_takes_its_place_in_the_layout()
    {
        KeyLayout.Map = German;
        Assert.Equal(("SDL:28", SfShortcuts.Refusal.None), SfShortcuts.FromPress('Z', 0, KeyMods.None));
        Assert.Equal(("SDL:47", SfShortcuts.Refusal.None), SfShortcuts.FromPress(0xBA, 0, KeyMods.None));
    }

    // ---- what the player types ----

    [Theory]
    [InlineData("Ctrl+G", "Ctrl+SDL:10")]
    [InlineData("f5", "SDL:62")]
    [InlineData(" ctrl + shift + F9 ", "Ctrl+Shift+SDL:66")]
    [InlineData("Shift+Ctrl+F9", "Ctrl+Shift+SDL:66")]
    [InlineData("Shift+Ctrl+SDL:66", "Ctrl+Shift+SDL:66")]   // ShroudForge's own form, modifiers in another order
    [InlineData("SDL:47", "SDL:47")]                         // as before: the raw form still works
    [InlineData("SDL:066", "SDL:66")]
    [InlineData("Ctrl+SDL:66|Mouse:4", "Ctrl+SDL:66|Mouse:4")]
    [InlineData("Mouse:4|Ctrl+G", "Ctrl+SDL:10|Mouse:4")]    // the keyboard chord first, as ShroudForge wants
    [InlineData("mouse:4", "Mouse:4")]
    [InlineData("Mouse4", "Mouse:4")]
    [InlineData("Shift+Mouse5", "Shift+Mouse:5")]
    [InlineData("MouseMiddle", "Mouse:2")]
    [InlineData("LeftBracket", "SDL:47")]
    [InlineData("Numpad7", "SDL:95")]
    [InlineData("NumpadEnter", "SDL:88")]
    [InlineData("Insert", "SDL:73")]
    [InlineData("Win+A", "Meta+SDL:4")]
    [InlineData("Ctrl+LeftCtrl", "SDL:224")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void A_readable_shortcut_is_converted_to_ShroudForges_form(string typed, string binding) =>
        Assert.Equal(binding, SfShortcuts.Normalize(typed));

    [Theory]
    [InlineData("WheelUp")]
    [InlineData("Ctrl+WheelDown")]
    [InlineData("Pad:A")]
    [InlineData("Ctrl+")]
    [InlineData("Hyper+G")]
    [InlineData("Ctrl+Ctrl+G")]
    [InlineData("G|H")]
    [InlineData("Mouse4|Mouse5")]
    [InlineData("SDL:512")]
    [InlineData("Mouse:33")]
    [InlineData("Mouse2")]  // Kiln's right button, ShroudForge's middle one: not guessed
    [InlineData("cleanup")]
    public void Text_that_is_no_shortcut_is_not_converted(string typed) => Assert.Null(SfShortcuts.Normalize(typed));

    [Fact]
    public void A_key_typed_as_its_character_on_this_keyboard_is_that_key()
    {
        KeyLayout.Map = German;
        Assert.Equal("SDL:47", SfShortcuts.Normalize("Ü"));
        Assert.Equal("Ctrl+SDL:47", SfShortcuts.Normalize("Ctrl+ü"));
        Assert.Equal("SDL:45", SfShortcuts.Normalize("ß"));
        Assert.Equal("SDL:28", SfShortcuts.Normalize("Z"));  // the German Z, at the place of a US Y
        KeyLayout.Map = null;
        Assert.Null(SfShortcuts.Normalize("Ü"));
    }

    // ---- what the page shows ----

    [Theory]
    [InlineData("Ctrl+SDL:10", "Ctrl + G")]
    [InlineData("SDL:66|Mouse:4", "F9 / Mouse4")]
    [InlineData("Ctrl+Alt+Shift+Meta+SDL:58", "Ctrl + Alt + Shift + Win + F1")]
    [InlineData("SDL:47", "LeftBracket")]
    [InlineData("SDL:95", "Numpad7")]
    [InlineData("SDL:88", "NumpadEnter")]
    [InlineData("SDL:224", "LeftCtrl")]
    [InlineData("SDL:72", "Pause")]
    [InlineData("Mouse:2", "MouseMiddle")]
    [InlineData("Mouse:1", "MouseLeft")]
    [InlineData("SDL:500", "SDL:500")]
    [InlineData("Shift+Ctrl+SDL:66", "Shift+Ctrl+SDL:66")]  // not ShroudForge's form: shown as it is
    [InlineData("", "")]
    public void A_shortcut_is_shown_with_Kilns_key_names(string binding, string label) => Assert.Equal(label, SfShortcuts.Label(binding));

    [Fact]
    public void A_layout_key_is_shown_as_the_character_it_makes_on_this_keyboard()
    {
        KeyLayout.Map = German;
        Assert.Equal("Ü", SfShortcuts.Label("SDL:47"));
        Assert.Equal("Ctrl + Z", SfShortcuts.Label("Ctrl+SDL:28"));
        Assert.Equal("^", SfShortcuts.Label("SDL:53"));
        Assert.Equal("F9", SfShortcuts.Label("SDL:66"));
    }

    [Fact]
    public void Every_key_kiln_names_converts_and_shows_back_as_itself()
    {
        foreach (var (index, sdl) in SfShortcuts.Set1)
        {
            var name = SfShortcuts.KeyName(sdl);
            if (name.StartsWith("SDL:", StringComparison.Ordinal)) continue;
            Assert.Equal("SDL:" + sdl, SfShortcuts.Normalize(name));
        }
    }

    // ---- the page's refusal ----

    [Fact]
    public void The_refusal_names_what_was_typed_and_starts_without_SDL_words()
    {
        var dir = Path.Combine(_root, "m");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "mod.json"), """{"id":"m","name":"M","version":"1.0.0","capabilities":["runtime"]}""");
        File.WriteAllText(Path.Combine(dir, "extended.mod.json"),
            """{"schemaVersion":1,"enabled":true,"settings":{"cleanup":{"value":"","control":"keybind","label":"Clean up altar zone"}}}""");
        var why = ShroudForgePackages.Check(ShroudForgePackages.Read(dir).Settings[0], JsonValue.Create("Ue"));
        Assert.NotNull(why);
        Assert.Contains("\"Ue\"", why);
        var first = why![..why.IndexOf('.')];
        Assert.DoesNotContain("SDL", first);
        Assert.StartsWith("Clean up altar zone", first);
    }
}
