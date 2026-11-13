// SPDX-License-Identifier: MIT
// The model an item block lies on the ground as (VisualBlock.Dropped, scope I-2 of dev.27): Dropped names it; without
// it, a model of the mod's own (a GUID) lies there as itself, and an "item:" model or Like as that item does (the
// bundle copies the other item's iconModel). The item's refs are what kiln.content writes.
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

public sealed class DroppedModelTests
{
    private const string Mod = "dropped-test";
    private const string Own = "6b696c6e-0000-4000-8000-00000000d0d0";

    private static JsonObject Lower(IBlock b) => Compiler.Lower(new ModBlocks(Mod, 1) { ManifestClients = "mod" }, "brand", b).Low.Json;

    private static JsonNode? Ref(JsonObject j, string key) => j["items"]![0]!["refs"]?[key];

    private static WeaponBlock Sword(VisualBlock look) => new() { Name = "Brand", Class = WeaponClass.Sword, Visual = look };

    [Fact]
    public void A_model_of_the_mods_own_lies_on_the_ground_as_itself()
    {
        var j = Lower(Sword(new VisualBlock { Model = Own }));
        Assert.Equal(Own, (string?)Ref(j, "iconModel"));
        Assert.Equal(Own, (string?)j["itemParts"]!["model"]);
    }

    [Fact]
    public void Dropped_says_otherwise()
    {
        var j = Lower(Sword(new VisualBlock { Model = Own, Dropped = "Weapon_T1_1H_Sword_Craft" }));
        Assert.Equal("item:Weapon_T1_1H_Sword_Craft", (string?)Ref(j, "iconModel"));
        Assert.Equal(Own, (string?)j["itemParts"]!["model"]);
    }

    [Fact]
    public void Dropped_alone_sets_only_the_ground_model()
    {
        var j = Lower(Sword(new VisualBlock { Dropped = "Weapon_T2_1H_Sword_01" }));
        Assert.Equal("item:Weapon_T2_1H_Sword_01", (string?)Ref(j, "iconModel"));
        Assert.Null(j["itemParts"]?["model"]);
    }

    [Fact]
    public void An_item_model_or_Like_brings_that_items_ground_model_with_its_look()
    {
        var byModel = Lower(Sword(new VisualBlock { Model = "item:Weapon_T2_1H_Sword_01" }));
        Assert.Null(Ref(byModel, "iconModel"));
        Assert.Equal("item:Weapon_T2_1H_Sword_01", (string?)byModel["itemParts"]!["model"]);
        var like = Lower(Sword(new VisualBlock { Like = "Weapon_T2_1H_Sword_01" }));
        Assert.Null(Ref(like, "iconModel"));
    }

    [Fact]
    public void Json_says_the_same()
    {
        var fromJson = Lower(BlockJson.Parse<WeaponBlock>($$"""{ "name": "Brand", "class": "sword", "visual": { "model": "{{Own}}" } }"""));
        Assert.Equal(Own, (string?)Ref(fromJson, "iconModel"));
        var dropped = Lower(BlockJson.Parse<WeaponBlock>($$"""{ "name": "Brand", "class": "sword", "visual": { "model": "{{Own}}", "dropped": "Weapon_T1_1H_Sword_Craft" } }"""));
        Assert.Equal("item:Weapon_T1_1H_Sword_Craft", (string?)Ref(dropped, "iconModel"));
    }
}
