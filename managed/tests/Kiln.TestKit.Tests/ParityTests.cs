// SPDX-License-Identifier: MIT
using Kiln.TestKit.Xunit;
using TkParity;
using Xunit;
using TestOutcome = Kiln.Testing.TestOutcome;

namespace Kiln.TestKit.Tests;

/// <summary>tk-parity offline, from its built folder (the one that deploys into a lab server). The lab runs the same
/// folder; the outcomes pinned here are what the lab's are compared with.</summary>
public sealed class ParityTests
{
    private static ModHandle Load(KilnHarness kiln) => kiln.LoadFolder(TestMods.Folder("TkParity"));

    /// <summary>A mod that registers a command asks for kiln.chat (Kiln.Chat subscribes to players' chat lines for it),
    /// which the kit does not fake. The commands tests run their lines through Commands.Execute, never through chat, so
    /// kiln.chat is allowed; kiln.net is not.</summary>
    public static readonly string[] Allowed = ["kiln.chat"];

    public static readonly (string Name, TestOutcome Outcome)[] Offline =
    [
        ("CommandsParity.A_registered_command_runs_as_the_console", TestOutcome.Pass),
        ("CommandsParity.A_remote_non_admin_runs_a_command_for_everyone", TestOutcome.Pass),
        ("CommandsParity.An_admin_command_is_denied_to_a_remote_non_admin", TestOutcome.Pass),
        ("CommandsParity.A_failing_handler_is_a_result", TestOutcome.Pass),
        ("EventsParity.Post_runs_every_handler_in_priority_order", TestOutcome.Pass),
        ("EventsParity.Cancel_stops_later_handlers_unless_they_receive_canceled", TestOutcome.Pass),
        ("EventsParity.A_disposed_subscription_is_gone", TestOutcome.Pass),
        ("NetworkParity.A_channel_opens_and_closes", TestOutcome.Skip),
        ("NetworkParity.A_message_round_trips_through_the_codec", TestOutcome.Skip),
        ("NetworkParity.A_second_version_of_a_channel_is_refused", TestOutcome.Skip),
    ];

    [Fact]
    public void EachOutcomeOffline()
    {
        List<KilnTestResult> r;
        using (var session = new KilnTestSession(Load, options: new KilnTestOptions { AllowUnavailable = Allowed }))
            r = session.RunAll(typeof(TkParityMod));
        Assert.True(Offline.Select(o => (o.Name, o.Outcome)).SequenceEqual(r.Select(x => (x.Name, x.Outcome))), r.Describe());
        foreach (var net in r.Where(x => x.Name.StartsWith("NetworkParity.", StringComparison.Ordinal)))
        {
            Assert.Equal(["kiln.net"], net.Unavailable);
            Assert.StartsWith("needs kiln.net, which is unavailable offline (KilnUnavailableException:", net.Message);
        }
        Assert.All(r.Where(x => x.Outcome == TestOutcome.Pass), x => Assert.Empty(x.Unavailable.Except(Allowed)));
    }

    [Fact]
    public void WithoutTheAllowanceTheCommandsTestsAreSkippedForKilnChat()
    {
        List<KilnTestResult> r;
        using (var session = new KilnTestSession(Load))
            r = session.RunAll(typeof(TkParityMod));
        foreach (var c in r.Where(x => x.Name.StartsWith("CommandsParity.", StringComparison.Ordinal)))
        {
            Assert.Equal(TestOutcome.Skip, c.Outcome);
            Assert.Equal("needs kiln.chat, which the test kit does not fake; the body passed", c.Message);
        }
    }

    [Fact]
    public void TheRunnerAloneGivesTheSameOrderAndBodies()
    {
        Kiln.Testing.TestReport report;
        using (var kiln = new KilnHarness())
        {
            Load(kiln);
            report = KilnTests.RunAllInTheRunner(kiln, "tk-parity");  // what tests.autoRun starts in a server, without the skip rule
        }
        Assert.Equal(Offline.Select(o => o.Name), report.Results.Select(x => x.Name));
        // Without the kit's rule the explicit unavailable exception fails; offline it opts into a named skip.
        Assert.All(report.Results.Where(x => x.Name.StartsWith("NetworkParity.", StringComparison.Ordinal)), x => Assert.Equal(TestOutcome.Fail, x.Outcome));
    }
}

/// <summary>tk-parity as xunit tests, from its built folder.</summary>
public sealed class TkParitySuite : KilnTestSuite<TkParityMod>
{
    protected override ModHandle LoadMod(KilnHarness kiln) => kiln.LoadFolder(TestMods.Folder("TkParity"));

    protected override IReadOnlyCollection<string> AllowUnavailable => ParityTests.Allowed;
}
