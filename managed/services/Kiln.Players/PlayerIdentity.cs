// SPDX-License-Identifier: MIT
// Kiln.Players supplies identity.players: PlayerRef.Of and PlayerRef.Resolve (Kiln.Api/Identity.cs) read players from
// here, through IdentitySources.Players, set by PlayersStartup at the first service tick (ServiceTick.AutoStart).
// It also holds the players' entity-stamp source, which is not registered yet (see PlayersEntitySource).
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

    // ---- the session stamp ----
    // A player's session entity is slot + 1, and the game gives it to the next connection in that slot, so a ref needs
    // a stamp that tells two sessions of one slot apart. The stamp is the players source's place in the entity seam's
    // order in bits 60..63 (Players = place 0, so 0x1) and a 60-bit session value in bits 0..59:
    //
    //   bits 60..63  0x1: what IdentitySources.Stamp(EntitySourceOrder.Players, value) puts there, so no other
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

    /// <summary>The 60-bit session value (see above). Never 0; below 2^60.</summary>
    internal static ulong SessionValue(Player p) =>
        ValueMark | (((p.Handle >> 6) & GenerationMask) << StepBits) | (p.JoinedStep & StepMask);

    /// <summary>The stamp of the player's session: the one function the entity source, the players source and the
    /// conversions use. Never 0.</summary>
    // Becomes `IdentitySources.Stamp(EntitySourceOrder.Players, SessionValue(p))` once the entity seam is merged.
    internal static ulong StampOf(Player p) => (1UL << 60) | SessionValue(p);

    /// <summary>The player's session entity with its stamp; <see cref="EntityRef.None"/> unless in game.</summary>
    // Becomes `EntityRef.Of(p.Entity)` once the players' entity source is registered (it returns this same stamp);
    // until then EntityRef.Of throws in a game, so the ref is built here.
    internal static EntityRef EntityOf(Player p) =>
        p.InGame && !p.Entity.IsNone ? new EntityRef(p.Entity, StampOf(p)) : EntityRef.None;

    /// <summary>The ref for this snapshot: the one place a <see cref="PlayerRef"/> is built from a <see cref="Player"/>.</summary>
    internal static PlayerRef RefOf(Player p) => new(p.SteamId, Roster.CharacterOf(p), EntityOf(p));

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

    /// <summary>Puts the players source into the identity seam (<see cref="PlayersStartup"/>, tests).</summary>
    internal static void Install() => IdentitySources.Players = RefSource;
}

/// <summary>identity.players: what <see cref="PlayerRef.Of"/> and <see cref="PlayerRef.Resolve"/> ask.</summary>
internal sealed class PlayersRefSource : IPlayerRefSource
{
    public PlayerRef FromEntity(EntityId player) =>
        PlayerIdentity.FindInGame(player, PlayerIdentity.PlayersCapability) is { } p ? PlayerIdentity.RefOf(p) : PlayerRef.None;

    public PlayerRef Current(ulong steamId, Guid characterId)
    {
        var wanted = new PlayerRef(steamId, characterId, EntityRef.None);
        foreach (var p in PlayerIdentity.InGame(PlayerIdentity.PlayersCapability))
        {
            var r = PlayerIdentity.RefOf(p);
            if (r.IsSamePerson(wanted)) return r;
        }
        return wanted;
    }
}

/// <summary>What the players' entity source says about an entity. The same names and values as the entity seam's
/// <c>EntitySerialAnswer</c> (Kiln.Api, once merged); this enum goes away then.</summary>
internal enum PlayersEntityAnswer
{
    /// <summary>Not a player id: the next source is asked.</summary>
    NotMine = 0,
    /// <summary>A player id with nobody in game in it now (empty slot, joining, leaving).</summary>
    Absent = 1,
    /// <summary>A player in game: the stamp is their session's.</summary>
    Alive = 2,
}

/// <summary>The players' entity stamps: for a player's session entity, the stamp of the session now in that slot (the
/// same stamp <see cref="PlayerRef.Entity"/> carries), so <see cref="PlayerRef.IsOnline"/> turns false once the player
/// left or another connection took the slot.</summary>
/// <remarks>Not registered: <see cref="IdentitySources.Entities"/> is never set from the service (the entity seam that
/// orders the sources is not merged yet). Once it is, this class implements its <c>IEntityStampSource</c>
/// (<see cref="Query"/> already has that shape) and <see cref="PlayersStartup"/> registers it.</remarks>
internal sealed class PlayersEntitySource
{
    /// <summary><see cref="PlayersEntityAnswer.NotMine"/> for an id that is not a player id
    /// (<see cref="EntityId.IsPlayer"/> false); <see cref="PlayersEntityAnswer.Alive"/> with the stamp of the session
    /// now in that slot when a player is in game there; else <see cref="PlayersEntityAnswer.Absent"/>. Tick thread.</summary>
    /// <exception cref="KilnUnavailableException">kiln.players cannot answer (<c>identity.entities</c>); never
    /// <see cref="PlayersEntityAnswer.Absent"/> or <see cref="PlayersEntityAnswer.NotMine"/> for that.</exception>
    public PlayersEntityAnswer Query(EntityId entity, out ulong stamp)
    {
        stamp = 0;
        if (!entity.IsPlayer) return PlayersEntityAnswer.NotMine;
        if (PlayerIdentity.FindInGame(entity, PlayerIdentity.EntitiesCapability) is not { } p) return PlayersEntityAnswer.Absent;
        stamp = PlayerIdentity.StampOf(p);
        return PlayersEntityAnswer.Alive;
    }
}

/// <summary>Today's single-source entity seam over <see cref="PlayersEntitySource"/>: Alive gives true with the stamp,
/// Absent and NotMine give false. For unit tests and the players-test lab mod only, which put it into
/// <see cref="IdentitySources.Entities"/> themselves; the service never does. Goes away with the new seam.</summary>
internal sealed class PlayersEntitySerialAdapter : IEntitySerialSource
{
    private readonly PlayersEntitySource _source = new();

    public bool TryGetSerial(EntityId entity, out ulong serial) =>
        _source.Query(entity, out serial) == PlayersEntityAnswer.Alive;
}

/// <summary>Started at the first service tick (<c>ServiceTick.AutoStart</c>), so identity works from the first tick
/// even when no loaded mod references Kiln.Players. Never throws: on a server or client without kiln.players the source
/// is still set, and it answers with <see cref="KilnUnavailableException"/> (<c>identity.players</c>).</summary>
internal static class PlayersStartup
{
    static PlayersStartup()
    {
        try
        {
            PlayerIdentity.Install();
            // IdentitySources.SetEntitySource(EntitySourceOrder.Players, new PlayersEntitySource());  // once the entity seam is merged
            Backend.Current.Log(0, LogLevel.Info,
                $"kiln.players: identity.players supplied (kiln.players {(SafeAvailable() ? "available" : "unavailable")})");
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
