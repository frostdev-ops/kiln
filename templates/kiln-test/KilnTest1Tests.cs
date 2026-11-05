using Kiln;
using Kiln.Testing;
using Assert = Kiln.Testing.Assert;

namespace KilnTest1;

/// <summary>Tests run one at a time on the tick thread, in Order, then class name, then declaration order. Every await
/// comes back to the tick thread, so a test may touch game state and call services. Only awaits are timed: never
/// block (Thread.Sleep, .Result) inside a test.</summary>
public sealed class KilnTest1Tests
{
    [KilnTest(Order = 0)]
    public void Kiln_is_running()
    {
        Assert.True(Game.IsTickThread, "tests run on the tick thread");
        Assert.True(Lifecycle.IsServerRunning, "auto-run starts after ServerStarted");
    }

    [KilnTest]
    public void Mod_under_test_is_loaded() =>
        Assert.True(Mods.IsLoaded("KILN_UNDER_TEST"), "the mod under test is loaded");

    [KilnTest(TimeoutSeconds = 10)]
    public async Task The_world_steps(TestContext t)
    {
        ulong first = 0, last = 0;
        void Count(in TickInfo tick)
        {
            if (first == 0) first = tick.Step;
            last = tick.Step;
        }
        t.Mod.Tick.Pre += Count;                            // Kiln removes it with the mod anyway; tidy up here
        try
        {
            await t.Ticks(5);                               // five server steps later
        }
        finally
        {
            t.Mod.Tick.Pre -= Count;
        }
        Assert.True(last >= first + 3, $"the world stepped ({first} -> {last})");
    }

    [KilnTest(TimeoutSeconds = 10)]
    public async Task Until_waits_for_a_condition(TestContext t)
    {
        var deadline = DateTime.UtcNow.AddSeconds(1);
        await t.Until(() => DateTime.UtcNow >= deadline, TimeSpan.FromSeconds(5), "a second to pass");
    }

    [KilnTest(Skip = "needs a joined player: run it from a client session")]
    public void Needs_a_player() { }
}
