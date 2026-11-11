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

    internal static string FixtureFile([CallerFilePath] string self = "") =>
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

    // A number compares by its kind as ShroudForge types it (5 is an integer, 5.0 is not) and its value, however the
    // node holds it (read from the fixture's text, or made by the reader with JsonValue.Create).
    private static JsonNode? Normal(JsonNode? n) => n switch
    {
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, Normal(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Normal).ToArray()),
        JsonValue v when ShroudForgePackages.NumberOf(v) is { } d =>
            JsonValue.Create(ShroudForgePackages.TypeOf(v) + " " + d.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
        _ => n?.DeepClone(),
    };

    /// <summary>The bytes of a case's extended.mod.json: <c>extended</c> as UTF-8, or <c>extendedLatin1</c> one byte per
    /// character (a file that is not UTF-8).</summary>
    private static byte[]? ExtendedBytes(JsonNode c) =>
        c["extendedLatin1"] is JsonValue l ? System.Text.Encoding.Latin1.GetBytes(l.GetValue<string>())
        : c["extended"] is JsonValue e ? System.Text.Encoding.UTF8.GetBytes(e.GetValue<string>()) : null;

    [Theory]
    [InlineData("\"launcher\":null")]
    [InlineData("\"launcher\":\"eml\"")]
    [InlineData("\"targets\":\"server\"")]
    [InlineData("\"targets\":[]")]
    [InlineData("\"targets\":[\"client\",42]")]
    [InlineData("\"targets\":[\"client\",\"client\"]")]
    public void Refused_launcher_and_targets_never_claim_to_run(string fields)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "mod.json"), "{\"id\":\"a\",\"version\":\"1.0.0\"}");
        File.WriteAllText(Path.Combine(_root, "extended.mod.json"), "{\"schemaVersion\":1,\"enabled\":true," + fields + "}");
        Assert.Throws<SfEditException>(() => ShroudForgePackages.Read(_root));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Reads_and_writes_like_the_launcher(string name)
    {
        var c = Cases().Single(x => x!["name"]!.GetValue<string>() == name)!;
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "mod.json"), c["modText"]?.GetValue<string>() ?? c["mod"]!.ToJsonString());
        File.WriteAllText(Path.Combine(dir, "src", "mod.lua"), "return {}");
        var original = ExtendedBytes(c);
        if (original is not null) File.WriteAllBytes(Path.Combine(dir, "extended.mod.json"), original);

        var refused = c["refused"]?.GetValue<bool>() == true;
        if (refused)
        {
            // ShroudForge refuses the package: so does the page, and nothing is written
            var error = Assert.Throws<SfEditException>(() => ShroudForgePackages.Read(dir));
            if (c["errorContains"] is { } reason)
            {
                Assert.Contains("ShroudForge would refuse", error.Message);
                Assert.Contains(reason.GetValue<string>(), error.Message);
            }
        }
        else
        {
            var p = ShroudForgePackages.Read(dir);
            var read = c["read"]!;
            Assert.Equal(read["enabled"]!.GetValue<bool>(), p.Enabled);
            Assert.Equal(read["switchLive"]!.GetValue<bool>(), p.SwitchLive);
            Same(read["settings"], new JsonArray(p.Settings.Select(View).ToArray()), name + ": settings");
            Same(read["enabledNow"], EnabledNow(p), name + ": enabledWhen");
            Same(read["repair"] ?? new JsonArray(), new JsonArray(p.Repair.Select(k => (JsonNode)k).ToArray()), name + ": settings to repair");
            if (c["sfRefused"]?.GetValue<bool>() == true) Assert.NotEmpty(p.Repair);
        }

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
        // the bytes, not File.ReadAllText: that would drop a byte order mark the writer left (GetString keeps it as U+FEFF)
        if (refused) Assert.Equal(original, File.ReadAllBytes(file));
        else
        {
            Assert.Equal(c["expect"]!.GetValue<string>(), new System.Text.UTF8Encoding(false, true).GetString(File.ReadAllBytes(file)));
            // a write repairs every setting an earlier Kiln damaged
            if (File.Exists(file)) Assert.Empty(ShroudForgePackages.Read(dir).Repair);
        }
        // the writer's temporary file (extended.mod.json.kiln-<id>.tmp) is gone
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
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
            """{"schemaVersion":1,"enabled":true,"groups":[{"label":"Blueprints","settings":[],"actions":[{"id":"saveBlueprint","label":"Save","confirm":"Overwrite?"}]}]}""");
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
        File.WriteAllText(Path.Combine(_root, "mod.json"), """{"id":"test","name":"Test","version":"1.0.0","capabilities":["runtime"]}""");
        File.WriteAllText(Path.Combine(_root, "extended.mod.json"), "{\"schemaVersion\":1,\"enabled\":true,\"settings\":{\"amount\":{" + settings + "}}}");
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

    /// <summary>A number that is not finite is refused before the writer, which would put null (serde_json's form)
    /// in its place; the launcher cannot hold one at all (serde_json's numbers are finite).</summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_number_that_is_not_finite_is_refused(double number)
    {
        var dir = Package("\"value\":0.5");
        var file = Path.Combine(dir, "extended.mod.json");
        var before = File.ReadAllBytes(file);
        JsonNode value = JsonValue.Create(number);
        Assert.NotNull(ShroudForgePackages.Check(ShroudForgePackages.Read(dir).Settings[0], value));
        Assert.Throws<SfEditException>(() => ShroudForgePackages.SetSetting(dir, "amount", value));
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Theory]
    [InlineData("\"value\":null")]
    [InlineData("\"value\":[]")]
    [InlineData("\"value\":1,\"applyAt\":\"soon\"")]
    [InlineData("\"value\":1,\"applyAt\":\"live\",\"restartRequired\":true")]
    [InlineData("\"value\":1,\"enabledWhen\":{}")]
    [InlineData("\"value\":1,\"options\":{\"not-a-number\":\"Bad\"}")]
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
    public void Enabled_rule_is_defensive_for_non_package_models()
    {
        var definition = new SfSettingDef { Key = "base", Type = "integer", Label = "base", Value = JsonValue.Create(5) };
        var settings = new SfSettingList([
            definition,
            definition with { Key = "child", EnabledWhen = new SfCondition("base", JsonNode.Parse("5.0")) },
            definition with { Key = "lost", EnabledWhen = new SfCondition("missing", JsonValue.Create(5)) },
            definition with { Key = "cycle", EnabledWhen = new SfCondition("cycle", JsonValue.Create(5)) },
        ]);
        var values = settings.ToDictionary(s => s.Key, s => s.Value);
        Assert.True(ShroudForgePackages.IsEnabled("child", settings, values));
        Assert.False(ShroudForgePackages.IsEnabled("lost", settings, values));
        Assert.False(ShroudForgePackages.IsEnabled("cycle", settings, values));
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

/// <summary>Large packages (megabytes of settings): run alone, so their allocations and garbage collections do not stretch
/// the timing of the worker and snapshot tests that run in parallel.</summary>
[CollectionDefinition(nameof(ShroudForgeLargeFileTests), DisableParallelization = true)]
public sealed class ShroudForgeLargeFileCollection;

[Collection(nameof(ShroudForgeLargeFileTests))]
public sealed class ShroudForgeLargeFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kiln-sf-large-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    private string Package()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "mod.json"), """{"id":"test","name":"Test","version":"1.0.0","capabilities":["runtime"]}""");
        File.WriteAllText(Path.Combine(_root, "extended.mod.json"), "{\"schemaVersion\":1,\"enabled\":true,\"settings\":{\"amount\":{\"value\":1}}}");
        return _root;
    }

    /// <summary><c>limits.fileBytes</c>: the same bound as the launcher's reader.</summary>
    [Fact]
    public void The_settings_file_size_is_bounded_like_the_launcher()
    {
        var limit = JsonNode.Parse(File.ReadAllText(ShroudForgeTests.FixtureFile()))!["limits"]!["fileBytes"]!.GetValue<int>();
        Assert.Equal(ShroudForgePackages.MaxFileBytes, limit);
        var dir = Package();
        var file = Path.Combine(dir, "extended.mod.json");
        const string body = "{\"schemaVersion\":1,\"enabled\":true,\"settings\":{\"a\":{\"value\":1}}}";
        File.WriteAllText(file, body + new string(' ', limit - body.Length));
        Assert.Single(ShroudForgePackages.Read(dir).Settings);
        File.WriteAllText(file, body + new string(' ', limit + 1 - body.Length));
        Assert.Throws<SfEditException>(() => ShroudForgePackages.Read(dir));
        Assert.Throws<SfEditException>(() => ShroudForgePackages.SetEnabled(dir, false));
        Assert.Equal(limit + 1, new FileInfo(file).Length);
    }

    /// <summary>The Mods page asks IsEnabled for every setting every frame, on the game's render thread: a lookup by key
    /// must not scan the list (50,000 settings fit in the 2 MiB file).</summary>
    [Fact]
    public void Settings_are_looked_up_by_key_not_by_scanning()
    {
        const int n = 50_000;
        var dir = Package();
        var body = string.Join(",", Enumerable.Range(0, n).Select(i => $"\"s{i:D5}\":{{\"value\":{i}}}"));
        File.WriteAllText(Path.Combine(dir, "extended.mod.json"), "{\"schemaVersion\":1,\"enabled\":true,\"settings\":{" + body + "}}");
        var p = ShroudForgePackages.Read(dir);
        var values = p.Values;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var enabled = p.Settings.Count(s => ShroudForgePackages.IsEnabled(s.Key, p.Settings, values));
        clock.Stop();
        Assert.Equal(n, enabled);
        Assert.Same(p.Settings[n - 1], ShroudForgePackages.Find(p.Settings, $"s{n - 1:D5}"));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"one frame's IsEnabled calls for {n} settings took {clock.Elapsed}");
    }
}
