// SPDX-License-Identifier: MIT
// The exact-cost adapter in its minimal form (19e's, which the taming lane widens): a cost is quoted before anything
// starts (a dry run of the exact take), quoted again at completion, then committed exactly once; the receipt says what was
// taken. A refusal after a commit keeps its receipt: no refund is invented, and no cross-store atomicity is claimed.
namespace Kiln;

internal interface IPetCosts
{
    /// <summary>Whether the owner can pay the cost now (no item moves). Null when it can; else why not.</summary>
    Task<string?> Quote(PetOwner owner, PetCostDefinition cost, ItemSelection? selection);
    /// <summary>Takes the cost, all or nothing; the receipt says what was taken (Committed true once it is gone).</summary>
    Task<CostReceipt> Commit(PetOwner owner, PetCostDefinition cost, ItemSelection? selection);
}

/// <summary>Costs through kiln.items: Items.Take, exact count, from the selected slot first (only that slot when one was
/// selected), all or nothing.</summary>
internal sealed class GamePetCosts : IPetCosts
{
    public async Task<string?> Quote(PetOwner owner, PetCostDefinition cost, ItemSelection? selection)
    {
        try
        {
            var r = await Items.Take(owner.Entity.Id, cost.Item, cost.Count, selection?.Slot, exactSlot: selection is not null, dryRun: true);
            return r.Status == ItemsStatus.Done ? null : $"CostUnavailable: {r.Message}";
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return $"CostUnavailable: {e.Message}";
        }
    }

    public async Task<CostReceipt> Commit(PetOwner owner, PetCostDefinition cost, ItemSelection? selection)
    {
        try
        {
            var r = await Items.Take(owner.Entity.Id, cost.Item, cost.Count, selection?.Slot, exactSlot: selection is not null);
            var done = r.Status == ItemsStatus.Done ? r.Done : 0;
            return new CostReceipt(cost.Item, cost.Count, done, done > 0);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return new CostReceipt(cost.Item, cost.Count, 0, false);
        }
    }
}
