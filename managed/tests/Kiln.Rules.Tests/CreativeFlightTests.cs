// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Rules.Tests;

/// <summary>Character flight as a rule, and the creative menu's backpack grid on the wire.</summary>
public class CreativeFlightTests
{
    private static readonly EntityId P = new(7);
    private static readonly RulesLimits Allowed = RulesLimits.Default with { AllowCharacterFlight = true };

    private static EffectiveRules Combine(RulesLimits limits, params PlayerRules[] sets) =>
        RulesMath.Combine(P, sets.Select(r => ((RuleSet?)null, r)).ToList(), limits);

    [Fact]
    public void FlightIsOffUntilTheServerAllowsIt()
    {
        var asked = new PlayerRules { CharacterFlight = CharacterFlightMode.Unlimited };
        var held = Combine(RulesLimits.Default, asked);
        Assert.Equal(CharacterFlightMode.Off, held.CharacterFlight);
        Assert.Contains("characterFlight", held.Denied);
        Assert.Equal(0u, held.Native.Flags & (RulesNative.CharacterFlight | RulesNative.FlightBaseOnly));
        var on = Combine(Allowed, asked);
        Assert.Equal(CharacterFlightMode.Unlimited, on.CharacterFlight);
        Assert.Equal(RulesNative.CharacterFlight, on.Native.Flags & (RulesNative.CharacterFlight | RulesNative.FlightBaseOnly));
        Assert.Contains("character flight", on.ToString());
        Assert.False(on.IsDefault);
    }

    [Fact]
    public void AnExplicitOffWinsThenTheWidestMode()
    {
        var unlimited = new PlayerRules { CharacterFlight = CharacterFlightMode.Unlimited };
        var bases = new PlayerRules { CharacterFlight = CharacterFlightMode.BuildingArea };
        var off = new PlayerRules { CharacterFlight = CharacterFlightMode.Off };
        Assert.Equal(CharacterFlightMode.BuildingArea, Combine(Allowed, bases, new PlayerRules()).CharacterFlight);
        Assert.Equal(CharacterFlightMode.Unlimited, Combine(Allowed, bases, unlimited).CharacterFlight);
        Assert.Equal(CharacterFlightMode.Off, Combine(Allowed, unlimited, off).CharacterFlight);
        var b = Combine(Allowed, bases);
        Assert.Equal(RulesNative.CharacterFlight | RulesNative.FlightBaseOnly, b.Native.Flags & (RulesNative.CharacterFlight | RulesNative.FlightBaseOnly));
    }

    [Fact]
    public void FlightReadsAndWritesJsonAndMerges()
    {
        var r = PlayerRules.FromJson("""{ "characterFlight": "BuildingArea" }""");
        Assert.Equal(CharacterFlightMode.BuildingArea, r.CharacterFlight);
        Assert.Contains("\"characterFlight\": \"BuildingArea\"", r.ToJson());
        Assert.Equal(CharacterFlightMode.Off, r.With(new PlayerRules { CharacterFlight = CharacterFlightMode.Off }).CharacterFlight);
        Assert.Equal(CharacterFlightMode.BuildingArea, r.With(new PlayerRules()).CharacterFlight);
        Assert.Null(PlayerRules.Creative.CharacterFlight);  // the preset leaves it to the menu and /creative flight
    }

    [Fact]
    public void WithoutTheHooksFlightIsDeniedAndAVanillaGameGetsNone()
    {
        var on = Combine(Allowed, new PlayerRules { CharacterFlight = CharacterFlightMode.Unlimited });
        var none = RulesCrafting.Supported(on, 0);
        Assert.Equal(CharacterFlightMode.Off, none.CharacterFlight);
        Assert.Contains("characterFlightUnavailable", none.Denied);
        Assert.Same(on, RulesCrafting.Supported(on, RulesNative.HookFlight));
        var part = on.Native;
        Assert.True(part.HasPredicted);
        Assert.False(part.HasGlide);
        Assert.Equal(0u, part.WithoutPredicted.Flags & RulesNative.CharacterFlight);
    }

    [Fact]
    public void TheMenuReadsTheModeFromTheStateBits()
    {
        Assert.Equal(CharacterFlightMode.Off, CreativeMenu.FlightOf(CreativeAccess.On | CreativeAccess.FlightAllowed));
        Assert.Equal(CharacterFlightMode.Unlimited, CreativeMenu.FlightOf(CreativeAccess.FlightUnlimited));
        Assert.Equal(CharacterFlightMode.BuildingArea, CreativeMenu.FlightOf(CreativeAccess.FlightBuildingArea));
    }

    [Fact]
    public void ASlotTravelsAfterTheOptionsAndOlderRequestsStillDecode()
    {
        var w = new NetWriter();
        w.Byte((byte)CreativeAsk.Give).UInt32(9).Int32(3).Int32(-1).Int32(-1).Double(0).Int32(0).UInt64(0x0000000300000412);
        var r = new NetReader(w.ToArray());
        var req = CreativeNet.DecodeRequest(ref r);
        Assert.Equal(0x0000000300000412UL, req.Slot);
        Assert.Equal(3, req.Count);
        var old = new NetWriter();
        old.Byte((byte)CreativeAsk.Give).UInt32(9).Int32(3).Int32(-1).Int32(-1).Double(0).Int32(0).Byte(0).Byte(0);
        var r2 = new NetReader(old.ToArray());
        Assert.Equal(0UL, CreativeNet.DecodeRequest(ref r2).Slot);
        var bad = new NetWriter();
        bad.Byte((byte)CreativeAsk.Give).UInt32(9).Int32(3).Int32(-1).Int32(-1).Double(0).Int32(0).Byte(0).Byte(7);
        var data = bad.ToArray();
        Assert.Throws<NetFormatException>(() => { var r3 = new NetReader(data); CreativeNet.DecodeRequest(ref r3); });
    }

    [Fact]
    public void TheBackpackRoundTripsAndIsBounded()
    {
        var m = new CreativeBackpackMsg([new CreativeSlot(0x100000412, 77, 5), new CreativeSlot(0x200000412, 0, 0)]);
        var w = new NetWriter();
        CreativeNet.EncodeBackpack(m, w);
        var r = new NetReader(w.ToArray());
        var back = CreativeNet.DecodeBackpack(ref r);
        Assert.Equal(m.Slots, back.Slots);
        var big = new NetWriter();
        big.UInt16(CreativeBackpackMsg.MaxSlots + 1);
        var data = big.ToArray();
        Assert.Throws<FormatException>(() => { var r2 = new NetReader(data); CreativeNet.DecodeBackpack(ref r2); });
    }
}
