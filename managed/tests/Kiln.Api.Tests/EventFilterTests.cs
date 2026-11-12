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
