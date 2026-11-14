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
/// At boot kiln.playeranim adds the states to the player's graph, on the server and on every player's game that has the mod (the manifest says
/// <c>"side": "both"</c> and <c>"clients": "mod"</c>), and the moves play the sample's own clips in them, with the timing below. A change to the
/// states applies from the next start. Not seen in a game client yet.
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
            // in the hand the skinned model and the graph of assets/cook.json, by their steps' names; the rigid model on the item, and in the hand where the moving one is not cooked
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

        // Follow-ups (docs/kiln/modding/item-blocks.md, "Follow-ups"): the same blade, whose block hands over to its own parry when it parried a blow
        // and to its first swing on the light attack. A weapon with follow-ups applies all its moves at once or none of them; the event says which.
        // The capability is switched off until its game test has passed, and a weapon with follow-ups is refused while it is: so ask first.
        if (Capabilities.IsHeld("blocks.move_followups"))
        {
            Log.Info("weapon-moves: blocks.move_followups is waiting for its game test: the Parrying Blade is left out");
            return;
        }
        Blocks.Define("parrying-blade", new WeaponBlock
        {
            Name = "Parrying Blade", Description = "Its guard answers.", Class = WeaponClass.Sword,
            Stats = new WeaponStats { Level = 5, Power = 30, Rarity = Rarity.Rare },
            Visual = new VisualBlock { Model = BladeModel, Animated = new AnimatedModel { Model = "moves_blade_anim", Graph = "moves_blade" } },
            Moves = new WeaponMoves
            {
                Primary =
                [
                    new MoveBlock { Animation = "slash-1", Like = "Attack_Player_1H_Single_LightChain1", HitAt = 0.42, Length = 1.0 },
                    new MoveBlock { Animation = "slash-2", Like = "Attack_Player_1H_Single_LightChain2", HitAt = 0.37, Length = 0.87 },
                ],
                // the game's parry, as this weapon's own move: a route leads to a move of the same weapon
                Parry = new MoveBlock { Like = "Ability_Player_Parry_AllWeapons" },
                Block = new MoveBlock
                {
                    Animation = "guard",
                    FollowUps =
                    [
                        new MoveFollowUpBlock { Trigger = MoveFollowUpTrigger.Parried, To = WeaponMoveSlot.Parry },
                        new MoveFollowUpBlock { Trigger = MoveFollowUpTrigger.Primary, To = WeaponMoveSlot.Primary, Link = 1 },
                    ],
                },
            },
            Recipe = new RecipeBlock { Needs = { ["Material_T1_Twigs"] = 2 } },
        });
        Events.On<WeaponMovesApplicationChangedEvent>(e =>
        {
            if (e.ModId != Context.Info.Id) return;
            if (e.After.Ok) Log.Info($"weapon-moves: {e.BlockName}: its moves are applied (generation {e.After.AppliedGeneration})");
            else Log.Warn($"weapon-moves: {e.BlockName}: {e.After.State}: {e.After.Reason}");
        });
    }
}
