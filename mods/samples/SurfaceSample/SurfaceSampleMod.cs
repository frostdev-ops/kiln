// SPDX-License-Identifier: MIT
using Kiln;

namespace SurfaceSample;

/// <summary>
/// Building materials with a look of their own. The game reads its material tables once at start, so a material defined here is there from the next start;
/// blocks/twins.json's are there at once. The textures are made by make_textures.py. Names of game items and materials are the catalog's (GameItems and GameMaterials
/// have them as constants once `kiln catalog build` has run).
/// </summary>
public sealed class SurfaceSampleMod : Mod
{
    public override void OnLoad()
    {
        // A look defined once: its own maps on the sides, a mossy top, the game's moonstone on the worn edges. Any number of materials can name it.
        var mossLook = Blocks.Define("moss", new SurfaceBlock
        {
            Side = new SurfaceLayer
            {
                Albedo = "textures/moss-brick.png",
                Normal = "textures/moss-brick_n.png",
                Roughness = "textures/moss-brick_r.png",
                Height = "textures/moss-brick_h.png",
            },
            // Bottom, Edge and Noise repeat Side when left out; EdgeTop repeats Edge
            Top = new SurfaceLayer { Albedo = "textures/moss-top.png", Tiling = 2 },
            Edge = SurfaceLayer.Of("StoneAncientMoonstone"),
            Bevel = 0.2f,
        });

        // A new wall material in that look, harder than rough stone. Its block item's icon is drawn from the surface.
        var moss = Blocks.Define("moss-brick", new BuildingMaterialBlock
        {
            Name = "Moss Brick",
            Description = "Soft to the eye, hard on the pick.",
            Surface = mossLook,
            // an iron pickaxe: 1200 / 60 = 20 hits a block; rough stone is 250 / 95
            Strength = new StrengthBlock { Hardness = Hardness.Hard, Health = 1200, Breaks = Breaks.Stone },
            Recipe = new RecipeBlock { Makes = 20, Needs = { ["Build_TerrainMaterial_T1_Stone"] = 10, ["Material_T1_Plant_Fibre"] = 2 } },
        });

        // The game's rough stone, with runes that glow: a game material's layer with a map of your own on top.
        Blocks.Define("rune-stone", new BuildingMaterialBlock
        {
            Name = "Rune Stone",
            Surface = new SurfaceBlock
            {
                Side = SurfaceLayer.Of("StoneRough") with { Emissive = new SurfaceEmissive { Map = "textures/runes.png", Strength = 3 } },
                Decoration = "none",
            },
            Recipe = new RecipeBlock { Makes = 10, Needs = { ["Build_TerrainMaterial_T1_Stone"] = 10 } },
        });

        // A retexture: rough wood walls get snow on their sides, everything else stays the game's. It asks nothing of other players.
        var snowy = Blocks.Define("snowy-logs", new MaterialTweakBlock
        {
            Material = "WoodRough",
            Surface = new SurfaceBlock { Side = new SurfaceLayer { Albedo = "textures/snowy-logs.png" } },
        });

        Log.Info($"surface sample: moss-brick is {moss.Status} on cell {moss.CellId}, snowy-logs is {snowy.Status}");
    }
}
