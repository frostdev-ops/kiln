// SPDX-License-Identifier: MIT
// dev.32 api-shape: members the C# side lacked to do what a data pack can.
using System.Linq;
using System.Text.Json.Nodes;
using Kiln;
using Kiln.Registry;
using DataRegistry = Kiln.Registry.Registry;
using Xunit;

namespace Kiln.RegistryTests;

public sealed class ApiShapeNewApiTests
{
    private static ModContext Mod() => HostAccess.CreateContext(new ModInfo("test", "Test", "1.0.0", "."), 77, ".");
    private static RegistryContext Frame() =>
        new(Mod(), RegistryEntryPoint.GameStart, RegistryPhase.Alter, null, "test-code", (registry, _, field) => (registry, field) switch
        {
            ("drops", "entries") => """[{"chance":0.25,"count.max":2,"count.min":1,"item":"game:wood","weight":1}]""",
            ("items", "perks") => """["game:perk_a","game:perk_b"]""",
            ("loot", "fixedRarity") => "\"none\"",
            _ => "null",
        }, (_, _) => new[] { "game:dt" });

    private static JsonArray Edits(RegistryContext context) => JsonNode.Parse(context.CaptureIr()!)!["edits"]!.AsArray();

    // finding 2: C# appends a drop entry as a data pack does: its item (read-only once it is made) given on the new element,
    // every field in the one append, its count nested
    [Fact]
    public void AppendsADropEntryWithItsItem()
    {
        var context = Frame();
        using var scope = context.Enter();
        var table = DataRegistry.For(context).Drops["game:dt"];
        var entry = table.Entries.AppendNew();
        entry.Item = new RegistryId("game:material_stone");
        entry.Count.Min = 2;
        entry.Count.Max = 5;
        entry.Chance = 0.5;
        Assert.Equal(new RegistryId("game:material_stone"), table.Entries[1].Item);
        // an element that is not new keeps its item: it is the entry's identity
        Assert.Throws<ArgumentException>(() => table.Entries[0].Item = new RegistryId("game:material_stone"));
        var edit = Edits(context).Single()!;
        var appended = edit["list"]!["append"]!.AsArray().Single()!.AsObject();
        Assert.Equal("game:material_stone", appended["item"]!.GetValue<string>());
        Assert.Equal(2, appended["count"]!["min"]!.GetValue<long>());
        Assert.Equal(5, appended["count"]!["max"]!.GetValue<long>());
        Assert.Equal(0.5, appended["chance"]!.GetValue<double>());
        context.Close();
    }

    // finding 4: an element of a list of plain values is set by index, as a data pack's "perks[0]" does
    [Fact]
    public void SetsAValueListElementByIndex()
    {
        var context = Frame();
        using var scope = context.Enter();
        var item = DataRegistry.For(context).Items["game:dt"];
        item.Perks[1] = new RegistryId("game:perk_c");
        Assert.Equal(new RegistryId("game:perk_c"), item.Perks[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => item.Perks[2] = new RegistryId("game:perk_c"));
        var edit = Edits(context).Single()!;
        Assert.Equal("perks[1]", edit["field"]!.GetValue<string>());
        Assert.Equal("game:perk_c", edit["op"]!.GetValue<string>());
        context.Close();
    }

    // finding 6: a loot entry's fixed rarity (loot.fixedRarity) is ItemLootEntry.Rarity, a RegistryRarity, null when the drop
    // rolls one (the data's "none")
    [Fact]
    public void AFixedRarityIsARarityOrNull()
    {
        var context = Frame();
        using var scope = context.Enter();
        var loot = DataRegistry.For(context).Loot["game:dt"];
        Assert.Null(loot.Rarity);
        loot.Rarity = RegistryRarity.Legendary;
        Assert.Equal(RegistryRarity.Legendary, loot.Rarity);
        loot.Rarity = null;
        Assert.Null(loot.Rarity);
        var edits = Edits(context);
        Assert.Equal(new[] { "legendary", "none" }, edits.Select(e => e!["op"]!.GetValue<string>()).ToArray());
        context.Close();
    }

    // review of api-shape, the owner's call (11-27): dev.31's FixedRarity (an ItemLootRarity, None for none) and the new Rarity
    // read and write the one field, so a dev.31 mod's edits and reads are the same edits and values as before
#pragma warning disable CS0618 // the dev.31 shape, on purpose
    [Fact]
    public void TheDev31FixedRarityAndRarityAreOneField()
    {
        var context = Frame();
        using var scope = context.Enter();
        var loot = DataRegistry.For(context).Loot["game:dt"];
        Assert.Equal(ItemLootRarity.None, loot.FixedRarity);
        loot.FixedRarity = ItemLootRarity.Epic;
        Assert.Equal(RegistryRarity.Epic, loot.Rarity);
        loot.Rarity = RegistryRarity.Common;
        Assert.Equal(ItemLootRarity.Common, loot.FixedRarity);
        loot.Rarity = null;
        Assert.Equal(ItemLootRarity.None, loot.FixedRarity);
        loot.FixedRarity = ItemLootRarity.Legendary;
        Assert.Equal(RegistryRarity.Legendary, loot.Rarity);
        loot.FixedRarity = ItemLootRarity.None;
        Assert.Null(loot.Rarity);
        var edits = Edits(context);
        Assert.All(edits, e => Assert.Equal("fixedRarity", e!["field"]!.GetValue<string>()));
        Assert.Equal(new[] { "epic", "common", "none", "legendary", "none" }, edits.Select(e => e!["op"]!.GetValue<string>()).ToArray());
        context.Close();
    }
#pragma warning restore CS0618
}

public sealed class ApiShapeEntryTests
{
    private static ModContext Mod() => HostAccess.CreateContext(new ModInfo("test", "Test", "1.0.0", "."), 77, ".");
    private static RegistryContext Frame() =>
        new(Mod(), RegistryEntryPoint.GameStart, RegistryPhase.Alter, null, "test-code",
            (_, _, field) => field == "level" ? "7" : "null", (_, _) => new[] { "game:wolf" });

    // docs lane A's A1: the entry class is Registries (Registry is also its namespace's name); Registry keeps dev.31's members
    [Fact]
    public void RegistriesIsTheEntryAndRegistryForwardsToIt()
    {
        var context = Frame();
        using var scope = context.Enter();
        Assert.Same(Registries.For(context), DataRegistry.For(context));
        Assert.Equal(new[] { "Available", "Change", "For", "Read", "Ready" },
            typeof(DataRegistry).GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(m => m is not System.Reflection.MethodInfo { IsSpecialName: true }).Select(m => m.Name).Distinct().Order().ToArray());
        Assert.Null(typeof(DataRegistry).GetMethod("Reauthor"));                              // only WorldLoad: Registries.Reauthor()
        Assert.Empty(typeof(Registries).GetMethod("Reauthor")!.GetParameters());
        Assert.True(typeof(IKilnResult).IsAssignableFrom(typeof(Registries).GetMethod("Reauthor")!.ReturnType));
        context.Close();
    }

    // docs lane A's A14: a value the entry does not have is never a silent 0: Get<int> throws, a nullable reads null, TryGet asks
    [Fact]
    public void AMissingValueIsNeverASilentDefault()
    {
        var context = Frame();
        using var scope = context.Enter();
        var wolf = Registries.For(context).Entries("creatures")["game:wolf"];
        Assert.Equal(7, wolf.Get<int>("level"));
        Assert.Throws<InvalidOperationException>(() => wolf.Get<double>("scale"));
        Assert.Null(wolf.Get<double?>("scale"));
        Assert.False(wolf.TryGet<double>("scale", out _));
        Assert.True(wolf.TryGet<int>("level", out var level));
        Assert.Equal(7, level);
        context.Close();
    }
}
