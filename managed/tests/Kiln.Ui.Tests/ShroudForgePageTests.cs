// SPDX-License-Identifier: MIT
// The ShroudForge part of the Mods page (ShroudForgeUi) drawn through the real page code (ModManagerPage.Draw) against a
// fake kiln.ui table: what happens to typed text when the field goes away, and which keys switch sections while a field
// has the keyboard. No game.
using System.Text.Json;
using System.Text.Json.Nodes;
using Kiln.FakeUi;
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
    /// <summary>A refresh that read the file before a save and finishes after it must not put the old values back.</summary>
    [Fact]
    public void A_read_that_started_before_a_save_does_not_put_old_values_back()
    {
        // a large description makes each read take a while, so a refresh and the writer's save overlap often
        var folder = Package($$"""{ "amount": { "value": 0, "label": "Amount", "description": "{{new string('d', 600_000)}}" } }""");
        var stale = new List<int>();
        for (var i = 1; i <= 150 && stale.Count == 0; i++)
        {
            var reads = Enumerable.Range(0, 3).Select(k => Task.Run(() => { Thread.SpinWait(Random.Shared.Next(0, 200_000)); ShroudForgeUi.Load(folder); })).ToArray();
            ShroudForgeUi.Edit(folder, "amount", JsonValue.Create(i));
            ShroudForgeUi.Flush();
            Task.WaitAll(reads);
            var onFile = ShroudForgePackages.NumberOf(ShroudForgePackages.Read(folder).Settings[0].Value);
            var shown = ShroudForgePackages.NumberOf(ShroudForgeUi.Get(folder, out _)!.Settings[0].Value);
            if (onFile == i && shown != onFile) stale.Add(i);
        }
        Assert.True(stale.Count == 0, $"old values shown after save {string.Join(", ", stale)}");
    }

    /// <summary>A setting whose key is the empty string is a setting, not the mod's switch.</summary>
    [Fact]
    public void A_setting_with_an_empty_key_is_not_the_switch()
    {
        var folder = Package("""{ "": { "value": false, "label": "Unnamed" } }""");
        File.WriteAllText(Path.Combine(folder, "extended.mod.json"), File.ReadAllText(Path.Combine(folder, "extended.mod.json")).Replace("\"enabled\": true", "\"enabled\": false"));
        ShroudForgeUi.Load(folder);
        ShroudForgeUi.Edit(folder, "", JsonValue.Create(true));
        ShroudForgeUi.Flush();
        var p = ShroudForgePackages.Read(folder);
        Assert.False(p.Enabled);
        Assert.Equal(JsonValueKind.True, p.Settings.Single(x => x.Key == "").Value!.GetValueKind());
    }

    /// <summary>A save that reached the file, after which the file cannot be read back, is not reported as "not saved".</summary>
    [Fact]
    public void A_save_whose_file_cannot_be_read_back_says_it_changed_the_file()
    {
        // a whole member of a fractional list is written without its fraction; the list then reads as whole numbers and
        // its options "1.5" no longer parse (sf-settings-data F1): the data layer refuses the file it just wrote
        var folder = Package("""{ "mult": { "value": [1.5], "label": "Multipliers", "options": { "1.0": "1x", "1.5": "1.5x" } } }""");
        var before = File.ReadAllText(Path.Combine(folder, "extended.mod.json"));
        ShroudForgeUi.Edit(folder, "mult", new JsonArray(JsonValue.Create(1.0)));
        ShroudForgeUi.Flush();
        var after = File.ReadAllText(Path.Combine(folder, "extended.mod.json"));
        bool readable;
        try { ShroudForgePackages.Read(folder); readable = true; }
        catch (SfEditException) { readable = false; }
        Assert.SkipWhen(after == before || readable, "the data layer now reads back what it wrote (sf-settings-data F1 fixed): no such save to report");
        var page = Page(Mod(folder));
        Frame(page);
        lock (FakeUiTable.Texts)
        {
            Assert.DoesNotContain(FakeUiTable.Texts, t => t.Contains("not saved", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(FakeUiTable.Texts, t => t.Contains("was changed", StringComparison.Ordinal) && t.Contains("cannot be read", StringComparison.Ordinal));
        }
        Assert.Null(ShroudForgeUi.Get(folder, out var error));  // no controls of the old package
        Assert.NotNull(error);
    }
}
