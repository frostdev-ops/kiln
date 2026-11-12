// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Api.Tests;

/// <summary>The entity-stamp seam: the sources of <see cref="IdentitySources.SetEntitySource"/> asked in the fixed
/// order Players, Items, Spawn, through <see cref="EntityRef.Of"/> and <see cref="EntityRef.IsAlive"/>.</summary>
public sealed class IdentitySourceOrderTests : KilnTest
{
    private static readonly EntitySourceOrder[] Places = [EntitySourceOrder.Players, EntitySourceOrder.Items, EntitySourceOrder.Spawn];
    private readonly IEntitySerialSource? _prevEntities = IdentitySources.Entities;
    private readonly IEntityStampSource?[] _prev = Places.Select(IdentitySources.EntitySource).ToArray();
    private readonly List<string> _asked = [];

    public IdentitySourceOrderTests()
    {
        IdentitySources.Entities = null;
        foreach (var p in Places) IdentitySources.SetEntitySource(p, null);
    }

    public override void Dispose()
    {
        IdentitySources.Entities = _prevEntities;
        for (var i = 0; i < Places.Length; i++) IdentitySources.SetEntitySource(Places[i], _prev[i]);
        base.Dispose();
    }

    /// <summary>A source that answers from a table and records that it was asked; ids not in the table are NotMine.</summary>
    private sealed class Fake(string name, List<string> asked) : IEntityStampSource
    {
        public readonly Dictionary<uint, (EntitySerialAnswer Answer, ulong Stamp)> Table = [];
        public Exception? Throw;

        public EntitySerialAnswer Query(EntityId entity, out ulong stamp)
        {
            asked.Add(name);
            if (Throw is not null) throw Throw;
            if (Table.TryGetValue(entity.Value, out var a))
            {
                stamp = a.Stamp;
                return a.Answer;
            }
            stamp = 0;
            return EntitySerialAnswer.NotMine;
        }
    }

    private (Fake Players, Fake Items, Fake Spawn) All()
    {
        var p = new Fake("players", _asked);
        var i = new Fake("items", _asked);
        var s = new Fake("spawn", _asked);
        IdentitySources.SetEntitySource(EntitySourceOrder.Players, p);
        IdentitySources.SetEntitySource(EntitySourceOrder.Items, i);
        IdentitySources.SetEntitySource(EntitySourceOrder.Spawn, s);
        return (p, i, s);
    }

    private static readonly EntityId Id = new(2600);

    [Fact]
    public void Sources_are_asked_players_then_items_then_spawn()
    {
        var (_, _, spawn) = All();
        spawn.Table[Id.Value] = (EntitySerialAnswer.Alive, IdentitySources.Stamp(EntitySourceOrder.Spawn, 9));
        var r = EntityRef.Of(Id);
        Assert.Equal(["players", "items", "spawn"], _asked);
        Assert.Equal(new EntityRef(Id, IdentitySources.Stamp(EntitySourceOrder.Spawn, 9)), r);
    }

    [Fact]
    public void Registration_order_does_not_change_the_asking_order()
    {
        var s = new Fake("spawn", _asked);
        var i = new Fake("items", _asked);
        var p = new Fake("players", _asked);
        IdentitySources.SetEntitySource(EntitySourceOrder.Spawn, s);
        IdentitySources.SetEntitySource(EntitySourceOrder.Items, i);
        IdentitySources.SetEntitySource(EntitySourceOrder.Players, p);
        s.Table[Id.Value] = (EntitySerialAnswer.Alive, IdentitySources.Stamp(EntitySourceOrder.Spawn, 1));
        EntityRef.Of(Id);
        Assert.Equal(["players", "items", "spawn"], _asked);
    }

    [Fact]
    public void NotMine_falls_through_to_the_next_source()
    {
        var (_, items, spawn) = All();
        var stamp = IdentitySources.Stamp(EntitySourceOrder.Items, 41);
        items.Table[Id.Value] = (EntitySerialAnswer.Alive, stamp);
        spawn.Table[Id.Value] = (EntitySerialAnswer.Alive, IdentitySources.Stamp(EntitySourceOrder.Spawn, 41));
        var r = EntityRef.Of(Id);
        Assert.Equal(stamp, r.Serial);
        Assert.Equal(["players", "items"], _asked);  // the first answer that is not NotMine is final
        Assert.True(r.IsAlive);
    }

    [Fact]
    public void Absent_is_final_and_does_not_fall_through()
    {
        var (_, items, spawn) = All();
        items.Table[Id.Value] = (EntitySerialAnswer.Absent, 0);
        spawn.Table[Id.Value] = (EntitySerialAnswer.Alive, IdentitySources.Stamp(EntitySourceOrder.Spawn, 3));
        Assert.Equal(EntityRef.None, EntityRef.Of(Id));
        Assert.Equal(["players", "items"], _asked);
        _asked.Clear();
        Assert.False(new EntityRef(Id, IdentitySources.Stamp(EntitySourceOrder.Spawn, 3)).IsAlive);
        Assert.Equal(["players", "items"], _asked);
    }

    [Fact]
    public void Nobody_answering_throws_identity_entities()
    {
        All();  // every source says NotMine
        var e = Assert.Throws<KilnUnavailableException>(() => EntityRef.Of(Id));
        Assert.Equal("identity.entities", e.Capability);
        Assert.Contains("2600", e.Reason);
        var alive = Assert.Throws<KilnUnavailableException>(() => new EntityRef(Id, 5).IsAlive);
        Assert.Equal("identity.entities", alive.Capability);
        Assert.Equal(["players", "items", "spawn", "players", "items", "spawn"], _asked);
    }

    [Fact]
    public void No_source_registered_throws_identity_entities()
    {
        var e = Assert.Throws<KilnUnavailableException>(() => EntityRef.Of(Id));
        Assert.Equal("identity.entities", e.Capability);
        var alive = Assert.Throws<KilnUnavailableException>(() => new EntityRef(Id, 5).IsAlive);
        Assert.Equal(e.Reason, alive.Reason);
        Assert.Throws<KilnUnavailableException>(() => new ItemInstanceRef(new EntityRef(Id, 5), new ItemId(1)).IsAlive);
    }

    [Fact]
    public void A_throwing_source_ends_the_call()
    {
        var (_, items, spawn) = All();
        items.Throw = new KilnUnavailableException("identity.entities", "kiln.items cannot read now");
        spawn.Table[Id.Value] = (EntitySerialAnswer.Alive, IdentitySources.Stamp(EntitySourceOrder.Spawn, 3));
        var e = Assert.Throws<KilnUnavailableException>(() => EntityRef.Of(Id));
        Assert.Same(items.Throw, e);
        Assert.Equal(["players", "items"], _asked);  // the spawn source is not asked
        _asked.Clear();
        Assert.Same(items.Throw, Assert.Throws<KilnUnavailableException>(() => new EntityRef(Id, 1).IsAlive));
        Assert.Equal(["players", "items"], _asked);
    }

    [Fact]
    public void Stamps_of_two_places_never_compare_equal()
    {
        foreach (var v in new[] { 1ul, 7ul, 0xffff_ffffUL, IdentitySources.MaxStampValue })
        {
            var stamps = Places.Select(p => IdentitySources.Stamp(p, v)).ToArray();
            Assert.Equal(3, stamps.Distinct().Count());
            for (var i = 0; i < Places.Length; i++)
            {
                Assert.Equal(Places[i], IdentitySources.PlaceOf(stamps[i]));
                Assert.Equal((ulong)(i + 1), stamps[i] >> 60);
                Assert.Equal(v, stamps[i] & IdentitySources.MaxStampValue);
                Assert.NotEqual(0ul, stamps[i]);
            }
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentitySources.Stamp(EntitySourceOrder.Items, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentitySources.Stamp(EntitySourceOrder.Items, IdentitySources.MaxStampValue + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentitySources.Stamp((EntitySourceOrder)3, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentitySources.SetEntitySource((EntitySourceOrder)(-1), null));
        Assert.Null(IdentitySources.PlaceOf(0));
        Assert.Null(IdentitySources.PlaceOf(4ul << 60 | 1));
    }

    [Fact]
    public void A_ref_from_one_place_is_not_alive_against_another_places_answer_for_the_same_id()
    {
        var (_, items, spawn) = All();
        spawn.Table[Id.Value] = (EntitySerialAnswer.Alive, IdentitySources.Stamp(EntitySourceOrder.Spawn, 77));
        var old = EntityRef.Of(Id);
        Assert.True(old.IsAlive);
        // The id now names an item entity whose sequence number happens to be the same value.
        items.Table[Id.Value] = (EntitySerialAnswer.Alive, IdentitySources.Stamp(EntitySourceOrder.Items, 77));
        Assert.False(old.IsAlive);
        var fresh = EntityRef.Of(Id);
        Assert.True(fresh.IsAlive);
        Assert.NotEqual(old.Serial, fresh.Serial);
        Assert.Equal(old.Id, fresh.Id);
    }

    [Fact]
    public void A_stamp_outside_the_sources_range_is_refused_as_unavailable()
    {
        var (_, items, _) = All();
        items.Table[Id.Value] = (EntitySerialAnswer.Alive, IdentitySources.Stamp(EntitySourceOrder.Spawn, 5));
        Assert.Equal("identity.entities", Assert.Throws<KilnUnavailableException>(() => EntityRef.Of(Id)).Capability);
        items.Table[Id.Value] = (EntitySerialAnswer.Alive, 12345);  // a raw value without a place
        Assert.Equal("identity.entities", Assert.Throws<KilnUnavailableException>(() => EntityRef.Of(Id)).Capability);
        items.Table[Id.Value] = ((EntitySerialAnswer)9, 0);
        Assert.Equal("identity.entities", Assert.Throws<KilnUnavailableException>(() => EntityRef.Of(Id)).Capability);
    }

    [Fact]
    public void Alive_without_a_stamp_gives_a_ref_without_serial()
    {
        var (_, _, spawn) = All();
        spawn.Table[Id.Value] = (EntitySerialAnswer.Alive, 0);
        var r = EntityRef.Of(Id);
        Assert.Equal(Id, r.Id);
        Assert.False(r.HasSerial);
        Assert.True(r.IsAlive);
        spawn.Table[Id.Value] = (EntitySerialAnswer.Absent, 0);
        Assert.False(r.IsAlive);
    }

    [Fact]
    public void A_removed_source_is_not_asked()
    {
        var (_, items, spawn) = All();
        items.Table[Id.Value] = (EntitySerialAnswer.Alive, IdentitySources.Stamp(EntitySourceOrder.Items, 2));
        spawn.Table[Id.Value] = (EntitySerialAnswer.Alive, IdentitySources.Stamp(EntitySourceOrder.Spawn, 2));
        IdentitySources.SetEntitySource(EntitySourceOrder.Items, null);
        Assert.Null(IdentitySources.EntitySource(EntitySourceOrder.Items));
        Assert.Equal(IdentitySources.Stamp(EntitySourceOrder.Spawn, 2), EntityRef.Of(Id).Serial);
        Assert.Equal(["players", "spawn"], _asked);
    }

    [Fact]
    public void The_explicit_single_source_answers_alone()
    {
        var (players, items, spawn) = All();
        spawn.Table[Id.Value] = (EntitySerialAnswer.Alive, IdentitySources.Stamp(EntitySourceOrder.Spawn, 2));
        var single = new SingleSource();
        single.Live[Id.Value] = 99;
        IdentitySources.Entities = single;
        Assert.Equal(new EntityRef(Id, 99), EntityRef.Of(Id));
        Assert.Empty(_asked);
        IdentitySources.Entities = null;
        Assert.Equal(IdentitySources.Stamp(EntitySourceOrder.Spawn, 2), EntityRef.Of(Id).Serial);
        Assert.NotEmpty(_asked);
        Assert.Same(players, IdentitySources.EntitySource(EntitySourceOrder.Players));
        Assert.Same(items, IdentitySources.EntitySource(EntitySourceOrder.Items));
    }

    private sealed class SingleSource : IEntitySerialSource
    {
        public readonly Dictionary<uint, ulong> Live = [];
        public bool TryGetSerial(EntityId entity, out ulong serial) => Live.TryGetValue(entity.Value, out serial);
    }

    [Fact]
    public void None_and_the_thread_rule_hold_before_any_source_is_asked()
    {
        All();
        Assert.Equal(EntityRef.None, EntityRef.Of(EntityId.None));
        Assert.False(EntityRef.None.IsAlive);
        Assert.Empty(_asked);
        Backend.TickThread = false;
        Assert.Throws<InvalidOperationException>(() => EntityRef.Of(Id));
        Assert.Throws<InvalidOperationException>(() => new EntityRef(Id, 1).IsAlive);
        Assert.False(new EntityRef(EntityId.None, 4).IsAlive);
        Assert.Empty(_asked);
    }
}
