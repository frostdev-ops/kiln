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
        Assert.True(e.FreezeShroudTimer && e.UnlimitedGliding);
        Assert.Equal(0, e.GlideSink);
        Assert.NotNull(e.GlideBoost);
        var n = e.Native;
        Assert.Equal(RulesNative.FlyAnywhere | RulesNative.BuildOutsideBases | RulesNative.IgnoreNoBuildZones | RulesNative.IgnoreEnemies |
                     RulesNative.IgnoreShroud | RulesNative.ShroudFreeze | RulesNative.GlideUnlimited | RulesNative.GlideBoost, n.Flags);
        Assert.Equal(0f, n.BuildCost);
        Assert.Equal(0f, n.GlideSink);
        Assert.Equal((float)GlideBoostRule.Default.Thrust, n.GlideThrust);
        Assert.Equal((float)GlideBoostRule.Default.MaxSpeed, n.GlideMaxSpeed);
        Assert.Equal(15, e.GlideBoost!.Climb);  // like a plane: boosting while looking up climbs at up to 15 m/s
        Assert.Equal(15f, n.GlideClimb);
    }

    [Fact]
    public void GlidingLikeAPlaneCombinesAndIsChecked()
    {
        var a = new PlayerRules { GlideBoost = new GlideBoostRule { Climb = 5 } };
        var b = new PlayerRules { GlideBoost = new GlideBoostRule { Climb = 25, MaxSpeed = 20 } };
        var e = Combine(a, b);
        Assert.Equal(25, e.GlideBoost!.Climb);  // the most generous
        Assert.Equal(35, e.GlideBoost.MaxSpeed);
        Assert.Equal(25f, e.Native.GlideClimb);
        Assert.Equal(0f, Combine(new PlayerRules { GlideBoost = new GlideBoostRule { Climb = 0 } }).Native.GlideClimb);  // never climbs
        // the server's speed limit holds the climb rate too
        var held = RulesMath.Combine(default, [(null, b)], RulesLimits.Default with { MaxGlideSpeed = 10 });
        Assert.Equal(10, held.GlideBoost!.Climb);
        Assert.Equal(10, held.GlideBoost.MaxSpeed);
        // values that make no sense are refused by name
        Assert.Contains(new PlayerRules { GlideBoost = new GlideBoostRule { Climb = -1 } }.Problems(), p => p.Contains("glideBoost.climb"));
        Assert.Contains(new PlayerRules { GlideBoost = new GlideBoostRule { Climb = double.NaN } }.Problems(), p => p.Contains("glideBoost.climb"));
        Assert.Contains(new PlayerRules { GlideBoost = new GlideBoostRule { Climb = 500 } }.Problems(), p => p.Contains("glideBoost.climb"));
        Assert.Empty(PlayerRules.Creative.Problems());
        // JSON: a boost without "climb" climbs at the default; "climb": 0 never climbs
        Assert.Equal(15, PlayerRules.FromJson("{ \"glideBoost\": { \"thrust\": 10 } }").GlideBoost!.Climb);
        Assert.Equal(0, PlayerRules.FromJson("{ \"glideBoost\": { \"climb\": 0 } }").GlideBoost!.Climb);
        // the client's half: without the glide rules (a client without the rules channel) nothing of the climb stays
        Assert.Equal(0f, Combine(PlayerRules.Creative).Native.WithoutGlide.GlideClimb);
    }

    [Fact]
    public void Dev15sCreativePresetIsUpgradedForTheClimb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiln-rules-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var outer = RulesPresets.FolderOverride;
        RulesPresets.FolderOverride = dir;
        try
        {
            var file = Path.Combine(dir, "creative.json");
            // 0.2.0-dev.15 wrote the boost without "climb": the same values today (climb's default), the member missing
            var dev15 = PlayerRules.Creative.ToJson().Replace(",\n    \"climb\": 15", "");
            Assert.DoesNotContain("climb", dev15);
            File.WriteAllText(file, "// Kiln's \"creative\" rules\n" + dev15 + "\n");
            Assert.True(RulesPresets.IsEarlierDefault("creative", file));
            Assert.Equal(PlayerRules.Creative, RulesPresets.Get("creative"));
            Assert.Contains("\"climb\"", File.ReadAllText(file));  // rewritten: the owner sees the new knob
            Assert.False(RulesPresets.IsEarlierDefault("creative", file));  // and it stays (no rewrite on every read)
            // an edited dev.15 file stays the owner's
            File.WriteAllText(file, dev15.Replace("\"thrust\": 15", "\"thrust\": 20"));
            Assert.False(RulesPresets.IsEarlierDefault("creative", file));
            Assert.Equal(20, RulesPresets.Get("creative")!.GlideBoost!.Thrust);
        }
        finally
        {
            RulesPresets.FolderOverride = outer;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ShroudAndGlidingCombine()
    {
        var a = new PlayerRules { MinShroudTime = 0.25, GlideSink = 0.5, GlideBoost = new GlideBoostRule { Thrust = 5, MaxSpeed = 50, Lift = 0 } };
        var b = new PlayerRules { MinShroudTime = 0.5, GlideSink = 0.5, GlideBoost = new GlideBoostRule { Thrust = 20, MaxSpeed = 20, Lift = 4 } };
        var e = Combine(a, b);
        Assert.Equal(0.5, e.MinShroudTime);     // floors: the highest
        Assert.Equal(0.25, e.GlideSink);        // multipliers: multiplied
        Assert.Equal(20, e.GlideBoost!.Thrust); // boost: the most generous of each value
        Assert.Equal(50, e.GlideBoost.MaxSpeed);
        Assert.Equal(4, e.GlideBoost.Lift);
        var denied = Combine(PlayerRules.Creative, new PlayerRules { FreezeShroudTimer = false, UnlimitedGliding = false });
        Assert.False(denied.FreezeShroudTimer);
        Assert.False(denied.UnlimitedGliding);
        Assert.True(denied.FlyAnywhere);
    }

    [Fact]
    public void ServerLimitsHoldShroudAndGliding()
    {
        var limits = RulesLimits.Default with { AllowFreezeShroudTimer = false, MaxShroudFloor = 0.5, MaxGlideSpeed = 25 };
        var e = RulesMath.Combine(default, [(null, PlayerRules.Creative with { MinShroudTime = 1 })], limits);
        Assert.False(e.FreezeShroudTimer);
        Assert.Equal(0.5, e.MinShroudTime);
        Assert.Equal(25, e.GlideBoost!.MaxSpeed);
        Assert.Contains("freezeShroudTimer", e.Denied);
        var off = RulesMath.Combine(default, [(null, PlayerRules.Creative)], RulesLimits.Default with { AllowGlideBoost = false });
        Assert.Null(off.GlideBoost);
        Assert.Equal(0u, off.Native.Flags & RulesNative.GlideBoost);
    }

    [Fact]
    public void AnEarlierUneditedCreativeFileIsUpgraded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiln-rules-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var old = Path.Combine(dir, "creative.json");
            File.WriteAllText(old, "// Kiln's \"creative\" rules\n" +
                (PlayerRules.Creative with { FreezeShroudTimer = null, UnlimitedGliding = null, GlideSink = null, GlideBoost = null }).ToJson());
            Assert.True(RulesPresets.IsEarlierDefault("creative", old));
            File.WriteAllText(old, (PlayerRules.Creative with { FreezeShroudTimer = null, UnlimitedGliding = null, GlideSink = null,
                GlideBoost = null, DigDamage = 10 }).ToJson());
            Assert.False(RulesPresets.IsEarlierDefault("creative", old));  // edited: left alone
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TheFirstPresetReadAfterAnUpdateIsThisVersions()
    {
        // The first /creative after an update read the preset before Kiln.Rules started (and updated the file), so it
        // applied the earlier Kiln's rules (no Shroud timer, no gliding: native flags 0x1f instead of 0x1df).
        var dir = Path.Combine(Path.GetTempPath(), "kiln-rules-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var outer = RulesPresets.FolderOverride;
        RulesPresets.FolderOverride = dir;
        try
        {
            var file = Path.Combine(dir, "creative.json");
            var earlier = PlayerRules.Creative with { FreezeShroudTimer = null, UnlimitedGliding = null, GlideSink = null, GlideBoost = null };
            File.WriteAllText(file, "// Kiln's \"creative\" rules\n" + earlier.ToJson() + "\n");
            var got = RulesPresets.Get("creative");
            Assert.Equal(PlayerRules.Creative, got);
            Assert.Equal(0x1dfu, Combine(got!).Native.Flags);
            // the file is this version's now, and stays the owner's once edited
            Assert.Equal(PlayerRules.Creative, PlayerRules.FromJson(File.ReadAllText(file)));
            File.WriteAllText(file, (earlier with { DigDamage = 10 }).ToJson());
            Assert.Equal(earlier with { DigDamage = 10 }, RulesPresets.Get("creative"));
            Assert.Equal(0x1fu, Combine(RulesPresets.Get("creative")!).Native.Flags);
        }
        finally
        {
            RulesPresets.FolderOverride = outer;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void KilnMovesCharactersOnlyInsideTheWorld()
    {
        Assert.True(RulesRuntime.InWorld(new WorldPosition(3790.5, 846.2, 1469.4)));
        Assert.True(RulesRuntime.InWorld(new WorldPosition(0, -100, 10240)));  // the edges and the voxels below 0
        // a frozen client's camera: Y 2^31 m below the world (a fixed-point overflow) made the game's rays endless
        Assert.False(RulesRuntime.InWorld(new WorldPosition(3790.5, -2147482752.0, 1467.7)));
        Assert.False(RulesRuntime.InWorld(new WorldPosition(double.NaN, 100, 100)));
        Assert.False(RulesRuntime.InWorld(new WorldPosition(100, double.PositiveInfinity, 100)));
        Assert.False(RulesRuntime.InWorld(new WorldPosition(20000, 100, 100)));
        Assert.False(RulesRuntime.InWorld(new WorldPosition(100, 5000, 100)));
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
