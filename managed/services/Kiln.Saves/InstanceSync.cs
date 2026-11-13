// SPDX-License-Identifier: MIT
// Instance data on players' games (dev.27). A key says who may see its values (InstanceKeyOptions.Sync: nobody, the
// player who holds the item, players near its holder). The server sends those values over a channel of Kiln's own,
// "kiln:instance" v1 (not required: a game without it gets nothing and is unaffected, as is a game without Kiln), and
// keeps each player's copy current: about four times a second it works out what every player may see now, compares it
// with what it sent that player, and sends the difference. So a changed value, an edit of a live object, a move, a claim,
// a loss and a player walking into or out of range are all one rule, and a value never reaches a player its key does
// not name. A client keeps what it is sent in a store a mod reads (InstanceData.Synced, InstanceDataSyncedEvent).
//
//   server -> client  Update   v1: [reset] [seq, prev] [gone: item entity ids] [items: entity, item type, holder,
//                              flat slot, values: mod, key, JSON or "removed"]
//   client -> server  LabView  lab peers only (tools/nettest): which holder entity this peer stands for, and where
//   client -> server  Ack      v1: the last update the game took in order, or "lost": it missed one after that
//
// Delivery: kiln.net can accept an update and drop it later (its retry queue gives up after two seconds), so an update
// counts as delivered only when the game acknowledges it. Every update names the one before it (prev); a game takes an
// update only right after the one it names, and otherwise reports the loss. A reported loss, or an update without an
// acknowledgement after ten seconds, starts that game over: a reset with the whole set, as at the first update. At most
// eight updates wait for their acknowledgement; the next difference waits for room.
//
// Limits: a value is at most 64 KB as JSON (the store's cap); one update is at most 96 KB encoded (everything in it:
// values, removals, gone items, headers) and 512 items, and what does not fit follows in the next updates (nothing is
// dropped). A game holds copies for at most 4,096 items: the server sends no more than that, the player's own items
// first, and sends a waiting item once there is room. A value larger than one update to the server's network.json
// maxMessageBytes is never sent (logged once per item and key). The client drops an update that breaks these limits.
// The wire format was changed before v1 shipped (dev.27 review: seq, prev and Ack). Docs: docs/kiln/services/saves.md
// "Instance data on players' games".
using System.Text;

namespace Kiln;

/// <summary>One item in an update: where it is, and the values that are new, changed or removed (null JSON).</summary>
internal sealed class SyncItemUpdate
{
    public uint Entity, Item, Holder;
    public int Slot;
    public readonly List<(string Mod, string Key, string? Json)> Values = [];
}

/// <summary>Server to client: the difference between what the player may see now and what it was sent.</summary>
internal sealed class SyncUpdate
{
    internal const byte Version = 1;
    internal const int MaxGone = 65536, MaxItems = 4096, MaxValuesPerItem = 256, MaxModBytes = 128, MaxKeyBytes = 800;

    /// <summary>The most bytes each part takes encoded (a count as a variable-length number takes at most 5), so the
    /// server's budget covers the whole message.</summary>
    internal const int HeaderBytes = 1 + 1 + 4 + 4 + 5 + 5, GoneBytes = 4, ItemHeaderBytes = 4 + 4 + 4 + 5 + 5;

    /// <summary>The most bytes one value (or one removal: <paramref name="json"/> null) takes encoded.</summary>
    internal static int ValueBytes(string mod, string key, string? json) =>
        5 + Encoding.UTF8.GetByteCount(mod) + 5 + Encoding.UTF8.GetByteCount(key) + 1 + (json is null ? 0 : 5 + Encoding.UTF8.GetByteCount(json));

    /// <summary>Everything the client holds is void: the full set follows (the first update to a player, and every start
    /// over after a lost update).</summary>
    public bool Reset;

    /// <summary>This update's number, unique on the server (never 0).</summary>
    public uint Seq;

    /// <summary>The number of the update the server sent this game before this one; 0 on a reset. A game takes the update
    /// only when it took that one last.</summary>
    public uint Prev;

    public readonly List<uint> Gone = [];
    public readonly List<SyncItemUpdate> Items = [];

    public bool IsEmpty => !Reset && Gone.Count == 0 && Items.Count == 0;

    public void Encode(NetWriter w)
    {
        w.Byte(Version).Bool(Reset).UInt32(Seq).UInt32(Prev).VarUInt((ulong)Gone.Count);
        foreach (var e in Gone) w.UInt32(e);
        w.VarUInt((ulong)Items.Count);
        foreach (var i in Items)
        {
            w.UInt32(i.Entity).UInt32(i.Item).UInt32(i.Holder).VarInt(i.Slot).VarUInt((ulong)i.Values.Count);
            foreach (var (mod, key, json) in i.Values)
            {
                w.String(mod).String(key).Bool(json is not null);
                if (json is not null) w.String(json);
            }
        }
    }

    /// <exception cref="NetFormatException">Another version, or a count or a text over the limits.</exception>
    public static SyncUpdate Decode(ref NetReader r)
    {
        var version = r.Byte();
        if (version != Version) throw new NetFormatException($"kiln:instance update version {version}, this game reads {Version}");
        var u = new SyncUpdate { Reset = r.Bool(), Seq = r.UInt32(), Prev = r.UInt32() };
        if (u.Seq == 0) throw new NetFormatException("kiln:instance update without a number");
        var gone = r.VarUInt();
        if (gone > MaxGone) throw new NetFormatException($"kiln:instance update names {gone} gone items (limit {MaxGone})");
        for (ulong k = 0; k < gone; k++) u.Gone.Add(r.UInt32());
        var items = r.VarUInt();
        if (items > MaxItems) throw new NetFormatException($"kiln:instance update has {items} items (limit {MaxItems})");
        for (ulong k = 0; k < items; k++)
        {
            var i = new SyncItemUpdate { Entity = r.UInt32(), Item = r.UInt32(), Holder = r.UInt32() };
            var slot = r.VarInt();
            if (slot is < -1 or > int.MaxValue) throw new NetFormatException($"kiln:instance update has flat slot {slot}");
            i.Slot = (int)slot;
            var values = r.VarUInt();
            if (values > MaxValuesPerItem) throw new NetFormatException($"kiln:instance update has {values} values on one item (limit {MaxValuesPerItem})");
            for (ulong v = 0; v < values; v++)
            {
                var mod = r.String(MaxModBytes);
                var key = r.String(MaxKeyBytes);
                i.Values.Add((mod, key, r.Bool() ? r.String(InstanceStore.MaxValueBytes) : null));
            }
            u.Items.Add(i);
        }
        return u;
    }
}

/// <summary>Lab peers (tools/nettest) have no player: one says which holder entity it stands for (a chest of the lab's
/// world, as if that were its inventory) and where it stands, for the keys that go to players nearby.</summary>
internal sealed record SyncLabView(uint As, bool HasAt, WorldPosition At)
{
    public void Encode(NetWriter w) => w.UInt32(As).Bool(HasAt).Position(At);

    public static SyncLabView Decode(ref NetReader r) => new(r.UInt32(), r.Bool(), r.Position3());
}

/// <summary>Client to server: the game took update <see cref="Seq"/> (and so every one before it), or, with
/// <see cref="Lost"/>, it took <see cref="Seq"/> last and then got one that does not follow it: an update was lost
/// between, and the game waits for the server to start it over.</summary>
internal sealed record SyncAck(uint Seq, bool Lost)
{
    public void Encode(NetWriter w) => w.UInt32(Seq).Bool(Lost);

    public static SyncAck Decode(ref NetReader r) => new(r.UInt32(), r.Bool());
}

/// <summary>A player's game the server may send to: who it is to the store (the entity whose inventory is "its own")
/// and where it stands (null: not known, so nothing reaches it as "nearby").</summary>
internal readonly record struct SyncViewer(ulong Id, EntityId Player, WorldPosition? Position, string Name);

/// <summary>What the server side of the sync needs from the network and the players, behind one interface, so the passes
/// are tested without a game (managed/tests/Kiln.Saves.Tests/InstanceSyncTests.cs).</summary>
internal interface IInstanceSyncLink
{
    /// <summary>The games that take updates now: connected, with the channel, and with a player in game.</summary>
    IReadOnlyList<SyncViewer> Viewers();

    /// <summary>Where a holder entity (a player, a chest, a station, an NPC) stands now, read from the game; null when it
    /// cannot be read (then nobody is near it).</summary>
    WorldPosition? PositionOf(EntityId holder);

    /// <summary>The largest update, in bytes, the channel carries.</summary>
    int MaxUpdateBytes { get; }

    /// <summary>False when it did not go out: the same difference is sent again at the next pass. True means only that the
    /// network took it: whether it arrived, the game's acknowledgement says.</summary>
    bool Send(in SyncViewer viewer, SyncUpdate update);
}

/// <summary>The server side: what each player was sent, and the pass that sends the difference. Tick thread (the passes,
/// and the games' acknowledgements: kiln.net's server-side handlers run there).</summary>
internal sealed class InstanceSyncServer(InstanceStore store, IInstanceSyncLink link, Func<long>? clock = null)
{
    /// <summary>The most bytes one update takes encoded, everything in it; one value of the store's largest size always
    /// fits (64 KB).</summary>
    internal const int UpdateBudgetBytes = 96 * 1024;

    internal const int MaxItemsPerUpdate = 512;

    /// <summary>Items a game holds copies for at most: the client's own cap. The server never sends more.</summary>
    internal const int MaxHeldItems = InstanceSyncClient.MaxItems;

    /// <summary>Updates that may wait for their acknowledgement; while that many wait, a game gets nothing new.</summary>
    internal const int MaxInFlight = 8;

    /// <summary>An update without an acknowledgement this long counts as lost: the game starts over.</summary>
    internal const long AckTimeoutMs = 10_000;

    /// <summary>A game's report of a lost update starts it over at most this often (a client may be hostile).</summary>
    internal const long LostEveryMs = 1_000;

    private sealed class SentItem
    {
        public ulong Serial;
        public uint Item, Holder;
        public int Slot;
        public readonly Dictionary<(string Mod, string Key), string> Values = new();
    }

    private sealed class ViewerState(EntityId player)
    {
        public readonly EntityId Player = player;
        public bool Fresh = true;

        /// <summary>The last update that went out to this game; the reset that started it over last (0: none yet).</summary>
        public uint Last, ResetSeq;

        /// <summary>The game said it lost an update after one of this state's: it starts over at the next pass.</summary>
        public bool LostReported;

        public long StartedOverAt = long.MinValue / 2;

        /// <summary>Updates that went out and wait for the game's acknowledgement, oldest first.</summary>
        public readonly List<(uint Seq, long At)> InFlight = [];

        public readonly Dictionary<uint, SentItem> Items = new();
    }

    private readonly Func<long> _now = clock ?? (static () => Environment.TickCount64);
    private readonly Dictionary<ulong, ViewerState> _viewers = new();
    private readonly HashSet<string> _noted = new(StringComparer.Ordinal);
    private uint _seq;

    internal long Updates, Values, Bytes, Unsent, Acked, StartOvers;

    internal int ViewerCount => _viewers.Count;

    /// <summary>What each viewer holds, for the admin listing and the tests: items and values sent.</summary>
    internal IEnumerable<(ulong Viewer, EntityId Player, int Items, int Values)> Sent() =>
        _viewers.Select(v => (v.Key, v.Value.Player, v.Value.Items.Count, v.Value.Items.Values.Sum(i => i.Values.Count)));

    /// <summary>Forgets what was sent (the channel closed, the world went): the next pass starts every viewer over.</summary>
    internal void Forget() => _viewers.Clear();

    /// <summary>A game's acknowledgement (<see cref="SyncAck"/>), from connection <paramref name="viewer"/>. One that
    /// names no update waiting for it is ignored: a late one from before a start over, or a hostile one.</summary>
    internal void Ack(ulong viewer, SyncAck ack)
    {
        if (!_viewers.TryGetValue(viewer, out var state)) return;
        if (ack.Lost)
        {
            // only a loss after an update of this state counts: the game took one at or after the last reset (a report from
            // before that reset, or from a game that has not taken it, waits for the reset or for the timeout)
            if (!state.Fresh && state.ResetSeq != 0 && !Before(ack.Seq, state.ResetSeq) && !Before(state.Last, ack.Seq)) state.LostReported = true;
            return;
        }
        var i = state.InFlight.FindIndex(f => f.Seq == ack.Seq);
        if (i < 0) return;
        state.InFlight.RemoveRange(0, i + 1);  // it took that one, so every one before it
        Acked++;
    }

    /// <summary>a comes before b among the server's update numbers (which wrap after 2^32).</summary>
    private static bool Before(uint a, uint b) => (int)(a - b) < 0;

    private void StartOver(ViewerState state, long now)
    {
        state.Fresh = true;
        state.Items.Clear();
        state.InFlight.Clear();
        state.LostReported = false;
        state.StartedOverAt = now;
        StartOvers++;
    }

    /// <summary>One pass: for every viewer, what it may see now against what it was sent. Returns the updates that went out.</summary>
    internal int Pass()
    {
        var viewers = link.Viewers();
        foreach (var id in _viewers.Keys.ToArray())
        {
            var state = _viewers[id];
            // gone, or the connection stands for another player now: what was sent means nothing any more
            if (!viewers.Any(v => v.Id == id && v.Player == state.Player)) _viewers.Remove(id);
        }
        if (viewers.Count == 0) return 0;

        // every value that may travel, by item
        var byItem = new Dictionary<LiveItem, List<InstanceStore.SyncedValue>>(ReferenceEqualityComparer.Instance);
        if (store.World is not null)
            foreach (var v in store.SyncedValues())
            {
                if (!byItem.TryGetValue(v.Item, out var list)) byItem[v.Item] = list = [];
                list.Add(v);
            }
        var budget = Math.Min(UpdateBudgetBytes, link.MaxUpdateBytes);
        var positions = new Dictionary<uint, WorldPosition?>();
        var now = _now();
        var sent = 0;
        foreach (var viewer in viewers)
        {
            if (!_viewers.TryGetValue(viewer.Id, out var state)) _viewers[viewer.Id] = state = new ViewerState(viewer.Player);
            if (state.LostReported && now - state.StartedOverAt >= LostEveryMs) StartOver(state, now);
            else if (state.InFlight.Count > 0 && now - state.InFlight[0].At >= AckTimeoutMs) StartOver(state, now);
            if (state.LostReported) continue;                    // the game takes nothing but the reset: it comes once allowed
            if (state.InFlight.Count >= MaxInFlight) continue;  // it waits for the game to take what is under way
            var want = new List<(uint Entity, LiveItem Item, List<InstanceStore.SyncedValue> Values, bool Own)>();
            foreach (var (item, values) in byItem)
            {
                var holder = item.Place.Holder;
                if (holder.IsNone) continue;  // placed nowhere: nobody's
                List<InstanceStore.SyncedValue>? seen = null;
                foreach (var v in values)
                    if (holder == viewer.Player || (v.Key.Sync == InstanceSync.Nearby && Near(viewer, holder, v.Key.NearbyRadius, positions)))
                        (seen ??= []).Add(v);
                if (seen is not null) want.Add((item.Ref.Entity.Id.Value, item, seen, holder == viewer.Player));
            }
            // the player's own items first, so others' never crowd them out of the game's cap; then by entity
            want.Sort(static (a, b) => a.Own != b.Own ? (a.Own ? -1 : 1) : a.Entity.CompareTo(b.Entity));
            var (update, commit) = Diff(state, want, budget);
            if (update.IsEmpty) continue;
            if (++_seq == 0) _seq = 1;
            update.Seq = _seq;
            update.Prev = update.Reset ? 0 : state.Last;
            if (!link.Send(viewer, update))
            {
                Unsent++;
                continue;
            }
            commit();
            state.Last = update.Seq;
            if (update.Reset) state.ResetSeq = update.Seq;
            state.InFlight.Add((update.Seq, now));
            sent++;
            Updates++;
        }
        return sent;
    }

    /// <summary>Whether the viewer stands within <paramref name="radius"/> of the holder entity as it stands now: read from
    /// the game once per pass for each holder. A holder whose position cannot be read is near nobody.</summary>
    private bool Near(in SyncViewer viewer, EntityId holder, double radius, Dictionary<uint, WorldPosition?> positions)
    {
        if (viewer.Position is not { } at) return false;
        if (!positions.TryGetValue(holder.Value, out var where)) positions[holder.Value] = where = link.PositionOf(holder);
        return where is { } p && p.DistanceTo(at) <= radius;
    }

    /// <summary>The update that turns what the viewer was sent into what it may see now (as far as one update holds), and
    /// what to remember once it went out. <paramref name="budget"/> covers the whole encoded update.</summary>
    private (SyncUpdate Update, Action Commit) Diff(ViewerState state, List<(uint Entity, LiveItem Item, List<InstanceStore.SyncedValue> Values, bool Own)> want, int budget)
    {
        var update = new SyncUpdate { Reset = state.Fresh };
        var commits = new List<Action> { () => state.Fresh = false };
        var used = SyncUpdate.HeaderBytes;
        var serialOf = new Dictionary<uint, ulong>(want.Count);
        foreach (var w in want) serialOf[w.Entity] = w.Item.Ref.Entity.Serial;

        // items the game holds that it may not see now, or whose entity id is another item now; what does not fit waits
        var waiting = new HashSet<uint>();
        foreach (var (entity, had) in state.Items.OrderBy(i => i.Key))
        {
            if (serialOf.TryGetValue(entity, out var serial) && serial == had.Serial) continue;
            if (used + SyncUpdate.GoneBytes > budget || update.Gone.Count >= SyncUpdate.MaxGone)
            {
                waiting.Add(entity);
                continue;
            }
            used += SyncUpdate.GoneBytes;
            update.Gone.Add(entity);
            commits.Add(() => state.Items.Remove(entity));
        }
        // how many the game holds once the gone ones went: a new item waits while that is its cap
        var held = state.Items.Count - update.Gone.Count;
        long values = 0;
        foreach (var (entity, item, list, _) in want)
        {
            if (update.Items.Count >= MaxItemsPerUpdate) break;
            if (waiting.Contains(entity)) continue;  // the item the id stood for leaves the game first
            var had = state.Items.TryGetValue(entity, out var h) && h.Serial == item.Ref.Entity.Serial ? h : null;
            if (had is null && held >= MaxHeldItems) continue;  // no room in the game: it follows once there is
            var op = new SyncItemUpdate { Entity = entity, Item = item.Ref.Item.Value, Holder = item.Place.Holder.Value, Slot = item.Place.FlatSlot };
            var cost = SyncUpdate.ItemHeaderBytes;
            var full = used + cost > budget;
            var sets = 0;
            // removals first: a value the player may not see any more leaves its game before anything new arrives
            if (had is not null && !full)
                foreach (var at in had.Values.Keys.OrderBy(k => k.Mod, StringComparer.Ordinal).ThenBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (list.Any(v => v.Key.ModId == at.Mod && v.Key.Name == at.Key && !TooLarge(item, v, budget))) continue;
                    var c = SyncUpdate.ValueBytes(at.Mod, at.Key, null);
                    if (used + cost + c > budget || op.Values.Count >= SyncUpdate.MaxValuesPerItem)
                    {
                        full = true;
                        break;
                    }
                    cost += c;
                    op.Values.Add((at.Mod, at.Key, null));
                }
            if (!full)
                foreach (var v in list)
                {
                    var at = (v.Key.ModId, v.Key.Name);
                    if (had is not null && had.Values.TryGetValue(at, out var old) && old == v.Json) continue;
                    if (TooLarge(item, v, budget)) continue;  // it stays on the server (an old copy went as a removal above)
                    var c = SyncUpdate.ValueBytes(at.ModId, at.Name, v.Json);
                    if (used + cost + c > budget || op.Values.Count >= SyncUpdate.MaxValuesPerItem)
                    {
                        full = true;  // the rest follows in the next update
                        break;
                    }
                    cost += c;
                    sets++;
                    op.Values.Add((at.ModId, at.Name, v.Json));
                }
            var moved = had is not null && (had.Holder != op.Holder || had.Slot != op.Slot);
            // a new item goes out only with a value: one without is nothing a client could show
            if ((op.Values.Count > 0 || moved) && used + cost <= budget)
            {
                used += cost;
                values += sets;
                update.Items.Add(op);
                if (had is null) held++;
                var serial = item.Ref.Entity.Serial;
                commits.Add(() =>
                {
                    if (!state.Items.TryGetValue(op.Entity, out var s) || s.Serial != serial) state.Items[op.Entity] = s = new SentItem { Serial = serial };
                    s.Item = op.Item;
                    s.Holder = op.Holder;
                    s.Slot = op.Slot;
                    foreach (var (mod, key, json) in op.Values)
                    {
                        if (json is null) s.Values.Remove((mod, key));
                        else s.Values[(mod, key)] = json;
                    }
                    if (s.Values.Count == 0) state.Items.Remove(op.Entity);
                });
            }
            if (full) break;
        }
        var bytes = used;
        commits.Add(() =>
        {
            Values += values;
            Bytes += bytes;
        });
        return (update, () =>
        {
            foreach (var c in commits) c();
        });
    }

    /// <summary>A value that does not fit one update even alone (network.json maxMessageBytes was lowered below it): it
    /// stays on the server, logged once per item and key.</summary>
    private bool TooLarge(LiveItem item, InstanceStore.SyncedValue v, int budget)
    {
        var alone = SyncUpdate.HeaderBytes + SyncUpdate.ItemHeaderBytes + SyncUpdate.ValueBytes(v.Key.ModId, v.Key.Name, v.Json);
        if (alone <= budget) return false;
        if (_noted.Add($"{item.Ref.Entity.Id.Value}:{v.Key.ModId}:{v.Key.Name}"))
            SavesRuntime.Log(LogLevel.Warn, $"Kiln.Saves: instance data {v.Key.ModId} key '{v.Key.Name}' of {item.Ref} is {alone} bytes as an update, more than one " +
                                            $"kiln:instance update carries here ({budget}): it is not sent to players");
        return true;
    }
}

/// <summary>The client side: the copies this game holds. Changed where the channel's messages arrive (the main thread),
/// read on any thread: every item is one object that is replaced, never changed.</summary>
internal sealed class InstanceSyncClient
{
    /// <summary>Items this game keeps copies for; an update that would go over it leaves the new items out (logged once).</summary>
    internal const int MaxItems = 4096;

    private readonly object _gate = new();
    private readonly Dictionary<uint, SyncedItem> _items = new();
    private bool _fullNoted, _chain, _lostSaid;
    private uint _last;

    internal long UpdatesIn, ValuesIn, Skipped;

    internal IReadOnlyList<SyncedItem> All()
    {
        lock (_gate) return [.. _items.Values.OrderBy(i => i.Entity.Value)];
    }

    internal SyncedItem? Find(EntityId item)
    {
        lock (_gate) return _items.GetValueOrDefault(item.Value);
    }

    internal int Count
    {
        get
        {
            lock (_gate) return _items.Count;
        }
    }

    /// <summary>Takes an update; returns the events it makes, in order (the caller posts them on the tick thread).</summary>
    internal List<InstanceDataSyncedEvent> Apply(SyncUpdate u) => Apply(u, out _);

    /// <summary>Takes an update when it follows the one this game took last (or is a reset); <paramref name="ack"/> is what
    /// to tell the server: that it was taken, or, once per gap, that one was lost before it (then it is not taken, and the
    /// copies stay as they are until the server's reset). Null: nothing to say.</summary>
    internal List<InstanceDataSyncedEvent> Apply(SyncUpdate u, out SyncAck? ack)
    {
        var events = new List<InstanceDataSyncedEvent>();
        lock (_gate)
        {
            if (!u.Reset && (!_chain || u.Prev != _last))
            {
                Skipped++;
                ack = _lostSaid ? null : new SyncAck(_last, Lost: true);
                _lostSaid = true;
                return events;
            }
            UpdatesIn++;
            _chain = true;
            _lostSaid = false;
            _last = u.Seq;
            ack = new SyncAck(u.Seq, Lost: false);
            if (u.Reset) ClearLocked(events);
            foreach (var e in u.Gone)
                if (_items.Remove(e, out var gone))
                    foreach (var (mod, key) in gone.Values.Keys)
                        events.Add(new InstanceDataSyncedEvent(gone, mod, key, InstanceSyncChange.Gone));
            foreach (var op in u.Items)
            {
                _items.TryGetValue(op.Entity, out var had);
                if (had is null && _items.Count >= MaxItems)
                {
                    if (!_fullNoted)
                    {
                        _fullNoted = true;
                        SavesRuntime.Log(LogLevel.Warn, $"Kiln.Saves: this game holds copies for {MaxItems} items already; the server's values for more are left out");
                    }
                    continue;
                }
                var values = had is null ? new Dictionary<(string, string), string>() : new Dictionary<(string, string), string>(had.Values);
                var changes = new List<(string Mod, string Key, InstanceSyncChange Change)>();
                foreach (var (mod, key, json) in op.Values)
                {
                    if (!CharacterSync.ValidModId(mod) || key.Length is 0 or > 200) continue;
                    if (json is null)
                    {
                        if (values.Remove((mod, key))) changes.Add((mod, key, InstanceSyncChange.Removed));
                    }
                    else
                    {
                        values[(mod, key)] = json;
                        changes.Add((mod, key, InstanceSyncChange.Set));
                        ValuesIn++;
                    }
                }
                if (values.Count == 0)
                {
                    // nothing left of it here: it goes
                    if (had is not null) _items.Remove(op.Entity);
                    var last = new SyncedItem(new EntityId(op.Entity), new ItemId(op.Item), new EntityId(op.Holder), op.Slot, had?.Values ?? values);
                    foreach (var (mod, key, change) in changes) events.Add(new InstanceDataSyncedEvent(last, mod, key, change));
                    continue;
                }
                var now = new SyncedItem(new EntityId(op.Entity), new ItemId(op.Item), new EntityId(op.Holder), op.Slot, values);
                _items[op.Entity] = now;
                foreach (var (mod, key, change) in changes) events.Add(new InstanceDataSyncedEvent(now, mod, key, change));
                if (had is not null && (had.Holder != now.Holder || had.FlatSlot != now.FlatSlot))
                    foreach (var (mod, key) in values.Keys)
                        if (!changes.Any(c => c.Mod == mod && c.Key == key))
                            events.Add(new InstanceDataSyncedEvent(now, mod, key, InstanceSyncChange.Moved));
            }
        }
        return events;
    }

    /// <summary>The connection ended: nothing this game holds is the server's any more.</summary>
    internal List<InstanceDataSyncedEvent> Clear()
    {
        var events = new List<InstanceDataSyncedEvent>();
        lock (_gate)
        {
            ClearLocked(events);
            _chain = _lostSaid = false;  // the next server starts with a reset
            _last = 0;
        }
        return events;
    }

    private void ClearLocked(List<InstanceDataSyncedEvent> events)
    {
        foreach (var gone in _items.Values.OrderBy(i => i.Entity.Value))
            foreach (var (mod, key) in gone.Values.Keys)
                events.Add(new InstanceDataSyncedEvent(gone, mod, key, InstanceSyncChange.Gone));
        _items.Clear();
        _fullNoted = false;
    }
}

/// <summary>The channel, the pass timer and the two sides in a running game. Started by the first key that travels
/// (<see cref="InstanceData.Key{T}"/>) or the first read of the client's copies (<see cref="InstanceData.Synced()"/>).</summary>
internal static class InstanceSyncRuntime
{
    internal const string ChannelName = "kiln:instance";
    internal const string ChannelVersion = "1";
    internal const int PassEveryMs = 250;
    private const ushort MsgUpdate = 0, MsgLabView = 1, MsgAck = 2;

    private static readonly object Gate = new();
    private static bool _started, _capShown, _warned;
    private static NetChannel? _channel;
    private static InstanceSyncServer? _server;
    private static long _lastPass;
    private static readonly Dictionary<uint, SyncLabView> LabViews = new();
    private static readonly System.Collections.Concurrent.ConcurrentQueue<InstanceDataSyncedEvent> Waiting = new();

    internal static readonly InstanceSyncClient ClientStore = new();

    internal static InstanceSyncServer? Server => _server;

    internal static bool ChannelOpen => _channel is not null;

    internal static bool Started => _started;

    /// <summary>The row saves.instance_data.sync: the channel can open here (kiln.net is in this process). Reported once, at
    /// Kiln.Saves' start; it does not depend on a session.</summary>
    internal static void ReportCapability()
    {
        lock (Gate)
        {
            if (_capShown) return;
            _capShown = true;
        }
        try
        {
            if (Network.Available) Capabilities.Report(InstanceData.SyncCapability, CapabilityState.Available, CapabilityReason.None, "");
            else
            {
                var net = Capabilities.Get("kiln.net");
                var known = net is { State: CapabilityState.Unavailable, Reason: not CapabilityReason.Unknown };
                Capabilities.Report(InstanceData.SyncCapability, CapabilityState.Unavailable, known ? net.Reason : CapabilityReason.ServiceMissing,
                    $"needs kiln.net: {(known && net.ReasonText.Length > 0 ? net.ReasonText : "not available in this process")}");
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            SavesRuntime.Log(LogLevel.Warn, $"Kiln.Saves: capabilities: {InstanceData.SyncCapability} was not reported: {e.Message}");
        }
    }

    /// <summary>The client's copies, for a read. Opens the channel at the first call.</summary>
    /// <exception cref="KilnUnavailableException">saves.instance_data.sync is off.</exception>
    internal static InstanceSyncClient RequireClient()
    {
        if (!Network.Available)
            throw new KilnUnavailableException(InstanceData.SyncCapability, "needs kiln.net, which is not available in this process");
        EnsureStarted();
        return ClientStore;
    }

    internal static void EnsureStarted()
    {
        if (_started) return;
        lock (Gate)
        {
            if (_started) return;
            _started = true;
            var outer = ModContext.CurrentMod;
            ModContext.CurrentMod = null;  // the channel and the tick are Kiln's, not the calling mod's
            try
            {
                if (!Network.Available) return;
                _channel = Network.Channel(ChannelName, ChannelVersion, required: false, maxMessageBytes: 256 * 1024)
                    .Register<SyncUpdate>(MsgUpdate, static (m, w) => m.Encode(w), static (ref NetReader r) => SyncUpdate.Decode(ref r), OnUpdate, NetDirection.ToClient)
                    .Register<SyncLabView>(MsgLabView, static (m, w) => m.Encode(w), static (ref NetReader r) => SyncLabView.Decode(ref r), OnLabView, NetDirection.ToServer)
                    .Register<SyncAck>(MsgAck, static (m, w) => m.Encode(w), static (ref NetReader r) => SyncAck.Decode(ref r), OnAck, NetDirection.ToServer);
                Network.Disconnected += OnDisconnected;
                _server = new InstanceSyncServer(SavesRuntime.Store.Instances, new GameLink(_channel));
                ServiceTick.Add("kiln instance sync", Tick);
                SavesRuntime.Log(LogLevel.Info, $"Kiln.Saves: instance data sync: channel {ChannelName} v{ChannelVersion} open (not required)");
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
    }

    // ---------------------------------------------------------------------------------------------- server side ----

    private static void Tick(in TickInfo tick)
    {
        Deliver();  // what a host's own game received on its main thread since the last step
        if (_server is not { } server || !Lifecycle.IsServerRunning) return;
        var now = Environment.TickCount64;
        if (now - _lastPass < PassEveryMs) return;
        _lastPass = now;
        try
        {
            server.Pass();
        }
        catch (KilnUnavailableException)
        {
            // the item source cannot answer now: the next pass tries again
        }
    }

    private static void OnLabView(SyncLabView m, NetContext ctx)
    {
        if (!ctx.Sender.IsLab) return;
        lock (LabViews)
        {
            if (m.As == 0) LabViews.Remove(ctx.Sender.Id);
            else LabViews[ctx.Sender.Id] = m;
        }
        SavesRuntime.Log(LogLevel.Info, $"Kiln.Saves: instance data sync: lab peer {ctx.Sender.Name} stands for entity {m.As}" +
                                        (m.HasAt ? $" at ({m.At.X:F1}, {m.At.Y:F1}, {m.At.Z:F1})" : ""));
    }

    /// <summary>A game took an update, or lost one (server side, tick thread, as the passes).</summary>
    private static void OnAck(SyncAck m, NetContext ctx) => _server?.Ack(ctx.Sender.Id, m);

    /// <summary>The real game behind the server's passes: Kiln.Network's peers, Kiln.Players' positions.</summary>
    private sealed class GameLink(NetChannel channel) : IInstanceSyncLink
    {
        public int MaxUpdateBytes => channel.MaxMessageBytes;

        public IReadOnlyList<SyncViewer> Viewers()
        {
            var list = new List<SyncViewer>();
            foreach (var p in Network.Peers)
            {
                if (p.State != NetPeerState.Ready || !p.HasChannel(ChannelName)) continue;
                if (p.IsLab)
                {
                    SyncLabView? view;
                    lock (LabViews) LabViews.TryGetValue(p.Id, out view);
                    if (view is not null) list.Add(new SyncViewer(p.Id, new EntityId(view.As), view.HasAt ? view.At : null, p.Name));
                    continue;
                }
                if (p.Player.IsNone) continue;
                list.Add(new SyncViewer(p.Id, p.Player, PositionOf(p.Player), p.Name));
            }
            lock (LabViews)
                foreach (var id in LabViews.Keys.Where(id => list.All(v => v.Id != id)).ToArray())
                    LabViews.Remove(id);  // the lab peer left
            return list;
        }

        public WorldPosition? PositionOf(EntityId holder)
        {
            try
            {
                // a player through kiln.players; a chest, a station, an NPC through kiln.spawn's read of its transform now
                if (holder.IsPlayer) return Players.Available ? Players.PositionOf(holder) : null;
                return SpawnLink.PositionOf(holder);
            }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
            {
                return null;  // no position to be had now: nobody is "nearby"
            }
        }

        public bool Send(in SyncViewer viewer, SyncUpdate update)
        {
            var id = (uint)viewer.Id;
            var peer = Network.Peers.FirstOrDefault(p => p.Id == id);
            if (peer is null) return false;
            try
            {
                return channel.SendTo(peer, update);
            }
            catch (ArgumentException e)
            {
                if (!_warned)
                {
                    _warned = true;
                    SavesRuntime.Log(LogLevel.Warn, $"Kiln.Saves: instance data sync: an update for {viewer.Name} could not be sent: {e.Message}");
                }
                return false;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------- client side ----

    private static void OnUpdate(SyncUpdate m, NetContext ctx)
    {
        var events = ClientStore.Apply(m, out var ack);
        if (ack is not null)
        {
            try
            {
                // not sent (the link is busy): the server hears of it with the next one, or starts this game over
                _channel?.SendToServer(ack);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                SavesRuntime.Log(LogLevel.Debug, $"Kiln.Saves: instance data sync: an acknowledgement was not sent: {e.Message}");
            }
        }
        Post(events);
    }

    private static void OnDisconnected(NetPeer server) => Post(ClientStore.Clear());

    /// <summary>The events of a change, on the tick thread and through the store's own queue (InstanceStore.Post), so they
    /// keep the order of the store's events: at once in a joined game (the main thread is the tick thread there), at the
    /// next step for the host of a world (the server thread is, and the message came on the main thread).</summary>
    private static void Post(List<InstanceDataSyncedEvent> events)
    {
        if (events.Count == 0 || !Events.HasSubscribers<InstanceDataSyncedEvent>()) return;
        foreach (var e in events) Waiting.Enqueue(e);
        if (Game.IsTickThread) Deliver();
    }

    private static void Deliver()
    {
        while (Waiting.TryDequeue(out var e))
        {
            try
            {
                SavesRuntime.Store.Instances.PostSynced(e);
            }
            catch (InvalidOperationException ex)
            {
                SavesRuntime.Log(LogLevel.Warn, $"Kiln.Saves: instance data sync: {e} was not delivered: {ex.Message}");
            }
        }
    }

    /// <summary>The test kit's session end: nothing sent or received counts for the next session.</summary>
    internal static void EndSession()
    {
        _server?.Forget();
        ClientStore.Clear();
        Waiting.Clear();
        lock (LabViews) LabViews.Clear();
    }

    /// <summary>One line for the admin listing.</summary>
    internal static string Describe()
    {
        if (!_started) return "sync: not started (no key with Sync declared here)";
        if (_channel is null) return "sync: off (kiln.net is not available)";
        var s = _server;
        return $"sync: channel {ChannelName} v{ChannelVersion}, {s?.ViewerCount ?? 0} player game(s) served, {s?.Updates ?? 0} update(s), " +
               $"{s?.Values ?? 0} value(s), {s?.Bytes ?? 0} bytes sent, {s?.Acked ?? 0} acknowledged, {s?.StartOvers ?? 0} start-over(s) after a loss; " +
               $"{ClientStore.Count} item(s) held as a client";
    }
}
