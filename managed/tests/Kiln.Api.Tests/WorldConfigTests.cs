// SPDX-License-Identifier: MIT
using System.Text.Json;
using Xunit;

namespace Kiln.Api.Tests;

/// <summary>Per-world mod configs (Config.RegisterWorld, Mod.WorldConfig): a file per world in the world's sidecar folder, values
/// that follow the open world. Kiln.Saves tells the API which folder is open; these tests play its part.</summary>
public sealed class WorldConfigTests : KilnTest
{
    private string WorldDir(string id) => Path.Combine(Root, "data", "worlds", id);

    private string FileOf(string world, string id) => Path.Combine(WorldDir(world), "config", id + ".json");

    private static WorldConfig<TestConfig> Register(ModContext mod, string? name = null) =>
        As(mod, () => Config.RegisterWorld<TestConfig>(name));

    /// <summary>What Kiln.Saves does when a world opens, closes or gets its id.</summary>
    private static void Open(string? dir) => WorldConfigs.Announce(dir);

    [Fact]
    public void Without_a_world_the_defaults_run_in_memory_and_no_file_is_written()
    {
        var cfg = Register(NewMod("wcnone"));
        Assert.Equal("wcnone-world", cfg.Id);
        Assert.True(cfg.PerWorld);
        Assert.Equal(ConfigKind.Server, cfg.Kind);
        Assert.Null(cfg.WorldId);
        Assert.Equal("", cfg.FilePath);
        Assert.Equal(3, cfg.Value.MaxHomes);
        cfg.Set("maxHomes", 9);  // memory only
        Assert.Equal(9, cfg.Value.MaxHomes);
        Assert.False(Directory.Exists(Path.Combine(Root, "data")));
        Config.Poll();  // nothing to read
        Assert.Equal(9, cfg.Value.MaxHomes);
    }

    [Fact]
    public void A_world_gets_a_commented_file_of_its_own_beside_the_world_data()
    {
        var cfg = Register(NewMod("wcfile"));
        Open(WorldDir("aaaa"));
        Assert.Equal("aaaa", cfg.WorldId);
        Assert.Equal(FileOf("aaaa", "wcfile-world"), cfg.FilePath);
        var text = File.ReadAllText(cfg.FilePath);
        Assert.Contains("// How many homes a player may set.", text);
        Assert.Contains("\"maxHomes\": 3", text);
        Assert.Contains("for the world aaaa", text);
        Assert.Contains("Every world has its own", text);
        Assert.False(File.Exists(Path.Combine(Root, "config", "wcfile-world.json")));  // not in the global config folder
    }

    [Fact]
    public void Values_follow_the_open_world_and_come_back_with_it()
    {
        var mod = NewMod("wcswitch");
        var cfg = Register(mod);
        var events = new List<ConfigChangedEvent>();
        As(mod, () => cfg.Changed += events.Add);
        Open(WorldDir("aaaa"));
        Assert.Empty(events);  // a fresh world with the defaults changes nothing
        cfg.Set("maxHomes", 12);
        cfg.Set("greeting", "Hello A");
        cfg.Set("pvp.enabled", true);
        events.Clear();

        Open(WorldDir("bbbb"));  // a hosted game's next world
        Assert.Equal("bbbb", cfg.WorldId);
        Assert.Equal(3, cfg.Value.MaxHomes);
        Assert.Equal("Welcome!", cfg.Value.Greeting);
        Assert.False(cfg.Value.Pvp.Enabled);
        var ev = Assert.Single(events);
        Assert.Equal(ConfigChangeSource.World, ev.Source);
        Assert.True(ev.Has("maxHomes") && ev.Has("greeting") && ev.Has("pvp.enabled"));
        Assert.Equal(3, ev.Changed.Count);
        cfg.Set("maxHomes", 40);
        Assert.Contains("\"maxHomes\": 12", File.ReadAllText(FileOf("aaaa", "wcswitch-world")));  // A's file is left alone
        Assert.Contains("\"maxHomes\": 40", File.ReadAllText(FileOf("bbbb", "wcswitch-world")));
        events.Clear();

        Open(WorldDir("aaaa"));  // back to A in the same process
        Assert.Equal(12, cfg.Value.MaxHomes);
        Assert.Equal("Hello A", cfg.Value.Greeting);
        Assert.True(cfg.Value.Pvp.Enabled);
        ev = Assert.Single(events);
        Assert.Equal(ConfigChangeSource.World, ev.Source);
        Assert.True(ev.Has("maxHomes") && ev.Has("greeting") && ev.Has("pvp.enabled"));
        Assert.Equal("aaaa", cfg.WorldId);
    }

    [Fact]
    public void Only_entries_that_differ_are_reported()
    {
        var mod = NewMod("wcdiff");
        var cfg = Register(mod);
        Open(WorldDir("aaaa"));
        cfg.Set("maxHomes", 7);
        Directory.CreateDirectory(Path.Combine(WorldDir("bbbb"), "config"));
        File.WriteAllText(FileOf("bbbb", "wcdiff-world"), """{ "maxHomes": 7, "greeting": "B" }""");
        ConfigChangedEvent? ev = null;
        As(mod, () => cfg.Changed += e => ev = e);
        Open(WorldDir("bbbb"));
        Assert.Equal(["greeting"], ev!.Changed);  // maxHomes is 7 in both worlds
        Assert.Equal(ConfigChangeSource.World, ev.Source);
    }

    [Fact]
    public void Closing_the_world_puts_the_defaults_back_and_keeps_the_file()
    {
        var cfg = Register(NewMod("wcclose"));
        Open(WorldDir("aaaa"));
        cfg.Set("maxHomes", 21);
        Open(null);  // the hosted world closed
        Assert.Null(cfg.WorldId);
        Assert.Equal("", cfg.FilePath);
        Assert.Equal(3, cfg.Value.MaxHomes);
        cfg.Set("maxHomes", 5);  // between worlds: memory only, nothing of A is touched
        Assert.Contains("\"maxHomes\": 21", File.ReadAllText(FileOf("aaaa", "wcclose-world")));
        Open(WorldDir("aaaa"));  // the file wins over what was set between worlds
        Assert.Equal(21, cfg.Value.MaxHomes);
    }

    [Fact]
    public void A_new_world_keeps_what_was_set_before_it_had_an_id()
    {
        var cfg = Register(NewMod("wcnew"));
        cfg.Set("greeting", "set before the first save");
        Open(WorldDir("cccc"));  // the id arrives with the first save
        Assert.Equal("set before the first save", cfg.Value.Greeting);
        Assert.Contains("set before the first save", File.ReadAllText(FileOf("cccc", "wcnew-world")));
    }

    [Fact]
    public void An_edit_of_the_worlds_file_is_picked_up_and_a_restart_entry_waits_for_the_next_world_load()
    {
        var mod = NewMod("wcedit");
        var cfg = Register(mod);
        Open(WorldDir("aaaa"));
        ConfigChangedEvent? ev = null;
        As(mod, () => cfg.Changed += e => ev = e);
        var path = FileOf("aaaa", "wcedit-world");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"maxHomes\": 3", "\"maxHomes\": 11").Replace("\"port\": 8080", "\"port\": 9090"));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Config.Poll();
        Assert.Equal(11, cfg.Value.MaxHomes);
        Assert.Equal(8080, cfg.Value.Port);
        Assert.Equal(["port"], cfg.PendingRestart);
        Assert.Equal(ConfigChangeSource.File, ev!.Source);
        Open(WorldDir("bbbb"));
        Open(WorldDir("aaaa"));  // loading the world again applies it
        Assert.Equal(9090, cfg.Value.Port);
        Assert.Empty(cfg.PendingRestart);
    }

    [Fact]
    public void Keys_the_mod_does_not_know_belong_to_their_world()
    {
        var cfg = Register(NewMod("wcunknown"));
        Directory.CreateDirectory(Path.Combine(WorldDir("aaaa"), "config"));
        File.WriteAllText(FileOf("aaaa", "wcunknown-world"), """{ "maxHomes": 4, "mystery": 1 }""");
        Open(WorldDir("aaaa"));
        Assert.Contains("\"mystery\"", File.ReadAllText(cfg.FilePath));
        Open(WorldDir("bbbb"));
        Assert.DoesNotContain("mystery", File.ReadAllText(cfg.FilePath));
        Assert.Contains("\"mystery\"", File.ReadAllText(FileOf("aaaa", "wcunknown-world")));
    }

    [Fact]
    public void A_config_that_registers_after_the_world_opened_binds_at_once()
    {
        Open(WorldDir("aaaa"));
        WorldConfigs.DirectoryProvider = () => WorldDir("aaaa");
        var cfg = Register(NewMod("wclate"));
        Assert.Equal("aaaa", cfg.WorldId);
        Assert.True(File.Exists(FileOf("aaaa", "wclate-world")));
    }

    [Fact]
    public void The_provider_is_asked_when_a_config_registers()
    {
        var asked = 0;
        WorldConfigs.DirectoryProvider = () =>
        {
            asked++;
            return WorldDir("pppp");
        };
        var cfg = Register(NewMod("wcprov"));
        Assert.Equal(1, asked);
        Assert.Equal("pppp", cfg.WorldId);
    }

    [Fact]
    public void Registering_twice_is_refused_and_unloading_the_mod_removes_the_config()
    {
        var mod = NewMod("wctwice");
        var cfg = Register(mod);
        Assert.Throws<InvalidOperationException>(() => Register(mod));
        Assert.Equal("wctwice-extra-world", Register(mod, "extra").Id);
        Assert.Same(cfg, Config.Find("wctwice-world"));
        mod.Retire();
        Assert.Null(Config.Find("wctwice-world"));
        Assert.Null(Config.Find("wctwice-extra-world"));
    }

    [Fact]
    public void A_mod_unloading_does_not_get_a_world_change()
    {
        var mod = NewMod("wcgone");
        Register(mod);
        var count = 0;
        As(mod, () => Config.Find("wcgone-world")!.Changed += _ => count++);
        mod.Retire();
        Open(WorldDir("aaaa"));
        Assert.Equal(0, count);
    }

    [Fact]
    public void Per_world_and_global_configs_of_one_mod_live_side_by_side()
    {
        var mod = NewMod("wcboth");
        var global = As(mod, () => Config.Register<TestConfig>(ConfigKind.Server));
        var world = Register(mod);
        Assert.Equal("wcboth-server", global.Id);
        Assert.Equal("wcboth-world", world.Id);
        Open(WorldDir("aaaa"));
        world.Set("maxHomes", 30);
        Assert.Equal(3, global.Value.MaxHomes);
        Assert.False(global.PerWorld);
        Assert.Equal(Path.Combine(Root, "config", "wcboth-server.json"), global.FilePath);
        Assert.Equal(["wcboth-server", "wcboth-world"], Config.All.Select(f => f.Id).ToArray());
    }

    [Fact]
    public void Synced_entries_of_a_world_config_are_what_a_server_sends()
    {
        var cfg = Register(NewMod("wcsync"));
        Open(WorldDir("aaaa"));
        cfg.Set("pvp.damageFactor", 2.5);
        using var doc = JsonDocument.Parse(cfg.SyncedValuesJson());
        Assert.Equal(2.5, doc.RootElement.GetProperty("pvp").GetProperty("damageFactor").GetDouble());
        Assert.False(doc.RootElement.TryGetProperty("maxHomes", out _));
        Open(WorldDir("bbbb"));
        using var other = JsonDocument.Parse(cfg.SyncedValuesJson());
        Assert.Equal(1.0, other.RootElement.GetProperty("pvp").GetProperty("damageFactor").GetDouble());
    }

    [Fact]
    public void The_schema_says_which_world_the_values_are_of()
    {
        var cfg = Register(NewMod("wcschema"));
        var before = cfg.Schema();
        Assert.True(before["perWorld"]!.GetValue<bool>());
        Assert.Null(before["world"]);
        Open(WorldDir("dddd"));
        var after = cfg.Schema();
        Assert.Equal("dddd", after["world"]!.GetValue<string>());
        Assert.Equal(cfg.FilePath, after["file"]!.GetValue<string>());
        Assert.False(File.Exists(cfg.SnapshotPath));  // the launcher edits global files; a world's has no offline snapshot
    }

    [Fact]
    public void A_broken_file_of_one_world_does_not_follow_to_the_next()
    {
        var cfg = Register(NewMod("wcbroken"));
        Directory.CreateDirectory(Path.Combine(WorldDir("aaaa"), "config"));
        File.WriteAllText(FileOf("aaaa", "wcbroken-world"), "{ oops");
        Open(WorldDir("aaaa"));
        Assert.NotNull(cfg.LastError);
        Assert.Equal(3, cfg.Value.MaxHomes);
        Assert.Equal("{ oops", File.ReadAllText(cfg.FilePath));  // kept for the admin to fix
        Open(WorldDir("bbbb"));
        Assert.Null(cfg.LastError);
        Assert.Contains("\"maxHomes\": 3", File.ReadAllText(cfg.FilePath));
    }

    /// <summary>A mod class, to try <see cref="Mod.WorldConfig{T}"/>.</summary>
    private sealed class SettingsMod : Mod
    {
        public WorldConfig<TestConfig> Cfg() => WorldConfig<TestConfig>();
        public WorldConfig<TestConfig> Named() => WorldConfig<TestConfig>("alt");
    }

    [Fact]
    public void Mod_WorldConfig_registers_once_for_the_calling_mod()
    {
        var ctx = NewMod("wcmod");
        var mod = new SettingsMod();
        HostAccess.Bind(mod, ctx);
        var cfg = mod.Cfg();
        Assert.Same(cfg, mod.Cfg());
        Assert.Equal("wcmod-world", cfg.Id);
        Assert.Equal("wcmod-alt-world", mod.Named().Id);
        Assert.Equal("wcmod", cfg.ModId);
        Open(WorldDir("aaaa"));
        Assert.True(File.Exists(FileOf("aaaa", "wcmod-world")));
        Assert.Equal(2, Config.All.Count(f => f.ModId == "wcmod"));
    }

    [Fact]
    public void Mod_WorldConfig_keeps_its_tick_thread_guard_after_caching()
    {
        var mod = new SettingsMod();
        HostAccess.Bind(mod, NewMod("wcguard"));
        mod.Cfg();
        Backend.TickThread = false;
        try { Assert.Throws<InvalidOperationException>(() => mod.Cfg()); }
        finally { Backend.TickThread = true; }
    }

    [Fact]
    public void Config_from_outside_a_mod_needs_a_name()
    {
        Assert.Throws<InvalidOperationException>(() => Config.RegisterWorld<TestConfig>());
        Assert.Equal("kiln-w-world", Config.RegisterWorld<TestConfig>("kiln-w").Id);
        Assert.Throws<ArgumentException>(() => Config.RegisterWorld<TestConfig>("Bad Name"));
    }
}
