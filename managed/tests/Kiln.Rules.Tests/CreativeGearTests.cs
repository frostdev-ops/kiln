// SPDX-License-Identifier: MIT
// Creative part 3: gear options (upgrades, gem slot), the journey level cap, the request's trailing options, map reveal.
using Xunit;

namespace Kiln.Rules.Tests;

public sealed class CreativeGearTests
{
    private static ItemInfo Item(ItemCategory category, bool gear) => new(new ItemId(7), "Thing", null, gear ? 1 : 100, category,
        ItemRarity.Common, gear, 2, 55, 0, 0, Guid.Empty);

    [Fact]
    public void GearExtrasAreCheckedOnTheServer()
    {
        var sword = Item(ItemCategory.Weapons, true);
        var helmet = Item(ItemCategory.Equipment, true);
        var wood = Item(ItemCategory.Materials, false);
        Assert.Null(CreativeTools.ValidateExtras(sword, 5, true, available: true));
        Assert.Null(CreativeTools.ValidateExtras(helmet, 3, false, available: true));
        Assert.Null(CreativeTools.ValidateExtras(wood, 0, false, available: false));
        Assert.NotNull(CreativeTools.ValidateExtras(helmet, 0, true, available: true));   // gem slots: weapons only
        Assert.NotNull(CreativeTools.ValidateExtras(wood, 1, false, available: true));    // upgrades: gear only
        Assert.NotNull(CreativeTools.ValidateExtras(sword, 6, false, available: true));
        Assert.NotNull(CreativeTools.ValidateExtras(sword, -1, false, available: true));
        Assert.NotNull(CreativeTools.ValidateExtras(sword, 1, false, available: false));  // unknown build: refused, not ignored
    }

    [Fact]
    public void JourneyCapsTheLevelAtTheCharacter()
    {
        Assert.Equal(12, CreativeTools.JourneyLevel(30, 12, 1, 55));
        Assert.Equal(12, CreativeTools.JourneyLevel(null, 12, 1, 55));   // unset: the character's level
        Assert.Equal(5, CreativeTools.JourneyLevel(5, 12, 1, 55));
        Assert.Throws<InvalidOperationException>(() => CreativeTools.JourneyLevel(null, 3, 20, 20));
        Assert.Throws<InvalidOperationException>(() => CreativeTools.JourneyLevel(30, 0, 1, 55));
    }

    [Fact]
    public void JourneyReservesLevelsForUpgradesAndRefusesGearAboveTheCap()
    {
        Assert.Equal(8, CreativeTools.JourneyLevel(30, 12, 1, 55, 5, ItemRarity.Epic));
        Assert.Equal(10, CreativeTools.JourneyLevel(null, 12, 1, 55, 2, ItemRarity.Epic));
        Assert.Equal(11, CreativeTools.JourneyLevel(30, 12, 1, 55, 5, ItemRarity.Common));
        Assert.Equal(7, CreativeTools.JourneyLevel(null, 12, 1, 55, 5)); // unknown rarity: reserve all five
        Assert.Throws<InvalidOperationException>(() => CreativeTools.JourneyLevel(20, 20, 20, 20, 1));
        Assert.Throws<InvalidOperationException>(() => CreativeTools.JourneyLevel(1, 12, 300, 255));
    }

    [Theory]
    [InlineData(-1)] [InlineData(6)] [InlineData(254)] [InlineData(256)] [InlineData(int.MaxValue)]
    public async Task ItemsRejectsOutOfRangeUpgradeCounts(int unlocked)
    {
        var result = await Items.Give(new EntityId(3), new ItemId(7), gear: new Gear { Unlocked = unlocked });
        Assert.Equal(ItemsStatus.Invalid, result.Status);
    }

    [Fact]
    public void ItemsRequestKeepsTheLegacyAbiLayout()
    {
        var t = typeof(Items).Assembly.GetType("Kiln.ItemsNative+Request", throwOnError: true)!;
        Assert.Equal(40, System.Runtime.InteropServices.Marshal.SizeOf(t));
        Assert.Equal((IntPtr)25, System.Runtime.InteropServices.Marshal.OffsetOf(t, "Unlock"));
        Assert.Equal((IntPtr)28, System.Runtime.InteropServices.Marshal.OffsetOf(t, "Pide"));
        Assert.Equal((IntPtr)32, System.Runtime.InteropServices.Marshal.OffsetOf(t, "Slot"));
    }

    [Fact]
    public void GiveOptionsTravelAsATrailingFieldAndOldRequestsStillDecode()
    {
        var opts = CreativeGiveOptions.Of(9, true, true);
        Assert.Equal(5 | CreativeGiveOptions.GemSlot | CreativeGiveOptions.Journey, opts);
        var w = new NetWriter();
        w.Byte((byte)CreativeAsk.Give).UInt32(0x43108313).Int32(1).Int32(10).Int32(2).Double(0).Int32(opts);
        var r = new NetReader(w.ToArray());
        Assert.Equal(opts, CreativeNet.DecodeRequest(ref r).Options);
        var old = new NetWriter();
        old.Byte((byte)CreativeAsk.Give).UInt32(0x43108313).Int32(1).Int32(10).Int32(2).Double(0);
        var r2 = new NetReader(old.ToArray());
        var req = CreativeNet.DecodeRequest(ref r2);
        Assert.Equal(0, req.Options);
        Assert.Equal(10, req.Level);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void PartialOptionsCannotSilentlyDropJourney(int bytes)
    {
        var w = new NetWriter();
        w.Byte((byte)CreativeAsk.Give).UInt32(7).Int32(1).Int32(10).Int32(2).Double(0);
        for (var i = 0; i < bytes; ++i) w.Byte(i == bytes - 1 ? (byte)2 : (byte)0);
        var data = w.ToArray();
        Assert.Throws<NetFormatException>(() => { var r = new NetReader(data); CreativeNet.DecodeRequest(ref r); });
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void LegacyRequestsAcceptGameTransportZeroPadding(int padding)
    {
        var w = new NetWriter();
        w.Byte((byte)CreativeAsk.Give).UInt32(7).Int32(1).Int32(10).Int32(2).Double(0);
        for (var i = 0; i < padding; ++i) w.Byte(0);
        var r = new NetReader(w.ToArray());
        Assert.Equal(0, CreativeNet.DecodeRequest(ref r).Options);
    }

    [Theory]
    [InlineData(-1)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(0x400)]
    public void UnknownOptionsAreRefusedByTheDecoderAndHostedInbox(int options)
    {
        var w = new NetWriter();
        w.Byte((byte)CreativeAsk.Give).UInt32(7).Int32(1).Int32(10).Int32(2).Double(0).Int32(options);
        var data = w.ToArray();
        Assert.Throws<NetFormatException>(() => { var r = new NetReader(data); CreativeNet.DecodeRequest(ref r); });
        Assert.False(new CreativeInbox().Enqueue(new EntityId(3), new CreativeRequest(CreativeAsk.Give, Options: options), true));
    }

    [Fact]
    public void MapRevealChoicesAndNoServiceFailSoft()
    {
        Assert.Equal(4, CreativeMap.Choices.Length);
        Assert.Equal((RulesNative.MapRadius, 200f), (CreativeMap.Choices[1].Mode, CreativeMap.Choices[1].Radius));
        Assert.Equal(RulesNative.MapWhole, CreativeMap.Choices[3].Mode);
        Assert.False(CreativeMap.Available);               // no kiln.rules in a unit test
        Assert.NotNull(CreativeMap.Set(3));                 // refused, not thrown
        Assert.Equal(0, CreativeMap.Choice);
        Assert.NotNull(CreativeMap.Set(99));
        // A world may have closed after selecting a mode, leaving no native service to reset.
        typeof(CreativeMap).GetField("_choice", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, 3);
        CreativeMap.Reset();
        Assert.Equal(0, CreativeMap.Choice);
    }
}
