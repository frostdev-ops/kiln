// SPDX-License-Identifier: MIT
// Exact costs from a selected slot, with receipts: kiln.item_costs (native/core/abi/services/kiln_item_costs.h), the
// exact-cost adapter of dossier chapter 19e section 5.2. Taming steps and revives (Kiln.Pets) pay through it; any mod may.
// A cost names one slot and what it holds now; there is no search and no fallback to another stack. The cost runs inside
// kiln.items' serial program in the next sim step; its receipt says what moved (or that nothing did, or that it is not
// known). One request id is one debit for the life of the process. Capabilities items.costs.exact and items.costs.wear.
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>One slot of one inventory owner, as it was read, chosen to pay a cost: the slot must still hold exactly this
/// when the cost runs (the same item, the same count, and for gear the same stamped item), or the cost refuses
/// (<see cref="CostRefusal.StaleSelection"/>) and nothing is taken. A plain stack is a value, not an object: the same item
/// and count taken out and put back is the same selection. Build it from a read with <see cref="Of"/> (or
/// <see cref="ItemCosts.Choose"/>); a client never names one: the server builds it from its own read. Since 0.2.0-dev.32.</summary>
/// <param name="InventoryOwner">Whose inventories the slot belongs to (a player's session entity, a chest).</param>
/// <param name="Slot">The slot.</param>
/// <param name="ExpectedItem">The item the slot holds.</param>
/// <param name="ExpectedCount">How many it holds (1 for gear).</param>
/// <param name="Instance">Gear: the item as one particular item (its stamped ref, <see cref="InventorySlot.Ref"/>);
/// <see cref="ItemInstanceRef.None"/> for a plain stack.</param>
public sealed record ItemSelection(EntityRef InventoryOwner, SlotId Slot, ItemId ExpectedItem, int ExpectedCount, ItemInstanceRef Instance)
{
    /// <summary>True for a plain stack (no item entity): what <see cref="ItemCosts.Take"/> pays from.</summary>
    public bool IsStack => Instance.IsNone;

    /// <summary>The selection of <paramref name="slot"/> as it was read from <paramref name="owner"/>'s inventory
    /// (<see cref="Items.Read"/>).</summary>
    /// <exception cref="ArgumentException">The slot is empty, or holds gear without a stamped ref (item instances off).</exception>
    public static ItemSelection Of(EntityRef owner, InventorySlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (slot.IsEmpty) throw new ArgumentException("the slot is empty", nameof(slot));
        if (!slot.ItemEntity.IsNone && slot.Ref.IsNone)
            throw new ArgumentException("the slot holds gear without a stamped ref (items.instances is off): it cannot be selected", nameof(slot));
        return new ItemSelection(owner, slot.Slot, slot.Item, slot.ItemEntity.IsNone ? slot.Count : 1, slot.ItemEntity.IsNone ? ItemInstanceRef.None : slot.Ref);
    }
}

/// <summary>What a cost does. Since 0.2.0-dev.32.</summary>
public enum CostKind
{
    /// <summary>Items taken from a plain stack in the selected slot (<see cref="ItemCosts.Take"/>).</summary>
    StackTake = 1,
    /// <summary>Durability taken off the selected gear item (<see cref="ItemCosts.Wear"/>).</summary>
    GearWear = 2,
    /// <summary>Items given back for a stack take (<see cref="ItemCosts.Refund"/>).</summary>
    StackRefund = 3,
    /// <summary>Durability given back for a gear wear (<see cref="ItemCosts.Refund"/>).</summary>
    GearRestore = 4,
}

/// <summary>Where a cost ended. Since 0.2.0-dev.32.</summary>
public enum CostState
{
    /// <summary>Nothing changed (<see cref="CostReceipt.Refusal"/> says why).</summary>
    Refused = 0,
    /// <summary>It happened; the receipt says what moved.</summary>
    Committed = 1,
    /// <summary>A fault inside the game's change: what the receipt says was read afterwards, and whether the cost was paid
    /// is not known. Never submit it again as if nothing happened, and never refund it automatically.</summary>
    Uncertain = 2,
}

/// <summary>How a committed cost compares with what was asked. Since 0.2.0-dev.32.</summary>
public enum CostDisposition
{
    /// <summary>Not reported (a refused cost, or a receipt made by hand).</summary>
    None = 0,
    /// <summary>Exactly what was asked moved.</summary>
    Exact = 1,
    /// <summary>A gear wear in a world with durability off: a use with 0 wear, as the game's own debits there.</summary>
    WearWaivedByWorld = 2,
    /// <summary>A gear change other than asked: another loader's hook on the game's durability debit changed it
    /// (<see cref="CostReceipt.Taken"/> is what the game applied, negative for a repair). It happened once; Kiln never
    /// debits again to reach the asked number.</summary>
    ModifiedByOtherMod = 3,
    /// <summary>Less moved than asked (the game took part of a stack, or a refund fit only partly).</summary>
    Partial = 4,
}

/// <summary>Why a cost changed nothing. Since 0.2.0-dev.32.</summary>
public enum CostRefusal
{
    /// <summary>Not refused.</summary>
    None = 0,
    /// <summary>The slot is no longer the owner's, or does not hold the selected item, count or stamped item now.</summary>
    StaleSelection = 1,
    /// <summary>A stack cost of an item that has an item entity of its own (gear, a gem).</summary>
    NotAStack = 2,
    /// <summary>A gear cost of an item without durability.</summary>
    NotGear = 3,
    /// <summary>The wear would leave the item below 1 and breaking was not allowed.</summary>
    WouldBreak = 4,
    /// <summary>The item is broken already (durability 0): not usable.</summary>
    Broken = 5,
    /// <summary>The game's transaction refused it.</summary>
    GameRefused = 6,
    /// <summary>A refund found no room.</summary>
    NoRoom = 7,
    /// <summary>The server world closed before the cost ran.</summary>
    WorldEnded = 8,
    /// <summary>The cost did not run (kiln.items' program stopped running, or was switched off).</summary>
    NotRun = 9,
    /// <summary>A gear restore of an item at its maximum durability.</summary>
    Full = 10,
    /// <summary>The request id is held by another cost.</summary>
    RequestConflict = 11,
    /// <summary>The request id was used and its receipt let go: one debit per request id, ever.</summary>
    RequestClosed = 12,
    /// <summary>1024 costs are waiting: try again in a step.</summary>
    Busy = 13,
    /// <summary>No server world runs here (or another one than the cost was made in).</summary>
    WrongWorld = 14,
    /// <summary>The owner is a player who is not in game with the session the selection was read in.</summary>
    StaleSession = 15,
    /// <summary>A refund of a receipt that took nothing, was refused, or is uncertain.</summary>
    NothingToRefund = 16,
}

/// <summary>A cost's receipt: what was asked, what moved, and whether it happened. The four positional values are the
/// item, how much was asked (items, or durability units for gear), how much moved (items taken or given back, or the
/// durability the game applied) and whether it committed; the rest tell the receipt apart and say why. A receipt is an
/// immutable record of the past: a refused cost spent nothing, a committed one is spent until something refunds it, an
/// uncertain one is neither (never treat it as either). Since 0.2.0-dev.32.</summary>
/// <param name="Item">The item.</param>
/// <param name="Requested">Items asked for (stacks) or durability units asked for (gear).</param>
/// <param name="Taken">Items taken (a refund: given back) or durability units the game applied (gear; see
/// <see cref="CostDisposition.ModifiedByOtherMod"/>).</param>
/// <param name="Committed">True when it happened (state <see cref="CostState.Committed"/>).</param>
public sealed record CostReceipt(ItemId Item, int Requested, int Taken, bool Committed)
{
    /// <summary>The request id the cost ran under (one debit per id).</summary>
    public Guid RequestId { get; init; }
    /// <summary>kiln.item_costs' ticket (0: the cost never reached it).</summary>
    public ulong NativeTicket { get; init; }
    /// <summary>What the cost did.</summary>
    public CostKind Kind { get; init; } = CostKind.StackTake;
    /// <summary>The slot it was paid from (a refund: the slot of the cost it gives back for).</summary>
    public ItemSelection? Selection { get; init; }
    /// <summary>True when it is not known whether the cost was paid (<see cref="CostState.Uncertain"/>).</summary>
    public bool Uncertain { get; init; }
    /// <summary>Committed, refused or uncertain.</summary>
    public CostState State => Uncertain ? CostState.Uncertain : Committed ? CostState.Committed : CostState.Refused;
    /// <summary>How what moved compares with what was asked (committed and uncertain receipts).</summary>
    public CostDisposition Disposition { get; init; }
    /// <summary>Why nothing changed (refused receipts).</summary>
    public CostRefusal Refusal { get; init; }
    /// <summary>Why it was refused or is uncertain, in words; "" otherwise.</summary>
    public string Reason { get; init; } = "";
    /// <summary>Gear: the durability before the change, as read in kiln.items' program.</summary>
    public int? DurabilityBefore { get; init; }
    /// <summary>Gear: the durability after it.</summary>
    public int? DurabilityAfter { get; init; }
    /// <summary>A refund: the request id of the cost it gives back for.</summary>
    public Guid? RefundOf { get; init; }
    /// <summary>The server step the cost ran in (0: it did not run).</summary>
    public ulong Step { get; init; }
}

/// <summary>A cost was paid, or may have been (its receipt is <see cref="CostState.Committed"/> or
/// <see cref="CostState.Uncertain"/>): posted on the tick thread when the receipt arrives, before the caller's task
/// continues; also when the mod that asked has unloaded. Not cancelable (it already happened). Stack takes also raise
/// <see cref="ItemsTakenEvent"/>, stack refunds <see cref="ItemsGivenEvent"/>. Since 0.2.0-dev.32.</summary>
[ServerOnly]
public sealed class ItemCostSettledEvent : KilnEvent
{
    internal ItemCostSettledEvent(CostReceipt receipt, string origin)
    {
        Receipt = receipt;
        Origin = origin;
    }

    /// <summary>The receipt.</summary>
    public CostReceipt Receipt { get; }

    /// <summary>The mod that asked ("kiln" outside mod code).</summary>
    public string Origin { get; }

    /// <summary>The kind, the item, what moved and the mod, for logs.</summary>
    public override string ToString() => $"{Receipt.Kind} {Receipt.Taken}/{Receipt.Requested} of {Receipt.Item} ({Receipt.State}) by {Origin}";
}

/// <summary>Exact costs from a selected slot, with receipts (kiln.item_costs). Each call submits one cost under the
/// caller's request id; it runs inside kiln.items' program in the next sim step and the task completes on the tick thread
/// in the step after, with the receipt. The same request id with the same cost returns the same task (while it runs) or
/// the same receipt (after); another cost under it is refused (<see cref="CostRefusal.RequestConflict"/>). A network
/// retry sends the same id; a new intended payment takes a new one. Nothing here is atomic with anything else (a pet
/// record, a save): a committed receipt says the inventory changed in the running world. Since 0.2.0-dev.32.</summary>
[ServerOnly]
public static unsafe class ItemCosts
{
    /// <summary>The capability of stack takes and stack refunds.</summary>
    public const string StacksCapability = "items.costs.exact";
    /// <summary>The capability of gear wear and gear restores.</summary>
    public const string WearCapability = "items.costs.wear";
    /// <summary>The most items one stack cost moves.</summary>
    public const int MaxCount = 100000;
    /// <summary>The most durability units one gear cost moves.</summary>
    public const int MaxWear = 32767;

    /// <summary>True when stack costs can be submitted (capability <c>items.costs.exact</c>).</summary>
    public static bool StacksAvailable => Runtime.StacksOn();

    /// <summary>True when gear costs can be submitted (capability <c>items.costs.wear</c>).</summary>
    public static bool WearAvailable => Runtime.WearOn();

    /// <summary>The first slot of <paramref name="owner"/>'s inventories (in their link order) holding a plain stack of
    /// at least <paramref name="count"/> of <paramref name="item"/>, as a selection; null when none does. A server-side
    /// choice for a cost the player did not pick a slot for.</summary>
    /// <exception cref="ArgumentException"><paramref name="owner"/> or <paramref name="item"/> is none.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is outside 1..100000.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [TickThreadOnly]
    public static ItemSelection? Choose(EntityRef owner, ItemId item, int count = 1)
    {
        Services.RequireTickThread("ItemCosts.Choose");
        if (owner.IsNone) throw new ArgumentException("no owner", nameof(owner));
        if (item.IsNone) throw new ArgumentException("no item", nameof(item));
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxCount);
        if (Runtime.ReadAll(owner.Id) is not { } slots) return null;
        foreach (var s in slots)
            if (s.Item == item && s.ItemEntity.IsNone && s.Count >= count) return ItemSelection.Of(owner, s);
        return null;
    }

    /// <summary>Whether <paramref name="selection"/> could pay <paramref name="amount"/> now (items for a stack, durability
    /// units for gear): null when it could; else why not, starting with the <see cref="CostRefusal"/> name. A read only:
    /// nothing moves, and the cost is checked again when it runs.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="selection"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [TickThreadOnly]
    public static string? Check(ItemSelection selection, int amount, bool allowBreak = false)
    {
        ArgumentNullException.ThrowIfNull(selection);
        Services.RequireTickThread("ItemCosts.Check");
        if (selection.InventoryOwner.IsNone || selection.Slot.IsNone) return "StaleSelection: no owner or slot";
        var slot = Runtime.ReadAll(selection.InventoryOwner.Id)?.FirstOrDefault(s => s.Slot == selection.Slot);
        if (slot is null) return "StaleSelection: the slot is not one of the owner's inventory slots";
        if (slot.Item != selection.ExpectedItem) return $"StaleSelection: the slot holds {slot.Item}, not {selection.ExpectedItem}";
        if (selection.IsStack)
        {
            if (!slot.ItemEntity.IsNone) return "NotAStack: the slot holds gear";
            if (slot.Count != selection.ExpectedCount) return $"StaleSelection: the slot holds {slot.Count}, not the {selection.ExpectedCount} selected";
            return amount <= slot.Count ? null : $"StaleSelection: the slot holds {slot.Count}, the cost is {amount}";
        }
        if (slot.ItemEntity != selection.Instance.Entity.Id || slot.Ref != selection.Instance) return "StaleSelection: the slot holds another item now";
        if (Runtime.ReadDurability(selection.Instance) is not { } d) return "NotGear: the item has no durability";
        if (d.Current <= 0) return "Broken: the item is broken";
        return allowBreak || d.Current - amount >= 1 ? null : "WouldBreak: the wear would leave the item below 1";
    }

    /// <summary>Takes <paramref name="count"/> items from the selected plain stack, from that slot only, all or nothing.</summary>
    /// <param name="selection">A plain stack's selection.</param>
    /// <param name="count">1 to the selection's count.</param>
    /// <param name="requestId">The caller's id of this payment: the same id is never a second debit.</param>
    /// <returns>The receipt (completes on the tick thread in the step after the cost ran). A refusal is a receipt with
    /// <see cref="CostReceipt.Committed"/> false.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selection"/> is null.</exception>
    /// <exception cref="ArgumentException">An empty request id, a gear selection, no owner, slot or item.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is outside 1..the selection's count.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the calling mod is unloading.</exception>
    /// <exception cref="KilnUnavailableException">The capability <c>items.costs.exact</c> is off.</exception>
    /// <example><code>
    /// var bait = ItemCosts.Choose(player.Entity, baitId, 1) ?? throw new InvalidOperationException("no bait");
    /// CostReceipt r = await ItemCosts.Take(bait, 1, Guid.NewGuid());
    /// if (!r.Committed) Log.Info($"not paid: {r.Refusal} {r.Reason}");
    /// </code></example>
    [TickThreadOnly]
    [RequiresCapability(StacksCapability)]
    public static Task<CostReceipt> Take(ItemSelection selection, int count, Guid requestId)
    {
        Validate(selection, requestId);
        if (!selection.IsStack) throw new ArgumentException("a gear selection is worn (ItemCosts.Wear), not taken", nameof(selection));
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Math.Min(selection.ExpectedCount, MaxCount));
        var c = Native(ItemCostsNative.StackTake, selection, selection.ExpectedItem, (uint)selection.ExpectedCount, (uint)count, false, requestId);
        return Submit(c, new Ask(CostKind.StackTake, selection, selection.ExpectedItem, count, requestId, null), "ItemCosts.Take");
    }

    /// <summary>Takes <paramref name="units"/> durability off the selected gear item through the game's own durability
    /// debit (other loaders' hooks on it stay in the chain), never by destroying and giving the tool again. Without
    /// <paramref name="allowBreak"/> a wear that would leave less than 1 refuses; with it the tool may break (the game
    /// sends no break notice for Kiln's cost). In a world with durability off the use costs 0
    /// (<see cref="CostDisposition.WearWaivedByWorld"/>).</summary>
    /// <param name="selection">A gear item's selection (its stamped ref).</param>
    /// <param name="units">0 to 32767 (0: a use that costs nothing, still checked and receipted).</param>
    /// <param name="requestId">As <see cref="Take"/>'s.</param>
    /// <param name="allowBreak">Let the wear reach 0.</param>
    /// <returns>As <see cref="Take"/>'s; <see cref="CostReceipt.Taken"/> is what the game applied.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selection"/> is null.</exception>
    /// <exception cref="ArgumentException">An empty request id, a stack selection, no owner, slot or item.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="units"/> is outside 0..32767.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the calling mod is unloading.</exception>
    /// <exception cref="KilnUnavailableException">The capability <c>items.costs.wear</c> is off.</exception>
    [TickThreadOnly]
    [RequiresCapability(WearCapability)]
    public static Task<CostReceipt> Wear(ItemSelection selection, int units, Guid requestId, bool allowBreak = false)
    {
        Validate(selection, requestId);
        if (selection.IsStack) throw new ArgumentException("a plain stack has no durability: take it (ItemCosts.Take)", nameof(selection));
        ArgumentOutOfRangeException.ThrowIfNegative(units);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(units, MaxWear);
        var c = Native(ItemCostsNative.GearWear, selection, selection.ExpectedItem, 1, (uint)units, allowBreak, requestId);
        return Submit(c, new Ask(CostKind.GearWear, selection, selection.ExpectedItem, units, requestId, null), "ItemCosts.Wear");
    }

    /// <summary>Gives back what a committed receipt took: its items to the same owner (its slot preferred; stacks are
    /// fungible, so this is a true refund of a stack), or its durability to the same stamped item (never above its max).
    /// A receipt that took nothing, was refused or is uncertain is never refunded
    /// (<see cref="CostRefusal.NothingToRefund"/>): Kiln does not give back what it cannot prove was taken. A refund is a
    /// cost of its own: its own request id, its own receipt (<see cref="CostReceipt.RefundOf"/>), and it may end
    /// <see cref="CostDisposition.Partial"/> or <see cref="CostRefusal.NoRoom"/>.</summary>
    /// <param name="receipt">A take's or a wear's receipt.</param>
    /// <param name="requestId">The refund's own id.</param>
    /// <exception cref="ArgumentNullException"><paramref name="receipt"/> is null.</exception>
    /// <exception cref="ArgumentException">An empty request id, or a refund's receipt.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the calling mod is unloading.</exception>
    /// <exception cref="KilnUnavailableException">The receipt kind's capability is off.</exception>
    [TickThreadOnly]
    [RequiresCapability(StacksCapability)]
    public static Task<CostReceipt> Refund(CostReceipt receipt, Guid requestId)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        Services.RequireTickThread("ItemCosts.Refund");
        if (requestId == Guid.Empty) throw new ArgumentException("an empty request id", nameof(requestId));
        if (receipt.Kind is CostKind.StackRefund or CostKind.GearRestore) throw new ArgumentException("a refund is not refunded", nameof(receipt));
        var kind = receipt.Kind == CostKind.StackTake ? CostKind.StackRefund : CostKind.GearRestore;
        var amount = receipt.Taken;
        if (!receipt.Committed || receipt.Uncertain || amount <= 0 || receipt.Selection is not { } sel)
            return Task.FromResult(new CostReceipt(receipt.Item, amount, 0, false)
            {
                RequestId = requestId, Kind = kind, Selection = receipt.Selection, Refusal = CostRefusal.NothingToRefund, RefundOf = receipt.RequestId,
                Reason = receipt.Uncertain ? "an uncertain cost is never refunded automatically" : "the receipt took nothing",
            });
        var c = kind == CostKind.StackRefund
            ? Native(ItemCostsNative.StackRefund, sel, receipt.Item, 0, (uint)Math.Min(amount, MaxCount), false, requestId)
            : Native(ItemCostsNative.GearRestore, sel, receipt.Item, 0, (uint)Math.Min(amount, MaxWear), false, requestId);
        return Submit(c, new Ask(kind, sel, receipt.Item, amount, requestId, receipt.RequestId), "ItemCosts.Refund");
    }

    // ------------------------------------------------------------------------------------------- plumbing ------

    private static void Validate(ItemSelection selection, Guid requestId)
    {
        ArgumentNullException.ThrowIfNull(selection);
        Services.RequireTickThread("ItemCosts");
        if (requestId == Guid.Empty) throw new ArgumentException("an empty request id: give each intended payment its own", nameof(requestId));
        if (selection.InventoryOwner.IsNone) throw new ArgumentException("the selection names no owner", nameof(selection));
        if (selection.Slot.IsNone) throw new ArgumentException("the selection names no slot", nameof(selection));
        if (selection.ExpectedItem.IsNone) throw new ArgumentException("the selection names no item", nameof(selection));
        if (selection.ExpectedCount < 1) throw new ArgumentException("the selection's count is below 1", nameof(selection));
    }

    /// <summary>A request id's two halves, as the native table keys it.</summary>
    internal static (ulong Lo, ulong Hi) Halves(Guid id)
    {
        Span<byte> b = stackalloc byte[16];
        id.TryWriteBytes(b);
        return (BitConverter.ToUInt64(b[..8]), BitConverter.ToUInt64(b[8..]));
    }

    internal static Guid RequestIdOf(ulong lo, ulong hi)
    {
        Span<byte> b = stackalloc byte[16];
        BitConverter.TryWriteBytes(b[..8], lo);
        BitConverter.TryWriteBytes(b[8..], hi);
        return new Guid(b);
    }

    private static ItemCostsNative.SelectedCost Native(uint kind, ItemSelection s, ItemId item, uint expected, uint amount, bool allowBreak, Guid id)
    {
        var (lo, hi) = Halves(id);
        var gear = kind is ItemCostsNative.GearWear or ItemCostsNative.GearRestore;
        return new ItemCostsNative.SelectedCost
        {
            Size = (uint)sizeof(ItemCostsNative.SelectedCost), Kind = kind, Holder = s.InventoryOwner.Id.Value, Slot = s.Slot.Value, Item = item.Value,
            ExpectedCount = expected, ItemEntity = gear ? s.Instance.Entity.Id.Value : 0, ItemStamp = gear ? StampOf(s.Instance) : 0, Amount = amount,
            AllowBreak = allowBreak ? 1u : 0, RequestLo = lo, RequestHi = hi,
        };
    }

    private static uint StampOf(ItemInstanceRef r)
    {
        if (IdentitySources.PlaceOf(r.Entity.Serial) != EntitySourceOrder.Items || (r.Entity.Serial & IdentitySources.MaxStampValue) > uint.MaxValue)
            throw new ArgumentException($"{r} is not a ref of an item entity (its stamp is not kiln.items'): take it from a slot's Ref", "selection");
        return (uint)(r.Entity.Serial & IdentitySources.MaxStampValue);
    }

    /// <summary>What a cost asked, kept with it to build its receipt.</summary>
    internal sealed record Ask(CostKind Kind, ItemSelection Selection, ItemId Item, int Amount, Guid RequestId, Guid? RefundOf);

    private sealed class Pending(TaskCompletionSource<CostReceipt> tcs, ModContext? mod, Ask ask, ItemCostsNative.SelectedCost cost)
    {
        public readonly TaskCompletionSource<CostReceipt> Tcs = tcs;
        public readonly ModContext? Mod = mod;
        public readonly Ask Ask = ask;
        public readonly ItemCostsNative.SelectedCost Cost = cost;
        public ulong Ticket;
        public bool Retired;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<ulong, Pending> InFlight = new();           // by native ticket
    private static readonly Dictionary<Guid, Pending> ByRequest = new();           // while it runs
    private static readonly Dictionary<Guid, (ItemCostsNative.SelectedCost Cost, CostReceipt Receipt)> Settled = new();  // after
    private static readonly Queue<Guid> SettledOrder = new();
    private static readonly HashSet<ModContext> Watched = new();
    private static IDisposable? _tick;
    private const int SettledKept = 4096;

    private static Task<CostReceipt> Submit(ItemCostsNative.SelectedCost c, Ask ask, string api)
    {
        var stack = ask.Kind is CostKind.StackTake or CostKind.StackRefund;
        if (!(stack ? Runtime.StacksOn() : Runtime.WearOn()))
            throw new KilnUnavailableException(stack ? StacksCapability : WearCapability, $"{api}: {(stack ? "stack" : "gear")} costs are not available here");
        var t = Runtime.Table ?? throw new KilnUnavailableException(stack ? StacksCapability : WearCapability, $"{api}: kiln.item_costs is not loaded");
        var mod = ModContext.CurrentMod;
        if (mod is { Alive: false }) throw new InvalidOperationException("the mod is unloading");
        lock (Gate)
        {
            // one debit per request id: the same cost is the same task or receipt; another is a conflict
            if (ByRequest.TryGetValue(ask.RequestId, out var running))
                return Same(running.Cost, c) ? running.Tcs.Task : Refused(ask, CostRefusal.RequestConflict, "the request id is held by another cost");
            if (Settled.TryGetValue(ask.RequestId, out var done))
                return Same(done.Cost, c) ? Task.FromResult(done.Receipt) : Refused(ask, CostRefusal.RequestConflict, "the request id was used by another cost");
        }
        // the holder's session: a player must be in game; another holder carries 0
        var (isPlayer, inGame, session) = Runtime.SessionOf(ask.Selection.InventoryOwner.Id);
        if (isPlayer && !inGame) return Refused(ask, CostRefusal.StaleSession, "the owner is not in game");
        if (ask.Selection.InventoryOwner.HasSerial && !Runtime.Alive(ask.Selection.InventoryOwner))
            return Refused(ask, CostRefusal.StaleSession, "the owner's entity is not the one the selection was read from");
        if (!t.Status(out var st) || st.WorldEpoch == 0) return Refused(ask, CostRefusal.WrongWorld, "no server world runs here");
        c.WorldEpoch = st.WorldEpoch;
        c.CharacterSession = isPlayer ? session : 0;
        var rc = t.Submit(in c, out var ticket);
        switch (rc)
        {
            case ItemCostsNative.Ok:
                break;
            case ItemCostsNative.InvalidArgument:
                throw new ArgumentException($"{api}: kiln.item_costs refused the cost's shape");
            case ItemCostsNative.Unavailable:
                throw new KilnUnavailableException(stack ? StacksCapability : WearCapability, $"{api}: kiln.item_costs turned {(stack ? "stack" : "gear")} costs off (see kiln.log)");
            case ItemCostsNative.WrongThread:
                throw new InvalidOperationException($"{api} must be called on the tick thread");
            default:
                return Refused(ask, RefusalOfSubmit(rc), $"kiln.item_costs refused it ({rc})");
        }
        lock (Gate)
        {
            if (InFlight.TryGetValue(ticket, out var same)) return same.Tcs.Task;  // the native side had it already
            var p = new Pending(new TaskCompletionSource<CostReceipt>(TaskCreationOptions.RunContinuationsAsynchronously), mod, ask, c) { Ticket = ticket };
            InFlight[ticket] = p;
            ByRequest[ask.RequestId] = p;
            if (mod != null && Watched.Add(mod)) mod.Retiring += () => Retire(mod);
            _tick ??= Runtime.AddTick("kiln.item_costs receipts", _ => Runtime.Pump());
            return p.Tcs.Task;
        }
    }

    // the same cost: every field but the world and session (filled at submit)
    private static bool Same(ItemCostsNative.SelectedCost a, ItemCostsNative.SelectedCost b)
    {
        a.WorldEpoch = b.WorldEpoch = 0;
        a.CharacterSession = b.CharacterSession = 0;
        return a.Equals(b);
    }

    private static Task<CostReceipt> Refused(Ask ask, CostRefusal why, string reason) =>
        Task.FromResult(new CostReceipt(ask.Item, ask.Amount, 0, false)
        {
            RequestId = ask.RequestId, Kind = ask.Kind, Selection = ask.Selection, Refusal = why, Reason = reason, RefundOf = ask.RefundOf,
        });

    internal static CostRefusal RefusalOfSubmit(int rc) => rc switch
    {
        ItemCostsNative.RequestConflict => CostRefusal.RequestConflict,
        ItemCostsNative.RequestClosed => CostRefusal.RequestClosed,
        ItemCostsNative.Busy => CostRefusal.Busy,
        ItemCostsNative.WrongWorld => CostRefusal.WrongWorld,
        ItemCostsNative.StaleSession => CostRefusal.StaleSession,
        _ => CostRefusal.NotRun,
    };

    private static void Retire(ModContext mod)
    {
        lock (Gate)
        {
            Watched.Remove(mod);
            foreach (var p in InFlight.Values)
                if (p.Mod == mod) p.Retired = true;
        }
    }

    /// <summary>The managed receipt of a native one (pure).</summary>
    internal static CostReceipt ReceiptOf(ItemCostsNative.CostResult n, Ask ask)
    {
        var committed = n.State == ItemCostsNative.Committed;
        var uncertain = n.State == ItemCostsNative.Uncertain;
        var gear = ask.Kind is CostKind.GearWear or CostKind.GearRestore;
        var taken = gear ? n.WearApplied : (int)Math.Min(n.QuantityTaken, int.MaxValue);
        return new CostReceipt(ask.Item, ask.Amount, committed || uncertain ? taken : 0, committed)
        {
            RequestId = ask.RequestId, NativeTicket = n.Ticket, Kind = ask.Kind, Selection = ask.Selection, Uncertain = uncertain,
            Disposition = committed || uncertain ? DispositionOf(n.Disposition) : CostDisposition.None,
            Refusal = n.State == ItemCostsNative.Refused ? RefusalOf(n.Refusal) : CostRefusal.None,
            Reason = ItemsNative.Text(n.Reason, 160), DurabilityBefore = gear && n.DurabilityBefore >= 0 ? n.DurabilityBefore : null,
            DurabilityAfter = gear && n.DurabilityAfter >= 0 ? n.DurabilityAfter : null, RefundOf = ask.RefundOf, Step = n.Step,
        };
    }

    private static CostDisposition DispositionOf(uint d) => d switch
    {
        ItemCostsNative.Exact => CostDisposition.Exact,
        ItemCostsNative.WearWaivedByWorld => CostDisposition.WearWaivedByWorld,
        ItemCostsNative.ModifiedByOther => CostDisposition.ModifiedByOtherMod,
        ItemCostsNative.Partial => CostDisposition.Partial,
        _ => CostDisposition.None,
    };

    private static CostRefusal RefusalOf(uint r) => r is >= 1 and <= 10 ? (CostRefusal)r : CostRefusal.NotRun;

    /// <summary>The table and the world, as the managed side asks them: the native table in a game, a made-up one in tests.</summary>
    internal interface ITable
    {
        int Submit(in ItemCostsNative.SelectedCost cost, out ulong ticket);
        int Peek(ulong ticket, out ItemCostsNative.CostResult result);
        int Acknowledge(ulong ticket);
        bool Status(out ItemCostsNative.Status status);
    }

    private sealed class NativeTable(ItemCostsNative.Table* t) : ITable
    {
        public int Submit(in ItemCostsNative.SelectedCost cost, out ulong ticket)
        {
            ulong tk = 0;
            fixed (ItemCostsNative.SelectedCost* c = &cost)
            {
                var rc = t->Submit(0, c, &tk);
                ticket = tk;
                return rc;
            }
        }

        public int Peek(ulong ticket, out ItemCostsNative.CostResult result)
        {
            ItemCostsNative.CostResult r = default;
            r.Size = (uint)sizeof(ItemCostsNative.CostResult);
            var rc = t->Peek(0, ticket, &r);
            result = r;
            return rc;
        }

        public int Acknowledge(ulong ticket) => t->Acknowledge(0, ticket);

        public bool Status(out ItemCostsNative.Status status)
        {
            ItemCostsNative.Status s = default;
            s.Size = (uint)sizeof(ItemCostsNative.Status);
            var ok = t->GetStatus(&s) == ItemCostsNative.Ok;
            status = s;
            return ok;
        }
    }

    /// <summary>The runtime's seams (tests replace them) and the receipts' pump.</summary>
    internal static class Runtime
    {
        private static ITable? _table;
        private static bool _looked;

        /// <summary>The table (null: kiln.item_costs is not here).</summary>
        internal static ITable? Table
        {
            get
            {
                if (_table != null || _looked) return _table;
                _looked = true;
                var t = Services.Get<ItemCostsNative.Table>(ItemCostsNative.Service, ItemCostsNative.Version);
                if (t != null && t->Header.Size >= (uint)sizeof(ItemCostsNative.Table)) _table = new NativeTable(t);
                return _table;
            }
            set
            {
                _table = value;
                _looked = value != null;
            }
        }

        internal static Func<bool> StacksOn = () => Capabilities.Is(StacksCapability);
        internal static Func<bool> WearOn = () => Capabilities.Is(WearCapability);
        internal static Action<KilnEvent> Sink = ev => Events.Post(ev);
        internal static Func<string, Action<ulong>, IDisposable> AddTick = (name, step) => ServiceTickAccess.Add(name, step);
        /// <summary>Whether an entity is a player, in game, and its session ((handle &lt;&lt; 32) ^ joined step).</summary>
        internal static Func<EntityId, (bool IsPlayer, bool InGame, ulong Session)> SessionOf = DefaultSession;
        internal static Func<EntityRef, bool> Alive = e =>
        {
            try { return e.IsAlive; }
            catch (KilnUnavailableException) { return true; }  // no entity source: the native checks still hold
        };
        internal static Func<EntityId, IReadOnlyList<InventorySlot>?> ReadAll = e => Items.Read(e)?.Slots;
        internal static Func<ItemInstanceRef, (int Current, int Max)?> ReadDurability = r =>
            Items.GearOf(r) is { Durability: { } d, DurabilityMax: { } m } ? (d, m) : null;

        private static (bool, bool, ulong) DefaultSession(EntityId e)
        {
            try
            {
                if (Players.Find(e) is not { } p) return (false, false, 0);
                return (true, p.InGame, ((ulong)p.Handle << 32) ^ p.JoinedStep);
            }
            catch (NotSupportedException)
            {
                return (false, false, 0);
            }
        }

        /// <summary>For tests: forget the table, the seams and every cost.</summary>
        internal static void Reset()
        {
            _table = null;
            _looked = false;
            StacksOn = () => Capabilities.Is(StacksCapability);
            WearOn = () => Capabilities.Is(WearCapability);
            Sink = ev => Events.Post(ev);
            AddTick = (name, step) => ServiceTickAccess.Add(name, step);
            SessionOf = DefaultSession;
            Alive = e =>
            {
                try { return e.IsAlive; }
                catch (KilnUnavailableException) { return true; }
            };
            ReadAll = e => Items.Read(e)?.Slots;
            ReadDurability = r => Items.GearOf(r) is { Durability: { } d, DurabilityMax: { } m } ? (d, m) : null;
            lock (Gate)
            {
                InFlight.Clear();
                ByRequest.Clear();
                Settled.Clear();
                SettledOrder.Clear();
                Watched.Clear();
                _tick?.Dispose();
                _tick = null;
            }
        }

        private static IDisposable? _capsTick;
        private static (bool, string, bool, string, long) _reported = (false, "?", false, "?", -1);

        /// <summary>The two rows (Items' start reports them; they follow kiln.item_costs' status every 25 steps after).</summary>
        internal static void ReportCapabilities()
        {
            RefreshCapabilities();
            _capsTick ??= ServiceTickAccess.Add("kiln.item_costs capabilities", step => { if (step % 25 == 0) RefreshCapabilities(); });
        }

        internal static void RefreshCapabilities()
        {
            var t = Table;
            (bool, string, bool, string) now;
            if (t == null) now = (false, "kiln.item_costs is not loaded (an older Kiln)", false, "kiln.item_costs is not loaded (an older Kiln)");
            else if (!t.Status(out var st)) now = (false, "kiln.item_costs did not answer", false, "kiln.item_costs did not answer");
            else now = (st.Stacks != 0, ItemsNative.Text(st.StacksReason, 160), st.Wear != 0, ItemsNative.Text(st.WearReason, 160));
            var epoch = Capabilities.CurrentBinding.Epoch;
            if ((now.Item1, now.Item2, now.Item3, now.Item4, epoch) == _reported) return;
            _reported = (now.Item1, now.Item2, now.Item3, now.Item4, epoch);
            var reason = t == null ? CapabilityReason.ServiceMissing : CapabilityReason.Faulted;
            if (now.Item1) Capabilities.Report(StacksCapability, CapabilityState.Available);
            else Capabilities.Report(StacksCapability, CapabilityState.Unavailable, reason, now.Item2);
            if (now.Item3) Capabilities.Report(WearCapability, CapabilityState.Available);
            else Capabilities.Report(WearCapability, CapabilityState.Unavailable, reason, now.Item4);
        }

        /// <summary>Tick thread, Pre: every settled receipt is read, let go natively, reported and handed to its task.</summary>
        internal static void Pump()
        {
            var t = Table;
            if (t == null) return;
            List<Pending> live;
            lock (Gate)
            {
                if (InFlight.Count == 0) return;
                live = InFlight.Values.ToList();
            }
            foreach (var p in live)
            {
                if (t.Peek(p.Ticket, out var n) != ItemCostsNative.Ok || n.State == ItemCostsNative.Queued) continue;
                var receipt = ReceiptOf(n, p.Ask);
                t.Acknowledge(p.Ticket);
                lock (Gate)
                {
                    InFlight.Remove(p.Ticket);
                    ByRequest.Remove(p.Ask.RequestId);
                    Settled[p.Ask.RequestId] = (p.Cost, receipt);
                    SettledOrder.Enqueue(p.Ask.RequestId);
                    while (SettledOrder.Count > SettledKept) Settled.Remove(SettledOrder.Dequeue());
                }
                Report(receipt, p.Mod?.Info.Id ?? "kiln");
                if (!p.Retired) p.Tcs.TrySetResult(receipt);
            }
        }

        private static void Report(CostReceipt r, string origin)
        {
            if (r.State == CostState.Refused) return;
            try
            {
                Sink(new ItemCostSettledEvent(r, origin));
                if (r is { Committed: true, Taken: > 0, Selection: { } s } && r.Kind is CostKind.StackTake or CostKind.StackRefund)
                {
                    var change = new ItemChange(s.Slot, r.Item, r.Taken, EntityId.None);
                    var result = new ItemsResult(r.Taken == r.Requested ? ItemsStatus.Done : ItemsStatus.Partial, s.InventoryOwner.Id, r.Item, r.Requested,
                        r.Taken, [change], r.Reason, 0, 1, false);
                    Sink(r.Kind == CostKind.StackTake ? new ItemsTakenEvent(result, origin) : new ItemsGivenEvent(result, origin));
                }
            }
            catch (Exception e)
            {
                Backend.Current.Log(0, LogLevel.Error, $"kiln.item_costs: posting a receipt's events failed: {e.Message}");
            }
        }
    }
}

/// <summary>kiln.item_costs' table and records (native/core/abi/services/kiln_item_costs.h).</summary>
internal static unsafe class ItemCostsNative
{
    public const string Service = "kiln.item_costs";
    public const uint Version = 1;
    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4;
    public const int RequestConflict = 100, RequestClosed = 101, Busy = 102, WrongWorld = 103, StaleSession = 104;
    public const uint StackTake = 1, GearWear = 2, StackRefund = 3, GearRestore = 4;
    public const uint Queued = 0, Committed = 1, Refused = 2, Uncertain = 3;
    public const uint Exact = 0, WearWaivedByWorld = 1, ModifiedByOther = 2, Partial = 3;
    public const uint StaleSelection = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct SelectedCost  // 80 bytes
    {
        public uint Size, Kind;
        public ulong WorldEpoch, CharacterSession;
        public uint Holder, Reserved;
        public ulong Slot;
        public uint Item, ExpectedCount, ItemEntity, ItemStamp, Amount, AllowBreak;
        public ulong RequestLo, RequestHi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CostResult  // 264 bytes
    {
        public uint Size, State, GameError, Disposition;
        public ulong Ticket;
        public uint QuantityTaken, WearRequested;
        public int WearApplied;
        public uint Refusal;
        public int DurabilityBefore, DurabilityAfter;
        public uint Kind, Item;
        public ulong Slot;
        public uint ItemEntity, ItemStamp;
        public ulong WorldEpoch, RequestLo, RequestHi, Step;
        public fixed byte Reason[160];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status  // 368 bytes
    {
        public uint Size, Stacks, Wear, Held;
        public ulong Committed, Refused, Uncertain, WorldEpoch;
        public fixed byte StacksReason[160];
        public fixed byte WearReason[160];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<ulong, SelectedCost*, ulong*, int> Submit;
        public delegate* unmanaged[Cdecl]<ulong, ulong, CostResult*, int> Peek;
        public delegate* unmanaged[Cdecl]<ulong, ulong, int> Acknowledge;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
    }
}
