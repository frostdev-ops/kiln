// SPDX-License-Identifier: MIT
// The fake kiln.items (native/core/abi/services/kiln_items.h, the v1 table) under the real Kiln.Items.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln.TestKit;

/// <summary>Mirror of <c>kiln_items.h</c>'s v1 table and the structs it uses. The sizes are pinned by tests to
/// Kiln.Items' private mirrors (ItemsNative).</summary>
internal static unsafe class ItemsAbi
{
    public const string Service = "kiln.items";
    public const uint Version = 1;  // the v1 table only: water (v2), moves (v3), instances (v4) and equip (v5) are not there
    public const int MaxChanges = 16, MaxInventories = 32, MessageMax = 160;

    // KilnItemsRequest.flags
    public const uint Level = 0x01, Rarity = 0x02, ExactSlot = 0x04, AllOrNothing = 0x08, Notify = 0x10, NoMerge = 0x20,
        DryRun = 0x40, Recipe = 0x80, Unlock = 0x100, GemSlot = 0x200;
    public const uint KnownFlags = 0x3FF;
    public const int ProgramNone = 0, ProgramPending = 1, ProgramActive = 2, ProgramRunning = 3, ProgramBroken = 4;

    // KilnItemsResult.status
    public const int Done = 0, Partial = 1, NoRoom = 2, NotEnough = 3, NoInventory = 4, UnknownItem = 5, SlotChanged = 6, Refused = 7,
        StatusUnavailable = 8, Dropped = 9;

    // find_inventories flags; InventorySetup flags
    public const uint FindContainers = 1, FindPlayers = 2, FindNpcs = 4, FindNonEmpty = 8;
    public const byte SetupIsPlayer = 0x01;
    public const byte CategoryGeneric = 4;
    public const byte SlotGear = 0x04;
    public const uint ItemFlagHasPide = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    public struct Request
    {
        public uint Size, Flags, Entity, Item, Count, Level;
        public byte Rarity, Unlock, R1, R2;
        public uint Pide;
        public ulong Slot;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Change
    {
        public ulong Slot;
        public uint Item, Amount, Pide, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Result
    {
        public uint Size;
        public int Status;
        public ulong Ticket;
        public uint Entity, Item, Requested, Done, GameError, ChangeCount, Flags, Steps;
        public fixed byte Changes[MaxChanges * 24];
        public fixed byte Message[MessageMax];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Slot
    {
        public ulong Id;
        public uint Item, Count, Pide;
        public ushort Level;
        public byte Rarity, Flags, Link, Category;
        public ushort State;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Inventory
    {
        public uint Size, Entity;
        public byte Initialized, SetupFlags, GenericSlots, AvailableSlots;
        public uint InventoryCount, SlotTotal, UsedSlots;
        public fixed uint Inventories[MaxInventories];
        public fixed byte Categories[MaxInventories];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ItemInfo
    {
        public uint Size, Id;
        public fixed byte Guid[16];
        public ushort MaxStack;
        public byte Category, Rarity;
        public uint ItemFlags, LevelMin, LevelMax;
        public byte EquipSlot, Gear, R0, R1;
        public NativeStr Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size;
        public byte Transactions, Read, Catalog, Program;
        public uint Pending, Items;
        public ulong Committed, RolledBack, ProgramUpdates;
        public fixed byte Detail[MessageMax];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<ulong, Request*, delegate* unmanaged[Cdecl]<Result*, void*, void>, void*, ulong*, int> Give;
        public delegate* unmanaged[Cdecl]<ulong, Request*, delegate* unmanaged[Cdecl]<Result*, void*, void>, void*, ulong*, int> Take;
        public delegate* unmanaged[Cdecl]<ulong, int> Cancel;
        public delegate* unmanaged[Cdecl]<ulong, void> CancelOwner;
        public delegate* unmanaged[Cdecl]<uint, Inventory*, Slot*, uint, uint*, int> Read;
        public delegate* unmanaged[Cdecl]<uint, uint*, uint, uint*, int> FindInventories;
        public delegate* unmanaged[Cdecl]<uint> ItemCount;
        public delegate* unmanaged[Cdecl]<uint, ItemInfo*, int> ItemAt;
        public delegate* unmanaged[Cdecl]<uint, ItemInfo*, int> ItemInfoOf;
        public delegate* unmanaged[Cdecl]<NativeStr, uint> FindItem;
        public delegate* unmanaged[Cdecl]<byte*, uint> IdOfGuid;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
    }

    /// <summary>The game's ItemId: FNV-1a-32 over the item GUID's 16 bytes.</summary>
    public static uint IdOf(ReadOnlySpan<byte> guid)
    {
        uint h = 0x811c9dc5;
        foreach (var b in guid) h = (h ^ b) * 0x01000193;
        return h;
    }
}

/// <summary>The C# functions behind the fake <c>kiln.items</c> table (v1): give, take, cancel, read, find_inventories,
/// the item catalog and status.</summary>
internal static unsafe class ItemsTable
{
    private static ItemsAbi.Table* _table;

    public static nint Create()
    {
        if (_table != null) return (nint)_table;
        var t = (ItemsAbi.Table*)NativeMemory.AllocZeroed((nuint)sizeof(ItemsAbi.Table));  // process lifetime, never freed
        t->Header = new ServiceHeader { Size = (uint)sizeof(ItemsAbi.Table), Version = ItemsAbi.Version };
        t->Give = &Give;
        t->Take = &Take;
        t->Cancel = &Cancel;
        t->CancelOwner = &CancelOwner;
        t->Read = &Read;
        t->FindInventories = &FindInventories;
        t->ItemCount = &ItemCount;
        t->ItemAt = &ItemAt;
        t->ItemInfoOf = &ItemInfoOf;
        t->FindItem = &FindItem;
        t->IdOfGuid = &IdOfGuid;
        t->GetStatus = &GetStatus;
        _table = t;
        return (nint)t;
    }

    private static ItemsFake? Fake => FakeTables.Active(ItemsAbi.Service)?.ItemsFake;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Give(ulong owner, ItemsAbi.Request* request, delegate* unmanaged[Cdecl]<ItemsAbi.Result*, void*, void> done, void* user, ulong* ticket) =>
        Submit(false, owner, request, done, user, ticket);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Take(ulong owner, ItemsAbi.Request* request, delegate* unmanaged[Cdecl]<ItemsAbi.Result*, void*, void> done, void* user, ulong* ticket) =>
        Submit(true, owner, request, done, user, ticket);

    private static int Submit(bool take, ulong owner, ItemsAbi.Request* request, delegate* unmanaged[Cdecl]<ItemsAbi.Result*, void*, void> done, void* user,
        ulong* ticket)
    {
        try
        {
            if (ticket != null) *ticket = 0;
            if (request == null || request->Size < (uint)sizeof(ItemsAbi.Request)) return FakeTables.InvalidArgument;
            if (Fake is not { } f) return FakeTables.Unavailable;
            if (!FakeTables.OnTickThread) return FakeTables.WrongThread;
            return f.Submit(take, owner, *request, (nint)done, (nint)user, ticket);
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Cancel(ulong ticket)
    {
        try
        {
            return Fake?.Cancel(ticket) == true ? FakeTables.Ok : FakeTables.NotFound;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CancelOwner(ulong owner)
    {
        try
        {
            Fake?.CancelOwner(owner);
        }
        catch
        {
            // never across into native code
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Read(uint entity, ItemsAbi.Inventory* info, ItemsAbi.Slot* slots, uint capacity, uint* count)
    {
        try
        {
            if (info == null || info->Size < (uint)sizeof(ItemsAbi.Inventory) || count == null || (slots == null && capacity > 0))
                return FakeTables.InvalidArgument;
            *count = 0;
            if (Fake is not { } f || FakeTables.Off(ItemsFake.ReadFeature)) return FakeTables.Unavailable;
            if (!FakeTables.OnTickThread) return FakeTables.WrongThread;
            return f.Read(entity, info, slots, capacity, count);
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FindInventories(uint flags, uint* out_, uint capacity, uint* count)
    {
        try
        {
            if (count == null || (out_ == null && capacity > 0) || (flags & ~0xFu) != 0) return FakeTables.InvalidArgument;
            *count = 0;
            if (Fake is not { } f || FakeTables.Off(ItemsFake.ReadFeature)) return FakeTables.Unavailable;
            if (!FakeTables.OnTickThread) return FakeTables.WrongThread;
            var found = f.Find(flags);
            *count = (uint)found.Count;
            for (var i = 0; i < found.Count && i < capacity; i++) out_[i] = found[i];
            return found.Count > capacity ? FakeTables.BufferTooSmall : FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint ItemCount()
    {
        try
        {
            return (uint)(Fake?.Catalog.Length ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ItemAt(uint index, ItemsAbi.ItemInfo* out_)
    {
        try
        {
            if (out_ == null || out_->Size < (uint)sizeof(ItemsAbi.ItemInfo)) return FakeTables.InvalidArgument;
            var all = Fake?.Catalog ?? [];
            if (index >= all.Length) return FakeTables.NotFound;
            ItemsFake.Fill(all[index], out_);
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ItemInfoOf(uint id, ItemsAbi.ItemInfo* out_)
    {
        try
        {
            if (out_ == null || out_->Size < (uint)sizeof(ItemsAbi.ItemInfo)) return FakeTables.InvalidArgument;
            if (Fake?.Item(id) is not { } item) return FakeTables.NotFound;
            ItemsFake.Fill(item, out_);
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint FindItem(NativeStr name)
    {
        try
        {
            var all = Fake?.Catalog ?? [];
            var n = name.ToString();
            return (all.FirstOrDefault(i => i.Name == n) ?? all.FirstOrDefault(i => string.Equals(i.Name, n, StringComparison.OrdinalIgnoreCase)))?.Id ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint IdOfGuid(byte* guid) => guid == null ? 0 : ItemsAbi.IdOf(new ReadOnlySpan<byte>(guid, 16));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetStatus(ItemsAbi.Status* out_)
    {
        try
        {
            if (out_ == null || out_->Size < (uint)sizeof(ItemsAbi.Status)) return FakeTables.InvalidArgument;
            var s = new ItemsAbi.Status { Size = (uint)sizeof(ItemsAbi.Status) };
            if (Fake is { } f) f.Fill(ref s);
            else SpawnAbi.CopyText("no server runs in this process now (the test kit's harness is gone)", s.Detail, ItemsAbi.MessageMax);
            *out_ = s;
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }
}

/// <summary>An item the fake <c>kiln.items</c> registry has (the test added it: the kit does not know the game's).</summary>
internal sealed record FakeItem(uint Id, Guid Guid, string Name, int MaxStack, ItemCategory Category, ItemRarity Rarity, bool Gear, int LevelMin,
    int LevelMax, nint NameUtf8, int NameLength);

/// <summary>Item names as UTF-8 for <c>KilnItemInfo.name</c>, which the header promises stay valid for the process: each
/// distinct name is allocated once per process and never freed (a test run keeps a few dozen bytes per name it used).</summary>
internal static unsafe class ItemNames
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, (nint Ptr, int Length)> Interned = new(StringComparer.Ordinal);

    public static (nint Ptr, int Length) Of(string name)
    {
        lock (Lock)
        {
            if (Interned.TryGetValue(name, out var have)) return have;
            var bytes = Encoding.UTF8.GetBytes(name);
            var p = (byte*)NativeMemory.Alloc((nuint)Math.Max(bytes.Length, 1));  // process lifetime, never freed
            bytes.CopyTo(new Span<byte>(p, bytes.Length));
            return Interned[name] = ((nint)p, bytes.Length);
        }
    }

    /// <summary>The bytes kept now (the names and their lengths).</summary>
    public static long Bytes
    {
        get
        {
            lock (Lock) return Interned.Values.Sum(v => (long)Math.Max(v.Length, 1));
        }
    }
}

/// <summary>One harness's kiln.items: the item registry the test made, an inventory per fake player and per container,
/// and the give and take requests mods made, run at the start of the next step.</summary>
internal sealed unsafe class ItemsFake
{
    // The sigdb features of the v1 table the kit models, and those of the later tables it does not.
    public const string TransactionFeature = "items.transaction", ReadFeature = "items.read", ProgramFeature = "items.program";

    public static readonly string[] Features = [TransactionFeature, ReadFeature, ProgramFeature];

    public static readonly string[] OfflineRows = ["items.fuel", "items.move", "items.names", "items.transfer", "items.moves", "items.instances",
        "items.equip", "items.pide_options", "items.result_peek", "items.recipe"];

    internal sealed class Stack
    {
        public uint Item, Pide;
        public int Count;
        public ushort Level;
        public byte Rarity;
    }

    internal sealed class Inv
    {
        public required uint Owner;
        public required bool IsPlayer;
        public required List<uint> Links;  // the 8-slot inventories, each an entity; a container's first is itself
        public required Stack[] Slots;
        public FakePlayer? Player;

        public ulong SlotId(int i) => Links[i / 8] | ((ulong)(uint)(i % 8) << 32);

        public int IndexOf(ulong slot)
        {
            var link = Links.IndexOf((uint)(slot & 0xFFFFFFFF));
            var index = (int)((slot >> 32) & 0xFF);
            return link < 0 || index >= 8 ? -1 : link * 8 + index;
        }
    }

    private sealed class Job
    {
        public required bool Take;
        public required ulong Ticket, Owner, Step;
        public required ItemsAbi.Request Request;
        public required nint User;
        public required nint Done;  // 0 once canceled: the transaction still runs, nobody is told
        public required string? Mod;
    }

    private static long _tickets;  // per process: tickets never repeat

    private readonly KilnHarness _harness;
    private readonly object _lock = new();
    private readonly List<FakeItem> _items = [];
    private FakeItem[] _catalog = [];
    private readonly Dictionary<uint, Inv> _inventories = [];
    private readonly List<Job> _pending = [];
    private readonly List<FakeItemsCall> _calls = [];
    private ulong _committed, _rolledBack, _updates;

    public ItemsFake(KilnHarness harness)
    {
        _harness = harness;
        ResetCatalogCache();
    }

    /// <summary>The 8-slot inventories a fake player's inventory gets when it is first used.</summary>
    public int PlayerInventories { get; set; } = 5;

    // Kiln.Items keeps the catalog it read for the process while the item count stays the same (and an empty registry
    // keeps the last one): the next harness's registry is another, so the cache is dropped (reflection: the cache is
    // private to Kiln.Items; a Kiln.Items without these fields has nothing to drop).
    private static void ResetCatalogCache()
    {
        try
        {
            var catalog = typeof(Items).Assembly.GetType("Kiln.ItemCatalog");
            if (catalog is null) return;
            foreach (var field in catalog.GetFields(BindingFlags.Static | BindingFlags.NonPublic))
            {
                if (field.Name == "_loadedCount") field.SetValue(null, 0u);
                else if (field.Name == "_all") field.SetValue(null, Array.Empty<ItemInfo>());
                else if (field.Name.StartsWith("_by", StringComparison.Ordinal) && field.GetValue(null) is System.Collections.IDictionary d) d.Clear();
            }
        }
        catch (Exception e) when (e is ArgumentException or FieldAccessException or TargetException or NotSupportedException)
        {
            // a cache of another shape: nothing to drop
        }
    }

    public void Ended()
    {
        ResetCatalogCache();
        lock (_lock)
        {
            _items.Clear();  // the names stay: KilnItemInfo.name is valid for the process (ItemNames)
            _catalog = [];
        }
    }

    // ---- the catalog ----

    public FakeItem[] Catalog
    {
        get
        {
            lock (_lock) return _catalog;
        }
    }

    public FakeItem? Item(uint id)
    {
        lock (_lock) return _items.FirstOrDefault(i => i.Id == id);
    }

    public FakeItem AddItem(string name, int maxStack, ItemCategory category, ItemRarity rarity, bool gear, int levelMin, int levelMax, Guid guid)
    {
        if (guid == Guid.Empty)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("kiln-testkit-item:" + name));
            hash[7] = (byte)((hash[7] & 0x0F) | 0x80);
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
            guid = new Guid(hash.AsSpan(0, 16));
        }
        var id = ItemsAbi.IdOf(guid.ToByteArray());
        lock (_lock)
        {
            foreach (var i in _items)
            {
                if (i.Id == id) throw new ArgumentException($"the game has item {id:x8} already ({i.Name})", nameof(guid));
                if (i.Name == name) throw new ArgumentException($"the game has an item {name} already", nameof(name));
            }
            var (utf8, length) = ItemNames.Of(name);
            var item = new FakeItem(id, guid, name, gear ? 1 : maxStack, category, rarity, gear, levelMin, levelMax, utf8, length);
            _items.Add(item);
            _catalog = [.. _items.OrderBy(i => i.Name, StringComparer.Ordinal)];
            return item;
        }
    }

    public static void Fill(FakeItem i, ItemsAbi.ItemInfo* out_)
    {
        var size = out_->Size;
        *out_ = default;
        out_->Size = size;
        out_->Id = i.Id;
        i.Guid.TryWriteBytes(new Span<byte>(out_->Guid, 16));
        out_->MaxStack = (ushort)i.MaxStack;
        out_->Category = (byte)i.Category;
        out_->Rarity = (byte)i.Rarity;
        out_->ItemFlags = i.Gear ? ItemsAbi.ItemFlagHasPide : 0;
        out_->LevelMin = (uint)i.LevelMin;
        out_->LevelMax = (uint)i.LevelMax;
        out_->Gear = i.Gear ? (byte)1 : (byte)0;
        out_->Name = new NativeStr { Ptr = (byte*)i.NameUtf8, Len = (nuint)i.NameLength };
    }

    // ---- inventories (tick thread) ----

    private EntitiesFake World => _harness.EntitiesFake ?? throw new InvalidOperationException("the harness has no world yet");

    public Inv AddContainer(uint entity, int inventories)
    {
        if (!World.Exists(entity)) throw new ArgumentException($"entity {entity} is not in the fake world (make it with kiln.Entities.Create)", nameof(entity));
        lock (_lock)
        {
            if (_inventories.ContainsKey(entity)) throw new ArgumentException($"entity {entity} has an inventory already", nameof(entity));
            var links = new List<uint> { entity };
            for (var i = 1; i < inventories; i++) links.Add(World.Create(null));  // each further 8-slot inventory is an entity of its own
            var inv = new Inv { Owner = entity, IsPlayer = false, Links = links, Slots = NewSlots(inventories) };
            _inventories[entity] = inv;
            return inv;
        }
    }

    private static Stack[] NewSlots(int inventories)
    {
        var slots = new Stack[inventories * 8];
        for (var i = 0; i < slots.Length; i++) slots[i] = new Stack();
        return slots;
    }

    /// <summary>The inventory of an entity: a container's, or an in-game fake player's (made at its first use); null
    /// when the entity has none.</summary>
    public Inv? Of(uint entity)
    {
        lock (_lock)
        {
            if (_inventories.TryGetValue(entity, out var inv))
            {
                if (inv.Player is null) return World.Exists(entity) ? inv : null;
                if (inv.Player.InGame && inv.Player.Entity.Value == entity) return inv;
                _inventories.Remove(entity);  // the player who held the slot left: the next one starts with their own
            }
        }
        return PlayerAt(entity) is { } player ? OfPlayer(player) : null;
    }

    private FakePlayer? PlayerAt(uint entity) =>
        _harness.PlayersFake?.Present.FirstOrDefault(p => p.InGame && p.Entity.Value == entity);

    public Inv OfPlayer(FakePlayer player)
    {
        lock (_lock)
        {
            var e = player.Entity.Value;
            if (_inventories.TryGetValue(e, out var inv) && ReferenceEquals(inv.Player, player)) return inv;
            var links = new List<uint>();
            for (var i = 0; i < PlayerInventories; i++) links.Add(World.Create(null));
            inv = new Inv { Owner = e, IsPlayer = true, Links = links, Slots = NewSlots(PlayerInventories), Player = player };
            _inventories[e] = inv;
            return inv;
        }
    }

    public List<uint> Find(uint flags)
    {
        var found = new List<uint>();
        foreach (var p in _harness.PlayersFake?.Present ?? [])
            if (p.InGame && ((flags & ItemsAbi.FindPlayers) != 0 || (flags & 7u) == 0)) OfPlayer(p);
        lock (_lock)
        {
            foreach (var (e, inv) in _inventories.OrderBy(kv => kv.Key))
            {
                if (Of(e) is null) continue;
                var kind = inv.IsPlayer ? ItemsAbi.FindPlayers : ItemsAbi.FindContainers;
                if ((flags & 7u) != 0 && (flags & kind) == 0) continue;  // no kind bit: every kind
                if ((flags & ItemsAbi.FindNonEmpty) != 0 && inv.Slots.All(s => s.Count == 0)) continue;
                found.Add(e);
            }
        }
        return found;
    }

    public int Read(uint entity, ItemsAbi.Inventory* info, ItemsAbi.Slot* slots, uint capacity, uint* count)
    {
        if (Of(entity) is not { } inv) return FakeTables.NotFound;
        lock (_lock)
        {
            var size = info->Size;
            *info = default;
            info->Size = size;
            info->Entity = entity;
            info->Initialized = 1;
            info->SetupFlags = inv.IsPlayer ? ItemsAbi.SetupIsPlayer : (byte)0;
            info->GenericSlots = (byte)Math.Min(inv.Slots.Length, 255);
            info->AvailableSlots = (byte)Math.Min(inv.Slots.Length, 255);
            info->InventoryCount = (uint)inv.Links.Count;
            info->SlotTotal = (uint)inv.Slots.Length;
            info->UsedSlots = (uint)inv.Slots.Count(s => s.Count > 0);
            for (var i = 0; i < inv.Links.Count && i < ItemsAbi.MaxInventories; i++)
            {
                info->Inventories[i] = inv.Links[i];
                info->Categories[i] = ItemsAbi.CategoryGeneric;
            }
            var n = Math.Min((uint)inv.Slots.Length, capacity);
            for (var i = 0; i < n; i++)
            {
                var s = inv.Slots[i];
                var item = s.Count > 0 ? Item(s.Item) : null;
                slots[i] = new ItemsAbi.Slot
                {
                    Id = inv.SlotId(i),
                    Item = s.Count > 0 ? s.Item : 0,
                    Count = (uint)s.Count,
                    Pide = s.Pide,
                    Level = s.Level,
                    Rarity = s.Count == 0 ? (byte)0xFF : s.Pide != 0 ? s.Rarity : (byte)(item?.Rarity ?? 0),
                    Flags = s.Pide != 0 ? ItemsAbi.SlotGear : (byte)0,
                    Link = (byte)(i / 8),
                    Category = ItemsAbi.CategoryGeneric,
                };
            }
            *count = n;
            return inv.Slots.Length > capacity ? FakeTables.BufferTooSmall : FakeTables.Ok;
        }
    }

    // ---- requests ----

    public int Submit(bool take, ulong owner, ItemsAbi.Request request, nint done, nint user, ulong* ticket)
    {
        // items.cpp submit(), in its order
        if (request.Entity == 0 || request.Item == 0 || request.Count is 0 or > 100000) return FakeTables.InvalidArgument;
        var allowed = take ? ItemsAbi.ExactSlot | ItemsAbi.DryRun
            : ItemsAbi.Level | ItemsAbi.Rarity | ItemsAbi.AllOrNothing | ItemsAbi.Notify | ItemsAbi.NoMerge | ItemsAbi.DryRun | ItemsAbi.Recipe |
              ItemsAbi.Unlock | ItemsAbi.GemSlot;
        if ((request.Flags & ~allowed) != 0) return FakeTables.InvalidArgument;
        if ((request.Flags & (ItemsAbi.Unlock | ItemsAbi.GemSlot)) != 0 && (request.Flags & ItemsAbi.Recipe) != 0) return FakeTables.InvalidArgument;
        if ((request.Flags & ItemsAbi.Unlock) != 0 ? request.Unlock is not (>= 1 and <= 5 or 255) : request.Unlock != 0) return FakeTables.InvalidArgument;
        // upgrades and gem slots need feature items.pide_options, which the kit does not model
        if ((request.Flags & (ItemsAbi.Unlock | ItemsAbi.GemSlot)) != 0) return FakeTables.Unavailable;
        if ((request.Flags & ItemsAbi.Recipe) != 0 && ((request.Flags & (ItemsAbi.Level | ItemsAbi.Rarity)) != 0 || request.Slot != 0)) return FakeTables.InvalidArgument;
        if ((request.Flags & ItemsAbi.Rarity) != 0 && request.Rarity > 4) return FakeTables.InvalidArgument;
        if ((request.Flags & ItemsAbi.Level) != 0 && request.Level is 0 or > 255) return FakeTables.InvalidArgument;
        if (request.Slot != 0 && ((request.Slot >> 40) != 0 || ((request.Slot >> 32) & 0xFF) > 7 || (uint)request.Slot == 0)) return FakeTables.InvalidArgument;
        if ((request.Flags & ItemsAbi.ExactSlot) != 0 && request.Slot == 0) return FakeTables.InvalidArgument;
        if (FakeTables.Off(TransactionFeature) || Program is ItemsAbi.ProgramNone or ItemsAbi.ProgramBroken) return FakeTables.Unavailable;
        var job = new Job
        {
            Take = take, Ticket = (ulong)Interlocked.Increment(ref _tickets), Owner = owner, Step = _harness.CurrentStep, Request = request,
            Done = done, User = user, Mod = HostAccess.Current?.Info.Id,
        };
        lock (_lock)
        {
            if (_pending.Count >= MaxPending) return FakeTables.Unavailable;
            _pending.Add(job);
        }
        if (ticket != null) *ticket = job.Ticket;
        return FakeTables.Ok;
    }

    // A cancel drops the callback, not the transaction (items.cpp t_cancel and t_cancel_owner erase g_callbacks only):
    // the request still runs at the next step, and nobody is told.
    public bool Cancel(ulong ticket)
    {
        lock (_lock)
        {
            var job = _pending.FirstOrDefault(j => j.Ticket == ticket && j.Done != 0);
            if (job is null) return false;
            job.Done = 0;
            return true;
        }
    }

    public void CancelOwner(ulong owner)
    {
        lock (_lock)
            foreach (var job in _pending.Where(j => j.Owner == owner)) job.Done = 0;
    }

    private const int MaxPending = 1024;  // items.cpp kMaxPending

    /// <summary>KILN_ITEMS_PROGRAM_*: pending until the server world stepped, then running; none when switched off.</summary>
    public int Program =>
        FakeTables.Off(ProgramFeature) ? ItemsAbi.ProgramNone
        : _harness.State is HarnessState.Created ? ItemsAbi.ProgramPending
        : ItemsAbi.ProgramRunning;

    public IReadOnlyList<FakeItemsCall> Calls
    {
        get
        {
            lock (_lock) return [.. _calls];
        }
    }

    public void Fill(ref ItemsAbi.Status s)
    {
        lock (_lock)
        {
            s.Transactions = FakeTables.Off(TransactionFeature) ? (byte)0 : (byte)1;
            s.Read = FakeTables.Off(ReadFeature) ? (byte)0 : (byte)1;
            s.Catalog = 1;
            s.Program = (byte)Program;
            s.Pending = (uint)_pending.Count;
            s.Items = (uint)_items.Count;
            s.Committed = _committed;
            s.RolledBack = _rolledBack;
            s.ProgramUpdates = _updates;
        }
    }

    /// <summary>The service's program at the start of a step: every request made before it runs as one transaction, in
    /// order, and its callback is called (the game's: in the next sim step's Pre).</summary>
    public void BeforeStep(ulong step)
    {
        List<Job> jobs;
        lock (_lock)
        {
            _updates++;
            if (_pending.Count == 0) return;
            jobs = [.. _pending];
            _pending.Clear();
        }
        foreach (var job in jobs)
        {
            var r = new ItemsAbi.Result
            {
                Size = (uint)sizeof(ItemsAbi.Result), Ticket = job.Ticket, Entity = job.Request.Entity, Item = job.Request.Item,
                Requested = job.Request.Count, Flags = job.Request.Flags, Steps = (uint)Math.Max(0L, (long)step - (long)job.Step - 1),
            };
            var changes = new List<ItemsAbi.Change>();
            string message;
            if (FakeTables.Off(TransactionFeature) || Program != ItemsAbi.ProgramRunning)
            {
                r.Status = ItemsAbi.StatusUnavailable;
                message = "give/take were switched off since the request was accepted; nothing changed";
            }
            else if (!job.Take && (job.Request.Flags & ItemsAbi.Recipe) != 0)
            {
                r.Status = ItemsAbi.StatusUnavailable;
                message = "recipe results (items.recipe) are not modelled by the test kit";
            }
            else (r.Status, r.Done, r.GameError, message) = job.Take ? RunTake(job.Request, changes) : RunGive(job.Request, changes);
            var dry = (job.Request.Flags & ItemsAbi.DryRun) != 0;
            lock (_lock)
            {
                if (r.Status is ItemsAbi.Done or ItemsAbi.Partial && !dry) _committed++;
                else _rolledBack++;
                _calls.Add(new FakeItemsCall(step, job.Mod, job.Take, new EntityId(job.Request.Entity), new ItemId(job.Request.Item),
                    (int)job.Request.Count, (int)r.Done, (ItemsStatus)r.Status, dry));
            }
            r.ChangeCount = (uint)Math.Min(changes.Count, ItemsAbi.MaxChanges);
            var c = (ItemsAbi.Change*)r.Changes;
            for (var i = 0; i < r.ChangeCount; i++) c[i] = changes[i];
            SpawnAbi.CopyText(message, r.Message, ItemsAbi.MessageMax);
            if (job.Done != 0) ((delegate* unmanaged[Cdecl]<ItemsAbi.Result*, void*, void>)job.Done)(&r, (void*)job.User);
        }
    }

    private const uint ErrorNoItem = 0x15, ErrorNoSetup = 0x16;

    private (int Status, uint Done, uint GameError, string Message) RunGive(ItemsAbi.Request q, List<ItemsAbi.Change> changes)
    {
        if (Of(q.Entity) is not { } inv) return (ItemsAbi.NoInventory, 0, ErrorNoSetup, $"entity {q.Entity} has no inventory");
        if (Item(q.Item) is not { } item) return (ItemsAbi.UnknownItem, 0, ErrorNoItem, $"item 0x{q.Item:x8} is not in the game's item registry");
        var dry = (q.Flags & ItemsAbi.DryRun) != 0;
        var all = (q.Flags & ItemsAbi.AllOrNothing) != 0;
        var noMerge = (q.Flags & ItemsAbi.NoMerge) != 0;
        lock (_lock)
        {
            // plan on a copy: the slots change only when the transaction commits
            var plan = new List<(int Index, int Amount)>();
            var room = inv.Slots.Select(s => (s.Item, s.Count)).ToArray();
            var left = (int)q.Count;
            var hint = q.Slot != 0 ? inv.IndexOf(q.Slot) : -1;
            if (q.Slot != 0 && hint < 0) return (ItemsAbi.SlotChanged, 0, 0, $"slot {q.Slot:x} is not one of entity {q.Entity}'s");
            void Fill(int i)
            {
                if (left == 0) return;
                var (have, n) = room[i];
                if (n > 0 && (have != item.Id || item.Gear)) return;
                if (n > 0 && noMerge) return;
                var put = Math.Min(left, item.MaxStack - n);
                if (put <= 0) return;
                room[i] = (item.Id, n + put);
                plan.Add((i, put));
                left -= put;
            }
            if (hint >= 0) Fill(hint);
            if (!item.Gear && !noMerge)
                for (var i = 0; i < room.Length && left > 0; i++)
                    if (room[i].Count > 0 && room[i].Item == item.Id) Fill(i);
            for (var i = 0; i < room.Length && left > 0; i++)
                if (room[i].Count == 0) Fill(i);
            var done = (int)q.Count - left;
            if (done == 0 || (all && left > 0))
                return (ItemsAbi.NoRoom, 0, 0, $"no room: {(done == 0 ? "nothing" : $"only {done} of {q.Count}")} fit in entity {q.Entity}'s inventory");
            var level = (ushort)(item.Gear ? Clamp((q.Flags & ItemsAbi.Level) != 0 ? (int)q.Level : item.LevelMin, item.LevelMin, item.LevelMax) : 0);
            var rarity = (q.Flags & ItemsAbi.Rarity) != 0 ? q.Rarity : (byte)item.Rarity;
            foreach (var (i, amount) in plan)
            {
                var pide = item.Gear && !dry ? World.Create(null) : 0u;  // gear: an item entity of its own, created at the commit
                changes.Add(new ItemsAbi.Change { Slot = inv.SlotId(i), Item = item.Id, Amount = (uint)amount, Pide = pide });
                if (dry) continue;
                var s = inv.Slots[i];
                s.Item = item.Id;
                s.Count += amount;
                if (item.Gear)
                {
                    s.Pide = pide;
                    s.Level = level;
                    s.Rarity = rarity;
                }
            }
            return left > 0
                ? (ItemsAbi.Partial, (uint)done, 0u, $"only {done} of {q.Count} fit in entity {q.Entity}'s inventory; the rest was never created")
                : (ItemsAbi.Done, (uint)done, 0u, "");
        }
    }

    private static int Clamp(int level, int min, int max) => max > 0 ? Math.Clamp(level, Math.Max(min, 1), max) : Math.Max(level, 0);

    private (int Status, uint Done, uint GameError, string Message) RunTake(ItemsAbi.Request q, List<ItemsAbi.Change> changes)
    {
        if (Of(q.Entity) is not { } inv) return (ItemsAbi.NoInventory, 0, ErrorNoSetup, $"entity {q.Entity} has no inventory");
        if (Item(q.Item) is null) return (ItemsAbi.UnknownItem, 0, ErrorNoItem, $"item 0x{q.Item:x8} is not in the game's item registry");
        var dry = (q.Flags & ItemsAbi.DryRun) != 0;
        lock (_lock)
        {
            var order = new List<int>();
            if ((q.Flags & ItemsAbi.ExactSlot) != 0)
            {
                var i = inv.IndexOf(q.Slot);
                if (i < 0 || inv.Slots[i].Count == 0 || inv.Slots[i].Item != q.Item || (q.Pide != 0 && inv.Slots[i].Pide != q.Pide))
                    return (ItemsAbi.SlotChanged, 0, 0, $"slot {q.Slot:x} no longer holds item 0x{q.Item:x8}");
                order.Add(i);
            }
            else
            {
                var hint = q.Slot != 0 ? inv.IndexOf(q.Slot) : -1;
                if (hint >= 0) order.Add(hint);
                for (var i = 0; i < inv.Slots.Length; i++)
                    if (i != hint) order.Add(i);
            }
            var there = order.Where(i => inv.Slots[i].Count > 0 && inv.Slots[i].Item == q.Item).Sum(i => inv.Slots[i].Count);
            if (there < q.Count) return (ItemsAbi.NotEnough, 0, 0, $"only {there} of {q.Count} are there; nothing was taken");
            var left = (int)q.Count;
            foreach (var i in order)
            {
                var s = inv.Slots[i];
                if (left == 0) break;
                if (s.Count == 0 || s.Item != q.Item) continue;
                var took = Math.Min(left, s.Count);
                changes.Add(new ItemsAbi.Change { Slot = inv.SlotId(i), Item = s.Item, Amount = (uint)took, Pide = s.Pide });
                left -= took;
                if (dry) continue;
                s.Count -= took;
                if (s.Count > 0) continue;
                if (s.Pide != 0) World.Destroy(s.Pide);  // gear taken out is destroyed by the game
                s.Item = 0;
                s.Pide = 0;
                s.Level = 0;
                s.Rarity = 0;
            }
            return (ItemsAbi.Done, q.Count, 0, "");
        }
    }

    // ---- the test's hand: as if the game had changed the inventory (no transaction, no event) ----

    public int Put(Inv inv, FakeItem item, int count)
    {
        lock (_lock)
        {
            var left = count;
            for (var pass = 0; pass < 2 && left > 0; pass++)
                for (var i = 0; i < inv.Slots.Length && left > 0; i++)
                {
                    var s = inv.Slots[i];
                    if (pass == 0 ? s.Count == 0 || s.Item != item.Id || item.Gear : s.Count != 0) continue;
                    var put = Math.Min(left, item.MaxStack - s.Count);
                    if (put <= 0) continue;
                    s.Item = item.Id;
                    s.Count += put;
                    if (item.Gear)
                    {
                        s.Pide = World.Create(null);
                        s.Level = (ushort)Math.Max(item.LevelMin, 0);
                        s.Rarity = (byte)item.Rarity;
                    }
                    left -= put;
                }
            return count - left;
        }
    }

    public void Clear(Inv inv)
    {
        lock (_lock)
            foreach (var s in inv.Slots)
            {
                if (s.Pide != 0) World.Destroy(s.Pide);
                s.Item = 0;
                s.Count = 0;
                s.Pide = 0;
                s.Level = 0;
                s.Rarity = 0;
            }
    }

    public object Lock => _lock;
}
