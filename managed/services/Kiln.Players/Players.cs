// SPDX-License-Identifier: MIT
// Managed face of kiln.players (native/core/abi/services/kiln_players.h). Usage: docs/kiln/services/players.md.
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

/// <summary>The rights of a player's user group (<c>enshrouded_server.json</c> "userGroups"), as the game prints them
/// after "logged in with Permissions:".</summary>
[Flags]
public enum PlayerPermissions : uint
{
    None = 0,
    KickBan = 0x01,
    AccessInventories = 0x02,
    EditBase = 0x04,
    EditWorld = 0x08,
    ExtendBase = 0x10,
    ReceiveExp = 0x20,
}

public enum PlayerState
{
    /// <summary>Connected and loading; no player entity yet.</summary>
    Joining = 1,
    /// <summary>Logged in; the player entity exists.</summary>
    InGame = 2,
    /// <summary>Being removed (disconnect, kick, shutdown).</summary>
    Leaving = 3,
}

/// <summary>Where kiln.players gets its data on this game build.</summary>
public enum PlayerSource
{
    /// <summary>Nothing works (the service is unavailable).</summary>
    None = 0,
    /// <summary>The game server's player records: every field.</summary>
    Records = 1,
    /// <summary>Only the game log (fallback): names, Steam ids, join/leave; no account id, user group or admin bit.</summary>
    GameLog = 2,
}

/// <summary>A player as kiln.players saw them at the last server-world step.</summary>
public sealed partial record Player
{
    /// <summary>The player's entity (1..16 on this build: slot + 1).</summary>
    public EntityId Entity { get; init; }
    /// <summary>The platform name (what the chat and the game log show).</summary>
    public string Name { get; init; } = "";
    /// <summary>SteamID64, from the game log; 0 when unknown.</summary>
    public ulong SteamId { get; init; }
    /// <summary>The game's own admin bit (it gates the admin client's actions). False when unknown; see
    /// <see cref="IsAdminKnown"/>. For server rights use <see cref="Permissions"/>.</summary>
    public bool IsAdmin { get; init; }
    /// <summary>Where the player stands (metres), as of the last step; null when unknown.</summary>
    public WorldPosition? Position { get; init; }

    /// <summary>The game's player slot (0..15).</summary>
    public int Slot { get; init; }
    public PlayerState State { get; init; }
    /// <summary>The user group's rights; <see cref="PlayerPermissions.None"/> when unknown (see
    /// <see cref="PermissionsKnown"/>).</summary>
    public PlayerPermissions Permissions { get; init; }
    public bool PermissionsKnown { get; init; }
    public bool IsAdminKnown { get; init; }
    /// <summary>The game's user-group id (raw); 0 when unknown.</summary>
    public uint UserGroupId { get; init; }
    /// <summary>The game's account id for the player; 0 when unknown.</summary>
    public ulong AccountId { get; init; }
    /// <summary>The game's connection handle (slot | generation &lt;&lt; 6); 0 when unknown.</summary>
    public uint Handle { get; init; }
    /// <summary>Server-world step when Kiln saw the join; 0 if they were in game before Kiln watched.</summary>
    public ulong JoinedStep { get; init; }

    public bool InGame => State == PlayerState.InGame;
    public bool Can(PlayerPermissions p) => (Permissions & p) == p;
    public override string ToString() => $"{Name} (entity {Entity}{(SteamId != 0 ? $", steam {SteamId}" : "")})";
}

/// <summary>A join or leave seen by kiln.players.</summary>
public sealed record PlayerEvent(ulong Sequence, bool Joined, ulong Step, Player Player);

/// <summary>What kiln.players can do on this build, and what it saw last.</summary>
public sealed record PlayersStatus(
    PlayerSource Source, bool Records, bool AdminBit, bool Positions, bool GameLog, bool SingletonMapAgrees,
    bool LayoutConsistent, int Slots, int InGame, int Connected, ulong LastSequence, ulong Scans, ulong LogLines,
    ulong LogLinesMatched, bool WorldCaptured, bool ServerPlayersCaptured, bool BasicPlayerInfosCaptured, bool Simulated);

/// <summary>One raw player record and its BasicPlayerInfos entry (diagnostics).</summary>
public readonly record struct PlayerSlotRaw(
    uint Handle, uint Peer, uint State, uint UserGroup, uint Permissions, uint Entity, uint NameLength, ulong AccountId,
    uint InfoEntity, uint InfoHandle, uint InfoAdminByte, uint InfoNonZeroBytes);

/// <summary>Who is on the server (kiln.players).</summary>
/// <remarks>
/// <para>Reads (<see cref="All"/>, <see cref="Find(string)"/>, ...) work from any thread: they return the snapshot the
/// service took at the start of the current (or last) server-world step. <see cref="PositionOf"/> reads live on the
/// tick thread.</para>
/// <para><see cref="Joined"/> and <see cref="Left"/> are raised on the tick thread, at the start of the step after the
/// change, inside your mod's tick (an exception counts as a fault of your mod). Subscribe from your mod's code on the
/// tick thread (e.g. <c>OnLoad</c>); handlers are dropped when your mod unloads. Players already in game when you
/// subscribe raise no <see cref="Joined"/>: read <see cref="All"/> first.</para>
/// </remarks>
[ServerOnly]
public static unsafe partial class Players
{
    // ---- native mirror (kiln_players.h) ----
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePlayer
    {
        public uint Size, Entity, Slot, State, Flags, Permissions, UserGroup, Handle;
        public byte IsAdmin;
        public fixed byte Reserved8[7];
        public ulong AccountId, SteamId;
        public double X, Y, Z;
        public ulong JoinedStep;
        public uint NameLen;
        public fixed byte Name[NameMax];
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeEvent
    {
        public ulong Seq;
        public uint Kind, Reserved;
        public ulong Step;
        public NativePlayer Player;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeStatus
    {
        public uint Size, Source;
        public byte Records, Admin, Positions, Log, MapOk, LayoutOk, Simulated, R1;
        public uint Slots, InGame, Connected, Reserved;
        public ulong LastSeq, Scans, LogLines, LogMatched;
        public nuint World, ServerPlayers, BasicInfos;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeSlotRaw
    {
        public uint Size, Handle, Peer, State, UserGroup, Permissions, Entity, NameLen;
        public ulong AccountId;
        public uint InfoEntity, InfoHandle, InfoAdmin, InfoNonzero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vec3
    {
        public double X, Y, Z;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<NativeStatus*, int> Status;
        public delegate* unmanaged[Cdecl]<uint, NativePlayer*, uint, uint*, int> List;
        public delegate* unmanaged[Cdecl]<uint, NativePlayer*, int> Get;
        public delegate* unmanaged[Cdecl]<NativeStr, NativePlayer*, int> FindName;
        public delegate* unmanaged[Cdecl]<ulong, NativePlayer*, int> FindSteamId;
        public delegate* unmanaged[Cdecl]<uint, Vec3*, int> Position;
        public delegate* unmanaged[Cdecl]<ulong, NativeEvent*, uint, uint*, int> Events;
        public delegate* unmanaged[Cdecl]<ulong> LastSeq;
        public delegate* unmanaged[Cdecl]<ulong, nint, nint, ulong*, int> Subscribe;
        public delegate* unmanaged[Cdecl]<ulong, int> Unsubscribe;
        public delegate* unmanaged[Cdecl]<uint, NativeSlotRaw*, int> RawSlot;
        public delegate* unmanaged[Cdecl]<NativeStr*, uint, NativePlayer*, uint, uint*, NativeEvent*, uint, uint*, int> ReplayLog;
        public delegate* unmanaged[Cdecl]<uint, Vec3*, int> ProbePosition;
    }

    internal const int NameMax = 128;
    private const int Ok = 0, NotFound = 2, Unavailable = 3, BufferTooSmall = 5;
    private const uint HasPosition = 0x01, HasSteamId = 0x02, HasAdmin = 0x04, HasPermissions = 0x08;

    private static Table* _table;
    private static bool _looked;

    private static Table* T
    {
        get
        {
            if (_table != null || _looked) return _table;
            _looked = true;
            if (sizeof(NativePlayer) != 224 || sizeof(NativeEvent) != 248 || sizeof(NativeStatus) != 88 ||
                sizeof(NativeSlotRaw) != 56)
                return null; // layout drift between this assembly and the header: refuse rather than misread
            _table = Services.Get<Table>("kiln.players", 1);
            return _table;
        }
    }

    // a KilnUnavailableException is a NotSupportedException: a catch written before dev.27 still catches it
    private static Table* Require(string api) =>
        T != null ? T : throw PlayersCaps.Off(PlayersCaps.Service, $"Players.{api}: kiln.players is unavailable on this server");

    /// <summary>The table is here (the rows; no lookup after the first).</summary>
    internal static bool TableHere => T != null;

    /// <summary>Whether kiln.players reads positions now, and the server world it reads (0: none).</summary>
    internal static (bool Positions, nuint World) PositionsNow()
    {
        var s = ReadStatus();
        return (s.Positions != 0, s.World);
    }

    /// <summary>True when kiln.players is loaded and works on this game build (capability <c>kiln.players</c>).</summary>
    [AnyThread]
    public static bool Available => T != null && Source != PlayerSource.None;

    /// <summary>Where the data comes from on this build.</summary>
    [AnyThread]
    public static PlayerSource Source => T == null ? PlayerSource.None : (PlayerSource)ReadStatus().Source;

    /// <summary>Players in game, in slot order.</summary>
    [AnyThread]
    [RequiresCapability(PlayersCaps.Service)]
    public static IReadOnlyList<Player> All => List(0, nameof(All));

    /// <summary>Every connection: joining, in game and leaving.</summary>
    [AnyThread]
    [RequiresCapability(PlayersCaps.Service)]
    public static IReadOnlyList<Player> Connected => List(1, nameof(Connected));

    /// <summary>How many players are in game.</summary>
    [AnyThread]
    [RequiresCapability(PlayersCaps.Service)]
    public static int Count
    {
        get
        {
            Require(nameof(Count));
            return (int)ReadStatus().InGame;
        }
    }

    /// <summary>The player with this entity id (any state), or null. A player slot's id goes to the next player who
    /// joins: to find a person you kept from an earlier step, use <see cref="FindPerson"/>.</summary>
    [AnyThread]
    [RequiresCapability(PlayersCaps.Service)]
    public static Player? Find(EntityId entity)
    {
        var t = Require(nameof(Find));
        NativePlayer p;
        return Check(t->Get(entity.Value, &p), nameof(Find)) ? Convert(&p) : null;
    }

    /// <summary>The player with this name, or null. Exact match first, then case-insensitive (ASCII); an in-game
    /// player wins over a joining or leaving one.</summary>
    [AnyThread]
    [RequiresCapability(PlayersCaps.Service)]
    public static Player? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var t = Require(nameof(Find));
        if (name.Length == 0) return null;
        var bytes = Encoding.UTF8.GetBytes(name);
        NativePlayer p;
        fixed (byte* b = bytes)
        {
            var s = new NativeStr { Ptr = b, Len = (nuint)bytes.Length };
            return Check(t->FindName(s, &p), nameof(Find)) ? Convert(&p) : null;
        }
    }

    /// <summary>The player with this SteamID64, or null.</summary>
    [AnyThread]
    [RequiresCapability(PlayersCaps.Service)]
    public static Player? FindBySteamId(ulong steamId)
    {
        var t = Require(nameof(FindBySteamId));
        NativePlayer p;
        return steamId != 0 && Check(t->FindSteamId(steamId, &p), nameof(FindBySteamId)) ? Convert(&p) : null;
    }

    /// <summary>The person <paramref name="person"/> as a player in game now (their current session, whatever slot it
    /// has), or null while they are offline. Unlike <see cref="Find(EntityId)"/> with an id kept from an earlier step,
    /// this never answers with another player who took the slot since. Tick thread.</summary>
    /// <exception cref="KilnUnavailableException"><c>kiln.players</c> is off, or no player source is registered
    /// (<c>identity.players</c>).</exception>
    [TickThreadOnly]
    [RequiresCapability(PlayersCaps.Service)]
    [RequiresCapability("identity.players")]
    public static Player? FindPerson(PlayerRef person)
    {
        if (person.SteamId == 0 && person.CharacterId == Guid.Empty) return null;
        var now = person.Resolve();
        return now.Entity.IsNone ? null : Find(now.Entity) is { InGame: true } p ? p : null;
    }

    /// <summary>True if the entity is a player in game.</summary>
    [AnyThread]
    [RequiresCapability(PlayersCaps.Service)]
    public static bool IsPlayer(EntityId entity) => Find(entity) is { InGame: true };

    /// <summary>The player's position now (tick thread) or at the last step (other threads); null if the entity is
    /// not a player in game or has no transform. Throws <see cref="KilnUnavailableException"/> (a
    /// <see cref="NotSupportedException"/>) naming <c>players.positions</c> when positions do not work now (no server
    /// world yet, or this build).</summary>
    [AnyThread]
    [RequiresCapability(PlayersCaps.Service)]
    [RequiresCapability(PlayersCaps.Positions)]
    public static WorldPosition? PositionOf(EntityId entity)
    {
        var t = Require(nameof(PositionOf));
        Vec3 v;
        var rc = t->Position(entity.Value, &v);
        if (rc == Unavailable) throw PlayersCaps.Off(PlayersCaps.Positions, "Players.PositionOf: positions are unavailable on this build");
        return rc == Ok ? new WorldPosition(v.X, v.Y, v.Z) : null;
    }

    // ---- events ----
    private sealed class ModState(ModContext context)
    {
        public readonly ModContext Context = context;
        public Action<Player>? Joined, Left;
        public ulong Cursor;
        public TickHandler? Pump;
    }

    private static readonly Dictionary<ModContext, ModState> _mods = new();
    private static readonly object _lock = new();

    /// <summary>A player logged in (their entity exists). Raised on the tick thread in your mod's tick.</summary>
    [RequiresCapability(PlayersCaps.Service)]
    public static event Action<Player> Joined
    {
        add => Attach(nameof(Joined)).Joined += value;
        remove { if (Existing() is { } s) s.Joined -= value; }
    }

    /// <summary>A player left (disconnect, kick, shutdown). <see cref="Player"/> is as they were last seen in game.</summary>
    [RequiresCapability(PlayersCaps.Service)]
    public static event Action<Player> Left
    {
        add => Attach(nameof(Left)).Left += value;
        remove { if (Existing() is { } s) s.Left -= value; }
    }

    private static ModState? Existing()
    {
        var ctx = ModContext.CurrentMod;
        if (ctx == null) return null;
        lock (_lock) return _mods.GetValueOrDefault(ctx);
    }

    private static ModState Attach(string api)
    {
        var t = Require(api);
        var ctx = ModContext.CurrentMod ?? throw new InvalidOperationException(
            $"Players.{api}: subscribe from your mod's code on the tick thread (OnLoad, a tick handler, Scheduler.Post)");
        lock (_lock)
        {
            if (_mods.TryGetValue(ctx, out var existing)) return existing;
            var s = new ModState(ctx) { Cursor = t->LastSeq() };
            s.Pump = (in TickInfo _) => Pump(s);
            ctx.Tick.Pre += s.Pump;
            ctx.Retiring += () =>
            {
                lock (_lock) _mods.Remove(ctx);
                s.Joined = s.Left = null;
                s.Pump = null;
            };
            _mods[ctx] = s;
            return s;
        }
    }

    private static void Pump(ModState s)
    {
        var t = T;
        if (t == null || (s.Joined == null && s.Left == null)) return;
        if (t->LastSeq() <= s.Cursor) return;
        var buf = stackalloc NativeEvent[8];
        for (var round = 0; round < 8; round++)
        {
            uint n = 0;
            if (t->Events(s.Cursor, buf, 8, &n) != Ok || n == 0) return;
            for (var i = 0; i < n; i++)
            {
                var e = &buf[i];
                if (e->Seq <= s.Cursor) continue;
                s.Cursor = e->Seq;
                var p = Convert(&e->Player);
                if (e->Kind == 1) s.Joined?.Invoke(p);
                else if (e->Kind == 2) s.Left?.Invoke(p);
            }
            if (n < 8) return;
        }
    }

    // ---- for the event bus (PlayerEvents.cs) ----
    internal static ulong LastSequence => T != null ? T->LastSeq() : 0;

    /// <summary>Up to <paramref name="max"/> events after <paramref name="cursor"/>, converted.</summary>
    internal static List<PlayerEvent> EventsAfter(ulong cursor, int max)
    {
        var list = new List<PlayerEvent>();
        var t = T;
        if (t == null) return list;
        var buf = stackalloc NativeEvent[8];
        while (list.Count < max)
        {
            uint n = 0;
            if (t->Events(cursor, buf, 8, &n) != Ok || n == 0) break;
            for (var i = 0; i < n; i++)
            {
                var e = &buf[i];
                if (e->Seq <= cursor) continue;
                cursor = e->Seq;
                if (e->Kind is 1 or 2) list.Add(new PlayerEvent(e->Seq, e->Kind == 1, e->Step, Convert(&e->Player)));
            }
            if (n < 8) break;
        }
        return list;
    }

    // ---- diagnostics ----
    /// <summary>Diagnostics and testing: status, raw records, the log tracker.</summary>
    public static class Diagnostics
    {
        /// <summary>What the service can do and saw last; null when kiln.players is not loaded.</summary>
        public static PlayersStatus? Status
        {
            get
            {
                if (T == null) return null;
                var s = ReadStatus();
                return new PlayersStatus((PlayerSource)s.Source, s.Records != 0, s.Admin != 0, s.Positions != 0,
                    s.Log != 0, s.MapOk != 0, s.LayoutOk != 0, (int)s.Slots, (int)s.InGame, (int)s.Connected,
                    s.LastSeq, s.Scans, s.LogLines, s.LogMatched, s.World != 0, s.ServerPlayers != 0, s.BasicInfos != 0,
                    s.Simulated != 0);
            }
        }

        /// <summary>One player record as the game holds it. Tick thread. Null when the records are unavailable or the
        /// slot is out of range.</summary>
        [TickThreadOnly]
        public static PlayerSlotRaw? RawSlot(int slot)
        {
            var t = Require(nameof(RawSlot));
            Services.RequireTickThread("Players.Diagnostics.RawSlot");
            if (slot < 0) return null;
            NativeSlotRaw r = default;
            r.Size = (uint)sizeof(NativeSlotRaw);
            if (t->RawSlot((uint)slot, &r) != Ok) return null;
            return new PlayerSlotRaw(r.Handle, r.Peer, r.State, r.UserGroup, r.Permissions, r.Entity, r.NameLen,
                r.AccountId, r.InfoEntity, r.InfoHandle, r.InfoAdmin, r.InfoNonzero);
        }

        /// <summary>The position path used for players (GetEntityTransform on the captured server world), for any
        /// entity: checks it on a server with no players. Tick thread; null if there is no such entity.</summary>
        [TickThreadOnly]
        public static WorldPosition? ProbePosition(EntityId entity)
        {
            var t = Require(nameof(ProbePosition));
            Services.RequireTickThread("Players.Diagnostics.ProbePosition");
            Vec3 v;
            return t->ProbePosition(entity.Value, &v) == Ok ? new WorldPosition(v.X, v.Y, v.Z) : null;
        }

        /// <summary>Runs a fresh copy of the service's game-log tracker over <paramref name="messages"/> (one game log
        /// message each; "\n" inside one is fine; the log file's "[I hh:mm:ss,mmm] " prefix is accepted) and returns
        /// the players it ends with (any state) and the events it raised. Touches no game state; any thread.</summary>
        public static (IReadOnlyList<Player> Players, IReadOnlyList<PlayerEvent> Events) ReplayLog(IReadOnlyList<string> messages)
        {
            ArgumentNullException.ThrowIfNull(messages);
            var t = Require(nameof(ReplayLog));
            var handles = new GCHandle[messages.Count];
            var strs = new NativeStr[messages.Count];
            try
            {
                for (var i = 0; i < messages.Count; i++)
                {
                    var b = Encoding.UTF8.GetBytes(messages[i] ?? "");
                    handles[i] = GCHandle.Alloc(b, GCHandleType.Pinned);
                    strs[i] = new NativeStr { Ptr = (byte*)handles[i].AddrOfPinnedObject(), Len = (nuint)b.Length };
                }
                var players = new NativePlayer[64];
                var events = new NativeEvent[256];
                uint np = 0, ne = 0;
                fixed (NativeStr* s = strs)
                fixed (NativePlayer* p = players)
                fixed (NativeEvent* e = events)
                {
                    var rc = t->ReplayLog(s, (uint)strs.Length, p, (uint)players.Length, &np, e, (uint)events.Length, &ne);
                    if (rc != Ok && rc != BufferTooSmall) throw new InvalidOperationException($"replay_log failed ({rc})");
                    var outPlayers = new List<Player>();
                    for (var i = 0; i < Math.Min(np, (uint)players.Length); i++) outPlayers.Add(Convert(&p[i]));
                    var outEvents = new List<PlayerEvent>();
                    for (var i = 0; i < Math.Min(ne, (uint)events.Length); i++)
                        outEvents.Add(new PlayerEvent(e[i].Seq, e[i].Kind == 1, e[i].Step, Convert(&e[i].Player)));
                    return (outPlayers, outEvents);
                }
            }
            finally
            {
                foreach (var h in handles)
                    if (h.IsAllocated) h.Free();
            }
        }
    }

    // ---- helpers ----
    private static NativeStatus ReadStatus()
    {
        NativeStatus s = default;
        s.Size = (uint)sizeof(NativeStatus);
        return T != null && T->Status(&s) == Ok ? s : default;
    }

    private static bool Check(int rc, string api) => rc switch
    {
        Ok => true,
        NotFound => false,
        // the table is here and answers "unavailable" (no source on this build, or the test kit switched it off): a plain
        // NotSupportedException still, since the test kit's tests check that exact type (Kiln.TestKit.Tests PlayersTests)
        Unavailable => throw new NotSupportedException($"Players.{api}: kiln.players is unavailable on this build"),
        _ => throw new InvalidOperationException($"Players.{api}: kiln.players returned {rc}"),
    };

    private static IReadOnlyList<Player> List(uint flags, string api)
    {
        var t = Require(api);
        var cap = 16u;
        while (true)
        {
            var buf = new NativePlayer[cap];
            uint n = 0;
            int rc;
            fixed (NativePlayer* p = buf) rc = t->List(flags, p, cap, &n);
            if (rc == BufferTooSmall && cap < 1024)
            {
                cap = Math.Max(cap * 2, n);
                continue;
            }
            Check(rc == BufferTooSmall ? Ok : rc, api);
            var list = new List<Player>((int)Math.Min(n, cap));
            fixed (NativePlayer* p = buf)
                for (var i = 0; i < Math.Min(n, cap); i++) list.Add(Convert(&p[i]));
            return list;
        }
    }

    internal static Player Convert(NativePlayer* p) => new()
    {
        Entity = new EntityId(p->Entity),
        Name = Encoding.UTF8.GetString(p->Name, (int)Math.Min(p->NameLen, NameMax - 1)),
        SteamId = (p->Flags & HasSteamId) != 0 ? p->SteamId : 0,
        IsAdmin = (p->Flags & HasAdmin) != 0 && p->IsAdmin != 0,
        IsAdminKnown = (p->Flags & HasAdmin) != 0,
        Position = (p->Flags & HasPosition) != 0 ? new WorldPosition(p->X, p->Y, p->Z) : null,
        Slot = (int)p->Slot,
        State = (PlayerState)p->State,
        Permissions = (p->Flags & HasPermissions) != 0 ? (PlayerPermissions)p->Permissions : PlayerPermissions.None,
        PermissionsKnown = (p->Flags & HasPermissions) != 0,
        UserGroupId = p->UserGroup,
        AccountId = p->AccountId,
        Handle = p->Handle,
        JoinedStep = p->JoinedStep,
    };
}
