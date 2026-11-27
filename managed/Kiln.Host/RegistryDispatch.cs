// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace Kiln.Host;

internal sealed class RegistryDispatch(ModManager mods) : IDisposable
{
    private readonly RegistryNative _native = new(mods.Host);
    private readonly List<Author> _authors = [];
    private readonly List<Runtime> _runtime = [];
    private readonly Dictionary<ulong, HashSet<ulong>> _changes = [];
    private readonly Dictionary<ulong, (ulong Owner, ulong World)> _scopedChanges = [];
    private ulong _cleanupWorld = ulong.MaxValue;
    private readonly Dictionary<ModContext, Action> _retirements = [];
    private volatile bool _closed;
    private readonly object _signals = new();
    private readonly List<(RegistryReadyPhase Phase, ulong Generation)> _pendingReady = []; // in arrival order (review4 nit)
    private bool _worldChanged;
    private string? _worldDirectory;
    private IDisposable? _ready;
    private bool _dispatchingWorld, _starting, _flushing;
    private readonly RegistryWorldBinding _worldBinding = new();
    // The code each GameStart author ran with at this start (its folder's DLL hash), and the reloaded mods whose GameStart
    // edits wait for the next start: the reload reply names them (ReloadReplies).
    private readonly Dictionary<string, Func<string>> _gameStartCode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _restartNotes = new(StringComparer.Ordinal);
    private sealed class Author(Mod instance, ModContext context, AssemblyLoadContext assembly, Func<string> code)
    {
        internal readonly Mod Instance = instance;
        internal readonly ModContext Context = context;
        internal readonly AssemblyLoadContext Assembly = assembly;
        internal readonly Func<string> Code = code;
        internal readonly Dictionary<string, ConfigFile> Inputs = new(StringComparer.Ordinal);
        internal IDisposable? Subscription;
    }
    private sealed class Runtime(Mod instance, ModContext context, RegistryEntryPoint points, Func<string> code, bool reload)
    {
        internal readonly Mod Instance = instance;
        internal readonly ModContext Context = context;
        internal readonly RegistryEntryPoint Points = points;
        internal readonly Func<string> Code = code;
        internal readonly bool Reload = reload;
        internal bool Pending = true, Faulted;
        internal ulong World;
        // A replacing HotReload pass that read per-world settings is world-scoped; it runs again once the next world is
        // bound (review4 NS3). 0: nothing to replay.
        internal ulong ReplayWorld;
        // review4 NS4: a transient KilnUnavailableException (live install window, budgets, lazy preparation) defers this
        // record's point to a later tick: the next tick first, then backing off to 5 s, logged once per streak.
        internal long RetryAt;
        internal int Retries;
        // This hot-reloaded record's HotReload pass stands in for its ModLoad point.
        internal bool ReplacesModLoad => Reload && (Points & RegistryEntryPoint.ModLoad) != 0 && (Points & RegistryEntryPoint.HotReload) != 0;
        internal string? WorldId;
        internal readonly HashSet<ulong> WorldChanges = [];
        // Registry.Reauthor(WorldLoad): author the WorldLoad point again in this world on the next tick (TryWorld)
        internal bool ReauthorWorld;
        // dev.32: the pass called a tick-thread API off the tick (Game.OffTickRefused): its re-authors run their pass on the tick
        internal bool PassOnTick;
        // re-authors in a row whose turn the registry moved past before it landed (a later one runs whole on the tick)
        internal int StaleReauthors;
    }
    internal void Prepare(ulong hostOwner)
    {
        RegistryRuntime.Host = this;
        BootFingerprint.RegistrySource = _native.Fingerprint;
        RegistryRuntime.Available = () => _native.LiveAvailable;
        RegistryRuntime.Manual = Manual;
        RegistryRuntime.Revert = (mod, handle) =>
        {
            var result = _native.Revert(mod.Owner, handle); ForgetChange(mod, handle); return result;
        };
        RegistryRuntime.Read = Read;
        RegistryRuntime.Reauthor = Reauthor;
        RegistryRuntime.WorldChanged = directory =>
        {
            lock (_signals) { _worldDirectory = directory; _worldChanged = true; }
        };
        if (!_native.Available) return;
        _starting = _native.StageStatus().Phase < 1;
        _ready = _native.Subscribe(hostOwner, true, (phase, generation) => Ready((RegistryReadyPhase)phase, generation), e => Log("ready callback", e));
        if (!_starting) return; // a late Host may read/live-edit, but cannot retroactively enter GameStart
        var candidates = mods.RegistryCandidates();
        var directories = candidates.Select(c => c.Directory).ToArray();
        foreach (var (manifest, directory) in candidates)
        {
            Author? author = null;
            try
            {
                if (!manifest.HasCode || !RegistryDeclarations.MightOverride(Path.Combine(directory, manifest.EntryAssembly), directories)) continue;
                var code = Code(directory);
                var loaded = mods.CreateRegistryAuthor(manifest, directory, candidates);
                if (loaded is null) continue;
                _gameStartCode[manifest.Id] = code;
                var (instance, context, assembly) = loaded.Value;
                author = new Author(instance, context, assembly, code);
                _authors.Add(author);
                author.Subscription = _native.Subscribe(context.Owner, false, (phase, _) => Stage(author, (RegistryPhase)phase), e => Retire(author, e));
            }
            catch (Exception e)
            {
                if (author is not null) Retire(author);
                Log(manifest.Id + " game-start author", e);
            }
        }
    }
    internal void Loaded(Mod instance, ModContext context, string directory, bool reload)
    {
        try
        {
            if (_closed || !context.Alive || _retirements.ContainsKey(context)) return;
            Runtime? record = null;
            Action retirement = () =>
            {
                // Still before core OwnerRevoke. Also covers manual changes if author setup fails below.
                RemoveChanges(context);
                if (record is not null) _runtime.Remove(record);
                _retirements.Remove(context);
            };
            _retirements.Add(context, retirement);
            context.Retiring += retirement;
            var points = RegistryDeclarations.PointsOf(instance.GetType());
            if (points != 0)
            {
                record = new Runtime(instance, context, points, Code(directory), reload);
                _runtime.Add(record);
            }
            if (reload) NoteGameStart(context.Info.Id, points, record?.Code);
        }
        catch (Exception e) { Log(context.Info.Id + " runtime author setup", e); }
    }
    internal void FlushLoads()
    {
        Game.AssertTickThread();
        if (_closed || _flushing) return;
        DrainSignals();
        // review4 nit: with no runtime author and no world-bound manual change there is nothing to poll on this tick (the
        // stage/store locks and Kiln.Saves' world folder stay untouched); a later Loaded record polls again.
        if (_runtime.Count == 0 && _scopedChanges.Count == 0) return;
        if (!_native.LiveAvailable || _native.StageStatus().Phase < 4) return; // a menu load can precede resources-ready
        _flushing = true;
        try
        {
            var now = Environment.TickCount64;
            var pending = _runtime.Where(r => r.Pending && r.Context.Alive && r.RetryAt <= now).ToArray();
            foreach (var record in pending) record.Pending = false;
            var retry = Apply(pending, RegistryEntryPoint.ModLoad, static r => (r.Points & RegistryEntryPoint.ModLoad) != 0 &&
                (!r.Reload || (r.Points & RegistryEntryPoint.HotReload) == 0));
            retry.UnionWith(Apply(pending, RegistryEntryPoint.HotReload, static r => r.Reload && (r.Points & RegistryEntryPoint.HotReload) != 0));
            foreach (var record in retry) record.Pending = true;
        }
        finally { _flushing = false; }
        TryWorld();
    }
    // A reloaded mod's GameStart edits run again only at the next start ("Already committed GameStart layers stay until process
    // restart, including through a runtime hot reload", registries/README.md): when its code changed (or it was no GameStart
    // author at this start) and no runtime point of its own re-runs OnRegistry, say so, rather than leave the new code with the
    // old edits silently.
    private void NoteGameStart(string id, RegistryEntryPoint points, Func<string>? code)
    {
        _restartNotes.Remove(id);
        if ((points & RegistryEntryPoint.GameStart) == 0 || (points & (RegistryEntryPoint.ModLoad | RegistryEntryPoint.HotReload)) != 0 || code is null) return;
        // the code identities are computed here only (a reload of a GameStart-only author), off the mod-load path (review4 nit)
        var ran = _gameStartCode.TryGetValue(id, out var was);
        if (ran && was!() == code()) return;
        var why = ran ? "the reloaded code" : "it was no GameStart author at this start: its code";
        _restartNotes[id] = $"RestartRequired: {id}'s OnRegistry runs at GameStart only ([RegistryEntryPoints] has no HotReload or ModLoad): " +
                            $"{why} changes its registry edits at the next start; until then the start's edits stay";
        mods.Host.Log(LogLevel.Warn, $"registry: {_restartNotes[id]}");
    }
    /// <summary>What a reload of <paramref name="mods"/> left for the next start outside registry.json: GameStart edits whose
    /// code changed (NoteGameStart). (mod, reason) pairs.</summary>
    internal IReadOnlyList<(string Mod, string Reason)> RestartNotes(IReadOnlyCollection<string> mods) =>
        _restartNotes.Where(n => mods.Contains(n.Key)).Select(n => (n.Key, n.Value)).ToList();
    /// <summary>Whether a runtime pass of one of <paramref name="mods"/> still waits (its ModLoad / HotReload point after a
    /// load or reload): the reload reply waits for it (ReloadReplies).</summary>
    internal bool PassPending(IReadOnlyCollection<string> mods) =>
        !_closed && _runtime.Exists(r => r.Pending && r.Context.Alive && mods.Contains(r.Context.Info.Id));
    /// <summary>registry.json's text as kiln.registry has it now; null without the registry.</summary>
    internal string? ReportJson() => _native.Available ? _native.ReportJson() : null;
    internal bool Available => !_closed && _native.Available;
    // review4 nit: the code identity (a hash of the mod folder's DLLs) is computed at the first RegistryContext.Identity
    // read, not when a mod loads on the tick thread. At load only the folder's DLL names are listed (metadata: a folder
    // that is gone still fails there, contained by Loaded); each DLL's content hash is kept by path, size and write time,
    // so a reload or a second context of unchanged files reads nothing again.
    private static readonly Dictionary<(string Path, long Length, long Written), byte[]> FileHashes = new();
    private static readonly Dictionary<string, int> FileReads = new(StringComparer.Ordinal);
    internal static int FileHashReads(string file) { lock (FileHashes) return FileReads.GetValueOrDefault(Path.GetFullPath(file)); }  // tests
    internal static Func<string> Code(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*.dll").Order(StringComparer.Ordinal).ToArray();
        string? identity = null;
        return () => identity ??= CodeIdentity(files);
    }
    private static string CodeIdentity(string[] files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(file)));
            hash.AppendData(FileHash(file));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private static byte[] FileHash(string file)
    {
        try
        {
            var info = new FileInfo(file);
            var key = (Path.GetFullPath(file), info.Length, info.LastWriteTimeUtc.Ticks);
            lock (FileHashes)
            {
                if (FileHashes.TryGetValue(key, out var known)) return known;
            }
            byte[] sum;
            using (var input = File.OpenRead(file)) sum = SHA256.HashData(input);
            lock (FileHashes)
            {
                FileHashes[key] = sum; FileReads[key.Item1] = FileReads.GetValueOrDefault(key.Item1) + 1;
            }
            return sum;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // gone or locked since the mod loaded: named as such, so Identity still answers (and differs from the file's)
            return System.Text.Encoding.UTF8.GetBytes("unreadable");
        }
    }
    private RegistryWorld? World(bool bound = true)
    {
        var status = _native.Status();
        if (status.World == 0) return null;
        var directory = WorldConfigs.Current();
        var world = new RegistryWorld(status.World, (WorldKind)status.Kind, directory is null ? null : Path.GetFileName(directory));
        if (!bound) return world;
        // review4 recheck RM2: bind here too, not only in TryWorld's polling, so a mod that only uses Registry.Change or
        // Registry.Read (no runtime author, so FlushLoads skips polling) still gets the bound world id and its settings.
        lock (_signals) { if (_worldChanged) { _worldBinding.Signal(_worldDirectory); _worldChanged = false; } }
        return _worldBinding.Matches(world) || _worldBinding.Bind(world) ? world : world with { Id = null };
    }
    // offTick: a re-authored pass on a worker thread (dev.32) reads in that world only (kiln.internal.registry v3 read_pass_in)
    private RegistryContext Context(ModContext owner, RegistryEntryPoint point, RegistryPhase phase, RegistryWorld? world,
        Func<string> code, bool stage, Dictionary<string, ConfigFile> inputs, bool readOnly = false, bool pass = false, ulong offTick = 0)
    {
        var nativeOwner = owner.Owner; // transport delegates must not retain a collectible mod context
        return new(owner, point, phase, world, code,
            offTick != 0 ? (registry, id, field) => _native.ReadPassIn(nativeOwner, offTick, registry, id, field)
            : pass ? (registry, id, field) => _native.ReadPass(nativeOwner, registry, id, field)
                 : (registry, id, field) => _native.Read(nativeOwner, stage, registry, id, field),
            (registry, pattern) => _native.Select(stage, registry, pattern), inputs)
            { ReadOnly = readOnly, CurrentWorldToken = () => _native.Status().World, CurrentStep = static () => ServiceTick.Step };
    }
    private void Stage(Author author, RegistryPhase phase)
    {
        if (!author.Context.Alive) return;
        var context = Context(author.Context, RegistryEntryPoint.GameStart, phase, null, author.Code, true, author.Inputs);
        using var scope = context.Enter();
        try
        {
            author.Instance.OnRegistry(context);
            var ir = context.CaptureIr();
            context.Close();
            if (ir is not null) _native.SubmitStage(author.Context.Owner, ir);
        }
        catch (Exception e) { context.Close(); Retire(author, e); }
    }
    private void Retire(Author author, Exception? error = null)
    {
        if (error is not null) Log(author.Context.Info.Id + " OnRegistry", error);
        author.Subscription?.Dispose(); author.Subscription = null;
        author.Context.Retire(); mods.Host.OwnerRevoke(author.Context.Owner);
        author.Inputs.Clear(); _authors.Remove(author); author.Assembly.Unload();
    }
    private void Ready(RegistryReadyPhase phase, ulong generation)
    {
        // Native readiness can arrive on a resource thread. Only the tick owns runtime records and author retirement.
        lock (_signals)
        {
            if (_closed) return;
            if (_pendingReady.Count == 16) _pendingReady.RemoveAt(1); // bounded; the first (START) and the newest survive
            _pendingReady.Add((phase, generation));
        }
    }
    private void DrainSignals()
    {
        (RegistryReadyPhase Phase, ulong Generation)[] ready;
        lock (_signals)
        {
            ready = [.. _pendingReady]; _pendingReady.Clear();
            if (_worldChanged) { _worldBinding.Signal(_worldDirectory); _worldChanged = false; }
        }
        // Every event since the last tick, in order: a Registry.Ready subscriber sees START even when WORLDLOAD followed it.
        foreach (var snapshot in ready)
        {
            if (_starting && (uint)snapshot.Phase >= 4)
            {
                _starting = false;
                foreach (var author in _authors.ToArray().Reverse()) Retire(author);
            }
            RegistryRuntime.PublishReady(snapshot.Phase, snapshot.Generation, _native.LiveAvailable ? World() : null);
        }
    }
    private void TryWorld()
    {
        Game.AssertTickThread();
        if (_dispatchingWorld || _flushing || !_native.LiveAvailable || _native.StageStatus().Phase < 4) return;
        _dispatchingWorld = true;
        try
        {
            var candidate = World(bound: false);
            PruneWorldChanges(candidate?.Token ?? 0);
            if (candidate is null) return;
            var bound = _worldBinding.Bind(candidate);
            var world = bound ? candidate.Value : candidate.Value with { Id = null };
            var now = Environment.TickCount64;
            if (bound)
            {
                // A replacing HotReload pass that read the previous world's settings expired with that world: author it again
                // with this world's settings, as its ModLoad point would have persisted (review4 NS3).
                var replays = _runtime.Where(r => !r.Pending && r.Context.Alive && r.ReplayWorld != 0 && r.ReplayWorld != world.Token &&
                    r.RetryAt <= now).ToArray();
                var previous = replays.ToDictionary(r => r, r => r.ReplayWorld);
                foreach (var record in replays) record.ReplayWorld = 0;
                foreach (var record in Apply(replays, RegistryEntryPoint.HotReload, static _ => true)) record.ReplayWorld = previous[record];
            }
            var records = _runtime.Where(r => !r.Pending && r.Context.Alive && (r.Points & RegistryEntryPoint.WorldLoad) != 0 &&
                r.RetryAt <= now && (r.World != world.Token || (bound && r.WorldId is null && world.Id is not null))).ToArray();
            // a re-authored pass of the world before may still run on the worker: it ends before the same mods' new world pass
            // runs (a world load, so the tick may wait), never beside it; its turn is dropped when it lands (ReauthorStep)
            if (records.Length > 0 && _reauthor is { Work: { IsCompleted: false } work } running && running.Records.Any(records.Contains))
                work.Wait(TimeSpan.FromSeconds(10));
            foreach (var record in records)
            {
                // Adoption reauthors this point with its settings, rather than stacking it on the id-less turn.
                foreach (var handle in record.WorldChanges.ToArray())
                    try
                    {
                        var result = _native.Revert(record.Context.Owner, handle);
                        if (result.Outcome is 6 or 7) Applied(record.Context, result);
                        ForgetChange(record.Context, handle); record.WorldChanges.Remove(handle);
                    }
                    catch (Exception e) { Log(record.Context.Info.Id + " previous world point cleanup", e); }
                record.World = world.Token; record.WorldId = world.Id; // our own notifications cannot recurse
            }
            // A transiently unavailable point authors this world again on a later tick, from scratch.
            foreach (var record in Apply(records, RegistryEntryPoint.WorldLoad, static _ => true)) { record.World = 0; record.WorldId = null; }
            foreach (var record in records) record.ReauthorWorld = false; // just authored for this world
            // Registry.Reauthor: the point again in the same world. With kiln-content's v3 the pass runs on a worker thread and its
            // turn lands on the tick in one step (ReauthorStep); else, as before, here: the new pass commits first (its reads leave
            // the mod's own layers out, so it derives from what lies below them), then the layers the point made before are taken
            // back: the game never sees a moment without the mod's values. A pass that faults keeps the earlier layers; a transient
            // refusal asks again on a later tick.
            if (_native.OffTickAvailable)
            {
                ReauthorStep(world, now);
                return;
            }
            var again = _runtime.Where(r => r.ReauthorWorld && !r.Pending && r.Context.Alive && (r.Points & RegistryEntryPoint.WorldLoad) != 0 &&
                r.World == world.Token && r.RetryAt <= now).ToArray();
            if (again.Length > 0)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew(); // the pass runs on the tick (the world thread): its time is the hitch
                var earlier = again.ToDictionary(r => r, r => r.WorldChanges.ToArray());
                foreach (var record in again) record.ReauthorWorld = false;
                var retry = Apply(again, RegistryEntryPoint.WorldLoad, static _ => true);
                foreach (var record in again)
                {
                    if (retry.Contains(record)) { record.ReauthorWorld = true; continue; }
                    if (record.Faulted || !record.Context.Alive) continue;
                    foreach (var handle in earlier[record])
                        try
                        {
                            var result = _native.Revert(record.Context.Owner, handle);
                            if (result.Outcome is 6 or 7) Applied(record.Context, result);
                            ForgetChange(record.Context, handle); record.WorldChanges.Remove(handle);
                        }
                        catch (Exception e) { Log(record.Context.Info.Id + " re-authored point cleanup", e); }
                }
                mods.Host.Log(LogLevel.Info, $"registry: WorldLoad authored again on request (Registry.Reauthor) for " +
                    $"{string.Join(", ", again.Select(r => r.Context.Info.Id))}: {clock.Elapsed.TotalMilliseconds:F1} ms on the tick");
            }
        }
        finally { _dispatchingWorld = false; }
    }
    // ---- Registry.Reauthor off the world tick (dev.32 LOOT-2, kiln.internal.registry v3) ----
    // A re-author is one job at a time: the mods that asked since the last one, their passes run together (every callback reads
    // the pre-pass values, the calls commit Adds, then Alters, then Finals, in load order) on a worker thread of Kiln.Host's,
    // which also has kiln-content prepare the turn (parse, compose, bind, evaluate); the tick only lands it (the changes and
    // layers swap in, one write of the bytes that change), and registry.json is built off the tick after. Requests made while a
    // job runs make one more job when it ends (coalescing: a burst of requests is at most two passes). The pass reads the registry
    // as it is when it starts; if the registry moved before its turn lands (another mod's change, a data pack, a write below), the
    // pass runs again; three times in a row, and the next one runs whole on the tick. A mod whose pass calls a tick-thread API
    // (refused off the tick) has its passes run on the tick from then on; Kiln still prepares their turns off it.
    private sealed class ReauthorJob
    {
        internal required Runtime[] Records;            // in load order
        internal required RegistryWorld World;
        internal required Dictionary<Runtime, ulong[]> Earlier;  // each record's layers of this world's point, taken out by the turn
        internal bool OnTick;                            // the passes run on the tick (a mod bound to it, or stale three times)
        internal ulong Epoch;
        internal readonly Dictionary<Runtime, Dictionary<string, ConfigFile>> Inputs = [];
        internal readonly List<(Runtime Record, string Ir)> Calls = [];  // in commit order
        internal readonly Dictionary<Runtime, Exception> Faults = [];
        internal readonly Dictionary<Runtime, string> TickOnly = [];     // a tick-thread member the pass called off the tick
        internal Exception? Transport;  // Kiln's own failure (the world's status, a native call), not the author's
        internal List<(Runtime Record, ulong Handle)> Replaced = [];
        internal List<(Runtime Record, string Ir)> Prepared = [];  // the calls the turn was prepared with, in its order
        internal ulong Ticket;
        internal int Rc;
        internal uint Stale;
        internal int Prepares;
        internal Task? Work;
        internal double PassMs, PrepareMs;
    }
    private ReauthorJob? _reauthor;
    /// <summary>Tests: the re-author job's worker, if one runs.</summary>
    internal Task? ReauthorWork => _reauthor?.Work;
    private void ReauthorStep(RegistryWorld world, long now)
    {
        if (_reauthor is { } job)
        {
            if (job.Work is { IsCompleted: false }) return;  // the worker is still at it; the tick goes on
            _reauthor = null;
            Land(job, world);
            now = Environment.TickCount64;  // requests made meanwhile start the next job now
        }
        var again = _runtime.Where(r => r.ReauthorWorld && !r.Pending && r.Context.Alive && (r.Points & RegistryEntryPoint.WorldLoad) != 0 &&
            r.World == world.Token && r.RetryAt <= now).ToArray();
        if (again.Length == 0) return;
        foreach (var record in again) record.ReauthorWorld = false;
        var next = new ReauthorJob { Records = again, World = world, Earlier = again.ToDictionary(r => r, r => r.WorldChanges.ToArray()) };
        next.OnTick = again.Any(r => r.PassOnTick || r.StaleReauthors >= 3);
        if (again.Any(r => r.StaleReauthors >= 3))
        {
            // the registry moved under three turns in a row: this one runs whole on the tick (pass, preparation, landing in
            // one step), so nothing can move under it and it lands
            Guarded(next, () => { RunPass(next); PrepareTurn(next); });
            Land(next, world);
            return;
        }
        _reauthor = next;
        // the pass on the tick when its mod calls tick-thread APIs or the registry kept moving; Kiln's work off it either way
        if (next.OnTick) Guarded(next, () => RunPass(next));
        next.Work = Task.Factory.StartNew(() => Guarded(next, () => { if (!next.OnTick) RunPass(next); PrepareTurn(next); }),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    private static void Guarded(ReauthorJob job, Action work)
    {
        try { work(); }
        catch (Exception e) { job.Transport ??= e; }  // Kiln's own failure: Land retries later
    }
    // The passes of the job's records, phase by phase in load order (Apply's order), on this thread: the tick's or the worker's.
    private void RunPass(ReauthorJob job)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var offTick = !job.OnTick;
        try { job.Epoch = _native.Epoch(); }
        catch (Exception e) { job.Transport = e; return; }
        foreach (var record in job.Records) job.Inputs[record] = new Dictionary<string, ConfigFile>(StringComparer.Ordinal);
        foreach (var phase in new[] { RegistryPhase.Add, RegistryPhase.Alter, RegistryPhase.Final })
            foreach (var record in job.Records)
            {
                if (!record.Context.Alive || job.Faults.ContainsKey(record) || job.TickOnly.ContainsKey(record)) continue;
                RegistryContext context;
                try { context = Context(record.Context, RegistryEntryPoint.WorldLoad, phase, job.World, record.Code, false, job.Inputs[record], pass: true, offTick: offTick ? job.World.Token : 0); }
                catch (Exception e) { job.Transport = e; return; }  // Kiln's world/status query, not the author
                var outer = Game.OffTickPass;
                Game.OffTickPass = offTick; Game.OffTickRefused = null;
                try
                {
                    using var scope = context.Enter();
                    record.Instance.OnRegistry(context);
                    var ir = context.CaptureIr();
                    context.Close();
                    if (ir is not null) job.Calls.Add((record, ir));
                }
                catch (Exception e) { job.Faults[record] = e; }  // the author's code: a fault, whatever the exception
                finally
                {
                    context.Close();
                    if (offTick && Game.OffTickRefused is { } member) job.TickOnly[record] = member;
                    Game.OffTickPass = outer; Game.OffTickRefused = null;
                }
            }
        job.PassMs = clock.Elapsed.TotalMilliseconds;
    }
    // Has kiln-content prepare the job's turn (off the tick): its records' calls in, their earlier layers out. Faulted records
    // and records bound to the tick keep theirs and add nothing.
    private void PrepareTurn(ReauthorJob job)
    {
        if (job.Transport is not null || job.TickOnly.Count > 0) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        job.Prepared = job.Calls.Where(c => !job.Faults.ContainsKey(c.Record) && c.Record.Context.Alive).ToList();
        var calls = job.Prepared.Select(c => (c.Record.Context.Owner, c.Ir)).ToList();
        job.Replaced = job.Records.Where(r => !job.Faults.ContainsKey(r) && r.Context.Alive).SelectMany(r => job.Earlier[r].Select(h => (Record: r, Handle: h))).ToList();
        if (calls.Count == 0 && job.Replaced.Count == 0) return;
        try
        {
            for (job.Prepares = 1; ; ++job.Prepares)
            {
                (job.Rc, job.Ticket, job.Stale) = _native.Prepare(calls, job.Replaced.Select(x => (x.Record.Context.Owner, x.Handle)).ToList(), job.Epoch);
                if (job.Rc != KilnAbi.ErrUnavailable || job.Stale != 2 || job.Prepares >= 3) break;
            }
        }
        catch (Exception e) { job.Transport = e; }
        job.PrepareMs = clock.Elapsed.TotalMilliseconds;
    }
    // On the tick: the job's turn lands, or the job says why not and what comes next.
    private void Land(ReauthorJob job, RegistryWorld world)
    {
        var names = string.Join(", ", job.Records.Select(r => r.Context.Info.Id));
        void Again(IEnumerable<Runtime> records) { foreach (var r in records) if (r.Context.Alive) r.ReauthorWorld = true; }
        // another world since the job began: its turn is for a world that ended (this world's own pass is authored by TryWorld)
        if (job.World.Token != world.Token || job.Records.Any(r => r.World != job.World.Token))
        {
            if (job.Ticket != 0) _native.Discard(job.Ticket);
            return;
        }
        foreach (var (record, e) in job.Faults)
        {
            record.Faulted = true;
            Log(record.Context.Info.Id + " OnRegistry WorldLoad (Registry.Reauthor)", e);
        }
        if (job.TickOnly.Count > 0)
        {
            // results built around a refused tick-thread call are never landed: the job runs again, those mods' passes on the tick
            foreach (var (record, member) in job.TickOnly)
            {
                record.PassOnTick = true;
                mods.Host.Log(LogLevel.Warn, $"registry: {record.Context.Info.Id}'s re-authored pass calls {member}, which needs the tick thread: " +
                    "its passes run on the world tick from now on (slower; read game state before Registry.Reauthor and keep it in the mod to keep them off it)");
            }
            if (job.Ticket != 0) _native.Discard(job.Ticket);
            Again(job.Records.Where(r => !job.Faults.ContainsKey(r)));
            return;
        }
        if (job.Transport is null && job.Rc == KilnAbi.ErrInvalidArgument)
        {
            // the registry refused the pass's calls themselves (an IR it cannot read): the author's, as a refused submit is
            foreach (var record in job.Records.Where(r => !job.Faults.ContainsKey(r) && r.Context.Alive))
            {
                record.Faulted = true;
                Log(record.Context.Info.Id + " OnRegistry WorldLoad (Registry.Reauthor)", new ArgumentException("the registry refused the pass's edits as invalid"));
            }
            return;
        }
        if (job.Transport is not null || (job.Rc != 0 && !(job.Rc == KilnAbi.ErrUnavailable && job.Stale == 1)))
        {
            // Kiln's own state (no world yet, a budget, a native error): asked again later, backing off (Apply's NS4 rule)
            var e = job.Transport ?? new KilnUnavailableException("kiln.registry", $"preparing the re-authored turn returned {job.Rc}");
            foreach (var record in job.Records.Where(r => !job.Faults.ContainsKey(r) && r.Context.Alive))
            {
                if (record.Retries == 0) mods.Host.Log(LogLevel.Warn, $"registry: {record.Context.Info.Id} Registry.Reauthor: the registry cannot take the pass yet ({e.Message}); retrying on a later tick");
                if (record.Retries >= MaxRetries) { Log(record.Context.Info.Id + " Registry.Reauthor (after " + record.Retries + " tries)", e); record.Retries = 0; continue; }
                record.RetryAt = Environment.TickCount64 + (record.Retries == 0 ? 0 : Math.Min(5000, 100L << Math.Min(record.Retries, 6)));
                ++record.Retries; record.ReauthorWorld = true;
            }
            return;
        }
        if (job.Ticket == 0 && job.Rc == 0) return;  // nothing to write: no calls, no earlier layers
        var calls = job.Prepared;
        (int Rc, uint Stale, RegistryCompletion[] Results) landed = (job.Rc, job.Stale, Array.Empty<RegistryCompletion>());
        var tick = System.Diagnostics.Stopwatch.StartNew();
        if (job.Rc == 0)
            landed = _native.Install(job.Ticket, anyway: job.OnTick || job.Prepares >= 3, calls.Count, job.Replaced.Count);
        var landedMs = tick.Elapsed.TotalMilliseconds;
        if (landed.Rc == KilnAbi.ErrUnavailable)
        {
            // the registry moved before the turn landed: the pass runs again (its reads are stale), or only the preparation
            var stale = landed.Stale == 2 ? "its preparation is stale" : "the registry moved since the pass read it";
            foreach (var record in job.Records.Where(r => !job.Faults.ContainsKey(r))) record.StaleReauthors += landed.Stale == 1 ? 1 : 0;
            mods.Host.Log(LogLevel.Debug, $"registry: Registry.Reauthor for {names}: {stale}; the pass runs again");
            Again(job.Records.Where(r => !job.Faults.ContainsKey(r)));
            return;
        }
        if (landed.Rc != 0)
        {
            Log(names + " Registry.Reauthor turn", new KilnUnavailableException("kiln.registry.live", $"landing the re-authored turn returned {landed.Rc}"));
            return;
        }
        for (var i = 0; i < calls.Count; ++i)
        {
            var (record, _) = calls[i];
            var result = Applied(record.Context, landed.Results[i], worldScoped: true);
            if (Retained(result)) record.WorldChanges.Add(result.Handle);
        }
        for (var j = 0; j < job.Replaced.Count; ++j)
        {
            var (record, handle) = job.Replaced[j];
            var result = landed.Results[calls.Count + j];
            if (result.Outcome is 5 or 6 or 7)
            {
                if (result.Outcome is 6 or 7) Applied(record.Context, result);
                ForgetChange(record.Context, handle); record.WorldChanges.Remove(handle);
            }
        }
        foreach (var record in job.Records.Where(r => !job.Faults.ContainsKey(r)))
        {
            record.Faulted = false; record.StaleReauthors = 0; record.Retries = 0; record.RetryAt = 0;
        }
        mods.Host.Log(LogLevel.Info, $"registry: WorldLoad authored again on request (Registry.Reauthor) for {names}: " +
            (job.OnTick ? $"pass {job.PassMs:F1} ms on the tick, turn prepared off it in {job.PrepareMs:F1} ms"
                        : $"pass and turn prepared off the tick in {job.PassMs + job.PrepareMs:F1} ms") + $", landed in {landedMs:F1} ms on the tick");
        // registry.json off the tick (the tick builds it a few seconds later if this does not)
        _ = Task.Run(() => { try { _native.BuildReport(); } catch { /* the tick builds it */ } });
    }
    // Returns the records whose point met a transient KilnUnavailableException: the caller schedules them again.
    // review4 NS5 (coordinator ruling, Nov 23): a runtime pass behaves like GameStart's stage. Every callback of the pass reads
    // the same pre-pass values (the live values without the reading mod's own registry layers, so a read-derived assignment
    // restates its intent and never stacks), whatever the load order. The pass's edits are captured, then committed only
    // after every callback ran: all Adds, then all Alters, then all Finals, each in Kiln.Host load order (dependencies first).
    // Live commit order across passes still decides between passes.
    private HashSet<Runtime> Apply(IEnumerable<Runtime> records, RegistryEntryPoint point, Func<Runtime, bool> selected)
    {
        Game.AssertTickThread();
        var ordered = records.Where(r => selected(r) && r.Context.Alive).ToArray();
        var retry = new HashSet<Runtime>();
        var inputs = ordered.ToDictionary(r => r, _ => new Dictionary<string, ConfigFile>(StringComparer.Ordinal));
        var captured = ordered.ToDictionary(r => r, _ => new List<(RegistryPhase Phase, string Ir)>());
        // review4 recheck RS1: a record's pass has one lifetime. If any phase read per-world settings, every phase of it is
        // world-scoped, so a replay at the next bound world replaces its layers instead of stacking a process-lifetime Final.
        var worldSettings = ordered.ToDictionary(r => r, _ => false);
        var committed = ordered.ToDictionary(r => r, _ => new List<ulong>());
        foreach (var record in ordered) record.Faulted = false; // a fault closes this point, not every future world/point
        void Remember(Runtime record, RegistryEntryPoint native, RegistryCompletion result)
        {
            Applied(record.Context, result, native != RegistryEntryPoint.ModLoad);
            if (Retained(result))
            {
                committed[record].Add(result.Handle);
                if (point == RegistryEntryPoint.WorldLoad) record.WorldChanges.Add(result.Handle);
                if (point == RegistryEntryPoint.HotReload && native == RegistryEntryPoint.HotReload && record.ReplacesModLoad && result.World != 0)
                    record.ReplayWorld = result.World;
            }
        }
        // review4 NS3: a HotReload pass that replaces ModLoad keeps the process lifetime of the layer it replaces, unless it
        // read per-world settings (then it is world-scoped and replayed at the next bound world).
        static RegistryEntryPoint NativePoint(Runtime record, RegistryEntryPoint point, bool worldSettings) =>
            point == RegistryEntryPoint.HotReload && record.ReplacesModLoad && !worldSettings ? RegistryEntryPoint.ModLoad : point;
        // transport: Kiln's own begin/submit/commit or context setup failed, not the author's code (review4 recheck RM1)
        void Fault(Runtime record, Exception error, bool transport = false)
        {
            record.Faulted = true;
            captured[record].Clear();
            foreach (var handle in committed[record].AsEnumerable().Reverse())
                try
                {
                    Applied(record.Context, _native.Revert(record.Context.Owner, handle));
                    ForgetChange(record.Context, handle); record.WorldChanges.Remove(handle);
                }
                catch (Exception e) { Log(record.Context.Info.Id + " point rollback (owner cleanup will retry)", e); }
            committed[record].Clear();
            // Only Kiln's own transient states retry (review4 NS4): begin/commit refused during the live install window, a
            // budget others hold, a lazy base preparation that failed. An UNAVAILABLE read inside the author's OnRegistry (an
            // unmapped field, an entry without the config) is the author's fault: that pass faults once, logged (recheck RM1).
            // A transport state that stays unavailable is logged as a fault after MaxRetries tries, with its real error.
            if (transport && error is KilnUnavailableException && record.Retries < MaxRetries)
            {
                if (record.Retries == 0)
                    mods.Host.Log(LogLevel.Warn, $"registry: {record.Context.Info.Id} OnRegistry {point}: the registry cannot take the pass yet ({error.Message}); retrying on a later tick");
                record.RetryAt = Environment.TickCount64 + (record.Retries == 0 ? 0 : Math.Min(5000, 100L << Math.Min(record.Retries, 6)));
                ++record.Retries; retry.Add(record);
                return;
            }
            Log(record.Context.Info.Id + " OnRegistry " + point + (transport && record.Retries >= MaxRetries ? $" (after {record.Retries} tries)" : ""), error);
        }
        var phases = new[] { RegistryPhase.Add, RegistryPhase.Alter, RegistryPhase.Final };
        foreach (var phase in phases)
            foreach (var record in ordered)
            {
                if (!record.Context.Alive || record.Faulted) continue;
                RegistryContext context;
                try { context = Context(record.Context, point, phase, World(), record.Code, false, inputs[record], pass: true); }
                catch (Exception e) { Fault(record, e, transport: true); continue; } // Kiln's world/status query, not the author
                try
                {
                    using var scope = context.Enter();
                    record.Instance.OnRegistry(context);
                    var ir = context.CaptureIr(); worldSettings[record] |= context.ReadWorldSettings; context.Close();
                    if (ir is not null) captured[record].Add((phase, ir));
                }
                catch (Exception e) { Fault(record, e); } // the author's code: a fault, whatever the exception
                finally { context.Close(); }
            }
        // Every callback has read the pre-pass view; the pass's edits land now, phase by phase, in load order.
        foreach (var phase in phases)
            foreach (var record in ordered)
            {
                if (!record.Context.Alive || record.Faulted) continue;
                var native = NativePoint(record, point, worldSettings[record]);
                foreach (var (_, ir) in captured[record].Where(x => x.Phase == phase).ToArray())
                {
                    ulong handle = 0;
                    try
                    {
                        handle = _native.Begin(record.Context.Owner, native);
                        _native.Submit(record.Context.Owner, handle, ir);
                        var result = _native.Commit(record.Context.Owner, handle); handle = 0;
                        Remember(record, native, result);
                    }
                    catch (Exception e)
                    {
                        if (handle != 0) try { _native.Revert(record.Context.Owner, handle); } catch { }
                        Fault(record, e, transport: true); break;
                    }
                }
            }
        foreach (var record in ordered)
            if (!retry.Contains(record)) { record.Retries = 0; record.RetryAt = 0; }
        return retry;
    }
    private const int MaxRetries = 10; // about 20 s of backoff (0, 100 ms ... 5 s) before a transport fault is logged
    private static bool Retained(RegistryCompletion result) => result.Outcome is 0 or 1 or 8 && result.Accepted != 0 && result.Handle != 0;
    private RegistryCompletion Applied(ModContext owner, RegistryCompletion result, bool worldScoped = false)
    {
        if (result.Outcome == 4 && result.Handle != 0)
        {
            _native.Revert(owner.Owner, result.Handle); // a world race leaves an unordered native draft, not a retained change
            result = result with { Handle = 0 };
        }
        if (Retained(result))
        {
            if (!_changes.TryGetValue(owner.Owner, out var handles)) _changes[owner.Owner] = handles = [];
            handles.Add(result.Handle);
            if (worldScoped) _scopedChanges[result.Handle] = (owner.Owner, result.World);
        }
        if (result.Refused != 0 || result.Outcome is 2 or 3 or 4 or 6 or 7)
            mods.Host.Log(LogLevel.Warn, $"registry: {owner.Info.Id}: outcome {result.Outcome}, {result.Accepted} accepted, {result.Refused} refused: {result.Reason}");
        return result;
    }
    // Registry.Reauthor: null when the point is queued, else the reason (the API logs it in the mod's name).
    private string? Reauthor(ModContext owner, RegistryEntryPoint point)
    {
        Game.AssertTickThread();
        if (point != RegistryEntryPoint.WorldLoad) return $"only the WorldLoad point is authored again on request, not {point}";
        if (_closed || !_native.LiveAvailable) return "the registry's live back-end is not available";
        var record = _runtime.FirstOrDefault(r => ReferenceEquals(r.Context, owner));
        if (record is null || (record.Points & RegistryEntryPoint.WorldLoad) == 0)
            return "the mod declares no WorldLoad point ([RegistryEntryPoints(RegistryEntryPoint.WorldLoad)] on its Mod class with an OnRegistry override)";
        record.ReauthorWorld = true;
        return null;
    }
    private RegistryCompletion Manual(ModContext owner, Action<RegistryContext> action)
    {
        Game.AssertTickThread();
        if (!_native.LiveAvailable) throw new KilnUnavailableException("kiln.registry.live");
        var world = World();
        if (world is null) return new(4, 0, 0, 0, 0, 0, 0, "a world is required");
        var code = "manual:" + owner.Info.Version; // a string, not the owner: the context must not hold the mod
        var context = Context(owner, RegistryEntryPoint.Manual, RegistryPhase.Alter, world, () => code, false, new());
        using var scope = context.Enter(); ulong handle = 0;
        try
        {
            action(context); var ir = context.CaptureIr(); context.Close();
            if (ir is null) return new(2, 0, 0, 0, world.Value.Token, 0, 0, "the transaction has no edits");
            handle = _native.Begin(owner.Owner, RegistryEntryPoint.Manual); _native.Submit(owner.Owner, handle, ir);
            var result = Applied(owner, _native.Commit(owner.Owner, handle), worldScoped: true); handle = 0; return result;
        }
        finally { context.Close(); if (handle != 0) try { _native.Revert(owner.Owner, handle); } catch { } }
    }
    private void Read(ModContext owner, Action<RegistryContext> action)
    {
        Game.AssertTickThread();
        if (!_native.LiveAvailable || _native.StageStatus().Phase < 4) throw new KilnUnavailableException("kiln.registry");
        var context = Context(owner, RegistryEntryPoint.Manual, RegistryPhase.Final, World(), static () => "resolved", false, new(), readOnly: true);
        using var scope = context.Enter();
        try { action(context); } finally { context.Close(); }
    }
    private void PruneWorldChanges(ulong world)
    {
        if (_cleanupWorld == world) return;
        _cleanupWorld = world;
        foreach (var (handle, scope) in _scopedChanges.Where(x => x.Value.World != world).ToArray())
            ForgetChange(scope.Owner, handle); // native already expired these handles; never replay an old snapshot
    }
    private void ForgetChange(ModContext owner, ulong handle) => ForgetChange(owner.Owner, handle);
    private void ForgetChange(ulong owner, ulong handle)
    {
        _scopedChanges.Remove(handle);
        foreach (var record in _runtime.Where(r => r.Context.Owner == owner)) record.WorldChanges.Remove(handle);
        if (!_changes.TryGetValue(owner, out var handles)) return;
        handles.Remove(handle);
        if (handles.Count == 0) _changes.Remove(owner);
    }
    private void RemoveChanges(ModContext owner)
    {
        if (!_changes.Remove(owner.Owner, out var handles)) return;
        foreach (var handle in handles)
            try { _native.Revert(owner.Owner, handle); }
            catch (Exception e) { Log(owner.Info.Id + " cleanup (core retirement will retry)", e); }
            finally { _scopedChanges.Remove(handle); }
    }
    public void Dispose()
    {
        if (_closed) return;
        _closed = true; _ready?.Dispose(); _ready = null;
        foreach (var author in _authors.ToArray().Reverse()) Retire(author);
        foreach (var (owner, retirement) in _retirements)
        {
            owner.Retiring -= retirement;
            RemoveChanges(owner);
        }
        _retirements.Clear(); _runtime.Clear(); _changes.Clear(); _scopedChanges.Clear();
        if (ReferenceEquals(RegistryRuntime.Host, this))
        {
            BootFingerprint.RegistrySource = null;
            RegistryRuntime.Host = null; RegistryRuntime.Available = null; RegistryRuntime.Manual = null;
            RegistryRuntime.Revert = null; RegistryRuntime.Read = null; RegistryRuntime.Reauthor = null; RegistryRuntime.WorldChanged = null;
            RegistryRuntime.LastReady = null;
        }
    }
    private void Log(string where, Exception e)
    {
        // Assembly loading and Activator wrap the typed export refusal; keep its entry-point guidance visible.
        for (Exception? cause = e; cause is not null; cause = cause.InnerException)
            if (cause is RegistryContextException) { e = cause; break; }
        mods.Host.Log(LogLevel.Error, $"registry: {where}: {e.GetType().Name}: {e.Message}; this callback's uncommitted edits were discarded");
    }
}

internal sealed partial class ModManager
{
    private RegistryDispatch? _registry;
    private bool _registryReload;
    private string? _registryFault;
    internal void PrepareRegistry(ulong hostOwner)
    {
        _registry = new(this);
        try { _registry.Prepare(hostOwner); }
        catch (Exception e) { _host.Log(LogLevel.Warn, $"registry: game-start authors were not prepared: {e.Message}"); }
    }
    internal void TickRegistry()
    {
        try { _registry?.FlushLoads(); _registryFault = null; }
        catch (Exception e)
        {
            if (_registryFault != e.Message) _host.Log(LogLevel.Warn, $"registry: runtime dispatch deferred: {e.Message}");
            _registryFault = e.Message;
        }
    }
    /// <summary>The reload reply waits for this (ReloadReplies): a runtime registry pass of one of <paramref name="mods"/>, or
    /// a data-pack application kiln.content has queued for its next Pre.</summary>
    internal bool RegistryAvailable => _registry?.Available ?? false;
    internal bool RegistryWorkPending(IReadOnlyCollection<string> mods) =>
        (_registry?.PassPending(mods) ?? false) || ContentStatusProbe.ReloadPending(_host);
    /// <summary>registry.json's text now (kiln.registry report_json); null without the registry.</summary>
    internal string? RegistryReportJson() => _registry?.ReportJson();
    /// <summary>The reloaded mods' GameStart edits that wait for the next start (RegistryDispatch.RestartNotes).</summary>
    internal IReadOnlyList<(string Mod, string Reason)> RegistryRestartNotes(IReadOnlyCollection<string> mods) =>
        _registry?.RestartNotes(mods) ?? [];
    internal List<(Manifest Manifest, string Directory)> RegistryCandidates()
    {
        var byId = BootCandidates().GroupBy(c => c.Manifest.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return RegistryOrder().Where(x => byId.ContainsKey(x.Id)).Select(x => (byId[x.Id].Manifest, byId[x.Id].Directory)).ToList();
    }
    internal (Mod Instance, ModContext Context, AssemblyLoadContext Assembly)? CreateRegistryAuthor(Manifest manifest, string directory,
        IReadOnlyList<(Manifest Manifest, string Directory)> snapshot)
    {
        var candidates = snapshot.ToDictionary(x => x.Manifest.Id, x => x.Manifest, StringComparer.Ordinal);
        var exports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal) { manifest.Id };
        var queue = new Queue<Manifest>([manifest]);
        while (queue.Count > 0)
            foreach (var dependency in queue.Dequeue().Dependencies)
                if (seen.Add(dependency.Id) && candidates.TryGetValue(dependency.Id, out var candidate))
                {
                    foreach (var file in candidate.Exports) exports.TryAdd(file, candidate.Id);
                    queue.Enqueue(candidate);
                }
        var alc = new ModLoadContext(manifest.Id + ":registry-author", directory,
            name => RegistryDeclarations.GameStartExport(manifest.Id, exports, name));
        ulong owner = 0;
        try
        {
            var assembly = alc.LoadFile(Path.Combine(directory, manifest.EntryAssembly));
            var type = manifest.EntryType is { } name ? assembly.GetType(name, true)!
                : assembly.GetExportedTypes().Single(t => t.IsSubclassOf(typeof(Mod)) && !t.IsAbstract);
            if ((RegistryDeclarations.PointsOf(type) & RegistryEntryPoint.GameStart) == 0) { alc.Unload(); return null; }
            owner = _host.OwnerCreate("registry-author:" + manifest.Id);
            var context = HostAccess.CreateContext(Info(manifest, directory), owner, Path.Combine(_dataDirectory, manifest.Id));
            var instance = (Mod)Activator.CreateInstance(type)!; HostAccess.Bind(instance, context);
            return (instance, context, alc);
        }
        catch { if (owner != 0) _host.OwnerRevoke(owner); alc.Unload(); throw; }
    }
}
