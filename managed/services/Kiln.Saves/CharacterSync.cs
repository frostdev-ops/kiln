// SPDX-License-Identifier: MIT
// Carries CharacterData.Carried with the character, the way the game carries its character save (docs/re/characters.md):
// the client holds the character, offers it when it joins, and the server sends it back at each world save.
//
// The game's own path can't carry this: the character file has a fixed list of block types, and its player_knowledge drops
// ids the server's static map lacks at every load (a character passing through a server without the mod would lose them). So
// channel "kiln:character" v1 (not required), and a sidecar in the client's kiln/data/characters/, never in the save folder:
// the game's migration scan moves unknown files there, and Steam cloud owns the other set. In a hosted game the local player's
// sidecar is read and written directly.
//
//   server -> client  CharHello    the player is in game: which character, does this server take imports
//   client -> server  CharOffer    the sidecar: revision + mod -> key -> JSON
//   server -> client  CharResult   what happened to the offer
//   server -> client  CharUpdate   changes since what the client has, at a world save (also: the server's copy was newer)
//   client -> server  CharAck      written
//
// The newest revision wins; on a tie the server's copy stays. A vanilla client never answers: its data stays on the server.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

public sealed record CharHello(Guid Character, string? World, bool Imports, int MaxBytes, long Revision);

public sealed record CharOffer(Guid Character, long Revision, Dictionary<string, Dictionary<string, JsonElement>> Mods);

public sealed record CharResult(Guid Character, bool Accepted, string Reason, long Revision, List<string> Applied, List<string> Kept, List<string> Dropped);

/// <summary>A null value removes the key.</summary>
public sealed record CharUpdate(Guid Character, long Revision, Dictionary<string, Dictionary<string, JsonElement?>> Changes, bool Final);

public sealed record CharAck(Guid Character, long Revision);

/// <summary>Lab peers (tools/nettest) have no player: they say which character they pretend to be.</summary>
public sealed record CharLabJoin(Guid Character, ulong Account, bool Local = false);

public sealed record CharLabLeave(Guid Character);

internal static class CharacterSync
{
    internal const string ChannelName = "kiln:character";
    private static NetChannel? _channel;
    private static bool _started;
    private static ulong _ticks;
    private static CharacterSettings _settings = new();
    private static Guid _clientCharacter;
    private static long _imports, _refused, _kept, _dropped, _updates, _updateBytes, _sidecarWrites;
    private static readonly Dictionary<uint, Session> Sessions = new();
    private static readonly Dictionary<uint, Session> LabSessions = new();

    private sealed class Session(CharacterKey key, EntityId entity, string name)
    {
        public readonly CharacterKey Key = key;
        public readonly EntityId Entity = entity;
        public readonly string Name = name;
        public NetPeer? Peer;
        public bool Local, HelloSent, Speaks, Offered;

        /// <summary>What the client has (per its last ack), what went out last.</summary>
        public Dictionary<(string Mod, string Key), string> Acked = new();
        public Dictionary<(string Mod, string Key), string>? Sent;
        public long SentRevision;
        public override string ToString() => $"{Name} character {Key}";
    }

    public static CharacterSyncStatus Status =>
        new(_started, _channel is not null, _settings.ImportProgress, Sessions.Count + LabSessions.Count, _imports, _refused, _kept, _dropped, _updates, _updateBytes, _sidecarWrites);

    public static void EnsureStarted()
    {
        if (_started) return;
        _started = true;
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;  // the channel and the tick are Kiln's, not the calling mod's
        try
        {
            if (Network.Available)
                _channel = Network.Channel(ChannelName, "1", required: false, maxMessageBytes: 256 * 1024)
                    .RegisterJson<CharHello>(OnHello, NetDirection.ToClient)
                    .RegisterJson<CharOffer>(OnOffer, NetDirection.ToServer)
                    .RegisterJson<CharResult>(OnResult, NetDirection.ToClient)
                    .RegisterJson<CharUpdate>(OnUpdate, NetDirection.ToClient)
                    .RegisterJson<CharAck>(OnAck, NetDirection.ToServer)
                    .RegisterJson<CharLabJoin>(OnLabJoin, NetDirection.ToServer)
                    .RegisterJson<CharLabLeave>(OnLabLeave, NetDirection.ToServer);
            ServiceTick.Add("characters", Tick);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            SavesRuntime.Log(LogLevel.Warn, $"Kiln.Saves: channel {ChannelName}: {e.Message}");
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    // ---------------------------------------------------------------------------------------------- server side ----
    private static void Tick(in TickInfo tick)
    {
        if (++_ticks % 25 != 0 || !Lifecycle.IsServerRunning || !Players.Available) return;
        var present = new HashSet<uint>();
        foreach (var p in Players.All)
        {
            if (!p.InGame || p.Entity.IsNone) continue;
            present.Add(p.Entity.Value);
            var key = CharacterStore.KeyOf(p);
            if (Sessions.TryGetValue(p.Entity.Value, out var s))
            {
                if (s.Key == key) continue;
                Close(s);
                Sessions.Remove(p.Entity.Value);
            }
            if (key.IsNone) continue;
            CharacterStore.RememberLast(key, CharacterStore.LegacyIds(p));
            if (key.IsAccountOnly) continue;  // no character id on this build: nothing can travel
            s = Sessions[p.Entity.Value] = new Session(key, p.Entity, p.Name);
            _settings = CharacterSettings.Load();
            s.Local = Client.Available && Client.IsHosting && (p.Slot == 0 || Client.LocalPlayer == p.Entity);
            SavesRuntime.Log(LogLevel.Info, $"characters: {s} in game{(s.Local ? " (this machine's player)" : "")}; imports {(_settings.ImportProgress ? "allowed" : "refused here")}");
            if (s.Local) OfferSidecar(s);
        }
        foreach (var gone in Sessions.Keys.Where(e => !present.Contains(e)).ToArray())
        {
            Close(Sessions[gone]);
            Sessions.Remove(gone);
        }
        foreach (var s in Sessions.Values)
        {
            if (s.Local || s.HelloSent || _channel is null) continue;
            if (Network.PeerOf(s.Entity) is not { State: NetPeerState.Ready } peer || !peer.HasChannel(ChannelName)) continue;
            s.Peer = peer;
            SendHello(s);
        }
    }

    /// <summary>A hosted game: this machine's player. Client and server are one process, so the sidecar is read directly.</summary>
    private static void OfferSidecar(Session s)
    {
        var file = CharacterSidecar.Load(s.Key.Id);
        var r = Decide(s, file.Revision, file.Mods);
        SavesRuntime.Log(LogLevel.Info, $"characters: {s}: its sidecar (revision {file.Revision}): {r.Reason}");
    }

    private static void SendHello(Session s)
    {
        s.HelloSent = true;
        _channel!.SendTo(s.Peer!, new CharHello(s.Key.Id, SavesRuntime.WorldId, _settings.ImportProgress, _settings.MaxImportBytes, CharacterStore.RevisionOf(s.Key)));
    }

    private static void Close(Session s)
    {
        try
        {
            Push(s, final: true);
        }
        catch (Exception e)
        {
            SavesRuntime.Log(LogLevel.Warn, $"characters: closing {s}: {e.Message}");
        }
        SavesRuntime.Log(LogLevel.Info, $"characters: {s} left");
    }

    /// <summary>A world save: where the game sends each character back. Tick thread.</summary>
    internal static void OnWorldSaving(SaveReason reason)
    {
        if (!_started) return;
        foreach (var s in Sessions.Values.Concat(LabSessions.Values))
        {
            try
            {
                Push(s, final: reason == SaveReason.Shutdown);
            }
            catch (Exception e)
            {
                SavesRuntime.Log(LogLevel.Warn, $"characters: sending {s} back: {e.Message}");
            }
        }
    }

    /// <summary>A hosted world closed: its save has run. The next world starts over.</summary>
    internal static void OnWorldClosing()
    {
        if (!_started) return;
        foreach (var s in Sessions.Values.Concat(LabSessions.Values)) Close(s);
        Sessions.Clear();
        LabSessions.Clear();
    }

    private static Dictionary<(string Mod, string Key), string> Flat(IReadOnlyDictionary<string, Dictionary<string, string>> mods)
    {
        var d = new Dictionary<(string, string), string>();
        foreach (var (m, keys) in mods)
            foreach (var (k, v) in keys)
                d[(m, k)] = v;
        return d;
    }

    /// <summary>One text per value, whoever wrote it, so a comparison sees real changes only.</summary>
    private static Dictionary<(string Mod, string Key), string> Canon(Dictionary<(string Mod, string Key), string> d)
    {
        foreach (var at in d.Keys.ToArray())
        {
            try
            {
                d[at] = JsonNode.Parse(d[at])?.ToJsonString() ?? "null";
            }
            catch (JsonException)
            {
                d.Remove(at);
            }
        }
        return d;
    }

    private static CharResult Answer(Session s, bool accepted, string reason, List<string>? applied = null, List<string>? kept = null, List<string>? dropped = null) =>
        new(s.Key.Id, accepted, reason, CharacterStore.RevisionOf(s.Key), applied ?? [], kept ?? [], dropped ?? []);

    /// <summary>Handles what a client (or this machine's sidecar) offers.</summary>
    private static CharResult Decide(Session s, long revision, Dictionary<string, Dictionary<string, string>> offered)
    {
        if (!_settings.ImportProgress)
        {
            _refused++;
            s.Speaks = false;
            return Answer(s, false, "this server keeps its own copy (characters.json importProgress false): nothing is taken from or sent to your copy");
        }
        var serverRevision = CharacterStore.RevisionOf(s.Key);
        if (revision <= serverRevision)
        {
            // Our copy stays: the client missed something (a crash, a lost sidecar), or there is nothing new. It gets what it lacks.
            s.Speaks = true;
            s.Acked = Canon(Flat(offered));
            Push(s, final: false);
            return Answer(s, false, $"the server's copy is as new or newer (revision {serverRevision} vs {revision})");
        }
        var clean = Validate(s, offered, out var dropped, out var refusal);
        if (refusal is not null)
        {
            _refused++;
            s.Speaks = false;
            SavesRuntime.Log(LogLevel.Warn, $"characters: {s}: offer refused: {refusal}");
            return Answer(s, false, refusal);
        }
        if (Events.HasSubscribers<CharacterImportingEvent>())
        {
            var view = clean.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.Keys.ToList(), StringComparer.Ordinal);
            var ev = Events.Post(new CharacterImportingEvent(s.Key, s.Entity, revision, serverRevision, view));
            if (ev.Canceled)
            {
                _refused++;
                s.Speaks = false;
                SavesRuntime.Log(LogLevel.Info, $"characters: {s}: offer refused: {ev.Reason}");
                return Answer(s, false, ev.Reason);
            }
        }
        var loaded = Mods.All.Where(m => m.IsLoaded).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var applied = clean.Keys.Where(m => loaded.Contains(m) || m.StartsWith("kiln-", StringComparison.Ordinal)).ToList();
        var kept = clean.Keys.Except(applied).ToList();
        CharacterStore.Replace(s.Key, clean);
        CharacterStore.SetRevision(s.Key, revision);
        s.Speaks = true;
        s.Acked = Canon(Flat(clean));
        s.Sent = null;
        _imports++;
        _kept += kept.Count;
        _dropped += dropped.Count;
        SavesRuntime.Log(LogLevel.Info, $"characters: {s}: took its carried data, revision {serverRevision} -> {revision}: applied [{string.Join(", ", applied)}], " +
                                        $"kept unread [{string.Join(", ", kept)}], dropped [{string.Join(", ", dropped)}]");
        if (Events.HasSubscribers<CharacterImportedEvent>()) Events.Post(new CharacterImportedEvent(s.Key, s.Entity, revision, applied, kept));
        return Answer(s, true, "taken", applied, kept, dropped);
    }

    /// <summary>The offer's checks. A broken value is dropped from what the server takes (the client keeps its file); caps refuse the
    /// whole offer. Values are parsed here only to prove they are JSON; a mod reads them as its own type later.</summary>
    private static Dictionary<string, Dictionary<string, string>> Validate(Session s, Dictionary<string, Dictionary<string, string>> offered, out List<string> dropped, out string? refusal)
    {
        dropped = [];
        refusal = null;
        var clean = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        long total = 0;
        var keys = 0;
        foreach (var (mod, map) in offered)
        {
            if (!ValidModId(mod))
            {
                dropped.Add(mod);
                continue;
            }
            foreach (var (k, json) in map)
            {
                keys++;
                total += json.Length;
                if (k.Length is 0 or > 200 || json.Length > _settings.MaxValueBytes || !IsJson(json))
                {
                    dropped.Add($"{mod}/{k}");
                    continue;
                }
                if (!clean.TryGetValue(mod, out var c)) clean[mod] = c = new Dictionary<string, string>(StringComparer.Ordinal);
                c[k] = json;
            }
        }
        if (total > _settings.MaxImportBytes) refusal = $"{total} bytes (limit {_settings.MaxImportBytes})";
        else if (keys > _settings.MaxKeys) refusal = $"{keys} keys (limit {_settings.MaxKeys})";
        else if (CharacterStore.CharactersOfAccount(s.Key.Account) >= _settings.MaxCharactersPerAccount && !CharacterStore.Snapshot(s.Key).Any())
            refusal = $"this account already has {_settings.MaxCharactersPerAccount} characters here";
        return clean;
    }

    internal static bool ValidModId(string id) =>
        id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':');

    private static bool IsJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Sends the client what it lacks, as a new revision. Nothing is sent when nothing changed.</summary>
    private static void Push(Session s, bool final)
    {
        if (!s.Speaks || s.Key.Id == Guid.Empty) return;
        var snap = Canon(Flat(CharacterStore.Snapshot(s.Key)));
        var changes = new Dictionary<string, Dictionary<string, JsonElement?>>(StringComparer.Ordinal);
        long bytes = 0;
        void Add((string Mod, string Key) at, JsonElement? v)
        {
            if (!changes.TryGetValue(at.Mod, out var m)) changes[at.Mod] = m = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
            m[at.Key] = v;
        }
        foreach (var (at, json) in snap)
        {
            if (s.Acked.TryGetValue(at, out var old) && old == json) continue;
            bytes += json.Length;
            Add(at, Parse(json));
        }
        foreach (var at in s.Acked.Keys)
            if (!snap.ContainsKey(at)) Add(at, null);
        if (changes.Count == 0) return;
        if (bytes > _settings.MaxImportBytes)
        {
            SavesRuntime.Log(LogLevel.Error, $"characters: {s}: {bytes} bytes of changes exceed the limit {_settings.MaxImportBytes}; they stay on this server");
            return;
        }
        var revision = s.SentRevision;
        if (s.Sent is null || !Same(s.Sent, snap))
        {
            revision = CharacterStore.RevisionOf(s.Key) + 1;
            CharacterStore.SetRevision(s.Key, revision);
        }
        s.Sent = snap;
        s.SentRevision = revision;
        _updates++;
        _updateBytes += bytes;
        if (s.Local)
        {
            CharacterSidecar.Apply(s.Key.Id, revision, changes, SavesRuntime.WorldId);
            _sidecarWrites++;
            s.Acked = snap;
            SavesRuntime.Log(LogLevel.Debug, $"characters: {s}: sidecar at revision {revision}");
            return;
        }
        if (s.Peer is null || _channel is null) return;
        var sent = _channel.SendTo(s.Peer, new CharUpdate(s.Key.Id, revision, changes, final));
        SavesRuntime.Log(LogLevel.Debug, $"characters: {s}: update revision {revision}, {changes.Sum(c => c.Value.Count)} key(s), {bytes} bytes{(final ? ", final" : "")}{(sent ? "" : " (not sent)")}");
    }

    private static bool Same(Dictionary<(string, string), string> a, Dictionary<(string, string), string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Session? SessionOf(NetContext ctx)
    {
        if (ctx.Sender.IsLab && ctx.Player.IsNone) return LabSessions.GetValueOrDefault(ctx.Sender.Id);
        return ctx.Player.IsNone ? null : Sessions.GetValueOrDefault(ctx.Player.Value);
    }

    private static void OnOffer(CharOffer m, NetContext ctx)
    {
        if (SessionOf(ctx) is not { } s) return;
        if (m.Character != s.Key.Id)
        {
            _refused++;
            SavesRuntime.Log(LogLevel.Warn, $"characters: {s}: an offer for another character ({m.Character}) was refused");
            ctx.Reply(Answer(s, false, "that is not the character you joined with"));
            return;
        }
        if (s.Offered) return;  // one per session: the character came in once, it doesn't change under a running game
        s.Offered = true;
        s.Peer ??= ctx.Sender;
        var offered = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (mod, keys) in m.Mods ?? [])
        {
            var d = offered[mod] = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in keys ?? []) d[k] = v.GetRawText();
        }
        ctx.Reply(Decide(s, m.Revision, offered));
    }

    private static void OnAck(CharAck m, NetContext ctx)
    {
        if (SessionOf(ctx) is not { } s || m.Character != s.Key.Id || s.Sent is null) return;
        if (m.Revision == s.SentRevision) s.Acked = s.Sent;
    }

    private static void OnLabJoin(CharLabJoin m, NetContext ctx)
    {
        if (!ctx.Sender.IsLab || _channel is null) return;
        var key = new CharacterKey(m.Account, m.Character);
        if (LabSessions.TryGetValue(ctx.Sender.Id, out var old)) Close(old);
        _settings = CharacterSettings.Load();
        var s = LabSessions[ctx.Sender.Id] = new Session(key, EntityId.None, "lab:" + ctx.Sender.Name) { Peer = ctx.Sender, Local = m.Local };
        SavesRuntime.Log(LogLevel.Info, $"characters: {s} in game (lab peer{(m.Local ? ", as a hosted game's local player" : "")}); imports {(_settings.ImportProgress ? "allowed" : "refused here")}");
        if (m.Local) OfferSidecar(s);
        else SendHello(s);
    }

    private static void OnLabLeave(CharLabLeave m, NetContext ctx)
    {
        if (!ctx.Sender.IsLab || !LabSessions.Remove(ctx.Sender.Id, out var s)) return;
        Close(s);
    }

    // ---------------------------------------------------------------------------------------------- client side ----
    private static void OnHello(CharHello m, NetContext ctx)
    {
        _clientCharacter = m.Character;
        if (!m.Imports)
        {
            SavesRuntime.Log(LogLevel.Info, $"characters: this server keeps its own copy of character {m.Character}'s progress; your sidecar stays as it is");
            return;
        }
        var file = CharacterSidecar.Load(m.Character);
        var mods = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
        foreach (var (mod, keys) in file.Mods)
            mods[mod] = keys.ToDictionary(kv => kv.Key, kv => Parse(kv.Value), StringComparer.Ordinal);
        var size = file.Mods.Sum(x => x.Value.Sum(y => (long)y.Value.Length));
        if (size > m.MaxBytes)
        {
            // No answer: the server treats this client like a vanilla one and leaves the sidecar alone.
            SavesRuntime.Log(LogLevel.Warn, $"characters: character {m.Character}: the sidecar has {size} bytes, this server takes {m.MaxBytes}: not offered");
            return;
        }
        SavesRuntime.Log(LogLevel.Info, $"characters: joined as character {m.Character}: offering revision {file.Revision}, {mods.Count} mod(s) (the server has revision {m.Revision})");
        ctx.Reply(new CharOffer(m.Character, file.Revision, mods));
    }

    private static void OnResult(CharResult m, NetContext ctx) =>
        SavesRuntime.Log(m.Accepted ? LogLevel.Info : LogLevel.Debug, $"characters: the server {(m.Accepted ? "took" : "did not take")} the offer: {m.Reason} (revision {m.Revision}" +
                                                                       $"{(m.Kept.Count > 0 ? $", kept unread: {string.Join(", ", m.Kept)}" : "")}{(m.Dropped.Count > 0 ? $", dropped: {string.Join(", ", m.Dropped)}" : "")})");

    private static void OnUpdate(CharUpdate m, NetContext ctx)
    {
        if (m.Character != _clientCharacter || m.Changes is null) return;
        CharacterSidecar.Apply(m.Character, m.Revision, m.Changes, null);
        _sidecarWrites++;
        ctx.Reply(new CharAck(m.Character, m.Revision));
    }
}

/// <summary>The client's copy of what a character carries: <c>kiln/data/characters/&lt;id&gt;.json</c> (with one older copy,
/// <c>.prev</c>). Not in the game's save folder: the game's migration scan moves unknown files there, and Steam cloud owns the
/// other set of saves. A copy of a character made with the game's copy function has another id, so it starts without this.</summary>
internal static class CharacterSidecar
{
    internal sealed record Contents(long Revision, Dictionary<string, Dictionary<string, string>> Mods);

    private static string Dir => Path.Combine(KilnPaths.Root, "data", "characters");

    internal static string PathOf(Guid id) => Path.Combine(Dir, $"{id:N}.json");

    public static Contents Load(Guid id)
    {
        var path = PathOf(id);
        foreach (var p in new[] { path, path + ".prev" })
        {
            try
            {
                if (!File.Exists(p)) continue;
                if (JsonNode.Parse(File.ReadAllBytes(p)) is not JsonObject doc) continue;
                var mods = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                if (doc["mods"] is JsonObject ms)
                    foreach (var (mod, keys) in ms)
                    {
                        if (keys is not JsonObject km) continue;
                        var d = mods[mod] = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var (k, v) in km)
                            if (v is not null) d[k] = v.ToJsonString();
                    }
                return new Contents(doc["revision"]?.GetValue<long>() ?? 0, mods);
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or FormatException or UnauthorizedAccessException)
            {
                SavesRuntime.Log(LogLevel.Warn, $"characters: {Path.GetFileName(p)} is unusable: {e.Message}");
            }
        }
        return new Contents(0, new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal));
    }

    private static bool Readable(string path)
    {
        try
        {
            return File.Exists(path) && JsonNode.Parse(File.ReadAllBytes(path)) is JsonObject;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Merges changes (keys of mods this update doesn't name stay) and writes the file atomically.</summary>
    public static void Apply(Guid id, long revision, Dictionary<string, Dictionary<string, JsonElement?>> changes, string? world)
    {
        var path = PathOf(id);
        var now = Load(id);
        foreach (var (mod, keys) in changes)
        {
            if (!CharacterSync.ValidModId(mod)) continue;
            if (!now.Mods.TryGetValue(mod, out var d)) now.Mods[mod] = d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in keys)
            {
                if (v is { ValueKind: not JsonValueKind.Null } e) d[k] = e.GetRawText();
                else d.Remove(k);
            }
            if (d.Count == 0) now.Mods.Remove(mod);
        }
        var doc = new JsonObject
        {
            ["format"] = 1,
            ["character"] = id.ToString(),
            ["revision"] = revision,
            ["updatedUnixMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        if (world is not null) doc["world"] = world;
        var mods = new JsonObject();
        foreach (var (mod, keys) in now.Mods)
        {
            var o = new JsonObject();
            foreach (var (k, v) in keys) o[k] = JsonNode.Parse(v);
            mods[mod] = o;
        }
        doc["mods"] = mods;
        Directory.CreateDirectory(Dir);
        if (Readable(path)) File.Copy(path, path + ".prev", overwrite: true);  // never a broken file over the good older copy
        WorldStore.WriteAtomic(path, System.Text.Encoding.UTF8.GetBytes(doc.ToJsonString()));
    }
}
