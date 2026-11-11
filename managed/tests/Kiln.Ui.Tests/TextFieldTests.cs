// SPDX-License-Identifier: MIT
// Text fields never write back a value they had to cut: UiDraw.InputText / InputMultiline hand ImGui the whole value
// when its UTF-8 fits the buffer, and a value that does not fit is shown read-only and left as it is. The ShroudForge
// page sizes its fields from the value, so a long setting is edited whole. No game: a fake kiln.ui table.
using System.Text.Json.Nodes;
using Kiln.FakeUi;
using Xunit;

namespace Kiln.TextFieldTests;

[Collection(UiSerial.Name)]
public sealed class TextFieldTests : IDisposable
{
    private const uint ReadOnly = 1u << 9;  // ImGuiInputTextFlags_ReadOnly (Dear ImGui 1.93)
    private readonly IKilnBackend _previous;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kiln-textfield-" + Guid.NewGuid().ToString("N"));
    private readonly UiDraw _ui = new();

    public TextFieldTests()
    {
        _previous = Backend.Current;
        FakeUiTable.Install();
        Backend.Current = new FakeUiTable.Backend();
        UiNative.ResetForTests();
        ShroudForgeUi.ResetForTests();
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

    [Fact]
    public void A_value_that_fits_the_buffer_reaches_imgui_whole()
    {
        var value = new string('a', 342);  // 342 bytes: fits 1025, used to be cut to 341 (a third of the buffer)
        var before = value;
        Assert.False(_ui.InputText("##v", ref value, 1025));
        Assert.Equal((before, 1026, 0u), FakeUiTable.Fields[^1]);

        var wide = string.Concat(Enumerable.Repeat("雪", 100));  // 300 bytes in 256 + ...: 86 chars used to be the limit
        var shroudlink = "x" + new string('y', 200);           // the shroudlink bridge's 256-byte field: 201 bytes
        Assert.False(_ui.InputText("##v", ref shroudlink, 256));
        Assert.Equal(shroudlink, FakeUiTable.Fields[^1].Text);
        Assert.False(_ui.InputMultiline("##v", ref wide, 301));
        Assert.Equal(wide, FakeUiTable.Fields[^1].Text);
    }

    [Fact]
    public void An_edit_comes_back_whole()
    {
        var value = new string('a', 342);
        FakeUiTable.Type = t => t + "b";
        Assert.True(_ui.InputText("##v", ref value, 1025));
        Assert.Equal(new string('a', 342) + "b", value);
    }

    [Fact]
    public void A_value_that_does_not_fit_is_read_only_and_never_written_back()
    {
        var value = new string('a', 2000);
        FakeUiTable.Type = t => t + "b";  // an edit to what the field shows (the cut text)
        Assert.False(_ui.InputText("##v", ref value, 1025));
        Assert.Equal(new string('a', 2000), value);
        var (shown, _, flags) = FakeUiTable.Fields[^1];
        Assert.Equal(ReadOnly, flags & ReadOnly);
        Assert.Equal(1025, shown.Length);

        var multi = string.Concat(Enumerable.Repeat("é", 600));  // 1200 bytes
        Assert.False(_ui.InputMultiline("##v", ref multi, 1025));
        Assert.Equal(string.Concat(Enumerable.Repeat("é", 600)), multi);
        Assert.Equal(512, FakeUiTable.Fields[^1].Text.Length);  // cut at a character, never inside one

        var nul = "before\0after";  // ImGui would see "before" only
        Assert.False(_ui.InputText("##v", ref nul, 256));
        Assert.Equal("before\0after", nul);
        Assert.Equal(ReadOnly, FakeUiTable.Fields[^1].Flags & ReadOnly);
    }

    private string Package(string settings)
    {
        var folder = Path.Combine(_root, "mods", "notes");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "mod.json"), """{"id":"notes","name":"Notes","version":"1.0.0","capabilities":["runtime"]}""");
        File.WriteAllText(Path.Combine(folder, "extended.mod.json"), "{\n  \"enabled\": true,\n  \"settings\": " + settings + "\n}\n");
        ShroudForgeUi.Load(folder);
        return folder;
    }

    private void Draw(string folder) => ShroudForgeUi.Draw(_ui, new CatalogMod { Engine = "shroudforge", Id = "notes", Name = "Notes", Folder = folder }, 1, 1, (_, _, _) => { });

    private static string? Saved(string folder, string key) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "extended.mod.json")))!["settings"]![key]!["value"]!.GetValue<string>();

    [Theory]
    [InlineData("text")]
    [InlineData("textarea")]
    public void A_long_shroudforge_text_setting_is_edited_whole(string control)
    {
        var long400 = new string('n', 400);  // no maxLength: the field used to hold 341 of them
        var folder = Package($$"""{ "note": { "value": "{{long400}}", "label": "Note", "control": "{{control}}" } }""");
        FakeUiTable.Type = t => t + "!";
        FakeUiTable.Item = new UiNative.Item { Active = 1 };
        Draw(folder);                      // the player types one character
        FakeUiTable.Type = null;
        FakeUiTable.Item = new UiNative.Item { Deactivated = 1 };
        Draw(folder);                      // and leaves the field
        ShroudForgeUi.Flush();
        Assert.Equal(long400 + "!", Saved(folder, "note"));
        Assert.Contains(FakeUiTable.Fields, f => f.Text == long400);
    }

    [Fact]
    public void A_shroudforge_text_too_long_for_any_field_is_shown_not_edited()
    {
        var huge = new string('h', 70_000);  // more than a one-line field can hold (64 KiB)
        var folder = Package($$"""{ "note": { "value": "{{huge}}", "label": "Note" } }""");
        FakeUiTable.Type = t => t + "!";
        FakeUiTable.Item = new UiNative.Item { Deactivated = 1 };
        Draw(folder);
        Draw(folder);
        ShroudForgeUi.Flush();
        Assert.Equal(huge, Saved(folder, "note"));
        Assert.DoesNotContain(FakeUiTable.Fields, f => (f.Flags & ReadOnly) == 0 && f.Text.Length > 0);
    }
}
