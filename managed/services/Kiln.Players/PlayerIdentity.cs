// SPDX-License-Identifier: MIT
// Kiln.Players supplies identity.players: PlayerRef.Of and PlayerRef.Resolve (Kiln.Api/Identity.cs) read players from
// here, through IdentitySources.Players, set by PlayersStartup at the first service tick (ServiceTick.AutoStart).
// It also registers the players' entity-stamp source at EntitySourceOrder.Players (PlayersEntitySource), which
// EntityRef.Of, EntityRef.IsAlive and so PlayerRef.IsOnline ask first.
namespace Kiln;

public sealed partial record Player
{
    /// <summary>This player as a <see cref="PlayerRef"/>: the Steam id, the character id and, while the player is in
    /// game, the session entity stamped for this session (the same ref <see cref="PlayerRef.Of"/> gives for
    /// <see cref="Entity"/> while this session lasts). A player who is joining or leaving gives an offline ref
    /// (<see cref="EntityRef.None"/>). Reads only this snapshot and kiln.characters, so it works on any thread and
    /// throws nothing for a non-null player; a snapshot of an earlier session gives that session's ref, whose
    /// <see cref="PlayerRef.IsOnline"/> is false.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> is null.</exception>
    [AnyThread]
    public static explicit operator PlayerRef(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return PlayerIdentity.RefOf(player);
    }
}

/// <summary>Where Kiln.Players' identity code reads players. The default reads kiln.players; unit tests put a fake
/// here.</summary>
internal interface IPlayerDirectory
{
    /// <summary>False when kiln.players is not loaded or does not work on this build.</summary>
    bool Available { get; }

    /// <summary>The player with this entity, any state, or null. May throw <see cref="NotSupportedException"/> when
    /// the service is unavailable.</summary>
    Player? Find(EntityId entity);

    /// <summary>Players in game. May throw <see cref="NotSupportedException"/> when the service is unavailable.</summary>
    IReadOnlyList<Player> InGame { get; }

    /// <summary>The player's character id (<see cref="Player.CharacterId"/>); <see cref="Guid.Empty"/> when unknown.</summary>
    Guid CharacterOf(Player player);
}

internal sealed class NativePlayerDirectory : IPlayerDirectory
{
    public static readonly NativePlayerDirectory Instance = new();
    public bool Available => Players.Available;
    public Player? Find(EntityId entity) => Players.Find(entity);
    public IReadOnlyList<Player> InGame => Players.All;
    public Guid CharacterOf(Player player) => player.CharacterId;
}

/// <summary>Players as identity sees them: the session stamp, the refs, the two sources.</summary>
internal static class PlayerIdentity
{
    /// <summary>The capability <see cref="PlayerRef.Of"/> and <see cref="PlayerRef.Resolve"/> need.</summary>
    internal const string PlayersCapability = "identity.players";

    /// <summary>The capability <see cref="EntityRef.Of"/> and <see cref="EntityRef.IsAlive"/> need.</summary>
    internal const string EntitiesCapability = "identity.entities";

    /// <summary>Where players come from (tests replace it).</summary>
    internal static IPlayerDirectory Roster { get; set; } = NativePlayerDirectory.Instance;

    /// <summary>The source <see cref="PlayersStartup"/> puts into <see cref="IdentitySources.Players"/>.</summary>
    internal static readonly PlayersRefSource RefSource = new();

    /// <summary>The source <see cref="PlayersStartup"/> registers at <see cref="EntitySourceOrder.Players"/>.</summary>
    internal static readonly PlayersEntitySource EntitySource = new();

    // ---- the session stamp ----
    // A player's session entity is slot + 1, and the game gives it to the next connection in that slot, so a ref needs
    // a stamp that tells two sessions of one slot apart. The stamp is the players source's place in the entity seam's
    // order in bits 60..63 (Players = place 0, so 0x1) and a 60-bit session value in bits 0..59:
    //
    //   bits 60..63  0x1: IdentitySources.Stamp(EntitySourceOrder.Players, value) puts it there, so no other
    //                entity source's stamp ever equals a player's
    //   bit  59      always 1, so the value is never 0
    //   bits 34..58  Handle >> 6, the connection handle's generation (low 25 bits)
    //   bits  0..33  JoinedStep, the server-world step kiln.players first saw the session in game (low 34 bits: about
    //                18 years of steps at 30 per second)
    //
    // Handle 0 (a player known only from the game log, or a lab simulation; the game's records never use handle 0 for
    // a connection): the generation is 0 and the join step alone tells the sessions apart. A slot holds one session at
    // a time and each session's join step is the scan that first saw it in game, so each later session of the slot has
    // a larger one (a leave and the next join seen in one scan: the new session gets that scan's step, the old one's
    // is earlier).
    // JoinedStep 0 (the session was in game at a scan of step 0, before kiln.players counted steps): the generation
    // alone tells the sessions apart. With both 0 the value is bit 59 alone; only one session per slot can be like
    // that (every later join in the slot is seen at a later step), so it still differs from the slot's other sessions.
    // The same person's two sessions differ the same way: each has its own handle generation and join step.
    private const ulong ValueMark = 1UL << 59;
    private const int StepBits = 34, GenerationBits = 25;
    private const ulong StepMask = (1UL << StepBits) - 1, GenerationMask = (1UL << GenerationBits) - 1;

    /// <summary>The 60-bit session value (see above). Never 0; at most 2^60 - 1 (<c>IdentitySources.MaxStampValue</c>),
    /// what <see cref="IdentitySources.Stamp"/> accepts.</summary>
    internal static ulong SessionValue(Player p) =>
        ValueMark | (((p.Handle >> 6) & GenerationMask) << StepBits) | (p.JoinedStep & StepMask);

    /// <summary>The stamp of the player's session: the one function the entity source, the players source and the
    /// conversions use. Never 0.</summary>
    internal static ulong StampOf(Player p) => IdentitySources.Stamp(EntitySourceOrder.Players, SessionValue(p));

    /// <summary>The session entity of this snapshot with its stamp; <see cref="EntityRef.None"/> unless in game. Built
    /// directly, not with <see cref="EntityRef.Of"/>: the <see cref="Player"/> conversion is any-thread and gives the
    /// ref of the session the snapshot shows, also when that session is over (<see cref="EntityRef.Of"/> would need the
    /// tick thread and would capture whoever holds the slot now). For a player in game it is the same ref
    /// <see cref="EntityRef.Of"/> gives, since the entity source answers with <see cref="StampOf"/>.</summary>
    internal static EntityRef EntityOf(Player p) =>
        p.InGame && !p.Entity.IsNone ? new EntityRef(p.Entity, StampOf(p)) : EntityRef.None;

    /// <summary>The ref of this snapshot (the <see cref="Player"/> conversion).</summary>
    internal static PlayerRef RefOf(Player p) => new(p.SteamId, Roster.CharacterOf(p), EntityOf(p));

    /// <summary>The ref of a player in game now, its session entity captured through the entity seam
    /// (<see cref="EntityRef.Of"/>), as <see cref="PlayersRefSource"/> gives it. Tick thread.</summary>
    internal static PlayerRef LiveRefOf(Player p) => new(p.SteamId, Roster.CharacterOf(p), EntityRef.Of(p.Entity));

    // ---- reads that turn "the service is off" into the capability's exception ----

    /// <summary>The player in game with this entity, or null. Throws <see cref="KilnUnavailableException"/>
    /// (<paramref name="capability"/>) when kiln.players cannot answer.</summary>
    internal static Player? FindInGame(EntityId entity, string capability)
    {
        var d = Require(capability);
        if (!entity.IsPlayer) return null;  // checked after Require: "unavailable" wins over "no player"
        try
        {
            return d.Find(entity) is { InGame: true } p && p.Entity == entity ? p : null;
        }
        catch (NotSupportedException e) when (e is not KilnUnavailableException)
        {
            throw new KilnUnavailableException(capability, e.Message, e);
        }
    }

    /// <summary>Players in game. Throws <see cref="KilnUnavailableException"/> (<paramref name="capability"/>) when
    /// kiln.players cannot answer.</summary>
    internal static IReadOnlyList<Player> InGame(string capability)
    {
        var d = Require(capability);
        try
        {
            return d.InGame;
        }
        catch (NotSupportedException e) when (e is not KilnUnavailableException)
        {
            throw new KilnUnavailableException(capability, e.Message, e);
        }
    }

    private static IPlayerDirectory Require(string capability)
    {
        var d = Roster;
        return d.Available ? d : throw new KilnUnavailableException(capability, "kiln.players is unavailable");
    }

    /// <summary>Puts the players source into <see cref="IdentitySources.Players"/> and registers the players' entity
    /// source at <see cref="EntitySourceOrder.Players"/> (<see cref="PlayersStartup"/>, tests). Never sets
    /// <see cref="IdentitySources.Entities"/>.</summary>
    internal static void Install()
    {
        IdentitySources.Players = RefSource;
        IdentitySources.SetEntitySource(EntitySourceOrder.Players, EntitySource);
    }
}

/// <summary>identity.players: what <see cref="PlayerRef.Of"/> and <see cref="PlayerRef.Resolve"/> ask.</summary>
internal sealed class PlayersRefSource : IPlayerRefSource
{
    public PlayerRef FromEntity(EntityId player) =>
        PlayerIdentity.FindInGame(player, PlayerIdentity.PlayersCapability) is { } p ? PlayerIdentity.LiveRefOf(p) : PlayerRef.None;

    public PlayerRef Current(ulong steamId, Guid characterId)
    {
        var wanted = new PlayerRef(steamId, characterId, EntityRef.None);
        foreach (var p in PlayerIdentity.InGame(PlayerIdentity.PlayersCapability))
            if (new PlayerRef(p.SteamId, PlayerIdentity.Roster.CharacterOf(p), EntityRef.None).IsSamePerson(wanted))
                return PlayerIdentity.LiveRefOf(p);
        return wanted;
    }
}

/// <summary>The players' entity stamps: for a player's session entity, the stamp of the session now in that slot (the
/// same stamp <see cref="PlayerRef.Entity"/> carries), so <see cref="PlayerRef.IsOnline"/> turns false once the player
/// left or another connection took the slot. Registered at <see cref="EntitySourceOrder.Players"/> by
/// <see cref="PlayersStartup"/>, so it is asked first.</summary>
internal sealed class PlayersEntitySource : IEntityStampSource
{
    /// <summary><see cref="EntitySerialAnswer.NotMine"/> for an id that is not a player id
    /// (<see cref="EntityId.IsPlayer"/> false), decided without asking kiln.players; <see cref="EntitySerialAnswer.Alive"/>
    /// with the stamp of the session now in that slot when a player is in game there; else
    /// <see cref="EntitySerialAnswer.Absent"/>. Tick thread.</summary>
    /// <exception cref="KilnUnavailableException">kiln.players cannot answer (<c>identity.entities</c>); never
    /// <see cref="EntitySerialAnswer.Absent"/> or <see cref="EntitySerialAnswer.NotMine"/> for that.</exception>
    public EntitySerialAnswer Query(EntityId entity, out ulong stamp)
    {
        stamp = 0;
        if (!entity.IsPlayer) return EntitySerialAnswer.NotMine;
        if (PlayerIdentity.FindInGame(entity, PlayerIdentity.EntitiesCapability) is not { } p) return EntitySerialAnswer.Absent;
        stamp = PlayerIdentity.StampOf(p);
        return EntitySerialAnswer.Alive;
    }
}

/// <summary>Started at the first service tick (<c>ServiceTick.AutoStart</c>), so identity works from the first tick
/// even when no loaded mod references Kiln.Players. Sets <see cref="IdentitySources.Players"/> and registers the
/// players' entity source (never <see cref="IdentitySources.Entities"/>). Never throws: on a server or client without
/// kiln.players both are still set, and they answer with <see cref="KilnUnavailableException"/>
/// (<c>identity.players</c>, <c>identity.entities</c>).</summary>
internal static class PlayersStartup
{
    static PlayersStartup()
    {
        try
        {
            PlayerIdentity.Install();
            Backend.Current.Log(0, LogLevel.Info,
                $"kiln.players: identity.players and the players' entity source supplied (kiln.players {(SafeAvailable() ? "available" : "unavailable")})");
        }
        catch (Exception e)
        {
            Backend.Current.Log(0, LogLevel.Warn, $"kiln.players: identity startup: {e.Message}");
        }
    }

    private static bool SafeAvailable()
    {
        try
        {
            return Players.Available;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
