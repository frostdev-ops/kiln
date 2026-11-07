// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Numerics;
using Xunit;

namespace Kiln.Ui.Tests;

public class RenderProjectionTests
{
    private const float W = 1920, H = 1080;
    private static readonly float Fov = MathF.PI / 3;  // 60 degrees vertical

    private static RenderCamera At(double x, double y, double z, Quaternion q) => new(new WorldPosition(x, y, z), q, Fov);

    [Fact]
    public void AheadIsTheCentre()
    {
        var c = At(0, 0, 0, Quaternion.Identity);
        Assert.True(c.Project(new WorldPosition(0, 0, 10), W, H, out var s, out var depth));
        Assert.Equal(W / 2, s.X, 0.01f);
        Assert.Equal(H / 2, s.Y, 0.01f);
        Assert.Equal(10, depth, 0.001f);
    }

    [Fact]
    public void XIsRightAndYIsUp()
    {
        var c = At(0, 0, 0, Quaternion.Identity);
        Assert.True(c.Project(new WorldPosition(1, 0, 10), W, H, out var r, out _));
        Assert.True(r.X > W / 2);
        Assert.True(c.Project(new WorldPosition(0, 1, 10), W, H, out var u, out _));
        Assert.True(u.Y < H / 2);
    }

    [Fact]
    public void BehindIsNotDrawable()
    {
        var c = At(0, 0, 0, Quaternion.Identity);
        Assert.False(c.Project(new WorldPosition(0, 0, -5), W, H, out _, out _));
        Assert.False(c.Project(new WorldPosition(0, 0, 0), W, H, out var s, out _));
        Assert.True(float.IsFinite(s.X) && float.IsFinite(s.Y));
    }

    [Fact]
    public void VerticalFieldOfViewReachesTheEdges()
    {
        var c = At(0, 0, 0, Quaternion.Identity);
        var t = Math.Tan(Fov / 2) * 10;
        Assert.True(c.Project(new WorldPosition(0, t, 10), W, H, out var top, out _));
        Assert.Equal(0, top.Y, 0.05f);
        Assert.True(c.Project(new WorldPosition(t * W / H, 0, 10), W, H, out var right, out _));
        Assert.Equal(W, right.X, 0.05f);
    }

    [Fact]
    public void YawNinetyLooksEast()
    {
        var c = At(0, 0, 0, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2));
        Assert.Equal(1, c.Forward.X, 1e-5f);
        Assert.Equal(90, c.Heading, 1e-3);
        Assert.True(c.Project(new WorldPosition(10, 0, 0), W, H, out var s, out _));
        Assert.Equal(W / 2, s.X, 0.01f);
        Assert.True(c.Project(new WorldPosition(10, 0, 2), W, H, out var nl, out _));
        Assert.True(nl.X < W / 2);  // north of the view line is to the left when looking east
    }

    [Fact]
    public void HeadingAndPitch()
    {
        Assert.Equal(0, At(0, 0, 0, Quaternion.Identity).Heading, 1e-6);
        Assert.Equal(270, At(0, 0, 0, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2)).Heading, 1e-3);
        var down = At(0, 0, 0, Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 4));
        Assert.Equal(-45, down.Pitch, 1e-3);
        Assert.True(down.Project(new WorldPosition(0, -10, 10), W, H, out var s, out _));
        Assert.Equal(H / 2, s.Y, 0.01f);
    }

    [Fact]
    public void PivotIsAheadByTheDistance()
    {
        var c = new RenderCamera(new WorldPosition(100, 50, 200), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2), Fov, 0.1f, 4);
        var p = c.Pivot;
        Assert.Equal(104, p.X, 1e-4);
        Assert.Equal(50, p.Y, 1e-4);
        Assert.Equal(200, p.Z, 1e-4);
    }

    [Fact]
    public void FarFromTheOriginKeepsPrecision()
    {
        var c = At(8123.25, 850.5, 6021.75, Quaternion.Identity);
        Assert.True(c.Project(new WorldPosition(8123.75, 850.5, 6023.75), W, H, out var s, out _));
        var expected = (0.5 / (2 * Math.Tan(Math.PI / 6) * W / H) * 0.5 + 0.5) * W;
        Assert.Equal(expected, s.X, 0.01);
    }

    [Fact]
    public void ClampedPointsSitOnTheEdgeInTheirDirection()
    {
        var ev = new RenderFrameEvent { Width = W, Height = H, Scale = 1, Camera = At(0, 0, 0, Quaternion.Identity) };
        Assert.True(ev.World.ToScreenClamped(new WorldPosition(0, 0, 10), 20, out var on, out _));
        Assert.Equal(W / 2, on.X, 0.01f);
        // behind and to the right: right edge, not mirrored to the left
        Assert.False(ev.World.ToScreenClamped(new WorldPosition(5, 0, -10), 20, out var r, out var angle));
        Assert.Equal(W - 20, r.X, 0.01f);
        Assert.Equal(0, angle, 1e-4f);
        // far to the left in front: left edge
        Assert.False(ev.World.ToScreenClamped(new WorldPosition(-100, 0, 1), 20, out var l, out _));
        Assert.Equal(20, l.X, 0.01f);
        // straight behind: bottom edge
        Assert.False(ev.World.ToScreenClamped(new WorldPosition(0, 0, -10), 20, out var b, out _));
        Assert.Equal(H - 20, b.Y, 0.01f);
    }

    [Fact]
    public void InvalidProjectionInputsAreNotDrawable()
    {
        var c = At(0, 0, 0, Quaternion.Identity);
        Assert.False(default(RenderCamera).Project(new WorldPosition(0, 0, 10), W, H, out _, out _));
        Assert.False(c.Project(new WorldPosition(double.NaN, 0, 10), W, H, out _, out _));
        Assert.False(c.Project(new WorldPosition(0, 0, double.PositiveInfinity), W, H, out _, out _));
        Assert.False(c.Project(new WorldPosition(0, 0, 10), 0, H, out _, out _));
        Assert.False(new RenderCamera(default, Quaternion.Identity, float.NaN).Valid);
    }

    [Fact]
    public void ClampedDirectionDoesNotSaturateAtLargeCoordinates()
    {
        var ev = new RenderFrameEvent { Width = W, Height = H, Scale = 1, Camera = At(0, 0, 0, Quaternion.Identity) };
        Assert.False(ev.World.ToScreenClamped(new WorldPosition(1e100, 2e100, 1), 20, out var s, out var angle));
        Assert.Equal(MathF.Atan2(-2, 1), angle, 1e-5f);
        Assert.Equal(20, s.Y, 0.01f);
        Assert.True(float.IsFinite(s.X));
    }

    [Fact]
    public void LinesAreClippedToTheVisibleFrustum()
    {
        var c = At(0, 0, 0, Quaternion.Identity);
        Assert.True(RenderMath.ClipLine(c, (-1e8, 0, 10), (1e8, 0, 10), W, H, out var a, out var b));
        Assert.Equal(0, a.X, 0.1f);
        Assert.Equal(W, b.X, 0.1f);
        Assert.Equal(H / 2, a.Y, 0.1f);
        Assert.True(RenderMath.ClipLine(c, (0, 0, -10), (0, 0, 10), W, H, out a, out b));
        Assert.Equal(new Vector2(W / 2, H / 2), a);
        Assert.Equal(a, b);
        Assert.False(RenderMath.ClipLine(c, (0, 0, -10), (0, 0, -1), W, H, out _, out _));
        Assert.False(RenderMath.ClipLine(c, (100, 0, 1), (100, 5, 1), W, H, out _, out _));
        Assert.False(RenderMath.ClipLine(c, (double.NaN, 0, 10), (0, 0, 10), W, H, out _, out _));
    }

    [Fact]
    public void NonUnitRotationIsNormalized()
    {
        var unit = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var c = At(0, 0, 0, unit * 1.2f);
        Assert.True(c.Project(new WorldPosition(10, 0, 0), W, H, out var s, out _));
        Assert.Equal(W / 2, s.X, 0.01f);
        Assert.Equal(1, c.Forward.Length(), 1e-5f);
    }

    [Fact]
    public void NoCameraDrawsNothing()
    {
        var ev = new RenderFrameEvent { Width = W, Height = H, Scale = 1 };
        Assert.False(ev.World.Ready);
        Assert.False(ev.World.ToScreen(new WorldPosition(0, 0, 10), out _));
        Assert.False(ev.World.Marker(new WorldPosition(0, 0, 10), Color.Red));
    }
}

public class RenderBudgetTests
{
    [Fact]
    public void WarmUpIsFree()
    {
        var b = new RenderBudget();
        for (var i = 0; i < RenderBudget.WarmUp; i++) Assert.Null(b.Record(3, 2));
    }

    [Fact]
    public void HardStallsCountDuringWarmup()
    {
        var b = new RenderBudget();
        Assert.Null(b.Record(100, 2));
        Assert.Null(b.Record(100, 2));
        Assert.Contains("stalled", b.Record(100, 2));
    }

    [Fact]
    public void NanCannotDisableTheBudget()
    {
        var before = Render.HandlerBudgetMs;
        Assert.Throws<ArgumentOutOfRangeException>(() => Render.HandlerBudgetMs = double.NaN);
        Assert.Equal(before, Render.HandlerBudgetMs);
    }

    [Fact]
    public void MostlySlowGoesOff()
    {
        var b = new RenderBudget();
        for (var i = 0; i < RenderBudget.WarmUp; i++) b.Record(0.1, 2);
        string? why = null;
        var n = 0;
        while (why == null && n < 1000) { why = b.Record(3, 2); n++; }
        Assert.Equal(RenderBudget.SlowLimit + 1, n);
        Assert.Contains("budget", why);
    }

    [Fact]
    public void SometimesSlowStaysOn()
    {
        var b = new RenderBudget();
        for (var i = 0; i < 5000; i++) Assert.Null(b.Record(i % 3 == 0 ? 3 : 0.5, 2));  // a third of the frames over
    }

    [Fact]
    public void ThreeStallsGoOff()
    {
        var b = new RenderBudget();
        for (var i = 0; i < RenderBudget.WarmUp; i++) b.Record(0.1, 2);
        Assert.Null(b.Record(150, 2));
        for (var i = 0; i < 50; i++) Assert.Null(b.Record(0.1, 2));
        Assert.Null(b.Record(150, 2));
        Assert.Contains("stalled", b.Record(150, 2));
    }
}

[Collection("bus")]
public class RenderDispatchTests
{
    private static RenderFrameEvent NewFrame() => new() { Width = 1280, Height = 720, Scale = 1 };

    [Fact]
    public void HandlersRunInPriorityOrder()
    {
        var order = new List<string>();
        using var a = Events.On<RenderFrameEvent>(_ => order.Add("normal"));
        using var b = Events.On<RenderFrameEvent>(_ => order.Add("high"), EventPriority.High);
        RenderDispatch.Dispatch(NewFrame(), RenderDispatch.Handlers());
        Assert.Equal(["high", "normal"], order);
    }

    [Fact]
    public void CatchAllHandlersDoNotGetFrames()
    {
        var all = 0;
        using var s = Events.On<KilnEvent>(_ => all++);
        Assert.DoesNotContain(RenderDispatch.Handlers(), h => ReferenceEquals(h, s));
        RenderDispatch.Dispatch(NewFrame(), RenderDispatch.Handlers());
        Assert.Equal(0, all);
    }

    [Fact]
    public void DisposedHandlersStop()
    {
        var n = 0;
        var s = Events.On<RenderFrameEvent>(_ => n++);
        RenderDispatch.Dispatch(NewFrame(), RenderDispatch.Handlers());
        s.Dispose();
        Assert.DoesNotContain(RenderDispatch.Handlers(), h => ReferenceEquals(h, s));
        RenderDispatch.Dispatch(NewFrame(), RenderDispatch.Handlers());
        Assert.Equal(1, n);
    }

    [Fact]
    public void AThrowingHandlerGoesOffAfterFiveAndOthersRun()
    {
        var good = 0;
        using var bad = Events.On<RenderFrameEvent>(_ => throw new InvalidOperationException("boom"), EventPriority.High);
        using var ok = Events.On<RenderFrameEvent>(_ => good++);
        for (var i = 0; i < 8; i++) RenderDispatch.Dispatch(NewFrame(), RenderDispatch.Handlers());
        Assert.False(bad.Active);
        Assert.Equal(Events.MaxFaults, bad.Stats.Faults);
        Assert.Equal(8, good);
        Assert.True(ok.Active);
    }

    [Fact]
    public void ASlowHandlerGoesOff()
    {
        var saved = Render.HandlerBudgetMs;
        Render.HandlerBudgetMs = 0.2;
        try
        {
            using var slow = Events.On<RenderFrameEvent>(_ =>
            {
                var t = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(t).TotalMilliseconds < 1) { }
            });
            var frames = 0;
            while (slow.Active && frames < 500)
            {
                RenderDispatch.Dispatch(NewFrame(), RenderDispatch.Handlers());
                frames++;
            }
            Assert.False(slow.Active);
            Assert.Equal(RenderBudget.WarmUp + RenderBudget.SlowLimit + 1, frames);
            Assert.True(Render.Stats.Disabled >= 1);
        }
        finally
        {
            Render.HandlerBudgetMs = saved;
        }
    }

    [Fact]
    public void RetainedFramesCannotBeDispatchedOnTheTickBus()
    {
        var ev = NewFrame();
        Assert.Throws<InvalidOperationException>(() => Events.Post(ev));
        Assert.Throws<InvalidOperationException>(() => Events.Enqueue(ev));
    }

    [Fact]
    public void DisposedHandlersInAnOldSnapshotDoNotRun()
    {
        var calls = 0;
        using var s = Events.On<RenderFrameEvent>(_ => calls++);
        var snapshot = RenderDispatch.Handlers();
        s.Dispose();
        RenderDispatch.Dispatch(NewFrame(), snapshot);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ThrowingStallsAreAlsoBudgeted()
    {
        using var s = Events.On<RenderFrameEvent>(_ =>
        {
            var start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 101) { }
            throw new InvalidOperationException("slow failure");
        });
        for (var i = 0; i < 3; i++) RenderDispatch.Dispatch(NewFrame(), RenderDispatch.Handlers());
        Assert.False(s.Active);
    }

    [Fact]
    public void DrawingIsEnabledOnlyInsideTheHandlerOnItsThread()
    {
        var ev = NewFrame();
        Assert.False(ev.Draw.Active);
        bool activeInside = false, activeElsewhere = true;
        using var s = Events.On<RenderFrameEvent>(e =>
        {
            activeInside = e.Draw.Active;
            // Use an actual other thread: Task waits may inline queued work onto the waiting thread.
            var worker = new Thread(() => activeElsewhere = e.Draw.Active);
            worker.Start();
            worker.Join();
            throw new InvalidOperationException("test cleanup after a fault");
        });
        RenderDispatch.Dispatch(ev, RenderDispatch.Handlers());
        Assert.True(activeInside);
        Assert.False(activeElsewhere);
        Assert.False(ev.Draw.Active);
        Assert.Equal(1, s.Stats.Faults);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CacheAndDisposeHandler()
    {
        var sub = Events.On<RenderFrameEvent>(_ => { });
        _ = RenderDispatch.Handlers();
        sub.Dispose();
        return new WeakReference(sub);
    }

    [Fact]
    public void CachedSnapshotsDoNotKeepUnloadedHandlersAlive()
    {
        var weak = CacheAndDisposeHandler();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(weak.IsAlive);
    }

    [Fact]
    public void HandlersCanDrawOutsideTheGameWithoutEffect()
    {
        // no overlay here: the drawing calls are no-ops and do not throw
        using var s = Events.On<RenderFrameEvent>(e =>
        {
            e.Draw.Line(0, 0, 10, 10, Color.Red);
            e.Draw.Text(5, 5, "fps", align: TextAlign.Center);
            e.World.Label(new WorldPosition(1, 2, 3), "x");
            e.World.Box(new WorldPosition(0, 0, 0), new Vector3(1, 1, 1), Color.Green);
        });
        RenderDispatch.Dispatch(NewFrame(), RenderDispatch.Handlers());
        Assert.True(s.Active);
        Assert.Equal(0, s.Stats.Faults);
    }
}
