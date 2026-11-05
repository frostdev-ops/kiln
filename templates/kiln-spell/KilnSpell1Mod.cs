using Kiln;

namespace KilnSpell1;

/// <summary>A staff spell. It flies, chills what it strikes and bursts where it lands; every charge costs 20 mana, and two twigs make 10 charges.
/// blocks/frostnova.json is another spell, as data, using the same status.</summary>
public sealed class KilnSpell1Mod : Mod
{
    public override void OnLoad()
    {
        // a status: 2 ice every 2 seconds and half speed, for 6 seconds
        var chilled = Blocks.Define("chilled", new StatusBlock
        {
            Name = "Chilled",
            Description = "Slowed by the cold, which bites a little every 2 seconds.",
            Seconds = 6,
            TickEvery = 2,
            OnTick = [new EffectBlock { Damage = Damage.Ice(2), Flinch = Flinch.None }],
            Modifies = { [Stat.MoveSpeed] = -0.5 },
        });

        // an effect: what happens, wherever something triggers it
        var burst = Blocks.Define("burst", new EffectBlock
        {
            Damage = Damage.Ice(25),
            Knockback = Knockback.Small,
            Applies = chilled,
        });

        Blocks.Define("frostshard", new SpellBlock
        {
            Name = "Frost Shard",
            Description = "A shard of ice that bursts where it lands.",
            Cast = new CastBlock { ManaCost = 20 },
            Delivery = new ProjectileBlock
            {
                Speed = 35,
                OnImpact = [new EffectBlock { Area = new AreaBlock { Radius = 3, Effects = [burst] } }],
            },
            // an item by its name; with a catalog (`kiln catalog build`) the GameItems constants complete them: GameItems.Material_T1_Twigs
            Recipe = new RecipeBlock { Makes = 10, Needs = { ["Material_T1_Twigs"] = 2 } },
        });
    }
}
