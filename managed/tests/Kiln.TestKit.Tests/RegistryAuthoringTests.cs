// SPDX-License-Identifier: MIT
#pragma warning disable KILNX031
using System.Text.Json.Nodes;
using TestMods;
using Xunit;
using DataRegistry = Kiln.Registry.Registries;

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
    // 0.2.0-dev.32: Registries.Reauthor offline is recorded (the test runs the pass itself with Author or RunReauthored)
    // refused as in a game
    [Fact]
    public void ReauthorRequestsAreRecordedNotRun()
    {
        using var kiln = new KilnHarness();
        var mod = kiln.Load<TkRegistryMod>(new ModManifest("tk-registry")); kiln.Boot();
        Assert.True(kiln.As(mod, () => Kiln.Registry.Registries.Reauthor().Ok));   // only WorldLoad is authored again: the call says so
        Assert.Equal(new[] { "tk-registry" }, kiln.Registry.Reauthors);
        kiln.Unload(mod);
    }
    // dev.32 api-shape: Author holds a call to a registry's cap of edits per call (RegistrySchema.EditsPerCall) as Kiln does:
    // one call over it is refused whole, nothing of it applies (Author gives no IR) and Refused says why, in Kiln's words.
    [Fact]
    public void ACallOverARegistrysCapIsRefusedWhole()
    {
        using var kiln = new KilnHarness();
        var mod = kiln.Load<TkRegistryMod>(new ModManifest("tk-registry")); kiln.Boot();
        kiln.Registry.Seed("items", "game:wood", "maxStack", "10");
        for (var i = 0; i < 4097; ++i) kiln.Registry.Seed("items", $"game:bulk_{i:d4}", "maxStack", "10");
        Assert.Null(kiln.Registry.Author(mod, phase: RegistryPhase.Final));
        Assert.StartsWith("items: 4098 edits in one call, over this registry's cap of 4096 edits per call", kiln.Registry.Refused);
        Assert.True(kiln.HasLog("over its cap of 4096 edits per call", mod.Id), kiln.DumpLogs());
        Assert.NotNull(kiln.Registry.Author(mod));  // under the cap: taken, Refused cleared
        Assert.Null(kiln.Registry.Refused);
        kiln.Unload(mod);
    }
    // dev.32 api-shape: RunReauthored runs the WorldLoad pass as Kiln runs a re-authored one (Registry.Reauthor): off the tick,
    // on a thread of its own (context.OffTick), where a tick-thread API is refused. A pass that calls one (caught or not) is never
    // landed: Refused says so, as Kiln then runs the mod's passes on the tick; the uncaught refusal is the test's.
    [Fact]
    public void AReauthoredPassRunsOffTheTick()
    {
        using var kiln = new KilnHarness();
        var mod = kiln.Load<TkRegistryMod>(new ModManifest("tk-registry")); kiln.Boot();
        kiln.Registry.Seed("items", "game:wood", "maxStack", "10");
        var ir = JsonNode.Parse(kiln.Registry.RunReauthored(mod)!)!;    // the mod's Alter pass reads context.OffTick
        Assert.Equal(1, ir["edits"]![1]!["op"]!.GetValue<int>());        // TkRegistryMod: MaxStack = 1 off the tick
        Assert.Null(kiln.Registry.Refused);
        Assert.Contains("re-authored registry pass", OffTickRefusal(kiln, mod));
        Assert.Contains("needs the tick thread", kiln.Registry.Refused);
        Assert.NotNull(kiln.Registry.RunReauthored(mod, offTick: false, phase: RegistryPhase.Final));  // on the tick it is taken
        Assert.Null(kiln.Registry.Refused);
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
    // The off-tick refusal the pass did not catch, as its message only: the exception's stack references the collectible mod
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string OffTickRefusal(KilnHarness kiln, ModHandle mod)
        => Assert.Throws<InvalidOperationException>(() => kiln.Registry.RunReauthored(mod, phase: RegistryPhase.Final)).Message;
    // A caught exception's stack itself references the collectible author's method. Release the assertion's local first.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void MissingSeed(KilnHarness kiln, ModHandle mod)
        => Assert.Throws<KeyNotFoundException>(() => kiln.Registry.Author(mod));
}
