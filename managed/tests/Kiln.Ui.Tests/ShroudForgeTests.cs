// SPDX-License-Identifier: MIT
// ShroudForge's packages as the in-game Mods page reads and writes them (ShroudForge.cs). The cases are shared with the
// launcher's Rust writer (schema/fixtures/shroudforge/cases.json, crates/kiln-manage/tests/shroudforge_fixtures.rs):
// both must read a setting the same way and write the same bytes.
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.ShroudForgeTests;

public sealed class ShroudForgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kiln-sf-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    private static string FixtureFile([CallerFilePath] string self = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "..", "..", "..", "schema", "fixtures", "shroudforge", "cases.json"));

    private static JsonArray Cases() => (JsonNode.Parse(File.ReadAllText(FixtureFile()))!["cases"] as JsonArray)!;

    public static TheoryData<string> Names()
    {
        var d = new TheoryData<string>();
        foreach (var c in Cases()) d.Add(c!["name"]!.GetValue<string>());
        return d;
    }

    private static JsonNode? View(SfSettingDef s)
    {
        var o = new JsonObject
        {
            ["key"] = s.Key, ["type"] = s.Type, ["label"] = s.Label, ["control"] = s.Control, ["applyAt"] = s.ApplyAt, ["live"] = s.Live,
        };
        if (s.EnabledWhen is { } w) o["enabledWhen"] = new JsonObject { ["setting"] = w.Setting, ["equals"] = w.EqualsValue?.DeepClone() };
        if (s.Options.Count > 0)
            o["options"] = new JsonArray(s.Options.Select(x => (JsonNode)new JsonObject { ["value"] = x.Value?.DeepClone(), ["label"] = x.Label }).ToArray());
        return o;
    }

    private static JsonArray EnabledNow(SfPackage p)
    {
        var values = p.Values;
        return new JsonArray(p.Settings.Where(s => ShroudForgePackages.IsEnabled(s.Key, p.Settings, values)).Select(s => (JsonNode)s.Key).ToArray());
    }

    private static void Same(JsonNode? expected, JsonNode? actual, string what) =>
        Assert.True(JsonNode.DeepEquals(Normal(expected), Normal(actual)), $"{what}\nexpected {expected?.ToJsonString()}\nactual   {actual?.ToJsonString()}");

    // 5 and 5.0 are the same number for the comparison (the fixture is written by Python, which keeps 5.0)
    private static JsonNode? Normal(JsonNode? n) => n switch
    {
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, Normal(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Normal).ToArray()),
        JsonValue v when ShroudForgePackages.NumberOf(v) is { } d => JsonValue.Create(d),
        _ => n?.DeepClone(),
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void Reads_and_writes_like_the_launcher(string name)
    {
        var c = Cases().Single(x => x!["name"]!.GetValue<string>() == name)!;
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "mod.json"), c["mod"]!.ToJsonString());
        File.WriteAllText(Path.Combine(dir, "src", "mod.lua"), "return {}");
        if (c["extended"] is JsonValue ext) File.WriteAllText(Path.Combine(dir, "extended.mod.json"), ext.GetValue<string>());

        var p = ShroudForgePackages.Read(dir);
        var read = c["read"]!;
        Assert.Equal(read["enabled"]!.GetValue<bool>(), p.Enabled);
        Assert.Equal(read["switchLive"]!.GetValue<bool>(), p.SwitchLive);
        Same(read["settings"], new JsonArray(p.Settings.Select(View).ToArray()), name + ": settings");
        Same(read["enabledNow"], EnabledNow(p), name + ": enabledWhen");

        var file = Path.Combine(dir, "extended.mod.json");
        foreach (var e in (c["edits"] as JsonArray)!)
        {
            var before = File.Exists(file) ? File.ReadAllBytes(file) : null;
            Exception? error = null;
            try
            {
                if (e!["enabled"] is { } on) ShroudForgePackages.SetEnabled(dir, on.GetValue<bool>());
                else ShroudForgePackages.SetSetting(dir, e["setting"]!.GetValue<string>(), e["value"]?.DeepClone());
            }
            catch (SfEditException x) { error = x; }
            Assert.True(e!["ok"]!.GetValue<bool>() == (error is null), $"{name}: {e.ToJsonString()} -> {error?.Message ?? "ok"}");
            if (error is not null) Assert.Equal(before, File.Exists(file) ? File.ReadAllBytes(file) : null);
        }
        Assert.Equal(c["expect"]!.GetValue<string>(), File.ReadAllText(file));
        Assert.False(File.Exists(file + ".kiln-tmp"));
        if (c["enabledAfter"] is { } after) Same(after, EnabledNow(ShroudForgePackages.Read(dir)), name + ": enabledWhen after the edits");
    }

    [Fact]
    public void An_action_request_is_the_file_shroudforges_runtime_reads()
    {
        var game = Path.Combine(_root, "game");
        var dir = Path.Combine(game, "mods", "editor");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "mod.json"), """{"id":"world-editor","name":"World Editor","version":"1.0.0","capabilities":["runtime"]}""");
        File.WriteAllText(Path.Combine(dir, "extended.mod.json"),
            """{"enabled":true,"groups":[{"label":"Blueprints","settings":[],"actions":[{"id":"saveBlueprint","label":"Save","confirm":"Overwrite?"}]}]}""");
        var p = ShroudForgePackages.Read(dir);
        Assert.Equal(new SfAction("saveBlueprint", "Save", null, "Overwrite?"), Assert.Single(Assert.Single(p.Groups).Actions));
        ShroudForgePackages.QueueAction(game, p, "saveBlueprint");
        var queued = Assert.Single(Directory.GetFiles(Path.Combine(game, "shroudforge", "ui", "actions")));
        Assert.StartsWith("world-editor-", Path.GetFileName(queued));
        var body = JsonNode.Parse(File.ReadAllText(queued))!;
        Assert.Equal(("world-editor", "saveBlueprint"), (body["modId"]!.GetValue<string>(), body["action"]!.GetValue<string>()));
        Assert.Throws<SfEditException>(() => ShroudForgePackages.QueueAction(game, p, "undeclared"));
        Assert.Empty(Directory.GetFiles(Path.Combine(game, "shroudforge", "ui"), "*.tmp"));

        // ShroudForge's own paths setting moves the folder
        Directory.CreateDirectory(Path.Combine(game, "shroudforge", "config"));
        File.WriteAllText(Path.Combine(game, "shroudforge", "config", "modloader-config.json"), """{"paths":{"ui":"elsewhere/ui"}}""");
        Assert.Equal(Path.Combine(game, "elsewhere/ui"), ShroudForgePackages.Folder(game, "ui"));
        Assert.Equal(Path.Combine(game, "shroudforge"), ShroudForgePackages.Folder(game, "state"));
    }

    private string Package(string settings = "\"value\":1")
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "mod.json"), """{"id":"test","name":"Test","version":"1","capabilities":["runtime"]}""");
        File.WriteAllText(Path.Combine(_root, "extended.mod.json"), "{\"enabled\":true,\"settings\":{\"amount\":{" + settings + "}}}");
        return _root;
    }

    [Fact]
    public void Writer_coalesces_recovers_after_invalid_metadata_and_drops_stale_controls()
    {
        var dir = Package();
        ShroudForgeUi.ResetForTests();
        ShroudForgeUi.Edit(dir, "amount", JsonValue.Create(2), 150);
        ShroudForgeUi.Edit(dir, "amount", JsonValue.Create(3), 150);
        Assert.Equal(1, ShroudForgePackages.NumberOf(ShroudForgePackages.Read(dir).Settings[0].Value));
        ShroudForgeUi.Flush();
        Assert.Equal(3, ShroudForgePackages.NumberOf(ShroudForgePackages.Read(dir).Settings[0].Value));
        ShroudForgeUi.Edit(dir, "amount", JsonValue.Create("bad"));
        ShroudForgeUi.Flush();
        Assert.Equal(3, ShroudForgePackages.NumberOf(ShroudForgePackages.Read(dir).Settings[0].Value));
        File.WriteAllText(Path.Combine(dir, "extended.mod.json"), "{not json}");
        ShroudForgeUi.Load(dir);
        Assert.Null(ShroudForgeUi.Get(dir, out var error));
        Assert.NotNull(error);
        ShroudForgeUi.Edit(dir, "amount", JsonValue.Create(4));
        ShroudForgeUi.Flush(); // a bad file must not kill the single writer
        Package();
        ShroudForgeUi.Edit(dir, "amount", JsonValue.Create(5));
        ShroudForgeUi.Flush();
        Assert.Equal(5, ShroudForgePackages.NumberOf(ShroudForgePackages.Read(dir).Settings[0].Value));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Theory]
    [InlineData("\"value\":null")]
    [InlineData("\"value\":[]")]
    [InlineData("\"value\":1,\"applyAt\":\"soon\"")]
    [InlineData("\"value\":1,\"applyAt\":\"live\",\"restartRequired\":true")]
    [InlineData("\"value\":1,\"enabledWhen\":{}")]
    [InlineData("\"value\":true,\"options\":{\"not-a-bool\":\"Bad\"}")]
    public void Malformed_metadata_is_not_editable(string settings)
    {
        var dir = Package(settings);
        var before = File.ReadAllBytes(Path.Combine(dir, "extended.mod.json"));
        Assert.Throws<SfEditException>(() => ShroudForgePackages.SetSetting(dir, "amount", JsonValue.Create(2)));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(dir, "extended.mod.json")));
    }

    [Fact]
    public void Untrusted_package_files_are_bounded_and_links_and_action_traversal_are_refused()
    {
        var dir = Package();
        var file = Path.Combine(dir, "extended.mod.json");
        File.WriteAllText(file, new string(' ', ShroudForgePackages.MaxFileBytes + 1));
        Assert.Throws<SfEditException>(() => ShroudForgePackages.Read(dir));
        Package();
        var target = file + ".outside";
        File.Move(file, target);
        File.CreateSymbolicLink(file, target);
        Assert.Throws<SfEditException>(() => ShroudForgePackages.SetEnabled(dir, false));
        Assert.Contains("\"enabled\":true", File.ReadAllText(target));
        File.Delete(file);
        File.Move(target, file);
        var package = ShroudForgePackages.Read(dir) with { Id = "../outside", Groups = [new SfGroup("Actions", null, [], [new SfAction("go", "Go", null, null)])] };
        Assert.Throws<SfEditException>(() => ShroudForgePackages.QueueAction(dir, package, "go"));
        package = package with { Id = "test", Groups = [new SfGroup("Actions", null, [], [new SfAction("../go", "Go", null, null)])] };
        Assert.Throws<SfEditException>(() => ShroudForgePackages.QueueAction(dir, package, "../go"));
    }

    [Theory]
    [InlineData("SDL:66", "F9")]
    [InlineData("Ctrl+Shift+SDL:14|Mouse:4", "Ctrl+Shift+K")]
    [InlineData("", null)]
    [InlineData("Mouse:4", null)]
    [InlineData("Meta+SDL:66", null)]
    [InlineData("SDL:511", null)]
    public void Menu_binding_uses_the_configured_supported_keyboard_chord(string sf, string? kiln) => Assert.Equal(kiln, SdlKeys.ToKilnKey(sf));

    [Theory]
    [InlineData("hosted", "kiln", "running", "client", "kiln", false)]
    [InlineData("hosted", "helper", "running", "client", "helper", false)]
    [InlineData("self-hosted", "kiln", "running", "client", null, true)]
    [InlineData("hosted", "kiln", "stopped", "client", null, true)]
    [InlineData("hosted", "off", "running", "client", null, true)]
    [InlineData("hosted", "kiln", "running", "server", null, true)]
    public void Menu_ownership_and_lifecycle_are_explicit(string mode, string menu, string runtime, string side, string? by, bool final)
    {
        var d = JsonSerializer.SerializeToElement(new { mode, menu, runtime, side, menuKey = "SDL:66" });
        var c = ShroudForgeMenu.Read(d);
        Assert.Equal((by, "SDL:66", final), (c.MenuBy, c.Key, c.Final));
    }

    [Fact]
    public void Values_made_by_the_page_render_like_values_read_from_a_file()
    {
        Assert.Equal("3", ShroudForgePackages.Render(JsonValue.Create(3)));
        Assert.Equal("3", ShroudForgePackages.Render(JsonValue.Create(3L)));
        Assert.Equal("3", ShroudForgePackages.Render(JsonValue.Create(3.0)));
        Assert.Equal("0.25", ShroudForgePackages.Render(JsonValue.Create(0.25f)));
        Assert.Equal("[\"a\", 2, true]", ShroudForgePackages.Render(new JsonArray("a", 2, true)));
        Assert.Equal("\"\\u0001é\\t\"", ShroudForgePackages.Render(JsonValue.Create("\u0001é\t")));
        Assert.Equal("integer", ShroudForgePackages.TypeOf(JsonNode.Parse("3")));
        Assert.Equal("number", ShroudForgePackages.TypeOf(JsonNode.Parse("3.0")));
        Assert.Equal("number", ShroudForgePackages.TypeOf(JsonValue.Create(3.0)));
        Assert.Equal("integer", ShroudForgePackages.TypeOf(JsonValue.Create(3)));
    }
}
