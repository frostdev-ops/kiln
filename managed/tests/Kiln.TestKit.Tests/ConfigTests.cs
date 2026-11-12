// SPDX-License-Identifier: MIT
using System.Text.RegularExpressions;
using TkConfig;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class ConfigTests
{
    private static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(5);

    [Fact]
    public void EveryGlobalKindWritesItsFileInTheTempConfigFolder()
    {
        using var kiln = new KilnHarness();
        kiln.Load<TkConfigMod>(new ModManifest("tk-config"));
        kiln.Step();
        Assert.Equal(kiln.ConfigDirectory, Config.Directory);
        foreach (var id in new[] { "tk-config", "tk-config-server", "tk-config-client" })
        {
            Assert.True(File.Exists(Path.Combine(kiln.ConfigDirectory, id + ".json")), id);
            Assert.Equal(Path.Combine(kiln.ConfigDirectory, id + ".json"), Config.Find(id)!.FilePath);
        }
        Assert.Contains("config: count 3", kiln.Lines("tk-config", "config:"));
    }

    [Fact]
    public void AnEditOnDiskReachesTheChangeHandlerAndABadFileIsRefused()
    {
        using var kiln = new KilnHarness();
        kiln.Load<TkConfigMod>(new ModManifest("tk-config"));
        kiln.Step();
        var file = Path.Combine(kiln.ConfigDirectory, "tk-config.json");
        var text = File.ReadAllText(file);
        var edited = Regex.Replace(text, @"(""count""\s*:\s*)3", "${1}7");
        Assert.NotEqual(text, edited);
        File.WriteAllText(file, edited);
        kiln.StepUntil(() => kiln.Lines("tk-config", "changed:").Count > 0, maxSteps: 2000, pace: Pace);
        Assert.Equal("changed: count count 7 source File", Assert.Single(kiln.Lines("tk-config", "changed:")));

        File.WriteAllText(file, "{ \"count\": 9, oops");
        kiln.StepUntil(() => Config.Find("tk-config")!.LastError is not null, maxSteps: 2000, pace: Pace);
        Assert.True(kiln.HasLog("is not valid JSON"), kiln.DumpLogs());
        kiln.Run(() => Mods.Send("tk-config", "values"));
        kiln.Step();
        Assert.Contains("values: count 7", kiln.Lines("tk-config", "values:"));
        Assert.Single(kiln.Lines("tk-config", "changed:"));
    }

    [Fact]
    public void ANewHarnessWatchesItsOwnConfigFolder()
    {
        string first;
        using (var kiln = new KilnHarness())
        {
            kiln.Load<TkConfigMod>(new ModManifest("tk-config"));
            kiln.Step();
            first = kiln.ConfigDirectory;
        }
        using (var kiln = new KilnHarness())
        {
            Assert.NotEqual(first, kiln.ConfigDirectory);
            Assert.Null(Config.Find("tk-config"));
            kiln.Load<TkConfigMod>(new ModManifest("tk-config"));
            kiln.Step();
            var file = Path.Combine(kiln.ConfigDirectory, "tk-config.json");
            File.WriteAllText(file, Regex.Replace(File.ReadAllText(file), @"(""count""\s*:\s*)3", "${1}5"));
            kiln.StepUntil(() => kiln.Lines("tk-config", "changed:").Count > 0, maxSteps: 2000, pace: Pace);
            Assert.Contains("count 5", Assert.Single(kiln.Lines("tk-config", "changed:")));
        }
    }
}
