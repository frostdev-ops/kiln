// SPDX-License-Identifier: MIT
// Server -> client config sync (Forge: server configs synced at login): the [Synced] entries of a server's server and
// common configs reach Kiln clients when they join and whenever they change; on the client they replace the mods'
// local values while connected (read-only there) and the local values come back on leave. Channel "kiln:config" v1
// (Kiln's own, not required: older Kiln clients join without it). Usage and protocol: docs/kiln/sync.md.
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Kiln;

/// <summary><c>kiln/config/sync.json</c>: what Kiln's server-to-client sync (content and configs) does.</summary>
public sealed class SyncSettings
{
    [Comment("Server: send this server's content changes (items and recipes: data packs and mods' code changes) to Kiln\n" +
             "clients when they join and whenever they change.")]
    public bool SendContent { get; set; } = true;

    [Comment("Server: send the [Synced] entries of server and common configs to Kiln clients.")]
    public bool SendConfigs { get; set; } = true;

    [Comment("Client: apply a server's content changes while connected (taken off again on leave).")]
    public bool AcceptContent { get; set; } = true;

    [Comment("Client: use a server's [Synced] config values while connected (local values come back on leave).")]
    public bool AcceptConfigs { get; set; } = true;

    [Comment("Client: while connected, also put back what this game changed on its own (local mods and data packs) where\n" +
             "the server has the game's original value, so the registries match the server's exactly.")]
    public bool MatchServer { get; set; } = true;

    [Comment("Largest content document (uncompressed JSON, KiB) a server sends or a client accepts.")]
    [Range(16, 65536)]
    public int MaxContentKiB { get; set; } = 8192;

    private static ModConfig<SyncSettings>? _file;
    private static readonly SyncSettings Defaults = new();

    /// <summary>The settings in use (registered as Kiln's config "sync" at the first use on the tick thread; defaults
    /// before that).</summary>
    public static SyncSettings Current
    {
        get
        {
            if (_file is { } f) return f.Value;
            if (!Game.IsTickThread) return Defaults;
            try
            {
                var prev = ModContext.CurrentMod;
                ModContext.CurrentMod = null;  // Kiln's own config, not the calling mod's
                try { _file = Config.Find("sync") as ModConfig<SyncSettings> ?? Config.Register<SyncSettings>(ConfigKind.Common, "sync"); }
                finally { ModContext.CurrentMod = prev; }
                return _file.Value;
            }
            catch (Exception e)
            {
                Backend.Current.Log(0, LogLevel.Warn, $"sync: kiln/config/sync.json unavailable ({e.Message}); using defaults");
                _file = null;
                return Defaults;
            }
        }
    }
}

/// <summary>
/// Client: a Kiln server's [Synced] config values were applied to <see cref="Config"/>, or (<see cref="Restored"/>)
/// this game's own values came back after leaving. Fires on the tick thread (the client's main thread in a joined
/// game) after the values are in place, right after the file's <see cref="ConfigChangedEvent"/> (source
/// <see cref="ConfigChangeSource.Server"/>). Not cancelable; nothing to change.
/// </summary>
public sealed class ConfigSyncedEvent : KilnEvent
{
    internal ConfigSyncedEvent(ConfigFile config, IReadOnlyList<string> changed, IReadOnlyList<string> errors, bool restored)
    {
        Config = config;
        Changed = changed;
        Errors = errors;
        Restored = restored;
    }

    public ConfigFile Config { get; }
    public string ModId => Config.ModId;

    /// <summary>Key paths whose running value changed.</summary>
    public IReadOnlyList<string> Changed { get; }

    /// <summary>Server values this game's schema refused (another mod version): those entries keep the local value.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>True when the local values came back (left the server, or the server stopped syncing this config).</summary>
    public bool Restored { get; }
}

/// <summary>Kiln's config sync (see <see cref="SyncedAttribute"/>). Runs by itself; mods read
/// <see cref="ConfigFile.ServerValues"/> or subscribe to <see cref="ConfigSyncedEvent"/>.</summary>
public static class ConfigSync
{
    public const string ChannelName = "kiln:config";
    public const string ChannelVersion = "1";
    internal const ushort Format = 1;
    private const ushort MsgState = 0, MsgAck = 1;
    private const int MaxConfigs = 512;

    internal sealed record Item(string Id, string ModId, ConfigKind Kind, string Values);

    internal sealed record State(ushort Format, uint StateId, IReadOnlyList<Item> Items)
    {
        public void Encode(NetWriter w)
        {
            w.UInt16(Format).UInt32(StateId).VarUInt((ulong)Items.Count);
            foreach (var i in Items) w.String(i.Id).String(i.ModId).Byte((byte)i.Kind).String(i.Values);
        }

        public static State Decode(ref NetReader r)
        {
            var format = r.UInt16();
            var id = r.UInt32();
            var n = (int)Math.Min(r.VarUInt(), MaxConfigs);
            var items = new List<Item>(n);
            for (var k = 0; k < n; k++) items.Add(new Item(r.String(256), r.String(256), (ConfigKind)r.Byte(), r.String(1 << 20)));
            return new State(format, id, items);
        }
    }

    internal sealed record Ack(uint StateId, int Applied, int Errors, string Detail)
    {
        public void Encode(NetWriter w) => w.UInt32(StateId).VarUInt((ulong)Applied).VarUInt((ulong)Errors).String(Detail);
        public static Ack Decode(ref NetReader r) => new(r.UInt32(), (int)Math.Min(r.VarUInt(), int.MaxValue), (int)Math.Min(r.VarUInt(), int.MaxValue), r.String(4096));
    }

    private static readonly object Lock = new();
    private static NetChannel? _channel;
    private static int _started;

    // server role (tick thread)
    private static State? _state;
    private static volatile bool _dirty = true;
    private static readonly Dictionary<uint, uint> Sent = new();

    // client role (main thread; received state under Lock)
    private static State? _received;
    private static Dictionary<string, JsonElement> _receivedValues = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConditionalWeakTable<ConfigFile, StrongBox<uint>> AppliedTo = new();
    private static volatile bool _clientDirty, _leavePending;

    /// <summary>Client: the server's config state is in use (configs with server values right now).</summary>
    public static int ServerConfigs => Config.All.Count(f => f.ServerValues.Count > 0);

    /// <summary>Client: the last state the server sent (id, configs), or null.</summary>
    public static (uint StateId, int Configs)? LastReceived => _received is { } s ? (s.StateId, s.Items.Count) : null;

    /// <summary>Server: configs with [Synced] entries being sent, and the state id.</summary>
    public static (uint StateId, int Configs)? Serving => _state is { } s ? (s.StateId, s.Items.Count) : null;

    /// <summary>Started with Kiln.Network (service autostart). Idempotent.</summary>
    internal static void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
        try
        {
            var prev = ModContext.CurrentMod;
            ModContext.CurrentMod = null;
            try
            {
                _channel = Network.Channel(ChannelName, ChannelVersion, required: false)
                    .Register<State>(MsgState, static (m, w) => m.Encode(w), static (ref NetReader r) => State.Decode(ref r), OnState, NetDirection.ToClient)
                    .Register<Ack>(MsgAck, static (m, w) => m.Encode(w), static (ref NetReader r) => Ack.Decode(ref r), OnAck, NetDirection.ToServer);
                Network.Disconnected += OnLeave;
                Network.ServerReady += _ => OnLeave(null);  // a new server: forget the last one's values
            }
            finally
            {
                ModContext.CurrentMod = prev;
            }
            Config.AnyChanged += f =>
            {
                if (f.Kind != ConfigKind.Client) _dirty = true;
            };
            Config.RegistryChanged += () => _dirty = true;
            Config.ServerValuesFor = ValuesFor;
            ServiceTick.Add("kiln config sync", Tick);
        }
        catch (Exception e)
        {
            Backend.Current.Log(0, LogLevel.Warn, $"sync: config sync unavailable: {e.Message}");
        }
    }

    private static void Log(LogLevel level, string text) => Backend.Current.Log(0, level, "sync: " + text);

    private static void Tick(in TickInfo tick)
    {
        if (_leavePending) Leave();
        if (_clientDirty) ApplyClient();
        if (tick.Step % 250 == 0) _dirty = true;  // values changed in code without Save() still go out (~10 s)
        if (tick.Step % 25 == 0 || _dirty) ServerTick();
    }

    // ------------------------------------------------------------------------------------------------- server ----

    private static void ServerTick()
    {
        var peers = Network.Peers;
        if (peers.Count == 0 || _channel is null)
        {
            lock (Sent) Sent.Clear();
            return;
        }
        if (!SyncSettings.Current.SendConfigs) return;
        if (_dirty || _state is null)
        {
            _dirty = false;
            var items = new List<Item>();
            foreach (var f in Config.All)
            {
                if (f.Kind == ConfigKind.Client || f.Id == "sync" || !f.Entries.Any(e => e.Synced)) continue;
                items.Add(new Item(f.Id, f.ModId, f.Kind, f.SyncedValuesJson()));
            }
            var id = Hash(items);
            if (_state is null || _state.StateId != id)
            {
                _state = new State(Format, id, items);
                if (items.Count > 0)
                    Log(LogLevel.Info, $"config state {id:x8}: {items.Count} config(s) with [Synced] entries ({string.Join(", ", items.Select(i => i.Id))})");
            }
        }
        var st = _state;
        if (st is null) return;
        var host = Kiln.Client.IsClient && Kiln.Client.IsHosting ? Kiln.Client.LocalPlayer : EntityId.None;
        lock (Sent)
        {
            foreach (var p in peers)
            {
                if (p.State != NetPeerState.Ready || !p.HasChannel(ChannelName)) continue;
                if (Sent.TryGetValue(p.Id, out var have) && have == st.StateId) continue;
                if (!host.IsNone && p.Player == host)
                {
                    Sent[p.Id] = st.StateId;  // the host's own client: same process, same configs
                    continue;
                }
                if (_channel.SendTo(p, st))
                {
                    Sent[p.Id] = st.StateId;
                    Log(LogLevel.Info, $"config state {st.StateId:x8} ({st.Items.Count} config(s)) sent to {p.Name}");
                }
            }
            foreach (var gone in Sent.Keys.Where(k => !peers.Any(p => p.Id == k)).ToList()) Sent.Remove(gone);
        }
    }

    private static void OnAck(Ack a, NetContext ctx) =>
        Log(a.Errors > 0 ? LogLevel.Warn : LogLevel.Info,
            $"{ctx.Sender.Name}: config state {a.StateId:x8}: {a.Applied} value(s) applied, {a.Errors} refused{(a.Detail.Length > 0 ? ": " + a.Detail : "")}");

    private static uint Hash(IEnumerable<Item> items)
    {
        var h = 2166136261u;
        foreach (var i in items)
            foreach (var c in i.Id + "\u0001" + i.Kind + "\u0001" + i.Values + "\u0002")
                h = (h ^ c) * 16777619u;
        return h == 0 ? 1 : h;
    }

    // ------------------------------------------------------------------------------------------------- client ----

    private static void OnState(State s, NetContext ctx)
    {
        if (s.Format != Format)
        {
            Log(LogLevel.Warn, $"config state format {s.Format} from the server is not supported ({Format}); ignored");
            return;
        }
        if (Kiln.Client.IsClient && Kiln.Client.IsHosting) return;  // the host's own client: nothing to take
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in s.Items)
        {
            if (i.Kind == ConfigKind.Client) continue;
            try
            {
                using var doc = JsonDocument.Parse(i.Values);
                values[i.Id] = doc.RootElement.Clone();
            }
            catch (JsonException e)
            {
                Log(LogLevel.Warn, $"config {i.Id} from the server is not JSON: {e.Message}");
            }
        }
        lock (Lock)
        {
            _received = s;
            _receivedValues = values;
        }
        Log(LogLevel.Info, $"config state {s.StateId:x8} from the server: {values.Count} config(s) ({string.Join(", ", values.Keys)})");
        _clientDirty = true;
        if (Game.IsTickThread) ApplyClient();
    }

    private static JsonElement? ValuesFor(ConfigFile file)
    {
        if (file.Kind == ConfigKind.Client || !SyncSettings.Current.AcceptConfigs) return null;
        lock (Lock)
        {
            if (_received is not { } s || !_receivedValues.TryGetValue(file.Id, out var v)) return null;
            AppliedTo.AddOrUpdate(file, new StrongBox<uint>(s.StateId));
            return v;
        }
    }

    /// <summary>Applies the received state to every registered config (tick thread).</summary>
    private static void ApplyClient()
    {
        if (!Game.IsTickThread) return;
        _clientDirty = false;
        State? s;
        Dictionary<string, JsonElement> values;
        lock (Lock)
        {
            s = _received;
            values = _receivedValues;
        }
        if (s is null) return;
        if (!SyncSettings.Current.AcceptConfigs)
        {
            Log(LogLevel.Info, "the server's config values are not used (acceptConfigs is off in kiln/config/sync.json)");
            return;
        }
        int applied = 0, errors = 0;
        var detail = new List<string>();
        foreach (var f in Config.All)
        {
            if (f.Kind == ConfigKind.Client || f.Id == "sync") continue;
            if (values.TryGetValue(f.Id, out var v))
            {
                if (AppliedTo.TryGetValue(f, out var box) && box.Value == s.StateId) continue;
                AppliedTo.AddOrUpdate(f, new StrongBox<uint>(s.StateId));
                var (changed, _, errs) = f.ApplyServerValues(v);
                applied += f.ServerValues.Count;
                errors += errs.Count;
                detail.AddRange(errs.Select(e => $"{f.Id}.{e}"));
                Post(f, changed, errs, restored: false);
            }
            else if (f.HasServerValues)
            {
                AppliedTo.Remove(f);
                var changed = f.ClearServerValues();
                Post(f, changed, [], restored: true);
            }
        }
        if (applied > 0 || errors > 0)
            _channel?.SendToServer(new Ack(s.StateId, applied, errors, string.Join("; ", detail.Take(8))));
    }

    private static void OnLeave(NetPeer? _)
    {
        lock (Lock)
        {
            _received = null;
            _receivedValues = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }
        _clientDirty = false;
        if (Game.IsTickThread) Leave();
        else _leavePending = true;
    }

    private static void Leave()
    {
        _leavePending = false;
        foreach (var f in Config.All)
        {
            AppliedTo.Remove(f);
            if (!f.HasServerValues) continue;
            var changed = f.ClearServerValues();
            Post(f, changed, [], restored: true);
        }
    }

    /// <summary>
    /// A self-check of the client side on one config of this process (tick thread; a lab / diagnostic tool): takes
    /// the config's own synced values as server values (nothing may change), then a server value one step off its
    /// first synced number (it must run, be read-only and stay out of the file), then leaves (the local value must
    /// come back). Raises the config's change events (source Server) like a real sync.
    /// </summary>
    [TickThreadOnly]
    public static string CheckRoundTrip(ConfigFile file)
    {
        Game.AssertTickThread();
        var problems = new List<string>();
        if (file.HasServerValues) return "error: the config holds server values now";
        var fileBefore = File.Exists(file.FilePath) ? File.ReadAllText(file.FilePath) : "";
        var own = System.Text.Json.Nodes.JsonNode.Parse(file.SyncedValuesJson())!.AsObject();
        using (var d = JsonDocument.Parse(own.ToJsonString()))
        {
            var (c1, _, e1) = file.ApplyServerValues(d.RootElement);
            if (c1.Count > 0 || e1.Count > 0) problems.Add($"its own values changed {string.Join(", ", c1)} / refused {string.Join(", ", e1)}");
        }
        if (file.ServerValues.Count == 0) problems.Add("no entry holds the server's value");
        var number = file.Entries.FirstOrDefault(e => e.Synced && e.Type == "int" && e.Value is int);
        string detail = "";
        if (number is not null)
        {
            var v = (int)number.Value!;
            var want = number.Max is { } max && v + 1 > max ? v - 1 : v + 1;
            var node = own;
            var parts = number.Path.Split('.');
            foreach (var part in parts[..^1]) node = node[part]!.AsObject();
            node[parts[^1]] = want;
            using var d = JsonDocument.Parse(own.ToJsonString());
            var (c2, _, _) = file.ApplyServerValues(d.RootElement);
            if (!c2.Contains(number.Path) || file.Get(number.Path) is not int now || now != want)
                problems.Add($"{number.Path}: the server's {want} did not run ({file.Get(number.Path)})");
            if (!file.IsFromServer(number.Path)) problems.Add($"{number.Path} is not marked as the server's");
            if (file.TrySet(number.Path, v, out _)) problems.Add($"{number.Path} could be set locally while it holds the server's value");
            var entry = file.Entries.First(e => e.Path == number.Path);
            if (!entry.FromServer || entry.FileValue is not int fv || fv != v) problems.Add($"{number.Path}: entry FromServer {entry.FromServer}, file value {entry.FileValue}");
            if ((File.Exists(file.FilePath) ? File.ReadAllText(file.FilePath) : "") != fileBefore) problems.Add("the file changed");
            detail = $"{number.Path} {v} -> server {want} -> {v}";
        }
        var c3 = file.ClearServerValues();
        if (file.ServerValues.Count > 0) problems.Add("server values left after leaving");
        if (number is not null && (file.Get(number.Path) is not int back || back != (int)number.Value!)) problems.Add($"{number.Path} is {file.Get(number.Path)} after leaving");
        if (number is not null && !c3.Contains(number.Path)) problems.Add($"leaving did not report {number.Path}");
        var r = problems.Count == 0 ? $"ok: {file.Id}: {detail}" : $"error: {file.Id}: {string.Join("; ", problems)}";
        Log(problems.Count == 0 ? LogLevel.Info : LogLevel.Warn, "config round trip: " + r);
        return r;
    }

    private static void Post(ConfigFile f, IReadOnlyList<string> changed, IReadOnlyList<string> errors, bool restored)
    {
        if (Events.HasSubscribers<ConfigSyncedEvent>()) Events.Post(new ConfigSyncedEvent(f, changed, errors, restored));
    }
}
