// SPDX-License-Identifier: MIT
// Kiln.Network's protocol engine: handshake, mod sync, channel tables, fragmentation, limits (docs/kiln/network.md).
// Server role state is touched on the server tick thread only; client role state on the client's main thread only
// (in a hosted world both run in one process, on their own threads). The registry (channels, properties) takes Lock.
namespace Kiln;

internal static class NetEngine
{
    internal static readonly object Lock = new();

    internal readonly record struct Encoded(ushort Message, byte[] Body);

    private static readonly List<NetChannel> Open_ = new();
    private static ushort _nextChannelId = 1;
    private static readonly Dictionary<string, (Func<string> Provider, ModContext? Owner)> Properties = new(StringComparer.Ordinal);
    private static NetworkConfig _config = new();
    private static string? _configError;
    private static int _started;
    private static IDisposable? _serverTick;
    private static bool _clientTickHooked;
    private static long _framesIn, _framesOut, _badFrames;

    // server role (tick thread; snapshots under Lock)
    private static readonly Dictionary<uint, NetPeer> Server = new();
    private static NetPeer[] _serverSnapshot = [];

    // client role (main thread)
    private static NetPeer? _link;
    private static List<Wire.ChannelEntry> _serverTable = [];
    private static NetServerAdvert? _serverAdvert;

    internal static readonly OwnedHandlers<Action<NetPeer>> ServerReadyHandlers = new();
    internal static readonly OwnedHandlers<Action<string, IReadOnlyList<string>>> RejectedHandlers = new();
    internal static readonly OwnedHandlers<Action<NetPeer>> ServerVanillaHandlers = new();
    internal static readonly OwnedHandlers<Action<NetPeer>> DisconnectedHandlers = new();

    static NetEngine()
    {
        ReloadConfig();
        NetModCalls.OnRetire(mod =>
        {
            List<NetChannel> gone;
            lock (Lock)
            {
                gone = Open_.Where(c => ReferenceEquals(c.Owner, mod)).ToList();
                foreach (var k in Properties.Where(kv => ReferenceEquals(kv.Value.Owner, mod)).Select(kv => kv.Key).ToList()) Properties.Remove(k);
            }
            foreach (var c in gone) Close(c);
        });
    }

    public static NetworkConfig Config => _config;
    public static NetPeer? ServerLink => _link;
    public static NetServerAdvert? ServerAdvert => _serverAdvert;
    public static string? ConfigError => _configError;

    /// <summary>What this server advertises (before the per-frame truncation).</summary>
    public static NetServerAdvert Advertised()
    {
        var req = NetAdvertise.Required;
        return new NetServerAdvert(NetAdvertise.ServerId(_config), NetAdvertise.ServerName(_config), Game.KilnVersion,
            _config.Modpack, req, req.Count, false, !NetAdvertise.RequiredReady);
    }

    public static void Log(LogLevel level, string text) => Backend.Current.Log(0, level, "net: " + text);
    public static void LogError(string text) => Log(LogLevel.Error, text);

    public static void ReloadConfig()
    {
        _config = NetworkConfig.Load(out _configError);
        if (_configError != null) Log(LogLevel.Warn, $"config: {_configError} (using defaults)");
        NetAdvertise.Reset();
    }

    /// <summary>Subscribes to kiln.net and starts the periodic work. Idempotent; runs at Kiln.Network's autostart or
    /// the first use.</summary>
    public static void EnsureStarted()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
        if (!NetNative.Subscribe())
        {
            Log(LogLevel.Warn, "kiln.net is unavailable on this build: mod channels stay local");
            return;
        }
        try
        {
            _serverTick = ServiceTick.Add("kiln.network", ServerTick);
        }
        catch (Exception e)
        {
            Log(LogLevel.Warn, $"no service tick: {e.Message}");
        }
        TryHookClientTick();
        NetNative.GateSet(NetNative.GateHold);  // until the first server tick knows the mod list (VanillaGate.GateMode)
        var s = NetNative.ReadStatus();
        Log(LogLevel.Info, $"Kiln.Network {Wire.Protocol} on kiln.net: roles {(s?.Roles ?? 0)}, channel {s?.Channel}, max frame {s?.MaxFrame}, " +
                           $"policy {_config.ModSync}/{_config.VersionMatch}{(_config.RequireKiln ? ", Kiln required" : "")}");
    }

    private static void TryHookClientTick()
    {
        if (_clientTickHooked) return;
        try
        {
            if (!Kiln.Client.Available || !Kiln.Client.IsClient) return;
            var prev = ModContext.CurrentMod;
            ModContext.CurrentMod = null;  // Kiln's own handler, not a mod's
            try
            {
                Kiln.Client.Tick += ClientTick;
            }
            finally
            {
                ModContext.CurrentMod = prev;
            }
            _clientTickHooked = true;
        }
        catch (Exception)
        {
            // Kiln.Client absent (old host): the client role then runs its timers on frames only
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Registry

    public static NetChannel Open(string name, string version, bool required, int maxMessageBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (name.Length > 128 || version.Length > 64) throw new ArgumentException("channel name or version too long");
        EnsureStarted();
        var owner = NetModCalls.Caller;
        NetChannel ch;
        lock (Lock)
        {
            var existing = Open_.FirstOrDefault(c => c.Name == name);
            if (existing != null)
            {
                if (ReferenceEquals(existing.Owner, owner) && existing.Version == version) return existing;
                throw new InvalidOperationException($"channel {name} is already open (by {existing.Owner?.Info.Id ?? "kiln"}, v{existing.Version})");
            }
            var limit = maxMessageBytes > 0 ? Math.Min(maxMessageBytes, _config.MaxMessageBytes) : _config.MaxMessageBytes;
            ch = new NetChannel(name, version, required, owner, limit) { ServerId = _nextChannelId++ };
            Open_.Add(ch);
        }
        NetModCalls.Watch(owner);
        Log(LogLevel.Info, $"channel {name} v{version} open (id {ch.ServerId}, {owner?.Info.Id ?? "kiln"})");
        OnChannelsChanged();
        return ch;
    }

    public static void Close(NetChannel ch)
    {
        lock (Lock)
        {
            if (ch.Closed) return;
            ch.Closed = true;
            Open_.Remove(ch);
        }
        Log(LogLevel.Info, $"channel {ch.Name} closed");
        OnChannelsChanged();
    }

    public static IReadOnlyList<NetChannel> AllChannels()
    {
        lock (Lock) return Open_.ToArray();
    }

    private static NetChannel? ByName(string name)
    {
        lock (Lock) return Open_.FirstOrDefault(c => c.Name == name);
    }

    private static NetChannel? ByServerId(ushort id)
    {
        lock (Lock) return Open_.FirstOrDefault(c => c.ServerId == id);
    }

    public static void SetProperty(string key, Func<string> provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(provider);
        var owner = NetModCalls.Caller;
        NetModCalls.Watch(owner);
        lock (Lock) Properties[key] = (provider, owner);
    }

    private static Dictionary<string, string> PropertyValues()
    {
        KeyValuePair<string, (Func<string> Provider, ModContext? Owner)>[] list;
        lock (Lock) list = Properties.ToArray();
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, (f, _)) in list)
        {
            try { d[k] = f() ?? ""; }
            catch (Exception e) { Log(LogLevel.Warn, $"handshake property {k} threw: {e.Message}"); }
        }
        return d;
    }

    private static List<Wire.ChannelEntry> ChannelTable()
    {
        lock (Lock) return Open_.Select(c => new Wire.ChannelEntry(c.ServerId, c.Name, c.Version, c.Required)).ToList();
    }

    /// <summary>Mods this side reports: loaded mods with side both (and client mods, from a client).</summary>
    private static List<Wire.ModEntry> LocalMods(bool client)
    {
        try
        {
            return Mods.All.Where(m => m.IsLoaded && (m.Side == ModSide.Both || (client && m.Side == ModSide.Client)))
                .Select(m => new Wire.ModEntry(m.Id, m.Version, m.Side)).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void OnChannelsChanged()
    {
        // Server role: tell Ready clients (on the tick thread; from elsewhere the next server tick does it).
        if (Game.IsTickThread) BroadcastChannelTable();
        else lock (Lock) _tableDirty = true;
        // Client role: tell the server which channels we have now.
        if (_link is { State: NetPeerState.Ready } link)
        {
            RemapClientChannels(link);
            SendHello(link, update: true);
        }
    }

    private static bool _tableDirty;

    private static void BroadcastChannelTable()
    {
        lock (Lock) _tableDirty = false;
        var table = ChannelTable();
        foreach (var p in ServerPeers())
        {
            if (p.State != NetPeerState.Ready) continue;
            RemapServerChannels(p);
            var w = Wire.Begin(Wire.Type.Channels, 256);
            Wire.WriteChannels(w, table);
            SendFrame(p.Id, w.WrittenSpan, p);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Transport events

    public static void OnPeer(uint peer, bool up)
    {
        if (peer == NetNative.PeerServer)
        {
            if (up)
            {
                _link = new NetPeer(peer)
                {
                    State = NetPeerState.Connecting, Name = "server", NextHelloAt = DateTime.UtcNow.AddSeconds(1),
                };
                Log(LogLevel.Info, "connected to a server: Kiln handshake in 1 s");
            }
            else if (_link is { } old)
            {
                old.State = NetPeerState.Closed;
                _link = null;
                _serverTable = [];
                Log(LogLevel.Info, "server connection closed");
                DisconnectedHandlers.Raise(h => h(old), "Network.Disconnected");
            }
            return;
        }
        if (up)
        {
            var p = new NetPeer(peer) { State = NetPeerState.Connecting };
            RefreshInfo(p);
            lock (Lock)
            {
                Server[peer] = p;
                _serverSnapshot = Server.Values.ToArray();
            }
        }
        else
        {
            NetPeer? p;
            lock (Lock)
            {
                Server.Remove(peer, out p);
                _serverSnapshot = Server.Values.ToArray();
            }
            if (p is null) return;
            var was = p.State;
            p.State = NetPeerState.Closed;
            Log(LogLevel.Info, $"peer {p.Name} (0x{peer:x}) left ({was})");
            if (Events.HasSubscribers<NetPeerLeftEvent>()) Events.Post(new NetPeerLeftEvent(p));
        }
    }

    public static void OnFrame(uint peer, ReadOnlySpan<byte> frame)
    {
        Interlocked.Increment(ref _framesIn);
        if (!Wire.TryHeader(frame, out var type))
        {
            Interlocked.Increment(ref _badFrames);
            return;
        }
        if (peer == NetNative.PeerServer)
        {
            ClientFrame(type, frame);
            return;
        }
        NetPeer? p;
        lock (Lock) Server.TryGetValue(peer, out p);
        if (p is null) return;
        ServerFrame(p, type, frame);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Server role

    internal static IReadOnlyList<NetPeer> ServerPeers()
    {
        lock (Lock) return _serverSnapshot;
    }

    internal static NetPeer? PeerOfPlayer(EntityId player)
    {
        if (player.IsNone) return null;
        foreach (var p in ServerPeers())
            if (p.Player == player) return p;
        return null;
    }

    private static void RefreshInfo(NetPeer p)
    {
        if (NetNative.Info(p.Id) is not { } i) return;
        var name = i.NameString();
        if (name.Length > 0) p.Name = name;
        if ((i.Flags & NetNative.PeerHeld) != 0) p.HeldSince ??= DateTime.UtcNow;
        else p.HeldSince = null;
        if ((i.Flags & NetNative.PeerHasPlayer) != 0)
        {
            p.Player = new EntityId(i.Entity);
            p.AccountId = i.Account;
            if (i.RecordState == 3) p.InGameSince ??= DateTime.UtcNow;
        }
    }

    private static bool TakeTokens(NetPeer p, int bytes)
    {
        var now = DateTime.UtcNow;
        var dt = (now - p.TokensAt).TotalSeconds;
        p.TokensAt = now;
        var c = _config;
        p.TokensMsgs = Math.Min(c.MessagesPerSecond * 2, (p.MessagesIn == 0 && p.TokensMsgs == 0 ? c.MessagesPerSecond * 2 : p.TokensMsgs) + dt * c.MessagesPerSecond);
        p.TokensBytes = Math.Min(c.BytesPerSecond * 2, (p.BytesIn == 0 && p.TokensBytes == 0 ? c.BytesPerSecond * 2 : p.TokensBytes) + dt * c.BytesPerSecond);
        if (p.TokensMsgs < 1 || p.TokensBytes < bytes) return false;
        p.TokensMsgs -= 1;
        p.TokensBytes -= bytes;
        return true;
    }

    private static void CountDrop(NetPeer p, string why)
    {
        p.Dropped++;
        var now = DateTime.UtcNow;
        p.RecentDrops.Enqueue(now);
        while (p.RecentDrops.Count > 0 && (now - p.RecentDrops.Peek()).TotalSeconds > 10) p.RecentDrops.Dequeue();
        if (p.Dropped == 1 || p.Dropped % 1000 == 0) Log(LogLevel.Warn, $"peer {p.Name}: dropped frame ({why}); {p.Dropped} dropped so far");
        if (_config.FloodKickDrops > 0 && p.RecentDrops.Count >= _config.FloodKickDrops && p.KickAt is null)
        {
            Log(LogLevel.Warn, $"peer {p.Name}: {p.RecentDrops.Count} drops in 10 s: kicking");
            p.KickAt = now;
        }
    }

    private static void ServerFrame(NetPeer p, Wire.Type type, ReadOnlySpan<byte> frame)
    {
        if (type != Wire.Type.Hello && !TakeTokens(p, frame.Length))
        {
            CountDrop(p, "rate limit");
            return;
        }
        switch (type)
        {
            case Wire.Type.Hello:
                HandleHello(p, frame);
                break;
            case Wire.Type.Data:
            case Wire.Type.Fragment:
                if (p.State == NetPeerState.Ready) ReceiveData(p, type, frame, NetDirection.ToServer);
                break;
            case Wire.Type.Ping:
                Pong(p, frame);
                break;
            case Wire.Type.Pong:
                HandlePong(p, frame);
                break;
            case Wire.Type.Bye:
                Log(LogLevel.Info, $"peer {p.Name} said bye");
                break;
        }
    }

    private static IReadOnlyList<Wire.ModEntry> RequiredServerMods() =>
        LocalMods(client: false).Where(m => !_config.OptionalMods.Contains(m.Id, StringComparer.Ordinal)).ToList();

    private static void HandleHello(NetPeer p, ReadOnlySpan<byte> frame)
    {
        Wire.Hello hello;
        try
        {
            hello = Wire.DecodeHello(frame);
        }
        catch (Exception e)
        {
            CountDrop(p, "bad hello: " + e.Message);
            return;
        }
        RefreshInfo(p);
        p.KilnVersion = hello.KilnVersion;
        p.Mods = hello.Mods.Select(m => new NetRemoteMod(m.Id, m.Version, m.Side)).ToList();
        p.Properties = hello.Properties;
        p.Declared = hello.Channels.ToDictionary(c => c.Name, c => c.Version, StringComparer.Ordinal);
        if (p.State == NetPeerState.Ready)
        {
            // a client re-announcing its channels (a client mod (un)loaded)
            RemapServerChannels(p);
            SendWelcome(p, hello.Nonce, p.Warnings);
            return;
        }
        if (p.State == NetPeerState.Rejected) return;

        var missing = new List<string>();
        var extra = new List<string>();
        var clientMods = hello.Mods.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var m in RequiredServerMods())
        {
            if (!clientMods.TryGetValue(m.Id, out var cm) || cm.Side == ModSide.Client) missing.Add($"{m.Id} {m.Version} (missing)");
            else if (!_config.VersionsMatch(m.Version, cm.Version)) missing.Add($"{m.Id} {m.Version} (client has {cm.Version})");
        }
        var serverIds = LocalMods(client: false).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var cm in hello.Mods)
            if (cm.Side == ModSide.Both && !serverIds.Contains(cm.Id)) extra.Add($"{cm.Id} {cm.Version}");
        foreach (var ch in AllChannels())
        {
            if (!ch.Required) continue;
            if (!p.Declared.TryGetValue(ch.Name, out var v)) missing.Add($"channel {ch.Name} v{ch.Version} (missing)");
            else if (v != ch.Version) missing.Add($"channel {ch.Name} v{ch.Version} (client has v{v})");
        }
        if (hello.Protocol != Wire.Protocol) missing.Add($"Kiln network protocol {Wire.Protocol} (client speaks {hello.Protocol})");

        var refuse = _config.ModSync == ModSyncPolicy.Refuse && missing.Count > 0;
        var refusedFor = missing;
        if (!refuse)
        {
            // The default policy: a mod that states clients: "mod" is one this client can't do without.
            var clientHas = hello.Mods.Where(m => m.Side != ModSide.Client).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            var lacking = VanillaGate.MissingStatedMods(_config, NetAdvertise.StatedModClients, clientHas);
            if (lacking.Count > 0)
            {
                refuse = true;
                refusedFor = missing.Where(m => lacking.Any(id => m.StartsWith(id + " ", StringComparison.Ordinal))).ToList();
            }
        }
        var reason = refuse ? "This server requires mods you do not have: " + string.Join(", ", refusedFor.Take(8)) : "";
        if (Events.HasSubscribers<NetClientHandshakeEvent>())
        {
            var ev = Events.Post(new NetClientHandshakeEvent(p, missing, extra, refuse) { Reason = reason });
            refuse = ev.Refuse || ev.Canceled;
            if (refuse) reason = ev.Reason.Length > 0 ? ev.Reason : reason.Length > 0 ? reason : "Refused by the server.";
        }
        var warnings = new List<string>();
        if (_config.ModSync != ModSyncPolicy.Off)
        {
            warnings.AddRange(missing.Select(m => "server mod not on this client: " + m));
            warnings.AddRange(extra.Select(m => "client mod not on the server: " + m));
        }
        p.Warnings = warnings;
        if (refuse)
        {
            p.State = NetPeerState.Rejected;
            SendFrame(p.Id, EncodeRejectAdvertised(new Wire.Reject(reason, refusedFor)), p);
            p.KickAt = DateTime.UtcNow.AddSeconds(1.5);
            Log(LogLevel.Warn, $"refusing {p.Name} (Kiln {hello.KilnVersion}): {string.Join("; ", refusedFor)}");
            return;
        }
        p.State = NetPeerState.Ready;
        RemapServerChannels(p);
        SendWelcome(p, hello.Nonce, warnings);
        // A hosted world's own client (machine slot 0) shares this process's templates and gets no content state.
        var localMachine = !p.IsLab && (p.Id & 0x7F) == 0;
        if (!localMachine && VanillaGate.AwaitsDefinitions(NetNative.ReadGate().KilnTemplates, p.HasChannel(ContentChannel))) p.DefinitionsSince = DateTime.UtcNow;
        else NetNative.WithholdSet(p.Id, false);
        Log(LogLevel.Info, $"{p.Name} (0x{p.Id:x}) joined with Kiln {hello.KilnVersion}: {hello.Mods.Count} mod(s), " +
                           $"{p.Channels.Count} shared channel(s){(warnings.Count > 0 ? $", {warnings.Count} warning(s): " + string.Join("; ", warnings.Take(4)) : "")}");
        if (Events.HasSubscribers<NetPeerReadyEvent>()) Events.Post(new NetPeerReadyEvent(p));
    }

    private static void RemapServerChannels(NetPeer p)
    {
        p.Channels.Clear();
        foreach (var ch in AllChannels())
            if (p.Declared.TryGetValue(ch.Name, out var v) && v == ch.Version) p.Channels[ch.Name] = ch.ServerId;
    }

    private static void SendWelcome(NetPeer p, uint nonce, IReadOnlyList<string> warnings)
    {
        var w = new Wire.Welcome(Wire.Protocol, Game.KilnVersion, nonce, _config.ModSync.ToString().ToLowerInvariant(),
            (uint)_config.MaxMessageBytes, LocalMods(client: false), ChannelTable(), warnings, PropertyValues());
        var plain = Wire.EncodeWelcome(w);
        var frame = plain;
        if (_config.Advertise && Advertise(w.Properties, plain.Length, "welcome") is { } props)
        {
            var withProps = Wire.EncodeWelcome(w with { Properties = props });
            if (withProps.Length <= MaxFrameBytes) frame = withProps;
            else Log(LogLevel.Warn, $"welcome with server properties is {withProps.Length} bytes: sent without them");
        }
        SendFrame(p.Id, frame, p);
    }

    private static int MaxFrameBytes => (int)Math.Max(NetNative.MaxFrame, 1200);

    /// <summary>REJECT plus the kiln.* server properties (when advertise is on and they fit).</summary>
    internal static byte[] EncodeRejectAdvertised(Wire.Reject j)
    {
        var plain = Wire.EncodeReject(j);
        if (!_config.Advertise) return plain;
        // the plain frame has no properties block; Advertise() counts one for the kiln.* keys
        if (Advertise(new Dictionary<string, string>(), plain.Length + 1, "reject") is not { } props) return plain;
        var withProps = Wire.EncodeReject(j with { Properties = props });
        return withProps.Length <= MaxFrameBytes ? withProps : plain;
    }

    /// <summary><paramref name="existing"/> (mods' handshake properties) merged with the kiln.* server properties,
    /// sized so the frame (now <paramref name="frameBytes"/> bytes) stays within 7 KB.</summary>
    private static Dictionary<string, string>? Advertise(IReadOnlyDictionary<string, string> existing, int frameBytes, string what)
    {
        try
        {
            var kiln = NetAdvertise.Properties(_config, frameBytes, out var truncated);
            var merged = new Dictionary<string, string>(existing, StringComparer.Ordinal);
            foreach (var (k, v) in kiln) merged[k] = v;
            if (truncated) Log(LogLevel.Info, $"{what}: required mods truncated to fit the frame ({kiln.GetValueOrDefault(NetAdvertise.KeyMore)} left out)");
            return merged;
        }
        catch (Exception e)
        {
            Log(LogLevel.Warn, $"server properties: {e.Message}");
            return null;
        }
    }

    private static void ServerTick(in TickInfo tick)
    {
        if (_tableDirty) BroadcastChannelTable();
        if (tick.Step % 25 != 0) return;  // ~1 s on a dedicated server
        NetAdvertise.Tick();
        var now = DateTime.UtcNow;
        var requiredExist = RequiredServerMods().Count > 0 || AllChannels().Any(c => c.Required);
        var gate = NetNative.ReadGate();
        var kilnClientMods = NetAdvertise.KilnClientMods.Count;
        var refuse = VanillaGate.ShouldRefuse(_config, requiredExist, kilnClientMods, gate.KilnTemplates, gate.CanWithhold);
        UpdateGate(gate, refuse);
        foreach (var p in ServerPeers())
        {
            RefreshInfo(p);
            if (!TestFreezeVanillaDecisions && VanillaGate.TimeIsUp(p.State, p.IsLab, p.HeldSince, p.InGameSince, now, _config))
            {
                var held = p.HeldSince != null;
                p.State = NetPeerState.Vanilla;
                var kick = refuse;
                if (Events.HasSubscribers<NetVanillaClientEvent>()) kick = Events.Post(new NetVanillaClientEvent(p, kick)).Kick;
                Log(kick ? LogLevel.Warn : LogLevel.Info, $"{p.Name} (0x{p.Id:x}) has no Kiln (vanilla client){(kick ? ": refusing" : held ? ": letting it in" : "")}");
                if (kick)
                {
                    // A vanilla game frees this frame unread; a Kiln build too old to prove itself shows the reason, so
                    // it gets the time REJECT normally gets before the game's own login error.
                    SendFrame(p.Id, EncodeRejectAdvertised(new Wire.Reject(VanillaGate.RefusalReason(kilnClientMods), [])), p);
                    p.KickAt = held ? now.AddSeconds(1.5) : now;
                }
                else if (held)
                {
                    var rc = NetNative.GateRelease(p.Id);
                    if (rc != NetNative.Ok) Log(LogLevel.Warn, $"letting {p.Name} (0x{p.Id:x}) in: rc {rc}");
                }
            }
            if (p.KickAt is { } at && now >= at)
            {
                p.KickAt = null;
                var rc = NetNative.Kick(p.Id, _config.KickReason);
                Log(rc == NetNative.Ok ? LogLevel.Info : LogLevel.Warn, $"kick {p.Name} (0x{p.Id:x}): rc {rc}");
            }
            if (p.State == NetPeerState.Ready && VanillaGate.DefinitionsWaitOver(p.DefinitionsSince, now, _config))
            {
                Log(LogLevel.Warn, $"{p.Name} (0x{p.Id:x}): no content ack in {_config.DefinitionsWaitSeconds:0.#} s: sending its Kiln-only entities anyway");
                PeerHasDefinitions(p);
            }
            ExpirePartial(p, now);
            if (p.State == NetPeerState.Ready && (now - p.PingSentAt).TotalSeconds >= 10) Ping(p);
        }
    }

    private const string ContentChannel = "kiln:content";

    /// <summary>A new content state is on its way to this Kiln client: Kiln-only entities queued from now on wait for it, as
    /// they may be of a template it is bringing. Tick thread.</summary>
    internal static void PeerAwaitsDefinitions(NetPeer p)
    {
        if (p.DefinitionsSince != null || NetNative.ReadGate().KilnTemplates == 0) return;
        p.DefinitionsSince = DateTime.UtcNow;
        NetNative.WithholdSet(p.Id, true);
    }

    /// <summary>This Kiln client acknowledged its content state, however that went: what it can have, it has. Kiln-only
    /// entities go out now; one of a template it still lacks is dropped by its guard. Tick thread.</summary>
    internal static void PeerHasDefinitions(NetPeer p)
    {
        var waited = p.DefinitionsSince is { } since ? (DateTime.UtcNow - since).TotalMilliseconds : -1;
        p.DefinitionsSince = null;
        NetNative.WithholdSet(p.Id, false);
        if (waited >= 0) Log(LogLevel.Info, $"{p.Name} (0x{p.Id:x}): has the server's definitions after {waited:0} ms: its Kiln-only entities go out");
    }

    /// <summary>Tests only (net-test): Kiln.Network stops deciding, as if it had died, so kiln.net's own limit is seen.</summary>
    internal static bool TestFreezeVanillaDecisions;

    private static uint _gateMode = uint.MaxValue;
    private static string _gateNote = "";

    /// <summary>Keeps the native gate in step with the policy and says, once per change, what that means for a client
    /// without Kiln. See docs/kiln/network.md "Clients without Kiln".</summary>
    private static void UpdateGate(in NetNative.GateInfo gate, bool refuse)
    {
        var mode = VanillaGate.GateMode(NetAdvertise.RequiredReady, refuse, gate.KilnTemplates);
        if (mode != _gateMode && NetNative.GateSet(mode) == NetNative.Ok) _gateMode = mode;
        if (!NetAdvertise.RequiredReady) return;  // the first seconds after boot: the mods that could refuse aren't known
        var note =
            gate.KilnTemplates > 0 && !gate.CanWithhold ? $"vanilla clients are refused: the server has {gate.KilnTemplates} template(s) only Kiln has, and this build can't keep them from a vanilla client" :
            refuse && !gate.CanHold ? "vanilla clients are refused, but this build can't hold them first: they can crash before the refusal arrives" :
            refuse ? "" :
            gate.KilnTemplates > 0 ? $"vanilla clients may join: {gate.KilnTemplates} template(s) only Kiln has are kept from them. Kiln items and other Kiln ids in what they do get can still crash them" :
            "";
        if (note == _gateNote) return;
        _gateNote = note;
        if (note.Length > 0) Log(LogLevel.Warn, note);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Client role

    private static void ClientTick(in TickInfo tick) => ClientTimers();

    private static void ClientTimers()
    {
        if (_link is not { } link) return;
        var now = DateTime.UtcNow;
        if (link.State == NetPeerState.Connecting && now >= link.NextHelloAt)
        {
            if (link.HelloAttempts < 3)
            {
                SendHello(link, update: false);
                link.HelloAttempts++;
                link.LastHelloAt = now;
                link.NextHelloAt = now.AddSeconds(3);
            }
            else if ((now - link.LastHelloAt).TotalSeconds >= 4)
            {
                link.State = NetPeerState.Vanilla;
                Log(LogLevel.Info, "the server did not answer the Kiln handshake: no Kiln there (vanilla server)");
                _serverAdvert = NetAdvertise.Parse(new Dictionary<string, string>(), null);
                LastServer.Record("vanilla", null, _serverAdvert, null);
                ServerVanillaHandlers.Raise(h => h(link), "Network.ServerVanilla");
            }
        }
        ExpirePartial(link, now);
        if (link.State == NetPeerState.Ready && (now - link.PingSentAt).TotalSeconds >= 10) Ping(link);
    }

    private static void SendHello(NetPeer link, bool update)
    {
        var chans = AllChannels().Select(c => new Wire.ChannelEntry(0, c.Name, c.Version, c.Required)).ToList();
        var hello = new Wire.Hello(Wire.Protocol, Game.KilnVersion, (uint)Environment.TickCount, LocalMods(client: true), chans,
            PropertyValues());
        var rc = SendFrame(NetNative.PeerServer, Wire.EncodeHello(hello), link);
        if (!update) Log(LogLevel.Info, $"handshake: hello #{link.HelloAttempts + 1} to the server ({hello.Mods.Count} mod(s), {chans.Count} channel(s)), rc {rc}");
    }

    private static void ClientFrame(Wire.Type type, ReadOnlySpan<byte> frame)
    {
        if (_link is not { } link) return;
        switch (type)
        {
            case Wire.Type.Welcome:
            {
                Wire.Welcome w;
                try { w = Wire.DecodeWelcome(frame); }
                catch (Exception e) { Log(LogLevel.Warn, $"bad welcome: {e.Message}"); return; }
                var first = link.State != NetPeerState.Ready;
                link.KilnVersion = w.KilnVersion;
                link.Mods = w.Mods.Select(m => new NetRemoteMod(m.Id, m.Version, m.Side)).ToList();
                link.Properties = w.Properties;
                link.Warnings = w.Warnings;
                _serverTable = w.Channels.ToList();
                RemapClientChannels(link);
                link.State = NetPeerState.Ready;
                _serverAdvert = NetAdvertise.Parse(w.Properties, w.KilnVersion);
                if (first)
                {
                    Log(LogLevel.Info, $"server speaks Kiln {w.KilnVersion} (policy {w.Policy}): {link.Channels.Count} shared channel(s)" +
                                       (w.Warnings.Count > 0 ? "; " + string.Join("; ", w.Warnings.Take(4)) : ""));
                    LastServer.Record(_serverAdvert.Present ? "welcome" : "vanilla", null, _serverAdvert, w.KilnVersion);
                    ServerReadyHandlers.Raise(h => h(link), "Network.ServerReady");
                }
                break;
            }
            case Wire.Type.Channels:
            {
                try
                {
                    var r = new NetReader(frame[Wire.HeaderSize..]);
                    _serverTable = Wire.ReadChannels(ref r);
                    RemapClientChannels(link);
                }
                catch (Exception e)
                {
                    Log(LogLevel.Warn, $"bad channel table: {e.Message}");
                }
                break;
            }
            case Wire.Type.Reject:
            {
                Wire.Reject j;
                try { j = Wire.DecodeReject(frame); }
                catch (Exception) { j = new Wire.Reject("refused", []); }
                link.State = NetPeerState.Rejected;
                link.Properties = j.Properties;
                _serverAdvert = NetAdvertise.Parse(j.Properties, null);
                Log(LogLevel.Warn, $"the server refused this client: {j.Reason}");
                LastServer.Record("rejected", j.Reason, _serverAdvert, null);
                RejectedHandlers.Raise(h => h(j.Reason, j.Details), "Network.Rejected");
                break;
            }
            case Wire.Type.Data:
            case Wire.Type.Fragment:
                if (link.State == NetPeerState.Ready) ReceiveData(link, type, frame, NetDirection.ToClient);
                break;
            case Wire.Type.Ping:
                Pong(link, frame);
                break;
            case Wire.Type.Pong:
                HandlePong(link, frame);
                break;
        }
        if (!_clientTickHooked) ClientTimers();
    }

    private static void RemapClientChannels(NetPeer link)
    {
        link.Channels.Clear();
        link.ChannelNames.Clear();
        foreach (var e in _serverTable)
        {
            var ch = ByName(e.Name);
            if (ch is null || ch.Version != e.Version) continue;
            link.Channels[e.Name] = e.Id;
            link.ChannelNames[e.Id] = e.Name;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Data

    private const int DataHeader = Wire.HeaderSize + 4;              // + u16 channel, u16 message
    private const int FragHeader = Wire.HeaderSize + 2 + 4 + 2 + 2;  // + u16 channel, u32 seq, u16 index, u16 count
    private const int FragFirstExtra = 2 + 4;                        // u16 message, u32 total

    private static int SendFrame(uint peer, ReadOnlySpan<byte> frame, NetPeer? p)
    {
        var rc = NetNative.Send(peer, frame);
        if (rc == NetNative.Ok)
        {
            Interlocked.Increment(ref _framesOut);
            if (p != null)
            {
                p.BytesOut += frame.Length;
            }
        }
        return rc;
    }

    private static bool SendEncoded(uint peer, NetPeer p, ushort channelId, Encoded e)
    {
        var max = (int)NetNative.MaxFrame;
        if (e.Body.Length > _config.MaxMessageBytes) return false;
        if (DataHeader + e.Body.Length <= max)
        {
            var w = Wire.Begin(Wire.Type.Data, DataHeader + e.Body.Length);
            w.UInt16(channelId).UInt16(e.Message).Raw(e.Body);
            if (SendFrame(peer, w.WrittenSpan, p) != NetNative.Ok) return false;
        }
        else
        {
            var chunk0 = max - FragHeader - FragFirstExtra;
            var chunk = max - FragHeader;
            var count = 1 + (e.Body.Length - chunk0 + chunk - 1) / chunk;
            if (count > ushort.MaxValue) return false;
            var seq = ++p.NextSeq;
            var off = 0;
            for (var i = 0; i < count; i++)
            {
                var n = Math.Min(i == 0 ? chunk0 : chunk, e.Body.Length - off);
                var w = Wire.Begin(Wire.Type.Fragment, max);
                w.UInt16(channelId).UInt32(seq).UInt16((ushort)i).UInt16((ushort)count);
                if (i == 0) w.UInt16(e.Message).UInt32((uint)e.Body.Length);
                w.Raw(e.Body.AsSpan(off, n));
                off += n;
                if (SendFrame(peer, w.WrittenSpan, p) != NetNative.Ok) return false;
            }
        }
        p.MessagesOut++;
        return true;
    }

    public static bool SendToPeer(NetChannel ch, NetPeer p, Encoded e)
    {
        if (ch.Closed || p.State != NetPeerState.Ready || p.IsServer) return false;
        if (!p.Channels.TryGetValue(ch.Name, out var id)) return false;
        if (!SendEncoded(p.Id, p, id, e)) return false;
        ch.MessagesOut++;
        ch.BytesOut += e.Body.Length;
        return true;
    }

    public static bool SendToServer(NetChannel ch, Encoded e)
    {
        if (ch.Closed || _link is not { State: NetPeerState.Ready } link) return false;
        if (!link.Channels.TryGetValue(ch.Name, out var id)) return false;
        lock (link)
        {
            if (!SendEncoded(NetNative.PeerServer, link, id, e)) return false;
        }
        ch.MessagesOut++;
        ch.BytesOut += e.Body.Length;
        return true;
    }

    private static NetChannel? ChannelForWire(NetPeer p, ushort id, NetDirection dir)
    {
        if (dir == NetDirection.ToServer)
        {
            var ch = ByServerId(id);
            return ch != null && p.Channels.ContainsKey(ch.Name) ? ch : null;
        }
        return p.ChannelNames.TryGetValue(id, out var name) ? ByName(name) : null;
    }

    private static void ReceiveData(NetPeer p, Wire.Type type, ReadOnlySpan<byte> frame, NetDirection dir)
    {
        ushort chanId, msgId;
        byte[] body;
        try
        {
            var r = new NetReader(frame[Wire.HeaderSize..]);
            chanId = r.UInt16();
            if (type == Wire.Type.Data)
            {
                msgId = r.UInt16();
                body = r.Rest().ToArray();
            }
            else
            {
                var seq = r.UInt32();
                var index = r.UInt16();
                var count = r.UInt16();
                if (count == 0 || index >= count) throw new NetFormatException("bad fragment index");
                if (!p.Partial.TryGetValue(seq, out var part))
                {
                    if (index != 0)
                    {
                        // first fragment lost or reordered: keep a placeholder only when it can still complete
                        CountDrop(p, "fragment before its first");
                        return;
                    }
                    var m = r.UInt16();
                    var total = (int)r.UInt32();
                    if (total > _config.MaxMessageBytes || total < 0) { CountDrop(p, $"message of {total} bytes over the limit"); return; }
                    if (p.Partial.Count >= 16) { CountDrop(p, "too many partial messages"); return; }
                    part = new NetPeer.Reassembly(chanId, m, total, count);
                    p.Partial[seq] = part;
                }
                else if (index == 0)
                {
                    r.UInt16();
                    r.UInt32();
                }
                if (part.Have.Length != count || part.Have[index]) { CountDrop(p, "duplicate fragment"); return; }
                var chunk = r.Rest();
                if (part.Filled + chunk.Length > part.Buffer.Length)
                {
                    // kiln.net pads every game-transport frame to a multiple of 4 bytes (the game's bit writers flush
                    // 32-bit words): the last fragment may carry up to 3 zero bytes past the message's end. Seen with a
                    // real client; the lab transport does not pad.
                    var excess = part.Filled + chunk.Length - part.Buffer.Length;
                    if (excess <= 3 && part.Received + 1 == count && chunk[^excess..].IndexOfAnyExcept((byte)0) < 0) chunk = chunk[..^excess];
                    else { p.Partial.Remove(seq); CountDrop(p, "fragment overflow"); return; }
                }
                // fragments arrive in order on a reliable channel; place them sequentially
                chunk.CopyTo(part.Buffer.AsSpan(part.Filled));
                part.Filled += chunk.Length;
                part.Have[index] = true;
                part.Received++;
                if (part.Received < count) return;
                p.Partial.Remove(seq);
                if (part.Filled != part.Buffer.Length) { CountDrop(p, "short message"); return; }
                msgId = part.Message;
                body = part.Buffer;
            }
        }
        catch (NetFormatException e)
        {
            CountDrop(p, e.Message);
            return;
        }
        if (body.Length > _config.MaxMessageBytes) { CountDrop(p, "message over the limit"); return; }
        var ch = ChannelForWire(p, chanId, dir);
        if (ch is null) return;  // a channel closed meanwhile
        p.MessagesIn++;
        p.BytesIn += body.Length;
        ch.MessagesIn++;
        ch.BytesIn += body.Length;
        if (body.Length > ch.MaxMessageBytes) { CountDrop(p, $"message over {ch.Name}'s limit"); return; }
        NetChannel.MessageType? mt;
        lock (Lock) ch.ById.TryGetValue(msgId, out mt);
        var ctx = new NetContext(ch, p, dir);
        if (mt is null)
        {
            if (ch.Raw is { } raw) NetModCalls.Invoke(ch.Owner, () => raw(ctx, msgId, body), $"{ch.Name} raw handler");
            return;
        }
        if ((mt.Direction & dir) == 0 || mt.Handler is null) return;
        object message;
        try
        {
            message = mt.Decode(body);
        }
        catch (Exception e)
        {
            ch.DecodeErrors++;
            if (ch.DecodeErrors <= 5) Log(LogLevel.Warn, $"{ch.Name}: cannot decode {mt.Type.Name} from {p.Name}: {e.Message}");
            return;
        }
        NetModCalls.Invoke(ch.Owner, () => mt.Handler(message, ctx), $"{ch.Name} {mt.Type.Name} handler");
    }

    private static void ExpirePartial(NetPeer p, DateTime now)
    {
        if (p.Partial.Count == 0) return;
        foreach (var k in p.Partial.Where(kv => (now - kv.Value.Started).TotalSeconds > 15).Select(kv => kv.Key).ToList())
        {
            p.Partial.Remove(k);
            CountDrop(p, "incomplete message timed out");
        }
    }

    private static void Ping(NetPeer p)
    {
        p.PingNonce = (uint)Random.Shared.Next();
        p.PingSentAt = DateTime.UtcNow;
        var w = Wire.Begin(Wire.Type.Ping, 16);
        w.UInt32(p.PingNonce).UInt64((ulong)Environment.TickCount64);
        SendFrame(p.Id, w.WrittenSpan, p);
    }

    private static void Pong(NetPeer p, ReadOnlySpan<byte> frame)
    {
        if (frame.Length < Wire.HeaderSize + 12) return;
        var w = Wire.Begin(Wire.Type.Pong, 16);
        w.Raw(frame.Slice(Wire.HeaderSize, 12));
        SendFrame(p.Id, w.WrittenSpan, p);
    }

    private static void HandlePong(NetPeer p, ReadOnlySpan<byte> frame)
    {
        try
        {
            var r = new NetReader(frame[Wire.HeaderSize..]);
            var nonce = r.UInt32();
            var sent = (long)r.UInt64();
            if (nonce == p.PingNonce) p.RoundTrip = TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - sent));
        }
        catch (NetFormatException)
        {
        }
    }

    public static NetworkStatus Status()
    {
        var s = NetNative.ReadStatus();
        var peers = ServerPeers();
        return new NetworkStatus(NetNative.Available, s is { } a && (a.Roles & NetNative.RoleServer) != 0,
            s is { } b && (b.Roles & NetNative.RoleClient) != 0, s is { Lab: 1 }, peers.Count + (_link != null ? 1 : 0),
            peers.Count(p => p.State == NetPeerState.Ready) + (_link is { State: NetPeerState.Ready } ? 1 : 0),
            AllChannels().Count, s?.MaxFrame ?? 0, Interlocked.Read(ref _framesIn), Interlocked.Read(ref _framesOut),
            (long)(s?.RxBytes ?? 0), (long)(s?.TxBytes ?? 0), (long)(s?.Dropped ?? 0), (long)(s?.SendFailures ?? 0), _configError);
    }
}

/// <summary>Started by Kiln's service autostart (or the first use): subscribes to kiln.net so the server answers
/// handshakes even when no mod opened a channel.</summary>
internal static class NetworkStartup
{
    static NetworkStartup()
    {
        NetEngine.EnsureStarted();
        ConfigSync.Start();  // docs/kiln/sync.md
    }
}
