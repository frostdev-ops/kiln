// SPDX-License-Identifier: MIT
// Items.MoveChecked and Items.CheckedMovesAvailable (0.2.0-dev.31.30, experimental in 31.x: KILNX033; asked for by
// Flamebound Ledger) offline, over a made-up kiln.items v3 table: what the call refuses itself, and what it hands
// kiln.items. The checks inside the inventory transaction are native/services/items/tests/move_test.cpp
// (checked_move_test); the game's side is the mod's lab fixture.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kiln;
using Kiln.TestKit;
using Xunit;
#pragma warning disable KILNX033  // experimental in 31.x (0.2.0-dev.31.30): the members under test

namespace Kiln.ItemsTests;

public sealed unsafe class CheckedMoveTests : IDisposable
{
    private sealed class Backend(params string[] features) : IKilnBackend
    {
        public string Version => "test";
        public bool IsTickThread => true;
        public bool HasFeature(string name) => features.Contains(name);
        public nint Service(string name, uint minVersion) => 0;
        public void Log(ulong owner, LogLevel level, string text) { }
    }

    private static int _calls;
    private static ItemsNative.MoveRequest _last;
    private static byte _moves = 1;
    private static uint _reserved = 1;
    private static string _detail = "";

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Move(ulong owner, ItemsNative.MoveRequest* request,
        delegate* unmanaged[Cdecl]<ItemsNative.MoveResult*, void*, void> callback, void* user, ulong* ticket)
    {
        _calls++;
        _last = *request;
        return ItemsNative.InvalidArgument;  // completes at once: the request is what these tests look at
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Status2(ItemsNative.Status2* s)
    {
        s->Moves = _moves;
        s->Reserved = _reserved;
        var bytes = System.Text.Encoding.UTF8.GetBytes(_detail);
        for (var i = 0; i < bytes.Length && i < 159; i++) s->Detail[i] = bytes[i];
        return ItemsNative.Ok;
    }

    private static readonly BindingFlags Statics = BindingFlags.Static | BindingFlags.NonPublic;
    private readonly FieldInfo[] _fields = new[] { "_table", "_v3" }.Select(n => typeof(Items).GetField(n, Statics)!).ToArray();
    private readonly object?[] _previous;
    private readonly IKilnBackend _was = Kiln.Backend.Current;
    private readonly ItemsNative.TableV3* _t;

    private static readonly EntityId Chest = new(5000), Player = new(3);
    private static readonly SlotId ChestSlot = SlotId.Of(new EntityId(5001), 2), BagSlot = SlotId.Of(new EntityId(4001), 5);
    private static readonly ItemId Wood = new(0x1111), Sword = new(0x2222);

    public CheckedMoveTests()
    {
        _previous = _fields.Select(f => f.GetValue(null)).ToArray();
        _t = (ItemsNative.TableV3*)NativeMemory.AllocZeroed((nuint)sizeof(ItemsNative.TableV3));
        _t->V2.V1.Header = new ServiceHeader { Size = (uint)sizeof(ItemsNative.TableV3), Version = 3 };
        _t->Move = &Move;
        _t->GetStatus2 = &Status2;
        _fields[0].SetValue(null, Pointer.Box(_t, typeof(ItemsNative.Table*)));
        _fields[1].SetValue(null, null);
        Kiln.Backend.Current = new Backend("items.move", "items.instances");
        (_calls, _moves, _reserved, _detail, _last) = (0, 1, 1, "", default);
    }

    public void Dispose()
    {
        Kiln.Backend.Current = _was;
        for (var i = 0; i < _fields.Length; i++) _fields[i].SetValue(null, _previous[i]);
        NativeMemory.Free(_t);
    }

    private static InventorySlot Plain(SlotId slot, ItemId item, int count) =>
        new(slot, item, count, EntityId.None, 0, null, InventoryCategory.Generic, 0, false, false, false, 0);

    private static InventorySlot Gear(SlotId slot, uint pide, ItemInstanceRef itemRef) =>
        new(slot, Sword, 1, new EntityId(pide), 3, ItemRarity.Rare, InventoryCategory.Generic, 0, true, false, false, 0) { Ref = itemRef };

    // ---- what the call refuses itself ----

    [Fact]
    public void A_null_expected_slot_throws_ArgumentNullException()
    {
        var e = Assert.Throws<ArgumentNullException>(() => { _ = Items.MoveChecked(Chest, null!, Player, 1); });  // at the call
        Assert.Equal("expected", e.ParamName);
        Assert.Equal(0, _calls);
    }

    [Fact]
    public void A_runtime_without_checked_moves_says_to_update_Kiln_and_moves_that_are_off_say_why()
    {
        _reserved = 0;  // kiln.items before checked moves: status2's bits are 0, and its move refuses the flag
        Assert.True(Items.MovesAvailable);
        Assert.False(Items.CheckedMovesAvailable);
        var old = Items.MoveChecked(Chest, Plain(ChestSlot, Wood, 7), Player, 2).Result;
        Assert.Equal(ItemsStatus.Unavailable, old.Status);
        Assert.Contains("update Kiln", old.Message);

        (_reserved, _moves, _detail) = (1, 0, "the kiln_items program is not running yet");
        Assert.False(Items.CheckedMovesAvailable);
        var off = Items.MoveChecked(Chest, Plain(ChestSlot, Wood, 7), Player, 2).Result;
        Assert.Equal(ItemsStatus.Unavailable, off.Status);
        Assert.Contains("the kiln_items program is not running yet", off.Message);
        Assert.DoesNotContain("update Kiln", off.Message);
        Assert.Equal(0, _calls);
    }

    [Fact]
    public void Gear_while_item_instances_are_off_is_unavailable_not_invalid()
    {
        // items.instances off: a read's Ref is None for every item entity, so no stamp can witness the gear
        Kiln.Backend.Current = new Backend("items.move");
        var off = Items.MoveChecked(Chest, Gear(ChestSlot, 3077, ItemInstanceRef.None), Player, 1).Result;
        Assert.Equal(ItemsStatus.Unavailable, off.Status);
        Assert.Contains("items.instances", off.Message);

        // instances on, and a slot that carries no stamp of kiln.items: the caller's slot, Invalid as before
        Kiln.Backend.Current = new Backend("items.move", "items.instances");
        Assert.Equal(ItemsStatus.Invalid, Items.MoveChecked(Chest, Gear(ChestSlot, 3077, ItemInstanceRef.None), Player, 1).Result.Status);
        Assert.Equal(0, _calls);
    }

    // ---- what it hands kiln.items ----

    [Fact]
    public void The_request_mirror_has_the_headers_layout()
    {
        // native items.cpp: sizeof(KilnItemsCheckedMoveRequest) == 72 (the 40-byte request, then source at 40 and
        // destination at 56), sizeof(KilnItemsSlotWitness) == 16; KILN_ITEMS_MOVE_CHECKED, KILN_ITEMS_HAS_CHECKED_MOVE
        Assert.Equal((72, 16), (sizeof(ItemsNative.MoveRequest), sizeof(ItemsNative.SlotWitness)));
        Assert.Equal((40, 56), ((int)Marshal.OffsetOf<ItemsNative.MoveRequest>(nameof(ItemsNative.MoveRequest.Source)),
            (int)Marshal.OffsetOf<ItemsNative.MoveRequest>(nameof(ItemsNative.MoveRequest.Destination))));
        Assert.Equal(8, (int)Marshal.OffsetOf<ItemsNative.SlotWitness>(nameof(ItemsNative.SlotWitness.Pide)));
        Assert.Equal((0x10u, 0x01u), (ItemsNative.MoveChecked, ItemsNative.HasCheckedMove));
    }

    [Fact]
    public void A_checked_move_hands_kiln_items_the_flag_the_slots_and_both_witnesses()
    {
        const uint stamp = 41;
        var sword = Gear(ChestSlot, 3077, new ItemInstanceRef(new EntityRef(new EntityId(3077), IdentitySources.Stamp(EntitySourceOrder.Items, stamp)), Sword));
        _ = Items.MoveChecked(Chest, sword, Player, 1, Plain(BagSlot, ItemId.None, 0)).Result;
        Assert.Equal(1, _calls);
        Assert.Equal((72u, ItemsNative.MoveChecked, ItemsNative.MoveStack), (_last.Size, _last.Flags, _last.Kind));
        Assert.Equal((Chest.Value, Player.Value, 1u, ChestSlot.Value, BagSlot.Value), (_last.From, _last.To, _last.Count, _last.Slot, _last.ToSlot));
        Assert.Equal((Sword.Value, 1u, 3077u, stamp), (_last.Source.Item, _last.Source.Count, _last.Source.Pide, _last.Source.Stamp));
        Assert.Equal(default, _last.Destination);  // an empty destination: all zeros, it must still be empty

        _ = Items.MoveChecked(Chest, Plain(ChestSlot, Wood, 7), Player, 2, Plain(BagSlot, Wood, 248)).Result;
        Assert.Equal((Wood.Value, 7u, 0u, 0u), (_last.Source.Item, _last.Source.Count, _last.Source.Pide, _last.Source.Stamp));
        Assert.Equal((Wood.Value, 248u, 0u, 0u), (_last.Destination.Item, _last.Destination.Count, _last.Destination.Pide, _last.Destination.Stamp));
        Assert.Equal((2u, BagSlot.Value), (_last.Count, _last.ToSlot));

        _ = Items.MoveChecked(Chest, Plain(ChestSlot, Wood, 7), Player, 7).Result;  // no destination: Kiln's choice
        Assert.Equal((0ul, 7u), (_last.ToSlot, _last.Count));
        Assert.Equal(default, _last.Destination);
        Assert.Equal(3, _calls);
    }

    public static TheoryData<string> Refusals => new() { "count 0", "count over the stack", "empty source", "destination without a slot", "gear without an item entity" };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void What_the_call_refuses_itself_never_reaches_kiln_items(string which)
    {
        var wood = Plain(ChestSlot, Wood, 7);
        var r = (which switch
        {
            "count 0" => Items.MoveChecked(Chest, wood, Player, 0),
            "count over the stack" => Items.MoveChecked(Chest, wood, Player, 8),
            "empty source" => Items.MoveChecked(Chest, Plain(ChestSlot, ItemId.None, 0), Player, 1),
            "destination without a slot" => Items.MoveChecked(Chest, wood, Player, 1, Plain(default, ItemId.None, 0)),
            _ => Items.MoveChecked(Chest, Plain(ChestSlot, Sword, 1) with { IsGear = true }, Player, 1),
        }).Result;
        Assert.Equal(ItemsStatus.Invalid, r.Status);
        Assert.Empty(r.Changes);
        Assert.Equal(0, _calls);
    }
}

/// <summary>The test kit does not model moves (items.move is offline there): a mod's test sees checked moves as
/// unavailable, as an answer, not an exception (docs/kiln/modding/inventory.md, "Checked moves").</summary>
public sealed class CheckedMoveKitTests
{
    [Fact]
    public void In_the_test_kit_checked_moves_are_unavailable()
    {
        using var kiln = new KilnHarness();
        kiln.Step();
        var (available, result) = kiln.Run(() => (Items.CheckedMovesAvailable, Items.MoveChecked(new EntityId(5000),
            new InventorySlot(SlotId.Of(new EntityId(5001), 0), new ItemId(0x1111), 5, EntityId.None, 0, null,
                InventoryCategory.Generic, 0, false, false, false, 0), new EntityId(3), 1).Result));
        Assert.False(available);
        Assert.Equal(ItemsStatus.Unavailable, result.Status);
        Assert.StartsWith("Items.MoveChecked: ", result.Message);
    }
}
