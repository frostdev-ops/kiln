// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class CoverageTests
{
    [Fact]
    public void TheEmbeddedServiceNamesAreTheHeaders()
    {
        var repo = typeof(CoverageTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "KilnRepository").Value!;
        var headers = Path.Combine(repo, "native");
        Assert.True(Directory.Exists(headers), headers);
        var define = new Regex(@"^#define\s+KILN_\w+_SERVICE\s+""(kiln\.[^""]+)""", RegexOptions.Multiline);
        var names = Directory.GetFiles(headers, "*.h", SearchOption.AllDirectories).SelectMany(f => define.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.Equal(names, ServiceNames.All.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryServiceHasARowAndTheCoreAreasAreThere()
    {
        var rows = Coverage.All;
        foreach (var s in ServiceNames.All) Assert.Contains(rows, r => r.Capabilities.Contains(s));
        foreach (var area in new[] { "Events", "Scheduler and await", "Config (global)", "Commands (console)", "Inter-mod messages", "Locale files",
                     "Lifecycle", "Mod loading and reload", "Fault policy", "Tests.RunAsync" })
            Assert.Single(rows, r => r.Area == area);
        Assert.Equal(CoverageKind.Unavailable, rows.Single(r => r.Area == "kiln.abilities").Kind);
        Assert.Equal(CoverageKind.RecordingFake, rows.Single(r => r.Area == "Combat").Kind);
        Assert.Equal(CoverageKind.Unavailable, rows.Single(r => r.Area == "Tests.RunAsync").Kind);
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Note)));
    }

    [Fact]
    public void TestsRunAsyncIsUnavailableOffline()
    {
        using var kiln = new KilnHarness();
        var e = Assert.ThrowsAny<Exception>(() => kiln.Run(() => Kiln.Testing.Tests.RunAsync("tk-base")).GetAwaiter().GetResult());
        Assert.Equal("Kiln.Host is not running", e.Message);
    }
}
