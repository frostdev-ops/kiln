// SPDX-License-Identifier: MIT
using System.Numerics;
using Kiln;

namespace ShaderSample;

/// <summary>
/// The effects are in blocks/vfx.json, read when the game starts. Each is on a key, played 4 m ahead:
/// <list type="bullet">
/// <item>Ctrl+Alt+F6, <c>swirl</c>: drawn by this mod's own module, shaders/swirl.hlsl. Blue dots rise from a disc and swirl around each other for 4 s.</item>
/// <item>Ctrl+Alt+F7, <c>swirl_plain</c>: the same block on Kiln's own sprite: the dots drift and slow down, with no swirl.</item>
/// <item>Ctrl+Alt+F8, <c>swirl_broken</c>: names a module that fails its checks on purpose. Kiln's sprite draws it (a pink burst), and kiln.log says why once at start.</item>
/// </list>
/// </summary>
public sealed class ShaderSampleMod : Mod
{
    public override void OnLoad()
    {
        if (!Client.Available || !Client.IsClient) return;
        KeyBindings.Register("shader-sample.swirl", "Ctrl+Alt+F6", () => PlayAhead("swirl"), "Particles drawn by this mod's own shader");
        KeyBindings.Register("shader-sample.plain", "Ctrl+Alt+F7", () => PlayAhead("swirl_plain"), "The same particles on Kiln's sprite");
        KeyBindings.Register("shader-sample.broken", "Ctrl+Alt+F8", () => PlayAhead("swirl_broken"), "A shader that fails its checks: Kiln's sprite instead");
    }

    // A key arrives on the main thread; where the player stands is read on the world's thread. Vfx takes it from any thread.
    private void PlayAhead(string name) => Context.Scheduler.Post(() =>
    {
        if (World.Transform(Client.LocalPlayer) is not { } t) return;
        var ahead = Vector3.Transform(new Vector3(0, 0, 4), t.Rotation);
        var h = Vfx.PlayAt(VfxId.Named(Context, name), new WorldPosition(t.Position.X + ahead.X, t.Position.Y + 0.5, t.Position.Z + ahead.Z));
        Log.Info($"shader-sample: {name} -> {h}");
    });
}
