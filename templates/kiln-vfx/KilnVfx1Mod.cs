using System.Numerics;
using Kiln;

namespace KilnVfx1;

/// <summary>One effect, twice: in blocks/spark.json and in code. The data file is read when the game starts, so that one is there from the first start. The game lists
/// its VFX once, so a block defined from code is there from the next start. Attach either one to a spell, a status or an effect with a <c>Vfx</c> field.</summary>
public sealed class KilnVfx1Mod : Mod
{
    public override void OnLoad()
    {
        var fromData = VfxId.Named(Context, "spark");

        var fromCode = Blocks.Define("spark_code", new VfxBlock
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
                    Texture = "vfx/dot.png",
                    Blend = Blend.Additive,
                },
            ],
            Light = new LightBlock { Color = "#7fd4ff", Radius = 6f, Brightness = 4f, Lifetime = 0.6f, FadeOut = 0.5f },
        });

        // a dedicated server has no screen and no keys
        if (!Client.Available || !Client.IsClient) return;
        KeyBindings.Register("kiln-mod-id.spark", "F7", () => PlayAhead(fromData), "Play the effect from blocks/spark.json");
        KeyBindings.Register("kiln-mod-id.spark-code", "Shift+F7", () => PlayAhead(fromCode), "Play the effect from code");
    }

    // A key arrives on the main thread; where the player stands is read on the world's thread. Vfx takes it from any thread.
    private void PlayAhead(VfxId vfx) => Context.Scheduler.Post(() =>
    {
        if (World.Transform(Client.LocalPlayer) is not { } t) return;
        var ahead = Vector3.Transform(new Vector3(0, 0, 3), t.Rotation);
        Vfx.PlayAt(vfx, new WorldPosition(t.Position.X + ahead.X, t.Position.Y + 1.0, t.Position.Z + ahead.Z));
    });
}
