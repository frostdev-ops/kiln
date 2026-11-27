// SPDX-License-Identifier: MIT
// A ShroudForge mod's shortcut setting on the Mods page (ShroudForgeUi), drawn through the real page code against a fake
// kiln.ui table (netjunk, 2026-11-26: Altar Cleanup's key field refused "Ü" twice and took only "SDL:47"). No game.
using System.Text.Json.Nodes;
using Kiln.FakeUi;
using Xunit;

namespace Kiln.SfHotkeyPageTests;

[Collection(UiSerial.Name)]
public sealed class SfHotkeyPageTests : IDisposable
{
    private readonly IKilnBackend _previous;
    private readonly Func<uint, uint, uint>? _layout = KeyLayout.Map;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kiln-sfhotkey-" + Guid.NewGuid().ToString("N"));
    private readonly UiDraw _ui = new();

    public SfHotkeyPageTests()
    {
        _previous = Backend.Current;
        FakeUiTable.Install();
        Backend.Current = new FakeUiTable.Backend();
        UiNative.ResetForTests();
        ShroudForgeUi.ResetForTests();
        KeyLayout.Map = null;
        _ui.Frame.DpiScale = 1;
        _ui.Frame.Width = 1920;
        _ui.Frame.Height = 1080;
    }

    public void Dispose()
    {
        UiEngine.EndMenuFrame();
        UiEngine.EndMenuFrame();
        ShroudForgeUi.ResetForTests();
        KeyLayout.Map = _layout;
        Backend.Current = _previous;
        UiNative.ResetForTests();
        FakeUiTable.Free();
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    private string Package(string value)
    {
        var folder = Path.Combine(_root, "mods", "altar-cleanup");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "mod.json"), """{"id":"altar-cleanup","name":"Altar Cleanup","version":"1.0.0","capabilities":["runtime"]}""");
        File.WriteAllText(Path.Combine(folder, "extended.mod.json"),
            "{\n  \"schemaVersion\": 1,\n  \"enabled\": true,\n  \"settings\": { \"cleanup\": { \"value\": \"" + value +
            "\", \"control\": \"keybind\", \"label\": \"Clean up altar zone\", \"applyAt\": \"live\" } }\n}\n");
        ShroudForgeUi.Load(folder);
        return folder;
    }

    private static ModManagerPage Page(string folder) => new()
    {
        Source = () => new CatalogSnapshot
        {
            Version = 1,
            Mods = [new CatalogMod { Engine = "shroudforge", Id = "altar-cleanup", Name = "Altar Cleanup", Folder = folder, State = ModRunState.Loaded }],
            Engines = [new CatalogEngine { Kind = "shroudforge", Name = "ShroudForge", Count = 1 }],
        },
        Filter = new EngineFilter("shroudforge"), Selected = "shroudforge/altar-cleanup",
    };

    private void Frame(ModManagerPage page)
    {
        _ui.Frame.Number++;
        lock (FakeUiTable.Texts) FakeUiTable.Texts.Clear();
        page.Draw(_ui, new PageRect(0, 0, 1800, 900), 1);
        UiEngine.EndMenuFrame();
        FakeUiTable.Pressed.Clear();
    }

    private static string Value(string folder) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "extended.mod.json")))!["settings"]!["cleanup"]!["value"]!.GetValue<string>();

    private static bool Shown(string text)
    {
        lock (FakeUiTable.Texts) return FakeUiTable.Texts.Any(t => t.Contains(text, StringComparison.Ordinal));
    }

    // the player typed into the shortcut's text field and left it
    private void Type(ModManagerPage page, string text)
    {
        FakeUiTable.Item = new UiNative.Item { Active = 1, Visible = 1 };
        FakeUiTable.Type = _ => text;
        Frame(page);
        FakeUiTable.Type = null;
        FakeUiTable.Item = new UiNative.Item { Deactivated = 1, Visible = 1 };
        Frame(page);
        FakeUiTable.Item = default;
        ShroudForgeUi.Flush();
    }

    [Theory]
    [InlineData("Ctrl+G", "Ctrl+SDL:10")]
    [InlineData("F5", "SDL:62")]
    [InlineData("LeftBracket", "SDL:47")]
    [InlineData("SDL:47", "SDL:47")]
    [InlineData("Shift+Mouse4", "Shift+Mouse:4")]
    public void A_shortcut_typed_the_way_Kiln_names_keys_is_saved_in_ShroudForges_form(string typed, string saved)
    {
        var folder = Package("SDL:66");
        var page = Page(folder);
        Frame(page);
        Type(page, typed);
        Assert.Equal(saved, Value(folder));
    }

    [Fact]
    public void A_key_typed_as_its_character_on_a_German_keyboard_is_that_key()
    {
        KeyLayout.Map = (code, type) => (code, type) switch
        {
            (0x1A, 3) => 0xBA, (0xBA, 4) => 0x1A, (0xBA, 2) => 'Ü', _ => 0,
        };
        var folder = Package("SDL:66");
        var page = Page(folder);
        Frame(page);
        Type(page, "Ü");
        Assert.Equal("SDL:47", Value(folder));
        ShroudForgeUi.Load(folder);
        Frame(page);
        Frame(page);
        Assert.True(Shown("Ü"), string.Join(" | ", FakeUiTable.Texts));
    }

    [Fact]
    public void Text_that_is_no_shortcut_is_refused_in_plain_words_and_the_file_keeps_its_value()
    {
        var folder = Package("SDL:66");
        var page = Page(folder);
        Frame(page);
        Type(page, "cleanup key");
        Assert.Equal("SDL:66", Value(folder));
        Frame(page);
        Frame(page);
        Assert.True(Shown("Clean up altar zone: Kiln cannot read \"cleanup key\" as a key."), string.Join(" | ", FakeUiTable.Texts));
    }

    [Fact]
    public void The_shortcut_is_shown_by_Kilns_key_names()
    {
        var folder = Package("Ctrl+SDL:10|Mouse:4");
        var page = Page(folder);
        Frame(page);
        Frame(page);
        Assert.True(Shown("Ctrl + G / Mouse4"), string.Join(" | ", FakeUiTable.Texts));
    }

    // ---- the capture box ----

    private static bool Press(uint vk, uint scan = 0, KeyMods mods = KeyMods.None)
    {
        var down = new UiNative.Key { Size = 24, Vk = vk, Scan = scan, Mods = (uint)mods, Down = 1, Captured = 1 };
        var kept = UiEngine.Key(down);
        UiEngine.Key(down with { Down = 0 });
        return kept;
    }

    // the player clicks the box (its button shows the shortcut, or "press a key..." while it waits)
    private void Arm(ModManagerPage page, string shown)
    {
        FakeUiTable.MenuOpen = true;
        FakeUiTable.Click = shown + "###capture";
        Frame(page);
        Assert.Null(FakeUiTable.Click);
        Assert.NotNull(UiEngine.CaptureId);
        Frame(page);  // drawn again: still waiting
        Assert.NotNull(UiEngine.CaptureId);
    }

    [Fact]
    public void A_key_pressed_in_the_box_is_saved_as_ShroudForges_code_and_kept_from_the_game_and_the_bindings()
    {
        var fired = 0;
        using var binding = KeyBindings.Register("test.sfcapture." + Guid.NewGuid().ToString("N"), "Ctrl+G", () => fired++, "a test binding", "Tests");
        var folder = Package("SDL:66");
        var page = Page(folder);
        Frame(page);
        Arm(page, "F9");
        Assert.True(Press(0x11, 0x1D, KeyMods.Ctrl));  // Ctrl alone: it waits for the key it goes with
        Assert.NotNull(UiEngine.CaptureId);
        Assert.True(Press('G', 0x22, KeyMods.Ctrl));
        Assert.Null(UiEngine.CaptureId);
        ShroudForgeUi.Flush();
        Assert.Equal("Ctrl+SDL:10", Value(folder));
        Assert.Equal(0, fired);
        ShroudForgeUi.Load(folder);
        Frame(page);
        Frame(page);
        Assert.True(Shown("Ctrl + G"), string.Join(" | ", FakeUiTable.Texts));
    }

    [Fact]
    public void Netjunks_key_pressed_on_a_German_keyboard_is_SDL_47_and_shows_as_U_umlaut()
    {
        KeyLayout.Map = (code, type) => (code, type) switch { (0x1A, 3) => 0xBA, (0xBA, 2) => 'Ü', _ => 0 };
        var folder = Package("SDL:66");
        var page = Page(folder);
        Frame(page);
        Arm(page, "F9");
        Assert.True(Press(0xBA, 0x1A));  // the raw record's make code: the key's place, the same for ShroudForge
        ShroudForgeUi.Flush();
        Assert.Equal("SDL:47", Value(folder));
        ShroudForgeUi.Load(folder);
        Frame(page);
        Frame(page);
        Assert.True(Shown("Shortcut: Ü"), string.Join(" | ", FakeUiTable.Texts));
    }

    [Fact]
    public void A_side_mouse_button_with_Shift_is_a_mouse_shortcut()
    {
        var folder = Package("SDL:66");
        var page = Page(folder);
        Frame(page);
        Arm(page, "F9");
        Assert.True(Press(0x05, 0, KeyMods.Shift));
        ShroudForgeUi.Flush();
        Assert.Equal("Shift+Mouse:4", Value(folder));
    }

    [Theory]
    [InlineData(0x08u, 0x0Eu)]   // Backspace
    [InlineData(0x2Eu, 0x153u)]  // Delete
    public void Backspace_or_Delete_in_the_box_leaves_the_setting_without_a_key(uint vk, uint scan)
    {
        var folder = Package("SDL:66");
        var page = Page(folder);
        Frame(page);
        Arm(page, "F9");
        Assert.True(Press(vk, scan));
        ShroudForgeUi.Flush();
        Assert.Equal("", Value(folder));
        ShroudForgeUi.Load(folder);
        Frame(page);
        Frame(page);
        Assert.True(Shown("Shortcut: none (off)"), string.Join(" | ", FakeUiTable.Texts));
    }

    [Fact]
    public void Unbind_clears_the_shortcut()
    {
        var folder = Package("SDL:66");
        var page = Page(folder);
        Frame(page);
        FakeUiTable.Click = "Unbind###clear";
        Frame(page);
        ShroudForgeUi.Flush();
        Assert.Equal("", Value(folder));
    }

    [Fact]
    public void The_wheel_is_refused_and_scrolls_on_a_controller_button_is_refused_and_kept_and_the_box_keeps_waiting()
    {
        var folder = Package("SDL:66");
        var page = Page(folder);
        Frame(page);
        Arm(page, "F9");
        Assert.False(Press(0x101));  // a wheel notch: the page still scrolls
        Assert.NotNull(UiEngine.CaptureId);
        Frame(page);
        Frame(page);
        Assert.True(Shown("cannot be a turn of the mouse wheel"), string.Join(" | ", FakeUiTable.Texts));
        Assert.True(Press(0x20C));  // the pad's A
        Assert.NotNull(UiEngine.CaptureId);
        Frame(page);
        Frame(page);
        Assert.True(Shown("cannot be a controller button"), string.Join(" | ", FakeUiTable.Texts));
        Assert.True(Press(0x74, 0x3F));  // F5: saved after all
        ShroudForgeUi.Flush();
        Assert.Equal("SDL:62", Value(folder));
    }

    [Fact]
    public void A_box_not_drawn_in_a_frame_or_a_closed_menu_takes_no_key()
    {
        var folder = Package("SDL:66");
        var page = Page(folder);
        Frame(page);
        Arm(page, "F9");
        page.Section = ModManagerSection.Network;  // another section: the box is not drawn
        Frame(page);
        Assert.Null(UiEngine.CaptureId);
        Assert.False(Press('G', 0x22));
        page.Section = ModManagerSection.Mods;
        Frame(page);
        Arm(page, "F9");
        FakeUiTable.MenuOpen = false;  // Escape closed the menu inside kiln.ui
        Assert.False(Press('G', 0x22));  // in play: the game's
        Assert.Null(UiEngine.CaptureId);
        ShroudForgeUi.Flush();
        Assert.Equal("SDL:66", Value(folder));
    }

    [Fact]
    public void Clicking_the_box_again_stops_waiting()
    {
        var folder = Package("SDL:66");
        var page = Page(folder);
        Frame(page);
        Arm(page, "F9");
        FakeUiTable.Click = "press a key...###capture";
        Frame(page);
        Assert.Null(UiEngine.CaptureId);
        Assert.False(Press('G', 0x22));
    }

    [Fact]
    public void Tab_in_the_box_is_the_shortcut_and_does_not_switch_sections()
    {
        var folder = Package("SDL:66");
        var page = Page(folder);
        Frame(page);
        Arm(page, "F9");
        Assert.True(Press(0x09, 0x0F));
        FakeUiTable.Pressed.Add(UiKey.Tab);  // ImGui sees the key in the same frame
        Frame(page);
        Assert.Equal(ModManagerSection.Mods, page.Section);
        ShroudForgeUi.Flush();
        Assert.Equal("SDL:43", Value(folder));
    }

    [Fact]
    public void A_text_field_clicked_while_the_box_waits_gets_what_is_typed_there()
    {
        // the player clicked the box, then clicked the field below it (or the search box) to type the shortcut instead: a
        // left click is no press the box sees, so it still waits; the keys typed into the field are text, not the shortcut
        var folder = Package("SDL:66");
        var page = Page(folder);
        Frame(page);
        Arm(page, "F9");
        var c = new UiNative.Key { Size = 24, Vk = 'C', Scan = 0x2E, Mods = (uint)KeyMods.Shift, Down = 1, TextInput = 1, Captured = 1 };
        Assert.False(UiEngine.Key(c));  // the field's: kiln.ui gives it to the field and keeps it from the game
        UiEngine.Key(c with { Down = 0 });
        Assert.Null(UiEngine.CaptureId);
        ShroudForgeUi.Flush();
        Assert.Equal("SDL:66", Value(folder));
    }

    [Fact]
    public void Enter_that_ends_the_box_does_not_press_the_box_again()
    {
        var folder = Package("SDL:66");
        var page = Page(folder);
        Frame(page);
        Arm(page, "F9");
        Assert.True(Press(0x0D, 0x1C));
        FakeUiTable.Click = "Enter###capture";  // ImGui's keyboard navigation presses the focused box with that Enter
        Frame(page);
        Assert.Null(FakeUiTable.Click);  // the box was pressed
        Assert.Null(UiEngine.CaptureId);  // and does not wait again
        ShroudForgeUi.Flush();
        Assert.Equal("SDL:40", Value(folder));
    }
}
