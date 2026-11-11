// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;
namespace Kiln.Ui.Tests;

public unsafe class SetToolsTests
{
    [ThreadStatic] private static string? _received;
    [ThreadStatic] private static int _result;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Open(NativeStr mod) { _received = mod.ToString(); return _result; }

    [Fact]
    public void MissingShortOldOrNullSlotTablesAreUnavailable()
    {
        Assert.Equal(-1, SetTools.Open(null, "mod"));
        var table = new SetTools.Table { Header = new ServiceHeader { Size = 8, Version = 1 }, Open = &Open };
        Assert.Equal(-1, SetTools.Open(&table, "mod"));
        table.Header.Size = (uint)sizeof(SetTools.Table); table.Header.Version = 0;
        Assert.Equal(-1, SetTools.Open(&table, "mod"));
        table.Header.Version = 1; table.Open = null;
        Assert.Equal(-1, SetTools.Open(&table, "mod"));
    }

    [Fact]
    public void OpenPassesUtf8ModFolderAndReturnsRuntimeRefusal()
    {
        var table = new SetTools.Table { Header = new ServiceHeader { Size = (uint)sizeof(SetTools.Table), Version = 1 }, Open = &Open };
        _result = 0; _received = null;
        Assert.Equal(0, SetTools.Open(&table, "mód-雪"));
        Assert.Equal("mód-雪", _received);
        _result = 77;
        Assert.Equal(77, SetTools.Open(&table, ""));
        Assert.Equal("", _received);
    }
}
