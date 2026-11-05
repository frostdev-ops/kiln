// SPDX-License-Identifier: MIT
// Kiln.Time on the event bus: TimeSetEvent, DayStartedEvent, NightStartedEvent (docs/kiln/events.md).
namespace Kiln;

/// <summary>Code changed the time of day through <see cref="Time.Set(double, TimeSetMode)"/> (a mod, a command,
/// Kiln.Admin). Posted on the tick thread right after the change. Not cancelable. Changes made in the game itself
/// (the admin menu, sleeping) do not raise it; watch <see cref="DayStartedEvent"/>/<see cref="NightStartedEvent"/>.</summary>
public sealed class TimeSetEvent : KilnEvent
{
    internal TimeSetEvent(double before, double hours, TimeSetMode mode, string by)
    {
        Before = before;
        Hours = hours;
        Mode = mode;
        By = by;
    }

    /// <summary>Time of day before (hours; NaN when it could not be read).</summary>
    public double Before { get; }

    /// <summary>The time set (hours, 0..24).</summary>
    public double Hours { get; }

    public TimeSetMode Mode { get; }

    /// <summary>Id of the mod whose code set it ("kiln" for Kiln itself).</summary>
    public string By { get; }

    public override string ToString() => $"time set {Time.Format(Before)} -> {Time.Format(Hours)} ({Mode}) by {By}";
}

/// <summary>The day phase began (the clock passed <see cref="DayTimeState.DayStart"/>, also by a set or sleeping).
/// Kiln samples the clock every 12 steps (about 0.5 s) while someone listens, so it is posted up to that late, on the
/// tick thread. Not cancelable.</summary>
public sealed class DayStartedEvent : KilnEvent
{
    static DayStartedEvent() => TimeEvents.Start();

    internal DayStartedEvent(DayTimeState state) => State = state;

    /// <summary>The clock when Kiln noticed.</summary>
    public DayTimeState State { get; }

    public double Hours => State.Hours;
}

/// <summary>The night phase began (see <see cref="DayStartedEvent"/>).</summary>
public sealed class NightStartedEvent : KilnEvent
{
    static NightStartedEvent() => TimeEvents.Start();

    internal NightStartedEvent(DayTimeState state) => State = state;

    public DayTimeState State { get; }
    public double Hours => State.Hours;
}

internal static class TimeEvents
{
    internal const int PollEverySteps = 12;
    private static readonly object Lock = new();
    private static IDisposable? _tick;
    private static bool? _wasDay;

    public static void Start()
    {
        lock (Lock) _tick ??= ServiceTick.Add("kiln.time events", Poll);
    }

    internal static void AfterSet(double before, double hours, TimeSetMode mode)
    {
        var h = (hours % 24 + 24) % 24;
        Events.Post(new TimeSetEvent(before, h, mode, ModContext.CurrentMod?.Info.Id ?? "kiln"));
    }

    private static void Poll(in TickInfo tick)
    {
        if (tick.Step % PollEverySteps != 0) return;
        var day = Events.HasSubscribers<DayStartedEvent>();
        var night = Events.HasSubscribers<NightStartedEvent>();
        if (!day && !night)
        {
            _wasDay = null;  // start fresh when someone subscribes again
            return;
        }
        if (!Time.TryGetState(out var s)) return;
        var was = _wasDay;
        _wasDay = s.IsDay;
        if (was is null || was == s.IsDay) return;
        if (s.IsDay && day) Events.Post(new DayStartedEvent(s));
        else if (!s.IsDay && night) Events.Post(new NightStartedEvent(s));
    }
}
