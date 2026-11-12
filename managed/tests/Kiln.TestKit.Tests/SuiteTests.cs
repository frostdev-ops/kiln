// SPDX-License-Identifier: MIT
using Kiln.TestKit.Xunit;
using SampleTests;
using TkSuite;
using Xunit;
using TestOutcome = Kiln.Testing.TestOutcome;

namespace Kiln.TestKit.Tests;

public sealed class SuiteTests
{
    private static readonly string[] Order =
    [
        "TkSuiteMod.First_sets_state",
        "OtherTests.Throws",
        "OtherTests.Returns_a_value_task",
        "TkSuiteMod.Sees_the_state_of_the_first",
        "TkSuiteMod.Awaits_ticks",
        "TkSuiteMod.Fails_an_assertion",
        "TkSuiteMod.Skipped_by_attribute",
        "TkSuiteMod.Skips_itself",
        "a custom name",
        "TkSuiteMod.Times_out_in_an_await",
        "TkSuiteMod.Broken",
        "TkSuiteMod.Runs_last",
    ];

    private static KilnTestSession Session() => new(k => k.Load<TkSuiteMod>(new ModManifest("tk-suite")));

    [Fact]
    public void TheListIsTheRunnersOrder()
    {
        Assert.Equal(Order, KilnHarness.ListTests(typeof(TkSuiteMod).Assembly));
        using var kiln = new KilnHarness();
        kiln.Load<TkSuiteMod>(new ModManifest("tk-suite"));
        var report = KilnTests.RunAllInTheRunner(kiln, "tk-suite");
        Assert.Equal(KilnHarness.ListTests(typeof(TkSuiteMod).Assembly), report.Results.Select(r => r.Name));
    }

    [Fact]
    public void TestByTestGivesWhatOneRunWithoutAFilterGives()
    {
        List<KilnTestResult> mine;
        using (var session = Session())
        {
            mine = session.RunAll(typeof(TkSuiteMod));
            Assert.Equal(1, session.HarnessCount);  // one harness, one load, for the whole suite
        }
        Kiln.Testing.TestReport report;
        using (var kiln = new KilnHarness())
        {
            kiln.Load<TkSuiteMod>(new ModManifest("tk-suite"));
            report = KilnTests.RunAllInTheRunner(kiln, "tk-suite");
        }
        Assert.Equal(report.Results.Select(r => (r.Name, r.Outcome, r.Message)), mine.Select(r => (r.Name, r.Outcome, r.Message)));
        Assert.All(mine, r => Assert.Empty(r.Unavailable));
    }

    [Fact]
    public void EachOutcomeAsTheRunnerGivesIt()
    {
        using var session = Session();
        var r = session.RunAll(typeof(TkSuiteMod));
        Assert.Equal(
        [
            TestOutcome.Pass, TestOutcome.Fail, TestOutcome.Pass, TestOutcome.Pass, TestOutcome.Pass, TestOutcome.Fail, TestOutcome.Skip,
            TestOutcome.Skip, TestOutcome.Pass, TestOutcome.Timeout, TestOutcome.Fail, TestOutcome.Pass,
        ], r.Select(x => x.Outcome));

        var failed = r.Named("TkSuiteMod.Fails_an_assertion");
        Assert.Equal("the value: expected <1>, got <2>", failed.Message);
        Assert.Contains("TkSuiteMod.Check", failed.Stack);  // the assertion's own frames, without Kiln.Testing's
        Assert.Contains("TkSuiteMod.Fails_an_assertion", failed.Stack);
        Assert.DoesNotContain("Kiln.Testing.Assert", failed.Stack);
        var threw = r.Named("OtherTests.Throws");
        Assert.Equal("InvalidOperationException: something broke", threw.Message);
        Assert.Contains("OtherTests.Throws", threw.Stack);
        Assert.Equal("skipped by its attribute", r.Named("TkSuiteMod.Skipped_by_attribute").Message);
        Assert.Equal("skipped by Assert.Skip", r.Named("TkSuiteMod.Skips_itself").Message);
        Assert.Equal("did not finish within 1 s", r.Named("TkSuiteMod.Times_out_in_an_await").Message);
        // a method the runner cannot call fails with its reason, as in a server
        Assert.Equal("a test takes no parameter or one TestContext", r.Named("TkSuiteMod.Broken").Message);
        Assert.Contains(r.Named("TkSuiteMod.Awaits_ticks").Log, l => l.Text.StartsWith("PASS TkSuiteMod.Awaits_ticks", StringComparison.Ordinal));
    }

    private static ModHandle SampleSet(KilnHarness kiln)
    {
        kiln.LoadFolder(TestMods.Folder("SampleLib"));
        kiln.LoadFolder(TestMods.Folder("SampleConsumer"));
        return kiln.LoadFolder(TestMods.Folder("SampleTests"));
    }

    [Fact]
    public void SampleTestsGiveTheRunnersOutcomesInTheRunnersOrder()
    {
        List<KilnTestResult> mine;
        using (var session = new KilnTestSession(SampleSet))
            mine = session.RunAll(typeof(SampleTestsMod));
        Kiln.Testing.TestReport report;
        using (var kiln = new KilnHarness())
        {
            SampleSet(kiln);
            report = KilnTests.RunAllInTheRunner(kiln, "sample-tests");  // what its tests.autoRun starts in a server
        }
        Assert.Equal(report.Results.Select(r => r.Name), KilnHarness.ListTests(typeof(SampleTestsMod).Assembly));
        Assert.Equal(report.Results.Select(r => (r.Name, r.Outcome)), mine.Select(r => (r.Name, r.Outcome)));
        Assert.Equal(9, mine.Count(r => r.Outcome == TestOutcome.Pass));
        Assert.Equal("shows how a skipped test is reported", Assert.Single(mine, r => r.Outcome == TestOutcome.Skip).Message);
    }

    [Fact]
    public void AnUnknownNameIsRefused()
    {
        using var session = Session();
        Assert.Throws<ArgumentException>(() => session.Run("TkSuiteMod.No_such_test"));
    }

    [Fact]
    public void ASetupThatThrowsFailsEveryTestOfTheSession()
    {
        using var session = new KilnTestSession(_ => throw new InvalidOperationException("no mod for you"));
        var e = Assert.Throws<InvalidOperationException>(() => session.Run("TkSuiteMod.Runs_last"));
        Assert.Contains("no mod for you", e.Message);
        Assert.Throws<InvalidOperationException>(() => session.Run("TkSuiteMod.Runs_last"));
        Assert.Equal(1, session.HarnessCount);
    }

    [Fact]
    public void ResultsMapOntoXunit()
    {
        KilnTestReport.Report(new KilnTestResult("t", TestOutcome.Pass, TimeSpan.Zero, null, null, []));
        var skip = Assert.Throws<KilnTestSkippedException>(() => KilnTestReport.Report(new KilnTestResult("t", TestOutcome.Skip, TimeSpan.Zero, "why", null, [])));
        Assert.Equal(global::Xunit.v3.DynamicSkipToken.Value + "why", skip.Message);
        var fail = Assert.Throws<KilnTestFailedException>(() => KilnTestReport.Report(new KilnTestResult("t", TestOutcome.Fail, TimeSpan.Zero, "bad", "   at Mod.Test()", [])));
        Assert.Equal("bad", fail.Message);
        Assert.Equal("   at Mod.Test()", fail.StackTrace);
        var timeout = Assert.Throws<KilnTestTimeoutException>(() => KilnTestReport.Report(new KilnTestResult("t", TestOutcome.Timeout, TimeSpan.Zero, "slow", "   at Mod.Spin()", [])));
        Assert.Equal("   at Mod.Spin()", timeout.StackTrace);
    }
}
