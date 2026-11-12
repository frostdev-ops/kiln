// SPDX-License-Identifier: MIT
using TkEvents;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class CapabilitiesTests
{
    [Fact]
    public void AnUnfakedServiceIsUnavailableByItsOwnCodeAndItsRowSaysOffline()
    {
        using var kiln = new KilnHarness();
        Assert.False(Items.Available);
        Assert.False(Chat.Available);
        Assert.False(kiln.Capabilities.Is("kiln.items"));
        var row = Assert.Single(kiln.Capabilities.All, r => r.Name == "kiln.items");
        Assert.False(row.Available);
        Assert.Equal("kiln.items is unavailable (offline): " + KitBackend.NoFakeText + ".", kiln.Capabilities.Why("kiln.items"));
        Assert.Equal(CapReason.Offline, KitBackend.Instance.Capabilities.Get("kiln.items").Reason);
        Assert.Equal("testkit", KitBackend.Instance.Capabilities.Get("kiln.items").Source);
        Assert.Contains(KitBackend.Instance.Asked.Since(0), a => a is { Kind: AskKind.Service, Name: "kiln.items" });
    }

    [Theory]
    [InlineData("kiln.characters")]
    [InlineData("kiln.flame")]
    [InlineData("kiln.knowledge")]
    [InlineData("kiln.playerstate")]
    [InlineData("kiln.progression")]
    [InlineData("kiln.questmark")]
    [InlineData("kiln.skills")]
    public void ServicesWhoseHeadersAreOutsideCoreAreOffline(string name)
    {
        using var kiln = new KilnHarness();
        Assert.Equal(CapReason.Offline, KitBackend.Instance.Capabilities.Get(name).Reason);
        Assert.Contains("unavailable (offline)", kiln.Capabilities.Why(name));
        Assert.Equal(CoverageKind.Unavailable, Assert.Single(Coverage.All, r => r.Area == name).Kind);
        Assert.Throws<InvalidOperationException>(() => kiln.Capabilities.Override(name, true));
    }

    [Fact]
    public void AFeatureOverrideShowsInGameHasFeatureAndEndsWithTheHarness()
    {
        using (var kiln = new KilnHarness())
        {
            var mark = KitBackend.Instance.Asked.Mark();
            Assert.False(Game.HasFeature("items.fuel"));
            Assert.Contains(KitBackend.Instance.Asked.Since(mark), a => a is { Kind: AskKind.Feature, Name: "items.fuel", ModId: null });
            kiln.Capabilities.Override("items.fuel", true);
            Assert.True(Game.HasFeature("items.fuel"));
            Assert.Equal("items.fuel is available (switched on by the test).", kiln.Capabilities.Why("items.fuel"));
            kiln.Capabilities.Override("items.fuel", false, "this test plays an old game build");
            Assert.False(Game.HasFeature("items.fuel"));
            Assert.Equal("items.fuel is unavailable (policy): this test plays an old game build.", kiln.Capabilities.Why("items.fuel"));
            kiln.Capabilities.ClearOverride("items.fuel");
            Assert.Equal(CapReason.Unknown, KitBackend.Instance.Capabilities.Get("items.fuel").Reason);
            kiln.Capabilities.Override("items.fuel", true);
            Assert.True(kiln.Capabilities.Is("tick"));
        }
        Assert.False(Game.HasFeature("items.fuel"));
        Assert.False(KitBackend.Instance.Capabilities.Is("tick"));
    }

    [Fact]
    public void SwitchingOnAnUnfakedServiceThrows()
    {
        using var kiln = new KilnHarness();
        var e = Assert.Throws<InvalidOperationException>(() => kiln.Capabilities.Override("kiln.combat", true));
        Assert.Contains("the test kit has no fake of this service", e.Message);
        Assert.False(kiln.Capabilities.Is("kiln.combat"));
        kiln.Capabilities.Override("kiln.combat", false);  // off is always allowed
        Assert.Throws<ArgumentException>(() => kiln.Capabilities.Override("no spaces", true));
    }

    [Fact]
    public void AModRequiringAnUnavailableFeatureIsRefusedByTheResolver()
    {
        using var kiln = new KilnHarness();
        kiln.Step();
        var e = Assert.Throws<ModLoadException>(() => kiln.Load<TkEventsMod>(new ModManifest("tk-events").Requires("items.fuel")));
        Assert.Equal(ModState.Failed, e.State);
        Assert.Contains("needs feature items.fuel, unavailable on this game build", e.Reason);
        Assert.Contains("items.fuel is unavailable: no service or feature of this name is known in this process.", e.Reason);
        Assert.Equal(ModState.Failed, kiln.Mod("tk-events").State);
        Assert.Contains("needs feature items.fuel", kiln.Mod("tk-events").Error);

        kiln.Capabilities.Override("items.fuel", true);
        var mod = kiln.Reload(kiln.Mod("tk-events"));
        Assert.True(mod.IsLoaded, mod.Error);
    }

    [Fact]
    public void AtTheFirstStepARefusedModFailsTheStep()
    {
        using var kiln = new KilnHarness();
        kiln.Load<TkEventsMod>(new ModManifest("tk-events").Requires("no.such.feature"));
        var e = Assert.Throws<ModLoadException>(() => kiln.Step());
        Assert.Contains("needs feature no.such.feature", e.Reason);
        Assert.Equal(HarnessState.Running, kiln.State);
    }

    [Fact]
    public void WithNoHarnessTheBackendIsIdle()
    {
        Assert.Equal("testkit", Game.KilnVersion);  // the kit's backend, with no harness
        Assert.False(Game.IsTickThread);
        KitBackend.Instance.Log(0, LogLevel.Info, "a line with no harness");
        Assert.Contains(KitBackend.Instance.IdleLines, l => l.Text == "a line with no harness" && l.Source == "kiln");
    }
}
