// SPDX-License-Identifier: MIT
// Instance data: values that stay with one item (InstanceData.cs is the mod's face). In a session an item is its entity id
// plus the stamp kiln.items gives that entity; every access compares both. Across a load the item is found again by its
// holder, its flat slot, its item id and the fingerprint of its save record (InstanceMatcher.cs). World holders' records ride
// the world's generation as bundles "instances/<mod>", players' as "character-instances/<character>/<mod>", staged and
// committed with every other bundle (Store.cs), so kill -9 gives the old or the new generation, never a mix.
// Format and rules: docs/kiln/services/saves.md "Instance data".
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>One value of one key on one item: its last JSON that fit, and the live object a mod holds.</summary>
internal sealed class InstanceSlot
{
    public string? Json;
    public object? Value;
    public Type? Type;

    /// <summary><see cref="Persist.Session"/>: never written.</summary>
    public bool Session;

    /// <summary>Bytes of the newest form that did not fit under the cap (0: it fits); its last form that fit is written.</summary>
    public int TooBig;
}

/// <summary>An item's (or a record's) values: mod id -> key -> slot.</summary>
internal sealed class InstanceValues
{
    public readonly SortedDictionary<string, SortedDictionary<string, InstanceSlot>> ByMod = new(StringComparer.Ordinal);

    public bool IsEmpty => ByMod.Values.All(k => k.Count == 0);

    public bool HasPersistent => ByMod.Values.Any(k => k.Values.Any(s => !s.Session));

    public void Take(InstanceValues other)
    {
        foreach (var (mod, keys) in other.ByMod)
        {
            if (!ByMod.TryGetValue(mod, out var mine)) ByMod[mod] = mine = new SortedDictionary<string, InstanceSlot>(StringComparer.Ordinal);
            foreach (var (k, s) in keys) mine[k] = s;
        }
        other.ByMod.Clear();
    }

    public IEnumerable<(string Mod, IReadOnlyList<string> Keys)> Parts =>
        ByMod.Where(kv => kv.Value.Count > 0).Select(kv => (kv.Key, (IReadOnlyList<string>)kv.Value.Keys.ToList()));
}

/// <summary>A declared key: its type and options, held for the mod that declared it until that mod unloads.</summary>
internal sealed class InstanceKeyInfo(ModContext mod, string name, Type type, Persist persist, IReadOnlyList<ItemId>? items)
{
    public readonly ModContext Mod = mod;
    public readonly string Name = name;
    public readonly Type Type = type;
    public readonly Persist Persist = persist;
    public readonly HashSet<ItemId>? Items = items is null ? null : [.. items];
    public object? Key;

    /// <summary>Who gets the values on their game (dev.27; InstanceSync.cs), and how near "nearby" is, in metres.</summary>
    public InstanceSync Sync { get; init; }

    public double NearbyRadius { get; init; } = InstanceKeyOptions.DefaultNearbyRadius;

    /// <summary>Reads the server's JSON on a client (SyncedItem.TryGet), on any thread: options of its own, as the store's
    /// per-mod ones are the tick thread's. They go with the key when its mod unloads.</summary>
    public readonly JsonSerializerOptions SyncJson = new() { IncludeFields = true };

    public string ModId => Mod.Info.Id;

    public bool SameOptions(Persist persist, IReadOnlyList<ItemId>? items) =>
        persist == Persist && (items is null ? Items is null : Items is not null && Items.SetEquals(items));

    public bool SameSync(InstanceSync sync, double radius) => sync == Sync && (sync != InstanceSync.Nearby || radius.Equals(NearbyRadius));
}

/// <summary>An item that carries data in this session.</summary>
internal sealed class LiveItem
{
    public required ItemInstanceRef Ref;
    public required string RecordId;
    public readonly InstanceValues Values = new();
    public IDisposable? Follow;
    public InstancePlace Place;
    public InstanceFields Fields;
    public HolderIdentity Holder;

    /// <summary>Its newest observation: the save that read it in <see cref="SeenEntry"/>'s holder (0: none), and its slot
    /// in that same read. A record is always written with a slot and a seen from one read, in one holder.</summary>
    public uint Seen;

    public int SeenSlot = -1;
    public HolderEntry? SeenEntry;

    public bool Fresh;  // read fresh, placed, in the save being staged
}

/// <summary>A saved record that waits for its item, or is set aside.</summary>
internal sealed class SavedRecord
{
    public required string Id;
    public int Slot;
    public InstanceFields Fields;
    public uint Seen;
    public bool Aside;
    public string Reason = "";
    public int Missed;
    public uint Since;
    public bool OnlyTrusted;
    public int MissSession = -1;

    /// <summary>A player's record: its slot (-1: not there) at every other state the client may bring back.</summary>
    public List<(uint Seen, int Slot)> Alt = [];

    public readonly InstanceValues Values = new();
}

/// <summary>A player's inventory as one state the client may bring back as its character file: the join of the current
/// visit, or a save after it. Item entity -> flat slot; null for a save that could not read the inventory (blind: a record
/// with such a state is never attached by its slot). Session memory: entity ids belong to this visit.</summary>
internal sealed record InventoryState(uint Id, EntityId Entity, Dictionary<uint, int>? SlotOf);

/// <summary>Who a holder is across sessions: a world holder's template and position, or a player's character.</summary>
internal readonly record struct HolderKey(HolderKind Kind, Guid Template, WorldPosition Position, bool Mobile, CharacterKey Character)
{
    public const double StaticRadius = 0.5, MobileRadius = 16;

    public static HolderKey Of(in HolderIdentity h) => new(h.Kind, h.Template, h.Position, h.Mobile, h.Character);

    public static HolderKey OfCharacter(CharacterKey c) => new(HolderKind.Player, Guid.Empty, default, false, c);

    public double Radius => Mobile ? MobileRadius : StaticRadius;

    /// <summary>The same holder key (a world holder: the same template at the same spot within 1 cm).</summary>
    public bool Same(in HolderKey o) => Kind == o.Kind && (Kind == HolderKind.Player
        ? Character == o.Character
        : Template == o.Template && Mobile == o.Mobile && Position.DistanceTo(o.Position) < 0.01);

    public string Text => Kind == HolderKind.Player
        ? $"character {Character}"
        : $"{Template} at ({Position.X:F1}, {Position.Y:F1}, {Position.Z:F1})";

    public override string ToString() => Text;
}

/// <summary>The saved records of one holder, and the witnesses they were saved with.</summary>
internal sealed class HolderEntry
{
    public HolderKey Key;
    public string? Name;
    public EntityId Bound;
    public ulong BoundVisit; // player slots can be reused, even by the same character

    /// <summary>The matcher ran over its records since it was bound (a player's entry is bound by a save before its
    /// join is seen; the next tick matches it).</summary>
    public bool Matched;

    public int Missed;
    public bool MissCounted;
    public bool FindUnavailable; // this session cannot count towards dropping records
    public string? Aside;
    public bool Crowded;
    public uint LastWitness;

    /// <summary>A player: joins counted, for the ids of join states (<see cref="InstanceStore.JoinIdBase"/>).</summary>
    public uint Joins;

    /// <summary>A player: the states of the current visit the client may bring back, oldest first (session memory).</summary>
    public readonly List<InventoryState> States = [];

    public readonly Dictionary<uint, Witness> Witness = new();
    public readonly List<SavedRecord> Records = [];

    public bool IsBound => !Bound.IsNone;
    public string Describe => Name is null ? Key.Text : $"{Name} {Key.Text}";
}

internal sealed partial class InstanceStore
{
    internal const string Capability = "saves.instance_data";
    internal const string WorldPrefix = "instances/", CharacterPrefix = "character-instances/";
    internal const int Format = 1;
    internal const int MaxRecordsPerHolderPerMod = 256;
    internal const int MaxValueBytes = 64 * 1024;
    internal const int MaxMissedSessions = 3;

    /// <summary>World holders are looked for this long after a load (10 minutes at 25 steps a second, as EntityData).</summary>
    internal const ulong MatchWindowSteps = 15000;

    /// <summary>A player's item that is gone: destroyed if the player is still in game this many steps later.</summary>
    internal const ulong LeaveWindowSteps = 50;

    /// <summary>Witness ids of a player's join states have this bit set (save witnesses are generation numbers).</summary>
    internal const uint JoinIdBase = 0x80000000;

    /// <summary>A player's states kept as possible character files: the join and the saves after it, the newest this many.
    /// A client brings back the file of the last save it received; the server times out a dead connection well within
    /// this many autosaves.</summary>
    internal const int PlayerStatesKept = 5;

    /// <summary>World holders not found yet are looked for all session, this many per tick (about a second), in turn: one
    /// kiln.spawn find each, and one read of the holder's items when exactly one entity fits.</summary>
    internal const int ResolvePerTick = 16;

    private readonly WorldStore _owner;
    private IInstanceWorld? _world;
    private readonly Dictionary<uint, LiveItem> _live = new();
    private readonly List<HolderEntry> _holders = [];
    private readonly Dictionary<uint, HolderEntry> _byEntity = new();
    private readonly List<Pending> _pending = [];
    private readonly Dictionary<ModContext, Dictionary<string, InstanceKeyInfo>> _keys = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _noted = new(StringComparer.Ordinal);
    private uint _loadedGeneration;
    private DataMatch _loadedMatch;
    private bool _playerGenerationTrusted = true;
    private int _session;
    private bool _matching, _windowCounted;
    private ulong _windowEnd;
    private int _resolveCursor;
    private Dictionary<uint, (HolderIdentity Identity, KilnUnavailableException? Error)>? _stagingHolders;

    private bool TrackPlayerInventories => _keys.Count != 0 || _holders.Any(h => h.Records.Count != 0);

    private sealed class Pending
    {
        public required LiveItem Item;
        public required CharacterKey Character;
        public required EntityId Player;
        public required ulong Visit;
        public required ulong Deadline;
        public required bool InventoryAlive;
    }

    public InstanceStore(WorldStore owner) => _owner = owner;

    /// <summary>The game behind the store; null until kiln.items supplies item instances (then InstanceData is off).</summary>
    internal IInstanceWorld? World
    {
        get => _world;
        set
        {
            if (ReferenceEquals(_world, value)) return;
            if (_world is not null)
            {
                _world.Moved -= OnMoved;
                _world.Gone -= OnGone;
            }
            _world = value;
            if (value is not null)
            {
                value.Moved += OnMoved;
                value.Gone += OnGone;
            }
            if (ReferenceEquals(_owner, SavesRuntime.Store)) InstanceCapabilities.Refresh();
        }
    }

    public int LiveCount => _live.Count;
    public int HolderCount => _holders.Count;
    public int WaitingCount => _holders.Sum(h => h.Records.Count(r => !r.Aside));
    public int AsideCount => _holders.Sum(h => h.Records.Count(r => r.Aside));
    public string Summary => $"instance data: {_live.Count} items, {WaitingCount} records waiting, {AsideCount} set aside, in {_holders.Count} holders";

    internal IReadOnlyList<HolderEntry> Holders => _holders;

    internal LiveItem? LiveOf(ItemInstanceRef item) =>
        _live.TryGetValue(item.Entity.Id.Value, out var l) && l.Ref.Entity.Serial == item.Entity.Serial ? l : null;

    internal IEnumerable<LiveItem> Live => _live.Values;

    /// <summary>The world closed or another one opens: nothing of this one stays (declared keys do: they are the mods').</summary>
    public void Clear()
    {
        // What still waits to be told (the bus refused it earlier) is told now, about the world it happened in. What
        // cannot be told now either is dropped: it must not arrive in the next world.
        if (_passes == 0 && !_draining) Drain();
        _afterPass.Clear();
        _cleared++;
        foreach (var l in _live.Values) Unfollow(l);
        foreach (var p in _pending) Unfollow(p.Item);
        _live.Clear();
        _holders.Clear();
        _byEntity.Clear();
        _pending.Clear();
        _noted.Clear();
        _loadedGeneration = 0;
        _loadedMatch = DataMatch.None;
        _playerGenerationTrusted = true;
        _matching = _windowCounted = false;
        _windowEnd = 0;
    }

    /// <summary>A test-kit session ends: the declared keys go too (the next session's mods declare their own).</summary>
    internal void EndSession()
    {
        Clear();
        _keys.Clear();
    }

    // ------------------------------------------------------------------------------------------------ keys ----

    internal InstanceKey<T> Declare<T>(ModContext mod, string name, InstanceKeyOptions options) where T : class, new()
    {
        if (!_keys.TryGetValue(mod, out var map))
        {
            _keys[mod] = map = new Dictionary<string, InstanceKeyInfo>(StringComparer.Ordinal);
            _owner.JsonFor(mod);  // watches the mod: its values are frozen and its keys dropped when it unloads
        }
        if (map.TryGetValue(name, out var have))
        {
            if (have.Type != typeof(T))
                throw new ArgumentException($"instance key '{name}' of {mod.Info.Id} is already declared with type {have.Type.FullName}, not {typeof(T).FullName}", nameof(name));
            if (!have.SameOptions(options.Persist, options.Items) || !have.SameSync(options.Sync, options.NearbyRadius))
                throw new ArgumentException($"instance key '{name}' of {mod.Info.Id} is already declared with other options (Persist {have.Persist}" +
                                            $"{(have.Items is null ? "" : $", {have.Items.Count} items")}, Sync {have.Sync})", nameof(name));
            return (InstanceKey<T>)have.Key!;
        }
        var info = new InstanceKeyInfo(mod, name, typeof(T), options.Persist, options.Items) { Sync = options.Sync, NearbyRadius = options.NearbyRadius };
        var key = new InstanceKey<T>(info);
        info.Key = key;
        map[name] = info;
        return key;
    }

    internal bool IsDeclared(InstanceKeyInfo info) =>
        info.Mod.Alive && _keys.TryGetValue(info.Mod, out var map) && map.TryGetValue(info.Name, out var have) && ReferenceEquals(have, info);

    /// <summary>A mod unloads: its live objects become JSON (they belong to its load context) and its keys go.</summary>
    internal void Retire(ModContext mod, JsonSerializerOptions? opts)
    {
        var id = mod.Info.Id;
        foreach (var l in _live.Values) Freeze(l.Values, id, opts, l.RecordId);
        foreach (var p in _pending) Freeze(p.Item.Values, id, opts, p.Item.RecordId);
        foreach (var h in _holders)
            foreach (var r in h.Records)
                Freeze(r.Values, id, opts, r.Id);
        _keys.Remove(mod);
    }

    private void Freeze(InstanceValues values, string mod, JsonSerializerOptions? opts, string record)
    {
        if (!values.ByMod.TryGetValue(mod, out var keys)) return;
        foreach (var (k, s) in keys)
        {
            if (s.Value is null) continue;
            Serialize(s, opts, mod, k, record);
            s.Value = null;
            s.Type = null;
        }
    }

    /// <summary>Brings a live object's JSON up to date, unless it grew past the cap: then its last form that fit stays
    /// (logged once per item and key, listed by the admin command).</summary>
    private string? Serialize(InstanceSlot s, JsonSerializerOptions? opts, string mod, string key, string record)
    {
        if (s.Value is null) return s.Json;
        string json;
        try
        {
            json = JsonSerializer.Serialize(s.Value, s.Type ?? s.Value.GetType(), opts);
        }
        catch (Exception e)
        {
            Note($"ser:{record}:{mod}:{key}", LogLevel.Error, $"Kiln.Saves: instance data {mod} key '{key}' (record {record}) could not be serialized ({e.Message}); its last form is kept");
            return s.Json;
        }
        var bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > MaxValueBytes)
        {
            s.TooBig = bytes;
            Note($"big:{record}:{mod}:{key}", LogLevel.Warn, $"Kiln.Saves: instance data {mod} key '{key}' (record {record}) is {bytes} bytes, over {MaxValueBytes}: " +
                                                          "its last form that fit is saved");
            return s.Json;
        }
        s.TooBig = 0;
        s.Json = json;
        return json;
    }

    /// <summary>The JSON a value would be written as; ArgumentException when it is over the cap.</summary>
    internal string JsonForSet<T>(InstanceKeyInfo info, T value) where T : class
    {
        var json = JsonSerializer.Serialize(value, typeof(T), _owner.JsonFor(info.Mod));
        var bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > MaxValueBytes)
            throw new ArgumentException($"the value for instance key '{info.Name}' is {bytes} bytes as JSON; at most {MaxValueBytes} are kept per value", nameof(value));
        return json;
    }

    internal T Materialize<T>(InstanceSlot s, InstanceKeyInfo info, string record, bool create) where T : class, new()
    {
        if (s.Value is T t) return t;
        var opts = _owner.JsonFor(info.Mod);
        if (s.Value is not null)
        {
            Serialize(s, opts, info.ModId, info.Name, record);
            s.Value = null;
            s.Type = null;
        }
        T? v;
        try
        {
            v = s.Json is null ? null : JsonSerializer.Deserialize<T>(s.Json, opts);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"instance data {info.ModId} key '{info.Name}' does not read as {typeof(T).Name}: {e.Message} (the stored data is kept)", e);
        }
        if (v is null)
        {
            if (!create) throw new InvalidDataException($"instance data {info.ModId} key '{info.Name}' is null");
            v = new T();
        }
        s.Value = v;
        s.Type = typeof(T);
        return v;
    }

    /// <summary>A detached copy for <see cref="InstanceData.Parked{T}"/>: null when the stored JSON does not read as T.</summary>
    internal T? Copy<T>(InstanceSlot s, InstanceKeyInfo info) where T : class
    {
        try
        {
            var json = s.Value is null ? s.Json : JsonSerializer.Serialize(s.Value, s.Type ?? s.Value.GetType(), _owner.JsonFor(info.Mod));
            return json is null ? null : JsonSerializer.Deserialize<T>(json, _owner.JsonFor(info.Mod));
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------------------------------- access ----

    /// <summary>Throws <see cref="KilnUnavailableException"/> (saves.instance_data) when there is no item source.</summary>
    internal IInstanceWorld RequireWorld()
    {
        var w = _world ?? throw new KilnUnavailableException(Capability,
            "kiln.items does not supply item instances in this process (items.instances): stamps, locations and save records of items are needed");
        try
        {
            w.Require();
        }
        catch (KilnUnavailableException e)
        {
            throw new KilnUnavailableException(Capability, $"{e.Capability} is off: {e.Reason ?? "no reason given"}", e);
        }
        return w;
    }

    /// <summary>The item's data in this session, by (entity id, stamp). Null when the ref is stale (the item is gone or the
    /// id belongs to another item now), or when it has none and <paramref name="create"/> is false.</summary>
    /// <exception cref="ArgumentException">A none ref, a ref without a stamp, an item the key does not allow.</exception>
    internal LiveItem? Lookup(ItemInstanceRef item, InstanceKeyInfo? info, bool create)
    {
        if (item.IsNone) throw new ArgumentException("no item (ItemInstanceRef.None)", nameof(item));
        if (!item.Entity.HasSerial)
            throw new ArgumentException($"{item} carries no stamp: take item refs from kiln.items' reads and results", nameof(item));
        var w = RequireWorld();
        var id = item.Entity.Id.Value;
        if (_live.TryGetValue(id, out var live) && live.Ref.Entity.Serial == item.Entity.Serial)
        {
            CheckAllowed(info, live.Fields.Item, item);
            return live;
        }
        var now = w.Read(item);
        if (now is not { } n) return null;
        if (live is not null) OnGone(live.Ref, live.Place, w.Step);  // the id carries another item now: the old one is gone
        CheckAllowed(info, n.Fields.Item, item);
        MatchHolderOf(w, n);
        if (_live.TryGetValue(id, out live) && live.Ref.Entity.Serial == item.Entity.Serial) return live;
        if (!create) return null;
        live = new LiveItem
        {
            Ref = new ItemInstanceRef(item.Entity, n.Fields.Item),
            RecordId = NewRecordId(),
            Place = n.Place,
            Fields = n.Fields,
            Holder = HolderOf(w, n.Place.Holder),
        };
        live.Follow = w.Follow(live.Ref);
        _live[id] = live;
        return live;
    }

    private static void CheckAllowed(InstanceKeyInfo? info, ItemId actual, ItemInstanceRef item)
    {
        if (info?.Items is { } allowed && !allowed.Contains(actual))
            throw new ArgumentException($"instance key '{info.Name}' of {info.ModId} is only for {allowed.Count} item type(s); {item} is {actual}", nameof(item));
    }

    internal static InstanceSlot? SlotOf(LiveItem live, InstanceKeyInfo info, bool create)
    {
        if (!live.Values.ByMod.TryGetValue(info.ModId, out var keys))
        {
            if (!create) return null;
            live.Values.ByMod[info.ModId] = keys = new SortedDictionary<string, InstanceSlot>(StringComparer.Ordinal);
        }
        if (keys.TryGetValue(info.Name, out var s)) return s;
        if (!create) return null;
        return keys[info.Name] = new InstanceSlot { Session = info.Persist == Persist.Session };
    }

    internal bool Remove(LiveItem live, InstanceKeyInfo info)
    {
        if (!live.Values.ByMod.TryGetValue(info.ModId, out var keys) || !keys.Remove(info.Name)) return false;
        if (keys.Count == 0) live.Values.ByMod.Remove(info.ModId);
        if (live.Values.IsEmpty)
        {
            _live.Remove(live.Ref.Entity.Id.Value);
            Unfollow(live);
        }
        return true;
    }

    internal IEnumerable<(LiveItem Item, InstanceSlot Slot)> AllOf(InstanceKeyInfo info)
    {
        foreach (var l in _live.Values.OrderBy(l => l.Ref.Entity.Id.Value).ToList())
            if (l.Values.ByMod.TryGetValue(info.ModId, out var keys) && keys.TryGetValue(info.Name, out var s))
                yield return (l, s);
    }

    internal IEnumerable<(HolderEntry? Holder, SavedRecord? Record, LiveItem? Pending, InstanceSlot Slot)> ParkedOf(InstanceKeyInfo info)
    {
        foreach (var h in _holders)
            foreach (var r in h.Records)
                if (r.Values.ByMod.TryGetValue(info.ModId, out var keys) && keys.TryGetValue(info.Name, out var s))
                    yield return (h, r, null, s);
        foreach (var p in _pending)
            if (p.Item.Values.ByMod.TryGetValue(info.ModId, out var keys) && keys.TryGetValue(info.Name, out var s))
                yield return (null, null, p.Item, s);
    }

    // ------------------------------------------------------------------------- claim and discard (dev.27) ----

    /// <summary>The saved record with this id, in its holder; null when no holder has it now.</summary>
    internal (HolderEntry Holder, SavedRecord Record)? FindRecord(string id)
    {
        foreach (var h in _holders)
            foreach (var r in h.Records)
                if (string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)) return (h, r);
        return null;
    }

    /// <summary>Whether the record still waits in its holder with values of <paramref name="mod"/> (null: any mod).</summary>
    internal bool IsParked(HolderEntry h, SavedRecord r, string? mod) =>
        _holders.Contains(h) && h.Records.Contains(r) && (mod is null ? !r.Values.IsEmpty : r.Values.ByMod.TryGetValue(mod, out var k) && k.Count > 0);

    /// <summary>Moves the values of <paramref name="mod"/> (null: every mod) from a parked record onto an item. Nothing moves
    /// when the item already has a value of one of those keys. The record goes once it holds nothing.</summary>
    /// <exception cref="ArgumentException">The record is not parked any more, or the item is none, stamp-less, stale or of a
    /// type <paramref name="info"/> does not allow.</exception>
    internal InstanceClaimResult ClaimRecord(HolderEntry h, SavedRecord r, string? mod, ItemInstanceRef item, InstanceKeyInfo? info, string by)
    {
        if (!IsParked(h, r, mod)) throw new ArgumentException($"record {r.Id} is no longer parked", nameof(item));
        var existed = item.IsNone ? null : LiveOf(item);
        var live = Lookup(item, info, create: true)
                   ?? throw new ArgumentException($"{item} is stale: the item is gone, or its id belongs to another item now", nameof(item));
        var parts = r.Values.ByMod.Where(kv => kv.Value.Count > 0 && (mod is null || kv.Key == mod)).ToList();
        var taken = new List<string>();
        foreach (var (m, keys) in parts)
            if (live.Values.ByMod.TryGetValue(m, out var have))
                taken.AddRange(keys.Keys.Where(have.ContainsKey).Select(k => mod is null ? $"{m}:{k}" : k));
        var names = parts.SelectMany(kv => kv.Value.Keys.Select(k => mod is null ? $"{kv.Key}:{k}" : k)).ToList();
        if (taken.Count > 0)
        {
            if (existed is null && live.Values.IsEmpty)
            {
                _live.Remove(live.Ref.Entity.Id.Value);  // made for this claim only
                Unfollow(live);
            }
            return new InstanceClaimResult(live.Ref, r.Id, mod ?? "", taken, false,
                $"the item already has {string.Join(", ", taken)}: remove {(taken.Count == 1 ? "it" : "them")} first");
        }
        foreach (var (m, keys) in parts)
        {
            if (!live.Values.ByMod.TryGetValue(m, out var mine))
                live.Values.ByMod[m] = mine = new SortedDictionary<string, InstanceSlot>(StringComparer.Ordinal);
            foreach (var (k, slot) in keys) mine[k] = slot;
            r.Values.ByMod.Remove(m);
        }
        if (r.Values.IsEmpty) h.Records.Remove(r);
        var detail = $"claimed by {by}: record {r.Id} of {h.Describe} slot {r.Slot} ({(r.Aside ? "set aside" : "waiting")})";
        SavesRuntime.Log(LogLevel.Info, $"Kiln.Saves: instance data {string.Join(", ", names)} {detail} onto {live.Ref}");
        if (Events.HasSubscribers<InstanceDataRestoredEvent>())
            foreach (var (m, keys) in parts)
                foreach (var k in keys.Keys)
                    Post(new InstanceDataRestoredEvent(live.Ref, m, k, InstanceMatch.Claimed, detail));
        return new InstanceClaimResult(live.Ref, r.Id, mod ?? "", names, true, null);
    }

    /// <summary>Drops the values of <paramref name="mod"/> (null: every mod) from a parked record; the record goes once it
    /// holds nothing. What was dropped, per mod; empty when the record is not parked (any more).</summary>
    internal IReadOnlyList<(string Mod, IReadOnlyList<string> Keys)> DiscardRecord(HolderEntry h, SavedRecord r, string? mod, string by)
    {
        if (!IsParked(h, r, mod)) return [];
        var gone = new InstanceValues();
        foreach (var (m, keys) in r.Values.ByMod.Where(kv => kv.Value.Count > 0 && (mod is null || kv.Key == mod)).ToList())
        {
            gone.ByMod[m] = keys;
            r.Values.ByMod.Remove(m);
        }
        if (r.Values.IsEmpty) h.Records.Remove(r);
        Lost(gone, r.Fields.Item, ItemInstanceRef.None, InstanceLossReason.Discarded,
            $"discarded by {by}: record {r.Id} of {h.Describe} slot {r.Slot} ({(r.Aside ? "set aside" : "waiting")})");
        return gone.Parts.ToList();
    }

    private static string NewRecordId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    private static void Unfollow(LiveItem l)
    {
        try
        {
            l.Follow?.Dispose();
        }
        catch (Exception e) when (e is KilnUnavailableException or InvalidOperationException)
        {
            // the item source is gone; nothing is followed any more
        }
        l.Follow = null;
    }

    private HolderIdentity HolderOf(IInstanceWorld w, EntityId holder)
    {
        if (_stagingHolders is not { } cache) return w.Holder(holder);
        if (!cache.TryGetValue(holder.Value, out var answer))
        {
            try
            {
                answer = (w.Holder(holder), null);
            }
            catch (KilnUnavailableException e)
            {
                answer = (default, e);
            }
            cache[holder.Value] = answer;
        }
        if (answer.Error is { } error) throw error;
        return answer.Identity;
    }

    // ------------------------------------------------------------------------------------- moves and gone ----

    private void OnMoved(ItemInstanceRef item, InstancePlace from, InstancePlace to)
    {
        if (LiveOf(item) is not { } live || _world is not { } w) return;
        // "Exists, placed nowhere" (its holder's teardown, maybe a drag): changes nothing the store knows of its place.
        if (to.Holder.IsNone) return;
        HolderIdentity identity;
        try
        {
            identity = HolderOf(w, to.Holder);
        }
        catch (KilnUnavailableException)
        {
            return; // the next staging re-reads it; unavailable is not a new identity
        }
        var same = to.Holder == live.Place.Holder;
        live.Place = to;
        live.Holder = Kept(live.Holder, same, identity);
    }

    /// <summary>A holder identity read again. While the item stays in the same holder entity (ids are never used twice in a
    /// world), a known identity is never replaced by a lesser one: none, or a player without a full character (a leaving
    /// player whose record was reset, a lookup that could not answer).</summary>
    internal static HolderIdentity Kept(in HolderIdentity old, bool sameHolder, in HolderIdentity fresh)
    {
        if (!sameHolder) return fresh;
        var oldKnown = old.Kind == HolderKind.World || (old.Kind == HolderKind.Player && !old.Character.IsNone && !old.Character.IsAccountOnly);
        var freshLess = fresh.Kind == HolderKind.None || (fresh.Kind == HolderKind.Player && (fresh.Character.IsNone || fresh.Character.IsAccountOnly));
        if (oldKnown && freshLess) return old;
        return sameHolder && old.Kind == HolderKind.Player && fresh.Kind == HolderKind.Player &&
            old.Character == fresh.Character && fresh.Visit == 0 ? fresh with { Visit = old.Visit } : fresh;
    }

    private void OnGone(ItemInstanceRef item, InstancePlace last, ulong step)
    {
        if (LiveOf(item) is not { } live) return;
        _live.Remove(item.Entity.Id.Value);
        Unfollow(live);
        if (!last.Holder.IsNone) live.Place = last;  // an unplaced last read keeps the last placed location
        if (live.Holder.Kind == HolderKind.Player)
        {
            if (live.Holder.Character.IsNone || live.Holder.Character.IsAccountOnly)
            {
                Lost(live.Values, live.Fields.Item, live.Ref, InstanceLossReason.Unmatched,
                    "gone from a player whose character has no id on this build (account-only), so it cannot wait for the character");
                return;
            }
            // Destroyed, or the player is leaving: not decided in this step. A live, initialized inventory at the step the
            // item went means it was not the leave's teardown, so that one is never parked as "the player left".
            var alive = false;
            try
            {
                if (_world is { } w && HolderOf(w, live.Place.Holder) is { Kind: HolderKind.Player } current &&
                    current.Character == live.Holder.Character && current.Visit != 0 && current.Visit == live.Holder.Visit)
                    w.ReadHolder(live.Place.Holder, out alive);
            }
            catch (KilnUnavailableException)
            {
                // No positive evidence that this visit's inventory is still alive.
            }
            _pending.Add(new Pending
            {
                Item = live, Character = live.Holder.Character, Player = live.Place.Holder, Visit = live.Holder.Visit,
                Deadline = step + LeaveWindowSteps, InventoryAlive = alive,
            });
            return;
        }
        Lost(live.Values, live.Fields.Item, live.Ref, InstanceLossReason.Destroyed, $"the item is gone (last in {live.Holder.Name ?? live.Place.Holder.ToString()} slot {live.Place.FlatSlot})");
    }

    // ----------------------------------------------------------------------------------------- the passes ----

    // The store's own work runs in passes: a tick's bookkeeping, a match of one holder's records, the staging of a save.
    // A pass keeps lists of its own while it runs (the outcomes of a match, the items of a save), and its events used to
    // go out in the middle of it. A handler may claim or discard a parked record (InstanceData.Claim, Discard): that
    // changed records the pass was still going to act on (a claim was overwritten by the attach planned before it; a
    // record claimed during staging was in neither half of the save), and broke loops over the records. So the events of
    // a pass wait until the outermost pass is finished, and then go out in order: a handler finds the store as the pass
    // left it. What it changes then belongs to the next pass (for a save: the next save).
    private int _passes;
    private bool _draining;
    private readonly Queue<Action> _afterPass = new();
    private int _cleared;  // counts Clear(): a handler that closed the world emptied the queue under the loop below

    private void BeginPass() => _passes++;

    private void EndPass()
    {
        if (--_passes > 0 || _draining) return;
        Drain();
    }

    // One queue, delivered in order by one loop. A handler may start a pass of its own (touching an item matches its
    // holder) or claim and discard: those events join the end of the queue and go out when the ones already waiting
    // have, from this loop, never from inside the handler. (Delivered from inside, they overtook the ones waiting, and a
    // chain of them ran into the event bus's nesting limit.)
    private void Drain()
    {
        _draining = true;
        try
        {
            while (_afterPass.TryPeek(out var post))
            {
                var cleared = _cleared;
                post();
                if (cleared == _cleared) _afterPass.Dequeue();  // else the queue is another one by now
            }
        }
        catch (Exception e)
        {
            // The bus isolates handlers, so this is the bus itself refusing (nested too deep in a caller's own event).
            // The event stays first in line for the next pass's end; nothing behind it is lost.
            SavesRuntime.Log(LogLevel.Warn, $"Kiln.Saves: {_afterPass.Count} item data event(s) wait for the next step: {e.Message}");
        }
        finally
        {
            _draining = false;
        }
    }

    // Always through the queue: an event posted outside a pass (a discard) must not overtake ones that still wait
    // because the bus refused them at the end of an earlier pass.
    private void Post<T>(T ev) where T : KilnEvent
    {
        _afterPass.Enqueue(() => Events.Post(ev));
        if (_passes == 0 && !_draining) Drain();
    }

    // ------------------------------------------------------------------------------------------- the tick ----

    /// <summary>A world load: holders are looked for from now on (world holders for <see cref="MatchWindowSteps"/>).</summary>
    public void StartMatching()
    {
        _session++;
        _matching = true;
        _windowCounted = false;
        _windowEnd = 0;
        foreach (var h in _holders) h.MissCounted = h.FindUnavailable = false;
    }

    /// <summary>Tick thread, about once a second: players who left or joined, world holders that streamed in, the end of
    /// the match window.</summary>
    public void Tick()
    {
        BeginPass();
        try
        {
            TickPass();
        }
        finally
        {
            EndPass();
        }
    }

    private void TickPass()
    {
        if (_world is not { } w) return;
        try
        {
            w.Require();
            var step = w.Step;
            if (_matching && _windowEnd == 0) _windowEnd = step + MatchWindowSteps;
            var players = w.PlayersInGame();
            ResolvePending(step, players);
            CheckBound(w, players);
            JoinPlayers(w, players);
            // World holders are looked for all session; the window only decides when one not found yet counts as missed.
            if (_matching) ResolveWorldHolders(w);
            if (_matching && step >= _windowEnd && !_windowCounted) CountMissedHolders();
        }
        catch (KilnUnavailableException)
        {
            // A session with unavailable reads is not evidence that an absent holder was destroyed.
            foreach (var h in _holders.Where(h => h.Key.Kind == HolderKind.World && !h.IsBound)) h.FindUnavailable = true;
        }
    }

    private void ResolvePending(ulong step, IReadOnlyList<(EntityId Entity, CharacterKey Character, ulong Visit)> players)
    {
        foreach (var p in _pending.ToArray())
        {
            // The slot can already hold another player, or the same character in another visit.
            var inGame = p.Visit != 0 && players.Any(x => x.Character == p.Character && x.Entity == p.Player && x.Visit == p.Visit);
            if (inGame && step < p.Deadline) continue;
            _pending.Remove(p);
            if (!inGame && !p.InventoryAlive)
            {
                ParkLeft(p);
                continue;
            }
            Lost(p.Item.Values, p.Item.Fields.Item, p.Item.Ref, InstanceLossReason.Destroyed, inGame
                ? $"gone from the inventory of character {p.Character}, who stayed in game"
                : $"gone from the inventory of character {p.Character} while it was still in use, before the player left");
        }
    }

    private void ParkLeft(Pending p)
    {
        var entry = EntryOfCharacter(p.Character, create: true)!;
        var (seen, slot, alt) = Observe(p.Item, entry);
        var r = new SavedRecord
        {
            Id = p.Item.RecordId,
            Slot = slot,
            Fields = p.Item.Fields,
            Seen = seen,
            Alt = alt,
            Reason = seen == 0 && alt.All(a => a.Slot < 0)
                ? "the player left with it before any save or join read it in the inventory: it cannot be told from a look-alike"
                : "the player left with it",
            Since = _owner.Generation,
        };
        r.Values.Take(p.Item.Values);
        entry.Records.Add(r);
        Parked(entry, r, InstanceParkReason.PlayerLeft);
    }

    private void CheckBound(IInstanceWorld w, IReadOnlyList<(EntityId Entity, CharacterKey Character, ulong Visit)> players)
    {
        foreach (var h in _holders.Where(h => h.IsBound).ToArray())
        {
            if (h.Key.Kind == HolderKind.Player)
            {
                if (!players.Any(p => p.Entity == h.Bound && p.Character == h.Key.Character && p.Visit == h.BoundVisit)) Unbind(h);
                continue;
            }
            HolderIdentity id;
            try
            {
                id = w.Holder(h.Bound);
            }
            catch (KilnUnavailableException)
            {
                continue;  // cannot tell now
            }
            if (id.Kind != HolderKind.World || id.Template != h.Key.Template) Unbind(h);  // destroyed, or the id is another entity now
        }
    }

    private void JoinPlayers(IInstanceWorld w, IReadOnlyList<(EntityId Entity, CharacterKey Character, ulong Visit)> players)
    {
        if (!TrackPlayerInventories) return;
        foreach (var (entity, character, visit) in players)
        {
            if (character.IsNone || character.IsAccountOnly) continue;
            // every join is read, records or not: the inventory as it came is a state the client may bring back again
            var entry = EntryOfCharacter(character, create: true)!;
            if (entry.Bound == entity && entry.BoundVisit == visit && entry.Matched) continue;
            TryBindPlayer(w, entry, entity, visit);
        }
    }

    private bool TryBindPlayer(IInstanceWorld w, HolderEntry entry, EntityId entity, ulong visit)
    {
        var items = w.ReadHolder(entity, out var initialized);
        if (!initialized) return false;
        if (entry.IsBound) Unbind(entry);
        if (_byEntity.TryGetValue(entity.Value, out var other)) Unbind(other);
        Bind(entry, entity, visit);
        var asMade = RunMatch(w, entry, items);
        // The join: the character file the client brought. A later file is this one or a later save's; earlier ones are
        // gone. Settled only when the items are as the loader made them (else the read may not be the file).
        var id = JoinIdBase | (++entry.Joins & ~JoinIdBase);
        entry.Witness[id] = new Witness(items.Select(i => new WitnessEntry(i.Place.FlatSlot, i.Fields)).ToList(), asMade);
        entry.States.RemoveAll(st => st.Entity != entity || st.Id == id || st.SlotOf is null);
        entry.States.Insert(0, new InventoryState(id, entity, items.ToDictionary(i => i.Ref.Entity.Id.Value, i => i.Place.FlatSlot)));
        Trim(entry);
        foreach (var i in items)
            if (LiveOf(i.Ref) is { } live)
            {
                live.Seen = id;
                live.SeenSlot = i.Place.FlatSlot;
                live.SeenEntry = entry;
            }
        return true;
    }

    private static void Trim(HolderEntry e)
    {
        while (e.States.Count > PlayerStatesKept) e.States.RemoveAt(0);
    }

    /// <summary>A save read a player's inventory (or could not: <paramref name="items"/> null): one more state the client
    /// may bring back.</summary>
    private static void AddState(HolderEntry e, uint id, EntityId entity, IReadOnlyList<InstanceItem>? items)
    {
        // the second staging of a save replaces the first; a blind second staging keeps a first that read
        if (items is null && e.States.Any(st => st.Id == id && st.SlotOf is not null)) return;
        e.States.RemoveAll(st => st.Id == id);
        e.States.Add(new InventoryState(id, entity, items?.ToDictionary(i => i.Ref.Entity.Id.Value, i => i.Place.FlatSlot)));
        Trim(e);
    }

    /// <summary>What a record of this item in this holder is written with: its newest observation in this holder (slot and
    /// seen from one read; none when no save read it here), and for a player its slot at every other state of the visit
    /// the client may bring back.</summary>
    private static (uint Seen, int Slot, List<(uint Seen, int Slot)> Alt) Observe(LiveItem live, HolderEntry e)
    {
        var seen = ReferenceEquals(live.SeenEntry, e) ? live.Seen : 0;
        var slot = seen != 0 ? live.SeenSlot : live.Place.FlatSlot;
        var alt = new List<(uint, int)>();
        if (e.Key.Kind == HolderKind.Player)
            foreach (var st in e.States)
                if (st.Entity == live.Place.Holder && st.Id != seen)
                    alt.Add((st.Id, st.SlotOf is null ? -2 : st.SlotOf.TryGetValue(live.Ref.Entity.Id.Value, out var at) ? at : -1));
        return (seen, slot, alt);
    }

    /// <summary>Up to <see cref="ResolvePerTick"/> unbound world holders, in turn, so a holder that streams in late is
    /// matched within a pass (200 unbound holders: 13 ticks) instead of when a mod happens to touch one of its items.</summary>
    private void ResolveWorldHolders(IInstanceWorld w)
    {
        var waiting = _holders.Where(h => h.Key.Kind == HolderKind.World && !h.IsBound && h.Aside is null && h.Records.Count > 0).ToArray();
        if (waiting.Length == 0) return;
        var n = Math.Min(ResolvePerTick, waiting.Length);
        var start = _resolveCursor % waiting.Length;
        for (var i = 0; i < n; i++) TryResolve(w, waiting[(start + i) % waiting.Length]);
        _resolveCursor = (start + n) % waiting.Length;
    }

    /// <summary>PLAN D6 rule 5: exactly one unclaimed entity of the template inside the radius, else the holder's records
    /// are set aside; none yet: it is looked for again.</summary>
    private bool TryResolve(IInstanceWorld w, HolderEntry h)
    {
        if (h.Crowded)
        {
            SetHolderAside(h, $"another entity of its template stood within {h.Key.Radius} m of it at the save");
            return false;
        }
        IReadOnlyList<EntityId> found;
        try
        {
            found = w.FindHolders(h.Key.Template, h.Key.Position, h.Key.Radius);
        }
        catch (KilnUnavailableException)
        {
            h.FindUnavailable = true;
            return false;
        }
        var free = found.Where(e => !_byEntity.ContainsKey(e.Value)).Distinct().ToArray();
        if (free.Length == 0) return false;
        if (free.Length > 1)
        {
            SetHolderAside(h, $"{free.Length} entities of its template within {h.Key.Radius} m: not certain which one it is");
            return false;
        }
        var items = w.ReadHolder(free[0], out var initialized);
        if (!initialized) return false;
        Bind(h, free[0]);
        if (HolderOf(w, free[0]) is { Kind: HolderKind.World } id) h.Name ??= id.Name;
        RunMatch(w, h, items);
        return true;
    }

    /// <summary>A mod touches an item whose holder has saved records not matched yet: match that holder now.</summary>
    private void MatchHolderOf(IInstanceWorld w, in InstanceItem item)
    {
        var holder = item.Place.Holder;
        if (holder.IsNone || (_byEntity.TryGetValue(holder.Value, out var bound) && bound.Key.Kind == HolderKind.World)) return;
        var id = HolderOf(w, holder);
        if (id.Kind == HolderKind.World)
        {
            foreach (var h in _holders.Where(h => h.Key.Kind == HolderKind.World && !h.IsBound && h.Aside is null && h.Records.Count > 0 &&
                                                  h.Key.Template == id.Template && h.Key.Position.DistanceTo(id.Position) <= h.Key.Radius).ToArray())
                if (TryResolve(w, h) && _byEntity.ContainsKey(holder.Value)) return;
        }
        else if (id.Kind == HolderKind.Player && !id.Character.IsNone && !id.Character.IsAccountOnly &&
                 EntryOfCharacter(id.Character, create: false) is { Records.Count: > 0 } entry &&
                 !(entry.Bound == holder && entry.BoundVisit == id.Visit && entry.Matched))
        {
            TryBindPlayer(w, entry, holder, id.Visit);
        }
    }

    private void CountMissedHolders()
    {
        _windowCounted = true;
        foreach (var h in _holders.Where(h => h.Key.Kind == HolderKind.World && !h.IsBound && h.Aside is null && h.Records.Count > 0).ToArray())
        {
            if (h.MissCounted || h.FindUnavailable) continue;
            h.MissCounted = true;
            h.Missed++;
            if (h.Missed >= MaxMissedSessions)
            {
                foreach (var r in h.Records)
                    Lost(r.Values, r.Fields.Item, ItemInstanceRef.None, InstanceLossReason.Unmatched,
                        $"its holder {h.Describe} was not found in {MaxMissedSessions} sessions");
                _holders.Remove(h);
                continue;
            }
            foreach (var r in h.Records.Where(r => !r.Aside)) Parked(h, r, InstanceParkReason.HolderAway);
        }
        var waiting = _holders.Count(h => h.Key.Kind == HolderKind.World && !h.IsBound && h.Records.Count > 0);
        if (waiting > 0)
            SavesRuntime.Log(LogLevel.Info, $"Kiln.Saves: instance data: {waiting} world holder(s) not found after the match window (kept, up to {MaxMissedSessions} sessions)");
    }

    private void Bind(HolderEntry h, EntityId entity, ulong visit = 0)
    {
        h.Bound = entity;
        h.BoundVisit = visit;
        h.Matched = false;
        h.Missed = 0;  // found: its missed sessions start over
        _byEntity[entity.Value] = h;
    }

    private void Unbind(HolderEntry h)
    {
        if (h.IsBound && _byEntity.TryGetValue(h.Bound.Value, out var b) && ReferenceEquals(b, h)) _byEntity.Remove(h.Bound.Value);
        h.Bound = EntityId.None;
        h.Matched = false;
    }

    private void SetHolderAside(HolderEntry h, string reason)
    {
        h.Aside = reason;
        foreach (var r in h.Records.Where(r => !r.Aside))
        {
            r.Aside = true;
            r.Reason = "holder: " + reason;
            r.Since = _owner.Generation;
            Lost(r.Values, r.Fields.Item, ItemInstanceRef.None, InstanceLossReason.Ambiguous, $"{h.Describe}: {reason}");
        }
        SavesRuntime.Log(LogLevel.Warn, $"Kiln.Saves: instance data of holder {h.Describe} set aside: {reason}");
    }

    /// <summary>Runs the matcher over a holder that was just found and acts on its decisions.</summary>
    private bool RunMatch(IInstanceWorld w, HolderEntry h, IReadOnlyList<InstanceItem> items)
    {
        BeginPass();
        try
        {
            return RunMatchPass(w, h, items);
        }
        finally
        {
            EndPass();
        }
    }

    private bool RunMatchPass(IInstanceWorld w, HolderEntry h, IReadOnlyList<InstanceItem> items)
    {
        h.Matched = true;
        var recs = h.Records.ToArray();
        var input = recs.Select(r => new MatchRecord(r.Slot, r.Fields.Stable, r.Seen, r.Aside, r.OnlyTrusted, r.Alt.Count > 0 ? r.Alt : null)).ToArray();
        var now = items.Select(i => new MatchItem(i.Place.FlatSlot, i.Fields, LiveOf(i.Ref) is not null)).ToArray();
        uint? trusted = h.Key.Kind == HolderKind.World && _loadedMatch is DataMatch.Exact or DataMatch.Staged ? _loadedGeneration : null;
        // Rule 1 also needs the holder's items as the loader made them in this session (nothing swapped or brought in
        // between their load and now): the entity ids tell.
        // The loader's own slots are known when the world file is the generation and its witness is settled.
        var made = items.Count;
        IReadOnlyList<int>? loadSlots = null;
        if (trusted is { } g && h.Witness.TryGetValue(g, out var gw) && gw.Settled)
        {
            made = gw.Entries.Count;
            loadSlots = gw.Entries.Select(e => e.Slot).ToList();
        }
        // A player's character loader does not make the items in flat-slot order (worn gear first): checked per link.
        string order;
        var asMade = h.Key.Kind == HolderKind.Player
            ? LoadOrder.AsMadeByLinks(items.Select(i => (i.Place.FlatSlot, i.Ref.Entity.Id.Value, i.Fields.Stable)).ToList(), out order)
            : LoadOrder.AsMade(h.Bound, h.Key.Kind == HolderKind.World, items.Select(i => (i.Place.FlatSlot, i.Ref.Entity.Id.Value)).ToList(),
                made, loadSlots, out order);
        // A client's character file can be newer than a restored world, including an invisible swap of twins.
        // Only the newest loaded data can vouch for all the states the client may bring.
        var slotMatch = asMade && (h.Key.Kind == HolderKind.World || _playerGenerationTrusted);
        var outcomes = InstanceMatcher.Match(input, h.Witness, now, trusted, slotMatch, world: h.Key.Kind == HolderKind.World);
        int attached = 0, aside = 0, parked = 0;
        foreach (var o in outcomes)
        {
            var r = recs[o.Record];
            switch (o.Kind)
            {
                case MatchKind.AttachExact:
                case MatchKind.AttachMoved:
                    h.Records.Remove(r);
                    Attach(w, r, items[o.Item], o.Kind == MatchKind.AttachExact ? InstanceMatch.Exact : InstanceMatch.Moved, o.Reason);
                    attached++;
                    break;
                case MatchKind.SetAside:
                    r.Aside = true;
                    r.Reason = o.Reason;
                    r.Since = _owner.Generation;
                    aside++;
                    Lost(r.Values, r.Fields.Item, ItemInstanceRef.None, InstanceLossReason.Ambiguous, $"{h.Describe} slot {r.Slot}: {o.Reason}");
                    break;
                case MatchKind.Parked:
                    r.Reason = o.Reason;
                    if (r.MissSession != _session)
                    {
                        r.MissSession = _session;
                        r.Missed++;
                    }
                    if (r.Missed >= MaxMissedSessions)
                    {
                        h.Records.Remove(r);
                        Lost(r.Values, r.Fields.Item, ItemInstanceRef.None, InstanceLossReason.Unmatched,
                            $"{h.Describe} slot {r.Slot}: no item fit it in {MaxMissedSessions} sessions ({o.Reason})");
                        break;
                    }
                    if (r.Since == 0) r.Since = _owner.Generation;
                    parked++;
                    Parked(h, r, InstanceParkReason.NoCandidate);
                    break;
            }
        }
        // A player's join with records says how the loader made the items (the runs by link), so a real join shows it.
        var player = h.Key.Kind == HolderKind.Player;
        SavesRuntime.Log(aside > 0 ? LogLevel.Warn : player && recs.Length > 0 ? LogLevel.Info : LogLevel.Debug,
            $"Kiln.Saves: instance data of {h.Describe} (entity {h.Bound}): {attached} attached, {parked} waiting, {aside} set aside" +
            $"{(trusted is not null ? $" (generation {trusted} is the world file's own)" : "")}" +
            $"{(asMade ? player ? $"; as loaded: {order}" : "" : $"; not as loaded: {order}")}");
        return asMade;
    }

    private void Attach(IInstanceWorld w, SavedRecord r, in InstanceItem item, InstanceMatch match, string reason)
    {
        var live = new LiveItem
        {
            Ref = item.Ref,
            RecordId = r.Id,
            Place = item.Place,
            Fields = item.Fields,
            Holder = HolderOf(w, item.Place.Holder),
            Seen = r.Seen,
            SeenSlot = r.Slot,
        };
        if (r.Seen != 0) live.SeenEntry = _byEntity.GetValueOrDefault(item.Place.Holder.Value);
        live.Values.Take(r.Values);
        live.Follow = w.Follow(item.Ref);
        _live[item.Ref.Entity.Id.Value] = live;
        if (!Events.HasSubscribers<InstanceDataRestoredEvent>()) return;
        foreach (var (mod, keys) in live.Values.Parts)
            foreach (var k in keys)
                Post(new InstanceDataRestoredEvent(item.Ref, mod, k, match, reason));
    }

    private void Parked(HolderEntry h, SavedRecord r, InstanceParkReason reason)
    {
        if (!Events.HasSubscribers<InstanceDataParkedEvent>()) return;
        foreach (var (mod, keys) in r.Values.Parts)
            Post(new InstanceDataParkedEvent(h.Describe, h.Key.Kind == HolderKind.Player ? h.Key.Character : default, r.Slot, r.Fields.Item, mod, keys, reason, r.Reason));
    }

    private void Lost(InstanceValues values, ItemId item, ItemInstanceRef last, InstanceLossReason reason, string detail)
    {
        SavesRuntime.Log(reason == InstanceLossReason.Destroyed ? LogLevel.Debug : LogLevel.Info,
            $"Kiln.Saves: instance data of {item} ({string.Join(", ", values.Parts.Select(p => p.Mod))}) {reason}: {detail}");
        if (!Events.HasSubscribers<InstanceDataLostEvent>()) return;
        foreach (var (mod, keys) in values.Parts)
            Post(new InstanceDataLostEvent(item, last, mod, keys, reason, detail));
    }

    private void Note(string id, LogLevel level, string text)
    {
        if (_noted.Add(id)) SavesRuntime.Log(level, text);
    }

    private HolderEntry? EntryOfCharacter(CharacterKey c, bool create)
    {
        var key = HolderKey.OfCharacter(c);
        var h = _holders.FirstOrDefault(x => x.Key.Same(key));
        if (h is null && create)
        {
            h = new HolderEntry { Key = key };
            _holders.Add(h);
        }
        return h;
    }

    // ------------------------------------------------------------------------------------------------ load ----

    /// <summary>One bundle of a loaded generation: "instances/&lt;mod&gt;" or "character-instances/&lt;character&gt;/&lt;mod&gt;".</summary>
    public void Absorb(string scope, JsonObject doc)
    {
        string mod;
        CharacterKey character = default;
        if (scope.StartsWith(WorldPrefix, StringComparison.Ordinal)) mod = scope[WorldPrefix.Length..];
        else if (scope.StartsWith(CharacterPrefix, StringComparison.Ordinal))
        {
            var rest = scope[CharacterPrefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0 || !CharacterKey.TryParse(rest[..slash], out character)) return;
            mod = rest[(slash + 1)..];
        }
        else return;
        if (doc["holders"] is not JsonArray holders) return;
        foreach (var n in holders)
        {
            try
            {
                if (n is JsonObject o) AbsorbHolder(o, mod, character);
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException or JsonException or ArgumentException)
            {
                SavesRuntime.Log(LogLevel.Error, $"Kiln.Saves: {scope}: a holder entry is unusable ({e.Message}); it is skipped");
            }
        }
    }

    private void AbsorbHolder(JsonObject o, string mod, CharacterKey character)
    {
        HolderKey key;
        var ho = o["holder"] as JsonObject ?? throw new FormatException("no holder");
        if (ho["kind"]?.GetValue<string>() == "character")
            key = HolderKey.OfCharacter(CharacterKey.TryParse(ho["character"]?.GetValue<string>(), out var c) ? c : character);
        else
        {
            var p = ho["pos"] as JsonArray ?? throw new FormatException("no pos");
            key = new HolderKey(HolderKind.World, Guid.Parse(ho["template"]!.GetValue<string>()),
                new WorldPosition(p[0]!.GetValue<double>(), p[1]!.GetValue<double>(), p[2]!.GetValue<double>()), ho["mobile"]?.GetValue<bool>() ?? false, default);
        }
        if (key.Kind == HolderKind.Player && (key.Character.IsNone || key.Character.IsAccountOnly)) return;
        var h = _holders.FirstOrDefault(x => x.Key.Same(key));
        if (h is null) _holders.Add(h = new HolderEntry { Key = key });
        h.Name ??= ho["name"]?.GetValue<string>();
        h.Missed = Math.Max(h.Missed, o["missed"]?.GetValue<int>() ?? 0);
        h.Crowded |= o["crowded"]?.GetValue<bool>() ?? false;
        h.Aside ??= o["aside"]?.GetValue<string>();
        h.LastWitness = Math.Max(h.LastWitness, o["last"]?.GetValue<uint>() ?? 0);
        h.Joins = Math.Max(h.Joins, o["joins"]?.GetValue<uint>() ?? 0);
        if (o["witness"] is JsonObject ws)
            foreach (var (obs, node) in ws)
            {
                if (!uint.TryParse(obs, out var g) || h.Witness.ContainsKey(g)) continue;
                // { "settled": bool, "items": [...] }; a bare list (an earlier writer) counts as not settled
                var list = node as JsonArray ?? node?["items"] as JsonArray ?? throw new FormatException("witness without items");
                var settled = node is JsonObject wo && (wo["settled"]?.GetValue<bool>() ?? false);
                h.Witness[g] = new Witness(list.Select(e => e as JsonArray ?? throw new FormatException("witness entry"))
                    .Select(e => new WitnessEntry(e[0]!.GetValue<int>(), ReadFields(e, 1))).ToList(), settled);
            }
        if (o["records"] is not JsonArray records) return;
        foreach (var rn in records)
        {
            if (rn is not JsonObject ro) continue;
            var id = ro["id"]?.GetValue<string>() ?? NewRecordId();
            var slot = ro["slot"]!.GetValue<int>();
            var fields = ReadFields(ro["rec"] as JsonArray ?? throw new FormatException("no rec"), 0);
            var seen = ro["seen"]?.GetValue<uint>() ?? 0;
            var state = ro["state"]?.GetValue<string>() ?? "waiting";
            // the parts of one item in several mods' bundles: same record id, place, fields and observation
            var r = h.Records.FirstOrDefault(x => x.Id == id && x.Slot == slot && x.Fields == fields && x.Seen == seen);
            if (r is null)
            {
                r = new SavedRecord { Id = id, Slot = slot, Fields = fields, Seen = seen };
                h.Records.Add(r);
            }
            r.Aside |= state == "aside";
            if (r.Reason.Length == 0) r.Reason = ro["why"]?.GetValue<string>() ?? "";
            r.Missed = Math.Max(r.Missed, ro["missed"]?.GetValue<int>() ?? 0);
            r.Since = Math.Max(r.Since, ro["since"]?.GetValue<uint>() ?? 0);
            r.OnlyTrusted |= ro["gone"]?.GetValue<bool>() ?? false;
            if (r.Alt.Count == 0 && ro["alt"] is JsonArray alts)
                r.Alt = alts.Select(a => a as JsonArray ?? throw new FormatException("alt entry"))
                    .Select(a => (a[0]!.GetValue<uint>(), a[1]!.GetValue<int>())).ToList();
            if (ro["keys"] is JsonObject ks)
            {
                if (!r.Values.ByMod.TryGetValue(mod, out var keys))
                    r.Values.ByMod[mod] = keys = new SortedDictionary<string, InstanceSlot>(StringComparer.Ordinal);
                foreach (var (k, v) in ks) keys[k] = new InstanceSlot { Json = v?.ToJsonString() ?? "null" };
            }
        }
    }

    private static InstanceFields ReadFields(JsonArray a, int at)
    {
        int I(int i) => a[at + i]!.GetValue<int>();
        uint U(int i) => a[at + i]!.GetValue<uint>();
        return new InstanceFields(new ItemId(U(0)), I(1), I(2), I(3), I(4), I(5), I(6), new ItemId(U(7)), I(8), I(9), new ItemId(U(10)), I(11));
    }

    private static JsonArray WriteFields(in InstanceFields f, int? slot = null)
    {
        var a = new JsonArray();
        if (slot is { } s) a.Add(s);
        a.Add(f.Item.Value);
        a.Add(f.Count);
        a.Add(f.Level);
        a.Add(f.Durability);
        a.Add(f.DurabilityMax);
        a.Add(f.Fuel);
        a.Add(f.FuelMax);
        a.Add(f.Dye.Value);
        a.Add(f.PerksAvailable);
        a.Add(f.PerksUnlocked);
        a.Add(f.Gem.Value);
        a.Add(f.GemLevel);
        return a;
    }

    /// <summary>The loaded generation and how it matched the world file (the trust of PLAN D6 rule 1 comes from it, for
    /// the whole session). Caps are applied to what was loaded.</summary>
    public void OnLoaded(uint generation, DataMatch match)
    {
        _loadedGeneration = generation;
        _loadedMatch = match;
        _playerGenerationTrusted = match is DataMatch.Exact or DataMatch.Staged &&
            _owner.Root is { } root && !WorldStore.ListManifests(Path.Combine(root, "manifests")).Any(m => m.Revision > generation);
        foreach (var h in _holders) ApplyCap(h, null);
    }

    // ----------------------------------------------------------------------------------------------- stage ----

    private sealed record WriteRec(string Id, int Slot, InstanceFields Fields, uint Seen, string State, string Reason, int Missed, uint Since,
        bool OnlyTrusted, InstanceValues Values, IReadOnlyList<(uint Seen, int Slot)> Alt);

    /// <summary>Both stagings of generation <paramref name="gen"/>: the place and fingerprint of every item with data are
    /// read fresh, and every holder's records are written with the witness of this save (PLAN D6 rule 6, D7).</summary>
    /// <param name="gen">The generation.</param>
    /// <param name="json">The mods' serializer options.</param>
    /// <param name="settled">This staging is the snapshot staging, or a shutdown save's: no simulation step comes between
    /// this read and the game's snapshot, so the witness taken now can vouch for the holder (rule 1).</param>
    public List<(string Scope, byte[] Data)> Stage(uint gen, Dictionary<string, JsonSerializerOptions> json, bool settled = false)
    {
        // Item reads can synchronously announce moves back to this store. Share the same identity cache with them.
        _stagingHolders = new();
        BeginPass();
        try
        {
            return StageSnapshot(gen, json, settled);
        }
        finally
        {
            _stagingHolders = null;
            EndPass();  // what staging found out (an item gone) is told now, with the save's bytes already made
        }
    }

    private List<(string Scope, byte[] Data)> StageSnapshot(uint gen, Dictionary<string, JsonSerializerOptions> json, bool settled)
    {
        var output = new List<(string Scope, byte[] Data)>();
        var w = _world;
        var reads = false;
        if (w is not null)
        {
            try
            {
                w.Require();
                reads = true;
            }
            catch (KilnUnavailableException)
            {
                reads = false;
            }
        }
        var written = new Dictionary<HolderEntry, List<WriteRec>>(ReferenceEqualityComparer.Instance);
        List<WriteRec> Of(HolderEntry h) => written.TryGetValue(h, out var l) ? l : written[h] = [];

        // 1. Every item with data, read now in one native call.
        // A leaving player's unplaced items can outlive its entity slot. Never combine them with the next visit.
        var byHolder = new Dictionary<(uint Entity, CharacterKey Character, ulong Visit), List<LiveItem>>();
        foreach (var live in _live.Values) live.Fresh = false;
        foreach (var pending in _pending) pending.Item.Fresh = false;
        var persistent = _live.Values.Where(live => live.Values.HasPersistent).ToArray();
        IReadOnlyList<InstanceItem?>? current = null;
        if (reads && persistent.Length != 0)
        {
            try
            {
                current = w!.Read(persistent.Select(live => live.Ref).ToArray());
            }
            catch (KilnUnavailableException)
            {
                // Cannot tell now: written where last seen, with the old observation.
            }
        }
        for (var i = 0; i < persistent.Length; i++)
        {
            var live = persistent[i];
            if (current is not null)
            {
                var now = current[i];
                if (now is null)
                {
                    OnGone(live.Ref, live.Place, w!.Step);
                    continue;
                }
                // "exists, placed nowhere" (its holder's teardown): it keeps its last placed location and its old observation
                if (now is { } n && !n.Place.Holder.IsNone)
                {
                    try
                    {
                        var identity = HolderOf(w!, n.Place.Holder);
                        var same = n.Place.Holder == live.Place.Holder;
                        live.Place = n.Place;
                        live.Fields = n.Fields;
                        live.Holder = Kept(live.Holder, same, identity);
                        live.Fresh = true;
                    }
                    catch (KilnUnavailableException)
                    {
                        // Keep its last observed holder and witness, never treat an unavailable identity as none.
                    }
                }
            }
            var group = (live.Place.Holder.Value,
                live.Holder.Kind == HolderKind.Player ? live.Holder.Character : default,
                live.Holder.Kind == HolderKind.Player ? live.Holder.Visit : 0);
            if (!byHolder.TryGetValue(group, out var list)) byHolder[group] = list = [];
            list.Add(live);
        }

        // 2. Per holder: its key, the witness of this save, and its live items as records.
        var playersRead = new HashSet<(uint Entity, CharacterKey Character, ulong Visit)>();
        var witnessed = new HashSet<HolderEntry>();
        var liveWrites = new List<(LiveItem Item, HolderEntry Entry)>();
        foreach (var (group, items) in byHolder.OrderBy(kv => kv.Key.Entity))
        {
            var holderId = group.Entity;
            var identity = items.FirstOrDefault(i => i.Fresh)?.Holder ?? items[0].Holder;
            var holder = new EntityId(holderId);
            if (identity.Kind == HolderKind.None ||
                (identity.Kind == HolderKind.Player && (identity.Character.IsNone || identity.Character.IsAccountOnly)))
            {
                Note($"nokey:{holderId}:{identity.Kind}", LogLevel.Warn, $"Kiln.Saves: instance data of {items.Count} item(s) in entity {holder}: " +
                     (identity.Kind == HolderKind.Player ? "the player's character has no id on this build" : "the holder has no persistent identity (not saved with a position)") +
                     "; kept for this session only");
                continue;
            }
            var key = HolderKey.Of(identity);
            // Stale items keep their character's records, but do not reclaim a slot now owned by another visit.
            var entry = key.Kind == HolderKind.Player && !items.Any(i => i.Fresh)
                ? EntryOfCharacter(key.Character, create: true)!
                : EntryForWrite(holder, key, identity.Name, identity.Visit);
            uint obs = 0;
            var blind = !reads;  // the reads could not answer: this save may have sent a file Kiln could not read
            if (reads && items.Any(i => i.Fresh))
            {
                try
                {
                    var list = w!.ReadHolder(holder, out var initialized);
                    if (initialized)
                    {
                        entry.Witness[gen] = new Witness(list.Select(i => new WitnessEntry(i.Place.FlatSlot, i.Fields)).ToList(), settled);
                        witnessed.Add(entry);
                        obs = gen;
                        if (key.Kind == HolderKind.Player)
                        {
                            entry.LastWitness = gen;
                            AddState(entry, gen, holder, list);
                            playersRead.Add(group);
                        }
                    }
                    if (key.Kind == HolderKind.World)
                        entry.Crowded = w.FindHolders(key.Template, key.Position, key.Radius).Any(e => e != holder);
                }
                catch (KilnUnavailableException)
                {
                    obs = 0;
                    blind = true;
                }
            }
            // A player whose inventory could not be read: this save may send a file Kiln never saw. (Not when the entity
            // is gone: nothing is sent to a client whose player entity was destroyed.)
            if (key.Kind == HolderKind.Player && obs == 0 && blind)
            {
                AddState(entry, gen, holder, null);
                playersRead.Add(group);
            }
            foreach (var live in items.OrderBy(i => i.Place.FlatSlot))
            {
                // A new observation only from a fresh, placed read in this save's list of this holder; otherwise the item keeps
                // its newest observation in this holder (slot and seen from that one read), or has none here.
                if (obs != 0 && live.Fresh)
                {
                    live.Seen = obs;
                    live.SeenSlot = live.Place.FlatSlot;
                    live.SeenEntry = entry;
                }
                liveWrites.Add((live, entry));
            }
        }

        // 2b. When instance data is in use, every player in game, with data or not: the inventory this save sends back
        // is a state the client may bring back. A key first declared mid-visit has no earlier state to rely on.
        if (w is not null && TrackPlayerInventories)
        {
            IReadOnlyList<(EntityId Entity, CharacterKey Character, ulong Visit)> players;
            try
            {
                players = w.PlayersInGame();
            }
            catch (KilnUnavailableException)
            {
                players = [];
            }
            foreach (var (entity, character, visit) in players)
            {
                if (character.IsNone || character.IsAccountOnly || playersRead.Contains((entity.Value, character, visit))) continue;
                var entry = EntryOfCharacter(character, create: true)!;
                IReadOnlyList<InstanceItem>? list = null;
                if (reads)
                {
                    try
                    {
                        // not initialized yet: the file this save sends is the one the client joined with; the join read
                        // drops this blind state, as nothing can move before the inventory is there
                        list = w.ReadHolder(entity, out var initialized);
                        if (!initialized) list = null;
                    }
                    catch (KilnUnavailableException)
                    {
                        list = null;
                    }
                }
                if (list is not null)
                {
                    entry.Witness[gen] = new Witness(list.Select(i => new WitnessEntry(i.Place.FlatSlot, i.Fields)).ToList(), settled);
                    witnessed.Add(entry);
                    entry.LastWitness = gen;
                }
                AddState(entry, gen, entity, list);
            }
        }

        // A snapshot witness replaces the first staging's list. It cannot vouch for an observation from that first
        // read when this read did not place the item. Include players re-read only by 2b, and pending leave notices.
        foreach (var live in _live.Values.Concat(_pending.Select(p => p.Item)))
            if (!live.Fresh && live.Seen == gen && live.SeenEntry is { } entry && witnessed.Contains(entry))
                live.Seen = 0;
        foreach (var (live, entry) in liveWrites)
        {
            var (seen, slot, alt) = Observe(live, entry);
            Of(entry).Add(new WriteRec(live.RecordId, slot, live.Fields, seen, "live",
                seen == 0 ? "no save has read it in this holder yet" : "", 0, 0, false, live.Values, alt));
        }

        // 3. Items gone from a player's inventory in a way that may be the leave: the generation keeps them, waiting, but
        // they may only come back to an inventory that is as it was (never by rule 2).
        foreach (var p in _pending.Where(p => !p.InventoryAlive))
        {
            if (!p.Item.Values.HasPersistent) continue;
            var entry = EntryOfCharacter(p.Character, create: true)!;
            var (seen, slot, alt) = Observe(p.Item, entry);
            Of(entry).Add(new WriteRec(p.Item.RecordId, slot, p.Item.Fields, seen, "waiting",
                "gone from the player's inventory at the save (the player leaving, or destroyed)", 0, gen, true, p.Item.Values, alt));
        }

        // 4. Waiting and set-aside records, wherever their holder is.
        foreach (var h in _holders)
        {
            ApplyCap(h, written.GetValueOrDefault(h));
            foreach (var r in h.Records)
                Of(h).Add(new WriteRec(r.Id, r.Slot, r.Fields, r.Seen, r.Aside ? "aside" : "waiting", r.Reason, r.Missed, r.Since, r.OnlyTrusted, r.Values, r.Alt));
        }

        // 5. Witnesses no record needs any more go.
        foreach (var h in _holders)
        {
            var keep = new HashSet<uint>((written.GetValueOrDefault(h) ?? []).SelectMany(r => r.Alt.Select(a => a.Seen).Append(r.Seen))) { h.LastWitness };
            keep.UnionWith(h.States.Select(st => st.Id));
            foreach (var g in h.Witness.Keys.Where(g => !keep.Contains(g)).ToArray()) h.Witness.Remove(g);
        }
        _holders.RemoveAll(h => !h.IsBound && h.Records.Count == 0 && h.States.Count == 0 && !written.ContainsKey(h));

        // 6. One bundle per mod (world holders) and per character and mod.
        var bundles = new SortedDictionary<string, List<(HolderEntry h, List<(WriteRec r, SortedDictionary<string, InstanceSlot> keys)> recs)>>(StringComparer.Ordinal);
        foreach (var (h, recs) in written.OrderBy(kv => kv.Key.Key.Text, StringComparer.Ordinal))
        {
            var mods = new SortedDictionary<string, List<(WriteRec, SortedDictionary<string, InstanceSlot>)>>(StringComparer.Ordinal);
            foreach (var r in recs.OrderBy(r => r.Slot).ThenBy(r => r.Id, StringComparer.Ordinal))
                foreach (var (mod, keys) in r.Values.ByMod)
                    if (keys.Values.Any(s => !s.Session))
                    {
                        if (!mods.TryGetValue(mod, out var l)) mods[mod] = l = [];
                        l.Add((r, keys));
                    }
            foreach (var (mod, all) in mods)
            {
                var scope = h.Key.Kind == HolderKind.Player ? $"{CharacterPrefix}{h.Key.Character.Text}/{mod}" : WorldPrefix + mod;
                if (!bundles.TryGetValue(scope, out var b)) bundles[scope] = b = [];
                var list = all;
                if (list.Count > MaxRecordsPerHolderPerMod)
                {
                    Note($"cap:{h.Key.Text}:{mod}", LogLevel.Warn, $"Kiln.Saves: instance data of {mod} in {h.Describe}: {list.Count} live records, " +
                                                                   $"over {MaxRecordsPerHolderPerMod}; the rest stays for this session only");
                    list = list.Take(MaxRecordsPerHolderPerMod).ToList();
                }
                b.Add((h, list));
            }
        }
        foreach (var (scope, entries) in bundles)
        {
            var mod = scope[(scope.LastIndexOf('/') + 1)..];
            var opts = json.GetValueOrDefault(mod);
            var doc = new JsonObject { ["format"] = Format, ["scope"] = scope };
            var arr = new JsonArray();
            foreach (var (h, recs) in entries) arr.Add(WriteHolder(h, recs, mod, opts));
            doc["holders"] = arr;
            output.Add((scope, Encoding.UTF8.GetBytes(doc.ToJsonString())));
        }
        return output;
    }

    private JsonObject WriteHolder(HolderEntry h, List<(WriteRec r, SortedDictionary<string, InstanceSlot> keys)> recs, string mod, JsonSerializerOptions? opts)
    {
        var ho = h.Key.Kind == HolderKind.Player
            ? new JsonObject { ["kind"] = "character", ["character"] = h.Key.Character.Text }
            : new JsonObject
            {
                ["kind"] = "world",
                ["template"] = h.Key.Template.ToString(),
                ["pos"] = new JsonArray(JsonValue.Create(Math.Round(h.Key.Position.X, 3)), JsonValue.Create(Math.Round(h.Key.Position.Y, 3)),
                    JsonValue.Create(Math.Round(h.Key.Position.Z, 3))),
                ["mobile"] = h.Key.Mobile,
            };
        if (h.Name is not null) ho["name"] = h.Name;
        var o = new JsonObject { ["holder"] = ho };
        if (h.Missed > 0) o["missed"] = h.Missed;
        if (h.Crowded) o["crowded"] = true;
        if (h.Aside is not null) o["aside"] = h.Aside;
        if (h.LastWitness != 0) o["last"] = h.LastWitness;
        if (h.Joins != 0) o["joins"] = h.Joins;
        var ws = new JsonObject();
        foreach (var g in recs.SelectMany(x => x.r.Alt.Select(a => a.Seen).Append(x.r.Seen)).Append(h.LastWitness).Where(g => g != 0).Distinct().Order())
            if (h.Witness.TryGetValue(g, out var wit))
                ws[g.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                {
                    ["settled"] = wit.Settled,
                    ["items"] = new JsonArray(wit.Entries.OrderBy(e => e.Slot).Select(e => (JsonNode?)WriteFields(e.Fields, e.Slot)).ToArray()),
                };
        if (ws.Count > 0) o["witness"] = ws;
        var ra = new JsonArray();
        foreach (var (r, keys) in recs)
        {
            var ko = new JsonObject();
            foreach (var (k, s) in keys)
            {
                if (s.Session) continue;
                var text = Serialize(s, opts, mod, k, r.Id);
                if (text is null) continue;
                ko[k] = JsonNode.Parse(text);
            }
            if (ko.Count == 0) continue;
            var ro = new JsonObject
            {
                ["id"] = r.Id,
                ["slot"] = r.Slot,
                ["rec"] = WriteFields(r.Fields),
                ["seen"] = r.Seen,
                ["state"] = r.State,
            };
            if (r.Reason.Length > 0) ro["why"] = r.Reason;
            if (r.Missed > 0) ro["missed"] = r.Missed;
            if (r.Since > 0) ro["since"] = r.Since;
            if (r.OnlyTrusted) ro["gone"] = true;
            if (r.Alt.Count > 0)
                ro["alt"] = new JsonArray(r.Alt.Select(a => (JsonNode?)new JsonArray(JsonValue.Create(a.Seen), JsonValue.Create(a.Slot))).ToArray());
            ro["keys"] = ko;
            ra.Add(ro);
        }
        o["records"] = ra;
        return o;
    }

    /// <summary>The bound entry of a holder entity, or a new one bound to it. A bound entry whose key no longer fits (its id
    /// is another entity now) lets go first.</summary>
    private HolderEntry EntryForWrite(EntityId holder, HolderKey key, string? name, ulong visit)
    {
        if (_byEntity.TryGetValue(holder.Value, out var h))
        {
            var fits = key.Kind == h.Key.Kind && (key.Kind == HolderKind.Player
                ? key.Character == h.Key.Character && visit == h.BoundVisit : key.Template == h.Key.Template);
            if (fits)
            {
                h.Key = key;
                h.Name ??= name;
                return h;
            }
            Unbind(h);
        }
        if (key.Kind == HolderKind.Player && EntryOfCharacter(key.Character, create: false) is { } c)
        {
            if (c.IsBound) Unbind(c);
            Bind(c, holder, visit);
            return c;
        }
        h = new HolderEntry { Key = key, Name = name };
        _holders.Add(h);
        Bind(h, holder, visit);
        return h;
    }

    /// <summary>PLAN D7: at most 256 records per holder per mod; over that, the oldest waiting or set-aside parts go
    /// first, reported.</summary>
    private void ApplyCap(HolderEntry h, List<WriteRec>? live)
    {
        var mods = h.Records.SelectMany(r => r.Values.ByMod.Keys).Concat(live?.SelectMany(r => r.Values.ByMod.Keys) ?? []).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var mod in mods)
        {
            var count = (live?.Count(r => r.Values.ByMod.TryGetValue(mod, out var k) && k.Values.Any(s => !s.Session)) ?? 0) +
                        h.Records.Count(r => r.Values.ByMod.ContainsKey(mod));
            var over = count - MaxRecordsPerHolderPerMod;
            if (over <= 0) continue;
            foreach (var r in h.Records.Where(r => r.Values.ByMod.ContainsKey(mod)).OrderBy(r => r.Since).ThenByDescending(r => r.Missed).Take(over).ToArray())
            {
                var keys = r.Values.ByMod[mod];
                r.Values.ByMod.Remove(mod);
                if (Events.HasSubscribers<InstanceDataLostEvent>())
                    Post(new InstanceDataLostEvent(r.Fields.Item, ItemInstanceRef.None, mod, keys.Keys.ToList(), InstanceLossReason.Capped,
                        $"{h.Describe} has more than {MaxRecordsPerHolderPerMod} records of {mod}: the oldest waiting or set-aside ones go"));
                if (r.Values.IsEmpty) h.Records.Remove(r);
            }
            SavesRuntime.Log(LogLevel.Warn, $"Kiln.Saves: instance data of {mod} in {h.Describe}: {over} oldest waiting or set-aside record(s) dropped (cap {MaxRecordsPerHolderPerMod})");
        }
    }

    // --------------------------------------------------------------------------------------------- listing ----

    /// <summary>What the admin command shows: per mod, what is attached, waiting and set aside, and values that did not fit.
    /// <paramref name="entity"/> (a holder or an item entity) and <paramref name="character"/> narrow it.</summary>
    internal IReadOnlyList<string> Listing(EntityId? entity = null, CharacterKey? character = null)
    {
        var lines = new List<string>
        {
            $"instance data: generation {_loadedGeneration} ({_loadedMatch}), {Summary}" + (_world is null ? "; kiln.items supplies no item instances here (items.instances off)" : ""),
        };
        bool Wanted(HolderKey key, EntityId bound, EntityId item) =>
            (entity is not { } e || e == bound || e == item) && (character is not { } c || (key.Kind == HolderKind.Player && key.Character == c));
        var mods = _live.Values.SelectMany(l => l.Values.ByMod.Keys).Concat(_holders.SelectMany(h => h.Records.SelectMany(r => r.Values.ByMod.Keys)))
            .Concat(_pending.SelectMany(p => p.Item.Values.ByMod.Keys)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var mod in mods)
        {
            var section = new List<string>();
            foreach (var l in _live.Values.OrderBy(l => l.Place.Holder.Value).ThenBy(l => l.Place.FlatSlot))
            {
                if (!l.Values.ByMod.TryGetValue(mod, out var keys) || keys.Count == 0) continue;
                if (!Wanted(HolderKey.Of(l.Holder), l.Place.Holder, l.Ref.Entity.Id)) continue;
                section.Add($"  attached  {l.Ref} in {(l.Holder.Kind == HolderKind.None ? "" : HolderKey.Of(l.Holder).Text + " ")}entity {l.Place.Holder} slot {l.Place.FlatSlot}: " +
                            string.Join(", ", keys.Select(k => k.Key + (k.Value.Session ? " (session)" : ""))));
                foreach (var (k, s) in keys.Where(k => k.Value.TooBig > 0))
                    section.Add($"  too big   {l.Ref} key {k}: {s.TooBig} bytes (cap {MaxValueBytes}); its last form that fit is saved");
            }
            foreach (var p in _pending)
                if (p.Item.Values.ByMod.TryGetValue(mod, out var keys) && keys.Count > 0 && Wanted(HolderKey.OfCharacter(p.Character), EntityId.None, p.Item.Ref.Entity.Id))
                    section.Add($"  deciding  {p.Item.Ref} of character {p.Character} slot {p.Item.Place.FlatSlot}: gone; destroyed or the player left (until step {p.Deadline})");
            foreach (var h in _holders.OrderBy(h => h.Key.Text, StringComparer.Ordinal))
                foreach (var r in h.Records.OrderBy(r => r.Slot))
                {
                    if (!r.Values.ByMod.TryGetValue(mod, out var keys) || keys.Count == 0) continue;
                    if (!Wanted(h.Key, h.Bound, EntityId.None)) continue;
                    section.Add($"  {(r.Aside ? "set aside" : "waiting  ")} record {r.Id} {h.Describe}{(h.IsBound ? $" (entity {h.Bound})" : "")} slot {r.Slot} item {r.Fields.Item}: " +
                                $"{(r.Reason.Length > 0 ? r.Reason : "not matched yet")}" +
                                $"{(h.Missed > 0 ? $"; holder missed {h.Missed} session(s)" : "")}{(r.Missed > 0 ? $"; missed {r.Missed} session(s)" : "")} [{string.Join(", ", keys.Keys)}]");
                }
            if (section.Count == 0) continue;
            lines.Add(mod + ":");
            lines.AddRange(section);
        }
        if (lines.Count == 1) lines.Add(entity is not null || character is not null ? "nothing for that holder" : "no instance data");
        return lines;
    }
}
