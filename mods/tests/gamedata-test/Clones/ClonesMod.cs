// SPDX-License-Identifier: MIT
using Kiln;

namespace GamedataClones;

/// <summary>
/// gamedata-test's clones (content/clones.json) and one reference from code: Weapon_T1_1H_Sword_01's fifth perk slot
/// set to the perk clone through kiln.gamedata (perkIds[4] follows: FNV-1a of the clone's GUID). A code layer of this
/// mod, so it comes off with it.
/// </summary>
public sealed class ClonesMod : Mod
{
    public override void OnLoad()
    {
        Log.Info("gamedata-clones: loaded (its data pack defines Kiln_Test_Frostbite and Kiln_Test_Frostbite_Perk)");
        if (!Content.GameDataAvailable) return;
        Content.OnReady(_ =>
        {
            var sword = Content.ItemData.Get("Weapon_T1_1H_Sword_01");
            if (sword == null) return;
            sword.Set("perkReferences[4]", "Kiln_Test_Frostbite_Perk");
            Log.Info($"gamedata-clones: Weapon_T1_1H_Sword_01 perkReferences[4] = {sword.Get("perkReferences[4]").Guid}, perkIds[4] = 0x{(uint)sword.Get("perkIds[4]").Integer:x8}");
        });
    }
}
