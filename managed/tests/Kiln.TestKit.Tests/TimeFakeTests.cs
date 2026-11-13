// SPDX-License-Identifier: MIT
// The kit's recording fake of kiln.time: Kiln.Time's own code on a day/night clock that runs on the harness's virtual time.
using System.Runtime.CompilerServices;
using TkFakes;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class TimeFakeTests
{
    private static string Said(CommandResult r) => string.Join("\n", r.Error is null ? r.Output : r.Output.Append(r.Error));

    private static KilnHarness Start()
    {
        var kiln = new KilnHarness();
        kiln.Load<TkFakesMod>();
        kiln.Step(2);
        return kiln;
    }

    [Fact]
    public void TheClockRunsOnVirtualTimeAndTheDayAndNightEventsFollowIt()
    {
        using var kiln = Start();
        Assert.InRange(kiln.Time.Hours, 6.0, 6.01);  // a new world's clock starts at 06:00
        Assert.True(kiln.Time.IsDay);
        Assert.StartsWith("time: 06:00 day True speed 1", Said(kiln.RunCommand("tkf-time")));
        // 2 minutes a phase: 12 hours of the clock in 120 s of virtual time, 0.1 hour a second
        kiln.Time.DayLength = TimeSpan.FromMinutes(2);
        kiln.Time.NightLength = TimeSpan.FromMinutes(2);
        kiln.Time.Set(17.9);
        kiln.StepUntil(() => kiln.Lines("tk-fakes", "night started").Count > 0, maxSteps: 60);  // 1 s to 18:00, then Kiln.Time's poll
        Assert.False(kiln.Time.IsDay);
        Assert.StartsWith("night started at 18:0", kiln.Lines("tk-fakes", "night started")[0]);

        // a mod sets the time forward: the next 07:30
        kiln.RunCommand("tkf-settime 7.5");
        Assert.InRange(kiln.Time.Hours, 7.5, 7.501);
        Assert.Equal(["time set: 07:30 Forward by tk-fakes"], kiln.Lines("tk-fakes", "time set:"));
        kiln.StepUntil(() => kiln.Lines("tk-fakes", "day started").Count > 0, maxSteps: 13);
        Assert.StartsWith("day started at 07:3", kiln.Lines("tk-fakes", "day started")[0]);

        // the speed: stopped, then given back
        kiln.RunCommand("tkf-speed 0");
        var stopped = kiln.Time.Hours;
        kiln.Step(50);
        Assert.Equal(stopped, kiln.Time.Hours);
        Assert.Equal(0, kiln.Time.Speed);
        Assert.Contains("speed 0 ", Said(kiln.RunCommand("tkf-time")));
        kiln.RunCommand("tkf-speed off");
        kiln.Step(25);
        Assert.InRange(kiln.Time.Hours - stopped, 0.09, 0.11);  // about a second of virtual time
        Assert.Equal(["set", "speed", "speed released"], kiln.Time.Calls.Select(c => c.What));
        Assert.All(kiln.Time.Calls, c => Assert.Equal("tk-fakes", c.ModId));
    }

    [Fact]
    public void TheGameModeRestartsTheClockAndTheWeatherIsOffline()
    {
        using var kiln = Start();
        kiln.RunCommand("tkf-settime 20 game");
        Assert.InRange(kiln.Time.Hours, 20.0, 20.001);
        Assert.Equal(["time set: 20:00 GameReset by tk-fakes"], kiln.Lines("tk-fakes", "time set:"));
        var state = kiln.Run(() => Time.State);
        Assert.Equal(TimeSpan.FromMinutes(30), state.DayLength);   // the game's defaults
        Assert.Equal(TimeSpan.FromMinutes(12), state.NightLength);
        Assert.False(state.WeatherAvailable);
        Assert.False(kiln.Run(() => Time.WeatherChoiceAvailable));
        Assert.Throws<InvalidOperationException>(() => kiln.Run(() => Time.ResetWeather()));
        Assert.Contains("offline", kiln.Capabilities.Why("time.weather_choose"));
        Assert.True(kiln.Run(() => Time.CanSetSpeed));
    }

    [Fact]
    public void BeforeTheFirstStepThereIsNoClockAndSwitchedOffNothingWorks()
    {
        using (var kiln = new KilnHarness())
        {
            kiln.Load<TkFakesMod>();
            Assert.False(kiln.Run(() => Time.Ready));  // the world is created at the first step
            kiln.Step();
            Assert.True(kiln.Run(() => Time.Ready));
            kiln.Capabilities.Override("time.set", false);
            Assert.Throws<InvalidOperationException>(() => kiln.Run(() => Time.Set(9)));
            kiln.Capabilities.ClearOverride("time.set");
            kiln.Capabilities.Override("kiln.time", false);
            Assert.False(kiln.Run(() => Time.Ready));
            kiln.Capabilities.ClearOverride("kiln.time");
            kiln.Run(() => Time.Set(9));
            Assert.InRange(kiln.Time.Hours, 9, 9.001);
        }
        Assert.False(Time.Ready);
        using var next = new KilnHarness();
        next.Step(2);
        Assert.InRange(next.Time.Hours, 6.0, 6.01);  // a new world again
        Assert.Empty(next.Time.Calls);
    }

    [Fact]
    public void TheTableMirrorsTheHeaderAsTheServiceReadsIt()
    {
        Assert.Equal((104, 48), (Unsafe.SizeOf<TimeAbi.State>(), Unsafe.SizeOf<TimeAbi.Table>()));
        Assert.Equal(Unsafe.SizeOf<TimeAbi.State>(), Mirror.Size(typeof(Time), "NativeState"));
        Assert.Equal(Unsafe.SizeOf<TimeAbi.Table>(), Mirror.Size(typeof(Time), "TableV2"));
    }
}
