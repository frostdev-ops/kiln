// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Rules.Tests;

public class CreativeTests
{
    private static readonly EntityId P = new(3);

    private static EffectiveRules Combine(RulesLimits limits, params PlayerRules[] sets) =>
        RulesMath.Combine(P, sets.Select(r => ((RuleSet?)null, r)).ToList(), limits);

    [Fact]
    public void NoFallDamageCombinesLikeASwitch()
    {
        Assert.False(Combine(RulesLimits.Default).NoFallDamage);
        var on = Combine(RulesLimits.Default, new PlayerRules { NoFallDamage = true });
        Assert.True(on.NoFallDamage);
        Assert.False(on.IsDefault);
        Assert.Contains("no fall damage", on.ToString());
        // a denial wins
        Assert.False(Combine(RulesLimits.Default, new PlayerRules { NoFallDamage = true }, new PlayerRules { NoFallDamage = false }).NoFallDamage);
        // the server may deny it
        var denied = Combine(RulesLimits.Default with { AllowNoFallDamage = false }, new PlayerRules { NoFallDamage = true });
        Assert.False(denied.NoFallDamage);
        Assert.Contains("noFallDamage", denied.Denied);
    }

    [Fact]
    public void NoFallDamageReadsAndWritesJson()
    {
        var r = PlayerRules.FromJson("""{ "noFallDamage": true }""");
        Assert.True(r.NoFallDamage);
        Assert.Contains("\"noFallDamage\": true", r.ToJson());
        Assert.True(new PlayerRules().With(r).NoFallDamage);
    }

    [Fact]
    public void TheCreativePresetIsUnchanged()
    {
        // creative's invulnerability already cancels falls; the preset file stays the released one
        Assert.Null(PlayerRules.Creative.NoFallDamage);
    }

    [Theory]
    [InlineData("Wood_Log", true)]
    [InlineData("Weapon_Sword_Debug", false)]
    [InlineData("Item_Test_Dummy", false)]
    [InlineData("Consumable_Deprecated_Apple", false)]
    [InlineData("Armor_Head_WIP", false)]
    [InlineData("", false)]
    public void CatalogHidesDebugAndRetiredEntries(string name, bool listed) => Assert.Equal(listed, CreativeCatalog.Listed(name, 0));

    [Fact]
    public void WithoutEquipmentLeavesToolsAndConsumables()
    {
        Assert.True(CreativeCatalog.IsEquipment(ItemCategory.Weapons, true));
        Assert.True(CreativeCatalog.IsEquipment(ItemCategory.Equipment, true));
        Assert.True(CreativeCatalog.IsEquipment(ItemCategory.WeaponGem, false));
        Assert.False(CreativeCatalog.IsEquipment(ItemCategory.Tools, true));
        Assert.False(CreativeCatalog.IsEquipment(ItemCategory.Consumables, false));
        Assert.False(CreativeCatalog.IsEquipment(ItemCategory.Ammunition, false));
    }

    [Fact]
    public void SearchMatchesEveryWordInLabelOrName()
    {
        Assert.True(CreativeCatalog.Matches("", "Wood Logs", "Wood_Log"));
        Assert.True(CreativeCatalog.Matches("logs wood", "Wood Logs", "Wood_Log"));
        Assert.True(CreativeCatalog.Matches("wood_log", "Wood Logs", "Wood_Log"));
        Assert.False(CreativeCatalog.Matches("stone", "Wood Logs", "Wood_Log"));
    }

    [Fact]
    public void CatalogPagesRoundTrip()
    {
        var page = new CreativeCatalogMsg(150, 4000,
        [
            new CreativeItem(0x43108313, "Weapon_Sword_T1", "Bronze Sword", ItemCategory.Weapons, ItemRarity.Uncommon, true, 1, 55, 1),
            new CreativeItem(7, "Wood_Log", "Wood_Log", ItemCategory.Materials, ItemRarity.Common, false, 0, 0, 100000),
        ]);
        var w = new NetWriter();
        CreativeNet.EncodeCatalog(page, w);
        var r = new NetReader(w.ToArray());
        var back = CreativeNet.DecodeCatalog(ref r);
        Assert.Equal(150, back.Start);
        Assert.Equal(4000, back.Total);
        Assert.Equal(page.Items[0], back.Items[0]);
        // a label equal to the name is not sent twice; the stack size is held to 16 bits
        Assert.Equal("Wood_Log", back.Items[1].Label);
        Assert.Equal(ushort.MaxValue, back.Items[1].MaxStack);
    }

    [Fact]
    public void FreeCraftingAndKeepConsumablesCombineLikeSwitches()
    {
        var on = Combine(RulesLimits.Default, new PlayerRules { FreeCrafting = true, KeepConsumables = true });
        Assert.True(on.FreeCrafting);
        Assert.True(on.KeepConsumables);
        Assert.Contains("free crafting", on.ToString());
        Assert.Contains("consumables kept", on.ToString());
        Assert.False(Combine(RulesLimits.Default, new PlayerRules { FreeCrafting = true }, new PlayerRules { FreeCrafting = false }).FreeCrafting);
        var denied = Combine(RulesLimits.Default with { AllowFreeCrafting = false, AllowKeepConsumables = false },
            new PlayerRules { FreeCrafting = true, KeepConsumables = true });
        Assert.False(denied.FreeCrafting || denied.KeepConsumables);
        Assert.Contains("freeCrafting", denied.Denied);
        Assert.Contains("keepConsumables", denied.Denied);
    }

    [Fact]
    public void CreativeAddsItsExtrasWherethePresetSaysNothing()
    {
        // what /creative applies: the extras, then the preset on top (an explicit false in the file wins)
        var applied = PlayerRules.CreativeExtras.With(PlayerRules.Creative);
        Assert.True(applied.FreeCrafting);
        Assert.True(applied.KeepConsumables);
        Assert.Equal(PlayerRules.Creative.BuildCost, applied.BuildCost);
        var edited = PlayerRules.CreativeExtras.With(PlayerRules.FromJson("""{ "buildCost": 0, "freeCrafting": false }"""));
        Assert.False(edited.FreeCrafting);
        Assert.True(edited.KeepConsumables);
        // the preset itself (and so the file Kiln writes) is unchanged
        Assert.Null(PlayerRules.Creative.FreeCrafting);
        Assert.DoesNotContain("freeCrafting", PlayerRules.Creative.ToJson());
    }

    [Fact]
    public void FoundItemsRoundTrip()
    {
        var w = new NetWriter();
        CreativeNet.EncodeFound(new CreativeFoundMsg(true, [1u, 0x43108313u]), w);
        var r = new NetReader(w.ToArray());
        var back = CreativeNet.DecodeFound(ref r);
        Assert.True(back.Available);
        Assert.Equal(new uint[] { 1, 0x43108313 }, back.Items);
    }

    [Fact]
    public void FastCycleTakesAboutNinetySeconds()
    {
        // the game's default day 30 min, night 12 min: one cycle in 90 s
        Assert.Equal(28, Time.FastSpeed(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(12)), 3);
        Assert.Equal(2, Time.FastSpeed(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)));      // never slower than x2
        Assert.Equal(28, Time.FastSpeed(TimeSpan.Zero, TimeSpan.Zero));                            // unknown lengths
    }
}
