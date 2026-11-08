// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using System.IO.Compression;
using Kiln;
using Xunit;

namespace KilnEmlTests;

public class GuidHashTests
{
    // The Ring of Light's buff: shroudlink's upstream test records that EML's game.guid.hash gives 2348168016 for it.
    private static readonly Guid RingOfLight = Guid.Parse("b2f8db72-a69d-43db-baf1-c69937ec0264");
    // The Bronze Sword (Weapon_T1_1H_SwordEpic_01): its itemId field is the hash of its GUID (assets-test, docs/re/items.md).
    private static readonly Guid BronzeSword = Guid.Parse("1d622bd9-8170-41b1-822e-d3984b386d95");

    [Fact] public void MatchesEmlsValueForAKnownGuid() => Assert.Equal(2348168016u, GuidHash.Of(RingOfLight));
    [Fact] public void IsTheItemIdOfAnItem() => Assert.Equal(0x43108313u, GuidHash.IdOf(BronzeSword));

    [Fact]
    public void ZeroGuidHashesButHasNoId()
    {
        Assert.Equal(0x69691905u, GuidHash.Of(Guid.Empty));   // FNV-1a of 16 zero bytes (docs/re/content.md)
        Assert.Equal(0u, GuidHash.IdOf(Guid.Empty));          // the game's GuidToItemId says 0 for "none"
        Assert.Equal(0u, GuidHash.IdOf(new byte[16]));
    }

    [Fact]
    public void BytesAndGuidAgree()
    {
        var bytes = RingOfLight.ToByteArray();
        Assert.Equal(new byte[] { 0x72, 0xdb, 0xf8, 0xb2, 0x9d, 0xa6, 0xdb, 0x43 }, bytes[..8]);   // the game's byte order
        Assert.Equal(GuidHash.Of(RingOfLight), GuidHash.Of(bytes));
        Assert.Equal(DataHash.Fnv1a32(bytes), GuidHash.Of(RingOfLight));
        Assert.Equal(RingOfLight, GuidHash.FromBytes(bytes));
        Assert.Equal(bytes, GuidHash.ToBytes(RingOfLight));
        Assert.Throws<ArgumentException>(() => GuidHash.Of(new byte[15]));
        Assert.Throws<ArgumentException>(() => GuidHash.IdOf(new byte[3]));
    }
}

public class DataImageTests
{
    [Fact]
    public void ANewImageIsTransparentBlack()
    {
        var img = new DataImage(3, 2);
        Assert.Equal((3, 2), (img.Width, img.Height));
        Assert.Equal(24, img.Pixels.Length);
        Assert.All(img.Pixels.ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(new Rgba(0, 0, 0, 0), img.GetPixel(2, 1));
    }

    [Fact]
    public void PixelAccessChecksBounds()
    {
        var img = new DataImage(4, 3);
        img.SetPixel(3, 2, 1, 2, 3, 4);
        Assert.Equal(new Rgba(1, 2, 3, 4), img.GetPixel(3, 2));
        Assert.Equal(0x01020304u, img.GetPixelPacked(3, 2));
        img.SetPixelPacked(0, 1, 0xDEADBEEF);
        Assert.Equal(new Rgba(0xDE, 0xAD, 0xBE, 0xEF), img.GetPixel(0, 1));
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, img.Pixels.Slice((1 * 4 + 0) * 4, 4).ToArray());   // row-major, R G B A
        Assert.Null(img.GetPixel(4, 0));
        Assert.Null(img.GetPixel(0, 3));
        Assert.Null(img.GetPixel(-1, 0));
        Assert.Null(img.GetPixelPacked(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.SetPixel(4, 0, 0, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.SetPixelPacked(0, 3, 1));
        Assert.Equal("#01020304", new Rgba(1, 2, 3, 4).ToString());
    }

    [Fact]
    public void SizesAreChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DataImage(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DataImage(1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DataImage(1 << 20, 1 << 20));
        Assert.Throws<ArgumentException>(() => DataImage.FromRgba8(new byte[15], 2, 2));
    }

    [Fact]
    public void RawRgba8RoundTrips()
    {
        var raw = Enumerable.Range(0, 5 * 3 * 4).Select(i => (byte)(i * 7)).ToArray();
        var img = DataImage.FromRgba8(raw, 5, 3);
        raw[0] = 99;                       // a copy: the source does not alias the image
        Assert.Equal(0, img.Pixels[0]);
        var back = img.ToRgba8();
        back[1] = 77;                      // a copy again
        Assert.Equal(7, img.Pixels[1]);
        var clone = img.Clone();
        clone.SetPixel(0, 0, 1, 1, 1, 1);
        Assert.Equal(0, img.Pixels[0]);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 9)]
    [InlineData(9, 1)]
    [InlineData(2, 2)]
    [InlineData(64, 33)]
    [InlineData(257, 3)]
    public void PngRoundTripsEverySize(int w, int h)
    {
        var rng = new Random(w * 1000 + h);
        var img = new DataImage(w, h);
        rng.NextBytes(img.Pixels);
        var png = img.EncodePng();
        Assert.True(DataImage.IsPng(png));
        var back = DataImage.DecodePng(png);
        Assert.Equal((w, h), (back.Width, back.Height));
        Assert.Equal(img.Pixels.ToArray(), back.Pixels.ToArray());
        Assert.Equal(img.Pixels.ToArray(), DataImage.Decode(png).Pixels.ToArray());
    }

    [Fact]
    public void SmoothImagesCompress()
    {
        var img = new DataImage(128, 128);
        for (var y = 0; y < 128; y++)
            for (var x = 0; x < 128; x++) img.SetPixel(x, y, (byte)x, (byte)y, (byte)(x + y), 255);
        var png = img.EncodePng();
        Assert.True(png.Length < 128 * 128 * 4 / 4, $"{png.Length} bytes for 64 KiB of gradient");
        Assert.Equal(img.Pixels.ToArray(), DataImage.DecodePng(png).Pixels.ToArray());
    }

    [Fact]
    public void TheEncodedFileIsAWellFormedPng()
    {
        var png = new DataImage(3, 2).EncodePng();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal(13u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8)));
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)));
        Assert.Equal(new byte[] { 8, 6, 0, 0, 0 }, png[24..29]);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 }, png[^12..]);   // IEND and its CRC
    }

    // Decoded PNGs from other programs: every colour type, bit depth, transparency kind, filter and Adam7.
    public static IEnumerable<object[]> GoldenNames() => ImageGolden.Cases.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(GoldenNames))]
    public void DecodesPngsOtherProgramsWrote(string name)
    {
        var (png, rgba, w, h) = ImageGolden.Cases[name];
        var img = DataImage.DecodePng(Convert.FromBase64String(png));
        Assert.Equal((w, h), (img.Width, img.Height));
        Assert.Equal(rgba, Convert.ToHexString(img.Pixels).ToLowerInvariant());
    }

    private static byte[] Golden(string name) => Convert.FromBase64String(ImageGolden.Cases[name].Png);

    private static void FixCrc(byte[] png, int chunkStart)
    {
        var len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(chunkStart));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(chunkStart + 8 + len), DataHash.Crc32(png.AsSpan(chunkStart + 4, 4 + len)));
    }

    [Fact]
    public void RefusesFilesThatAreNotValidPngs()
    {
        var good = Golden("rgba8");
        Assert.Throws<InvalidDataException>(() => DataImage.DecodePng(new byte[] { 1, 2, 3 }));
        Assert.Throws<NotSupportedException>(() => DataImage.Decode("GIF89a......"u8));
        Assert.Throws<InvalidDataException>(() => DataImage.DecodePng(good[..^20]));     // cut inside the data
        Assert.Throws<InvalidDataException>(() => DataImage.DecodePng(good[..(good.Length - 12)]));   // no IEND

        var badCrc = (byte[])good.Clone();
        badCrc[20] ^= 1;                                                                  // width, behind IHDR's checksum
        Assert.Throws<InvalidDataException>(() => DataImage.DecodePng(badCrc));

        var noHeader = (byte[])good.Clone();
        noHeader[12] = (byte)'X';
        FixCrc(noHeader, 8);
        Assert.Throws<InvalidDataException>(() => DataImage.DecodePng(noHeader));         // the first chunk is not IHDR

        var huge = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(16), 0x7fffffff);               // 2G x 9
        FixCrc(huge, 8);
        Assert.Throws<InvalidDataException>(() => DataImage.DecodePng(huge));

        var badDepth = (byte[])good.Clone();
        badDepth[24] = 3;                                                                 // 3-bit RGBA does not exist
        FixCrc(badDepth, 8);
        Assert.Throws<InvalidDataException>(() => DataImage.DecodePng(badDepth));

        var tooLong = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(tooLong.AsSpan(8), 0xfffffff0);             // a chunk longer than the file
        Assert.Throws<InvalidDataException>(() => DataImage.DecodePng(tooLong));
    }

    [Fact]
    public void RefusesDataThatDoesNotFitTheHeader()
    {
        // a valid 9x9 RGBA file whose header says 9x10: the image data ends early
        var png = (byte[])Golden("rgba8").Clone();
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 10);
        FixCrc(png, 8);
        Assert.Throws<InvalidDataException>(() => DataImage.DecodePng(png));

        // a palette file with a pixel whose index is not in the palette: 1 pixel, index 5, a 1-colour palette, 8 bits
        var raw = new byte[] { 0, 5 };
        using var z = new MemoryStream();
        using (var zl = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zl.Write(raw);
        var bad = Build(1, 1, 8, 3, [new("PLTE", [1, 2, 3]), new("IDAT", z.ToArray())]);
        Assert.Throws<InvalidDataException>(() => DataImage.DecodePng(bad));

        // an unknown critical chunk is refused, an unknown ancillary one is skipped
        var ok = Build(1, 1, 8, 0, [new("zzZz", [1]), new("IDAT", Zlib([0, 200]))]);
        Assert.Equal(new Rgba(200, 200, 200, 255), DataImage.DecodePng(ok).GetPixel(0, 0));
        var critical = Build(1, 1, 8, 0, [new("ZZzz", [1]), new("IDAT", Zlib([0, 200]))]);
        Assert.Throws<NotSupportedException>(() => DataImage.DecodePng(critical));

        // a filter type that does not exist
        var filter = Build(1, 1, 8, 0, [new("IDAT", Zlib([9, 200]))]);
        Assert.Throws<InvalidDataException>(() => DataImage.DecodePng(filter));
    }

    private static byte[] Zlib(byte[] raw)
    {
        using var z = new MemoryStream();
        using (var zl = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zl.Write(raw);
        return z.ToArray();
    }

    private readonly record struct Chunk(string Type, byte[] Data);

    private static byte[] Build(int w, int h, int depth, int colorType, Chunk[] chunks)
    {
        using var m = new MemoryStream();
        m.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
        ihdr[8] = (byte)depth;
        ihdr[9] = (byte)colorType;
        void Put(string type, byte[] data)
        {
            var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            var word = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
            m.Write(word);
            m.Write(body);
            BinaryPrimitives.WriteUInt32BigEndian(word, DataHash.Crc32(body));
            m.Write(word);
        }
        Put("IHDR", ihdr);
        foreach (var c in chunks) Put(c.Type, c.Data);
        Put("IEND", []);
        return m.ToArray();
    }
}
