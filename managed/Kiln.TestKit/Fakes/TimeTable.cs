// SPDX-License-Identifier: MIT
// The fake kiln.time (native/core/abi/services/kiln_time.h: the v1 table with its later additions, without the v2 weather
// functions) under the real Kiln.Time.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln.TestKit;

/// <summary>Mirror of <c>kiln_time.h</c>'s table up to <c>time_scale_hold</c> and its state record. The sizes are
/// pinned by tests to Kiln.Time's private mirrors.</summary>
internal static unsafe class TimeAbi
{
    public const string Service = "kiln.time";
    public const uint Version = 1;  // the v1 table and its later additions: the v2 weather table and choice are not there
    public const uint SetForward = 0, SetGame = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct State
    {
        public uint Size;
        public byte IsDay, WeatherAvailable, WeatherChoiceReady, R1;
        public double Hours, HoursField, DayStartHours, NightStartHours, DayLengthS, NightLengthS;
        public float TimeScale;
        public uint ChangeCounter;
        public long EcsTimeNs, GameTimeNs, AnchorEcsNs, AnchorGameNs;
        public uint SyncedCounter, WeatherLayers;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<State*, int> Get;
        public delegate* unmanaged[Cdecl]<double, uint, int> SetTimeOfDay;
        public delegate* unmanaged[Cdecl]<int> ResetWeather;
        public delegate* unmanaged[Cdecl]<float, int> SetTimeScale;
        public delegate* unmanaged[Cdecl]<float> TimeScaleHold;
    }
}

/// <summary>The C# functions behind the fake <c>kiln.time</c> table: get, set_time_of_day, set_time_scale,
/// time_scale_hold. reset_weather answers unavailable (the weather is not modelled).</summary>
internal static unsafe class TimeTable
{
    private static TimeAbi.Table* _table;

    public static nint Create()
    {
        if (_table != null) return (nint)_table;
        var t = (TimeAbi.Table*)NativeMemory.AllocZeroed((nuint)sizeof(TimeAbi.Table));  // process lifetime, never freed
        t->Header = new ServiceHeader { Size = (uint)sizeof(TimeAbi.Table), Version = TimeAbi.Version };
        t->Get = &Get;
        t->SetTimeOfDay = &SetTimeOfDay;
        t->ResetWeather = &ResetWeather;
        t->SetTimeScale = &SetTimeScale;
        t->TimeScaleHold = &TimeScaleHold;
        _table = t;
        return (nint)t;
    }

    private static TimeFake? Fake => FakeTables.Active(TimeAbi.Service)?.TimeFake;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Get(TimeAbi.State* out_)
    {
        try
        {
            if (out_ == null) return FakeTables.InvalidArgument;
            if (Fake is not { } f || FakeTables.Off(TimeFake.ReadFeature) || !f.WorldCreated) return FakeTables.Unavailable;
            *out_ = f.Read();
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetTimeOfDay(double hours, uint mode)
    {
        try
        {
            if (Fake is not { } f || FakeTables.Off(TimeFake.SetFeature) || !f.WorldCreated) return FakeTables.Unavailable;
            if (!FakeTables.OnTickThread) return FakeTables.WrongThread;
            if (!double.IsFinite(hours) || mode > TimeAbi.SetGame) return FakeTables.InvalidArgument;
            f.Set(hours, mode, HostAccess.Current?.Info.Id);
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    // The weather (its layers, phenomena and rolls) is not modelled.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ResetWeather() => FakeTables.OnTickThread ? FakeTables.Unavailable : FakeTables.WrongThread;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetTimeScale(float scale)
    {
        try
        {
            if (Fake is not { } f || FakeTables.Off(TimeFake.ScaleFeature) || !f.WorldCreated) return FakeTables.Unavailable;
            if (!FakeTables.OnTickThread) return FakeTables.WrongThread;
            if (!float.IsFinite(scale) || scale > 1000) return FakeTables.InvalidArgument;
            f.Hold(scale < 0 ? null : scale, HostAccess.Current?.Info.Id);
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static float TimeScaleHold()
    {
        try
        {
            if (FakeTables.Off(TimeFake.ScaleFeature)) return -2f;  // this build cannot hold it
            return Fake?.Held ?? -1f;
        }
        catch
        {
            return -1f;
        }
    }
}

/// <summary>One harness's day/night clock on virtual time: the game clock moves by each step's
/// <see cref="KilnHarnessOptions.WallDeltaMs"/> times the speed; day from 06:00, night from 18:00, each phase as long as
/// the server's setting says.</summary>
internal sealed class TimeFake
{
    public const string ReadFeature = "time.read", SetFeature = "time.set", ScaleFeature = "time.scale";

    public static readonly string[] Features = [ReadFeature, SetFeature, ScaleFeature];

    public static readonly string[] OfflineRows = ["time.weather", "time.weather_choose", "time.sync_probe"];

    public const double DayStart = 6, NightStart = 18;

    private readonly KilnHarness _harness;
    private readonly object _lock = new();
    private readonly List<FakeTimeCall> _calls = [];
    private long _gameNs;          // the day/night cycle's clock (game time)
    private long _lastEcsNs;
    private long _anchorEcsNs, _anchorGameNs;
    private uint _counter, _synced = uint.MaxValue;
    private double _hoursField;
    private float? _held;

    public TimeFake(KilnHarness harness)
    {
        _harness = harness;
        ForgetLastDayOrNight();
        // a new world's clock: the first morning, as the game's start of a world (day 0 at 06:00 would be the start of the day phase)
        _gameNs = 0;
        _hoursField = HoursAt(0);
    }

    // Kiln.Time watches day and night across its polls in a static (TimeEvents._wasDay): a new harness is a new world, so
    // the last harness's day or night is forgotten (reflection: the field is private to Kiln.Time; a Kiln.Time without it
    // has nothing to forget).
    private static void ForgetLastDayOrNight()
    {
        try
        {
            typeof(Time).Assembly.GetType("Kiln.TimeEvents")?.GetField("_wasDay", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(null, null);
        }
        catch (Exception e) when (e is ArgumentException or FieldAccessException or System.Reflection.TargetException)
        {
            // a watch of another shape: nothing to forget
        }
    }

    /// <summary>The server's GameSettings dayTimeDuration and nightTimeDuration (the game's defaults: 30 and 12 minutes).</summary>
    public TimeSpan DayLength { get; private set; } = TimeSpan.FromMinutes(30);

    public TimeSpan NightLength { get; private set; } = TimeSpan.FromMinutes(12);

    /// <summary>The world exists from the harness's first step until its server stops.</summary>
    public bool WorldCreated => _harness.State is HarnessState.Starting or HarnessState.Running;

    public float? Held
    {
        get
        {
            lock (_lock) return _held;
        }
    }

    public float Scale
    {
        get
        {
            lock (_lock) return _held ?? 1f;
        }
    }

    private long CycleNs => DayLength.Ticks * 100 + NightLength.Ticks * 100;

    /// <summary>The time of day the clock shows at game time <paramref name="gameNs"/>.</summary>
    public double HoursAt(long gameNs)
    {
        var day = DayLength.Ticks * 100.0;
        var t = (double)(((gameNs % CycleNs) + CycleNs) % CycleNs);
        var hours = t < day ? DayStart + 12 * t / day : NightStart + 12 * (t - day) / (NightLength.Ticks * 100.0);
        return hours % 24;
    }

    // Where in the cycle (game time from its start) the clock shows `hours`.
    private long PositionOf(double hours)
    {
        hours = (hours % 24 + 24) % 24;
        double day = DayLength.Ticks * 100.0, night = NightLength.Ticks * 100.0;
        return hours >= DayStart && hours < NightStart
            ? (long)((hours - DayStart) / 12 * day)
            : (long)(day + ((hours - NightStart + 24) % 24) / 12 * night);
    }

    public double Hours
    {
        get
        {
            lock (_lock) return HoursAt(_gameNs);
        }
    }

    public bool IsDay(double hours) => hours >= DayStart && hours < NightStart;

    public TimeAbi.State Read()
    {
        lock (_lock)
        {
            var hours = HoursAt(_gameNs);
            return new TimeAbi.State
            {
                Size = (uint)Unsafe.SizeOf<TimeAbi.State>(),
                IsDay = IsDay(_hoursField) ? (byte)1 : (byte)0,  // as of the last sim step
                Hours = hours,
                HoursField = _hoursField,
                DayStartHours = DayStart,
                NightStartHours = NightStart,
                DayLengthS = DayLength.TotalSeconds,
                NightLengthS = NightLength.TotalSeconds,
                TimeScale = _held ?? 1f,
                ChangeCounter = _counter,
                EcsTimeNs = _harness.VirtualMs * 1_000_000,
                GameTimeNs = _gameNs,
                AnchorEcsNs = _anchorEcsNs,
                AnchorGameNs = _anchorGameNs,
                SyncedCounter = _synced,
            };
        }
    }

    // ---- writes ----

    public void Set(double hours, uint mode, string? mod, bool record = true)
    {
        lock (_lock)
        {
            var cycle = CycleNs;
            var target = PositionOf(hours);
            if (mode == TimeAbi.SetGame) _gameNs = target;  // the game's setTimeOfDay: the clock restarts in its first cycle
            else
            {
                // forward to the next time the clock shows it: today if still ahead, else tomorrow
                var start = _gameNs - ((_gameNs % cycle) + cycle) % cycle;
                var at = start + target;
                if (at <= _gameNs) at += cycle;
                _gameNs = at;
            }
            Anchor();
            if (record) _calls.Add(new FakeTimeCall(_harness.CurrentStep, mod, mode == TimeAbi.SetGame ? "set game" : "set", (hours % 24 + 24) % 24));
        }
    }

    public void Hold(float? scale, string? mod)
    {
        lock (_lock)
        {
            _held = scale;
            Anchor();
            _calls.Add(new FakeTimeCall(_harness.CurrentStep, mod, scale is null ? "speed released" : "speed", scale ?? 1));
        }
    }

    public void Lengths(TimeSpan day, TimeSpan night)
    {
        lock (_lock)
        {
            var at = HoursAt(_gameNs);
            DayLength = day;
            NightLength = night;
            _gameNs = PositionOf(at) + (_gameNs - ((_gameNs % CycleNs) + CycleNs) % CycleNs);  // same time of day, new lengths
            Anchor();
        }
    }

    private void Anchor()
    {
        _anchorEcsNs = _harness.VirtualMs * 1_000_000;
        _anchorGameNs = _gameNs;
        _counter++;
    }

    public IReadOnlyList<FakeTimeCall> Calls
    {
        get
        {
            lock (_lock) return [.. _calls];
        }
    }

    /// <summary>A sim step of the game: the clock moves by the step's time at the current speed, the Daytime field is
    /// written, and the server sends a changed counter to clients.</summary>
    public void BeforeStep()
    {
        lock (_lock)
        {
            var ecs = _harness.VirtualMs * 1_000_000;
            if (_harness.State is HarnessState.Starting or HarnessState.Running)
                _gameNs += (long)((ecs - _lastEcsNs) * (double)(_held ?? 1f));
            _lastEcsNs = ecs;
            _hoursField = HoursAt(_gameNs);
            _synced = _counter;
        }
    }
}

/// <summary>A change to the clock a mod made (<see cref="HarnessTime.Calls"/>).</summary>
public sealed class FakeTimeCall
{
    internal FakeTimeCall(ulong step, string? modId, string what, double value)
    {
        Step = step;
        ModId = modId;
        What = what;
        Value = value;
    }

    /// <summary>The step during which it ran.</summary>
    public ulong Step { get; }

    /// <summary>The mod that asked; null for a call outside any mod.</summary>
    public string? ModId { get; }

    /// <summary><c>set</c> (forward), <c>set game</c> (the game's setTimeOfDay), <c>speed</c> or <c>speed released</c>.</summary>
    public string What { get; }

    /// <summary>The time of day set (0..24), or the speed held (1 when released).</summary>
    public double Value { get; }

    /// <summary>The call, for a log line.</summary>
    public override string ToString() => $"step {Step} {ModId ?? "kiln"}: {What} {Value:0.###}";
}
