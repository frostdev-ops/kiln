// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
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

    /// <summary>True when the native service is loaded: capability <c>kiln.items</c>
    /// (<see cref="Capabilities.Why"/> says why not).</summary>
    public static bool Available => Can(Caps.Service);

    /// <summary>True when give/take can run (the service's program runs and its checks passed).</summary>
    public static bool CanTransact => Status()?.Transactions == true;

    private static ItemsNative.TableWithResultPeek* ResultTable
    {
        get
        {
            var t = T;
            return t != null && t->Header.Size >= sizeof(ItemsNative.TableWithResultPeek) &&
                ((ItemsNative.TableWithResultPeek*)t)->PeekResult != null ? (ItemsNative.TableWithResultPeek*)t : null;
        }
    }

    /// <summary>The runtime can observe inventory commits before their next-step callbacks. Needed for a journal saved with the inventory.
    /// The same answer as capability <c>items.result_peek</c>, read from the table itself.</summary>
    public static bool ResultPeekAvailable => ResultTable != null;

    /// <summary>Observes the result of a Give/Take task, including an inventory commit whose normal callback has not yet run. Runs no callbacks,
    /// completes no task, and submits nothing. False: no result yet, or an older runtime has no result peek. The normal callback still runs once.</summary>
    public static bool TryGetResult(Task<ItemsResult> transaction, [NotNullWhen(true)] out ItemsResult? result)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.IsCompleted)
        {
            result = transaction.GetAwaiter().GetResult();
            return true;
        }
        result = null;
        if (!TaskTickets.TryGetValue(transaction, out var pending)) return false;
        result = Volatile.Read(ref pending.Observed);
        if (result != null) return true;
        var t = ResultTable;
        if (t == null || pending.Ticket == 0) return false;
        ItemsNative.Result native = default;
        native.Size = (uint)sizeof(ItemsNative.Result);
        if (t->PeekResult(pending.Ticket, &native) != ItemsNative.Ok) return false;
        result = ResultOf(&native);
        return true;
    }

    /// <summary>This build supports <see cref="Gear.Unlocked"/> and <see cref="Gear.GemSlot"/> (capability
    /// <c>items.pide_options</c>; a give with them is Unavailable otherwise).</summary>
    public static bool GearOptionsAvailable => Available && Can(Caps.GearOptions);

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
    /// <param name="id">The item's id.</param>
    /// <returns>The catalog entry, or null when the id is not in the world's registry (or it has not loaded its items yet).</returns>
    public static ItemInfo? Get(ItemId id) => ItemCatalog.Get(T, id);

    /// <summary>
    /// Finds an item by id ("0x2b0cf1d5", "722268629"), debug name ("Material_T1_Wood_Logs") or display name
    /// ("Wood Logs"), exact match first, then ignoring case. Returns null when nothing matches. Display names come
    /// from the dashboard catalog shipped with Kiln; when several items share one, a non-blueprint wins.
    /// </summary>
    /// <param name="nameOrId">An id (<c>"0x2b0cf1d5"</c>, <c>"722268629"</c>), a debug name or a display name.</param>
    /// <returns>The item, or null when nothing matches.</returns>
    /// <example><code>
    /// ItemInfo? logs = Items.Lookup("Material_T1_Wood_Logs");
    /// </code></example>
    public static ItemInfo? Lookup(string nameOrId) => ItemCatalog.Lookup(T, nameOrId);

    /// <summary>Items whose debug or display name contains <paramref name="text"/> (ignoring case).</summary>
    /// <param name="text">The text to look for.</param>
    /// <param name="max">At most this many items (default 25).</param>
    /// <returns>The matching items, at most <paramref name="max"/>.</returns>
    public static IReadOnlyList<ItemInfo> Search(string text, int max = 25) => ItemCatalog.Search(T, text, max);

    /// <summary>
    /// The item's name for players (since 0.2.0-dev.29.15). On a game client it is in the game's text language
    /// (<c>GameLanguage.Current</c>), read from the game's own texts at run time; that works in the main menu and on a
    /// client that joined another's world too, where <see cref="Get"/> has no catalog. A new item (kiln.content) gets its
    /// definition's text for that language, else its <c>DisplayName</c>. On a dedicated server (whose data has no texts),
    /// and on a client until the game's texts are read (the first call starts that in the background; it takes a fraction
    /// of a second), it is the English name Kiln ships for the game's items. Null for an id Kiln knows no name of.
    /// </summary>
    /// <param name="item">The item's id.</param>
    /// <returns>The name, or null for an id Kiln knows no name of.</returns>
    /// <remarks>Any thread, also a draw callback: it never waits for a read. Keep the answer per frame, not per call site,
    /// if you draw many names.</remarks>
    [AnyThread]
    public static string? DisplayName(ItemId item)
    {
        if (item.Value == 0) return null;
        try
        {
            // a game item never asks kiln.content (its listing is read again on a miss); a new item is no game item
            if (GameNames.ItemGuid(item) is not null) return GameNames.ItemName(item) ?? ItemCatalog.ShippedName(item.Value);
            if (DefinedNames.Of(item.Value, GameNames.Language) is { Length: > 0 } defined) return defined;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException or KilnUnavailableException)
        {
            // fall back to the English name below
        }
        return ItemCatalog.ShippedName(item.Value);
    }

    // ------------------------------------------------------------------------------------------- reading -------

    /// <summary>The inventory of <paramref name="entity"/>, or null when it has none (or it does not exist).</summary>
    /// <param name="entity">The inventory owner: a player, a chest, a villager.</param>
    /// <returns>A copy of its slots as they are now (at most 256 are read), or null.</returns>
    /// <exception cref="KilnUnavailableException">The capability <c>kiln.items</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <example><code>
    /// InventoryContents? bag = Items.Read(player.Entity.Id);
    /// int logs = bag?.Slots.Where(s =&gt; s.Item == logsId).Sum(s =&gt; s.Count) ?? 0;
    /// </code></example>
    /// <seealso cref="Give(EntityId, ItemId, int, Gear?, GiveOptions, SlotId?)"/>
    /// <seealso cref="ItemSelection.Of"/>
    [RequiresCapability(Caps.Service)]
    [TickThreadOnly]
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
                (s.Flags & 0x04) != 0, (s.Flags & 0x01) != 0, (s.Flags & 0x02) != 0, s.State)
            {
                Ref = s.Count == 0 ? ItemInstanceRef.None : ItemRefs.Of(s.Pide, s.Item, s.Reserved),
            });
        }
        return new InventoryContents(entity, info.Initialized != 0, info.SetupFlags, info.GenericSlots,
            info.AvailableSlots, invs, list);
    }

    /// <summary>Entities with an initialized inventory of these kinds (one pass over the world's entities). Players'
    /// storages only with <see cref="InventoryKinds.PlayerStorage"/> (since 0.2.0-dev.30.7).</summary>
    /// <param name="kinds">Which kinds of inventory owner to find (default: containers).</param>
    /// <returns>The entities that hold such an inventory.</returns>
    /// <exception cref="KilnUnavailableException">The capability <c>kiln.items</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [RequiresCapability(Caps.Service)]
    [TickThreadOnly]
    public static IReadOnlyList<EntityId> FindInventories(InventoryKinds kinds = InventoryKinds.Containers)
    {
        var t = Require("Items.FindInventories");
        // without kiln.items.storage there is no holder to find, and a kiln.items of its time does not know the flag
        if ((kinds & InventoryKinds.PlayerStorage) != 0 && StorageNative.T == null)
        {
            kinds &= ~InventoryKinds.PlayerStorage;
            if ((kinds & InventoryKinds.All) == 0) return [];
        }
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
    /// <param name="target">Whose inventory receives it: a player, a chest, a villager.</param>
    /// <param name="item">The item.</param>
    /// <param name="count">How many (default 1).</param>
    /// <param name="gear">For gear: the level (1 to 255), rarity, unlocked upgrades (0 to 5, or 255 for all) and a gem slot it
    /// comes with; null for the game's defaults.</param>
    /// <param name="options">Popup, all-or-nothing, no merging, or a dry run (<see cref="GiveOptions"/>).</param>
    /// <param name="slot">Put it in this slot first, when given.</param>
    /// <returns>A task that completes on the tick thread with the result: how many were given and which slots changed. A bad
    /// argument (a level out of range) is a result with <see cref="ItemsStatus.Invalid"/>, not an exception.</returns>
    /// <exception cref="KilnUnavailableException">The capability <c>kiln.items</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <example><code>
    /// ItemsResult r = await Items.Give(player.Entity.Id, logsId, 50, options: GiveOptions.Notify | GiveOptions.AllOrNothing);
    /// if (r.Status != ItemsStatus.Done) Log.Info($"not given: {r.Status} {r.Message}");
    /// </code></example>
    /// <seealso cref="Take"/>
    /// <seealso cref="Move"/>
    [RequiresCapability(Caps.Service)]
    [TickThreadOnly]
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
            if (g.Unlocked is { } un)
            {
                if (un is < 0 or > 5 && un != 255)
                    return Invalid(target, item, count, "unlocked upgrades must be 0-5, or 255 for all");
                if (un > 0)
                {
                    r.Flags |= ItemsNative.Unlock;
                    r.Unlock = un >= 5 ? (byte)255 : (byte)un;
                }
            }
            if (g.GemSlot)
            {
                r.Flags |= ItemsNative.GemSlot;
                // v7: gear other than weapons gets the slot when a mod allowed its kind (Gems.AllowSockets)
                if (ItemSockets.AllowsGive(item)) r.Flags |= ItemsNative.GemSlotAny;
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
    /// <param name="target">Whose inventory receives the outputs.</param>
    /// <param name="recipeId">The recipe's id.</param>
    /// <param name="count">How many times the recipe's result is made (default 1).</param>
    /// <param name="options">Popup, no merging or a dry run (<see cref="GiveOptions"/>).</param>
    /// <returns>A task that completes on the tick thread with the result.</returns>
    /// <exception cref="KilnUnavailableException">The capability <c>kiln.items</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [RequiresCapability(Caps.Service)]
    [TickThreadOnly]
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
    /// <param name="target">Whose inventory receives it.</param>
    /// <param name="item">An id, debug name or display name, as <see cref="Lookup"/> takes.</param>
    /// <param name="count">How many (default 1).</param>
    /// <param name="gear">As <see cref="Give(EntityId, ItemId, int, Gear?, GiveOptions, SlotId?)"/>'s.</param>
    /// <param name="options">As <see cref="Give(EntityId, ItemId, int, Gear?, GiveOptions, SlotId?)"/>'s.</param>
    /// <returns>A task that completes on the tick thread with the result.</returns>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [RequiresCapability(Caps.Service)]
    [TickThreadOnly]
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
    /// <param name="target">Whose inventory to take from.</param>
    /// <param name="item">The item.</param>
    /// <param name="count">How many (default 1); if the inventory holds fewer, nothing is taken.</param>
    /// <param name="from">The slot to try first (and the only one with <paramref name="exactSlot"/>).</param>
    /// <param name="exactSlot">True: take from <paramref name="from"/> only; it needs a slot.</param>
    /// <param name="itemEntity">With <paramref name="exactSlot"/>: the slot must hold this item entity (gear).</param>
    /// <param name="dryRun">True: run the transaction and roll it back; the result says what would happen.</param>
    /// <returns>A task that completes on the tick thread with the result; <see cref="ItemsResult.Done"/> says how many left.</returns>
    /// <exception cref="KilnUnavailableException">The capability <c>kiln.items</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <example><code>
    /// ItemsResult r = await Items.Take(player.Entity.Id, baitId, 2);
    /// if (r.Status == ItemsStatus.Done) { /* paid */ }
    /// </code></example>
    /// <seealso cref="ItemCosts.Take"/>
    /// <seealso cref="Give(EntityId, ItemId, int, Gear?, GiveOptions, SlotId?)"/>
    [RequiresCapability(Caps.Service)]
    [TickThreadOnly]
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
    /// <param name="target">Whose inventory the slot belongs to.</param>
    /// <param name="slot">The slot as read (<see cref="Read"/>); an empty one is a result with <see cref="ItemsStatus.Invalid"/>.</param>
    /// <param name="dryRun">True: roll the transaction back; the result says what would happen.</param>
    /// <returns>A task that completes on the tick thread with the result.</returns>
    /// <exception cref="KilnUnavailableException">The capability <c>kiln.items</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [RequiresCapability(Caps.Service)]
    [TickThreadOnly]
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
        if (t == null)
        {
            Rows.Ensure();
            throw ServiceCaps.Unavailable(Caps.Service, "kiln.items is unavailable");
        }
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
        public ItemsResult? Observed;
        public bool Take;  // for ItemsGivenEvent / ItemsTakenEvent
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<long, Pending> InFlight = new();
    // Weak keys: completed tasks are not retained by the service, while a save can still identify a task after OnDone removes its in-flight entry.
    private static readonly ConditionalWeakTable<Task<ItemsResult>, Pending> TaskTickets = new();
    private static readonly HashSet<ModContext> Watched = new();
    private static long _nextKey;

    private static Task<ItemsResult> Submit(bool take, ItemsNative.Request r, string api) =>
        SubmitEx(take, new ItemsNative.RequestEx { Base = r }, api);

    // v7: the request with its extension (Base.Size says which: the v1 size, or sizeof(RequestEx) for a dye / durability)
    internal static Task<ItemsResult> SubmitEx(bool take, ItemsNative.RequestEx rx, string api)
    {
        var r = rx.Base;
        var t = Require(api);
        if (r.Count == 0) return Invalid(new EntityId(r.Entity), new ItemId(r.Item), 0, "count must be 1-100000");
        if (r.Entity == 0) return Invalid(default, new ItemId(r.Item), (int)r.Count, "no entity");
        if (r.Item == 0) return Invalid(new EntityId(r.Entity), default, (int)r.Count, "no item");
        var mod = ModContext.CurrentMod;
        var tcs = new TaskCompletionSource<ItemsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var p = new Pending(tcs, mod) { Take = take };
        TaskTickets.Add(tcs.Task, p);
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
            ? t->Take(owner, &rx.Base, &OnDone, (void*)key, &ticket)
            : t->Give(owner, &rx.Base, &OnDone, (void*)key, &ticket);
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
            p.Ticket = ticket;
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
            var result = ResultOf(r);
            // Item events run synchronously before the task completes and may request a save themselves.
            Volatile.Write(ref p.Observed, result);
            ItemTracker.OnTransaction(result, p.Take);
            ItemsEvents.Completed(p.Take, result, p.Mod);
            p.Tcs.TrySetResult(result);
        }
        catch
        {
            // never let an exception cross into native code
        }
    }

    private static ItemsResult ResultOf(ItemsNative.Result* r)
    {
        var changes = new ItemChange[Math.Min(r->ChangeCount, 16u)];
        var c = (ItemsNative.Change*)r->Changes;
        for (var i = 0; i < changes.Length; i++)
            changes[i] = new ItemChange(new SlotId(c[i].Slot), new ItemId(c[i].Item), (int)c[i].Amount, new EntityId(c[i].Pide))
            {
                Ref = ItemRefs.Of(c[i].Pide, c[i].Item, c[i].Reserved),
            };
        var status = Enum.IsDefined(typeof(ItemsStatus), r->Status) ? (ItemsStatus)r->Status : ItemsStatus.Refused;
        return new ItemsResult(status, new EntityId(r->Entity), new ItemId(r->Item), (int)r->Requested,
            (int)r->Done, changes, ItemsNative.Text(r->Message, 160), (int)r->GameError, (int)r->Steps,
            (r->Flags & ItemsNative.DryRun) != 0);
    }
}
