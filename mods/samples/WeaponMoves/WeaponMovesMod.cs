// SPDX-License-Identifier: MIT
using Kiln;

namespace WeaponMovesSample;

/// <summary>
/// A sword with moves of its own: a chain of two swings, a heavy and a block, each naming a state of this mod and the game sequence it starts from,
/// with its own timing; and a held model with moving parts. blocks/weapon.json says the same in data. docs/kiln/modding/item-blocks.md, "Moves".
///
/// The clips are the sample's own: make_assets.py writes a rig and its motions to assets/src/fighter.gltf, and assets/cook.json retargets them onto
/// the player's skeleton when the mod is cooked (the second swing is the first one mirrored and a little faster). assets/player-states.json makes
/// a state of each clip, placed like the game's sword attacks and block.
///
/// In this build nothing reads assets/player-states.json yet: when content is ready each name is reported ("player states are not available in
/// this build") and each move plays its <c>Like</c> sequence's own animation, with the timing below. Once the player-anim package is in the build,
/// the moves play the sample's own clips. The held model moves either way.
/// </summary>
public sealed class WeaponMovesMod : Mod
{
    // derive("weapon-moves", "model", "moves_blade"): the rigid model the cook's model step of that name makes
    private const string BladeModel = "cf4dc9c5-be6a-5e8d-a0d0-cd4cb877fb51";

    public override void OnLoad()
    {
        Blocks.Define("turning-blade", new WeaponBlock
        {
            Name = "Turning Blade", Description = "A crystal turns in the blade.", Class = WeaponClass.Sword,
            Stats = new WeaponStats { Level = 5, Power = 30, Rarity = Rarity.Rare },
            // in the hand the skinned model and the graph of assets/cook.json, by their steps' names; the rigid model for the icon and the ground
            Visual = new VisualBlock { Model = BladeModel, Animated = new AnimatedModel { Model = "moves_blade_anim", Graph = "moves_blade" } },
            Moves = new WeaponMoves
            {
                // a chain: the first swing hands over to the second, the second to the first. HitAt is when the blow lands, Length when it ends
                Primary =
                [
                    new MoveBlock { Animation = "slash-1", Like = "Attack_Player_1H_Single_LightChain1", HitAt = 0.42, Length = 1.0 },
                    new MoveBlock { Animation = "slash-2", Like = "Attack_Player_1H_Single_LightChain2", HitAt = 0.37, Length = 0.87 },
                ],
                Heavy = new MoveBlock { Animation = "cleave", Like = "Attack_Player_Melee_Heavy_Sword_Diagonal", HitAt = 0.9, Length = 1.6 },
                // a block lands no blow: no HitAt. Its state is placed like the game's Block_FullBody, so the legs keep the block's walk
                Block = new MoveBlock { Animation = "guard" },
            },
            Recipe = new RecipeBlock { Needs = { ["Material_T1_Twigs"] = 1 } },
        });
    }
}
