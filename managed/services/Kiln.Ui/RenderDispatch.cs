// SPDX-License-Identifier: MIT
// Posts RenderFrameEvent on the render thread (inside Kiln.Ui's overlay frame) to the bus's handlers of that class, with
// a time budget per handler. The bus's own Post is for the tick thread; this runs the same subscriptions (priority order,
// as the subscribing mod, removed on unload) on the render thread.
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Kiln;

/// <summary>A handler's time record: turns a handler off that is over its budget in most recent frames, or stalls frames.</summary>
internal sealed class RenderBudget
{
    public const int Window = 120;     // frames looked at
    public const int WarmUp = 30;      // ordinary budget grace (JIT); hard stalls always count
    public const int SlowLimit = 60;   // over budget in more than this many of the window's frames: off
    public const double StallMs = 100; // one call this long is a stall
    public const int StallLimit = 3;   // hard stalls before it goes off

    private readonly bool[] _over = new bool[Window];
    private int _next, _overCount, _stalls;
    private long _calls;
    private double _windowMs;
    private readonly double[] _ms = new double[Window];

    public long Calls => _calls;
    public int OverCount => _overCount;
    public double AverageMs => _calls == 0 ? 0 : _windowMs / Math.Min(_calls, Window);

    /// <summary>Records one call; returns why the handler must go off, or null.</summary>
    public string? Record(double ms, double budgetMs)
    {
        _calls++;
        _windowMs += ms - _ms[_next];
        _ms[_next] = ms;
        var over = _calls > WarmUp && ms > budgetMs;
        if (_over[_next]) _overCount--;
        _over[_next] = over;
        if (over) _overCount++;
        _next = (_next + 1) % Window;
        if (ms >= StallMs && ++_stalls >= StallLimit)
            return $"it stalled {_stalls} frames for {StallMs:F0} ms or more (last {ms:F0} ms)";
        if (_calls <= WarmUp) return null;
        if (_overCount > SlowLimit)
            return $"it took more than its {budgetMs:0.##} ms budget in {_overCount} of the last {Window} frames (average {AverageMs:F2} ms)";
        return null;
    }
}

internal static class RenderDispatch
{
    private static readonly object Lock = new();
    private static int _started;
    private static ulong _frameHandle;       // kiln.render acquire_frames while handlers exist
    private static Timer? _watch;
    private static RenderFrameEvent? _event;
    private static readonly ConditionalWeakTable<EventSubscription, RenderBudget> Budgets = new();
    // A strong last-snapshot cache would pin unloaded mods when no overlay/timer is running.
    private static readonly ConditionalWeakTable<EventSubscription[], EventSubscription[]> Filtered = new();
    private static long _frames;
    private static double _lastMs, _maxMs, _avgDt = 1.0 / 60;
    private static int _disabled;

    /// <summary>Called by RenderFrameEvent's static constructor (the first subscription). Any thread.</summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        if (!UiNative.Available || !RenderNative.Available)
        {
            if (UiNative.Available) UiEngine.Log(LogLevel.Warn, "render: kiln.render is missing (an older kiln-ui.dll): RenderFrameEvent is not posted");
            return;
        }
        UiEngine.EnsureStarted();
        // Subscriptions come and go with mods; a few checks a second keep the overlay's frames wanted only while someone listens.
        _watch = new Timer(_ => Check(), null, 0, 250);
        UiEngine.Log(LogLevel.Info, $"render: frame events on; camera {(Render.CameraAvailable ? "on" : "off (feature render.camera)")}");
    }

    /// <summary>The handlers of RenderFrameEvent itself (a handler of a base class, such as a KilnEvent catch-all, does not
    /// get render frames).</summary>
    internal static EventSubscription[] Handlers()
    {
        var src = Events.HandlersOf(typeof(RenderFrameEvent));
        return Filtered.GetValue(src, static source => source.Where(s => s.Type == typeof(RenderFrameEvent)).ToArray());
    }

    private static unsafe void Check()
    {
        try
        {
            var want = Handlers().Length > 0;
            lock (Lock)
            {
                if (want && _frameHandle == 0)
                {
                    ulong h = 0;
                    if (RenderNative.T->AcquireFrames(0, &h) == 0) _frameHandle = h;
                }
                else if (!want && _frameHandle != 0)
                {
                    RenderNative.T->Release(_frameHandle);
                    _frameHandle = 0;
                }
            }
        }
        catch (Exception e)
        {
            UiEngine.Log(LogLevel.Error, $"render: {e.Message}");
        }
    }

    /// <summary>Render thread, inside the overlay frame (UiEngine.Frame).</summary>
    public static void Frame(UiDraw ui)
    {
        if (_started == 0) return;
        var subs = Handlers();
        if (subs.Length == 0)
        {
            if (Volatile.Read(ref _frameHandle) != 0) Check();
            return;
        }
        var ev = _event ??= new RenderFrameEvent();
        var f = ui.Frame;
        ev.Frame = f.Number;
        ev.DeltaSeconds = f.DeltaSeconds;
        _avgDt += (Math.Clamp(f.DeltaSeconds, 0.0001, 0.25) - _avgDt) * 0.1;
        ev.Fps = (float)(1.0 / _avgDt);
        ev.Width = f.Width;
        ev.Height = f.Height;
        ev.Scale = f.DpiScale;
        ev.MenuOpen = f.MenuOpen != 0;
        ev.Capturing = f.Capturing != 0;
        ev.Camera = new RenderCamera(RenderNative.ReadCamera());
        Dispatch(ev, subs);
    }

    /// <summary>Runs <paramref name="subs"/> for <paramref name="ev"/> with the budget rules (also the unit tests' entry).</summary>
    internal static void Dispatch(RenderFrameEvent ev, EventSubscription[] subs)
    {
        var outer = ModContext.CurrentMod;
        var outerSync = SynchronizationContext.Current;
        var frameStart = Stopwatch.GetTimestamp();
        try
        {
            foreach (var sub in subs)
            {
                if (!sub.Active) continue;
                if (sub.Filter is { } filter && !filter.Passes(ev)) continue;
                var mod = sub.Mod;
                if (mod is { Alive: false }) continue;
                ModContext.CurrentMod = mod;
                SynchronizationContext.SetSynchronizationContext(mod?.Scheduler.SynchronizationContext ?? outerSync);
                var start = Stopwatch.GetTimestamp();
                Exception? error = null;
                ev.Draw.Begin();
                try
                {
                    sub.Invoke!(ev);
                }
                catch (Exception e)
                {
                    error = e;
                }
                finally
                {
                    ev.Draw.End();
                }
                var ticks = Stopwatch.GetTimestamp() - start;
                Events.RecordCall(sub, ticks);
                var why = Budgets.GetValue(sub, _ => new RenderBudget()).Record(ticks * 1000.0 / Stopwatch.Frequency, Render.HandlerBudgetMs);
                if (error != null)
                {
                    Interlocked.Increment(ref sub.Faults);
                    Log(sub, LogLevel.Error, $"render: handler {sub.Name} threw ({sub.Faults}/{Events.MaxFaults}): {error}");
                    if (sub.Faults >= Events.MaxFaults) Disable(sub, $"it threw {Events.MaxFaults} times");
                }
                if (sub.Active && why != null) Disable(sub, why);
            }
        }
        finally
        {
            ModContext.CurrentMod = outer;
            SynchronizationContext.SetSynchronizationContext(outerSync);
        }
        var ms = (Stopwatch.GetTimestamp() - frameStart) * 1000.0 / Stopwatch.Frequency;
        Volatile.Write(ref _lastMs, ms);
        if (ms > _maxMs) Volatile.Write(ref _maxMs, ms);
        Interlocked.Increment(ref _frames);
    }

    private static void Disable(EventSubscription sub, string why)
    {
        Events.Remove(sub);
        Interlocked.Increment(ref _disabled);
        Log(sub, LogLevel.Warn, $"render: handler {sub.Name} turned off: {why}");
        try
        {
            if (UiNative.Available) Hud.Toast($"{sub.Owner}: a render handler was turned off ({(why.StartsWith("it threw") ? "errors" : "too slow")})", ToastKind.Warning, 6);
        }
        catch
        {
            // a toast must not break the frame
        }
    }

    private static void Log(EventSubscription sub, LogLevel level, string text)
    {
        if (sub.Mod is { } m)
        {
            if (level == LogLevel.Error) m.Log.Error(text);
            else m.Log.Warn(text);
        }
        else UiEngine.Log(level, text);
    }

    public static RenderStats Stats()
    {
        var st = RenderNative.ReadStatus();
        return new RenderStats(Handlers().Length, Interlocked.Read(ref _frames), Volatile.Read(ref _lastMs), Volatile.Read(ref _maxMs), Volatile.Read(ref _disabled),
            st is { Camera: 1 }, st is { CameraValid: 1 }, (long)(st?.CameraFrames ?? 0));
    }
}
