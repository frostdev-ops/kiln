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
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x44), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x46), 2);       // sections
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x54), 0xF0);    // optional header size
        int opt = 0x58;
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(opt), 0x20B);
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(opt + 24), 0x140000000);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(opt + 56), 0x3000); // image size
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(opt + 60), 0x200);  // mapped headers
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(opt + 108), 16);   // data directories
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
        t[0x3F] = 0xAB;
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

    public static IEnumerable<object[]> MalformedImages()
    {
        const int opt = 0x58, text = opt + 0xF0;
        byte[] With32(int offset, uint value) { var f = MakePe(Text()); BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(offset), value); return f; }
        byte[] With16(int offset, ushort value) { var f = MakePe(Text()); BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(offset), value); return f; }
        yield return ["negative PE offset", With32(0x3C, uint.MaxValue)];
        yield return ["overflowing PE offset", With32(0x3C, int.MaxValue)];
        yield return ["wrong machine", With16(0x44, 0xAA64)];
        yield return ["short optional header", With16(0x54, 0x10)];
        yield return ["directory count outside header", With32(opt + 108, uint.MaxValue)];
        yield return ["headers truncate section table", With32(opt + 60, 0x180)];
        yield return ["truncated optional header", MakePe(Text())[..0xB0]];
        yield return ["truncated section table", With16(0x46, ushort.MaxValue)];
        yield return ["huge virtual code size", With32(text + 8, uint.MaxValue)];
        yield return ["raw code past EOF", With32(text + 20, uint.MaxValue)];
        yield return ["code beyond image", With32(text + 12, 0x3000)];
        yield return ["overflowing section RVA", With32(text + 12, 0xFFFFFFF0)];
        yield return ["overlapping code sections", With32(text + 40 + 12, 0x1000)];
        yield return ["missing relocation bytes", With32(opt + 112 + 44, 0x100)];
        yield return ["overflowing relocation block", With32(0x244, uint.MaxValue)];
        yield return ["relocation block past directory", With32(0x244, 12)];
        yield return ["odd relocation block", With32(0x244, 9)];
        yield return ["relocation targets outside image", With32(0x240, 0xFFFFFFF0)];
        yield return ["unsupported code relocation", With16(0x248, (3 << 12) | 0x20)];
        yield return ["relocation straddles code boundary", With16(0x248, (10 << 12) | 0x3F)];
    }

    [Theory]
    [MemberData(nameof(MalformedImages))]
    public void RejectsMalformedPeBeforeReadingOrAllocating(string description, byte[] file)
    {
        _ = description;
        Assert.Throws<FormatException>(() => PeCode.Parse(file, 0x7FF600000000));
    }

    [Fact]
    public void EveryTruncatedPrefixIsRejectedCleanly()
    {
        var file = MakePe(Text());
        for (int n = 0; n < 0x250; ++n)
            Assert.Throws<FormatException>(() => PeCode.Parse(file.AsSpan(0, n), 0x140000000));
    }

    [Fact]
    public void ReadRefusesAnOverflowingAddressRange()
    {
        var code = PeCode.Parse(MakePe(Text()), 0x140000000);
        Assert.False(code.TryRead(ulong.MaxValue, new byte[0x1001]));
        Assert.True(code.TryRead(0x1040, Span<byte>.Empty));
        Assert.False(code.TryRead(0x1041, Span<byte>.Empty));
    }

    [Fact]
    public void PatternsHandleWhitespaceAndRejectValuesOutsideTheByteDomain()
    {
        Assert.Equal(new short[] { 0x48, 0x8B, -1, 0x05 }, BytePattern.Parse(" 48\t8B\r\n?? 05 "));
        Assert.Throws<ArgumentNullException>(() => BytePattern.Parse(null!));
        var code = PeCode.Parse(MakePe(Text()), 0x140000000);
        Assert.Throws<ArgumentOutOfRangeException>(() => code.FindUnique(new short[] { 0x18D, 0x04 }, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => code.FindUnique(new short[] { 0x8D, -2 }, out _));
        Assert.Equal(0x103F, code.FindUnique(new short[] { 0xAB }, out int count));
        Assert.Equal(1, count);
        Assert.Equal(-1, code.FindUnique(new short[0x100], out count));
        Assert.Equal(0, count);
    }

    [Fact]
    public void TryFindReportsInvalidOrUnavailableImagesWithoutThrowing()
    {
        Assert.False(OriginalCode.TryFindUnique("invalid", out var address, out var why));
        Assert.Equal((nuint)0, address);
        Assert.NotEmpty(why);
        Assert.Throws<ArgumentOutOfRangeException>(() => OriginalCode.Read(0, -1));
    }

    [Fact]
    public void KeepValidatesElementCountsBeforeMultiplication()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => { Keep.Span<long>("tests.negative", -1); });
        Assert.Throws<OverflowException>(() => { Keep.Span<long>("tests.overflow", 0x20000000); });
        Assert.Throws<OverflowException>(() => { Keep.Span<long>("tests.wrap-positive", 0x20000001); });
    }

    [Fact]
    public void KeepPublishesOneInitializedAllocationUnderContention()
    {
        string key = "tests.contended." + Guid.NewGuid();
        var pointers = new nint[128];
        Parallel.For(0, pointers.Length, i => pointers[i] = Keep.Block(key, 32));
        Assert.All(pointers, p => Assert.Equal(pointers[0], p));
        Assert.Equal(new int[8], Keep.Span<int>(key, 8).ToArray());
        Keep.Span<int>(key, 8)[7] = 123;
        Parallel.For(0, pointers.Length, _ => Assert.Equal(123, Keep.Span<int>(key, 64)[7]));
    }


    [Fact]
    public void CodeFromAnotherBuildCannotSupplyRunningAddresses()
    {
        var file = MakePe(Text());
        Assert.Equal(0x1000u, PeCode.Parse(file, 0x140000000, file.AsSpan(0, 0x200)).TextRva);
        var otherBuild = (byte[])file.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(otherBuild.AsSpan(0x48), 12345); // COFF timestamp
        Assert.Throws<FormatException>(() => PeCode.Parse(file, 0x140000000, otherBuild.AsSpan(0, 0x200)));
        Assert.Throws<FormatException>(() => PeCode.Parse(file, 0x140000000, file.AsSpan(0, 0x1FF)));
        Assert.Throws<FormatException>(() => PeCode.Parse(file, 0x140000000, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void RespectsDeclaredDirectoriesAndMapsZeroVirtualSize()
    {
        var file = MakePe(Text());
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x58 + 108), 0); // ignore undeclared directory bytes
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x58 + 0xF0 + 8), 0); // SizeOfRawData is the fallback
        var code = PeCode.Parse(file, 0x7FF600000000);
        var buf = new byte[8];
        Assert.True(code.TryRead(0x1020, buf));
        Assert.Equal(0x140001000ul, BinaryPrimitives.ReadUInt64LittleEndian(buf));
        Assert.Equal(0x40, code.Text.Length);
    }

    [Fact]
    public void OversizedVirtualCodeIsRejectedBeforeAllocation()
    {
        var file = MakePe(Text());
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x46), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x58 + 108), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x58 + 56), 0x30000000);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x58 + 0xF0 + 8), 0x20000001);
        Assert.Throws<FormatException>(() => PeCode.Parse(file, 0x140000000));
        Assert.Throws<FormatException>(() => PeCode.Parse(MakePe(Text()), ulong.MaxValue - 0x1000));
    }

    [Fact]
    public void KeepZeroSizeHasAnEmptyView()
    {
        string key = "tests.zero." + Guid.NewGuid();
        Assert.NotEqual(0, Keep.Block(key, 0));
        Assert.Empty(Keep.Span<byte>(key, 10).ToArray());
    }

}
