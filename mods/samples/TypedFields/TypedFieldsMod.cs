// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.GameIds;

namespace TypedFields;

/// <summary>
/// Below building blocks: the game's own data, changed through typed paths. Every name here completes in the editor and is
/// checked by the compiler: a wrong field does not exist, and a wrong value type does not fit.
/// </summary>
public sealed class TypedFieldsMod : Mod
{
    private static readonly Guid LongBolt = Guid.Parse("5f0e3c52-7c7a-4f6e-9b58-3f4d9a6c1e21");

    public override void OnLoad()
    {
        // a clone of a projectile that flies longer and bursts bigger, with typed values instead of a string list
        Content.Templates.Define(new GameDataClone
        {
            Guid = LongBolt,
            Name = "Typed_Long_Bolt",
            Base = GameTemplates.Projectile_Player_Staff_IceBolt_1,
            Values =
            {
                { GameFields.Templates.LifeTime.LifeTime, TimeSpan.FromSeconds(8) },
                { GameFields.Templates.ImpactConfiguration.ConfigValues.SpawnTemplate, GameTemplates.Explosion_Player_Spell_IceBolt_1 },
            },
        });

        Content.OnReady(_ =>
        {
            var sword = Content.Items.Get(GameItems.Weapon_T1_1H_SwordEpic_01)!;
            sword.Set(GameFields.Items.DamageSetup.DmgMod, 1.5f);
            sword.Set(GameFields.Items.Rarity, GameEnums.ItemRarity.Legendary);

            // a config value, by the name the game's programs give it; its tooltip row follows
            var bolt = Content.ItemData.Get(GameItems.Ammo_T1_Spell_Ice_Bolt_lvl5)!;
            bolt.Set(GameFields.Items.ImpactValues.ManaCost, 20);

            // a component's field on a template
            var projectile = Content.Templates.Get(GameTemplates.Projectile_Player_Staff_IceBolt_1)!;
            projectile.Set(GameFields.Templates.StaticGravityModifier.Modifier, 0.5f);

            Log.Info($"typed fields: sword dmgMod {sword.Get(GameFields.Items.DamageSetup.DmgMod)}, was {sword.Original(GameFields.Items.DamageSetup.DmgMod)}; " +
                     $"bolt mana {bolt.Get(GameFields.Items.ImpactValues.ManaCost)}");
        });
    }
}
