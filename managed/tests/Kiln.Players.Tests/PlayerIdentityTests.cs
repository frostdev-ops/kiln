// SPDX-License-Identifier: MIT
// identity.players without a game: Kiln.Players' source and entity source over a fake roster, reached through the real
// PlayerRef / EntityRef code of Kiln.Api. The seams in Kiln.Api (IdentitySources) are internal to it, so this test
// sets them by reflection; the entity source sits behind its test adapter (the service never registers it).
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Kiln.PlayerTests;

/// <summary>A roster the test fills: connections with their state, handle, join step and character.</summary>
internal sealed class FakeRoster : IPlayerDirectory
{
    public bool IsAvailable = true;
    public bool ThrowNotSupported;
    public readonly List<Player> Connections = [];
    public readonly Dictionary<string, Guid> Characters = [];

    public bool Available => IsAvailable;

    public Player? Find(EntityId entity)
    {
        if (ThrowNotSupported) throw new NotSupportedException("Players.Find: kiln.players is unavailable on this build");
        return Connections.FirstOrDefault(p => p.Entity == entity);
    }

    public IReadOnlyList<Player> InGame => ThrowNotSupported
        ? throw new NotSupportedException("Players.All: kiln.players is unavailable on this build")
        : Connections.Where(p => p.InGame).ToList();

    public Guid CharacterOf(Player player) => Characters.GetValueOrDefault(player.Name);

    /// <summary>A connection in <paramref name="slot"/>: entity slot + 1, handle slot | generation &lt;&lt; 6.</summary>
    public Player Join(string name, int slot, ulong steam, uint generation, ulong step, PlayerState state = PlayerState.InGame,
        Guid? character = null)
    {
        Leave(slot);
        var p = new Player
        {
            Name = name, Slot = slot, Entity = new EntityId((uint)slot + 1), SteamId = steam, State = state,
            Handle = generation == 0 ? 0 : (uint)slot | generation << 6, JoinedStep = step,
        };
        Connections.Add(p);
        if (character is { } c) Characters[name] = c;
        return p;
    }

    public void Leave(int slot) => Connections.RemoveAll(p => p.Slot == slot);
}

public sealed class PlayerIdentityTests : IDisposable
{
    private const ulong SteamA = 76561190000000001, SteamB = 76561190000000002;
    private static readonly Guid CharA = new("1b4e28ba-2fa1-11d2-883f-0016d3cca427"), CharB = new("2c5f39cb-3fb2-12e3-994f-1127e4ddb538");

    private static readonly PropertyInfo EntitiesSeam = Seam("Entities"), PlayersSeam = Seam("Players");
    private readonly object? _prevEntities = EntitiesSeam.GetValue(null), _prevPlayers = PlayersSeam.GetValue(null);
    private readonly IPlayerDirectory _prevRoster = PlayerIdentity.Roster;
    private readonly FakeRoster _roster = new();

    private static PropertyInfo Seam(string name) =>
        typeof(PlayerRef).Assembly.GetType("Kiln.IdentitySources", throwOnError: true)!
            .GetProperty(name, BindingFlags.Public | BindingFlags.Static)!;

    public PlayerIdentityTests()
    {
        PlayerIdentity.Roster = _roster;
        PlayerIdentity.Install();
        EntitiesSeam.SetValue(null, new PlayersEntitySerialAdapter());
    }

    public void Dispose()
    {
        EntitiesSeam.SetValue(null, _prevEntities);
        PlayersSeam.SetValue(null, _prevPlayers);
        PlayerIdentity.Roster = _prevRoster;
    }

    private static KilnUnavailableException Unavailable(Action call, string capability)
    {
        var e = Assert.Throws<KilnUnavailableException>(call);
        Assert.Equal(capability, e.Capability);
        return e;
    }

    // ---- Of ----

    [Fact]
    public void Of_a_player_in_game_gives_the_person_and_the_stamped_session()
    {
        var alder = _roster.Join("Alder", 0, SteamA, generation: 1, step: 120, character: CharA);
        var r = PlayerRef.Of(new EntityId(1));
        Assert.Equal(SteamA, r.SteamId);
        Assert.Equal(CharA, r.CharacterId);
        Assert.Equal(new EntityId(1), r.Entity.Id);
        Assert.True(r.Entity.HasSerial);
        Assert.Equal(PlayerIdentity.StampOf(alder), r.Entity.Serial);
        Assert.True(r.IsOnline);
    }

    [Fact]
    public void Of_a_non_player_or_an_empty_slot_is_none()
    {
        _roster.Join("Alder", 0, SteamA, 1, 120);
        Assert.True(PlayerRef.Of(new EntityId(2000)).IsNone);  // not a player id
        Assert.True(PlayerRef.Of(new EntityId(5)).IsNone);     // a player id, nobody in that slot
        Assert.True(PlayerRef.Of(EntityId.None).IsNone);
    }

    [Fact]
    public void Of_a_joining_or_leaving_player_is_none()
    {
        _roster.Join("Alder", 0, SteamA, 1, 0, PlayerState.Joining);
        _roster.Join("Birch", 1, SteamB, 1, 130, PlayerState.Leaving);
        Assert.True(PlayerRef.Of(new EntityId(1)).IsNone);
        Assert.True(PlayerRef.Of(new EntityId(2)).IsNone);
    }

    // ---- Resolve ----

    [Fact]
    public void Resolve_finds_the_current_session_or_gives_an_offline_ref()
    {
        _roster.Join("Alder", 0, SteamA, 1, 120, character: CharA);
        var online = new PlayerRef(SteamA, Guid.Empty, EntityRef.None).Resolve();
        Assert.Equal(PlayerRef.Of(new EntityId(1)), online);
        Assert.True(online.IsOnline);

        var offline = new PlayerRef(SteamB, CharB, EntityRef.None).Resolve();
        Assert.Equal(new PlayerRef(SteamB, CharB, EntityRef.None), offline);
        Assert.False(offline.IsOnline);
        Assert.True(PlayerRef.None.Resolve().IsNone);
    }

    [Fact]
    public void Resolve_by_character_when_the_steam_id_is_unknown()
    {
        _roster.Join("Alder", 0, 0, 1, 120, character: CharA);   // no Steam id (records without the log)
        _roster.Join("Birch", 1, SteamB, 1, 125, character: CharB);
        var r = new PlayerRef(0, CharA, EntityRef.None).Resolve();
        Assert.Equal(new EntityId(1), r.Entity.Id);
        Assert.True(r.IsOnline);
        // A Steam id that does not match is another person, even with the same character id.
        Assert.True(new PlayerRef(SteamA, CharB, EntityRef.None).Resolve().Entity.IsNone);
    }

    // ---- unavailable is not "no such player" ----

    [Fact]
    public void Service_off_throws_identity_players_never_none()
    {
        _roster.IsAvailable = false;
        var e = Unavailable(() => PlayerRef.Of(new EntityId(1)), "identity.players");
        Assert.Equal("kiln.players is unavailable", e.Reason);
        Unavailable(() => PlayerRef.Of(new EntityId(2000)), "identity.players");  // not "not a player" either
        Unavailable(() => new PlayerRef(SteamA, Guid.Empty, EntityRef.None).Resolve(), "identity.players");
    }

    [Fact]
    public void A_NotSupportedException_from_kiln_players_becomes_identity_players()
    {
        _roster.ThrowNotSupported = true;
        var e = Unavailable(() => PlayerRef.Of(new EntityId(1)), "identity.players");
        Assert.IsType<NotSupportedException>(e.InnerException);
        Unavailable(() => new PlayerRef(SteamA, Guid.Empty, EntityRef.None).Resolve(), "identity.players");
    }

    [Fact]
    public void No_source_throws_identity_players()
    {
        PlayersSeam.SetValue(null, null);
        Unavailable(() => PlayerRef.Of(new EntityId(1)), "identity.players");
    }

    // ---- slot reuse and rejoin ----

    [Fact]
    public void Slot_reuse_another_person_in_the_same_slot()
    {
        _roster.Join("Alder", 0, SteamA, generation: 1, step: 100);
        var refA = PlayerRef.Of(new EntityId(1));
        _roster.Leave(0);
        Assert.False(refA.IsOnline);
        Assert.True(refA.Resolve().Entity.IsNone);
        _roster.Join("Birch", 0, SteamB, generation: 2, step: 400);
        var refB = PlayerRef.Of(new EntityId(1));

        Assert.Equal(refA.Entity.Id, refB.Entity.Id);
        Assert.NotEqual(refA.Entity.Serial, refB.Entity.Serial);
        Assert.False(refA.IsOnline);
        Assert.False(refA.IsSamePerson(refB));
        Assert.True(refB.IsOnline);
        Assert.True(refA.Resolve().Entity.IsNone);
    }

    [Fact]
    public void Slot_reuse_from_the_game_log_has_no_handle_and_still_differs()
    {
        _roster.Join("Alder", 0, SteamA, generation: 0, step: 100);   // game-log source: handle 0
        var refA = PlayerRef.Of(new EntityId(1));
        _roster.Join("Birch", 0, SteamB, generation: 0, step: 400);
        var refB = PlayerRef.Of(new EntityId(1));
        Assert.True(refA.Entity.HasSerial);
        Assert.False(refA.IsOnline);
        Assert.True(refB.IsOnline);
        Assert.False(refA.IsSamePerson(refB));
    }

    [Fact]
    public void The_same_person_rejoining_gets_a_new_session()
    {
        _roster.Join("Alder", 0, SteamA, generation: 1, step: 100, character: CharA);
        var first = PlayerRef.Of(new EntityId(1));
        _roster.Leave(0);
        _roster.Join("Alder", 0, SteamA, generation: 2, step: 900);
        var second = PlayerRef.Of(new EntityId(1));

        Assert.False(first.IsOnline);
        Assert.True(first.IsSamePerson(second));
        Assert.NotEqual(first, second);  // == compares the session too
        var now = first.Resolve();
        Assert.True(now.IsOnline);
        Assert.Equal(second, now);
    }

    [Fact]
    public void Rejoining_into_another_slot_resolves_there()
    {
        _roster.Join("Alder", 0, SteamA, 1, 100);
        var first = PlayerRef.Of(new EntityId(1));
        _roster.Leave(0);
        _roster.Join("Birch", 0, SteamB, 2, 200);
        _roster.Join("Alder", 3, SteamA, 1, 300);
        var now = first.Resolve();
        Assert.Equal(new EntityId(4), now.Entity.Id);
        Assert.True(now.IsOnline);
        Assert.False(first.IsOnline);
    }

    // ---- the stamp ----

    [Fact]
    public void The_stamp_is_never_zero_and_carries_the_players_place()
    {
        var bare = new Player { Entity = new EntityId(1), State = PlayerState.InGame };   // handle 0, join step 0
        Assert.NotEqual(0UL, PlayerIdentity.StampOf(bare));
        Assert.Equal(1UL, PlayerIdentity.StampOf(bare) >> 60);
        Assert.NotEqual(0UL, PlayerIdentity.SessionValue(bare));
        Assert.True(PlayerIdentity.SessionValue(bare) < 1UL << 60);

        var a = new Player { Entity = new EntityId(1), State = PlayerState.InGame, Handle = 0 | 1u << 6, JoinedStep = 0 };
        var b = a with { Handle = 0 | 2u << 6 };
        var c = a with { JoinedStep = 7 };
        var stamps = new[] { bare, a, b, c }.Select(PlayerIdentity.StampOf).ToHashSet();
        Assert.Equal(4, stamps.Count);
        Assert.All(stamps, s => Assert.Equal(1UL, s >> 60));
        // The slot bits of the handle do not enter it (the entity id already is the slot).
        Assert.Equal(PlayerIdentity.StampOf(a), PlayerIdentity.StampOf(a with { Handle = 5 | 1u << 6 }));
    }

    // ---- the entity source ----

    [Fact]
    public void Entity_source_answers_not_mine_absent_alive()
    {
        var src = new PlayersEntitySource();
        var alder = _roster.Join("Alder", 0, SteamA, 1, 100);
        _roster.Join("Birch", 1, SteamB, 1, 0, PlayerState.Joining);

        Assert.Equal(PlayersEntityAnswer.NotMine, src.Query(new EntityId(2000), out var s0));
        Assert.Equal(0UL, s0);
        Assert.Equal(PlayersEntityAnswer.NotMine, src.Query(EntityId.None, out _));
        Assert.Equal(PlayersEntityAnswer.Absent, src.Query(new EntityId(5), out _));
        Assert.Equal(PlayersEntityAnswer.Absent, src.Query(new EntityId(2), out _));   // joining
        Assert.Equal(PlayersEntityAnswer.Alive, src.Query(new EntityId(1), out var s1));
        Assert.Equal(PlayerIdentity.StampOf(alder), s1);
        Assert.Equal(PlayerRef.Of(new EntityId(1)).Entity.Serial, s1);
    }

    [Fact]
    public void Entity_source_off_throws_identity_entities()
    {
        var src = new PlayersEntitySource();
        _roster.Join("Alder", 0, SteamA, 1, 100);
        var r = PlayerRef.Of(new EntityId(1));
        _roster.IsAvailable = false;
        var e = Unavailable(() => src.Query(new EntityId(1), out _), "identity.entities");
        Assert.Equal("kiln.players is unavailable", e.Reason);
        Unavailable(() => src.Query(new EntityId(5), out _), "identity.entities");
        Unavailable(() => _ = r.IsOnline, "identity.entities");   // not "offline"
        _roster.IsAvailable = true;
        _roster.ThrowNotSupported = true;
        Unavailable(() => src.Query(new EntityId(1), out _), "identity.entities");
    }

    [Fact]
    public void Entity_source_not_mine_needs_no_service()
    {
        // NotMine is decided by the id alone, so the next source in the seam's order is asked even with kiln.players off.
        _roster.IsAvailable = false;
        Assert.Equal(PlayersEntityAnswer.NotMine, new PlayersEntitySource().Query(new EntityId(2000), out _));
    }

    // ---- conversions ----

    [Fact]
    public void Player_converts_to_the_same_ref_as_Of()
    {
        var alder = _roster.Join("Alder", 0, SteamA, 1, 100, character: CharA);
        Assert.Equal(PlayerRef.Of(alder.Entity), (PlayerRef)alder);
        Assert.True(((PlayerRef)alder).IsOnline);
        var joining = _roster.Join("Birch", 1, SteamB, 1, 0, PlayerState.Joining, CharB);
        Assert.Equal(new PlayerRef(SteamB, CharB, EntityRef.None), (PlayerRef)joining);
        Assert.Throws<ArgumentNullException>(() => (PlayerRef)(Player)null!);
    }

    [Fact]
    public void Player_conversion_works_without_the_service()
    {
        var alder = _roster.Join("Alder", 0, SteamA, 1, 100);
        var expected = PlayerRef.Of(alder.Entity);
        _roster.IsAvailable = false;
        Assert.Equal(expected, (PlayerRef)alder);
    }

    [Fact]
    public void TalentHolder_converts_through_Of()
    {
        _roster.Join("Alder", 0, SteamA, 1, 100, character: CharA);
        var holder = new TalentHolder(SteamA, new EntityId(1)) { Character = new CharacterKey(SteamA, CharA) };
        Assert.Equal(PlayerRef.Of(new EntityId(1)), (PlayerRef)holder);
        Assert.True(((PlayerRef)holder).IsOnline);

        // Offline holder: no entity.
        var offline = new TalentHolder(SteamB, EntityId.None) { Character = new CharacterKey(SteamB, CharB) };
        Assert.Equal(new PlayerRef(SteamB, CharB, EntityRef.None), (PlayerRef)offline);

        // The holder's slot now has someone else: the holder's offline ref, not the other person.
        _roster.Join("Birch", 0, SteamB, 2, 400, character: CharB);
        Assert.Equal(new PlayerRef(SteamA, CharA, EntityRef.None), (PlayerRef)holder);

        // Nobody in the slot.
        _roster.Leave(0);
        Assert.Equal(new PlayerRef(SteamA, CharA, EntityRef.None), (PlayerRef)holder);

        _roster.IsAvailable = false;
        Unavailable(() => _ = (PlayerRef)holder, "identity.players");
        Assert.Equal(new PlayerRef(SteamB, CharB, EntityRef.None), (PlayerRef)offline);   // no entity: no service needed
    }

    [Fact]
    public void QuestHolder_converts_through_Of()
    {
        _roster.Join("Alder", 0, SteamA, 1, 100, character: CharA);
        var key = new CharacterKey(SteamA, CharA);
        var holder = new QuestHolder($"character:{key.Text}", SteamA, new EntityId(1)) { Character = key };
        Assert.Equal(PlayerRef.Of(new EntityId(1)), (PlayerRef)holder);

        Assert.True(((PlayerRef)QuestHolder.World).IsNone);
        Assert.True(((PlayerRef)default(QuestHolder)).IsNone);
        Assert.True(((PlayerRef)QuestHolder.OfAccount(SteamA)).IsNone);   // the account is not a Steam id for sure
        Assert.Equal(new PlayerRef(0, CharA, EntityRef.None), (PlayerRef)QuestHolder.OfCharacter(key));

        // Another character in the holder's slot.
        _roster.Join("Birch", 0, SteamB, 2, 400, character: CharB);
        Assert.Equal(new PlayerRef(0, CharA, EntityRef.None), (PlayerRef)holder);

        _roster.IsAvailable = false;
        Unavailable(() => _ = (PlayerRef)holder, "identity.players");
    }

    // ---- start ----

    [Fact]
    public void Startup_supplies_the_source_and_does_not_throw_without_kiln_players()
    {
        PlayersSeam.SetValue(null, null);
        EntitiesSeam.SetValue(null, null);
        PlayerIdentity.Roster = NativePlayerDirectory.Instance;   // the real service: not loaded in a test process
        RuntimeHelpers.RunClassConstructor(typeof(PlayersStartup).TypeHandle);
        Assert.Same(PlayerIdentity.RefSource, PlayersSeam.GetValue(null));
        Assert.Null(EntitiesSeam.GetValue(null));   // the service never registers an entity source
        Unavailable(() => PlayerRef.Of(new EntityId(1)), "identity.players");
        Unavailable(() => new PlayerRef(SteamA, Guid.Empty, EntityRef.None).Resolve(), "identity.players");
    }
}
