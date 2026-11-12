// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.Testing;
using Assert = Kiln.Testing.Assert;

namespace TkSuite;

/// <summary>[KilnTest] methods with every outcome the runner gives: pass, fail, skip (by attribute and by Assert.Skip),
/// timeout (an await), a method the runner cannot call, ordered and async tests, tests on the instance.</summary>
public sealed class TkSuiteMod : Mod
{
    private int _first;
    private ulong _step;

    public override void OnLoad() => Context.Tick.Pre += (in TickInfo t) => _step = t.Step;

    [KilnTest(Order = -1)]
    public void First_sets_state() => _first = 41;

    [KilnTest]
    public void Sees_the_state_of_the_first() => Assert.Equal(41, _first, "the tests share one loaded instance");

    [KilnTest]
    public async Task Awaits_ticks(TestContext t)
    {
        Assert.True(Game.IsTickThread, "starts on the tick thread");
        var before = _step;
        await t.Ticks(3);
        Assert.True(Game.IsTickThread, "after the ticks");
        // the continuation runs in the third step's Pre, before this mod's own Tick.Pre handler of that step
        Assert.True(_step >= before + 2, $"three steps later: {before} -> {_step}");
    }

    [KilnTest]
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoOptimization | System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]  // the test asserts this frame; a Release build would make its last call a tail call
    public void Fails_an_assertion() => Check(2);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]  // its frame is asserted, also in a Release build
    private static void Check(int value) => Assert.Equal(1, value, "the value");

    [KilnTest(Skip = "skipped by its attribute")]
    public void Skipped_by_attribute() => Assert.Fail("never runs");

    [KilnTest]
    public void Skips_itself() => Assert.Skip("skipped by Assert.Skip");

    [KilnTest("a custom name")]
    public static void Named() { }

    [KilnTest(TimeoutSeconds = 1)]
    public async Task Times_out_in_an_await(TestContext t) => await t.Ticks(int.MaxValue);

    [KilnTest]
    public void Broken(int value) => _first = value;

    [KilnTest(Order = 5)]
    public void Runs_last() { }
}

/// <summary>Tests on a class of their own (a new instance each); its full name sorts before TkSuiteMod's.</summary>
public sealed class OtherTests
{
    [KilnTest]
    public void Throws() => throw new InvalidOperationException("something broke");

    [KilnTest]
    public ValueTask Returns_a_value_task() => ValueTask.CompletedTask;
}
