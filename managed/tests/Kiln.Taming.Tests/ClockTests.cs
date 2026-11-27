// SPDX-License-Identifier: MIT
// Taming's time in a game (GameTamingWorld.Now): seconds of the game's own 60 Hz world clock, whatever the step rate, as
// the pets review fixed for Kiln.Pets: a dedicated server steps 15 to 25 times a second (the clearance lab measured 14.7),
// so counting steps as 1/60 s ran every taming timeout (inactivity, lifetime, the 10-a-second limit, the 10 minutes a
// closed attempt answers) 2.5 to 4 times too slow.
using Kiln;
using Kiln.TestKit;
using Xunit;

namespace Kiln.TamingTests;

public sealed class ClockTests
{
    [Theory]
    [InlineData(68.0)]          // 14.7 steps a second, the clearance lab's server
    [InlineData(40.8)]          // 24.5, a dedicated server's usual rate (the kit's default)
    [InlineData(1000.0 / 60)]   // 60: the one rate the step count was right for
    public void Taming_time_is_the_games_clock_at_any_step_rate(double msPerStep)
    {
        using var kiln = new KilnHarness(new KilnHarnessOptions { WallDeltaMs = msPerStep });
        kiln.Boot();
        kiln.Step(2);
        kiln.Run(TamingStartup.Start);   // its service tick (the start type may have run once in this process for another kit)
        kiln.Step();
        var world = new GameTamingWorld();
        var before = kiln.Run(() => world.Now);
        kiln.Step((int)Math.Round(10_000 / msPerStep));   // 10 s of the game's clock
        Assert.InRange(kiln.Run(() => world.Now) - before, 9.9, 10.1);
    }

    [Fact]
    public void Taming_time_holds_between_steps_and_is_one_clock()
    {
        using var kiln = new KilnHarness(new KilnHarnessOptions { WallDeltaMs = 68 });
        kiln.Boot();
        kiln.Step(2);
        kiln.Run(TamingStartup.Start);
        kiln.Step(3);
        var world = new GameTamingWorld();
        var (a, b) = kiln.Run(() => (world.Now, world.Now));
        Assert.Equal(a, b);
        Assert.Equal(a, kiln.Run(() => new GameTamingWorld().Now));
    }
}
