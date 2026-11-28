// SPDX-License-Identifier: MIT
//
// The Kiln menu's ShroudForge group in a kiln-backend start (SfiCatalog.cs, SfiState.cs and their branch in
// ModCatalog.Read): which branch a start takes, the rows made from kiln/state/sfi.json (schema/sfi/sfi-state.schema.json),
// and the tripwire: a kiln-backend start never reaches <game>/shroudforge, while hosted and installed starts stay exactly
// as they were. No game, no runtime.
using System.Text.Json;
using System.Text.Json.Nodes;
using Kiln.FakeUi;
using Xunit;

namespace Kiln.SfiCatalogTests;

[Collection(UiSerial.Name)]
public sealed class SfiCatalogTests : IDisposable
{
    internal const string L = "3f2b8c1e5a7d4e2f";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kiln-sfi-catalog-" + Guid.NewGuid().ToString("N"));
    private string Kiln => Path.Combine(_root, "kiln");
    private string State => Path.Combine(Kiln, "state");
    private string Mods => Path.Combine(_root, "mods");
    private string SfRoot => Path.Combine(_root, "shroudforge");
    private const int Pid = 4242;
    private static readonly DateTime Start = DateTime.UtcNow.AddMinutes(-30);
    private static long StartMs => new DateTimeOffset(Start).ToUnixTimeMilliseconds();
    private readonly List<string> _sfRoot = [];

    public SfiCatalogTests()
    {
        Directory.CreateDirectory(State);
        Directory.CreateDirectory(Mods);
        // the tripwire: every lookup of ShroudForge's folders for this test's game (other tests' games are not counted)
        ShroudForgePackages.OnSfRoot = (game, key) =>
        {
            if (ModCatalog.SamePath(game, _root)) lock (_sfRoot) _sfRoot.Add(key);
        };
    }

    public void Dispose()
    {
        ShroudForgePackages.OnSfRoot = null;
        SfiState.ResetForTests();
        try
        {
            if (!OperatingSystem.IsWindows() && Directory.Exists(SfRoot)) File.SetUnixFileMode(SfRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }

    // ---------------------------------------------------------------------------------------- fixtures ----

    internal static JsonObject Pkg(string id, string intent = "selected", string data = "not-needed", string lua = "active", bool? archive = false,
        bool? runtime = true, long? epoch = 1, JsonObject? skip = null, JsonObject? warning = null, bool sensitive = false, JsonArray? dlls = null)
    {
        var p = new JsonObject
        {
            ["id"] = id, ["version"] = "1.0.0", ["name"] = "Name " + id, ["intent"] = intent, ["data"] = data, ["lua"] = lua,
            ["dlls"] = dlls ?? new JsonArray(), ["archivePresent"] = archive, ["runtimeActive"] = runtime,
            ["saveSensitive"] = sensitive
                ? new JsonObject { ["value"] = true, ["basis"] = "unknown-mixed", ["policyRow"] = null }
                : new JsonObject { ["value"] = false, ["basis"] = "none", ["policyRow"] = null },
            ["skip"] = skip, ["warning"] = warning,
        };
        if (epoch is { } e) p["ownerEpoch"] = e;
        return p;
    }

    internal static JsonObject Record(string phase, IEnumerable<JsonObject> packages, int pid = Pid, long? startMs = null, string launchId = L,
        string selected = "kiln", JsonObject? reason = null, long revision = 1)
    {
        var d = new JsonObject
        {
            ["format"] = 1, ["writer"] = "kiln-sfi",
            ["launch"] = new JsonObject
            {
                ["launchId"] = launchId, ["planHash"] = new string('a', 64), ["pid"] = pid, ["processStartMs"] = startMs ?? StartMs,
                ["side"] = "client", ["profile"] = new JsonObject { ["id"] = "p", ["generation"] = 1 },
                ["backend"] = new JsonObject { ["selected"] = selected, ["source"] = "kiln.launch", ["reasonCode"] = "profile-setting", ["text"] = null },
                ["frozenAtMs"] = StartMs + 100,
            },
            ["phase"] = phase, ["incomplete"] = phase is "starting" or "running",
            ["previousStart"] = new JsonObject { ["disposition"] = "none", ["launchId"] = null, ["pid"] = null },
            ["updatedMs"] = StartMs + 4000, ["warningRevision"] = revision, ["packages"] = new JsonArray([.. packages]),
        };
        if (reason is not null) d["reason"] = reason;
        return d;
    }

    private void WriteSfi(JsonObject record) => File.WriteAllText(Path.Combine(State, "sfi.json"), record.ToJsonString());
    private void WriteRaw(string file, string text) => File.WriteAllText(Path.Combine(State, file), text);

    /// <summary>kiln-shroudforge's status of a start whose frozen backend is <paramref name="backend"/> (mode off, as it
    /// stands aside for the interpreter), or of a start with no launch plan (<paramref name="backend"/> null).</summary>
    private void SfStatus(string? backend, string mode = "off", string launchId = L, string? reason = null)
    {
        var o = new JsonObject { ["pid"] = Pid, ["mode"] = mode, ["reason"] = reason ?? "", ["updatedUnixMs"] = StartMs + 1000, ["modsDir"] = Mods };
        if (mode == "hosted") { o["menu"] = "kiln"; o["runtime"] = "running"; o["startupPrepared"] = true; }
        if (backend is not null) o["launch"] = new JsonObject { ["backend"] = backend, ["reasonCode"] = "profile-setting", ["reasonClass"] = "selected", ["reason"] = "x", ["launchId"] = launchId };
        WriteRaw("shroudforge.json", o.ToJsonString());
    }

    internal static string Package(string folder, string id, bool on = true, string caps = "runtime", string? marker = null, string? launcher = null,
        bool nativeIni = false, string settings = "{}")
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "mod.json"), $$"""{"id":"{{id}}","name":"Name {{id}}","version":"1.0.0","capabilities":["{{caps}}"]}""");
        File.WriteAllText(Path.Combine(folder, "extended.mod.json"), "{\n  \"schemaVersion\": 1,\n  \"enabled\": " + (on ? "true" : "false") +
            (launcher is null ? "" : $",\n  \"launcher\": \"{launcher}\"") + ",\n  \"settings\": " + settings + "\n}\n");
        if (nativeIni)
        {
            Directory.CreateDirectory(Path.Combine(folder, "bin"));
            File.WriteAllText(Path.Combine(folder, "native-plugin.ini"), "[Plugin]\nEnabled=1\nDll=bin/native.dll\n");
            File.WriteAllBytes(Path.Combine(folder, "bin", "native.dll"), [0x4d, 0x5a]);
        }
        if (marker is not null)
            File.WriteAllText(Path.Combine(folder, ".kiln-library.json"), JsonSerializer.Serialize(new { format = 1, kind = marker, id, version = "1.0.0", hash = "kdh1:" + new string('0', 64) }));
        return folder;
    }

    private void Profile(params (string Kind, string Id, string Path, bool Enabled)[] entries)
    {
        var file = Path.Combine(Kiln, "profile.json");
        var mods = new JsonObject();
        foreach (var g in entries.GroupBy(e => e.Kind))
            mods[g.Key] = new JsonArray([.. g.Select(e => (JsonNode)new JsonObject { ["id"] = e.Id, ["version"] = "1.0.0", ["path"] = e.Path, ["enabled"] = e.Enabled })]);
        File.WriteAllText(file, new JsonObject { ["format"] = 1, ["id"] = "p", ["name"] = "Test", ["generation"] = 1, ["dropins"] = true, ["mods"] = mods }.ToJsonString());
        WriteRaw("boot.json", JsonSerializer.Serialize(new { format = 1, pid = Pid, side = "client", profile = new { mode = "active", id = "p", name = "Test", file, missing = Array.Empty<object>() }, plugins = Array.Empty<object>(), chain = Array.Empty<object>() }));
    }

    /// <summary>ShroudForge's own folder holding decoys: its config moves the mods folder to one with a decoy package, its
    /// state.json says its runtime runs every package in this process, its actions folder exists. Anything of a
    /// kiln-backend start that read them would show a decoy row, or Active from ShroudForge's state.</summary>
    private string Decoys(params string[] activeIds)
    {
        var decoyMods = Path.Combine(_root, "decoy-mods");
        Package(Path.Combine(decoyMods, "decoy"), "decoy");
        Directory.CreateDirectory(Path.Combine(SfRoot, "config"));
        Directory.CreateDirectory(Path.Combine(SfRoot, "ui", "actions"));
        File.WriteAllText(Path.Combine(SfRoot, "config", "modloader-config.json"), JsonSerializer.Serialize(new { paths = new { mods = decoyMods } }));
        File.WriteAllText(Path.Combine(SfRoot, "state.json"), JsonSerializer.Serialize(new
        {
            runtime = new { pid = Pid, running = true, updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), active = activeIds.Append("decoy").ToArray() },
            assets = new { status = "applied", target = "enshrouded", completedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), mods = activeIds.Select(i => new { id = i, version = "1.0.0" }).ToArray() },
        }));
        return decoyMods;
    }

    private string TreeOf(string dir) => !Directory.Exists(dir) ? "" : string.Join("\n",
        Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(p => $"{p}|{(File.Exists(p) ? new FileInfo(p).Length : -1)}|{File.GetLastWriteTimeUtc(p).Ticks}"));

    private CatalogSnapshot Read(SfiBranch? force = null) => ModCatalog.Read(new CatalogSources
    {
        KilnRoot = Kiln, Pid = Pid, ProcessStartUtc = Start, Side = "client", Sfi = force,
    });

    private static CatalogMod Row(CatalogSnapshot s, string engine, string id) => Assert.Single(s.Mods, m => m.Engine == engine && m.Id == id);
    private static CatalogEngine Group(CatalogSnapshot s) => s.Engines.Single(e => e.Kind == "shroudforge");

    private static string Shape(CatalogSnapshot s) => string.Join("\n", s.Mods.Select(m =>
        $"{m.Engine}|{m.Id}|{m.Name}|{m.Version}|{m.State}|{m.Word}|{m.Reason}|{m.Help}|{m.Folder}|{m.Source}|{m.Missing}|{m.Active}|{m.Sfi is null}")) +
        "\n" + string.Join("\n", s.Engines.Select(e => $"{e.Kind}|{e.Word}|{e.Health}|{e.Note}|{e.Count}|{e.Reported}"));

    private int SfRootHits() { lock (_sfRoot) return _sfRoot.Count; }

    // ------------------------------------------------------------------------------- hosted unchanged ----

    /// <summary>Hosted and installed starts (kiln.launch froze them, or no launch plan at all), with or without this start's
    /// aside sfi.json, take ModCatalog's own branch: the same rows as a read that skips the decision, and that branch does
    /// read ShroudForge's folders (which proves the tripwire counts).</summary>
    [Theory]
    [InlineData("hosted", false)]
    [InlineData("installed", false)]
    [InlineData("hosted", true)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    public void Hosted_and_installed_starts_take_todays_branch_unchanged(string? backend, bool asideRecord)
    {
        Package(Path.Combine(Mods, "lua"), "lua");
        Package(Path.Combine(Mods, "off"), "off", on: false);
        Decoys("lua");
        SfStatus(backend, mode: "hosted");
        if (asideRecord)
            WriteSfi(Record("aside", [], selected: backend ?? "hosted", reason: new JsonObject { ["code"] = "backend-hosted", ["text"] = "hosted" }));
        var legacy = Read(SfiBranch.Legacy);
        var hitsLegacy = SfRootHits();
        Assert.True(hitsLegacy > 0, "the legacy branch reads ShroudForge's folders: the tripwire must see that");
        var decided = Read();
        Assert.Equal(Shape(legacy), Shape(decided));
        Assert.True(SfRootHits() > hitsLegacy);
        Assert.All(decided.Mods, m => Assert.Null(m.Sfi));
    }

    // ------------------------------------------------------------------------------------- tripwire ----

    public static TheoryData<string> KilnBranches => new() { "running", "pending", "unknown", "external", "refused", "mismatch" };

    /// <summary>Every kiln-backend branch: nothing looks up ShroudForge's folders (config, state.json, ui/actions), its
    /// decoys never show, its tree is untouched, and the same read with the folder unreadable gives the same rows.</summary>
    [Theory]
    [MemberData(nameof(KilnBranches))]
    public void A_kiln_backend_start_never_reaches_the_shroudforge_folder(string branch)
    {
        Profile(("shroudforge", "lua", Path.Combine(Mods, "lua"), true));
        Package(Path.Combine(Mods, "lua"), "lua", marker: "shroudforge");
        Package(Path.Combine(Mods, "hand"), "hand");
        var decoyMods = Decoys("lua", "hand");
        SfStatus("kiln", mode: branch == "external" ? "self-hosted" : "off", launchId: branch == "mismatch" ? "0000000000000001" : L);
        switch (branch)
        {
            case "running" or "external" or "mismatch":
                WriteSfi(Record("running", [Pkg("lua", epoch: 2), Pkg("hand", epoch: 3)]));
                break;
            case "refused":
                WriteSfi(Record("refused", [], reason: new JsonObject { ["code"] = "held", ["text"] = "kiln-sfi.interpreter is held" }));
                break;
            case "unknown":
                WriteRaw("sfi.json", "{ not json");
                break;
        }
        var before = TreeOf(SfRoot);
        var s = Read();
        Assert.Equal(0, SfRootHits());
        Assert.DoesNotContain(s.Mods, m => m.Id == "decoy" || m.Folder is { } f && f.StartsWith(decoyMods, StringComparison.Ordinal));
        Assert.Equal(before, TreeOf(SfRoot));
        var lua = Row(s, "shroudforge", "lua");
        Assert.Equal(Path.Combine(Mods, "lua"), lua.Folder);
        Assert.Equal(branch == "running", lua.Active);
        Assert.Equal(branch == "running", Row(s, "shroudforge", "hand").Active);
        Assert.NotNull(lua.Sfi);
        Assert.True(lua.Sfi!.NextStartOnly);
        var word = Group(s).Word;
        Assert.Equal(branch switch { "running" => "running", "pending" => "pending", "external" => "external", "refused" => "refused", _ => "unknown" }, word);

        // readable, then unreadable: the same rows (nothing there was read in the first place)
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(SfRoot, UnixFileMode.None);
            Assert.Equal(Shape(s), Shape(Read()));
            Assert.Equal(0, SfRootHits());
        }
    }

    // ---------------------------------------------------------------------------------------- rows ----

    /// <summary>Active is the bridge's rule: a live phase, runtimeActive true, intent selected and an ownerEpoch of
    /// 1 or more. Without ownerEpoch (no live runtime) the row is Loaded but takes no actions; ended takes none either.</summary>
    [Fact]
    public void Active_comes_from_runtime_active_and_the_owner_epoch_only()
    {
        Package(Path.Combine(Mods, "a"), "a");
        Package(Path.Combine(Mods, "b"), "b");
        Package(Path.Combine(Mods, "c"), "c");
        SfStatus("kiln");
        WriteSfi(Record("running", [Pkg("a", epoch: 3), Pkg("b", epoch: null), Pkg("c", lua: "ready", runtime: false, epoch: null)]));
        var s = Read();
        Assert.Equal((ModRunState.Loaded, "active", true), (Row(s, "shroudforge", "a").State, Row(s, "shroudforge", "a").Word, Row(s, "shroudforge", "a").Active));
        Assert.Equal((ModRunState.Loaded, false), (Row(s, "shroudforge", "b").State, Row(s, "shroudforge", "b").Active));
        Assert.Equal((ModRunState.Waiting, "starting", false), (Row(s, "shroudforge", "c").State, Row(s, "shroudforge", "c").Word, Row(s, "shroudforge", "c").Active));
        Assert.Equal(EngineHealth.Ok, Group(s).Health);
        Assert.True(Group(s).Reported);

        WriteSfi(Record("ended", [Pkg("a", lua: "stopped", runtime: false, epoch: 3)]));
        Assert.False(Row(Read(), "shroudforge", "a").Active);
    }

    /// <summary>Malformed, out-of-schema, another pid's, a reused pid's and another start of this pid: never Active, never
    /// ShroudForge's own state, and the rows say pending or unknown.</summary>
    [Theory]
    [InlineData("garbage", "unknown")]
    [InlineData("schema", "unknown")]
    [InlineData("duplicate", "unknown")]
    [InlineData("other-pid", "unconfirmed")]
    [InlineData("reused-pid", "unconfirmed")]
    [InlineData("same-pid-new-start", "unknown")]
    [InlineData("aside-of-this-start", "unknown")]
    public void Malformed_stale_and_reused_records_are_never_active(string kind, string word)
    {
        Package(Path.Combine(Mods, "a"), "a");
        Decoys("a");
        SfStatus("kiln");
        var path = Path.Combine(State, "sfi.json");
        switch (kind)
        {
            case "garbage": WriteRaw("sfi.json", "\u0000\u0001 nope"); break;
            case "schema": WriteSfi(Record("running", [Pkg("a", lua: "active", runtime: false)])); break;
            case "duplicate": WriteSfi(Record("running", [Pkg("a"), Pkg("a")])); break;
            case "other-pid": WriteSfi(Record("running", [Pkg("a")], pid: Pid + 1)); break;
            case "reused-pid":
                WriteSfi(Record("running", [Pkg("a")], startMs: StartMs - 3_600_000));
                File.SetLastWriteTimeUtc(path, Start.AddMinutes(-60));
                break;
            case "same-pid-new-start": WriteSfi(Record("running", [Pkg("a")], startMs: StartMs - 3_600_000)); break;
            case "aside-of-this-start":
                WriteSfi(Record("aside", [], selected: "hosted", reason: new JsonObject { ["code"] = "backend-hosted", ["text"] = "hosted" }));
                break;
        }
        var s = Read();
        var a = Row(s, "shroudforge", "a");
        Assert.False(a.Active);
        Assert.Equal(ModRunState.Waiting, a.State);
        Assert.Equal(word, a.Word);
        Assert.Equal(0, SfRootHits());
        Assert.False(Group(s).Reported);
    }

    private const string Whitelist = """{"whitelist":{"value":["Sulfur"],"control":"multiselect","label":"Whitelist","options":{"Sulfur":"Sulfur","Coal":"Coal"}}}""";

    /// <summary>HTR (Discord, 2026-11-27, 10:32 pm ET): Mineral Production Boxes (Nexus 347) in the player's own folder in
    /// the game's mods folder, its extension saying EML with ShroudForge settings in it. kiln-eml, the launch plan's data
    /// writer, ran it as an EML drop-in. Before a save loaded (the interpreter had not reported) the menu listed it twice,
    /// EML "Loaded" and ShroudForge with its settings; once the interpreter's record named it, step 1 refined the EML row
    /// in place and its folder no longer made a ShroudForge row: only EML was left, without switch or settings. Now one
    /// ShroudForge row with its folder at both moments, with the interpreter's truth once it reports; and the same while
    /// ShroudForge's own loader runs the folder (external).</summary>
    [Theory]
    [InlineData("kiln")]
    [InlineData("external")]
    public void A_shroudforge_mod_kiln_eml_runs_is_one_shroudforge_row_before_and_after_a_save_loads(string branch)
    {
        var folder = Package(Path.Combine(Mods, "mineral-production-boxes"), "mineral-production-boxes", launcher: "EML", settings: Whitelist);
        var real = Package(Path.Combine(Mods, "auto-loot"), "auto-loot", caps: "patch", launcher: "EML");
        Profile();
        WriteRaw("eml.json", JsonSerializer.Serialize(new
        {
            pid = Pid, updatedUnixMs = StartMs + 2000, phase = "done",
            mods = new[]
            {
                new { id = "mineral-production-boxes", name = "Mineral Production Boxes", version = "1.0.0", path = folder, source = "game-mods", state = "loaded" },
                new { id = "auto-loot", name = "Auto Loot", version = "1.0.0", path = real, source = "game-mods", state = "loaded" },
            },
        }));
        SfStatus("kiln", mode: branch == "external" ? "self-hosted" : "off");

        void One(CatalogSnapshot s, ModRunState state, string word, bool active)
        {
            var row = Assert.Single(s.Mods, m => m.Folder is { } f && ModCatalog.SamePath(f, folder));
            Assert.Equal("shroudforge", row.Engine);
            Assert.Equal((state, word, active), (row.State, row.Word, row.Active));
            Assert.NotNull(row.Sfi);
            Assert.Contains(ShroudForgePackages.Read(row.Folder!).Settings, d => d.Key == "whitelist");
            // the real EML mod (the stamp only) keeps its EML row (its own folder's switch row is ShroudForge's, as before)
            Assert.Contains(s.Mods, m => m.Engine == "eml" && m.Folder is { } f && ModCatalog.SamePath(f, real));
        }

        // before a save loads: the interpreter has not reported yet (ShroudForge's own loader: it never does)
        if (branch == "external") One(Read(), ModRunState.NotLoaded, "external", false);
        else One(Read(), ModRunState.Waiting, "unconfirmed", false);
        // after: the interpreter's record names it (a self-hosted start's record is not this branch's: still external)
        WriteSfi(Record("running", [Pkg("mineral-production-boxes", epoch: 2)]));
        if (branch == "external") One(Read(), ModRunState.NotLoaded, "external", false);
        else One(Read(), ModRunState.Loaded, "active", true);
    }

    /// <summary>The launcher's compatibility entry (kiln-manage profile.rs, emlRuntime): an EML package that needs
    /// ShroudForge's runtime is in the profile twice, under eml and under shroudforge, with the same folder. One row,
    /// ShroudForge's, before and after the interpreter reports.</summary>
    [Fact]
    public void The_launchers_compatibility_entry_makes_one_row_not_two()
    {
        var folder = Package(Path.Combine(Mods, "endless-chests"), "endless-chests", marker: "eml", launcher: "EML");
        Profile(("eml", "endless-chests", folder, true), ("shroudforge", "endless-chests", folder, true));
        WriteRaw("eml.json", JsonSerializer.Serialize(new
        {
            pid = Pid, updatedUnixMs = StartMs + 2000, phase = "done",
            mods = new[] { new { id = "endless-chests", name = "Endless Chests", version = "1.0.0", path = folder, source = "profile", state = "loaded" } },
        }));
        SfStatus("kiln");
        foreach (var record in new[] { false, true })
        {
            if (record) WriteSfi(Record("running", [Pkg("endless-chests", epoch: 2)]));
            var s = Read();
            var row = Assert.Single(s.Mods, m => m.Folder is { } f && ModCatalog.SamePath(f, folder));
            Assert.Equal(("shroudforge", record), (row.Engine, row.Active));
        }
    }

    /// <summary>The compatibility entry as the launcher writes it: both entries name the EML package's Library folder
    /// (kiln-manage profile.rs render_routed), and a kiln start places Kiln's EML copy in the game's mods folder with
    /// ShroudForge's switch on (tests/sfi_placement.rs: wardrobe), which the interpreter runs. kiln-eml names the Library
    /// folder (the plan's data writer) or Kiln's copy. One ShroudForge row on Kiln's copy, before and after the
    /// interpreter reports: its switch and settings are the copy's, never the Library's (review of 0.2.0-dev.31.24).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_compatibility_entry_of_a_library_package_is_one_row_on_kilns_copy(bool emlNamesTheCopy)
    {
        var lib = Package(Path.Combine(_root, "library", "eml", "wardrobe", "1.0.0"), "wardrobe", on: false, launcher: "EML", settings: Whitelist);
        var copy = Package(Path.Combine(Mods, "wardrobe"), "wardrobe", marker: "eml", launcher: "EML", settings: Whitelist);
        Profile(("eml", "wardrobe", lib, true), ("shroudforge", "wardrobe", lib, true));
        WriteRaw("eml.json", JsonSerializer.Serialize(new
        {
            pid = Pid, updatedUnixMs = StartMs + 2000, phase = "done",
            mods = new[] { new { id = "wardrobe", name = "Wardrobe", version = "1.0.0", path = emlNamesTheCopy ? copy : lib, source = "profile",
                state = emlNamesTheCopy ? "own-loader" : "loaded" } },
        }));
        SfStatus("kiln");
        foreach (var record in new[] { false, true })
        {
            if (record) WriteSfi(Record("running", [Pkg("wardrobe", epoch: 2)]));
            var s = Read();
            var row = Assert.Single(s.Mods, m => m.Id == "wardrobe");
            Assert.Equal(("shroudforge", copy, record), (row.Engine, row.Folder, row.Active));
            Assert.DoesNotContain(s.Mods, m => m.Folder is { } f && ModCatalog.SamePath(f, lib));
        }
    }

    /// <summary>A ShroudForge package the profile has off, which kiln-eml lists (its row: off in the profile), moves to the
    /// ShroudForge group before the interpreter reports. Until the interpreter says something of its own about it, the row
    /// keeps kiln-eml's words (off in the profile), not "has not reported"; then the interpreter's (review of
    /// 0.2.0-dev.31.24).</summary>
    [Fact]
    public void A_moved_row_the_profile_has_off_keeps_kiln_emls_words_until_the_interpreter_reports()
    {
        var folder = Package(Path.Combine(Mods, "mineral-production-boxes"), "mineral-production-boxes", launcher: "EML", settings: Whitelist);
        Profile(("eml", "mineral-production-boxes", folder, false));
        WriteRaw("eml.json", JsonSerializer.Serialize(new
        {
            pid = Pid, updatedUnixMs = StartMs + 2000, phase = "done",
            mods = new[] { new { id = "mineral-production-boxes", name = "Mineral Production Boxes", version = "1.0.0", path = folder, source = "profile",
                state = "disabled", reason = "switched off in the profile" } },
        }));
        SfStatus("kiln");
        var row = Assert.Single(Read().Mods, m => m.Id == "mineral-production-boxes");
        Assert.Equal(("shroudforge", ModRunState.Disabled, folder), (row.Engine, row.State, row.Folder));
        Assert.Contains("off in the profile", row.Reason);
        Assert.NotNull(row.Sfi);
        WriteSfi(Record("running", [Pkg("mineral-production-boxes", intent: "disabled", epoch: 2)]));
        row = Assert.Single(Read().Mods, m => m.Id == "mineral-production-boxes");
        Assert.Equal(("shroudforge", ModRunState.Disabled), (row.Engine, row.State));
        Assert.Contains("Switched off for this start", row.Reason);
    }

    /// <summary>kiln-eml, the plan's data writer, failed to apply the compatibility entry's package: its EML row is the
    /// only place that failure shows, so the one-row rule leaves it (and the EML group's problem count) in place.</summary>
    [Fact]
    public void A_failed_eml_row_of_the_compatibility_entry_stays()
    {
        var lib = Package(Path.Combine(_root, "library", "eml", "wardrobe", "1.0.0"), "wardrobe", launcher: "EML", settings: Whitelist);
        var copy = Package(Path.Combine(Mods, "wardrobe"), "wardrobe", marker: "eml", launcher: "EML", settings: Whitelist);
        Profile(("eml", "wardrobe", lib, true), ("shroudforge", "wardrobe", lib, true));
        WriteRaw("eml.json", JsonSerializer.Serialize(new
        {
            pid = Pid, updatedUnixMs = StartMs + 2000, phase = "done",
            mods = new[] { new { id = "wardrobe", name = "Wardrobe", version = "1.0.0", path = lib, source = "profile", state = "failed",
                reason = "patch failed: the target container is missing" } },
        }));
        SfStatus("kiln");
        var s = Read();
        var eml = Assert.Single(s.Mods, m => m.Engine == "eml" && m.Id == "wardrobe");
        Assert.Equal(ModRunState.Failed, eml.State);
        Assert.Contains("target container is missing", eml.Reason);
        Assert.Equal(1, s.Engines.Single(e => e.Kind == "eml").Problems);
        Assert.Equal(copy, Assert.Single(s.Mods, m => m.Engine == "shroudforge" && m.Id == "wardrobe").Folder);
    }

    /// <summary>An EML package the interpreter runs from the mods root stays one EML row with the interpreter's truth; no
    /// second ShroudForge row for its folder.</summary>
    [Fact]
    public void An_eml_package_the_interpreter_runs_is_one_eml_row()
    {
        var folder = Package(Path.Combine(Mods, "eml-mod"), "eml-mod", marker: "eml", launcher: "EML");
        Profile(("eml", "eml-mod", folder, true));
        WriteRaw("eml.json", JsonSerializer.Serialize(new
        {
            pid = Pid, updatedUnixMs = StartMs + 2000, mode = "aside",
            mods = new[] { new { id = "eml-mod", name = "EML Mod", version = "1.0.0", path = folder, status = "own-loader", reason = "ShroudForge runs it" } },
        }));
        SfStatus("kiln");
        WriteSfi(Record("running", [Pkg("eml-mod", epoch: 4)]));
        var s = Read();
        var rows = s.Mods.Where(m => m.Folder is { } f && ModCatalog.SamePath(f, folder)).ToArray();
        var row = Assert.Single(rows);
        Assert.Equal("eml", row.Engine);
        Assert.Equal((ModRunState.Loaded, "active", true), (row.State, row.Word, row.Active));
        Assert.NotNull(row.Sfi);
        Assert.DoesNotContain(s.Mods, m => m.Engine == "shroudforge" && m.Id == "eml-mod");
    }

    /// <summary>Committed data with a stopped runtime is said as such (applied until restart), never "not applied"; an
    /// unknown archive stays unknown.</summary>
    [Fact]
    public void Committed_data_with_a_stopped_runtime_and_an_unknown_archive()
    {
        Package(Path.Combine(Mods, "late"), "late", caps: "patch");
        Package(Path.Combine(Mods, "ended"), "ended", caps: "patch");
        Package(Path.Combine(Mods, "unsure"), "unsure", caps: "patch");
        SfStatus("kiln");
        var skip = new JsonObject { ["code"] = "runtime-error", ["text"] = "init.lua:3: boom", ["part"] = "lua", ["chain"] = new JsonArray("base-lib"), ["late"] = true, ["loader"] = null };
        var warning = new JsonObject { ["revision"] = 2, ["trigger"] = "skipped", ["presence"] = "data-applied-runtime-stopped", ["saveSensitive"] = true, ["text"] = "Do not save while this mod is off; restore it and restart first." };
        WriteSfi(Record("running",
        [
            Pkg("late", data: "committed", lua: "stopped", archive: true, runtime: false, epoch: null, skip: skip, warning: warning, sensitive: true),
            Pkg("ended", data: "committed", lua: "stopped", archive: true, runtime: false, epoch: null),
            Pkg("unsure", data: "uncertain", lua: "not-needed", archive: null, runtime: false, epoch: null),
        ], revision: 2));
        var s = Read();
        var late = Row(s, "shroudforge", "late");
        Assert.Equal((ModRunState.Failed, "stopped"), (late.State, late.Word));
        Assert.Contains("init.lua:3: boom", late.Reason);
        Assert.Contains("Because of: base-lib", late.Reason);
        Assert.Contains("Do not save", late.Help);
        Assert.Contains(late.Sfi!.Lines, l => l.Text.Contains("Game data in the archive: yes", StringComparison.Ordinal));
        var ended = Row(s, "shroudforge", "ended");
        Assert.Equal((ModRunState.NotLoaded, "stopped"), (ended.State, ended.Word));
        Assert.Contains("stays applied until the game restarts", ended.Reason);
        var unsure = Row(s, "shroudforge", "unsure");
        Assert.Equal((ModRunState.Waiting, "unknown"), (unsure.State, unsure.Word));
        Assert.DoesNotContain("not applied", unsure.Reason + string.Join(" ", unsure.Sfi!.Lines.Select(l => l.Text)), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(unsure.Sfi!.Lines, l => l.Text.Contains("not known", StringComparison.Ordinal));
    }

    /// <summary>The switch in the package's file is what the next start asks for; the row says what runs now and what a
    /// restart changes. A switch turned off while the runtime still runs is not shown as off.</summary>
    [Fact]
    public void Requested_switch_against_what_runs_now()
    {
        Package(Path.Combine(Mods, "runs"), "runs", on: false);
        Package(Path.Combine(Mods, "data"), "data", on: false, caps: "patch");
        Package(Path.Combine(Mods, "wanted"), "wanted", on: true);
        SfStatus("kiln");
        WriteSfi(Record("running",
        [
            Pkg("runs", epoch: 2),
            Pkg("data", data: "committed", lua: "not-needed", archive: true, runtime: false, epoch: null),
            Pkg("wanted", intent: "disabled", lua: "not-needed", runtime: false, epoch: null),
        ]));
        var s = Read();
        var runs = Row(s, "shroudforge", "runs");
        Assert.Equal((ModRunState.Loaded, true), (runs.State, runs.Active));
        Assert.Contains("still runs until the game restarts", runs.Help);
        Assert.Contains(runs.Sfi!.Lines, l => l.Text == "Requested for the next start: off.");
        Assert.Contains("game data stays applied", Row(s, "shroudforge", "data").Help);
        var wanted = Row(s, "shroudforge", "wanted");
        Assert.Equal((ModRunState.Disabled, false), (wanted.State, wanted.Active));
        Assert.Contains("Switched on for the next start", wanted.Help);
    }

    /// <summary>The M behaviours kept in kiln mode: the row is the copy its engine runs (Kiln's placed copy in the mods
    /// root, not the profile's second copy), and a package with a native-plugin.ini is listed once, while a real XHL-Core
    /// plugin still shows.</summary>
    [Fact]
    public void The_row_is_the_copy_its_engine_runs_and_native_plugin_packages_are_listed_once()
    {
        var copy = Package(Path.Combine(Kiln, "profiles", "p", "shroudforge", "ut"), "ut", nativeIni: true);
        var placed = Package(Path.Combine(Mods, "ut"), "ut", marker: "shroudforge", nativeIni: true);
        var hand = Package(Path.Combine(Mods, "handini"), "handini", nativeIni: true);
        var xhl = Path.Combine(Mods, "XHL-Auto-Loot");
        Directory.CreateDirectory(xhl);
        File.WriteAllText(Path.Combine(xhl, "mod.json"), """{"id":"XHL-Auto-Loot","name":"XHL Auto Loot","version":"1.0.0"}""");
        File.WriteAllText(Path.Combine(xhl, "native-plugin.ini"), "[Plugin]\nEnabled=1\nDll=auto-loot.dll\n");
        Profile(("shroudforge", "ut", copy, true));
        Decoys();
        SfStatus("kiln");
        WriteSfi(Record("running", [Pkg("ut", epoch: 1), Pkg("handini", epoch: 1)]));
        var s = Read();
        var row = Row(s, "shroudforge", "ut");
        Assert.Equal(("profile", placed), (row.Source, row.Folder));
        Assert.Single(s.Mods, m => m.Folder is { } f && ModCatalog.SamePath(f, placed));
        Assert.Equal("shroudforge", Assert.Single(s.Mods, m => m.Folder is { } f && ModCatalog.SamePath(f, hand)).Engine);
        Assert.Contains(s.Mods, m => m.Engine == "xhl" && m.Folder is { } f && ModCatalog.SamePath(f, xhl));
        Assert.Equal(0, SfRootHits());
    }

    /// <summary>A planned kiln start that finds ShroudForge's own loader shows only that state: no row counts as the
    /// interpreter's, whatever a kiln record says.</summary>
    [Fact]
    public void External_shroudforge_under_a_kiln_plan_is_never_the_interpreters()
    {
        Package(Path.Combine(Mods, "a"), "a");
        SfStatus("kiln", mode: "self-hosted", reason: "ShroudForge's own loader (dwmapi.dll) runs it.");
        WriteSfi(Record("running", [Pkg("a", epoch: 5)]));
        var s = Read();
        var a = Row(s, "shroudforge", "a");
        Assert.Equal((ModRunState.NotLoaded, "external", false), (a.State, a.Word, a.Active));
        Assert.Equal(SfiBranchKind.External, a.Sfi!.Branch);
        Assert.Contains("dwmapi.dll", Group(s).Note);
        Assert.False(Group(s).Reported);
    }

    /// <summary>A refused start, a launch id that disagrees with kiln-shroudforge's, and packages only sfi.json names.</summary>
    [Fact]
    public void Refused_mismatched_and_record_only_packages()
    {
        Package(Path.Combine(Mods, "a"), "a");
        SfStatus("kiln");
        WriteSfi(Record("refused", [], reason: new JsonObject { ["code"] = "held", ["text"] = "kiln-sfi.interpreter is held" }));
        var s = Read();
        Assert.Equal(("refused", ModRunState.NotLoaded), (Row(s, "shroudforge", "a").Word, Row(s, "shroudforge", "a").State));
        Assert.Equal(EngineHealth.Problem, Group(s).Health);
        Assert.Contains("is held", Group(s).Note);

        // gone from the profile and the folder, active at the previous start: its save warning still shows
        var warning = new JsonObject { ["revision"] = 1, ["trigger"] = "save-sensitive-off", ["presence"] = "not-applied", ["saveSensitive"] = true, ["text"] = "Gone mod is off. Do not save." };
        WriteSfi(Record("running", [Pkg("gone", intent: "absent", lua: "not-needed", runtime: false, epoch: null, warning: warning, sensitive: true)]));
        var gone = Row(Read(), "shroudforge", "gone");
        Assert.Equal(("sfi", "removed", ModRunState.Disabled), (gone.Source, gone.Word, gone.State));
        Assert.Null(gone.Folder);
        Assert.Contains("Do not save", gone.Reason);
    }

    // --------------------------------------------------------------------------------------- the reader ----

    /// <summary>Every valid shared state fixture reads within the schema: kiln records as Kiln with every package, others
    /// as Aside (the reader is no stricter than the schema the writer is tested against).</summary>
    [Fact]
    public void Every_valid_state_fixture_reads_within_the_schema()
    {
        var dir = Path.Combine(global::Kiln.SfiBridgeTests.SfiFixtures.Root(), "state", "valid");
        var files = Directory.GetFiles(dir, "*.json");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var doc = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            var launch = doc["launch"]!.AsObject();
            launch["pid"] = Pid;
            launch["processStartMs"] = StartMs;
            using var parsed = JsonDocument.Parse(doc.ToJsonString());
            var r = SfiState.Parse(parsed.RootElement, Pid, Start);
            var selected = launch["backend"]!["selected"]!.GetValue<string>();
            if (selected == "kiln")
            {
                Assert.True(r.Start == SfiStart.Kiln, $"{Path.GetFileName(file)}: {r.Start} {r.Why}");
                Assert.Equal(doc["packages"]!.AsArray().Count, r.Record!.Packages.Count);
            }
            else Assert.Equal((Path.GetFileName(file), SfiStart.Aside), (Path.GetFileName(file), r.Start));
        }
    }

    /// <summary>The reader refuses what the schema refuses that would change a row: over the package cap, a warning
    /// revision above the record's, a skip on a disabled package, an ownerEpoch below 1, a FIFO or link in place of the file.</summary>
    [Fact]
    public void The_reader_holds_the_schemas_bounds()
    {
        Unknown(Record("running", Enumerable.Range(0, SfiState.MaxPackages + 1).Select(i => Pkg("p" + i))));
        var warning = new JsonObject { ["revision"] = 9, ["trigger"] = "skipped", ["presence"] = "not-applied", ["saveSensitive"] = false, ["text"] = "x" };
        var skip = new JsonObject { ["code"] = "dll-gate", ["text"] = "x", ["part"] = "dll", ["chain"] = new JsonArray(), ["late"] = false, ["loader"] = null };
        Unknown(Record("running", [Pkg("a", lua: "excluded", runtime: false, skip: skip, warning: warning)]));
        Unknown(Record("running", [Pkg("a", intent: "disabled", lua: "not-needed", runtime: false, skip: skip.DeepClone().AsObject(), warning: null)]));
        Unknown(Record("running", [Pkg("a", epoch: 0)]));
        Unknown(Record("running", [Pkg("a", data: "committed", archive: false)]));

        if (!OperatingSystem.IsWindows())
        {
            var path = Path.Combine(State, "sfi.json");
            File.Delete(path);
            var target = Path.Combine(_root, "elsewhere.json");
            File.WriteAllText(target, Record("running", [Pkg("a")]).ToJsonString());
            File.CreateSymbolicLink(path, target);
            Assert.Equal(SfiStart.Unknown, SfiState.Read(Src()).Start);
            File.Delete(path);
            var mkfifo = System.Diagnostics.Process.Start("mkfifo", path);
            mkfifo.WaitForExit();
            var read = Task.Run(() => SfiState.Read(Src()));
            Assert.True(read.Wait(TimeSpan.FromSeconds(10)), "a FIFO in place of sfi.json blocked the reader");
            Assert.Equal(SfiStart.Unknown, read.Result.Start);
        }
    }

    private CatalogSources Src() => new() { KilnRoot = Kiln, Pid = Pid, ProcessStartUtc = Start, Side = "client" };

    private void Unknown(JsonObject record)
    {
        WriteSfi(record);
        var r = SfiState.Read(Src());
        Assert.True(r.Start == SfiStart.Unknown, $"{r.Start}: {r.Why}");
        Assert.Null(r.Record);
    }
}
