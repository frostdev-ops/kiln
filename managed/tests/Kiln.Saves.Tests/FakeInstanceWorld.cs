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
    private uint _nextId;
    public ulong Step { get; set; } = 1000;
    public bool Off { get; set; }
    public int ReadCalls { get; private set; }
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
        if (Off) throw new KilnUnavailableException("items.instances", "off in the test");
    }

    public InstanceItem? Read(ItemInstanceRef item)
    {
        Require();
        ReadCalls++;
        return Items.TryGetValue(item.Entity.Id.Value, out var i) && i.Ref.Entity.Serial == item.Entity.Serial ? Info(i) : null;
    }

    public IReadOnlyList<InstanceItem> ReadHolder(EntityId holder, out bool initialized)
    {
        Require();
        initialized = Holders.TryGetValue(holder.Value, out var h) && h.Initialized && (h.Identity.Kind != HolderKind.Player || h.InGame);
        return initialized ? Items.Values.Where(i => i.Holder == holder).OrderBy(i => i.Slot).Select(Info).ToList() : [];
    }

    public IDisposable Follow(ItemInstanceRef item)
    {
        var id = item.Entity.Id.Value;
        Followed[id] = Followed.GetValueOrDefault(id) + 1;
        return new Handle(this, item);
    }

    public HolderIdentity Holder(EntityId holder) => Holders.TryGetValue(holder.Value, out var h) ? h.Identity : HolderIdentity.None;

    public IReadOnlyList<EntityId> FindHolders(Guid template, WorldPosition near, double radius) =>
        Holders.Values.Where(h => h.Identity.Kind == HolderKind.World && h.Identity.Template == template && h.Identity.Position.DistanceTo(near) <= radius)
            .OrderBy(h => h.Identity.Position.DistanceTo(near)).Select(h => h.Entity).ToList();

    public IReadOnlyList<(EntityId Entity, CharacterKey Character)> PlayersInGame() =>
        Holders.Values.Where(h => h.Identity.Kind == HolderKind.Player && h.InGame).Select(h => (h.Entity, h.Identity.Character)).ToList();

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

    public EntityId Join(CharacterKey character, string? name = null)
    {
        var e = new EntityId(_nextId++);
        Holders[e.Value] = new FakeHolder { Entity = e, Identity = HolderIdentity.OfPlayer(character, name ?? "Player"), InGame = true };
        foreach (var (slot, f) in CharacterFiles.GetValueOrDefault(character) ?? []) Give(e, slot, f);
        return e;
    }

    public ItemInstanceRef Give(EntityId holder, int slot, InstanceFields fields, uint? reuseId = null)
    {
        if (Items.Values.Any(i => i.Holder == holder && i.Slot == slot)) throw new InvalidOperationException($"slot {slot} of {holder} is taken");
        var id = reuseId ?? _nextId++;
        if (Items.ContainsKey(id)) throw new InvalidOperationException($"entity {id} exists");
        var r = new ItemInstanceRef(new EntityRef(new EntityId(id), (1UL << 60) | ++_stamps), fields.Item);
        Items[id] = new FakeItem { Ref = r, Holder = holder, Slot = slot, Fields = fields };
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

    /// <summary>The game's load in a new process: new entity ids and stamps, the same slots and record fields. Players are
    /// not in game until they join (with their character file).</summary>
    public static FakeInstanceWorld Load(Snapshot s, uint firstId = 5000, bool streamed = true)
    {
        var w = new FakeInstanceWorld(firstId);
        foreach (var (holder, items) in s.World)
        {
            var e = w.Chest(holder.Template, holder.Position, holder.Mobile, holder.Name);
            w.Holders[e.Value].Initialized = streamed;
            foreach (var (slot, f) in items) w.Give(e, slot, f);
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
