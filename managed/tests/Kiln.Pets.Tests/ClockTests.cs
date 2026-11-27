// SPDX-License-Identifier: MIT
// The pets' time in a game (GamePetWorld.Now): seconds of the game's own 60 Hz world clock, whatever the step rate. Review
// of dev.32 pets: it counted steps as 1/60 s each, but a dedicated server steps 15 to 25 times a second (the clearance lab
// measured 14.7), so every pet timer (revive channel, stuck and repath timers, a body's 5 s to get ready, the owners'
// view's 5 Hz and 1 s keepalive) ran 2.5 to 4 times too slow.
using Kiln;
using Kiln.TestKit;
using Xunit;

namespace Kiln.PetsTests;

public sealed class ClockTests
{
    [Theory]
    [InlineData(68.0)]     // 14.7 steps a second, the clearance lab's server
    [InlineData(40.8)]     // 24.5, a dedicated server's usual rate (the kit's default)
    [InlineData(1000.0 / 60)]   // 60: the one rate the step count was right for
    public void Pet_time_is_the_games_clock_at_any_step_rate(double msPerStep)
    {
        using var kiln = new KilnHarness(new KilnHarnessOptions { WallDeltaMs = msPerStep });
        kiln.Boot();
        kiln.Step(2);
        kiln.Run(PetsRuntime.Start);   // its service tick (the start type ran once in this process, maybe for another kit)
        kiln.Step();
        var world = new GamePetWorld();
        var before = kiln.Run(() => world.Now);
        var steps = (int)Math.Round(10_000 / msPerStep);   // 10 s of the game's clock
        kiln.Step(steps);
        var elapsed = kiln.Run(() => world.Now) - before;
        Assert.InRange(elapsed, 9.9, 10.1);
    }

    [Fact]
    public void Pet_time_holds_between_steps()
    {
        using var kiln = new KilnHarness(new KilnHarnessOptions { WallDeltaMs = 68 });
        kiln.Boot();
        kiln.Step(2);
        kiln.Run(PetsRuntime.Start);
        kiln.Step(3);
        var world = new GamePetWorld();
        var (a, b) = kiln.Run(() => (world.Now, world.Now));
        Assert.Equal(a, b);
        Assert.Equal(a, kiln.Run(() => new GamePetWorld().Now));   // one clock, whichever world reads it
    }
}
