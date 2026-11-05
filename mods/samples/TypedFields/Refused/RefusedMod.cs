// SPDX-License-Identifier: MIT
using Kiln;

namespace TypedFields.Refused;

public sealed class RefusedMod : Mod
{
    public override void OnLoad() => Content.OnReady(_ =>
    {
        // string paths still work, and they are checked: dmgMood is not a field of damageSetup
        Content.Items.Get("Weapon_T1_1H_SwordEpic_01")!.Set("damageSetup.dmgMood", 1.5);
    });
}
