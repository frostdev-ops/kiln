// SPDX-License-Identifier: MIT
// kiln#155 (fafawanji): [Range(min, max, step, SoftMin/SoftMax = true)]: a soft side ends the slider only.
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.Api.Tests;

public sealed class SoftRangeConfig
{
    [Range(0, 1, 0.1, SoftMax = true)] public double CostMultiplier { get; set; } = 1;
    [Range(0, 10, SoftMin = true)] public int Offset { get; set; } = 2;
    [Range(0, 5, 1, SoftMin = true, SoftMax = true)] public int Both { get; set; } = 1;
    [Range(0, 1)] public double Hard { get; set; } = 0.5;
    [Range(1, 60, SoftMax = true)] public TimeSpan Wait { get; set; } = TimeSpan.FromSeconds(5);
    [Range(0, 1, SoftMax = true)] public List<double> Factors { get; set; } = [0.5];
    [Range(0, 100, SoftMax = true)] public byte Small { get; set; } = 10;
}

public sealed class SoftDecimalConfig
{
    [Range(0, 1, SoftMax = true)] public decimal Price { get; set; } = 1;
}

public sealed class SoftRangeCommands
{
    [Command("softcost")]
    public double Run([Range(0, 1, 0.1, SoftMax = true)] double value) => value;
    [Command("softint")]
    public int RunInt([Range(0, 10, SoftMin = true)] int value) => value;
}

public sealed class ConfigSoftRangeTests : KilnTest
{
    [Fact]
    public void The_attribute_and_entry_contract_is_additive()
    {
        var plain = new RangeAttribute(0, 1);
        Assert.False(plain.SoftMin);
        Assert.False(plain.SoftMax);
        Assert.Null(plain.Step);
        var soft = new RangeAttribute(0, 1, 0.1) { SoftMax = true };
        Assert.Equal(0.1, soft.Step);
        Assert.True(soft.SoftMax);
        Assert.False(soft.SoftMin);
        var entry = new ConfigEntry("a", "float", null, 0.0, 0.0, 0.0, 0, 1, [], false, false);
        Assert.False(entry.SoftMin);
        Assert.False(entry.SoftMax);
        Assert.True((entry with { SoftMax = true }).SoftMax);
    }

    [Fact]
    public void Typed_values_past_a_soft_side_are_kept_and_the_hard_side_still_refuses()
    {
        var cfg = As(NewMod("softtyped"), () => Config.Register<SoftRangeConfig>());
        Assert.True(cfg.TrySet("costMultiplier", 2, out var error), error);
        Assert.Equal(2.0, cfg.Value.CostMultiplier);
        Assert.True(cfg.TrySet("costMultiplier", "3", out error), error);
        Assert.Equal(3.0, cfg.Value.CostMultiplier);
        Assert.False(cfg.TrySet("costMultiplier", -1, out error));
        Assert.Equal("\"costMultiplier\" must be in 0..max (got -1)", error);
        Assert.True(cfg.TrySet("offset", -5, out error), error);
        Assert.Equal(-5, cfg.Value.Offset);
        Assert.False(cfg.TrySet("offset", 11, out error));
        Assert.Equal("\"offset\" must be in -2147483648..10 (got 11)", error);
        Assert.True(cfg.TrySet("both", -7, out error), error);
        Assert.True(cfg.TrySet("both", 70, out error), error);
        Assert.Equal(70, cfg.Value.Both);
        Assert.False(cfg.TrySet("both", 3e9, out error));    // the int's own bounds stay
        Assert.False(cfg.TrySet("small", 300, out error));   // a byte stays a byte
        Assert.Equal("\"small\" must be in 0..255 (got 300)", error);
        Assert.True(cfg.TrySet("small", 200, out error), error);
        Assert.True(cfg.TrySet("wait", "5m", out error), error);
        Assert.Equal(TimeSpan.FromMinutes(5), cfg.Value.Wait);
        Assert.False(cfg.TrySet("wait", "0s", out error));
        Assert.True(cfg.TrySet("factors", "[0.5, 4]", out error), error);
        Assert.Equal([0.5, 4.0], cfg.Value.Factors);
        // a range without a soft side: exactly as before
        Assert.False(cfg.TrySet("hard", 2, out error));
        Assert.Equal("\"hard\" must be in 0..1 (got 2)", error);
    }

    [Fact]
    public void File_values_past_a_soft_side_load_without_a_clamp_or_warning()
    {
        var cfg = As(NewMod("softfile"), () => Config.Register<SoftRangeConfig>());
        File.WriteAllText(cfg.FilePath,
            "{\"costMultiplier\": 3, \"offset\": -4, \"both\": 99, \"wait\": \"10m\", \"factors\": [2.5], \"small\": 250, \"hard\": 0.5}");
        cfg.Reload();
        Assert.Equal(3.0, cfg.Value.CostMultiplier);
        Assert.Equal(-4, cfg.Value.Offset);
        Assert.Equal(99, cfg.Value.Both);
        Assert.Equal(TimeSpan.FromMinutes(10), cfg.Value.Wait);
        Assert.Equal([2.5], cfg.Value.Factors);
        Assert.Equal((byte)250, cfg.Value.Small);
        Assert.Empty(cfg.Warnings);
        Assert.Contains("\"costMultiplier\": 3", File.ReadAllText(cfg.FilePath));   // the file is not corrected
        // the hard side still clamps with a warning
        File.WriteAllText(cfg.FilePath, "{\"costMultiplier\": -2, \"offset\": 12, \"hard\": 2}");
        cfg.Reload();
        Assert.Equal(0.0, cfg.Value.CostMultiplier);
        Assert.Equal(10, cfg.Value.Offset);
        Assert.Equal(1.0, cfg.Value.Hard);
        Assert.Contains(cfg.Warnings, w => w.Contains("\"costMultiplier\" = -2 is outside 0..max; using 0"));
        Assert.Contains(cfg.Warnings, w => w.Contains("\"offset\" = 12 is outside -2147483648..10; using 10"));
        Assert.Contains(cfg.Warnings, w => w.Contains("\"hard\" = 2 is outside 0..1; using 1"));
    }

    [Fact]
    public void A_decimal_past_a_soft_side_is_bounded_by_the_decimal_type_not_an_overflow()
    {
        // past a soft side only the number's own type bounds it: for a decimal that is about 7.9e28, which a double
        // passes easily (1e30); the hard range used to keep such values away from the decimal conversion
        var cfg = As(NewMod("softdecimal"), () => Config.Register<SoftDecimalConfig>());
        Assert.True(cfg.TrySet("price", 5, out var error), error);
        Assert.Equal(5m, cfg.Value.Price);
        Assert.False(cfg.TrySet("price", 1e30, out error));
        Assert.StartsWith("\"price\" must be in 0..max", error);
        Assert.Equal(5m, cfg.Value.Price);
        File.WriteAllText(cfg.FilePath, "{\"price\": 1e30}");
        cfg.Reload();
        Assert.Equal((decimal)7.9e28, cfg.Value.Price);
        Assert.Contains(cfg.Warnings, w => w.StartsWith("\"price\" = ") && w.EndsWith("is outside 0..max; using max"));
    }

    [Fact]
    public void Soft_flags_reach_the_entry_and_every_schema_and_the_file_comment()
    {
        var cfg = As(NewMod("softschema"), () => Config.Register<SoftRangeConfig>());
        var cost = cfg.Entries.Single(e => e.Path == "costMultiplier");
        Assert.Equal((0.0, 1.0, 0.1, false, true), (cost.Min, cost.Max, cost.Step, cost.SoftMin, cost.SoftMax));
        var offset = cfg.Entries.Single(e => e.Path == "offset");
        Assert.Equal((true, false), (offset.SoftMin, offset.SoftMax));
        Assert.All(cfg.Entries.Where(e => e.Path == "hard"), e => Assert.False(e.SoftMin || e.SoftMax));
        foreach (var schema in new[] { cfg.Schema(), cfg.SchemaSnapshot(), JsonNode.Parse(File.ReadAllText(cfg.SnapshotPath))!.AsObject() })
        {
            var entries = schema["entries"]!.AsArray().ToDictionary(e => (string)e!["path"]!, e => e!.AsObject());
            Assert.True((bool)entries["costMultiplier"]["softMax"]!);
            Assert.False(entries["costMultiplier"].ContainsKey("softMin"));
            Assert.Equal(1.0, (double)entries["costMultiplier"]["max"]!);   // the slider's end stays max
            Assert.True((bool)entries["offset"]["softMin"]!);
            Assert.False(entries["offset"].ContainsKey("softMax"));
            Assert.True((bool)entries["both"]["softMin"]! && (bool)entries["both"]["softMax"]!);
            Assert.True((bool)entries["wait"]["softMax"]!);
            Assert.True((bool)entries["factors"]["softMax"]!);
            Assert.False(entries["hard"].ContainsKey("softMin") || entries["hard"].ContainsKey("softMax"));
        }
        var text = File.ReadAllText(cfg.FilePath);
        Assert.Contains("Range: 0 .. 1 (slider; higher values allowed)", text);
        Assert.Contains("Range: 0 .. 10 (slider; lower values allowed)", text);
        Assert.Contains("Range: 0 .. 5 (slider; lower and higher values allowed)", text);
        // a duration and a list have no slider (a text field in every editor): the meta line does not name one
        Assert.Contains("Range: 1s .. 1m (higher values allowed)", text);
        Assert.Contains("Default: [0.5] | Range: 0 .. 1 (higher values allowed)", text);
        Assert.DoesNotContain("1m (slider", text);
        Assert.Matches(@"Default: 0\.5 \| Range: 0 \.\. 1\r?\n", text);   // the hard one, as before
    }

    [Fact]
    public void Command_arguments_have_no_bound_on_a_soft_side()
    {
        As(NewMod("softcommands"), () => Commands.RegisterAll(new SoftRangeCommands()));
        Assert.True(Commands.Execute(CommandSource.Console, "softcost 3").Ok);
        Assert.False(Commands.Execute(CommandSource.Console, "softcost -1").Ok);
        Assert.True(Commands.Execute(CommandSource.Console, "softint -20").Ok);
        Assert.False(Commands.Execute(CommandSource.Console, "softint 11").Ok);
    }
}
