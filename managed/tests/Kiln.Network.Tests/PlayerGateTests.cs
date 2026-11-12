// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using Xunit;

namespace Kiln.Network.Tests;

/// <summary>The server side of kick and ban (PlayerGate) on a fake kiln.net: a banned account is refused once the
/// connection names it, a PlayerLoginEvent handler can refuse, a Kiln client gets the REJECT first, the kick goes out
/// with the game's Generic login error, and the task and PlayerKickedEvent follow it.</summary>
[Collection(nameof(SteamGuardRelayTests))]  // both drive the engine's statics (TestTransport): never at once
public sealed class PlayerGateTests : IDisposable
{
    private const uint Vanilla = 0x91, Other = 0x92, Kiln = 0x93;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kiln-gate-" + Guid.NewGuid().ToString("n"));
    private readonly Fake _net = new();

    private sealed class Fake : INetTransport
    {
        public readonly ConcurrentDictionary<uint, (uint Entity, ulong Account)> Peers = new();
        public readonly ConcurrentQueue<(uint Peer, uint Reason)> Kicks = new();
        public readonly ConcurrentQueue<(uint Peer, byte[] Frame)> Sent = new();
        public int Send(uint peer, byte[] frame) { Sent.Enqueue((peer, frame)); return NetNative.Ok; }
        public NetNative.Status Status() => new() { Roles = NetNative.RoleServer, MaxFrame = 1200 };
        public NetNative.PeerInfo? Info(uint peer) => Peers.TryGetValue(peer, out var x)
            ? new NetNative.PeerInfo { Peer = peer, Flags = NetNative.PeerHasPlayer, Entity = x.Entity, Account = x.Account }
            : null;
        public int Kick(uint peer, uint reason) { Kicks.Enqueue((peer, reason)); return NetNative.Ok; }
    }

    public PlayerGateTests()
    {
        NetNative.TestTransport = _net;
        NetEngine.TestServerRole = true;
        PlayerAdmin.UseStore(new BanStore(Path.Combine(_dir, "bans.json")));
        PlayerGate.Start();
    }

    public void Dispose()
    {
        foreach (var p in new[] { Vanilla, Other, Kiln }) NetEngine.OnPeer(p, false);
        NetNative.TestTransport = null;
        NetEngine.TestServerRole = null;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private NetPeer Up(uint id, ulong account)
    {
        _net.Peers[id] = (id, account);
        NetEngine.OnPeer(id, true);
        return NetEngine.ServerPeers().Single(p => p.Id == id);
    }

    [Fact]
    public void TheKickRowFollowsKilnNet()
    {
        Capability Row(CapabilityState s, CapabilityReason r, string t) => new("x", s, r, t, "sigdb");
        var ok = Row(CapabilityState.Available, CapabilityReason.None, "");
        Assert.Equal(CapabilityState.Available, PlayerGate.Decide(true, ok, ok, NetNative.RoleServer).Item1);
        Assert.Equal(CapabilityReason.WrongSide, PlayerGate.Decide(true, ok, ok, 0).Item2);
        var off = Row(CapabilityState.Unavailable, CapabilityReason.Policy, "safe idle");
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.Policy, "needs net.kick: safe idle"), PlayerGate.Decide(true, ok, off, 1));
        Assert.Equal(CapabilityReason.ServiceMissing, PlayerGate.Decide(false, Row(CapabilityState.Unavailable, CapabilityReason.Unknown, ""), ok, 1).Item2);
    }

    [Fact]
    public async Task ABannedAccountIsRefusedOnceItsConnectionNamesIt()
    {
        Bans.Add(4242UL, "griefing");
        var kicked = new List<PlayerKickedEvent>();
        using var sub = Events.On<PlayerKickedEvent>(e => kicked.Add(e));
        var p = Up(Vanilla, 4242);
        var nobody = Up(Other, 0);  // not named yet: nothing happens
        PlayerGate.Check(p);
        PlayerGate.Check(nobody);
        Assert.NotNull(p.Kick);
        Assert.Null(nobody.Kick);
        Assert.Empty(_net.Sent);  // a vanilla client gets no REJECT frame: the game's login failure only
        NetEngine.RunDueKicks(DateTime.UtcNow);
        Assert.Equal([(Vanilla, PlayerGate.KickReasonCode)], _net.Kicks.ToArray());
        var r = await p.Kick!.Done.Task;
        Assert.True(r.Ok);
        Assert.Single(kicked);
        Assert.True(kicked[0].Banned);
        Assert.Contains("griefing", kicked[0].Reason);
        Assert.StartsWith("You are banned from this server", kicked[0].Reason);
        // checked again later: it is already being refused, nothing new
        PlayerGate.Check(p);
        Assert.Single(_net.Kicks);
    }

    [Fact]
    public void ALoginHandlerRefusesOnceAndOthersComeIn()
    {
        var seen = new List<(string, ulong)>();
        using var sub = Events.On<PlayerLoginEvent>(e =>
        {
            seen.Add((e.Name, e.AccountId));
            if (e.AccountId == 5) { e.Reason = "whitelist only"; e.Cancel(); }
        });
        var refused = Up(Vanilla, 5);
        var allowed = Up(Other, 6);
        PlayerGate.Check(refused);
        PlayerGate.Check(allowed);
        PlayerGate.Check(allowed);  // once per connection
        Assert.Equal(2, seen.Count);
        Assert.NotNull(refused.Kick);
        Assert.Equal("whitelist only", refused.Kick!.Reason);
        Assert.False(refused.Kick.Banned);
        Assert.Null(allowed.Kick);
        // a ban made while the other is in game reaches it at the next check
        Bans.Add(6UL, "later");
        PlayerGate.Check(allowed);
        Assert.NotNull(allowed.Kick);
    }

    [Fact]
    public async Task AKilnClientReadsTheRejectBeforeTheLoginFailure()
    {
        var p = Up(Kiln, 77);
        p.State = NetPeerState.Ready;  // a Kiln client after its handshake
        var t = PlayerGate.Refuse(p, new PlayerRef(0, Guid.Empty, EntityRef.None), "lab", "bye", "a-mod", false);
        Assert.Single(_net.Sent);
        Assert.True(Wire.TryHeader(_net.Sent.Single().Frame, out var type) && type == Wire.Type.Reject);
        NetEngine.RunDueKicks(DateTime.UtcNow);
        Assert.Empty(_net.Kicks);  // 1.5 s grace
        Assert.Same(t, PlayerGate.Refuse(p, default, "lab", "again", null, false));  // one refusal per connection
        NetEngine.RunDueKicks(DateTime.UtcNow + PlayerGate.KilnClientGrace + TimeSpan.FromMilliseconds(10));
        Assert.Single(_net.Kicks);
        var r = await t;
        Assert.True(r.Ok);
        Assert.Equal("bye", p.Kick!.Reason);
    }

    [Fact]
    public async Task AConnectionThatLeavesOnTheRefusalCountsAsKicked()
    {
        var p = Up(Kiln, 78);
        p.State = NetPeerState.Ready;
        var t = PlayerGate.Refuse(p, default, "lab", "bye", null, false);
        NetEngine.OnPeer(Kiln, false);
        Assert.True((await t).Ok);
        Assert.Empty(_net.Kicks);
    }
}
