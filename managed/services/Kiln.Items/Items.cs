// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

/// <summary>
/// Items in inventories: give and take (stacks and gear with a level and rarity), read an entity's inventory, and the
/// game's item catalog. Managed face of the native service kiln.items (native/core/abi/services/kiln_items.h).
/// </summary>
/// <remarks>
/// Give and take go through the game's own inventory transaction, inside the service's ECS program during the next
/// sim step: the returned task completes on the tick thread one or two steps later (40-80 ms on a dedicated server).
/// Read, FindInventories and the catalog are synchronous. Call everything on the tick thread (mod code runs there).
/// </remarks>
public static unsafe partial class Items
{
    private static ItemsNative.Table* _table;

    // Looked up until found (the table lives for the whole process).
    private static ItemsNative.Table* T =>
        _table != null ? _table : (_table = Services.Get<ItemsNative.Table>(ItemsNative.Service, ItemsNative.Version));

    /// <summary>True when the native service is loaded.</summary>
    public static bool Available => T != null;

    /// <summary>True when give/take can run (the service's program runs and its checks passed).</summary>
    public static bool CanTransact => Status()?.Transactions == true;

    /// <summary>The service's state, or null when it is not loaded.</summary>
    public static ItemsServiceStatus? Status()
    {
        if (T == null) return null;
        ItemsNative.Status s = default;
        s.Size = (uint)sizeof(ItemsNative.Status);
        if (T->GetStatus(&s) != ItemsNative.Ok) return null;
        return new ItemsServiceStatus(s.Transactions != 0, s.Read != 0, s.Catalog != 0, s.Program, (int)s.Pending,
            (int)s.Items, s.Committed, s.RolledBack, s.ProgramUpdates, ItemsNative.Text(s.Detail, 160));
    }

    // ------------------------------------------------------------------------------------------- catalog -------

    /// <summary>Every item of the server world's registry, sorted by debug name; empty until the world has loaded its
    /// items (a few seconds after it starts).</summary>
    public static IReadOnlyList<ItemInfo> All => ItemCatalog.Load(T);

    /// <summary>The item with this id, or null.</summary>
    public static ItemInfo? Get(ItemId id) => ItemCatalog.Get(T, id);

    /// <summary>
    /// Finds an item by id ("0x2b0cf1d5", "722268629"), debug name ("Material_T1_Wood_Logs") or display name
    /// ("Wood Logs"), exact match first, then ignoring case. Returns null when nothing matches. Display names come
    /// from the dashboard catalog shipped with Kiln; when several items share one, a non-blueprint wins.
    /// </summary>
    public static ItemInfo? Lookup(string nameOrId) => ItemCatalog.Lookup(T, nameOrId);

    /// <summary>Items whose debug or display name contains <paramref name="text"/> (ignoring case).</summary>
    public static IReadOnlyList<ItemInfo> Search(string text, int max = 25) => ItemCatalog.Search(T, text, max);

    // ------------------------------------------------------------------------------------------- reading -------

    /// <summary>The inventory of <paramref name="entity"/>, or null when it has none (or it does not exist).</summary>
    public static InventoryContents? Read(EntityId entity)
    {
        var t = Require("Items.Read");
        const int cap = 32 * 8;
        var slots = stackalloc ItemsNative.Slot[cap];
        ItemsNative.Inventory info = default;
        info.Size = (uint)sizeof(ItemsNative.Inventory);
        uint n = 0;
        var rc = t->Read(entity.Value, &info, slots, cap, &n);
        if (rc == ItemsNative.NotFound) return null;
        if (rc != ItemsNative.Ok && rc != ItemsNative.BufferTooSmall) throw Failure("Items.Read", rc);
        var invs = new List<(EntityId, InventoryCategory)>((int)info.InventoryCount);
        for (var i = 0; i < info.InventoryCount && i < 32; i++)
            invs.Add((new EntityId(info.Inventories[i]), (InventoryCategory)info.Categories[i]));
        var list = new List<InventorySlot>((int)n);
        for (var i = 0; i < n; i++)
        {
            ref var s = ref slots[i];
            list.Add(new InventorySlot(new SlotId(s.Id), new ItemId(s.Item), (int)s.Count, new EntityId(s.Pide), s.Level,
                s.Rarity <= 4 ? (ItemRarity)s.Rarity : null, (InventoryCategory)s.Category, s.Link,
                (s.Flags & 0x04) != 0, (s.Flags & 0x01) != 0, (s.Flags & 0x02) != 0, s.State));
        }
        return new InventoryContents(entity, info.Initialized != 0, info.SetupFlags, info.GenericSlots,
            info.AvailableSlots, invs, list);
    }

    /// <summary>Entities with an initialized inventory of these kinds (one pass over the world's entities).</summary>
    public static IReadOnlyList<EntityId> FindInventories(InventoryKinds kinds = InventoryKinds.Containers)
    {
        var t = Require("Items.FindInventories");
        var cap = 4096u;
        while (true)
        {
            var buf = new uint[cap];
            uint n;
            int rc;
            fixed (uint* p = buf) rc = t->FindInventories((uint)kinds, p, cap, &n);
            if (rc == ItemsNative.BufferTooSmall && cap < (1u << 20)) { cap = Math.Max(cap * 2, n + 64); continue; }
            if (rc != ItemsNative.Ok && rc != ItemsNative.BufferTooSmall) throw Failure("Items.FindInventories", rc);
            var list = new List<EntityId>((int)Math.Min(n, cap));
            for (var i = 0; i < n && i < cap; i++) list.Add(new EntityId(buf[i]));
            return list;
        }
    }

    // ------------------------------------------------------------------------------------------- give / take ---

    /// <summary>
    /// Puts <paramref name="count"/> x <paramref name="item"/> into <paramref name="target"/>'s inventory (a player,
    /// chest, villager): stacks are topped up first, then empty slots; gear gets its own item entity with
    /// <paramref name="gear"/>'s level and rarity. Completes on the tick thread with what happened (never throws for
    /// game-side outcomes; check <see cref="ItemsResult.Status"/>).
    /// </summary>
    public static Task<ItemsResult> Give(EntityId target, ItemId item, int count = 1, Gear? gear = null,
        GiveOptions options = GiveOptions.None, SlotId? slot = null)
    {
        var r = NewRequest(target, item, count);
        if (gear is { } g)
        {
            if (g.Level is { } lv)
            {
                if (lv is < 1 or > 255) return Invalid(target, item, count, "level must be 1-255");
                r.Flags |= ItemsNative.Level;
                r.Level = (uint)lv;
            }
            if (g.Rarity is { } ra)
            {
                r.Flags |= ItemsNative.Rarity;
                r.Rarity = (byte)ra;
            }
        }
        if (options.HasFlag(GiveOptions.Notify)) r.Flags |= ItemsNative.Notify;
        if (options.HasFlag(GiveOptions.AllOrNothing)) r.Flags |= ItemsNative.AllOrNothing;
        if (options.HasFlag(GiveOptions.NoMerge)) r.Flags |= ItemsNative.NoMerge;
        if (options.HasFlag(GiveOptions.DryRun)) r.Flags |= ItemsNative.DryRun;
        if (slot is { } s) r.Slot = s.Value;
        return Submit(false, r, "Items.Give");
    }

    /// <summary>
    /// Creates the outputs of a recipe in <paramref name="target"/>'s inventory through the game's own recipe result
    /// function, exactly as crafting creates them (no level or rarity is passed, so an item comes out as the game's
    /// defaults make it). Inputs are not taken and a station's requirements count as met.
    /// <see cref="ItemsResult.Done"/> counts recipe runs,
    /// <see cref="ItemsResult.Changes"/> lists the stacks with their item entities.
    /// </summary>
    public static Task<ItemsResult> GiveRecipeResult(EntityId target, uint recipeId, int count = 1,
        GiveOptions options = GiveOptions.None)
    {
        var r = NewRequest(target, new ItemId(recipeId), count);
        r.Flags |= ItemsNative.Recipe;
        if (options.HasFlag(GiveOptions.Notify)) r.Flags |= ItemsNative.Notify;
        if (options.HasFlag(GiveOptions.NoMerge)) r.Flags |= ItemsNative.NoMerge;
        if (options.HasFlag(GiveOptions.DryRun)) r.Flags |= ItemsNative.DryRun;
        return Submit(false, r, "Items.GiveRecipeResult");
    }

    /// <summary>Give by name or id (see <see cref="Lookup"/>); an unknown name completes with
    /// <see cref="ItemsStatus.UnknownItem"/>.</summary>
    public static Task<ItemsResult> Give(EntityId target, string item, int count = 1, Gear? gear = null,
        GiveOptions options = GiveOptions.None)
    {
        var info = Lookup(item);
        return info == null
            ? Task.FromResult(ItemsResult.Fail(ItemsStatus.UnknownItem, target, ItemId.None, count, $"no item named '{item}'"))
            : Give(target, info.Id, count, gear, options);
    }

    /// <summary>
    /// Takes <paramref name="count"/> x <paramref name="item"/> out of <paramref name="target"/>'s inventory, all or
    /// nothing. <paramref name="from"/> is tried first (players: otherwise their held action-bar slot), then every other
    /// slot (backpack, action bars, equipment); with <paramref name="exactSlot"/> only that slot, and only while it
    /// still holds the item (and <paramref name="itemEntity"/> when given). Gear taken out is destroyed by the game.
    /// </summary>
    public static Task<ItemsResult> Take(EntityId target, ItemId item, int count = 1, SlotId? from = null,
        bool exactSlot = false, EntityId? itemEntity = null, bool dryRun = false)
    {
        var r = NewRequest(target, item, count);
        if (from is { } s) r.Slot = s.Value;
        if (exactSlot)
        {
            if (from is null) return Invalid(target, item, count, "an exact-slot take needs a slot");
            r.Flags |= ItemsNative.ExactSlot;
            if (itemEntity is { } p) r.Pide = p.Value;
        }
        if (dryRun) r.Flags |= ItemsNative.DryRun;
        return Submit(true, r, "Items.Take");
    }

    /// <summary>Takes everything in one slot as read (exact slot, the same item entity for gear).</summary>
    public static Task<ItemsResult> TakeSlot(EntityId target, InventorySlot slot, bool dryRun = false) =>
        slot.IsEmpty
            ? Invalid(target, slot.Item, 0, "the slot is empty")
            : Take(target, slot.Item, slot.ItemEntity.IsNone ? slot.Count : 1, slot.Slot, true,
                slot.ItemEntity.IsNone ? null : slot.ItemEntity, dryRun);

    // ------------------------------------------------------------------------------------------- plumbing ------

    private static ItemsNative.Table* Require(string api)
    {
        Services.RequireTickThread(api);
        var t = T;
        if (t == null) throw new NotSupportedException("kiln.items is unavailable");
        return t;
    }

    private static Exception Failure(string api, int rc) => rc switch
    {
        ItemsNative.WrongThread => new InvalidOperationException($"{api} must be called on the tick thread"),
        ItemsNative.Unavailable => new NotSupportedException($"{api}: kiln.items cannot read the world (yet)"),
        _ => new InvalidOperationException($"{api} failed ({rc})"),
    };

    private static ItemsNative.Request NewRequest(EntityId target, ItemId item, int count) => new()
    {
        Size = (uint)sizeof(ItemsNative.Request),
        Entity = target.Value,
        Item = item.Value,
        Count = count is > 0 and <= 100000 ? (uint)count : 0,
    };

    private static Task<ItemsResult> Invalid(EntityId e, ItemId item, int count, string why) =>
        Task.FromResult(ItemsResult.Fail(ItemsStatus.Invalid, e, item, count, why));

    private sealed class Pending(TaskCompletionSource<ItemsResult> tcs, ModContext? mod)
    {
        public readonly TaskCompletionSource<ItemsResult> Tcs = tcs;
        public readonly ModContext? Mod = mod;
        public ulong Ticket;
        public bool Take;  // for ItemsGivenEvent / ItemsTakenEvent
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<long, Pending> InFlight = new();
    private static readonly HashSet<ModContext> Watched = new();
    private static long _nextKey;

    private static Task<ItemsResult> Submit(bool take, ItemsNative.Request r, string api)
    {
        var t = Require(api);
        if (r.Count == 0) return Invalid(new EntityId(r.Entity), new ItemId(r.Item), 0, "count must be 1-100000");
        if (r.Entity == 0) return Invalid(default, new ItemId(r.Item), (int)r.Count, "no entity");
        if (r.Item == 0) return Invalid(new EntityId(r.Entity), default, (int)r.Count, "no item");
        var mod = ModContext.CurrentMod;
        var tcs = new TaskCompletionSource<ItemsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var p = new Pending(tcs, mod) { Take = take };
        long key;
        lock (Gate)
        {
            key = ++_nextKey;
            InFlight[key] = p;
            if (mod != null && Watched.Add(mod)) mod.Retiring += () => Retire(mod);
        }
        ulong ticket = 0;
        var owner = Services.CallerOwner;
        var rc = take
            ? t->Take(owner, &r, &OnDone, (void*)key, &ticket)
            : t->Give(owner, &r, &OnDone, (void*)key, &ticket);
        if (rc != ItemsNative.Ok)
        {
            lock (Gate) InFlight.Remove(key);
            var status = rc == ItemsNative.InvalidArgument ? ItemsStatus.Invalid : ItemsStatus.Unavailable;
            var why = rc == ItemsNative.InvalidArgument
                ? "refused: bad arguments (count, flags, level, rarity or slot)"
                : "give/take are not available: " + (Status()?.Detail ?? "kiln.items is not loaded");
            return Task.FromResult(ItemsResult.Fail(status, new EntityId(r.Entity), new ItemId(r.Item), (int)r.Count, why));
        }
        lock (Gate)
        {
            if (InFlight.TryGetValue(key, out var still)) still.Ticket = ticket;
        }
        return tcs.Task;
    }

    // A mod unloads: its requests' callbacks are dropped (the transactions may still run).
    private static void Retire(ModContext mod)
    {
        List<ulong> tickets = new();
        lock (Gate)
        {
            Watched.Remove(mod);
            foreach (var (k, p) in InFlight.Where(kv => kv.Value.Mod == mod).ToList())
            {
                InFlight.Remove(k);
                if (p.Ticket != 0) tickets.Add(p.Ticket);
            }
        }
        var t = _table;
        if (t != null) foreach (var tk in tickets) t->Cancel(tk);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnDone(ItemsNative.Result* r, void* user)
    {
        try
        {
            Pending? p;
            lock (Gate)
            {
                if (!InFlight.Remove((long)user, out p)) return;
            }
            var changes = new ItemChange[Math.Min(r->ChangeCount, 16u)];
            var c = (ItemsNative.Change*)r->Changes;
            for (var i = 0; i < changes.Length; i++)
                changes[i] = new ItemChange(new SlotId(c[i].Slot), new ItemId(c[i].Item), (int)c[i].Amount, new EntityId(c[i].Pide));
            var status = Enum.IsDefined(typeof(ItemsStatus), r->Status) ? (ItemsStatus)r->Status : ItemsStatus.Refused;
            var result = new ItemsResult(status, new EntityId(r->Entity), new ItemId(r->Item), (int)r->Requested,
                (int)r->Done, changes, ItemsNative.Text(r->Message, 160), (int)r->GameError, (int)r->Steps,
                (r->Flags & ItemsNative.DryRun) != 0);
            ItemsEvents.Completed(p.Take, result, p.Mod);
            p.Tcs.TrySetResult(result);
        }
        catch
        {
            // never let an exception cross into native code
        }
    }
}
