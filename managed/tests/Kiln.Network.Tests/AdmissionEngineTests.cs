// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Network.Tests;

/// <summary>Mandatory protocols in the engine's hello decision (NetAdmission.SetMandatoryProtocol): a client without
/// the channel at that version is refused under every modSync value, a wrong version is refused, a Ready client that
/// announces its channels again without it is refused before the re-announcement fast path, and a client with it gets
/// Welcome. Hellos are fed through the engine's frame entry on a fake kiln.net.</summary>
[Collection(nameof(SteamGuardRelayTests))]  // the engine's statics: never at once with the other engine tests
public sealed class AdmissionEngineTests : IDisposable
{
    private const uint P = 0xB1;
    private readonly AdmissionTests.FakeGate _gate = new();
    private readonly AdmissionTests.FakeNet _net = new();

    public AdmissionEngineTests()
    {
        NetNative.TestTransport = _net;
        NetEngine.TestServerRole = true;
        NetAdmission.ResetForTests();
        NetAdmission.TestGate = _gate;
    }

    public void Dispose()
    {
        NetEngine.OnPeer(P, false);
        NetAdmission.ResetForTests();
        NetAdmission.TestGate = null;
        NetEngine.UseConfigForTests(new NetworkConfig());
        NetNative.TestTransport = null;
        NetEngine.TestServerRole = null;
    }

    private NetPeer Up()
    {
        NetEngine.OnPeer(P, true);
        return NetEngine.ServerPeers().Single(x => x.Id == P);
    }

    private void Hello(params (string Name, string Version)[] channels)
    {
        var hello = new Wire.Hello(Wire.Protocol, Game.KilnVersion, 7, [],
            channels.Select(c => new Wire.ChannelEntry(0, c.Name, c.Version, false)).ToList(), new Dictionary<string, string>());
        NetEngine.OnFrame(P, Wire.EncodeHello(hello));
    }

    private Wire.Type? LastFrame()
    {
        var last = _net.Sent.Where(s => s.Peer == P).Select(s => s.Frame).LastOrDefault();
        return last != null && Wire.TryHeader(last, out var t) ? t : null;
    }

    private string? LastReject() => _net.Sent.Where(s => s.Peer == P && Wire.TryHeader(s.Frame, out var t) && t == Wire.Type.Reject)
        .Select(s => Wire.DecodeReject(s.Frame).Reason).LastOrDefault();

    [Theory]
    [InlineData(ModSyncPolicy.Warn)]
    [InlineData(ModSyncPolicy.Off)]
    [InlineData(ModSyncPolicy.Refuse)]
    public void A_client_without_the_protocol_is_refused_under_every_modsync(ModSyncPolicy policy)
    {
        NetEngine.UseConfigForTests(new NetworkConfig { ModSync = policy });
        Assert.True(NetAdmission.SetMandatoryProtocol("kiln:station-menu", "1", true, "station-menu protocol 1 required").Ok);
        var p = Up();
        Hello(("kiln:content", "1"));
        Assert.Equal(NetPeerState.Rejected, p.State);
        Assert.Contains("station-menu protocol 1 required", LastReject());  // under refuse, after any other missing item
        Assert.NotNull(p.KickAt);
        Assert.DoesNotContain(_net.Sent, s => s.Peer == P && Wire.TryHeader(s.Frame, out var t) && t == Wire.Type.Welcome);
    }

    [Fact]
    public void A_wrong_version_is_refused_and_the_right_one_gets_welcome()
    {
        Assert.True(NetAdmission.SetMandatoryProtocol("kiln:station-menu", "1", true, "station-menu protocol 1 required").Ok);
        var p = Up();
        Hello(("kiln:station-menu", "2"));
        Assert.Equal(NetPeerState.Rejected, p.State);
        NetEngine.OnPeer(P, false);
        p = Up();
        Hello(("kiln:station-menu", "1"));
        Assert.Equal(NetPeerState.Ready, p.State);
        Assert.Equal(Wire.Type.Welcome, LastFrame());
    }

    [Fact]
    public void A_ready_client_that_drops_the_protocol_is_refused_before_the_reannouncement_path()
    {
        Assert.True(NetAdmission.SetMandatoryProtocol("kiln:station-menu", "1", true, "station-menu protocol 1 required").Ok);
        var p = Up();
        Hello(("kiln:station-menu", "1"));
        Assert.Equal(NetPeerState.Ready, p.State);
        var welcomes = _net.Sent.Count(s => s.Peer == P && Wire.TryHeader(s.Frame, out var t) && t == Wire.Type.Welcome);
        Hello(("kiln:station-menu", "1"));  // the fast path: Welcome again
        Assert.Equal(welcomes + 1, _net.Sent.Count(s => s.Peer == P && Wire.TryHeader(s.Frame, out var t) && t == Wire.Type.Welcome));
        Hello(("kiln:content", "1"));  // its station-menu capability went: the same hard requirement refuses it
        Assert.Equal(NetPeerState.Rejected, p.State);
        Assert.Equal("station-menu protocol 1 required", LastReject());
        Assert.NotNull(p.KickAt);
    }

    [Fact]
    public void Enabling_a_protocol_a_connected_peer_lacks_is_refused_and_not_installed()
    {
        var p = Up();
        Hello(("kiln:content", "1"));
        Assert.Equal(NetPeerState.Ready, p.State);
        var r = NetAdmission.SetMandatoryProtocol("kiln:station-menu", "1", true, "x");
        Assert.False(r.Ok);
        Assert.Contains("peers lack it", r.Reason);
        Assert.Equal([p], NetAdmission.PeersLacking("kiln:station-menu", "1"));
        Hello(("kiln:content", "1"));  // nothing was installed: it is not refused
        Assert.Equal(NetPeerState.Ready, p.State);
    }

    [Fact]
    public void A_protocol_refuses_clients_without_kiln_and_turned_off_it_is_gone()
    {
        Assert.True(NetAdmission.SetMandatoryProtocol("kiln:station-menu", "1", true, "station-menu protocol 1 required").Ok);
        Assert.True(NetAdmission.VanillaRefused(false, out var why));
        Assert.Equal("station-menu protocol 1 required", why);
        Assert.True(NetAdmission.SetMandatoryProtocol("kiln:station-menu", "1", false, "").Ok);
        Assert.False(NetAdmission.VanillaRefused(false, out _));
        var p = Up();
        Hello(("kiln:content", "1"));
        Assert.Equal(NetPeerState.Ready, p.State);
    }
}
