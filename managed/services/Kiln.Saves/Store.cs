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
/// temp name, flushed and renamed, so a crash leaves the old or the new file, never a torn one.</para>
/// <para>Loading picks the manifest of the world file on disk (its header revision + time), so restoring an older world
/// file (from the game's backup folder or a Kiln backup) brings back the mod data that belongs to it. Manifests are
/// kept for the newest 30 generations and for every world file in the save folder and its <c>backup/</c>.</para>
/// </remarks>
internal sealed unsafe class WorldStore
{
    private const int KeepGenerations = 30;
    private static readonly TimeSpan OrphanAge = TimeSpan.FromMinutes(10);
    private static long _tmpCounter;

    private readonly Dictionary<string, Bundle> _bundles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonSerializerOptions> _json = new(StringComparer.Ordinal);
    private readonly HashSet<ModContext> _watched = new(ReferenceEqualityComparer.Instance);
    private bool _opened;
    private bool _closed;  // a hosted world closed and the next one has not loaded: nothing here belongs to any world yet
    private uint _loadedRevision, _loadedUnix;
    private int _commits;

    internal readonly EntityTable Entities;

    /// <summary>Data that stays with one item (InstanceData): bundles "instances/&lt;mod&gt;" and
    /// "character-instances/&lt;character&gt;/&lt;mod&gt;" of the same generation.</summary>
    internal readonly InstanceStore Instances;

    public WorldStore()
    {
        Entities = new EntityTable();
        Instances = new InstanceStore(this);
    }

    public string? WorldId { get; private set; }
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
        Entities.StartMatching();
        Instances.StartMatching();
        SavesRuntime.Log(LogLevel.Info, $"Kiln.Saves: world {WorldId} revision {revision} loaded by the game; mod data " +
                                        $"generation {Generation} ({Match}), {_bundles.Count} bundles, {Entities.Summary}, {Instances.Summary}");
    }

    /// <summary>A new world got its id with its first save.</summary>
    public void AdoptWorld(string id)
    {
        if (WorldId != null || _closed) return;
        var root = SavesNative.GetPath(SavesNative.PathData);
        if (root == null) return;
        WorldId = id;
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
        DetachAll();
        WorldId = null;
        Root = null;
        Generation = 0;
        Match = DataMatch.None;
        _loadedRevision = _loadedUnix = 0;
        _opened = false;
        _closed = true;
        WorldConfigs.Announce(null);
    }

    private void DetachAll()
    {
        _bundles.Clear();
        Entities.Clear();
        Instances.Clear();
    }

    /// <summary>Test kit (SavesRuntime.EndSession): after <see cref="CloseWorld"/>, the next world opens as in a fresh process.</summary>
    internal void Reopenable()
    {
        _closed = false;
        _commits = 0;
        Instances.EndSession();
    }

    private void Load(uint revision, uint unix)
    {
        _loadedRevision = revision;
        _loadedUnix = unix;
        DetachAll();
        var dir = Path.Combine(Root!, "manifests");
        var all = ListManifests(dir);
        var order = new List<(ManifestName m, DataMatch match)>();
        foreach (var m in all.Where(m => !m.Pending && m.Revision == revision && (unix == 0 || m.Unix == unix)).OrderByDescending(m => m.Unix))
            order.Add((m, DataMatch.Exact));
        foreach (var m in all.Where(m => m.Pending && m.Revision == revision)) order.Add((m, DataMatch.Staged));
        foreach (var m in all.Where(m => !m.Pending && m.Revision < revision).OrderByDescending(m => m.Revision).ThenByDescending(m => m.Unix))
            order.Add((m, DataMatch.Older));
        foreach (var (m, match) in order)
        {
            if (!TryLoadManifest(m.Path, out var bundles, out var error))
            {
                SavesRuntime.Log(LogLevel.Error, $"Kiln.Saves: manifest {Path.GetFileName(m.Path)} is unusable ({error}); trying an older one");
                continue;
            }
            foreach (var (scope, doc) in bundles) Absorb(scope, doc);
            Generation = m.Revision;
            Match = match;
            Instances.OnLoaded(m.Revision, match);
            var level = match == DataMatch.Older ? LogLevel.Warn : LogLevel.Info;
            SavesRuntime.Log(level, $"Kiln.Saves: world {WorldId} file revision {revision}: mod data generation {m.Revision} " +
                                    $"({match}{(match == DataMatch.Older ? ": the world file is newer than Kiln's data" : "")}, " +
                                    $"{Path.GetFileName(m.Path)}, {bundles.Count} bundles)");
            return;
        }
        Generation = 0;
        Match = DataMatch.None;
        Instances.OnLoaded(0, DataMatch.None);
        SavesRuntime.Log(LogLevel.Info, $"Kiln.Saves: world {WorldId} file revision {revision}: no mod data yet ({all.Count} manifests, none usable)");
    }

    private bool TryLoadManifest(string path, out List<(string scope, JsonObject doc)> bundles, out string error)
    {
        bundles = [];
        error = "";
        try
        {
            var man = JsonNode.Parse(File.ReadAllBytes(path))?.AsObject() ?? throw new InvalidDataException("empty");
            if (man["bundles"] is not JsonObject list) return true;
            foreach (var (scope, entry) in list)
            {
                var sha = entry?["sha256"]?.GetValue<string>() ?? throw new InvalidDataException($"{scope}: no sha256");
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

    /// <summary>Writes every bundle (new blobs only) and the pending manifest of generation <paramref name="gen"/>.
    /// Tick thread, right after the WorldSaving handlers.</summary>
    public void Stage(uint gen, SaveReason reason, bool late)
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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var entries = new SortedDictionary<string, (string sha, int size)>(StringComparer.Ordinal);
        int fresh = 0;
        long bytes = 0;
        foreach (var b in _bundles.Values)
        {
            if (b.Keys.Count == 0) continue;
            var opts = _json.GetValueOrDefault(b.Mod);
            var data = Serialize(b, opts);
            Put(b.Scope, data, entries, ref fresh, ref bytes);
        }
        foreach (var (mod, data) in Entities.Stage(_json))
            Put("entity/" + mod, data, entries, ref fresh, ref bytes);
        foreach (var (scope, data) in Instances.Stage(gen, _json))
            Put(scope, data, entries, ref fresh, ref bytes);
        var man = new JsonObject
        {
            ["format"] = 1,
            ["world"] = WorldId,
            ["revision"] = gen,
            ["state"] = "pending",
            ["reason"] = Saves.Text(reason),
            ["late"] = late,
            ["createdUnixMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["kiln"] = Game.KilnVersion,
        };
        var list = new JsonObject();
        foreach (var (scope, (sha, size)) in entries) list[scope] = new JsonObject { ["sha256"] = sha, ["size"] = size };
        man["bundles"] = list;
        WriteAtomic(PendingPath(gen), Encoding.UTF8.GetBytes(man.ToJsonString()));
        Generation = gen;
        SavesRuntime.Log(late ? LogLevel.Warn : LogLevel.Debug,
            $"Kiln.Saves: staged mod data generation {gen} ({Saves.Text(reason)}{(late ? ", late: after the world write" : "")}): " +
            $"{entries.Count} bundles, {fresh} new blobs ({bytes} bytes) in {sw.Elapsed.TotalMilliseconds:F1} ms");
    }

    private void Put(string scope, byte[] data, SortedDictionary<string, (string, int)> entries, ref int fresh, ref long bytes)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(data));
        var path = BlobPath(sha);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteAtomic(path, data);
            fresh++;
            bytes += data.Length;
        }
        entries[scope] = (sha, data.Length);
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

    /// <summary>The world file of generation <paramref name="rev"/> is complete: confirm the staged manifest.</summary>
    public void Commit(uint rev, uint unix, SaveReason reason)
    {
        EnsureOpen();
        if (Root == null || rev == 0) return;
        if (unix == 0 && ReadHeader(SavesRuntime.WorldFile) is { } h && h.Revision == rev) unix = h.Unix;
        var pending = PendingPath(rev);
        if (!File.Exists(pending)) Stage(rev, reason, late: true);  // the save began before Kiln.Saves was started
        if (!File.Exists(pending)) return;
        try
        {
            var man = JsonNode.Parse(File.ReadAllBytes(pending))!.AsObject();
            man["state"] = "committed";
            man["worldUnix"] = unix;
            man["committedUnixMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            WriteAtomic(CommittedPath(rev, unix), Encoding.UTF8.GetBytes(man.ToJsonString()));
            File.Delete(pending);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            SavesRuntime.Log(LogLevel.Error, $"Kiln.Saves: confirming generation {rev} failed: {e.Message} (it stays staged; loading still finds it)");
            return;
        }
        Generation = rev;
        Match = DataMatch.Exact;
        _loadedRevision = rev;
        _loadedUnix = unix;
        if (++_commits % 10 == 1) Prune();
    }

    // ---- files ----
    internal readonly record struct ManifestName(string Path, uint Revision, uint Unix, bool Pending);

    internal static List<ManifestName> ListManifests(string dir)
    {
        var list = new List<ManifestName>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            var n = System.IO.Path.GetFileNameWithoutExtension(f);
            if (n.EndsWith(".pending", StringComparison.Ordinal))
            {
                if (uint.TryParse(n[..^8], out var r)) list.Add(new ManifestName(f, r, 0, true));
                continue;
            }
            var dash = n.IndexOf('-');
            if (dash > 0 && uint.TryParse(n[..dash], out var rev) && uint.TryParse(n[(dash + 1)..], out var unix))
                list.Add(new ManifestName(f, rev, unix, false));
        }
        return list;
    }

    private string PendingPath(uint rev) => Path.Combine(Root!, "manifests", $"{rev:D10}.pending.json");
    private string CommittedPath(uint rev, uint unix) => Path.Combine(Root!, "manifests", $"{rev:D10}-{unix}.json");
    internal string BlobPath(string sha) => Path.Combine(Root!, "blobs", sha[..2], sha + ".json");

    internal static void WriteAtomic(string path, byte[] data)
    {
        var tmp = $"{path}.tmp-{Environment.ProcessId}-{Interlocked.Increment(ref _tmpCounter)}";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            fs.Write(data);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    internal readonly record struct Header(Guid Guid, uint Revision, uint Unix);

    internal static Header? ReadHeader(string? file)
    {
        if (file == null) return null;
        try
        {
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

    /// <summary>Drops manifests no world file needs any more, then blobs no kept manifest names.</summary>
    internal void Prune()
    {
        if (Root == null) return;
        try
        {
            var dir = Path.Combine(Root, "manifests");
            var all = ListManifests(dir);
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in all.Where(m => !m.Pending).OrderByDescending(m => m.Revision).Take(KeepGenerations)) keep.Add(m.Path);
            var wanted = new HashSet<(uint, uint)>();
            if (Saves.SaveDirectory is { } sd && Directory.Exists(sd))
            {
                foreach (var f in Directory.EnumerateFiles(sd, "world*.ksd").Concat(
                             Directory.Exists(Path.Combine(sd, "backup")) ? Directory.EnumerateFiles(Path.Combine(sd, "backup")) : []))
                    if (ReadHeader(f) is { } h && h.Guid.ToString() == WorldId) wanted.Add((h.Revision, h.Unix));
            }
            foreach (var m in all.Where(m => !m.Pending && wanted.Contains((m.Revision, m.Unix)))) keep.Add(m.Path);
            var newest = all.Where(m => !m.Pending).Select(m => m.Revision).DefaultIfEmpty(0u).Max();
            foreach (var m in all.Where(m => m.Pending && m.Revision > newest)) keep.Add(m.Path);
            int dropped = 0, blobsDropped = 0;
            foreach (var m in all.Where(m => !keep.Contains(m.Path)))
            {
                File.Delete(m.Path);
                dropped++;
            }
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in keep)
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllBytes(p))?["bundles"] is JsonObject list)
                        foreach (var (_, e) in list)
                            if (e?["sha256"]?.GetValue<string>() is { } sha) referenced.Add(sha);
                }
                catch (Exception e) when (e is IOException or JsonException)
                {
                    return;  // unsure what is referenced: keep every blob
                }
            }
            var cutoff = DateTime.UtcNow - OrphanAge;
            foreach (var f in Directory.EnumerateFiles(Path.Combine(Root, "blobs"), "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(f);
                var tmp = name.Contains(".tmp-", StringComparison.Ordinal);
                var sha = tmp ? "" : Path.GetFileNameWithoutExtension(name);
                if ((tmp || !referenced.Contains(sha)) && File.GetLastWriteTimeUtc(f) < cutoff)
                {
                    File.Delete(f);
                    blobsDropped++;
                }
            }
            foreach (var f in Directory.EnumerateFiles(dir, "*.tmp-*"))
                if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f);
            if (dropped + blobsDropped > 0)
                SavesRuntime.Log(LogLevel.Debug, $"Kiln.Saves: pruned {dropped} manifests and {blobsDropped} blobs; {keep.Count} manifests kept");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SavesRuntime.Log(LogLevel.Warn, $"Kiln.Saves: pruning the sidecar failed: {e.Message}");
        }
    }

    /// <summary>The committed manifest of (revision, world time), for backups.</summary>
    internal string? CommittedManifest(uint rev, uint unix)
    {
        if (Root == null) return null;
        var p = CommittedPath(rev, unix);
        if (File.Exists(p)) return p;
        return ListManifests(Path.Combine(Root, "manifests")).Where(m => !m.Pending && m.Revision == rev)
            .OrderByDescending(m => m.Unix).Select(m => m.Path).FirstOrDefault();
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
