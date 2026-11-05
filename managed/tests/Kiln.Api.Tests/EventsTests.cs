// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Api.Tests;

public class PingEvent : KilnEvent
{
    public List<string> Seen { get; } = [];
}

public sealed class LoudPingEvent : PingEvent;

public sealed class StopEvent : CancelableEvent
{
    public List<string> Seen { get; } = [];
}

public sealed class AskEvent : ResultEvent;

public sealed class LazyEvent : KilnEvent
{
    public static int Started;
    static LazyEvent() => Started++;
}

public sealed class ModWithHandlers : Mod
{
    public readonly List<string> Got = [];
    public static int StaticHits;

    [Subscribe(EventPriority.High)]
    private void OnPing(PingEvent e) => Got.Add("high");

    [Subscribe]
    public void OnStop(StopEvent e) => e.Cancel();

    [Subscribe(EventPriority.Monitor, ReceiveCanceled = true)]
    private static void Watch(StopEvent e) => StaticHits++;
}

public sealed class BadHandlers
{
    [Subscribe]
    public void Good(PingEvent e) { }

    [Subscribe]
    public int Bad(PingEvent e) => 1;
}

public sealed class EventsTests : KilnTest
{
    [Fact]
    public void Priority_then_subscription_order()
    {
        Events.On<PingEvent>(e => e.Seen.Add("normal1"));
        Events.On<PingEvent>(e => e.Seen.Add("lowest"), EventPriority.Lowest);
        Events.On<PingEvent>(e => e.Seen.Add("highest"), EventPriority.Highest);
        Events.On<PingEvent>(e => e.Seen.Add("normal2"));
        Events.On<PingEvent>(e => e.Seen.Add("monitor"), EventPriority.Monitor);
        var ev = Events.Post(new PingEvent());
        Assert.Equal(["highest", "normal1", "normal2", "lowest", "monitor"], ev.Seen);
    }

    [Fact]
    public void Cancel_skips_later_handlers_unless_they_receive_canceled()
    {
        var mod = NewMod("canceller");
        As(mod, () => Events.On<StopEvent>(e => { e.Seen.Add("a"); e.Cancel(); }, EventPriority.High));
        Events.On<StopEvent>(e => e.Seen.Add("b"));
        Events.On<StopEvent>(e => e.Seen.Add("c"), EventPriority.Low, receiveCanceled: true);
        var ev = Events.Post(new StopEvent());
        Assert.Equal(["a", "c"], ev.Seen);
        Assert.True(ev.Canceled);
        Assert.Equal("canceller", ev.CanceledBy);
    }

    [Fact]
    public void Uncancel_by_a_receive_canceled_handler_lets_later_ones_run()
    {
        Events.On<StopEvent>(e => e.Cancel(), EventPriority.Highest);
        Events.On<StopEvent>(e => e.Canceled = false, EventPriority.High, receiveCanceled: true);
        Events.On<StopEvent>(e => e.Seen.Add("normal"));
        var ev = Events.Post(new StopEvent());
        Assert.False(ev.Canceled);
        Assert.Null(ev.CanceledBy);
        Assert.Equal(["normal"], ev.Seen);
    }

    [Fact]
    public void Base_class_handlers_get_derived_events()
    {
        Events.On<KilnEvent>(e => ((PingEvent)e).Seen.Add("any"));
        Events.On<PingEvent>(e => e.Seen.Add("ping"));
        Events.On<LoudPingEvent>(e => e.Seen.Add("loud"));
        Assert.Equal(["any", "ping", "loud"], Events.Post(new LoudPingEvent()).Seen);
        Assert.Equal(["any", "ping"], Events.Post(new PingEvent()).Seen);
    }

    [Fact]
    public void HasSubscribers_follows_subscribe_dispose_and_unload()
    {
        Assert.False(Events.HasSubscribers<PingEvent>());
        var sub = Events.On<PingEvent>(_ => { });
        Assert.True(Events.HasSubscribers<PingEvent>());
        Assert.True(Events.HasSubscribers<LoudPingEvent>());  // a base-class handler counts
        Assert.False(Events.HasSubscribers<StopEvent>());
        sub.Dispose();
        Assert.False(Events.HasSubscribers<PingEvent>());
        Assert.False(Events.HasSubscribers(typeof(LoudPingEvent)));

        var mod = NewMod("m");
        As(mod, () => Events.On<StopEvent>(_ => { }));
        Assert.True(Events.HasSubscribers<StopEvent>());
        mod.Retire();
        Assert.False(Events.HasSubscribers<StopEvent>());
        Assert.Empty(Events.Subscriptions);
    }

    [Fact]
    public void HasSubscribers_is_cheap()
    {
        Events.On<PingEvent>(_ => { });
        for (var i = 0; i < 1000; i++) Events.HasSubscribers<PingEvent>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var n = 0;
        for (var i = 0; i < 1_000_000; i++)
            if (Events.HasSubscribers<PingEvent>() && !Events.HasSubscribers<StopEvent>()) n++;
        sw.Stop();
        Assert.Equal(1_000_000, n);
        Assert.True(sw.Elapsed.TotalMilliseconds < 200, $"2M checks took {sw.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public void Posting_inside_a_handler_works_and_restores_the_mod()
    {
        var a = NewMod("a");
        var b = NewMod("b");
        string? inner = null, after = null;
        As(b, () => Events.On<StopEvent>(_ => inner = ModContext.CurrentMod?.Info.Id));
        As(a, () => Events.On<PingEvent>(e =>
        {
            e.Seen.Add("outer:" + ModContext.CurrentMod?.Info.Id);
            Events.Post(new StopEvent());
            after = ModContext.CurrentMod?.Info.Id;
        }));
        Events.Post(new PingEvent());
        Assert.Equal("b", inner);
        Assert.Equal("a", after);
        Assert.Null(ModContext.CurrentMod);
    }

    [Fact]
    public void Endless_recursion_is_stopped()
    {
        var depth = 0;
        Events.On<PingEvent>(e =>
        {
            depth++;
            Events.Post(new PingEvent());
        });
        Events.Post(new PingEvent());
        Assert.Equal(Events.MaxDepth, depth);
        Assert.True(Backend.Logged("levels deep"));
    }

    [Fact]
    public void Subscribing_during_dispatch_takes_effect_at_the_next_post()
    {
        var late = 0;
        Events.On<PingEvent>(_ => Events.On<PingEvent>(_ => late++));
        Events.Post(new PingEvent());
        Assert.Equal(0, late);
        Events.Post(new PingEvent());
        Assert.Equal(1, late);
    }

    [Fact]
    public void Unsubscribing_during_dispatch_takes_effect_at_once()
    {
        EventSubscription? second = null;
        var ran = false;
        Events.On<PingEvent>(_ => second!.Dispose(), EventPriority.High);
        second = Events.On<PingEvent>(_ => ran = true);
        Events.Post(new PingEvent());
        Assert.False(ran);
        Assert.False(second.Active);
    }

    [Fact]
    public void A_throwing_handler_is_isolated_and_removed_after_five_faults()
    {
        var after = 0;
        var faults = 0;
        Action<EventSubscription, Exception> count = (_, _) => faults++;
        Events.HandlerFaulted += count;
        var bad = Events.On<PingEvent>(_ => throw new InvalidOperationException("boom"), EventPriority.High);
        Events.On<PingEvent>(_ => after++);
        for (var i = 0; i < 7; i++) Events.Post(new PingEvent());
        Assert.Equal(7, after);
        Assert.Equal(Events.MaxFaults, bad.Stats.Faults);
        Assert.False(bad.Active);
        Assert.True(Backend.Logged("removed after 5 faults"));
        Events.HandlerFaulted -= count;
        Assert.True(faults >= 5);
    }

    [Fact]
    public void Post_off_the_tick_thread_throws_but_Enqueue_works()
    {
        var got = 0;
        Events.On<PingEvent>(_ => got++);
        Backend.TickThread = false;
        Assert.Throws<InvalidOperationException>(() => Events.Post(new PingEvent()));
        Events.Enqueue(new PingEvent());
        Backend.TickThread = true;
        Assert.Equal(0, got);
        Step();
        Assert.Equal(1, got);
    }

    [Fact]
    public void Stats_count_calls_and_time()
    {
        var sub = Events.On<PingEvent>(_ => Thread.SpinWait(1000));
        for (var i = 0; i < 10; i++) Events.Post(new PingEvent());
        Assert.Equal(10, sub.Stats.Calls);
        Assert.True(sub.Stats.TotalMs > 0);
        Assert.Contains(Events.Diagnostics.Handlers, h => h.Stats.Calls == 10 && h.Owner == "kiln");
        Events.Diagnostics.ResetStats();
        Assert.Equal(0, sub.Stats.Calls);
    }

    [Fact]
    public void Register_subscribes_attribute_methods_and_Unregister_removes_them()
    {
        var mod = NewMod("handlers");
        var target = new ModWithHandlers();
        HostAccess.Bind(target, mod);
        ModWithHandlers.StaticHits = 0;
        var subs = As(mod, () => Events.Register(target));
        Assert.Equal(3, subs.Count);
        Events.Post(new PingEvent());
        Assert.Equal(["high"], target.Got);
        var stop = Events.Post(new StopEvent());
        Assert.True(stop.Canceled);
        Assert.Equal(1, ModWithHandlers.StaticHits);
        Assert.Equal(3, Events.Unregister(target));
        Assert.False(Events.HasSubscribers<PingEvent>());
    }

    [Fact]
    public void Register_with_a_bad_method_subscribes_nothing()
    {
        Assert.Throws<ArgumentException>(() => Events.Register(new BadHandlers()));
        Assert.False(Events.HasSubscribers<PingEvent>());
    }

    [Fact]
    public void Result_events_decide()
    {
        Assert.True(Events.Post(new AskEvent()).Decide(vanilla: true));
        Events.On<AskEvent>(e => e.Deny());
        Assert.False(Events.Post(new AskEvent()).Decide(vanilla: true));
        Events.On<AskEvent>(e => e.Allow(), EventPriority.Low);
        var ev = Events.Post(new AskEvent());
        Assert.Equal(EventResult.Allow, ev.Result);
        Assert.True(ev.Decide(vanilla: false));
    }

    [Fact]
    public void Handlers_run_as_their_mod_and_dead_mods_are_skipped()
    {
        var mod = NewMod("owner");
        string? seen = null;
        As(mod, () => Events.On<PingEvent>(_ => seen = ModContext.CurrentMod?.Info.Id));
        Events.Post(new PingEvent());
        Assert.Equal("owner", seen);
        mod.Retire();
        seen = null;
        Events.Post(new PingEvent());
        Assert.Null(seen);
        Assert.Throws<InvalidOperationException>(() => As(mod, () => Events.On<PingEvent>(_ => { })));
    }

    [Fact]
    public void Subscribing_runs_the_event_class_static_constructor()
    {
        var before = LazyEvent.Started;
        Events.On<LazyEvent>(_ => { });
        Assert.True(LazyEvent.Started >= 1);
        Assert.True(LazyEvent.Started >= before);
    }

    [Fact]
    public void Service_tick_rides_a_mod_and_moves_when_it_unloads()
    {
        var runs = 0;
        ServiceTick.Add("test", (in TickInfo _) => runs++);
        var a = NewMod("a");
        var b = NewMod("b");
        ServiceTick.Offer(a);
        ServiceTick.Offer(b);
        var t1 = HostAccess.Tick(10, 20, 2, 0, 40);
        a.Tick.RaisePre(in t1);
        b.Tick.RaisePre(in t1);  // same step: runs once
        Assert.Equal(1, runs);
        Assert.False(b.Tick.HasPost);
        a.Retire();
        var t2 = HostAccess.Tick(11, 22, 2, 0, 40);
        b.Tick.RaisePre(in t2);
        Assert.Equal(2, runs);
        ServiceTick.HostDriven = true;
        var t3 = HostAccess.Tick(12, 24, 2, 0, 40);
        b.Tick.RaisePre(in t3);
        Assert.Equal(2, runs);
        ServiceTick.Run(in t3);
        Assert.Equal(3, runs);
    }

    [Fact]
    public void Service_tick_handlers_run_as_kiln_and_are_isolated()
    {
        string? mod = "unset";
        ServiceTick.Add("bad", (in TickInfo _) => throw new InvalidOperationException("tick boom"));
        ServiceTick.Add("good", (in TickInfo _) => mod = ModContext.CurrentMod?.Info.Id);
        var m = NewMod("rider");
        As(m, () => Step());
        Assert.Null(mod);
        Assert.True(Backend.Logged("tick boom"));
    }
}
