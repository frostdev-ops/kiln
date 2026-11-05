// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.GameIds;

namespace FrostboltBlock;

/// <summary>
/// Everything this mod adds, said once. Frost Burst is what happens; the spell's area and the sword's hits both use it.
/// blocks/frostlance.json is the same spell as data.
/// </summary>
public sealed class FrostboltBlockMod : Mod
{
    public override void OnLoad()
    {
        var chilled = Blocks.Define("chilled", new StatusBlock
        {
            Name = "Chilled",
            Description = "The cold bites a little every 2 seconds.",
            Seconds = 8,
            TickEvery = 2,
            OnTick = [new EffectBlock { Damage = Damage.Ice(3), Flinch = Flinch.None }],
        });

        var frostBurst = Blocks.Define("frost-burst", new EffectBlock
        {
            Damage = Damage.Ice(40),
            Flinch = Flinch.Medium,
            Knockback = Knockback.Small,
            Applies = chilled,
        });

        Blocks.Define("frostbolt", new SpellBlock
        {
            Name = "Frostbolt",
            Description = "A shard of ice that bursts where it lands, and chills what it hits.",
            Cast = new CastBlock { ManaCost = 30 },
            Delivery = new ProjectileBlock
            {
                Speed = 30,
                Gravity = 0.1f,
                OnImpact = [new EffectBlock { Area = new AreaBlock { Radius = 3, Effects = [frostBurst] } }],
            },
            Recipe = new RecipeBlock { Station = Stations.Hand, Makes = 10, Needs = { [GameItems.Material_T1_Twigs] = 2 } },
        });

        Blocks.Define("frost-edge", new WeaponBlock
        {
            Name = "Frost Edge",
            Description = "Every blow bursts with frost.",
            Class = WeaponClass.Sword,
            Stats = new WeaponStats { Level = 5, Power = 30 },
            OnHit = [frostBurst],
            Recipe = new RecipeBlock { Needs = { [GameItems.Material_T1_Twigs] = 4 } },
        });
    }
}
