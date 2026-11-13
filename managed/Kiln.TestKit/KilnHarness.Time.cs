// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private HarnessTime? _time;

    /// <summary>The fake kiln.time state of this harness (null before the constructor set it up).</summary>
    internal TimeFake? TimeFake { get; private set; }

    /// <summary>The server's day/night clock as the kit's fake of kiln.time runs it on the harness's virtual time.</summary>
    public HarnessTime Time => _time ??= new HarnessTime(this);

    private void StartTime() => TimeFake = new TimeFake(this);
}

/// <summary>
/// The fake kiln.time of a harness (<see cref="KilnHarness.Time"/>): Kiln.Time's own code (<c>Time.OfDay</c>,
/// <c>State</c>, <c>IsDay</c>, <c>Set</c>, <c>SetSpeed</c>, <c>SetCycle</c>, <c>TimeSetEvent</c>, <c>DayStartedEvent</c>,
/// <c>NightStartedEvent</c>) runs on it.
/// </summary>
/// <remarks>
/// <para><b>Modelled.</b> The clock runs on the harness's virtual time: each step moves it by
/// <see cref="KilnHarnessOptions.WallDeltaMs"/> times the speed (a mod's <c>Time.SetSpeed</c>), from the first step on
/// (the world exists from then; a new world's clock starts at 06:00, the start of the day phase). The day phase runs
/// from 06:00 to 18:00 in <see cref="DayLength"/>, the night from 18:00 to 06:00 in <see cref="NightLength"/> (the game's
/// defaults: 30 and 12 minutes). <c>Time.Set</c> jumps forward to the next time the clock shows the hour (or restarts the
/// clock in its first cycle, mode Game); the day and night events follow the clock as Kiln.Time polls it.</para>
/// <para><b>Not modelled</b> (offline): the weather (<c>time.weather</c>, <c>time.weather_choose</c>: the table, the
/// layers, a choice, a reset), the sync probe, sleeping players speeding the clock, and saving the time with the world
/// (a harness on the same folder starts at 06:00 again).</para>
/// </remarks>
public sealed class HarnessTime
{
    private readonly KilnHarness _harness;

    internal HarnessTime(KilnHarness harness) => _harness = harness;

    private TimeFake Fake => _harness.TimeFake ?? throw new InvalidOperationException("the harness has no clock yet");

    /// <summary>The time of day now, 0..24.</summary>
    public double Hours => Fake.Hours;

    /// <summary>Whether it is day (06:00 to 18:00).</summary>
    public bool IsDay => Fake.IsDay(Fake.Hours);

    /// <summary>The speed the clock runs at (1: the game's; what a mod's <c>Time.SetSpeed</c> holds).</summary>
    public double Speed => Fake.Scale;

    /// <summary>Sets the time of day as the game's own admin menu does (setTimeOfDay: the clock restarts in its first
    /// cycle), with no event.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not a finite number.</exception>
    public void Set(double hours)
    {
        if (!double.IsFinite(hours)) throw new ArgumentOutOfRangeException(nameof(hours));
        _harness.Run(() => Fake.Set(hours, TimeAbi.SetGame, null, record: false));
    }

    /// <summary>How long the day phase lasts in real time (the server's GameSettings dayTimeDuration, 2 to 60 minutes;
    /// default 30). A change keeps the time of day.</summary>
    public TimeSpan DayLength
    {
        get => Fake.DayLength;
        set => _harness.Run(() => Fake.Lengths(Check(value), Fake.NightLength));
    }

    /// <summary>How long the night phase lasts in real time (nightTimeDuration, 2 to 60 minutes; default 12).</summary>
    public TimeSpan NightLength
    {
        get => Fake.NightLength;
        set => _harness.Run(() => Fake.Lengths(Fake.DayLength, Check(value)));
    }

    private static TimeSpan Check(TimeSpan length) =>
        length >= TimeSpan.FromMinutes(2) && length <= TimeSpan.FromMinutes(60)
            ? length
            : throw new ArgumentOutOfRangeException(nameof(length), "the game takes 2 to 60 minutes");

    /// <summary>Every change to the clock a mod made, in order.</summary>
    public IReadOnlyList<FakeTimeCall> Calls => _harness.Run(() => Fake.Calls);
}
