// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Api.Tests;

/// <summary>EntityRef, ItemInstanceRef and PlayerRef: none values, equality, the conversion to EntityId, and the resolver seams
/// through fakes (id reuse, serial 0, no source, wrong thread).</summary>
public sealed class IdentityTests : KilnTest
{
    private readonly IEntitySerialSource? _prevEntities = IdentitySources.Entities;
    private readonly IPlayerRefSource? _prevPlayers = IdentitySources.Players;
    private readonly FakeSerials _serials = new();

    public IdentityTests()
    {
        IdentitySources.Entities = _serials;
        IdentitySources.Players = null;
    }

    public override void Dispose()
    {
        IdentitySources.Entities = _prevEntities;
        IdentitySources.Players = _prevPlayers;
        base.Dispose();
    }

    private sealed class FakeSerials : IEntitySerialSource
    {
        public readonly Dictionary<uint, ulong> Live = [];
        public int Calls;

        public bool TryGetSerial(EntityId entity, out ulong serial)
        {
            Calls++;
            return Live.TryGetValue(entity.Value, out serial);
        }
    }

    private sealed class FakePlayers : IPlayerRefSource
    {
        public readonly Dictionary<uint, PlayerRef> ByEntity = [];
        public readonly Dictionary<(ulong, Guid), PlayerRef> Online = [];
        public int Calls;

        public PlayerRef FromEntity(EntityId player)
        {
            Calls++;
            return ByEntity.TryGetValue(player.Value, out var p) ? p : PlayerRef.None;
        }

        public PlayerRef Current(ulong steamId, Guid characterId)
        {
            Calls++;
            return Online.TryGetValue((steamId, characterId), out var p) ? p : new PlayerRef(steamId, characterId, EntityRef.None);
        }
    }

    private static uint TakesId(EntityId id) => id.Value;

    // --- none values, equality, conversion ---

    [Fact]
    public void Defaults_are_none()
    {
        Assert.True(default(EntityRef).IsNone);
        Assert.Equal(EntityRef.None, default);
        Assert.False(default(EntityRef).HasSerial);
        Assert.Equal("none", EntityRef.None.ToString());
        Assert.True(default(ItemInstanceRef).IsNone);
        Assert.Equal(ItemInstanceRef.None, default);
        Assert.Equal("none", ItemInstanceRef.None.ToString());
        Assert.True(default(PlayerRef).IsNone);
        Assert.Equal(PlayerRef.None, default);
        Assert.False(default(PlayerRef).HasCharacter);
        Assert.Equal("none", PlayerRef.None.ToString());
        Assert.True(default(ItemId).IsNone);
    }

    [Fact]
    public void Any_part_set_is_not_none()
    {
        Assert.False(new EntityRef(new EntityId(3), 0).IsNone);
        Assert.True(new EntityRef(EntityId.None, 9).IsNone);  // a serial without an id refers to nothing
        Assert.False(new ItemInstanceRef(new EntityRef(new EntityId(4), 1), ItemId.None).IsNone);
        Assert.True(new ItemInstanceRef(EntityRef.None, new ItemId(7)).IsNone);
        Assert.False(new PlayerRef(76561198000000001, Guid.Empty, EntityRef.None).IsNone);
        Assert.False(new PlayerRef(0, Guid.NewGuid(), EntityRef.None).IsNone);
        Assert.False(new PlayerRef(0, Guid.Empty, new EntityRef(new EntityId(2), 0)).IsNone);
        Assert.True(new PlayerRef(0, Guid.NewGuid(), EntityRef.None).HasCharacter);
        Assert.True(new EntityRef(new EntityId(3), 8).HasSerial);
    }

    [Fact]
    public void Equality_and_hash_use_every_part()
    {
        var a = new EntityRef(new EntityId(12), 345);
        Assert.Equal(a, new EntityRef(new EntityId(12), 345));
        Assert.Equal(a.GetHashCode(), new EntityRef(new EntityId(12), 345).GetHashCode());
        Assert.NotEqual(a, new EntityRef(new EntityId(12), 346));
        Assert.NotEqual(a, new EntityRef(new EntityId(13), 345));

        var i = new ItemInstanceRef(a, new ItemId(0xabcd));
        Assert.Equal(i, new ItemInstanceRef(new EntityRef(new EntityId(12), 345), new ItemId(0xabcd)));
        Assert.Equal(i.GetHashCode(), new ItemInstanceRef(new EntityRef(new EntityId(12), 345), new ItemId(0xabcd)).GetHashCode());
        Assert.NotEqual(i, new ItemInstanceRef(a, new ItemId(0xabce)));
        Assert.NotEqual(i, new ItemInstanceRef(new EntityRef(new EntityId(12), 1), new ItemId(0xabcd)));

        var g = Guid.NewGuid();
        var p = new PlayerRef(7, g, a);
        Assert.Equal(p, new PlayerRef(7, g, new EntityRef(new EntityId(12), 345)));
        Assert.Equal(p.GetHashCode(), new PlayerRef(7, g, new EntityRef(new EntityId(12), 345)).GetHashCode());
        Assert.NotEqual(p, new PlayerRef(8, g, a));
        Assert.NotEqual(p, new PlayerRef(7, Guid.NewGuid(), a));
        Assert.NotEqual(p, new PlayerRef(7, g, EntityRef.None));
    }

    [Fact]
    public void ToString_shows_id_serial_item_and_player()
    {
        Assert.Equal("12#345", new EntityRef(new EntityId(12), 345).ToString());
        Assert.Equal("12", new EntityRef(new EntityId(12), 0).ToString());
        Assert.Equal("0x0000abcd@12#345", new ItemInstanceRef(new EntityRef(new EntityId(12), 345), new ItemId(0xabcd)).ToString());
        var g = Guid.Parse("1b4e28ba-2fa1-11d2-883f-0016d3cca427");
        Assert.Equal("76/1b4e28ba-2fa1-11d2-883f-0016d3cca427@3#7", new PlayerRef(76, g, new EntityRef(new EntityId(3), 7)).ToString());
        Assert.Equal("76", new PlayerRef(76, Guid.Empty, EntityRef.None).ToString());
    }

    [Fact]
    public void EntityRef_converts_to_EntityId_for_existing_apis()
    {
        var r = new EntityRef(new EntityId(42), 9);
        Assert.Equal(42u, TakesId(r));
        EntityId id = r;
        Assert.Equal(new EntityId(42), id);
        Assert.Equal(EntityId.None, (EntityId)EntityRef.None);
    }

    // --- EntityRef.Of / IsAlive ---

    [Fact]
    public void Of_captures_the_current_serial_and_IsAlive_tracks_it()
    {
        _serials.Live[12] = 345;
        var r = EntityRef.Of(new EntityId(12));
        Assert.Equal(new EntityRef(new EntityId(12), 345), r);
        Assert.True(r.IsAlive);
    }

    [Fact]
    public void Destroyed_entity_is_not_alive_and_Of_gives_none()
    {
        _serials.Live[12] = 345;
        var r = EntityRef.Of(new EntityId(12));
        _serials.Live.Remove(12);
        Assert.False(r.IsAlive);
        Assert.Equal(EntityRef.None, EntityRef.Of(new EntityId(12)));
    }

    [Fact]
    public void Reused_id_with_another_serial_is_not_alive()
    {
        _serials.Live[12] = 345;
        var old = EntityRef.Of(new EntityId(12));
        _serials.Live[12] = 346;  // destroyed, and the id handed to a new spawn
        Assert.False(old.IsAlive);
        var fresh = EntityRef.Of(new EntityId(12));
        Assert.True(fresh.IsAlive);
        Assert.NotEqual(old, fresh);
        Assert.Equal(old.Id, fresh.Id);
    }

    [Fact]
    public void Serial_zero_falls_back_to_existence()
    {
        _serials.Live[5] = 0;  // exists, no spawn stamp
        var r = EntityRef.Of(new EntityId(5));
        Assert.False(r.HasSerial);
        Assert.Equal(new EntityId(5), r.Id);
        Assert.True(r.IsAlive);
        _serials.Live[5] = 77;  // a ref without a serial cannot tell a new spawn apart: still true
        Assert.True(r.IsAlive);
        _serials.Live.Remove(5);
        Assert.False(r.IsAlive);
    }

    [Fact]
    public void None_in_gives_none_without_asking_the_source()
    {
        Assert.Equal(EntityRef.None, EntityRef.Of(EntityId.None));
        Assert.False(EntityRef.None.IsAlive);
        Assert.Equal(0, _serials.Calls);
    }

    [Fact]
    public void No_entity_source_Of_and_liveness_throw_unavailable_except_for_none()
    {
        _serials.Live[12] = 345;
        var r = EntityRef.Of(new EntityId(12));
        IdentitySources.Entities = null;
        var e = Assert.Throws<KilnUnavailableException>(() => EntityRef.Of(new EntityId(12)));
        Assert.Equal("identity.entities", e.Capability);
        var alive = Assert.Throws<KilnUnavailableException>(() => r.IsAlive);
        Assert.Equal(e.Capability, alive.Capability);
        Assert.Equal(e.Reason, alive.Reason);
        var item = Assert.Throws<KilnUnavailableException>(() => new ItemInstanceRef(r, new ItemId(1)).IsAlive);
        Assert.Equal(e.Capability, item.Capability);
        var player = Assert.Throws<KilnUnavailableException>(() => new PlayerRef(7, Guid.Empty, r).IsOnline);
        Assert.Equal(e.Capability, player.Capability);
        Assert.Equal(EntityRef.None, EntityRef.Of(EntityId.None));
        Backend.TickThread = false;
        Assert.False(EntityRef.None.IsAlive);
        Assert.False(new EntityRef(EntityId.None, 9).IsAlive);
        Assert.False(ItemInstanceRef.None.IsAlive);
        Assert.False(PlayerRef.None.IsOnline);
    }

    [Fact]
    public void Wrong_thread_throws_for_Of_and_IsAlive()
    {
        _serials.Live[12] = 345;
        var r = EntityRef.Of(new EntityId(12));
        Backend.TickThread = false;
        Assert.Throws<InvalidOperationException>(() => EntityRef.Of(new EntityId(12)));
        Assert.Throws<InvalidOperationException>(() => r.IsAlive);
        Assert.Throws<InvalidOperationException>(() => new ItemInstanceRef(r, new ItemId(1)).IsAlive);
        Assert.Throws<InvalidOperationException>(() => PlayerRef.Of(new EntityId(1)));
        Assert.False(EntityRef.None.IsAlive);  // none never asks, so it never throws
    }

    // --- ItemInstanceRef ---

    [Fact]
    public void ItemInstanceRef_IsAlive_follows_its_entity()
    {
        _serials.Live[900] = 11;
        var item = new ItemInstanceRef(EntityRef.Of(new EntityId(900)), new ItemId(0x1234));
        Assert.True(item.IsAlive);
        _serials.Live[900] = 12;
        Assert.False(item.IsAlive);
        Assert.False(ItemInstanceRef.None.IsAlive);
    }

    // --- PlayerRef ---

    [Fact]
    public void PlayerRef_without_a_session_serial_is_not_online_even_when_the_slot_exists()
    {
        var players = new FakePlayers();
        IdentitySources.Players = players;
        var character = Guid.NewGuid();
        _serials.Live[3] = 0;
        var old = new PlayerRef(7, character, EntityRef.Of(new EntityId(3)));
        Assert.True(old.Entity.IsAlive);  // entity existence alone cannot identify a player session
        Assert.False(old.IsOnline);

        _serials.Live[3] = 71;  // the slot now belongs to another session
        Assert.False(old.IsOnline);
        players.Online[(7, character)] = new PlayerRef(7, character, EntityRef.Of(new EntityId(3)));
        Assert.True(old.Resolve().IsOnline);
    }

    [Fact]
    public void PlayerRef_Of_Resolve_and_IsOnline_through_the_source()
    {
        var players = new FakePlayers();
        IdentitySources.Players = players;
        var g = Guid.NewGuid();
        _serials.Live[3] = 70;
        var session = EntityRef.Of(new EntityId(3));
        var online = new PlayerRef(76561198000000001, g, session);
        players.ByEntity[3] = online;
        players.Online[(76561198000000001, g)] = online;

        var p = PlayerRef.Of(new EntityId(3));
        Assert.Equal(online, p);
        Assert.True(p.IsOnline);
        Assert.Equal(PlayerRef.None, PlayerRef.Of(new EntityId(500)));  // not a player
        Assert.Equal(online, p.Resolve());

        // The player leaves; entity 3 goes to someone else. The old ref is offline, Resolve says so too.
        _serials.Live[3] = 71;
        players.Online.Remove((76561198000000001, g));
        Assert.False(p.IsOnline);
        var now = p.Resolve();
        Assert.Equal(76561198000000001ul, now.SteamId);
        Assert.Equal(g, now.CharacterId);
        Assert.True(now.Entity.IsNone);
        Assert.False(now.IsOnline);

        // Back in a new session.
        _serials.Live[9] = 72;
        players.Online[(76561198000000001, g)] = new PlayerRef(76561198000000001, g, EntityRef.Of(new EntityId(9)));
        Assert.Equal(new EntityRef(new EntityId(9), 72), p.Resolve().Entity);
        Assert.True(p.Resolve().IsOnline);
    }

    [Fact]
    public void PlayerRef_none_in_gives_none_without_asking()
    {
        var players = new FakePlayers();
        IdentitySources.Players = players;
        Assert.Equal(PlayerRef.None, PlayerRef.Of(EntityId.None));
        Assert.Equal(PlayerRef.None, PlayerRef.None.Resolve());
        Assert.False(PlayerRef.None.IsOnline);
        Assert.Equal(0, players.Calls);
    }

    [Fact]
    public void PlayerRef_without_a_source_throws_unavailable()
    {
        var e1 = Assert.Throws<KilnUnavailableException>(() => PlayerRef.Of(new EntityId(3)));
        Assert.Equal("identity.players", e1.Capability);
        var e2 = Assert.Throws<KilnUnavailableException>(() => new PlayerRef(5, Guid.Empty, EntityRef.None).Resolve());
        Assert.Equal("identity.players", e2.Capability);
        Assert.Equal(PlayerRef.None, PlayerRef.Of(EntityId.None));
        Assert.Equal(PlayerRef.None, PlayerRef.None.Resolve());
    }
}
