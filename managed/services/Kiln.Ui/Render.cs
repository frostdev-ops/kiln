// SPDX-License-Identifier: MIT
// Kiln.Ui render events: code every presented frame, 2D drawing in pixels and drawing at world positions with the game's
// camera. Native side: kiln.render (native/core/abi/services/kiln_render.h). Usage: docs/kiln/render.md.
using System.Numerics;

namespace Kiln;

/// <summary>
/// Posted on the Kiln event bus once per presented frame of the game client, while anything subscribes to it
/// (Forge: <c>RenderGuiEvent</c> / <c>RenderLevelStageEvent</c>). Draw through <see cref="Draw"/> (pixels) and
/// <see cref="World"/> (world positions) during the handler.
/// </summary>
/// <remarks>
/// <para><b>Thread:</b> the game's render thread (the thread presenting frames), not the tick thread. Read only what your
/// mod keeps itself (copies, snapshots); act on the game with <c>Client.Post</c> (client world) or
/// <c>Context.Scheduler.Post</c> (server world); post bus events with <c>Events.Enqueue</c>.</para>
/// <para><b>Budget:</b> each handler gets <see cref="Render.HandlerBudgetMs"/> per frame. A handler that runs over it in
/// most frames, stalls a frame for over 100 ms three times, or throws five times is turned off (logged, and a toast says
/// which mod). Hard stalls count during warm-up and on faults. Nothing can interrupt a handler that is running, so keep it short.</para>
/// <para>The event object, <see cref="Draw"/> and <see cref="World"/> are reused every frame: don't keep them.</para>
/// <para>Never posted on a dedicated server. Subscribing there is harmless.</para>
/// </remarks>
public sealed class RenderFrameEvent : KilnEvent
{
    internal override bool TickDispatchAllowed => false;

    // Runs when the first handler subscribes (the bus runs an event class's static constructor then).
    static RenderFrameEvent() => RenderDispatch.Start();

    internal RenderFrameEvent()
    {
        Draw = new RenderDraw();
        World = new WorldDraw(this);
    }

    /// <summary>Overlay frames since the game started.</summary>
    public ulong Frame { get; internal set; }

    /// <summary>Seconds since the previous frame (at most 0.25).</summary>
    public float DeltaSeconds { get; internal set; }

    /// <summary>Frames per second, smoothed over about half a second.</summary>
    public float Fps { get; internal set; }

    /// <summary>Screen size in pixels.</summary>
    public float Width { get; internal set; }

    public float Height { get; internal set; }

    /// <summary>UI scale (DPI); multiply your own pixel sizes by it.</summary>
    public float Scale { get; internal set; }

    /// <summary>The Kiln menu is open.</summary>
    public bool MenuOpen { get; internal set; }

    /// <summary>A Kiln screen or window has the mouse and keyboard.</summary>
    public bool Capturing { get; internal set; }

    /// <summary>The camera the game renders with this frame (<see cref="RenderCamera.Valid"/> expires 500 ms after the last write,
    /// or is false when the camera is not found on this game build).</summary>
    public RenderCamera Camera { get; internal set; }

    /// <summary>The game's screen (main menu, in game, map, ...).</summary>
    public GameScreen Screen => Ui.Screens.Current;

    /// <summary>Drawing in pixels, under Kiln's windows and over the game's picture.</summary>
    public RenderDraw Draw { get; }

    /// <summary>Drawing at world positions with <see cref="Camera"/>.</summary>
    public WorldDraw World { get; }
}

/// <summary>The game's camera: where it is, where it looks, its lens.</summary>
/// <remarks>World metres, x east, y up, z north. Camera space: x right, y up, z forward. Read where the client writes the
/// local player's camera each frame; camera shake is not included.</remarks>
public readonly struct RenderCamera
{
    internal RenderCamera(in RenderNative.Camera c)
    {
        Valid = c.Valid != 0 && RenderMath.ValidLens(c.FovY) && RenderMath.ValidRotation(new Quaternion(c.Qx, c.Qy, c.Qz, c.Qw));
        Position = new WorldPosition(c.X, c.Y, c.Z);
        Rotation = Valid ? Quaternion.Normalize(new Quaternion(c.Qx, c.Qy, c.Qz, c.Qw)) : Quaternion.Identity;
        FovY = c.FovY > 0 ? c.FovY : MathF.PI / 3;
        Aspect = c.Aspect;
        Near = c.Near;
        Far = c.Far;
        Distance = c.Distance;
        Frame = c.Frame;
        AgeMs = c.AgeMs;
    }

    /// <summary>A camera for tests and tools.</summary>
    public RenderCamera(WorldPosition position, Quaternion rotation, float fovY, float near = 0.1f, float distance = 0)
    {
        Valid = double.IsFinite(position.X) && double.IsFinite(position.Y) && double.IsFinite(position.Z) &&
            RenderMath.ValidLens(fovY) && RenderMath.ValidRotation(rotation);
        Position = position;
        Rotation = Valid ? Quaternion.Normalize(rotation) : Quaternion.Identity;
        FovY = fovY;
        Near = near;
        Far = 10000;
        Distance = distance;
        Aspect = 0;
        Frame = 0;
        AgeMs = 0;
    }

    /// <summary>A camera was written in the last half second (in game).</summary>
    public bool Valid { get; }

    public WorldPosition Position { get; }

    /// <summary>Turns camera space into world space.</summary>
    public Quaternion Rotation { get; }

    /// <summary>Vertical field of view in radians.</summary>
    public float FovY { get; }

    /// <summary>The game's own aspect value (projection uses the screen's width / height).</summary>
    public float Aspect { get; }

    public float Near { get; }
    public float Far { get; }

    /// <summary>Distance of the third-person camera from what it looks at (the character), metres.</summary>
    public float Distance { get; }

    /// <summary>Camera writes since the game started (one per client frame).</summary>
    public ulong Frame { get; }

    public uint AgeMs { get; }

    /// <summary>The view direction (unit vector, world space).</summary>
    public Vector3 Forward => (Vector3)RenderMath.Rotate(Rotation, 0, 0, 1);

    public Vector3 Right => (Vector3)RenderMath.Rotate(Rotation, 1, 0, 0);
    public Vector3 Up => (Vector3)RenderMath.Rotate(Rotation, 0, 1, 0);

    /// <summary>Compass heading of the view in degrees: 0 north (+z), 90 east (+x), 180 south, 270 west.</summary>
    public double Heading
    {
        get
        {
            var f = Forward;
            var h = Math.Atan2(f.X, f.Z) * 180 / Math.PI;
            return h < 0 ? h + 360 : h;
        }
    }

    /// <summary>Degrees above the horizon (negative: looking down).</summary>
    public double Pitch => Math.Asin(Math.Clamp(Forward.Y, -1f, 1f)) * 180 / Math.PI;

    /// <summary>What the camera looks at: <see cref="Position"/> + <see cref="Forward"/> x <see cref="Distance"/> (about
    /// the character's head in third person).</summary>
    public WorldPosition Pivot
    {
        get
        {
            var f = Forward;
            return new WorldPosition(Position.X + f.X * Distance, Position.Y + f.Y * Distance, Position.Z + f.Z * Distance);
        }
    }

    /// <summary>A world point in camera space (x right, y up, z forward; metres).</summary>
    public (double X, double Y, double Z) ToCameraSpace(WorldPosition p)
    {
        var inv = new Quaternion(-Rotation.X, -Rotation.Y, -Rotation.Z, Rotation.W);
        var v = RenderMath.Rotate(inv, p.X - Position.X, p.Y - Position.Y, p.Z - Position.Z);
        return (v.X, v.Y, v.Z);
    }

    /// <summary>Pixels of a world point on a <paramref name="width"/> x <paramref name="height"/> screen. False when it is
    /// behind the camera (do not draw it then; <see cref="WorldDraw.ToScreenClamped"/> gives an edge position).</summary>
    public bool Project(WorldPosition p, float width, float height, out Vector2 screen, out float depth)
    {
        var (x, y, z) = ToCameraSpace(p);
        return RenderMath.ProjectCamera(this, x, y, z, width, height, out screen, out depth);
    }
}

/// <summary>The projection kiln.render uses (also native/services/ui/render_math.h).</summary>
internal static class RenderMath
{
    public readonly record struct D3(double X, double Y, double Z)
    {
        public static explicit operator Vector3(D3 d) => new((float)d.X, (float)d.Y, (float)d.Z);
    }

    public static D3 Rotate(Quaternion q, double vx, double vy, double vz)
    {
        double x = q.X, y = q.Y, z = q.Z, w = q.W;
        var tx = 2.0 * (y * vz - z * vy);
        var ty = 2.0 * (z * vx - x * vz);
        var tz = 2.0 * (x * vy - y * vx);
        return new D3(vx + w * tx + (y * tz - z * ty), vy + w * ty + (z * tx - x * tz), vz + w * tz + (x * ty - y * tx));
    }

    public static bool ValidLens(float fov) => float.IsFinite(fov) && fov > 0 && fov < MathF.PI;
    public static bool ValidRotation(Quaternion q) => float.IsFinite(q.LengthSquared()) && q.LengthSquared() > 1e-12f;

    public static float NearPlane(in RenderCamera c) => float.IsFinite(c.Near) && c.Near > 0.01f ? c.Near : 0.05f;

    public static bool ProjectCamera(in RenderCamera c, double x, double y, double z, float width, float height, out Vector2 screen,
        out float depth)
    {
        screen = default;
        depth = 0;
        if (!c.Valid || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) ||
            !float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0) return false;
        var aspect = (double)width / height;
        var t = Math.Tan(c.FovY * 0.5);
        double tx, ty;
        if (Render.FieldOfViewIsHorizontal)
        {
            tx = t;
            ty = t / aspect;
        }
        else
        {
            tx = t * aspect;
            ty = t;
        }
        var front = z > NearPlane(c);
        var zz = Math.Abs(z) < 1e-6 ? (z < 0 ? -1e-6 : 1e-6) : z;
        var sx = (x / (zz * tx) * 0.5 + 0.5) * width;
        var sy = (0.5 - y / (zz * ty) * 0.5) * height;
        screen = new Vector2(Clamp(sx), Clamp(sy));
        depth = (float)z;
        return front;
    }

    // Clip in camera space before perspective division, preserving lines whose endpoints are far off screen.
    internal static bool ClipLine(in RenderCamera c, (double X, double Y, double Z) a, (double X, double Y, double Z) b,
        float width, float height, out Vector2 sa, out Vector2 sb)
    {
        sa = sb = default;
        if (!c.Valid || width <= 0 || height <= 0 || !float.IsFinite(width) || !float.IsFinite(height) ||
            !double.IsFinite(a.X) || !double.IsFinite(a.Y) || !double.IsFinite(a.Z) ||
            !double.IsFinite(b.X) || !double.IsFinite(b.Y) || !double.IsFinite(b.Z)) return false;
        var t = Math.Tan(c.FovY / 2.0);
        var aspect = (double)width / height;
        var horizontal = Render.FieldOfViewIsHorizontal;
        var tx = horizontal ? t : t * aspect;
        var ty = horizontal ? t / aspect : t;
        var near = NearPlane(c) + 1e-6;
        // Sequential plane clipping also avoids subtracting enormous screen coordinates after projection.
        if (!Plane(ref a, ref b, a.Z - near, b.Z - near) ||
            !Plane(ref a, ref b, a.X + a.Z * tx, b.X + b.Z * tx) ||
            !Plane(ref a, ref b, a.Z * tx - a.X, b.Z * tx - b.X) ||
            !Plane(ref a, ref b, a.Y + a.Z * ty, b.Y + b.Z * ty) ||
            !Plane(ref a, ref b, a.Z * ty - a.Y, b.Z * ty - b.Y)) return false;
        return ProjectCamera(c, a.X, a.Y, a.Z, width, height, out sa, out _) &&
            ProjectCamera(c, b.X, b.Y, b.Z, width, height, out sb, out _);
    }

    private static bool Plane(ref (double X, double Y, double Z) a, ref (double X, double Y, double Z) b, double da, double db)
    {
        if (!double.IsFinite(da) || !double.IsFinite(db) || (da < 0 && db < 0)) return false;
        if ((da < 0) == (db < 0)) return true;
        // Scale the distances before dividing so da-db cannot overflow.
        var scale = Math.Max(Math.Abs(da), Math.Abs(db));
        var t = (da / scale) / (da / scale - db / scale);
        var cut = (a.X * (1 - t) + b.X * t, a.Y * (1 - t) + b.Y * t, a.Z * (1 - t) + b.Z * t);
        if (da < 0) a = cut;
        else b = cut;
        return true;
    }

    private static float Clamp(double v) => double.IsFinite(v) ? (float)Math.Clamp(v, -1e7, 1e7) : (v < 0 ? -1e7f : 1e7f);
}

/// <summary>Text alignment for <see cref="RenderDraw.Text"/>.</summary>
public enum TextAlign
{
    Left, Center, Right,
}

/// <summary>Text look for <see cref="RenderDraw.Text"/>.</summary>
public enum TextStyle
{
    Plain, Shadow, Outline,
}

/// <summary>Marker shapes for <see cref="WorldDraw.Marker"/>.</summary>
public enum MarkerShape
{
    Diamond, Circle, Square, Triangle, Cross,
}

/// <summary>Immediate-mode drawing in pixels (origin top left, y down) for a <see cref="RenderFrameEvent"/> handler. Drawn
/// under every Kiln window, screen, HUD line and toast, over the game's picture. Only valid during the handler.</summary>
public sealed unsafe class RenderDraw
{
    internal RenderDraw() { }

    private int _thread;
    internal bool Active => Volatile.Read(ref _thread) == Environment.CurrentManagedThreadId;
    internal void Begin() => Volatile.Write(ref _thread, Environment.CurrentManagedThreadId);
    internal void End() => Volatile.Write(ref _thread, 0);
    private RenderNative.Table* T => Active ? RenderNative.T : null;

    public void Line(float x0, float y0, float x1, float y1, Color color, float thickness = 1)
    {
        if (T != null) T->Line(x0, y0, x1, y1, color.Rgba, thickness);
    }

    public void Line(Vector2 a, Vector2 b, Color color, float thickness = 1) => Line(a.X, a.Y, b.X, b.Y, color, thickness);

    /// <summary>A rectangle outline from (x0, y0) to (x1, y1).</summary>
    public void Rect(float x0, float y0, float x1, float y1, Color color, float thickness = 1, float rounding = 0)
    {
        if (T != null) T->Rect(x0, y0, x1, y1, color.Rgba, rounding, thickness, 0);
    }

    public void FillRect(float x0, float y0, float x1, float y1, Color color, float rounding = 0)
    {
        if (T != null) T->Rect(x0, y0, x1, y1, color.Rgba, rounding, 1, 1);
    }

    /// <summary>A circle outline; <paramref name="segments"/> 0 = automatic.</summary>
    public void Circle(float x, float y, float radius, Color color, float thickness = 1, int segments = 0)
    {
        if (T != null) T->Circle(x, y, radius, color.Rgba, segments, thickness, 0);
    }

    public void FillCircle(float x, float y, float radius, Color color, int segments = 0)
    {
        if (T != null) T->Circle(x, y, radius, color.Rgba, segments, 1, 1);
    }

    public void Triangle(Vector2 a, Vector2 b, Vector2 c, Color color, float thickness = 1)
    {
        if (T != null) T->Triangle(a.X, a.Y, b.X, b.Y, c.X, c.Y, color.Rgba, thickness, 0);
    }

    public void FillTriangle(Vector2 a, Vector2 b, Vector2 c, Color color)
    {
        if (T != null) T->Triangle(a.X, a.Y, b.X, b.Y, c.X, c.Y, color.Rgba, 1, 1);
    }

    /// <summary>Connected lines through <paramref name="points"/> (2..4096).</summary>
    public void Polyline(ReadOnlySpan<Vector2> points, Color color, float thickness = 1, bool closed = false)
    {
        if (T == null || points.Length < 2) return;
        fixed (Vector2* p = points) T->Polyline((float*)p, (uint)points.Length, color.Rgba, thickness, closed ? 1 : 0, 0);
    }

    /// <summary>A filled convex polygon (3..4096 points, clockwise on screen).</summary>
    public void FillPolygon(ReadOnlySpan<Vector2> points, Color color)
    {
        if (T == null || points.Length < 3) return;
        fixed (Vector2* p = points) T->Polyline((float*)p, (uint)points.Length, color.Rgba, 1, 1, 1);
    }

    /// <summary>A filled arrow head at <paramref name="tip"/> pointing along <paramref name="angle"/> (radians, 0 = right,
    /// pi/2 = down), <paramref name="size"/> pixels long.</summary>
    public void Arrow(Vector2 tip, float angle, float size, Color color)
    {
        var d = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        var n = new Vector2(-d.Y, d.X);
        var back = tip - d * size;
        FillTriangle(tip, back + n * (size * 0.5f), back - n * (size * 0.5f), color);
    }

    /// <summary>Text at (x, y): the top left corner, the top centre (<see cref="TextAlign.Center"/>) or the top right.
    /// <paramref name="size"/> 0 = the overlay's font size; <paramref name="middle"/> centres it vertically on y.</summary>
    public void Text(float x, float y, string text, Color? color = null, float size = 0, TextAlign align = TextAlign.Left,
        TextStyle style = TextStyle.Shadow, bool middle = false)
    {
        if (T == null || string.IsNullOrEmpty(text)) return;
        uint flags = style switch { TextStyle.Shadow => 1u, TextStyle.Outline => 2u, _ => 0u };
        flags |= align switch { TextAlign.Center => 4u, TextAlign.Right => 16u, _ => 0u };
        if (middle) flags |= 8u;
        T->Text(x, y, (color ?? Color.White).Rgba, size, flags, Utf8.Of(text));
    }

    public (float Width, float Height) MeasureText(string text, float size = 0)
    {
        if (T == null) return (0, 0);
        float w, h;
        T->TextSize(Utf8.Of(text), size, &w, &h);
        return (w, h);
    }

    /// <summary>An image (<see cref="Ui.LoadImage(string)"/>, a PNG in your mod) stretched over the rectangle. It shows
    /// from the frame after it was loaded.</summary>
    public void Image(UiImage image, float x0, float y0, float x1, float y1, Color? tint = null)
    {
        if (T == null || image.Handle == 0) return;
        T->Image(image.Handle, x0, y0, x1, y1, 0, 0, 1, 1, (tint ?? Color.White).Rgba);
    }

    /// <summary>An image centred on (x, y), <paramref name="width"/> x <paramref name="height"/> pixels (0 = its own
    /// size).</summary>
    public void ImageCentered(UiImage image, float x, float y, float width = 0, float height = 0, Color? tint = null)
    {
        var w = width > 0 ? width : image.Width;
        var h = height > 0 ? height : image.Height;
        Image(image, x - w / 2, y - h / 2, x + w / 2, y + h / 2, tint);
    }
}

/// <summary>Drawing at world positions (metres) with the frame's camera. Nothing is drawn while the camera is not valid,
/// and points behind the camera are skipped (lines and boxes are cut at the near plane).</summary>
public sealed class WorldDraw
{
    private readonly RenderFrameEvent _frame;

    internal WorldDraw(RenderFrameEvent frame) => _frame = frame;

    private RenderDraw D => _frame.Draw;
    private RenderCamera Cam => _frame.Camera;

    /// <summary>The camera is known this frame.</summary>
    public bool Ready => _frame.Camera.Valid;

    /// <summary>Metres from the camera.</summary>
    public double DistanceTo(WorldPosition p) => Cam.Position.DistanceTo(p);

    /// <summary>Pixels of <paramref name="p"/>; false when it is behind the camera or there is no camera.</summary>
    public bool ToScreen(WorldPosition p, out Vector2 screen)
    {
        screen = default;
        return Ready && Cam.Project(p, _frame.Width, _frame.Height, out screen, out _);
    }

    /// <summary>Like <see cref="ToScreen"/>, but a point off screen or behind the camera is placed on the screen's edge
    /// (inset by <paramref name="margin"/> pixels) in its direction. Returns true when the point is on screen;
    /// <paramref name="angle"/> is the direction from the screen's centre (radians, 0 = right, pi/2 = down), for an
    /// arrow.</summary>
    public bool ToScreenClamped(WorldPosition p, float margin, out Vector2 screen, out float angle)
    {
        screen = new Vector2(_frame.Width / 2, _frame.Height / 2);
        angle = MathF.PI / 2;
        if (!Ready || !float.IsFinite(margin) || !float.IsFinite(_frame.Width) || !float.IsFinite(_frame.Height) ||
            _frame.Width <= 0 || _frame.Height <= 0) return false;
        var (x, y, z) = Cam.ToCameraSpace(p);
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return false;
        var front = RenderMath.ProjectCamera(Cam, x, y, z, _frame.Width, _frame.Height, out var s, out _);
        var c = new Vector2(_frame.Width / 2, _frame.Height / 2);
        margin = Math.Clamp(margin, 0, MathF.Min(c.X, c.Y));
        var half = new Vector2(c.X - margin, c.Y - margin);
        if (front && MathF.Abs(s.X - c.X) <= half.X && MathF.Abs(s.Y - c.Y) <= half.Y)
        {
            screen = s;
            var d0 = s - c;
            angle = d0.LengthSquared() > 1e-6f ? MathF.Atan2(d0.Y, d0.X) : MathF.PI / 2;
            return true;
        }
        // direction in pixel space (y down); behind the camera the camera-space x/y still say which side it lies on
        var length = Math.Max(Math.Abs(x), Math.Abs(y));
        var d = length > 1e-12 ? new Vector2((float)(x / length), (float)(-y / length)) : new Vector2(0, 1);
        var k = MathF.Min(half.X / MathF.Max(MathF.Abs(d.X), 1e-6f), half.Y / MathF.Max(MathF.Abs(d.Y), 1e-6f));
        screen = c + d * k;
        angle = MathF.Atan2(d.Y, d.X);
        return false;
    }

    /// <summary>A marker of <paramref name="size"/> pixels (times the UI scale) at <paramref name="p"/>.</summary>
    public bool Marker(WorldPosition p, Color color, float size = 10, MarkerShape shape = MarkerShape.Diamond, bool outline = true)
    {
        if (!ToScreen(p, out var s)) return false;
        DrawShape(s, color, size * _frame.Scale, shape, outline);
        return true;
    }

    internal void DrawShape(Vector2 s, Color color, float r, MarkerShape shape, bool outline)
    {
        var dark = new Color(0, 0, 0, (byte)(color.A * 3 / 4));
        switch (shape)
        {
            case MarkerShape.Circle:
                if (outline) D.FillCircle(s.X, s.Y, r + 1.5f, dark);
                D.FillCircle(s.X, s.Y, r, color);
                break;
            case MarkerShape.Square:
                if (outline) D.FillRect(s.X - r - 1.5f, s.Y - r - 1.5f, s.X + r + 1.5f, s.Y + r + 1.5f, dark);
                D.FillRect(s.X - r, s.Y - r, s.X + r, s.Y + r, color);
                break;
            case MarkerShape.Triangle:  // pointing down at the spot
            {
                var o = outline ? 2f : 0f;
                if (outline) D.FillTriangle(new(s.X - r - o, s.Y - 2 * r - o), new(s.X + r + o, s.Y - 2 * r - o), new(s.X, s.Y + o), dark);
                D.FillTriangle(new(s.X - r, s.Y - 2 * r), new(s.X + r, s.Y - 2 * r), new(s.X, s.Y), color);
                break;
            }
            case MarkerShape.Cross:
                if (outline)
                {
                    D.Line(s.X - r, s.Y - r, s.X + r, s.Y + r, dark, 4);
                    D.Line(s.X - r, s.Y + r, s.X + r, s.Y - r, dark, 4);
                }
                D.Line(s.X - r, s.Y - r, s.X + r, s.Y + r, color, 2);
                D.Line(s.X - r, s.Y + r, s.X + r, s.Y - r, color, 2);
                break;
            default:
            {
                Span<Vector2> q = [new(s.X, s.Y - r), new(s.X + r, s.Y), new(s.X, s.Y + r), new(s.X - r, s.Y)];
                if (outline)
                {
                    var o = r + 1.5f;
                    Span<Vector2> qo = [new(s.X, s.Y - o), new(s.X + o, s.Y), new(s.X, s.Y + o), new(s.X - o, s.Y)];
                    D.FillPolygon(qo, dark);
                }
                D.FillPolygon(q, color);
                break;
            }
        }
    }

    /// <summary>Text centred above <paramref name="p"/>, raised by <paramref name="offsetY"/> pixels (times the UI scale).</summary>
    public bool Label(WorldPosition p, string text, Color? color = null, float size = 0, float offsetY = 14,
        TextStyle style = TextStyle.Outline)
    {
        if (!ToScreen(p, out var s)) return false;
        D.Text(s.X, s.Y - offsetY * _frame.Scale, text, color, size, TextAlign.Center, style, middle: true);
        return true;
    }

    /// <summary>An image centred on <paramref name="p"/>, <paramref name="size"/> pixels wide (times the UI scale; the
    /// height follows the image).</summary>
    public bool Icon(WorldPosition p, UiImage image, float size = 32, Color? tint = null)
    {
        if (!ToScreen(p, out var s) || image.Width <= 0) return false;
        var w = size * _frame.Scale;
        var h = w * image.Height / image.Width;
        D.Image(image, s.X - w / 2, s.Y - h / 2, s.X + w / 2, s.Y + h / 2, tint);
        return true;
    }

    /// <summary>A line between two world points, cut where it passes behind the camera.</summary>
    public void Line(WorldPosition a, WorldPosition b, Color color, float thickness = 2)
    {
        if (!Ready) return;
        var va = Cam.ToCameraSpace(a);
        var vb = Cam.ToCameraSpace(b);
        CameraLine(va, vb, color, thickness);
    }

    private void CameraLine((double X, double Y, double Z) a, (double X, double Y, double Z) b, Color color, float thickness)
    {
        if (RenderMath.ClipLine(Cam, a, b, _frame.Width, _frame.Height, out var sa, out var sb))
            D.Line(sa, sb, color, thickness);
    }

    /// <summary>The edges of an axis-aligned box between two corners.</summary>
    public void Box(WorldPosition min, WorldPosition max, Color color, float thickness = 2)
    {
        if (!Ready) return;
        Span<(double X, double Y, double Z)> c = stackalloc (double, double, double)[8];
        for (var i = 0; i < 8; i++)
            c[i] = Cam.ToCameraSpace(new WorldPosition((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z));
        ReadOnlySpan<int> edges = [0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7];
        for (var e = 0; e < edges.Length; e += 2) CameraLine(c[edges[e]], c[edges[e + 1]], color, thickness);
    }

    /// <summary>A box of <paramref name="size"/> metres centred on <paramref name="center"/>.</summary>
    public void Box(WorldPosition center, Vector3 size, Color color, float thickness = 2) =>
        Box(new WorldPosition(center.X - size.X / 2, center.Y - size.Y / 2, center.Z - size.Z / 2),
            new WorldPosition(center.X + size.X / 2, center.Y + size.Y / 2, center.Z + size.Z / 2), color, thickness);

    /// <summary>A level ring of <paramref name="radius"/> metres around <paramref name="center"/> (on the ground at its
    /// height).</summary>
    public void Circle(WorldPosition center, double radius, Color color, float thickness = 2, int segments = 48)
    {
        if (!Ready || radius <= 0) return;
        segments = Math.Clamp(segments, 8, 256);
        var prev = Cam.ToCameraSpace(new WorldPosition(center.X + radius, center.Y, center.Z));
        for (var i = 1; i <= segments; i++)
        {
            var a = i * 2 * Math.PI / segments;
            var cur = Cam.ToCameraSpace(new WorldPosition(center.X + Math.Cos(a) * radius, center.Y, center.Z + Math.Sin(a) * radius));
            CameraLine(prev, cur, color, thickness);
            prev = cur;
        }
    }
}

/// <summary>Render events, the game's camera and frame diagnostics (client).</summary>
public static class Render
{
    /// <summary>Render events and drawing work in this process (a game client with Kiln's overlay).</summary>
    public static bool Available => RenderNative.Available && UiNative.Available;

    /// <summary>The game's camera is found on this build (world drawing works).</summary>
    public static bool CameraAvailable => RenderNative.ReadStatus() is { Camera: 1 };

    /// <summary>The latest camera. Any thread; inside a <see cref="RenderFrameEvent"/> use its <c>Camera</c>, which is the
    /// same for the whole frame.</summary>
    public static RenderCamera Camera => new(RenderNative.ReadCamera());

    private static double _budgetMs = 2.0;

    /// <summary>Milliseconds a <see cref="RenderFrameEvent"/> handler may take per frame (0.1 to 100, default 2). Over it in
    /// most of the last 120 frames turns the handler off.</summary>
    public static double HandlerBudgetMs
    {
        get => Volatile.Read(ref _budgetMs);
        set
        {
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "The render budget must be finite");
            Volatile.Write(ref _budgetMs, Math.Clamp(value, 0.1, 100));
        }
    }

    private static bool _horizontal;

    /// <summary>Calibration: read the camera's field of view as horizontal instead of vertical. For checking the
    /// projection on a game build where markers drift towards the screen's edges; leave it off otherwise.</summary>
    public static bool FieldOfViewIsHorizontal { get => Volatile.Read(ref _horizontal); set => Volatile.Write(ref _horizontal, value); }

    /// <summary>Frame diagnostics.</summary>
    public static RenderStats Stats => RenderDispatch.Stats();
}

/// <summary><see cref="Render.Stats"/>: handlers, frames posted, the time all handlers took (last frame and the slowest),
/// handlers turned off, and the camera's state.</summary>
public readonly record struct RenderStats(int Handlers, long Frames, double LastFrameMs, double MaxFrameMs, int Disabled,
    bool Camera, bool CameraValid, long CameraFrames);
