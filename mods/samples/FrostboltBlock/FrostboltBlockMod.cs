// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.GameIds;

namespace FrostboltBlock;

/// <summary>
/// Everything this mod adds, said once. Frost Burst is what happens; the spell's area and the sword's hits both use it.
/// Its effects are its own: a charge at the hand, a trail in flight, a burst where it lands, an aura on who is chilled. A VFX from code is there from the
/// next start, as the game lists them once; blocks/frostlance.json is the same spell as data, whose VFX are there from the first.
/// </summary>
public sealed class FrostboltBlockMod : Mod
{
    public override void OnLoad()
    {
        var burst = Blocks.Define("frost_burst", new VfxBlock
        {
            Particles =
            [
                new ParticleBlock
                {
                    Burst = 24,
                    Lifetime = (0.4f, 0.9f),
                    Speed = (2f, 5f),
                    Spread = Shape.Cone(35),
                    Gravity = -4f,
                    Size = Curve.Linear(0.15f, 0f),
                    Color = Gradient.Of("#ffffff", "#7fd4ff"),
                    Alpha = Curve.Linear(1f, 0f),
                    Texture = "vfx/shard.png",
                    Blend = Blend.Additive,
                },
            ],
            Light = new LightBlock { Color = "#7fd4ff", Radius = 6f, Brightness = 4f, Lifetime = 0.6f, FadeOut = 0.5f },
        });
        var charge = Blocks.Define("frost_charge", new VfxBlock
        {
            Particles = [new ParticleBlock { Burst = 8, Lifetime = 0.3f, Speed = (0.2f, 0.6f), Spread = Shape.Sphere, Size = Curve.Linear(0.08f, 0f), Color = "#bfe8ff", Texture = "vfx/shard.png", Blend = Blend.Additive }],
        });
        var trail = Blocks.Define("frost_trail", new VfxBlock
        {
            Particles = [new ParticleBlock { Rate = 40, Lifetime = 0.5f, Speed = (0f, 0.3f), Spread = Shape.Sphere, Size = Curve.Linear(0.12f, 0f), Alpha = Curve.Linear(0.8f, 0f), Color = "#7fd4ff", Texture = "vfx/shard.png", Blend = Blend.Additive }],
            Light = new LightBlock { Color = "#7fd4ff", Radius = 3f, Brightness = 2f },
        });
        var aura = Blocks.Define("frost_aura", new VfxBlock
        {
            Particles = [new ParticleBlock { Rate = 6, Lifetime = (0.6f, 1.2f), Speed = (0.1f, 0.4f), Spread = Shape.Up, SpawnRadius = 0.5f, Size = 0.1f, Alpha = Curve.Linear(0.6f, 0f), Color = "#bfe8ff", Texture = "vfx/shard.png", Blend = Blend.Additive }],
        });

        var chilled = Blocks.Define("chilled", new StatusBlock
        {
            Name = "Chilled",
            Description = "The cold bites a little every 2 seconds.",
            Seconds = 8,
            TickEvery = 2,
            Vfx = aura,
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
            Cast = new CastBlock { ManaCost = 30, Vfx = charge },
            Delivery = new ProjectileBlock
            {
                Speed = 30,
                Gravity = 0.1f,
                Vfx = trail,
                OnImpact = [new EffectBlock { Vfx = burst, Area = new AreaBlock { Radius = 3, Effects = [frostBurst] } }],
            },
            Recipe = new RecipeBlock { Station = Stations.Hand, Makes = 10, Needs = { [GameItems.Material_T1_Twigs] = 2 } },
        });

        Blocks.Define("frost-edge", new WeaponBlock
        {
            Name = "Frost Edge",
            Description = "Every blow bursts with frost.",
            Class = WeaponClass.Sword,
            Stats = new WeaponStats { Level = 5, Power = 30 },
            OnHit = new OnHitBlock { Effects = [frostBurst], Vfx = burst },
            Recipe = new RecipeBlock { Needs = { [GameItems.Material_T1_Twigs] = 4 } },
        });
    }
}
