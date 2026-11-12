// SPDX-License-Identifier: MIT
// The fake kiln.players (native/core/abi/services/kiln_players.h, v1) under the real Kiln.Players.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln.TestKit;

/// <summary>Mirror of <c>kiln_players.h</c> v1. The sizes are pinned by tests to the numbers <c>Players.cs</c> checks and
/// to the service's own private mirrors.</summary>
internal static unsafe class PlayersAbi
{
    public const uint Version = 1;
    public const int NameMax = 128;
    public const uint MaxSlots = 64;
    public const uint StateJoining = 1, StateInGame = 2, StateLeaving = 3;
    public const uint HasPosition = 0x01, HasSteamId = 0x02, HasAdmin = 0x04, HasPermissions = 0x08, FromRecords = 0x10, FromLog = 0x20;
    public const uint EventJoined = 1, EventLeft = 2;
    public const uint SourceNone = 0, SourceRecords = 1, SourceLog = 2;
    public const uint ListInGame = 0, ListAll = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct Player
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
    public struct Event
    {
        public ulong Seq;
        public uint Kind, Reserved;
        public ulong Step;
        public Player Player;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size, Source;
        public byte Records, Admin, Positions, Log, MapOk, LayoutOk, Simulated, Reserved8;
        public uint Slots, InGame, Connected, Reserved;
        public ulong LastSeq, Scans, LogLines, LogMatched;
        public nuint World, ServerPlayers, BasicInfos;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SlotRaw
    {
        public uint Size, Handle, Peer, State, UserGroup, Permissions, Entity, NameLen;
        public ulong AccountId;
        public uint InfoEntity, InfoHandle, InfoAdmin, InfoNonzero;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vec3
    {
        public double X, Y, Z;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<Status*, int> Status;
        public delegate* unmanaged[Cdecl]<uint, Player*, uint, uint*, int> List;
        public delegate* unmanaged[Cdecl]<uint, Player*, int> Get;
        public delegate* unmanaged[Cdecl]<NativeStr, Player*, int> FindName;
        public delegate* unmanaged[Cdecl]<ulong, Player*, int> FindSteamId;
        public delegate* unmanaged[Cdecl]<uint, Vec3*, int> Position;
        public delegate* unmanaged[Cdecl]<ulong, Event*, uint, uint*, int> Events;
        public delegate* unmanaged[Cdecl]<ulong> LastSeq;
        public delegate* unmanaged[Cdecl]<ulong, nint, nint, ulong*, int> Subscribe;
        public delegate* unmanaged[Cdecl]<ulong, int> Unsubscribe;
        public delegate* unmanaged[Cdecl]<uint, SlotRaw*, int> RawSlot;
        public delegate* unmanaged[Cdecl]<NativeStr*, uint, Player*, uint, uint*, Event*, uint, uint*, int> ReplayLog;
        public delegate* unmanaged[Cdecl]<uint, Vec3*, int> ProbePosition;
    }
}

/// <summary>The C# functions behind the fake <c>kiln.players</c> table. Modelled: status, list, get, find_name,
/// find_steam_id, position, events, last_seq, and probe_position for player entities. Unavailable: subscribe and
/// unsubscribe (native subscribers; Kiln.Players polls), raw_slot (there is no game record), replay_log (there is no
/// log tracker), probe_position for any other entity (there is no world).</summary>
internal static unsafe class PlayersTable
{
    private static PlayersAbi.Table* _table;

    public static nint Create()
    {
        if (_table != null) return (nint)_table;
        var t = (PlayersAbi.Table*)NativeMemory.AllocZeroed((nuint)sizeof(PlayersAbi.Table));  // process lifetime, never freed
        t->Header = new ServiceHeader { Size = (uint)sizeof(PlayersAbi.Table), Version = PlayersAbi.Version };
        t->Status = &Status;
        t->List = &List;
        t->Get = &Get;
        t->FindName = &FindName;
        t->FindSteamId = &FindSteamId;
        t->Position = &Position;
        t->Events = &Events;
        t->LastSeq = &LastSeq;
        t->Subscribe = &Subscribe;
        t->Unsubscribe = &Unsubscribe;
        t->RawSlot = &RawSlot;
        t->ReplayLog = &ReplayLog;
        t->ProbePosition = &ProbePosition;
        _table = t;
        return (nint)t;
    }

    private static PlayersFake? Fake => FakeTables.Active(FakeTables.PlayersService)?.PlayersFake;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Status(PlayersAbi.Status* out_)
    {
        try
        {
            if (out_ == null) return FakeTables.InvalidArgument;
            var size = out_->Size;
            if (size < (uint)sizeof(PlayersAbi.Status)) return FakeTables.InvalidArgument;
            var s = new PlayersAbi.Status { Size = (uint)sizeof(PlayersAbi.Status), LastSeq = PlayersFake.LastSeq };
            if (Fake is { } f) f.FillStatus(ref s);  // else source none: nothing works with no harness (or switched off)
            *out_ = s;
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int List(uint flags, PlayersAbi.Player* out_, uint capacity, uint* count)
    {
        try
        {
            if (count == null || (out_ == null && capacity > 0)) return FakeTables.InvalidArgument;
            if (Fake is not { } f) return FakeTables.Unavailable;
            var all = f.Snapshot(flags);
            *count = (uint)all.Length;
            var n = Math.Min((uint)all.Length, capacity);
            for (var i = 0; i < n; i++) out_[i] = all[i];
            return all.Length > capacity ? FakeTables.BufferTooSmall : FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Get(uint entity, PlayersAbi.Player* out_)
    {
        try
        {
            if (out_ == null) return FakeTables.InvalidArgument;
            if (Fake is not { } f) return FakeTables.Unavailable;
            foreach (var p in f.Snapshot(PlayersAbi.ListAll))
                if (p.Entity == entity)
                {
                    *out_ = p;
                    return FakeTables.Ok;
                }
            return FakeTables.NotFound;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FindName(NativeStr name, PlayersAbi.Player* out_)
    {
        try
        {
            if (out_ == null) return FakeTables.InvalidArgument;
            if (Fake is not { } f) return FakeTables.Unavailable;
            var wanted = name.ToString();
            var all = f.Snapshot(PlayersAbi.ListAll);
            // exact first, then ASCII case-insensitive; an in-game player wins over one joining or leaving
            foreach (var exact in new[] { true, false })
                foreach (var inGame in new[] { true, false })
                    foreach (var p in all)
                    {
                        if ((p.State == PlayersAbi.StateInGame) != inGame) continue;
                        var n = PlayersFake.NameOf(p);
                        if (exact ? n == wanted : AsciiEqualsIgnoreCase(n, wanted))
                        {
                            *out_ = p;
                            return FakeTables.Ok;
                        }
                    }
            return FakeTables.NotFound;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    private static bool AsciiEqualsIgnoreCase(string a, string b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            char x = a[i], y = b[i];
            if (x == y) continue;
            if (x < 128 && y < 128 && char.ToLowerInvariant(x) == char.ToLowerInvariant(y)) continue;
            return false;
        }
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FindSteamId(ulong steamId, PlayersAbi.Player* out_)
    {
        try
        {
            if (out_ == null) return FakeTables.InvalidArgument;
            if (Fake is not { } f) return FakeTables.Unavailable;
            if (steamId == 0) return FakeTables.NotFound;
            foreach (var p in f.Snapshot(PlayersAbi.ListAll))
                if (p.SteamId == steamId && (p.Flags & PlayersAbi.HasSteamId) != 0)
                {
                    *out_ = p;
                    return FakeTables.Ok;
                }
            return FakeTables.NotFound;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Position(uint entity, PlayersAbi.Vec3* out_)
    {
        try
        {
            if (out_ == null) return FakeTables.InvalidArgument;
            if (Fake is not { } f) return FakeTables.Unavailable;
            // tick thread: now (the position the test set last); any other thread: the last scan's
            if (f.PositionOf(entity, live: FakeTables.OnTickThread) is not { } v) return FakeTables.NotFound;
            *out_ = new PlayersAbi.Vec3 { X = v.X, Y = v.Y, Z = v.Z };
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Events(ulong afterSeq, PlayersAbi.Event* out_, uint capacity, uint* count)
    {
        try
        {
            if (count == null || (out_ == null && capacity > 0)) return FakeTables.InvalidArgument;
            *count = 0;
            if (Fake is not { } f) return FakeTables.Unavailable;
            var list = f.EventsAfter(afterSeq, capacity);
            for (var i = 0; i < list.Count; i++) out_[i] = list[i];
            *count = (uint)list.Count;
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong LastSeq() => PlayersFake.LastSeq;

    // Native subscribers (a C++ service's callback): Kiln.Players polls events() instead. Not modelled.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Subscribe(ulong owner, nint fn, nint user, ulong* handle) => FakeTables.Unavailable;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Unsubscribe(ulong handle) => FakeTables.Unavailable;

    // There is no game player record to read.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int RawSlot(uint slot, PlayersAbi.SlotRaw* out_) => FakeTables.OnTickThread ? FakeTables.Unavailable : FakeTables.WrongThread;

    // There is no game-log tracker in the kit.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReplayLog(NativeStr* lines, uint count, PlayersAbi.Player* out_, uint capacity, uint* outCount,
        PlayersAbi.Event* events, uint eventsCapacity, uint* outEvents) => FakeTables.Unavailable;

    // The position path for players works (their positions are the test's); there is no world for other entities.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ProbePosition(uint entity, PlayersAbi.Vec3* out_)
    {
        try
        {
            if (out_ == null) return FakeTables.InvalidArgument;
            if (!FakeTables.OnTickThread) return FakeTables.WrongThread;
            if (Fake is not { } f || !f.IsPlayerInGame(entity)) return FakeTables.Unavailable;
            if (f.PositionOf(entity, live: true) is not { } v) return FakeTables.NotFound;
            *out_ = new PlayersAbi.Vec3 { X = v.X, Y = v.Y, Z = v.Z };
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }
}

/// <summary>One harness's players, as the fake kiln.players reports them: changes the test makes take effect at the scan
/// at the start of the next step, as the native service reads the game's records at each server-world Pre.</summary>
internal sealed unsafe class PlayersFake
{
    /// <summary>Player slots on this build (<c>status.slots</c>).</summary>
    public const int Slots = 16;

    private const int Ring = 64;
    private static long _seq;  // per process: sequence numbers never go back

    public static ulong LastSeq => (ulong)Interlocked.Read(ref _seq);

    private readonly object _lock = new();
    private readonly KilnHarness _harness;
    private readonly FakePlayer?[] _slots = new FakePlayer?[Slots];  // who holds a slot (joining, in game, or leaving until the scan)
    private readonly uint[] _generation = new uint[Slots];
    private readonly List<FakePlayer> _joining = [];
    private readonly List<FakePlayer> _leaving = [];
    private readonly Queue<PlayersAbi.Event> _ring = new();
    private PlayersAbi.Player[] _snapshot = [];
    private readonly Dictionary<uint, WorldPosition?> _snapPositions = new();
    private ulong _scans;
    private ulong _nextSteam = 76561197960265728UL + 0x7E57_0000;  // a SteamID64 for a player the test gave none

    public PlayersFake(KilnHarness harness) => _harness = harness;

    public void FillStatus(ref PlayersAbi.Status s)
    {
        lock (_lock)
        {
            s.Source = PlayersAbi.SourceRecords;  // full data: entity, Steam id, admin bit, user-group rights, position
            s.Records = 0;    // no game records behind it (raw_slot is unavailable)
            s.Admin = 1;      // the admin bit is known for every player
            s.Positions = 1;  // positions are the test's
            s.Log = 0;
            s.MapOk = 0;
            s.LayoutOk = 0;
            s.Simulated = 1;  // the players are simulated (by the test kit)
            s.Slots = Slots;
            s.InGame = (uint)_snapshot.Count(p => p.State == PlayersAbi.StateInGame);
            s.Connected = (uint)_snapshot.Length;
            s.Scans = _scans;
        }
    }

    // ---- the test's side ----

    public FakePlayer Join(string name, ulong steamId, PlayerPermissions rights, bool isAdmin, WorldPosition? position, uint userGroupId)
    {
        lock (_lock)
        {
            var slot = Array.IndexOf(_slots, null);
            if (slot < 0)
                throw new InvalidOperationException($"the server is full: all {Slots} player slots are taken (a player who leaves frees the slot at the next step)");
            if (steamId == 0) steamId = _nextSteam++;
            foreach (var p in _slots)
                if (p is not null && p.SteamId == steamId)
                    throw new InvalidOperationException($"Steam id {steamId} is on the server already ({p.Name})");
            var handle = (uint)slot | (++_generation[slot] << 6);
            var player = new FakePlayer(this, name, steamId, slot, handle, rights, isAdmin, userGroupId, position);
            _slots[slot] = player;
            _joining.Add(player);
            return player;
        }
    }

    public bool Leave(FakePlayer player)
    {
        lock (_lock)
        {
            if (player.Phase is FakePlayer.Stage.Left or FakePlayer.Stage.Leaving) return false;
            if (player.Phase == FakePlayer.Stage.Joining)
            {
                // left before any scan saw them in game: the game never logged them in, no event
                _joining.Remove(player);
                _slots[player.Slot] = null;
                player.Phase = FakePlayer.Stage.Left;
                return true;
            }
            player.Phase = FakePlayer.Stage.Leaving;
            _leaving.Add(player);
            return true;
        }
    }

    public IReadOnlyList<FakePlayer> Present
    {
        get
        {
            lock (_lock) return _slots.Where(p => p is not null && p.Phase is FakePlayer.Stage.Joining or FakePlayer.Stage.InGame).ToArray()!;
        }
    }

    internal object Lock => _lock;

    // ---- the scan (tick thread, start of each step, before Kiln's service tick) ----

    /// <summary>Returns how many players left in this scan (the game requests a save for each).</summary>
    public int Scan(ulong step)
    {
        lock (_lock)
        {
            _scans++;
            var left = 0;
            foreach (var p in _leaving)
            {
                var last = p.Native(PlayersAbi.StateInGame);  // LEFT: as last seen in game (the last scan's position)
                Emit(PlayersAbi.EventLeft, step, last);
                _slots[p.Slot] = null;
                p.Phase = FakePlayer.Stage.Left;
                left++;
            }
            _leaving.Clear();
            var joined = _joining.ToArray();
            _joining.Clear();
            foreach (var p in joined)
            {
                p.Phase = FakePlayer.Stage.InGame;
                p.JoinedStep = step;
            }
            var now = new List<PlayersAbi.Player>();
            _snapPositions.Clear();
            foreach (var p in _slots)
            {
                if (p is not { Phase: FakePlayer.Stage.InGame }) continue;
                p.SeenPosition = p.LivePosition;
                now.Add(p.Native(PlayersAbi.StateInGame));
                _snapPositions[(uint)p.Slot + 1] = p.SeenPosition;
            }
            foreach (var p in joined) Emit(PlayersAbi.EventJoined, step, p.Native(PlayersAbi.StateInGame));
            _snapshot = now.ToArray();
            return left;
        }
    }

    /// <summary>The harness ends: everyone is gone, with no events (the server and its mods are gone already).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var p in _slots)
                if (p is not null)
                    p.Phase = FakePlayer.Stage.Left;
            Array.Clear(_slots);
            _joining.Clear();
            _leaving.Clear();
            _ring.Clear();
            _snapshot = [];
        }
    }

    private void Emit(uint kind, ulong step, PlayersAbi.Player player)
    {
        var e = new PlayersAbi.Event { Seq = (ulong)Interlocked.Increment(ref _seq), Kind = kind, Step = step, Player = player };
        _ring.Enqueue(e);
        while (_ring.Count > Ring) _ring.Dequeue();
    }

    // ---- reads (any thread) ----

    public PlayersAbi.Player[] Snapshot(uint flags)
    {
        lock (_lock) return _snapshot;  // every snapshot entry is in game: ALL lists the same players
    }

    public List<PlayersAbi.Event> EventsAfter(ulong after, uint capacity)
    {
        var list = new List<PlayersAbi.Event>();
        lock (_lock)
            foreach (var e in _ring)
            {
                if (list.Count >= capacity) break;
                if (e.Seq > after) list.Add(e);
            }
        return list;
    }

    public bool IsPlayerInGame(uint entity)
    {
        lock (_lock) return _snapshot.Any(p => p.Entity == entity);
    }

    public WorldPosition? PositionOf(uint entity, bool live)
    {
        lock (_lock)
        {
            if (!_snapPositions.TryGetValue(entity, out var seen)) return null;  // not a player in game
            if (!live) return seen;
            var slot = (int)entity - 1;
            return slot is >= 0 and < Slots && _slots[slot] is { Phase: FakePlayer.Stage.InGame or FakePlayer.Stage.Leaving } p ? p.LivePosition : seen;
        }
    }

    public static string NameOf(PlayersAbi.Player p) => Encoding.UTF8.GetString(p.Name, (int)Math.Min(p.NameLen, PlayersAbi.NameMax - 1));

    internal ulong CurrentStep => _harness.CurrentStep;
}
