// SPDX-License-Identifier: MIT
// Offline config schema snapshots (contracts.md 5.5, schema/config-schema.schema.json) and configs in a profile's
// configDir (KilnPaths.Config).
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.Api.Tests;

public sealed class SnapshotConfigV2
{
    [Comment("How many homes a player may set."), Range(1, 50)]
    public int MaxHomes { get; set; } = 5;

    public bool Extra { get; set; } = true;
}

public sealed class ConfigSnapshotTests : KilnTest
{
    private string Snapshot(string id) => Path.Combine(Root, "config", ".schema", id + ".json");

    [Fact]
    public void Register_writes_the_schema_without_values()
    {
        var mod = NewMod("snapmod");
        var cfg = As(mod, () => Config.Register<TestConfig>(ConfigKind.Server));
        cfg.Set("maxHomes", 9);
        var file = Snapshot("snapmod-server");
        Assert.True(File.Exists(file), file);
        Assert.Equal(file, cfg.SnapshotPath);
        var snap = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.Equal(1, (int)snap["format"]!);
        Assert.Equal("kiln", (string)snap["source"]!);
        Assert.Equal("snapmod-server.json", (string)snap["file"]!);
        Assert.Equal("jsonc", (string)snap["valueFormat"]!);
        Assert.Equal(("snapmod-server", "snapmod", "1.0.0", "server"),
            ((string)snap["id"]!, (string)snap["mod"]!, (string)snap["modVersion"]!, (string)snap["kind"]!));
        Assert.StartsWith("sha256:", (string)snap["schemaHash"]!);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", (string)snap["written"]!);
        var entries = snap["entries"]!.AsArray().Select(e => e!.AsObject()).ToDictionary(e => (string)e["path"]!);
        Assert.Equal(["maxHomes", "greeting", "difficulty", "cooldown", "port", "banned", "pvp.enabled", "pvp.damageFactor"],
            entries.Keys);
        var homes = entries["maxHomes"];
        Assert.Equal(("int", 3, 1.0, 50.0, "How many homes a player may set."),
            ((string)homes["type"]!, (int)homes["default"]!, (double)homes["min"]!, (double)homes["max"]!, (string)homes["comment"]!));
        Assert.False(homes.ContainsKey("value"));
        Assert.False(homes.ContainsKey("fileValue"));
        Assert.Equal(["Easy", "Normal", "Hard"], entries["difficulty"]["options"]!.AsArray().Select(x => (string)x!));
        Assert.Equal("30s", (string)entries["cooldown"]["default"]!);
        Assert.True((bool)entries["port"]["requiresRestart"]!);
        Assert.Equal("list<string>", (string)entries["banned"]["type"]!);
        Assert.True((bool)entries["pvp.damageFactor"]["synced"]!);
        Assert.Equal("pvp", (string)snap["sections"]![0]!["path"]!);
        Assert.Equal("PvP rules.", (string)snap["sections"]![0]!["comment"]!);
        Assert.False(File.Exists(file + ".tmp"));
    }

    [Fact]
    public void Reloading_the_same_class_keeps_the_snapshot_and_a_changed_class_rewrites_it()
    {
        var mod = NewMod("reloadmod");
        As(mod, () => Config.Register<TestConfig>());
        var file = Snapshot("reloadmod");
        var first = File.ReadAllText(file);
        File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        mod.Retire();  // the mod unloads; its config leaves the registry

        var again = NewMod("reloadmod");
        As(again, () => Config.Register<TestConfig>());
        Assert.Equal(first, File.ReadAllText(file));
        Assert.Equal(2020, File.GetLastWriteTimeUtc(file).Year);  // not rewritten
        again.Retire();

        var changed = NewMod("reloadmod");
        As(changed, () => Config.Register<SnapshotConfigV2>());
        var snap = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.NotEqual((string)JsonNode.Parse(first)!["schemaHash"]!, (string)snap["schemaHash"]!);
        Assert.Equal(["maxHomes", "extra"], snap["entries"]!.AsArray().Select(e => (string)e!["path"]!));
        Assert.Equal(5, (int)snap["entries"]![0]!["default"]!);
    }

    [Fact]
    public void Configs_and_snapshots_follow_the_profile_config_dir()
    {
        var profileConfig = Path.Combine(Path.GetDirectoryName(Root)!, "profiles", "ember-run", "config");
        KilnPaths.Config = profileConfig;
        var mod = NewMod("profmod");
        var cfg = As(mod, () => Config.Register<TestConfig>());
        Assert.Equal(Path.Combine(profileConfig, "profmod.json"), cfg.FilePath);
        Assert.True(File.Exists(Path.Combine(profileConfig, "profmod.json")));
        Assert.True(File.Exists(Path.Combine(profileConfig, ".schema", "profmod.json")));
        Assert.False(File.Exists(Path.Combine(Root, "config", "profmod.json")));
        Assert.Equal(profileConfig, Config.Directory);
    }

    [Fact]
    public void A_corrupt_snapshot_is_written_again()
    {
        var file = Snapshot("corruptmod");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ not json");
        var mod = NewMod("corruptmod");
        As(mod, () => Config.Register<TestConfig>());
        Assert.Equal("corruptmod", (string)JsonNode.Parse(File.ReadAllText(file))!["id"]!);
    }
}
