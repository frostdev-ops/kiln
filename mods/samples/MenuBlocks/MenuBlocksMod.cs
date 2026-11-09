// SPDX-License-Identifier: MIT
using Kiln;

namespace MenuBlocks;

/// <summary>
/// What a station's crafting menu is. By default it is the menu of the station's Kind; Menu = new MenuBlock { Like = ... } opens another station's,
/// written in place or as a part that several stations name. blocks/twins.json says the same in data.
/// </summary>
public sealed class MenuBlocksMod : Mod
{
    public override void OnLoad()
    {
        // a forge (it works on its own, and burns charcoal) whose menu is the drying rack's
        Blocks.Define("rack-forge", new StationBlock
        {
            Name = "Rack Forge", Description = "A forge that thinks it is a drying rack.",
            Kind = StationKind.Forge,
            Menu = new MenuBlock { Like = StationKind.DryingRack },
            Processing = new ProcessingBlock { Slots = 8 },
            Recipe = new RecipeBlock { Needs = { ["Material_T1_Wood_Logs"] = 6 } },
            Recipes = [new RecipeBlock { Seconds = 3, Needs = { ["Material_T1_Twigs"] = 2 }, Output = "Material_T1_Resin" }],
        });

        // a menu part: write it once, name it on several stations
        var kitchen = Blocks.Define("kitchen-menu", new MenuBlock { Like = StationKind.Kitchen });
        Blocks.Define("camp-stove", new StationBlock
        {
            Name = "Camp Stove", Kind = StationKind.Workbench, Menu = kitchen,
            Recipe = new RecipeBlock { Needs = { ["Material_T1_Wood_Logs"] = 3 } },
            Recipes = [new RecipeBlock { Needs = { ["Material_T1_Twigs"] = 1 }, Output = "Material_T1_Resin" }],
        });
    }
}
