// SPDX-License-Identifier: MIT
using TkBlock;
using Xunit;
using TestOutcome = Kiln.Testing.TestOutcome;

namespace Kiln.TestKit.Tests;

public sealed class WatchdogTests(ITestOutputHelper output)
{
    private static KilnTestSession Session() =>
        new(k => k.Load<TkBlockMod>(new ModManifest("tk-block")), options: new KilnTestOptions { WatchdogGrace = TimeSpan.FromMilliseconds(300) });

    private void Show(KilnTestResult r) =>
        output.WriteLine($"{r.Name}: {r.Outcome} in {r.Duration.TotalSeconds:0.00} s: {r.Message}\n{string.Join("\n", r.Notes)}\n{r.Stack}\n");

    [Fact]
    public void BlockedTestsAreStoppedWithTheirStackAndTheNextTestRunsOnAFreshHarness()
    {
        using var session = Session();
        var r = session.RunAll(typeof(TkBlockMod));
        r.ForEach(Show);
        Assert.Equal(
        [
            "TkBlockMod.Blocks_in_task_wait", "TkBlockMod.Passes_after_the_wait", "TkBlockMod.Blocks_on_a_lock", "TkBlockMod.Passes_after_the_lock",
            "TkBlockMod.Spins_forever", "TkBlockMod.Passes_after_the_loop", "TkBlockMod.Awaits_ticks_for_a_while", "TkBlockMod.Blocks_briefly",
            "TkBlockMod.Sleeps_forever",
        ], r.Select(x => x.Name));
        Assert.Equal(
        [
            TestOutcome.Timeout, TestOutcome.Pass, TestOutcome.Timeout, TestOutcome.Pass, TestOutcome.Timeout, TestOutcome.Pass, TestOutcome.Pass,
            TestOutcome.Pass, TestOutcome.Timeout,
        ], r.Select(x => x.Outcome));

        var wait = r.Named("TkBlockMod.Blocks_in_task_wait");
        Assert.Contains("Blocks_in_task_wait", wait.Stack);  // the test's own async frame
        Assert.Contains("TkBlockMod.WaitFor", wait.Stack);
        Assert.Contains("Task.Wait", wait.Stack);
        Assert.Contains("stopped by Thread.Interrupt", wait.Message);
        Assert.StartsWith("blocked the tick thread: a server step did not return within 1.3 s", wait.Message);
        var locked = r.Named("TkBlockMod.Blocks_on_a_lock");
        Assert.Contains("TkBlockMod.Blocks_on_a_lock", locked.Stack);
        Assert.Contains("stopped by Thread.Interrupt", locked.Message);
        var spin = r.Named("TkBlockMod.Spins_forever");
        Assert.Contains("TkBlockMod.Spins_forever", spin.Stack);
        Assert.Contains("stopped by ControlledExecution's abort", spin.Message);
        var sleep = r.Named("TkBlockMod.Sleeps_forever");
        Assert.Contains("TkBlockMod.Sleeps_forever", sleep.Stack);
        Assert.Contains("stopped by Thread.Interrupt", sleep.Message);
        foreach (var stopped in new[] { wait, locked, spin, sleep })
            Assert.DoesNotContain(stopped.Notes, n => n.Contains("could not be stopped", StringComparison.Ordinal));

        // the test after each stop ran on a fresh harness and load, and says so
        foreach (var after in new[] { "TkBlockMod.Passes_after_the_wait", "TkBlockMod.Passes_after_the_lock", "TkBlockMod.Passes_after_the_loop" })
            Assert.Contains(r.Named(after).Notes, n => n.StartsWith("ran on a fresh harness and a fresh load", StringComparison.Ordinal));
        Assert.Equal(4, session.HarnessCount);
    }

    [Fact]
    public void ASlowTestThatStaysAliveIsNotStopped()
    {
        using var session = Session();
        var slow = session.Run("TkBlockMod.Awaits_ticks_for_a_while");
        Show(slow);
        Assert.Equal(TestOutcome.Pass, slow.Outcome);
        Assert.True(slow.Duration > TimeSpan.FromSeconds(1.4), $"it ran {slow.Duration}: longer than the grace");
        var brief = session.Run("TkBlockMod.Blocks_briefly");  // one 0.8 s step, under 1 s + 0.3 s
        Show(brief);
        Assert.Equal(TestOutcome.Pass, brief.Outcome);
        Assert.Equal(1, session.HarnessCount);
        Assert.Equal(HarnessState.Running, session.Harness!.State);
    }

    [Fact]
    public void ASetupThatBlocksIsStoppedAndFailsTheSession()
    {
        using var session = new KilnTestSession(k =>
        {
            var mod = k.Load<TkBlockMod>(new ModManifest("tk-block"));
            k.Run(() => Thread.Sleep(Timeout.Infinite));
            return mod;
        }, options: new KilnTestOptions { SetupTimeout = TimeSpan.FromSeconds(1) });
        var e = Assert.Throws<InvalidOperationException>(() => session.Run("TkBlockMod.Passes_after_the_wait"));
        output.WriteLine(e.Message);
        Assert.StartsWith("the suite's setup blocked the tick thread for more than 1 s and was stopped by Thread.Interrupt", e.Message);
        Assert.Contains("ASetupThatBlocksIsStoppedAndFailsTheSession", e.Message);  // the stack: the test's own lambda
        Assert.Throws<InvalidOperationException>(() => session.Run("TkBlockMod.Passes_after_the_wait"));
        Assert.Equal(1, session.HarnessCount);
    }

    [Fact]
    public void AStoppedHarnessLeavesNoHandlerOrCommandBehind()
    {
        using (var session = Session())
        {
            Assert.Equal(TestOutcome.Timeout, session.Run("TkBlockMod.Blocks_in_task_wait").Outcome);
            Assert.Equal(HarnessState.Abandoned, session.Harness!.State);
        }
        Assert.False(TestMods.Subscribed("tk-block"));
        using var kiln = new KilnHarness();
        kiln.Boot();
        Assert.Null(kiln.Run(() => Commands.Find("tk-block")));
        Assert.False(kiln.RunCommand("tk-block").Found);
    }
}
