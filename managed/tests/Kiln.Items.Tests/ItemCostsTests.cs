// SPDX-License-Identifier: MIT
// ItemCosts (kiln.item_costs, the exact-cost adapter of dossier 19e 5.2) offline, over a made-up kiln.item_costs table
// that keeps tickets, request ids and receipts as the native one does (native/services/items/tests/costs_test.cpp tests
// that one): the request each call submits, one debit per request id (the same task while it runs, the same receipt
// after, a conflict for another cost under the same id), receipts as the native side settled them (committed, refused,
// uncertain, wear modified by another hook or waived by the world), refunds of what a receipt took (never of an uncertain
// one), the one error rule, and the events. What the game does is the lab test mods/tests/item-costs-test.
using Kiln;
using Xunit;

namespace Kiln.ItemsTests;

public sealed unsafe class ItemCostsTests : IDisposable
{
    private const uint Bait = 0xabcd, Pick = 0x2525bb44;
    private static readonly EntityRef Player = new(new EntityId(3), 77);
    private static readonly SlotId Slot = SlotId.Of(new EntityId(2001), 3);

    private sealed class Backend : IKilnBackend
    {
        public bool Tick = true;
        public readonly List<string> Logged = new();
        public string Version => "test";
        public bool IsTickThread => Tick;
        public bool HasFeature(string name) => false;
        public nint Service(string name, uint minVersion) => 0;
        public void Log(ulong owner, LogLevel level, string text) => Logged.Add(text);
    }

    internal sealed class FakeTable : ItemCosts.ITable
    {
        public readonly List<ItemCostsNative.SelectedCost> Submitted = new();
        public readonly Dictionary<ulong, ItemCostsNative.CostResult> Results = new();
        public readonly Dictionary<(ulong, ulong), ulong> ByRequest = new();
        public readonly HashSet<ulong> Acked = new();
        public readonly HashSet<(ulong, ulong)> Closed = new();
        public int NextRc;  // a submit refusal to answer once
        public ulong NextTicket = 100;
        public ulong Epoch = 5;
        public bool Stacks = true, Wear = true;

        public int Submit(in ItemCostsNative.SelectedCost c, out ulong ticket)
        {
            ticket = 0;
            if (NextRc != 0)
            {
                var rc = NextRc;
                NextRc = 0;
                return rc;
            }
            var key = (c.RequestLo, c.RequestHi);
            if (ByRequest.TryGetValue(key, out var held))
            {
                ticket = held;
                return ItemCostsNative.Ok;
            }
            if (Closed.Contains(key)) return ItemCostsNative.RequestClosed;
            ticket = NextTicket++;
            ByRequest[key] = ticket;
            Submitted.Add(c);
            Results[ticket] = new ItemCostsNative.CostResult { Size = (uint)sizeof(ItemCostsNative.CostResult), State = ItemCostsNative.Queued, Ticket = ticket, Kind = c.Kind, Item = c.Item };
            return ItemCostsNative.Ok;
        }

        public int Peek(ulong ticket, out ItemCostsNative.CostResult r) =>
            Results.TryGetValue(ticket, out r) ? ItemCostsNative.Ok : ItemCostsNative.NotFound;

        public int Acknowledge(ulong ticket)
        {
            if (!Results.TryGetValue(ticket, out var r)) return ItemCostsNative.NotFound;
            if (r.State == ItemCostsNative.Queued) return ItemCostsNative.InvalidArgument;
            Acked.Add(ticket);
            Results.Remove(ticket);
            foreach (var (k, t) in ByRequest.ToList())
                if (t == ticket)
                {
                    ByRequest.Remove(k);
                    Closed.Add(k);
                }
            return ItemCostsNative.Ok;
        }

        public bool Status(out ItemCostsNative.Status s)
        {
            s = new ItemCostsNative.Status { Size = (uint)sizeof(ItemCostsNative.Status), Stacks = Stacks ? 1u : 0, Wear = Wear ? 1u : 0, WorldEpoch = Epoch };
            return true;
        }

        public void Settle(ulong ticket, uint state, uint taken = 0, int applied = 0, uint disposition = 0, uint refusal = 0, int before = -1, int after = -1, string reason = "")
        {
            var r = Results[ticket];
            r.State = state;
            r.QuantityTaken = taken;
            r.WearApplied = applied;
            r.Disposition = disposition;
            r.Refusal = refusal;
            r.DurabilityBefore = before;
            r.DurabilityAfter = after;
            r.Step = 42;
            var bytes = System.Text.Encoding.UTF8.GetBytes(reason);
            for (var i = 0; i < bytes.Length && i < 159; i++) r.Reason[i] = bytes[i];
            Results[ticket] = r;
        }
    }

    private readonly FakeTable _table = new();
    private readonly List<KilnEvent> _events = new();
    private readonly Backend _backend = new();
    private readonly IKilnBackend _was = Kiln.Backend.Current;
    private readonly ModContext? _caller = HostAccess.Current;
    private readonly ModContext _mod = HostAccess.CreateContext(new ModInfo("mod-a", "mod-a", "1", ""), 11, "");

    public ItemCostsTests()
    {
        Kiln.Backend.Current = _backend;
        ItemCosts.Runtime.Reset();
        ItemCosts.Runtime.Table = _table;
        ItemCosts.Runtime.StacksOn = () => true;
        ItemCosts.Runtime.WearOn = () => true;
        ItemCosts.Runtime.Sink = _events.Add;
        ItemCosts.Runtime.SessionOf = e => e == Player.Id ? (true, true, 0x99ul) : (false, false, 0ul);
        ItemCosts.Runtime.AddTick = (_, _) => new NoTick();
        HostAccess.Current = _mod;
    }

    private sealed class NoTick : IDisposable
    {
        public void Dispose() { }
    }

    public void Dispose()
    {
        ItemCosts.Runtime.Reset();
        HostAccess.Current = _caller;
        Kiln.Backend.Current = _was;
    }

    private static ItemSelection Stack(int count = 5) => new(Player, Slot, new ItemId(Bait), count, ItemInstanceRef.None);
    private static ItemSelection Tool() => new(Player, Slot, new ItemId(Pick), 1, new ItemInstanceRef(new EntityRef(new EntityId(4100), Stamped(17)), new ItemId(Pick)));
    private static ulong Stamped(uint stamp) => IdentitySources.Stamp(EntitySourceOrder.Items, stamp);

    [Fact]
    public void A_stack_take_submits_the_selected_slot_exactly_and_completes_with_its_receipt()
    {
        var id = Guid.NewGuid();
        var task = ItemCosts.Take(Stack(), 2, id);
        var c = Assert.Single(_table.Submitted);
        Assert.Equal((ItemCostsNative.StackTake, 5ul, 0x99ul, 3u, Slot.Value, Bait, 5u, 0u, 0u, 2u), (c.Kind, c.WorldEpoch, c.CharacterSession, c.Holder, c.Slot, c.Item, c.ExpectedCount, c.ItemEntity, c.ItemStamp, c.Amount));
        Assert.Equal(id, ItemCosts.RequestIdOf(c.RequestLo, c.RequestHi));
        Assert.False(task.IsCompleted);
        ItemCosts.Runtime.Pump();
        Assert.False(task.IsCompleted);  // still queued: nothing settled, nothing acknowledged
        Assert.Empty(_table.Acked);
        _table.Settle(100, ItemCostsNative.Committed, taken: 2);
        ItemCosts.Runtime.Pump();
        var r = task.Result;
        Assert.Equal((new ItemId(Bait), 2, 2, true), (r.Item, r.Requested, r.Taken, r.Committed));
        Assert.Equal((CostKind.StackTake, CostState.Committed, CostDisposition.Exact, CostRefusal.None, id, 100ul), (r.Kind, r.State, r.Disposition, r.Refusal, r.RequestId, r.NativeTicket));
        Assert.Equal(Stack(), r.Selection);
        Assert.Contains(100ul, _table.Acked);
        var settled = Assert.Single(_events.OfType<ItemCostSettledEvent>());
        Assert.Equal(("mod-a", r), (settled.Origin, settled.Receipt));
        var taken = Assert.Single(_events.OfType<ItemsTakenEvent>());
        Assert.Equal((new EntityId(3), new ItemId(Bait), 2, "mod-a"), (taken.Target, taken.Item, taken.Count, taken.Origin));
    }

    [Fact]
    public void One_request_id_is_one_debit_the_same_task_while_it_runs_and_the_same_receipt_after()
    {
        var id = Guid.NewGuid();
        var first = ItemCosts.Take(Stack(), 2, id);
        var again = ItemCosts.Take(Stack(), 2, id);
        Assert.Same(first, again);
        Assert.Single(_table.Submitted);
        _table.Settle(100, ItemCostsNative.Committed, taken: 2);
        ItemCosts.Runtime.Pump();
        var after = ItemCosts.Take(Stack(), 2, id);
        Assert.True(after.IsCompletedSuccessfully);
        Assert.Same(first.Result, after.Result);
        Assert.Single(_table.Submitted);
        // another cost under the same id: a conflict, nothing submitted
        var other = ItemCosts.Take(Stack(), 3, id).Result;
        Assert.Equal((false, CostRefusal.RequestConflict), (other.Committed, other.Refusal));
        Assert.Single(_table.Submitted);
    }

    [Fact]
    public void A_request_id_the_native_side_closed_is_never_a_second_debit()
    {
        var id = Guid.NewGuid();
        _table.Closed.Add(ItemCosts.Halves(id));
        var r = ItemCosts.Take(Stack(), 1, id).Result;
        Assert.Equal((false, CostRefusal.RequestClosed, 0), (r.Committed, r.Refusal, r.Taken));
        Assert.Empty(_table.Submitted);
    }

    [Theory]
    [InlineData(ItemCostsNative.WrongWorld, CostRefusal.WrongWorld)]
    [InlineData(ItemCostsNative.StaleSession, CostRefusal.StaleSession)]
    [InlineData(ItemCostsNative.Busy, CostRefusal.Busy)]
    [InlineData(ItemCostsNative.RequestConflict, CostRefusal.RequestConflict)]
    public void A_submit_the_native_side_refuses_is_a_refused_receipt(int rc, CostRefusal refusal)
    {
        _table.NextRc = rc;
        var r = ItemCosts.Take(Stack(), 1, Guid.NewGuid()).Result;
        Assert.Equal((false, CostState.Refused, refusal), (r.Committed, r.State, r.Refusal));
    }

    [Fact]
    public void Refused_and_uncertain_receipts_say_so_and_are_never_called_committed()
    {
        var a = ItemCosts.Take(Stack(), 1, Guid.NewGuid());
        var b = ItemCosts.Take(Stack(), 2, Guid.NewGuid());
        _table.Settle(100, ItemCostsNative.Refused, refusal: ItemCostsNative.StaleSelection, reason: "the slot holds 4 now");
        _table.Settle(101, ItemCostsNative.Uncertain, taken: 2, disposition: ItemCostsNative.Exact, reason: "the game's take faulted");
        ItemCosts.Runtime.Pump();
        Assert.Equal((false, CostState.Refused, CostRefusal.StaleSelection, "the slot holds 4 now", 0), (a.Result.Committed, a.Result.State, a.Result.Refusal, a.Result.Reason, a.Result.Taken));
        Assert.Equal((false, CostState.Uncertain, 2), (b.Result.Committed, b.Result.State, b.Result.Taken));
        Assert.True(b.Result.Uncertain);
        // the uncertain one is reported (it may have changed the slot); the refused one is not an event
        Assert.Single(_events.OfType<ItemCostSettledEvent>());
        Assert.Empty(_events.OfType<ItemsTakenEvent>());
    }

    [Fact]
    public void A_gear_wear_names_the_stamped_item_and_reports_what_the_game_applied()
    {
        var w = ItemCosts.Wear(Tool(), 7, Guid.NewGuid());
        var c = Assert.Single(_table.Submitted);
        Assert.Equal((ItemCostsNative.GearWear, 4100u, 17u, 1u, 7u, 0u), (c.Kind, c.ItemEntity, c.ItemStamp, c.ExpectedCount, c.Amount, c.AllowBreak));
        _table.Settle(100, ItemCostsNative.Committed, applied: 3, disposition: ItemCostsNative.ModifiedByOther, before: 180, after: 177);
        ItemCosts.Runtime.Pump();
        var r = w.Result;
        Assert.Equal((CostKind.GearWear, 7, 3, CostDisposition.ModifiedByOtherMod, (int?)180, (int?)177, true), (r.Kind, r.Requested, r.Taken, r.Disposition, r.DurabilityBefore, r.DurabilityAfter, r.Committed));
        var waived = ItemCosts.Wear(Tool(), 2, Guid.NewGuid(), allowBreak: true);
        Assert.Equal(1u, _table.Submitted[1].AllowBreak);
        _table.Settle(101, ItemCostsNative.Committed, disposition: ItemCostsNative.WearWaivedByWorld, before: 50, after: 50);
        ItemCosts.Runtime.Pump();
        Assert.Equal((CostDisposition.WearWaivedByWorld, 0, true), (waived.Result.Disposition, waived.Result.Taken, waived.Result.Committed));
        Assert.Empty(_events.OfType<ItemsTakenEvent>());  // a wear takes no items
        Assert.Equal(2, _events.OfType<ItemCostSettledEvent>().Count());
    }

    [Fact]
    public void A_refund_gives_back_exactly_what_a_receipt_took_and_never_what_an_uncertain_one_may_have()
    {
        var t = ItemCosts.Take(Stack(), 2, Guid.NewGuid());
        var w = ItemCosts.Wear(Tool(), 7, Guid.NewGuid());
        var u = ItemCosts.Take(Stack(), 1, Guid.NewGuid());
        _table.Settle(100, ItemCostsNative.Committed, taken: 2);
        _table.Settle(101, ItemCostsNative.Committed, applied: 6, disposition: ItemCostsNative.ModifiedByOther, before: 100, after: 94);
        _table.Settle(102, ItemCostsNative.Uncertain, taken: 1);
        ItemCosts.Runtime.Pump();
        var rid = Guid.NewGuid();
        var back = ItemCosts.Refund(t.Result, rid);
        var c = _table.Submitted[3];
        Assert.Equal((ItemCostsNative.StackRefund, 3u, Slot.Value, Bait, 2u, 0u, 0u), (c.Kind, c.Holder, c.Slot, c.Item, c.Amount, c.ExpectedCount, c.ItemEntity));
        var restore = ItemCosts.Refund(w.Result, Guid.NewGuid());
        var g = _table.Submitted[4];
        Assert.Equal((ItemCostsNative.GearRestore, 4100u, 17u, 6u), (g.Kind, g.ItemEntity, g.ItemStamp, g.Amount));
        var none = ItemCosts.Refund(u.Result, Guid.NewGuid());
        Assert.True(none.IsCompletedSuccessfully);
        Assert.Equal((false, CostRefusal.NothingToRefund), (none.Result.Committed, none.Result.Refusal));
        Assert.Equal(5, _table.Submitted.Count);
        _table.Settle(103, ItemCostsNative.Committed, taken: 2);
        ItemCosts.Runtime.Pump();
        Assert.Equal((CostKind.StackRefund, 2, true, t.Result.RequestId), (back.Result.Kind, back.Result.Taken, back.Result.Committed, back.Result.RefundOf));
        var given = Assert.Single(_events.OfType<ItemsGivenEvent>());
        Assert.Equal((new EntityId(3), 2), (given.Target, given.Count));
        Assert.False(restore.IsCompleted);
    }

    [Fact]
    public void Bad_arguments_throw_and_an_off_capability_names_itself()
    {
        Assert.Throws<ArgumentNullException>(() => { _ = ItemCosts.Take(null!, 1, Guid.NewGuid()); });
        Assert.Throws<ArgumentException>(() => { _ = ItemCosts.Take(Stack(), 1, Guid.Empty); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = ItemCosts.Take(Stack(), 0, Guid.NewGuid()); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = ItemCosts.Take(Stack(5), 6, Guid.NewGuid()); });
        Assert.Throws<ArgumentException>(() => { _ = ItemCosts.Take(Tool(), 1, Guid.NewGuid()); });   // a tool is worn, not taken
        Assert.Throws<ArgumentException>(() => { _ = ItemCosts.Wear(Stack(), 1, Guid.NewGuid()); });  // a stack has no durability
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = ItemCosts.Wear(Tool(), 32768, Guid.NewGuid()); });
        Assert.Throws<ArgumentException>(() => { _ = ItemCosts.Take(Stack() with { InventoryOwner = EntityRef.None }, 1, Guid.NewGuid()); });
        Assert.Throws<ArgumentException>(() => { _ = ItemCosts.Take(Stack() with { Slot = default }, 1, Guid.NewGuid()); });
        Assert.Empty(_table.Submitted);
        ItemCosts.Runtime.StacksOn = () => false;
        var e = Assert.Throws<KilnUnavailableException>(() => { _ = ItemCosts.Take(Stack(), 1, Guid.NewGuid()); });
        Assert.Equal(ItemCosts.StacksCapability, e.Capability);
        ItemCosts.Runtime.WearOn = () => false;
        e = Assert.Throws<KilnUnavailableException>(() => { _ = ItemCosts.Wear(Tool(), 1, Guid.NewGuid()); });
        Assert.Equal(ItemCosts.WearCapability, e.Capability);
        _backend.Tick = false;
        ItemCosts.Runtime.StacksOn = () => true;
        Assert.Throws<InvalidOperationException>(() => { _ = ItemCosts.Take(Stack(), 1, Guid.NewGuid()); });
    }

    [Fact]
    public void A_player_who_left_or_rejoined_refuses_before_anything_is_submitted()
    {
        ItemCosts.Runtime.SessionOf = _ => (true, false, 0ul);  // a player, not in game
        var r = ItemCosts.Take(Stack(), 1, Guid.NewGuid()).Result;
        Assert.Equal((false, CostRefusal.StaleSession), (r.Committed, r.Refusal));
        Assert.Empty(_table.Submitted);
        // a holder that is no player (a chest) carries session 0
        ItemCosts.Runtime.SessionOf = _ => (false, false, 0ul);
        ItemCosts.Take(Stack() with { InventoryOwner = new EntityRef(new EntityId(5000), 0) }, 1, Guid.NewGuid());
        Assert.Equal(0ul, Assert.Single(_table.Submitted).CharacterSession);
    }

    [Fact]
    public void A_cost_whose_mod_unloaded_still_settles_and_is_acknowledged()
    {
        var t = ItemCosts.Take(Stack(), 2, Guid.NewGuid());
        _mod.Retire();
        _table.Settle(100, ItemCostsNative.Committed, taken: 2);
        ItemCosts.Runtime.Pump();
        Assert.Contains(100ul, _table.Acked);   // never left in the native table
        Assert.False(t.IsCompleted);            // the unloaded mod's continuations do not run
        Assert.Single(_events.OfType<ItemCostSettledEvent>());  // the debit is still reported
    }

    [Fact]
    public void A_cost_a_Kiln_service_submits_for_a_mod_completes_after_that_mod_unloaded_and_still_names_it()
    {
        var t = ItemCosts.AsService(() => ItemCosts.Take(Stack(), 2, Guid.NewGuid()));
        _mod.Retire();
        _table.Settle(100, ItemCostsNative.Committed, taken: 2);
        ItemCosts.Runtime.Pump();
        Assert.True(t.IsCompletedSuccessfully);                              // the service's receipt, whoever unloaded
        Assert.Equal("mod-a", Assert.Single(_events.OfType<ItemCostSettledEvent>()).Origin);
        Assert.Equal(5ul, t.Result.WorldEpoch);
        // a service's refund of a retired mod's cost still runs (an escrow given back when its mod unloads)
        var back = ItemCosts.AsService(() => ItemCosts.Refund(t.Result, Guid.NewGuid()));
        Assert.Equal(2, _table.Submitted.Count);
        Assert.Equal(5ul, _table.Submitted[1].WorldEpoch);
        Assert.False(back.IsCompleted);
    }

    [Fact]
    public void A_selection_is_built_from_a_slot_as_read()
    {
        var slot = new InventorySlot(Slot, new ItemId(Bait), 4, EntityId.None, 0, null, InventoryCategory.Generic, 0, false, false, false, 0);
        Assert.Equal(new ItemSelection(Player, Slot, new ItemId(Bait), 4, ItemInstanceRef.None), ItemSelection.Of(Player, slot));
        var gear = slot with { Item = new ItemId(Pick), Count = 1, ItemEntity = new EntityId(4100), IsGear = true, Ref = Tool().Instance };
        var s = ItemSelection.Of(Player, gear);
        Assert.Equal((Tool().Instance, 1, false), (s.Instance, s.ExpectedCount, s.IsStack));
        Assert.Throws<ArgumentException>(() => ItemSelection.Of(Player, slot with { Count = 0 }));
    }

    // ---- review of dev.32 TAME: refunds that can never double-pay ------------------------------------------------------
    // ItemCosts.Refund gave back what any receipt it was handed said, once per *refund* request id: the same paid receipt
    // under a second request id was given back again, a copy with a larger Taken gave back more, and a receipt of an earlier
    // world was refunded into the world running now (the refund carried the current world's epoch).

    // a refund's answer that must already be there (a refused one never waits for a step): never blocks a test
    private static CostReceipt Now(Task<CostReceipt> t)
    {
        Assert.True(t.IsCompleted, "the refund was submitted: it did not answer at once");
        return t.Result;
    }

    private CostReceipt Paid(int count = 2, ulong ticket = 100)
    {
        var t = ItemCosts.Take(Stack(), count, Guid.NewGuid());
        _table.Settle(ticket, ItemCostsNative.Committed, taken: (uint)count);
        ItemCosts.Runtime.Pump();
        return t.Result;
    }

    [Fact]
    public void A_paid_receipt_is_given_back_once_whatever_request_id_asks_again()
    {
        var paid = Paid();
        var first = Guid.NewGuid();
        var back = ItemCosts.Refund(paid, first);
        Assert.Equal(2, _table.Submitted.Count);
        Assert.Same(back, ItemCosts.Refund(paid, first));                  // the same refund request: the same task
        var twice = Now(ItemCosts.Refund(paid, Guid.NewGuid()));         // another request while it runs
        Assert.Equal((false, "AlreadyRefunded"), (twice.Committed, twice.Refusal.ToString()));
        _table.Settle(101, ItemCostsNative.Committed, taken: 2);
        ItemCosts.Runtime.Pump();
        Assert.True(Now(back).Committed);
        var thrice = Now(ItemCosts.Refund(paid, Guid.NewGuid()));        // and after it gave everything back
        Assert.Equal((false, "AlreadyRefunded"), (thrice.Committed, thrice.Refusal.ToString()));
        Assert.Same(back.Result, Now(ItemCosts.Refund(paid, first)));    // the first one's replay still answers
        Assert.Equal(2, _table.Submitted.Count);
    }

    [Fact]
    public void A_refund_gives_back_what_Kiln_took_never_what_a_changed_copy_of_the_receipt_says()
    {
        var paid = Paid();
        var r = Now(ItemCosts.Refund(paid with { Taken = 99 }, Guid.NewGuid()));
        Assert.False(r.Committed);
        Assert.Equal(CostRefusal.NothingToRefund, r.Refusal);
        var forged = new CostReceipt(new ItemId(Bait), 50, 50, true) { RequestId = Guid.NewGuid(), Selection = Stack(), Kind = CostKind.StackTake };
        Assert.Equal(CostRefusal.NothingToRefund, Now(ItemCosts.Refund(forged, Guid.NewGuid())).Refusal);
        Assert.Single(_table.Submitted);                                   // the take only
    }

    [Fact]
    public void A_receipt_of_an_earlier_world_is_never_refunded_into_the_world_that_runs_now()
    {
        var paid = Paid();
        _table.Epoch = 6;
        var r = Now(ItemCosts.Refund(paid, Guid.NewGuid()));
        Assert.Equal((false, CostRefusal.WrongWorld), (r.Committed, r.Refusal));
        Assert.Single(_table.Submitted);
    }

    [Fact]
    public void A_refund_that_gave_nothing_may_be_asked_again_and_one_that_gave_part_gives_only_the_rest()
    {
        var paid = Paid(3);
        var full = ItemCosts.Refund(paid, Guid.NewGuid());
        _table.Settle(101, ItemCostsNative.Refused, refusal: 7, reason: "no room");
        ItemCosts.Runtime.Pump();
        Assert.Equal(CostRefusal.NoRoom, Now(full).Refusal);
        var part = ItemCosts.Refund(paid, Guid.NewGuid());                  // nothing came back: it may be asked again
        Assert.Equal(3u, _table.Submitted[2].Amount);
        _table.Settle(102, ItemCostsNative.Committed, taken: 1, disposition: ItemCostsNative.Partial);
        ItemCosts.Runtime.Pump();
        Assert.Equal((true, 1), (Now(part).Committed, Now(part).Taken));
        ItemCosts.Refund(paid, Guid.NewGuid());                             // the rest: 2
        Assert.Equal(2u, _table.Submitted[3].Amount);
        _table.Settle(103, ItemCostsNative.Uncertain, taken: 0);
        ItemCosts.Runtime.Pump();
        var after = Now(ItemCosts.Refund(paid, Guid.NewGuid()));          // an uncertain refund: never again
        Assert.Equal("AlreadyRefunded", after.Refusal.ToString());
        Assert.Equal(4, _table.Submitted.Count);
    }
}
