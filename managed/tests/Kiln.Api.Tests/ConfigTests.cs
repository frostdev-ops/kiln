// SPDX-License-Identifier: MIT
using System.Text.Json;
using Xunit;

namespace Kiln.Api.Tests;

public enum Difficulty
{
    Easy,
    Normal,
    Hard,
}

[Comment("PvP rules.")]
public sealed class PvpSection
{
    [Comment("Players may hurt each other.")]
    public bool Enabled { get; set; }

    [Range(0, 10), Synced]
    public double DamageFactor { get; set; } = 1.0;
}

public sealed class TestConfig
{
    [Comment("How many homes a player may set.")]
    [Range(1, 50)]
    public int MaxHomes { get; set; } = 3;

    public string Greeting { get; set; } = "Welcome!";

    public Difficulty Difficulty { get; set; } = Difficulty.Normal;

    [Range(1, 3600)]
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(30);

    [RequiresRestart]
    public int Port { get; set; } = 8080;

    public List<string> Banned { get; set; } = ["griefer"];

    public PvpSection Pvp { get; set; } = new();

    [ConfigIgnore]
    public int NotInFile { get; set; } = 7;
}

public sealed class ConfigTests : KilnTest
{
    private string File(string id) => Path.Combine(Root, "config", id + ".json");

    private ModConfig<TestConfig> Register(ModContext mod, ConfigKind kind = ConfigKind.Common) =>
        As(mod, () => Config.Register<TestConfig>(kind));

    [Fact]
    public void Reset_discards_the_previous_watchers_future_poll_deadline()
    {
        Register(NewMod("old"));
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        typeof(Config).GetField("_nextPoll", flags)!.SetValue(null, Environment.TickCount64 + 60_000);
        typeof(Config).GetField("_signalStep", flags)!.SetValue(null, 9000UL);
        typeof(Config).GetField("_signal", flags)!.SetValue(null, true);
        Config.Reset();
        var fresh = Register(NewMod("fresh"));
        ConfigChangedEvent? changed = null;
        fresh.Changed += value => changed = value;
        var text = System.IO.File.ReadAllText(File("fresh")).Replace("\"maxHomes\": 3", "\"maxHomes\": 12");
        System.IO.File.WriteAllText(File("fresh"), text);
        System.IO.File.SetLastWriteTimeUtc(File("fresh"), DateTime.UtcNow.AddSeconds(5));
        Step(); // first poll must run even before the new watcher's two-step debounce is due
        Assert.NotNull(changed);
        Assert.Contains("maxHomes", changed!.Changed);
    }

    [Fact]
    public void First_load_writes_a_commented_file_with_the_defaults()
    {
        var mod = NewMod("cfgmod");
        var cfg = Register(mod);
        Assert.Equal("cfgmod", cfg.Id);
        var text = System.IO.File.ReadAllText(File("cfgmod"));
        Assert.Contains("// How many homes a player may set.", text);
        Assert.Contains("Range: 1 .. 50", text);
        Assert.Contains("\"maxHomes\": 3", text);
        Assert.Contains("\"pvp\": {", text);
        Assert.Contains("// PvP rules.", text);
        Assert.Contains("One of: Easy, Normal, Hard", text);
        Assert.Contains("Requires a restart", text);
        Assert.Contains("\"cooldown\": \"30s\"", text);
        Assert.DoesNotContain("notInFile", text);
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        Assert.Equal(3, doc.RootElement.GetProperty("maxHomes").GetInt32());
        Assert.Equal("cfgmod-server", Register(mod, ConfigKind.Server).Id);
    }

    [Fact]
    public void Existing_values_are_kept_clamped_and_unknown_keys_preserved()
    {
        System.IO.File.WriteAllText(File("keep"), """
            // my notes
            {
              "maxHomes": 99,
              "greeting": "Hi there",
              "difficulty": "hard",
              "cooldown": "2m",
              "banned": ["a", "b"],
              "pvp": { "enabled": true, "damageFactor": -3 },
              "mystery": { "x": 1 },
            }
            """);
        var cfg = Register(NewMod("keep"));
        Assert.Equal(50, cfg.Value.MaxHomes);
        Assert.Equal("Hi there", cfg.Value.Greeting);
        Assert.Equal(Difficulty.Hard, cfg.Value.Difficulty);
        Assert.Equal(TimeSpan.FromMinutes(2), cfg.Value.Cooldown);
        Assert.Equal(["a", "b"], cfg.Value.Banned);
        Assert.True(cfg.Value.Pvp.Enabled);
        Assert.Equal(0, cfg.Value.Pvp.DamageFactor);
        Assert.Contains(cfg.Warnings, w => w.Contains("maxHomes") && w.Contains("using 50"));
        var text = System.IO.File.ReadAllText(File("keep"));
        Assert.Contains("\"maxHomes\": 50", text);
        Assert.Contains("\"mystery\"", text);
        Assert.Contains("Unknown key", text);
        Assert.True(Backend.Logged("unknown key(s) kept: mystery"));
    }

    [Fact]
    public void Bad_values_fall_back_to_the_default_with_a_warning()
    {
        System.IO.File.WriteAllText(File("bad"), """{ "maxHomes": "lots", "difficulty": "Nightmare", "pvp": 5 }""");
        var cfg = Register(NewMod("bad"));
        Assert.Equal(3, cfg.Value.MaxHomes);
        Assert.Equal(Difficulty.Normal, cfg.Value.Difficulty);
        Assert.Equal(3, cfg.Warnings.Count);
    }

    [Fact]
    public void Broken_json_keeps_the_values_and_the_file()
    {
        var cfg = Register(NewMod("broken"));
        cfg.Set("maxHomes", 7);
        System.IO.File.WriteAllText(File("broken"), "{ \"maxHomes\": 9, oops");
        Config.Poll();
        Assert.Equal(7, cfg.Value.MaxHomes);
        Assert.NotNull(cfg.LastError);
        Assert.Equal("{ \"maxHomes\": 9, oops", System.IO.File.ReadAllText(File("broken")));
        System.IO.File.WriteAllText(File("broken"), "{ \"maxHomes\": 9 }");
        Config.Poll();
        Assert.Equal(9, cfg.Value.MaxHomes);
        Assert.Null(cfg.LastError);
    }

    [Fact]
    public void Editing_the_file_raises_the_change_event_on_the_service_tick()
    {
        var mod = NewMod("live");
        var cfg = Register(mod);
        ConfigChangedEvent? local = null, bus = null;
        string? handlerMod = null;
        As(mod, () => cfg.Changed += e =>
        {
            local = e;
            handlerMod = ModContext.CurrentMod?.Info.Id;
        });
        Events.On<ConfigChangedEvent>(e => bus = e);
        var text = System.IO.File.ReadAllText(File("live"))
            .Replace("\"maxHomes\": 3", "\"maxHomes\": 12")
            .Replace("\"port\": 8080", "\"port\": 9090")
            .Replace("\"enabled\": false", "\"enabled\": true");
        System.IO.File.WriteAllText(File("live"), text);
        System.IO.File.SetLastWriteTimeUtc(File("live"), DateTime.UtcNow.AddSeconds(5));
        Step(26);  // polling fallback (watcher signals may come too)
        Assert.NotNull(local);
        Assert.Same(local, bus);
        Assert.Equal("live", handlerMod);
        Assert.Equal(ConfigChangeSource.File, local!.Source);
        Assert.Contains("maxHomes", local.Changed);
        Assert.Contains("pvp.enabled", local.Changed);
        Assert.Equal(["port"], local.PendingRestart);
        Assert.Equal(12, cfg.Value.MaxHomes);
        Assert.Equal(8080, cfg.Value.Port);  // requires restart: running value stays
        Assert.Equal(["port"], cfg.PendingRestart);
        Assert.True(cfg.Value.Pvp.Enabled);
    }

    [Fact]
    public void Set_validates_writes_and_notifies()
    {
        var cfg = Register(NewMod("setter"));
        ConfigChangedEvent? ev = null;
        cfg.Changed += e => ev = e;
        Assert.False(cfg.TrySet("maxHomes", 500, out var err));
        Assert.Contains("1..50", err);
        Assert.False(cfg.TrySet("nope", 1, out err));
        Assert.True(cfg.TrySet("maxHomes", "20", out _));
        Assert.Equal(20, cfg.Value.MaxHomes);
        Assert.Equal(ConfigChangeSource.Api, ev!.Source);
        cfg.Set("difficulty", "easy");
        cfg.Set("cooldown", "1h");
        cfg.Set("banned", "[\"x\", \"y\"]");
        cfg.Set("pvp.damageFactor", JsonDocument.Parse("2.5").RootElement);
        cfg.Set("port", 1);
        Assert.Equal(Difficulty.Easy, cfg.Value.Difficulty);
        Assert.Equal(TimeSpan.FromHours(1), cfg.Value.Cooldown);
        Assert.Equal(["x", "y"], cfg.Value.Banned);
        Assert.Equal(2.5, cfg.Value.Pvp.DamageFactor);
        Assert.Equal(8080, cfg.Value.Port);
        Assert.Equal(1, cfg.Entries.Single(e => e.Path == "port").FileValue);
        var text = System.IO.File.ReadAllText(File("setter"));
        Assert.Contains("\"maxHomes\": 20", text);
        Assert.Contains("\"port\": 1", text);
        Assert.Contains("\"cooldown\": \"1h\"", text);
        Assert.Equal(20, cfg.Get("maxHomes"));
    }

    [Fact]
    public void Code_changes_are_saved_and_survive_a_reregistration()
    {
        var mod = NewMod("saver");
        var cfg = Register(mod);
        cfg.Value.Greeting = "changed in code";
        cfg.Save();
        mod.Retire();
        Assert.Null(Config.Find("saver"));
        var again = Register(NewMod("saver"));
        Assert.Equal("changed in code", again.Value.Greeting);
    }

    [Fact]
    public void Schema_lists_entries_with_ranges_and_synced_values()
    {
        var cfg = Register(NewMod("schema"));
        var schema = cfg.Schema();
        var entries = schema["entries"]!.AsArray();
        var homes = entries.Single(e => e!["path"]!.GetValue<string>() == "maxHomes")!;
        Assert.Equal("int", homes["type"]!.GetValue<string>());
        Assert.Equal(1, homes["min"]!.GetValue<double>());
        Assert.Equal(50, homes["max"]!.GetValue<double>());
        Assert.Contains(entries, e => e!["path"]!.GetValue<string>() == "pvp.damageFactor" && e["synced"]!.GetValue<bool>());
        Assert.Equal("""{"pvp":{"damageFactor":1}}""", cfg.SyncedValuesJson());
        Assert.Single(Config.Schema());
        Assert.Same(cfg, Config.Find("SCHEMA"));
    }

    [Fact]
    public void Durations_parse_and_format()
    {
        Assert.True(Durations.TryParse("1h30m", out var a));
        Assert.Equal(TimeSpan.FromMinutes(90), a);
        Assert.True(Durations.TryParse("250ms", out var b));
        Assert.Equal(TimeSpan.FromMilliseconds(250), b);
        Assert.True(Durations.TryParse("00:05:00", out var c));
        Assert.Equal(TimeSpan.FromMinutes(5), c);
        Assert.True(Durations.TryParse("90", out var d));
        Assert.Equal(TimeSpan.FromSeconds(90), d);
        Assert.False(Durations.TryParse("5 parsecs", out _));
        Assert.Equal("1h30m", Durations.Format(TimeSpan.FromMinutes(90)));
        Assert.Equal("2d3s", Durations.Format(TimeSpan.FromDays(2) + TimeSpan.FromSeconds(3)));
    }
}
