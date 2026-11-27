// SPDX-License-Identifier: MIT
// Revive costs through the exact-cost adapter (kiln.item_costs, Kiln.Items' ItemCosts; dossier 19e 5.2): a cost is quoted
// before anything starts and again at completion (ItemCosts.Check: a read, nothing moves), then taken exactly once from one
// slot (ItemCosts.Take) under one request id per revive operation and world. The receipt is Kiln.Items' CostReceipt:
// committed, refused or uncertain. A refusal after a commit keeps its receipt: no refund is invented, and no cross-store
// atomicity is claimed.
using System.Security.Cryptography;

namespace Kiln;

internal interface IPetCosts
{
    /// <summary>Whether the owner can pay the cost now (no item moves). Null when it can; else why not.</summary>
    Task<string?> Quote(PetOwner owner, PetCostDefinition cost, ItemSelection? selection);
    /// <summary>Takes the cost from one slot, all or nothing, under <paramref name="requestId"/> (one debit per id); the
    /// receipt says what was taken, or that it is not known (<see cref="CostReceipt.Uncertain"/>).</summary>
    Task<CostReceipt> Commit(PetOwner owner, PetCostDefinition cost, ItemSelection? selection, Guid requestId);
}

/// <summary>Costs through kiln.item_costs. With no selection, the first slot of the owner's inventories holding a plain
/// stack of the whole cost pays it (<see cref="ItemCosts.Choose"/>, chosen again at each quote and at the commit); a
/// selection must be the owner's plain stack of the cost's item, holding at least the cost, and only that slot pays.</summary>
internal sealed class GamePetCosts : IPetCosts
{
    // the adapter's three calls (tests replace them)
    internal Func<EntityRef, ItemId, int, ItemSelection?> Choose { get; init; } = (owner, item, count) => ItemCosts.Choose(owner, item, count);
    internal Func<ItemSelection, int, string?> Check { get; init; } = (selection, count) => ItemCosts.Check(selection, count);
    internal Func<ItemSelection, int, Guid, Task<CostReceipt>> Take { get; init; } = ItemCosts.Take;

    /// <summary>The request id of a revive's debit: one per operation and world, so a repeat of the operation in the same
    /// world is the same debit, and an operation id used again in a later world never answers with an old world's receipt
    /// (kiln.item_costs keeps a request id's receipt for the life of the process).</summary>
    internal static Guid RequestFor(Guid operation, ulong worldEpoch)
    {
        Span<byte> data = stackalloc byte[16 + 8 + 16];
        "kiln.pets.revive"u8.CopyTo(data);
        operation.TryWriteBytes(data[16..]);
        BitConverter.TryWriteBytes(data[32..], worldEpoch);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        hash[7] = (byte)((hash[7] & 0x0f) | 0x50);   // a name-based id (RFC 9562 version 5 layout)
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash[..16]);
    }

    // the selection that pays: the caller's (checked against the cost), or the one the adapter chooses
    private ItemSelection? Select(PetOwner owner, PetCostDefinition cost, ItemSelection? selection, out string? refused)
    {
        refused = null;
        if (selection is null)
        {
            var chosen = Choose(owner.Entity, cost.Item, cost.Count);
            if (chosen is null) refused = $"CostUnavailable: no single stack of {cost.Count} x {cost.Item} in the owner's inventories";
            return chosen;
        }
        if (selection.InventoryOwner.Id != owner.Entity.Id) refused = "CostUnavailable: StaleSelection: the selected slot is not the owner's";
        else if (selection.ExpectedItem != cost.Item) refused = $"CostUnavailable: StaleSelection: the selected slot holds {selection.ExpectedItem}, the cost is {cost.Item}";
        else if (!selection.IsStack) refused = "CostUnavailable: StaleSelection: a revive cost is taken from a plain stack, not a gear item";
        else if (selection.ExpectedCount < cost.Count) refused = $"CostUnavailable: StaleSelection: the selected stack holds {selection.ExpectedCount}, the cost is {cost.Count}";
        return refused is null ? selection : null;
    }

    public Task<string?> Quote(PetOwner owner, PetCostDefinition cost, ItemSelection? selection)
    {
        try
        {
            if (Select(owner, cost, selection, out var refused) is not { } paying) return Task.FromResult(refused);
            return Task.FromResult(Check(paying, cost.Count) is { } why ? $"CostUnavailable: {why}" : null);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return Task.FromResult<string?>($"CostUnavailable: {e.Message}");
        }
    }

    public Task<CostReceipt> Commit(PetOwner owner, PetCostDefinition cost, ItemSelection? selection, Guid requestId)
    {
        ItemSelection? paying = null;
        try
        {
            paying = Select(owner, cost, selection, out var refused);
            if (paying is null) return Task.FromResult(Refused(cost, selection, requestId, CostRefusal.StaleSelection, refused!));
            return Take(paying, cost.Count, requestId);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // the adapter refused before it ran (off, not on the tick thread, a shape it does not take): nothing moved
            return Task.FromResult(Refused(cost, paying ?? selection, requestId, CostRefusal.NotRun, $"CostUnavailable: {e.Message}"));
        }
    }

    private static CostReceipt Refused(PetCostDefinition cost, ItemSelection? selection, Guid requestId, CostRefusal why, string reason) =>
        new(cost.Item, cost.Count, 0, false) { RequestId = requestId, Selection = selection, Refusal = why, Reason = reason };
}
