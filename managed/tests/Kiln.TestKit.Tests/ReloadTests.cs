// SPDX-License-Identifier: MIT
using TkLeaky;
using TkLifeBase;
using TkLifeUser;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class ReloadTests
{
    [Fact]
    public void TwentyLoadAndReloadCyclesLeaveNoLoadContextAlive()
    {
        using var kiln = new KilnHarness();
        var b = kiln.Load<TkLifeBaseMod>(TestMods.Base());
        var user = kiln.Load<TkLifeUserMod>(TestMods.User());
        kiln.Step();
        for (var i = 0; i < 20; i++)
        {
            kiln.Reload(b);  // tk-user comes along (the cascade); the call checks both old contexts are gone
            kiln.Step();
            Assert.True(b.IsLoaded && user.IsLoaded);
            kiln.Unload(user);
            kiln.Reload(user);  // an unloaded mod loads again from its folder
            kiln.Step();
        }
        Assert.Equal(20, kiln.Lines("tk-base", "life: Reloaded tk-base ok True").Count);
        Assert.Equal(41, kiln.Lines("tk-user", "life: OnLoad").Count);
        kiln.Unload(b);  // with tk-user; checks every context unloaded so far is gone
    }

    [Fact]
    public void AModThatLeaksMakesUnloadThrow()
    {
        using var kiln = new KilnHarness();
        var leaky = kiln.Load<TkLeakyMod>(new ModManifest("tk-leaky"));
        kiln.Step();
        var e = Assert.Throws<ModLeakException>(() => kiln.Unload(leaky));
        Assert.Equal(["tk-leaky"], e.ModIds);
        Assert.Equal(ModState.Unloaded, leaky.State);
    }

    [Fact]
    public void WithoutTheCheckAnUnloadOfALeakDoesNotThrow()
    {
        using var kiln = new KilnHarness();
        var leaky = kiln.Load<TkLeakyMod>(new ModManifest("tk-leaky"));
        kiln.Step();
        kiln.Unload(leaky, checkCollected: false);
        Assert.Equal(ModState.Unloaded, leaky.State);
    }
}
