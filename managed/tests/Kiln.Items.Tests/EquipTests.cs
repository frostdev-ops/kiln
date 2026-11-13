// SPDX-License-Identifier: MIT
// Items.Equip / Items.Unequip offline: the slot mapping to the game's keen::EquipmentSlot, the capability verdict, the
// result built from a native result (the one error rule: IKilnResult), and the argument and capability errors. What the
// game does is the lab test (mods/tests/items-test, EquipTest.cs); the native logic is native/services/items/tests.
using Kiln;
using Kiln.TestKit;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace Kiln.ItemsTests;

public sealed unsafe class EquipTests
{
    // The calls return a task but throw before one exists: run them as a plain call.
    private static void Call(Func<Task> f) => _ = f();

    [Fact]
    public void EverySlotMapsToTheGamesValueAndBack()
    {
        foreach (var slot in Enum.GetValues<EquipmentSlot>())
            Assert.Equal(slot, Items.SlotOfGame(Items.GameSlot(slot)));
        Assert.Equal(11, Items.GameSlot(EquipmentSlot.Ring1));
        Assert.Equal(16, Items.GameSlot(EquipmentSlot.FishingBait));
        Assert.Equal(21, Items.GameSlot(EquipmentSlot.HookShot));
        Assert.Equal(22, Items.GameSlot(EquipmentSlot.Glider));
        Assert.Equal(23, Items.GameSlot(EquipmentSlot.Backpack));
    }

    [Theory]
    [InlineData(17)]  // TerrainMaterial
    [InlineData(18)]  // BlueprintMaterial_Default
    [InlineData(20)]  // OvergrowthMaterial
    [InlineData(24)]  // Invalid
    [InlineData(25)]  // MainHand
    [InlineData(0xFF)]
    public void TheBuildingMaterialsAndInvalidAreNoEquipmentSlots(int game) => Assert.Null(Items.SlotOfGame((byte)game));

    [Fact]
    public void AValueOutsideTheEnumIsRefused() => Assert.Throws<ArgumentException>(() => Items.GameSlot((EquipmentSlot)20));

    [Fact]
    public void TheCapabilityVerdictFollowsTheTable()
    {
        Assert.Equal(Items.EquipVerdict.NoTable, Items.DecideEquip(null, false, false, "").Verdict);
        var older = Items.DecideEquip(4, false, false, "");
        Assert.Equal(Items.EquipVerdict.NoV5, older.Verdict);
        Assert.Contains("v4 has no equip", older.Text);
        var off = Items.DecideEquip(5, true, false, "equip needs kiln.world (its entity programs): kiln.world is not loaded");
        Assert.Equal(Items.EquipVerdict.Off, off.Verdict);
        Assert.Contains("kiln.world", off.Text);
        Assert.Equal(Items.EquipVerdict.Off, Items.DecideEquip(5, true, false, "").Verdict);
        Assert.Equal(Items.EquipVerdict.On, Items.DecideEquip(5, true, true, "").Verdict);
    }

    private static ItemsNative.EquipResult Native(int status, uint flags, byte slot, string message = "")
    {
        var n = new ItemsNative.EquipResult
        {
            Size = (uint)sizeof(ItemsNative.EquipResult), Status = status, Ticket = 9, Player = 1042, Flags = flags,
            EquipmentSlot = slot, Steps = 1, Item = 0x79442800, Pide = 5000, Stamp = 0, From = 0x0000000300000800UL,
            To = 0x0000000200000801UL, Replaced = 0x4623bcb0, ReplacedPide = 5001, ReplacedTo = 0x0000000300000800UL,
        };
        var bytes = System.Text.Encoding.UTF8.GetBytes(message);
        for (var i = 0; i < bytes.Length && i < 159; i++) n.Message[i] = bytes[i];
        return n;
    }

    [Fact]
    public void ADoneResultIsOk()
    {
        var r = Items.ToResult(Native(0, 0, 11));
        IKilnResult k = r;
        Assert.True(k.Ok);
        Assert.Null(k.Reason);
        Assert.Equal(EquipmentSlot.Ring1, r.Slot);
        Assert.False(r.Unequipped);
        Assert.False(r.DryRun);
        Assert.Equal(new ItemId(0x79442800), r.Item);
        Assert.Equal(new EntityId(5000), r.ItemEntity);
        Assert.Equal(new SlotId(0x0000000300000800UL), r.From);
        Assert.Equal(new SlotId(0x0000000200000801UL), r.To);
        Assert.Equal(new ItemId(0x4623bcb0), r.Replaced);
        Assert.Equal(new EntityId(5001), r.ReplacedEntity);
        Assert.True(r.Ref.IsNone);  // no stamp: items.instances off
    }

    [Fact]
    public void ARefusalCarriesTheReason()
    {
        var r = Items.ToResult(Native(7, 0, 10, "the item does not fit that equipment slot: the game refused it"));
        Assert.False(r.Ok);
        Assert.Equal(ItemsStatus.Refused, r.Status);
        Assert.Equal("the item does not fit that equipment slot: the game refused it", r.Reason);
        var unknown = Items.ToResult(Native(42, 0, 10));
        Assert.Equal(ItemsStatus.Refused, unknown.Status);
        Assert.Equal("Refused", unknown.Reason);
    }

    [Fact]
    public void UnequipAndDryRunFlagsAreRead()
    {
        var r = Items.ToResult(Native(2, ItemsNative.EquipUnequip | ItemsNative.EquipDryRun, 22, "no free slot in the player's backpack (error 0x11)"));
        Assert.True(r.Unequipped);
        Assert.True(r.DryRun);
        Assert.Equal(ItemsStatus.NoRoom, r.Status);
        Assert.Equal(EquipmentSlot.Glider, r.Slot);
        Assert.Null(Items.ToResult(Native(0, 0, 0xFF)).Slot);
    }

    [Fact]
    public void AFailIsNotOk()
    {
        var r = EquipResult.Fail(ItemsStatus.Refused, new EntityId(1), EquipmentSlot.Ring0, false, default, "the player is dead", false);
        Assert.False(r.Ok);
        Assert.Equal("the player is dead", r.Reason);
        Assert.Contains("equip Refused", r.ToString());
    }

    [Fact]
    public void BadArgumentsThrowBeforeAnythingElse()
    {
        using var kiln = new KilnHarness();
        kiln.Run(() =>
        {
            Assert.Throws<ArgumentException>(() => Call(() => Items.Equip(EntityId.None, new SlotId(0x0000000100000800UL))));
            Assert.Throws<ArgumentException>(() => Call(() => Items.Equip(new EntityId(1), default)));
            Assert.Throws<ArgumentException>(() => Call(() => Items.Equip(new EntityId(1), new SlotId(0x0000000100000800UL), (EquipmentSlot)99)));
            Assert.Throws<ArgumentException>(() => Call(() => Items.Unequip(EntityId.None, EquipmentSlot.Ring0)));
            Assert.Throws<ArgumentException>(() => Call(() => Items.Unequip(new EntityId(1), (EquipmentSlot)(-1))));
        });
    }

    [Fact]
    public void WithoutKilnItemsTheCapabilityIsOff()
    {
        using var kiln = new KilnHarness();
        kiln.Run(() =>
        {
            var e = Assert.Throws<KilnUnavailableException>(() => Call(() => Items.Equip(new EntityId(1), new SlotId(0x0000000100000800UL), EquipmentSlot.Ring0)));
            Assert.Equal("items.equip", e.Capability);
            e = Assert.Throws<KilnUnavailableException>(() => Call(() => Items.Unequip(new EntityId(1), EquipmentSlot.Glider)));
            Assert.Equal("items.equip", e.Capability);
        });
    }

    [Fact]
    public void UnknownOptionBitsAreABadArgument()
    {
        Items.CheckOptions(EquipOptions.None);
        Items.CheckOptions(EquipOptions.DryRun);
        Assert.Throws<ArgumentException>(() => Items.CheckOptions((EquipOptions)2));
        using var kiln = new KilnHarness();
        kiln.Run(() =>
        {
            Assert.Throws<ArgumentException>(() => Call(() => Items.Equip(new EntityId(1), new SlotId(0x0000000100000800UL), null, (EquipOptions)4)));
            Assert.Throws<ArgumentException>(() => Call(() => Items.Unequip(new EntityId(1), EquipmentSlot.Ring0, (EquipOptions)3)));
        });
    }

    [Fact]
    public void AFullQueueIsARefusalNotAMissingCapability()
    {
        var r = Items.SubmitRefusal(ItemsNative.BufferTooSmall, new EntityId(1), EquipmentSlot.Ring0, false, default, false);
        Assert.NotNull(r);
        Assert.False(r!.Ok);
        Assert.Equal(ItemsStatus.Refused, r.Status);
        Assert.Contains("waiting", r.Reason);
        Assert.Null(Items.SubmitRefusal(ItemsNative.Unavailable, new EntityId(1), null, true, default, false));
        Assert.Null(Items.SubmitRefusal(ItemsNative.InvalidArgument, new EntityId(1), null, true, default, false));
    }

    [Fact]
    public void NoServerWorldIsASessionStateNotAnOff()
    {
        // a game client in its menu or joined elsewhere: the row follows the session (RefreshEquip), it is not lowered for good
        Assert.Equal(Items.EquipVerdict.On, Items.DecideEquip(5, true, false, Items.NoServerWorld + " (a game client in its menu)").Verdict);
        Assert.Equal(Items.EquipVerdict.Off, Items.DecideEquip(5, true, false, "the death check cannot be made: kiln.types is not loaded").Verdict);
    }

    [Fact]
    public void AChangeIsReportedWhenTheModThatAskedUnloadedFirst()
    {
        // the asking mod's retirement drops only its task: the committed change still reaches every subscriber
        using var kiln = new KilnHarness();
        var seen = new List<EquipChangedEvent>();
        var sub = Events.On<EquipChangedEvent>(e => seen.Add(e));
        try
        {
            var gone = new TaskCompletionSource<EquipResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stays = new TaskCompletionSource<EquipResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            kiln.Run(() =>
            {
                var a = Items.Track(gone, null);
                Items.RetireWhere(m => m is null);
                var b = Items.Track(stays, null);
                Items.Complete(a, Items.ToResult(Native(0, 0, 11)));
                Items.Complete(b, Items.ToResult(Native(0, 0, 10)));
                Items.Complete(b, Items.ToResult(Native(0, 0, 10)));  // a second result for one key: nothing more
            });
            Assert.Equal(2, seen.Count);
            Assert.Equal(EquipmentSlot.Ring1, seen[0].Slot);
            Assert.False(gone.Task.IsCompleted);
            Assert.True(stays.Task.IsCompletedSuccessfully);
        }
        finally
        {
            sub.Dispose();
        }
    }

    [Fact]
    public void ARefusalOrADryRunIsNoChange()
    {
        using var kiln = new KilnHarness();
        var seen = 0;
        var sub = Events.On<EquipChangedEvent>(_ => seen++);
        try
        {
            kiln.Run(() =>
            {
                Items.Complete(Items.Track(new TaskCompletionSource<EquipResult>(), null), Items.ToResult(Native(7, 0, 10, "refused")));
                Items.Complete(Items.Track(new TaskCompletionSource<EquipResult>(), null), Items.ToResult(Native(0, ItemsNative.EquipDryRun, 10)));
            });
            Assert.Equal(0, seen);
        }
        finally
        {
            sub.Dispose();
        }
    }

    [Fact]
    public void OffTheTickThreadThrows()
    {
        using var kiln = new KilnHarness();
        Assert.Throws<InvalidOperationException>(() => Call(() => Items.Unequip(new EntityId(1), EquipmentSlot.Glider)));
    }
}
