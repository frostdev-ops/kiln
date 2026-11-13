// SPDX-License-Identifier: MIT
using TkOffline;
using Xunit;
using TestOutcome = Kiln.Testing.TestOutcome;

namespace Kiln.TestKit.Tests;

public sealed class OfflineSkipTests
{
    private static KilnTestSession Session(params string[] allowed) =>
        new(k => k.Load<TkOfflineMod>(new ModManifest("tk-offline")), options: new KilnTestOptions { AllowUnavailable = allowed });

    [Fact]
    public void ATestThatAsksForAnUnfakedServiceIsSkippedWithItsName()
    {
        using var session = Session();
        var r = session.RunAll(typeof(TkOfflineMod));
        var passed = r.Named("TkOfflineMod.Reads_settings_and_passes");
        Assert.Equal(TestOutcome.Skip, passed.Outcome);
        Assert.Equal("needs kiln.settings, which the test kit does not fake; the body passed", passed.Message);
        Assert.Equal(["kiln.settings"], passed.Unavailable);
        Assert.Equal(TestOutcome.Pass, passed.Runner!.Outcome);
        var failed = r.Named("TkOfflineMod.Reads_settings_and_fails");
        Assert.Equal(TestOutcome.Fail, failed.Outcome);
        Assert.Equal("needs kiln.settings, which the test kit does not fake; the body failed: settings should be there", failed.Message);
        Assert.Equal(["kiln.settings"], failed.Unavailable);
        Assert.Throws<Kiln.TestKit.Xunit.KilnTestFailedException>(() => Kiln.TestKit.Xunit.KilnTestReport.Report(failed));
        var feature = r.Named("TkOfflineMod.Reads_a_feature");
        Assert.Equal(TestOutcome.Skip, feature.Outcome);
        Assert.Equal("needs the game feature tk.feature, which is off offline; the body passed", feature.Message);
        var thrown = r.Named("TkOfflineMod.Throws_unavailable");
        Assert.Equal(TestOutcome.Skip, thrown.Outcome);
        Assert.StartsWith("needs tk.capability, which is unavailable offline (KilnUnavailableException: tk.capability is not available: not on this build)", thrown.Message);
        Assert.Equal(TestOutcome.Pass, r.Named("TkOfflineMod.Asks_for_nothing").Outcome);
        Assert.Empty(r.Named("TkOfflineMod.Asks_for_nothing").Unavailable);
    }

    [Theory]
    [InlineData("TkOfflineMod.Reads_a_feature_and_times_out")]
    [InlineData("TkOfflineMod.Reads_a_feature_and_blocks")]
    public void AnUnavailableAskDoesNotHideATimeout(string name)
    {
        using var session = new KilnTestSession(k => k.Load<TkOfflineMod>(new ModManifest("tk-offline")),
            options: new KilnTestOptions { WatchdogGrace = TimeSpan.FromMilliseconds(250) });
        var result = session.Run(name);
        Assert.Equal(TestOutcome.Timeout, result.Outcome);
        Assert.Equal(["tk.timeout"], result.Unavailable);
        Assert.Contains("tk.timeout", result.Message);
        Assert.Throws<Kiln.TestKit.Xunit.KilnTestTimeoutException>(() => Kiln.TestKit.Xunit.KilnTestReport.Report(result));
    }

    [Fact]
    public void CatchingUnavailableDoesNotHideAnUnrelatedFailure()
    {
        using var session = Session();
        var result = session.Run("TkOfflineMod.Catches_unavailable_then_fails");
        Assert.Equal(TestOutcome.Fail, result.Outcome);
        Assert.Contains("pure logic regression", result.Message);
        Assert.Contains("tk.optional", result.Message);
    }

    [Fact]
    public void AServiceThatRemembersItsMissIsSeenInEveryTest()
    {
        // kiln.hooks asks the backend once per process and keeps the answer: the kit makes it ask again before each test
        using var session = Session();
        foreach (var name in new[] { "TkOfflineMod.Hooks_first", "TkOfflineMod.Hooks_second", "TkOfflineMod.Hooks_second" })
        {
            var r = session.Run(name);
            Assert.Equal(TestOutcome.Skip, r.Outcome);
            Assert.Equal(["kiln.hooks"], r.Unavailable);
        }
    }

    [Fact]
    public void TheAllowListTurnsTheRuleOff()
    {
        using var session = Session("kiln.settings", "kiln.hooks");
        Assert.Equal(TestOutcome.Pass, session.Run("TkOfflineMod.Reads_settings_and_passes").Outcome);
        var failed = session.Run("TkOfflineMod.Reads_settings_and_fails");
        Assert.Equal(TestOutcome.Fail, failed.Outcome);
        Assert.Equal("settings should be there", failed.Message);
        Assert.Equal(TestOutcome.Pass, session.Run("TkOfflineMod.Hooks_second").Outcome);
        Assert.Equal(TestOutcome.Skip, session.Run("TkOfflineMod.Reads_a_feature").Outcome);  // only the names allowed
    }
}
