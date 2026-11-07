// SPDX-License-Identifier: MIT
// GameValueCodec: the checked scalar reads and writes behind Reflected components, GameEvents.Post and kiln-set; and
// the fail-soft face of GameTypes / GameSystems without kiln.types.
using Kiln;
using Xunit;

public class GameValueCodecTests
{
    private static readonly (string, long)[] Rarity = [("Common", 0), ("Uncommon", 1), ("Rare", 2), ("Epic", 3), ("Legendary", 4)];

    [Fact]
    public void ReadsSignedAndUnsignedWidths()
    {
        Assert.Equal(-1L, GameValueCodec.Read([0xff], GameTypeKind.S8, 1));
        Assert.Equal(255L, GameValueCodec.Read([0xff], GameTypeKind.U8, 1));
        Assert.Equal(-2L, GameValueCodec.Read([0xfe, 0xff, 0xff, 0xff], GameTypeKind.S32, 4));
        Assert.Equal(ulong.MaxValue, GameValueCodec.Read([0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff], GameTypeKind.U64, 8));
        Assert.Equal(true, GameValueCodec.Read([1], GameTypeKind.Bool, 1));
        Assert.Equal(1.5, (double)GameValueCodec.Read(BitConverter.GetBytes(1.5f), GameTypeKind.F32, 4), 6);
    }

    [Fact]
    public void WritesWithinRange()
    {
        var b = new byte[4];
        GameValueCodec.Write(b, GameTypeKind.S32, 4, -20);
        Assert.Equal(-20L, GameValueCodec.Read(b, GameTypeKind.S32, 4));
        GameValueCodec.Write(b, GameTypeKind.U16, 2, 65535);
        Assert.Equal(65535L, GameValueCodec.Read(b, GameTypeKind.U16, 2));
        GameValueCodec.Write(b, GameTypeKind.F32, 4, 2.25);
        Assert.Equal(2.25, (double)GameValueCodec.Read(b, GameTypeKind.F32, 4), 6);
        GameValueCodec.Write(b, GameTypeKind.S32, 4, 7.0);  // a whole double is a whole number
        Assert.Equal(7L, GameValueCodec.Read(b, GameTypeKind.S32, 4));
    }

    [Fact]
    public void RefusesWhatDoesNotFitAndWritesNothing()
    {
        var b = new byte[] { 9, 9, 9, 9 };
        Assert.Throws<ArgumentException>(() => GameValueCodec.Write(b, GameTypeKind.S32, 4, 1L << 40, field: "hp"));
        Assert.Throws<ArgumentException>(() => GameValueCodec.Write(b, GameTypeKind.U8, 1, -1));
        Assert.Throws<ArgumentException>(() => GameValueCodec.Write(b, GameTypeKind.U8, 1, 256));
        Assert.Throws<ArgumentException>(() => GameValueCodec.Write(b, GameTypeKind.S32, 4, 1.5));
        Assert.Throws<ArgumentException>(() => GameValueCodec.Write(b, GameTypeKind.Bool, 1, 2));
        Assert.Throws<ArgumentException>(() => GameValueCodec.Write(b, GameTypeKind.F32, 4, 1e300));
        Assert.Throws<ArgumentException>(() => GameValueCodec.Write(b, GameTypeKind.S32, 4, "many"));
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, b);
    }

    [Fact]
    public void EnumsTakeTheirOwnValuesByNameOrNumber()
    {
        var b = new byte[1];
        GameValueCodec.Write(b, GameTypeKind.Enum, 1, "legendary", Rarity);
        Assert.Equal(4L, GameValueCodec.Read(b, GameTypeKind.Enum, 1));
        GameValueCodec.Write(b, GameTypeKind.Enum, 1, 2, Rarity);
        Assert.Equal("Rare (2)", GameValueCodec.Format(GameValueCodec.Read(b, GameTypeKind.Enum, 1), GameTypeKind.Enum, Rarity));
        Assert.Throws<ArgumentException>(() => GameValueCodec.Write(b, GameTypeKind.Enum, 1, 9, Rarity));
        Assert.Throws<ArgumentException>(() => GameValueCodec.Write(b, GameTypeKind.Enum, 1, "Mythic", Rarity));
        GameValueCodec.Write(b, GameTypeKind.Enum, 1, 9, []);  // an enum that lists no values takes any that fits
    }

    [Fact]
    public void FormatsBitmasksInHex()
    {
        Assert.Equal("0x81", GameValueCodec.Format(0x81L, GameTypeKind.Bitmask8));
        Assert.Equal("false", GameValueCodec.Format(false, GameTypeKind.Bool));
    }

    [Fact]
    public void WithoutKilnTypesEverythingIsOffNotBroken()
    {
        Assert.False(GameTypes.Available);
        Assert.Null(GameTypes.Find("Health"));
        Assert.Empty(GameTypes.All);
        Assert.Empty(GameTypes.Components);
        Assert.False(GameSystems.Available);
        Assert.Empty(GameSystems.All);
        Assert.Null(GameSystems.Find("durability_loss"));
        Assert.Throws<ArgumentException>(() => GameSystems.SetEnabled("durability_loss", false));
        Assert.Throws<NotSupportedException>(() => GameSystems.Profile(true));
        Assert.Equal("kiln.types is not running", GameTypes.Status);
    }
}
