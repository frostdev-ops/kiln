// SPDX-License-Identifier: MIT
#pragma warning disable KILNX031
using System.Text.Json.Nodes;
using TestMods;
using Xunit;
using DataRegistry = Kiln.Registry.Registry;

namespace Kiln.TestKit.Tests;

public sealed class RegistryAuthoringTests
{
    [Fact]
    public void ASeededAuthorRecordsIrAndExpiresCellsWithoutClaimingNativeAvailability()
    {
        using var kiln = new KilnHarness();
        var mod = kiln.Load<TkRegistryMod>(new ModManifest("tk-registry")); kiln.Boot();
        kiln.Registry.Seed("items", "game:wood", "maxStack", "10");
        var text = kiln.Registry.Author(mod);
        var ir = JsonNode.Parse(text!)!;
        Assert.Equal("remove", ir["edits"]![0]!["entry"]!.GetValue<string>());
        Assert.Equal("loot", ir["edits"]![0]!["registry"]!.GetValue<string>());
        Assert.Equal(15, ir["edits"]![1]!["op"]!.GetValue<int>());
        Assert.Equal("alter", ir["stage"]!.GetValue<string>());
        var add = JsonNode.Parse(kiln.Registry.Author(mod, phase: RegistryPhase.Add)!)!;
        Assert.Equal("add", add["stage"]!.GetValue<string>());
        Assert.Equal("tk-registry:logs", add["edits"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("game:wood", add["edits"]![0]!["like"]!.GetValue<string>());
        Assert.Equal(17, add["edits"]![0]!["fields"]!["maxStack"]!.GetValue<int>());
        Assert.False(DataRegistry.Available);
        Assert.Contains(Coverage.All, row => row.Area == "Registry authoring" && row.Kind == CoverageKind.RecordingFake && row.Capabilities.Count == 0);
        kiln.Unload(mod); // the mod verifies that a retained cell failed closed and releases it
    }
    // 0.2.0-dev.32: Registry.Reauthor offline is recorded (the test runs the pass itself with Author); another point is
    // refused as in a game
    [Fact]
    public void ReauthorRequestsAreRecordedNotRun()
    {
        using var kiln = new KilnHarness();
        var mod = kiln.Load<TkRegistryMod>(new ModManifest("tk-registry")); kiln.Boot();
        Assert.True(kiln.As(mod, () => DataRegistry.Reauthor(RegistryEntryPoint.WorldLoad)));
        Assert.False(kiln.As(mod, () => DataRegistry.Reauthor(RegistryEntryPoint.ModLoad)));
        Assert.Equal(new[] { "tk-registry" }, kiln.Registry.Reauthors);
        Assert.True(kiln.HasLog("Reauthor(ModLoad) was not queued: only the WorldLoad point is authored again on request", mod.Id), kiln.DumpLogs());
        kiln.Unload(mod);
    }
    [Fact]
    public void MissingSeedsThrowInsteadOfInventingGameValues()
    {
        using var kiln = new KilnHarness();
        var mod = kiln.Load<TkRegistryMod>(new ModManifest("tk-registry")); kiln.Boot();
        MissingSeed(kiln, mod);
        kiln.Unload(mod);
    }
    // A caught exception's stack itself references the collectible author's method. Release the assertion's local first.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void MissingSeed(KilnHarness kiln, ModHandle mod)
        => Assert.Throws<KeyNotFoundException>(() => kiln.Registry.Author(mod));
}
