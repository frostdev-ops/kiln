// SPDX-License-Identifier: MIT
// Lab test of kiln.item_costs (dev.32; docs/kiln/services/item-costs.md), player-free. A chest gets two identical stacks of
// logs and a sword. Stacks: a take of 2 from the selected stack only (the other identical stack keeps its 5); the same
// request id again (the same receipt, nothing more taken); a selection read before that take (stale count: refused,
// nothing taken); a refund (the 2 come back). Gear: a wear of 7 through the game's durability debit; the same request id
// again (no second wear); an adjacent identical wear under a new id (wears once more); a wear that would break it without
// allowBreak (refused, unchanged); a restore of the first wear; a wear to 0 with allowBreak (broken); a wear of a broken
// sword (refused). Lines "PASS/FAIL ..." and "item-costs-test done: N PASS, M FAIL" in kiln.log.
using Kiln;

namespace ItemCostsTest;

public sealed class ItemCostsTestMod : Mod
{
    private const string Logs = "Material_T1_Wood_Logs";
    private const string Sword = "Weapon_T1_1H_Sword_Craft";

    private int _pass, _fail;
    private bool _stop, _ran;

    private void Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"{(ok ? "PASS" : "FAIL")} {what}");
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n && !_stop; i++) await Context.Scheduler.NextTick();
    }

    public override void OnUnload() => _stop = true;

    public override void OnLoad()
    {
        Context.Tick.Pre += (in TickInfo t) =>
        {
            if (_ran || t.Step % 25 != 0 || !Lifecycle.IsServerRunning || !Items.Available) return;
            _ran = true;
            _ = Run();
        };
    }

    private async Task Run()
    {
        try
        {
            for (var i = 0; i < 400 && !(ItemCosts.StacksAvailable && ItemCosts.WearAvailable && Items.GearReadAvailable); i++) await Steps(5);
            Check(ItemCosts.StacksAvailable, $"items.costs.exact is on: {Capabilities.Get(ItemCosts.StacksCapability)}");
            Check(ItemCosts.WearAvailable, $"items.costs.wear is on: {Capabilities.Get(ItemCosts.WearCapability)}");
            if (!ItemCosts.StacksAvailable) return;
            await Steps(25 * 2);
            var chest = await Chest();
            var owner = EntityRefOf(chest);
            await StackCosts(chest, owner);
            if (ItemCosts.WearAvailable) await GearCosts(chest, owner);
        }
        catch (Exception e)
        {
            Check(false, $"the test threw: {e}");
        }
        finally
        {
            Log.Info($"item-costs-test done: {_pass} PASS, {_fail} FAIL");
        }
    }

    private static EntityRef EntityRefOf(EntityId e)
    {
        try { return EntityRef.Of(e) is { IsNone: false } r ? r : new EntityRef(e, 0); }
        catch (KilnUnavailableException) { return new EntityRef(e, 0); }
    }

    private async Task<EntityId> Chest()
    {
        foreach (var e in Items.FindInventories(InventoryKinds.Containers))
            if (Items.Read(e) is { Initialized: true } c && c.FreeSlots() >= 6 && World.Position(e) is not null) return e;
        var bases = World.Find("BaseIdComponent");
        var near = bases.Count > 0 && World.Position(bases[0]) is { } b ? b with { X = b.X + 6 } : new WorldPosition(3806, 834, 1434);
        var chest = await Spawn.Entity("LootChest_Random_Cairn_Sarcophagus_Wood", near);
        await Steps(30);
        Log.Info($"item-costs-test: spawned chest {chest} at {near}");
        return chest;
    }

    private IReadOnlyList<InventorySlot> SlotsOf(EntityId chest, ItemId item) =>
        Items.Read(chest)?.Slots.Where(s => s.Item == item && !s.IsEmpty).ToList() ?? [];

    private async Task StackCosts(EntityId chest, EntityRef owner)
    {
        var logs = Items.Lookup(Logs)!.Id;
        foreach (var s in SlotsOf(chest, logs)) await Items.TakeSlot(chest, s);
        var g1 = await Items.Give(chest, logs, 5, options: GiveOptions.NoMerge);
        var g2 = await Items.Give(chest, logs, 5, options: GiveOptions.NoMerge);
        var two = SlotsOf(chest, logs);
        Check(g1.Status == ItemsStatus.Done && g2.Status == ItemsStatus.Done && two.Count == 2 && two.All(s => s.Count == 5), $"two identical stacks of 5 logs in chest {chest}: {string.Join(", ", two.Select(s => $"{s.Slot}={s.Count}"))}");
        if (two.Count != 2) return;
        var a = ItemSelection.Of(owner, two[0]);
        var bSlot = two[1].Slot;
        Check(ItemCosts.Check(a, 2) is null, $"Check: the selected stack can pay 2: {ItemCosts.Check(a, 2) ?? "ok"}");

        var id = Guid.NewGuid();
        var r = await ItemCosts.Take(a, 2, id);
        var now = SlotsOf(chest, logs);
        Check(r is { Committed: true, Taken: 2, Disposition: CostDisposition.Exact, Kind: CostKind.StackTake } && r.NativeTicket != 0,
            $"a take of 2 from the selected slot commits exactly: {r}");
        Check(now.FirstOrDefault(s => s.Slot == a.Slot)?.Count == 3 && now.FirstOrDefault(s => s.Slot == bSlot)?.Count == 5,
            $"only the selected slot was debited (5 -> 3), the identical stack keeps 5: {string.Join(", ", now.Select(s => $"{s.Slot}={s.Count}"))}");

        var again = await ItemCosts.Take(a, 2, id);
        await Steps(2);
        Check(ReferenceEquals(again, r) && SlotsOf(chest, logs).Sum(s => s.Count) == 8, $"the same request id again: the same receipt, nothing more taken (8 logs): {again}");

        var stale = await ItemCosts.Take(a, 1, Guid.NewGuid());  // read when the slot held 5; it holds 3 now
        Check(stale is { Committed: false, Refusal: CostRefusal.StaleSelection } && SlotsOf(chest, logs).Sum(s => s.Count) == 8,
            $"a selection read before the take (count 5, the slot holds 3) is refused and takes nothing: {stale}");

        var fresh = ItemSelection.Of(owner, SlotsOf(chest, logs).First(s => s.Slot == a.Slot));
        var all = await ItemCosts.Take(fresh, 3, Guid.NewGuid());
        Check(all is { Committed: true, Taken: 3 } && SlotsOf(chest, logs).FirstOrDefault(s => s.Slot == a.Slot) is null,
            $"a fresh selection takes the last 3 of the stack (the slot is empty now): {all}");

        var back = await ItemCosts.Refund(r, Guid.NewGuid());
        Check(back is { Committed: true, Taken: 2, Kind: CostKind.StackRefund } && back.RefundOf == r.RequestId && SlotsOf(chest, logs).Sum(s => s.Count) == 7,
            $"the first take's refund gives 2 back (7 logs): {back}");
        var none = await ItemCosts.Refund(stale, Guid.NewGuid());
        Check(none is { Committed: false, Refusal: CostRefusal.NothingToRefund }, $"a refused take is never refunded: {none}");
    }

    private async Task GearCosts(EntityId chest, EntityRef owner)
    {
        var sword = Items.Lookup(Sword)!.Id;
        var give = await Items.Give(chest, sword, 1, new Gear());
        await Steps(3);  // the game computes durability in the item's first step
        var slot = Items.Read(chest)?.Slots.FirstOrDefault(s => s.Item == sword && !s.Ref.IsNone && give.Changes.Any(c => c.Ref == s.Ref));
        var g0 = slot is null ? null : Items.GearOf(slot.Ref);
        Check(slot is not null && g0 is { Durability: > 30 }, $"a sword with durability in chest {chest}: {g0}");
        if (slot is null || g0?.Durability is not { } d0) return;
        var sel = ItemSelection.Of(owner, slot);

        var id = Guid.NewGuid();
        var w = await ItemCosts.Wear(sel, 7, id);
        var d1 = Items.GearOf(sel.Instance)?.Durability;
        Check(w is { Committed: true, Kind: CostKind.GearWear } && w.Taken == 7 && w.DurabilityBefore == d0 && w.DurabilityAfter == d0 - 7 && d1 == d0 - 7,
            $"a wear of 7 through the game's debit: {d0} -> {d1}: {w}");
        Check(w.Disposition == CostDisposition.Exact, $"no other hook changed it (Exact): {w.Disposition}");

        var replay = await ItemCosts.Wear(sel, 7, id);
        await Steps(2);
        Check(ReferenceEquals(replay, w) && Items.GearOf(sel.Instance)?.Durability == d0 - 7, $"the same request id again: no second wear ({Items.GearOf(sel.Instance)?.Durability})");

        var next = await ItemCosts.Wear(sel, 7, Guid.NewGuid());
        Check(next is { Committed: true, Taken: 7 } && Items.GearOf(sel.Instance)?.Durability == d0 - 14,
            $"an adjacent identical wear under a new id wears once more ({Items.GearOf(sel.Instance)?.Durability}): {next}");

        var left = Items.GearOf(sel.Instance)?.Durability ?? 0;
        var refused = await ItemCosts.Wear(sel, left, Guid.NewGuid());
        Check(refused is { Committed: false, Refusal: CostRefusal.WouldBreak } && Items.GearOf(sel.Instance)?.Durability == left,
            $"a wear of all {left} without allowBreak is refused and changes nothing: {refused}");

        var restore = await ItemCosts.Refund(w, Guid.NewGuid());
        Check(restore is { Committed: true, Taken: 7, Kind: CostKind.GearRestore } && Items.GearOf(sel.Instance)?.Durability == left + 7,
            $"the first wear's restore gives 7 back ({Items.GearOf(sel.Instance)?.Durability}): {restore}");

        var all = Items.GearOf(sel.Instance)?.Durability ?? 0;
        var broke = await ItemCosts.Wear(sel, all + 5, Guid.NewGuid(), allowBreak: true);
        await Steps(2);
        var broken = Items.GearOf(sel.Instance);
        Check(broke is { Committed: true } && broke.Taken == all && broken?.Durability == 0,
            $"a wear past 0 with allowBreak breaks it (the game clamps at 0; {all} applied): {broke}; now {broken}");
        var dead = await ItemCosts.Wear(sel, 1, Guid.NewGuid(), allowBreak: true);
        Check(dead is { Committed: false, Refusal: CostRefusal.Broken }, $"a broken sword refuses any wear: {dead}");
    }
}
