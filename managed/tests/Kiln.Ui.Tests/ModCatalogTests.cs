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

    [Fact]
    public void Inactive_shroudtopia_names_its_settings_and_reported_name()
    {
        Write("shroudtopia.json", new
        {
            updatedUnixMs = StartMs + 60_000,
            mods = new[] { new { id = "library-id", name = "Reported Name.With Dot", status = "inactive", reason = "" } },
        });
        var row = Assert.Single(Read().Mods.Where(m => m.Engine == "shroudtopia"));
        Assert.Equal(ModRunState.Disabled, row.State);
        Assert.Equal("Switched off in Shroudtopia's settings.", row.Reason);
        Assert.Equal("In shroudtopia/shroudtopia.json in the profile's config folder, set active to true in the mods entry named \"Reported Name.With Dot\" (the name in kiln.log's Registered mod line). Save the file; Shroudtopia reads it again while the game runs.", row.Help);
    }

    // x-01 task 9b: a Shroudtopia mod that is switched on and never sets its own `active` flag read "inactive",
    // "Switched off in Shroudtopia's settings.", which sent the player to a setting that was already on.
    [Fact]
    public void A_shroudtopia_mod_that_does_not_report_itself_active_is_waiting_with_the_plugins_reason()
    {
        Write("shroudtopia.json", new
        {
            updatedUnixMs = StartMs + 60_000,
            mods = new[] { new { id = "item-skill-text", name = "item-skill-text", status = "activating",
                reason = "switched on, but the mod does not report itself active: it leaves its own 'active' flag off after Activate" } },
        });
        var row = Assert.Single(Read().Mods.Where(m => m.Engine == "shroudtopia"));
        Assert.Equal(ModRunState.Waiting, row.State);
        Assert.Equal("Switched on, but the mod does not report itself active: it leaves its own 'active' flag off after Activate.", row.Reason);
        Assert.Contains("[shroudtopia:item-skill-text]", row.Help);
        Assert.DoesNotContain("set active to true", row.Help);
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
    [InlineData("activating", ModRunState.Waiting)]
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

    /// <summary>Player report (Nexus, 2026-11-11): Loot Leveler and Enemy Leveler (<c>side: "server"</c>) showed "Not
    /// loaded" and "Server only" in the main menu, read as "does not work in single-player". On a client Kiln.Host
    /// starts them with the player's own world (ModManager.StartHosting).</summary>
    [Fact]
    public void A_server_mod_on_a_client_waits_for_the_players_own_world()
    {
        Profile([]);
        var s = Read(
            new KilnModInput("loot-leveler", "Loot Leveler", "0.1.0", Dir("lib", "loot"), ModSide.Server, ModState.Skipped,
                "side 'server': loads while this client hosts a world", [], -1),
            new KilnModInput("enemy-leveler", "Enemy Leveler", "0.1.0", Dir("lib", "enemy"), ModSide.Server, ModState.Skipped,
                "side 'server': not loaded on a client (kiln.json mods.hosted is false)", [], -1),
            new KilnModInput("off", "Off", "1.0.0", Dir("lib", "off"), ModSide.Server, ModState.Skipped, "disabled by a .disabled file", [], -1));
        var loot = Mod(s, "kiln", "loot-leveler");
        Assert.Equal((ModRunState.Waiting, "Waiting"), (loot.State, loot.Chip));
        Assert.Equal("Starts with your own world, when you play alone or friends join you. It does not run in someone else's world.", loot.Reason);
        Assert.Null(loot.Help);
        var enemy = Mod(s, "kiln", "enemy-leveler");
        Assert.Equal(ModRunState.NotLoaded, enemy.State);
        Assert.Equal("Off in this game: its kiln.json turns off mods that start with your world (mods.hosted is false).", enemy.Reason);
        Assert.NotNull(enemy.Help);
        Assert.Equal((ModRunState.NotLoaded, "Disabled by a .disabled file"), (Mod(s, "kiln", "off").State, Mod(s, "kiln", "off").Reason));
        Assert.Equal(0, s.Problems);
        // the side chip names where it runs in a player's words: on a client a server mod runs in their own world
        Assert.Equal("Your world only", ModManagerLogic.SideText("server", "client"));
        Assert.Equal("Server only", ModManagerLogic.SideText("server", "server"));
        Assert.Equal("Game only", ModManagerLogic.SideText("client", "client"));
        Assert.Equal("Server only", ModManagerLogic.SideText("server", "client", "emf"));  // other engines decide for themselves
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

    /// <summary>kiln-dllmod (packages for Enshrouded ModLoader, docs/kiln/engines/dllmod.md): its mods are listed with the
    /// state of kiln/state/dllmod.json. Before a world of your own starts they wait for it; started, refused with the
    /// plugin's reason, switched off; a profile entry it has not reported yet is still a row.</summary>
    [Fact]
    public void Enshrouded_modloader_packages_show_their_state_from_dllmod_json()
    {
        var carry = Dir("Mods", "EnshroudedModLoader", "carryon");
        var unseen = Dir("Mods", "EnshroudedModLoader", "unseen");
        Profile(new() { ["dllmod"] = [new { id = "carryon", version = "0.1.50-dev", path = carry }, new { id = "unseen", version = "1", path = unseen }] });
        Write("dllmod.json", new
        {
            format = 1, pid = Pid, status = "waiting", reason = "the mods start when a world of your own starts", steppedAside = false,
            mods = new object[]
            {
                new { id = "carryon", package = "carryon/carryon.dll", name = "carryon", version = "0.1.50-dev", status = "waiting", reason = "", folder = carry },
                new { id = "lantern", package = "lantern/lantern.dll", name = "lantern", version = "0.1.29-dev", status = "started", reason = "" },
                new { id = "broken", package = "broken/broken.dll", name = "broken", version = "1", status = "rejected", reason = "broken.dll.mod: abi must be given once" },
                new { id = "off", package = "off/off.dll", name = "off", version = "1", status = "disabled", reason = "switched off in the profile" },
            },
        });
        var s = Read();
        var w = Mod(s, "dllmod", "carryon");
        Assert.Equal(ModRunState.Waiting, w.State);
        Assert.Contains("world of your own", w.Reason);
        Assert.False(w.NeedsAttention);
        Assert.Equal(ModRunState.Loaded, Mod(s, "dllmod", "lantern").State);
        var b = Mod(s, "dllmod", "broken");
        Assert.Equal(ModRunState.Failed, b.State);
        Assert.Equal("broken.dll.mod: abi must be given once", b.Reason);
        Assert.NotNull(b.Help);
        Assert.Equal(ModRunState.Disabled, Mod(s, "dllmod", "off").State);
        Assert.Equal(ModRunState.NotLoaded, Mod(s, "dllmod", "unseen").State);  // in the profile, not in the report
        var e = s.Engines.Single(e => e.Kind == "dllmod");
        Assert.Equal("Enshrouded ModLoader", e.Name);
        Assert.Contains("Enshrouded ModLoader", ModCatalog.EngineAbout("dllmod"));
        Assert.Equal(ModCatalog.EngineOrder.Length - 1, Array.IndexOf(ModCatalog.EngineOrder, "dllmod"));  // the launcher's order: last
    }

    [Fact]
    public void Enshrouded_modloader_steps_aside_and_refuses_another_build()
    {
        Profile([]);
        Write("dllmod.json", new
        {
            format = 1, pid = Pid, status = "stepped aside", steppedAside = true, steppedAsideReason = "Enshrouded ModLoader runs in this game: it is loaded (dinput8.dll)",
            mods = new object[]
            {
                new { id = "carryon", status = "own-loader", reason = "runs under Enshrouded ModLoader's own loader; switch it on in the world's Edit > MODS page" },
                new { id = "lib-copy", status = "not-loaded", reason = "Enshrouded ModLoader's own loader runs this start" },
            },
        });
        var s = Read();
        Assert.Equal(ModRunState.Loaded, Mod(s, "dllmod", "carryon").State);
        var nl = Mod(s, "dllmod", "lib-copy");
        Assert.Equal(ModRunState.Failed, nl.State);
        Assert.NotNull(nl.Help);
        Assert.StartsWith("Stepped aside", s.Engines.Single(e => e.Kind == "dllmod").Note);

        Write("dllmod.json", new
        {
            format = 1, pid = Pid, status = "unsupported", steppedAside = false,
            reason = "mods for Enshrouded ModLoader are made for client build 26430625 (enshrouded.exe 5d9ef2f1), and this game is 0123abcd",
            mods = new object[] { new { id = "carryon", status = "skipped", reason = "mods for Enshrouded ModLoader are made for client build 26430625" } },
        });
        s = Read();
        Assert.Equal(ModRunState.NotLoaded, Mod(s, "dllmod", "carryon").State);
        var e = s.Engines.Single(e => e.Kind == "dllmod");
        Assert.Equal(EngineHealth.Problem, e.Health);
        Assert.Contains("26430625", e.Note);
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
        // a readable, switched-on runtime package: only the missing runtime report keeps it from Loaded
        File.WriteAllText(Path.Combine(mods, "pack", "mod.json"), """{"id":"pack","name":"Pack","version":"1.0.0","capabilities":["runtime"]}""");
        File.WriteAllText(Path.Combine(mods, "pack", "extended.mod.json"), """{"schemaVersion":1,"enabled":true,"targets":["client"]}""");
        var handFolder = Dir("mods", "hand");
        File.WriteAllText(Path.Combine(handFolder, "mod.json"), """{"id":"hand","name":"Hand Made","version":"0.1.0"}""");
        File.WriteAllText(Path.Combine(handFolder, "extended.mod.json"), """{"schemaVersion":1,"enabled":false}""");
        Write("shroudforge.json", new { format = 1, mode = "hosted", reason = "", updated = StartMs / 1000 + 10, modsDir = mods });
        var s = Read();
        var pack = Mod(s, "shroudforge", "pack");
        Assert.Equal((ModRunState.Waiting, "unconfirmed"), (pack.State, pack.Word)); // host running is not per-mod proof
        Assert.Contains("no current per-mod runtime report", pack.Reason);
        Assert.Equal(ModRunState.Disabled, Mod(s, "shroudforge", "off").State);
        var hand = Mod(s, "shroudforge", "hand");
        Assert.Equal(("Hand Made", ModRunState.Disabled, "folder"), (hand.Name, hand.State, hand.Source));
        Assert.Equal("ShroudForge: runtime hosted by Kiln. Pick a mod to switch it and change its settings.", s.Engines.Single(e => e.Kind == "shroudforge").Note);

        Write("shroudforge.json", new { format = 1, mode = "not-installed", reason = "", updated = StartMs / 1000 + 10 });
        s = Read();
        Assert.Equal(ModRunState.Failed, Mod(s, "shroudforge", "pack").State);
        Assert.Equal(EngineHealth.Problem, s.Engines.Single(e => e.Kind == "shroudforge").Health);
    }

    // ---- inbox 84: in Copy path mode a ShroudForge profile entry names Kiln's second copy under kiln/profiles/, which
    // ShroudForge never reads; the row is the copy ShroudForge runs (in <game>/mods), and a setting written there sticks ----

    private const string UtHash = "kdh1:a516c609cfc5672f9ac9484f92f28b0280024635510c7c0630ec9f6b33fc2668";

    /// <summary>A ShroudForge package shaped like Unified EML Tweaks 2.1.1: mod.json, extended.mod.json with one setting,
    /// native-plugin.ini naming bin/ut_native.dll (ShroudForge's own DLL loader loads it), and Kiln's marker when placed.</summary>
    private string SfPackage(string folder, bool on = true, string? marker = null)
    {
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "mod.json"), """{"id":"unifiedemltweaks","name":"Unified EML Tweaks","version":"2.1.1","capabilities":["patch","export"]}""");
        File.WriteAllText(Path.Combine(folder, "extended.mod.json"), "{\n  \"schemaVersion\": 1,\n  \"enabled\": " + (on ? "true" : "false") +
            ",\n  \"settings\": {\n    \"stackSize\": { \"value\": false, \"label\": \"Bigger stacks\", \"control\": \"toggle\" }\n  }\n}\n");
        File.WriteAllText(Path.Combine(folder, "native-plugin.ini"), "[Plugin]\nEnabled=1\nDll=bin/ut_native.dll\n");
        File.WriteAllBytes(Path.Combine(folder, "bin", "ut_native.dll"), [0x4d, 0x5a]);
        if (marker is not null)
            File.WriteAllText(Path.Combine(folder, ".kiln-library.json"), JsonSerializer.Serialize(new { format = 1, kind = marker, id = "unifiedemltweaks", version = "2.1.1", hash = UtHash }));
        return folder;
    }

    /// <summary>The player's kiln/profile.json (pathMode copy, a ShroudForge entry with a path relative to kiln/, no
    /// "enabled" key), ShroudForge hosted with its mods folder in the game's mods folder.</summary>
    private void CopyModeProfile(string path)
    {
        var file = Path.Combine(Kiln, "profile.json");
        File.WriteAllText(file, $$"""
            {"configDir":"profiles/my-mods/config","dataDir":null,"dropins":true,"format":1,"generation":14,"id":"my-mods",
             "mods":{"emf":[],"kiln":[],"native":[],"plugin":[],
               "shroudforge":[{"hash":"{{UtHash}}","id":"unifiedemltweaks","path":{{JsonSerializer.Serialize(path)}},"version":"2.1.1"}]},
             "name":"My mods","pathMode":"copy","writer":"kiln-manage 0.2.0-dev"}
            """);
        Write("boot.json", new { format = 1, pid = Pid, side = "client", profile = new { mode = "active", id = "my-mods", name = "My mods", file, missing = Array.Empty<object>() }, plugins = Array.Empty<object>(), chain = Array.Empty<object>() });
        Write("shroudforge.json", new { pid = Pid, mode = "hosted", menu = "kiln", runtime = "running", modsDir = Path.Combine(_root, "mods"), startupPrepared = true });
    }

    private static string ExtendedOf(string folder) => File.ReadAllText(Path.Combine(folder, "extended.mod.json"));

    [Fact]
    public void In_copy_mode_a_shroudforge_profile_row_is_the_copy_shroudforge_runs_and_a_setting_lands_there()
    {
        var copy = SfPackage(Path.Combine(Kiln, "profiles", "my-mods", "shroudforge", "unifiedemltweaks"));
        var placed = SfPackage(Path.Combine(_root, "mods", "unifiedemltweaks"), marker: "shroudforge");
        CopyModeProfile("profiles/my-mods/shroudforge/unifiedemltweaks");
        var s = Read();
        var row = Mod(s, "shroudforge", "unifiedemltweaks");
        Assert.Equal(("profile", placed), (row.Source, row.Folder));  // the details' FOLDER line names the folder ShroudForge runs
        Assert.False(row.Missing);
        // listed once: ShroudForge's own DLL loader loads its native-plugin.ini, it is no XHL-Core plugin of its own
        Assert.DoesNotContain(s.Mods, m => m.Engine == "xhl");
        Assert.Single(s.Mods, m => m.Folder is { } f && ModCatalog.SamePath(f, placed));

        // the page writes the row's folder (ShroudForgeUi.Edit -> ShroudForgePackages.SetSetting)
        var before = ExtendedOf(copy);
        ShroudForgePackages.SetSetting(row.Folder!, "stackSize", System.Text.Json.Nodes.JsonValue.Create(true));
        Assert.True(ShroudForgePackages.Read(placed).Values["stackSize"]!.GetValue<bool>());
        Assert.Equal(before, ExtendedOf(copy));  // Kiln's second copy under kiln/profiles/ is not touched
        // read again (the page reads the row's folder once a second): the value it shows is the one ShroudForge reads
        Assert.True(ShroudForgePackages.Read(Mod(Read(), "shroudforge", "unifiedemltweaks").Folder!).Values["stackSize"]!.GetValue<bool>());
    }

    [Fact]
    public void In_copy_mode_without_kilns_copy_in_the_mods_folder_the_row_keeps_the_profile_path()
    {
        var copy = SfPackage(Path.Combine(Kiln, "profiles", "my-mods", "shroudforge", "unifiedemltweaks"));
        // another kind's copy of that name (Kiln's marker says xhl): not this entry's
        SfPackage(Path.Combine(_root, "mods", "unifiedemltweaks"), marker: "xhl");
        CopyModeProfile("profiles/my-mods/shroudforge/unifiedemltweaks");
        Assert.Equal(copy, Mod(Read(), "shroudforge", "unifiedemltweaks").Folder);
        // no folder of that name at all
        Directory.Delete(Path.Combine(_root, "mods", "unifiedemltweaks"), true);
        Assert.Equal(copy, Mod(Read(), "shroudforge", "unifiedemltweaks").Folder);
        // the player's own folder of the same package (no marker), as the launcher's settings editor finds it
        var own = SfPackage(Path.Combine(_root, "mods", "unifiedemltweaks"));
        Assert.Equal(own, Mod(Read(), "shroudforge", "unifiedemltweaks").Folder);
    }

    [Fact]
    public void In_normal_path_mode_a_shroudforge_profile_row_is_unchanged()
    {
        var placed = SfPackage(Path.Combine(_root, "mods", "unifiedemltweaks"), marker: "shroudforge");
        Profile(new() { ["shroudforge"] = [new { id = "unifiedemltweaks", version = "2.1.1", hash = UtHash, path = placed }] });
        Write("shroudforge.json", new { pid = Pid, mode = "hosted", menu = "kiln", runtime = "running", modsDir = Path.Combine(_root, "mods"), startupPrepared = true });
        var s = Read();
        var row = Mod(s, "shroudforge", "unifiedemltweaks");
        Assert.Equal(("profile", placed), (row.Source, row.Folder));
        Assert.DoesNotContain(s.Mods, m => m.Engine == "xhl");
        ShroudForgePackages.SetSetting(row.Folder!, "stackSize", System.Text.Json.Nodes.JsonValue.Create(true));
        Assert.True(ShroudForgePackages.Read(placed).Values["stackSize"]!.GetValue<bool>());
    }

    [Fact]
    public void A_shroudforge_package_with_a_native_plugin_ini_is_listed_once_and_a_real_xhl_plugin_still_shows()
    {
        Profile([]);
        Write("shroudforge.json", new { pid = Pid, mode = "hosted", menu = "kiln", runtime = "running", modsDir = Path.Combine(_root, "mods"), startupPrepared = true });
        // a ShroudForge package the player put there (no marker), with its own DLL loader file
        var sf = SfPackage(Path.Combine(_root, "mods", "unifiedemltweaks"));
        // an XHL-Core plugin: an EML mod with native-plugin.ini, no ShroudForge file of its own
        var xhl = Dir("mods", "XHL-Auto-Loot");
        File.WriteAllText(Path.Combine(xhl, "mod.json"), """{"id":"XHL-Auto-Loot","name":"XHL Auto Loot","version":"1.0.0"}""");
        File.WriteAllText(Path.Combine(xhl, "native-plugin.ini"), "[Plugin]\nEnabled=1\nDll=auto-loot.dll\n");
        // an XHL-Core plugin that ShroudForge runs as an EML package (its switch: launcher EML): still an XHL-Core plugin
        var switched = Dir("mods", "XHL-Stacks");
        File.WriteAllText(Path.Combine(switched, "mod.json"), """{"id":"XHL-Stacks","name":"XHL Stacks","version":"1.0.0"}""");
        File.WriteAllText(Path.Combine(switched, "native-plugin.ini"), "[Plugin]\nEnabled=1\nDll=stacks.dll\n");
        File.WriteAllText(Path.Combine(switched, "extended.mod.json"), """{"schemaVersion":1,"enabled":true,"launcher":"EML"}""");
        var s = Read();
        Assert.Equal("shroudforge", Assert.Single(s.Mods, m => m.Folder is { } f && ModCatalog.SamePath(f, sf)).Engine);
        Assert.Contains(s.Mods, m => m.Engine == "xhl" && m.Folder is { } f && ModCatalog.SamePath(f, xhl));
        Assert.Contains(s.Mods, m => m.Engine == "xhl" && m.Folder is { } f && ModCatalog.SamePath(f, switched));
        // ShroudForge configured to another mods folder: the game's mods folder is XHL-Core's alone again, and its plugins
        // still show; this package is no XHL-Core plugin all the same (XHL-Core refuses Dll=bin/ut_native.dll, a path)
        Write("shroudforge.json", new { pid = Pid, mode = "hosted", menu = "kiln", runtime = "running", modsDir = Dir("elsewhere") });
        Directory.CreateDirectory(Path.Combine(_root, "shroudforge", "config"));
        File.WriteAllText(Path.Combine(_root, "shroudforge", "config", "modloader-config.json"), JsonSerializer.Serialize(new { paths = new { mods = Dir("elsewhere") } }));
        s = Read();
        Assert.Contains(s.Mods, m => m.Engine == "xhl" && m.Folder is { } f && ModCatalog.SamePath(f, xhl));
        Assert.DoesNotContain(s.Mods, m => m.Engine == "xhl" && m.Folder is { } f && ModCatalog.SamePath(f, sf));
    }

    /// <summary>A minimal PE32+ DLL file with these exports (the shape of kiln-manage's <c>pe::build_test_dll</c>).</summary>
    private static byte[] TestDll(params string[] exports)
    {
        var b = new byte[0x200];
        void U16(byte[] a, int at, ushort v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(at), v);
        void U32(byte[] a, int at, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(at), v);
        b[0] = (byte)'M'; b[1] = (byte)'Z'; U32(b, 0x3c, 0x80); U32(b, 0x80, 0x4550);
        const int coff = 0x84, opt = coff + 20;
        U16(b, coff, 0x8664); U16(b, coff + 2, 1); U16(b, coff + 16, 240); U16(b, coff + 18, 0x2022);
        U16(b, opt, 0x20b); U32(b, opt + 108, 16);
        const uint va = 0x1000;
        var n = (uint)exports.Length;
        uint funcs = va + 40, names = funcs + 4 * n, ords = names + 4 * n, strings = ords + 2 * n;
        var str = new List<byte>(System.Text.Encoding.ASCII.GetBytes("plugin.dll\0"));
        var dllName = strings; strings += (uint)str.Count;
        var nameRvas = new List<uint>();
        foreach (var e in exports) { nameRvas.Add(strings); str.AddRange(System.Text.Encoding.ASCII.GetBytes(e + "\0")); strings += (uint)e.Length + 1; }
        var head = new byte[40]; U32(head, 12, dllName); U32(head, 16, 1); U32(head, 20, n); U32(head, 24, n); U32(head, 28, funcs); U32(head, 32, names); U32(head, 36, ords);
        var data = new List<byte>(head);
        for (var i = 0; i < n; i++) data.AddRange(BitConverter.GetBytes(0x2000u));
        foreach (var r in nameRvas) data.AddRange(BitConverter.GetBytes(r));
        for (var i = 0; i < n; i++) data.AddRange(BitConverter.GetBytes((ushort)i));
        data.AddRange(str);
        var size = (uint)data.Count; var raw = (size + 0x1ff) / 0x200 * 0x200;
        while (data.Count < raw) data.Add(0);
        var sec = opt + 240;
        System.Text.Encoding.ASCII.GetBytes(".edata").CopyTo(b, sec); U32(b, sec + 8, size); U32(b, sec + 12, va); U32(b, sec + 16, raw); U32(b, sec + 20, 0x200); U32(b, sec + 36, 0x40000040);
        U32(b, opt + 112, va); U32(b, opt + 116, size);
        return [.. b, .. data];
    }

    /// <summary>inbox 92 and Smart Quick Stack: <c>native-plugin.ini</c> is ShroudForge's file too. A folder XHL-Core would not
    /// run (a Dll value with a path, a DLL without XHL-Core's exports) is no XHL row; XHL's own plugins still are.</summary>
    [Fact]
    public void Only_a_folder_xhl_core_would_run_is_listed_as_an_xhl_plugin()
    {
        Profile([]);
        var path = Dir("mods", "smart-quick-stack");
        File.WriteAllText(Path.Combine(path, "mod.json"), """{"id":"smart-quick-stack","name":"Smart Quick Stack","version":"0.16.1-beta"}""");
        File.WriteAllText(Path.Combine(path, "native-plugin.ini"), "[Plugin]\r\nEnabled=1\r\nDll=bin\\sqs.dll\r\n");
        Directory.CreateDirectory(Path.Combine(path, "bin"));
        File.WriteAllBytes(Path.Combine(path, "bin", "sqs.dll"), TestDll("DllMain"));
        var plain = Dir("mods", "plain-dll-mod");
        File.WriteAllText(Path.Combine(plain, "native-plugin.ini"), "[Plugin]\nEnabled=1\nDll=PlainMod.dll\n");
        File.WriteAllBytes(Path.Combine(plain, "PlainMod.dll"), TestDll("DllMain", "Init"));
        var json = Dir("mods", "XHL-Remote-Chest");
        File.WriteAllText(Path.Combine(json, "native-plugin.ini"), "[Plugin]\nDll=XHL-Remote-Chest.dll\n");
        File.WriteAllText(Path.Combine(json, "xhl-plugin.json"), "{}");
        File.WriteAllBytes(Path.Combine(json, "XHL-Remote-Chest.dll"), TestDll("DllMain"));
        var abi = Dir("mods", "XHL-Free-Climb");
        File.WriteAllText(Path.Combine(abi, "native-plugin.ini"), "[Plugin]\nEnabled=1\nDll=XHL-Free-Climb.dll\n[Climbing]\nspeed=2\n");
        File.WriteAllBytes(Path.Combine(abi, "XHL-Free-Climb.dll"), TestDll("XhlNativePluginQuery", "XhlNativePluginStart", "XhlNativePluginRequestStop"));
        Assert.Equal(["XhlNativePluginQuery", "XhlNativePluginStart", "XhlNativePluginRequestStop"], ModCatalog.PeExports(Path.Combine(abi, "XHL-Free-Climb.dll")));
        var xhlRows = Read().Mods.Where(m => m.Engine == "xhl").Select(m => LeafName(m.Folder!)).Order().ToArray();
        Assert.Equal(["XHL-Free-Climb", "XHL-Remote-Chest"], xhlRows);
    }

    private static string LeafName(string p) => Path.GetFileName(p.TrimEnd('/', '\\'));

    [Fact]
    public void Shroudforge_reports_real_activation_errors_side_and_switches_not_just_the_host()
    {
        Profile([]);
        foreach (var id in new[] { "active", "failed", "blocked", "disabled", "server", "inactive", "patch" })
        {
            var folder = Dir("mods", id);
            File.WriteAllText(Path.Combine(folder, "mod.json"), JsonSerializer.Serialize(new { id, name = "Name " + id, version = "1.0.0", capabilities = new[] { id == "patch" ? "patch" : "runtime" } }));
            File.WriteAllText(Path.Combine(folder, "extended.mod.json"), JsonSerializer.Serialize(new { schemaVersion = 1, enabled = id != "disabled", targets = new[] { id == "server" ? "server" : "client" } }));
        }
        Write("shroudforge.json", new { pid = Pid, mode = "hosted", menu = "kiln", runtime = "running", modsDir = Path.Combine(_root, "mods") });
        var state = Path.Combine(Dir("shroudforge"), "state.json");
        void Runtime(int pid, bool running, long updatedAt, string effect = "write-confirmed") => File.WriteAllText(state, JsonSerializer.Serialize(new { runtime = new { pid, running, updatedAt, active = new[] { "active", "disabled", "server" }, errors = new Dictionary<string, string> { ["failed"] = "init.lua: broken", ["blocked"] = "dependency-blocked: other" }, effects = new Dictionary<string, object> { ["active"] = new { state = effect, detail = "effect diagnostic" } } } }));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Runtime(Pid, true, now);
        var s = Read();
        Assert.Equal(("Name active", ModRunState.Loaded), (Mod(s, "shroudforge", "active").Name, Mod(s, "shroudforge", "active").State));
        Assert.Equal(ModRunState.Failed, Mod(s, "shroudforge", "failed").State);
        Assert.Contains("init.lua: broken", Mod(s, "shroudforge", "failed").Reason);
        Assert.Equal(ModRunState.NotLoaded, Mod(s, "shroudforge", "blocked").State);
        Assert.Equal(ModRunState.Disabled, Mod(s, "shroudforge", "disabled").State);
        Assert.Equal("other side", Mod(s, "shroudforge", "server").Word);
        Assert.Equal("inactive", Mod(s, "shroudforge", "inactive").Word);
        Assert.Equal("startup", Mod(s, "shroudforge", "patch").Word);
        // the five states a mod reports (runtime.report_effect; mods/tests/sf-probe) and the four ShroudForge's runtime
        // sets: error (its native DLL failed to load) an error, write-confirmed, loaded and no-change plain, anything else
        // (pending or unknown) a warning. write-failed is the mod's own report of one change that did not apply: a mod
        // that still runs stays loaded, with that report as a warning (netjunk, Altar Cleanup: the whole mod showed red
        // after one refused voxel write it had handled, undo intact).
        foreach (var (effect, want, warn) in new[]
                 {
                     ("write-failed", ModRunState.Loaded, true), ("waiting", ModRunState.Waiting, false), ("no-target", ModRunState.Waiting, false),
                     ("no-change", ModRunState.Loaded, false), ("write-confirmed", ModRunState.Loaded, false),
                     ("loading", ModRunState.Waiting, false), ("loaded", ModRunState.Loaded, false), ("error", ModRunState.Failed, false),
                     ("restart-required", ModRunState.Waiting, false), ("something-new", ModRunState.Waiting, false),
                 })
        {
            Runtime(Pid, true, now, effect);
            var m = Mod(Read(), "shroudforge", "active");
            Assert.Equal((effect, want, warn), (effect, m.State, m.Warn));
            Assert.Contains("effect diagnostic", m.Reason);
            Assert.Equal(want == ModRunState.Failed, m.NeedsAttention);
            Assert.True(m.Active);  // ShroudForge runs it, whatever its effect says
            Assert.Equal(warn ? GameColors.Attention : GameColors.Of(m.State, m.Missing), GameColors.Of(m));
        }
        Assert.False(Mod(Read(), "shroudforge", "inactive").Active);
        foreach (var stale in new[] { (Pid + 1, true, now), (Pid, false, now), (Pid, true, now - 60) })
        {
            Runtime(stale.Item1, stale.Item2, stale.Item3);
            Assert.Equal(ModRunState.Waiting, Mod(Read(), "shroudforge", "active").State);
        }
    }

    /// <summary>A mod with no Lua part (capability "patch" only) never shows in ShroudForge's runtime report. Its proof is
    /// ShroudForge's own record of the startup game-data pass (state.json "assets"): the pass of this game start, with
    /// the status "applied", this side's target and this mod at this version in its list. A real client showed SF Unlock
    /// Blueprints as "Waiting" while that record listed it. A record from an earlier start, another target, another
    /// version or without the mod proves nothing and keeps the careful wording; a failed pass of this start is said.</summary>
    [Fact]
    public void A_startup_only_shroudforge_mod_is_applied_when_this_starts_asset_record_lists_it()
    {
        Profile([]);
        foreach (var (id, version) in new[] { ("patch", "1.0.2"), ("unlisted", "1.0.0"), ("updated", "2.0.0") })
        {
            var folder = Dir("mods", id);
            File.WriteAllText(Path.Combine(folder, "mod.json"), JsonSerializer.Serialize(new { id, name = "Name " + id, version, capabilities = new[] { "patch" } }));
            File.WriteAllText(Path.Combine(folder, "extended.mod.json"), JsonSerializer.Serialize(new { schemaVersion = 1, enabled = true, targets = new[] { "client" } }));
        }
        Write("shroudforge.json", new { pid = Pid, mode = "hosted", menu = "kiln", runtime = "running", modsDir = Path.Combine(_root, "mods") });
        var state = Path.Combine(Dir("shroudforge"), "state.json");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var listed = new object[] { new { id = "patch", version = "1.0.2" }, new { id = "updated", version = "1.9.0" } };
        void Put(object? assets, bool runtime = true)
        {
            var doc = new Dictionary<string, object?> { ["schemaVersion"] = 1 };
            if (runtime) doc["runtime"] = new { pid = Pid, running = true, updatedAt = now, active = Array.Empty<string>(), errors = new Dictionary<string, string>(), effects = new Dictionary<string, object>() };
            if (assets is not null) doc["assets"] = assets;
            File.WriteAllText(state, JsonSerializer.Serialize(doc));
        }

        Put(new { schemaVersion = 1, status = "applied", phase = "startup", target = "enshrouded", completedAt = now - 5, mods = listed });
        var s = Read();
        var patch = Mod(s, "shroudforge", "patch");
        Assert.Equal((ModRunState.Loaded, "applied"), (patch.State, patch.Word));
        Assert.Contains("this game start", patch.Reason);
        Assert.False(patch.Active);  // nothing of it runs: a change of its settings needs a new start
        Assert.False(patch.NeedsAttention);
        Assert.Equal((ModRunState.Waiting, "startup"), (Mod(s, "shroudforge", "unlisted").State, Mod(s, "shroudforge", "unlisted").Word));
        Assert.Equal((ModRunState.Waiting, "startup"), (Mod(s, "shroudforge", "updated").State, Mod(s, "shroudforge", "updated").Word));

        // the record alone is enough: ShroudForge's runtime report may be missing or stale
        Put(new { schemaVersion = 1, status = "applied", phase = "startup", target = "enshrouded", completedAt = now - 5, mods = listed }, runtime: false);
        Assert.Equal("applied", Mod(Read(), "shroudforge", "patch").Word);

        foreach (var notProof in new object[]
                 {
                     new { schemaVersion = 1, status = "applied", phase = "startup", target = "enshrouded", completedAt = new DateTimeOffset(Start).ToUnixTimeSeconds() - 60, mods = listed },  // an earlier start
                     new { schemaVersion = 1, status = "applied", phase = "startup", target = "enshrouded_server", completedAt = now - 5, mods = listed },  // the other side's files
                     new { schemaVersion = 1, status = "preparing", target = "enshrouded" },  // the pass is still running
                     new { schemaVersion = 1, status = "applied", phase = "startup", target = "enshrouded", completedAt = now + 3600, mods = listed },  // a clock that cannot be this start
                     new { schemaVersion = 1, status = "applied", phase = "startup", target = "enshrouded", completedAt = "soon", mods = listed },
                     new { schemaVersion = 1, status = "applied", phase = "startup", target = "enshrouded", completedAt = now - 5, mods = "patch" },
                 })
        {
            Put(notProof);
            var m = Mod(Read(), "shroudforge", "patch");
            Assert.Equal((ModRunState.Waiting, "startup"), (m.State, m.Word));
        }

        Put(new { schemaVersion = 1, status = "failed", phase = "startup", target = "enshrouded", completedAt = now - 5, error = "runtime.lifecycle unavailable in world-editor" });
        var failed = Mod(Read(), "shroudforge", "patch");
        Assert.Equal((ModRunState.Failed, "failed"), (failed.State, failed.Word));
        Assert.Contains("runtime.lifecycle unavailable in world-editor", failed.Reason);
        Assert.True(failed.NeedsAttention);

        // The usual start: ShroudForge finds its earlier pass still current for this start's mods and leaves the record
        // as it is, with the time of the start that wrote it (a real client, the second start with the same mods:
        // SF Unlock Blueprints stayed "Waiting"). What ties the record to this start is then the adapter's status of
        // this process: ShroudForge's startup preparation succeeded.
        var earlier = new { schemaVersion = 1, status = "applied", phase = "startup", target = "enshrouded", completedAt = new DateTimeOffset(Start).ToUnixTimeSeconds() - 600, mods = listed };
        Put(earlier);
        Write("shroudforge.json", new { pid = Pid, mode = "hosted", menu = "kiln", runtime = "running", modsDir = Path.Combine(_root, "mods"), startupPrepared = true });
        s = Read();
        patch = Mod(s, "shroudforge", "patch");
        Assert.Equal((ModRunState.Loaded, "applied"), (patch.State, patch.Word));
        Assert.Contains("still current", patch.Reason);
        Assert.Equal("startup", Mod(s, "shroudforge", "updated").Word);  // listed at another version: still no proof
        // the word of another process, or none, does not make an old record this start's
        Write("shroudforge.json", new { pid = Pid + 1, mode = "hosted", menu = "kiln", runtime = "running", modsDir = Path.Combine(_root, "mods"), startupPrepared = true });
        Assert.NotEqual("applied", Mod(Read(), "shroudforge", "patch").Word);
        Write("shroudforge.json", new { pid = Pid, mode = "hosted", menu = "kiln", runtime = "running", modsDir = Path.Combine(_root, "mods"), startupPrepared = false });
        Assert.Equal("startup", Mod(Read(), "shroudforge", "patch").Word);
        // a failed pass of an earlier start says nothing about this one
        Write("shroudforge.json", new { pid = Pid, mode = "hosted", menu = "kiln", runtime = "running", modsDir = Path.Combine(_root, "mods"), startupPrepared = true });
        Put(new { schemaVersion = 1, status = "failed", phase = "startup", target = "enshrouded", completedAt = new DateTimeOffset(Start).ToUnixTimeSeconds() - 600, error = "old" });
        Assert.Equal("startup", Mod(Read(), "shroudforge", "patch").Word);
        Write("shroudforge.json", new { pid = Pid, mode = "hosted", menu = "kiln", runtime = "running", modsDir = Path.Combine(_root, "mods") });

        // a process whose start time is unknown cannot tell this start's record from an older one
        Put(new { schemaVersion = 1, status = "applied", phase = "startup", target = "enshrouded", completedAt = now - 5, mods = listed });
        var unknownStart = ModCatalog.Read(new CatalogSources { KilnRoot = Kiln, Pid = Pid, Side = "client" });
        Assert.Equal("startup", Mod(unknownStart, "shroudforge", "patch").Word);
    }

    /// <summary>A real client (2026-11-11): Kiln hosted ShroudForge, kiln-eml stood aside and listed the profile's four EML
    /// mods as running under ShroudForge, and the menu showed them Loaded. ShroudForge runs a package of its mods folder
    /// only while its extended.mod.json says enabled: true; three had no such file and nothing applied them. While
    /// ShroudForge runs, an EML row in its folder says what ShroudForge's switch and its record of the startup pass say,
    /// whatever an older kiln-eml wrote; a player's own legacy EML folder is listed under ShroudForge with its switch.</summary>
    [Fact]
    public void Eml_mods_in_shroudforges_folder_run_only_while_its_switch_is_on()
    {
        var mods = Dir("mods");
        void Package(string folder, string id, string caps, string? ext, bool kilns)
        {
            var dir = Dir("mods", folder);
            File.WriteAllText(Path.Combine(dir, "mod.json"), $$"""{"id":"{{id}}","name":"Name {{id}}","version":"1.0.0","capabilities":[{{caps}}]}""");
            File.WriteAllText(Path.Combine(Dir("mods", folder, "src"), "mod.lua"), "return {}");
            if (ext is not null) File.WriteAllText(Path.Combine(dir, "extended.mod.json"), ext);
            if (kilns) File.WriteAllText(Path.Combine(dir, ".kiln-library.json"), $$"""{"format":1,"kind":"eml","id":"{{id}}","version":"1.0.0","hash":"h"}""");
        }
        Package("fcu-render-observer", "fcu-render-observer", "\"patch\",\"runtime-register-dll\"", """{"schemaVersion":1,"enabled":true,"targets":["client"],"launcher":"EML","settings":{}}""", true);
        Package("more-gliders", "more-gliders", "\"patch\"", null, true);
        Package("aldrich-bare-body", "aldrich-bare-body", "\"patch\"", """{"schemaVersion":1,"enabled":false,"targets":["client","server"],"launcher":"EML","settings":{}}""", true);
        Package("Hand Made", "hand-made", "\"patch\"", null, false);  // the player's own folder, not in the profile
        var ids = new[] { "fcu-render-observer", "more-gliders", "aldrich-bare-body" };
        Profile(new() { ["eml"] = ids.Select(id => (object)new { id, version = "1.0.0", path = Path.Combine(_root, "library", "eml", id) }).ToArray() });
        // what kiln-eml wrote before this fix: every mod own-loader, "runs under ShroudForge"
        Write("eml.json", new
        {
            format = 1, pid = Pid, phase = "aside", aside = "ShroudForge owns startup asset patching and runs EML packages in its mods folder",
            mods = ids.Select(id => new { id, source = "profile", path = Path.Combine(mods, id), state = "own-loader", reason = $"runs under ShroudForge (hosted by Kiln) (Kiln's copy at {Path.Combine(mods, id)})" }).ToArray(),
        });
        Write("shroudforge.json", new { pid = Pid, mode = "hosted", menu = "kiln", runtime = "running", modsDir = mods, startupPrepared = true });
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        File.WriteAllText(Path.Combine(Dir("shroudforge"), "state.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            assets = new { schemaVersion = 1, status = "applied", phase = "startup", target = "enshrouded", completedAt = now - 5, mods = new[] { new { id = "fcu-render-observer", version = "1.0.0" } } },
        }));
        var s = Read();

        var fcu = Mod(s, "eml", "fcu-render-observer");
        Assert.Equal((ModRunState.Loaded, "applied"), (fcu.State, fcu.Word));
        foreach (var id in new[] { "more-gliders", "aldrich-bare-body" })
        {
            var m = Mod(s, "eml", id);
            Assert.Equal(ModRunState.Disabled, m.State);
            Assert.DoesNotContain("runs under", m.Reason);
            Assert.Contains("Kiln launcher", m.Help);
        }
        Assert.Contains("no extended.mod.json", Mod(s, "eml", "more-gliders").Reason);
        Assert.Contains("enabled: false", Mod(s, "eml", "aldrich-bare-body").Reason);
        var eml = s.Engines.Single(e => e.Kind == "eml");
        Assert.Equal(EngineHealth.Problem, eml.Health);
        Assert.Contains("2 of the profile's EML mods are switched off in ShroudForge", eml.Note);
        // Kiln's copies are the EML rows; the player's own legacy folder is ShroudForge's to switch, and says so
        Assert.DoesNotContain(s.Mods, m => m.Engine == "shroudforge" && ids.Contains(m.Id));
        var hand = Mod(s, "shroudforge", "hand-made");
        Assert.Equal((ModRunState.Disabled, "folder"), (hand.State, hand.Source));
        Assert.Equal("You put this mod into ShroudForge's folder yourself, and ShroudForge has it switched off.", hand.Reason);
        Assert.StartsWith("Switch it on below, or remove the folder and add the mod with Add mods in the Kiln launcher.", hand.Help);

        // with ShroudForge's report of this start gone, kiln-eml's own word still says it does not run
        File.Delete(Path.Combine(State, "shroudforge.json"));
        Write("eml.json", new
        {
            format = 1, pid = Pid, phase = "aside", aside = "ShroudForge owns startup asset patching and runs EML packages in its mods folder",
            mods = new[] { new { id = "more-gliders", source = "profile", path = Path.Combine(mods, "more-gliders"), state = "off-in-shroudforge", reason = "ShroudForge (hosted by Kiln) runs this start, but ShroudForge has Kiln's copy switched off" } },
        });
        var off = Mod(Read(), "eml", "more-gliders");
        Assert.Equal(ModRunState.Disabled, off.State);
        Assert.DoesNotContain("Turn it on in the Kiln launcher (Profiles)", off.Help ?? "");
    }

    /// <summary>Player report (Nexus, 14 Nov 2026): an EML mod copied by hand into ShroudForge's mods folder, while Kiln
    /// hosts ShroudForge, stays off (no extended.mod.json), and the page's words read as "Kiln wants a ShroudForge file
    /// from an EML mod". Its row says the promised words, on the ShroudForge row (with the switch) and on the EML row of a
    /// profile mod that folder serves, and nothing about ShroudForge's files.</summary>
    [Fact]
    public void A_hand_copied_eml_mod_shroudforge_keeps_off_says_the_promised_words()
    {
        var mods = Dir("mods");
        foreach (var id in new[] { "hand-copied", "hand-profile" })
        {
            File.WriteAllText(Path.Combine(Dir("mods", id), "mod.json"), $$"""{"id":"{{id}}","name":"Name {{id}}","version":"1.0.0","capabilities":["patch"]}""");
            File.WriteAllText(Path.Combine(Dir("mods", id, "src"), "mod.lua"), "return {}");
        }
        Profile(new() { ["eml"] = [new { id = "hand-profile", version = "1.0.0", path = Path.Combine(_root, "library", "eml", "hand-profile") }] });
        Write("eml.json", new
        {
            format = 1, pid = Pid, phase = "aside", aside = "ShroudForge owns startup asset patching and runs EML packages in its mods folder",
            mods = new[] { new { id = "hand-profile", source = "profile", path = Path.Combine(mods, "hand-profile"), state = "off-in-shroudforge", reason = "ShroudForge (hosted by Kiln) runs this start." } },
        });
        Write("shroudforge.json", new { pid = Pid, mode = "hosted", menu = "kiln", runtime = "running", modsDir = mods, startupPrepared = true });
        var s = Read();

        const string Promised = "You put this mod into ShroudForge's folder yourself, and ShroudForge has it switched off.";
        var copied = Mod(s, "shroudforge", "hand-copied");
        Assert.Equal((ModRunState.Disabled, Promised), (copied.State, copied.Reason));
        Assert.StartsWith("Switch it on below, or remove the folder and add the mod with Add mods in the Kiln launcher.", copied.Help);
        var row = Mod(s, "eml", "hand-profile");
        Assert.Equal((ModRunState.Disabled, Promised), (row.State, row.Reason));
        Assert.Equal("Switch it on in Mods > ShroudForge (its own row there), or remove the folder and press Play in the Kiln launcher: the mod is in your profile, so Kiln then places its own copy, switched on.", row.Help);
        // its own row in Mods > ShroudForge carries the switch
        Assert.Equal(Promised, Mod(s, "shroudforge", "hand-profile").Reason);
        foreach (var m in new[] { copied, row })
            Assert.DoesNotContain("extended.mod.json", (m.Reason ?? "") + (m.Help ?? ""));
    }

    /// <summary>On Windows ShroudForge rewrites its mods folder at every start from a canonical path, <c>\\?\C:\…\mods</c>,
    /// and Kiln's game folder has no <c>\\?\</c> (Nexus, 14 Nov 2026: Auto Loot Plus and Endless Chests stayed off). The
    /// same folder in another spelling is the same folder, as Windows compares them: no verbatim prefix, one separator,
    /// no trailing one, case ignored (the callers compare without regard to case).</summary>
    [Theory]
    [InlineData(@"\\?\C:\Game\mods", @"C:\Game\mods")]
    [InlineData(@"c:\game\MODS\", @"C:\Game\mods")]
    [InlineData(@"\\?\c:\game\MODS\", @"C:/Game/mods")]
    [InlineData(@"//?/C:/Game/mods/", @"C:\Game\mods")]
    [InlineData(@"\\?\UNC\server\share\Game\mods", @"\\server\share\Game\mods")]
    [InlineData(@"\\?\unc\SERVER\share\game\mods\", @"//server/share/Game/mods")]
    [InlineData(@"\\?\C:\", @"C:\")]
    public void Folders_compare_as_windows_compares_them(string a, string b)
    {
        Assert.Equal(ModCatalog.NormPath(b), ModCatalog.NormPath(a), ignoreCase: true);
        Assert.True(ModCatalog.SamePath(a, b));
        Assert.True(ModCatalog.SamePath(b, a));
    }

    [Theory]
    [InlineData(@"\\?\C:\Game\mods", @"C:\Game\mods2")]
    [InlineData(@"\\?\C:\Game", @"C:\Game\mods")]
    [InlineData(@"\\?\UNC\server\share\mods", @"C:\server\share\mods")]
    [InlineData(@"\\?\UNC\server\share\mods", @"\\?\server\share\mods")]
    public void Other_folders_stay_other_folders(string a, string b)
    {
        Assert.NotEqual(ModCatalog.NormPath(b), ModCatalog.NormPath(a), StringComparer.OrdinalIgnoreCase);
        Assert.False(ModCatalog.SamePath(a, b));
    }

    /// <summary>The same report from Kiln's menu: ShroudForge's folder is <c>\\?\</c> and upper case with a trailing
    /// separator, Kiln's copy of a profile EML mod is in it without: the row still says ShroudForge has Kiln's copy off
    /// (no extended.mod.json), not that it runs, and the copy is no hand-installed ShroudForge row.</summary>
    [Fact]
    public void An_eml_copy_is_in_shroudforges_folder_whatever_its_spelling()
    {
        var mods = Dir("mods");
        var copy = Dir("mods", "auto-loot-plus");
        File.WriteAllText(Path.Combine(copy, "mod.json"), """{"id":"auto-loot-plus","name":"Auto Loot Plus","version":"1.0.0","capabilities":["patch"]}""");
        File.WriteAllText(Path.Combine(Dir("mods", "auto-loot-plus", "src"), "mod.lua"), "return {}");
        File.WriteAllText(Path.Combine(copy, ".kiln-library.json"), """{"format":1,"kind":"eml","id":"auto-loot-plus","version":"1.0.0","hash":"h"}""");
        Profile(new() { ["eml"] = [new { id = "auto-loot-plus", version = "1.0.0", path = Path.Combine(_root, "library", "eml", "auto-loot-plus") }] });
        Write("eml.json", new
        {
            format = 1, pid = Pid, phase = "aside", aside = "ShroudForge owns startup asset patching and runs EML packages in its mods folder",
            mods = new[] { new { id = "auto-loot-plus", source = "profile", path = copy, state = "own-loader", reason = $"runs under ShroudForge (hosted by Kiln) (Kiln's copy at {copy})" } },
        });
        Write("shroudforge.json", new { pid = Pid, mode = "hosted", menu = "kiln", runtime = "running", modsDir = @"\\?\" + mods.ToUpperInvariant() + @"\", startupPrepared = true });
        var s = Read();
        var m = Mod(s, "eml", "auto-loot-plus");
        Assert.Equal((ModRunState.Disabled, ModCatalog.OffInShroudForge), (m.State, m.Word));
        Assert.Contains("no extended.mod.json", m.Reason);
        Assert.Contains("Kiln launcher", m.Help);
        Assert.DoesNotContain(s.Mods, x => x.Engine == "shroudforge");
    }

    /// <summary>A copy Kiln's launcher placed in ShroudForge's folder (its marker says eml) that no EML row of this
    /// start names (kiln-eml did not report, or the profile no longer lists the mod) is listed under ShroudForge with
    /// its switch, but not as a folder the player put there: that is only the player's own folder.</summary>
    [Fact]
    public void A_copy_kiln_placed_in_shroudforges_folder_is_not_called_the_players_own()
    {
        var mods = Dir("mods");
        foreach (var (folder, kilns) in new[] { ("kiln-copy", true), ("own-folder", false) })
        {
            var dir = Dir("mods", folder);
            File.WriteAllText(Path.Combine(dir, "mod.json"), $$"""{"id":"{{folder}}","name":"Name {{folder}}","version":"1.0.0","capabilities":["patch"]}""");
            File.WriteAllText(Path.Combine(Dir("mods", folder, "src"), "mod.lua"), "return {}");
            if (kilns) File.WriteAllText(Path.Combine(dir, ".kiln-library.json"), $$"""{"format":1,"kind":"eml","id":"{{folder}}","version":"1.0.0","hash":"h"}""");
        }
        Profile(new() { ["eml"] = [new { id = "kiln-copy", version = "1.0.0", path = Path.Combine(_root, "library", "eml", "kiln-copy") }] });
        Write("shroudforge.json", new { pid = Pid, mode = "hosted", menu = "kiln", runtime = "running", modsDir = mods, startupPrepared = true });
        var s = Read();
        var copy = Mod(s, "shroudforge", "kiln-copy");
        Assert.Equal((ModRunState.Disabled, "folder"), (copy.State, copy.Source));
        Assert.DoesNotContain("yourself", copy.Reason);
        Assert.Contains("has not switched it on", copy.Reason);
        Assert.StartsWith("Switch it on below", copy.Help);
        Assert.Equal(ModCatalog.HandEmlOff, Mod(s, "shroudforge", "own-folder").Reason);  // the player's own folder: the promised words
    }

    // ---- a ShroudForge mod the profile or kiln-eml calls EML (HTR, Discord 2026-11-27: Mineral Production Boxes, Nexus 347) ----

    /// <summary>Nexus 347's extended.mod.json: ShroudForge's EML stamp (<c>"launcher": "EML"</c>) with its own settings in
    /// it, the whitelist ShroudForge's window shows.</summary>
    private const string WhitelistExt = """
        {"schemaVersion":1,"enabled":true,"targets":["client","server"],"launcher":"EML","settings":{
          "whitelist":{"value":["Sulfur","Coal"],"control":"multiselect","label":"Whitelist","options":{"Sulfur":"Sulfur","Coal":"Coal","Iron":"Iron Ore"}},
          "interval":{"value":1800,"min":30,"max":7200,"label":"Refill interval (s)","applyAt":"live"}}}
        """;

    /// <summary>What ShroudForge's ensure_eml_extension writes into an EML package (and Kiln into its EML copies).</summary>
    private const string StampExt = """{"$schema":"https://bonsaibauer.github.io/shroudforge/schemas/extended.mod.schema.json","schemaVersion":1,"enabled":true,"targets":["client","server"],"launcher":"EML","settings":{}}""";

    /// <summary>A package folder <c>mods/&lt;id&gt;</c>: mod.json, src/mod.lua, the given extended.mod.json (none when
    /// null) and Kiln's EML marker when <paramref name="kilns"/>.</summary>
    private string EmlPackage(string id, string? ext, bool kilns = true, string caps = "\"patch\",\"runtime\"", string parent = "mods")
    {
        var dir = Dir(parent, id);
        File.WriteAllText(Path.Combine(dir, "mod.json"), $$"""{"id":"{{id}}","name":"Name {{id}}","version":"0.3.1","capabilities":[{{caps}}]}""");
        File.WriteAllText(Path.Combine(Dir(parent, id, "src"), "mod.lua"), "return {}");
        if (ext is not null) File.WriteAllText(Path.Combine(dir, "extended.mod.json"), ext);
        if (kilns) File.WriteAllText(Path.Combine(dir, ".kiln-library.json"), $$"""{"format":1,"kind":"eml","id":"{{id}}","version":"0.3.1","hash":"h"}""");
        return dir;
    }

    private void SfRuntimeActive(params string[] ids) =>
        File.WriteAllText(Path.Combine(Dir("shroudforge"), "state.json"), JsonSerializer.Serialize(new
        {
            runtime = new { pid = Pid, running = true, updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), active = ids },
        }));

    /// <summary>HTR's start: ShroudForge's own install runs next to Kiln (self-hosted), and Kiln's copy of a profile EML
    /// entry in ShroudForge's folder holds Nexus 347's settings. kiln-eml stood aside and said own-loader, which read as a
    /// bare "Loaded" under EML with no switch and no settings. The row is ShroudForge's now, with ShroudForge's runtime
    /// truth (its state.json says the mod is active) and the folder the page reads its switch and whitelist from. A real
    /// EML mod beside it (the stamp only), and a mod with both manifests that is ShroudForge's own, stay where they were.</summary>
    [Theory]
    [InlineData("self-hosted")]
    [InlineData("hosted")]
    public void A_shroudforge_mod_kiln_eml_calls_eml_is_listed_under_shroudforge_with_its_settings(string mode)
    {
        var mods = Dir("mods");
        var boxes = EmlPackage("mineral-production-boxes", WhitelistExt);
        var real = EmlPackage("auto-loot", StampExt, caps: "\"patch\"");
        var both = EmlPackage("climb-anywhere", """{"schemaVersion":1,"enabled":true,"targets":["client"],"settings":{"range":{"value":5,"min":1,"max":10}}}""", kilns: false);
        File.WriteAllText(Path.Combine(both, ".kiln-library.json"), """{"format":1,"kind":"shroudforge","id":"climb-anywhere","version":"0.3.1","hash":"h"}""");
        Profile(new()
        {
            ["eml"] = [new { id = "mineral-production-boxes", version = "0.3.1", path = boxes }, new { id = "auto-loot", version = "0.3.1", path = real }],
            ["shroudforge"] = [new { id = "climb-anywhere", version = "0.3.1", path = both }],
        });
        var who = mode == "self-hosted" ? "ShroudForge (its own loader)" : "ShroudForge (hosted by Kiln)";
        Write("eml.json", new
        {
            format = 1, pid = Pid, phase = "aside", aside = "ShroudForge owns startup asset patching and runs EML packages in its mods folder",
            mods = new[]
            {
                new { id = "mineral-production-boxes", source = "profile", path = boxes, state = "own-loader", reason = $"runs under {who}: its ShroudForge switch is on (Kiln's copy at {boxes})" },
                new { id = "auto-loot", source = "profile", path = real, state = "own-loader", reason = $"runs under {who}: its ShroudForge switch is on (Kiln's copy at {real})" },
            },
        });
        Write("shroudforge.json", new { pid = Pid, mode, menu = mode == "hosted" ? "kiln" : null, runtime = "running", modsDir = mods, startupPrepared = true });
        SfRuntimeActive("mineral-production-boxes", "auto-loot", "climb-anywhere");
        var s = Read();

        var row = Mod(s, "shroudforge", "mineral-production-boxes");
        Assert.Equal((ModRunState.Loaded, "active", true), (row.State, row.Word, row.Active));
        Assert.Equal(boxes, row.Folder);
        Assert.Equal("profile", row.Source);
        Assert.Contains("shroudforge", row.SearchText);
        Assert.DoesNotContain(s.Mods, m => m.Engine == "eml" && m.Id == "mineral-production-boxes");
        Assert.Single(s.Mods, m => m.Folder is { } f && ModCatalog.SamePath(f, boxes));  // one row, not a second folder row
        // what the page's switch and settings read (ShroudForgeUi.Draw draws every ShroudForge row with a folder)
        var p = ShroudForgePackages.Read(row.Folder!);
        Assert.True(p.Enabled);
        Assert.Contains(p.Settings, d => d.Key == "whitelist");
        Assert.Contains(p.Settings, d => d.Key == "interval");

        // a real EML mod: the stamp only, still an EML row (EmlUnderSf: its switch is on, no startup proof for its patch)
        var eml = Mod(s, "eml", "auto-loot");
        Assert.NotEqual(ModRunState.Failed, eml.State);
        Assert.DoesNotContain(s.Mods, m => m.Engine == "shroudforge" && m.Id == "auto-loot");
        // both manifests, ShroudForge's own: the ShroudForge row it always was
        Assert.Equal((ModRunState.Loaded, "active"), (Mod(s, "shroudforge", "climb-anywhere").State, Mod(s, "shroudforge", "climb-anywhere").Word));
        Assert.Equal(3, s.Mods.Count);
    }

    /// <summary>HTR (10:32 pm ET): the mod in both sections. The launcher puts an EML package that needs ShroudForge's
    /// runtime into the profile twice, under eml (off for kiln-eml) and under shroudforge, with the same folder (kiln-manage
    /// profile.rs, emlRuntime's compatibility entry), and kiln-eml reports the eml one as running under ShroudForge: an
    /// EML "Loaded" row next to the ShroudForge row. One ShroudForge row now, before a save loads (ShroudForge's runtime has
    /// not reported) and after (its state.json lists the mod active), for a package with ShroudForge settings and for one
    /// with ShroudForge's EML stamp only.</summary>
    [Fact]
    public void The_launchers_compatibility_entry_is_one_shroudforge_row_before_and_after_a_save_loads()
    {
        var mods = Dir("mods");
        var chests = EmlPackage("endless-chests", StampExt, caps: "\"runtime\",\"runtime-register-dll\"");
        var boxes = EmlPackage("mineral-production-boxes", WhitelistExt);
        Profile(new()
        {
            ["eml"] = [new { id = "endless-chests", version = "0.3.1", path = chests, enabled = false }, new { id = "mineral-production-boxes", version = "0.3.1", path = boxes, enabled = false }],
            ["shroudforge"] = [new { id = "endless-chests", version = "0.3.1", path = chests, enabled = true }, new { id = "mineral-production-boxes", version = "0.3.1", path = boxes, enabled = true }],
        });
        Write("eml.json", new
        {
            format = 1, pid = Pid, phase = "aside", aside = "ShroudForge owns startup asset patching and runs EML packages in its mods folder",
            mods = new[] { chests, boxes }.Select(f => new { id = Path.GetFileName(f), source = "profile", path = f, state = "own-loader",
                reason = $"runs under ShroudForge (hosted by Kiln): delegated to ShroudForge's runtime (Kiln's copy at {f})" }).ToArray(),
        });
        Write("shroudforge.json", new { pid = Pid, mode = "hosted", menu = "kiln", runtime = "running", modsDir = mods, startupPrepared = true });
        foreach (var loaded in new[] { false, true })
        {
            if (loaded) SfRuntimeActive("endless-chests", "mineral-production-boxes");
            var s = Read();
            foreach (var f in new[] { chests, boxes })
            {
                var row = Assert.Single(s.Mods, m => m.Folder is { } x && ModCatalog.SamePath(x, f));
                Assert.Equal(("shroudforge", loaded), (row.Engine, row.Active));
            }
            Assert.DoesNotContain(s.Mods, m => m.Engine == "eml");
        }
    }

    /// <summary>The compatibility entry as the launcher writes it (kiln-manage profile.rs render_routed): an EML entry's path
    /// is its Library folder (an EML package runs from the Library unless a DLL reads it in the game's mods folder), and the
    /// shroudforge entry is an exact copy of it. ShroudForge runs Kiln's EML copy in its mods folder, the folder kiln-eml's
    /// report names (kr-ynqz's kiln.log and eml.json: endless-chests). Review of 0.2.0-dev.31.24: the ShroudForge row read
    /// and wrote the Library folder (the copy's marker says eml, not shroudforge), so the one-row rule never matched kiln-eml's
    /// row: Nexus 347 was two ShroudForge rows, Endless Chests an EML row and a ShroudForge row "without a switch file". One
    /// ShroudForge row on Kiln's copy now, with the switch and settings ShroudForge reads; the Library is never its folder.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void The_compatibility_entry_of_a_library_package_is_one_shroudforge_row_on_kilns_copy(bool settings, bool loaded)
    {
        var mods = Dir("mods");
        var id = settings ? "mineral-production-boxes" : "endless-chests";
        var lib = Dir("library", "eml", id, "0.3.1");
        File.WriteAllText(Path.Combine(lib, "mod.json"), $$"""{"id":"{{id}}","name":"Name {{id}}","version":"0.3.1","capabilities":["patch","runtime"]}""");
        File.WriteAllText(Path.Combine(Dir("library", "eml", id, "0.3.1", "src"), "mod.lua"), "return {}");
        // the Library's copy as the mod ships it: Endless Chests has no extended.mod.json; Nexus 347's own, switched off here
        // so that a row reading the Library would say so
        if (settings) File.WriteAllText(Path.Combine(lib, "extended.mod.json"), WhitelistExt.Replace("\"enabled\":true", "\"enabled\":false"));
        var copy = EmlPackage(id, settings ? WhitelistExt : StampExt);  // mods/<id>: Kiln's EML copy, switched on
        Profile(new()
        {
            ["eml"] = [new { id, version = "0.3.1", path = lib, enabled = false }],
            ["shroudforge"] = [new { id, version = "0.3.1", path = lib, enabled = true }],
        });
        Write("eml.json", new
        {
            format = 1, pid = Pid, phase = "aside", aside = "ShroudForge owns startup asset patching and runs EML packages in its mods folder",
            mods = new[] { new { id, source = "profile", path = copy, state = "own-loader",
                reason = $"runs under ShroudForge (hosted by Kiln): its ShroudForge switch is on (Kiln's copy at {copy})" } },
        });
        Write("shroudforge.json", new { pid = Pid, mode = "hosted", menu = "kiln", runtime = "running", modsDir = mods, startupPrepared = true });
        if (loaded) SfRuntimeActive(id);
        var s = Read();
        var row = Assert.Single(s.Mods, m => m.Id == id);
        Assert.Equal(("shroudforge", copy, "profile"), (row.Engine, row.Folder, row.Source));
        Assert.Equal(loaded, row.Active);
        Assert.NotEqual(ModRunState.Disabled, row.State);
        Assert.DoesNotContain(s.Mods, m => m.Folder is { } f && ModCatalog.SamePath(f, lib));
        Assert.Equal(settings, ShroudForgePackages.Read(row.Folder!).Settings.Any(d => d.Key == "whitelist"));
    }

    /// <summary>Nexus 347's settings as the mod ships them (a Chinese label before the English one) and as HTR edited them
    /// in the game's mods folder (English only), with the options as ShroudForge's object map.</summary>
    private static string Labelled(bool chinese) => $$$"""
        {"schemaVersion":1,"enabled":true,"targets":["client","server"],"launcher":"EML","settings":{
          "mineralWhitelist":{"value":["sulfur","coal"],"label":"{{{(chinese ? "\u77ff\u77f3 | " : "")}}}Auto-mining whitelist","control":"multiselect",
            "options":{"copper":"{{{(chinese ? "\u94dc\u77ff / " : "")}}}Copper Ore","sulfur":"Sulfur","coal":"Coal"},"applyAt":"restart","restartRequired":true}},
         "groups":[{"label":"{{{(chinese ? "\u91c7\u77ff / " : "")}}}Mining whitelist","settings":["mineralWhitelist"]}]}
        """;

    /// <summary>HTR (Discord, 2026-11-27 ~11 pm ET): he replaced every Chinese label of the mod's extended.mod.json in the
    /// game's mods folder with English, and the Kiln menu still drew ????. The ShroudForge page draws its row's folder
    /// (ShroudForgeUi reads it again within a second of a change; no restart, no cached copy), and the compatibility
    /// entry's row was the Library copy, with the labels the mod shipped, never the folder ShroudForge runs: Kiln's copy,
    /// or the player's own folder of the package under another name, which kiln-eml names. The page reads the file the
    /// player edited now.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_page_reads_the_settings_file_shroudforge_runs_not_the_librarys(bool kilnsCopy)
    {
        const string id = "mineral-production-boxes";
        var mods = Dir("mods");
        var lib = Dir("library", "eml", id, "0.3.1");
        var run = kilnsCopy ? Dir("mods", id) : Dir("mods", "MineralProductionBoxes");  // the player's own folder: the archive's name
        foreach (var (folder, chinese) in new[] { (lib, true), (run, false) })
        {
            File.WriteAllText(Path.Combine(folder, "mod.json"), $$"""{"id":"{{id}}","name":"Mineral Production Boxes","version":"0.3.1","capabilities":["patch","runtime"]}""");
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(folder, "src")).FullName, "mod.lua"), "return {}");
            File.WriteAllText(Path.Combine(folder, "extended.mod.json"), Labelled(chinese));
        }
        if (kilnsCopy) File.WriteAllText(Path.Combine(run, ".kiln-library.json"), $$"""{"format":1,"kind":"eml","id":"{{id}}","version":"0.3.1","hash":"h"}""");
        Profile(new()
        {
            ["eml"] = [new { id, version = "0.3.1", path = lib, enabled = false }],
            ["shroudforge"] = [new { id, version = "0.3.1", path = lib, enabled = true }],
        });
        Write("eml.json", new
        {
            format = 1, pid = Pid, phase = "aside", aside = "ShroudForge owns startup asset patching and runs EML packages in its mods folder",
            mods = new[] { new { id, source = "profile", path = run, state = "own-loader",
                reason = $"runs under ShroudForge (its own loader): its ShroudForge switch is on ({(kilnsCopy ? "Kiln's copy" : "hand-installed mod")} at {run})" } },
        });
        Write("shroudforge.json", new { pid = Pid, mode = "self-hosted", runtime = "running", modsDir = mods, startupPrepared = true });
        var s = Read();
        var row = Assert.Single(s.Mods, m => m.Id == id);
        Assert.Equal(("shroudforge", run), (row.Engine, row.Folder));
        // what the page draws: ShroudForgeUi's read of the row's folder
        ShroudForgeUi.Load(row.Folder!);
        var p = ShroudForgeUi.Get(row.Folder!, out var error);
        Assert.Null(error);
        var whitelist = Assert.Single(p!.Settings, d => d.Key == "mineralWhitelist");
        Assert.Equal("Auto-mining whitelist", whitelist.Label);
        Assert.Contains(whitelist.Options, o => o.Label == "Copper Ore");
        Assert.Equal("Mining whitelist", Assert.Single(p.Groups).Label);
    }

    /// <summary>HTR's ShroudForge config names its mods folder with the extended-length prefix
    /// (<c>\\?\D:\...\Enshrouded\mods</c>), as ShroudForge writes it on Windows; kiln-shroudforge's status and kiln-eml may
    /// name it either way. The one-row rules (the compatibility entry, a moved package) and the page's read of the row's
    /// folder hold for every spelling.</summary>
    [Fact]
    public void Extended_length_paths_keep_one_row_and_a_readable_folder()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("skipped: the \\\\?\\ prefix is a Windows path form");
        var mods = Dir("mods");
        var prefixed = @"\\?\" + mods;
        Directory.CreateDirectory(Path.Combine(_root, "shroudforge", "config"));
        File.WriteAllText(Path.Combine(_root, "shroudforge", "config", "modloader-config.json"), JsonSerializer.Serialize(new { paths = new { mods = prefixed } }));
        var lib = Dir("library", "eml", "endless-chests", "0.3.1");
        File.WriteAllText(Path.Combine(lib, "mod.json"), """{"id":"endless-chests","name":"Endless Chests","version":"0.3.1","capabilities":["runtime"]}""");
        var chests = EmlPackage("endless-chests", StampExt, caps: "\"runtime\"");
        var boxes = EmlPackage("mineral-production-boxes", Labelled(false));
        Profile(new()
        {
            ["eml"] = [new { id = "endless-chests", version = "0.3.1", path = lib, enabled = false }, new { id = "mineral-production-boxes", version = "0.3.1", path = boxes }],
            ["shroudforge"] = [new { id = "endless-chests", version = "0.3.1", path = lib, enabled = true }],
        });
        Write("eml.json", new
        {
            format = 1, pid = Pid, phase = "aside", aside = "ShroudForge owns startup asset patching and runs EML packages in its mods folder",
            mods = new[]
            {
                new { id = "endless-chests", source = "profile", path = Path.Combine(prefixed, "endless-chests"), state = "own-loader", reason = "runs under ShroudForge (its own loader): its ShroudForge switch is on" },
                new { id = "mineral-production-boxes", source = "profile", path = boxes.ToUpperInvariant(), state = "own-loader", reason = "runs under ShroudForge (its own loader): its ShroudForge switch is on" },
            },
        });
        Write("shroudforge.json", new { pid = Pid, mode = "self-hosted", runtime = "running", modsDir = prefixed, startupPrepared = true });
        var s = Read();
        foreach (var (id, folder) in new[] { ("endless-chests", chests), ("mineral-production-boxes", boxes) })
        {
            var row = Assert.Single(s.Mods, m => m.Id == id);
            Assert.Equal("shroudforge", row.Engine);
            Assert.True(ModCatalog.SamePath(row.Folder!, folder), row.Folder);
            Assert.True(ShroudForgePackages.Read(row.Folder!).Enabled);
        }
        Assert.Equal("Auto-mining whitelist", Assert.Single(ShroudForgePackages.Read(Mod(s, "shroudforge", "mineral-production-boxes").Folder!).Settings).Label);
        Assert.DoesNotContain(s.Mods, m => m.Engine == "eml");
    }

    /// <summary>Where Kiln cannot read the moved mod's files while ShroudForge's own runtime runs it, the row says so and
    /// points to ShroudForge's own window; a hosted start (no such window) does not.</summary>
    [Theory]
    [InlineData("self-hosted", true)]
    [InlineData("hosted", false)]
    public void An_unreadable_shroudforge_mod_under_shroudforges_own_runtime_points_to_its_window(string mode, bool window)
    {
        var mods = Dir("mods");
        // schemaVersion 2: ShroudForgePackages refuses the file (the page cannot draw it); it is still ShroudForge's
        var boxes = EmlPackage("mineral-production-boxes", WhitelistExt.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"));
        Profile(new() { ["eml"] = [new { id = "mineral-production-boxes", version = "0.3.1", path = boxes }] });
        Write("eml.json", new
        {
            format = 1, pid = Pid, phase = "aside", aside = "ShroudForge owns startup asset patching",
            mods = new[] { new { id = "mineral-production-boxes", source = "profile", path = boxes, state = "own-loader", reason = "runs under ShroudForge" } },
        });
        Write("shroudforge.json", new { pid = Pid, mode, runtime = "running", modsDir = mods, startupPrepared = true });
        var row = Mod(Read(), "shroudforge", "mineral-production-boxes");
        Assert.Equal((ModRunState.Waiting, "unconfirmed"), (row.State, row.Word));
        if (window)
        {
            Assert.Contains("ShroudForge's own Modloader window", row.Help);
            Assert.Contains("shroudforge.exe", row.Help);
        }
        else Assert.True(row.Help is null || !row.Help.Contains("Modloader window", StringComparison.Ordinal), row.Help);
    }

    /// <summary>ShroudForge's preparation failed: the moved row fails like the profile's ShroudForge mods (ShroudForge's
    /// note); a moved row the profile has off stays off with kiln-eml's words.</summary>
    [Fact]
    public void A_moved_shroudforge_mod_fails_with_shroudforge_and_stays_off_when_the_profile_has_it_off()
    {
        var mods = Dir("mods");
        var boxes = EmlPackage("mineral-production-boxes", WhitelistExt);
        var off = EmlPackage("off-boxes", WhitelistExt.Replace("\"enabled\":true", "\"enabled\":false"));
        Profile(new()
        {
            ["eml"] = [new { id = "mineral-production-boxes", version = "0.3.1", path = boxes }, new { id = "off-boxes", version = "0.3.1", path = off, enabled = false }],
        });
        Write("eml.json", new
        {
            format = 1, pid = Pid, phase = "aside", aside = "ShroudForge owns startup asset patching",
            mods = new[]
            {
                new { id = "mineral-production-boxes", source = "profile", path = boxes, state = "own-loader", reason = "runs under ShroudForge (hosted by Kiln)" },
                new { id = "off-boxes", source = "profile", path = off, state = "disabled", reason = "off-boxes is off in the profile: Kiln switched its copy off" },
            },
        });
        Write("shroudforge.json", new { pid = Pid, mode = "failed", modsDir = mods, reason = "ShroudForge's startup preparation failed" });
        var s = Read();
        var row = Mod(s, "shroudforge", "mineral-production-boxes");
        Assert.Equal((ModRunState.Failed, "failed"), (row.State, row.Word));
        Assert.Contains("startup preparation failed", row.Reason);
        var shut = Mod(s, "shroudforge", "off-boxes");
        Assert.Equal((ModRunState.Disabled, "disabled"), (shut.State, shut.Word));
        Assert.Contains("off in the profile", shut.Reason);
        Assert.DoesNotContain(s.Mods, m => m.Engine == "eml");
        Assert.DoesNotContain(s.Engines, e => e.Kind == "eml" && e.Health == EngineHealth.Problem);

        // the same off row while ShroudForge runs: kiln-eml's words, not ShroudForge's switch in the files
        Write("shroudforge.json", new { pid = Pid, mode = "hosted", menu = "kiln", runtime = "running", modsDir = mods, startupPrepared = true });
        SfRuntimeActive("mineral-production-boxes");
        shut = Mod(Read(), "shroudforge", "off-boxes");
        Assert.Equal((ModRunState.Disabled, "disabled"), (shut.State, shut.Word));
        Assert.Contains("off in the profile", shut.Reason);
    }

    /// <summary>No ShroudForge runs it: kiln-eml applied it as an EML mod (from the Library), or EML's own loader runs the
    /// game's mods folder. It stays an EML row, and says its ShroudForge settings do nothing this start; a real EML mod
    /// says nothing of the kind.</summary>
    [Theory]
    [InlineData("kiln-eml")]
    [InlineData("eml-loader")]
    public void A_shroudforge_mod_that_runs_as_an_eml_mod_says_its_settings_do_not_apply(string runner)
    {
        var parent = runner == "kiln-eml" ? "library" : "mods";
        var boxes = EmlPackage("mineral-production-boxes", WhitelistExt, kilns: false, parent: parent);
        var real = EmlPackage("auto-loot", StampExt, kilns: false, caps: "\"patch\"", parent: parent);
        Profile(new() { ["eml"] = [new { id = "mineral-production-boxes", version = "0.3.1", path = boxes }, new { id = "auto-loot", version = "0.3.1", path = real }] });
        if (runner == "kiln-eml")
            Write("eml.json", new
            {
                format = 1, pid = Pid, phase = "done",
                mods = new[]
                {
                    new { id = "mineral-production-boxes", source = "profile", path = boxes, state = "loaded", reason = (string?)null },
                    new { id = "auto-loot", source = "profile", path = real, state = "loaded", reason = (string?)null },
                },
            });
        else
            Write("eml.json", new
            {
                format = 1, pid = Pid, phase = "aside", aside = "dbghelp.dll is loaded",
                mods = new[]
                {
                    new { id = "mineral-production-boxes", source = "profile", path = boxes, state = "own-loader", reason = (string?)"runs under EML's own loader" },
                    new { id = "auto-loot", source = "profile", path = real, state = "own-loader", reason = (string?)"runs under EML's own loader" },
                },
            });
        var s = Read();
        var row = Mod(s, "eml", "mineral-production-boxes");
        Assert.Equal(ModRunState.Loaded, row.State);
        Assert.Contains("only ShroudForge reads", row.Help);
        // (the game's mods folder is ShroudForge's too: its packages there keep their "ShroudForge did not run" rows)
        Assert.DoesNotContain(s.Mods, m => m.Engine == "shroudforge" && m.State == ModRunState.Loaded);
        var eml = Mod(s, "eml", "auto-loot");
        Assert.True(eml.Help is null || !eml.Help.Contains("ShroudForge", StringComparison.Ordinal), eml.Help);
    }

    /// <summary>kiln-eml says ShroudForge runs the package while kiln-shroudforge's status of this start is missing (Kiln does
    /// not know ShroudForge's state, so the row stays EML): it must not also say that its ShroudForge settings do nothing
    /// (review of 0.2.0-dev.31.24).</summary>
    [Fact]
    public void An_eml_row_that_shroudforge_runs_does_not_say_its_settings_do_nothing()
    {
        var mods = Dir("mods");
        var boxes = EmlPackage("mineral-production-boxes", WhitelistExt, kilns: false);
        Profile(new() { ["eml"] = [new { id = "mineral-production-boxes", version = "0.3.1", path = boxes }] });
        Write("eml.json", new
        {
            format = 1, pid = Pid, phase = "aside", aside = "ShroudForge owns startup asset patching and runs EML packages in its mods folder",
            mods = new[] { new { id = "mineral-production-boxes", source = "profile", path = boxes, state = "own-loader",
                reason = $"runs under ShroudForge (its own loader): its ShroudForge switch is on (hand-installed mod at {boxes})" } },
        });
        var row = Mod(Read(), "eml", "mineral-production-boxes");
        Assert.Equal(ModRunState.Loaded, row.State);
        Assert.True(row.Help is null || !row.Help.Contains("only ShroudForge reads", StringComparison.Ordinal), row.Help);
    }

    /// <summary>The profile has the mod off, but ShroudForge runs Kiln's copy anyway (its switch is on there and this
    /// ShroudForge takes no skip list): kiln-eml's row says so and how to stop it. Moved to the ShroudForge group, the row
    /// keeps those words rather than ShroudForge's bare "active" (review of 0.2.0-dev.31.24).</summary>
    [Fact]
    public void A_moved_row_that_runs_although_off_in_the_profile_keeps_kiln_emls_words()
    {
        var mods = Dir("mods");
        var boxes = EmlPackage("mineral-production-boxes", WhitelistExt);
        Profile(new() { ["eml"] = [new { id = "mineral-production-boxes", version = "0.3.1", path = boxes, enabled = false }] });
        Write("eml.json", new
        {
            format = 1, pid = Pid, phase = "aside", aside = "ShroudForge owns startup asset patching and runs EML packages in its mods folder",
            mods = new[] { new { id = "mineral-production-boxes", source = "profile", path = boxes, state = "own-loader",
                reason = $"runs under ShroudForge (its own loader) although it is off in the profile: ShroudForge's switch is on in Kiln's copy {boxes}, " +
                         "and Kiln does not host ShroudForge this start, so it cannot have it skipped. Press Play in the Kiln launcher (or activate the profile again): Kiln then takes its copy out (or switches it off)" } },
        });
        Write("shroudforge.json", new { pid = Pid, mode = "self-hosted", runtime = "running", modsDir = mods, startupPrepared = true });
        SfRuntimeActive("mineral-production-boxes");
        var s = Read();
        var row = Mod(s, "shroudforge", "mineral-production-boxes");
        Assert.Equal(boxes, row.Folder);
        Assert.Contains("although it is off in the profile", row.Reason);
        Assert.DoesNotContain(s.Mods, m => m.Engine == "eml");
    }

    [Fact]
    public void A_failed_shroudforge_preparation_does_not_leave_eml_rows_loaded()
    {
        var folder = Dir("mods", "broken");
        Profile(new() { ["eml"] = [new { id = "broken", version = "1.0.0", path = folder }] });
        Write("eml.json", new
        {
            pid = Pid, phase = "aside", aside = "ShroudForge owns startup asset patching",
            mods = new[] { new { id = "broken", path = folder, state = "own-loader", reason = "runs under ShroudForge (hosted by Kiln)" } },
        });
        Write("shroudforge.json", new { pid = Pid, mode = "failed", modsDir = Path.Combine(_root, "mods"), reason = "ShroudForge's startup preparation failed" });
        var m = Mod(Read(), "eml", "broken");
        Assert.Equal((ModRunState.Failed, "failed"), (m.State, m.Word));
        Assert.Contains("startup preparation failed", m.Reason);
        Assert.DoesNotContain("runs under", m.Reason);
        Assert.Equal(EngineHealth.Problem, Read().Engines.Single(e => e.Kind == "eml").Health);
        // A failed ShroudForge cannot discredit the independently installed EML loader.
        Write("eml.json", new
        {
            pid = Pid, phase = "aside", aside = "EML's own loader",
            mods = new[] { new { id = "broken", path = folder, state = "own-loader", reason = "runs under EML's own loader" } },
        });
        Assert.Equal(ModRunState.Loaded, Mod(Read(), "eml", "broken").State);
    }

    /// <summary>Folders Kiln places in <c>&lt;game&gt;/mods/</c> for other hosts (an XHL-Core companion, an EML mod a DLL
    /// reads there) are not Enshwick mods, even while Enshwick runs: XHL entries are their own group, never Loaded.</summary>
    [Fact]
    public void Xhl_companions_and_dll_carrying_eml_folders_are_not_enshwick_mods()
    {
        void Put(string folder, string file, string text) => File.WriteAllText(Path.Combine(Dir("mods", folder), file), text);
        Put("XHL-Vein-Mining", "native-plugin.ini", "[Plugin]\nDll=XHL-Vein-Mining.dll\n");
        Put("XHL-Vein-Mining", "XHL-Vein-Mining.dll", "MZ");
        Put("XHL-Vein-Mining", ".kiln-library.json", """{"format":1,"kind":"xhl","id":"xhl-vein-mining","version":"2.0.0","hash":"h"}""");
        Put("auto-loot-plus", "mod.json", """{"id":"auto-loot-plus","name":"Auto Loot Plus","version":"1.0.0"}""");
        Put("auto-loot-plus", "loot.dll", "MZ");
        Put("Hand-XHL", "native-plugin.ini", "[Plugin]\nDll=Hand-XHL.dll\n");
        Put("Hand-XHL", "Hand-XHL.dll", "MZ");
        Put("settings-only", "helper.dll", "MZ");
        Put("settings-only", ".kiln-library.json", """{"format":1,"kind":"native","id":"pack","version":"1","hash":"h"}""");
        Put("author.pack", "pack.toml", "id = \"author.pack\"");
        Put("author.dllmod", "dllmod.dll", "MZ");
        Profile(new()
        {
            ["xhl"] = [new { id = "xhl-vein-mining", version = "2.0.0", path = Path.Combine(_root, "mods", "XHL-Vein-Mining") },
                       new { id = "xhl-gone", version = "1.0.0", path = Path.Combine(_root, "mods", "XHL-Gone") }],
            ["eml"] = [new { id = "auto-loot-plus", version = "1.0.0", path = Path.Combine(_root, "mods", "auto-loot-plus") }],
        });
        Write("enshwick.json", new { format = 1, status = "started", updatedUnixMs = StartMs + 1, mods = Array.Empty<object>() });
        var s = Read();

        Assert.Equal(["author.dllmod", "author.pack"], s.Mods.Where(m => m.Engine == "enshwick").Select(m => m.Id).Order().ToArray());
        var xhl = Mod(s, "xhl", "xhl-vein-mining");
        Assert.Equal((ModRunState.NotLoaded, "placed", false), (xhl.State, xhl.Word, xhl.Missing));
        Assert.Contains("Kiln does not load it", xhl.Reason);
        Assert.Contains("Requires XHL-Core", xhl.Help);
        Assert.True(Mod(s, "xhl", "xhl-gone").Missing);
        var hand = Mod(s, "xhl", "Hand-XHL");
        Assert.Equal((ModRunState.NotLoaded, "folder"), (hand.State, hand.Source));
        Assert.DoesNotContain(s.Mods, m => m.State == ModRunState.Loaded && m.Folder is { } f && f.Contains("XHL", StringComparison.Ordinal));
        var engine = s.Engines.Single(e => e.Kind == "xhl");
        Assert.Equal(("XHL", EngineHealth.Problem), (engine.Name, engine.Health));  // the missing entry
        Assert.Contains("Kiln never loads", engine.Note);
        Assert.Equal(ModCatalog.EngineOrder.ToList().IndexOf("eml") + 1, ModCatalog.EngineOrder.ToList().IndexOf("xhl"));
    }

    /// <summary>XHL-Core started with the game (Kiln's native loader or the chain loaded it): an XHL companion's page says
    /// so in plain words and carries no "Requires XHL-Core" warning (inbox 39, PepegaJedi's XHL Remote Chest, chest.png).
    /// Kiln still cannot tell whether XHL-Core ran the plugin, so the row stays Not loaded. A failed XHL-Core keeps the
    /// warning.</summary>
    [Fact]
    public void Xhl_companions_say_xhl_core_started_when_it_did()
    {
        var chest = Dir("mods", "XHL-Remote-Chest");
        File.WriteAllText(Path.Combine(chest, "native-plugin.ini"), "[Plugin]\nDll=XHL-Remote-Chest.dll\n");
        File.WriteAllText(Path.Combine(chest, "XHL-Remote-Chest.dll"), "MZ");
        var hand = Dir("mods", "Hand-XHL");
        File.WriteAllText(Path.Combine(hand, "native-plugin.ini"), "[Plugin]\nDll=Hand-XHL.dll\n");
        var core = Dir("kiln", "chain", "xhl-core");
        object Core(string state) => new
        {
            format = 1, pid = Pid,
            mods = new[] { new { id = "xhl-core", version = "0.0.0", state, folder = core, source = "profile", phase = "dllmain",
                                 dlls = new[] { new { file = "xhl-core.dll", status = state, error = 0 } } } },
        };
        void Setup(object native)
        {
            Profile(new()
            {
                ["native"] = [new { id = "xhl-core", version = "0.0.0", path = core }],
                ["xhl"] = [new { id = "xhl-remote-chest", version = "0.0.0", path = chest }],
            });
            Write("native.json", native);
        }

        Setup(Core("loaded"));
        var s = Read();
        foreach (var m in new[] { Mod(s, "xhl", "xhl-remote-chest"), Mod(s, "xhl", "Hand-XHL") })
        {
            Assert.Equal(ModRunState.NotLoaded, m.State);
            Assert.Null(m.Help);
            Assert.DoesNotContain("Requires XHL-Core", m.Reason);
            Assert.Contains("XHL-Core started with the game", m.Reason);
            Assert.Contains("XHL-Native-Plugin-Host.log", m.Reason);
        }
        Assert.Contains("XHL-Core started with the game", s.Engines.Single(e => e.Kind == "xhl").Note);

        // only the chain names it (an XHL-Core chain-loaded from kiln/chain, no native report): the same
        Setup(new { format = 1, pid = Pid, mods = Array.Empty<object>() });
        var file = Path.Combine(Kiln, "profile.json");
        Write("boot.json", new
        {
            format = 1, pid = Pid, side = "client", profile = new { mode = "active", id = "p", name = "Test", file, missing = Array.Empty<object>() },
            plugins = Array.Empty<object>(),
            chain = new[] { new { path = Path.Combine(core, "xhl-core.dll"), source = "profile", status = "loaded", error = (string?)null } },
        });
        Assert.Null(Mod(Read(), "xhl", "xhl-remote-chest").Help);

        // XHL-Core failed to load: the warning stays
        Setup(Core("failed"));
        var failed = Mod(Read(), "xhl", "xhl-remote-chest");
        Assert.Contains("Requires XHL-Core", failed.Help);
        Assert.DoesNotContain("XHL-Core started with the game", failed.Reason);
    }

    // a profile with a switched-on runtime package and one switched off in ShroudForge, hosted by Kiln
    private string TwoShroudForgeMods()
    {
        var mods = Dir("mods");
        foreach (var (id, on) in new[] { ("pack", true), ("off", false) })
        {
            var folder = Dir("mods", id);
            File.WriteAllText(Path.Combine(folder, "mod.json"), $$"""{"id":"{{id}}","name":"{{id}}","version":"1.0.0","capabilities":["runtime"]}""");
            File.WriteAllText(Path.Combine(folder, "extended.mod.json"), $$"""{"schemaVersion":1,"enabled":{{(on ? "true" : "false")}},"targets":["client"]}""");
        }
        Profile(new() { ["shroudforge"] = [new { id = "pack", version = "1.0.0", path = Path.Combine(mods, "pack") }, new { id = "off", version = "1.0.0", path = Path.Combine(mods, "off") }] });
        // "keys" (dev.24: where mod shortcuts get their key state) is an object beside the fields the catalog reads
        Write("shroudforge.json", new { pid = Pid, mode = "hosted", menu = "kiln", runtime = "running", modsDir = mods,
            keys = new { source = "kiln", reason = "Kiln passes the game window's key, mouse button and focus records to the runtime (host key feed v1)" } });
        File.WriteAllText(Path.Combine(Dir("shroudforge"), "state.json"), JsonSerializer.Serialize(new { runtime = new { pid = Pid, running = true, updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), active = new[] { "pack" } } }));
        return mods;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_matching_pid_does_not_make_a_report_from_before_this_start_current(bool timestamp)
    {
        var path = Path.Combine(State, "set.json");
        Write("set.json", timestamp ? new { pid = Pid, updatedUnixMs = StartMs - 60_000 } : (object)new { pid = Pid });
        File.SetLastWriteTimeUtc(path, Start.AddMinutes(-1));
        Assert.False(ModCatalog.ReadState(new CatalogSources { KilnRoot = Kiln, Pid = Pid, ProcessStartUtc = Start }, "set.json").Current);
    }

    [Fact]
    public void A_recent_shroudforge_report_with_a_reused_pid_is_not_activation_proof()
    {
        TwoShroudForgeMods();
        var now = DateTimeOffset.UtcNow;
        var path = Path.Combine(_root, "shroudforge", "state.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { runtime = new { pid = Pid, running = true, updatedAt = now.AddSeconds(-10).ToUnixTimeSeconds(), active = new[] { "pack" } } }));
        var s = ModCatalog.Read(new CatalogSources { KilnRoot = Kiln, Pid = Pid, ProcessStartUtc = now.AddSeconds(-3).UtcDateTime, Side = "client" });
        var pack = Mod(s, "shroudforge", "pack");
        Assert.Equal((ModRunState.Waiting, "unconfirmed", false), (pack.State, pack.Word, pack.Active));
    }

    /// <summary>ShroudForge's config names a state folder no file call takes (a NUL in it): its runtime report cannot be
    /// read, so its mods are unconfirmed, never left looking Loaded because nothing refined them.</summary>
    [Fact]
    public void A_shroudforge_state_folder_no_file_call_takes_leaves_its_mods_unconfirmed_not_loaded()
    {
        TwoShroudForgeMods();
        File.WriteAllText(Path.Combine(Dir("shroudforge", "config"), "modloader-config.json"), """{ "paths": { "state": "a\u0000b" } }""");
        var s = Read();
        Assert.Equal((ModRunState.Waiting, "unconfirmed"), (Mod(s, "shroudforge", "pack").State, Mod(s, "shroudforge", "pack").Word));
        Assert.Equal(ModRunState.Disabled, Mod(s, "shroudforge", "off").State);
    }

    /// <summary>A package put into ShroudForge's folder by hand whose name holds a lone surrogate escape (a cut emoji) is
    /// listed under its folder's name, and the others are still read.</summary>
    [Fact]
    public void A_hand_installed_package_with_a_cut_emoji_in_its_name_leaves_the_others_read()
    {
        var mods = TwoShroudForgeMods();
        File.WriteAllText(Path.Combine(Dir("mods", "hand"), "extended.mod.json"), """{"enabled":false,"name":"Mod \ud83d"}""");
        var s = Read();
        var pack = Mod(s, "shroudforge", "pack");
        Assert.Equal((ModRunState.Loaded, "active", true), (pack.State, pack.Word, pack.Active));
        Assert.Equal(ModRunState.Disabled, Mod(s, "shroudforge", "off").State);
        Assert.Equal(("hand", ModRunState.Disabled), (Mod(s, "shroudforge", "hand").Name, Mod(s, "shroudforge", "hand").State));
        Assert.Equal(EngineHealth.Ok, s.Engines.Single(e => e.Kind == "shroudforge").Health);
    }

    /// <summary>A switched-off entry whose folder is still in the game's mods folder (a copy kept because the player
    /// changed it, say) is off in the profile, but a loader that reads that folder may still run it: the row says so.</summary>
    [Fact]
    public void A_switched_off_entry_whose_folder_stays_in_the_mods_folder_may_still_run()
    {
        File.WriteAllText(Path.Combine(Dir("mods", "XHL-Kept"), "native-plugin.ini"), "[Plugin]\nDll=XHL-Kept.dll\n");
        Dir("mods", "wick-kept");
        Profile(new()
        {
            ["xhl"] = [new { id = "xhl-kept", version = "1", path = Path.Combine(_root, "mods", "XHL-Kept"), enabled = false },
                       new { id = "xhl-gone", version = "1", path = Path.Combine(_root, "mods", "XHL-Gone"), enabled = false }],
            ["enshwick"] = [new { id = "wick-kept", version = "1", path = Dir("lib", "wick-kept"), enabled = false },
                            new { id = "wick-off", version = "1", path = Dir("lib", "wick-off"), enabled = false }],
        });
        var s = Read();
        var kept = Mod(s, "xhl", "xhl-kept");
        Assert.Equal(ModRunState.Disabled, kept.State);
        Assert.Contains("still in the game's mods folder", kept.Reason);
        Assert.Contains("XHL-Core may still load it", kept.Reason);
        Assert.Equal("Switched off in the profile.", Mod(s, "xhl", "xhl-gone").Reason);
        Assert.Contains("Enshwick's loader", Mod(s, "enshwick", "wick-kept").Reason);
        Assert.Equal("Switched off in the profile.", Mod(s, "enshwick", "wick-off").Reason);
    }

    /// <summary>Settings an earlier Kiln wrote without their fraction: ShroudForge refuses the whole mod until a save puts
    /// the fraction back, so the row says so (and what repairs it) instead of reading as healthy.</summary>
    [Fact]
    public void A_shroudforge_mod_whose_settings_need_repair_says_shroudforge_refuses_it()
    {
        var mods = TwoShroudForgeMods();
        File.WriteAllText(Path.Combine(mods, "pack", "extended.mod.json"),
            """{"schemaVersion":1,"enabled":true,"targets":["client"],"settings":{"speed":{"value":2,"label":"Speed","options":{"1.5":"Slow","2.0":"Fast"}}}}""");
        File.WriteAllText(Path.Combine(mods, "off", "extended.mod.json"),
            """{"schemaVersion":1,"enabled":false,"targets":["client"],"settings":{"speed":{"value":2,"label":"Speed","options":{"1.5":"Slow","2.0":"Fast"}}}}""");
        Assert.Equal(["speed"], ShroudForgePackages.Read(Path.Combine(mods, "pack")).Repair);
        var s = Read();
        var pack = Mod(s, "shroudforge", "pack");
        Assert.Equal((ModRunState.Failed, "needs repair", true), (pack.State, pack.Word, pack.NeedsAttention));
        Assert.Contains("ShroudForge refuses this mod until its settings are saved once", pack.Reason);
        Assert.Contains("Speed", pack.Reason);
        Assert.Contains("repairs it", pack.Help);
        var off = Mod(s, "shroudforge", "off");
        Assert.Equal(ModRunState.Disabled, off.State);
        Assert.Contains("until its settings are saved once", off.Reason);
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

    /// <summary>Every proxy DLL mod is a dinput8.dll: with two of them chained (Spawn the World and Auto Loot Plus) both
    /// rows had the ImGui id "##mod.chain/dinput8.dll", and ImGui's "conflicting ID" popup stood under each (Nexus,
    /// 2026-11-14); a click on the second chose the first. A DLL chained twice (chain.ini and the profile) is the
    /// proxy's duplicate of the same path. Plugins that failed before they named themselves are named by their file.</summary>
    [Fact]
    public void Chain_dlls_and_plugins_with_one_file_name_get_their_own_row_ids_and_keys()
    {
        var file = Path.Combine(Kiln, "profile.json");
        File.WriteAllText(file, """{"format":1,"id":"p","generation":1,"mods":{}}""");
        Write("boot.json", new
        {
            format = 1, pid = Pid, profile = new { mode = "active", id = "p", file },
            chain = new[]
            {
                new { path = @"C:\g\mods\spawn-the-world\dinput8.dll", source = "profile", status = "loaded" },
                new { path = @"C:\g\mods\auto-loot-plus\dinput8.dll", source = "profile", status = "loaded" },
                new { path = @"C:\g\mods\auto-loot-plus\dinput8.dll", source = "chain", status = "duplicate" },
            },
            plugins = new[]
            {
                new { file = "helper.dll", path = @"C:\g\kiln\plugins\helper.dll", status = "failed", error = "bad image" },
                new { file = "helper.dll", path = @"C:\lib\plugin\helper\1.0.0+aaaaaaaa\helper.dll", status = "failed", error = "bad image" },
            },
        });
        var s = Read();
        var chain = s.Mods.Where(m => m.Engine == "chain").ToArray();
        Assert.Equal(3, chain.Length);
        Assert.All(chain, m => Assert.Equal(("dinput8.dll", "dinput8.dll"), (m.Id, m.Name)));  // what a player sees stays
        Assert.Equal(2, s.Mods.Count(m => m.Engine == "plugin" && m.Id == "helper.dll"));
        var rows = ModManagerLogic.Rows(s, EngineFilter.All, "");
        Assert.Equal(5, rows.Count);
        Assert.Equal(5, rows.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(5, rows.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count());
        // the same keys under the engine's own filter and at the next read: a refresh keeps the selection
        Assert.Equal(rows.Where(r => r.Mod.Engine == "chain").Select(r => r.Key), ModManagerLogic.Rows(Read(), new EngineFilter("chain"), "").Select(r => r.Key));
        // the second one stays chosen, and the details show that one
        var second = rows.Where(r => r.Mod.Engine == "chain").ElementAt(1);
        var page = new ModManagerPage { Filter = new EngineFilter("chain"), Selected = second.Key };
        page.Update(s, false);
        Assert.Equal(second.Key, page.Selected);
        Assert.Equal((@"C:\g\mods\auto-loot-plus\dinput8.dll", "loaded"), (page.Rows.Single(r => r.Key == page.Selected).Mod.Folder, page.Rows.Single(r => r.Key == page.Selected).Mod.Word));
    }

    /// <summary>Two rows of one engine with one id and nothing else to tell them apart (two native mods of one id: the
    /// second is the duplicate that does not load; an engine that reports a mod twice) still get their own keys, the
    /// first keeps the plain one.</summary>
    [Fact]
    public void Rows_that_share_an_id_still_get_their_own_keys()
    {
        Write("native.json", new
        {
            format = 1, pid = Pid,
            mods = new object[]
            {
                new { id = "creative", folder = @"C:\g\kiln\mods\creative", state = "loaded" },
                new { id = "creative", folder = @"C:\g\kiln\mods\creative-copy", state = "failed", reason = "duplicate native mod id 'creative'; only the first loads" },
            },
        });
        Write("emf.json", new { format = 1, updatedUnixMs = StartMs + 1, mods = new[] { new { id = "a", status = "loaded" }, new { id = "a", status = "duplicate" } } });
        var s = Read();
        var rows = ModManagerLogic.Rows(s, EngineFilter.All, "");
        Assert.Equal(4, rows.Count);
        Assert.Equal(4, rows.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, rows.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(["creative", "a"], rows.Where(r => r.Mod.State == ModRunState.Loaded).Select(r => r.Mod.Id));
        Assert.Equal(@"C:\g\kiln\mods\creative", rows.Single(r => r.Mod.Engine == "native" && r.Mod.State == ModRunState.Loaded).Mod.Folder);
        Assert.Equal("emf/a", rows.Single(r => r.Mod.Engine == "emf" && r.Mod.State == ModRunState.Loaded).Key);
        Assert.Equal(rows.Select(r => r.Key), ModManagerLogic.Rows(Read(), EngineFilter.All, "").Select(r => r.Key));  // the same at every read
    }

    [Fact]
    public void Duplicate_row_keys_follow_the_folder_and_source_across_refreshes()
    {
        var a = new CatalogMod { Engine = "shroudforge", Id = "same", Name = "A", Folder = @"C:\mods\a", Source = "folder" };
        var b = a with { Name = "B", Folder = @"C:\mods\b" };
        List<CatalogMod> first = [a, b];
        ModCatalog.UniqueKeys(first);
        var selected = ModManagerLogic.KeyOf(first[1]);
        List<CatalogMod> reversed = [b with { State = ModRunState.Disabled }, a];
        ModCatalog.UniqueKeys(reversed);
        Assert.Equal(selected, ModManagerLogic.KeyOf(reversed[0]));
        List<CatalogMod> remaining = [b];
        ModCatalog.UniqueKeys(remaining);
        Assert.Equal(selected, ModManagerLogic.KeyOf(remaining[0]));
        var chain = a with { Engine = "chain", Key = a.Folder, Source = "profile" };
        Assert.NotEqual(ModManagerLogic.KeyOf(chain), ModManagerLogic.KeyOf(chain with { Source = "chain" }));
    }

    [Fact]
    public void Generated_suffixes_do_not_take_a_real_rows_key()
    {
        var a = new CatalogMod { Engine = "emf", Id = "a", Name = "A" };
        List<CatalogMod> rows = [a, a, a with { Id = "a#2" }];
        ModCatalog.UniqueKeys(rows);
        Assert.Equal(ModManagerLogic.KeyOf(a with { Id = "a#2" }), ModManagerLogic.KeyOf(rows[2]));
        Assert.Equal(3, rows.Select(ModManagerLogic.KeyOf).Distinct().Count());
    }

    [Fact]
    public void Path_keys_cannot_reset_imguis_hash_with_three_hash_characters()
    {
        var a = new CatalogMod { Engine = "chain", Id = "dinput8.dll", Name = "DLL", Key = @"C:\a###same\dinput8.dll" };
        var b = a with { Key = @"C:\b###same\dinput8.dll" };
        var rows = ModManagerLogic.Rows(new CatalogSnapshot { Mods = [a, b] }, EngineFilter.All, "");
        Assert.All(rows, row => Assert.DoesNotContain("###", row.Id));
        Assert.NotEqual(rows[0].Id, rows[1].Id);
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

    /// <summary>WaitForSnapshot waits for the refresh that runs; once one has landed it reads nothing more. It used to
    /// start a second refresh then, which made the test below count two reads when the first refresh was quick.</summary>
    [Fact]
    public void Waiting_for_a_snapshot_that_already_landed_reads_nothing_more()
    {
        Profile([]);
        var reads = 0;
        ModCatalog.Sources = () => { Interlocked.Increment(ref reads); return new CatalogSources { KilnRoot = Kiln, Pid = Pid, ProcessStartUtc = Start }; };
        try
        {
            ModCatalog.Snapshot(TimeSpan.FromMinutes(1));
            var until = Environment.TickCount64 + 10_000;
            while (ModCatalog.Loading && Environment.TickCount64 < until) Thread.Sleep(5);
            Assert.False(ModCatalog.Loading);
            ModCatalog.WaitForSnapshot();
            Assert.Equal(1, reads);
        }
        finally
        {
            ModCatalog.Sources = ModCatalog.DefaultSources;
        }
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
        Assert.Equal(ModManagerSection.Graphics, ModManagerLogic.Step(ModManagerSection.Mods, -1));
        Assert.Equal(ModManagerSection.Graphics, ModManagerLogic.Step(ModManagerSection.Overlay, 1));
        Assert.Equal(ModManagerSection.Mods, ModManagerLogic.Step(ModManagerSection.Graphics, 1));
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
