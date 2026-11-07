// SPDX-License-Identifier: MIT
using System.Collections.Immutable;
using System.Numerics;
using Kiln;

namespace RenderSample;

/// <summary>
/// Render events in three parts, all drawn from one <see cref="RenderFrameEvent"/> handler on the render thread:
///  - an FPS counter (top left) with the frame time;
///  - a compass strip (top centre) from the camera's heading;
///  - waypoints: Ctrl+J drops one where the character stands, Ctrl+Shift+J removes the nearest. Each shows a pin image
///    (a PNG in this mod), its name and distance, a ring on the ground and a line down to it; off screen an arrow on the
///    screen's edge points the way. Ctrl+Alt+J hides or shows everything.
/// Ctrl+Alt+K writes the camera to the log (for checking the projection); Ctrl+Alt+F reads the field of view as
/// horizontal for a test.
/// </summary>
public sealed class RenderSampleMod : Mod
{
    private sealed record Waypoint(string Name, WorldPosition At, Color Color);

    private static readonly Color[] Palette = [Color.Orange, Color.Cyan, Color.Green, Color.Yellow, new(230, 120, 230)];

    // Only the main thread writes; the render thread reads one published reference for the whole frame.
    private sealed record WaypointsState(uint Session, ImmutableArray<Waypoint> Points);
    private WaypointsState _waypoints = new(0, []);
    private volatile bool _visible = true;
    private int _next = 1;
    private UiImage? _pin;
    private float _frameMs;

    public override void OnLoad()
    {
        if (!Render.Available)
        {
            Log.Info("rendersample: no overlay here (dedicated server or no kiln.render): nothing to do");
            return;
        }
        _pin = Ui.LoadImage(Path.Combine(Context.Info.Directory, "waypoint.png"));
        Client.Tick += OnTick;
        Events.On<RenderFrameEvent>(OnFrame);
        KeyBindings.Register("rendersample.mark", "Ctrl+J", Mark, "Drop a waypoint where the character stands");
        KeyBindings.Register("rendersample.unmark", "Ctrl+Shift+J", Unmark, "Remove the nearest waypoint");
        KeyBindings.Register("rendersample.toggle", "Ctrl+Alt+J", () => _visible = !_visible, "Show or hide the render sample");
        KeyBindings.Register("rendersample.camera", "Ctrl+Alt+K", LogCamera, "Write the camera to the log");
        KeyBindings.Register("rendersample.fov", "Ctrl+Alt+F", () =>
        {
            Render.FieldOfViewIsHorizontal = !Render.FieldOfViewIsHorizontal;
            Hud.Toast($"field of view read as {(Render.FieldOfViewIsHorizontal ? "horizontal" : "vertical")}");
        }, "Projection test: field of view horizontal / vertical");
        Log.Info($"rendersample: ready (camera {(Render.CameraAvailable ? "found" : "not found on this build: no waypoints")}); " +
                 "Ctrl+J drops a waypoint, Ctrl+Shift+J removes the nearest, Ctrl+Alt+J hides all");
    }

    // ---- main thread (key handlers) ----

    private void OnTick(in TickInfo _) => CheckSession();

    private WaypointsState CheckSession()
    {
        var session = Client.ClientWorldSession;
        var state = Volatile.Read(ref _waypoints);
        if (state.Session != session)
        {
            state = new WaypointsState(session, []);
            Volatile.Write(ref _waypoints, state);
        }
        return state;
    }

    private void Mark()
    {
        var state = CheckSession();
        var cam = Render.Camera;
        if (!cam.Valid)
        {
            Hud.Toast("No camera yet: join or host a world first", ToastKind.Warning);
            return;
        }
        var feet = cam.Pivot;  // what the third-person camera looks at: about the character's head
        feet = feet with { Y = feet.Y - 1.6 };
        var wp = new Waypoint($"Waypoint {_next}", feet, Palette[(_next - 1) % Palette.Length]);
        _next++;
        Volatile.Write(ref _waypoints, state with { Points = state.Points.Add(wp) });
        Hud.Toast($"{wp.Name} at {wp.At}", ToastKind.Success, 2);
        Log.Info($"rendersample: {wp.Name} at {wp.At} (camera {cam.Position}, heading {cam.Heading:F0})");
    }

    private void Unmark()
    {
        var state = CheckSession();
        var cam = Render.Camera;
        if (state.Points.IsEmpty) return;
        var nearest = state.Points.MinBy(p => p.At.DistanceTo(cam.Position))!;
        Volatile.Write(ref _waypoints, state with { Points = state.Points.Remove(nearest) });
        Hud.Toast($"{nearest.Name} removed", ToastKind.Info, 2);
    }

    private void LogCamera()
    {
        var c = Render.Camera;
        var s = Render.Stats;
        Log.Info($"rendersample: camera valid {c.Valid}, at {c.Position}, rotation ({c.Rotation.X:F4}, {c.Rotation.Y:F4}, {c.Rotation.Z:F4}, " +
                 $"{c.Rotation.W:F4}), heading {c.Heading:F1}, pitch {c.Pitch:F1}, fovY {c.FovY * 180 / MathF.PI:F1} deg, aspect {c.Aspect:F3}, " +
                 $"near {c.Near:G3}, far {c.Far:G4}, distance {c.Distance:F2}, age {c.AgeMs} ms; frames {s.Frames}, handlers {s.Handlers}, " +
                 $"last {s.LastFrameMs:F3} ms, max {s.MaxFrameMs:F3} ms");
        Hud.Toast($"camera {c.Position}, heading {c.Heading:F0}", ToastKind.Info, 3);
    }

    // ---- render thread ----

    private void OnFrame(RenderFrameEvent e)
    {
        var state = Volatile.Read(ref _waypoints);
        if (!_visible) return;
        _frameMs += (e.DeltaSeconds * 1000 - _frameMs) * 0.1f;
        var s = e.Scale;
        e.Draw.FillRect(8 * s, 8 * s, 150 * s, 30 * s, new Color(0, 0, 0, 110), 4 * s);
        e.Draw.Text(14 * s, 12 * s, $"{e.Fps:F0} fps  {_frameMs:F1} ms", Color.White);
        if (!e.Camera.Valid) return;  // menu or loading: nothing in the world to point at
        Compass(e);
        if (state.Session == Client.ClientWorldSession) Waypoints(e, state.Points);
    }

    private static void Compass(RenderFrameEvent e)
    {
        var s = e.Scale;
        float w = 420 * s, h = 26 * s, cx = e.Width / 2, top = 10 * s;
        e.Draw.FillRect(cx - w / 2, top, cx + w / 2, top + h, new Color(0, 0, 0, 120), 4 * s);
        var heading = e.Camera.Heading;
        const double span = 120;  // degrees across the strip
        for (var deg = 0; deg < 360; deg += 15)
        {
            var d = ((deg - heading + 540) % 360) - 180;  // -180..180 from the view
            if (Math.Abs(d) > span / 2) continue;
            var x = cx + (float)(d / span) * w;
            var label = deg switch { 0 => "N", 90 => "E", 180 => "S", 270 => "W", _ => null };
            if (label != null) e.Draw.Text(x, top + h / 2, label, deg == 0 ? Color.Red : Color.White, 18 * s, TextAlign.Center, middle: true);
            else e.Draw.Line(x, top + h * 0.6f, x, top + h - 3 * s, Color.Gray, 1.5f * s);
        }
        e.Draw.Triangle(new(cx - 5 * s, top + h + 6 * s), new(cx + 5 * s, top + h + 6 * s), new(cx, top + h), Color.Orange, 1.5f * s);
    }

    private void Waypoints(RenderFrameEvent e, ImmutableArray<Waypoint> points)
    {
        var s = e.Scale;
        foreach (var p in points)
        {
            var dist = e.World.DistanceTo(p.At);
            e.World.Circle(p.At, 1.5, p.Color.WithAlpha(200), 2 * s);
            var top = p.At with { Y = p.At.Y + 2.5 };
            e.World.Line(p.At, top, p.Color.WithAlpha(160), 2 * s);
            if (e.World.ToScreen(top, out var pt) && pt.X >= 0 && pt.X <= e.Width && pt.Y >= 0 && pt.Y <= e.Height)
            {
                if (_pin != null)
                {
                    float pw = 24 * s, ph = 32 * s;  // the pin's point at the spot
                    e.Draw.Image(_pin, pt.X - pw / 2, pt.Y - ph, pt.X + pw / 2, pt.Y, p.Color);
                }
                else e.World.Marker(top, p.Color, 8, MarkerShape.Triangle);
                e.Draw.Text(pt.X, pt.Y - 38 * s, $"{p.Name}  {dist:F0} m", p.Color, 0, TextAlign.Center, TextStyle.Outline, middle: true);
            }
            else
            {
                e.World.ToScreenClamped(top, 40 * s, out var edge, out var angle);
                e.Draw.Arrow(edge + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 14 * s, angle, 16 * s, p.Color);
                e.Draw.Text(edge.X, edge.Y, $"{dist:F0} m", p.Color, 0, TextAlign.Center, TextStyle.Outline, middle: true);
            }
        }
    }
}
