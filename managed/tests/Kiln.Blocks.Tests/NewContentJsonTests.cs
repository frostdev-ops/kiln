// SPDX-License-Identifier: MIT
// The JSON kiln.content's define() gets for a new item and a new recipe (NewContent.ToJson, which Register sends unchanged): pinned, so the
// boot generation's IR (chapter 08), which carries the same JSON, cannot drift from what a runtime definition registers.
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.Blocks.Tests;

public sealed class NewContentJsonTests
{
    [Fact]
    public void An_items_define_json_is_pinned()
    {
        var def = new ItemDefinition
        {
            Guid = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), Name = "boot-gallery.wood-token", BaseOn = "Material_T1_Wood_Logs",
            DisplayName = "Boot Token", Description = "a token", Lore = "old", Source = "boot", RolledLoot = true,
            Refs = new Dictionary<string, string> { ["model"] = "item:Material_T1_Wood_Logs" },
        };
        var expected = """
            {"guid":"0f8fad5b-d9cb-469f-a165-70867728950e","name":"boot-gallery.wood-token","base":"Material_T1_Wood_Logs","displayName":"Boot Token","description":"a token","lore":"old","source":"boot","rolledLoot":true,"refs":{"model":"item:Material_T1_Wood_Logs"}}
            """;
        Assert.Equal(expected.Trim(), NewContent.ToJson(def).ToJsonString());
    }

    [Fact]
    public void A_recipes_define_json_is_pinned()
    {
        var def = new RecipeDefinition
        {
            Guid = Guid.Parse("1f8fad5b-d9cb-469f-a165-70867728950e"), Name = "boot-gallery.token-recipe", Station = "workshop:alchemist",
            Inputs = [new RecipeIngredient("Material_T1_Wood_Logs", 2)], Outputs = [new RecipeIngredient("boot-gallery.wood-token", 1)],
            Duration = TimeSpan.FromSeconds(3),
        };
        var expected = """
            {"guid":"1f8fad5b-d9cb-469f-a165-70867728950e","name":"boot-gallery.token-recipe","station":"workshop:alchemist","inputs":[{"count":2,"item":"Material_T1_Wood_Logs"}],"outputs":[{"count":1,"item":"boot-gallery.wood-token"}],"duration":3,"unlock":"always"}
            """;
        Assert.Equal(expected.Trim(), NewContent.ToJson(def).ToJsonString());
    }
}
