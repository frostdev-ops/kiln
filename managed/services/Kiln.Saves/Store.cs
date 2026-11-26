// SPDX-License-Identifier: MIT
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>
/// The per-world sidecar of mod data: <c>kiln/data/worlds/&lt;world id&gt;/</c>.
/// </summary>
/// <remarks>
/// <para>Layout. <c>blobs/ab/&lt;sha256&gt;.json</c>: one bundle per scope (<c>world/&lt;mod&gt;</c>,
/// <c>player/&lt;steam id&gt;/&lt;mod&gt;</c>, <c>entity/&lt;mod&gt;</c>), content-addressed, written once and never
/// changed. <c>manifests/&lt;revision&gt;.pending.json</c>: the generation staged for the world save with that
/// revision (written when the game accepts the save, before its snapshot). <c>manifests/&lt;revision&gt;-&lt;world
/// file time&gt;.json</c>: the same generation, confirmed after the world file was written. Every file is written to a
/// temp name, forced to disk and renamed, so a crash leaves the old or the new file, never a torn one. One exception
/// (since 0.2.0-dev.29, issue #39): a generation whose bundles are those of the previous one, confirmed by this process
/// and on disk, with no new blob, renames its pending manifest and its confirmation into place without forcing them to
/// disk. A game crash or kill -9 cannot tell the difference (the system still writes the files); a power loss or a system
/// crash before the system wrote them can leave them missing or torn, and loading then skips them (an unusable manifest is
/// logged) and takes an older generation with the same bundles, at worst the newest one forced to disk, which pruning
/// keeps: what a lost rename could already give.</para>
/// <para>Loading picks the manifest of the world file on disk (its header revision + time), so restoring an older world
/// file (from the game's backup folder or a Kiln backup) brings back the mod data that belongs to it. Manifests are
/// kept for the newest 30 generations and for every world file in the save folder and its <c>backup/</c>.</para>
/// <para>Who writes (since 0.2.0-dev.29.3, issue #39): the tick thread only serializes the bundles (in memory); every file
/// operation of a save (blobs, manifests, the pending manifest's delete, pruning) runs on <see cref="SaveWriter"/>'s
/// thread, in order, and kiln.saves holds the game's world write until the writer has the generation's pending manifest
/// on disk (bounded: <c>docs/kiln/services/saves.md</c> says where the order does not hold). A world load and a backup
/// read the folder only while the writer is idle; a world close and the shutdown save wait for it, bounded.</para>
/// </remarks>
internal sealed unsafe class WorldStore
{
    internal const int KeepGenerations = 30;
    internal static readonly TimeSpan OrphanAge = TimeSpan.FromMinutes(10);
    private static long _tmpCounter;

    private readonly Dictionary<string, Bundle> _bundles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonSerializerOptions> _json = new(StringComparer.Ordinal);
    private readonly HashSet<ModContext> _watched = new(ReferenceEqualityComparer.Instance);
    private bool _opened;
    private bool _closed;  // a hosted world closed and the next one has not loaded: nothing here belongs to any world yet
    private uint _loadedRevision, _loadedUnix;
    private int _commits;

    /// <summary>The world session the writer's jobs belong to: a load and a world close start a new one, so what the writer
    /// knows of one session (the generation it confirmed last, the newest one forced to disk) never counts in another.
    /// Unique in the process (tests make stores of their own; they share the one writer).</summary>
    private int _epoch = Interlocked.Increment(ref _epochs);
    private static int _epochs;

    /// <summary>Generations this session staged (tick thread). A confirmation of a generation not among them stages it
    /// first, late: the save began before Kiln.Saves was started.</summary>
    private readonly HashSet<uint> _stagedGens = [];

    /// <summary>Writes every file of the save path, off the tick thread (one writer for the process).</summary>
    internal SaveWriter Writer => SaveWriter.Shared;

    /// <summary>The load of the open world could not read its folder (the writer was still busy with it after
    /// <see cref="SaveWriter.DrainLimit"/>): its revision and file time, to read it later (<see cref="RetryLoad"/>).</summary>
    private (uint Revision, uint Unix)? _deferred;

    /// <summary>The game has loaded this world (its matching starts when the data is read).</summary>
    private bool _matching;

    /// <summary>The world's mod data is not read yet (review 2): it is unavailable, and nothing is saved over it.</summary>
    internal bool LoadDeferred => _deferred != null;

    /// <summary>Reads a deferred load once the writer is idle (the pump, about once a second, and the game's load). True
    /// when it was read now; the caller then tells mods (WorldLoadedEvent).</summary>
    internal bool RetryLoad()
    {
        if (_deferred is not { } d || !Writer.Idle) return false;
        Load(d.Revision, d.Unix);
        if (_deferred != null) return false;
        if (_matching)
        {
            Entities.StartMatching();
            Instances.StartMatching();
        }
        SavesRuntime.Log(LogLevel.Info, $"Kiln.Saves: world {WorldId}: mod data read now that the writer is done: generation {Generation} ({Match})");
        return true;
    }

    /// <summary>Files <see cref="WriteAtomic(string, byte[])"/> wrote in this process, and of them the ones forced to disk
    /// (tests count the writes and the forced writes of a save).</summary>
    internal static long FilesWritten, FilesForced;

    /// <summary>Test seam: called before every file operation of the save path and of a load (<c>check</c>, <c>create</c>,
    /// <c>flush</c>, <c>rename</c>, <c>read</c>, <c>delete</c>, <c>list</c>, <c>folder</c>) with its name, on the thread that does it. Tests slow the
    /// disk with it (issue #39: on a busy hard drive each of these can take seconds).</summary>
    internal static Action<string>? IoHook;

    internal static void Io(string op) => IoHook?.Invoke(op);

    internal readonly EntityTable Entities;

    /// <summary>Data that stays with one item (InstanceData): bundles "instances/&lt;mod&gt;" and
    /// "character-instances/&lt;character&gt;/&lt;mod&gt;" of the same generation.</summary>
    internal readonly InstanceStore Instances;
    internal readonly PlayerStorageStore Storage;

    public WorldStore()
    {
        Entities = new EntityTable();
        Instances = new InstanceStore(this);
        Storage = new PlayerStorageStore(this);
    }

    public string? WorldId { get; private set; }

    /// <summary>A new world the game has loaded but not saved yet (no id until its first save; its mod data is kept in
    /// memory and goes to the world's folder with that save): an id for this session that readers of "which world" use
    /// until <see cref="AdoptWorld"/> gives the real one (Kiln.ModValues' revisions are scoped by it). Empty otherwise.</summary>
    internal Guid ProvisionalWorld { get; private set; }
    public string? Root { get; private set; }
    public uint Generation { get; private set; }
    public DataMatch Match { get; private set; }

    internal sealed class Bundle(string scope, string mod)
    {
        public readonly string Scope = scope;
        public readonly string Mod = mod;
        public readonly SortedDictionary<string, Slot> Keys = new(StringComparer.Ordinal);
    }

    internal sealed class Slot
    {
        public string? Json;   // last serialized form (what is on disk, or newer)
        public object? Value;  // the live object a mod holds (null: not materialized, or the mod unloaded)
        public Type? Type;
    }

    // ---- identity / load ----

    /// <summary>Opens the sidecar of the world file on disk (first use: a mod's OnLoad, before the game loads it).</summary>
    public void EnsureOpen()
    {
        if (_opened || _closed) return;
        _opened = true;
        if (SavesNative.ReadStatus() is not { HasGuid: not 0 } s) return;  // new world: the id comes with the first save
        var id = SavesNative.ToGuid(s.Guid).ToString();
        Open(id, s.Revision, s.WorldUnix);
    }

    private void Open(string id, uint revision, uint unix)
    {
        var root = SavesNative.GetPath(SavesNative.PathData);
        if (root == null) return;
        WorldId = id;
        Root = Path.Combine(root, id);
        Directory.CreateDirectory(Path.Combine(Root, "manifests"));
        Directory.CreateDirectory(Path.Combine(Root, "blobs"));
        Load(revision, unix);
        WorldConfigs.Announce(Root);
    }

    /// <summary>The game's own load finished. Normally the same world/revision Kiln opened at first use.</summary>
    public void OnWorldLoaded(string? id, uint revision, uint unix, bool newWorld)
    {
        _closed = false;
        EnsureOpen();
        if (id == null)
        {
            SavesRuntime.Log(LogLevel.Info, $"Kiln.Saves: new world (no file yet); mod data is kept in memory until the first save");
            _deferred = null;
            if (WorldId == null && ProvisionalWorld == Guid.Empty) ProvisionalWorld = Guid.NewGuid();
            Entities.StartMatching();
            Instances.StartMatching();
            return;
        }
        if (WorldId == null)
        {
            Open(id, revision, unix);
        }
        else if (!string.Equals(WorldId, id, StringComparison.OrdinalIgnoreCase) || revision != _loadedRevision ||
                 (unix != 0 && _loadedUnix != 0 && unix != _loadedUnix))
        {
            SavesRuntime.Log(LogLevel.Warn, $"Kiln.Saves: the game loaded world {id} revision {revision}, Kiln had opened " +
                                            $"{WorldId} revision {_loadedRevision}: reloading mod data (objects mods already hold are detached)");
            DetachAll();
            Open(id, revision, unix);
        }
        _matching = true;
        if (_deferred != null && RetryLoad()) return;  // the writer may have finished since: read it now (it starts the matching)
        Entities.StartMatching();
        Instances.StartMatching();
        SavesRuntime.Log(LogLevel.Info, $"Kiln.Saves: world {WorldId} revision {revision} loaded by the game; mod data " +
                                        (_deferred != null
                                            ? "not read yet (Kiln.Saves' writer is still busy with this folder): unavailable until it is"
                                            : $"generation {Generation} ({Match}), {_bundles.Count} bundles, {Entities.Summary}, {Instances.Summary}"));
    }

    /// <summary>A new world got its id with its first save.</summary>
    public void AdoptWorld(string id)
    {
        if (WorldId != null || _closed) return;
        var root = SavesNative.GetPath(SavesNative.PathData);
        if (root == null) return;
        WorldId = id;
        ProvisionalWorld = Guid.Empty;
        Root = Path.Combine(root, id);
        Directory.CreateDirectory(Path.Combine(Root, "manifests"));
        Directory.CreateDirectory(Path.Combine(Root, "blobs"));
        SavesRuntime.Log(LogLevel.Info, $"Kiln.Saves: world id {id} (new world); mod data goes to {Root}");
        WorldConfigs.Announce(Root);
    }

    /// <summary>A hosted world closed after its shutdown save. A process hosts one world after another, and the next one starts about
    /// 10 s before the game loads it: if its data stayed open here, that time would read and write the closed world's (seen:
    /// the Warden "was placed once in this world" in every later world, and a new world would have filed its data under the old
    /// world's id). So every world of a process starts as the first does, with nothing, until its load opens its own. What a
    /// late write still reports stays staged, and the load finds it.</summary>
    public void CloseWorld()
    {
        // The closed world's last files (its shutdown save's confirmation) are written before anything else can open a
        // world, the same one included, and before a process that quits after the close ends (bounded, logged).
        Writer.Drain("closing the world");
        Writer.Release();  // the writer's pool task need not wait for the next world's jobs
        DetachAll();
        WorldId = null;
        ProvisionalWorld = Guid.Empty;
        Root = null;
        Generation = 0;
        Match = DataMatch.None;
        _loadedRevision = _loadedUnix = 0;
        _deferred = null;
        _matching = false;
        NewSession();
        _opened = false;
        _closed = true;
        WorldConfigs.Announce(null);
    }

    private void DetachAll()
    {
        _bundles.Clear();
        Entities.Clear();
        Instances.Clear();
        Storage.Clear();
        ServiceEntityData.RaiseWorldChanged();  // services drop what they keep per entity id (Kiln.World's waiting scales)
    }

    /// <summary>Test kit (SavesRuntime.EndSession): after <see cref="CloseWorld"/>, the next world opens as in a fresh process.</summary>
    internal void Reopenable()
    {
        _closed = false;
        _commits = 0;
        Instances.EndSession();
    }

    /// <summary>What this session knew about its stagings and confirmations goes: the writer starts its next job in a new
    /// session (it reads <see cref="_epoch"/> from the job).</summary>
    private void NewSession()
    {
        _epoch = Interlocked.Increment(ref _epochs);
        _stagedGens.Clear();
    }

    private void Load(uint revision, uint unix)
    {
        _loadedRevision = revision;
        _loadedUnix = unix;
        NewSession();
        DetachAll();
        var dir = Path.Combine(Root!, "manifests");
        // One stable read (review 1): only once the writer has every file it was handed on disk, and with nothing written
        // while it reads, so a confirmation that is still being written cannot make it take an older generation.
        if (!Writer.TryExclusive("loading mod data", SaveWriter.DrainLimit, () =>
        {
            Io("list");
            var all = ListManifests(dir);
            var order = new List<(ManifestName m, DataMatch match)>();
            foreach (var m in all.Where(m => !m.Pending && m.Revision == revision && (unix == 0 || m.Unix == unix)).OrderByDescending(m => m.Unix))
                order.Add((m, DataMatch.Exact));
            foreach (var m in all.Where(m => m.Pending && m.Revision == revision)) order.Add((m, DataMatch.Staged));
            foreach (var m in all.Where(m => !m.Pending && m.Revision < revision).OrderByDescending(m => m.Revision).ThenByDescending(m => m.Unix))
                order.Add((m, DataMatch.Older));
            var unusable = new List<string>();
            foreach (var (m, match) in order)
            {
                if (!TryLoadManifest(m.Path, out var bundles, out var error))
                {
                    unusable.Add($"Kiln.Saves: manifest {Path.GetFileName(m.Path)} is unusable ({error}); trying an older one");
                    continue;
                }
                return (Manifest: (ManifestName?)m, Match: match, Bundles: bundles, Newer: all.Any(x => x.Revision > m.Revision), Count: all.Count, Unusable: unusable);
            }
            return (Manifest: null, Match: DataMatch.None, Bundles: new List<(string, JsonObject)>(), Newer: all.Count > 0, Count: all.Count, Unusable: unusable);
        }, out var found))
        {
            // Review 2: the writer is still busy with this folder (a disk that does not answer). Reading it now could take an
            // older generation; waiting longer would hold the tick thread. So the load is deferred: no mod data until it can
            // be read (Saves.WorldDataReady is false, as for a hosted world before its load), nothing staged or confirmed
            // over it, and the pump reads it as soon as the writer is idle (RetryLoad), then WorldLoadedEvent goes.
            _deferred = (revision, unix);
            Generation = 0;
            Match = DataMatch.None;
            Instances.OnLoaded(0, DataMatch.None, true);
            SavesRuntime.Log(LogLevel.Warn, $"Kiln.Saves: world {WorldId} file revision {revision}: mod data not read yet: Kiln.Saves' writer " +
                                            "has not finished this folder's files; until it has, the world's mod data is unavailable " +
                                            "(Saves.WorldDataReady is false), nothing is saved over it, and it is read as soon as the writer is done");
            return;
        }
        _deferred = null;
        foreach (var line in found.Unusable) SavesRuntime.Log(LogLevel.Error, line);
        if (found.Manifest is { } chosen)
        {
            foreach (var (scope, doc) in found.Bundles) Absorb(scope, doc);
            Generation = chosen.Revision;
            Match = found.Match;
            Instances.OnLoaded(chosen.Revision, found.Match, found.Newer);
            var level = found.Match == DataMatch.Older ? LogLevel.Warn : LogLevel.Info;
            SavesRuntime.Log(level, $"Kiln.Saves: world {WorldId} file revision {revision}: mod data generation {chosen.Revision} " +
                                    $"({found.Match}{(found.Match == DataMatch.Older ? ": the world file is newer than Kiln's data" : "")}, " +
                                    $"{Path.GetFileName(chosen.Path)}, {found.Bundles.Count} bundles)");
            return;
        }
        Generation = 0;
        Match = DataMatch.None;
        Instances.OnLoaded(0, DataMatch.None, found.Newer);
        SavesRuntime.Log(LogLevel.Info, $"Kiln.Saves: world {WorldId} file revision {revision}: no mod data yet ({found.Count} manifests, none usable)");
    }

    internal IReadOnlyList<string> PendingStorageValues(string scope)
    {
        if (Root is null || LoadDeferred) return [];
        var root = Root;
        // A join during a save's write waits for the next service step instead of reading half-written manifests.
        if (!Writer.Idle) throw new StorageRestoreDeferredException("the writer is busy; player storage restore deferred");
        if (!Writer.TryExclusive("reading pending player storage", TimeSpan.Zero, () =>
        {
            var values = new List<string>();
            foreach (var manifest in ListManifests(Path.Combine(root, "manifests")).Where(m => m.Pending))
                if (TryLoadManifest(manifest.Path, out var bundles, out _))
                    foreach (var (name, data) in bundles)
                        if (name == scope && data["keys"]?[PlayerStorageStore.Key] is { } document) values.Add(document.ToJsonString());
            return values;
        }, out var result))
        {
            throw new StorageRestoreDeferredException("player storage pending candidates are not available yet; restore deferred");
        }
        return result;
    }

    private bool TryLoadManifest(string path, out List<(string scope, JsonObject doc)> bundles, out string error)
    {
        bundles = [];
        error = "";
        try
        {
            Io("read");
            var man = JsonNode.Parse(File.ReadAllBytes(path))?.AsObject() ?? throw new InvalidDataException("empty");
            if (man["bundles"] is not JsonObject list) return true;
            foreach (var (scope, entry) in list)
            {
                var sha = entry?["sha256"]?.GetValue<string>() ?? throw new InvalidDataException($"{scope}: no sha256");
                Io("read");
                var bytes = File.ReadAllBytes(BlobPath(sha));
                var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (actual != sha) throw new InvalidDataException($"{scope}: blob {sha[..12]} has sha256 {actual[..12]}");
                var doc = JsonNode.Parse(bytes)?.AsObject() ?? throw new InvalidDataException($"{scope}: empty blob");
                bundles.Add((scope, doc));
            }
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = e.Message;
            return false;
        }
    }

    private void Absorb(string scope, JsonObject doc)
    {
        if (scope.StartsWith("entity/", StringComparison.Ordinal))
        {
            Entities.Absorb(scope["entity/".Length..], doc);
            return;
        }
        if (scope.StartsWith(InstanceStore.WorldPrefix, StringComparison.Ordinal) || scope.StartsWith(InstanceStore.CharacterPrefix, StringComparison.Ordinal))
        {
            Instances.Absorb(scope, doc);
            return;
        }
        var mod = ModOf(scope);
        var b = new Bundle(scope, mod);
        if (doc["keys"] is JsonObject keys)
            foreach (var (k, v) in keys)
                b.Keys[k] = new Slot { Json = v?.ToJsonString() ?? "null" };
        _bundles[scope] = b;
    }

    private static string ModOf(string scope) => scope[(scope.LastIndexOf('/') + 1)..];

    // ---- access (tick thread, mod code) ----

    internal static ModContext CallerMod(string api)
    {
        Services.RequireTickThread(api);
        return ModContext.CurrentMod ?? throw new InvalidOperationException($"{api} must be called from a mod's code (its handlers, OnLoad or tasks)");
    }

    internal static string CheckKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (key.Length > 200) throw new ArgumentException("keys are at most 200 characters", nameof(key));
        return key;
    }

    internal Bundle? FindBundle(string scope) => _bundles.GetValueOrDefault(scope);

    internal Bundle GetBundle(string scope, ModContext mod)
    {
        EnsureOpen();
        Watch(mod);
        if (!_bundles.TryGetValue(scope, out var b)) _bundles[scope] = b = new Bundle(scope, mod.Info.Id);
        return b;
    }

    internal IEnumerable<Bundle> BundlesWithPrefix(string prefix) =>
        _bundles.Values.Where(b => b.Scope.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>A bundle for data that arrives as JSON (no mod context: a client's carried data, Kiln.Saves' own records).</summary>
    internal Bundle GetBundleRaw(string scope, string mod)
    {
        EnsureOpen();
        if (!_bundles.TryGetValue(scope, out var b)) _bundles[scope] = b = new Bundle(scope, mod);
        return b;
    }

    /// <summary>A slot's JSON as of now: a live object is serialized, like a save does.</summary>
    internal string? JsonOf(Bundle b, Slot s, string key)
    {
        if (s.Value == null) return s.Json;
        try
        {
            s.Json = JsonSerializer.Serialize(s.Value, s.Type ?? s.Value.GetType(), _json.GetValueOrDefault(b.Mod));
        }
        catch (Exception e)
        {
            SavesRuntime.Log(LogLevel.Error, $"Kiln.Saves: {b.Scope} key '{key}' could not be serialized ({e.Message}); its last saved form is used");
        }
        return s.Json;
    }

    internal JsonSerializerOptions JsonFor(ModContext mod)
    {
        Watch(mod);
        if (!_json.TryGetValue(mod.Info.Id, out var o))
            _json[mod.Info.Id] = o = new JsonSerializerOptions { IncludeFields = true, WriteIndented = false };
        return o;
    }

    internal void Watch(ModContext mod)
    {
        if (!_watched.Add(mod)) return;
        mod.Retiring += () => Retire(mod);
    }

    /// <summary>A mod unloads: its live objects are serialized and dropped (they belong to its load context), and its
    /// serializer cache goes with them. A reload of the same mod id sees the same data.</summary>
    private void Retire(ModContext mod)
    {
        _watched.Remove(mod);
        var id = mod.Info.Id;
        var opts = _json.GetValueOrDefault(id);
        foreach (var b in _bundles.Values.Where(b => b.Mod == id))
            foreach (var (k, s) in b.Keys)
                Freeze(s, opts, mod, b.Scope, k);
        Entities.Retire(id, opts, mod);
        Instances.Retire(mod, opts);
        _json.Remove(id);
        if (opts != null) JsonCaches.Clear();
    }

    internal static void Freeze(Slot s, JsonSerializerOptions? opts, ModContext? mod, string scope, string key)
    {
        if (s.Value == null) return;
        try
        {
            s.Json = JsonSerializer.Serialize(s.Value, s.Type ?? s.Value.GetType(), opts);
        }
        catch (Exception e)
        {
            Report(mod, $"Kiln.Saves: {scope} key '{key}' could not be serialized ({e.Message}); its last saved form is kept");
        }
        s.Value = null;
        s.Type = null;
    }

    private static void Report(ModContext? mod, string text)
    {
        if (mod != null) mod.Log.Error(text);
        else SavesRuntime.Log(LogLevel.Error, text);
    }

    internal T GetOrCreate<T>(Slot s, ModContext mod, string scope, string key, bool create) where T : class
    {
        if (s.Value is T t) return t;
        var opts = JsonFor(mod);
        if (s.Value != null) Freeze(s, opts, mod, scope, key);
        T? v;
        try
        {
            v = s.Json == null ? null : JsonSerializer.Deserialize<T>(s.Json, opts);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{scope} key '{key}' does not read as {typeof(T).Name}: {e.Message} (the stored data is kept)", e);
        }
        if (v == null)
        {
            if (!create) throw new InvalidDataException($"{scope} key '{key}' is null");
            v = Activator.CreateInstance<T>();
        }
        s.Value = v;
        s.Type = typeof(T);
        return v;
    }

    // ---- staging / commit ----

    /// <summary>Captures generation <paramref name="gen"/>: every bundle serialized, in memory, and hands it to the
    /// <see cref="Writer"/>, which writes its new blobs and its pending manifest off this thread (since 0.2.0-dev.29.3;
    /// before, the tick thread wrote them here). Tick thread, right after the WorldSaving handlers (<paramref name="snapshot"/>
    /// false), and again at the snapshot staging (<paramref name="snapshot"/> true). The game's world write waits until
    /// the writer has it on disk (kiln.saves' stage barrier); without the barrier this thread writes it, as before.</summary>
    public void Stage(uint gen, SaveReason reason, bool late, bool snapshot = false)
    {
        EnsureOpen();
        if (Root == null)
        {
            SavesRuntime.Log(LogLevel.Warn, "Kiln.Saves: the world has no id yet; mod data stays in memory this time");
            return;
        }
        if (gen == 0)
        {
            SavesRuntime.Log(LogLevel.Warn, "Kiln.Saves: the save's revision is unknown; mod data not written");
            return;
        }
        if (_deferred != null)
        {
            // Not read yet (review 2): an empty store must never be staged over the data on disk. The world file of this
            // save gets no generation of its own; loading it takes the newest one on disk, which is the data this world had.
            SavesRuntime.Log(LogLevel.Warn, $"Kiln.Saves: generation {gen} not staged: this world's mod data is not read yet (the writer is still busy)");
            return;
        }
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Storage.Stage(gen);
        // Kiln's services write their per-character records now (19d §4.7: the final snapshot staging re-reads live state first)
        ServiceCharacters.RaiseStaging(gen, snapshot, late, _epoch);
        var bundles = new List<(string Scope, byte[] Data)>();
        foreach (var b in _bundles.Values)
        {
            if (b.Keys.Count == 0) continue;
            var data = Serialize(b, _json.GetValueOrDefault(b.Mod));
            bundles.Add((b.Scope, data));
            // the exact bytes this staging serialized: what a service acknowledges once the generation is committed
            if (b.Scope.StartsWith(ServiceCharacters.Prefix, StringComparison.Ordinal)) ServiceCharacters.RaiseStaged(b.Scope, data, gen, snapshot, _epoch);
        }
        ServiceEntityData.RaiseStaging();  // Kiln services refresh their values on entities (Kiln.World's transform scale)
        foreach (var (mod, data) in Entities.Stage(_json)) bundles.Add(("entity/" + mod, data));
        // Settled: nothing simulates between this read and the game's snapshot (the snapshot staging, a shutdown save).
        bundles.AddRange(Instances.Stage(gen, _json, settled: !late && (snapshot || reason == SaveReason.Shutdown)));
        var job = new SaveWriter.StageJob
        {
            Epoch = _epoch, Root = Root, WorldId = WorldId, Gen = gen, Reason = reason, Late = late, Snapshot = snapshot,
            Bundles = bundles, CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Kiln = Game.KilnVersion,
        };
        job.TickMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        _stagedGens.Add(gen);
        Generation = gen;
        Storage.Track(job);
        Writer.Stage(job);
    }

    /// <summary>A manifest's bundle list in one fixed form (scope, sha256, size; ordinal by scope), to compare two manifests.</summary>
    internal static string BundlesKey(JsonObject? list)
    {
        if (list == null) return "";
        var sb = new StringBuilder();
        foreach (var (scope, e) in list.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(scope).Append('\n').Append(e?["sha256"]?.GetValue<string>()).Append('\n').Append(e?["size"]?.ToJsonString()).Append('\n');
        return sb.ToString();
    }

    /// <summary>A staging or a commit that takes longer than this logs at Info, not Debug (issue #39: on a hard drive a
    /// forced write held the server for seconds at each autosave, and only a debug log said so).</summary>
    internal static double SlowMs = 250;

    internal static LogLevel TimingLevel(double ms) => ms > SlowMs ? LogLevel.Info : LogLevel.Debug;

    /// <summary>What a pending manifest says apart from its creation time.</summary>
    internal static string ManifestContent(JsonObject man)
    {
        var copy = (JsonObject)man.DeepClone();
        copy.Remove("createdUnixMs");
        return copy.ToJsonString();
    }

    private static byte[] Serialize(Bundle b, JsonSerializerOptions? opts)
    {
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("format", 1);
            w.WriteString("scope", b.Scope);
            w.WriteStartObject("keys");
            foreach (var (k, s) in b.Keys)
            {
                if (s.Value != null)
                {
                    try
                    {
                        s.Json = JsonSerializer.Serialize(s.Value, s.Type ?? s.Value.GetType(), opts);
                    }
                    catch (Exception e)
                    {
                        SavesRuntime.Log(LogLevel.Error, $"Kiln.Saves: {b.Scope} key '{k}' could not be serialized ({e.Message}); its last saved form is written");
                    }
                }
                if (s.Json == null) continue;
                w.WritePropertyName(k);
                w.WriteRawValue(s.Json, skipInputValidation: true);
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>The world file of generation <paramref name="rev"/> is complete: the <see cref="Writer"/> confirms the
    /// staged manifest (and prunes, at the 1st, 11th, 21st... confirmation of the process) off this thread. Tick thread.
    /// The shutdown save's confirmation waits for the writer (bounded): the process ends soon after it.</summary>
    public void Commit(uint rev, uint unix, SaveReason reason)
    {
        EnsureOpen();
        if (Root == null || rev == 0 || _deferred != null) return;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_stagedGens.Contains(rev)) Stage(rev, reason, late: true);  // the save began before Kiln.Saves was started
        if (Root == null || !_stagedGens.Contains(rev)) return;
        _stagedGens.RemoveWhere(g => g <= rev);
        ServiceCharacters.RaiseCommitted(rev, unix, _epoch);
        var job = new SaveWriter.CommitJob
        {
            Epoch = _epoch, Root = Root, WorldId = WorldId, Rev = rev, Unix = unix, Reason = reason, Prune = ++_commits % 10 == 1,
            WorldFile = unix == 0 ? SavesRuntime.WorldFile : null,
        };
        // Every confirmation carries the save folder: a pruning that stopped for a staging runs again at the next one, and
        // it keeps the generations of the game's backups only when it can see them (review 1). A native path, no disk.
        job.SaveDir = Saves.SaveDirectory;
        Generation = rev;
        Match = DataMatch.Exact;
        _loadedRevision = rev;
        if (unix != 0) _loadedUnix = unix;
        job.TickMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        Writer.Commit(job);
        Storage.Committed(rev);
        if (reason == SaveReason.Shutdown)
        {
            Writer.Drain("the shutdown save");
            Writer.Release();  // the process exits soon: its pool task gives its thread back now, not after its idle wait
        }
    }

    // ---- files ----
    internal readonly record struct ManifestName(string Path, uint Revision, uint Unix, bool Pending);

    internal static List<ManifestName> ListManifests(string dir) => ListManifestFiles(dir).Select(m => m.Name).ToList();

    /// <summary>The manifests with each file's size and last write time, from the one listing (pruning keeps what it read of a
    /// manifest per version of the file).</summary>
    internal static List<(ManifestName Name, long Size, DateTime Written)> ListManifestFiles(string dir)
    {
        var list = new List<(ManifestName, long, DateTime)>();
        var folder = new DirectoryInfo(dir);
        if (!folder.Exists) return list;
        foreach (var info in folder.EnumerateFiles("*.json"))
        {
            var f = System.IO.Path.Combine(dir, info.Name);
            var n = System.IO.Path.GetFileNameWithoutExtension(f);
            if (n.EndsWith(".pending", StringComparison.Ordinal))
            {
                if (uint.TryParse(n[..^8], out var r)) list.Add((new ManifestName(f, r, 0, true), info.Length, info.LastWriteTimeUtc));
                continue;
            }
            var dash = n.IndexOf('-');
            if (dash > 0 && uint.TryParse(n[..dash], out var rev) && uint.TryParse(n[(dash + 1)..], out var unix))
                list.Add((new ManifestName(f, rev, unix, false), info.Length, info.LastWriteTimeUtc));
        }
        return list;
    }

    internal static string PendingPath(string root, uint rev) => Path.Combine(root, "manifests", $"{rev:D10}.pending.json");
    internal static string CommittedPath(string root, uint rev, uint unix) => Path.Combine(root, "manifests", $"{rev:D10}-{unix}.json");
    internal static string BlobPath(string root, string sha) => Path.Combine(root, "blobs", sha[..2], sha + ".json");
    private string CommittedPath(uint rev, uint unix) => CommittedPath(Root!, rev, unix);
    internal string BlobPath(string sha) => BlobPath(Root!, sha);

    internal static void WriteAtomic(string path, byte[] data)
    {
        var disk = new DiskTime();
        WriteAtomic(path, data, ref disk);
    }

    /// <summary>Writes <paramref name="data"/> to a temp name, forces it to disk (unless <paramref name="force"/> is false:
    /// only an unchanged generation's pending manifest and confirmation, see the class remarks) and renames it into place;
    /// each step's time goes into <paramref name="disk"/>.</summary>
    internal static void WriteAtomic(string path, byte[] data, ref DiskTime disk, bool force = true)
    {
        var tmp = $"{path}.tmp-{Environment.ProcessId}-{Interlocked.Increment(ref _tmpCounter)}";
        var t = System.Diagnostics.Stopwatch.GetTimestamp();
        Io("create");
        var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var ms = Since(t);
        disk.CreateMs += ms;
        disk.Note("creating", path, ms);
        try
        {
            t = System.Diagnostics.Stopwatch.GetTimestamp();
            fs.Write(data);
            fs.Flush(flushToDisk: false);  // to the system
            ms = Since(t);
            disk.WriteMs += ms;
            disk.Note("writing", path, ms);
            if (force)
            {
                t = System.Diagnostics.Stopwatch.GetTimestamp();
                Io("flush");
                fs.Flush(flushToDisk: true);
                ms = Since(t);
                disk.FlushMs += ms;
                disk.Note("forcing to disk", path, ms);
                Interlocked.Increment(ref FilesForced);
            }
            else
            {
                disk.Unforced++;
            }
        }
        finally
        {
            t = System.Diagnostics.Stopwatch.GetTimestamp();
            fs.Dispose();
            ms = Since(t);
            disk.CloseMs += ms;
            disk.Note("closing", path, ms);
        }
        // On Windows File.Move is MoveFileExW(MOVEFILE_REPLACE_EXISTING | MOVEFILE_COPY_ALLOWED), no write-through (0.2.0-dev.31.5
        // measured it: about 0.3 ms with Windows Defender on, see docs/kiln/services/saves.md "A slow writer").
        t = System.Diagnostics.Stopwatch.GetTimestamp();
        Io("rename");
        File.Move(tmp, path, overwrite: true);
        ms = Since(t);
        disk.RenameMs += ms;
        disk.Note("renaming", path, ms);
        disk.Files++;
        Interlocked.Increment(ref FilesWritten);
    }

    private static double Since(long t) => System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds;

    internal readonly record struct Header(Guid Guid, uint Revision, uint Unix);

    internal static Header? ReadHeader(string? file)
    {
        if (file == null) return null;
        try
        {
            Io("read");
            using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> b = stackalloc byte[0x24];
            if (f.Read(b) != b.Length || b[0] != (byte)'S' || b[1] != (byte)'V' || b[2] != (byte)'2' || b[3] != (byte)'C') return null;
            return new Header(new Guid(b.Slice(0x0C, 16)), BitConverter.ToUInt32(b[0x1C..]), BitConverter.ToUInt32(b[0x20..]));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The manifest of generation <paramref name="rev"/> a backup carries: its confirmation of that world file time,
    /// else another confirmation of the revision, else its pending manifest (the same generation, when its confirmation
    /// could not be written; a restore loads it as staged). Only one that reads and names blobs that are all there.</summary>
    internal string? UsableManifest(uint rev, uint unix)
    {
        if (Root == null) return null;
        var exact = CommittedPath(rev, unix);
        var candidates = new List<string> { exact };
        candidates.AddRange(ListManifests(Path.Combine(Root, "manifests")).Where(m => !m.Pending && m.Revision == rev && m.Path != exact)
            .OrderByDescending(m => m.Unix).Select(m => m.Path));
        candidates.Add(PendingPath(Root, rev));
        foreach (var p in candidates)
        {
            try
            {
                if (!File.Exists(p) || JsonNode.Parse(File.ReadAllBytes(p))?["bundles"] is not JsonObject list) continue;
                if (list.All(kv => kv.Value?["sha256"]?.GetValue<string>() is { } sha && File.Exists(BlobPath(sha)))) return p;
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
            {
                // not usable: the next one
            }
        }
        return null;
    }

    internal IEnumerable<string> BlobsOf(string manifest)
    {
        if (JsonNode.Parse(File.ReadAllBytes(manifest))?["bundles"] is not JsonObject list) yield break;
        foreach (var (_, e) in list)
            if (e?["sha256"]?.GetValue<string>() is { } sha)
                yield return BlobPath(sha);
    }
}

/// <summary>System.Text.Json keeps the metadata of every type it serialized in process-wide caches, which keeps an
/// unloaded mod's load context alive even when each mod had its own options. Its hot-reload hook
/// (<c>JsonSerializerOptionsUpdateHandler.ClearCache</c>, what MetadataUpdateHandler calls) drops those caches; Kiln.Saves
/// calls it when a mod whose data it serialized unloads. Other users only lose warm caches.</summary>
internal static class JsonCaches
{
    private static readonly System.Reflection.MethodInfo? ClearCache = typeof(JsonSerializerOptions).Assembly
        .GetType("System.Text.Json.JsonSerializerOptionsUpdateHandler")
        ?.GetMethod("ClearCache", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

    private static bool _warned;

    public static void Clear()
    {
        try
        {
            if (ClearCache != null) ClearCache.Invoke(null, [null]);
            else if (!_warned)
            {
                _warned = true;
                SavesRuntime.Log(LogLevel.Warn, "Kiln.Saves: System.Text.Json's cache reset was not found; an unloaded mod's types may stay in memory");
            }
        }
        catch (Exception e)
        {
            if (!_warned) SavesRuntime.Log(LogLevel.Warn, $"Kiln.Saves: resetting System.Text.Json's caches failed: {e.Message}");
            _warned = true;
        }
    }
}
