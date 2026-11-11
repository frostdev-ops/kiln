// SPDX-License-Identifier: MIT
// The ShroudForge part of the Mods page (ShroudForgeUi) drawn through the real page code (ModManagerPage.Draw) against a
// fake kiln.ui table: what happens to typed text when the field goes away, and which keys switch sections while a field
// has the keyboard. No game.
using System.Text.Json.Nodes;
using Kiln.Ui.Tests;
using Xunit;

namespace Kiln.ShroudForgePageTests;

[Collection(UiSerial.Name)]
public sealed class ShroudForgePageTests : IDisposable
{
    private readonly IKilnBackend _previous;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kiln-sfpage-" + Guid.NewGuid().ToString("N"));
    private readonly UiDraw _ui = new();

    public ShroudForgePageTests()
    {
        _previous = Backend.Current;
        FakeUiTable.Install();
        Backend.Current = new FakeUiTable.Backend();
        UiNative.ResetForTests();
        ShroudForgeUi.ResetForTests();
        _ui.Frame.DpiScale = 1;
        _ui.Frame.Width = 1920;
        _ui.Frame.Height = 1080;
    }

    public void Dispose()
    {
        ShroudForgeUi.ResetForTests();
        Backend.Current = _previous;
        UiNative.ResetForTests();
        FakeUiTable.Free();
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    private string Package(string settings, string id = "notes")
    {
        var folder = Path.Combine(_root, "mods", id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "mod.json"), $$"""{"id":"{{id}}","name":"Notes","version":"1.0.0","capabilities":["runtime"]}""");
        File.WriteAllText(Path.Combine(folder, "extended.mod.json"), "{\n  \"enabled\": true,\n  \"settings\": " + settings + "\n}\n");
        ShroudForgeUi.Load(folder);
        return folder;
    }

    private static CatalogMod Mod(string folder, ModRunState state = ModRunState.Loaded) =>
        new() { Engine = "shroudforge", Id = "notes", Name = "Notes", Folder = folder, State = state };

    private static ModManagerPage Page(CatalogMod mod) => new()
    {
        Source = () => new CatalogSnapshot
        {
            Version = 1, Mods = [mod], Engines = [new CatalogEngine { Kind = "shroudforge", Name = "ShroudForge", Count = 1 }],
        },
        Filter = new EngineFilter("shroudforge"), Selected = "shroudforge/notes",
    };

    private void Frame(ModManagerPage page)
    {
        _ui.Frame.Number++;
        page.Draw(_ui, new PageRect(0, 0, 1800, 900), 1);
        FakeUiTable.Pressed.Clear();
    }

    private static string Value(string folder, string key) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "extended.mod.json")))!["settings"]![key]!["value"]!.GetValue<string>();

    [Theory]
    [InlineData(UiKey.Tab)]
    [InlineData(UiKey.GamepadRB)]
    [InlineData(UiKey.GamepadLB)]
    public void Section_keys_do_not_leave_a_setting_field_that_has_the_keyboard(UiKey key)
    {
        var folder = Package("""{ "note": { "value": "old", "label": "Note" } }""");
        var page = Page(Mod(folder));
        Frame(page);
        FakeUiTable.Item = new UiNative.Item { Active = 1, Visible = 1 };  // the player clicked into the field
        FakeUiTable.Type = t => "new";
        Frame(page);
        FakeUiTable.Type = null;
        FakeUiTable.Pressed.Add(key);
        Frame(page);
        Assert.Equal(ModManagerSection.Mods, page.Section);
        FakeUiTable.Item = new UiNative.Item { Deactivated = 1, Visible = 1 };  // and left it: saved
        Frame(page);
        ShroudForgeUi.Flush();
        Assert.Equal("new", Value(folder, "note"));
    }

    [Fact]
    public void Text_typed_into_a_field_that_went_away_is_dropped_not_shown_as_the_value()
    {
        var folder = Package("""{ "note": { "value": "old", "label": "Note" } }""");
        var page = Page(Mod(folder));
        FakeUiTable.Item = new UiNative.Item { Active = 1, Visible = 1 };
        FakeUiTable.Type = t => "typed";
        Frame(page);
        FakeUiTable.Type = null;
        FakeUiTable.Item = default;
        page.Section = ModManagerSection.Network;  // the field is not drawn for a while (another section, menu closed)
        Frame(page);
        Frame(page);
        page.Section = ModManagerSection.Mods;
        FakeUiTable.Fields.Clear();
        Frame(page);
        Assert.Equal("old", FakeUiTable.Fields[^1].Text);  // the file's value, not the lost text
        ShroudForgeUi.Flush();
        Assert.Equal("old", Value(folder, "note"));
    }
}
