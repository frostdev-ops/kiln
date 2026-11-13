// SPDX-License-Identifier: MIT
// Which water refill covers a container, as kiln.world asks it for a tracked write of Fuel that did not hold
// (WorldWriteResult.WrittenOverBy). The first try in a real game (13 Nov 2026): a can in the player's backpack while the
// Creative menu's rule set kept the player's inventories full (refill 1: entity 1, its inventories). What the refill does
// in the game is the lab test (mods/tests/world-writes-test, CarriedPart.cs).
using Kiln;
using Xunit;

namespace Kiln.ItemsTests;

public sealed class FuelKeepersTests
{
    private static readonly EntityId Player = new(1), Can = new(17881), OtherCan = new(17882), Barrel = new(900);
    private static readonly ItemId CanItem = new(0x8ec15337), Pouch = new(0x11111111);

    private static FuelKeep Keep(EntityId on, bool inventories = true, ItemId item = default, FuelRefill? policy = null, ulong id = 1)
    {
        var k = new FuelKeep(on, item, inventories, policy ?? FuelRefill.Full);
        k.Attach(id, (long)id);
        return k;
    }

    private static InventorySlot Slot(EntityId item, ItemId id, byte index) =>
        new(new SlotId(0x0000000200000000UL | index), id, 1, item, 1, null, InventoryCategory.Generic, 0, false, false, false, 0);

    private static InventoryContents? Backpack(EntityId holder) => holder == Player
        ? new InventoryContents(Player, true, 1, 8, 8, [(new EntityId(2), InventoryCategory.Generic)], [Slot(Can, CanItem, 0), Slot(EntityId.None, default, 1)])
        : null;

    [Fact]
    public void A_refill_of_the_players_inventories_covers_the_can_in_the_backpack()
    {
        var who = FuelKeepers.Covering([Keep(Player)], Can, Backpack);
        Assert.Equal("kiln.items' water refill 1 (keep full) of the inventories of 1, set by kiln", who);
        Assert.Null(FuelKeepers.Covering([Keep(Player)], OtherCan, Backpack));   // not in those inventories
    }

    [Fact]
    public void An_item_filter_a_rate_and_a_refill_on_the_container_itself()
    {
        Assert.Null(FuelKeepers.Covering([Keep(Player, item: Pouch)], Can, Backpack));
        var only = FuelKeepers.Covering([Keep(Player, item: CanItem, id: 4)], Can, Backpack, id => id == CanItem ? "Tool_T1_WateringCan" : null);
        Assert.Equal("kiln.items' water refill 4 (keep full) of the inventories of 1, Tool_T1_WateringCan only, set by kiln", only);
        var rate = FuelKeepers.Covering([Keep(Barrel, inventories: false, policy: FuelRefill.ChargesPerSecond(2), id: 9)], Barrel, Backpack);
        Assert.Equal("kiln.items' water refill 9 (2 charges/s) of 900, set by kiln", rate);
        Assert.Null(FuelKeepers.Covering([Keep(Barrel, inventories: false)], Can, Backpack));
    }

    [Fact]
    public void A_refill_that_is_not_active_covers_nothing()
    {
        var never = new FuelKeep(Player, default, true, FuelRefill.Full);   // never attached: no native handle
        Assert.Null(FuelKeepers.Covering([never], Can, Backpack));
        Assert.Null(FuelKeepers.Covering([], Can, Backpack));
    }
}
