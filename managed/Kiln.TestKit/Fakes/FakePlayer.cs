// SPDX-License-Identifier: MIT
using System.Text;

namespace Kiln.TestKit;

/// <summary>
/// The players of a harness's server (<see cref="KilnHarness.Players"/>). They are what the kit's fake of the native
/// <c>kiln.players</c> service reports, so Kiln.Players' own code (<see cref="global::Kiln.Players.All"/>,
/// <see cref="global::Kiln.Players.Find(string)"/>, <see cref="global::Kiln.Players.Joined"/>,
/// <see cref="PlayerJoinedEvent"/>, ...) and everything built on it (permissions, command selectors,
/// <see cref="PlayerData"/> through <see cref="EntityData"/>) runs unchanged.
/// </summary>
/// <remarks>
/// <para>A <see cref="Join"/> or <see cref="FakePlayer.Leave"/> takes effect at the start of the next step, as the native
/// service sees the game's player records then: the player is in <see cref="global::Kiln.Players.All"/> from that step, and
/// the join or leave reaches mods in it. What the fake does not have, it reports as unavailable: raw player records
/// (<see cref="global::Kiln.Players.Diagnostics.RawSlot"/>), the game-log replay, and the position probe for entities
/// that are not players. <see cref="PlayersStatus.Simulated"/> is true.</para>
/// <para>The game saves the world when a player leaves (reason <see cref="SaveReason.PlayerLeft"/>); the harness's world
/// does too, over the steps after the leave.</para>
/// </remarks>
public sealed class HarnessPlayers
{
    private readonly KilnHarness _harness;

    internal HarnessPlayers(KilnHarness harness) => _harness = harness;

    /// <summary>Player slots of the server (16, as a dedicated server of this game has).</summary>
    public int Slots => PlayersFake.Slots;

    /// <summary>
    /// A player connects and logs in: in game, with an entity (slot + 1), at the start of the next step.
    /// </summary>
    /// <param name="name">The platform name (what the chat and the game log show).</param>
    /// <param name="steamId">SteamID64; 0 (default) gives each player a distinct made-up one.</param>
    /// <param name="rights">The rights of the player's user group (<c>enshrouded_server.json</c> "userGroups"):
    /// <see cref="PlayerPermissions.KickBan"/> makes the player an admin for Kiln's commands (unless permissions.json says
    /// otherwise). Default none: a plain player.</param>
    /// <param name="isAdmin">The game's own admin bit (<see cref="Player.IsAdmin"/>).</param>
    /// <param name="position">Where the player stands; null: unknown (<see cref="Player.Position"/> is null).</param>
    /// <param name="userGroupId">The game's raw user-group id (<see cref="Player.UserGroupId"/>); 0: unknown.</param>
    /// <exception cref="InvalidOperationException">All slots are taken, or the Steam id is on the server already.</exception>
    public FakePlayer Join(string name, ulong steamId = 0, PlayerPermissions rights = PlayerPermissions.None, bool isAdmin = false,
        WorldPosition? position = null, uint userGroupId = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (Encoding.UTF8.GetByteCount(name) >= PlayersAbi.NameMax) throw new ArgumentException($"a player name has at most {PlayersAbi.NameMax - 1} bytes of UTF-8", nameof(name));
        return _harness.PlayersFakeOrThrow.Join(name, steamId, rights, isAdmin, position, userGroupId);
    }

    /// <summary>The players the test joined that have not left (in game, or joining until the next step).</summary>
    public IReadOnlyList<FakePlayer> All => _harness.PlayersFakeOrThrow.Present;
}

/// <summary>A player the test joined (<see cref="HarnessPlayers.Join"/>).</summary>
public sealed class FakePlayer
{
    internal enum Stage
    {
        Joining,
        InGame,
        Leaving,
        Left,
    }

    private readonly PlayersFake _owner;
    private WorldPosition? _position;

    internal FakePlayer(PlayersFake owner, string name, ulong steamId, int slot, uint handle, PlayerPermissions rights, bool isAdmin,
        uint userGroupId, WorldPosition? position)
    {
        _owner = owner;
        Name = name;
        SteamId = steamId;
        Slot = slot;
        Handle = handle;
        Rights = rights;
        IsAdmin = isAdmin;
        UserGroupId = userGroupId;
        _position = position;
    }

    /// <summary>The platform name.</summary>
    public string Name { get; }

    /// <summary>SteamID64 (the one given to <see cref="HarnessPlayers.Join"/>, or a made-up one).</summary>
    public ulong SteamId { get; }

    /// <summary>The game's player slot (0..15), the lowest one free at the join.</summary>
    public int Slot { get; }

    /// <summary>The player's entity: slot + 1, as on this game build.</summary>
    public EntityId Entity => new((uint)Slot + 1);

    /// <summary>The user group's rights.</summary>
    public PlayerPermissions Rights { get; }

    /// <summary>The game's admin bit.</summary>
    public bool IsAdmin { get; }

    /// <summary>The game's raw user-group id (0: unknown).</summary>
    public uint UserGroupId { get; }

    internal uint Handle { get; }

    internal Stage Phase { get; set; } = Stage.Joining;

    internal ulong JoinedStep { get; set; }

    internal WorldPosition? SeenPosition { get; set; }

    internal WorldPosition? LivePosition
    {
        get
        {
            lock (_owner.Lock) return _position;
        }
    }

    /// <summary>Where the player stands. A change is what <see cref="global::Kiln.Players.PositionOf"/> reads on the tick
    /// thread at once; <see cref="global::Kiln.Players.All"/> and reads from other threads see it from the next step (the
    /// service's snapshot). Null: unknown.</summary>
    public WorldPosition? Position
    {
        get => LivePosition;
        set
        {
            lock (_owner.Lock) _position = value;
        }
    }

    /// <summary>In game: from the step after the join until the step after the leave.</summary>
    public bool InGame
    {
        get
        {
            lock (_owner.Lock) return Phase is Stage.InGame or Stage.Leaving;
        }
    }

    /// <summary>The player disconnects. At the start of the next step they are gone from
    /// <see cref="global::Kiln.Players.All"/> and the leave reaches mods (<see cref="global::Kiln.Players.Left"/>,
    /// <see cref="PlayerLeftEvent"/>); a player who leaves before that step was never in game and raises nothing. Returns
    /// false when the player has left (or is leaving) already: a second leave changes nothing.</summary>
    public bool Leave() => _owner.Leave(this);

    /// <summary>What this player's chat showed, in order: server lines mods sent them (<see cref="global::Kiln.Chat.Tell(EntityId, string)"/>,
    /// <see cref="global::Kiln.Chat.Broadcast"/>, command replies) and the lines players typed that nobody consumed
    /// (<see cref="KilnHarness.Chat"/> has the details).</summary>
    public IReadOnlyList<FakeChatLine> Chat => _owner.Harness.ChatFake?.ShownTo(this) ?? [];

    /// <summary>The player types <paramref name="text"/> in chat (a command line starts with <see cref="Commands.Prefix"/>).
    /// It reaches Kiln.Chat at the start of the next step: <see cref="global::Kiln.Chat.Received"/> handlers,
    /// <see cref="ChatMessageEvent"/> and, for a command line, the command as this player; a line nobody consumed is shown
    /// to every player. A player who is not in game by then is nobody the chat knows: the line reaches the handlers with
    /// no player and is shown to no one.</summary>
    /// <exception cref="ArgumentException">An empty text.</exception>
    public void Say(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        var chat = _owner.Harness.ChatFake ?? throw new InvalidOperationException("the harness has no chat yet");
        chat.Queue(Handle, text);
    }

    internal unsafe PlayersAbi.Player Native(uint state)
    {
        var p = new PlayersAbi.Player
        {
            Size = (uint)sizeof(PlayersAbi.Player),
            Entity = (uint)Slot + 1,
            Slot = (uint)Slot,
            State = state,
            Flags = PlayersAbi.HasSteamId | PlayersAbi.HasAdmin | PlayersAbi.HasPermissions,
            Permissions = (uint)Rights,
            UserGroup = UserGroupId,
            Handle = Handle,
            IsAdmin = IsAdmin ? (byte)1 : (byte)0,
            SteamId = SteamId,
            JoinedStep = JoinedStep,
        };
        if (SeenPosition is { } v)
        {
            p.Flags |= PlayersAbi.HasPosition;
            p.X = v.X;
            p.Y = v.Y;
            p.Z = v.Z;
        }
        var bytes = Encoding.UTF8.GetBytes(Name);
        var n = Math.Min(bytes.Length, PlayersAbi.NameMax - 1);
        for (var i = 0; i < n; i++) p.Name[i] = bytes[i];
        p.NameLen = (uint)n;
        return p;
    }

    /// <summary>The name, the session entity and the Steam id, for a log line.</summary>
    public override string ToString() => $"{Name} (entity {Entity}, steam {SteamId})";
}
