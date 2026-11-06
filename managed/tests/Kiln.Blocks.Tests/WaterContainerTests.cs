// SPDX-License-Identifier: MIT
// Water containers as item blocks: a watering can or a water pouch that holds what the block says. The water is in the item's own entity, from a
// template every can of the game shares, so the block clones that template with its capacity and points the new item at the clone. Checked here
// on what the block lowers to, before anything reaches the game (the lab test, mods/tests/blocks-water-test, checks the game's side).
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

public sealed class WaterContainerTests
{
    private static JsonObject Lower(ItemBlock b, string name = "big-can") =>
        Compiler.Lower(new ModBlocks("water-test", 1), name, b).Low.Json;

    private static BlockError Refused(ItemBlock b) => Assert.Throws<BlockError>(() => Lower(b));

    [Fact]
    public void A_watering_can_that_holds_more_clones_the_cans_water_template()
    {
        var j = Lower(new ItemBlock { Name = "Big Can", Kind = ItemKind.WateringCan, Holds = 1000 });
        var t = Assert.Single(j["templates"]!.AsArray())!.AsObject();
        Assert.Equal("Pide_Fuel_Watering_Can", (string?)t["base"]);
        Assert.Equal("water-test.big-can.water", (string?)t["name"]);
        Assert.Equal(1000, (int)t["fields"]!["FuelSetup.maxFuelCharges"]!);
        var item = Assert.Single(j["items"]!.AsArray())!.AsObject();
        Assert.Equal("Tool_T1_WateringCan", (string?)item["base"]);
        Assert.Equal((string?)t["guid"], (string?)item["refs"]!["pidEntity"]);
    }

    [Fact]
    public void A_water_pouch_clones_the_pouchs_template()
    {
        var j = Lower(new ItemBlock { Name = "Big Pouch", Kind = ItemKind.WaterPouch, Holds = 60 }, "big-pouch");
        var t = Assert.Single(j["templates"]!.AsArray())!.AsObject();
        Assert.Equal("Pide_Fuel_Water_Pouch", (string?)t["base"]);
        Assert.Equal(60, (int)t["fields"]!["FuelSetup.maxFuelCharges"]!);
        Assert.Equal("Tool_Water_Pouch_Refillable", (string?)j["items"]![0]!["base"]);
    }

    [Fact]
    public void Without_holds_it_keeps_the_games_template()
    {
        var j = Lower(new ItemBlock { Name = "Plain Can", Kind = ItemKind.WateringCan });
        Assert.Null(j["templates"]);
        Assert.Null(j["items"]![0]!["refs"]!["pidEntity"]);
    }

    [Fact]
    public void The_clone_keeps_its_id_across_loads_and_follows_was()
    {
        var a = Lower(new ItemBlock { Name = "Big Can", Kind = ItemKind.WateringCan, Holds = 1000 });
        var b = Lower(new ItemBlock { Name = "Big Can", Kind = ItemKind.WateringCan, Holds = 500 });
        Assert.Equal((string?)a["templates"]![0]!["guid"], (string?)b["templates"]![0]!["guid"]);
        var renamed = Lower(new ItemBlock { Name = "Huge Can", Kind = ItemKind.WateringCan, Holds = 1000, Was = "big-can" }, "huge-can");
        Assert.Equal((string?)a["templates"]![0]!["guid"], (string?)renamed["templates"]![0]!["guid"]);
        Assert.Equal((string?)a["items"]![0]!["guid"], (string?)renamed["items"]![0]!["guid"]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1023)]
    public void The_whole_range_compiles(int holds) =>
        Assert.Equal(holds, (int)Lower(new ItemBlock { Name = "Can", Kind = ItemKind.WateringCan, Holds = holds })["templates"]![0]!["fields"]!["FuelSetup.maxFuelCharges"]!);

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1024)]
    [InlineData(1_000_000)]
    public void Outside_1_to_1023_is_refused_and_says_why(int holds)
    {
        var e = Refused(new ItemBlock { Name = "Can", Kind = ItemKind.WateringCan, Holds = holds });
        Assert.Equal("holds", e.Field);
        Assert.Contains("outside 1..1023", e.Problem);
        Assert.Contains("charges × 64 in 16 bits", e.Problem);
    }

    [Fact]
    public void Holds_is_only_for_water_containers()
    {
        Assert.Equal("a material has no holds: that is for a watering-can or water-pouch", Refused(new ItemBlock { Name = "Rock", Holds = 10 }).Problem);
        Assert.Equal("holds", Refused(new ItemBlock { Name = "Axe", Tool = new ToolBlock { Harvests = Harvest.Wood }, Holds = 10 }).Field);
    }

    [Fact]
    public void A_water_container_does_not_stack() =>
        Assert.Equal("a watering-can has an item entity of its own: it does not stack",
            Refused(new ItemBlock { Name = "Can", Kind = ItemKind.WateringCan, Holds = 200, Stack = 5 }).Problem);

    [Theory]
    [InlineData("watering-can", 4.0, 128)]
    [InlineData("watering-can", 0.25, 8)]
    [InlineData("watering-can", 8.0, 256)]
    [InlineData("water-pouch", 2.0, 32)]
    public void Flow_sets_what_a_step_moves(string kind, double flow, int volume)
    {
        var b = BlockJson.Parse<ItemBlock>($$"""{ "name": "Fast", "kind": "{{kind}}", "flow": {{flow.ToString(System.Globalization.CultureInfo.InvariantCulture)}} }""");
        var j = Lower(b);
        Assert.Equal(volume, (int)j["itemRules"]!["impactValues.0xd8f493f3"]!);
        Assert.Null(j["templates"]);   // the rate is the item's: no clone without holds
    }

    [Fact]
    public void Without_flow_the_games_rate_stays() =>
        Assert.Null(Lower(new ItemBlock { Name = "Can", Kind = ItemKind.WateringCan, Holds = 300 })["itemRules"]?["impactValues.0xd8f493f3"]);

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.2)]
    [InlineData(8.5)]
    [InlineData(double.NaN)]
    public void Flow_outside_its_range_is_refused(double flow)
    {
        var e = Refused(new ItemBlock { Name = "Can", Kind = ItemKind.WateringCan, Flow = flow });
        Assert.Equal("flow", e.Field);
        Assert.Contains("outside 0.25..8", e.Problem);
    }

    [Fact]
    public void Flow_is_only_for_water_containers() =>
        Assert.Equal("a material has no flow: that is for a watering-can or water-pouch", Refused(new ItemBlock { Name = "Rock", Flow = 2 }).Problem);

    [Fact]
    public void Json_names_the_kinds()
    {
        var b = BlockJson.Parse<ItemBlock>("""{ "name": "Big Can", "kind": "watering-can", "holds": 1000 }""");
        Assert.Same(ItemKind.WateringCan, b.Kind);
        Assert.Equal(1000, b.Holds);
        Assert.Same(ItemKind.WaterPouch, BlockJson.Parse<ItemBlock>("""{ "name": "P", "kind": "water-pouch" }""").Kind);
        var e = Assert.ThrowsAny<JsonException>(() => BlockJson.Parse<ItemBlock>("""{ "name": "B", "kind": "bucket" }"""));
        Assert.Contains("watering-can, water-pouch", e.Message);
    }

    [Fact]
    public void The_kinds_are_the_premades_only() =>
        Assert.DoesNotContain(typeof(ItemKind).GetConstructors(), c => c.IsPublic);
}
