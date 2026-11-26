// SPDX-License-Identifier: MIT
// Server modpacks, server side (docs/kiln/network.md "Server modpacks", contracts.md 5.8): the server's stable id and
// name, its network.json modpack, and the mods players need (manifest clients: "mod") with their kdh1, sent as trailing
// handshake properties in REJECT and WELCOME. Client side: parsing those properties back (last-server.json).
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kiln;

/// <summary>Where players get a server's mods (<c>network.json</c> <c>modpack</c>; schema common modpackRef).</summary>
/// <param name="Url">Where players download the modpack: an <c>http</c>, <c>https</c> or <c>file</c> URL of at most 2048 characters.</param>
/// <param name="Sha256">The SHA-256 of the download, as 64 lowercase hex digits.</param>
/// <param name="Name">The modpack's display name, or null.</param>
/// <param name="Version">The modpack's version, or null.</param>
/// <param name="Size">The download size in bytes, or null when not stated.</param>
public sealed record NetModpack(string Url, string Sha256, string? Name = null, string? Version = null, long? Size = null)
{
    internal static NetModpack? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        string? S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var url = S("url");
        var sha = S("sha256")?.ToLowerInvariant();
        if (url is null || sha is null || !NetAdvertise.IsModpackUrl(url) || !NetAdvertise.IsSha256(sha)) return null;
        long? size = e.TryGetProperty("size", out var z) && z.TryGetInt64(out var n) && n >= 0 ? n : null;
        return new NetModpack(url, sha, S("name"), S("version"), size);
    }

    internal JsonObject ToJson()
    {
        var o = new JsonObject { ["url"] = Url, ["sha256"] = Sha256 };
        if (Name != null) o["name"] = Name;
        if (Version != null) o["version"] = Version;
        if (Size != null) o["size"] = Size;
        return o;
    }
}

/// <summary>A mod players need: kind (<c>kiln</c> | <c>native</c>), id, version, kdh1 of its folder.</summary>
/// <param name="Id">The mod's id.</param>
/// <param name="Version">The mod's version string.</param>
/// <param name="Hash">The kdh1 hash of the mod's folder (see <see cref="DirHash"/>).</param>
/// <param name="Kind">Either <c>kiln</c> (a Kiln mod) or <c>native</c> (a native mod). Default <c>kiln</c>.</param>
/// <param name="Name">The mod's display name, or null.</param>
public sealed record NetRequiredMod(string Id, string Version, string Hash, string Kind = "kiln", string? Name = null)
{
    internal JsonObject ToJson(bool withName = false)
    {
        var o = new JsonObject { ["id"] = Id, ["version"] = Version, ["hash"] = Hash };
        if (Kind != "kiln") o["kind"] = Kind;
        if (withName && Name != null) o["name"] = Name;
        return o;
    }

    /// <summary>The schema's requiredMod object (kind always present).</summary>
    internal JsonObject ToJsonFull()
    {
        var o = new JsonObject { ["id"] = Id, ["version"] = Version, ["hash"] = Hash, ["kind"] = Kind };
        if (Name != null) o["name"] = Name;
        return o;
    }
}

/// <summary>What a server advertises in REJECT/WELCOME. Server side (<see cref="Network.Advertised"/>): this server,
/// everything; client side (<see cref="Network.ServerAdvert"/>): what the server sent, <see cref="Truncated"/> when
/// required[] was cut to keep the frame under 7 KB (<see cref="RequiredCount"/> is the full count).</summary>
/// <param name="ServerId">The server's stable id, or null when the handshake carried none.</param>
/// <param name="ServerName">The name the server sends to clients, or null.</param>
/// <param name="Kiln">The server's Kiln version, or null when it is not known.</param>
/// <param name="Modpack">Where players get the server's mods, or null when the server names no modpack.</param>
/// <param name="Required">The mods players need, with their hashes. On the client side this list can be cut short (see <paramref name="Truncated"/>).</param>
/// <param name="RequiredCount">How many mods players need in all, also when <paramref name="Required"/> was cut short.</param>
/// <param name="Truncated">True when <paramref name="Required"/> holds fewer mods than <paramref name="RequiredCount"/>.</param>
/// <param name="Pending">True while the server has not finished hashing the required mods, so their hashes are not complete yet.</param>
public sealed record NetServerAdvert(string? ServerId, string? ServerName, string? Kiln, NetModpack? Modpack,
    IReadOnlyList<NetRequiredMod> Required, int RequiredCount, bool Truncated, bool Pending)
{
    /// <summary>The handshake carried Kiln's server properties (false: an old Kiln server, or advertise off).</summary>
    public bool Present => ServerId != null;
}

internal static partial class NetAdvertise
{
    // property keys (values are text; the old decoder allows keys <= 128 bytes and values <= 4096 bytes)
    public const string KeyId = "kiln.server.id", KeyName = "kiln.server.name", KeyKiln = "kiln.server.kiln";
    public const string KeyUrl = "kiln.modpack.url", KeySha = "kiln.modpack.sha256", KeyPackName = "kiln.modpack.name",
        KeyPackVersion = "kiln.modpack.version", KeyPackSize = "kiln.modpack.size";
    public const string KeyRequired = "kiln.required";            // JSON array chunk; more chunks: kiln.required.1, .2, ...
    public const string KeyCount = "kiln.required.count";         // total required mods
    public const string KeyMore = "kiln.required.more";           // how many did not fit (only when truncated)
    public const string KeyPending = "kiln.required.pending";     // "1": hashes not computed yet
    public const int FrameBudget = 7 * 1024;
    public const int ChunkBytes = 4000;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex ServerIdPattern();

    public static bool IsSha256(string s) => s.Length == 64 && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsModpackUrl(string s) =>
        s.Length <= 2048 && (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             s.StartsWith("file://", StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------------------------------------ identity

    private static string? _generatedId;
    private static (DateTime Stamp, string? Name) _nameCache;

    public static string IdFile => Path.Combine(KilnPaths.Root, "data", "kiln-network", "server-id");

    /// <summary>network.json serverId, else kiln/data/kiln-network/server-id (generated once: "srv-" + 12 hex).</summary>
    public static string ServerId(NetworkConfig c)
    {
        if (c.ServerId is { } configured)
        {
            if (ServerIdPattern().IsMatch(configured)) return configured;
            NetEngine.Log(LogLevel.Warn, $"network.json serverId '{configured}' is not a valid id ([A-Za-z0-9][A-Za-z0-9._-]{{0,63}}); using the generated one");
        }
        if (_generatedId is { } g) return g;
        try
        {
            var path = IdFile;
            if (File.Exists(path) && File.ReadAllText(path).Trim() is var text && ServerIdPattern().IsMatch(text)) return _generatedId = text;
            var id = "srv-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", id + "\n");
            File.Move(path + ".tmp", path, overwrite: true);
            NetEngine.Log(LogLevel.Info, $"server id {id} (written to {path})");
            return _generatedId = id;
        }
        catch (Exception e)
        {
            NetEngine.Log(LogLevel.Warn, $"server id: {e.Message}");
            return _generatedId = "srv-unknown";
        }
    }

    /// <summary>network.json serverName, else enshrouded_server.json name (null on a client without one).</summary>
    public static string? ServerName(NetworkConfig c)
    {
        if (!string.IsNullOrWhiteSpace(c.ServerName)) return Clip(c.ServerName.Trim(), 128);
        try
        {
            var file = Path.Combine(KilnPaths.Server, "enshrouded_server.json");
            if (!File.Exists(file)) return null;
            var stamp = File.GetLastWriteTimeUtc(file);
            if (_nameCache.Stamp == stamp) return _nameCache.Name;
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var name = doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? Clip(n.GetString()!.Trim(), 128) : null;
            _nameCache = (stamp, name);
            return name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    public static void Reset() => _generatedId = null;

    // ------------------------------------------------------------------------------------------------ required mods

    internal sealed record Candidate(string Kind, string Id, string Version, string? Name, string Dir);

    private static volatile NetRequiredMod[] _required = [];
    private static volatile string[] _kilnClientMods = [];
    private static volatile string[] _statedModClients = [];
    private static volatile bool _ready;
    private static int _computing;
    private static string _lastSignature = "";
    private static long _lastFull;
    private static readonly Dictionary<string, (string Sig, string Hash)> HashCache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);

    public static IReadOnlyList<NetRequiredMod> Required => _required;

    /// <summary>Loaded mods whose manifest says <c>clients: "kiln"</c> or <c>"mod"</c> out loud: what they add can't be
    /// shown by a vanilla client, so vanilla clients are refused (see <see cref="VanillaGate.ShouldRefuse"/>). A
    /// default from <c>side</c> doesn't count: many <c>side: both</c> mods work with vanilla clients.</summary>
    public static IReadOnlyList<string> KilnClientMods => _kilnClientMods;

    /// <summary>Loaded Kiln mods with <c>side: both</c> whose manifest says <c>clients: "mod"</c> out loud: a Kiln client
    /// without one is refused under the default policy. Native mods and other sides aren't here: the handshake doesn't list
    /// them, so a client that has one couldn't show it.</summary>
    public static IReadOnlyList<string> StatedModClients => _statedModClients;
    public static bool RequiredReady => _ready;

    /// <summary>Server tick, about once a second: recompute when the mod list changed, or every 60 s (cheap when no
    /// folder changed: each folder's tree signature is compared before hashing).</summary>
    public static void Tick()
    {
        var snapshot = Snapshot();
        var sig = Signature(snapshot.Select(m => (m.Id, m.Version, m.State, m.Directory)));
        var now = Environment.TickCount64;
        if (sig == _lastSignature && now - _lastFull < 60_000) return;
        Refresh(snapshot, sig);
    }

    /// <summary>What a recompute follows: each mod's id, version, state and folder, and when its kiln.mod.json was written.
    /// The last one: a `reload` that changes only "clients" (the version kept) changed what joining
    /// players need, and the gate went on with the old answer for up to a minute, so a vanilla client could still join a
    /// server whose reloaded mod now needs Kiln.</summary>
    internal static string Signature(IEnumerable<(string Id, string Version, ModState State, string Directory)> mods) =>
        string.Join("\n", mods.Select(m => $"{m.Id}\0{m.Version}\0{m.State}\0{m.Directory}\0{ManifestStamp(m.Directory)}"));

    private static long ManifestStamp(string directory)
    {
        try { return directory.Length == 0 ? 0 : File.GetLastWriteTimeUtc(Path.Combine(directory, "kiln.mod.json")).Ticks; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return 0; }
    }

    /// <summary>Recompute now (network_reload, a manual refresh).</summary>
    public static void Refresh() => Refresh(Snapshot(), null);

    private static void Refresh(List<ModEntry> snapshot, string? sig)
    {
        if (Interlocked.CompareExchange(ref _computing, 1, 0) != 0) return;
        if (sig != null) _lastSignature = sig;
        _lastFull = Environment.TickCount64;
        var kilnRoot = KilnPaths.Root;
        Task.Run(() =>
        {
            try
            {
                Compute(snapshot, kilnRoot);
            }
            catch (Exception e)
            {
                NetEngine.Log(LogLevel.Warn, $"required mods: {e.Message}");
            }
            finally
            {
                Volatile.Write(ref _computing, 0);
            }
        });
    }

    private static List<ModEntry> Snapshot()
    {
        try
        {
            return [.. Mods.All];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void Compute(List<ModEntry> mods, string kilnRoot)
    {
        var candidates = new List<Candidate>();
        var kilnClient = new List<string>();
        var statedMod = new List<string>();
        foreach (var m in mods)
        {
            if (!Counts(m.State, m.Error, m.Directory)) continue;
            var declared = DeclaredClients(Path.Combine(m.Directory, "kiln.mod.json"));
            if (declared is "kiln" or "mod") kilnClient.Add(m.Id);
            if (declared == "mod" && m.Side == ModSide.Both) statedMod.Add(m.Id);
            if (ClientsOf(Path.Combine(m.Directory, "kiln.mod.json"), "server") == "mod")
                candidates.Add(new Candidate("kiln", m.Id, m.Version, m.Name, m.Directory));
        }
        var nativeRoot = Path.Combine(kilnRoot, "native");
        if (Directory.Exists(nativeRoot))
            foreach (var dir in Directory.GetDirectories(nativeRoot))
            {
                if (Path.GetFileName(dir).StartsWith('.') || File.Exists(Path.Combine(dir, ".disabled"))) continue;
                var manifest = Path.Combine(dir, "native.json");
                if (!File.Exists(manifest)) continue;
                if (DeclaredClients(manifest) is "kiln" or "mod" && NativeIdentity(manifest) is { } declared) kilnClient.Add(declared.Id);
                if (ClientsOf(manifest, "client") != "mod") continue;
                if (NativeIdentity(manifest) is { } ident) candidates.Add(new Candidate("native", ident.Id, ident.Version, ident.Name, dir));
            }
        candidates.AddRange(EngineCandidates(kilnRoot));  // other engines' mods (AdvertiseEngines.cs)
        var game = Path.GetDirectoryName(Path.GetFullPath(kilnRoot)) ?? kilnRoot;
        var oldKilnClient = _kilnClientMods;
        _kilnClientMods = [.. kilnClient.Distinct()];
        _statedModClients = [.. statedMod.Distinct()];
        if (!oldKilnClient.SequenceEqual(_kilnClientMods) && _kilnClientMods.Length > 0)
            NetEngine.Log(LogLevel.Info, $"vanilla clients are refused: these mods need Kiln on the client: {string.Join(", ", _kilnClientMods.Take(12))}");
        var list = new List<NetRequiredMod>();
        foreach (var c in candidates.DistinctBy(c => (c.Kind, c.Id)))
        {
            string hash;
            try
            {
                var tree = IsEngineKind(c.Kind) ? EngineSignature(c.Dir, game) : DirHash.Signature(c.Dir, c.Kind == "kiln");
                lock (HashCache)
                    if (HashCache.TryGetValue(c.Dir, out var cached) && cached.Sig == tree)
                    {
                        list.Add(new NetRequiredMod(c.Id, c.Version, cached.Hash, c.Kind, c.Name));
                        continue;
                    }
                hash = AdvertisedHash(c.Kind, c.Dir, game);
                lock (HashCache) HashCache[c.Dir] = (tree, hash);
            }
            catch (Exception e) when (e is DirHashException or IOException or UnauthorizedAccessException)
            {
                lock (Warned)
                    if (Warned.Add(c.Dir)) NetEngine.Log(LogLevel.Warn, $"required mod {c.Id}: cannot hash {c.Dir}: {e.Message} (not advertised)");
                continue;
            }
            list.Add(new NetRequiredMod(c.Id, c.Version, hash, c.Kind, c.Name));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Kind + "\0" + a.Id, b.Kind + "\0" + b.Id));
        var old = _required;
        _required = [.. list];
        var first = !_ready;
        _ready = true;
        if (first || !old.SequenceEqual(list))
            NetEngine.Log(LogLevel.Info, $"advertising {list.Count} mod(s) players need: " +
                                          (list.Count == 0 ? "none" : string.Join(", ", list.Take(12).Select(r => $"{r.Id} {r.Version} {r.Hash[..13]}"))));
    }

    /// <summary>Another engine's kind, advertised by <see cref="EngineHash"/>: not <c>kiln</c>, <c>native</c> or <c>emf</c>
    /// (emf is one of Kiln's own kinds for kiln-manage: ModKind::CORE, not is_engine, whose engine_identity does not apply
    /// to it).</summary>
    internal static bool IsEngineKind(string kind) => kind is not ("kiln" or "native" or "emf");

    /// <summary>The hash required[] carries for a mod of <paramref name="kind"/> in <paramref name="dir"/>, the same on the
    /// server that advertises it and on the client that compares its own copy (LastServer.Missing). A Kiln mod by its
    /// identity: its files without what the cook made on this machine (assets/cooked), so a server's cooked folder and a
    /// player's give the same hash; another engine's mod by what stays the same while it runs (AdvertiseIdentity.cs: the
    /// Library entry of Kiln's copy, else its files without logs and settings); a native or EMF mod's folder is all its
    /// own. <paramref name="game"/>: the game or server folder whose kiln/state holds the receipts.</summary>
    internal static string AdvertisedHash(string kind, string dir, string game) =>
        kind == "kiln" ? DirHash.ModIdentity(dir) : IsEngineKind(kind) ? EngineHash(kind, dir, game) : DirHash.Compute(dir);

    /// <summary>Kiln.Host's reason for a mod whose profile entry is off (Mods.cs, "disabled in profile").</summary>
    internal const string DisabledInProfile = "disabled in profile";

    /// <summary>Whether a mod of the host's list counts for what players need: not a failed one, and not one switched off
    /// (a <c>.disabled</c> file, or its profile entry off: Skipped "disabled in profile"; until 0.2.0-dev.29.11 such a mod
    /// was still advertised, and a game hosting its world refused its own player, who had it off too). A mod skipped for
    /// its side (a client mod on a server) still counts: players need it.</summary>
    internal static bool Counts(ModState state, string? error, string? directory) =>
        !string.IsNullOrEmpty(directory) && state != ModState.Failed
        && !(state == ModState.Skipped && error is { } e && e.StartsWith(DisabledInProfile, StringComparison.Ordinal))
        && !File.Exists(Path.Combine(directory, ".disabled"));

    /// <summary>The <c>clients</c> a manifest states itself, without a default; null when it says nothing.</summary>
    internal static string? DeclaredClients(string manifest)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("clients", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A manifest's <c>clients</c> (none|kiln|mod), defaulting from <c>side</c> (server: none, else mod).</summary>
    internal static string? ClientsOf(string manifest, string defaultSide)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("clients", out var c) && c.ValueKind == JsonValueKind.String) return c.GetString();
            var side = root.TryGetProperty("side", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : defaultSide;
            return side == "server" ? "none" : "mod";
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (string Id, string Version, string? Name)? NativeIdentity(string manifest)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var r = doc.RootElement;
            string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return S("id") is { } id && S("version") is { } ver ? (id, ver, S("name")) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------------------------------------ encoding

    private static int VarLen(int n)
    {
        var len = 1;
        while (n >= 0x80)
        {
            n >>= 7;
            len++;
        }
        return len;
    }

    internal static int PropCost(string key, string value)
    {
        var k = Encoding.UTF8.GetByteCount(key);
        var v = Encoding.UTF8.GetByteCount(value);
        return VarLen(k) + k + VarLen(v) + v;
    }

    /// <summary>The kiln.* properties to append to a REJECT/WELCOME whose other parts (and other properties) take
    /// <paramref name="frameBytes"/> bytes, so that the whole frame stays within <see cref="FrameBudget"/>.
    /// required[] is cut when it does not fit (<c>kiln.required.more</c> = how many were left out).</summary>
    public static Dictionary<string, string> Properties(NetworkConfig c, int frameBytes, out bool truncated) =>
        Properties(c, frameBytes, _required, _ready, out truncated);

    internal static Dictionary<string, string> Properties(NetworkConfig c, int frameBytes, IReadOnlyList<NetRequiredMod> required,
        bool ready, out bool truncated)
    {
        truncated = false;
        var props = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KeyId] = ServerId(c),
            [KeyKiln] = Game.KilnVersion,
        };
        if (ServerName(c) is { } name) props[KeyName] = name;
        if (c.Modpack is { } mp)
        {
            props[KeyUrl] = mp.Url;
            props[KeySha] = mp.Sha256;
            if (mp.Name != null) props[KeyPackName] = Clip(mp.Name, 256);
            if (mp.Version != null) props[KeyPackVersion] = Clip(mp.Version, 64);
            if (mp.Size != null) props[KeyPackSize] = mp.Size.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        props[KeyCount] = required.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!ready) props[KeyPending] = "1";
        // what is left for the required chunks (+ 2 bytes for a grown property count, + the "more" key)
        var budget = FrameBudget - frameBytes - props.Sum(kv => PropCost(kv.Key, kv.Value)) - 2 - PropCost(KeyMore, "99999");
        var chunks = new List<StringBuilder>();
        var used = 0;
        var sent = 0;
        foreach (var r in required)
        {
            var json = r.ToJson().ToJsonString();
            var n = Encoding.UTF8.GetByteCount(json);
            var cur = chunks.Count > 0 ? chunks[^1] : null;
            var curBytes = cur is null ? 0 : Encoding.UTF8.GetByteCount(cur.ToString());
            int delta;
            bool fresh;
            if (cur is not null && curBytes + 1 + n <= ChunkBytes)
            {
                // grow the current chunk: its value gets ",<json>" (and maybe a longer length varint)
                delta = 1 + n + VarLen(curBytes + 1 + n) - VarLen(curBytes);
                fresh = false;
            }
            else
            {
                if (n + 2 > ChunkBytes) continue;  // cannot happen with ids/versions/hashes of the allowed sizes
                var key = ChunkKey(chunks.Count);
                delta = PropCost(key, new string('x', n + 2));
                fresh = true;
            }
            if (used + delta > budget)
            {
                truncated = true;
                break;
            }
            used += delta;
            sent++;
            if (fresh) chunks.Add(new StringBuilder("[").Append(json).Append(']'));
            else cur!.Insert(cur.Length - 1, "," + json);
        }
        for (var i = 0; i < chunks.Count; i++) props[ChunkKey(i)] = chunks[i].ToString();
        if (sent < required.Count)
        {
            truncated = true;
            props[KeyMore] = (required.Count - sent).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return props;
    }

    private static string ChunkKey(int i) => i <= 0 ? KeyRequired : KeyRequired + "." + i;

    // ------------------------------------------------------------------------------------------------ client side

    /// <summary>The server's kiln.* properties from a REJECT/WELCOME (empty advert when absent).</summary>
    public static NetServerAdvert Parse(IReadOnlyDictionary<string, string> p, string? kilnVersion)
    {
        string? Get(string k) => p.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
        NetModpack? modpack = null;
        if (Get(KeyUrl) is { } url && Get(KeySha)?.ToLowerInvariant() is { } sha && IsSha256(sha) && IsModpackUrl(url))
            modpack = new NetModpack(url, sha, Get(KeyPackName), Get(KeyPackVersion),
                long.TryParse(Get(KeyPackSize), out var size) && size >= 0 ? size : null);
        var required = new List<NetRequiredMod>();
        for (var i = 0; i < 256; i++)
        {
            if (Get(ChunkKey(i)) is not { } chunk) break;
            try
            {
                if (JsonNode.Parse(chunk) is not JsonArray a) continue;
                foreach (var n in a)
                {
                    if (n is not JsonObject o) continue;
                    string? S(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                    if (S("id") is { } id && S("version") is { } ver && S("hash") is { } hash)
                        required.Add(new NetRequiredMod(id, ver, hash, S("kind") ?? "kiln", S("name")));
                }
            }
            catch (JsonException)
            {
            }
        }
        var count = int.TryParse(Get(KeyCount), out var c) ? c : required.Count;
        var more = int.TryParse(Get(KeyMore), out var m) ? m : 0;
        return new NetServerAdvert(Get(KeyId), Get(KeyName), Get(KeyKiln) ?? kilnVersion, modpack, required,
            Math.Max(count, required.Count + more), more > 0 || count > required.Count, Get(KeyPending) == "1");
    }
}
