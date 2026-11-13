// SPDX-License-Identifier: MIT
// Water in containers: kiln.items v2's fuel functions (native/core/abi/services/kiln_items.h, "water").
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln;

public static unsafe partial class Items
{
    /// <summary>
    /// Water in containers: watering cans, water pouches, the water pump, barrels and wells. The game calls it Fuel; it
    /// counts 64 units per charge (a can of 100 charges holds 6,400 units).
    /// </summary>
    /// <remarks>
    /// Writes run inside kiln.items' program in the next sim step, through the game's tracked write, so players see the
    /// new amount and the next save keeps it, as after the game's own fill or pour. A task completes on the tick thread
    /// one or two steps later. Call everything on the tick thread.
    /// </remarks>
    public static class Fuel
    {
        /// <summary>Units per charge (the game's <c>waterChargeAmount</c>).</summary>
        public const int UnitsPerCharge = 64;

        /// <summary>True when the native service has the water functions and they resolved on this game build
        /// (capability <c>items.fuel</c>), and its program is not off (read live: it can break in a session).</summary>
        public static bool Available => Can(Caps.Fuel) && Status() is { Program: not (0 or 4) };

        /// <summary>The water <paramref name="container"/> holds (an item entity, a barrel, a well), or null when it
        /// holds none.</summary>
        [RequiresCapability(Caps.Fuel)]
        [TickThreadOnly]
        public static FuelLevel? Read(EntityId container)
        {
            Services.RequireTickThread("Items.Fuel.Read");
            var t = RequireV2("Items.Fuel.Read");
            int cur = 0, max = 0;
            var rc = t->FuelRead(container.Value, &cur, &max);
            return rc switch
            {
                ItemsNative.Ok => new FuelLevel(cur, max),
                ItemsNative.NotFound => null,
                _ => throw Failure("Items.Fuel.Read", rc),
            };
        }

        /// <summary>Sets <paramref name="container"/>'s water to <paramref name="units"/> (clamped to 0..max).</summary>
        [RequiresCapability(Caps.Fuel)]
        [TickThreadOnly]
        public static Task<FuelResult> Set(EntityId container, int units, bool dryRun = false) =>
            Submit(container, ItemsNative.FuelSet, units, ItemId.None, dryRun);

        /// <summary>Adds <paramref name="units"/> (negative: takes) to <paramref name="container"/>'s water.</summary>
        [RequiresCapability(Caps.Fuel)]
        [TickThreadOnly]
        public static Task<FuelResult> Add(EntityId container, int units, bool dryRun = false) =>
            Submit(container, ItemsNative.FuelAdd, units, ItemId.None, dryRun);

        /// <summary>Fills <paramref name="container"/> to its max.</summary>
        [RequiresCapability(Caps.Fuel)]
        [TickThreadOnly]
        public static Task<FuelResult> Fill(EntityId container, bool dryRun = false) =>
            Submit(container, ItemsNative.FuelFill, 0, ItemId.None, dryRun);

        /// <summary>Fills every water container in <paramref name="holder"/>'s inventories (a player's backpack, action
        /// bars and equipment, a chest), only those of <paramref name="item"/> when given.</summary>
        [RequiresCapability(Caps.Fuel)]
        [TickThreadOnly]
        public static Task<FuelResult> FillAll(EntityId holder, ItemId item = default, bool dryRun = false) =>
            Submit(holder, ItemsNative.FuelFill | ItemsNative.FuelInventories, 0, item, dryRun);

        /// <summary>Sets every water container in <paramref name="holder"/>'s inventories to <paramref name="units"/>
        /// (each clamped to its max), only those of <paramref name="item"/> when given.</summary>
        [RequiresCapability(Caps.Fuel)]
        [TickThreadOnly]
        public static Task<FuelResult> SetAll(EntityId holder, int units, ItemId item = default, bool dryRun = false) =>
            Submit(holder, ItemsNative.FuelSet | ItemsNative.FuelInventories, units, item, dryRun);

        /// <summary>Fills every water container of the given items in <paramref name="holder"/>'s inventories, one
        /// request per item (an empty list: every container). The result adds up the requests.</summary>
        [RequiresCapability(Caps.Fuel)]
        [TickThreadOnly]
        public static Task<FuelResult> FillAll(EntityId holder, IEnumerable<ItemId> items, bool dryRun = false) =>
            FuelSequence.FillEach(holder, items.Distinct().ToList(), dryRun);

        /// <summary>
        /// A refill policy: every sim step, after the game's own fill and pour, the containers get water back as
        /// <paramref name="policy"/> says (full, or a rate up to a level), until the returned handle is disposed (or the
        /// calling mod unloads, or the server world closes). <paramref name="inventories"/> true: every container in
        /// <paramref name="entity"/>'s inventories (only <paramref name="item"/> when given); false:
        /// <paramref name="entity"/> itself (an item entity, a barrel, a well).
        /// </summary>
        /// <remarks>It does not end when the entity goes away. A player's entity id is reused by the next player who
        /// joins, so dispose it when the player leaves (Kiln.Rules' <c>Water</c> rule does that for you).</remarks>
        [RequiresCapability(Caps.Fuel)]
        [TickThreadOnly]
        public static FuelKeep Refill(EntityId entity, FuelRefill policy, ItemId item = default, bool inventories = true)
        {
            ArgumentNullException.ThrowIfNull(policy);
            Services.RequireTickThread("Items.Fuel.Refill");
            var t = RequireV2("Items.Fuel.Refill");
            if (entity.IsNone) throw new ArgumentException("no entity", nameof(entity));
            var r = new ItemsNative.FuelRequest
            {
                Size = (uint)sizeof(ItemsNative.FuelRequest), Entity = entity.Value, Item = item.Value, Limit = policy.Limit,
                Flags = (policy.IsFull ? ItemsNative.FuelFill : ItemsNative.FuelAdd) | (inventories ? ItemsNative.FuelInventories : 0),
                Units = policy.IsFull ? 0 : (int)Math.Clamp(Math.Ceiling(policy.UnitsPerSecond), 1, 1_000_000_000),
            };
            var keep = new FuelKeep(entity, item, inventories, policy);
            long key;
            lock (Gate)
            {
                key = ++_next;
                Keeps[key] = keep;
            }
            ulong handle = 0;
            var rc = t->FuelKeep(Services.CallerOwner, &r, &OnRefill, (void*)key, &handle);
            if (rc != ItemsNative.Ok)
            {
                lock (Gate) Keeps.Remove(key);
                throw rc == ItemsNative.Unavailable
                    ? new NotSupportedException("Items.Fuel.Refill: " + (Status()?.Detail ?? "kiln.items is not loaded") + " (or 256 refills are active)")
                    : Failure("Items.Fuel.Refill", rc);
            }
            keep.Attach(handle, key);
            var mod = ModContext.CurrentMod;
            keep.Origin = mod?.Info.Id ?? "kiln";
            if (mod != null)
            {
                Action retire = keep.Dispose;
                mod.Retiring += retire;
                keep.Detach = () => mod.Retiring -= retire;
            }
            return keep;
        }

        /// <summary>Keeps water containers full (<see cref="Refill"/> with <see cref="FuelRefill.Full"/>).</summary>
        [RequiresCapability(Caps.Fuel)]
        [TickThreadOnly]
        public static FuelKeep KeepFull(EntityId entity, ItemId item = default, bool inventories = true) =>
            Refill(entity, FuelRefill.Full, item, inventories);

        internal static void Forget(long key)
        {
            lock (Gate) Keeps.Remove(key);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnRefill(ulong handle, ItemsNative.FuelChange* c, uint count, void* user)
        {
            try
            {
                FuelKeep? keep;
                lock (Gate) Keeps.TryGetValue((long)user, out keep);
                if (keep is null || count == 0) return;
                var changes = new FuelChange[count];
                for (var i = 0; i < count; i++)
                    changes[i] = new FuelChange(new EntityId(c[i].Container), new ItemId(c[i].Item), new SlotId(c[i].Slot),
                        c[i].Before, c[i].After, c[i].Max);
                keep.Raise(changes);
            }
            catch
            {
                // never let an exception cross into native code
            }
        }

        internal static void End(ulong handle)
        {
            var t = V2;
            if (t != null && handle != 0) t->FuelKeepEnd(handle);
        }

        // ---------------------------------------------------------------------------------------- plumbing ------

        private static readonly object Gate = new();
        private static readonly Dictionary<long, FuelKeep> Keeps = new();
        private static readonly Dictionary<long, (TaskCompletionSource<FuelResult> Tcs, ModContext? Mod, ulong Ticket)> InFlight = new();
        private static readonly HashSet<ModContext> Watched = new();
        private static long _next;

        private static Task<FuelResult> Submit(EntityId entity, uint flags, int units, ItemId item, bool dryRun, int limit = 0)
        {
            Services.RequireTickThread("Items.Fuel");
            var t = RequireV2("Items.Fuel");
            if (entity.IsNone) return Task.FromResult(FuelResult.Fail(ItemsStatus.Invalid, entity, item, "no entity"));
            var r = new ItemsNative.FuelRequest
            {
                Size = (uint)sizeof(ItemsNative.FuelRequest), Entity = entity.Value, Item = item.Value, Units = units,
                Flags = flags | (dryRun ? ItemsNative.FuelDryRun : 0), Limit = limit,
            };
            var mod = ModContext.CurrentMod;
            var tcs = new TaskCompletionSource<FuelResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            long key;
            lock (Gate)
            {
                key = ++_next;
                InFlight[key] = (tcs, mod, 0);
                if (mod != null && Watched.Add(mod)) mod.Retiring += () => Retire(mod);
            }
            ulong ticket = 0;
            var rc = t->Fuel(Services.CallerOwner, &r, &OnDone, (void*)key, &ticket);
            if (rc != ItemsNative.Ok)
            {
                lock (Gate) InFlight.Remove(key);
                return Task.FromResult(FuelResult.Fail(rc == ItemsNative.InvalidArgument ? ItemsStatus.Invalid : ItemsStatus.Unavailable,
                    entity, item, rc == ItemsNative.InvalidArgument ? "refused: bad arguments"
                        : "water writes are not available: " + (Status()?.Detail ?? "kiln.items is not loaded")));
            }
            lock (Gate)
            {
                if (InFlight.TryGetValue(key, out var p)) InFlight[key] = (p.Tcs, p.Mod, ticket);
            }
            return tcs.Task;
        }

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
            var t = V2;
            if (t != null) foreach (var tk in tickets) t->V1.Cancel(tk);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnDone(ItemsNative.FuelResult* r, void* user)
        {
            try
            {
                (TaskCompletionSource<FuelResult> Tcs, ModContext? Mod, ulong Ticket) p;
                lock (Gate)
                {
                    if (!InFlight.Remove((long)user, out p)) return;
                }
                var changes = new FuelChange[Math.Min(r->ChangeCount, 16u)];
                var c = (ItemsNative.FuelChange*)r->Changes;
                for (var i = 0; i < changes.Length; i++)
                    changes[i] = new FuelChange(new EntityId(c[i].Container), new ItemId(c[i].Item), new SlotId(c[i].Slot),
                        c[i].Before, c[i].After, c[i].Max);
                var status = Enum.IsDefined(typeof(ItemsStatus), r->Status) ? (ItemsStatus)r->Status : ItemsStatus.Refused;
                p.Tcs.TrySetResult(new FuelResult(status, new EntityId(r->Entity), new ItemId(r->Item), (int)r->Matched,
                    (int)r->Changed, changes, ItemsNative.Text(r->Message, 160), (int)r->Steps,
                    (r->Flags & ItemsNative.FuelDryRun) != 0));
            }
            catch
            {
                // never let an exception cross into native code
            }
        }
    }

    private static ItemsNative.TableV2* _v2;

    // The v2 table, or null when the native service is v1 (an older runtime).
    private static ItemsNative.TableV2* V2
    {
        get
        {
            if (_v2 != null) return _v2;
            var t = T;
            if (t == null || t->Header.Size < (uint)sizeof(ItemsNative.TableV2)) return null;
            return _v2 = (ItemsNative.TableV2*)t;
        }
    }

    private static ItemsNative.TableV2* RequireV2(string api)
    {
        var t = V2;
        if (t == null)
            throw T == null
                ? ServiceCaps.Unavailable(Caps.Service, "kiln.items is unavailable")
                : ServiceCaps.Unavailable(Caps.Fuel, $"{api}: this Kiln runtime's kiln.items has no water functions (update Kiln)");
        return t;
    }
}

/// <summary>The water a container holds, in units (64 per charge).</summary>
public readonly record struct FuelLevel(int Units, int Max)
{
    public double Charges => Units / (double)Items.Fuel.UnitsPerCharge;
    public double MaxCharges => Max / (double)Items.Fuel.UnitsPerCharge;
    public bool IsFull => Units >= Max;
    public override string ToString() => $"{Units}/{Max} ({Charges:0.#}/{MaxCharges:0.#} charges)";
}

/// <summary>One container a water write changed.</summary>
/// <param name="Container">The item entity, or the placed container.</param>
/// <param name="Item">Its item (none for a placed container).</param>
/// <param name="Slot">Where it sits (none for a placed container).</param>
/// <param name="Before">Units before the write.</param>
/// <param name="After">Units after it (a dry run: what they would be).</param>
/// <param name="Max">The container's max, in units.</param>
public readonly record struct FuelChange(EntityId Container, ItemId Item, SlotId Slot, int Before, int After, int Max);

/// <summary>What a water write did. <see cref="Status"/> is <see cref="ItemsStatus.Done"/> also when nothing needed a
/// change (<see cref="Changed"/> 0); <see cref="ItemsStatus.SlotChanged"/> when the entity holds no water;
/// <see cref="ItemsStatus.NoInventory"/> when a holder has no inventory.</summary>
public sealed record FuelResult(ItemsStatus Status, EntityId Entity, ItemId Item, int Matched, int Changed,
    IReadOnlyList<FuelChange> Changes, string Message, int Steps, bool DryRun) : IKilnResult
{
    public bool Ok => Status == ItemsStatus.Done;

    // IKilnResult's reason is null when the change was made; the record's Message says why otherwise (since 0.2.0-dev.27)
    string? IKilnResult.Reason => Ok ? null : Message;

    internal static FuelResult Fail(ItemsStatus s, EntityId e, ItemId item, string why) =>
        new(s, e, item, 0, 0, Array.Empty<FuelChange>(), why, 0, false);

    public override string ToString() =>
        $"{Status}: {Changed} of {Matched} container(s) changed{(DryRun ? " (dry run)" : "")}{(Message.Length > 0 ? ": " + Message : "")}";
}

/// <summary>How a refill policy gives water back (<see cref="Items.Fuel.Refill"/>).</summary>
public sealed record FuelRefill
{
    /// <summary>Units per second of game time; <see cref="double.PositiveInfinity"/> keeps the containers full.</summary>
    public double UnitsPerSecond { get; init; } = double.PositiveInfinity;

    /// <summary>The level kept or refilled to, in units; 0 = each container's max. A container above it is left as it
    /// is.</summary>
    public int Limit { get; init; }

    public bool IsFull => double.IsPositiveInfinity(UnitsPerSecond);

    /// <summary>Full every step.</summary>
    public static FuelRefill Full { get; } = new();

    /// <summary><paramref name="charges"/> charges per second (64 units each).</summary>
    public static FuelRefill ChargesPerSecond(double charges, int limitUnits = 0) =>
        new() { UnitsPerSecond = Math.Max(charges, 0.001) * Items.Fuel.UnitsPerCharge, Limit = Math.Max(limitUnits, 0) };

    public override string ToString() =>
        (IsFull ? "keep full" : $"{UnitsPerSecond / Items.Fuel.UnitsPerCharge:0.##} charges/s") + (Limit > 0 ? $" up to {Limit} units" : "");
}

/// <summary>Water a refill policy gave back (<see cref="Items.Fuel.Refill"/>): posted on the tick thread in the Pre after
/// it happened, at most once per step and policy, with every container it changed since the last one. Not cancelable
/// (it happened). Built only while someone subscribes.</summary>
public sealed class WaterRefilledEvent : KilnEvent
{
    internal WaterRefilledEvent(FuelKeep keep, IReadOnlyList<FuelChange> changes)
    {
        Keep = keep;
        Changes = changes;
    }

    public FuelKeep Keep { get; }

    /// <summary>The holder (or the container itself) the policy is on.</summary>
    public EntityId Entity => Keep.Entity;

    public IReadOnlyList<FuelChange> Changes { get; }

    /// <summary>Units given back, over every container.</summary>
    public long Units => Changes.Sum(c => (long)Math.Max(0, c.After - c.Before));

    /// <summary>Id of the mod that set the policy ("kiln" outside mod code).</summary>
    public string Origin => Keep.Origin;

    public override string ToString() => $"{Units} units back to {Changes.Count} container(s) of {Entity} ({Keep.Policy}) by {Origin}";
}

/// <summary>An active refill policy (<see cref="Items.Fuel.Refill"/>). Dispose it to stop.</summary>
public sealed class FuelKeep : IDisposable
{
    private ulong _handle;
    private long _key;
    private long _units;
    internal Action? Detach;

    internal FuelKeep(EntityId entity, ItemId item, bool inventories, FuelRefill policy)
    {
        Entity = entity;
        Item = item;
        Inventories = inventories;
        Policy = policy;
    }

    internal void Attach(ulong handle, long key)
    {
        _handle = handle;
        _key = key;
    }

    public EntityId Entity { get; }
    public ItemId Item { get; }
    public bool Inventories { get; }
    public FuelRefill Policy { get; }
    public bool Active => Volatile.Read(ref _handle) != 0;

    /// <summary>Id of the mod that set it ("kiln" outside mod code).</summary>
    public string Origin { get; internal set; } = "kiln";

    /// <summary>Units given back so far.</summary>
    public long UnitsAdded => Interlocked.Read(ref _units);

    /// <summary>Raised on the tick thread (Pre) with the containers this policy refilled since the last call.</summary>
    public event Action<IReadOnlyList<FuelChange>>? Refilled;

    internal void Raise(FuelChange[] changes)
    {
        if (!Active) return;
        Interlocked.Add(ref _units, changes.Sum(c => (long)Math.Max(0, c.After - c.Before)));
        try
        {
            Refilled?.Invoke(changes);
        }
        catch (Exception e)
        {
            Backend.Current.Log(0, LogLevel.Error, $"kiln.items: a Refilled handler of {Origin} threw: {e.Message}");
        }
        if (Events.HasSubscribers<WaterRefilledEvent>()) Events.Post(new WaterRefilledEvent(this, changes));
    }

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, 0);
        if (h == 0) return;
        Items.Fuel.End(h);
        Items.Fuel.Forget(_key);
        Detach?.Invoke();
        Detach = null;
    }
}

// Async helpers live outside the unsafe Items class (C# allows no await in an unsafe context).
internal static class FuelSequence
{
    public static async Task<FuelResult> FillEach(EntityId holder, List<ItemId> items, bool dryRun)
    {
        if (items.Count == 0) return await Items.Fuel.FillAll(holder, ItemId.None, dryRun);
        FuelResult? sum = null;
        foreach (var item in items)
        {
            var r = await Items.Fuel.FillAll(holder, item, dryRun);
            if (!r.Ok) return r;
            sum = sum is null ? r : sum with { Matched = sum.Matched + r.Matched, Changed = sum.Changed + r.Changed, Changes = [.. sum.Changes, .. r.Changes] };
        }
        return sum!;
    }
}
