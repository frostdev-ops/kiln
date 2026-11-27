// SPDX-License-Identifier: MIT
// Key names in the player's keyboard layout (KeyLayout.cs; netjunk, 2026-11-26: a German keyboard and Kiln in English,
// the Keys page called his Ü key "["/LeftBracket). Only the shown name follows the layout; what Kiln stores does not
// change. No game: a played layout.
using Kiln.FakeUi;
using Xunit;

namespace Kiln.KeyLayoutTests;

[Collection(UiSerial.Name)]
public sealed class KeyLayoutTests : IDisposable
{
    private readonly Func<uint, uint, uint>? _map = KeyLayout.Map;
    private readonly Func<long> _stamp = KeyLayout.Stamp;

    public KeyLayoutTests() => KeyLayout.Map = German;

    public void Dispose()
    {
        KeyLayout.Map = _map;
        KeyLayout.Stamp = _stamp;
    }

    // MapVirtualKeyExW of a German (QWERTZ) layout for the keys used here: Ü at the US [ place (VK_OEM_1 there), ^ (a dead
    // key) at the US backquote place, Z and Y swapped
    internal static uint German(uint code, uint type)
    {
        (uint Scan, uint Vk, uint Ch)[] keys =
        [
            (0x1A, 0xBA, 'Ü'), (0x29, 0xDC, 0x8000_0000u | '^'), (0x0C, 0xDB, 'ß'), (0x15, 'Z', 'Z'), (0x2C, 'Y', 'Y'),
            (0x1E, 'A', 'A'),
        ];
        foreach (var k in keys)
        {
            if (type == KeyLayout.VscToVkEx && code == k.Scan) return k.Vk;
            if (type == KeyLayout.VkToVscEx && code == k.Vk) return k.Scan;
            if (type == KeyLayout.VkToChar && code == k.Vk) return k.Ch;
        }
        return 0;
    }

    [Theory]
    [InlineData("LeftBracket", "Ü")]
    [InlineData("Ctrl+Backquote", "Ctrl+^")]
    [InlineData("Minus", "ß")]
    [InlineData("Z", "Z")]          // a letter binding is that letter in any layout
    [InlineData("F5", "F5")]
    [InlineData("Shift+Numpad7", "Shift+Numpad7")]
    [InlineData("Comma", "Comma")]  // a key the layout does not name keeps Kiln's name
    [InlineData("Pad:LB+A", "Pad:LB+A")]
    [InlineData("None", "None")]
    public void A_binding_is_shown_by_the_character_its_key_makes_in_the_players_layout(string stored, string shown)
    {
        var chord = KeyChord.Parse(stored);
        Assert.Equal(shown, KeyLayout.Show(chord));
        Assert.Equal(stored, chord.ToString());  // keybinds.json keeps the stored form
    }

    [Fact]
    public void Without_a_layout_the_names_are_Kilns()
    {
        KeyLayout.Map = null;
        Assert.Equal("LeftBracket", KeyLayout.Show(KeyChord.Parse("LeftBracket")));
        Assert.Equal("LeftBracket", SfShortcuts.Label("SDL:47"));
    }

    [Fact]
    public void The_keys_page_shows_the_layouts_name()
    {
        var previous = Backend.Current;
        FakeUiTable.Install();
        Backend.Current = new FakeUiTable.Backend();
        UiNative.ResetForTests();
        var b = KeyBindings.Register("test.layout." + Guid.NewGuid().ToString("N"), "LeftBracket", null, "a test binding", "Tests");
        try
        {
            FakeUiTable.OpenTables = true;
            var ui = new UiDraw();
            lock (FakeUiTable.Texts) FakeUiTable.Texts.Clear();
            UiEngine.KeysTab(ui);
            lock (FakeUiTable.Texts) Assert.Contains("Ü", FakeUiTable.Texts);  // the Default column
        }
        finally
        {
            b.Dispose();
            UiEngine.EndMenuFrame();
            Backend.Current = previous;
            UiNative.ResetForTests();
            FakeUiTable.Free();
        }
    }

    [Fact]
    public void A_shroudforge_shortcut_shown_earlier_is_named_again_after_a_layout_switch()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiln-layout-" + Guid.NewGuid().ToString("N"));
        var previous = Backend.Current;
        FakeUiTable.Install();
        Backend.Current = new FakeUiTable.Backend();
        UiNative.ResetForTests();
        ShroudForgeUi.ResetForTests();
        try
        {
            var folder = Path.Combine(root, "mods", "m");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "mod.json"), """{"id":"m","name":"M","version":"1.0.0","capabilities":["runtime"]}""");
            File.WriteAllText(Path.Combine(folder, "extended.mod.json"),
                """{"schemaVersion":1,"enabled":true,"settings":{"k":{"value":"SDL:47","control":"keybind","label":"Key"}}}""");
            ShroudForgeUi.Load(folder);
            var page = new ModManagerPage
            {
                Source = () => new CatalogSnapshot
                {
                    Version = 1, Mods = [new CatalogMod { Engine = "shroudforge", Id = "m", Name = "M", Folder = folder, State = ModRunState.Loaded }],
                    Engines = [new CatalogEngine { Kind = "shroudforge", Name = "ShroudForge", Count = 1 }],
                },
                Filter = new EngineFilter("shroudforge"), Selected = "shroudforge/m",
            };
            var ui = new UiDraw();
            ui.Frame.DpiScale = 1;
            ui.Frame.Width = 1920;
            ui.Frame.Height = 1080;
            bool Shows(string text)
            {
                lock (FakeUiTable.Texts) FakeUiTable.Texts.Clear();
                ui.Frame.Number++;
                page.Draw(ui, new PageRect(0, 0, 1800, 900), 1);
                lock (FakeUiTable.Texts) return FakeUiTable.Texts.Contains(text);
            }
            KeyLayout.Map = null;
            KeyLayout.Stamp = () => 1;  // a US layout
            Shows("");
            Assert.True(Shows("Shortcut: LeftBracket"));
            KeyLayout.Map = German;
            KeyLayout.Stamp = () => 2;  // the player switched to German
            Shows("");
            Assert.True(Shows("Shortcut: Ü"));
        }
        finally
        {
            ShroudForgeUi.ResetForTests();
            Backend.Current = previous;
            UiNative.ResetForTests();
            FakeUiTable.Free();
            try { Directory.Delete(root, true); }
            catch (IOException) { }
        }
    }
}
