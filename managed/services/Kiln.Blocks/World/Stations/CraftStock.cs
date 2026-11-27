// SPDX-License-Identifier: MIT
// GitHub #68 step 2, the crafting menu. While a Kiln client's crafting menu is open, the server tells it what the opted-in
// stations of the base it stands in add to its crafting stock, so the menu's counts and its craft button allow a craft
// that only those outputs can complete. Channel "kiln:craftstock", required: false: a vanilla client never waits on it,
// never says its menu opened and is never sent anything.
//  - Small: only the items of the recipes that take a lent item (native craft_items), only while the menu is open, and
//    again only when a count changed.
//  - Bound to the session: a player who leaves, a player who joins into the slot, a menu that closes or stops saying it
//    is open (5 s keep-alive, 15 s at most), and a new world end it. A send that did not go out is tried again.
//  - Only the base the player may craft from by the game's own right (CanAccessInventories, the payment's own test): a
//    visitor without it is sent nothing.
//  - Not authority: the craft's payment recounts everything (native kiln.stations, docs/re/stations.md §10).
//  - The station screen's fill buttons (0.2.0-dev.29.19, client capability stations.fill_ui): the game enables a
//    production station's "+1" and "+10" only when its own dry run on the bag and the Magic chests goes through. While
//    the snapshot is on, kiln-items notes each fill the game refused for missing materials; the client asks the server
//    about those (station, recipe, choices), the server answers how many more crafts its own fill with station outputs
//    covers (StationsCore.FillCrafts, the rules the press itself runs with), and the client's query says "can fill" for
//    up to that many. The press is the game's own; StationsCore.OnSourceFill runs it as Kiln's fill. docs/re/stations.md §10.3.
//  - A production station's screen (0.2.0-dev.30.8, GitHub #132): its ingredient rows count only what their transfer
//    buttons move (the game's item transfer: the bag and the Magic chests), so the client names every item with none
//    there (View): the rows keep the game's counts and the snapshot stays on for the fill buttons above.
//  - Since 0.2.0-dev.31.16 those transfer buttons also take from the player's fill-opted links (Kiln's item fill,
//    StationsCore.SourceItemFill): message 10 names, per item those links hold, the bag, the Magic chests and the links
//    together, and the station screen counts that (View); every other item keeps the game's count. A server without it
//    never sends message 10, and the rows stay the game's.
// docs/kiln/services/stations.md, "What each client shows".
using System.Collections.Concurrent;
using System.Text;

namespace Kiln;

/// <summary>Server to client: one snapshot, per item the bag, the base's Magic chests and the opted-in outputs together.
/// Empty: the game's own counts again.</summary>
internal sealed record CraftStockRows(CraftStockRow[] Rows)
{
    public const ushort Id = 1;

    public static void Encode(CraftStockRows m, NetWriter w)
    {
        var n = Math.Min(m.Rows.Length, CraftStock.MaxRows);
        w.UInt16((ushort)n);
        for (var i = 0; i < n; i++) w.UInt32(m.Rows[i].Item).UInt32(m.Rows[i].Count);
    }

    public static CraftStockRows Decode(ref NetReader r)
    {
        var n = Math.Min((int)r.UInt16(), CraftStock.MaxRows);
        var rows = new CraftStockRow[n];
        for (var i = 0; i < n; i++) rows[i] = new CraftStockRow(r.UInt32(), r.UInt32());
        return new CraftStockRows(rows);
    }
}

internal readonly record struct CraftStockRow(uint Item, uint Count);

/// <summary>Server to client (GitHub #132, 0.2.0-dev.31.16): what a production station screen's ingredient rows may count,
/// per item the player's fill-opted links hold: the bag, the Magic chests of the player's base and those links together
/// (what the rows' transfer buttons move through Kiln's item fill). Empty: none (the game's counts). An older client has
/// no message 10 and drops it. Id 10: 5 to 9 are DyeStock's, LockStock's and BuildStock's on this channel.</summary>
internal sealed record CraftStockStationRows(CraftStockRow[] Rows)
{
    public const ushort Id = 10;
    public static void Encode(CraftStockStationRows m, NetWriter w) => CraftStockRows.Encode(new CraftStockRows(m.Rows), w);
    public static CraftStockStationRows Decode(ref NetReader r) => new(CraftStockRows.Decode(ref r).Rows);
}

/// <summary>Client to server: a crafting menu opened (and, every 5 s, is still open) or closed.</summary>
internal sealed record CraftStockHello(bool Open)
{
    public const ushort Id = 2;
    public static void Encode(CraftStockHello m, NetWriter w) => w.Bool(m.Open);
    public static CraftStockHello Decode(ref NetReader r) => new(r.Bool());
}

/// <summary>One category choice of a station screen's fill (the game's selection: category, option index).</summary>
internal readonly record struct FillChoice(uint Category, byte Index);

/// <summary>A fill the station screen asked the game about: the station, the recipe, its four category choices.</summary>
internal readonly record struct FillKey(uint Station, uint Recipe, FillChoice C0, FillChoice C1, FillChoice C2, FillChoice C3)
{
    public IEnumerable<FillChoice> Choices => [C0, C1, C2, C3];

    public static void Write(NetWriter w, in FillKey k)
    {
        w.UInt32(k.Station).UInt32(k.Recipe);
        foreach (var c in k.Choices) w.UInt32(c.Category).Byte(c.Index);
    }

    public static FillKey Read(ref NetReader r)
    {
        var station = r.UInt32();
        var recipe = r.UInt32();
        var c = new FillChoice[4];
        for (var i = 0; i < 4; i++) c[i] = new FillChoice(r.UInt32(), r.Byte());
        return new FillKey(station, recipe, c[0], c[1], c[2], c[3]);
    }

    public static FillKey Of(in StationsNative.FillKey k) =>
        new(k.Station, k.Recipe, new(k.C0.Category, k.C0.Index), new(k.C1.Category, k.C1.Index), new(k.C2.Category, k.C2.Index),
            new(k.C3.Category, k.C3.Index));

    public StationsNative.FillKey Native() => new()
    {
        Station = Station, Recipe = Recipe,
        C0 = new StationsNative.Choice { Category = C0.Category, Index = C0.Index },
        C1 = new StationsNative.Choice { Category = C1.Category, Index = C1.Index },
        C2 = new StationsNative.Choice { Category = C2.Category, Index = C2.Index },
        C3 = new StationsNative.Choice { Category = C3.Category, Index = C3.Index },
    };

    /// <summary>The choices as a fill takes them (none: the station's own).</summary>
    public IReadOnlyList<CategoryChoice> FillChoices() => [.. Choices.Where(c => c.Category != 0).Select(c => new CategoryChoice(c.Category, c.Index))];
}

/// <summary>The server's answer for one asked fill: how many more crafts its fill with station outputs covers now.</summary>
internal readonly record struct FillAnswer(FillKey Key, uint Crafts);

/// <summary>Client to server: fills the station screen asked the game about, that the game refused for missing materials
/// and that no answer names yet (at most 16).</summary>
internal sealed record CraftStockFillAsk(FillKey[] Keys)
{
    public const ushort Id = 3;

    public static void Encode(CraftStockFillAsk m, NetWriter w)
    {
        var n = Math.Min(m.Keys.Length, CraftStock.MaxFillKeys);
        w.Byte((byte)n);
        for (var i = 0; i < n; i++) FillKey.Write(w, m.Keys[i]);
    }

    public static CraftStockFillAsk Decode(ref NetReader r)
    {
        var n = Math.Min((int)r.Byte(), CraftStock.MaxFillKeys);
        var keys = new FillKey[n];
        for (var i = 0; i < n; i++) keys[i] = FillKey.Read(ref r);
        return new CraftStockFillAsk(keys);
    }
}

/// <summary>Server to client: the answers for every fill the open menu asked about. Empty: none (the game's buttons).</summary>
internal sealed record CraftStockFillAnswers(FillAnswer[] Rows)
{
    public const ushort Id = 4;

    public static void Encode(CraftStockFillAnswers m, NetWriter w)
    {
        var n = Math.Min(m.Rows.Length, CraftStock.MaxFillKeys);
        w.Byte((byte)n);
        for (var i = 0; i < n; i++)
        {
            FillKey.Write(w, m.Rows[i].Key);
            w.UInt32(m.Rows[i].Crafts);
        }
    }

    public static CraftStockFillAnswers Decode(ref NetReader r)
    {
        var n = Math.Min((int)r.Byte(), CraftStock.MaxFillKeys);
        var rows = new FillAnswer[n];
        for (var i = 0; i < n; i++)
        {
            var k = FillKey.Read(ref r);
            rows[i] = new FillAnswer(k, r.UInt32());
        }
        return new CraftStockFillAnswers(rows);
    }
}

/// <summary>Started at the first service tick (ServiceTick.AutoStart), so the channels are there before anyone joins. It
/// registers the channel and, on a client, watches the screen; nothing else runs until a Kiln client opens a crafting
/// menu (Kiln.Blocks' data tick does not start here). Since 0.2.0-dev.29.19 also the channel kiln:upgrades (an upgrade
/// station's window, UpgradeStations), which does nothing until a station sends one.</summary>
internal static class CraftStockStartup
{
    static CraftStockStartup()
    {
        CraftStock.Start();
        UpgradeNet.Start();
    }
}

internal static class CraftStock
{
    public const string ChannelName = "kiln:craftstock";
    public const string ChannelVersion = "1";
    internal const int MaxRows = StationsNative.MaxCraftStock;
    internal const ulong RefreshSteps = 30;  // ~1.2 s: an open menu follows the stations' outputs
    internal static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan SessionLife = TimeSpan.FromSeconds(15);
    internal static readonly CraftStockRows Off = new([]);
    internal const int MaxFillKeys = StationsNative.MaxFillKeys;
    internal const string FillUiCapability = "stations.fill_ui";
    internal const string StockUiCapability = "stations.stock_ui";

    private sealed class Session
    {
        public string? Sent;  // the key of the last snapshot that went out; null: none yet, or the last send failed
        public long Seen;     // ms of the last hello
        public bool Due = true;
        public readonly List<FillKey> Asked = [];  // the station screen's fills the client asked about, oldest first
        public string? FillSent;                   // the key of the last answers that went out
        public bool FillDue;
        public string? StationSent = "";           // the key of the last station rows that went out ("": none, as the client
                                                   // starts; null: the last send failed)
    }

    private static readonly object Gate = new();
    private static NetChannel? _channel;
    private static bool _started, _playersWatched;
    private static IDisposable? _tick;
    private static ulong _lastStep;
    private static int _warned;
    private static readonly Dictionary<uint, Session> Sessions = new();
    private static readonly ConcurrentQueue<(EntityId Player, bool Open)> Inbox = new();
    private static readonly ConcurrentQueue<(EntityId Player, FillKey[] Keys)> AskInbox = new();
    private static readonly Dictionary<BaseId, IReadOnlyDictionary<ItemId, long>> StockCache = new();

    /// <summary>Test hooks: the clock (ms), what a player is sent (null: off), the delivery (true: it went out).</summary>
    internal static Func<long> Clock = static () => Environment.TickCount64;
    internal static Func<EntityId, CraftStockRows?>? BuildHook;
    internal static Func<EntityId, CraftStockRows, bool>? SendHook;
    /// <summary>Test hooks: the answers for a player's asked fills (null: the real plan), their delivery.</summary>
    internal static Func<EntityId, IReadOnlyList<FillKey>, FillAnswer[]>? FillBuildHook;
    internal static Func<EntityId, CraftStockFillAnswers, bool>? FillSendHook;
    /// <summary>Test hooks: a player's station rows (null: the real count), their delivery.</summary>
    internal static Func<EntityId, CraftStockRows>? StationBuildHook;
    internal static Func<EntityId, CraftStockStationRows, bool>? StationSendHook;

    /// <summary>Registers the channel (and on a client the screen watch). Idempotent; a refused channel is not retried.</summary>
    internal static void Start()
    {
        lock (Gate)
        {
            if (_started) return;
            _started = true;
        }
        if (!Network.Available) return;
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;  // Kiln's own channel, not a mod's
        try
        {
            _channel = Register(Network.Channel(ChannelName, ChannelVersion, required: false));
        }
        catch (Exception e)
        {
            ModLog.Warn($"kiln.stations: the channel {ChannelName} is unavailable: {e.Message}");
            return;
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
        try
        {
            if (Client.IsClient) StartClient();
        }
        catch (Exception e) when (e is KilnUnavailableException or InvalidOperationException or TypeInitializationException)
        {
            ModLog.Warn($"kiln.stations: the crafting menu does not count crafting sources (linked sources, station outputs) on this client: {e.Message}");
        }
    }

    /// <summary>Every message of the channel: this class's own, then those of the stocks that share it (DyeStock, LockStock,
    /// BuildStock). Each id is taken once on a channel (NetChannel.Register throws on a second): a message added here takes
    /// the next free id after all of them.</summary>
    internal static NetChannel Register(NetChannel channel)
    {
        channel.Register<CraftStockRows>(CraftStockRows.Id, static (m, w) => CraftStockRows.Encode(m, w),
                static (ref NetReader r) => CraftStockRows.Decode(ref r), static (m, _) => { if (!Client.IsHosting) ClientRows(m); },
                NetDirection.ToClient)
            .Register<CraftStockHello>(CraftStockHello.Id, static (m, w) => CraftStockHello.Encode(m, w),
                static (ref NetReader r) => CraftStockHello.Decode(ref r), static (m, ctx) => Enqueue(ctx.Player, m.Open),
                NetDirection.ToServer)
            // 0.2.0-dev.29.19: the station screen's fill buttons. An older peer has no such message ids and drops them.
            .Register<CraftStockFillAsk>(CraftStockFillAsk.Id, static (m, w) => CraftStockFillAsk.Encode(m, w),
                static (ref NetReader r) => CraftStockFillAsk.Decode(ref r), static (m, ctx) => EnqueueAsk(ctx.Player, m.Keys),
                NetDirection.ToServer)
            .Register<CraftStockFillAnswers>(CraftStockFillAnswers.Id, static (m, w) => CraftStockFillAnswers.Encode(m, w),
                static (ref NetReader r) => CraftStockFillAnswers.Decode(ref r), static (m, _) => { if (!Client.IsHosting) ClientFill(m); },
                NetDirection.ToClient)
            // 0.2.0-dev.31.16 (GitHub #132): the station screen's per-ingredient rows. An older peer drops the id.
            .Register<CraftStockStationRows>(CraftStockStationRows.Id, static (m, w) => CraftStockStationRows.Encode(m, w),
                static (ref NetReader r) => CraftStockStationRows.Decode(ref r), static (m, _) => { if (!Client.IsHosting) ClientStationRows(m); },
                NetDirection.ToClient);
        DyeStock.Attach(channel);
        LockStock.Attach(channel);  // GitHub #147, 0.2.0-dev.31.12: locks from linked sources
        BuildStock.Attach(channel);  // GitHub #132, 0.2.0-dev.31.12: block placements from linked sources
        RespecStock.Attach(channel);  // GitHub #150: the skill respec from linked sources
        return channel;
    }

    // ================================================================ server ====

    /// <summary>A hello from a client's menu (any thread): the server's tick takes it.</summary>
    internal static void Enqueue(EntityId player, bool open)
    {
        if (player.IsNone || Inbox.Count > 1024) return;
        Inbox.Enqueue((player, open));
        EnsureTick();
    }

    /// <summary>A client's asked fills (any thread): the server's tick takes them.</summary>
    internal static void EnqueueAsk(EntityId player, FillKey[] keys)
    {
        if (player.IsNone || keys.Length == 0 || AskInbox.Count > 1024) return;
        AskInbox.Enqueue((player, keys));
        EnsureTick();
    }

    /// <summary>Asked fills (tick thread): added to the player's open session (oldest dropped past 16), answered at once.
    /// Without an open session (no hello) they are dropped.</summary>
    internal static void Ask(EntityId player, IReadOnlyList<FillKey> keys)
    {
        lock (Gate)
        {
            if (!Sessions.TryGetValue(player.Value, out var s)) return;
            foreach (var k in keys)
            {
                if (k.Station == 0 || k.Recipe == 0) continue;
                s.FillDue = true;  // asked again: the client has no answer for it, so the answers go out again
                if (s.Asked.Contains(k)) continue;
                if (s.Asked.Count >= MaxFillKeys) s.Asked.RemoveAt(0);
                s.Asked.Add(k);
            }
        }
    }

    /// <summary>Tests: the fills a player's session holds.</summary>
    internal static IReadOnlyList<FillKey> AskedOf(EntityId player)
    {
        lock (Gate) return Sessions.TryGetValue(player.Value, out var s) ? [.. s.Asked] : [];
    }

    private static void EnsureTick()
    {
        lock (Gate) _tick ??= ServiceTick.Add("kiln.stations craft stock", static (in TickInfo t) => Pump(t.Step));
    }

    private static void WatchPlayers()
    {
        lock (Gate)
        {
            if (_playersWatched) return;
            _playersWatched = true;
        }
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;  // Kiln's own subscription
        try
        {
            Events.On<PlayerLeftEvent>(static e => Gone(e.Player.Entity));
            Events.On<PlayerJoinedEvent>(static e => Gone(e.Player.Entity));
        }
        catch (Exception e) when (e is KilnUnavailableException or InvalidOperationException or TypeInitializationException or FileNotFoundException)
        {
            // kiln.players is not here: a session still ends 15 s after its last hello
            ModLog.Warn($"kiln.stations: crafting stock does not see players leave ({e.Message}); a session ends 15 s after its last hello");
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    /// <summary>A player left, or someone joined into the slot (tick thread): that entity's session goes.</summary>
    internal static void Gone(EntityId player)
    {
        lock (Gate) Sessions.Remove(player.Value);
    }

    /// <summary>A hello (tick thread). Open: a new session is sent a snapshot at once; a keep-alive only extends it.
    /// Closed: the session goes (the client clears its own counts).</summary>
    internal static void Hello(EntityId player, bool open)
    {
        if (player.IsNone) return;
        lock (Gate)
        {
            if (!open)
            {
                Sessions.Remove(player.Value);
                return;
            }
            if (!Sessions.TryGetValue(player.Value, out var s)) Sessions[player.Value] = s = new Session();
            s.Seen = Clock();
        }
        WatchPlayers();
    }

    /// <summary>The server's tick (every step, once a client said hello): the hellos, then the sessions due.</summary>
    internal static void Pump(ulong step)
    {
        var n = 0;
        while (n++ < 256 && Inbox.TryDequeue(out var h)) Hello(h.Player, h.Open);
        while (n++ < 512 && AskInbox.TryDequeue(out var a)) Ask(a.Player, a.Keys);
        List<(EntityId Player, Session S)>? due = null;
        var now = Clock();
        lock (Gate)
        {
            if (step < _lastStep) Sessions.Clear();  // a new world: its entity ids name other things
            _lastStep = step;
            if (Sessions.Count == 0) return;
            foreach (var p in Sessions.Where(kv => now - kv.Value.Seen > (long)SessionLife.TotalMilliseconds).Select(kv => kv.Key).ToList())
                Sessions.Remove(p);
            foreach (var (p, s) in Sessions)
                if (s.Due || s.FillDue || step % RefreshSteps == 0) (due ??= []).Add((new EntityId(p), s));
        }
        if (due is null) return;
        StockCache.Clear();  // each base's chests read once per pass
        foreach (var (p, s) in due) Refresh(p, s);
    }

    private static void Refresh(EntityId player, Session s)
    {
        CraftStockRows rows;
        try
        {
            rows = Build(player) ?? Off;
        }
        catch (Exception e) when (e is KilnUnavailableException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            rows = Off;
        }
        var key = Key(rows);
        bool send;
        lock (Gate)
        {
            if (!Sessions.TryGetValue(player.Value, out var live) || !ReferenceEquals(live, s)) return;  // it ended meanwhile
            send = s.Due || s.Sent != key;
        }
        if (send)
        {
            var ok = Deliver(player, rows);
            lock (Gate)
            {
                s.Due = false;
                s.Sent = ok ? key : null;  // not sent: the next pass tries again
            }
        }
        RefreshFill(player, s, rows.Rows.Length > 0);
        RefreshStation(player, s);
    }

    // The station screen's per-ingredient rows (GitHub #132, 0.2.0-dev.31.16): again when they changed, and at the session's
    // first pass; an empty list once when they go. A send that did not go out is tried again at the next pass.
    private static void RefreshStation(EntityId player, Session s)
    {
        CraftStockRows rows;
        try
        {
            rows = StationView(player);
        }
        catch (Exception e) when (e is KilnUnavailableException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            rows = Off;
        }
        var key = Key(rows);
        lock (Gate)
        {
            if (!Sessions.TryGetValue(player.Value, out var live) || !ReferenceEquals(live, s)) return;
            if (s.StationSent == key) return;
        }
        var ok = DeliverStation(player, new CraftStockStationRows(rows.Rows));
        lock (Gate) s.StationSent = ok ? key : null;
    }

    /// <summary>What a player's production station screen may count (tick thread): per item the player's fill-opted links
    /// hold, the bag, the Magic chests of the player's base and those links. Empty unless the row's press can take from
    /// the links (<see cref="StationsCore.SourceItemFill"/>'s rules: stations.fill, an item-fill kiln-items.dll, a
    /// fill-opted link, CanAccessInventories, a base).</summary>
    internal static CraftStockRows StationView(EntityId player)
    {
        if (StationBuildHook is { } hook) return hook(player);
        if (!Capabilities.Is(Stations.FillCapability) || !Stations.Available || !Players.Available || !StationsCore.FillLinksOf(player) ||
            !StationsCore.Backend.HasItemFills) return Off;
        if (Players.Find(player) is not { InGame: true } who || StationsCore.CanAccessInventories(who) is not true) return Off;
        if (Bases.Of(player) is not { } home) return Off;
        var (rc, links) = CraftingCore.FillStock(player);  // the Magic chests of the player's base left out: counted below
        if (rc != StationsNative.Ok || links.Length == 0) return Off;
        var linked = new Dictionary<uint, long>();
        foreach (var row in links) linked[row.Item] = linked.GetValueOrDefault(row.Item) + row.Count;
        var bag = new Dictionary<uint, long>();
        if (Items.Read(player) is { } inv)
            foreach (var slot in inv.Slots)
                if (slot.Item.Value != 0 && slot.Count > 0) bag[slot.Item.Value] = bag.GetValueOrDefault(slot.Item.Value) + slot.Count;
        if (!StockCache.TryGetValue(home.Id, out var stock)) StockCache[home.Id] = stock = Bases.Stock(home.Id);
        var chests = new Dictionary<uint, long>();
        foreach (var (item, count) in stock) chests[item.Value] = count;
        return StationCompose(linked, bag, chests);
    }

    /// <summary>The station rows on plain values: per item the links hold (count above 0), the bag, the chests and the
    /// links together, sorted; none at all when that is more than <see cref="MaxRows"/> rows (never a cut list).</summary>
    internal static CraftStockRows StationCompose(IReadOnlyDictionary<uint, long> linked, IReadOnlyDictionary<uint, long> bag,
        IReadOnlyDictionary<uint, long> chests)
    {
        var rows = new List<CraftStockRow>();
        foreach (var (item, n) in linked.OrderBy(kv => kv.Key))
        {
            if (item == 0 || n <= 0) continue;
            var total = n + bag.GetValueOrDefault(item) + chests.GetValueOrDefault(item);
            rows.Add(new CraftStockRow(item, (uint)Math.Min(total, uint.MaxValue)));
        }
        return rows.Count > MaxRows ? Off : new CraftStockRows([.. rows]);
    }

    private static bool DeliverStation(EntityId player, CraftStockStationRows m)
    {
        if (StationSendHook is { } hook) return hook(player, m);
        if (Client.IsHosting && Client.HasLocalPlayer && Client.LocalPlayer == player) return ApplyStation(new CraftStockRows(m.Rows));
        if (_channel is null || Network.PeerOf(player) is not { } peer || !peer.HasChannel(ChannelName)) return false;
        return _channel.SendTo(player, m);
    }

    // The answers for the session's asked fills: again when one is asked, and when an answer changed (the refresh
    // steps). While the snapshot is empty there is nothing to answer (the client's query stays the game's).
    private static void RefreshFill(EntityId player, Session s, bool stockOn)
    {
        FillKey[] asked;
        bool due;
        lock (Gate)
        {
            if (s.Asked.Count == 0) return;
            asked = [.. s.Asked];
            due = s.FillDue;
        }
        FillAnswer[] answers;
        try
        {
            answers = stockOn ? BuildFill(player, asked) : [];
        }
        catch (Exception e) when (e is KilnUnavailableException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            answers = [];
        }
        var key = FillKeyText(answers);
        lock (Gate)
        {
            if (!Sessions.TryGetValue(player.Value, out var live) || !ReferenceEquals(live, s)) return;
            if (!due && s.FillSent == key) return;
        }
        var ok = DeliverFill(player, new CraftStockFillAnswers(answers));
        lock (Gate)
        {
            s.FillDue = false;
            s.FillSent = ok ? key : null;
        }
    }

    /// <summary>The answers for a player's asked fills (tick thread): per key, how many more crafts the button's fill with
    /// station outputs covers now (0: none, or the button's rules refuse it).</summary>
    internal static FillAnswer[] BuildFill(EntityId player, IReadOnlyList<FillKey> keys)
    {
        if (FillBuildHook is { } hook) return hook(player, keys);
        // GitHub #132: a player's fill-opted links count as well as opted-in station outputs
        if (!Capabilities.Is(Stations.FillCapability) ||
            (!Capabilities.Is(Stations.OutputSourcesCapability) && !StationsCore.FillLinksOf(player))) return [.. keys.Select(k => new FillAnswer(k, 0))];
        if (!Stations.Available || !Players.Available) return [.. keys.Select(k => new FillAnswer(k, 0))];
        bool? mayAccess = Players.Find(player) is { InGame: true } who ? StationsCore.CanAccessInventories(who) : null;
        var answers = new FillAnswer[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            uint crafts;
            try
            {
                crafts = StationsCore.FillCrafts(player, new EntityId(keys[i].Station), keys[i].Recipe, keys[i].FillChoices(), mayAccess);
            }
            catch (Exception e) when (e is ArgumentException or KeyNotFoundException)
            {
                crafts = 0;  // a key that names no station or a choice out of range
            }
            answers[i] = new FillAnswer(keys[i], crafts);
        }
        return answers;
    }

    internal static string FillKeyText(FillAnswer[] answers)
    {
        var b = new StringBuilder();
        foreach (var a in answers)
        {
            b.Append(a.Key.Station).Append(':').Append(a.Key.Recipe);
            foreach (var c in a.Key.Choices) b.Append(',').Append(c.Category).Append('/').Append(c.Index);
            b.Append('=').Append(a.Crafts).Append(';');
        }
        return b.ToString();
    }

    private static bool DeliverFill(EntityId player, CraftStockFillAnswers m)
    {
        if (FillSendHook is { } hook) return hook(player, m);
        if (Client.IsHosting && Client.HasLocalPlayer && Client.LocalPlayer == player) return ApplyFill(m);
        if (_channel is null || Network.PeerOf(player) is not { } peer || !peer.HasChannel(ChannelName)) return false;
        return _channel.SendTo(player, m);
    }

    /// <summary>What a player is sent now: null when nothing of theirs can be raised (no right to the base's
    /// inventories, no base, nothing opted in that holds outputs, or more items than one snapshot carries).</summary>
    internal static CraftStockRows? Build(EntityId player)
    {
        if (BuildHook is { } hook) return hook(player);
        var outputsOn = Capabilities.Is(Stations.CraftSourcesCapability) && Capabilities.Is(Stations.OutputSourcesCapability) && Stations.Available;
        var linkedOn = Capabilities.Is(Crafting.SourcesCapability) && CraftingCore.HasSources;
        if ((!outputsOn && !linkedOn) || !Players.Available || !StationsCore.Backend.HasCraftStock) return null;
        if (Players.Find(player) is not { InGame: true } who) return null;
        var mayAccess = StationsCore.CanAccessInventories(who);
        var home = mayAccess is true && Stations.Available ? Bases.Of(player) : null;  // a visitor's base is not even read
        var lent = new Dictionary<uint, long>();
        if (outputsOn && home is not null)
            foreach (var station in Stations.OutputSources(home.Id))
                if (Stations.Read(station) is { } info)
                    foreach (var slot in info.Outputs)
                        if (slot.Item.Value != 0 && slot.Count > 0) lent[slot.Item.Value] = lent.GetValueOrDefault(slot.Item.Value) + slot.Count;
        var registered = false;
        if (linkedOn)
        {
            var extra = CraftingCore.ExtraStock(player);
            if (extra.Rc != StationsNative.Ok) return null; // never publish a cut or unvalidated linked snapshot
            registered = extra.Rows.Length != 0;
            foreach (var row in extra.Rows) lent[row.Item] = lent.GetValueOrDefault(row.Item) + row.Count;
        }
        var rows = From(mayAccess, home is not null, lent, StationsCore.Backend.CraftItems, () =>
        {
            var bag = new Dictionary<uint, long>();
            if (Items.Read(player) is { } inv)
                foreach (var slot in inv.Slots)
                    if (slot.Item.Value != 0 && slot.Count > 0) bag[slot.Item.Value] = bag.GetValueOrDefault(slot.Item.Value) + slot.Count;
            return bag;
        }, () =>
        {
            if (home is null) return new Dictionary<uint, long>();
            if (!StockCache.TryGetValue(home.Id, out var stock)) StockCache[home.Id] = stock = Bases.Stock(home.Id);
            var chests = new Dictionary<uint, long>();
            foreach (var (item, count) in stock) chests[item.Value] = count;
            return chests;
        }, out var tooMany, registered);
        if (tooMany && Interlocked.Exchange(ref _warned, 1) == 0)
            ModLog.Warn($"kiln.stations: player {player}'s crafting stock names more than {MaxRows} items: its crafting menus show the game's counts");
        return rows;
    }

    /// <summary>The decision behind <see cref="Build"/> on plain values: nothing unless the player may access the base's
    /// inventories (the game's own CanAccessInventories, the test the payment relies on), stands in a base and some
    /// opted-in station there holds outputs; then the items craft_items names, counted.</summary>
    internal static CraftStockRows? From(bool? mayAccess, bool inBase, IReadOnlyDictionary<uint, long> lent,
        Func<uint[], (int Rc, uint[] Items)> craftItems, Func<IReadOnlyDictionary<uint, long>> bag,
        Func<IReadOnlyDictionary<uint, long>> chests, out bool tooMany, bool registered = false)
    {
        tooMany = false;
        if (lent.Count == 0 || (!registered && (mayAccess is not true || !inBase))) return null;
        var (rc, items) = craftItems([.. lent.Keys.Order()]);
        if (rc != StationsNative.Ok) return null;
        var rows = Compose(items, lent, bag(), chests());
        tooMany = rows is null;
        return rows;
    }

    /// <summary>One snapshot: per item of <paramref name="items"/> the bag, the chests and the lent outputs together,
    /// sorted, without empty rows. Null when that is more than <see cref="MaxRows"/> rows (never a cut snapshot: a
    /// missing item would count as none).</summary>
    internal static CraftStockRows? Compose(IReadOnlyCollection<uint> items, IReadOnlyDictionary<uint, long> lent,
        IReadOnlyDictionary<uint, long> bag, IReadOnlyDictionary<uint, long> chests)
    {
        var rows = new List<CraftStockRow>();
        foreach (var item in items.Distinct().Order())
        {
            if (item == 0) continue;
            var n = lent.GetValueOrDefault(item) + bag.GetValueOrDefault(item) + chests.GetValueOrDefault(item);
            if (n > 0) rows.Add(new CraftStockRow(item, (uint)Math.Min(n, uint.MaxValue)));
        }
        return rows.Count > MaxRows ? null : new CraftStockRows([.. rows]);
    }

    internal static string Key(CraftStockRows rows)
    {
        var b = new StringBuilder();
        foreach (var r in rows.Rows) b.Append(r.Item).Append('=').Append(r.Count).Append(';');
        return b.ToString();
    }

    /// <summary>What a screen counts of a snapshot (GitHub #132). A crafting menu counts all of it: its craft's payment
    /// takes from the opted-in outputs and the linked sources too. A production station's screen counts only what its
    /// rows' transfer buttons move, the game's own item transfer from the bag and the Magic chests, which is the game's
    /// own count: there every item is named with none, so no row is raised and the snapshot stays on for the "+1" / "+10"
    /// answers (the fill takes the outputs; those buttons do not read the rows).</summary>
    internal static CraftStockRows View(CraftStockRows rows, bool factory) => View(rows, factory, Off);

    /// <summary>The same with the server's station rows (GitHub #132, 0.2.0-dev.31.16): a production station's screen
    /// counts, per item they name, what its transfer buttons now move through Kiln's item fill (the bag, the Magic chests
    /// and the player's fill-opted links); every other item of the snapshot is named with none (the game's count).</summary>
    internal static CraftStockRows View(CraftStockRows rows, bool factory, CraftStockRows station)
    {
        if (!factory) return rows;
        var counts = station.Rows.Where(r => r.Item != 0).GroupBy(r => r.Item).ToDictionary(g => g.Key, g => g.Last().Count);
        var named = rows.Rows.Select(r => r with { Count = counts.GetValueOrDefault(r.Item) }).ToList();
        var seen = rows.Rows.Select(r => r.Item).ToHashSet();
        named.AddRange(station.Rows.Where(r => r.Item != 0 && r.Count > 0 && seen.Add(r.Item)));
        return new CraftStockRows([.. named.Take(MaxRows)]);
    }

    /// <summary>A production station's screen (keen::HudScreenType FactoryCrafting).</summary>
    internal static bool IsFactory(GameScreen s) => s.Hud == HudScreen.FactoryCrafting || s.Crafting is { IsFactory: true };

    // To the player's Kiln client: in this process for the hosting player, else over the channel to a peer that has it.
    private static bool Deliver(EntityId player, CraftStockRows rows)
    {
        if (SendHook is { } hook) return hook(player, rows);
        if (Client.IsHosting && Client.HasLocalPlayer && Client.LocalPlayer == player) return Apply(rows);
        if (_channel is null || Network.PeerOf(player) is not { } peer || !peer.HasChannel(ChannelName)) return false;
        return _channel.SendTo(player, rows);
    }

    /// <summary>Tests: everything back to the start.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Sessions.Clear();
            _tick?.Dispose();
            _tick = null;
            _lastStep = 0;
            _warned = 0;
            _open = _ticking = false;
        }
        lock (ViewGate)
        {
            _rows = Off;
            _station = Off;
            _factory = false;
        }
        while (Inbox.TryDequeue(out _)) { }
        while (AskInbox.TryDequeue(out _)) { }
        _lastAsked = "";
        _lastAskPoll = _lastAskSent = 0;
        StockCache.Clear();
        Clock = static () => Environment.TickCount64;
        BuildHook = null;
        SendHook = null;
        FillBuildHook = null;
        FillSendHook = null;
        StationBuildHook = null;
        StationSendHook = null;
        AskHook = null;
        ServerSideHook = null;
        HelloHook = null;
        ApplyHook = null;
    }

    /// <summary>Tests: whether a session is open for the player, and the key last sent to it.</summary>
    internal static (bool Open, string? Sent) SessionOf(EntityId player)
    {
        lock (Gate) return Sessions.TryGetValue(player.Value, out var s) ? (true, s.Sent) : (false, null);
    }

    // ================================================================ client ====

    private static volatile bool _open, _ticking;
    private static long _lastHello;
    // The last snapshot as the server sent it, and whether the open screen is a production station's: what native counts
    // is View(_rows, _factory), applied again when either changes (any thread: the UI thread, the client's main thread,
    // the hosting server's tick).
    private static readonly object ViewGate = new();
    private static CraftStockRows _rows = Off;
    private static CraftStockRows _station = Off;  // the server's station rows (message 10), GitHub #132
    private static bool _factory;

    /// <summary>Test hooks: whether the server takes hellos (hosting, or Kiln there); a hello sent; a snapshot applied.</summary>
    internal static Func<bool>? ServerSideHook;
    internal static Action<bool>? HelloHook;
    internal static Func<CraftStockRows, bool>? ApplyHook;

    private static void StartClient()
    {
        if (!StationsCore.Backend.HasCraftStock) return;
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;
        try
        {
            Ui.Screens.Changed += ClientChanged;  // screen changes only: no work in other frames
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    private static bool ServerSide() => ServerSideHook?.Invoke() ?? (Client.IsHosting || Network.ServerHasKiln);

    /// <summary>A screen change (the game's UI thread): a crafting menu opened (the server is told, the keep-alive starts)
    /// or closed (the server is told, the counts go back to the game's).</summary>
    internal static void ClientChanged(GameScreen before, GameScreen now)
    {
        Factory(IsFactory(now));
        var open = now.Kind == ScreenKind.Crafting && ServerSide() &&
                   (Capabilities.Is(Crafting.SourceUiCapability) || Capabilities.Is(Stations.CraftUiCapability) || Capabilities.Is(FillUiCapability) || Capabilities.Is(StockUiCapability));
        if (open == _open) return;
        _open = open;
        SwitchTick(open);
        _lastHello = Environment.TickCount64;
        SendHello(open);
        _lastAsked = "";
        if (!open)
        {
            lock (ViewGate) _station = Off;  // the server sends them again for the next menu
            Apply(Off);
        }
    }

    private static void SwitchTick(bool on)
    {
        if (HelloHook is not null) return;  // tests drive the keep-alive themselves
        if (on == _ticking) return;
        _ticking = on;
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;
        try
        {
            if (on) Ui.Screens.UiTick += ClientTick;
            else Ui.Screens.UiTick -= ClientTick;
        }
        catch (Exception e) when (e is KilnUnavailableException or InvalidOperationException)
        {
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    // Every UI frame while a crafting menu is open: the keep-alive every 5 s.
    internal static void ClientTick(GameScreen now)
    {
        if (!_open || now.Kind != ScreenKind.Crafting) return;  // Changed ends the watch
        PollAsks(Environment.TickCount64);
        var tick = Environment.TickCount64;
        if (tick - _lastHello < (long)KeepAlive.TotalMilliseconds) return;
        _lastHello = tick;
        SendHello(true);
    }

    private static string _lastAsked = "";
    private static long _lastAskPoll, _lastAskSent;
    internal static readonly TimeSpan AskPoll = TimeSpan.FromMilliseconds(250);
    /// <summary>Test hook: an ask sent (instead of the channel or the hosting server's inbox).</summary>
    internal static Action<FillKey[]>? AskHook;

    /// <summary>Every 250 ms while a crafting menu is open (the game's UI thread): the fills the station screen asked the
    /// game about that it refused for missing materials, with no answer yet (native), go to the server once each set.</summary>
    internal static void PollAsks(long nowMs)
    {
        if (!_open || nowMs - _lastAskPoll < (long)AskPoll.TotalMilliseconds) return;
        _lastAskPoll = nowMs;
        if (!StationsCore.Backend.HasFillUi) return;
        var (rc, raw) = StationsCore.Backend.FillAsks();
        if (rc != StationsNative.Ok && rc != StationsNative.BufferTooSmall) return;
        var keys = raw.Select(k => FillKey.Of(k)).ToArray();
        var text = string.Join(';', keys.Select(k => k.ToString()));
        if (keys.Length == 0 || (text == _lastAsked && nowMs - _lastAskSent < (long)KeepAlive.TotalMilliseconds)) return;
        _lastAsked = text;
        _lastAskSent = nowMs;  // the same set again only after 5 s: an ask or its answer may have been lost
        SendAsk(keys);
    }

    private static void SendAsk(FillKey[] keys)
    {
        if (AskHook is { } hook)
        {
            hook(keys);
            return;
        }
        if (Client.IsHosting && Client.HasLocalPlayer)
        {
            EnqueueAsk(Client.LocalPlayer, keys);  // the server half in this process takes it on its tick
            return;
        }
        _channel?.SendToServer(new CraftStockFillAsk(keys));
    }

    // The server's answers (the client's main thread). Ones that arrive after the menu closed are not kept.
    internal static void ClientFill(CraftStockFillAnswers m)
    {
        if (!_open && m.Rows.Length > 0) return;
        ApplyFill(m);
    }

    /// <summary>Hands the answers to kiln.stations (the station screen's fill query reads them); empty clears them.</summary>
    internal static bool ApplyFill(CraftStockFillAnswers m)
    {
        if (!StationsCore.Backend.HasFillUi) return false;
        var rows = (m.Rows.Length > 0 && !_open ? [] : m.Rows).Take(MaxFillKeys)
            .Select(a => new StationsNative.FillAnswer { Key = a.Key.Native(), Crafts = a.Crafts }).ToArray();
        return StationsCore.Backend.SetFillAnswers(rows) == StationsNative.Ok;
    }

    private static void SendHello(bool open)
    {
        if (HelloHook is { } hook)
        {
            hook(open);
            return;
        }
        if (Client.IsHosting && Client.HasLocalPlayer)
        {
            Enqueue(Client.LocalPlayer, open);  // the server half in this process takes it on its tick
            return;
        }
        _channel?.SendToServer(new CraftStockHello(open));
    }

    // The server's snapshot (the client's main thread). One that arrives after the menu closed is not kept.
    internal static void ClientRows(CraftStockRows m)
    {
        if (!_open && m.Rows.Length > 0) return;
        Apply(m);
    }

    /// <summary>Hands a snapshot to kiln.stations as the open screen counts it (<see cref="View(CraftStockRows, bool)"/>; native logs its
    /// arrival); an empty one clears it.</summary>
    internal static bool Apply(CraftStockRows rows)
    {
        if (rows.Rows.Length > 0 && !_open) rows = Off;  // the hosting player's menu closed meanwhile
        lock (ViewGate)
        {
            _rows = rows;
            return Push(View(rows, _factory, _station));
        }
    }

    // The server's station rows (the client's main thread). Ones that arrive after the menu closed are not kept.
    internal static void ClientStationRows(CraftStockStationRows m) => ClientStationRows(new CraftStockRows(m.Rows));

    internal static void ClientStationRows(CraftStockRows m)
    {
        if (!_open) return;
        ApplyStation(m);
    }

    /// <summary>Keeps the server's station rows and applies the snapshot again when a production station's screen is open.</summary>
    internal static bool ApplyStation(CraftStockRows station)
    {
        if (!_open) station = Off;
        lock (ViewGate)
        {
            _station = station;
            return !_factory || Push(View(_rows, true, station));
        }
    }

    // The screen changed between a production station's and a crafting menu (the game's UI thread): the snapshot again,
    // as the new screen counts it.
    private static void Factory(bool factory)
    {
        lock (ViewGate)
        {
            if (_factory == factory) return;
            _factory = factory;
            if (_rows.Rows.Length > 0 || _station.Rows.Length > 0) Push(View(_rows, factory, _station));
        }
    }

    private static bool Push(CraftStockRows view)
    {
        if (ApplyHook is { } hook) return hook(view);
        if (!StationsCore.Backend.HasCraftStock) return false;
        var stacks = new StationsNative.Stack[view.Rows.Length];
        for (var i = 0; i < stacks.Length; i++)
            stacks[i] = new StationsNative.Stack { Item = view.Rows[i].Item, Count = view.Rows[i].Count };
        return StationsCore.Backend.SetCraftStock(stacks) == StationsNative.Ok;
    }
}
