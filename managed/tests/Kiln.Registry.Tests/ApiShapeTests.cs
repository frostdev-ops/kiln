// SPDX-License-Identifier: MIT
// dev.32 api-shape (before mod API 0.2.27 freezes): the C# side does what a data pack can, in the data pack's own shapes.
using System.Linq;
using System.Text.Json.Nodes;
using Kiln;
using Kiln.Registry;
using DataRegistry = Kiln.Registry.Registry;
using Xunit;

namespace Kiln.RegistryTests;

public sealed class ApiShapeTests
{
    private static ModContext Mod() => HostAccess.CreateContext(new ModInfo("test", "Test", "1.0.0", "."), 77, ".");
    // a drop table with one entry (kiln.registry.live's reads give an element's struct leaves flat: "count.min"), a recipe
    // with one input, an item with two perks
    private static RegistryContext Frame(RegistryPhase phase = RegistryPhase.Alter) =>
        new(Mod(), RegistryEntryPoint.GameStart, phase, null, "test-code", (registry, _, field) => (registry, field) switch
        {
            ("drops", "entries") => """[{"chance":0.25,"count.max":2,"count.min":1,"item":"game:wood","weight":1}]""",
            ("recipes", "inputs") => """[{"category":null,"count":1,"item":"game:wood"}]""",
            ("items", "perks") => """["game:perk_a","game:perk_b"]""",
            ("loot", "fixedRarity") => "\"none\"",
            _ => "null",
        }, (_, _) => new[] { "game:dt" });

    private static JsonArray Edits(RegistryContext context) => JsonNode.Parse(context.CaptureIr()!)!["edits"]!.AsArray();

    // finding 3: an element's struct (a drop entry's count) is a nested object in what C# sends, as the C# types and the docs
    // show it, never the flat dotted leaves the live reads give
    [Fact]
    public void ElementsGoOutNestedAsTheTypesShowThem()
    {
        var context = Frame();
        using var scope = context.Enter();
        var table = DataRegistry.For(context).Drops["game:dt"];
        Assert.Equal(1, table.Entries[0].Count.Min);           // the flat read is read
        Assert.Equal(1, table.Entries.RemoveWhere(e => e.Item == new RegistryId("game:wood")));
        var removed = Edits(context).Single()!["list"]!["remove"]![0]!.AsObject();
        Assert.Equal(1, removed["count"]!["min"]!.GetValue<long>());
        Assert.Equal(2, removed["count"]!["max"]!.GetValue<long>());
        Assert.False(removed.ContainsKey("count.min"));
        Assert.Equal("game:wood", removed["item"]!.GetValue<string>());
        context.Close();
    }

    // finding 2 (and the index trap under it): an appended element's fields go in the append itself, by value, never as index
    // edits after it (another mod's append in the same pass would shift the index onto its element)
    [Fact]
    public void AnAppendedElementsFieldsGoInTheAppend()
    {
        var context = Frame();
        using var scope = context.Enter();
        var recipe = DataRegistry.For(context).Recipes["game:dt"];
        var input = recipe.Inputs.AppendNew();
        input.Item = new RegistryId("game:stone");
        input.Count = 4;
        Assert.Equal(2, recipe.Inputs.Count);
        Assert.Equal(4, recipe.Inputs[1].Count);               // the stage's view has it
        var edit = Edits(context).Single()!;
        Assert.Equal("inputs", edit["field"]!.GetValue<string>());
        var appended = edit["list"]!["append"]!.AsArray().Single()!;
        Assert.Equal("game:stone", appended["item"]!.GetValue<string>());
        Assert.Equal(4, appended["count"]!.GetValue<long>());
        context.Close();
    }

    // A later list operation on the list ends that: the element was named by value in it, so a field set after it is an
    // element edit by index, as a data pack's would be.
    [Fact]
    public void ARemovalAfterAnAppendSealsTheAppendedElement()
    {
        var context = Frame();
        using var scope = context.Enter();
        var recipe = DataRegistry.For(context).Recipes["game:dt"];
        var input = recipe.Inputs.AppendNew();
        input.Count = 4;
        recipe.Inputs.RemoveWhere(x => x.Item == new RegistryId("game:wood"));
        input = recipe.Inputs[0];
        input.Count = 5;
        var edits = Edits(context);
        Assert.Equal(3, edits.Count);
        Assert.Equal(4, edits[0]!["list"]!["append"]![0]!["count"]!.GetValue<long>());
        Assert.Equal("inputs[0].count", edits[2]!["field"]!.GetValue<string>());
        context.Close();
    }

    // finding 6: one name for a rarity. RegistryRarity is one rarity (an item's, a loot entry's fixed one: null is none);
    // RegistryRarities is a set of them (the rarities a drop may roll), its bits in RegistryRarity's order.
    [Fact]
    public void OneNameForARarity()
    {
        var enums = typeof(RegistrySchema).Assembly.GetExportedTypes().Where(t => t.IsEnum && t.Name.Contains("Rarit")).Select(t => t.Name).Order().ToArray();
        Assert.Equal(new[] { "RegistryRarities", "RegistryRarity" }, enums);
        Assert.Equal(typeof(RegistryRarity?), typeof(ItemLootEntry).GetProperty("FixedRarity")!.PropertyType);
        Assert.Equal(typeof(RegistryRarity), typeof(ItemEntry).GetProperty("Rarity")!.PropertyType);
        foreach (var r in Enum.GetValues<RegistryRarity>())
            Assert.Equal(r.ToString(), ((RegistryRarities)(1u << (int)r)).ToString());
    }
}
