// SPDX-License-Identifier: MIT
// Items.MoveChecked and Items.CheckedMovesAvailable (0.2.0-dev.32, asked for by Flamebound Ledger) offline, over a made-up
// kiln.items v3 table: what the call refuses itself, and what it hands kiln.items. The checks inside the inventory
// transaction are native/services/items/tests/move_test.cpp (checked_move_test); the game's side is the mod's lab fixture.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kiln;
using Xunit;

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
    private static byte _moves = 1;
    private static uint _reserved = 1;
    private static string _detail = "";

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Move(ulong owner, ItemsNative.MoveRequest* request,
        delegate* unmanaged[Cdecl]<ItemsNative.MoveResult*, void*, void> callback, void* user, ulong* ticket)
    {
        _calls++;
        return ItemsNative.InvalidArgument;  // completes at once
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
    private static readonly SlotId ChestSlot = SlotId.Of(new EntityId(5001), 2);
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
        (_calls, _moves, _reserved, _detail) = (0, 1, 1, "");
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
        var e = Assert.Throws<ArgumentNullException>(() => Items.MoveChecked(Chest, null!, Player, 1));
        Assert.Equal("expected", e.ParamName);
        Assert.Equal(0, _calls);
    }
}
