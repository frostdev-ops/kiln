using System.Numerics;
using Kiln;

namespace KilnShader1;

/// <summary>
/// The effects are in blocks/vfx.json, read when the game starts. Two keys play them 4 m ahead of you:
/// F7 "swirl" with this mod's own hooks (shaders/swirl.hlsl), Shift+F7 "swirl_plain", the same block on Kiln's sprite alone.
/// After you change the .hlsl, rebuild the module: kiln-vfxcook shader build --mod . (it compiles, stamps and checks
/// it; a module that fails Kiln's checks is not linked, the effect is drawn by Kiln's own sprite and kiln.log says why).
/// </summary>
public sealed class KilnShader1Mod : Mod
{
    public override void OnLoad()
    {
        // Only a player's game draws anything: on a dedicated server there is nothing to do.
        if (!Client.Available || !Client.IsClient) return;
        KeyBindings.Register("kiln-mod-id.swirl", "F7", () => PlayAhead("swirl"), "Particles moved by this mod's shader hooks");
        KeyBindings.Register("kiln-mod-id.plain", "Shift+F7", () => PlayAhead("swirl_plain"), "The same particles without the hooks");
    }

    // A key arrives on the main thread; where the player stands is read on the world's thread.
    private void PlayAhead(string name) => Context.Scheduler.Post(() =>
    {
        if (World.Transform(Client.LocalPlayer) is not { } t) return;
        var ahead = Vector3.Transform(new Vector3(0, 0, 4), t.Rotation);
        var played = Vfx.PlayAt(VfxId.Named(Context, name), new WorldPosition(t.Position.X + ahead.X, t.Position.Y + 0.5, t.Position.Z + ahead.Z));
        Log.Info($"{name}: {played}");
    });
}
