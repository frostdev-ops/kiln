// SPDX-License-Identifier: MIT
// Action speed from a server to its clients: the Kiln channel "kiln:speed" v1 (required: false, so it never blocks a join;
// a vanilla client or an older Kiln has no channel and sees server-timed results). Kiln sends it itself: mods need no
// [Synced] config. Messages:
//  - Rates (1): the whole effective view (the defaults and every player with values of their own), whenever it changes and
//    to each client when it becomes ready. A client applies it to its own player's prediction and presentation.
//  - Items (2): the server's classified items (kiln.items' catalog: which item is a melee weapon, a tool or a ranged
//    weapon), once to each client: a client has no catalog of its own.
// A hosting client is a peer of its own server; it already applies the rates and drops the copies it receives.
namespace Kiln;

internal static unsafe class ActionSpeedRelay
{
    public const string ChannelName = "kiln:speed";
    public const string ChannelVersion = "1";
    public const byte WireVersion = 1;
    private const ushort MsgRates = 1, MsgItems = 2;
    private const int MaxPlayers = 64, MaxItems = 8192;

    internal sealed record RatesMsg(byte Version, float[] Defaults, (uint Entity, float[] Rates)[] Players);
    internal sealed record ItemsMsg((uint Item, byte Kind)[] Items);

    private static readonly object Lock = new();
    private static NetChannel? _channel;
    private static EventSubscription? _ready;
    private static bool _itemsSent;
    private static IDisposable? _tick;
    private static long _sent, _received;

    public static bool Open => _channel is not null;
    public static long Sent => Interlocked.Read(ref _sent);
    public static long Received => Interlocked.Read(ref _received);

    /// <summary>Opens the channel (Kiln's own, not the calling mod's). Idempotent; fails soft without kiln.net.</summary>
    public static void Start()
    {
        lock (Lock)
        {
            if (_channel is not null) return;
            bool net;
            try
            {
                net = Network.Available;
            }
            catch (Exception)
            {
                return;
            }
            if (!net) return;
            var outer = ModContext.CurrentMod;
            ModContext.CurrentMod = null;
            try
            {
                _channel = Network.Channel(ChannelName, ChannelVersion, required: false)
                    .Register<RatesMsg>(MsgRates, static (m, w) => EncodeRates(w, m), static (ref NetReader r) => DecodeRates(ref r),
                        static (m, ctx) => OnRates(m), NetDirection.ToClient)
                    .Register<ItemsMsg>(MsgItems, static (m, w) => EncodeItems(w, m), static (ref NetReader r) => DecodeItems(ref r),
                        static (m, ctx) => OnItems(m), NetDirection.ToClient);
                _ready = Events.On<NetPeerReadyEvent>(static e => SendInitial(e.Peer));
                Network.Disconnected += static _ => ActionSpeedState.SetRemote(ActionSpeedState.View.Empty);
                _tick ??= ServiceTick.Add("kiln action speed relay", static (in TickInfo t) => Sweep(t.Step));
            }
            catch (Exception e)
            {
                SpeedLog.Warn($"relay channel {ChannelName} unavailable: {e.Message}");
            }
            finally
            {
                ModContext.CurrentMod = outer;
            }
        }
    }

    // ---- the wire ----

    internal static RatesMsg ToMsg(ActionSpeedState.View v) =>
        new(WireVersion, v.Defaults.Select(d => (float)d).ToArray(),
            v.Players.OrderBy(kv => kv.Key).Take(MaxPlayers).Select(kv => (kv.Key, kv.Value.Select(x => (float)x).ToArray())).ToArray());

    internal static ActionSpeedState.View FromMsg(RatesMsg m)
    {
        static double Clean(float f) => float.IsFinite(f) ? Math.Clamp(f, ActionSpeed.Min, ActionSpeed.Max) : 1.0;
        var players = new Dictionary<uint, double[]>();
        foreach (var (e, r) in m.Players)
            if (e is >= 1 and <= 1023) players[e] = r.Select(Clean).ToArray();
        return new ActionSpeedState.View(m.Defaults.Select(Clean).ToArray(), players);
    }

    internal static void EncodeRates(NetWriter w, RatesMsg m)
    {
        w.Byte(m.Version);
        for (var k = 0; k < ActionSpeedState.Kinds; k++) w.Single(m.Defaults[k]);
        w.UInt16((ushort)m.Players.Length);
        foreach (var (e, r) in m.Players)
        {
            w.UInt32(e);
            for (var k = 0; k < ActionSpeedState.Kinds; k++) w.Single(r[k]);
        }
    }

    internal static RatesMsg DecodeRates(ref NetReader r)
    {
        var version = r.Byte();
        if (version != WireVersion) throw new NetFormatException($"{ChannelName}: rates version {version}, this Kiln reads {WireVersion}");
        var d = new float[ActionSpeedState.Kinds];
        for (var k = 0; k < d.Length; k++) d[k] = r.Single();
        int n = r.UInt16();
        if (n > MaxPlayers) throw new NetFormatException($"{ChannelName}: {n} players (at most {MaxPlayers})");
        var players = new (uint, float[])[n];
        for (var i = 0; i < n; i++)
        {
            var e = r.UInt32();
            var rates = new float[ActionSpeedState.Kinds];
            for (var k = 0; k < rates.Length; k++) rates[k] = r.Single();
            players[i] = (e, rates);
        }
        return new RatesMsg(version, d, players);
    }

    internal static void EncodeItems(NetWriter w, ItemsMsg m)
    {
        w.UInt32((uint)m.Items.Length);
        foreach (var (item, kind) in m.Items) w.UInt32(item).Byte(kind);
    }

    internal static ItemsMsg DecodeItems(ref NetReader r)
    {
        var n = r.UInt32();
        if (n > MaxItems) throw new NetFormatException($"{ChannelName}: {n} items (at most {MaxItems})");
        var items = new (uint, byte)[n];
        for (var i = 0; i < n; i++) items[i] = (r.UInt32(), r.Byte());
        return new ItemsMsg(items);
    }

    // ---- server side (tick thread) ----

    /// <summary>The view changed on a server or host: every Kiln client gets it.</summary>
    public static void Broadcast(ActionSpeedState.View view)
    {
        Start();
        if (_channel is not { } ch || !IsServerSide()) return;
        try
        {
            Interlocked.Add(ref _sent, ch.SendToAll(ToMsg(view)));
        }
        catch (Exception e)
        {
            SpeedLog.Warn($"sending the rates failed: {e.Message}");
        }
    }

    // a dedicated server, a host, or a client in the menu: the side whose own mods decide (a joined client applies the
    // server's)
    private static bool IsServerSide()
    {
        try
        {
            return !ActionSpeedState.IsJoinedClient();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void SendInitial(NetPeer peer)
    {
        if (_channel is not { } ch || !IsServerSide()) return;
        var items = Items();
        if (items is { Length: > 0 }) ch.SendTo(peer, new ItemsMsg(items));
        var view = ActionSpeedState.Compute();
        if (!view.IsIdentity && ch.SendTo(peer, ToMsg(view))) Interlocked.Increment(ref _sent);
    }

    private static (uint, byte)[]? Items()
    {
        var t = SpeedNative.T;
        if (t == null || t->ItemKinds == null) return null;
        uint n = 0;
        if (t->ItemKinds(null, 0, &n) != SpeedNative.Ok || n == 0) return null;
        var buf = new SpeedNative.ItemKind[Math.Min(n, MaxItems)];
        fixed (SpeedNative.ItemKind* p = buf)
            if (t->ItemKinds(p, (uint)buf.Length, &n) != SpeedNative.Ok) return null;
        return buf.Select(i => (i.Item, i.Kind)).ToArray();
    }

    // the item list can be read after clients joined (the catalog is read once a world loads): send it once then
    private static void Sweep(ulong step)
    {
        if (_itemsSent || step % 50 != 11 || _channel is not { } ch || !IsServerSide()) return;
        if (Items() is not { Length: > 0 } items) return;
        _itemsSent = true;
        ch.SendToAll(new ItemsMsg(items));
    }

    // ---- client side (main thread) ----

    private static void OnRates(RatesMsg m)
    {
        Interlocked.Increment(ref _received);
        if (IsServerSide()) return;  // a host's own copy: it applies its rates already
        ActionSpeedState.SetRemote(FromMsg(m));
    }

    private static void OnItems(ItemsMsg m)
    {
        Interlocked.Increment(ref _received);
        if (IsServerSide()) return;
        var t = SpeedNative.T;
        if (t == null || t->SetItemKinds == null) return;
        var buf = m.Items.Where(i => i.Kind < ActionSpeedState.Kinds && i.Item != 0)
            .Select(i => new SpeedNative.ItemKind { Item = i.Item, Kind = i.Kind }).ToArray();
        fixed (SpeedNative.ItemKind* p = buf) t->SetItemKinds(p, (uint)buf.Length);
    }

    /// <summary>Tests: the client's handling of a received message.</summary>
    internal static void Receive(RatesMsg m) => OnRates(m);
}
