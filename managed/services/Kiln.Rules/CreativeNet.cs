// SPDX-License-Identifier: MIT
// kiln:creative: the creative menu's requests (take an item, set the time, the day and night cycle, creative mode on
// or off) and the server's answers. Opened by Kiln on both sides, not required: a game without it joins as before.
// A game hosting its own world talks to its own server half directly, with no network message.
namespace Kiln;

/// <summary>What the creative menu asks the server for.</summary>
internal enum CreativeAsk : byte
{
    State = 0,
    Give = 1,
    Time = 2,
    Cycle = 3,
    Creative = 4,
    Catalog = 5,
    Found = 6,
}

/// <summary>One request: <see cref="Kind"/> says which fields count.</summary>
internal readonly record struct CreativeRequest(CreativeAsk Kind, uint Item = 0, int Count = 0, int Level = -1, int Rarity = -1,
    double Value = 0);

/// <summary>What a player may do in the creative menu, as the server sees it.</summary>
[Flags]
internal enum CreativeAccess : uint
{
    None = 0,
    /// <summary>The player has the creative permission (an admin, a hosted world's host).</summary>
    Admin = 1,
    /// <summary>Creative mode is on for the player.</summary>
    On = 2,
    /// <summary>Taking items works (creative mode on, and this build can give items).</summary>
    Give = 4,
    /// <summary>Setting the time of day works (an admin, and this build can set it).</summary>
    Time = 8,
    /// <summary>The day and night cycle can be stopped or sped up (an admin, and this build can hold the speed).</summary>
    Cycle = 16,
}

internal readonly record struct CreativeStateMsg(CreativeAccess Access, TimeCycle Cycle, double Hours, string Rules, string Hooks);

internal readonly record struct CreativeReplyMsg(bool Ok, string Text);

/// <summary>A catalogue entry as the menu lists it.</summary>
internal readonly record struct CreativeItem(uint Id, string Name, string Label, ItemCategory Category, ItemRarity Rarity, bool IsGear,
    int LevelMin, int LevelMax, int MaxStack);

internal readonly record struct CreativeCatalogMsg(int Start, int Total, IReadOnlyList<CreativeItem> Items);

/// <summary>The items a player has found (picked up, crafted, used or looked at: the game's item knowledge), for the
/// menu's journey mode. <see cref="Available"/> false: this server cannot read knowledge.</summary>
internal readonly record struct CreativeFoundMsg(bool Available, IReadOnlyList<uint> Items);

internal static class CreativeNet
{
    public const string ChannelName = "kiln:creative";
    public const string ChannelVersion = "1";
    private const ushort MsgRequest = 0, MsgState = 1, MsgReply = 2, MsgCatalog = 3, MsgFound = 4;
    private static readonly object Lock = new();
    private static NetChannel? _channel;
    private static bool _ticking;
    private static int _session;
    internal static int Session => Volatile.Read(ref _session);
    private static readonly Queue<(int Session, Action Action)> Completions = new();

    // requests the server half runs on its tick (from the network, or from a hosting game's own menu)
    private static readonly CreativeInbox Inbox = new();

    public static bool Open => _channel is not null;

    public static void Start()
    {
        lock (Lock)
        {
            if (_channel is not null) return;
            var outer = ModContext.CurrentMod;
            ModContext.CurrentMod = null;
            try
            {
                if (!_ticking)
                {
                    ServiceTick.Add("kiln creative", static (in TickInfo _) => Drain());
                    Events.On<ServerStoppingEvent>(_ => WorldClosed(), EventPriority.Lowest);
                    _ticking = true;
                }
                if (!Network.Available) return;
                _channel = Network.Channel(ChannelName, ChannelVersion, required: false, maxMessageBytes: 64 * 1024)
                    .Register<CreativeRequest>(MsgRequest,
                        static (m, w) => w.Byte((byte)m.Kind).UInt32(m.Item).Int32(m.Count).Int32(m.Level).Int32(m.Rarity).Double(m.Value),
                        static (ref NetReader r) => new CreativeRequest((CreativeAsk)r.Byte(), r.UInt32(), r.Int32(), r.Int32(), r.Int32(), r.Double()),
                        static (m, c) => Inbox.Enqueue(c.Player, m, false, c.Sender), NetDirection.ToServer)
                    .Register<CreativeStateMsg>(MsgState,
                        static (m, w) => w.UInt32((uint)m.Access).Byte((byte)m.Cycle).Double(m.Hours).String(m.Rules).String(m.Hooks),
                        static (ref NetReader r) => new CreativeStateMsg((CreativeAccess)r.UInt32(), (TimeCycle)r.Byte(), r.Double(),
                            r.String() ?? "", r.String() ?? ""),
                        static (m, _) => CreativeMenu.OnState(m), NetDirection.ToClient)
                    .Register<CreativeReplyMsg>(MsgReply,
                        static (m, w) => w.Bool(m.Ok).String(m.Text),
                        static (ref NetReader r) => new CreativeReplyMsg(r.Bool(), r.String() ?? ""),
                        static (m, _) => CreativeMenu.OnReply(m), NetDirection.ToClient)
                    .Register<CreativeCatalogMsg>(MsgCatalog, EncodeCatalog, DecodeCatalog,
                        static (m, _) => CreativeMenu.OnCatalog(m), NetDirection.ToClient)
                    .Register<CreativeFoundMsg>(MsgFound, EncodeFound, DecodeFound,
                        static (m, _) => CreativeMenu.OnFound(m), NetDirection.ToClient);
                Network.ServerReady += _ => CreativeMenu.OnServerReady();
                Network.Disconnected += _ => CreativeMenu.OnDisconnected();
                Events.On<NetPeerReadyEvent>(e => Inbox.Enqueue(e.Peer.Player, new CreativeRequest(CreativeAsk.State), false, e.Peer),
                    EventPriority.Monitor);
            }
            catch (Exception e)
            {
                RulesRuntime.Log(LogLevel.Warn, $"channel {ChannelName} unavailable: {e.Message}");
            }
            finally
            {
                ModContext.CurrentMod = outer;
            }
        }
    }

    internal static void EncodeCatalog(CreativeCatalogMsg m, NetWriter w)
    {
        w.Int32(m.Start).Int32(m.Total).UInt16((ushort)m.Items.Count);
        foreach (var i in m.Items)
            w.UInt32(i.Id).String(i.Name).String(i.Label == i.Name ? "" : i.Label).Byte((byte)i.Category).Byte((byte)i.Rarity)
                .Bool(i.IsGear).Byte((byte)Math.Clamp(i.LevelMin, 0, 255)).Byte((byte)Math.Clamp(i.LevelMax, 0, 255))
                .UInt16((ushort)Math.Clamp(i.MaxStack, 0, ushort.MaxValue));
    }

    internal static void EncodeFound(CreativeFoundMsg m, NetWriter w)
    {
        w.Bool(m.Available).Int32(m.Items.Count);
        foreach (var id in m.Items) w.UInt32(id);
    }

    internal static CreativeFoundMsg DecodeFound(ref NetReader r)
    {
        var ok = r.Bool();
        var n = r.Int32();
        if (n is < 0 or > 16380) throw new FormatException("Invalid found-item count.");
        var list = new uint[n];
        for (var i = 0; i < n; i++) list[i] = r.UInt32();
        return new CreativeFoundMsg(ok, list);
    }

    internal static CreativeCatalogMsg DecodeCatalog(ref NetReader r)
    {
        int start = r.Int32(), total = r.Int32(), n = r.UInt16();
        if (start < 0 || total < 0 || total > 16384 || n > 150 || start > total - n)
            throw new FormatException("Invalid catalogue page.");
        var list = new List<CreativeItem>(n);
        for (var k = 0; k < n; k++)
        {
            var id = r.UInt32();
            var name = r.String() ?? "";
            var label = r.String() is { Length: > 0 } l ? l : name;
            list.Add(new CreativeItem(id, name, label, (ItemCategory)r.Byte(), (ItemRarity)r.Byte(), r.Bool(), r.Byte(), r.Byte(), r.UInt16()));
        }
        return new CreativeCatalogMsg(start, total, list);
    }

    // ---------------------------------------------------------------------------------------- client side ------

    /// <summary>Client: sends a request to the server (or, in a game hosting its world, to its own server half). False
    /// when there is nobody to ask: a remote server without Kiln's creative channel.</summary>
    public static bool Ask(CreativeRequest req)
    {
        if (Hosting(out var self))
        {
            return Inbox.Enqueue(self, req, true);
        }
        var ch = _channel;
        if (ch is null) return false;
        try
        {
            return ch.SendToServer(req);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool Hosting(out EntityId self)
    {
        self = default;
        try
        {
            if (!Client.Available || !Client.IsHosting || !Client.HasLocalPlayer) return false;
            self = Client.LocalPlayer;
            return !self.IsNone;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------- server side ------

    private static void Drain()
    {
        for (var i = 0; i < 32; i++)
        {
            (int Session, Action Action) work;
            lock (Lock)
            {
                if (!Completions.TryDequeue(out work)) break;
            }
            if (work.Session == Session)
            {
                try { work.Action(); }
                catch (Exception e) { RulesRuntime.Log(LogLevel.Warn, $"creative reply: {e.Message}"); }
            }
        }
        for (var i = 0; i < 16 && Inbox.TryDequeue(out var r); i++)
        {
            try
            {
                // Entity ids can be reused after a leave. A request belongs to the peer that sent it.
                if (r.Local ? !Hosting(out var self) || self != r.Player
                    : r.Peer is not { State: NetPeerState.Ready } || !ReferenceEquals(Network.PeerOf(r.Player), r.Peer)) continue;
                CreativeServer.Handle(r.Player, r.Req, r.Local);
            }
            catch (Exception e)
            {
                RulesRuntime.Log(LogLevel.Warn, $"creative menu request {r.Req.Kind} from player {r.Player}: {e.Message}");
            }
        }
    }

    internal static void Post(Action action, int session)
    {
        lock (Lock)
            if (session == _session && Completions.Count < 1024) Completions.Enqueue((session, action));
    }

    internal static void WorldClosed()
    {
        lock (Lock)
        {
            Interlocked.Increment(ref _session);
            Completions.Clear();
            Inbox.Clear();
        }
    }

    /// <summary>Server: the answer goes to the player's game (or straight to the hosting game's own menu).</summary>
    public static void SendState(EntityId player, CreativeStateMsg m, bool local)
    {
        if (local || IsHost(player)) CreativeMenu.OnState(m);
        else Send(player, m);
    }

    public static void SendReply(EntityId player, CreativeReplyMsg m, bool local)
    {
        if (local || IsHost(player)) CreativeMenu.OnReply(m);
        else Send(player, m);
    }

    public static void SendCatalog(EntityId player, CreativeCatalogMsg m, bool local)
    {
        if (local || IsHost(player)) CreativeMenu.OnCatalog(m);
        else Send(player, m);
    }

    public static void SendFound(EntityId player, CreativeFoundMsg m, bool local)
    {
        if (local || IsHost(player)) CreativeMenu.OnFound(m);
        else Send(player, m);
    }

    private static bool IsHost(EntityId player) => Hosting(out var self) && self == player;

    private static void Send<T>(EntityId player, T m) where T : notnull
    {
        var ch = _channel;
        if (ch is null) return;
        try
        {
            ch.SendTo(player, m);
        }
        catch (Exception)
        {
            // the player left
        }
    }
}

// Keep both memory use and tick work bounded even when a modified client floods an optional channel. Peer identity
// is retained until dispatch; no field in CreativeRequest can select another player.
internal sealed class CreativeInbox
{
    internal readonly record struct Entry(EntityId Player, CreativeRequest Req, bool Local, NetPeer? Peer);
    private readonly Queue<Entry> _queue = new();
    private readonly Dictionary<EntityId, (long Since, int Requests, int Pending, long FoundAt)> _players = new();
    internal int Count { get { lock (_queue) return _queue.Count; } }

    public bool Enqueue(EntityId player, CreativeRequest req, bool local, NetPeer? peer = null, long? at = null)
    {
        if (player.IsNone || !Enum.IsDefined(req.Kind)) return false;
        var now = at ?? Environment.TickCount64;
        lock (_queue)
        {
            if (_queue.Count >= 128) return false;
            if (_players.Count >= 1024)
                foreach (var p in _players.Where(p => p.Value.Pending == 0 && now - p.Value.Since >= 2000).Select(p => p.Key).ToArray())
                    _players.Remove(p);
            if (!_players.TryGetValue(player, out var s))
            {
                if (_players.Count >= 1024) return false;
                s = (now, 0, 0, long.MinValue / 2);
            }
            if (now - s.Since >= 1000) s = (now, 0, s.Pending, s.FoundAt);
            if (s.Pending >= 4 || s.Requests >= 64 || (req.Kind == CreativeAsk.Found && now - s.FoundAt < 2000)) return false;
            _players[player] = (s.Since, s.Requests + 1, s.Pending + 1, req.Kind == CreativeAsk.Found ? now : s.FoundAt);
            _queue.Enqueue(new(player, req, local, peer));
            return true;
        }
    }

    public bool TryDequeue(out Entry entry)
    {
        lock (_queue)
        {
            if (!_queue.TryDequeue(out entry)) return false;
            var s = _players[entry.Player];
            _players[entry.Player] = (s.Since, s.Requests, s.Pending - 1, s.FoundAt);
            return true;
        }
    }
    public void Clear() { lock (_queue) { _queue.Clear(); _players.Clear(); } }
}
