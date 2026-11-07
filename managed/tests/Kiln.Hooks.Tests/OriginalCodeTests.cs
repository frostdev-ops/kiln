// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using Kiln;
using Xunit;

namespace Kiln.Hooks.Tests;

public class OriginalCodeTests
{
    // A minimal PE32+ image: preferred base 0x140000000, .text at RVA 0x1000 (raw 0x200, 0x40 bytes), .reloc at RVA
    // 0x2000 (raw 0x240) with one DIR64 entry at .text+0x20.
    static byte[] MakePe(byte[] text)
    {
        var f = new byte[0x300];
        f[0] = (byte)'M'; f[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(0x3C), 0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x40), 0x4550);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x46), 2);       // sections
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x54), 0xF0);    // optional header size
        int opt = 0x58;
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(opt), 0x20B);
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(opt + 24), 0x140000000);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(opt + 112 + 40), 0x2000);  // base relocation directory
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(opt + 112 + 44), 10);
        int s = opt + 0xF0;
        void Sec(int at, string name, uint va, uint vsize, uint raw, uint rawSize)
        {
            System.Text.Encoding.ASCII.GetBytes(name).CopyTo(f, at);
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(at + 8), vsize);
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(at + 12), va);
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(at + 16), rawSize);
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(at + 20), raw);
        }
        Sec(s, ".text", 0x1000, 0x40, 0x200, 0x40);
        Sec(s + 40, ".reloc", 0x2000, 0x10, 0x240, 0x10);
        text.CopyTo(f, 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x240), 0x1000);          // page
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x244), 10);              // block size
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x248), (10 << 12) | 0x20);
        return f;
    }

    static byte[] Text()
    {
        var t = new byte[0x40];
        byte[] fn = [0x8D, 0x04, 0x11, 0x05, 0xDE, 0xC0, 0x17, 0x5A, 0x90, 0x90, 0x90, 0x90, 0xC3];
        fn.CopyTo(t, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(t.AsSpan(0x20), 0x140001000);  // an absolute address (relocated)
        t[0x30] = 0xCC; t[0x31] = 0xCC; t[0x38] = 0xCC; t[0x39] = 0xCC;
        return t;
    }

    [Fact]
    public void PatternParsesBytesAndWildcards()
    {
        Assert.Equal(new short[] { 0x48, 0x8B, -1, 0x05, -1 }, BytePattern.Parse("48 8B ?? 05 ?"));
        Assert.Equal(new short[] { 0x48, 0x8B }, BytePattern.Parse("488B"));
        Assert.Throws<FormatException>(() => BytePattern.Parse("?? 48"));
        Assert.Throws<FormatException>(() => BytePattern.Parse("4G"));
    }

    [Fact]
    public void FindsTheUniqueMatchOnly()
    {
        var code = PeCode.Parse(MakePe(Text()), 0x140000000);
        Assert.Equal(0x1000, code.FindUnique(BytePattern.Parse("8D 04 11 05 ?? C0 17 5A"), out int n));
        Assert.Equal(1, n);
        Assert.Equal(-1, code.FindUnique(BytePattern.Parse("CC CC"), out n));
        Assert.Equal(2, n);  // ambiguous: refused
        Assert.Equal(-1, code.FindUnique(BytePattern.Parse("11 22 33"), out n));
        Assert.Equal(0, n);
    }

    [Fact]
    public void ReadsTheFileBytesAndAppliesRelocations()
    {
        var at = PeCode.Parse(MakePe(Text()), 0x140000000);
        var buf = new byte[8];
        Assert.True(at.TryRead(0x1020, buf));
        Assert.Equal(0x140001000ul, BinaryPrimitives.ReadUInt64LittleEndian(buf));
        var moved = PeCode.Parse(MakePe(Text()), 0x7FF600000000);  // loaded elsewhere (ASLR)
        Assert.True(moved.TryRead(0x1020, buf));
        Assert.Equal(0x7FF600001000ul, BinaryPrimitives.ReadUInt64LittleEndian(buf));
        Assert.True(moved.TryRead(0x1000, buf.AsSpan(0, 3)));
        Assert.Equal(new byte[] { 0x8D, 0x04, 0x11 }, buf[..3]);
        Assert.False(moved.TryRead(0x0FFF, buf));
        Assert.False(moved.TryRead(0x103C, buf));
    }

    [Fact]
    public void KeepGivesTheSameZeroedBlockPerKey()
    {
        var a = Keep.Block("tests.keep", 16);
        var b = Keep.Block("tests.keep", 64);
        Assert.Equal(a, b);
        var span = Keep.Span<int>("tests.keep", 4);
        Assert.Equal(4, span.Length);
        Assert.Equal(0, span[0]);
        span[0] = 42;
        Assert.Equal(42, Keep.Span<int>("tests.keep", 4)[0]);
        Assert.NotEqual(a, Keep.Block("tests.other", 4));
    }
}
