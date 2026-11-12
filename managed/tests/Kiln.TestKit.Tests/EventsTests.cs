// SPDX-License-Identifier: MIT
using TkEvents;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class EventsTests
{
    [Fact]
    public void SubscribeAndOnHandlersRunAndGoWithTheUnload()
    {
        using var kiln = new KilnHarness();
        var mod = kiln.Load<TkEventsMod>(new ModManifest("tk-events"));
        kiln.Step();
        Assert.True(mod.IsLoaded, mod.Error);
        Assert.True(TestMods.Subscribed("tk-events"));

        kiln.Run(() => Mods.Send("tk-events", "ping", "one"));
        kiln.Step();
        Assert.Contains("subscribe: one", kiln.Lines("tk-events", "subscribe:"));
        Assert.Contains("on: one", kiln.Lines("tk-events", "on:"));

        kiln.Unload(mod);
        Assert.Equal(ModState.Unloaded, mod.State);
        Assert.False(TestMods.Subscribed("tk-events"));
        Assert.False(kiln.Run(() => Mods.Send("tk-events", "ping", "two")));
        kiln.Step();
        Assert.Empty(kiln.FindLogs("two", "tk-events"));
    }

    [Fact]
    public void TestCodeSubscriptionsEndWithTheHarness()
    {
        var calls = 0;
        using (var kiln = new KilnHarness())
        {
            kiln.Run(() => Events.On<ServerStartedEvent>(_ => calls++));
            kiln.Step();
            Assert.Equal(1, calls);
        }
        using (var kiln = new KilnHarness())
        {
            kiln.Step();
            Assert.Equal(1, calls);
        }
    }
}
