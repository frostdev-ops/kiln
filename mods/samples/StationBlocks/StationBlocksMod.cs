// SPDX-License-Identifier: MIT
using System.Numerics;
using Kiln;
using Kiln.GameIds;

namespace StationBlocks;

/// <summary>
/// Stations and props, said once. A drying rack and a forge of your own run their own recipes on their own, from their own inventory; a bench crafts at once; a prop just stands there.
/// All on the game's art: a model of your own goes in <c>Visual.Model</c>, cooked by tools/modelcook. blocks/twins.json says the same in data.
/// </summary>
public sealed class StationBlocksMod : Mod
{
    public override void OnLoad()
    {
        // a part: write one, use it everywhere
        var hearth = Blocks.Define("warm-hearth", new ComfortBlock { Amount = 2, Category = ComfortCategory.Hearth, Range = 5, Warmth = 1 });

        // Like the game's drying rack, with a workshop of its own: it runs this and nothing else. A cycle takes 4 s, the inputs go when it starts, the outputs come when it ends.
        Blocks.Define("herb-rack", new StationBlock
        {
            Name = "Herb Rack", Description = "Hangs what you give it until it's done.",
            Placement = new PlacementBlock { On = Surface.Floor, InBase = true },
            Processing = new ProcessingBlock { Slots = 12 },
            Recipe = new RecipeBlock { Station = StationKind.Workbench.Workshop, Needs = { [GameItems.Material_T1_Wood_Logs] = 6 } },
            Recipes =
            [
                new RecipeBlock { Seconds = 4, Needs = { [GameItems.Material_T1_Twigs] = 2 }, Output = GameItems.Material_T1_Wood_Logs, Makes = 3 },
                new RecipeBlock { Seconds = 300, Needs = { [GameItems.Food_T1_raw_fruit_Red_Berries] = 1 }, Output = GameItems.Food_T1_processed_fruit_Dried_RedBerry },
            ],
        });

        // Fuel is an input, as in the game's forge: a cycle takes it when it starts, and with none left the forge stops.
        var forge = Blocks.Define("cold-forge", new StationBlock
        {
            Name = "Cold Forge", Description = "Burns charcoal without a flicker of warmth for the metal.",
            Kind = StationKind.Forge,
            Processing = new ProcessingBlock { Slots = 16, GoesOutInRain = false },
            Comfort = hearth,
            Recipe = new RecipeBlock { Station = StationKind.Workbench.Workshop, Needs = { [GameItems.Build_TerrainMaterial_T1_Stone] = 20 } },
            Recipes =
            [
                new RecipeBlock { Seconds = 3, Burns = { [GameItems.Material_T1_Charcoal] = 2 }, Needs = { [GameItems.Material_T1_Metal_Scraps] = 1 }, Output = GameItems.Material_T1_Metal_Sheets },
            ],
        });

        // Your own item says where it's made, as every item block does. Station takes the block.
        Blocks.Define("frost-ingot", new ItemBlock
        {
            Name = "Frost Ingot", Stack = 50,
            Visual = new VisualBlock { Like = GameItems.Material_T5_Silver_Bar },
            Recipe = new RecipeBlock { Station = forge, Seconds = 2, Burns = { [GameItems.Material_T1_Charcoal] = 1 }, Needs = { [GameItems.Material_T1_Metal_Sheets] = 1 } },
        });

        // The same block with no time and no processing: a player crafts at it at once. It shares the game's workbench: its recipes are listed there too.
        Blocks.Define("fine-bench", new StationBlock
        {
            Name = "Fine Bench",
            Kind = StationKind.Workbench,
            Recipes = [new RecipeBlock { Needs = { [GameItems.Material_T1_Twigs] = 1 }, Output = GameItems.Material_T1_Resin }],
        });

        // Props. A game item's look brings its model, its collision and how it places; the rest is yours.
        Blocks.Define("old-table", new PropBlock
        {
            Name = "Old Table",
            Visual = new VisualBlock { Like = GameItems.Prop_Decoration_Comfort1_T0_Table_Medium },
            Comfort = new ComfortBlock { Amount = 3, Category = ComfortCategory.Table },
            Recipe = new RecipeBlock { Needs = { [GameItems.Material_T1_Wood_Logs] = 4 } },
        });
        Blocks.Define("lucky-banner", new PropBlock
        {
            Name = "Lucky Banner",
            Visual = new VisualBlock { Like = GameItems.Prop_Decoration_T4_Banner_Wall },
            Placement = new PlacementBlock { Fog = true, Size = new Vector3(1, 3, 0.25f) },
            Comfort = hearth,
        });
    }
}
