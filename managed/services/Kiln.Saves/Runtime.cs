// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>Kiln.Saves' engine room: the native pump, event dispatch, pending requests.</summary>
/// <remarks>Started by the module initializer (any first use of this assembly: a mod subscribing to one of its events,
/// or calling one of its classes), so mods that only use <c>[Subscribe]</c> need nothing else.</remarks>
internal static unsafe class SavesRuntime
{
    private const uint PumpEverySteps = 25;  // ~1 s: EntityData's destroy checks and re-matching
    private static readonly object Gate = new();
    private static int _state;  // 0 not tried, 1 active, 2 unavailable
    private static ulong _lastSeq;
    private static string? _worldFile;
    private static bool _loadedEventSeen, _labHosted;
    private static readonly List<Pending> Requests = [];
    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);

    internal static readonly WorldStore Store = new();

    private sealed class Pending
    {
        public required SaveReason Reason;
        public required ulong AfterSeq;
        public required ModContext? Mod;
        public TaskCompletionSource<SaveResult>? Save;
        public TaskCompletionSource<BackupInfo>? Backup;
        public string? BackupName;
    }

    [ModuleInitializer]
    internal static void ModuleInit()
    {
        try
        {
            // per-world configs (Kiln.Api's WorldConfig) keep their files in this world's sidecar folder, and follow it
            WorldConfigs.DirectoryProvider = OpenWorldDirectory;
            Ensure();
        }
        catch
        {
            // never fail a type load; Ensure is retried on first use
            _state = 0;
        }
    }

    internal static bool Ensure()
    {
        if (_state != 0) return _state == 1;
        lock (Gate)
        {
            if (_state != 0) return _state == 1;
            var t = SavesNative.T;
            if (t == null)
            {
                _state = 2;
                // the rows exist from the start, also when kiln.saves is not there (once, unavailable)
                InstanceCapabilities.Refresh();
                InstanceCapabilities.ReportCarried();
                return false;
            }
            ulong last = 0;
            t->SetPump(0, &OnPump, null, &last);
            t->SetPumpInterval(PumpEverySteps);
            _lastSeq = last;
            _state = 1;
            // The host unloads mods on a stop signal before the game makes its shutdown save: announce that save to
            // mods while they still run. A service subscription (no mod context), so it outlives whichever mod
            // triggered this start.
            var outer = ModContext.CurrentMod;
            ModContext.CurrentMod = null;
            try
            {
                Events.On<ServerStoppingEvent>(OnServerStopping, EventPriority.Lowest);
            }
            finally
            {
                ModContext.CurrentMod = outer;
            }
            var st = SavesNative.ReadStatus();
            Log(LogLevel.Info, $"Kiln.Saves: started (native events from seq {last + 1}; state {StateText(st?.State ?? 0)}, " +
                               $"world {GuidOf(st)?.ToString() ?? "unknown"} revision {st?.Revision ?? 0})");
            AttachItems();
            InstanceCapabilities.Refresh();
            InstanceCapabilities.ReportCarried();
            if (st is { } s0 && (s0.Features & SavesNative.CanSnapshotStage) == 0)
                Log(LogLevel.Warn, "Kiln.Saves: this build has no snapshot staging: instance data is read one simulation step before the " +
                                   "game's snapshot, so identical items (twins) cannot be told apart after a restart (they are set aside)");
            return true;
        }
    }

    internal static void Log(LogLevel level, string text) => Backend.Current.Log(0, level, text);

    /// <summary>Instance data reads the game's items through Kiln.Items (kiln.items v4). Set once at the start; whether item
    /// instances are on is asked at every call, so a server without them has InstanceData off, naming items.instances.</summary>
    private static void AttachItems()
    {
        try
        {
            Store.Instances.World ??= new InstanceGame();
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException or MissingMethodException)
        {
            Log(LogLevel.Warn, $"Kiln.Saves: Kiln.Items could not be loaded ({e.Message}); instance data is off (items.instances)");
        }
    }

    /// <summary>The sidecar folder of the world that is open (opened now if nothing used it yet, as WorldData does); null
    /// before a new world's first save, while a hosted world is closed, or without the native service.</summary>
    private static string? OpenWorldDirectory()
    {
        if (!Ensure()) return null;
        Store.EnsureOpen();
        return Store.Root;
    }

    private static bool _shutdownAnnounced;
    private static ulong _savingSeq, _snapshotSeq;
    private static uint _savingRevision;

    /// <summary>Tick thread, after the mods' own ServerStopping handlers and before they unload. The game's shutdown
    /// save comes a few steps later; what mods write now (and what they hold when they unload) goes into it.</summary>
    private static void OnServerStopping(ServerStoppingEvent e)
    {
        // A game client closing a hosted world: the game's own shutdown save has already run (its Saving/Saved events
        // went out then) and the world is going; the next hosted world is a new session with its own load.
        if (e.Reason == "world-stopping")
        {
            CharacterSync.OnWorldClosing();
            CloseHostedWorld();
            return;
        }
        if (_shutdownAnnounced) return;
        _shutdownAnnounced = true;
        var st = SavesNative.ReadStatus();
        var next = st is { } s ? s.Revision + 1 : 0;
        Log(LogLevel.Info, $"Kiln.Saves: server stopping ({e.Reason}): WorldSavingEvent(Shutdown) for the final save " +
                           $"(expected revision {next}) goes to mods now, before they unload");
        Store.EnsureOpen();
        if (Events.HasSubscribers<WorldSavingEvent>())
            Events.Post(new WorldSavingEvent(SaveReason.Shutdown, next, Store.WorldId, WorldFile, true, e.Step));
        CharacterSync.OnWorldSaving(SaveReason.Shutdown);
    }

    /// <summary>The closed world's last events first: they commit its save, into its own store. Without the game's load hook nothing
    /// would ever open the next world, so it stays as it was there.</summary>
    private static void CloseHostedWorld()
    {
        DrainEvents();
        _worldFile = null;
        if (SavesNative.ReadStatus() is { } st && (st.Features & SavesNative.CanLoad) == 0)
        {
            Log(LogLevel.Warn, "Kiln.Saves: hosted world closed; this build has no load hook, so mod data stays open for the next world");
            return;
        }
        var closed = Store.WorldId;
        Store.CloseWorld();
        _loadedEventSeen = false;
        InstanceCapabilities.Refresh();
        Log(LogLevel.Info, $"Kiln.Saves: hosted world {closed ?? "(no id yet)"} closed after its shutdown save; the next world opens its own mod data when the game loads it");
    }

    /// <summary>Lab: what a hosted game's close does to the mod data, on a dedicated server (no second world exists there).</summary>
    internal static void LabCloseWorld()
    {
        CloseHostedWorld();
        _labHosted = true;
        InstanceCapabilities.Refresh();
    }

    /// <summary>Lab: what the next world's load does, for a world id and revision the test chooses.</summary>
    internal static void LabWorldLoaded(string? id, uint revision, uint unix, bool newWorld)
    {
        _worldFile = null;
        _loadedEventSeen = true;
        Store.OnWorldLoaded(id, revision, unix, newWorld);
        InstanceCapabilities.Refresh();
        if (Events.HasSubscribers<WorldLoadedEvent>())
            Events.Post(new WorldLoadedEvent(Store.WorldId ?? id, revision, newWorld, WorldFile, Store.Match));
    }

    /// <summary>Test kit: the session of the open world ends, so the next one starts as a fresh process does. The world's
    /// data closes (instance data included) and the store can open the next world; the save sequence, the shutdown
    /// announcement, the cached world file and the pending requests go. The native table and its event sequence
    /// (<c>_state</c>, <c>_lastSeq</c>) live for the process and stay.</summary>
    internal static void EndSession()
    {
        Store.CloseWorld();
        Store.Reopenable();
        _shutdownAnnounced = false;
        _loadedEventSeen = false;
        _labHosted = false;
        _worldFile = null;
        _savingSeq = _snapshotSeq = 0;
        _savingRevision = 0;
        lock (Gate)
        {
            Requests.Clear();
            Watched.Clear();
        }
        InstanceCapabilities.Forget();
        InstanceCapabilities.Refresh();
    }

    /// <summary>The world's own data can be read: always on a dedicated server (its one world's data opens before the game loads
    /// it), and on a game client once the game has loaded the hosted world.</summary>
    internal static bool WorldDataReady
    {
        get
        {
            if (!Ensure()) return true;
            var st = SavesNative.ReadStatus();
            var hosted = _labHosted || (st is { } h && (h.Features & SavesNative.ClientHosted) != 0);
            // a lab's hosted close without the native status (the offline tests) waits for its load as a hosted game does
            return !hosted || (st is { } c && (c.Features & SavesNative.CanLoad) == 0) || _loadedEventSeen;
        }
    }

    internal static string? WorldId => Store.WorldId ?? GuidOf(SavesNative.ReadStatus())?.ToString();

    internal static string? WorldFile => _worldFile ??= SavesNative.GetPath(SavesNative.PathWorldFile);

    private static Guid? GuidOf(SavesNative.Status? s)
    {
        if (s is not { HasGuid: not 0 } v) return null;
        var g = SavesNative.ToGuid(v.Guid);
        return g == Guid.Empty ? null : g;
    }

    private static string StateText(uint s) => s switch { 1 => "loading", 2 => "running", 3 => "saving", _ => "none" };

    internal static SaveStatus? ReadStatus()
    {
        if (SavesNative.ReadStatus() is not { } s) return null;
        return new SaveStatus(StateText(s.State), WorldId,
            s.LastWrittenUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(s.LastWrittenUnixMs) : null, s.Revision,
            s.AutosaveIntervalNs > 0 ? TimeSpan.FromTicks(s.AutosaveIntervalNs / 100) : null, (SaveReason)s.LastReason,
            TimeSpan.FromMilliseconds(s.LastDurationMs), (long)s.Accepted, (long)s.Written, (long)s.Failed, (long)s.Refused,
            s.PendingRequest != 0, s.Slot, Store.Generation, Store.Match, (s.Features & SavesNative.CanRequest) != 0,
            (s.Features & SavesNative.CanWrite) != 0);
    }

    // ---- requests ----
    internal static Task<SaveResult> Request(SaveReason reason)
    {
        if (!Ensure()) throw new NotSupportedException("kiln.saves is unavailable");
        var p = Enqueue(reason, out var error);
        if (p == null) return Task.FromResult(new SaveResult(false, reason, 0, TimeSpan.Zero, error));
        p.Save = new TaskCompletionSource<SaveResult>();
        return p.Save.Task;
    }

    internal static Task<BackupInfo> Backup(string name)
    {
        if (!Ensure()) throw new NotSupportedException("kiln.saves is unavailable");
        SavesBackups.CheckName(name);
        var p = Enqueue(SaveReason.Backup, out var error);
        if (p == null) return Task.FromException<BackupInfo>(new InvalidOperationException($"backup '{name}' not started: {error}"));
        p.BackupName = name;
        p.Backup = new TaskCompletionSource<BackupInfo>();
        return p.Backup.Task;
    }

    private static Pending? Enqueue(SaveReason reason, out string? error)
    {
        var t = SavesNative.T!;
        var st = SavesNative.ReadStatus();
        error = null;
        if (st is not { } s || (s.Features & SavesNative.CanRequest) == 0)
        {
            error = "the game's save request was not found on this build";
            return null;
        }
        var rc = t->Request((uint)reason);
        if (rc != SavesNative.Ok)
        {
            error = $"kiln.saves refused the request (rc {rc})";
            return null;
        }
        var mod = ModContext.CurrentMod;
        var p = new Pending { Reason = reason, AfterSeq = s.LastSeq, Mod = mod };
        lock (Gate)
        {
            Requests.Add(p);
            if (mod != null && Watched.Add(mod))
                mod.Retiring += () =>
                {
                    lock (Gate)
                    {
                        Requests.RemoveAll(r => ReferenceEquals(r.Mod, mod));
                        Watched.Remove(mod);
                    }
                };
        }
        return p;
    }

    // ---- pump (tick thread) ----
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPump(uint kind, void* user)
    {
        try
        {
            Pump();
        }
        catch (Exception e)
        {
            try
            {
                Log(LogLevel.Error, $"Kiln.Saves: pump failed: {e}");
            }
            catch
            {
                // nothing may unwind into native code
            }
        }
    }

    private static void Pump()
    {
        if (SavesNative.T == null) return;
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;  // service context; Events.Post sets each handler's mod
        try
        {
            DrainEvents();
            Store.Entities.Tick();
            Store.Instances.Tick();
            InstanceCapabilities.Refresh();
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    private static void DrainEvents()
    {
        var t = SavesNative.T;
        if (t == null) return;
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;
        try
        {
            SavesNative.Event ev;
            while (t->NextEvent(_lastSeq, &ev) == SavesNative.Ok)
            {
                _lastSeq = ev.Seq;
                try
                {
                    Dispatch(ev);
                }
                catch (Exception e)
                {
                    Log(LogLevel.Error, $"Kiln.Saves: handling native event {ev.Seq} (kind {ev.Kind}) failed: {e}");
                }
            }
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    private static void Dispatch(in SavesNative.Event ev)
    {
        var reason = (SaveReason)ev.Reason;
        Guid? guid = null;
        if ((ev.Flags & SavesNative.FlagHasGuid) != 0)
            fixed (byte* g = ev.Guid) guid = SavesNative.ToGuid(g);
        var id = guid is { } x && x != Guid.Empty ? x.ToString() : null;
        switch (ev.Kind)
        {
            case SavesNative.EvSaving:
            {
                _savingSeq = ev.Seq;
                _savingRevision = ev.Revision;
                _snapshotSeq = 0;
                Store.EnsureOpen();
                if (id != null) Store.AdoptWorld(id);
                var sync = (ev.Flags & SavesNative.FlagSync) != 0;
                var announced = reason == SaveReason.Shutdown && _shutdownAnnounced;  // mods had it at ServerStopping
                if (!announced && Events.HasSubscribers<WorldSavingEvent>())
                    Events.Post(new WorldSavingEvent(reason, ev.Revision, Store.WorldId ?? id, WorldFile, sync, ev.Step));
                CharacterSync.OnWorldSaving(reason);
                Store.Stage(ev.Revision, reason, late: false);
                break;
            }
            case SavesNative.EvSnapshot:
            {
                // Only final serialization: do not repeat WorldSaving handlers or overwrite a target after its snapshot.
                if (ev.SaveSeq == 0 || ev.SaveSeq != _savingSeq || ev.SaveSeq == _snapshotSeq || ev.Revision != _savingRevision) break;
                _snapshotSeq = ev.SaveSeq;
                Store.Stage(ev.Revision, reason, late: false, snapshot: true);
                break;
            }
            case SavesNative.EvWritten:
            {
                _worldFile = null;
                if (id != null) Store.AdoptWorld(id);
                Store.Commit(ev.Revision, ev.WorldUnix, reason);
                var dur = TimeSpan.FromMilliseconds(ev.DurationMs);
                CompleteRequests(ev, true, null);
                if (Events.HasSubscribers<WorldSavedEvent>())
                    Events.Post(new WorldSavedEvent(reason, true, ev.Revision, Store.WorldId, WorldFile, dur, 0, false));
                break;
            }
            case SavesNative.EvFailed:
            {
                var timeout = (ev.Flags & SavesNative.FlagTimeout) != 0;
                if (timeout) CompleteRequests(ev, false, "the game did not accept the save request within 60 s");
                Log(LogLevel.Warn, $"Kiln.Saves: save {(timeout ? "request timed out" : $"write failed (result {ev.Result})")} ({Saves.Text(reason)})");
                if (Events.HasSubscribers<WorldSavedEvent>())
                    Events.Post(new WorldSavedEvent(reason, false, ev.Revision, Store.WorldId, WorldFile,
                        TimeSpan.FromMilliseconds(ev.DurationMs), (int)ev.Result, timeout));
                break;
            }
            case SavesNative.EvLoaded:
            {
                _worldFile = null;
                _loadedEventSeen = true;
                var newWorld = (ev.Flags & SavesNative.FlagNewWorld) != 0;
                Store.OnWorldLoaded(id, ev.Revision, ev.WorldUnix, newWorld);
                InstanceCapabilities.Refresh();
                if (Events.HasSubscribers<WorldLoadedEvent>())
                    Events.Post(new WorldLoadedEvent(Store.WorldId ?? id, ev.Revision, newWorld, WorldFile, Store.Match));
                break;
            }
        }
    }

    internal static bool WorldLoaded => _loadedEventSeen;

    private static void CompleteRequests(in SavesNative.Event ev, bool success, string? error)
    {
        List<Pending> done;
        var saveSeq = ev.SaveSeq;
        var kind = ev.Kind;
        var reason = (SaveReason)ev.Reason;
        lock (Gate)
        {
            // Written: every request made before the save was accepted. Timeout: the requests of that reason.
            done = Requests.Where(p => kind == SavesNative.EvWritten ? saveSeq > p.AfterSeq : p.Reason == reason).ToList();
            foreach (var p in done) Requests.Remove(p);
        }
        foreach (var p in done)
        {
            var outer = ModContext.CurrentMod;
            ModContext.CurrentMod = p.Mod;
            try
            {
                if (p.Save != null)
                    p.Save.TrySetResult(new SaveResult(success, (SaveReason)ev.Reason, ev.Revision, TimeSpan.FromMilliseconds(ev.DurationMs), error));
                if (p.Backup != null)
                {
                    if (!success) p.Backup.TrySetException(new InvalidOperationException($"backup '{p.BackupName}' failed: {error}"));
                    else
                    {
                        try
                        {
                            p.Backup.TrySetResult(SavesBackups.Create(p.BackupName!, ev.Revision, ev.WorldUnix, (SaveReason)ev.Reason));
                        }
                        catch (Exception e)
                        {
                            Log(LogLevel.Error, $"Kiln.Saves: backup '{p.BackupName}' failed: {e.Message}");
                            p.Backup.TrySetException(e);
                        }
                    }
                }
            }
            finally
            {
                ModContext.CurrentMod = outer;
            }
        }
    }
}
