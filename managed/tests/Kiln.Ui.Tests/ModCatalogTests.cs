// SPDX-License-Identifier: MIT
// The mod manager's model (ModCatalog: every engine's state file, stale and malformed files, profile-only mods, the
// launcher's normalisation) and the screen's logic (ModManagerLogic: filters, search, selection, the attention row). No game.
using System.Text.Json;
using Xunit;

namespace Kiln.ModCatalogTests;

public sealed class ModCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kiln-catalog-" + Guid.NewGuid().ToString("N"));
    private string Kiln => Path.Combine(_root, "kiln");
    private string State => Path.Combine(Kiln, "state");
    private const int Pid = 4242;
    private static readonly DateTime Start = DateTime.UtcNow.AddMinutes(-30);  // files written now are of this start
    private static long StartMs => new DateTimeOffset(Start).ToUnixTimeMilliseconds();

    public ModCatalogTests() => Directory.CreateDirectory(State);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    private void Write(string file, object o) => File.WriteAllText(Path.Combine(State, file), JsonSerializer.Serialize(o));
    private void WriteRaw(string file, string text) => File.WriteAllText(Path.Combine(State, file), text);

    private string Dir(params string[] parts)
    {
        var p = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    private void Profile(Dictionary<string, object[]> mods, object? dropins = null)
    {
        var file = Path.Combine(Kiln, "profile.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new { format = 1, id = "p", name = "Test", generation = 1, dropins = dropins ?? false, mods }));
        Write("boot.json", new { format = 1, pid = Pid, side = "client", profile = new { mode = "active", id = "p", name = "Test", file, missing = Array.Empty<object>() }, plugins = Array.Empty<object>(), chain = Array.Empty<object>() });
    }

    private CatalogSnapshot Read(params KilnModInput[] kiln) => ModCatalog.Read(new CatalogSources
    {
        KilnRoot = Kiln, Pid = Pid, ProcessStartUtc = Start, KilnMods = () => kiln, Side = "client",
    });

    private static CatalogMod Mod(CatalogSnapshot s, string engine, string id) => Assert.Single(s.Mods, m => m.Engine == engine && m.Id == id);

    /// <summary>shroudlink reports one line per mod DLL. 0.2.0-dev.22 imported its zip as one Library entry named after
    /// the first DLL (<c>assistants.dll</c>, five mods inside): that profile entry is covered by the DLLs reported from
    /// its folder and is no extra row.</summary>
    [Fact]
    public void A_profile_entry_is_covered_by_the_dlls_reported_from_its_folder()
    {
        var bundle = Dir("library", "shroudlink", "assistants.dll", "0.0.0+37909dbb");
        var single = Dir("library", "shroudlink", "expeditions.dll", "0.0.0+0dd3d0d0");
        var other = Dir("library", "shroudlink", "meter", "0.7.2+aaaaaaaa");
        Profile(new()
        {
            ["shroudlink"] =
            [
                new { id = "assistants.dll", path = bundle, version = "0.0.0", enabled = true },
                new { id = "expeditions.dll", path = single, version = "0.0.0", enabled = true },
                new { id = "meter", path = other, version = "0.7.2", enabled = true },
            ],
        });
        Write("shroudlink.json", new
        {
            format = 1, plugin = "kiln-shroudlink", started = true, updatedUnixMs = StartMs + 60_000,
            mods = new object[]
            {
                new { id = "assistants", dll = Path.Combine(bundle, "assistants.dll").Replace('/', '\\'), status = "loaded", reason = "" },
                new { id = "chestsort", dll = Path.Combine(bundle, "chestsort.dll").Replace('/', '\\'), status = "loaded", reason = "" },
                new { id = "expeditions", dll = Path.Combine(single, "expeditions.dll"), status = "loaded", reason = "" },
            },
        });
        var s = Read();
        var rows = s.Mods.Where(m => m.Engine == "shroudlink").Select(m => (m.Id, m.State)).ToArray();
        Assert.Equal(
            [("assistants", ModRunState.Loaded), ("chestsort", ModRunState.Loaded), ("expeditions", ModRunState.Loaded), ("meter", ModRunState.NotLoaded)],
            rows);
    }

    // ---- the launcher's normalisation (EngineStatus.tsx; the cases of launcher/tests/engine-kinds.spec.ts) ----

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Headline_matches_the_launcher()
    {
        Assert.Equal("failed", ModCatalog.Headline(J("""{"status":"failed","phase":"early","reason":"loader faulted"}""")));
        Assert.Equal("started", ModCatalog.Headline(J("""{"status":"started","phase":"world"}""")));
        Assert.Equal("done", ModCatalog.Headline(J("""{"phase":"done"}""")));
        Assert.Equal("hosted", ModCatalog.Headline(J("""{"mode":"hosted"}""")));
        Assert.Equal("stepped aside", ModCatalog.Headline(J("""{"abi":null,"steppedAside":true}""")));
        Assert.Equal("waiting for Ember's ABI", ModCatalog.Headline(J("""{"abi":null,"steppedAside":false}""")));
        Assert.Equal("running", ModCatalog.Headline(J("""{"started":true}""")));
        Assert.Equal("idle", ModCatalog.Headline(J("""{"started":false}""")));
        Assert.Null(ModCatalog.Headline(J("""{"status":""}""")));  // empty strings count as absent
        Assert.Null(ModCatalog.Headline(J("[]")));
    }

    [Fact]
    public void Tone_matches_the_launcher()
    {
        Assert.Equal("err", ModCatalog.ToneOf("failed"));
        Assert.Equal("err", ModCatalog.ToneOf("refused"));
        Assert.Equal("idle", ModCatalog.ToneOf("stepped aside"));
        Assert.Equal("idle", ModCatalog.ToneOf("not-installed"));
        Assert.Equal("idle", ModCatalog.ToneOf("waiting for Ember's ABI"));
        Assert.Equal("ok", ModCatalog.ToneOf("hosted"));
        Assert.Equal("ok", ModCatalog.ToneOf("done"));
        Assert.Equal("info", ModCatalog.ToneOf("starting"));
        Assert.Equal("idle", ModCatalog.ToneOf(null));
    }

    [Fact]
    public void Mod_lines_take_the_launchers_field_order()
    {
        var lines = ModCatalog.ModLines(J("""
            {"mods":[
              {"id":"a","state":"loaded","status":"x","reason":"r","detail":"d"},
              {"name":"Shroudtopia Mod","status":"active","detail":"d"},
              {"folder":"ensh","action":"copy","note":"copied"},
              {"id":"b","active":false,"error":"e"},
              {"id":"c","status":7},
              "not an object"
            ]}
            """));
        Assert.Equal(5, lines.Count);
        Assert.Equal(("a", "loaded", "r"), (lines[0].Id, lines[0].State, lines[0].Reason));
        Assert.Equal(("Shroudtopia Mod", "active", "d"), (lines[1].Id, lines[1].State, lines[1].Reason));
        Assert.Equal(("ensh", "copy", "copied"), (lines[2].Id, lines[2].State, lines[2].Reason));
        Assert.Equal(("b", "off", "e"), (lines[3].Id, lines[3].State, lines[3].Reason));
        Assert.Equal("7", lines[4].State);  // numbers read as text, as str() does
    }

    [Theory]
    [InlineData("loaded", ModRunState.Loaded)]
    [InlineData("active", ModRunState.Loaded)]
    [InlineData("started", ModRunState.Loaded)]
    [InlineData("failed", ModRunState.Failed)]
    [InlineData("rejected", ModRunState.Failed)]
    [InlineData("faulted", ModRunState.Failed)]
    [InlineData("disabled", ModRunState.Disabled)]
    [InlineData("inactive", ModRunState.Disabled)]
    [InlineData("pending", ModRunState.Waiting)]
    [InlineData("waiting-for-abi", ModRunState.Waiting)]
    [InlineData("candidate", ModRunState.Waiting)]
    [InlineData("skipped", ModRunState.NotLoaded)]
    [InlineData("duplicate", ModRunState.NotLoaded)]
    [InlineData("something new", ModRunState.NotLoaded)]
    [InlineData("own-loader", ModRunState.Loaded)]
    [InlineData("not-loaded", ModRunState.Failed)]
    internal void Engine_words_map_to_states(string word, ModRunState state) => Assert.Equal(state, ModCatalog.StateOf(word));

    // ---- each engine's file ----

    [Fact]
    public void Kiln_mods_come_from_the_mod_list_with_load_order_and_dependencies()
    {
        Profile(new() { ["kiln"] = [new { id = "a", version = "1.0.0", path = Dir("lib", "a") }, new { id = "gone", version = "2.0.0", path = Path.Combine(_root, "lib", "gone") }] });
        var deps = new[] { new ModDependency("b", ">=1", false) };
        var s = Read(
            new KilnModInput("a", "Alpha", "1.0.0", Dir("lib", "a"), ModSide.Client, ModState.Loaded, null, deps, 0),
            new KilnModInput("c", "", "0.1.0", Dir("lib", "c"), ModSide.Server, ModState.Failed, "needs b", [], -1));
        var a = Mod(s, "kiln", "a");
        Assert.Equal(("Alpha", ModRunState.Loaded, 0, "client"), (a.Name, a.State, a.LoadIndex, a.Side));
        Assert.Same(deps, a.Dependencies);
        var c = Mod(s, "kiln", "c");
        Assert.Equal(("c", ModRunState.Failed, "Needs b"), (c.Name, c.State, c.Reason));  // no name: the id; reasons read as sentences
        Assert.NotNull(c.Help);
        var gone = Mod(s, "kiln", "gone");
        Assert.True(gone.Missing);
        Assert.Equal(ModRunState.Failed, gone.State);
        Assert.Equal("Missing", gone.Chip);
        Assert.Equal(2, s.Problems);
    }

    [Fact]
    public void Emf_greeneye_flame_and_shroudtopia_lists_are_read()
    {
        Profile([]);
        Write("emf.json", new { format = 1, updatedUnixMs = StartMs + 5000, steppedAside = false, emfIdle = "", mods = new object[]
        {
            new { id = "beast", name = "Beastmaster", version = "0.3.3", status = "loaded", reason = "", side = "client" },
            new { id = "fish", name = "Fishing", version = "0.2", status = "rejected", reason = "needs EMF 29" },
        } });
        Write("greeneye.json", new { format = 1, updatedUnixMs = StartMs + 5000, mods = new[] { new { id = "fps", name = "FPS", version = "2", status = "candidate", reason = "" } } });
        Write("flame.json", new { format = 1, updatedUnixMs = StartMs + 5000, mods = new[] { new { id = "lamp", status = "started", reason = "" } } });
        Write("shroudtopia.json", new { format = 1, updatedUnixMs = StartMs + 5000, mods = new[] { new { name = "Glider", id = "glider", status = "inactive", reason = "" } } });
        var s = Read();
        Assert.Equal(("Beastmaster", ModRunState.Loaded, "client"), (Mod(s, "emf", "beast").Name, Mod(s, "emf", "beast").State, Mod(s, "emf", "beast").Side));
        var fish = Mod(s, "emf", "fish");
        Assert.Equal((ModRunState.Failed, "Needs EMF 29"), (fish.State, fish.Reason));  // shown as a sentence
        Assert.Equal(ModRunState.Waiting, Mod(s, "greeneye", "fps").State);
        Assert.Equal(ModRunState.Loaded, Mod(s, "flame", "lamp").State);
        Assert.Equal(ModRunState.Disabled, Mod(s, "shroudtopia", "glider").State);
        Assert.Equal(EngineHealth.Problem, s.Engines.Single(e => e.Kind == "emf").Health);
        Assert.Equal(EngineHealth.Ok, s.Engines.Single(e => e.Kind == "flame").Health);  // no state word while running: fine
    }

    [Fact]
    public void Eml_uses_its_pid_and_says_what_it_did()
    {
        Profile([]);
        Write("eml.json", new { format = 1, pid = Pid, phase = "done", patched = 3, mods = new[] { new { id = "stacks", name = "Bigger Stacks", version = "0.1.0", state = "loaded", reason = (string?)null } } });
        var s = Read();
        Assert.Equal(ModRunState.Loaded, Mod(s, "eml", "stacks").State);
        Assert.Contains("3 changes", s.Engines.Single(e => e.Kind == "eml").Note);
    }

    [Fact]
    public void Ember_mods_wait_and_say_why()
    {
        Profile([]);
        Write("ember.json", new { format = 1, abi = (object?)null, steppedAside = false, updatedUnixMs = StartMs + 1, mods = new[] { new { id = "motd", status = "waiting-for-abi", reason = "" } } });
        var s = Read();
        var m = Mod(s, "ember", "motd");
        Assert.Equal(ModRunState.Waiting, m.State);
        Assert.Contains("cannot run yet", m.Reason);
        Assert.Equal("Ember: mods are found but cannot run yet.", s.Engines.Single(e => e.Kind == "ember").Note);
        Assert.False(m.NeedsAttention);
    }

    [Fact]
    public void Stepped_aside_engines_say_so_and_their_mods_are_not_loaded()
    {
        Profile([]);
        Write("emf.json", new { format = 1, updatedUnixMs = StartMs + 1, steppedAside = true, steppedAsideReason = "EMF's own core is in the process", mods = new[] { new { id = "x", status = "pending", reason = "" } } });
        var s = Read();
        Assert.Equal(ModRunState.NotLoaded, Mod(s, "emf", "x").State);
        var e = s.Engines.Single(e => e.Kind == "emf");
        Assert.StartsWith("EMF: stepped aside for the real EMF", e.Note);
        Assert.Equal(EngineHealth.Idle, e.Health);
    }

    [Fact]
    public void Set_json_is_read_and_without_it_the_folder_is_listed()
    {
        Profile([]);
        Write("set.json", new { format = 1, pid = Pid, status = "running", mods = new[] { new { id = "sprint", name = "Sprint", version = "1.0", status = "failed", reason = "init.lua:1: boom", folder = "x" } } });
        var s = Read();
        Assert.Equal((ModRunState.Failed, "init.lua:1: boom"), (Mod(s, "set", "sprint").State, Mod(s, "set", "sprint").Reason));

        File.Delete(Path.Combine(State, "set.json"));  // an older kiln-set
        File.WriteAllText(Path.Combine(Dir("shrouded_engine_tweaks", "mods", "hotbar"), "init.lua"), "-- lua");
        Dir("shrouded_engine_tweaks", "mods", "not-a-mod");
        s = Read();
        var m = Mod(s, "set", "hotbar");
        Assert.Equal(ModRunState.NotLoaded, m.State);
        Assert.DoesNotContain(s.Mods, x => x.Id == "not-a-mod");
        Assert.Equal("SET did not run this start.", s.Engines.Single(e => e.Kind == "set").Note);
    }

    [Fact]
    public void Shroudforge_lists_the_profile_and_its_folder_with_the_runtime_state()
    {
        var mods = Dir("mods");
        Profile(new() { ["shroudforge"] = [new { id = "pack", version = "1.0.0", path = Dir("mods", "pack") }, new { id = "off", version = "1.0.0", path = Dir("mods", "off"), enabled = false }] });
        File.WriteAllText(Path.Combine(Dir("mods", "hand"), "extended.mod.json"), """{"id":"hand","name":"Hand Made","version":"0.1","enabled":false}""");
        Write("shroudforge.json", new { format = 1, mode = "hosted", reason = "", updated = StartMs / 1000 + 10, modsDir = mods });
        var s = Read();
        Assert.Equal(ModRunState.Loaded, Mod(s, "shroudforge", "pack").State);
        Assert.Equal(ModRunState.Disabled, Mod(s, "shroudforge", "off").State);
        var hand = Mod(s, "shroudforge", "hand");
        Assert.Equal(("Hand Made", ModRunState.Disabled, "folder"), (hand.Name, hand.State, hand.Source));
        Assert.Equal("ShroudForge: runtime hosted by Kiln.", s.Engines.Single(e => e.Kind == "shroudforge").Note);

        Write("shroudforge.json", new { format = 1, mode = "not-installed", reason = "", updated = StartMs / 1000 + 10 });
        s = Read();
        Assert.Equal(ModRunState.Failed, Mod(s, "shroudforge", "pack").State);
        Assert.Equal(EngineHealth.Problem, s.Engines.Single(e => e.Kind == "shroudforge").Health);
    }

    [Fact]
    public void Enshwick_lists_the_profile_and_its_copy_notes()
    {
        Profile(new() { ["enshwick"] = [new { id = "wick", version = "1.0.0", path = Dir("lib", "wick") }, new { id = "bad", version = "1.0.0", path = Dir("lib", "bad") }] });
        Write("enshwick.json", new { format = 1, status = "started", updatedUnixMs = StartMs + 1, mods = new[] { new { folder = "bad", action = "failed", note = "the folder is in use" }, new { folder = "wick", action = "copy", note = "" } } });
        var s = Read();
        Assert.Equal(ModRunState.Loaded, Mod(s, "enshwick", "wick").State);
        var bad = Mod(s, "enshwick", "bad");
        Assert.Equal((ModRunState.Failed, "The folder is in use"), (bad.State, bad.Reason));
        Assert.Equal("Enshwick's loader started.", s.Engines.Single(e => e.Kind == "enshwick").Note);
    }

    [Fact]
    public void Native_mods_chain_dlls_and_failed_engine_plugins_are_reported()
    {
        var file = Path.Combine(Kiln, "profile.json");
        File.WriteAllText(file, """{"format":1,"id":"p","generation":1,"mods":{}}""");
        Write("boot.json", new
        {
            format = 1, pid = Pid, profile = new { mode = "active", id = "p", file },
            chain = new[] { new { path = @"C:\g\fix.dll", source = "chain", status = "loaded", error = (string?)null }, new { path = @"C:\g\old.dll", source = "chain", status = "missing", error = (string?)"not there" } },
            plugins = new[]
            {
                new { id = "kiln-greeneye", file = "kiln-greeneye.dll", path = @"C:\g\kiln\plugins\kiln-greeneye.dll", status = "failed", error = (string?)"bad image" },
                new { id = "kiln-emf", file = "kiln-emf.dll", path = @"C:\g\kiln\plugins\kiln-emf.dll", status = "loaded", error = (string?)null },
                new { id = "my-plugin", file = "my.dll", path = @"C:\g\kiln\plugins\my.dll", status = "loaded", error = (string?)null },
            },
        });
        Write("native.json", new { format = 1, pid = Pid, mods = new object[] { new { id = "creative", version = "1.3.0", state = "failed", reason = "", dlls = new[] { new { file = "c.dll", error = "LoadLibrary 126" } } } } });
        var s = Read();
        Assert.Equal((ModRunState.Failed, "c.dll: LoadLibrary 126"), (Mod(s, "native", "creative").State, Mod(s, "native", "creative").Reason));
        Assert.Equal(ModRunState.Loaded, Mod(s, "chain", "fix.dll").State);
        Assert.True(Mod(s, "chain", "old.dll").Missing);
        Assert.Equal(ModRunState.Loaded, Mod(s, "plugin", "my-plugin").State);
        Assert.DoesNotContain(s.Mods, m => m.Id is "kiln-emf" or "kiln-greeneye");  // Kiln's own engine plugins are not mods
        var g = s.Engines.Single(e => e.Kind == "greeneye");  // listed although it has no mod: its plugin failed
        Assert.Equal(EngineHealth.Problem, g.Health);
        Assert.Contains("bad image", g.Note);
    }

    // ---- engine mods whose manifest id is no Kiln id: the profile has the Library's id, the plugin reports the manifest's ----

    [Fact]
    public void An_engine_row_matches_its_profile_entry_by_folder_whatever_the_separators_and_case()
    {
        // the profile (written by the launcher) has backslashes, kiln-eml reports the path it resolved with slashes
        Profile(new() { ["eml"] = [new { id = "hoardershelper", version = "1.0.0", path = @"Z:\Data\Lib\hoardershelper" }] });
        Write("eml.json", new
        {
            format = 1, pid = Pid, phase = "done",
            mods = new[] { new { id = "Hoarders Helper", name = "Hoarder's Helper", version = "1.0.0", state = "loaded", path = "z:/data/lib/hoardershelper", reason = (string?)null } },
        });
        var s = Read();
        var m = Assert.Single(s.Mods, x => x.Engine == "eml");
        Assert.Equal(("Hoarders Helper", ModRunState.Loaded), (m.Id, m.State));
        Assert.False(m.Missing);
    }

    [Fact]
    public void An_engine_row_without_a_folder_matches_by_the_slug_of_its_id()
    {
        Profile(new()
        {
            ["eml"] =
            [
                new { id = "less-dense-shroud-fog", version = "1.0.0", path = Dir("lib", "less-dense-shroud-fog") },
                new { id = "antisocialian-s-tweaks", version = "1.0.0", path = Dir("lib", "antisocialian-s-tweaks") },
                new { id = "unreported", version = "1.0.0", path = Dir("lib", "unreported") },
            ],
        });
        Write("eml.json", new
        {
            format = 1, pid = Pid, phase = "done",
            mods = new[]
            {
                new { id = "Less Dense Shroud Fog", state = "loaded" },
                new { id = "antisocialian's tweaks", state = "loaded" },
            },
        });
        var s = Read();
        // two rows from the plugin, and only the mod it did not report as "not reported": no duplicates of the first two
        Assert.Equal(["Less Dense Shroud Fog", "antisocialian's tweaks", "unreported"], s.Mods.Where(x => x.Engine == "eml").Select(x => x.Id).ToArray());
        Assert.Equal(ModRunState.NotLoaded, Mod(s, "eml", "unreported").State);
    }

    [Fact]
    public void The_slug_is_the_one_the_library_makes()
    {
        // the cases of kiln-manage's library::id_from_name (crates/kiln-manage/src/library.rs and tests/import_shapes.rs)
        Assert.Equal("hoardershelper", ModCatalog.IdFromName("HoardersHelper"));
        Assert.Equal("xhl-max-stack-65535", ModCatalog.IdFromName("XHL-Max-Stack-65535"));
        Assert.Equal("less-dense-shroud-fog", ModCatalog.IdFromName("Less Dense Shroud Fog"));
        Assert.Equal("antisocialian-s-tweaks", ModCatalog.IdFromName("antisocialian's tweaks"));
        Assert.Equal("no_intro", ModCatalog.IdFromName("no_intro"));
        Assert.Equal("escape", ModCatalog.IdFromName("../escape"));
        Assert.Equal("a-b", ModCatalog.IdFromName("  -.A  B_."));
        Assert.Equal("n", ModCatalog.IdFromName("\u00dcn\u00ef"));
        Assert.Equal("mod", ModCatalog.IdFromName("***"));
        Assert.Equal(64, ModCatalog.IdFromName(new string('x', 200)).Length);
        Assert.Equal("p", ModCatalog.NormPath(@"p\").TrimEnd('/'));
        Assert.Equal("Z:/a/b", ModCatalog.NormPath(@"Z:\a\b\"));
    }

    // ---- stale, malformed and missing files ----

    [Fact]
    public void A_file_from_an_earlier_start_does_not_count()
    {
        Profile(new() { ["eml"] = [new { id = "stacks", version = "0.1.0", path = Dir("lib", "stacks") }], ["flame"] = [new { id = "lamp", version = "1", path = Dir("lib", "lamp") }] });
        Write("eml.json", new { format = 1, pid = Pid + 1, phase = "done", mods = new[] { new { id = "stacks", state = "loaded" } } });
        Write("flame.json", new { format = 1, updatedUnixMs = StartMs - 3_600_000, mods = new[] { new { id = "lamp", status = "started" } } });
        var s = Read();
        var stacks = Mod(s, "eml", "stacks");
        Assert.Equal(ModRunState.NotLoaded, stacks.State);
        Assert.Contains("earlier start", stacks.Reason);
        Assert.Equal(ModRunState.NotLoaded, Mod(s, "flame", "lamp").State);
        Assert.Equal(EngineHealth.Idle, s.Engines.Single(e => e.Kind == "flame").Health);
    }

    [Fact]
    public void Files_without_a_time_use_their_modification_time()
    {
        var path = Path.Combine(State, "x.json");
        File.WriteAllText(path, "{}");
        File.SetLastWriteTimeUtc(path, Start.AddHours(-1));
        var src = new CatalogSources { KilnRoot = Kiln, Pid = Pid, ProcessStartUtc = Start };
        Assert.False(ModCatalog.IsCurrent(src, J("{}"), path));
        File.SetLastWriteTimeUtc(path, Start.AddMinutes(1));
        Assert.True(ModCatalog.IsCurrent(src, J("{}"), path));
        Assert.True(ModCatalog.IsCurrent(src, J($$"""{"written":"{{Start.AddSeconds(30):yyyy-MM-ddTHH:mm:ssZ}}"}"""), path));
        Assert.False(ModCatalog.IsCurrent(src, J("""{"updated":1}"""), path));
    }

    [Fact]
    public void Malformed_half_written_and_odd_files_never_throw()
    {
        Profile(new() { ["flame"] = [new { id = "lamp", version = "1", path = Dir("lib", "lamp") }], ["greeneye"] = [new { id = "g", path = Dir("lib", "g") }] });
        WriteRaw("flame.json", "{\"format\": 1, \"mods\": [{\"id\": \"la");
        WriteRaw("greeneye.json", "[1, 2, 3]");
        WriteRaw("emf.json", "{\"mods\": 5}");
        WriteRaw("ember.json", "{\"mods\": [null, 1, \"x\", {\"status\": null}]}");
        WriteRaw("native.json", "");
        var s = Read();
        var flame = s.Engines.Single(e => e.Kind == "flame");
        Assert.Equal(EngineHealth.Problem, flame.Health);
        Assert.Contains("could not be read", flame.Note);
        Assert.Equal(ModRunState.NotLoaded, Mod(s, "flame", "lamp").State);
        Assert.Equal(ModRunState.NotLoaded, Mod(s, "greeneye", "g").State);
        Assert.Single(s.Mods, m => m.Engine == "ember");  // the one object, its id "?"
    }

    [Fact]
    public void No_state_folder_and_no_profile_is_an_empty_catalog()
    {
        var s = ModCatalog.Read(new CatalogSources { KilnRoot = Path.Combine(_root, "nowhere"), Pid = Pid, ProcessStartUtc = Start });
        Assert.Empty(s.Mods);
        Assert.Empty(s.Engines);
    }

    [Fact]
    public void Profile_only_mods_cover_disabled_missing_and_unreported()
    {
        Profile(new()
        {
            ["greeneye"] =
            [
                new { id = "on", version = "1", path = Dir("lib", "on"), enabled = true },
                new { id = "off", version = "1", path = Dir("lib", "off"), enabled = false },
                new { id = "gone", version = "1", path = Path.Combine(_root, "lib", "gone"), enabled = true },
            ],
        });
        File.WriteAllText(Path.Combine(_root, "lib", "on", "mod.json"), """{"name":"On Mod"}""");
        var s = Read();  // greeneye.json missing: the plugin did not run
        var on = Mod(s, "greeneye", "on");
        Assert.Equal(("On Mod", ModRunState.NotLoaded), (on.Name, on.State));
        Assert.Contains("did not run this start", on.Reason);
        Assert.Equal(ModRunState.Disabled, Mod(s, "greeneye", "off").State);
        Assert.True(Mod(s, "greeneye", "gone").Missing);
        Assert.Equal("Greeneye did not run this start.", s.Engines.Single(e => e.Kind == "greeneye").Note);
    }

    [Fact]
    public void Play_once_without_mods_says_so()
    {
        Write("boot.json", new { format = 1, pid = Pid, profile = new { mode = "no-mods" } });
        var s = Read();
        Assert.Equal("Play once without mods: no mod loads this start.", s.Profile);
    }

    [Fact]
    public void Snapshot_answers_at_once_and_refreshes_on_a_worker()
    {
        Profile([]);
        Write("emf.json", new { format = 1, updatedUnixMs = StartMs + 1, mods = new[] { new { id = "a", status = "loaded" } } });
        var reads = 0;
        ModCatalog.Sources = () => { Interlocked.Increment(ref reads); return new CatalogSources { KilnRoot = Kiln, Pid = Pid, ProcessStartUtc = Start }; };
        try
        {
            var first = ModCatalog.Snapshot(TimeSpan.FromMinutes(1));  // empty: the first refresh runs on a worker
            Assert.Empty(first.Mods);
            var s = ModCatalog.WaitForSnapshot();
            Assert.Single(s.Mods);
            Assert.Same(s, ModCatalog.Snapshot(TimeSpan.FromMinutes(1)));  // fresh: no new read
            Assert.Equal(1, reads);
        }
        finally
        {
            ModCatalog.Sources = ModCatalog.DefaultSources;
            ModCatalog.SetForTests(null);
        }
    }

    [Fact]
    public void Summary_lists_every_engine_and_mod()
    {
        Profile([]);
        Write("emf.json", new { format = 1, updatedUnixMs = StartMs + 1, mods = new[] { new { id = "a", name = "Alpha", version = "1.0", status = "failed", reason = "boom" } } });
        var text = ModCatalog.Summary(Read());
        Assert.Contains("EMF: 1 mod", text);
        Assert.Contains("  Alpha 1.0: failed (Boom)", text);
    }

    // ---- the screen's logic ----

    private static CatalogSnapshot Sample()
    {
        CatalogMod M(string engine, string id, string name, ModRunState st, bool missing = false) => new()
        {
            Engine = engine, Id = id, Name = name, State = st, Missing = missing, SearchText = $"{name} {id} {ModCatalog.EngineName(engine)}".ToLowerInvariant(),
        };
        var mods = new[]
        {
            M("kiln", "frost-edge", "Frost Edge", ModRunState.Loaded), M("kiln", "labels", "Storage Labels", ModRunState.Failed),
            M("emf", "map-notes", "Map Notes", ModRunState.Loaded), M("emf", "fishing", "Fishing Plus", ModRunState.Disabled),
            M("eml", "lost", "Lost", ModRunState.Failed, missing: true),
        };
        return new CatalogSnapshot
        {
            Mods = mods, Version = 1,
            Engines =
            [
                new CatalogEngine { Kind = "kiln", Name = "Kiln", Count = 2, Problems = 1 },
                new CatalogEngine { Kind = "emf", Name = "EMF", Count = 2 },
                new CatalogEngine { Kind = "eml", Name = "EML", Count = 1, Problems = 1 },
            ],
        };
    }

    [Fact]
    public void The_attention_row_shows_only_when_something_is_wrong()
    {
        var rows = ModManagerLogic.EngineRows(Sample(), kilnSettings: true);
        Assert.Equal(["All mods", "Needs attention", "Kiln", "EMF", "EML", "Kiln settings"], rows.Select(r => r.Label));
        Assert.Equal("2", rows[1].Count);
        var fine = Sample() with { Mods = Sample().Mods.Where(m => !m.NeedsAttention).ToArray() };
        Assert.DoesNotContain(ModManagerLogic.EngineRows(fine, false), r => r.Filter.IsAttention);
        Assert.Equal(EngineFilter.All, ModManagerLogic.Valid(fine, EngineFilter.Attention, false));
        Assert.Equal(EngineFilter.All, ModManagerLogic.Valid(fine, new EngineFilter("ember"), false));
        Assert.Equal(EngineFilter.All, ModManagerLogic.Valid(fine, EngineFilter.Settings, false));
        Assert.Equal(new EngineFilter("emf"), ModManagerLogic.Valid(fine, new EngineFilter("emf"), false));
    }

    [Fact]
    public void Filters_list_their_mods_and_show_the_engine_only_when_mixed()
    {
        var s = Sample();
        Assert.Equal(5, ModManagerLogic.Rows(s, EngineFilter.All, "").Count);
        Assert.Equal(["Storage Labels", "Lost"], ModManagerLogic.Rows(s, EngineFilter.Attention, "").Select(r => r.Mod.Name));
        var emf = ModManagerLogic.Rows(s, new EngineFilter("emf"), "");
        Assert.Equal(["Map Notes", "Fishing Plus"], emf.Select(r => r.Mod.Name));
        Assert.DoesNotContain("EMF", emf[0].Sub);
        Assert.StartsWith("EMF", ModManagerLogic.Rows(s, EngineFilter.All, "").Single(r => r.Mod.Id == "map-notes").Sub);
        Assert.Empty(ModManagerLogic.Rows(s, EngineFilter.Settings, ""));
    }

    [Fact]
    public void Search_matches_every_word_in_name_id_or_engine()
    {
        var s = Sample();
        Assert.Equal(["Map Notes"], ModManagerLogic.Rows(s, EngineFilter.All, "map").Select(r => r.Mod.Name));
        Assert.Equal(["Map Notes"], ModManagerLogic.Rows(s, EngineFilter.All, "  NOTES  emf ").Select(r => r.Mod.Name));
        Assert.Equal(2, ModManagerLogic.Rows(s, EngineFilter.All, "kiln").Count);
        Assert.Equal(["frost-edge"], ModManagerLogic.Rows(s, EngineFilter.All, "frost-ed").Select(r => r.Mod.Id));
        Assert.Empty(ModManagerLogic.Rows(s, EngineFilter.All, "zzz"));
        Assert.Empty(ModManagerLogic.Rows(s, new EngineFilter("emf"), "frost"));
    }

    [Fact]
    public void The_selection_is_kept_while_it_is_listed()
    {
        var rows = ModManagerLogic.Rows(Sample(), EngineFilter.All, "");
        Assert.Equal("emf/fishing", ModManagerLogic.Select(rows, "emf/fishing"));
        Assert.Equal("kiln/frost-edge", ModManagerLogic.Select(rows, "emf/gone"));
        Assert.Equal("kiln/frost-edge", ModManagerLogic.Select(rows, null));
        Assert.Null(ModManagerLogic.Select([], "kiln/frost-edge"));
    }

    [Fact]
    public void Sections_wrap_both_ways()
    {
        Assert.Equal(ModManagerSection.Network, ModManagerLogic.Step(ModManagerSection.Mods, 1));
        Assert.Equal(ModManagerSection.Overlay, ModManagerLogic.Step(ModManagerSection.Mods, -1));
        Assert.Equal(ModManagerSection.Mods, ModManagerLogic.Step(ModManagerSection.Overlay, 1));
    }

    [Fact]
    public void The_page_recomputes_only_when_something_changed()
    {
        var page = new ModManagerPage { Filter = EngineFilter.Attention, Selected = "emf/gone" };
        var s = Sample();
        page.Update(s, false);
        var rows = page.Rows;
        Assert.Equal("kiln/labels", page.Selected);  // the first problem
        page.Update(s, false);
        Assert.Same(rows, page.Rows);  // same snapshot, filter and search: the same list (no work per frame)
        page.Search = "lost";
        page.Update(s, false);
        Assert.Equal("eml/lost", page.Selected);
        page.Update(s with { Version = 2, Mods = s.Mods.Where(m => !m.NeedsAttention).ToArray() }, false);
        Assert.Equal(EngineFilter.All, page.Filter);  // nothing wrong any more: back to all mods
    }
}

/// <summary>Kiln.Ui's view of the kiln.ui v3 table matches native/services/ui/ui.cpp's static_asserts.</summary>
public sealed unsafe class UiTableV3Tests
{
    [Fact]
    public void The_v3_slots_and_structs_have_the_native_layout()
    {
        Assert.Equal(sizeof(UiNative.Table2) + 20 * sizeof(void*), sizeof(UiNative.Table3));
        Assert.Equal(60, sizeof(UiNative.Cursor));
        Assert.Equal(28, sizeof(UiNative.Item));
        var t = default(UiNative.Table3);
        var push = (byte*)&t.PushFontSize - (byte*)&t;
        var wheelActive = (byte*)&t.Base.WheelActive - (byte*)&t;
        Assert.Equal(wheelActive + sizeof(void*), push);
    }
}
