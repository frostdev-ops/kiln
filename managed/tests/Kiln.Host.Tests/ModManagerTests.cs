// SPDX-License-Identifier: MIT
using Kiln.Host;
using Xunit;

namespace Kiln.Host.Tests;

/// <summary>The basic behaviour (Hello sample) must not change.</summary>
public sealed class ModManagerTests : ModsFixture
{
    [Fact]
    public void Loads_ticks_and_unloads_the_sample()
    {
        InstallHello();
        var host = new FakeHost();
        using var mods = NewManager(host);
        mods.LoadAll();
        Assert.Single(mods.Mods);
        for (ulong s = 1; s <= 24; s++) mods.Pre(Tick(s), []);
        Assert.False(host.Has("still on the tick thread"));  // NextTick = one step each
        for (ulong s = 25; s <= 30; s++) mods.Pre(Tick(s), []);
        Assert.True(host.Has("still on the tick thread"));
        Assert.True(mods.Unload("hello", out _));
        Assert.True(host.Has("goodbye"));
        Assert.Empty(mods.Mods);
    }

    [Fact]
    public void Hundred_reloads_leak_no_load_context()
    {
        InstallHello();
        var host = new FakeHost();
        using var mods = NewManager(host);
        mods.LoadAll();
        for (var i = 0; i < 100; i++)
        {
            mods.Pre(Tick((ulong)i + 1), []);
            Assert.True(mods.Reload("hello", out var error), error);
        }
        Assert.True(mods.Unload("hello", out _));
        Assert.Equal(0, mods.CollectAndCountLeaks(out var leaked));
        Assert.Empty(leaked);
        Assert.Equal(101, host.Revoked.Count);
    }

    [Fact]
    public void Faulting_mod_is_disabled_after_five_faults_with_a_crash_report()
    {
        InstallHello();
        var host = new FakeHost();
        var reports = Path.Combine(Root, "crash-reports");
        var crash = new CrashReports(reports, null, () => "Kiln: test\n", () => "  hello\n");
        using var mods = NewManager(host, new ModManagerOptions { CrashReports = crash });
        mods.LoadAll();
        var mod = mods.Find("hello")!;
        mod.ModContext.Tick.Pre += (in TickInfo _) => throw new InvalidOperationException("boom");
        for (ulong s = 1; s <= 10; s++) mods.Pre(Tick(s), []);
        Assert.True(mod.Disabled);
        Assert.Equal(ModManager.MaxFaults, mod.Faults);
        Assert.Contains(mod.Owner, host.Revoked);
        var report = Assert.Single(Directory.GetFiles(reports, "crash-*-mod-disabled.txt"));
        var text = File.ReadAllText(report);
        Assert.Contains("mod hello 0.1.0 disabled after 5 faults within 60 s", text);
        Assert.Contains("InvalidOperationException: boom", text);
        Assert.Contains("-- Mods (load order) --", text);
        Assert.Equal(ModState.Disabled, Kiln.Mods.Get("hello")!.State);
    }

    [Fact]
    public void Faults_spread_wider_than_the_window_do_not_disable()
    {
        InstallHello();
        var host = new FakeHost();
        var now = 0.0;
        using var mods = NewManager(host, new ModManagerOptions { Faults = new FaultPolicy { Limit = 3, WindowSeconds = 10, Clock = () => now } });
        mods.LoadAll();
        var mod = mods.Find("hello")!;
        mod.ModContext.Tick.Pre += (in TickInfo _) => throw new InvalidOperationException("slow boom");
        for (ulong s = 1; s <= 6; s++)
        {
            now += 6;  // one fault every 6 s: at most 2 inside any 10 s window
            mods.Pre(Tick(s), []);
        }
        Assert.False(mod.Disabled);
        Assert.Equal(6, mod.Faults);
        Assert.Equal(2, Kiln.Mods.Get("hello")!.Stats.RecentFaults);
        now += 0.1;
        mods.Pre(Tick(7), []);
        Assert.True(mod.Disabled, "three faults within 10 s");
    }

    [Fact]
    public void Scheduled_work_and_event_handler_faults_count()
    {
        InstallHello();
        var host = new FakeHost();
        using var mods = NewManager(host);
        mods.LoadAll();
        var mod = mods.Find("hello")!;
        HostAccess.Current = mod.ModContext;  // subscribe as the mod
        Events.On<ProbeEvent>(_ => throw new InvalidOperationException("handler boom"));
        HostAccess.Current = null;
        for (var i = 0; i < 2; i++) mod.ModContext.Scheduler.Post(() => throw new InvalidOperationException("task boom"));
        mods.Pre(Tick(1), []);
        Assert.Equal(2, mod.Faults);
        Assert.True(host.Has("threw in scheduled work"));
        for (var i = 0; i < 3; i++) Events.Post(new ProbeEvent());
        Assert.True(mod.Disabled);
        Assert.True(host.Has("fault in event handler for ProbeEvent"));
        Assert.False(Events.HasSubscribers<ProbeEvent>(), "a disabled mod's subscriptions are gone");
    }

    private sealed class ProbeEvent : KilnEvent;
}
