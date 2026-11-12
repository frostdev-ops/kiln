// SPDX-License-Identifier: MIT
// A fake of the game behind IInstanceWorld: holders (chests by template and position, players by character), item entities
// with stamps, following with Moved and Gone, and the game's own save and load (new entity ids and stamps, the same slots and
// record fields, the player's inventory coming back with the character). The store and the matcher under test are the real ones.
using System.Reflection;

namespace Kiln;

internal sealed class FakeInstanceWorld : IInstanceWorld
{
    internal sealed class FakeItem
    {
        public ItemInstanceRef Ref;
        public EntityId Holder;
        public int Slot;
        public InstanceFields Fields;

        /// <summary>Ground truth for tests: which item this is, through character files and rejoins.</summary>
        public int Identity;

        /// <summary>The holder whose teardown left it placed nowhere.</summary>
        public EntityId FormerHolder;
    }

    internal sealed class FakeHolder
    {
        public EntityId Entity;
        public HolderIdentity Identity;
        public bool Initialized = true;
        public bool InGame;
    }

    /// <summary>The game's save: each world holder's records, and each character's file (what the client keeps).</summary>
    internal sealed record Snapshot(
        List<(HolderIdentity Holder, List<(int Slot, InstanceFields Fields)> Items)> World,
        Dictionary<CharacterKey, List<(int Slot, InstanceFields Fields)>> Characters);

    private static ulong _stamps;  // per process, never reused, never 0 (as kiln.items' sequence)
    private static ulong _visits;
    private uint _nextId;
    public ulong Step { get; set; } = 1000;
    public bool Off { get; set; }
    public bool SpawnOff { get; set; }
    public bool FindOff { get; set; }

    /// <summary>A leaving player's entity answers without its character (a lookup after its record was reset).</summary>
    public bool ForgetLeavingCharacter { get; set; }

    /// <summary>Ground truth of the character files: slot -> identity of the item there.</summary>
    public readonly Dictionary<CharacterKey, Dictionary<int, int>> FileIdentities = new();
    public int ReadCalls { get; private set; }
    public int BatchReadCalls { get; private set; }
    public int ReadHolderCalls { get; private set; }
    public int HolderCalls { get; private set; }
    public bool NotifyMovesOnRead { get; set; }
    public int FindCalls { get; set; }

    public void ResetReadCounts() => ReadCalls = BatchReadCalls = ReadHolderCalls = HolderCalls = 0;
    public readonly Dictionary<uint, FakeItem> Items = new();
    public readonly Dictionary<uint, FakeHolder> Holders = new();
    public readonly Dictionary<uint, int> Followed = new();
    public readonly Dictionary<CharacterKey, List<(int Slot, InstanceFields Fields)>> CharacterFiles = new();

    public event Action<ItemInstanceRef, InstancePlace, InstancePlace>? Moved;
    public event Action<ItemInstanceRef, InstancePlace, ulong>? Gone;

    public FakeInstanceWorld(uint firstId = 2000) => _nextId = firstId;

    // ---- IInstanceWorld ----
    public void Require()
    {
        if (Off) throw new KilnUnavailableException(OffCapability, OffReason);
    }

    /// <summary>What <see cref="Require"/> names when <see cref="Off"/>: items.instances, or kiln.spawn for the holder
    /// identity operations.</summary>
    public string OffCapability { get; set; } = global::Kiln.Items.Caps.Instances;

    public string OffReason { get; set; } = "off in the test";

    public InstanceItem? Read(ItemInstanceRef item)
    {
        Require();
        ReadCalls++;
        return ReadInfo(item);
    }

    public IReadOnlyList<InstanceItem?> Read(IReadOnlyList<ItemInstanceRef> items)
    {
        Require();
        BatchReadCalls++;
        return items.Select(ReadInfo).ToArray();
    }

    private InstanceItem? ReadInfo(ItemInstanceRef item)
    {
        if (!Items.TryGetValue(item.Entity.Id.Value, out var i) || i.Ref.Entity.Serial != item.Entity.Serial) return null;
        if (NotifyMovesOnRead && !i.Holder.IsNone && IsFollowed(item))
            Moved?.Invoke(item, default, new InstancePlace(i.Holder, i.Slot));
        return Info(i);
    }

    public IReadOnlyList<InstanceItem> ReadHolder(EntityId holder, out bool initialized)
    {
        Require();
        ReadHolderCalls++;
        // a leaving player's inventory reads as long as its entity exists
        initialized = Holders.TryGetValue(holder.Value, out var h) && h.Initialized;
        return initialized ? Items.Values.Where(i => i.Holder == holder).OrderBy(i => i.Slot).Select(Info).ToList() : [];
    }

    public IDisposable Follow(ItemInstanceRef item)
    {
        var id = item.Entity.Id.Value;
        Followed[id] = Followed.GetValueOrDefault(id) + 1;
        return new Handle(this, item);
    }

    public HolderIdentity Holder(EntityId holder)
    {
        HolderCalls++;
        if (SpawnOff) throw new KilnUnavailableException("kiln.spawn", "holder identity unavailable in the test");
        return !Holders.TryGetValue(holder.Value, out var h) ? HolderIdentity.None
            : h.Identity.Kind == HolderKind.Player && !h.InGame && ForgetLeavingCharacter ? HolderIdentity.OfPlayer(default, h.Identity.Name)
            : h.Identity;
    }

    public IReadOnlyList<EntityId> FindHolders(Guid template, WorldPosition near, double radius)
    {
        FindCalls++;
        if (FindOff || SpawnOff) throw new KilnUnavailableException("kiln.spawn", "find unavailable in the test");
        return Holders.Values.Where(h => h.Identity.Kind == HolderKind.World && h.Identity.Template == template && h.Identity.Position.DistanceTo(near) <= radius)
            .OrderBy(h => h.Identity.Position.DistanceTo(near)).Select(h => h.Entity).ToList();
    }

    public IReadOnlyList<(EntityId Entity, CharacterKey Character, ulong Visit)> PlayersInGame() =>
        Holders.Values.Where(h => h.Identity.Kind == HolderKind.Player && h.InGame).Select(h => (h.Entity, h.Identity.Character, h.Identity.Visit)).ToList();

    private sealed class Handle(FakeInstanceWorld w, ItemInstanceRef item) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            var id = item.Entity.Id.Value;
            if (w.Followed.TryGetValue(id, out var n)) w.Followed[id] = n - 1;
            if (w.Followed.GetValueOrDefault(id) <= 0) w.Followed.Remove(id);
        }
    }

    private static InstanceItem Info(FakeItem i) => new(i.Ref, new InstancePlace(i.Holder, i.Slot), i.Fields);

    public bool IsFollowed(ItemInstanceRef r) => Followed.GetValueOrDefault(r.Entity.Id.Value) > 0 && Items.TryGetValue(r.Entity.Id.Value, out var i) && i.Ref == r;

    // ---- the world, changed by the test ----
    public EntityId Chest(Guid template, WorldPosition at, bool mobile = false, string? name = null)
    {
        var e = new EntityId(_nextId++);
        Holders[e.Value] = new FakeHolder { Entity = e, Identity = HolderIdentity.OfWorld(template, at, mobile, name ?? "Chest") };
        return e;
    }

    public EntityId Join(CharacterKey character, string? name = null, EntityId? reuseId = null)
    {
        var e = reuseId ?? new EntityId(_nextId++);
        if (Holders.ContainsKey(e.Value)) throw new InvalidOperationException($"holder {e} exists");
        Holders[e.Value] = new FakeHolder { Entity = e, Identity = HolderIdentity.OfPlayer(character, name ?? "Player") with { Visit = ++_visits }, InGame = true };
        var ids = FileIdentities.GetValueOrDefault(character);
        var file = CharacterFiles.GetValueOrDefault(character) ?? [];
        foreach (var (slot, f) in PlayerLoaderWornFirst ? file.OrderBy(i => i.Slot < 256 ? 1 : 0).ThenBy(i => i.Slot) : file.OrderBy(i => i.Slot))
            Give(e, slot, f, identity: ids is not null && ids.TryGetValue(slot, out var x) ? x : null);
        return e;
    }

    /// <summary>The character loader as a real join showed it (dev26-playtest, 2026-11-12: the item in flat slot 256 was
    /// made before the backpack's): the items of the links the save does not number (worn gear, 256 and up) first, then the
    /// backpack in flat-slot order. Off: everything in flat-slot order.</summary>
    public bool PlayerLoaderWornFirst { get; set; }

    /// <summary>The game sends the character back (a world save while the player is in game, or its final save): the
    /// client's file is now the inventory as it is.</summary>
    public void SendCharacter(EntityId player)
    {
        var c = Holders[player.Value].Identity.Character;
        var placed = Items.Values.Where(i => i.Holder == player).OrderBy(i => i.Slot).ToList();
        CharacterFiles[c] = placed.Select(i => (i.Slot, i.Fields)).ToList();
        FileIdentities[c] = placed.ToDictionary(i => i.Slot, i => i.Identity);
    }

    /// <summary>Leave from the menu, first part: the record is Leaving; the player is out of the in-game list, its entity and
    /// inventory remain until the final save is acknowledged.</summary>
    public void BeginLeave(EntityId player) => Holders[player.Value].InGame = false;

    /// <summary>The player entity (with its inventory setup) is destroyed; its item entities live on for one step, placed
    /// nowhere (docs: leave-order.md section 3).</summary>
    public void DestroyPlayerEntity(EntityId player)
    {
        Holders.Remove(player.Value);
        foreach (var i in Items.Values.Where(i => i.Holder == player))
        {
            i.FormerHolder = player;
            i.Holder = EntityId.None;
            i.Slot = -1;
        }
    }

    /// <summary>The next step: the item entities left by a holder's teardown go (Gone, from a place nowhere).</summary>
    public void TearDown(EntityId player)
    {
        foreach (var i in Items.Values.Where(i => i.FormerHolder == player && i.Holder.IsNone).ToList()) Destroy(i.Ref);
    }

    /// <summary>A crash or a lost connection: the record is reset and the entity destroyed in one call, no leave save;
    /// the client keeps the file of the last save it received.</summary>
    public void Crash(EntityId player)
    {
        DestroyPlayerEntity(player);
        TearDown(player);
    }

    public ItemInstanceRef Give(EntityId holder, int slot, InstanceFields fields, uint? reuseId = null, int? identity = null)
    {
        if (Items.Values.Any(i => i.Holder == holder && i.Slot == slot)) throw new InvalidOperationException($"slot {slot} of {holder} is taken");
        var id = reuseId ?? _nextId++;
        if (Items.ContainsKey(id)) throw new InvalidOperationException($"entity {id} exists");
        var r = new ItemInstanceRef(new EntityRef(new EntityId(id), (1UL << 60) | ++_stamps), fields.Item);
        Items[id] = new FakeItem { Ref = r, Holder = holder, Slot = slot, Fields = fields, Identity = identity ?? (int)id };
        return r;
    }

    public FakeItem Of(ItemInstanceRef r) => Items[r.Entity.Id.Value];

    /// <summary>Moves an item; <paramref name="seen"/>: the tracker notices (Moved for a followed item).</summary>
    public void Move(ItemInstanceRef r, EntityId holder, int slot, bool seen = true)
    {
        var i = Of(r);
        if (Items.Values.Any(x => x != i && x.Holder == holder && x.Slot == slot)) throw new InvalidOperationException($"slot {slot} of {holder} is taken");
        var from = new InstancePlace(i.Holder, i.Slot);
        i.Holder = holder;
        i.Slot = slot;
        if (seen && Followed.GetValueOrDefault(r.Entity.Id.Value) > 0) Moved?.Invoke(r, from, new InstancePlace(holder, slot));
    }

    public void Swap(ItemInstanceRef a, ItemInstanceRef b, bool seen = true)
    {
        var x = Of(a);
        var y = Of(b);
        var fa = new InstancePlace(x.Holder, x.Slot);
        var fb = new InstancePlace(y.Holder, y.Slot);
        (x.Holder, x.Slot, y.Holder, y.Slot) = (y.Holder, y.Slot, x.Holder, x.Slot);
        if (!seen) return;
        if (Followed.GetValueOrDefault(a.Entity.Id.Value) > 0) Moved?.Invoke(a, fa, fb);
        if (Followed.GetValueOrDefault(b.Entity.Id.Value) > 0) Moved?.Invoke(b, fb, fa);
    }

    public void Destroy(ItemInstanceRef r, bool seen = true)
    {
        var i = Of(r);
        Items.Remove(r.Entity.Id.Value);
        if (seen && Followed.GetValueOrDefault(r.Entity.Id.Value) > 0) Gone?.Invoke(r, new InstancePlace(i.Holder, i.Slot), Step);
    }

    public void SetFields(ItemInstanceRef r, InstanceFields f) => Of(r).Fields = f;

    /// <summary>The player leaves: the character goes back to the client (its file), the item entities are destroyed.</summary>
    public void Leave(EntityId player)
    {
        var h = Holders[player.Value];
        CharacterFiles[h.Identity.Character] = Items.Values.Where(i => i.Holder == player).OrderBy(i => i.Slot).Select(i => (i.Slot, i.Fields)).ToList();
        h.InGame = false;
        h.Initialized = false;
        foreach (var i in Items.Values.Where(i => i.Holder == player).ToList()) Destroy(i.Ref);
        Holders.Remove(player.Value);
    }

    /// <summary>The game's world save: world holders' records, and every character in game sent back to its client.</summary>
    public Snapshot SaveGame()
    {
        foreach (var h in Holders.Values.Where(h => h.Identity.Kind == HolderKind.Player && h.InGame))
            CharacterFiles[h.Identity.Character] = Items.Values.Where(i => i.Holder == h.Entity).OrderBy(i => i.Slot).Select(i => (i.Slot, i.Fields)).ToList();
        var world = Holders.Values.Where(h => h.Identity.Kind == HolderKind.World)
            .Select(h => (h.Identity, Items.Values.Where(i => i.Holder == h.Entity).OrderBy(i => i.Slot).Select(i => (i.Slot, i.Fields)).ToList())).ToList();
        return new Snapshot(world, CharacterFiles.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()));
    }

    /// <summary>The game's load in a new process: new entity ids and stamps (from one growing counter: each holder, then
    /// its item entities in flat-slot order), the same slots and record fields. Players are not in game until they join
    /// (with their character file). <paramref name="outOfSlotOrder"/>: a loader that makes the items in reverse order.</summary>
    public static FakeInstanceWorld Load(Snapshot s, uint firstId = 5000, bool streamed = true, bool outOfSlotOrder = false)
    {
        var w = new FakeInstanceWorld(firstId);
        foreach (var (holder, items) in s.World)
        {
            var e = w.Chest(holder.Template, holder.Position, holder.Mobile, holder.Name);
            w.Holders[e.Value].Initialized = streamed;
            foreach (var (slot, f) in outOfSlotOrder ? items.OrderByDescending(i => i.Slot) : items.OrderBy(i => i.Slot)) w.Give(e, slot, f);
        }
        foreach (var (c, file) in s.Characters) w.CharacterFiles[c] = file.ToList();
        return w;
    }

    public EntityId HolderAt(Guid template, WorldPosition at) =>
        Holders.Values.Single(h => h.Identity.Kind == HolderKind.World && h.Identity.Template == template && h.Identity.Position.DistanceTo(at) < 0.01).Entity;

    public ItemInstanceRef At(EntityId holder, int slot) => Items.Values.Single(i => i.Holder == holder && i.Slot == slot).Ref;

    public EntityId PlayerOf(CharacterKey c) => Holders.Values.Single(h => h.Identity.Kind == HolderKind.Player && h.Identity.Character == c).Entity;
}

/// <summary>Record fields for tests.</summary>
internal static class Rec
{
    public static readonly ItemId Sword = new(0x5a0d0001), Ring = new(0x41a90002), Can = new(0x8ec15337), Bow = new(0xb0b00003);

    public static InstanceFields Of(ItemId item, int level = 5, int durability = 100, int durabilityMax = 100, int fuel = 0, int fuelMax = 0,
        uint dye = 0, int perksAvailable = 0, int perksUnlocked = 0, uint gem = 0, int gemLevel = 0) =>
        new(item, 1, level, durability, durabilityMax, fuel, fuelMax, new ItemId(dye), perksAvailable, perksUnlocked, new ItemId(gem), gemLevel);
}

/// <summary>Mod contexts for tests (Kiln.Api keeps their constructor internal).</summary>
internal static class TestMods
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly FieldInfo Current = typeof(ModContext).GetField("CurrentMod", BindingFlags.NonPublic | BindingFlags.Static)!;

    public static ModContext Create(string id)
    {
        var logger = (Logger)typeof(Logger).GetConstructor(Hidden, [typeof(ulong)])!.Invoke([0UL]);
        return (ModContext)typeof(ModContext).GetConstructor(Hidden, [typeof(ModInfo), typeof(Logger), typeof(string)])!
            .Invoke([new ModInfo(id, id, "1.0.0", AppContext.BaseDirectory), logger, AppContext.BaseDirectory]);
    }

    public static ModContext? CurrentMod
    {
        get => (ModContext?)Current.GetValue(null);
        set => Current.SetValue(null, value);
    }

    /// <summary>Runs <paramref name="body"/> as <paramref name="mod"/>'s code.</summary>
    public static T As<T>(ModContext mod, Func<T> body)
    {
        var outer = CurrentMod;
        CurrentMod = mod;
        try
        {
            return body();
        }
        finally
        {
            CurrentMod = outer;
        }
    }

    public static void As(ModContext mod, Action body) => As(mod, () =>
    {
        body();
        return 0;
    });

    /// <summary>The mod unloads (its Retiring handlers run, as the host does after OnUnload).</summary>
    public static void Retire(ModContext mod) => typeof(ModContext).GetMethod("Retire", Hidden)!.Invoke(mod, null);
}
