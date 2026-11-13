// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>Kiln.Network (channels, peers, the handshake, mod sync) by the contract.</summary>
public sealed class NetworkContractTests : ContractTest
{
    [Fact]
    public void With_no_transport_the_features_are_lowered_and_nothing_throws()
    {
        StartRows(typeof(Network).Assembly);
        foreach (var f in NetworkCaps.Features) AssertOffOffline(f, "needs kiln.net");
        Assert.False(Network.Available);
        Assert.Empty(Network.Peers);
        Assert.Null(Network.Server);
    }

    [Fact]
    public void Members_carry_their_side_and_thread()
    {
        AssertHas<ServerOnlyAttribute>(Property(typeof(Network), "Peers"));
        AssertHas<ClientOnlyAttribute>(Property(typeof(Network), "Server"));
        AssertHas<ServerOnlyAttribute>(Method(typeof(Network), "PeerOf", typeof(EntityId)));
        AssertHas<TickThreadOnlyAttribute>(Method(typeof(ConfigSync), "CheckRoundTrip", typeof(ConfigFile)));
        var near = typeof(NetChannel).GetMethods().Single(m => m.Name == "SendNear");
        AssertHas<TickThreadOnlyAttribute>(near);
        AssertHas<ServerOnlyAttribute>(near);
        var toServer = typeof(NetChannel).GetMethods().Single(m => m.Name == "SendToServer");
        AssertHas<ClientOnlyAttribute>(toServer);
        AssertHas<AnyThreadAttribute>(toServer);
        AssertRequires(Method(typeof(Network), "Kick", typeof(NetPeer), typeof(string)), "players.kick");
    }
}
