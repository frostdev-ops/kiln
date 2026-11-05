// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>How <see cref="Time.Set(double, TimeSetMode)"/> moves the clock.</summary>
public enum TimeSetMode
{
    /// <summary>Jump forward to the next time the clock shows the given hour (later today, or tomorrow). The game's
    /// day/night clock keeps counting up, so weather and other timers keep running.</summary>
    Forward = 0,

    /// <summary>Call the game's own setTimeOfDay, like the in-game admin menu and loading a save: the day/night clock
    /// restarts at day 0. Weather timed on the old clock may then stall for a while; call
    /// <see cref="Time.ResetWeather"/> after it.</summary>
    GameReset = 1,
}

/// <summary>A snapshot of the server's day/night clock.</summary>
/// <param name="Hours">Time of day, 0..24, at the current game clock (reflects a set at once).</param>
/// <param name="HoursField">The game's own time-of-day field, updated once per sim step.</param>
/// <param name="IsDay">Day phase (<see cref="DayStart"/> to <see cref="NightStart"/>), as of the last step.</param>
/// <param name="DayStart">Start of the day phase in hours (6 by default).</param>
/// <param name="NightStart">Start of the night phase in hours (18 by default).</param>
/// <param name="DayLength">Real time the day phase lasts (game setting <c>dayTimeDuration</c>).</param>
/// <param name="NightLength">Real time the night phase lasts (game setting <c>nightTimeDuration</c>).</param>
/// <param name="TimeScale">1 normally; the game raises it while players sleep at night.</param>
/// <param name="ChangeCounter">Goes up on every re-anchor; the server re-sends the clock to clients when it moves.</param>
/// <param name="SyncedCounter">The counter the server last sent to clients; null when not observed yet.</param>
/// <param name="GameTime">The day/night cycle clock (one cycle = day + night length).</param>
/// <param name="EcsTime">The world's ECS time.</param>
/// <param name="WeatherAvailable">Whether <see cref="Time.ResetWeather"/> can work.</param>
/// <param name="WeatherLayers">Number of weather layers (0 = unknown).</param>
public readonly record struct DayTimeState(
    double Hours,
    double HoursField,
    bool IsDay,
    double DayStart,
    double NightStart,
    TimeSpan DayLength,
    TimeSpan NightLength,
    float TimeScale,
    uint ChangeCounter,
    uint? SyncedCounter,
    TimeSpan GameTime,
    TimeSpan EcsTime,
    bool WeatherAvailable,
    int WeatherLayers)
{
    /// <summary>"HH:mm" of <see cref="Hours"/>.</summary>
    public string Clock => Time.Format(Hours);

    public override string ToString() =>
        $"{Clock} ({(IsDay ? "day" : "night")}, day {DayLength.TotalMinutes:F0} min, night {NightLength.TotalMinutes:F0} min, " +
        $"scale {TimeScale:F2}, counter {ChangeCounter})";
}

/// <summary>
/// The server world's time of day (service <c>kiln.time</c>). Changes replicate to every client through the game's
/// own sync and are saved with the world. Reads work from any thread; changes must be made on the tick thread
/// (mod code normally runs there).
/// </summary>
public static unsafe class Time
{
    private const int Ok = 0, InvalidArgument = 1, Unavailable = 3, WrongThread = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeState
    {
        public uint Size;
        public byte IsDay, WeatherAvailable, R0, R1;
        public double Hours, HoursField, DayStartHours, NightStartHours, DayLengthS, NightLengthS;
        public float TimeScale;
        public uint ChangeCounter;
        public long EcsTimeNs, GameTimeNs, AnchorEcsNs, AnchorGameNs;
        public uint SyncedCounter, WeatherLayers;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<NativeState*, int> Get;
        public delegate* unmanaged[Cdecl]<double, uint, int> SetTimeOfDay;
        public delegate* unmanaged[Cdecl]<int> ResetWeather;
    }

    private static Table* _table;
    private static Table* T => _table != null ? _table : (_table = Services.Get<Table>("kiln.time", 1));

    /// <summary>True when the native service is loaded and usable on this game build.</summary>
    public static bool Available => T != null;

    /// <summary>True once the server world exists and its clock can be read.</summary>
    public static bool Ready => TryGetState(out _);

    /// <summary>Time of day in hours, 0..24 (e.g. 13.5 = 13:30). Setting it calls <see cref="Set(double, TimeSetMode)"/>
    /// in <see cref="TimeSetMode.Forward"/> mode.</summary>
    public static double OfDay
    {
        get => State.Hours;
        set => Set(value);
    }

    /// <summary>The current clock state. Throws when the service or the world is not available.</summary>
    public static DayTimeState State
    {
        get
        {
            if (T == null) throw new NotSupportedException("kiln.time is unavailable on this game build");
            NativeState s;
            Check(T->Get(&s), "Time.State");
            return Convert(in s);
        }
    }

    /// <summary>Like <see cref="State"/>, without throwing.</summary>
    public static bool TryGetState(out DayTimeState state)
    {
        state = default;
        if (T == null) return false;
        NativeState s;
        if (T->Get(&s) != Ok) return false;
        state = Convert(in s);
        return true;
    }

    public static bool IsDay => State.IsDay;
    public static TimeSpan DayLength => State.DayLength;
    public static TimeSpan NightLength => State.NightLength;

    /// <summary>Sets the time of day (hours; wrapped into 0..24). Tick thread only.</summary>
    public static void Set(double hours, TimeSetMode mode = TimeSetMode.Forward)
    {
        if (T == null) throw new NotSupportedException("kiln.time is unavailable on this game build");
        if (!double.IsFinite(hours)) throw new ArgumentOutOfRangeException(nameof(hours));
        Services.RequireTickThread("Time.Set");
        var watched = Events.HasSubscribers<TimeSetEvent>();
        var before = watched && TryGetState(out var s0) ? s0.Hours : double.NaN;
        Check(T->SetTimeOfDay(hours, (uint)mode), "Time.Set");
        if (watched) TimeEvents.AfterSet(before, hours, mode);
    }

    /// <summary>Sets the time of day from a clock time (e.g. <c>TimeSpan.FromHours(7.5)</c> = 07:30).</summary>
    public static void Set(TimeSpan timeOfDay, TimeSetMode mode = TimeSetMode.Forward) => Set(timeOfDay.TotalHours, mode);

    /// <summary>Sets the time from text: "7", "7.5", "07:30", "19:45", "7:30pm", "noon", "midnight", "dawn", "dusk".
    /// Throws <see cref="FormatException"/> on anything else.</summary>
    public static void Set(string text, TimeSetMode mode = TimeSetMode.Forward)
    {
        if (!TryParse(text, out var hours)) throw new FormatException($"not a time of day: '{text}'");
        Set(hours, mode);
    }

    /// <summary>Ends all weather and lets every weather layer pick a new phenomenon on the server's next weather
    /// update (in the same frame). Clients follow through the game's weather sync. Tick thread only.</summary>
    public static void ResetWeather()
    {
        if (T == null) throw new NotSupportedException("kiln.time is unavailable on this game build");
        Services.RequireTickThread("Time.ResetWeather");
        Check(T->ResetWeather(), "Time.ResetWeather");
    }

    /// <summary>"HH:mm" for hours 0..24.</summary>
    public static string Format(double hours)
    {
        var minutes = (int)Math.Round(((hours % 24 + 24) % 24) * 60) % (24 * 60);
        return $"{minutes / 60:D2}:{minutes % 60:D2}";
    }

    /// <summary>Parses "7", "7.5", "07:30", "7:30:15", "7pm", "7:30 am", "noon", "midnight", "dawn" (6:00),
    /// "dusk" (18:00).</summary>
    public static bool TryParse(string? text, out double hours)
    {
        hours = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().ToLowerInvariant();
        switch (s)
        {
            case "noon": hours = 12; return true;
            case "midnight": hours = 0; return true;
            case "dawn" or "morning" or "day": hours = 6; return true;
            case "dusk" or "evening" or "night": hours = 18; return true;
        }
        var pm = false;
        var am = false;
        if (s.EndsWith("pm")) { pm = true; s = s[..^2].TrimEnd(); }
        else if (s.EndsWith("am")) { am = true; s = s[..^2].TrimEnd(); }
        double h;
        if (s.Contains(':'))
        {
            var parts = s.Split(':');
            if (parts.Length is < 2 or > 3) return false;
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hh) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var mm) || mm > 59)
                return false;
            var ss = 0;
            if (parts.Length == 3 && (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out ss) || ss > 59))
                return false;
            h = hh + mm / 60.0 + ss / 3600.0;
        }
        else if (!double.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out h)) return false;
        if (pm || am)
        {
            if (h < 1 || h >= 13) return false;
            if (h >= 12) h -= 12;
            if (pm) h += 12;
        }
        if (h < 0 || h > 24) return false;
        hours = h % 24;
        return true;
    }

    private static DayTimeState Convert(in NativeState s) => new(
        s.Hours, s.HoursField, s.IsDay != 0, s.DayStartHours, s.NightStartHours,
        TimeSpan.FromSeconds(s.DayLengthS), TimeSpan.FromSeconds(s.NightLengthS), s.TimeScale, s.ChangeCounter,
        s.SyncedCounter == uint.MaxValue ? null : s.SyncedCounter,
        TimeSpan.FromTicks(s.GameTimeNs / 100), TimeSpan.FromTicks(s.EcsTimeNs / 100), s.WeatherAvailable != 0,
        (int)s.WeatherLayers);

    private static void Check(int rc, string api)
    {
        switch (rc)
        {
            case Ok: return;
            case InvalidArgument: throw new ArgumentException($"{api}: invalid argument");
            case Unavailable: throw new InvalidOperationException($"{api}: not available (no server world yet, or the game build lacks it)");
            case WrongThread: throw new InvalidOperationException($"{api} must be called on the tick thread (use Scheduler.Post)");
            default: throw new InvalidOperationException($"{api} failed (kiln error {rc})");
        }
    }
}
