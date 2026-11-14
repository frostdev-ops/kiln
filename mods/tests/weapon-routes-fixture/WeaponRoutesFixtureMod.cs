// SPDX-License-Identifier: MIT
// The routed sword of the moves lab and of the guided checks 16c-b and 16c-d: its block hands over to its own parry when it parried a blow, and
// to its first light attack on Primary; the light chain links up by itself (1 to 2 to 1), and the parry is the game's, as this sword's own move.
// Defined in code so that the mod has an owner of its own: unloading the mod takes the sword's layers off. blocks/weapons.json has the fixture's
// other blocks (a sword whose routes overlap and is refused, a shield, a staff charge).
using Kiln;

namespace WeaponRoutesFixture;

public sealed class WeaponRoutesFixtureMod : Mod
{
    public override void OnLoad()
    {
        // the capability is switched off until its game test has passed; a weapon with follow-ups is refused while it is
        if (Capabilities.IsHeld("blocks.move_followups"))
        {
            Log.Info("weapon-routes-fixture: blocks.move_followups is held (game test pending): route-sword is not defined");
            return;
        }
        Blocks.Define("route-sword", new WeaponBlock
        {
            Name = "Route Sword", Class = WeaponClass.Sword,
            Stats = new WeaponStats { Level = 5, Power = 30, Rarity = Rarity.Rare },
            Moves = new WeaponMoves
            {
                Primary =
                [
                    new MoveBlock { Like = "Attack_Player_1H_Single_LightChain1" },
                    new MoveBlock { Like = "Attack_Player_1H_Single_LightChain2" },
                ],
                Parry = new MoveBlock { Like = "Ability_Player_Parry_AllWeapons" },
                Block = new MoveBlock
                {
                    FollowUps =
                    [
                        new MoveFollowUpBlock { Trigger = MoveFollowUpTrigger.Parried, To = WeaponMoveSlot.Parry },
                        new MoveFollowUpBlock { Trigger = MoveFollowUpTrigger.Primary, To = WeaponMoveSlot.Primary, Link = 1 },
                    ],
                },
            },
        });
    }
}
