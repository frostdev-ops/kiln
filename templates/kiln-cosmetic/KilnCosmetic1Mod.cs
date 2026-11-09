using Kiln;

namespace KilnCosmetic1;

/// <summary>Vanity from building blocks: a hat for the cosmetic head slot, a hair style and a beard, on the game's looks (no files to cook).
/// blocks/cosmetics.json makes the same kinds as data. A model of your own is a WornLook with Model = "models/hat.gltf" in a data file;
/// `kiln cook` makes what the game reads from it (docs/kiln/modding/cosmetics.md).</summary>
public sealed class KilnCosmetic1Mod : Mod
{
    public override void OnLoad()
    {
        Blocks.Define("captains-hat", new CosmeticBlock
        {
            Name = "Captain's Hat",
            Description = "Worn at a jaunty angle.",
            Slot = CosmeticSlot.Head,
            Look = new WornLook { Like = "Vanity_TX_Helmet_05_tricorn" },
            Recipe = new RecipeBlock { Needs = { ["Material_T1_Plant_Fibre"] = 4 } },
        });
        Blocks.Define("neat-crop", new HairBlock { Name = "Neat Crop", Look = new WornLook { Like = "VC_Hair_04_02_frenchcrop" } });
        Blocks.Define("light-stubble", new BeardBlock { Name = "Light Stubble", Look = new WornLook { Like = "VC_Beard12_stubble" } });
    }
}
