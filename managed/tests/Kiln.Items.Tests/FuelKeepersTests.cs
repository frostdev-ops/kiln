// SPDX-License-Identifier: MIT
// Which water refill wrote over a tracked write of Fuel, as kiln.world asks it (WorldWriteResult.WrittenOverBy). The
// first try in a real game (13 Nov 2026): a can in the player's backpack while the Creative menu's rule set kept the
// player's inventories full (refill 1: entity 1, its inventories). A refill is named only from its own report of the
// step the write was checked in; covering the container is not enough (review of 56d44391: a capped refill was named
// for KeepFull's write, and a refill of a world that had closed for a reused entity id). The lab test is
// mods/tests/world-writes-test, CarriedPart.cs.
using Kiln;
using Xunit;

namespace Kiln.ItemsTests;

public sealed class FuelKeepersTests : IDisposable
{
    private static readonly EntityId Player = new(1), Can = new(17881), OtherCan = new(17882);
    private static readonly ItemId CanItem = new(0x8ec15337);

    public FuelKeepersTests() => FuelKeepers.Reset();
    public void Dispose() => FuelKeepers.Reset();

    private static FuelKeep Keep(FuelRefill policy, ulong id, ItemId item = default)
    {
        var k = new FuelKeep(Player, item, true, policy);
        k.Attach(id, (long)id);
        return k;
    }

    private static FuelChange Change(EntityId container, int before, int after) => new(container, CanItem, default, before, after, 6400);

    // the bytes a whole-component World.Update<Ecs.Fuel> leaves: header, then the units and the max
    private static byte[] Fuel(int units)
    {
        var b = new byte[16];
        BitConverter.GetBytes(units).CopyTo(b, 8);
        BitConverter.GetBytes(6400).CopyTo(b, 12);
        return b;
    }

    private static string? Who(EntityId container, int units, long session, ulong step) =>
        FuelKeepers.Attribute(container, 0, Fuel(units), session, step);

    // Findings 1 and 2 need the running world's session; the keeps take it from Lifecycle at Attach.
    private static long Now => Lifecycle.ServerSession;

    [Fact]
    public void The_refill_whose_report_wrote_over_the_value_is_named()
    {
        var full = Keep(FuelRefill.Full, 1);
        FuelKeepers.Note(full, [Change(Can, 3200, 6400)], Now, 100);
        Assert.Equal("kiln.items' water refill 1 (keep full) of the inventories of 1, set by kiln", Who(Can, 3200, Now, 100));
        Assert.Equal("kiln.items' water refill 1 (keep full) of the inventories of 1, set by kiln", Who(Can, 3200, Now, 101));  // reported the Pre before
        Assert.Null(Who(OtherCan, 3200, Now, 100));                                                                          // no report for it
    }

    // Review finding 1: a refill capped at 640 units and a KeepFull on the same inventory; the can is written to 3200; the
    // capped one leaves it alone, KeepFull raises it to 6400. Only KeepFull reported a change: it is the one named.
    [Fact]
    public void A_refill_that_covers_the_can_but_did_not_write_is_never_named()
    {
        var capped = Keep(FuelRefill.ChargesPerSecond(1000, limitUnits: 640), 2);
        var full = Keep(FuelRefill.Full, 3);
        FuelKeepers.Note(capped, [], Now, 100);
        FuelKeepers.Note(full, [Change(Can, 3200, 6400)], Now, 100);
        Assert.Equal("kiln.items' water refill 3 (keep full) of the inventories of 1, set by kiln", Who(Can, 3200, Now, 100));

        // the capped one wrote, but from another value than the write left (it raised 100 to 640 earlier): not named
        FuelKeepers.Reset();
        FuelKeepers.Note(capped, [Change(Can, 100, 640)], Now, 100);
        Assert.Null(Who(Can, 3200, Now, 100));
    }

    // Review finding 2: the server world closed (native drop_world ended every refill), a new one started, and its reused
    // entity ids put a can in the same holder's inventories. A refill of the old world names nothing, even with a report
    // of its own.
    [Fact]
    public void A_refill_of_a_world_that_closed_names_nothing()
    {
        var old = Keep(FuelRefill.Full, 5);
        FuelKeepers.Note(old, [Change(Can, 3200, 6400)], Now, 100);
        Assert.NotNull(Who(Can, 3200, Now, 100));   // in its own world it is the one

        var phase = Lifecycle.Phase;
        Lifecycle.Phase = ServerPhase.Booting;      // the next server world boots (a hosted world opened again)
        Lifecycle.Phase = phase;
        Assert.False(old.Current);
        Assert.Null(Who(Can, 3200, Now, 100));      // the old world's reports do not count in this one
        FuelKeepers.Note(old, [Change(Can, 3200, 6400)], Now, 101);
        Assert.Null(Who(Can, 3200, Now, 101));      // nor does a report of a refill set in the old world
    }

    [Fact]
    public void A_stale_report_or_one_from_another_value_names_nothing()
    {
        var full = Keep(FuelRefill.Full, 1);
        FuelKeepers.Note(full, [Change(Can, 3200, 6400)], Now, 100);
        Assert.Null(Who(Can, 3200, Now, 102));   // two steps old: not the step the write was checked in
        Assert.Null(Who(Can, 3100, Now, 100));   // the write left another value
        Assert.Null(Who(Can, 3200, Now, 0));     // no service step yet: nothing can be shown
        Assert.Null(FuelKeepers.Attribute(Can, 12, BitConverter.GetBytes(6400), Now, 100));   // the units are not among the written bytes
        Assert.Equal("kiln.items' water refill 1 (keep full) of the inventories of 1, set by kiln",
            FuelKeepers.Attribute(Can, 8, BitConverter.GetBytes(3200), Now, 100));             // World.WriteAt(can, "Fuel", 8, 3200)
    }

    [Fact]
    public void A_refill_disposed_or_never_set_names_nothing_and_an_item_filter_is_said()
    {
        var never = new FuelKeep(Player, default, true, FuelRefill.Full);   // never attached: no native handle
        FuelKeepers.Note(never, [Change(Can, 3200, 6400)], Now, 100);
        Assert.Null(Who(Can, 3200, Now, 100));

        FuelKeepers.Reset();
        var only = Keep(FuelRefill.Full, 4, CanItem);
        FuelKeepers.Note(only, [Change(Can, 3200, 6400)], Now, 100);
        Assert.Equal("kiln.items' water refill 4 (keep full) of the inventories of 1, Tool_T1_WateringCan only, set by kiln",
            FuelKeepers.Attribute(Can, 0, Fuel(3200), Now, 100, id => id == CanItem ? "Tool_T1_WateringCan" : null));
    }
}
