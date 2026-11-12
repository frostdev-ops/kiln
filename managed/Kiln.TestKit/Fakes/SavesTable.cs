// SPDX-License-Identifier: MIT
// The fake kiln.saves (native/core/abi/services/kiln_saves.h, v1) under the real Kiln.Saves.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln.TestKit;

/// <summary>Mirror of <c>kiln_saves.h</c> v1. Sizes are pinned by tests to the service's own mirror (<c>SavesNative</c>).</summary>
internal static unsafe class SavesAbi
{
    public const uint Version = 1;
    public const uint ReasonUnknown = 0, ReasonAutosave = 1, ReasonShutdown = 2, ReasonPlayerLeft = 3, ReasonRequest = 4, ReasonBackup = 5;
    public const uint EvSaving = 1, EvWritten = 2, EvFailed = 3, EvLoaded = 4, EvSnapshot = 5;
    public const uint FlagSync = 1, FlagHasGuid = 2, FlagNewWorld = 4, FlagTimeout = 8;
    public const uint StateNone = 0, StateLoading = 1, StateRunning = 2, StateSaving = 3;
    public const uint CanRequest = 1, CanWrite = 2, CanLoad = 4, CanReason = 8, ClientHosted = 16, CanSnapshotStage = 32;
    public const int PathSaveDir = 0, PathWorldFile = 1, PathData = 2, PathBackups = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public uint Size, Kind;
        public ulong Seq;
        public uint Reason, Flags;
        public ulong Step;
        public long UnixMs;
        public uint Revision, Result;
        public ushort Slot, Reserved16;
        public uint WorldUnix;
        public fixed byte Guid[16];
        public double DurationMs;
        public ulong SaveSeq;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size, Features, State, LastReason;
        public fixed byte Guid[16];
        public uint HasGuid, Revision;
        public ushort Slot, PendingRequest;
        public uint WorldUnix;
        public long LastWrittenUnixMs, LastAcceptedUnixMs;
        public double LastDurationMs;
        public long AutosaveIntervalNs;
        public ulong Accepted, Refused, Written, Failed, LastSeq, Step;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
        public delegate* unmanaged[Cdecl]<uint, int> Request;
        public delegate* unmanaged[Cdecl]<ulong, Event*, int> NextEvent;
        public delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<uint, void*, void>, void*, ulong*, int> SetPump;
        public delegate* unmanaged[Cdecl]<int, byte*, nuint, nuint*, int> Path;
        public delegate* unmanaged[Cdecl]<uint, int> SetPumpInterval;
    }
}

/// <summary>The C# functions behind the fake <c>kiln.saves</c> table. Modelled: status, request (reason request),
/// next_event, set_pump, path (save folder, sidecar root, backups folder), set_pump_interval. Unavailable: request with
/// reason backup (there is no world file to copy), path of the world file (NOT_FOUND: the kit writes none). Not reported:
/// the SNAPSHOT stage (CAN_SNAPSHOT_STAGE is off, as on a build without it) and CLIENT_HOSTED.</summary>
internal static unsafe class SavesTable
{
    private static SavesAbi.Table* _table;
    private static delegate* unmanaged[Cdecl]<uint, void*, void> _pump;
    private static void* _pumpUser;
    private static uint _pumpInterval;

    public static nint Create()
    {
        if (_table != null) return (nint)_table;
        var t = (SavesAbi.Table*)NativeMemory.AllocZeroed((nuint)sizeof(SavesAbi.Table));  // process lifetime, never freed
        t->Header = new ServiceHeader { Size = (uint)sizeof(SavesAbi.Table), Version = SavesAbi.Version };
        t->GetStatus = &GetStatus;
        t->Request = &Request;
        t->NextEvent = &NextEvent;
        t->SetPump = &SetPump;
        t->Path = &Path;
        t->SetPumpInterval = &SetPumpInterval;
        _table = t;
        return (nint)t;
    }

    private static WorldFake? Fake => FakeTables.Active(FakeTables.SavesService)?.WorldFake;

    public static uint PumpInterval => Volatile.Read(ref _pumpInterval);

    public static bool HasPump => _pump != null;

    /// <summary>Calls Kiln.Saves' pump (tick thread), as the native service does inside the game's save request and at the
    /// start of a step with events queued.</summary>
    public static void CallPump(uint kind)
    {
        var fn = _pump;
        if (fn != null) fn(kind, _pumpUser);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetStatus(SavesAbi.Status* out_)
    {
        try
        {
            if (out_ == null || out_->Size < (uint)sizeof(SavesAbi.Status)) return FakeTables.InvalidArgument;
            var s = new SavesAbi.Status { Size = (uint)sizeof(SavesAbi.Status), LastSeq = WorldFake.LastSeq };
            if (Fake is { } f) f.FillStatus(ref s);  // else: no server world (state none, nothing works)
            *out_ = s;
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Request(uint reason)
    {
        try
        {
            if (Fake is not { } f) return FakeTables.Unavailable;
            return reason switch
            {
                SavesAbi.ReasonRequest => f.Request(reason),
                SavesAbi.ReasonBackup => FakeTables.Unavailable,  // a backup copies the world file; the kit has none
                _ => FakeTables.InvalidArgument,
            };
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int NextEvent(ulong afterSeq, SavesAbi.Event* out_)
    {
        try
        {
            if (out_ == null) return FakeTables.InvalidArgument;
            if (Fake is not { } f || f.Next(afterSeq) is not { } e) return FakeTables.NotFound;
            *out_ = e;
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    // The one pump (Kiln.Saves): wiring for the whole process, not a harness's state.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetPump(ulong owner, delegate* unmanaged[Cdecl]<uint, void*, void> fn, void* user, ulong* lastSeq)
    {
        _pump = fn;
        _pumpUser = user;
        if (lastSeq != null) *lastSeq = WorldFake.LastSeq;
        return FakeTables.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Path(int kind, byte* buffer, nuint capacity, nuint* outLen)
    {
        try
        {
            if (outLen == null || (buffer == null && capacity > 0)) return FakeTables.InvalidArgument;
            if (Fake is not { } f) return FakeTables.Unavailable;
            var path = f.PathOf(kind, out var rc);
            if (path is null) return rc;
            var bytes = Encoding.UTF8.GetBytes(path);
            *outLen = (nuint)bytes.Length;
            if (capacity < (nuint)bytes.Length) return FakeTables.BufferTooSmall;
            bytes.CopyTo(new Span<byte>(buffer, bytes.Length));
            if (capacity > (nuint)bytes.Length) buffer[bytes.Length] = 0;
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetPumpInterval(uint steps)
    {
        Volatile.Write(ref _pumpInterval, steps);
        return FakeTables.Ok;
    }
}

/// <summary>
/// One harness's world as the fake kiln.saves reports it: a world id, its revision and header time (kept in
/// <c>savegame/kiln-testkit-world.json</c> of the server folder, the kit's stand-in for the world file's header), and the
/// game's save cycle on the harness's steps: a request is accepted at the start of the next step (SAVING, delivered
/// synchronously, as inside the game's request), the world file is complete at the start of the step after
/// (WRITTEN, revision + 1). The game's own saves: the autosave every 30 s of the harness's virtual time (the timer
/// restarts at every request), a save when a player leaves, and the shutdown save when the server stops (skipped by
/// <see cref="KilnHarness.Kill"/>). The world loads (LOADED) at the first step after the server has started.
/// </summary>
internal sealed unsafe class WorldFake
{
    private const int QueueMax = 256;
    private const long AutosaveMs = 30_000, RequestTimeoutMs = 60_000;
    internal const string StateFileName = "kiln-testkit-world.json";
    private static long _seq;

    public static ulong LastSeq => (ulong)Interlocked.Read(ref _seq);

    private readonly object _lock = new();
    private readonly KilnHarness _harness;
    private readonly List<SavesAbi.Event> _queue = [];
    private bool _loaded, _killed, _queuedSincePump;
    private uint? _pending;
    private long _pendingSinceMs, _lastRequestMs;
    private (ulong Seq, uint Reason, uint Revision, long AcceptedMs)? _writing;
    private ulong _accepted, _written, _failed, _stepsSincePump;  // refused: the fake never refuses a request it accepted as pending
    private uint _lastReason;
    private long _lastWrittenUnixMs, _lastAcceptedUnixMs;
    private double _lastDurationMs;

    public WorldFake(KilnHarness harness, Guid? worldId)
    {
        _harness = harness;
        Directory.CreateDirectory(SaveDirectory);
        var state = ReadState();
        Id = worldId ?? (state?["current"]?.GetValue<string>() is { } cur && Guid.TryParse(cur, out var g) ? g : Guid.NewGuid());
        if (Id == Guid.Empty) throw new ArgumentException("the world id cannot be the empty GUID", nameof(worldId));
        if (state?["worlds"]?[Id.ToString()] is JsonObject w)
        {
            Revision = w["revision"]?.GetValue<uint>() ?? 0;
            Unix = w["unix"]?.GetValue<uint>() ?? 0;
        }
        WriteState();
    }

    public Guid Id { get; }

    /// <summary>The newest written (or loaded) revision; 0 for a world never saved.</summary>
    public uint Revision { get; private set; }

    /// <summary>The world file header's UTC seconds of <see cref="Revision"/>.</summary>
    public uint Unix { get; private set; }

    public string SaveDirectory => Path.Combine(_harness.ServerDirectory, "savegame");

    public string DataRoot => Path.Combine(_harness.DataDirectory, "worlds");

    public string BackupsDirectory => Path.Combine(_harness.KilnDirectory, "backups");

    public bool Loaded
    {
        get
        {
            lock (_lock) return _loaded;
        }
    }

    public bool Saving
    {
        get
        {
            lock (_lock) return _writing is not null || _pending is not null;
        }
    }

    public ulong Written
    {
        get
        {
            lock (_lock) return _written;
        }
    }

    // ---- table functions (any thread) ----

    public void FillStatus(ref SavesAbi.Status s)
    {
        lock (_lock)
        {
            s.Features = SavesAbi.CanRequest | SavesAbi.CanWrite | SavesAbi.CanLoad | SavesAbi.CanReason;
            s.State = _harness.CurrentStep == 0 ? SavesAbi.StateNone
                : !_loaded ? SavesAbi.StateLoading
                : _writing is not null ? SavesAbi.StateSaving
                : SavesAbi.StateRunning;
            s.LastReason = _lastReason;
            var bytes = Id.ToByteArray();
            for (var i = 0; i < 16; i++) s.Guid[i] = bytes[i];
            s.HasGuid = 1;  // the kit's world has its id from the start, a new one too
            s.Revision = Revision;
            s.Slot = 0;     // unknown
            s.PendingRequest = _pending is not null ? (ushort)1 : (ushort)0;
            s.WorldUnix = Unix;
            s.LastWrittenUnixMs = _lastWrittenUnixMs;
            s.LastAcceptedUnixMs = _lastAcceptedUnixMs;
            s.LastDurationMs = _lastDurationMs;
            s.AutosaveIntervalNs = AutosaveMs * 1_000_000;
            s.Accepted = _accepted;
            s.Refused = 0;
            s.Written = _written;
            s.Failed = _failed;
            s.Step = _harness.CurrentStep;
        }
    }

    /// <summary>Asks for a save at the start of the next step (after a save that is being written); requests made while
    /// one is pending are merged into it.</summary>
    public int Request(uint reason)
    {
        lock (_lock)
        {
            if (_killed) return FakeTables.Unavailable;
            if (_pending is null)
            {
                _pending = reason;
                _pendingSinceMs = _harness.VirtualMs;
            }
            return FakeTables.Ok;
        }
    }

    public SavesAbi.Event? Next(ulong after)
    {
        lock (_lock)
            foreach (var e in _queue)
                if (e.Seq > after) return e;
        return null;
    }

    public string? PathOf(int kind, out int rc)
    {
        rc = FakeTables.Ok;
        switch (kind)
        {
            case SavesAbi.PathSaveDir: return SaveDirectory;
            case SavesAbi.PathData: return DataRoot;
            case SavesAbi.PathBackups: return BackupsDirectory;
            case SavesAbi.PathWorldFile:
                rc = FakeTables.NotFound;  // the kit writes no world file
                return null;
            default:
                rc = FakeTables.InvalidArgument;
                return null;
        }
    }

    // ---- the game's side (tick thread) ----

    /// <summary>The start of a step, before Kiln's service tick (the native Pre): a written save is reported, the world
    /// loads, the autosave timer runs, a pending request is accepted (the pump runs inside it).</summary>
    public void BeforeStep(ulong step, bool serverRunning)
    {
        var accepted = false;
        lock (_lock)
        {
            if (_killed) return;
            CompleteWrite(step);
            var now = _harness.VirtualMs;
            if (!_loaded && serverRunning)
            {
                _loaded = true;
                _lastRequestMs = now;
                Queue(new SavesAbi.Event
                {
                    Kind = SavesAbi.EvLoaded, Step = step, Revision = Revision, WorldUnix = Unix,
                    Flags = Revision == 0 ? SavesAbi.FlagNewWorld : 0,
                });
            }
            if (_loaded && _pending is null && _writing is null && now - _lastRequestMs >= AutosaveMs)
            {
                _pending = SavesAbi.ReasonAutosave;
                _pendingSinceMs = now;
            }
            if (_pending is { } reason && !_loaded && now - _pendingSinceMs >= RequestTimeoutMs)
            {
                _pending = null;
                _failed++;
                Queue(new SavesAbi.Event { Kind = SavesAbi.EvFailed, Reason = reason, Step = step, Revision = Revision, Flags = SavesAbi.FlagTimeout });
            }
            if (_loaded && _writing is null && _pending is { } r) accepted = Accept(r, step);
        }
        if (accepted) Pump(SavesAbi.EvSaving);
    }

    /// <summary>After the players' scan: the game requests a save for each player who left.</summary>
    public void PlayersLeft(int count)
    {
        if (count > 0) Request(SavesAbi.ReasonPlayerLeft);
    }

    /// <summary>The end of the native Pre: the pump runs when events are queued, and every <c>set_pump_interval</c> steps.</summary>
    public void AfterScan()
    {
        bool run;
        lock (_lock)
        {
            if (_killed) return;
            _stepsSincePump++;
            var interval = SavesTable.PumpInterval;
            run = _queuedSincePump || (interval > 0 && _stepsSincePump >= interval);
        }
        if (run) Pump(0);
    }

    /// <summary>The server stops (its mods have unloaded): the game's shutdown save, a save that was being written first.
    /// Compressed into the stop step; in the game it takes a few more steps.</summary>
    public void ShutdownSave(ulong step)
    {
        lock (_lock)
        {
            if (_killed || !_loaded) return;
            CompleteWrite(step);
            _pending = null;  // merged into the shutdown save
            Accept(SavesAbi.ReasonShutdown, step);
        }
        Pump(SavesAbi.EvSaving);
        lock (_lock) CompleteWrite(step);
        Pump(SavesAbi.EvWritten);
    }

    /// <summary>The server process is killed: nothing more is saved, a save being written never completes.</summary>
    public void Kill()
    {
        lock (_lock)
        {
            _killed = true;
            _pending = null;
            _writing = null;
        }
    }

    private bool Accept(uint reason, ulong step)
    {
        _pending = null;
        _accepted++;
        _lastReason = reason;
        _lastAcceptedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _lastRequestMs = _harness.VirtualMs;  // the autosave timer restarts at every request
        var revision = Revision + 1;
        var seq = Queue(new SavesAbi.Event { Kind = SavesAbi.EvSaving, Reason = reason, Step = step, Revision = revision, Flags = SavesAbi.FlagSync });
        _writing = (seq, reason, revision, _harness.VirtualMs);
        return true;
    }

    private void CompleteWrite(ulong step)
    {
        if (_writing is not { } w) return;
        _writing = null;
        var unix = (uint)Math.Max(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Unix + 1L);
        Revision = w.Revision;
        Unix = unix;
        WriteState();
        _written++;
        _lastWrittenUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _lastDurationMs = _harness.VirtualMs - w.AcceptedMs;
        Queue(new SavesAbi.Event
        {
            Kind = SavesAbi.EvWritten, Reason = w.Reason, Step = step, Revision = w.Revision, WorldUnix = unix,
            DurationMs = _lastDurationMs, SaveSeq = w.Seq,
        });
    }

    private ulong Queue(SavesAbi.Event e)
    {
        e.Size = (uint)sizeof(SavesAbi.Event);
        e.Seq = (ulong)Interlocked.Increment(ref _seq);
        e.Flags |= SavesAbi.FlagHasGuid;
        e.UnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bytes = Id.ToByteArray();
        for (var i = 0; i < 16; i++) e.Guid[i] = bytes[i];
        _queue.Add(e);
        if (_queue.Count > QueueMax) _queue.RemoveAt(0);
        _queuedSincePump = true;
        return e.Seq;
    }

    private void Pump(uint kind)
    {
        lock (_lock)
        {
            _queuedSincePump = false;
            _stepsSincePump = 0;
        }
        SavesTable.CallPump(kind);  // outside the lock: the pump reads status and events
    }

    // ---- the kit's stand-in for the world file header ----

    private string StateFile => Path.Combine(SaveDirectory, StateFileName);

    private JsonObject? ReadState()
    {
        try
        {
            return File.Exists(StateFile) ? JsonNode.Parse(File.ReadAllText(StateFile)) as JsonObject : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            throw new InvalidDataException($"{StateFile}: {e.Message}", e);
        }
    }

    private void WriteState()
    {
        var state = ReadState() ?? new JsonObject();
        if (state["worlds"] is not JsonObject worlds) state["worlds"] = worlds = new JsonObject();
        worlds[Id.ToString()] = new JsonObject { ["revision"] = Revision, ["unix"] = Unix };
        state["current"] = Id.ToString();
        state["note"] = "Kiln.TestKit's world: what the game keeps in the world file header (id, revision, time). No game data.";
        var tmp = StateFile + ".tmp";
        File.WriteAllText(tmp, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, StateFile, overwrite: true);
    }
}
