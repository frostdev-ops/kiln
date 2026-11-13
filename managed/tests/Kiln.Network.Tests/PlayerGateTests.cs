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
        public int KickAnswer = NetNative.Ok;  // what the game's kick call answers
        public int Kick(uint peer, uint reason) { Kicks.Enqueue((peer, reason)); return KickAnswer; }
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
        Assert.Equal(CapabilityState.Available, PlayerGate.Decide(true, ok, ok, ok, NetNative.RoleServer).Item1);
        Assert.Equal(CapabilityReason.WrongSide, PlayerGate.Decide(true, ok, ok, ok, 0).Item2);
        var off = Row(CapabilityState.Unavailable, CapabilityReason.Policy, "safe idle");
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.Policy, "needs net.kick: safe idle"), PlayerGate.Decide(true, ok, off, ok, 1));
        Assert.Equal(CapabilityReason.ServiceMissing, PlayerGate.Decide(false, Row(CapabilityState.Unavailable, CapabilityReason.Unknown, ""), ok, ok, 1).Item2);
    }

    [Fact]
    public void WithoutTheConnectionRecordsNeitherKickNorBansSayAvailable()
    {
        // the kick call resolved, the records that say whose connection it is did not: no ban could ever be enforced
        Capability Row(string name, CapabilityState s, CapabilityReason r, string t) => new(name, s, r, t, "sigdb");
        var ok = Row("x", CapabilityState.Available, CapabilityReason.None, "");
        var none = Row("net.players", CapabilityState.Unavailable, CapabilityReason.SymbolMissing, "constant net/gs.records: 0 matches");
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.SymbolMissing, "needs net.players: constant net/gs.records: 0 matches"),
            PlayerGate.Decide(true, ok, ok, none, NetNative.RoleServer));
        var unknown = Row("net.players", CapabilityState.Unavailable, CapabilityReason.Unknown, "");
        var (s, r, t) = PlayerGate.Decide(true, ok, ok, unknown, NetNative.RoleServer);
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.SymbolMissing), (s, r));
        Assert.Contains("net.players", t);
    }

    [Fact]
    public async Task AKickTheGameRefusedIsTriedAgainAndABannedPlayerDoesNotStay()
    {
        Bans.Add(5151UL, "griefing");
        var kicked = new List<PlayerKickedEvent>();
        using var sub = Events.On<PlayerKickedEvent>(e => kicked.Add(e));
        var p = Up(Vanilla, 5151);
        _net.KickAnswer = NetNative.Ok + 7;      // the game's kick call fails
        PlayerGate.Check(p);
        var first = p.Kick!.Done.Task;
        NetEngine.RunDueKicks(DateTime.UtcNow);
        var r = await first;
        Assert.False(r.Ok);
        Assert.Contains("rc", r.Reason);
        Assert.Empty(kicked);
        Assert.Null(p.Kick);                     // the connection is not left as "being refused" for good
        Assert.Equal(1, p.KickFailures);
        // the gate does not hammer it every tick ...
        PlayerGate.Check(p);
        Assert.Null(p.Kick);
        Assert.Single(_net.Kicks);
        // ... but after the pause the ban is enforced again, and this time the game takes it
        p.KickRetryAt = DateTime.UtcNow.AddSeconds(-1);
        _net.KickAnswer = NetNative.Ok;
        PlayerGate.Check(p);
        Assert.NotNull(p.Kick);
        var second = p.Kick!.Done.Task;
        Assert.NotSame(first, second);
        NetEngine.RunDueKicks(DateTime.UtcNow);
        Assert.True((await second).Ok);
        Assert.Equal(2, _net.Kicks.Count);
        Assert.Single(kicked);
        // an explicit kick after a failure does not get the old failed task either
        var q = Up(Other, 77);
        _net.KickAnswer = NetNative.Ok + 7;
        var failed = PlayerGate.Refuse(q, default, "lab", "bye", "admin", false);
        NetEngine.RunDueKicks(DateTime.UtcNow);
        Assert.False((await failed).Ok);
        _net.KickAnswer = NetNative.Ok;
        var again = PlayerGate.Refuse(q, default, "lab", "bye", "admin", false);
        Assert.NotSame(failed, again);
        NetEngine.RunDueKicks(DateTime.UtcNow);
        Assert.True((await again).Ok);
    }

    [Fact]
    public async Task AKickThatWentOutIsReportedOnceThoughItsConnectionLeavesAfterwards()
    {
        var kicked = new List<PlayerKickedEvent>();
        using var sub = Events.On<PlayerKickedEvent>(e => kicked.Add(e));
        var p = Up(Vanilla, 91);
        var t = PlayerGate.Refuse(p, default, "lab", "bye", "admin", false);
        NetEngine.RunDueKicks(DateTime.UtcNow);
        Assert.True((await t).Ok);
        Assert.Single(kicked);
        NetEngine.OnPeer(Vanilla, false);       // the game drops the connection a moment later
        Assert.Single(kicked);                  // not a second kick
        Assert.Single(_net.Kicks);
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
        var kicked = new List<PlayerKickedEvent>();
        using var sub = Events.On<PlayerKickedEvent>(e => kicked.Add(e));
        NetEngine.OnPeer(Kiln, false);
        Assert.True((await t).Ok);
        Assert.Empty(_net.Kicks);
        Assert.Single(kicked);
    }
}
