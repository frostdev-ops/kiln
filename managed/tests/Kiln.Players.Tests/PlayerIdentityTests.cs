// SPDX-License-Identifier: MIT
// identity.players without a game: Kiln.Players' source and entity source over a fake roster, reached through the real
// PlayerRef / EntityRef code of Kiln.Api and its entity seam (PlayerIdentity.Install registers the players' entity
// source at EntitySourceOrder.Players, as PlayersStartup does). The seam (IdentitySources) is internal to Kiln.Api, so
// this test saves, clears and restores it by reflection.
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

    private static readonly Type Sources = typeof(PlayerRef).Assembly.GetType("Kiln.IdentitySources", throwOnError: true)!;
    private static readonly Type Order = typeof(PlayerRef).Assembly.GetType("Kiln.EntitySourceOrder", throwOnError: true)!;
    private static readonly PropertyInfo EntitiesSeam = Sources.GetProperty("Entities", BindingFlags.Public | BindingFlags.Static)!;
    private static readonly PropertyInfo PlayersSeam = Sources.GetProperty("Players", BindingFlags.Public | BindingFlags.Static)!;
    private static readonly MethodInfo SetSource = Sources.GetMethod("SetEntitySource", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo GetSource = Sources.GetMethod("EntitySource", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly object[] Places = Enum.GetValues(Order).Cast<object>().ToArray();

    private readonly object? _prevEntities = EntitiesSeam.GetValue(null), _prevPlayers = PlayersSeam.GetValue(null);
    private readonly object?[] _prevSources = Places.Select(p => GetSource.Invoke(null, [p])).ToArray();
    private readonly IPlayerDirectory _prevRoster = PlayerIdentity.Roster;
    private readonly FakeRoster _roster = new();

    /// <summary>The entity source registered at this place of the seam's order ("Players", "Items", "Spawn").</summary>
    private static object? SourceAt(string place) => GetSource.Invoke(null, [Enum.Parse(Order, place)]);

    private static void ClearSeams()
    {
        EntitiesSeam.SetValue(null, null);
        PlayersSeam.SetValue(null, null);
        foreach (var p in Places) SetSource.Invoke(null, [p, null]);
    }

    public PlayerIdentityTests()
    {
        // Only the players' source in the seam: no single-source override, no spawn or item source.
        ClearSeams();
        PlayerIdentity.Roster = _roster;
        PlayerIdentity.Install();
    }

    public void Dispose()
    {
        EntitiesSeam.SetValue(null, _prevEntities);
        PlayersSeam.SetValue(null, _prevPlayers);
        for (var i = 0; i < Places.Length; i++) SetSource.Invoke(null, [Places[i], _prevSources[i]]);
        PlayerIdentity.Roster = _prevRoster;
    }

    /// <summary>The players' entity source's answer as its number (NotMine 0, Absent 1, Alive 2). Its signature uses
    /// Kiln.Api's internal EntitySerialAnswer, which this assembly cannot name, so it is called by reflection.</summary>
    private static int Answer(EntityId e, out ulong stamp)
    {
        var args = new object?[] { e, 0UL };
        try
        {
            var answer = typeof(PlayersEntitySource).GetMethod("Query")!.Invoke(PlayerIdentity.EntitySource, args)!;
            stamp = (ulong)args[1]!;
            return (int)answer;
        }
        catch (TargetInvocationException x) when (x.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(x.InnerException);
            throw;
        }
    }

    private const int NotMine = 0, Absent = 1, Alive = 2;

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

    [Fact]
    public void The_largest_generation_and_step_still_fit_the_seams_stamp()
    {
        // IdentitySources.Stamp refuses 0 and anything above 2^60 - 1; the packing masks into exactly that range.
        var max = new Player { Entity = new EntityId(16), Slot = 15, State = PlayerState.InGame, Handle = uint.MaxValue, JoinedStep = ulong.MaxValue };
        Assert.Equal((1UL << 60) - 1, PlayerIdentity.SessionValue(max));
        Assert.Equal(ulong.MaxValue >> 3, PlayerIdentity.StampOf(max));   // 0x1 in bits 60-63, all value bits set
        var top = max with { JoinedStep = (1UL << 34) - 1, Handle = (uint)((1UL << 25) - 1) << 6 };
        Assert.Equal(PlayerIdentity.StampOf(max), PlayerIdentity.StampOf(top));
    }

    [Fact]
    public void The_source_through_EntityRef_Of_and_the_snapshot_give_the_same_ref()
    {
        var alder = _roster.Join("Alder", 0, SteamA, 3, 250, character: CharA);
        Assert.Equal(EntityRef.Of(alder.Entity), PlayerIdentity.EntityOf(alder));
        Assert.Equal(PlayerIdentity.LiveRefOf(alder), PlayerIdentity.RefOf(alder));
        Assert.Equal(PlayerRef.Of(alder.Entity), PlayerIdentity.RefOf(alder));
        Assert.Equal(new PlayerRef(SteamA, Guid.Empty, EntityRef.None).Resolve(), PlayerIdentity.RefOf(alder));
    }

    [Fact]
    public void A_snapshot_of_an_ended_session_converts_to_that_session()
    {
        var first = _roster.Join("Alder", 0, SteamA, 1, 100);
        _roster.Join("Birch", 0, SteamB, 2, 400);
        var r = (PlayerRef)first;                     // not the slot's current session
        Assert.NotEqual(PlayerRef.Of(first.Entity), r);
        Assert.False(r.IsOnline);
        var off = Task.Run(() => (PlayerRef)first).GetAwaiter().GetResult();   // any thread
        Assert.Equal(r, off);
    }

    // ---- the entity source ----

    [Fact]
    public void Entity_source_answers_not_mine_absent_alive()
    {
        var alder = _roster.Join("Alder", 0, SteamA, 1, 100);
        _roster.Join("Birch", 1, SteamB, 1, 0, PlayerState.Joining);

        Assert.Equal(NotMine, Answer(new EntityId(2000), out var s0));
        Assert.Equal(0UL, s0);
        Assert.Equal(NotMine, Answer(EntityId.None, out _));
        Assert.Equal(Absent, Answer(new EntityId(5), out _));
        Assert.Equal(Absent, Answer(new EntityId(2), out _));   // joining
        Assert.Equal(Alive, Answer(new EntityId(1), out var s1));
        Assert.Equal(PlayerIdentity.StampOf(alder), s1);
        Assert.Equal(PlayerRef.Of(new EntityId(1)).Entity.Serial, s1);
    }

    [Fact]
    public void Entity_source_off_throws_identity_entities()
    {
        _roster.Join("Alder", 0, SteamA, 1, 100);
        var r = PlayerRef.Of(new EntityId(1));
        _roster.IsAvailable = false;
        var e = Unavailable(() => Answer(new EntityId(1), out _), "identity.entities");
        Assert.Equal("kiln.players is unavailable", e.Reason);
        Unavailable(() => Answer(new EntityId(5), out _), "identity.entities");
        Unavailable(() => _ = r.IsOnline, "identity.entities");   // not "offline"
        _roster.IsAvailable = true;
        _roster.ThrowNotSupported = true;
        Unavailable(() => Answer(new EntityId(1), out _), "identity.entities");
    }

    [Fact]
    public void Entity_source_not_mine_needs_no_service()
    {
        // NotMine is decided by the id alone, so the next source in the seam's order is asked even with kiln.players off.
        _roster.IsAvailable = false;
        Assert.Equal(NotMine, Answer(new EntityId(2000), out _));
    }

    [Fact]
    public void Ids_outside_the_player_slots_need_another_source()
    {
        // Known limit: with only the players' source registered (no spawn or item source yet), every source says
        // NotMine for a non-player id, so EntityRef.Of throws identity.entities for it. PlayerRef.Of does not depend on it.
        _roster.Join("Alder", 0, SteamA, 1, 100);
        Unavailable(() => EntityRef.Of(new EntityId(2000)), "identity.entities");
        Assert.True(PlayerRef.Of(new EntityId(2000)).IsNone);
        Assert.True(EntityRef.Of(new EntityId(5)).IsNone);   // a player slot: the players' source answers Absent
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuestHolder_without_a_character_checks_the_account_before_using_a_reused_slot(bool sessionOnly)
    {
        _roster.Join("Alder", 0, SteamA, 1, 100);
        var holder = new QuestHolder(sessionOnly ? "session:1" : $"player:{SteamA}",
            sessionOnly ? 0 : SteamA, new EntityId(1));
        if (!sessionOnly) Assert.Equal(PlayerRef.Of(holder.Entity), (PlayerRef)holder);
        _roster.Join("Birch", 0, SteamB, 2, 400);
        Assert.Equal(PlayerRef.None, (PlayerRef)holder);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuestHolder_checks_the_account_when_the_reused_slots_character_is_unknown(bool hasAccount)
    {
        _roster.Join("Alder", 0, SteamA, 1, 100, character: CharA);
        var holder = new QuestHolder($"character:{SteamA}.{CharA}", hasAccount ? SteamA : 0, new EntityId(1))
            { Character = new CharacterKey(SteamA, CharA) };
        Assert.Equal(PlayerRef.Of(holder.Entity), (PlayerRef)holder);
        _roster.Join("Birch", 0, SteamB, 2, 400);
        Assert.Equal(new PlayerRef(0, CharA, EntityRef.None), (PlayerRef)holder);
    }

    [Fact]
    public void QuestHolder_can_check_the_account_while_the_same_players_character_is_unknown()
    {
        _roster.Join("Alder", 0, SteamA, 1, 100);
        var holder = new QuestHolder($"character:{SteamA}.{CharA}", SteamA, new EntityId(1))
            { Character = new CharacterKey(SteamA, CharA) };
        Assert.Equal(PlayerRef.Of(holder.Entity), (PlayerRef)holder);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TalentHolder_returns_offline_when_no_identity_can_be_compared_after_slot_reuse(bool hasSteam, bool hasCharacter)
    {
        _roster.Join("Alder", 0, SteamA, 1, 100, character: CharA);
        var holder = new TalentHolder(hasSteam ? SteamA : 0, new EntityId(1))
            { Character = hasCharacter ? new CharacterKey(SteamA, CharA) : default };
        _roster.Join("Birch", 0, hasSteam ? 0 : SteamB, 2, 400);
        Assert.Equal(new PlayerRef(hasSteam ? SteamA : 0, hasCharacter ? CharA : Guid.Empty, EntityRef.None), (PlayerRef)holder);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TalentHolder_accepts_a_comparable_matching_identity(bool bySteam)
    {
        _roster.Join("Alder", 0, bySteam ? SteamA : 0, 1, 100, character: bySteam ? null : CharA);
        var holder = new TalentHolder(bySteam ? SteamA : 0, new EntityId(1))
            { Character = bySteam ? default : new CharacterKey(SteamA, CharA) };
        Assert.Equal(PlayerRef.Of(holder.Entity), (PlayerRef)holder);
    }

    // ---- start ----

    [Fact]
    public void Startup_supplies_the_source_and_does_not_throw_without_kiln_players()
    {
        ClearSeams();
        PlayerIdentity.Roster = NativePlayerDirectory.Instance;   // the real service: not loaded in a test process
        RuntimeHelpers.RunClassConstructor(typeof(PlayersStartup).TypeHandle);
        Assert.Same(PlayerIdentity.RefSource, PlayersSeam.GetValue(null));
        Assert.Same(PlayerIdentity.EntitySource, SourceAt("Players"));
        Assert.Null(SourceAt("Items"));
        Assert.Null(SourceAt("Spawn"));
        Assert.Null(EntitiesSeam.GetValue(null));   // the single-source override is never set by the service
        Unavailable(() => EntityRef.Of(new EntityId(1)), "identity.entities");  // the registered source, kiln.players off
        Unavailable(() => PlayerRef.Of(new EntityId(1)), "identity.players");
        Unavailable(() => new PlayerRef(SteamA, Guid.Empty, EntityRef.None).Resolve(), "identity.players");
    }
}
