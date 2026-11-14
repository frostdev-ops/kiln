// SPDX-License-Identifier: MIT
// The tick-thread checks in Mod.OnBoot: while a boot-author frame is open (before the game's first tick) "use Scheduler.Post" cannot be
// followed, so Game.AssertTickThread and Services.RequireTickThread name the member and say to call it in OnLoad. Elsewhere the
// message is the one it always was.
using System.Runtime.CompilerServices;
using Xunit;

namespace Kiln.Api.Tests;

internal static class TickProbe
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Touch() => Game.AssertTickThread();

    public static int Value
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        get
        {
            Game.AssertTickThread();
            return 1;
        }
    }
}

public sealed class BootThreadMessageTests : KilnTest
{
    private const string Hint = " is not available in OnBoot (it runs before the game's first tick); call it in OnLoad";

    private Exception? Boot(Action body, bool open = true)
    {
        var frame = new BootContext("probe", 1, BootRoles.DedicatedServer, new UnavailableGameData()) { Open = open };
        BootFrame.Current = frame;
        Backend.TickThread = false;
        try
        {
            body();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
        finally
        {
            BootFrame.Current = null;
            Backend.TickThread = true;
        }
    }

    [Fact]
    public void AssertTickThread_in_OnBoot_names_the_member_and_says_OnLoad()
    {
        Assert.Equal("TickProbe.Touch" + Hint, Assert.IsType<InvalidOperationException>(Boot(TickProbe.Touch)).Message);
        Assert.Equal("TickProbe.Value" + Hint, Assert.IsType<InvalidOperationException>(Boot(() => _ = TickProbe.Value)).Message);
        // a Kiln member that asserts it
        var world = Assert.IsType<InvalidOperationException>(Boot(() => As(NewMod("probe"), () => Config.RegisterWorld<AltarSettings>())));
        Assert.Equal("Config.RegisterWorld" + Hint, world.Message);
    }

    [Fact]
    public void RequireTickThread_in_OnBoot_says_OnLoad_for_the_api_it_names()
    {
        var e = Assert.IsType<InvalidOperationException>(Boot(() => Services.RequireTickThread("Items.Give")));
        Assert.Equal("Items.Give" + Hint, e.Message);
    }

    [Fact]
    public void Outside_OnBoot_and_after_its_frame_closed_the_messages_are_unchanged()
    {
        Backend.TickThread = false;
        try
        {
            Assert.Equal("This must run on the tick thread; use Scheduler.Post.", Assert.Throws<InvalidOperationException>(TickProbe.Touch).Message);
            Assert.Equal("Items.Give must be called on the tick thread (use Scheduler.Post)",
                Assert.Throws<InvalidOperationException>(() => Services.RequireTickThread("Items.Give")).Message);
        }
        finally
        {
            Backend.TickThread = true;
        }
        Assert.Equal("This must run on the tick thread; use Scheduler.Post.",
            Assert.IsType<InvalidOperationException>(Boot(TickProbe.Touch, open: false)).Message);
        Assert.Null(Boot(() => { }));  // nothing asserted, nothing thrown
    }

    [Fact]
    public void On_the_tick_thread_nothing_is_thrown_even_in_a_frame()
    {
        BootFrame.Current = new BootContext("probe", 1, BootRoles.DedicatedServer, new UnavailableGameData());
        try
        {
            TickProbe.Touch();
            Services.RequireTickThread("Items.Give");
        }
        finally
        {
            BootFrame.Current = null;
        }
    }
}
