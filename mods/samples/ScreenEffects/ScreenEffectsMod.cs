// SPDX-License-Identifier: MIT
using Kiln;

namespace ScreenEffects;

/// <summary>
/// Screen effects as blocks: one a status shows while a player has it, one an effect shows for a moment, one written in place.
/// blocks/twins.json says the same in data. Each play is sent to the Kiln clients that should see it; a vanilla client just doesn't.
/// </summary>
public sealed class ScreenEffectsMod : Mod
{
    public override void OnLoad()
    {
        // colder and greyer, with a blue edge: while the status lasts, then fades out over a second
        var cold = Blocks.Define("cold", new ScreenEffectBlock
        {
            Temperature = -0.6f, Saturation = 0.7f,
            Vignette = new ScreenVignette { Color = "#10243a", Amount = 0.6f },
            FadeIn = 1, FadeOut = 1,
        });
        var frozen = Blocks.Define("frozen", new StatusBlock
        {
            Name = "Frozen", Description = "Slow, and the world goes pale.", Seconds = 10,
            Modifies = { [Stat.MoveSpeed] = -0.4 }, Screen = cold,
        });

        // a white flash: on at once, gone over 1.5 s. An effect's screen needs Seconds
        var flash = Blocks.Define("flash", new ScreenEffectBlock { Fill = "#ffffff", Seconds = 0.2f, FadeOut = 1.5f });

        // a staff spell that lands with a flash for those near, and freezes
        Blocks.Define("flash-frost", new SpellBlock
        {
            Name = "Flash Frost", Description = "Lands with a blinding flash, and freezes.",
            Cast = new CastBlock { ManaCost = 25 },
            Delivery = new ProjectileBlock
            {
                Speed = 30,
                OnImpact = [new EffectBlock { Screen = flash, Area = new AreaBlock { Radius = 4, Effects = [new EffectBlock { Damage = Damage.Ice(15), Applies = frozen }] } }],
            },
            Recipe = new RecipeBlock { Makes = 5, Needs = { ["Material_T1_Twigs"] = 2 } },
        });

        // written in place: a short red pulse for the one a blow hits
        Blocks.Define("bleed", new StatusBlock
        {
            Name = "Bleeding", Seconds = 6, TickEvery = 2,
            OnTick = [new EffectBlock { Damage = Damage.Pierce(3), Flinch = Flinch.None, Screen = new ScreenEffectBlock { Tint = "#ff2020", Seconds = 0.3f, FadeOut = 0.5f } }],
        });
    }
}
