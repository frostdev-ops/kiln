// SPDX-License-Identifier: MIT
#pragma warning disable KILNX001  // LabBots is lab-only API
using Xunit;

namespace Kiln.PlayerTests;

/// <summary>The row <c>labbot.join</c> (LabBots.Capability) without a game: Kiln.Players' rows report it, so
/// <c>Capabilities.Why</c> says why bots cannot join here instead of "no capability of this name is registered" (the
/// contract suite's rule for every name a member requires). On a server the core's sigdb row and kiln.labbot's own reports
/// (policy off a lab, a symbol, a hook) decide it; Kiln.Players adds the row for a process where kiln.labbot is not there.</summary>
[Collection("labbot-table")]
public sealed unsafe class LabBotsCapabilityTests : IDisposable
{
    public LabBotsCapabilityTests() => LabBotNative.UseTable(null);

    public void Dispose() => LabBotNative.UseTable(null);

    [Fact]
    public void WithoutKilnLabbotTheRowSaysWhyBotsCannotJoin()
    {
        LabBots.ReportCapability();  // what PlayersCaps' rows run (the contract suite checks they do)
        var row = Capabilities.Get(LabBots.Capability);
        Assert.Equal(CapabilityState.Unavailable, row.State);
        Assert.NotEqual(CapabilityReason.Unknown, row.Reason);
        Assert.Contains("kiln.labbot", row.ReasonText);
        Assert.Contains("lab server", row.ReasonText);
        Assert.False(Capabilities.Is(LabBots.Capability));
        // a member that needs it names the row's reason
        var e = Assert.Throws<KilnUnavailableException>(() => LabBots.Join("Bot Alder"));
        Assert.Contains("kiln.labbot", e.Message);
    }
}
