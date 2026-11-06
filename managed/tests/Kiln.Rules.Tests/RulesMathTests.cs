// SPDX-License-Identifier: MIT
using System.Text.Json;
using Xunit;

namespace Kiln.Rules.Tests;

public class RulesMathTests
{
    private static readonly EntityId P = new(3);

    private static EffectiveRules Combine(params PlayerRules[] sets) =>
        RulesMath.Combine(P, sets.Select(r => ((RuleSet?)null, r)).ToList(), RulesLimits.Default);

    [Fact]
    public void NothingIsTheGamesRules()
    {
        var e = Combine();
        Assert.True(e.IsDefault);
        Assert.Equal(RulesNativePart.Default, e.Native);
    }

    [Fact]
    public void SwitchesGrantAndADenialWins()
    {
        Assert.True(Combine(new PlayerRules { FlyAnywhere = true }).FlyAnywhere);
        Assert.True(Combine(new PlayerRules { FlyAnywhere = true }, new PlayerRules()).FlyAnywhere);
        Assert.False(Combine(new PlayerRules { FlyAnywhere = true }, new PlayerRules { FlyAnywhere = false }).FlyAnywhere);
        Assert.False(Combine(new PlayerRules { FlyAnywhere = false }).FlyAnywhere);
    }

    [Fact]
    public void CarryingAndKeepingFollowTheirSwitches()
    {
        var e = Combine(new PlayerRules { FlyAnywhere = true, BuildOutsideBases = true });
        Assert.True(e.FlightCarriesBody);
        Assert.True(e.KeepBuildsOutsideBases);
        e = Combine(new PlayerRules { FlyAnywhere = true, FlightCarriesBody = false });
        Assert.False(e.FlightCarriesBody);
        e = Combine(new PlayerRules { FlightCarriesBody = true, KeepBuildsOutsideBases = true });
        Assert.False(e.FlightCarriesBody);         // not without flight
        Assert.False(e.KeepBuildsOutsideBases);    // not without building outside bases
    }

    [Fact]
    public void MultipliersMultiply()
    {
        Assert.Equal(0.25, Combine(new PlayerRules { BuildCost = 0.5 }, new PlayerRules { BuildCost = 0.5 }).BuildCost, 6);
        Assert.Equal(0, Combine(new PlayerRules { BuildCost = 0 }, new PlayerRules { BuildCost = 2 }).BuildCost);
        Assert.Equal(6, Combine(new PlayerRules { DigDamage = 2 }, new PlayerRules { DigDamage = 3 }).DigDamage);
    }

    [Fact]
    public void FloorsTakeTheHighest()
    {
        var e = Combine(new PlayerRules { MinStamina = 0.25 }, new PlayerRules { MinStamina = 1, MinMana = 0.5 });
        Assert.Equal(1, e.MinStamina);
        Assert.Equal(0.5, e.MinMana);
        Assert.Equal(0, e.MinHealth);
    }

    [Fact]
    public void WaterIsTheMostGenerous()
    {
        var a = new PlayerRules { Water = WaterRule.Rate(2, "Tool_T1_WateringCan") };
        var b = new PlayerRules { Water = WaterRule.Rate(5, "my-mod.big-can") };
        var w = Combine(a, b).Water!;
        Assert.Equal(5, w.ChargesPerSecond);
        Assert.Equal(new[] { "my-mod.big-can", "Tool_T1_WateringCan" }, w.Items);
        var all = Combine(a, new PlayerRules { Water = WaterRule.KeepFull() }).Water!;
        Assert.True(all.KeepsFull);
        Assert.Null(all.Items);   // one rule set without a list: every container
    }

    [Fact]
    public void TheServerLimitsHold()
    {
        var limits = RulesLimits.Default with { AllowFlyAnywhere = false, MinBuildCost = 0.5, MaxDigDamage = 10 };
        var e = RulesMath.Combine(P, [(null, PlayerRules.Creative)], limits);
        Assert.False(e.FlyAnywhere);
        Assert.False(e.FlightCarriesBody);
        Assert.Equal(0.5, e.BuildCost);
        Assert.Equal(10, e.DigDamage);
        Assert.Contains("flyAnywhere", e.Denied);
        Assert.False(e.IgnoreBasePermissions);    // default limit: other players' bases stay theirs
    }

    [Fact]
    public void BasePermissionsNeedTheServer()
    {
        var r = new PlayerRules { IgnoreBasePermissions = true };
        Assert.False(RulesMath.Combine(P, [(null, r)], RulesLimits.Default).IgnoreBasePermissions);
        Assert.True(RulesMath.Combine(P, [(null, r)], RulesLimits.Default with { AllowIgnoreBasePermissions = true }).IgnoreBasePermissions);
    }

    [Fact]
    public void CreativeIsWhatTheCommandPromises()
    {
        var e = Combine(PlayerRules.Creative);
        Assert.True(e.FlyAnywhere && e.FlightCarriesBody && e.BuildOutsideBases && e.IgnoreNoBuildZones && e.Invulnerable);
        Assert.Equal(0, e.BuildCost);
        Assert.Equal(0, e.BuildRefund);
        Assert.Equal(1000, e.DigDamage);
        Assert.Equal(1, e.MinStamina);
        Assert.True(e.Water!.KeepsFull);
        var n = e.Native;
        Assert.Equal(RulesNative.FlyAnywhere | RulesNative.BuildOutsideBases | RulesNative.IgnoreNoBuildZones | RulesNative.IgnoreEnemies |
                     RulesNative.IgnoreShroud, n.Flags);
        Assert.Equal(0f, n.BuildCost);
    }

    [Fact]
    public void JsonRoundTrips()
    {
        var json = PlayerRules.Creative.ToJson();
        Assert.Contains("\"flyAnywhere\": true", json);
        Assert.Contains("\"chargesPerSecond\": \"Infinity\"", json);
        var back = PlayerRules.FromJson(json);
        Assert.Equal(PlayerRules.Creative, back);
        var easy = PlayerRules.FromJson("""
            // easy farming: water comes back, digging is quicker
            { "water": { "chargesPerSecond": 2, "items": ["Tool_T1_WateringCan"] }, "digDamage": 3, }
            """);
        Assert.Equal(3, easy.DigDamage);
        Assert.Equal(2, easy.Water!.ChargesPerSecond);
    }

    [Fact]
    public void JsonNamesAWrongMember()
    {
        var e = Assert.Throws<JsonException>(() => PlayerRules.FromJson("""{ "flyAnywere": true }"""));
        Assert.Contains("flyAnywere", e.Message);
    }

    [Fact]
    public void ProblemsNameTheMember()
    {
        Assert.Empty(PlayerRules.Creative.Problems());
        var p = new PlayerRules { BuildCost = -1, MinStamina = 2, Water = WaterRule.Rate(0) }.Problems();
        Assert.Equal(3, p.Count);
        Assert.Contains(p, x => x.StartsWith("buildCost"));
        Assert.Contains(p, x => x.StartsWith("minStamina"));
        Assert.Contains(p, x => x.StartsWith("water.chargesPerSecond"));
    }

    [Fact]
    public void WithOverlays()
    {
        var r = PlayerRules.Creative.With(new PlayerRules { DigDamage = 1, Invulnerable = false });
        Assert.Equal(1, r.DigDamage);
        Assert.False(r.Invulnerable);
        Assert.True(r.FlyAnywhere);
    }
}

public class RuleScopeTests
{
    private static readonly EntityId A = new(1), B = new(2);

    [Fact]
    public void PlayersAndEveryone()
    {
        Assert.True(RuleScope.ForPlayer(A).Covers(A, null));
        Assert.False(RuleScope.ForPlayer(A).Covers(B, null));
        Assert.True(RuleScope.Everyone.Covers(B, null));
        Assert.Throws<ArgumentException>(() => RuleScope.ForPlayer(EntityId.None));
    }

    [Fact]
    public void Areas()
    {
        var c = new WorldPosition(100, 50, 100);
        var sphere = RuleScope.Sphere(c, 10);
        Assert.True(sphere.Covers(A, new WorldPosition(105, 50, 105)));
        Assert.False(sphere.Covers(A, new WorldPosition(110, 50, 110)));
        Assert.False(sphere.Covers(A, null));   // an area needs a position
        var cyl = RuleScope.Cylinder(c, 10, 40, 60);
        Assert.True(cyl.Covers(A, new WorldPosition(108, 59, 100)));
        Assert.False(cyl.Covers(A, new WorldPosition(108, 61, 100)));
        var box = RuleScope.Box(new WorldPosition(10, 0, 10), new WorldPosition(0, 5, 0));
        Assert.True(box.Covers(A, new WorldPosition(5, 2, 5)));
        Assert.False(box.Covers(A, new WorldPosition(5, 6, 5)));
        Assert.True(sphere.Only(A).Covers(A, c));
        Assert.False(sphere.Only(A).Covers(B, c));
        Assert.True(sphere.IsArea && !RuleScope.Everyone.IsArea);
    }
}
