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

    private string Package(string settings, string id = "notes", string? groups = null)
    {
        var folder = Path.Combine(_root, "mods", id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "mod.json"), $$"""{"id":"{{id}}","name":"Notes","version":"1.0.0","capabilities":["runtime"]}""");
        File.WriteAllText(Path.Combine(folder, "extended.mod.json"), "{\n  \"enabled\": true,\n  \"settings\": " + settings +
            (groups is null ? "" : ",\n  \"groups\": " + groups) + "\n}\n");
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
        // a write that leaves a file the data layer refuses (as an earlier writer did with a whole member of a fractional
        // list, sf-settings-data F1). The real writer reads back what it writes now, so a stand-in does it here.
        var folder = Package("""{ "mult": { "value": [1.5], "label": "Multipliers", "options": { "1.0": "1x", "1.5": "1.5x" } } }""");
        ShroudForgeUi.SetSetting = (f, _, _) =>
        {
            File.WriteAllText(Path.Combine(f, "extended.mod.json"), """{ "enabled": true, "settings": { "mult": { "value": [1.5], "label": "Multipliers", "options": {} } } }""");
            return ShroudForgePackages.Read(f);  // throws: the file it just wrote cannot be read
        };
        try
        {
            ShroudForgeUi.Edit(folder, "mult", new JsonArray(JsonValue.Create(1.0)));
            ShroudForgeUi.Flush();
            Assert.Throws<SfEditException>(() => ShroudForgePackages.Read(folder));
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
        finally { ShroudForgeUi.SetSetting = ShroudForgePackages.SetSetting; }
    }
    private static string Chain(int n)
    {
        var parts = new List<string>();
        for (var i = 0; i < n; i++)
            parts.Add(i == 0 ? "\"s0\": { \"value\": true, \"label\": \"S0\" }"
                : $"\"s{i}\": {{ \"value\": true, \"label\": \"S{i}\", \"enabledWhen\": {{ \"setting\": \"s{i - 1}\", \"equals\": true }} }}");
        return "{ " + string.Join(", ", parts) + " }";
    }

    /// <summary>A frame of a package costs the same however its settings depend on each other: what the page needs is
    /// worked out when the package or an edit changes, not per frame (it used to walk the enabledWhen chain of every
    /// setting with a linear search per step, every frame).</summary>
    [Fact]
    public void A_frame_does_not_redo_the_packages_work()
    {
        var folder = Package(Chain(200));
        var mod = Mod(folder);
        ShroudForgeUi.Draw(_ui, mod, 1, 1, (_, _, _) => { });  // the first frame works it out
        Assert.NotNull(ShroudForgeUi.Get(folder, out _));
        lock (FakeUiTable.Texts) FakeUiTable.Texts.Clear();
        var before = GC.GetAllocatedBytesForCurrentThread();
        ShroudForgeUi.Draw(_ui, mod, 1, 1, (_, _, _) => { });
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes < 96 * 1024, $"a frame of a 200-setting chain allocated {bytes} bytes");
        lock (FakeUiTable.Texts) Assert.Contains("S199", FakeUiTable.Texts);  // every setting is still drawn
    }
    /// <summary>The page's indexed enabledWhen walk says what the data layer's rule says, for chains, cycles, a missing
    /// controller, numbers compared by value and options.</summary>
    [Fact]
    public void The_indexed_enabled_rule_is_the_data_layers()
    {
        var folder = Package("""
            {
              "a": { "value": true, "label": "A" },
              "b": { "value": 5, "label": "B", "enabledWhen": { "setting": "a", "equals": true } },
              "c": { "value": "x", "label": "C", "enabledWhen": { "setting": "b", "equals": 5.0 } },
              "d": { "value": 1, "label": "D", "enabledWhen": { "setting": "c", "equals": "y" } },
              "e": { "value": 1, "label": "E", "enabledWhen": { "setting": "gone", "equals": 1 } },
              "f": { "value": 1, "label": "F", "enabledWhen": { "setting": "g", "equals": 1 } },
              "g": { "value": 1, "label": "G", "enabledWhen": { "setting": "f", "equals": 1 } },
              "h": { "value": "fast", "label": "H", "options": { "fast": "Fast", "slow": "Slow" } },
              "i": { "value": 2, "label": "I", "enabledWhen": { "setting": "h", "equals": "fast" } },
              "j": { "value": false, "label": "J", "enabledWhen": { "setting": "a", "equals": false } }
            }
            """);
        var p = ShroudForgePackages.Read(folder);
        var byKey = p.Settings.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var values = p.Values;
        var rule = new ShroudForgeUi.EnabledRule(byKey, values);
        var ours = p.Settings.Reverse().Select(x => (x.Key, rule.Of(x.Key))).Reverse().ToArray();  // remembered answers in any order
        var theirs = p.Settings.Select(x => (x.Key, ShroudForgePackages.IsEnabled(x.Key, p.Settings, values))).ToArray();
        Assert.Equal(theirs, ours);
        Assert.Equal([true, true, true, false, false, false, false, true, true, false], ours.Select(x => x.Item2));
    }

    /// <summary>A string value with an unpaired surrogate escape (what JSON.stringify writes for a cut emoji): every read
    /// of it would throw. The data layer refuses the file when it reads it, as ShroudForge does, and the page says so
    /// once instead of throwing on the render thread every frame.</summary>
    [Fact]
    public void A_value_no_reader_can_use_is_reported_not_thrown_every_frame()
    {
        var folder = Package("""{ "name": { "value": "\ud83d", "label": "Name" }, "other": { "value": 1, "label": "Other" } }""");
        var page = Page(Mod(folder));
        Frame(page);  // used to throw InvalidOperationException out of the frame
        Frame(page);
        Assert.Null(ShroudForgeUi.Get(folder, out var error));
        Assert.Contains("lone surrogate", error);
        lock (FakeUiTable.Texts) Assert.Contains(FakeUiTable.Texts, t => t.StartsWith("Its settings cannot be shown", StringComparison.Ordinal));
    }
    private string Actions(string folder) => Path.Combine(_root, "shroudforge", "ui", "actions");

    private string ActionPackage() => Package("""{ "level": { "value": 1, "label": "Level" } }""",
        groups: """[ { "label": "Tools", "actions": [ { "id": "reset", "label": "Reset progress" } ] } ]""");

    private string? _lastLine;

    /// <summary>Clicks the action, then draws frames until a new result line shows (it is sent on a worker).</summary>
    private string? ClickAction(ModManagerPage page)
    {
        FakeUiTable.Click = "Reset progress";
        Frame(page);
        for (var i = 0; i < 200; i++)
        {
            lock (FakeUiTable.Texts) FakeUiTable.Texts.Clear();
            Frame(page);
            lock (FakeUiTable.Texts)
                if (FakeUiTable.Texts.FirstOrDefault(t => t.Contains("Reset progress", StringComparison.Ordinal) && t != "Reset progress") is { } line &&
                    line != _lastLine)
                    return _lastLine = line;
            Thread.Sleep(10);
        }
        return null;
    }

    private void UseGame() => ModCatalog.Sources = () => new CatalogSources { KilnRoot = Path.Combine(_root, "kiln") };

    /// <summary>An action asks the running mod: while ShroudForge does not run it the button queues nothing (a request
    /// left in ShroudForge's folder would run at a later start).</summary>
    [Fact]
    public void An_action_of_a_mod_shroudforge_does_not_run_queues_nothing()
    {
        UseGame();
        try
        {
            var folder = ActionPackage();
            var line = ClickAction(Page(Mod(folder, ModRunState.Waiting)));
            Assert.False(Directory.Exists(Actions(folder)) && Directory.EnumerateFiles(Actions(folder)).Any());
            Assert.NotNull(line);
            Assert.Contains("not sent", line);
        }
        finally { ModCatalog.Sources = ModCatalog.DefaultSources; }
    }

    /// <summary>ShroudForge keeps a request for an active mod that has no handler for ever, and looks at 32 files per turn:
    /// Kiln leaves at most one of its own per mod waiting.</summary>
    [Fact]
    public void An_action_request_does_not_pile_up_while_an_earlier_one_waits()
    {
        UseGame();
        try
        {
            var folder = ActionPackage();
            var page = Page(Mod(folder) with { Active = true });
            Assert.StartsWith("Requested", ClickAction(page));
            Assert.Contains("still waiting", ClickAction(page));
            Assert.Single(Directory.EnumerateFiles(Actions(folder)));
            foreach (var f in Directory.EnumerateFiles(Actions(folder))) File.Delete(f);  // ShroudForge took it
            Assert.StartsWith("Requested", ClickAction(page));
            Assert.Single(Directory.EnumerateFiles(Actions(folder)));
        }
        finally { ModCatalog.Sources = ModCatalog.DefaultSources; }
    }

    /// <summary>A request an earlier game start left (another pid) is still one waiting: ShroudForge keeps it and may run
    /// it at a later start, so Kiln adds none and says so.</summary>
    [Fact]
    public void A_request_an_earlier_game_left_counts_as_waiting()
    {
        UseGame();
        try
        {
            var folder = ActionPackage();
            Directory.CreateDirectory(Actions(folder));
            var old = Path.Combine(Actions(folder), $"notes-1762800000000000000-{Guid.NewGuid():N}-{Environment.ProcessId + 1}.json");
            File.WriteAllText(old, """{"modId":"notes","action":"reset","createdAt":1762800000}""");
            File.WriteAllText(Path.Combine(Actions(folder), $"notes-extra-1762800000000000000-{Guid.NewGuid():N}-7.json"), "{}");  // another mod's
            var line = ClickAction(Page(Mod(folder) with { Active = true }));
            Assert.NotNull(line);
            Assert.Contains("still waiting", line);
            Assert.Equal(2, Directory.EnumerateFiles(Actions(folder)).Count());
            File.Delete(old);
            Assert.StartsWith("Requested", ClickAction(Page(Mod(folder) with { Active = true })));  // another mod's file is not this one's
        }
        finally { ModCatalog.Sources = ModCatalog.DefaultSources; }
    }

    /// <summary>A request that cannot be written (ShroudForge's config names a ui folder with a NUL in it, which no file
    /// call takes) fails with a message, not a silent worker.</summary>
    [Fact]
    public void An_action_that_cannot_be_sent_says_why()
    {
        UseGame();
        try
        {
            var folder = ActionPackage();
            var config = Path.Combine(_root, "shroudforge", "config");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "modloader-config.json"), """{ "paths": { "ui": "a\u0000b" } }""");
            var line = ClickAction(Page(Mod(folder) with { Active = true }));
            Assert.NotNull(line);
            Assert.Contains("could not be sent", line);
        }
        finally { ModCatalog.Sources = ModCatalog.DefaultSources; }
    }
    /// <summary>The shroudlink page keeps its way back when the bridge goes away while it is open.</summary>
    [Fact]
    public void The_shroudlink_page_keeps_its_back_button_when_the_bridge_is_gone()
    {
        var page = Page(Mod(Package("{}")));
        ShroudlinkMenu.Open = true;  // opened while the bridge answered; it does not any more (off Windows it never does)
        try
        {
            Frame(page);
            lock (FakeUiTable.Texts) Assert.Contains("Back to mods", FakeUiTable.Texts);
            FakeUiTable.Click = "Back to mods";
            Frame(page);
            Assert.False(ShroudlinkMenu.Open);
            Assert.Equal(ModManagerSection.Mods, page.Section);
        }
        finally { ShroudlinkMenu.Open = false; }
    }
    /// <summary>The page re-reads the selected package every second: the same bytes make no new package and no new view.</summary>
    [Fact]
    public void A_reread_of_unchanged_files_keeps_the_package()
    {
        var folder = Package("""{ "level": { "value": 1, "label": "Level" } }""");
        var first = ShroudForgeUi.Get(folder, out _);
        ShroudForgeUi.Load(folder);
        Assert.Same(first, ShroudForgeUi.Get(folder, out _));
        File.WriteAllText(Path.Combine(folder, "extended.mod.json"), File.ReadAllText(Path.Combine(folder, "extended.mod.json")).Replace("1", "2"));
        ShroudForgeUi.Load(folder);
        Assert.NotSame(first, ShroudForgeUi.Get(folder, out _));
    }

    /// <summary>A package with more settings than a page can hold (a generated or broken file) shows the first ones and
    /// says how many more it has.</summary>
    [Fact]
    public void A_large_package_draws_its_first_settings_and_says_how_many_are_left()
    {
        var folder = Package("{ " + string.Join(", ", Enumerable.Range(0, 1000).Select(i => $"\"s{i}\": true")) + " }");
        ShroudForgeUi.Draw(_ui, Mod(folder), 1, 1, (_, _, _) => { });
        lock (FakeUiTable.Texts)
        {
            Assert.Contains("s299", FakeUiTable.Texts);
            Assert.DoesNotContain("s300", FakeUiTable.Texts);
            Assert.Contains(FakeUiTable.Texts, t => t.StartsWith("700 more settings are not shown here", StringComparison.Ordinal));
        }
    }

    /// <summary>A 10,000-step enabledWhen chain: each setting's answer is worked out once, on the worker that read the
    /// package, not by walking the chain again for every setting on the render thread.</summary>
    [Fact]
    public void A_long_enabled_chain_costs_one_walk_and_the_frame_none()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var folder = Package(Chain(10_000));  // read (and its view made) here, as the read worker does
        var mod = Mod(folder);
        var before = GC.GetAllocatedBytesForCurrentThread();
        ShroudForgeUi.Draw(_ui, mod, 1, 1, (_, _, _) => { });
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(watch.ElapsedMilliseconds < 3000, $"reading and drawing a 10,000-step chain took {watch.ElapsedMilliseconds} ms");
        Assert.True(bytes < 256 * 1024, $"the first frame allocated {bytes} bytes");
    }
    /// <summary>The page of a mod whose settings an earlier Kiln damaged says that ShroudForge refuses it until a save.</summary>
    [Fact]
    public void A_mod_whose_settings_need_repair_says_so_on_its_page()
    {
        var folder = Package("""{ "speed": { "value": 2, "label": "Speed", "options": { "1.5": "Slow", "2.0": "Fast" } } }""");
        ShroudForgeUi.Draw(_ui, Mod(folder), 1, 1, (_, _, _) => { });
        lock (FakeUiTable.Texts)
            Assert.Contains(FakeUiTable.Texts, t => t.StartsWith("ShroudForge refuses this mod until its settings are saved once", StringComparison.Ordinal) &&
                                                    t.Contains("Changing any setting or its switch here repairs it", StringComparison.Ordinal));
    }
}
