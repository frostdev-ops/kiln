// SPDX-License-Identifier: MIT
// Spell Tweaks: the changes are all in content/spelltweaks.json (a kiln.gamedata data pack). This code only logs what
// the game uses now, to show the C# side of the same data: Content.ItemData / Content.Templates / Content.Sequences.
using Kiln;

namespace SpellTweaks;

public sealed class SpellTweaksMod : Mod
{
    public override void OnLoad()
    {
        // Content.OnReady needs kiln.content, the pack's data kiln.gamedata: two services, so two questions.
        if (!Content.Available || !Content.GameDataAvailable)
        {
            Log.Warn("kiln.content or kiln.gamedata is not available on this build: the pack does nothing");
            return;
        }
        Content.OnReady(_ =>
        {
            var mana = Content.Items.Get("Ammo_T1_Spell_Fire_Ball_lvl10")?.Config("ManaCost");
            var jump = Content.Items.Get("Ability_DoubleJump")?.Config("StaminaCost");
            var expl = Content.Templates.Get("Explosion_Player_Spell_Fireball_1");
            var cast = Content.Sequences.Get("Attack_Player_Staff_FixedAnim_Fireball_fast");
            const string radius = "ColliderResourceComponent.colliders[0].dataArray[0].shape.radius";
            Log.Info($"Fireball I: mana {mana?.Get()} (vanilla {mana?.Original()}), leaves the staff at " +
                     $"{cast?.Event("SpawnEntityEvent").Time.TotalSeconds:0.00} s (vanilla {cast?.Original("events[SpawnEntityEvent#0].time")}), " +
                     $"explosion radius {expl?.Get(radius)} (vanilla {expl?.Original(radius)}), push-back " +
                     $"{expl?.GetJson("ImpactConfiguration.configValues.PushBackStrength")?["value"]}; double jump stamina {jump?.Get()} (vanilla {jump?.Original()})");
        });
    }
}
