using Kiln;

namespace KilnScreenEffect1;

/// <summary>Screen effects are blocks: you describe what the screen does, and a status or an effect shows it. Kiln
/// sends each play to the players who should see it; a player without Kiln just does not see it.</summary>
public sealed class KilnScreenEffect1Mod : Mod
{
    public override void OnLoad()
    {
        // While a status lasts: colder and greyer, with a blue edge. FadeIn and FadeOut are seconds.
        var cold = Blocks.Define("cold", new ScreenEffectBlock
        {
            Temperature = -0.6f, Saturation = 0.7f,
            Vignette = new ScreenVignette { Color = "#10243a", Amount = 0.6f },
            FadeIn = 1, FadeOut = 1,
        });
        var chilled = Blocks.Define("chilled", new StatusBlock
        {
            Name = "Chilled", Description = "Slow, and the world goes pale.", Seconds = 10,
            Modifies = { [Stat.MoveSpeed] = -0.4 }, Screen = cold,
        });

        // For a moment: a white flash, on at once and gone over 1.5 s. A screen on an effect needs Seconds.
        var flash = Blocks.Define("flash", new ScreenEffectBlock { Fill = "#ffffff", Seconds = 0.2f, FadeOut = 1.5f });

        // Something that shows both: a staff spell that lands with the flash for those near and chills what it hits.
        Blocks.Define("chill-bolt", new SpellBlock
        {
            Name = "Chill Bolt", Description = "Lands with a flash, and chills.",
            Cast = new CastBlock { ManaCost = 25 },
            Delivery = new ProjectileBlock
            {
                Speed = 30,
                OnImpact = [new EffectBlock { Screen = flash, Area = new AreaBlock { Radius = 4, Effects = [new EffectBlock { Damage = Damage.Ice(15), Applies = chilled }] } }],
            },
            Recipe = new RecipeBlock { Makes = 5, Needs = { ["Material_T1_Twigs"] = 2 } },
        });
    }
}
