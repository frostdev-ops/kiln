// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Kiln.BlockTests;

public sealed unsafe class MoveLifetimeTests
{
    private sealed class MoveBackend : IKilnBackend
    {
        public string Version => "test";
        public bool IsTickThread => true;
        public bool HasFeature(string name) => false;
        public nint Service(string name, uint minVersion) => 0;
        public void Log(ulong owner, LogLevel level, string text) { }
    }

    private static int _calls;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Move(ulong owner, ItemsNative.MoveRequest* request,
        delegate* unmanaged[Cdecl]<ItemsNative.MoveResult*, void*, void> callback, void* user, ulong* ticket)
    {
        _calls++;
        return ItemsNative.InvalidArgument;
    }

    [Theory]
    [InlineData(MoveKind.Stack)]
    [InlineData(MoveKind.QuickStack)]
    [InlineData(MoveKind.All)]
    public void Retired_owner_cannot_submit_or_retain_inventory_moves(MoveKind kind)
    {
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var fields = new[] { "_table", "_v3" }.Select(name => typeof(Items).GetField(name, flags)!).ToArray();
        var previous = fields.Select(field => field.GetValue(null)).ToArray();
        var backend = Backend.Current;
        var caller = HostAccess.Current;
        var table = (ItemsNative.TableV3*)NativeMemory.AllocZeroed((nuint)sizeof(ItemsNative.TableV3));
        table->V2.V1.Header = new ServiceHeader { Size = (uint)sizeof(ItemsNative.TableV3), Version = 3 };
        table->Move = &Move;
        var mod = HostAccess.CreateContext(new ModInfo("retired-move", "retired-move", "1", ""), 21, "");
        mod.Retire();
        try
        {
            Backend.Current = new MoveBackend();
            fields[0].SetValue(null, Pointer.Box(table, typeof(ItemsNative.Table*)));
            fields[1].SetValue(null, null);
            HostAccess.Current = mod;
            _calls = 0;
            Assert.Throws<InvalidOperationException>(() =>
            {
                _ = kind switch
                {
                    MoveKind.Stack => Items.Move(new EntityId(42), new SlotId(1), new EntityId(43)),
                    MoveKind.QuickStack => Items.QuickStack(new EntityId(42), new EntityId(43)),
                    _ => Items.MoveAll(new EntityId(42), new EntityId(43)),
                };
            });
            Assert.Equal(0, _calls);
        }
        finally
        {
            typeof(Items).GetMethod("RetireMoves", flags)!.Invoke(null, [mod]);
            HostAccess.Current = caller;
            Backend.Current = backend;
            for (var i = 0; i < fields.Length; i++) fields[i].SetValue(null, previous[i]);
            NativeMemory.Free(table);
        }
    }
}
