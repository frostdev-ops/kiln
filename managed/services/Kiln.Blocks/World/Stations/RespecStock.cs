// SPDX-License-Identifier: MIT
// GitHub #150, since 0.2.0-dev.31.18: the skill tree's respec (a node's unlearn, the Flame Altar's skill reset) counts
// and pays its respec item (the game's Runes) from the player's links registered with CraftSourceOptions.Respec, the
// managed half. The server pays natively (kiln-items, the respec payer's InventoryPay call: the bag and the Magic chests
// of the player's base first, then the links, all or nothing); the player's own game decides whether to send the respec at
// all (the skill tree offers the unlearn, the altar enables its reset, only when its own count reaches the cost), so a Kiln
// client needs the links' count:
//  - the server sends a player with a respec-opted link, on the channel kiln:craftstock (required: false), whether it
//    supports the respec from links and the row of its respec item (what those links hold, 0 when none; the bag and the
//    Magic chests not included: the client counts those itself), again when the count changes (it looks every 30 steps);
//  - the client's unlearn and reset checks (native kiln-items) count the game's count plus that row;
//  - not authority: the server's payment rechecks every link inside the game's transaction.
// A vanilla client is sent nothing: the server still pays its respec from the links, but its own screens only offer the
// unlearn or the reset when its bag and the Magic chests cover it. docs/kiln/services/stations.md, "The skill respec from
// linked sources".
using System.Text;

namespace Kiln;

/// <summary>Server to client: whether the server counts and pays the skill respec from the player's respec links, and the
/// row of its respec item (what those links hold). Unsupported: the game's own respec counts.</summary>
internal sealed record RespecStockRows(bool Supported, CraftStockRow[] Rows)
{
    internal const ushort Id = 11;  // 1 to 10 are taken on kiln:craftstock (10: CraftStockStationRows, 31.16)

    internal static void Encode(RespecStockRows m, NetWriter w)
    {
        var n = Math.Min(m.Rows.Length, RespecStock.MaxRows);
        w.Bool(m.Supported).UInt16((ushort)n);
        for (var i = 0; i < n; i++) w.UInt32(m.Rows[i].Item).UInt32(m.Rows[i].Count);
    }

    internal static RespecStockRows Decode(ref NetReader r)
    {
        var supported = r.Bool();
        var n = r.UInt16();
        if (n > RespecStock.MaxRows) throw new InvalidDataException("too many respec rows");
        var rows = new CraftStockRow[n];
        var seen = new HashSet<uint>();
        for (var i = 0; i < n; i++)
        {
            var item = r.UInt32();
            var count = r.UInt32();
            if (item == 0 || !seen.Add(item)) throw new InvalidDataException("invalid respec row");
            rows[i] = new(item, count);
        }
        return new(supported, supported ? rows : []);
    }
}

internal static class RespecStock
{
    internal const int MaxRows = StationsNative.MaxRespecStock;
    internal const ulong RefreshSteps = 30;  // ~1.2 s, as the other snapshots
    internal static readonly RespecStockRows Off = new(false, []);

    private sealed class Session
    {
        public int Links;     // live respec-opted handles of this player
        public string? Sent;  // the key of the last message that went out; null: none or failed
        public bool Due = true;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<uint, Session> Sessions = [];
    private static NetChannel? _channel;
    private static IDisposable? _tick;
    private static bool _attached, _playersWatched;
    private static ulong _lastStep;

    /// <summary>Test hooks: the links' row (instead of native source_stock), a delivery, the client's native call.</summary>
    internal static Func<EntityId, (int Rc, StationsNative.Stack[] Rows)>? RowsHook;
    internal static Func<EntityId, RespecStockRows, bool>? SendHook;
    internal static Func<bool>? SupportedHook;
    internal static Func<bool, StationsNative.Stack[], int>? ApplyHook;
    internal static Func<bool>? ServerSideHook;

    internal static void Attach(NetChannel channel)
    {
        if (_attached) return;
        _attached = true;
        _channel = channel;
        // An older peer has no such message id and drops it.
        channel.Register<RespecStockRows>(RespecStockRows.Id, RespecStockRows.Encode,
            static (ref NetReader r) => RespecStockRows.Decode(ref r), static (m, _) => { if (!Client.IsHosting) ClientApply(m); },
            NetDirection.ToClient);
    }

    // ================================================================ server ====

    /// <summary>A respec-opted link was registered for the player (tick thread); returns the session it counts in, so a
    /// handle of an earlier login never ends a later login's session.</summary>
    internal static object? Track(EntityId player)
    {
        if (player.IsNone) return null;
        Session s;
        lock (Gate)
        {
            if (!Sessions.TryGetValue(player.Value, out var found)) Sessions[player.Value] = found = new Session();
            s = found;
            s.Links++;
            s.Due = true;
            _tick ??= ServiceTick.Add("crafting respec sources", static (in TickInfo t) => Pump(t.Step));
        }
        WatchPlayers();
        return s;
    }

    /// <summary>A respec-opted link was disposed (any thread). After the last one the next tick sends "unsupported".</summary>
    internal static void Untrack(EntityId player, object? session)
    {
        lock (Gate)
        {
            if (!Sessions.TryGetValue(player.Value, out var s) || !ReferenceEquals(s, session) || s.Links == 0) return;
            if (--s.Links == 0) s.Due = true;
        }
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
            Events.On<PlayerJoinedEvent>(static e => Joined(e.Player.Entity));
        }
        catch (Exception e) when (e is KilnUnavailableException or InvalidOperationException or TypeInitializationException or FileNotFoundException)
        {
            ModLog.Warn($"crafting: respec sources do not see players leave ({e.Message})");
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    /// <summary>A player left: that entity's links are another login's (the native registry no longer lets them pay).</summary>
    internal static void Gone(EntityId player)
    {
        lock (Gate) Sessions.Remove(player.Value);
    }

    /// <summary>Someone joined into the slot: a mod's join handler may already have linked for the new login; the new client
    /// was sent nothing, so the next tick sends it the current message.</summary>
    internal static void Joined(EntityId player)
    {
        lock (Gate)
        {
            if (!Sessions.TryGetValue(player.Value, out var s)) return;
            s.Sent = null;
            s.Due = true;
        }
    }

    /// <summary>The server's tick (while any player has a respec link): the sessions due, at most every 30 steps.</summary>
    internal static void Pump(ulong step)
    {
        List<(EntityId Player, Session S)>? due = null;
        lock (Gate)
        {
            var refresh = step >= _lastStep + RefreshSteps || step < _lastStep;
            if (refresh) _lastStep = step;
            foreach (var (p, s) in Sessions)
                if (s.Due || refresh) (due ??= []).Add((new EntityId(p), s));
        }
        if (due is null) return;
        foreach (var (player, s) in due)
        {
            bool closing;
            lock (Gate) closing = s.Links == 0;
            if (closing)
            {
                try { Deliver(player, Off); }
                catch (Exception e) when (e is KilnUnavailableException or InvalidOperationException) { }
                lock (Gate)
                    if (Sessions.TryGetValue(player.Value, out var live) && ReferenceEquals(live, s) && s.Links == 0) Sessions.Remove(player.Value);
                continue;
            }
            RespecStockRows m;
            try
            {
                m = Build(player);
            }
            catch (Exception e) when (e is KilnUnavailableException or InvalidOperationException or ArgumentException)
            {
                m = Off;
            }
            var key = Key(m);
            lock (Gate)
            {
                if (!Sessions.TryGetValue(player.Value, out var live) || !ReferenceEquals(live, s)) continue;
                if (!s.Due && s.Sent == key) continue;
            }
            var ok = Deliver(player, m);
            lock (Gate)
            {
                s.Due = false;
                s.Sent = ok ? key : null;
            }
        }
    }

    /// <summary>What the player is sent now: unsupported without the capability or the links' row; else the respec item's
    /// row, its count 0 when the links hold none (so the client knows the item and counts its own bag alone).</summary>
    internal static RespecStockRows Build(EntityId player)
    {
        var supported = SupportedHook?.Invoke() ?? (Capabilities.Is(Crafting.RespecSourcesCapability) && CraftingCore.HasRespec);
        if (!supported) return Off;
        var (rc, rows) = RowsHook?.Invoke(player) ?? CraftingCore.RespecStockRows(player);
        if (rc != StationsNative.Ok) return Off;
        return new RespecStockRows(true, rows.Where(r => r.Item != 0).DistinctBy(r => r.Item).OrderBy(r => r.Item).Take(MaxRows)
            .Select(r => new CraftStockRow(r.Item, r.Count)).ToArray());
    }

    internal static string Key(RespecStockRows m)
    {
        var b = new StringBuilder(m.Supported ? "on;" : "off;");
        foreach (var r in m.Rows) b.Append(r.Item).Append('=').Append(r.Count).Append(';');
        return b.ToString();
    }

    // To the player's Kiln client: in this process for the hosting player, else over the channel to a peer that has it.
    private static bool Deliver(EntityId player, RespecStockRows m)
    {
        if (SendHook is { } hook) return hook(player, m);
        if (Client.IsHosting && Client.HasLocalPlayer && Client.LocalPlayer == player) return ClientApply(m);
        if (_channel is null || Network.PeerOf(player) is not { } peer || !peer.HasChannel(CraftStock.ChannelName)) return false;
        return _channel.SendTo(player, m);
    }

    /// <summary>Tests: whether a session is open and how many links it counts.</summary>
    internal static (bool Open, int Links) SessionOf(EntityId player)
    {
        lock (Gate) return Sessions.TryGetValue(player.Value, out var s) ? (true, s.Links) : (false, 0);
    }

    // ================================================================ client ====

    private static bool ServerSide() => ServerSideHook?.Invoke() ?? (Client.IsHosting || Network.ServerHasKiln);

    /// <summary>Hands the server's message to kiln-items: on with its rows, or off (the game's own respec counts).</summary>
    internal static bool ClientApply(RespecStockRows m)
    {
        var on = m.Supported && ServerSide();
        var rows = on ? m.Rows.Take(MaxRows).Select(r => new StationsNative.Stack { Item = r.Item, Count = r.Count }).ToArray() : [];
        var rc = ApplyHook?.Invoke(on, rows) ?? CraftingCore.SetRespecStock(on, rows);
        return rc == StationsNative.Ok;
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
        }
        RowsHook = null;
        SendHook = null;
        SupportedHook = null;
        ApplyHook = null;
        ServerSideHook = null;
    }
}
