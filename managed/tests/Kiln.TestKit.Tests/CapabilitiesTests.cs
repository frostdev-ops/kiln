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
        Assert.False(Settings.Available);
        Assert.False(Gameplay.Available);
        Assert.False(kiln.Capabilities.Is("kiln.settings"));
        var row = Assert.Single(kiln.Capabilities.All, r => r.Name == "kiln.settings");
        Assert.False(row.Available);
        Assert.Equal("kiln.settings is unavailable (offline): " + KitBackend.NoFakeText + ".", kiln.Capabilities.Why("kiln.settings"));
        Assert.Equal(CapReason.Offline, KitBackend.Instance.Capabilities.Get("kiln.settings").Reason);
        Assert.Equal("testkit", KitBackend.Instance.Capabilities.Get("kiln.settings").Source);
        Assert.Contains(KitBackend.Instance.Asked.Since(0), a => a is { Kind: AskKind.Service, Name: "kiln.settings" });
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

    // A game feature no managed service reports. items.fuel is Kiln.Items' row too since Kiln.Items reports its
    // capabilities: once Items starts in this process (kiln.items is not faked) it holds that row off for good.
    [Fact]
    public void AFeatureOverrideShowsInGameHasFeatureAndEndsWithTheHarness()
    {
        using (var kiln = new KilnHarness())
        {
            var mark = KitBackend.Instance.Asked.Mark();
            Assert.False(Game.HasFeature("gameplay.craft"));
            Assert.Contains(KitBackend.Instance.Asked.Since(mark), a => a is { Kind: AskKind.Feature, Name: "gameplay.craft", ModId: null });
            kiln.Capabilities.Override("gameplay.craft", true);
            Assert.True(Game.HasFeature("gameplay.craft"));
            Assert.Equal("gameplay.craft is available (switched on by the test).", kiln.Capabilities.Why("gameplay.craft"));
            kiln.Capabilities.Override("gameplay.craft", false, "this test plays an old game build");
            Assert.False(Game.HasFeature("gameplay.craft"));
            Assert.Equal("gameplay.craft is unavailable (policy): this test plays an old game build.", kiln.Capabilities.Why("gameplay.craft"));
            kiln.Capabilities.ClearOverride("gameplay.craft");
            Assert.Equal(CapReason.Unknown, KitBackend.Instance.Capabilities.Get("gameplay.craft").Reason);
            kiln.Capabilities.Override("gameplay.craft", true);
            Assert.True(kiln.Capabilities.Is("tick"));
        }
        Assert.False(Game.HasFeature("gameplay.craft"));
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
        var e = Assert.Throws<ModLoadException>(() => kiln.Load<TkEventsMod>(new ModManifest("tk-events").Requires("gameplay.craft")));
        Assert.Equal(ModState.Failed, e.State);
        Assert.Contains("needs feature gameplay.craft, unavailable on this game build", e.Reason);
        Assert.Contains("gameplay.craft is unavailable: no service or feature of this name is known in this process.", e.Reason);
        Assert.Equal(ModState.Failed, kiln.Mod("tk-events").State);
        Assert.Contains("needs feature gameplay.craft", kiln.Mod("tk-events").Error);

        kiln.Capabilities.Override("gameplay.craft", true);
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
