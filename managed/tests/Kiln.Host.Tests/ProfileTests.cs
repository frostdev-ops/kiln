// SPDX-License-Identifier: MIT
// Kiln.Host with a profile (kiln.profile, docs/kiln/profiles.md): mod folders outside kiln/mods, drop-ins on/off,
// "disabled in profile", id mismatches, no-mods/invalid, the per-mod data root and state/mods.json.
using System.Text.Json.Nodes;
using Kiln.Host;
using Xunit;

namespace Kiln.Host.Tests;

public sealed class ProfileTests : ModsFixture
{
    private readonly string _library;
    private readonly string _stateFile;

    public ProfileTests()
    {
        _library = Directory.CreateDirectory(Path.Combine(Root, "library", "kiln")).FullName;
        _stateFile = Path.Combine(Root, "state", "mods.json");
    }

    /// <summary>A built sample in library/kiln/&lt;id&gt;/1.0.0+00000000 (like the launcher's library).</summary>
    private string Library(string project, string id)
    {
        var tmp = Install(project, "_staging-" + id);
        var dst = Path.Combine(_library, id, "1.0.0+00000000");
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        Directory.Move(tmp, dst);
        return dst;
    }

    private ModManager Manager(FakeHost host, HostProfile profile, string? data = null)
    {
        var m = new ModManager(host, ModsDir, data ?? DataDir, new ModManagerOptions
        {
            KilnVersion = host.Version, HasFeature = host.HasFeature, StateFile = Path.Combine(LogsDir, "mods.txt"),
            Profile = profile, ModsJsonFile = _stateFile,
        });
        ModsBackend.Current = m;
        return m;
    }

    private static HostProfile Active(bool dropins, params ProfileFolder[] folders) => new()
    {
        Mode = ProfileMode.Active, Id = "ember-run", Generation = 7, Folders = folders, Dropins = dropins,
    };

    private JsonObject State() => JsonNode.Parse(File.ReadAllText(_stateFile))!.AsObject();

    private Dictionary<string, JsonObject> StateMods() =>
        State()["mods"]!.AsArray().Select(m => m!.AsObject()).GroupBy(m => (string)m["id"]!)
            .ToDictionary(g => g.Key, g => g.First());  // loaded mods come first (a skipped id clash later)

    [Fact]
    public void Profile_folders_load_in_dependency_order_and_kiln_mods_stays_off()
    {
        var lib = Library("SampleLib", "sample-lib");
        var consumer = Library("SampleConsumer", "sample-consumer");
        var tests = Library("SampleTests", "sample-tests");
        InstallHello();  // kiln/mods/hello: a drop-in, off with dropins false
        var host = new FakeHost();
        // the launcher's order is not the load order: the resolver still sorts
        using var mods = Manager(host, Active(false, new("sample-tests", "1.0.0", tests, true),
            new("sample-consumer", null, consumer, true), new("sample-lib", "1.2.0", lib, true)));
        Assert.Equal([tests, consumer, lib], mods.Discover());
        mods.LoadAll();
        Assert.Equal(["sample-lib", "sample-consumer", "sample-tests"], mods.Mods.Select(m => m.Id));
        Assert.Null(Kiln.Mods.Get("hello"));
        Assert.True(host.Has("\"hello sample-consumer, from sample-lib 1.2.0\""), host.Dump());
        var state = State();
        Assert.Equal((1, "active", "ember-run", 7), ((int)state["format"]!, (string)state["profile"]!["mode"]!,
            (string)state["profile"]!["id"]!, (int)state["profile"]!["generation"]!));
        Assert.Equal(Environment.ProcessId, (int)state["pid"]!);
        Assert.True((int)state["generation"]! >= 1);
        var lib1 = StateMods()["sample-lib"];
        Assert.Equal(("profile", "loaded", lib, 0, "server", "none"), ((string)lib1["source"]!, (string)lib1["state"]!,
            (string)lib1["folder"]!, (int)lib1["loadIndex"]!, (string)lib1["side"]!, (string)lib1["clients"]!));
    }

    [Fact]
    public void Dropins_true_adds_kiln_mods_after_the_profile_and_the_profile_wins_an_id_clash()
    {
        var lib = Library("SampleLib", "sample-lib");
        Install("SampleLib", "sample-lib");  // the same id as a drop-in
        InstallHello();
        var host = new FakeHost();
        using var mods = Manager(host, Active(true, new ProfileFolder("sample-lib", null, lib, true)));
        mods.LoadAll();
        Assert.Equal(["hello", "sample-lib"], mods.Mods.Select(m => m.Id).Order());
        Assert.Equal(lib, mods.Find("sample-lib")!.Directory);
        var state = StateMods();
        Assert.Equal("dropin", (string)state["hello"]["source"]!);
        Assert.Equal("profile", (string)state["sample-lib"]["source"]!);
        var clash = State()["mods"]!.AsArray().Select(m => m!.AsObject())
            .Single(m => (string)m["folder"]! == Path.Combine(ModsDir, "sample-lib"));
        Assert.Equal("skipped", (string)clash["state"]!);
        Assert.Contains("the profile lists sample-lib too", (string)clash["reason"]!);
    }

    [Fact]
    public void Disabled_entries_are_skipped_and_their_dependents_fail()
    {
        var lib = Library("SampleLib", "sample-lib");
        var consumer = Library("SampleConsumer", "sample-consumer");
        var host = new FakeHost();
        using var mods = Manager(host, Active(false, new("sample-lib", null, lib, false), new("sample-consumer", null, consumer, true)));
        mods.LoadAll();
        Assert.Empty(mods.Mods);
        var libEntry = Kiln.Mods.Get("sample-lib")!;
        Assert.Equal((ModState.Skipped, "disabled in profile"), (libEntry.State, libEntry.Error));
        var c = Kiln.Mods.Get("sample-consumer")!;
        Assert.Equal(ModState.Failed, c.State);
        Assert.Contains("sample-lib", c.Error);
        Assert.Contains("disabled in profile", c.Error);
        Assert.Equal("skipped", (string)StateMods()["sample-lib"]["state"]!);
    }

    [Fact]
    public void A_manifest_id_that_differs_from_the_profile_entry_fails()
    {
        var hello = Library("Hello", "hello");
        var host = new FakeHost();
        using var mods = Manager(host, Active(false, new ProfileFolder("greeter", null, hello, true)));
        mods.LoadAll();
        Assert.Empty(mods.Mods);
        var e = Kiln.Mods.Get("hello")!;
        Assert.Equal(ModState.Failed, e.State);
        Assert.Equal("the profile lists it as 'greeter' but its kiln.mod.json says id 'hello'", e.Error);
        Assert.True(host.Has("not loaded: the profile lists it as 'greeter'"), host.Dump());
    }

    [Theory]
    [InlineData(3)]  // ProfileMode.NoMods
    [InlineData(4)]  // ProfileMode.Invalid
    public void No_mods_and_an_invalid_profile_load_nothing_not_even_kiln_mods(int modeValue)
    {
        var mode = (ProfileMode)modeValue;
        var lib = Library("SampleLib", "sample-lib");
        InstallHello();
        var host = new FakeHost();
        var profile = new HostProfile
        {
            Mode = mode, Folders = [new("sample-lib", null, lib, true)], Dropins = true,
            Error = mode == ProfileMode.Invalid ? "\"generation\" must be an integer >= 1" : null,
        };
        using var mods = Manager(host, profile);
        Assert.Empty(mods.Discover());
        mods.LoadAll();
        Assert.Empty(mods.Mods);
        Assert.Empty(Kiln.Mods.All);
        Assert.True(host.Has("mods: none load this start"), host.Dump());
        Assert.Equal(mode == ProfileMode.NoMods ? "no-mods" : "invalid", (string)State()["profile"]!["mode"]!);
    }

    [Fact]
    public void The_profile_data_dir_is_the_per_mod_data_root()
    {
        var hello = Library("Hello", "hello");
        var data = Path.Combine(Root, "profile-data");
        var host = new FakeHost();
        using var mods = Manager(host, Active(false, new ProfileFolder("hello", null, hello, true)), data);
        mods.LoadAll();
        Assert.Equal(Path.Combine(data, "hello"), mods.Find("hello")!.ModContext.DataDirectory);
    }

    [Fact]
    public void Legacy_layout_is_unchanged()
    {
        InstallHello();
        var host = new FakeHost();
        using var mods = Manager(host, HostProfile.Legacy);
        Assert.Equal([Path.Combine(ModsDir, "hello")], mods.Discover());
        mods.LoadAll();
        Assert.Equal(["hello"], mods.Mods.Select(m => m.Id));
        Assert.Equal(("legacy", "dropin"), ((string)State()["profile"]!["mode"]!, (string)StateMods()["hello"]["source"]!));
        Assert.Equal(Path.Combine(DataDir, "hello"), mods.Find("hello")!.ModContext.DataDirectory);
    }

    [Fact]
    public void Reload_of_a_never_loaded_profile_mod_finds_its_folder()
    {
        var hello = Library("Hello", "hello");
        var host = new FakeHost();
        using var mods = Manager(host, Active(false, new ProfileFolder("hello", null, hello, true)));
        // nothing loaded yet (as before the first step); reload looks the id up in the profile
        var r = mods.ReloadCore("hello");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(hello, mods.Find("hello")!.Directory);
    }
}
