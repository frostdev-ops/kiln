// SPDX-License-Identifier: MIT
// Event filters (dev.27): Events.On<T>(handler, filter) gets only the events about its entities; the union the bus gives
// a service that holds work for its handlers (kiln.combat's hit hold) is null as soon as one handler takes everything.
using Xunit;

namespace Kiln.Api.Tests;

public sealed class HitLikeEvent(EntityId target, EntityId attacker) : KilnEvent, IEntityEvent
{
    public EntityId Target { get; } = target;
    public EntityId Attacker { get; } = attacker;
    public bool Involves(EntityId entity) => !entity.IsNone && (entity == Target || entity == Attacker);
}

public sealed class NoEntityEvent : KilnEvent;

public sealed class EventFilterTests : KilnTest
{
    private static readonly EntityId Doll = new(100), Other = new(200), Player = new(7);

    [Fact]
    public void A_filtered_handler_gets_only_its_entities_events()
    {
        var watched = new List<EntityId>();
        var all = 0;
        using var a = Events.On<HitLikeEvent>(e => watched.Add(e.Target), EventFilter.ForEntity(Doll));
        using var b = Events.On<HitLikeEvent>(_ => all++);
        Events.Post(new HitLikeEvent(Other, Player));
        Events.Post(new HitLikeEvent(Doll, Player));
        Events.Post(new HitLikeEvent(Other, Doll));  // the doll hits: it takes part
        Assert.Equal([Doll, Other], watched);
        Assert.Equal(3, all);
        Assert.Equal(EventFilter.ForEntity(Doll).Entities!.Single(), a.Filter!.Entities!.Single());
        Assert.Null(b.Filter);
    }

    [Fact]
    public void The_union_is_null_when_one_handler_takes_everything()
    {
        using var a = Events.On<HitLikeEvent>(_ => { }, EventFilter.ForEntity(Doll));
        using var b = Events.On<HitLikeEvent>(_ => { }, EventFilter.ForEntities([Other, Doll]));
        Assert.Equal(new HashSet<EntityId> { Doll, Other }, Events.EntityFilterUnion(typeof(HitLikeEvent)));
        var v = Events.Version;
        using (Events.On<KilnEvent>(_ => { }))  // a catch-all of a base class takes hits too
        {
            Assert.NotEqual(v, Events.Version);
            Assert.Null(Events.EntityFilterUnion(typeof(HitLikeEvent)));
        }
        Assert.NotNull(Events.EntityFilterUnion(typeof(HitLikeEvent)));
        b.Dispose();
        Assert.Equal(new HashSet<EntityId> { Doll }, Events.EntityFilterUnion(typeof(HitLikeEvent)));
    }

    [Fact]
    public void The_filter_keeps_its_own_copy_of_the_entities()
    {
        var mine = new HashSet<EntityId> { Doll };
        var filter = new EventFilter { Entities = mine };
        var got = new List<EntityId>();
        using var a = Events.On<HitLikeEvent>(e => got.Add(e.Target), filter);
        mine.Add(Other);        // the caller's set changes afterwards: the subscription does not
        mine.Remove(Doll);
        Events.Post(new HitLikeEvent(Other, Player));
        Events.Post(new HitLikeEvent(Doll, Player));
        Assert.Equal([Doll], got);
        Assert.Equal(new HashSet<EntityId> { Doll }, Events.EntityFilterUnion(typeof(HitLikeEvent)));
        Assert.Equal([Doll], filter.Entities!);
    }

    // ---- EntityWatch: what a service holds for the handlers follows the subscriptions ----

    private sealed class Service
    {
        public readonly List<uint[]> Pushes = new();
        public bool Refuses;
        public bool Push(uint[] ids)
        {
            if (Refuses) return false;
            lock (Pushes) Pushes.Add(ids);
            return true;
        }
        public uint[] Last { get { lock (Pushes) return Pushes[^1]; } }
    }

    [Fact]
    public void A_new_subscription_is_with_the_service_before_On_returns()
    {
        var svc = new Service();
        using var watch = new EntityWatch([typeof(HitLikeEvent)], svc.Push);
        using var a = Events.On<HitLikeEvent>(_ => { }, EventFilter.ForEntity(Doll));
        Assert.Equal([Doll.Value], svc.Last);   // no Refresh was called: the subscription itself told the service
        // from another thread, as a mod's worker would: when On has returned, the service watches that entity too
        EventSubscription? b = null;
        var t = new Thread(() => b = Events.On<HitLikeEvent>(_ => { }, EventFilter.ForEntity(Other)));
        t.Start();
        t.Join();
        Assert.Equal([Doll.Value, Other.Value], svc.Last);
        // a handler that takes everything: the service holds everything (the empty list), again at once
        using var all = Events.On<HitLikeEvent>(_ => { });
        Assert.Empty(svc.Last);
        b!.Dispose();
    }

    [Fact]
    public void A_removed_subscription_narrows_the_set_at_the_next_refresh()
    {
        var svc = new Service();
        using var watch = new EntityWatch([typeof(HitLikeEvent)], svc.Push);
        using var a = Events.On<HitLikeEvent>(_ => { }, EventFilter.ForEntity(Doll));
        var all = Events.On<HitLikeEvent>(_ => { });
        Assert.Empty(svc.Last);
        all.Dispose();
        Assert.Empty(svc.Last);                 // holding more than needed for a step loses nothing
        watch.Refresh();                        // the service's own step
        Assert.Equal([Doll.Value], svc.Last);
        var pushes = svc.Pushes.Count;
        watch.Refresh();
        Assert.Equal(pushes, svc.Pushes.Count); // nothing changed: nothing pushed
    }

    [Fact]
    public void Every_added_subscription_is_pushed_even_when_the_entities_stay_the_same()
    {
        // the push is where a service passes on what else a new handler needs (kiln.combat: the kinds of event it wants)
        var svc = new Service();
        using var watch = new EntityWatch([typeof(HitLikeEvent)], svc.Push);
        using var a = Events.On<HitLikeEvent>(_ => { });
        var after = svc.Pushes.Count;
        Assert.Empty(svc.Last);
        using var b = Events.On<HitLikeEvent>(_ => { });             // still "everything": pushed all the same
        Assert.Equal(after + 1, svc.Pushes.Count);
        using var c = Events.On<NoEntityEvent>(_ => { });            // another event altogether: pushed too
        Assert.Equal(after + 2, svc.Pushes.Count);
        watch.Refresh();                                              // the service's own step pushes nothing new
        watch.Refresh();
        Assert.Equal(after + 2, svc.Pushes.Count);
    }

    [Fact]
    public void A_push_the_service_did_not_take_is_tried_again()
    {
        var svc = new Service { Refuses = true };
        using var watch = new EntityWatch([typeof(HitLikeEvent)], svc.Push);
        using var a = Events.On<HitLikeEvent>(_ => { }, EventFilter.ForEntity(Doll));
        Assert.Empty(svc.Pushes);
        svc.Refuses = false;
        watch.Refresh();
        Assert.Equal([Doll.Value], svc.Last);
    }

    [Fact]
    public void The_watched_ids_of_several_event_types_are_one_set()
    {
        Assert.Equal([3u, 5u, 9u], EntityWatch.Compute([new HashSet<EntityId> { new(9), new(3) }, new HashSet<EntityId> { new(5), new(3) }]));
        Assert.Empty(EntityWatch.Compute([new HashSet<EntityId> { new(9) }, null]));  // one kind has a handler that takes everything
        Assert.Empty(EntityWatch.Compute([new HashSet<EntityId>(), new HashSet<EntityId>()]));
        Assert.Empty(EntityWatch.Compute([]));
    }

    [Fact]
    public void A_handler_that_fails_to_follow_does_not_break_subscribing()
    {
        using var watch = new EntityWatch([typeof(HitLikeEvent)], _ => throw new InvalidOperationException("native table gone"));
        using var a = Events.On<HitLikeEvent>(_ => { }, EventFilter.ForEntity(Doll));
        Assert.True(a.Active);
    }

    [Fact]
    public void Nobody_listening_gives_an_empty_union() => Assert.Empty(Events.EntityFilterUnion(typeof(NoEntityEvent))!);

    [Fact]
    public void An_entity_filter_needs_an_entity_event_and_an_entity()
    {
        var e = Assert.Throws<ArgumentException>(() => Events.On<NoEntityEvent>(_ => { }, EventFilter.ForEntity(Doll)));
        Assert.Contains("IEntityEvent", e.Message);
        Assert.Throws<ArgumentException>(() => EventFilter.ForEntity(EntityId.None));
        Assert.Throws<ArgumentException>(() => EventFilter.ForEntities([]));
        Assert.Throws<ArgumentException>(() => Events.On<HitLikeEvent>(_ => { }, new EventFilter { Entities = new HashSet<EntityId>() }));
        Assert.Throws<ArgumentNullException>(() => Events.On<HitLikeEvent>(_ => { }, (EventFilter)null!));
        // a filter with no field set takes everything, on any event type
        var got = 0;
        using var s = Events.On<NoEntityEvent>(_ => got++, new EventFilter());
        Events.Post(new NoEntityEvent());
        Assert.Equal(1, got);
    }

    [Fact]
    public void Passes_reads_the_event()
    {
        var f = EventFilter.ForEntities([Doll, Other]);
        Assert.True(f.Passes(new HitLikeEvent(Doll, Player)));
        Assert.False(f.Passes(new HitLikeEvent(Player, Player)));
        Assert.False(f.Passes(new NoEntityEvent()));
        Assert.True(new EventFilter().Passes(new NoEntityEvent()));
    }
}
