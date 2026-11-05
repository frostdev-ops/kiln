// SPDX-License-Identifier: MIT
using System.Numerics;
using Kiln;

namespace VfxSample;

/// <summary>
/// The effects are in blocks/vfx.json, read when the game starts. Each one is on a key, played 4 m ahead:
/// <list type="bullet">
/// <item>Ctrl+F6, <c>ember_burst</c>: sparks and smoke of the mod's own, two particle blocks and a light.</item>
/// <item>Ctrl+F7, <c>warm_glow</c>: a light and nothing else.</item>
/// <item>Ctrl+F8, one of the game's by name: a template that carries it. Nothing of it is in this mod.</item>
/// <item>Ctrl+F9, <c>ember_puff</c>: a block from code, there from the next start, because the game lists its VFX once.</item>
/// </list>
/// The spell <c>vfx_sample_bolt</c> in the same file shows the other way, with no code: a charge at the hand, a trail in flight, a burst where it lands.
/// </summary>
public sealed class VfxSampleMod : Mod
{
    private const string GameFireball = "Explosion_Player_Spell_Fireball_1";

    public override void OnLoad()
    {
        var burst = VfxId.Named(Context, "ember_burst");
        var glow = VfxId.Named(Context, "warm_glow");

        var puff = Blocks.Define("ember_puff", new VfxBlock
        {
            Particles =
            [
                new ParticleBlock
                {
                    Burst = 8,
                    Lifetime = (0.8f, 1.4f),
                    Speed = (0.2f, 0.6f),
                    Spread = Shape.Sphere,
                    Size = Curve.Linear(0.4f, 1.2f),
                    Color = "#6b4a36",
                    Alpha = Curve.Linear(0.5f, 0f),
                    Texture = "vfx/puff_2x2.png",
                    Flipbook = new Flipbook { Columns = 2, Rows = 2 },
                },
            ],
        });

        if (!Client.Available || !Client.IsClient) return;
        KeyBindings.Register("vfx-sample.burst", "Ctrl+F6", () => PlayAhead(burst), "Sparks and smoke, a block from data");
        KeyBindings.Register("vfx-sample.glow", "Ctrl+F7", () => PlayAhead(glow), "A light, a block from data");
        KeyBindings.Register("vfx-sample.game", "Ctrl+F8", () => PlayAhead(VfxId.Game(GameFireball)), "One of the game's VFX, by name");
        KeyBindings.Register("vfx-sample.puff", "Ctrl+F9", () => PlayAhead(puff), "A block from code, there from the next start");
    }

    // A key arrives on the main thread; where the player stands is read on the world's thread. Vfx takes it from any thread.
    private void PlayAhead(VfxId vfx) => Context.Scheduler.Post(() =>
    {
        if (World.Transform(Client.LocalPlayer) is not { } t) return;
        var ahead = Vector3.Transform(new Vector3(0, 0, 4), t.Rotation);
        Vfx.PlayAt(vfx, new WorldPosition(t.Position.X + ahead.X, t.Position.Y + 1.0, t.Position.Z + ahead.Z));
    });
}
