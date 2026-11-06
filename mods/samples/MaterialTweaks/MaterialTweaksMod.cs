// SPDX-License-Identifier: MIT
using Kiln;

namespace MaterialTweaks;

/// <summary>
/// What it takes to dig three of the game's materials. The names are the catalog's (GameMaterials has them as constants once `kiln catalog build` has run).
/// The game reads its material tables once at start, so a tweak defined here is there from the next start; blocks/twins.json's are there at once.
/// </summary>
public sealed class MaterialTweaksMod : Mod
{
    public override void OnLoad()
    {
        // a strength is a part: write it once, use it on several materials
        var vault = Blocks.Define("vault", new StrengthBlock { Hardness = Hardness.Unbreakable });

        // a stone pickaxe does 50 to slightly hard and 35 to moderately hard: rough wood walls take longer, and the weakest enemies' strikes do nothing
        Blocks.Define("tough-logs", new MaterialTweakBlock { Material = "WoodRough", Strength = new StrengthBlock { Hardness = Hardness.ModeratelyHard } });

        // nothing digs it, nothing dismantles it
        var stone = Blocks.Define("vault-stone", new MaterialTweakBlock { Material = "StoneFancy", Strength = vault });

        // 2000 health at very hard: an iron pickaxe needs 40 hits per block; at 400 it needs 8
        Blocks.Define("soft-iron", new MaterialTweakBlock { Material = "MetalIron", Strength = new StrengthBlock { Health = 400 } });

        Log.Info($"material tweaks: vault-stone is {stone.Status}, on cell {stone.CellId}");
    }
}
