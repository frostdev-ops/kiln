// SPDX-License-Identifier: MIT
// A piece for a vanity slot, on a model of our own. blocks/cosmetics.json has it again in data, as the lab test holds them to be the same, and a cape. kiln cook finds the files of the looks
// in data files; a look only in code would need a worn step in assets/cook.json (docs/kiln/modding/cosmetics.md). This one's file is named there too.
using Kiln;
using Kiln.GameIds;

namespace CosmeticSample;

public sealed class CosmeticSampleMod : Mod
{
    public override void OnLoad()
    {
        Blocks.Define("frost-hood", new CosmeticBlock
        {
            Name = "Frost Hood",
            Description = "Cold to the touch, warm to the wearer.",
            Slot = CosmeticSlot.Head,
            Look = new WornLook
            {
                Model = "models/frost-hood.gltf",   // a mesh with no skin, around its origin, and a morph target named Female
                Attach = WornAttach.Head,           // so the origin is the head joint
                Fits = Fit.Hood,                    // the game swaps hair for its hood cut
                Hides = [BodyPart.Hair],
            },
            Recipe = new RecipeBlock { Needs = { [GameItems.Material_T1_Plant_Fibre] = 12 } },
        });
    }
}
