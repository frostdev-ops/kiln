using Kiln;

namespace KilnShader1;

/// <summary>
/// The effects are in blocks/vfx.json, read when the game starts. Two keys play them 4 m ahead of you:
/// F7 "swirl" with this mod's own hooks (shaders/swirl.hlsl), Shift+F7 "swirl_plain", the same block on Kiln's sprite alone.
/// After you change the .hlsl, rebuild the module with kiln-vfxcook (its "shader build" command: it compiles, stamps
/// and checks it; a module that fails Kiln's checks is not linked, the effect is drawn by Kiln's own sprite and
/// kiln.log says why).
/// </summary>
public sealed class KilnShader1Mod : Mod
{
    // The camera of the last frame: written on the render thread, read when a key is pressed.
    private readonly object _gate = new();
    private RenderCamera _camera;

    public override void OnLoad()
    {
        // Only a player's game draws anything: on a dedicated server there is nothing to do.
        if (!Client.Available || !Client.IsClient) return;
        if (!Render.Available)
        {
            Log.Info("no render events on this game build: the keys are not bound");
            return;
        }
        // Where to play it comes from the player's own camera, so it works in a hosted and in a joined game alike
        // (the world's entities are readable only in the game that hosts).
        Events.On<RenderFrameEvent>(e =>
        {
            lock (_gate) _camera = e.Camera;
        });
        KeyBindings.Register("kiln-mod-id.swirl", "F7", () => PlayAhead("swirl"), "Particles moved by this mod's shader hooks");
        KeyBindings.Register("kiln-mod-id.plain", "Shift+F7", () => PlayAhead("swirl_plain"), "The same particles without the hooks");
    }

    private void PlayAhead(string name)
    {
        RenderCamera camera;
        lock (_gate) camera = _camera;
        if (!camera.Valid)
        {
            Log.Info($"{name}: no camera yet (not in a game?)");
            return;
        }
        // Pivot is what the camera looks at (about the character's head); Forward the way it looks.
        var from = camera.Pivot;
        var ahead = camera.Forward;
        var played = Vfx.PlayAt(VfxId.Named(Context, name), new WorldPosition(from.X + ahead.X * 4, from.Y, from.Z + ahead.Z * 4));
        Log.Info($"{name}: {played}");
    }
}
