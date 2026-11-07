// SPDX-License-Identifier: MIT
using Kiln;
using Xunit;

namespace KilnEmlTests;

public class DataHashTests
{
    // Check values from the algorithms' published catalogues; kiln-eml's probe mod got the same values from EML.
    [Fact] public void Fnv1a32() => Assert.Equal(0xBB86B11Cu, DataHash.Fnv1a32("123456789"));
    [Fact] public void Fnv1a32Empty() => Assert.Equal(0x811C9DC5u, DataHash.Fnv1a32(""));
    [Fact] public void Crc32() => Assert.Equal(0xCBF43926u, DataHash.Crc32("123456789"));
    [Fact] public void Crc64() => Assert.Equal(0x6C40DF5F0B497347ul, DataHash.Crc64("123456789"));
}

public class DataBufferTests
{
    [Fact]
    public void RoundTripsEveryType()
    {
        var b = new DataBuffer(4);
        b.WriteBool(true); b.WriteI8(-2); b.WriteU8(250); b.WriteI16(-300); b.WriteU16(60000);
        b.WriteI32(-70000); b.WriteU32(4000000000); b.WriteI64(-5); b.WriteU64(7); b.WriteF16((Half)1.5);
        b.WriteF32(2.5f); b.WriteF64(3.25); b.WriteString("abc");
        Assert.True(b.ReadBool()); Assert.Equal(-2, b.ReadI8()); Assert.Equal(250, b.ReadU8());
        Assert.Equal(-300, b.ReadI16()); Assert.Equal(60000, b.ReadU16()); Assert.Equal(-70000, b.ReadI32());
        Assert.Equal(4000000000u, b.ReadU32()); Assert.Equal(-5, b.ReadI64()); Assert.Equal(7ul, b.ReadU64());
        Assert.Equal((Half)1.5, b.ReadF16()); Assert.Equal(2.5f, b.ReadF32()); Assert.Equal(3.25, b.ReadF64());
        Assert.Equal("abc", b.ReadString(3));
        Assert.Equal(0, b.Remaining);
        Assert.Throws<InvalidOperationException>(() => b.ReadU8());
    }

    [Fact]
    public void ByteOrder()
    {
        var b = new DataBuffer { Order = Kiln.ByteOrder.Big };
        b.WriteU16(0x0102);
        b.Order = Kiln.ByteOrder.Little;
        Assert.Equal(1, b.ReadU8());
        b.Head = 0;
        Assert.Equal(0x0201, b.ReadU16());
    }

    [Fact]
    public void CursorsWrapCopy()
    {
        var b = DataBuffer.Wrap("abcdef");
        Assert.Equal((0, 6, 6), (b.Head, b.Tail, b.Remaining));
        b.Head = 2; b.Skip(1);
        Assert.Equal(3, b.Head);
        b.Tail = 5;
        Assert.Equal("de", b.ToString());
        var c = new DataBuffer();
        c.Copy(b);
        Assert.Equal("de", c.ToString());
        b.Reset();
        b.Reserve(100);
        Assert.True(b.Capacity >= 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Head = 1);
    }
}

public class EmlStatusTests
{
    [Fact]
    public void ReadsKilnEmlState()
    {
        var f = Path.GetTempFileName();
        File.WriteAllText(f, """
            {"format":1,"kilnEml":"0.1.0","eml":"0.2.0","phase":"done","file":"enshrouded_server","patched":1,"cached":false,
             "restored":false,"ms":283.1,"error":null,"aside":null,"mods":[
              {"id":"kiln_eml_slice","version":"1.0.0","name":"slice","source":"game-mods","path":"Z:\\x","state":"loaded","reason":null,"capabilities":["patch"]},
              {"id":null,"version":null,"name":null,"source":"game-mods","path":"Z:\\y","state":"skipped","reason":"an EMF mod","capabilities":[]}]}
            """);
        var s = Kiln.Eml.Load(f);
        Assert.NotNull(s);
        Assert.Equal("done", s!.Phase);
        Assert.Equal(1, s.Patched);
        Assert.Equal("0.2.0", s.EmlVersion);
        Assert.Equal(2, s.Mods.Count);
        Assert.Equal("patch", s.Mods[0].Capabilities[0]);
        Assert.Null(Kiln.Eml.Load(f + ".missing"));
        File.WriteAllText(f, "{\"format\":2}");
        Assert.Null(Kiln.Eml.Load(f));
        File.Delete(f);
    }
}
